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
        Pragma(source, "PRAGMA busy_timeout=5000;");   // CLAUDE.md: every connection waits for a lock rather than failing at once
        using var dest = new SqliteConnection($"Data Source={file};Pooling=False");
        dest.Open();
        Pragma(dest, "PRAGMA busy_timeout=5000;");
        CopyWaitingForLocks(source, dest);
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

    /// <summary>The online backup takes a read lock on the source; while a writer holds the database for a moment (SQLITE_BUSY or SQLITE_LOCKED, which
    /// Windows file locking produces far more often than Linux) the whole copy is retried, for up to <see cref="BusyWait"/>, then the error is thrown and
    /// the backup service raises its alert (rule 8). CI run 71 failed a backup this way.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);

    private static void CopyWaitingForLocks(SqliteConnection source, SqliteConnection dest)
    {
        var deadline = DateTime.UtcNow + BusyWait;
        for (var delay = 50; ; delay = Math.Min(delay * 2, 1000))
        {
            try { source.BackupDatabase(dest); return; }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 && DateTime.UtcNow + TimeSpan.FromMilliseconds(delay) < deadline)   // SQLITE_BUSY, SQLITE_LOCKED
            {
                Thread.Sleep(delay);
            }
        }
    }

    private static void Pragma(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

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

    /// <summary>
    /// Copies a backup over the target database, refusing an unhealthy backup, and puts the restored file back into WAL mode (a backup is written in the default
    /// journal mode so it is one self-contained file; the live database is WAL, D2). The key ring, credentials and attachments are separate directories the runbook
    /// copies back (D12); this only restores the database.
    /// </summary>
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
        using var c = new SqliteConnection($"Data Source={targetDb};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = (string)cmd.ExecuteScalar()!;
        if (!mode.Equals("wal", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"The restored database could not be switched to WAL (journal_mode={mode})");
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
