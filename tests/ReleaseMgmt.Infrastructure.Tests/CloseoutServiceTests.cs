using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-36 close-out services against the migrated schema with a test clock (users rte/rm/gov1/dev from the trigger fixture).</summary>
public sealed class CloseoutServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private static readonly Actor Rte = new("rte"), Dev = new("dev");

    private (string Path, FakeTimeProvider Time, TrainLifecycleService Trains, CloseoutService Closeout) Env()
    {
        var path = fx.FreshPath();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 6, 30, 45, TimeSpan.Zero));
        var db = new Factory(path);
        using (var c = TriggerSuiteFixture.Open(path))
            TriggerSuiteFixture.Run(c, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,ActualStartAt,CreatedAt,UpdatedAt) VALUES('t9','R26.12','2026-12-01','Low','Executing','2026-10-30T02:00:00Z','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        return (path, time, new TrainLifecycleService(db, time), new CloseoutService(db, time));
    }

    private static object? Scalar(string path, string sql)
    {
        using var c = TriggerSuiteFixture.Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    [Fact]
    public async Task The_PIR_created_by_the_trigger_audits_with_the_service_actor_and_clock_and_the_service_takes_over()
    {
        var e = Env();
        Assert.True((await e.Trains.CompleteAsync("t9", "Unsuccessful", "rolled back", Rte)).IsOk);
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT OccurredAt FROM AuditEvents WHERE Action='AutoRequired'"));
        Assert.Equal("rte", Scalar(e.Path, "SELECT ActorUserId FROM AuditEvents WHERE Action='AutoRequired'"));

        var pir = (await e.Closeout.GetPirAsync("t9"))!.Pir!;
        Assert.Equal("Required", pir.Status);
        Assert.Equal(1, pir.Version);
        e.Time.Advance(TimeSpan.FromHours(1));
        var held = await e.Closeout.HoldAsync("t9", "Review held; index missing", Rte, expectedVersion: 1);
        Assert.True(held.IsOk);
        Assert.Equal(2, held.Value!.Version);
        Assert.Equal(new DateTime(2026, 10, 30, 7, 30, 45, DateTimeKind.Utc), held.Value.HeldAt);   // from TimeProvider, not the wall clock
        Assert.Equal("2026-10-30T07:30:45Z", Scalar(e.Path, "SELECT OccurredAt FROM AuditEvents WHERE Action='Hold'"));
        Assert.Equal(ResultKind.Conflict, (await e.Closeout.CloseAsync("t9", Rte, expectedVersion: 1)).Kind);
        Assert.True((await e.Closeout.CloseAsync("t9", Rte, expectedVersion: 2)).IsOk);
    }

    [Fact]
    public async Task Action_due_dates_are_judged_by_the_test_clock_and_only_planners_or_the_owner_complete()
    {
        var e = Env();
        await e.Trains.CompleteAsync("t9", "SuccessfulWithIssues", null, Rte);
        var past = await e.Closeout.AddActionAsync("t9", new("x", "dev", new DateOnly(2026, 10, 29)), Rte);
        Assert.Equal(CloseoutGuards.InvalidPirAction, Assert.Single(past.Failures).Guard);
        var ok = await e.Closeout.AddActionAsync("t9", new("Fix index", "dev", new DateOnly(2026, 10, 30)), Rte);   // today is allowed
        Assert.True(ok.IsOk);
        Assert.Equal(CloseoutGuards.CloseoutRole, Assert.Single((await e.Closeout.AddActionAsync("t9", new("y", "dev", new DateOnly(2026, 11, 2)), Dev)).Failures).Guard);
        Assert.True((await e.Closeout.CompleteActionAsync(ok.Value!.Id, Dev, null)).IsOk);   // its owner
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT OccurredAt FROM AuditEvents WHERE Action='Complete' AND EntityType='PirAction'"));
    }
}
