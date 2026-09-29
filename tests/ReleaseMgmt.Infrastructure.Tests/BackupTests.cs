using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Backup;

namespace ReleaseMgmt.Infrastructure.Tests;

public class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-bk-").FullName;
    private string Db => Path.Combine(_dir, "live.db");
    private string Conn => $"Data Source={Db};Pooling=False";

    public BackupTests()
    {
        using var c = new SqliteConnection(Conn); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE T (V TEXT); INSERT INTO T VALUES ('hello');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class RecordingAlerts : IAlertSink
    {
        public List<(string Source, string Kind)> Raised { get; } = [];
        public Task RaiseAsync(string s, string k, string key, string m, CancellationToken ct = default) { Raised.Add((s, k)); return Task.CompletedTask; }
    }

    [Fact]
    public void Backup_then_restore_yields_an_intact_copy()
    {
        var file = BackupRunner.Backup(Conn, Path.Combine(_dir, "bk"), new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc));
        var target = Path.Combine(_dir, "restored", "r.db");
        BackupRunner.Restore(file, target);
        Assert.Equal("ok", BackupRunner.IntegrityCheck(target));
        using var c = new SqliteConnection($"Data Source={target};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT V FROM T";
        Assert.Equal("hello", cmd.ExecuteScalar());
    }

    [Fact]
    public void Restore_refuses_a_corrupt_backup()
    {
        var bad = Path.Combine(_dir, "bad.db");
        File.WriteAllText(bad, "not a database");
        Assert.ThrowsAny<Exception>(() => BackupRunner.Restore(bad, Path.Combine(_dir, "t.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "t.db")));
    }

    [Fact]
    public void Prune_keeps_frequent_backups_2_days_and_nightlies_30_days()
    {
        var bk = Path.Combine(_dir, "bk"); Directory.CreateDirectory(bk);
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        string Make(string name, int ageDays) { var p = Path.Combine(bk, name); File.WriteAllText(p, "x"); File.SetLastWriteTimeUtc(p, now.AddDays(-ageDays)); return p; }
        var oldFreq = Make("releasemgmt-a.db", 3);
        var newFreq = Make("releasemgmt-b.db", 1);
        var oldNightly = Make("releasemgmt-nightly-c.db", 31);
        var newNightly = Make("releasemgmt-nightly-d.db", 20);
        Assert.Equal(2, BackupRunner.Prune(bk, now));
        Assert.False(File.Exists(oldFreq)); Assert.True(File.Exists(newFreq));
        Assert.False(File.Exists(oldNightly)); Assert.True(File.Exists(newNightly));
    }

    [Fact]
    public async Task Service_writes_frequent_and_one_nightly_per_day()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));
        var alerts = new RecordingAlerts();
        var opts = new BackupOptions(Conn, Path.Combine(_dir, "bk"), TimeSpan.FromMinutes(15));
        var svc = new BackupService(opts, time, alerts, NullLogger<BackupService>.Instance);
        await svc.RunOnceAsync();
        time.Advance(TimeSpan.FromMinutes(15));
        await svc.RunOnceAsync();
        var files = Directory.GetFiles(opts.Directory).Select(Path.GetFileName).ToList();
        Assert.Equal(2, files.Count(f => !f!.Contains("nightly")));
        Assert.Single(files, f => f!.Contains("nightly"));
        Assert.Empty(alerts.Raised);
        Assert.NotNull(svc.LastSuccessUtc);
    }

    [Fact]
    public async Task Failed_backup_raises_a_BackupFailed_alert()
    {
        var alerts = new RecordingAlerts();
        var opts = new BackupOptions($"Data Source={Path.Combine(_dir, "missing", "nope.db")};Mode=ReadOnly;Pooling=False", Path.Combine(_dir, "bk"), TimeSpan.FromMinutes(15));
        var svc = new BackupService(opts, new FakeTimeProvider(DateTimeOffset.UtcNow), alerts, NullLogger<BackupService>.Instance);
        await svc.RunOnceAsync();
        Assert.Equal([("Backup", "BackupFailed")], alerts.Raised);
        Assert.Null(svc.LastSuccessUtc);
    }
}
