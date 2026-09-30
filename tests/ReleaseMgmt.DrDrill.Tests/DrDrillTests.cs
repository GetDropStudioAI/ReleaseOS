using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Backup;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.DrDrill;

/// <summary>
/// REOS-52. The default run is a small instance (a few seconds) so the whole restore path (real API process, real key ring, real restore CLI) is guarded on the
/// Windows and macOS CI runners. tools/dr_drill.py runs the same test at production scale (DR_DRILL_SCALE=full) and prints the step timings.
/// </summary>
public class DrDrillTests
{
    [Fact]
    public async Task Total_loss_is_restored_from_the_backup_and_the_three_directories_within_budget()
    {
        var scale = DrScale.FromEnv();
        var scratch = Path.Combine(Path.GetTempPath(), "reos-dr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var report = await DisasterRecoveryDrill.RunAsync(scale, scratch);
            var markdown = report.ToMarkdown();
            Console.WriteLine(markdown);
            if (Environment.GetEnvironmentVariable("DR_DRILL_REPORT") is { Length: > 0 } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, report.ToJson());
            }
            Assert.True(report.Passed, markdown);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Environment.GetEnvironmentVariable("DR_KEEP") != "1") DisasterRecoveryDrill.DeleteWithRetry(scratch);
        }
    }
}

/// <summary>
/// The backup of a live WAL database while writes are happening (D3: a backup every 15 minutes, never with the application stopped). Writers use the real
/// TaskService, whose every write changes a task row and adds one audit row in a single transaction; that gives an invariant a torn snapshot would break:
/// for each task, Version = 1 + its audit rows and (Complete rows - Reopen rows) = IsCompleted. Every backup taken during the writes must pass integrity_check
/// (BackupRunner enforces it) and that invariant, and must be a single self-contained file.
/// </summary>
public class LiveBackupConsistencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-live-bk-").FullName;
    private string Db => Path.Combine(_dir, "live.db");
    private string Conn => $"Data Source={Db};Pooling=False";
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>().UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private static void Run(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [Fact]
    public async Task Backups_taken_while_writers_run_are_healthy_and_transaction_consistent()
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("DR_LIVE_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 3;
        using (var ctx = new Factory(Db).CreateDbContext()) ctx.Database.Migrate();
        var tasks = Enumerable.Range(1, 8).Select(i => $"k{i}").ToArray();
        using (var c = new SqliteConnection(Conn))
        {
            c.Open(); Run(c, SqliteConnectionInterceptor.Pragmas);
            Run(c, "INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('rte','rte@x.com','Rae T.','RTE')");
            Run(c, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
            Run(c, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,LastChangedByUserId) VALUES('g1','t1','Code Freeze','Standard',1,5,'2026-10-23','Gated','rte','InProgress','rte')");
            foreach (var t in tasks) Run(c, $"INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('{t}','g1','Task {t}','rte',{t[1..]})");
        }
        using (var c = new SqliteConnection(Conn)) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", cmd.ExecuteScalar()); }   // the source really is a WAL database

        var svc = new TaskService(new Factory(Db), TimeProvider.System);
        var actor = new Actor("rte");
        // Run until the minimum time AND the minimum work are both reached (a slow shared CI runner needs longer, not a weaker test); hard cap 90 s.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long writes = 0;
        var writers = tasks.Select(t => Task.Run(async () =>
        {
            for (var done = false; !stop.IsCancellationRequested; done = !done)
            {
                var r = done ? await svc.ReopenAsync(t, actor) : await svc.CompleteAsync(t, actor);
                if (!r.IsOk) throw new InvalidOperationException($"writer {t}: {r.Kind}");
                Interlocked.Increment(ref writes);
            }
        })).ToArray();

        var bk = Path.Combine(_dir, "bk"); var backups = new List<(string File, long AuditRows)>();
        var stamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var n = 0; !stop.IsCancellationRequested; n++)
        {
            var file = BackupRunner.Backup(Conn, bk, stamp.AddSeconds(n));   // throws unless the copy passes integrity_check
            using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False"); c.Open();
            long Count(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }
            foreach (var t in tasks)
            {
                var completes = Count($"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ChecklistTask' AND EntityId='{t}' AND Action='Complete'");
                var reopens = Count($"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ChecklistTask' AND EntityId='{t}' AND Action='Reopen'");
                var version = Count($"SELECT Version FROM ChecklistTasks WHERE Id='{t}'");
                var isDone = Count($"SELECT IsCompleted FROM ChecklistTasks WHERE Id='{t}'");
                Assert.True(version == 1 + completes + reopens, $"backup {n}, {t}: Version {version} but {completes + reopens} audit rows (a torn snapshot)");
                Assert.True(completes - reopens == isDone, $"backup {n}, {t}: {completes} completes - {reopens} reopens != IsCompleted {isDone}");
            }
            backups.Add((file, Count("SELECT COUNT(*) FROM AuditEvents")));
            Assert.False(File.Exists(file + "-wal"), "a backup is one self-contained file");
            using (var jm = c.CreateCommand()) { jm.CommandText = "PRAGMA journal_mode"; Assert.Equal("delete", jm.ExecuteScalar()); }
            await Task.Delay(20);
            if (clock.Elapsed.TotalSeconds >= seconds && Interlocked.Read(ref writes) > 50 && backups.Count >= 3) break;
        }
        stop.Cancel();
        await Task.WhenAll(writers);   // a failed writer (for example SQLITE_BUSY surfacing as an exception) fails the test here

        Assert.True(backups.Count >= 3, $"only {backups.Count} backups completed in {clock.Elapsed.TotalSeconds:F0} s");
        Assert.True(backups[^1].AuditRows > backups[0].AuditRows, "the writers must have been running between the first and the last backup");
        Assert.True(writes > 50, $"only {writes} writes happened");
        Console.WriteLine($"live backup consistency: {backups.Count} backups during {writes} concurrent service writes, audit rows {backups[0].AuditRows} -> {backups[^1].AuditRows}; every backup passed integrity_check and the Version/audit invariant");
    }
}
