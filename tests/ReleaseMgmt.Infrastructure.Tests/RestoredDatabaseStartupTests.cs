using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Backup;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-52 defect: the API could not start on a database restored from a backup. A backup is one self-contained file in the default journal mode; the
/// application's first connection is EF's read-only Exists() probe, and the interceptor's <c>PRAGMA journal_mode=WAL</c> failed there with "attempt to write a
/// readonly database". The earlier restore tests only read the restored file with sqlite, never opened it the way the application does.
/// </summary>
public sealed class RestoredDatabaseStartupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-restore-boot-").FullName;
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static ReleaseDbContext Open(string path) => new(new DbContextOptionsBuilder<ReleaseDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);

    private static string JournalMode(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode"; return (string)cmd.ExecuteScalar()!;
    }

    private string BackupOfMigratedDatabase()
    {
        var live = Path.Combine(_dir, "live.db");
        using (var ctx = Open(live)) ctx.Database.Migrate();
        var file = BackupRunner.Backup($"Data Source={live};Pooling=False", Path.Combine(_dir, "bk"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual("wal", JournalMode(file));   // the premise: a backup is not in WAL mode
        return file;
    }

    [Fact]
    public void Restore_leaves_the_database_in_WAL_mode_and_the_application_opens_and_migrates_it()
    {
        var backup = BackupOfMigratedDatabase();
        var target = Path.Combine(_dir, "restored", "releasemgmt.db");
        BackupRunner.Restore(backup, target);
        Assert.Equal("wal", JournalMode(target));
        using var ctx = Open(target);
        ctx.Database.Migrate();   // what Program.cs does at start-up: Exists() (read-only probe), then a normal connection
        Assert.Equal("ok", BackupRunner.IntegrityCheck(target));
    }

    [Fact]
    public void A_backup_copied_into_place_by_hand_still_starts_because_read_only_connections_skip_the_journal_mode()
    {
        var backup = BackupOfMigratedDatabase();
        var target = Path.Combine(_dir, "by-hand.db");
        File.Copy(backup, target);
        using (var ctx = Open(target)) ctx.Database.Migrate();
        Assert.Equal("wal", JournalMode(target));   // the first read-write connection made the switch
    }
}
