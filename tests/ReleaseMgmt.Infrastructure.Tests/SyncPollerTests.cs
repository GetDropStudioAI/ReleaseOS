using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-40 acceptance on a fake clock and recorded HTTP: 401, 404 and timeout each alert within one interval; 429 alerts within three; 288 identical failures leave one
/// row with count 288; one clean cycle resolves and keeps history. REOS-41: mismatch rules raise warnings only.
/// </summary>
public sealed class SyncPollerTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private record AlertRow(string Id, string Source, string Kind, long Count, bool Resolved, string? Train, string Message, string? ResolvedAt);

    private static List<AlertRow> Alerts(Env e, string? kind = null) =>
        [.. e.Query("SELECT Id,SourceSystem,Kind,OccurrenceCount,IsResolved,ReleaseTrainId,ErrorMessage,ResolvedAt FROM SyncAlerts WHERE (? IS NULL OR Kind=?) ORDER BY FirstOccurredAt,Id", kind, kind)
            .Select(r => new AlertRow((string)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, (long)r[4]! == 1, (string?)r[5], (string)r[6]!, (string?)r[7]))];

    private Env Sn(Action<Env>? seed = null, Dictionary<string, string?>? cfg = null)
    {
        var e = NewEnv(fx, cfg: cfg);
        e.AddSnLink();
        seed?.Invoke(e);
        return e;
    }

    // ---- a clean cycle ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_clean_cycle_stamps_the_link_and_the_connector_bookkeeping()
    {
        var e = Sn(); e.SnAnswers("-2");
        var r = await e.Poller.RunOnceAsync();

        Assert.Equal(ConnectorCycleResult.Clean, r.Single(x => x.Source == "ServiceNow").Outcome);
        Assert.Equal(ConnectorCycleResult.Skipped, r.Single(x => x.Source == "Jira").Outcome);   // no ConnectorState row, nothing to do
        Assert.Equal("InSync", e.Link("l1", "SyncState"));
        Assert.Equal("Scheduled", e.Link("l1", "LastSyncedStatus"));
        Assert.Equal("2026-10-30T02:30:00Z", e.Link("l1", "LastSyncedAt"));
        var s = e.Query("SELECT LastCycleStartedAt,LastCycleCompletedAt,LastSuccessAt,ConsecutiveFailures FROM ConnectorState WHERE SourceSystem='ServiceNow'")[0];
        Assert.Equal(["2026-10-30T02:30:00Z", "2026-10-30T02:30:00Z", "2026-10-30T02:30:00Z", 0L], s);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal(1, e.Http.Count);
    }

    [Fact]
    public async Task Links_of_archived_trains_and_disabled_connectors_are_not_polled()
    {
        var e = Sn(x => x.Sql("UPDATE ReleaseTrains SET ArchivedAt='2026-10-01T00:00:00Z' WHERE Id='t1'")); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        Assert.Equal(0, e.Http.Count);

        e.Sql("UPDATE ReleaseTrains SET ArchivedAt=NULL WHERE Id='t1'; UPDATE ConnectorState SET IsEnabled=0 WHERE SourceSystem='ServiceNow'");
        await e.Poller.RunOnceAsync();
        Assert.Equal(0, e.Http.Count);
    }

    [Fact]
    public async Task A_connector_with_no_links_makes_no_call_raises_nothing_but_still_completes_a_cycle()
    {
        var e = NewEnv(fx); e.Creds.Items.Clear();   // configured, but no credentials and nothing linked
        var r = await e.Poller.RunOnceAsync();
        Assert.Equal(ConnectorCycleResult.Idle, r.Single(x => x.Source == "ServiceNow").Outcome);
        Assert.Equal(0, e.Http.Count);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal("2026-10-30T02:30:00Z", e.Query("SELECT LastCycleCompletedAt FROM ConnectorState")[0][0]);
        Assert.Null(e.Query("SELECT LastSuccessAt FROM ConnectorState")[0][0]);   // idle proves nothing about the connector
    }

    [Fact]
    public async Task A_configured_base_url_creates_the_connector_row_without_an_admin_visit()
    {
        var e = NewEnv(fx, serviceNow: false, cfg: new() { ["Connectors:ServiceNow:BaseUrl"] = SnUrl });
        e.Creds.Items["ServiceNow"] = new ConnectorCredentials("Basic", "u", Secret);
        e.AddSnLink(); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        Assert.Equal(SnUrl, e.Query("SELECT BaseUrl FROM ConnectorState WHERE SourceSystem='ServiceNow'")[0][0]);
        Assert.Equal("InSync", e.Link("l1", "SyncState"));
    }

    // ---- 401 / 404 / timeout alert within one interval ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_401_alerts_in_the_first_cycle_marks_links_AuthFailed_and_keeps_LastSyncedAt()
    {
        var e = Sn(); e.SnAnswers(); await e.Poller.RunOnceAsync();                        // synced once...
        e.Time.Advance(Interval);
        e.Http.Always(401, """{"error":{"message":"User Not Authenticated"}}"""); await e.Poller.RunOnceAsync();   // ...then the token is revoked

        var a = Assert.Single(Alerts(e));
        Assert.Equal(("ServiceNow", "AuthFailed", 1L, false, (string?)null), (a.Source, a.Kind, a.Count, a.Resolved, a.Train));   // system-wide: no train, so the banner shows
        Assert.Contains("refused the credentials (HTTP 401)", a.Message);
        Assert.DoesNotContain("service-now.com", a.Message);
        Assert.Equal("AuthFailed", e.Link("l1", "SyncState"));
        Assert.Equal("2026-10-30T02:30:00Z", e.Link("l1", "LastSyncedAt"));                 // stamped only on success
        Assert.Equal(1, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));
        Assert.Equal("2026-10-30T02:35:00Z", e.Query("SELECT LastCycleCompletedAt FROM ConnectorState")[0][0]);   // the poller is alive
        Assert.Equal("2026-10-30T02:30:00Z", e.Query("SELECT LastSuccessAt FROM ConnectorState")[0][0]);
        Assert.Contains(e.Sink.Raised, x => x is ("ServiceNow", "AuthFailed", "connector", _));
        Assert.True(e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='SyncAlert'") >= 2);   // RTE and Release Manager are told in-app
        Assert.Contains(a.Id, e.Realtime.Alerts);                                             // and every client is pushed SyncAlertRaised
    }

    [Fact]
    public async Task A_403_is_an_auth_failure_too()
    {
        var e = Sn(); e.Http.Always(403);
        await e.Poller.RunOnceAsync();
        Assert.Equal("AuthFailed", Assert.Single(Alerts(e)).Kind);
    }

    [Fact]
    public async Task A_404_alerts_for_that_link_only_and_the_rest_of_the_cycle_carries_on()
    {
        var e = Sn(x => x.AddSnLink("l2", "CHG0030002"));
        e.Http.Respond = (req, _) => Task.FromResult(Uri.UnescapeDataString(req.RequestUri!.Query).Contains("CHG0030001") ? Json(200, SnEmpty) : Json(200, SnChange("-1")));
        await e.Poller.RunOnceAsync();

        var a = Assert.Single(Alerts(e));
        Assert.Equal(("NotFound", "t1"), (a.Kind, a.Train));
        Assert.Contains("CHG0030001", a.Message);
        Assert.Equal("NotFound", e.Link("l1", "SyncState"));
        Assert.Equal("InSync", e.Link("l2", "SyncState"));
        Assert.Equal(0, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));   // the connector itself is fine
        Assert.NotNull(e.Query("SELECT LastSuccessAt FROM ConnectorState")[0][0]);
    }

    [Fact]
    public async Task A_404_from_the_http_layer_is_classified_the_same_way()
    {
        var e = Sn(); e.Http.Always(404);
        await e.Poller.RunOnceAsync();
        Assert.Equal("NotFound", Assert.Single(Alerts(e)).Kind);
    }

    [Fact]
    public async Task A_timeout_alerts_Unreachable_in_the_first_cycle_and_stops_the_cycle()
    {
        var e = Sn(x => x.AddSnLink("l2", "CHG0030002"));
        e.Http.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(200, "{}"); };
        await e.Poller.RunOnceAsync();

        var a = Assert.Single(Alerts(e));
        Assert.Equal(("ServiceNow", "Unreachable", 1L, (string?)null), (a.Source, a.Kind, a.Count, a.Train));
        Assert.Contains("did not answer within 0.3 s", a.Message);
        Assert.Equal(1, e.Http.Count);                        // the second link was not tried against a dead host
        Assert.Equal(1, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));
        Assert.Equal("Unsynced", e.Link("l1", "SyncState"));  // unreachable says nothing about the link itself
    }

    [Fact]
    public async Task Server_errors_and_network_failures_are_Unreachable()
    {
        var e = Sn(); e.Http.Always(503);
        await e.Poller.RunOnceAsync();
        Assert.Equal("Unreachable", Assert.Single(Alerts(e)).Kind);

        e.Time.Advance(Interval);
        e.Http.Respond = (_, _) => throw new HttpRequestException("connection refused");
        await e.Poller.RunOnceAsync();
        var a = Assert.Single(Alerts(e));                     // same fingerprint: one row
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public async Task Missing_credentials_are_an_AuthFailed_alert_not_silence()
    {
        var e = Sn(); e.Creds.Items.Clear();
        await e.Poller.RunOnceAsync();
        var a = Assert.Single(Alerts(e));
        Assert.Equal("AuthFailed", a.Kind);
        Assert.Contains("No ServiceNow credentials", a.Message);
        Assert.Equal(0, e.Http.Count);
    }

    [Fact]
    public async Task A_base_url_that_is_not_allowed_is_reported_and_never_called()
    {
        var e = Sn(x => x.Sql("UPDATE ConnectorState SET BaseUrl='http://acme.service-now.com'")); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        Assert.Equal(0, e.Http.Count);
        Assert.Contains("must use https", Assert.Single(Alerts(e, "Unreachable")).Message);
    }

    // ---- 429 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_429_backs_off_and_alerts_only_after_three_consecutive_cycles_within_three_intervals()
    {
        var e = Sn(); e.Http.Always(429);

        await e.Poller.RunOnceAsync();                                    // cycle 1
        Assert.Empty(Alerts(e));
        Assert.Equal(1, e.Http.Count);
        var again = await e.Poller.RunOnceAsync();                        // still inside the 30 s backoff: no request at all
        Assert.Equal(ConnectorCycleResult.Backoff, again.Single(x => x.Source == "ServiceNow").Outcome);
        Assert.Equal(1, e.Http.Count);

        e.Time.Advance(Interval); await e.Poller.RunOnceAsync();          // cycle 2
        Assert.Empty(Alerts(e));
        e.Time.Advance(Interval); await e.Poller.RunOnceAsync();          // cycle 3: within three intervals of the first failure
        var a = Assert.Single(Alerts(e));
        Assert.Equal(("RateLimited", 1L, (string?)null), (a.Kind, a.Count, a.Train));
        Assert.Equal(3, e.Http.Count);
        Assert.Equal(3, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));

        e.Time.Advance(Interval); await e.Poller.RunOnceAsync();
        Assert.Equal(2, Assert.Single(Alerts(e)).Count);                  // the fourth repeats it, never a new row
    }

    [Fact]
    public async Task A_success_between_429s_restarts_the_count()
    {
        var e = Sn();
        e.Http.Always(429);
        await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);
        e.SnAnswers(); await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);   // clean
        e.Http.Always(429);
        await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync();
        Assert.Empty(Alerts(e));                                          // 2 + 2 is not 3 consecutive
    }

    [Fact]
    public async Task The_backoff_is_exponential_honours_Retry_After_and_never_outlasts_one_interval()
    {
        var e = Sn(cfg: new() { ["Sync:BackoffBaseSeconds"] = "30" });
        e.Http.Always(() => Json(429, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(100))));
        await e.Poller.RunOnceAsync();
        e.Time.Advance(TimeSpan.FromSeconds(99));
        Assert.Equal(ConnectorCycleResult.Backoff, (await e.Poller.RunOnceAsync()).Single(x => x.Source == "ServiceNow").Outcome);
        e.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ConnectorCycleResult.Failed, (await e.Poller.RunOnceAsync()).Single(x => x.Source == "ServiceNow").Outcome);   // Retry-After elapsed: tried again

        e.Http.Always(() => Json(429, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(3))));
        e.Time.Advance(TimeSpan.FromSeconds(200));
        await e.Poller.RunOnceAsync();
        e.Time.Advance(Interval);                                         // a 3 h Retry-After is capped at the 5 min interval
        Assert.Equal(ConnectorCycleResult.Failed, (await e.Poller.RunOnceAsync()).Single(x => x.Source == "ServiceNow").Outcome);
    }

    // ---- 288 identical failures ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Two_hundred_eighty_eight_identical_failures_leave_one_row_with_count_288()
    {
        var e = Sn(); e.Http.Always(401);
        for (var i = 0; i < 288; i++)                                     // one day of 5-minute cycles
        {
            await e.Poller.RunOnceAsync();
            e.Time.Advance(Interval);
        }
        var a = Assert.Single(Alerts(e));
        Assert.Equal(("AuthFailed", 288L, false), (a.Kind, a.Count, a.Resolved));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal(288, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='SyncAlert' AND UserId='rte'"));   // told once, not 288 times
    }

    [Fact]
    public async Task Several_links_failing_in_one_cycle_count_once_per_cycle()
    {
        var e = Sn(x => x.AddSnLink("l2", "CHG0030002")); e.Http.Always(401);
        for (var i = 0; i < 5; i++) { await e.Poller.RunOnceAsync(); e.Time.Advance(Interval); }
        Assert.Equal(5, Assert.Single(Alerts(e)).Count);
        Assert.Equal(5, e.Http.Count);                                    // and the cycle stopped after the first link each time
    }

    // ---- auto-resolve --------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task One_clean_cycle_resolves_the_alert_and_keeps_its_history_and_a_new_failure_opens_a_new_row()
    {
        var e = Sn();
        e.Http.Always(401);
        await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync(); e.Time.Advance(Interval);
        var first = Assert.Single(Alerts(e));
        Assert.Equal(2, first.Count);

        e.SnAnswers();                                                    // the credential is fixed
        await e.Poller.RunOnceAsync();
        var resolved = Assert.Single(Alerts(e));
        Assert.Equal((first.Id, 2L, true, "2026-10-30T02:40:00Z"), (resolved.Id, resolved.Count, resolved.Resolved, resolved.ResolvedAt));   // history kept: same row, count intact
        Assert.Equal("InSync", e.Link("l1", "SyncState"));
        Assert.Equal(0, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND Action='AutoResolved'"));
        Assert.True(e.Realtime.Alerts.Count(id => id == first.Id) >= 3);   // raised, repeated and resolved are each pushed

        e.Time.Advance(Interval); e.Http.Always(401);
        await e.Poller.RunOnceAsync();
        var all = Alerts(e);
        Assert.Equal(2, all.Count);                                       // the resolved row plus a fresh open one
        Assert.True(all[0].Resolved); Assert.False(all[1].Resolved); Assert.Equal(1, all[1].Count);
    }

    [Fact]
    public async Task A_link_level_alert_resolves_when_that_link_reads_cleanly()
    {
        var e = Sn(); e.Http.Always(200, SnEmpty);
        await e.Poller.RunOnceAsync();
        Assert.False(Assert.Single(Alerts(e, "NotFound")).Resolved);

        e.Time.Advance(Interval); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        Assert.True(Assert.Single(Alerts(e, "NotFound")).Resolved);
        Assert.Equal("InSync", e.Link("l1", "SyncState"));
    }

    [Fact]
    public async Task A_failing_cycle_does_not_resolve_other_alerts_and_a_backoff_cycle_is_not_clean()
    {
        var e = Sn(); e.Http.Always(401);
        await e.Poller.RunOnceAsync();
        e.Time.Advance(Interval); e.Http.Always(503);                    // a different failure: the 401 alert stays open until a clean cycle
        await e.Poller.RunOnceAsync();
        Assert.Equal(2, Alerts(e).Count(a => !a.Resolved));
    }

    [Fact]
    public async Task An_unexpected_engine_failure_raises_SyncEngine_Stalled_and_resolves_on_the_next_good_cycle()
    {
        var e = Sn();
        var bad = new SyncPollerService(e.Db, e.Time, new ThrowingFactory(), e.Alerts, e.Sink, new SyncCycleWriter(e.Db, e.Time), e.Options, e.Config, NullLogger<SyncPollerService>.Instance);
        var r = await bad.RunOnceAsync();
        Assert.Equal("Engine", r.Single(x => x.Source == "ServiceNow").ErrorKind);
        var a = Assert.Single(Alerts(e, "Stalled"));
        Assert.Equal("SyncEngine", a.Source);
        Assert.Contains("failed unexpectedly", a.Message);

        e.SnAnswers();
        e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync();
        Assert.True(Assert.Single(Alerts(e, "Stalled")).Resolved);
    }

    private sealed class ThrowingFactory : IConnectorFactory
    {
        public Task<IConnector> CreateAsync(string source, string baseUrl, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    // ---- 5.3.7 mismatch: warnings only --------------------------------------------------------------------------------------------------------
    private Env Running(string state)
    {
        var e = NewEnv(fx);
        e.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t2','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        e.AddSnLink("m1", "CHG0030001", "t2");
        e.SnAnswers(state);
        return e;
    }

    [Fact]
    public async Task Train_executing_with_the_change_not_in_implement_is_a_mismatch_warning_and_the_cycle_stays_clean()
    {
        var e = Running("-2");                                            // Scheduled
        var r = await e.Poller.RunOnceAsync();

        Assert.Equal(ConnectorCycleResult.Clean, r.Single(x => x.Source == "ServiceNow").Outcome);   // a mismatch is a finding, not a failure
        Assert.Equal("Mismatch", e.Link("m1", "SyncState"));
        Assert.Equal("Implement", e.Link("m1", "ExpectedStatus"));
        Assert.Equal("Scheduled", e.Link("m1", "LastSyncedStatus"));
        var a = Assert.Single(Alerts(e, "Mismatch"));
        Assert.Equal(("t2", "CHG0030001 is Scheduled; the train is Executing, so the change should be in Implement"), (a.Train, a.Message));
        Assert.Equal(0, e.Count("SELECT ConsecutiveFailures FROM ConnectorState"));
        Assert.Equal("Executing", (string)e.Query("SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t2'")[0][0]!);   // nothing about the train changed

        e.Time.Advance(Interval); e.SnAnswers("-1");                      // the change moves to Implement
        await e.Poller.RunOnceAsync();
        Assert.Equal("InSync", e.Link("m1", "SyncState"));
        Assert.True(Assert.Single(Alerts(e, "Mismatch")).Resolved);
    }

    [Fact]
    public async Task Change_window_and_completion_rules_are_evaluated_from_the_train_and_its_window()
    {
        var e = Running("-1");
        e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w2','t2','2026-10-30T03:00:00Z','2026-10-30T07:00:00Z')");   // the change plans 02:00-06:00
        await e.Poller.RunOnceAsync();
        var a = Assert.Single(Alerts(e, "Mismatch"));
        Assert.Contains("plans 2026-10-30T02:00Z to 2026-10-30T06:00Z", a.Message);

        // a Complete train whose change is still in Review 25 h later
        e.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CloseCode,ActualEndAt,CreatedAt,UpdatedAt) VALUES('t3','Done','2026-10-29','Low','Complete','Successful','2026-10-29T01:00:00Z','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        e.AddSnLink("m3", "CHG0030003", "t3");
        e.SnAnswers("0");                                                 // Review
        e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync();
        Assert.Contains(Alerts(e, "Mismatch"), x => x.Train == "t3" && x.Message.Contains("more than 24 h after the train completed"));
        Assert.Equal("Closed", e.Link("m3", "ExpectedStatus"));
    }

    [Fact]
    public async Task Jira_fix_version_dates_and_release_state_follow_the_same_rules()
    {
        var e = NewEnv(fx, serviceNow: false, jira: true);
        e.AddJiraLink("j1", "PAY/4.5.0");                                 // product p1 of t1 (target 2026-10-30)
        e.AddJiraLink("j2", "PAY-12", "Train", "t1");
        e.Http.Respond = (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath.Contains("/version") ? Json(200, JiraVersions(false, "2026-11-06")) : Json(200, JiraIssue));
        await e.Poller.RunOnceAsync();

        Assert.Equal("Mismatch", e.Link("j1", "SyncState"));
        Assert.Equal("Unreleased", e.Link("j1", "LastSyncedStatus"));
        Assert.Equal("InSync", e.Link("j2", "SyncState"));
        Assert.Equal("In Progress", e.Link("j2", "LastSyncedStatus"));
        var a = Assert.Single(Alerts(e, "Mismatch"));
        Assert.Equal("Jira", a.Source);
        Assert.Contains("releases on 2026-11-06; the train's target is 2026-10-30", a.Message);
    }

    [Fact]
    public async Task Stored_text_is_keys_states_and_dates_only()
    {
        var e = Running("-2");
        await e.Poller.RunOnceAsync();
        // the recorded body carried a short_description with a card number; it must not be anywhere in the database
        var dump = string.Join("\n", new[] { "ExternalLinks", "SyncAlerts", "ConnectorState", "AuditEvents", "Notifications" }
            .SelectMany(t => e.Query($"SELECT * FROM {t}").Select(r => string.Join("|", r))));
        Assert.DoesNotContain("4111111111111111", dump);
        Assert.DoesNotContain("Card PAN", dump);
        Assert.DoesNotContain(Secret, dump);
    }

    // ---- cadence ------------------------------------------------------------------------------------------------------------------------------
    private static async Task Until(Func<bool> cond, string what)
    {
        var t = Environment.TickCount64;
        while (!cond()) { if (Environment.TickCount64 - t > 10_000) Assert.Fail("Timed out waiting for " + what); await Task.Delay(10); }
    }

    [Fact]
    public async Task The_timer_polls_every_five_minutes_and_every_minute_while_a_deployment_window_is_open()
    {
        var e = Sn(cfg: new() { ["Sync:StartDelaySeconds"] = "0" }); e.SnAnswers();
        // The loop registers its next wait on the fake clock only after a cycle and a database read; give it a moment before each advance.
        async Task Advance(TimeSpan by) { await Task.Delay(600); e.Time.Advance(by); }
        await e.Poller.StartAsync(default);
        try
        {
            await Until(() => e.Http.Count == 1, "the first cycle");
            await Advance(TimeSpan.FromMinutes(1)); await Task.Delay(600);
            Assert.Equal(1, e.Http.Count);                                // no window: one minute later nothing has happened
            await Advance(TimeSpan.FromMinutes(4));
            await Until(() => e.Http.Count == 2, "the cycle after five minutes");

            e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w','t1','2026-10-30T02:00:00Z','2026-10-30T09:00:00Z')");   // window open now
            await Advance(TimeSpan.FromMinutes(5));                       // the slow wait in progress ends; the loop then sees the open window
            await Until(() => e.Http.Count == 3, "the cycle that notices the window");
            await Advance(TimeSpan.FromMinutes(1));
            await Until(() => e.Http.Count == 4, "a one-minute cycle");
            await Advance(TimeSpan.FromMinutes(1));
            await Until(() => e.Http.Count == 5, "another one-minute cycle");
        }
        finally { await e.Poller.StopAsync(default); }
    }

    [Fact]
    public async Task With_Sync_Enabled_false_the_poller_does_nothing()
    {
        var e = Sn(cfg: new() { ["Sync:StartDelaySeconds"] = "0", ["Sync:Enabled"] = "false" });
        var poller = new SyncPollerService(e.Db, e.Time, new ConnectorFactory(new FakeHttpFactory(e.Http), e.Creds, e.Options, e.Time, new TestEnv(), e.Config), e.Alerts, e.Sink,
            new SyncCycleWriter(e.Db, e.Time), e.Options, e.Config, NullLogger<SyncPollerService>.Instance);
        e.SnAnswers();
        await poller.StartAsync(default);
        await Task.Delay(200);
        await poller.StopAsync(default);
        Assert.Equal(0, e.Http.Count);
    }
}
