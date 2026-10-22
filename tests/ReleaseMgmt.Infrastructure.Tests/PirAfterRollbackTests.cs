using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-86 (abort with a reason) and REOS-87 (a PIR is required after a rollback or an abort, due within Pir:DueBusinessDays business days of
/// its creation in Display:TimeZone). The test clock starts Friday 30 Oct 2026 06:30 UTC, which is Friday 01:30 in America/Chicago.
/// </summary>
public sealed class PirAfterRollbackTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private static readonly Actor Rte = new("rte");

    private sealed record Env(string Path, FakeTimeProvider Time, TrainLifecycleService Trains, CloseoutService Closeout, RunService Runs);

    private Env Make(string status = "Executing", Dictionary<string, string?>? settings = null)
    {
        var path = fx.FreshPath();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 6, 30, 45, TimeSpan.Zero));
        var db = new Factory(path);
        using (var c = TriggerSuiteFixture.Open(path))
        {
            TriggerSuiteFixture.Run(c, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,ActualStartAt,CreatedAt,UpdatedAt) VALUES('t9','R26.12','2026-12-01','Low','{status}','2026-10-30T02:00:00Z','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
            TriggerSuiteFixture.Run(c, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s9','t9','R-901','Deploy','Deploy','rte','2026-10-30T06:00:00Z',30)");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        return new(path, time, new TrainLifecycleService(db, time), new CloseoutService(db, time, null, config), new RunService(db, time));
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
    public async Task Aborting_keeps_the_reason_in_the_audit_row_and_an_Executing_train_gets_a_PIR_in_the_same_transaction()
    {
        var e = Make();
        var r = await e.Trains.AbortAsync("t9", "  CAB withdrew approval  ", Rte, expectedVersion: 1);
        Assert.True(r.IsOk);
        Assert.Equal("CAB withdrew approval", Scalar(e.Path, "SELECT json_extract(AfterJson,'$.reason') FROM AuditEvents WHERE Action='Abort'"));
        Assert.Equal("Aborted", Scalar(e.Path, "SELECT RequiredReason FROM PostImplementationReviews WHERE ReleaseTrainId='t9'"));
        Assert.Equal("rte", Scalar(e.Path, "SELECT ActorUserId FROM AuditEvents WHERE Action='AutoRequired' AND EntityType='PostImplementationReview'"));
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT OccurredAt FROM AuditEvents WHERE Action='AutoRequired'"));
    }

    [Theory]
    [InlineData("Planning")]
    [InlineData("Gated")]
    public async Task Aborting_before_Executing_requires_no_PIR_and_the_reason_is_optional(string status)
    {
        var e = Make(status);
        Assert.True((await e.Trains.AbortAsync("t9", Rte)).IsOk);
        Assert.Equal(0L, Scalar(e.Path, "SELECT count(*) FROM PostImplementationReviews"));
        Assert.Null(Scalar(e.Path, "SELECT json_extract(AfterJson,'$.reason') FROM AuditEvents WHERE Action='Abort'"));
    }

    [Fact]
    public async Task A_live_run_rolled_back_requires_a_PIR_once_and_aborting_afterwards_keeps_that_one()
    {
        var e = Make();
        var run = (await e.Runs.StartRunAsync("t9", "Live", Rte)).Value!;
        Assert.True((await e.Runs.EndRunAsync(run.Id, "RolledBack", Rte)).IsOk);
        Assert.Equal("RollbackPerformed", Scalar(e.Path, "SELECT RequiredReason FROM PostImplementationReviews WHERE ReleaseTrainId='t9'"));

        Assert.True((await e.Trains.AbortAsync("t9", "rolled back, not retrying", Rte)).IsOk);
        Assert.Equal(1L, Scalar(e.Path, "SELECT count(*) FROM PostImplementationReviews"));
        Assert.Equal("RollbackPerformed", Scalar(e.Path, "SELECT RequiredReason FROM PostImplementationReviews"));
        Assert.Equal(1L, Scalar(e.Path, "SELECT count(*) FROM AuditEvents WHERE Action='AutoRequired'"));
    }

    [Theory]
    [InlineData("Rehearsal", "RolledBack")]
    [InlineData("Live", "Aborted")]
    [InlineData("Live", "Completed")]
    public async Task Only_a_live_run_that_rolled_back_requires_a_PIR(string mode, string outcome)
    {
        var e = Make();
        var run = (await e.Runs.StartRunAsync("t9", mode, Rte)).Value!;
        if (outcome == "Completed")
        {
            var step = (await e.Runs.StartStepAsync(run.Id, "s9", null, Rte)).Value!;
            Assert.True((await e.Runs.DoneStepAsync(run.Id, "s9", null, Rte, step.Version)).IsOk);
        }
        Assert.True((await e.Runs.EndRunAsync(run.Id, outcome, Rte)).IsOk);
        Assert.Equal(0L, Scalar(e.Path, "SELECT count(*) FROM PostImplementationReviews"));
    }

    [Fact]
    public async Task The_due_date_is_one_business_day_after_creation_in_the_display_zone_and_overdue_only_after_that_day_ends()
    {
        var e = Make();
        Assert.True((await e.Trains.AbortAsync("t9", "abandoned", Rte)).IsOk);   // Friday 30 Oct, 01:30 in Chicago

        var v = (await e.Closeout.GetPirAsync("t9"))!;
        Assert.Equal(new DateOnly(2026, 11, 2), v.DueOn);   // the weekend is skipped: Monday
        Assert.False(v.Overdue);

        e.Time.SetUtcNow(new DateTimeOffset(2026, 11, 3, 5, 59, 0, TimeSpan.Zero));   // Monday 23:59 in Chicago (CST from 1 Nov): still due today
        Assert.False((await e.Closeout.GetPirAsync("t9"))!.Overdue);
        e.Time.SetUtcNow(new DateTimeOffset(2026, 11, 3, 6, 1, 0, TimeSpan.Zero));    // Tuesday 00:01
        var late = (await e.Closeout.GetPirAsync("t9"))!;
        Assert.True(late.Overdue);

        Assert.True((await e.Closeout.ScheduleAsync("t9", Rte, late.Pir!.Version)).IsOk);   // scheduling does not move the date
        Assert.True((await e.Closeout.GetPirAsync("t9"))!.Overdue);
        Assert.True((await e.Closeout.HoldAsync("t9", "Held the review", Rte, 2)).IsOk);
        Assert.False((await e.Closeout.GetPirAsync("t9"))!.Overdue);
    }

    [Fact]
    public async Task Holidays_and_the_configured_number_of_days_move_the_due_date()
    {
        var e = Make(settings: new() { ["Pir:DueBusinessDays"] = "2" });
        using (var c = TriggerSuiteFixture.Open(e.Path)) TriggerSuiteFixture.Run(c, "INSERT INTO Holidays(Day,Name) VALUES('2026-11-02','Closure')");
        Assert.True((await e.Trains.AbortAsync("t9", "abandoned", Rte)).IsOk);
        Assert.Equal(new DateOnly(2026, 11, 4), (await e.Closeout.GetPirAsync("t9"))!.DueOn);   // Mon is a holiday: Tue, Wed
    }

    [Fact]
    public async Task A_PIR_for_a_close_code_or_started_by_hand_has_no_due_date_and_an_Aborted_train_may_start_one()
    {
        var e = Make();
        Assert.True((await e.Trains.CompleteAsync("t9", "Unsuccessful", null, Rte)).IsOk);
        var v = (await e.Closeout.GetPirAsync("t9"))!;
        Assert.Equal("CloseCode=Unsuccessful", v.Pir!.RequiredReason);
        Assert.Null(v.DueOn);

        var g = Make("Gated");
        Assert.True((await g.Trains.AbortAsync("t9", Rte)).IsOk);
        var manual = await g.Closeout.CreateManualPirAsync("t9", Rte);
        Assert.True(manual.IsOk);
        Assert.Null((await g.Closeout.GetPirAsync("t9"))!.DueOn);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("one")]
    public void A_bad_due_days_setting_fails_fast(string value) =>
        Assert.Throws<InvalidOperationException>(() => PirDue.BusinessDaysFrom(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Pir:DueBusinessDays"] = value }).Build()));
}
