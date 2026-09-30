using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// Security review SEC-D8 (OWASP API4): any signed-in role, a Viewer included, may queue release reports, run sheets and scorecards, and the worker renders
/// one job at a time. Unbounded, one person could queue a job for every train and kind and hold everyone else's exports behind them. Open (Queued or Running)
/// jobs per requester are capped by <c>Exports:MaxOpenJobsPerUser</c> (Q-SEC-D3); a repeat of an open request still returns that job.
/// </summary>
public sealed class ExportQueueLimitTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Sink : IAlertSink { public Task RaiseAsync(string s, string k, string key, string m, CancellationToken ct = default) => Task.CompletedTask; }

    private (ExportService Jobs, string Path) Env(Dictionary<string, string?> settings)
    {
        var path = fx.FreshPath();
        using (var c = TriggerSuiteFixture.Open(path))
            TriggerSuiteFixture.Run(c, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','R26.11','2026-11-13','Low','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T14:00:00Z"));
        var db = new Factory(path);
        settings["Pdf:QuestPdfLicense"] = "Community";
        settings["Exports:Directory"] = System.IO.Path.Combine(fx.Dir, "exports-" + Guid.NewGuid().ToString("N")[..8]);
        var options = ExportOptions.From(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), fx.Dir, isDevelopment: false);
        return (new ExportService(db, time, options, new SyncAlertWriter(db, time, NullLogger<SyncAlertWriter>.Instance), new Sink(), NullLogger<ExportService>.Instance), path);
    }

    [Fact]
    public async Task One_person_cannot_queue_more_open_exports_than_the_limit()
    {
        var (jobs, _) = Env(new() { ["Exports:MaxOpenJobsPerUser"] = "3" });
        var viewer = new Actor("dev");   // a Viewer: release report, run sheet and scorecard are Read
        Assert.True((await jobs.EnqueueAsync("t1", "ReleaseReport", null, viewer)).IsOk);
        Assert.True((await jobs.EnqueueAsync("t1", "RunSheet", null, viewer)).IsOk);
        Assert.True((await jobs.EnqueueAsync("t1", "Scorecard", null, viewer)).IsOk);
        var fourth = await jobs.EnqueueAsync("t2", "ReleaseReport", null, viewer);
        Assert.False(fourth.IsOk);
        Assert.Equal("ExportQueueFull", fourth.Failures[0].Guard);

        Assert.True((await jobs.EnqueueAsync("t1", "RunSheet", null, viewer)).IsOk);          // the same open request is still the idempotent double click
        Assert.True((await jobs.EnqueueAsync("t2", "ReleaseReport", null, new Actor("rte"))).IsOk);   // other people are not affected
    }

    [Fact]
    public async Task The_default_limit_is_ten_open_jobs()
    {
        var (jobs, path) = Env([]);
        var viewer = new Actor("dev");
        using (var c = TriggerSuiteFixture.Open(path))
            for (var i = 0; i < 4; i++)
                TriggerSuiteFixture.Run(c, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('x{i}','X{i}','2026-11-13','Low','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')");
        var trains = new[] { "t1", "t2", "x0", "x1", "x2", "x3" };
        var n = 0;
        foreach (var t in trains) foreach (var k in new[] { "ReleaseReport", "RunSheet" }) if ((await jobs.EnqueueAsync(t, k, null, viewer)).IsOk) n++;
        Assert.Equal(10, n);
    }
}
