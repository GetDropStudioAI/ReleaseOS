using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-37: every reminder and escalation of PROJECT_SCOPE 5.5 fires once, at the right time, on a fake clock, and never again.</summary>
public sealed class NotificationSchedulerTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Cfg(Dictionary<string, string?> v) : IConfiguration
    {
        public string? this[string key] { get => v.GetValueOrDefault(key); set => v[key] = value; }
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new NotSupportedException();
    }

    private sealed class RecordingSink : IAlertSink
    {
        public List<(string Source, string Kind, string Key, string Message)> Raised { get; } = [];
        public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default) { Raised.Add((sourceSystem, kind, key, message)); return Task.CompletedTask; }
    }

    private sealed class ThrowingNotifier : INotifier
    {
        public Task<string> NotifyAsync(NotificationRequest request, CancellationToken ct = default) => throw new InvalidOperationException("inbox is down");
    }

    private sealed class RecordingWebhooks : ITeamWebhookSender
    {
        public List<WebhookNotice> Sent { get; } = [];
        public Task<bool> SendAsync(WebhookNotice notice, CancellationToken ct = default) { Sent.Add(notice); return Task.FromResult(true); }
    }

    private sealed class Env
    {
        public required string Path { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required IDbContextFactory<ReleaseDbContext> Db { get; init; }
        public RecordingSink Sink { get; } = new();
        public RecordingWebhooks Hooks { get; } = new();

        public NotificationScheduler Scheduler(INotifier? notifier = null, string zone = "UTC")
        {
            var real = new Notifier(Db, Time);
            return new NotificationScheduler(Db, Time, notifier ?? real, new SyncAlertWriter(Db, Time, NullLogger<SyncAlertWriter>.Instance, real), Sink,
                new Cfg(new() { ["Display:TimeZone"] = zone }), NullLogger<NotificationScheduler>.Instance, Hooks);
        }

        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(Path); TriggerSuiteFixture.Run(c, sql, p); }

        public List<object?[]> Query(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(Path);
            using var cmd = c.CreateCommand();
            TriggerSuiteFixture.Bind(cmd, sql, p);
            using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
            return rows;
        }

        public long Count(string sql, params object?[] p) => Convert.ToInt64(Query(sql, p)[0][0]);
        public void At(string utc) => Time.SetUtcNow(DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal));

        /// <summary>Who got a notice of this kind/level for this entity, sorted.</summary>
        public string[] Who(string kind, string entityId, int level) =>
            [.. Query("SELECT UserId FROM Notifications WHERE Kind=? AND EntityId=? AND EscalationLevel=? ORDER BY UserId", kind, entityId, level).Select(r => (string)r[0]!)];
    }

    /// <summary>Template seed: users rte/rm/gov1/gov2/dev; t1 Planning with g1 (Pending, due Fri 2026-10-23, owner rte). The template's g2 is removed.</summary>
    private Env NewEnv(string now = "2026-10-20T14:00:00Z", bool withGate = true)
    {
        var path = fx.FreshPath();
        var e = new Env { Path = path, Time = new FakeTimeProvider(), Db = new Factory(path) };
        e.At(now);
        e.Sql("DELETE FROM StageGates WHERE Id='g2'");   // g2 (InProgress in the template) only takes part when a test re-creates it
        if (!withGate) e.Sql("DELETE FROM StageGates WHERE Id='g1'");   // step and condition scenarios run late enough that g1 would be overdue and add noise
        return e;
    }

    // ---- gate entered ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Gate_in_progress_notifies_its_owner_once_however_often_the_scan_runs()
    {
        var e = NewEnv();
        e.Sql("UPDATE StageGates SET Status='InProgress',LastChangedAt='2026-10-20T14:00:00Z' WHERE Id='g1'");
        var s = e.Scheduler();

        Assert.Equal(1, await s.ScanAsync());
        Assert.Equal(["rte"], e.Who("GateEntered", "g1", 0));
        Assert.Equal(0, await s.ScanAsync());
        e.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await e.Scheduler().ScanAsync());   // a fresh scheduler (a restart) remembers too: the Notifications row is the record
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='GateEntered'"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='Created'"));   // the notifier audits each row
    }

    [Fact]
    public async Task A_pending_gate_is_not_announced_and_an_archived_trains_overdue_gate_never_escalates()
    {
        var e = NewEnv();
        Assert.Equal(0, await e.Scheduler().ScanAsync());               // g1 is Pending and not yet due
        e.At("2026-11-30T00:00:00Z");                                   // long past its due date...
        e.Sql("UPDATE ReleaseTrains SET ArchivedAt='2026-11-01T00:00:00Z' WHERE Id='t1'");   // ...but the train is archived
        Assert.Equal(0, await e.Scheduler().ScanAsync());
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM Notifications"));
    }

    [Fact]
    public async Task A_team_owned_gate_notifies_every_member_and_the_team_webhook_goes_out_once()
    {
        var e = NewEnv();
        e.Sql(@"INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','platform','Platform');
                INSERT INTO TeamMembers(TeamId,UserId) VALUES('tm1','dev'),('tm1','gov2');
                INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerTeamId,Status) VALUES('g3','t1','Perf test','Standard',3,3,'2026-10-27','Executing','tm1','InProgress')");
        var s = e.Scheduler();
        await s.ScanAsync();
        await s.ScanAsync();
        Assert.Equal(["dev", "gov2"], e.Who("GateEntered", "g3", 0));
        var hook = Assert.Single(e.Hooks.Sent, h => h.Kind == "GateEntered");
        Assert.Equal("tm1", hook.TeamId);
        Assert.Equal("t1", hook.TrainId);
    }

    // ---- 50% reminder -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reminder_fires_at_half_the_time_from_entry_to_due_and_only_once()
    {
        var e = NewEnv("2026-10-20T00:00:00Z");
        // Entered Tue 20 Oct 00:00Z; due at the end of Wed 28 Oct (= 29 Oct 00:00Z): 9 days, half way is 24 Oct 12:00Z.
        e.Sql("INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status) VALUES('g2','t1','Compliance Sign-off','Compliance',2,2,'2026-10-28','Executing','gov1','InProgress')");
        e.Sql("INSERT INTO GateTransitions(StageGateId,FromStatus,ToStatus,OccurredAt) VALUES('g2','Pending','InProgress','2026-10-20T00:00:00Z')");
        var s = e.Scheduler();
        await s.ScanAsync();                                     // GateEntered only
        Assert.Empty(e.Who("GateReminder", "g2", 0));

        e.At("2026-10-24T11:59:59Z");
        await s.ScanAsync();
        Assert.Empty(e.Who("GateReminder", "g2", 0));

        e.At("2026-10-24T12:00:00Z");
        await s.ScanAsync();
        Assert.Equal(["gov1"], e.Who("GateReminder", "g2", 0));

        e.At("2026-10-25T09:00:00Z");
        Assert.Equal(0, await s.ScanAsync());
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='GateReminder'"));
    }

    // ---- overdue: level 1 to the Release Manager, level 2 to the RTE one business day later ---------------------------------------------
    [Fact]
    public async Task Overdue_escalates_to_the_release_manager_at_once_and_to_the_RTE_after_one_business_day()
    {
        var e = NewEnv();
        var s = e.Scheduler();
        e.At("2026-10-23T23:59:59Z");                                   // g1 is due Friday 23 Oct: still on time
        await s.ScanAsync();
        Assert.Empty(e.Who("GateOverdue", "g1", 1));

        e.At("2026-10-24T00:00:00Z");                                   // Saturday: DueOn has passed
        await s.ScanAsync();
        Assert.Equal(["rm"], e.Who("GateOverdue", "g1", 1));
        Assert.Empty(e.Who("GateOverdue", "g1", 2));

        e.At("2026-10-26T12:00:00Z");                                   // Monday is the business day that has to pass
        await s.ScanAsync();
        Assert.Empty(e.Who("GateOverdue", "g1", 2));
        e.At("2026-10-26T23:59:59Z");
        await s.ScanAsync();
        Assert.Empty(e.Who("GateOverdue", "g1", 2));

        e.At("2026-10-27T00:00:00Z");
        await s.ScanAsync();
        Assert.Equal(["rte"], e.Who("GateOverdue", "g1", 2));

        e.At("2026-11-05T00:00:00Z");
        Assert.Equal(0, await s.ScanAsync());                           // and never again
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='GateOverdue' AND EntityId='g1' AND EscalationLevel=1"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='GateOverdue' AND EntityId='g1' AND EscalationLevel=2"));
    }

    [Fact]
    public async Task A_holiday_does_not_count_as_the_business_day_for_level_2()
    {
        var e = NewEnv();
        e.Sql("INSERT INTO Holidays(Day,Name) VALUES('2026-10-26','Observed holiday')");   // the Monday after the due Friday
        var s = e.Scheduler();

        e.At("2026-10-27T00:00:00Z");                                   // would be level 2 without the holiday
        await s.ScanAsync();
        Assert.Equal(["rm"], e.Who("GateOverdue", "g1", 1));
        Assert.Empty(e.Who("GateOverdue", "g1", 2));

        e.At("2026-10-27T23:59:59Z");
        await s.ScanAsync();
        Assert.Empty(e.Who("GateOverdue", "g1", 2));
        e.At("2026-10-28T00:00:00Z");                                   // Tuesday is the business day; level 2 when it is over
        await s.ScanAsync();
        Assert.Equal(["rte"], e.Who("GateOverdue", "g1", 2));
    }

    [Fact]
    public async Task Due_means_the_end_of_DueOn_in_the_display_zone()
    {
        var e = NewEnv();
        var s = e.Scheduler(zone: "America/Chicago");                    // CDT (UTC-5) until 1 Nov 2026
        e.At("2026-10-24T04:59:59Z");                                   // still Friday 23:59:59 in Chicago
        await s.ScanAsync();
        Assert.Empty(e.Who("GateOverdue", "g1", 1));
        e.At("2026-10-24T05:00:00Z");
        await s.ScanAsync();
        Assert.Equal(["rm"], e.Who("GateOverdue", "g1", 1));
    }

    // ---- steps ------------------------------------------------------------------------------------------------------------------------
    private static void SeedLiveRun(Env e)
    {
        e.Sql(@"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t2','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
                  ('x1','t2','R-001','Deploy','Deploy API','dev','2026-10-30T06:00:00Z',30),
                  ('x2','t2','R-002','Rollback','Restore snapshot','dev','2026-10-30T05:00:00Z',30);
                INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('run1','t2','Live','2026-10-30T05:00:00Z','rte');
                INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e1','run1','x1'),('e2','run1','x2')");
    }

    [Fact]
    public async Task A_live_step_that_has_not_started_warns_owner_and_RTE_at_5_minutes_and_the_release_manager_at_30()
    {
        var e = NewEnv("2026-10-30T05:00:00Z", withGate: false);
        SeedLiveRun(e);
        var s = e.Scheduler();

        e.At("2026-10-30T06:04:59Z");
        await s.ScanAsync();
        Assert.Empty(e.Who("StepLate", "e1", 1));

        e.At("2026-10-30T06:05:00Z");
        await s.ScanAsync();
        Assert.Equal(["dev", "rte"], e.Who("StepLate", "e1", 1));
        Assert.Empty(e.Who("StepLate", "e1", 2));

        e.At("2026-10-30T06:29:59Z");
        Assert.Equal(0, await s.ScanAsync());
        e.At("2026-10-30T06:30:00Z");
        await s.ScanAsync();
        Assert.Equal(["rm"], e.Who("StepLate", "e1", 2));

        e.At("2026-10-30T09:00:00Z");
        Assert.Equal(0, await s.ScanAsync());
        Assert.Equal(3, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='StepLate'"));
        Assert.Empty(e.Who("StepLate", "e2", 1));                       // the Rollback section only runs when needed: never "late"
    }

    [Fact]
    public async Task A_scan_that_first_sees_a_step_very_late_sends_both_levels()
    {
        var e = NewEnv("2026-10-30T06:40:00Z", withGate: false);
        SeedLiveRun(e);
        await e.Scheduler().ScanAsync();
        Assert.Equal(["dev", "rte"], e.Who("StepLate", "e1", 1));
        Assert.Equal(["rm"], e.Who("StepLate", "e1", 2));
    }

    [Fact]
    public async Task Started_steps_ended_runs_and_rehearsals_are_never_chased()
    {
        var e = NewEnv("2026-10-30T09:00:00Z", withGate: false);
        SeedLiveRun(e);
        e.Sql("UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T06:01:00Z' WHERE Id='e1'");
        Assert.Equal(0, await e.Scheduler().ScanAsync());

        e.Sql("UPDATE StepExecutions SET Status='Scheduled',ActualStartAt=NULL WHERE Id='e1'");
        e.Sql("UPDATE RunbookRuns SET Mode='Rehearsal' WHERE Id='run1'");   // rehearsals never page anyone
        Assert.Equal(0, await e.Scheduler().ScanAsync());
        e.Sql("UPDATE RunbookRuns SET Mode='Live',EndedAt='2026-10-30T08:00:00Z',Outcome='Aborted' WHERE Id='run1'");
        Assert.Equal(0, await e.Scheduler().ScanAsync());
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM Notifications"));
    }

    // ---- Go/No-Go conditions (D29) --------------------------------------------------------------------------------------------------
    private const string Decision = "INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES(?,?,'GoWithConditions','rm',?,'{}')";
    private const string Condition = "INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES(?,?,?,?,?)";

    [Fact]
    public async Task A_condition_warns_its_owner_an_hour_before_expiry_and_owner_and_release_manager_when_it_expires()
    {
        var e = NewEnv("2026-10-25T00:00:00Z", withGate: false);
        e.Sql(Decision, "d1", "t1", "2026-10-24T00:00:00Z");
        e.Sql(Condition, "c1", "d1", "Load test signed off", "dev", "2026-10-25T12:00:00Z");
        var s = e.Scheduler();

        e.At("2026-10-25T10:59:59Z");
        await s.ScanAsync();
        Assert.Empty(e.Who("ConditionExpiring", "c1", 0));

        e.At("2026-10-25T11:00:00Z");
        await s.ScanAsync();
        Assert.Equal(["dev"], e.Who("ConditionExpiring", "c1", 0));
        e.At("2026-10-25T11:59:59Z");
        Assert.Equal(0, await s.ScanAsync());

        e.At("2026-10-25T12:00:00Z");
        await s.ScanAsync();
        Assert.Equal(["dev", "rm"], e.Who("ConditionExpired", "c1", 1));
        e.At("2026-10-26T12:00:00Z");
        Assert.Equal(0, await s.ScanAsync());
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='ConditionExpiring'"));
        Assert.Equal(2, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='ConditionExpired'"));
    }

    [Fact]
    public async Task Closed_conditions_and_conditions_of_a_superseded_decision_are_left_alone()
    {
        var e = NewEnv("2026-10-25T12:00:00Z", withGate: false);
        e.Sql(Decision, "d1", "t1", "2026-10-24T00:00:00Z");
        e.Sql(Condition, "old", "d1", "Old list", "dev", "2026-10-25T11:00:00Z");
        e.Sql(Decision, "d2", "t1", "2026-10-25T00:00:00Z");
        e.Sql(Condition, "closed", "d2", "Done already", "dev", "2026-10-25T11:00:00Z");
        e.Sql("UPDATE GoNoGoConditions SET ClosedAt='2026-10-25T09:00:00Z',ClosedByUserId='dev' WHERE Id='closed'");
        Assert.Equal(0, await e.Scheduler().ScanAsync());
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind LIKE 'Condition%'"));
    }

    // ---- failures are never swallowed -------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_failing_rule_raises_an_alert_and_a_SyncAlerts_row_and_is_retried_by_the_next_scan()
    {
        var e = NewEnv();
        var s = e.Scheduler(new ThrowingNotifier());
        Assert.Equal(0, await s.ScanAsync());                            // g1 is Pending and nothing else exists: nothing fails yet
        Assert.Empty(e.Sink.Raised);

        e.Sql("UPDATE StageGates SET Status='InProgress',LastChangedAt='2026-10-20T14:00:00Z' WHERE Id='g1'");
        await s.ScanAsync();                                             // does not throw...
        var alert = Assert.Single(e.Sink.Raised);                        // ...it raises
        Assert.Equal(("Notifications", "DeliveryFailed", "scheduler:gates"), (alert.Source, alert.Kind, alert.Key));
        Assert.Contains("inbox is down", alert.Message);

        var row = Assert.Single(e.Query("SELECT SourceSystem,Kind,OccurrenceCount,IsResolved FROM SyncAlerts"));
        Assert.Equal(["Notifications", "DeliveryFailed", 1L, 0L], row);
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND Action='Raised'"));
        Assert.Equal(["rm", "rte"], e.Query("SELECT UserId FROM Notifications WHERE Kind='SyncAlert' ORDER BY UserId").Select(r => (string)r[0]!).ToArray());   // RTEs and Release Managers are told in-app

        await s.ScanAsync();                                             // retried, still failing: one open row whose count grows, no second in-app copy
        Assert.Equal(2, e.Sink.Raised.Count);
        Assert.Equal(2L, e.Count("SELECT OccurrenceCount FROM SyncAlerts"));
        Assert.Equal(2, e.Count("SELECT COUNT(*) FROM Notifications WHERE Kind='SyncAlert'"));

        Assert.Equal(1, await e.Scheduler().ScanAsync());                // the inbox is back: the notice that failed is delivered now
        Assert.Equal(["rte"], e.Who("GateEntered", "g1", 0));
    }

    [Fact]
    public async Task One_failing_rule_does_not_stop_the_others()
    {
        var e = NewEnv("2026-10-30T06:10:00Z");
        SeedLiveRun(e);
        e.Sql("DROP TABLE Holidays");                                    // the gate rule needs it (only when a gate is open: g1 is)
        var s = e.Scheduler();
        Assert.Equal(2, await s.ScanAsync());                            // steps still fired: dev + rte
        Assert.Equal(["dev", "rte"], e.Who("StepLate", "e1", 1));
        Assert.Contains(e.Sink.Raised, a => a.Key == "scheduler:gates");
    }
}
