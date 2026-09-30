using Microsoft.Data.Sqlite;
using ReleaseMgmt.Infrastructure.Backup;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// CI run 71 (windows-latest): a 15-minute backup taken while the app was writing failed with SQLITE_BUSY ("database is locked"). The backup opened its own
/// connection without the busy timeout CLAUDE.md requires on every connection, so a lock held for a moment by a writer failed the backup at once instead of
/// waiting. A missed backup is alerted, but the next chance is 15 minutes later.
/// </summary>
public class BackupBusyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bkbusy-").FullName;
    private string Db => Path.Combine(_dir, "app.db");
    private string Conn => $"Data Source={Db};Pooling=False";

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void Run(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [Fact]
    public async Task A_backup_waits_out_a_lock_held_briefly_by_a_writer_instead_of_failing()
    {
        using (var c = new SqliteConnection(Conn)) { c.Open(); Run(c, "PRAGMA journal_mode=WAL; CREATE TABLE t(x); INSERT INTO t VALUES (1);"); }

        // A writer holds the database lock for ~0.8 s (exclusive locking mode keeps even readers out, which is what a busy moment looks like on Windows).
        var writer = new SqliteConnection(Conn);
        writer.Open();
        Run(writer, "PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; BEGIN IMMEDIATE; INSERT INTO t VALUES (2); COMMIT;");
        var release = Task.Run(async () =>
        {
            await Task.Delay(800);
            Run(writer, "PRAGMA locking_mode=NORMAL; SELECT count(*) FROM t;");   // the next access after NORMAL drops the lock
            writer.Dispose();
        });

        var file = BackupRunner.Backup(Conn, Path.Combine(_dir, "bk"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        await release;
        using var b = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        b.Open();
        using var cmd = b.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM t";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }
}
