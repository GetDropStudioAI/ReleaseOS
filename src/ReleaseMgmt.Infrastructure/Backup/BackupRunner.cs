using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Infrastructure.Backup;

/// <summary>SQLite online backup API, retention pruning and restore (D3). 15-minute backups are kept 2 days; one nightly per UTC day is kept 30 days.</summary>
public static class BackupRunner
{
    public const string Prefix = "releasemgmt-";
    public const string NightlyPrefix = "releasemgmt-nightly-";
    public const string Extension = ".db";
    public static readonly TimeSpan NightlyRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan FrequentRetention = TimeSpan.FromDays(2);

    public static string Backup(string sourceConnectionString, string backupDir, DateTime utcNow, bool nightly = false)
    {
        Directory.CreateDirectory(backupDir);
        var file = Path.Combine(backupDir, $"{(nightly ? NightlyPrefix : Prefix)}{utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}{Extension}");
        using var source = new SqliteConnection(sourceConnectionString);
        source.Open();
        using var dest = new SqliteConnection($"Data Source={file};Pooling=False");
        dest.Open();
        source.BackupDatabase(dest);
        using (var cmd = dest.CreateCommand())
        {
            // The copy inherits WAL mode; switch it off so a backup is one self-contained file.
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            cmd.ExecuteNonQuery();
        }
        dest.Close();
        var check = IntegrityCheck(file);
        if (check != "ok")
        {
            File.Delete(file);
            throw new InvalidOperationException($"Backup failed integrity_check: {check}");
        }
        return file;
    }

    public static int Prune(string backupDir, DateTime utcNow)
    {
        if (!Directory.Exists(backupDir)) return 0;
        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(backupDir, $"{Prefix}*{Extension}"))
        {
            var keep = Path.GetFileName(f).StartsWith(NightlyPrefix, StringComparison.Ordinal) ? NightlyRetention : FrequentRetention;
            if (File.GetLastWriteTimeUtc(f) < utcNow - keep) { File.Delete(f); removed++; }
        }
        return removed;
    }

    /// <summary>Copies a backup over the target database, refusing an unhealthy backup.</summary>
    public static void Restore(string backupFile, string targetDb)
    {
        var check = IntegrityCheck(backupFile);
        if (check != "ok") throw new InvalidOperationException($"Backup failed integrity_check: {check}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetDb));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(targetDb + suffix)) File.Delete(targetDb + suffix);
        }
        File.Copy(backupFile, targetDb, overwrite: true);
    }

    public static string IntegrityCheck(string dbFile)
    {
        using var c = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string)cmd.ExecuteScalar()!;
    }
}
