using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-41: a stopped poller produces a SyncEngine/Stalled alert within three intervals, and only a stopped poller does.</summary>
public sealed class SyncWatchdogTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static List<object?[]> Stalled(Env e) => e.Query("SELECT SourceSystem,Kind,OccurrenceCount,IsResolved,ReleaseTrainId,ErrorMessage FROM SyncAlerts WHERE Kind='Stalled' ORDER BY FirstOccurredAt");

    private Env Polled()
    {
        var e = NewEnv(fx); e.AddSnLink(); e.SnAnswers();
        return e;
    }

    [Fact]
    public async Task A_stopped_poller_alerts_within_three_intervals_and_a_cycle_resolves_it_with_history_kept()
    {
        var e = Polled();
        await e.Poller.RunOnceAsync();                                    // the last heartbeat, 02:30
        var dog = e.NewWatchdog();                                        // (the poller now "stops": nothing runs it again)

        e.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(await dog.CheckOnceAsync());                         // 14 min: still inside three intervals
        Assert.Empty(Stalled(e));

        e.Time.Advance(TimeSpan.FromMinutes(1));                          // 15 min = three intervals
        Assert.Equal(["ServiceNow"], await dog.CheckOnceAsync());
        var row = Assert.Single(Stalled(e));
        Assert.Equal(["SyncEngine", "Stalled", 1L, 0L, null], row[..5]);   // system-wide: shows on the banner
        Assert.Contains("No ServiceNow sync cycle has completed since 2026-10-30 02:30:00Z", (string)row[5]!);
        Assert.Contains("poller is stopped or stuck", (string)row[5]!);
        Assert.True(e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='SyncAlert'") >= 2);   // RTE and Release Manager are told
        Assert.Contains(e.Sink.Raised, x => x is ("SyncEngine", "Stalled", "poller:ServiceNow", _));

        e.Time.Advance(TimeSpan.FromMinutes(1));
        await dog.CheckOnceAsync();
        Assert.Equal(2L, Assert.Single(Stalled(e))[2]);                   // repeated checks bump the one row

        await e.Poller.RunOnceAsync();                                    // the poller is back
        Assert.Empty(await dog.CheckOnceAsync());
        var after = Assert.Single(Stalled(e));
        Assert.Equal([2L, 1L], after[2..4]);                              // resolved, count and row kept
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE Action='AutoResolved' AND EntityType='SyncAlert'"));
    }

    [Fact]
    public async Task A_poller_that_fails_against_the_itsm_is_alive_and_is_not_reported_stalled()
    {
        var e = Polled(); e.Http.Always(401);
        var dog = e.NewWatchdog();
        for (var i = 0; i < 12; i++)
        {
            await e.Poller.RunOnceAsync();
            e.Time.Advance(Interval);
            Assert.Empty(await dog.CheckOnceAsync());
        }
        Assert.Empty(Stalled(e));
        Assert.Equal(12, e.Count("SELECT OccurrenceCount FROM SyncAlerts WHERE Kind='AuthFailed'"));   // the connector alert is separate
    }

    [Fact]
    public async Task A_restart_after_long_downtime_is_not_a_stall_until_three_intervals_after_the_start()
    {
        var e = Polled();
        e.Sql("UPDATE ConnectorState SET LastCycleCompletedAt='2026-10-28T00:00:00Z'");   // the app was down for two days
        var dog = e.NewWatchdog();
        Assert.Empty(await dog.CheckOnceAsync());
        e.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(await dog.CheckOnceAsync());
        e.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["ServiceNow"], await dog.CheckOnceAsync());         // the poller never came back
        Assert.Contains("since 2026-10-28 00:00:00Z", (string)Stalled(e)[0][5]!);
    }

    [Fact]
    public async Task While_a_deployment_window_has_been_open_the_threshold_is_three_one_minute_intervals()
    {
        var e = Polled();
        await e.Poller.RunOnceAsync();                                    // heartbeat 02:30
        e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w','t1','2026-10-30T02:20:00Z','2026-10-30T09:00:00Z')");   // open since 02:20
        var dog = e.NewWatchdog();
        e.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(await dog.CheckOnceAsync());                         // 2 min < 3 x 1 min
        e.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["ServiceNow"], await dog.CheckOnceAsync());         // 3 min: three fast intervals
    }

    [Fact]
    public async Task A_window_that_just_opened_keeps_the_slow_threshold_because_the_last_cycle_may_be_five_minutes_old()
    {
        var e = Polled();
        await e.Poller.RunOnceAsync();                                    // 02:30
        var dog = e.NewWatchdog();
        e.Time.Advance(TimeSpan.FromMinutes(4));                          // 02:34
        e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w','t1','2026-10-30T02:33:00Z','2026-10-30T09:00:00Z')");   // opened a minute ago
        Assert.Empty(await dog.CheckOnceAsync());
    }

    [Fact]
    public async Task Disabled_connectors_and_an_install_with_no_connectors_are_not_watched()
    {
        var e = Polled();
        await e.Poller.RunOnceAsync();
        e.Sql("UPDATE ConnectorState SET IsEnabled=0");
        var dog = e.NewWatchdog();
        e.Time.Advance(TimeSpan.FromHours(3));
        Assert.Empty(await dog.CheckOnceAsync());

        var none = NewEnv(fx, serviceNow: false);
        var dog2 = none.NewWatchdog();
        none.Time.Advance(TimeSpan.FromHours(3));
        Assert.Empty(await dog2.CheckOnceAsync());
        Assert.Empty(Stalled(none));
    }

    [Fact]
    public async Task Each_connector_is_judged_on_its_own_heartbeat()
    {
        var e = NewEnv(fx, jira: true); e.AddSnLink(); e.SnAnswers();
        e.AddJiraLink("j1", "PAY-12", "Train", "t1");
        await e.Poller.RunOnceAsync();
        var dog = e.NewWatchdog();
        e.Time.Advance(TimeSpan.FromMinutes(10));
        e.Sql("UPDATE ConnectorState SET LastCycleCompletedAt='2026-10-30T02:40:00Z' WHERE SourceSystem='ServiceNow'");   // only ServiceNow keeps beating
        e.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(["Jira"], await dog.CheckOnceAsync());
        Assert.Equal("Jira", Assert.Single(Stalled(e))[5]!.ToString()!.Split(' ')[1]);
    }
}
