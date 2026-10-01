using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-72 / Q-050c (decided 2026-09-30): generated export files are kept Exports:RetentionDays (365) days, then deleted; the job row and its audit trail stay.</summary>
public sealed class ExportRetentionTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Sink : IAlertSink
    {
        public List<(string Source, string Kind, string Key, string Message)> Raised { get; } = [];
        public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default) { Raised.Add((sourceSystem, kind, key, message)); return Task.CompletedTask; }
    }

    private sealed record Env(string Path, string Dir, FakeTimeProvider Time, ExportService Jobs, ExportWorker Worker, Sink Alerts)
    {
        public object? Scalar(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(Path); using var cmd = c.CreateCommand();
            TriggerSuiteFixture.Bind(cmd, sql, p);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(Path); TriggerSuiteFixture.Run(c, sql, p); }
        public long Count(string sql, params object?[] p) => Convert.ToInt64(Scalar(sql, p));
        public string? StoragePath(string id) => (string?)Scalar("SELECT StoragePath FROM ExportJobs WHERE Id=?", id);
        public void At(string utc) => Time.SetUtcNow(DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    private Env NewEnv(int retentionDays = ExportOptions.DefaultRetentionDays)
    {
        var path = fx.FreshPath();
        var dir = Path.Combine(fx.Dir, "exports-" + Guid.NewGuid().ToString("N")[..8]);
        var time = new FakeTimeProvider(); time.SetUtcNow(DateTimeOffset.Parse("2026-10-20T14:00:00Z"));
        var db = new Factory(path);
        var options = new ExportOptions(dir, "Community", true, 5, TimeSpan.FromSeconds(300), 2, 5000, null, RetentionDays: retentionDays);
        var sink = new Sink();
        var alerts = new SyncAlertWriter(db, time, NullLogger<SyncAlertWriter>.Instance);
        var jobs = new ExportService(db, time, options, alerts, sink, NullLogger<ExportService>.Instance);
        var loader = new ExportModelLoader(db, new AuditQueryService(db), new DisplayClock(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")), options);
        var store = new AttachmentService(db, time, new AttachmentOptions(Path.Combine(fx.Dir, "att-" + Guid.NewGuid().ToString("N")[..8]), 1_000_000), sink);
        var snaps = new MetricSnapshotService(db, time, new AnalyticsService(new SqliteAnalyticsConnectionFactory(path)));
        return new Env(path, dir, time, jobs, new ExportWorker(jobs, loader, snaps, store, options, time, NullLogger<ExportWorker>.Instance), sink);
    }

    private static async Task<string> Rendered(Env e, string kind = "EvidencePack", string? format = "zip")
    {
        var r = await e.Jobs.EnqueueAsync("t1", kind, format, new Actor("rte"));
        Assert.True(r.IsOk, r.Failures.FirstOrDefault()?.Message);
        await e.Worker.RunOnceAsync();
        Assert.Equal("Done", e.Scalar("SELECT Status FROM ExportJobs WHERE Id=?", r.Value!.Id));
        return r.Value.Id;
    }

    [Fact]
    public async Task A_file_older_than_the_retention_is_deleted_and_its_job_marked_expired_but_the_record_and_audit_trail_stay()
    {
        var e = NewEnv();
        var old = await Rendered(e);                                   // an evidence pack ZIP completed 2026-10-20
        var oldFile = Path.Combine(e.Dir, e.StoragePath(old)!);
        e.At("2027-10-15T00:00:00Z");
        var young = await Rendered(e, "ReleaseReport", null);           // completed 2027-10-15
        var youngFile = Path.Combine(e.Dir, e.StoragePath(young)!);
        var auditBefore = e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityId=?", old);
        var sha = (string)e.Scalar("SELECT Sha256 FROM ExportJobs WHERE Id=?", old)!;

        e.At("2027-10-20T13:59:59Z");                                  // one second short of 365 days: nothing yet
        Assert.Empty((await e.Jobs.PurgeExpiredAsync()).Expired);
        Assert.True(File.Exists(oldFile));

        e.At("2027-10-20T14:00:01Z");
        var r = await e.Jobs.PurgeExpiredAsync();
        Assert.Equal([old], r.Expired);
        Assert.Empty(r.Failed);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(youngFile));                            // younger than the retention: untouched
        Assert.Equal("Done", e.Scalar("SELECT Status FROM ExportJobs WHERE Id=?", old));   // the export was produced; only its file is gone
        Assert.Null(e.StoragePath(old));
        Assert.Equal(sha, e.Scalar("SELECT Sha256 FROM ExportJobs WHERE Id=?", old));   // the hash stays as the record of what was handed out
        var p = JsonDocument.Parse((string)e.Scalar("SELECT Parameters FROM ExportJobs WHERE Id=?", old)!).RootElement;
        Assert.Equal("2027-10-20T14:00:01Z", p.GetProperty("expiredAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal(4L, e.Count("SELECT Version FROM ExportJobs WHERE Id=?", old));   // Queued 1, Running 2, Done 3, Expired 4
        Assert.Equal(auditBefore + 1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityId=?", old));   // nothing deleted, one row added
        var audit = (string)e.Scalar("SELECT AfterJson FROM AuditEvents WHERE EntityType='ExportJob' AND EntityId=? AND Action='Expire'", old)!;
        Assert.Contains("365", audit);
        Assert.Equal("2027-10-20T14:00:01Z", e.Scalar("SELECT OccurredAt FROM AuditEvents WHERE EntityId=? AND Action='Expire'", old));   // the injected clock
        Assert.Equal("t1", e.Scalar("SELECT ReleaseTrainId FROM AuditEvents WHERE EntityId=? AND Action='Expire'", old));   // in the train's audit log

        // the download is refused with a readable reason (the endpoint answers 410 Gone)
        var file = await e.Jobs.FindFileAsync(old);
        Assert.Equal(ExportGuards.Expired, file.Failures.Single().Guard);
        Assert.Contains("365 days", file.Failures[0].Message);
        Assert.Contains("Exports:RetentionDays", file.Failures[0].Message);
        Assert.Equal("2027-10-20T14:00:01Z", (await e.Jobs.GetAsync(old))!.ExpiredAt!.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.True((await e.Jobs.FindFileAsync(young)).IsOk);

        // idempotent: a second pass finds nothing
        Assert.Empty((await e.Jobs.PurgeExpiredAsync()).Expired);
        Assert.Equal(auditBefore + 1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityId=?", old));
        Assert.Empty(e.Alerts.Raised);
    }

    [Fact]
    public async Task The_retention_is_configurable_and_a_bad_value_stops_the_start_naming_the_key()
    {
        static ExportOptions From(string? days) => ExportOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Exports:RetentionDays"] = days }).Build(), Path.GetTempPath(), false);
        Assert.Equal(365, From(null).RetentionDays);
        Assert.Equal(30, From("30").RetentionDays);
        foreach (var bad in new[] { "0", "-5", "a year", "1.5" })
            Assert.Contains("Exports:RetentionDays", Assert.Throws<InvalidOperationException>(() => From(bad)).Message);

        var e = NewEnv(retentionDays: 30);
        var id = await Rendered(e, "Scorecard", null);
        e.At("2026-11-19T14:00:01Z");
        Assert.Equal([id], (await e.Jobs.PurgeExpiredAsync()).Expired);
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_raises_an_alert_and_leaves_its_job_as_it_was()
    {
        var e = NewEnv();
        var id = await Rendered(e, "RunSheet", null);
        e.Sql("UPDATE ExportJobs SET StoragePath='../outside.pdf' WHERE Id=?", id);   // a hand-edited row: the purge never deletes outside the exports directory
        e.At("2027-11-01T00:00:00Z");
        var r = await e.Jobs.PurgeExpiredAsync();
        Assert.Equal([id], r.Failed);
        Assert.Equal("../outside.pdf", e.StoragePath(id));
        Assert.Contains(e.Alerts.Raised, a => a is { Source: "Export", Kind: "ExportFailed" } && a.Message.Contains("retention"));
        Assert.Equal(1L, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed' AND Fingerprint IS NOT NULL AND IsResolved=0"));
    }

    [Fact]
    public async Task The_worker_purges_at_start_and_then_at_most_once_an_hour_by_the_injected_clock()
    {
        var e = NewEnv();
        var first = await Rendered(e, "Scorecard", null);                // completed 2026-10-20T14:00
        e.At("2026-10-21T00:00:00Z");
        var second = await Rendered(e, "Scorecard", null);               // completed 2026-10-21T00:00
        e.At("2027-10-20T23:30:00Z");
        await e.Worker.PurgeIfDueAsync();                                // the first pass (as at start-up)
        Assert.Null(e.StoragePath(first));
        Assert.NotNull(e.StoragePath(second));                           // not yet 365 days old
        e.At("2027-10-21T00:10:00Z");                                    // second is now past retention, but the last pass was 40 min ago
        await e.Worker.PurgeIfDueAsync();
        Assert.NotNull(e.StoragePath(second));
        e.At("2027-10-21T00:30:00Z");                                    // an hour after the last pass: due
        await e.Worker.PurgeIfDueAsync();
        Assert.Null(e.StoragePath(second));
    }
}
