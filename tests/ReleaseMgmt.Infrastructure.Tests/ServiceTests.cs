using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>Domain services against the migrated schema, seeded like the trigger suite (users rte/rm/gov1/gov2/dev, train t1, gates g1 Code Freeze + g2 Compliance).</summary>
public sealed class ServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Env
    {
        public required string Path { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required IDbContextFactory<ReleaseDbContext> Db { get; init; }
        public TrainLifecycleService Trains => new(Db, Time);
        public GateService Gates => new(Db, Time);
        public WaiverService Waivers => new(Db, Time);
        public TaskService Tasks => new(Db, Time);
        public BaselineService Baselines => new(Db, Time);
        public ScheduleService Schedule => new(Db, Time);

        public void Sql(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(Path);
            TriggerSuiteFixture.Run(c, sql, p);
        }

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
    }

    private Env NewEnv()
    {
        var path = fx.FreshPath();
        return new Env { Path = path, Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero)), Db = new Factory(path) };
    }

    private static readonly Actor Rte = new("rte"), Rm = new("rm"), Gov1 = new("gov1"), Gov2 = new("gov2"), Dev = new("dev");

    private static string[] Guards(ServiceResult<ReleaseTrains> r) => [.. r.Failures.Select(f => f.Guard)];

    // ---- guards name themselves (M1: illegal transitions return 422 naming the guard) --------------------------------
    [Fact]
    public async Task Illegal_train_transition_names_its_guard()
    {
        var e = NewEnv();
        var r = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal([Domain.Services.Guards.IllegalTransition], Guards(r));
    }

    [Fact]
    public async Task Uncertified_gate_blocks_Gated_and_names_the_gate()
    {
        var e = NewEnv();
        var r = await e.Trains.AdvanceAsync("t1", "Gated", Rte);
        var f = Assert.Single(r.Failures);
        Assert.Equal(Domain.Services.Guards.GateLockout, f.Guard);
        Assert.Equal(["Code Freeze"], f.Items);
        Assert.Equal(0, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Advance'")); // a refused write leaves no audit row
    }

    [Fact]
    public async Task Illegal_gate_transitions_name_their_guard()
    {
        var e = NewEnv();
        var skip = await e.Gates.CertifyAsync("g1", Rte); // Pending -> Certified
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], skip.Failures.Select(f => f.Guard));
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.FailAsync("g1", Rte)).Failures.Select(f => f.Guard)); // Pending -> Failed
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.ReopenAsync("g1", Rte)).Failures.Select(f => f.Guard)); // Pending -> InProgress via reopen? no: Pending has no reopen
    }

    [Fact]
    public async Task Certify_reports_every_reason_at_once()
    {
        var e = NewEnv();
        // g2 is Compliance, In progress, task open, earlier gate uncertified, and rm is not a Governance Officer
        var r = await e.Gates.CertifyAsync("g2", Rm);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        var g = r.Failures.Select(f => f.Guard).ToHashSet();
        Assert.Contains(Domain.Services.Guards.GateOpenTasks, g);
        Assert.Contains(Domain.Services.Guards.EarlierGateOpen, g);
        Assert.Contains(Domain.Services.Guards.ComplianceCertifierRole, g);
    }

    [Fact]
    public async Task Segregation_of_duties_blocks_a_certifier_who_completed_a_task()
    {
        var e = NewEnv();
        await Certify(e, "g1");
        Assert.True((await e.Tasks.CompleteAsync("k2", Gov1)).IsOk);
        var r = await e.Gates.CertifyAsync("g2", Gov1);
        Assert.Equal([Domain.Services.Guards.SegregationOfDuties], r.Failures.Select(f => f.Guard));
        Assert.True((await e.Gates.CertifyAsync("g2", Gov2)).IsOk);
    }

    private async Task Certify(Env e, string gate, string task = "k1")
    {
        Assert.True((await e.Tasks.CompleteAsync(task, Rte)).IsOk);
        Assert.True((await e.Gates.StartAsync(gate, Rte)).IsOk);
        Assert.True((await e.Gates.CertifyAsync(gate, Rte)).IsOk);
    }

    // ---- audit: one service row per write, cascades write their own with the right actor and time ------------------------
    [Fact]
    public async Task Each_write_makes_one_service_audit_row_and_each_cascade_one_trigger_row_with_the_right_actor()
    {
        var e = NewEnv();
        Assert.True((await e.Tasks.CompleteAsync("k1", Rte)).IsOk);
        Assert.True((await e.Gates.StartAsync("g1", Rte)).IsOk);
        e.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await e.Gates.CertifyAsync("g1", Rte)).IsOk);

        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Certify' AND ActorUserId='rte'"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Start' AND EntityId='g1'"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Complete' AND EntityId='k1'"));

        // cascades: baseline auto-capture (Code Freeze) and evidence lock, attributed to the certifier at the service clock
        foreach (var cascade in new[] { "AutoCaptured", "EvidenceLocked" })
        {
            var row = Assert.Single(e.Query("SELECT ActorUserId, OccurredAt FROM AuditEvents WHERE Action=?", cascade));
            Assert.Equal("rte", row[0]);
            Assert.Equal("2026-10-20T14:05:00Z", row[1]);
        }
        var log = e.Query("SELECT ActorUserId, OccurredAt FROM GateTransitions WHERE ToStatus='Certified'").Single();
        Assert.Equal(["rte", "2026-10-20T14:05:00Z"], [(string)log[0]!, (string)log[1]!]);
    }

    [Fact]
    public async Task Reopening_a_task_decertifies_its_gate_with_the_reopener_as_actor()
    {
        var e = NewEnv();
        await Certify(e, "g1");
        e.Time.Advance(TimeSpan.FromHours(2));
        var r = await e.Tasks.ReopenAsync("k1", Dev);
        Assert.True(r.IsOk);
        var gate = e.Query("SELECT Status, LastChangedByUserId, LastChangedAt FROM StageGates WHERE Id='g1'").Single();
        Assert.Equal(["InProgress", "dev", "2026-10-20T16:00:00Z"], [(string)gate[0]!, (string)gate[1]!, (string)gate[2]!]);
        var dec = Assert.Single(e.Query("SELECT ActorUserId, OccurredAt FROM AuditEvents WHERE Action='Decertified'"));
        Assert.Equal(["dev", "2026-10-20T16:00:00Z"], [(string)dec[0]!, (string)dec[1]!]);
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Reopen' AND EntityId='k1' AND ActorUserId='dev'"));
    }

    [Fact]
    public async Task Adding_a_task_to_a_certified_gate_decertifies_it()
    {
        var e = NewEnv();
        await Certify(e, "g1");
        var r = await e.Tasks.AddAsync("g1", "Late add", null, null, Rm);
        Assert.True(r.IsOk);
        Assert.Equal("rte", r.Value!.OwnerUserId); // defaults to the gate owner
        var gate = e.Query("SELECT Status, LastChangedByUserId FROM StageGates WHERE Id='g1'").Single();
        Assert.Equal(["InProgress", "rm"], [(string)gate[0]!, (string)gate[1]!]);
    }

    // ---- optimistic concurrency ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Stale_version_returns_conflict_with_the_current_row()
    {
        var e = NewEnv();
        Assert.True((await e.Gates.StartAsync("g1", Rte, expectedVersion: 1)).IsOk);   // version 1 -> 2
        var stale = await e.Gates.FailAsync("g1", Rte, expectedVersion: 1);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        var current = Assert.IsType<StageGates>(stale.Current);
        Assert.Equal(2, current.Version);
        Assert.Equal("InProgress", current.Status);
        Assert.Equal("InProgress", e.Query("SELECT Status FROM StageGates WHERE Id='g1'").Single()[0]);
    }

    [Fact]
    public async Task Every_write_bumps_Version_and_stamps_LastChanged()
    {
        var e = NewEnv();
        e.Time.Advance(TimeSpan.FromMinutes(1));
        var r = await e.Gates.StartAsync("g1", Dev);
        Assert.Equal(2, r.Value!.Version);
        Assert.Equal("dev", r.Value.LastChangedByUserId);
        Assert.Equal(new DateTime(2026, 10, 20, 14, 1, 0, DateTimeKind.Utc), r.Value.LastChangedAt);
    }

    // ---- waivers ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Waiver_needs_a_reason_a_different_person_and_a_Governance_Officer()
    {
        var e = NewEnv();
        await Certify(e, "g1");
        Assert.Equal([Domain.Services.Guards.WaiverReason], (await e.Waivers.RequestAsync("g2", "too short", Gov1)).Failures.Select(f => f.Guard));
        var w = (await e.Waivers.RequestAsync("g2", "Pen-test vendor slipped; CISO accepted risk", Gov1)).Value!;
        Assert.Equal([Domain.Services.Guards.WaiverSelfApproval], (await e.Waivers.ApproveAsync(w.Id, Gov1)).Failures.Select(f => f.Guard));
        Assert.Equal([Domain.Services.Guards.WaiverApproverRole], (await e.Waivers.ApproveAsync(w.Id, Rm)).Failures.Select(f => f.Guard));

        Assert.Equal([Domain.Services.Guards.WaiverRequired], (await e.Gates.WaiveAsync("g2", Gov1)).Failures.Select(f => f.Guard));
        Assert.True((await e.Waivers.ApproveAsync(w.Id, Gov2)).IsOk);
        var waived = await e.Gates.WaiveAsync("g2", Gov1);
        Assert.True(waived.IsOk);
        Assert.Equal("Waived", waived.Value!.Status);

        // Waived is terminal: no reopen (D26), and it is not Certified in reports
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.ReopenAsync("g2", Gov1)).Failures.Select(f => f.Guard));
    }

    // ---- lifecycle end to end ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Train_walks_Planning_to_Complete_only_when_every_guard_is_met()
    {
        var e = NewEnv();
        await Certify(e, "g1");
        Assert.True((await e.Trains.AdvanceAsync("t1", "Gated", Rte)).IsOk);

        // Gated -> Executing: gate g2 uncertified, no Go, High risk without rollback rehearsal
        var blocked = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        var g = Guards(blocked).ToHashSet();
        Assert.Contains(Domain.Services.Guards.GateLockout, g);
        Assert.Contains(Domain.Services.Guards.ExecutingRequiresGo, g);
        Assert.Contains(Domain.Services.Guards.RollbackNotRehearsed, g);
        Assert.DoesNotContain(Domain.Services.Guards.ExecutingRequiresBaseline, g); // Code Freeze certified -> baseline exists

        Assert.True((await e.Tasks.CompleteAsync("k2", Gov1)).IsOk);
        Assert.True((await e.Gates.CertifyAsync("g2", Gov2)).IsOk);
        e.Sql("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('d1','t1','Go','rm','2026-10-20T13:00:00Z','{}')");
        Assert.Equal([Domain.Services.Guards.RollbackNotRehearsed], Guards(await e.Trains.AdvanceAsync("t1", "Executing", Rte)));
        Assert.True((await e.Trains.RecordRollbackRehearsedAsync("t1", Rte)).IsOk);

        var exec = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        Assert.True(exec.IsOk);
        Assert.Equal("Executing", exec.Value!.CurrentStatus);
        Assert.NotNull(exec.Value.ActualStartAt);

        Assert.Equal([Domain.Services.Guards.CloseCodeRequired], Guards(await e.Trains.CompleteAsync("t1", "Bogus", null, Rte)));
        var done = await e.Trains.CompleteAsync("t1", "SuccessfulWithIssues", "rollback of one product", Rte);
        Assert.True(done.IsOk);
        // PIR is auto-required by trigger, with its own audit row (actor and service time)
        Assert.Equal(1, e.Count("SELECT count(*) FROM PostImplementationReviews WHERE ReleaseTrainId='t1' AND RequiredReason='CloseCode=SuccessfulWithIssues'"));
        Assert.Equal("rte", e.Query("SELECT ActorUserId FROM AuditEvents WHERE Action='AutoRequired'").Single()[0]);
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Complete' AND EntityType='ReleaseTrain'"));
    }

    [Fact]
    public async Task Expired_open_condition_blocks_Executing_using_the_service_clock()
    {
        var e = NewEnv();
        e.Sql("UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'");
        await Certify(e, "g1");
        await e.Trains.AdvanceAsync("t1", "Gated", Rte);
        Assert.True((await e.Tasks.CompleteAsync("k2", Gov1)).IsOk);
        Assert.True((await e.Gates.CertifyAsync("g2", Gov2)).IsOk);
        e.Sql("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('d9','t1','GoWithConditions','rm','2026-10-20T13:00:00Z','{}')");
        e.Sql("INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES('c9','d9','CISO sign-off','gov1','2026-10-20T15:00:00Z')");
        e.Time.Advance(TimeSpan.FromHours(2)); // 16:00 > 15:00
        var r = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        var f = Assert.Single(r.Failures);
        Assert.Equal(Domain.Services.Guards.ConditionExpired, f.Guard);
        Assert.Equal(["CISO sign-off"], f.Items);
    }

    // ---- baseline ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Manual_baseline_capture_is_once_only_and_immutable()
    {
        var e = NewEnv();
        var b = await e.Baselines.CaptureAsync("t1", Rte);
        Assert.True(b.IsOk);
        Assert.Equal(new DateOnly(2026, 10, 30), b.Value!.PlannedReleaseDate);
        using var doc = System.Text.Json.JsonDocument.Parse(b.Value.SnapshotJson);
        Assert.Equal(2, doc.RootElement.GetProperty("products").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("gates").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Equal([Domain.Services.Guards.BaselineExists], (await e.Baselines.CaptureAsync("t1", Rte)).Failures.Select(f => f.Guard));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Capture' AND EntityType='Baseline'"));
    }

    // ---- business-day DueOn -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Changing_the_target_date_moves_every_DueOn_across_weekends_and_a_holiday()
    {
        var e = NewEnv();
        e.Sql("INSERT INTO Holidays(Day,Name) VALUES('2026-11-02','Company holiday')");
        // Fri 2026-11-06: 5 business days back skipping Mon 11-02 -> Thu 10-29; 2 back -> Wed 11-04
        var r = await e.Schedule.ChangeTargetDateAsync("t1", new DateOnly(2026, 11, 6), Rm);
        Assert.True(r.IsOk);
        Assert.Equal("2026-10-29", e.Query("SELECT DueOn FROM StageGates WHERE Id='g1'").Single()[0]);
        Assert.Equal("2026-11-04", e.Query("SELECT DueOn FROM StageGates WHERE Id='g2'").Single()[0]);
        var audit = e.Query("SELECT ActorUserId, BeforeJson, AfterJson FROM AuditEvents WHERE Action='ChangeTargetDate'").Single();
        Assert.Equal("rm", audit[0]);
        Assert.Contains("2026-10-23", (string)audit[1]!);   // before
        Assert.Contains("2026-10-29", (string)audit[2]!);   // after
        Assert.Equal(2, r.Value!.Version);
    }

    [Theory]
    [InlineData("2026-10-30", 5, "2026-10-23")] // plain week
    [InlineData("2026-10-26", 1, "2026-10-23")] // Monday - 1 lands on Friday, not Sunday
    [InlineData("2026-10-30", 0, "2026-10-30")] // offset 0 is the target itself
    [InlineData("2026-11-03", 2, "2026-10-30")] // across a weekend
    public void Business_day_subtraction(string target, int days, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), BusinessDays.SubtractBusinessDays(DateOnly.Parse(target), days, new HashSet<DateOnly>()));

    [Fact]
    public void Business_day_addition_skips_weekends_and_holidays() =>
        Assert.Equal(new DateOnly(2026, 11, 3), BusinessDays.AddBusinessDays(new DateOnly(2026, 10, 30), 1, new HashSet<DateOnly> { new(2026, 11, 2) }));

    // ---- trigger failures map to DbRule ------------------------------------------------------------------------------------------------
    private sealed class Probe(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider t) : ServiceBase(dbf, t)
    {
        public Task<ServiceResult<int>> IllegalGateUpdate() => RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleAsync(x => x.Id == "g1");
            g.Status = "Failed"; g.LastChangedByUserId = "rte";   // Pending -> Failed: the service would have refused; the trigger is the backstop
            await db.SaveChangesAsync();
            return ServiceResult<int>.Ok(1);
        }, default);
    }

    [Fact]
    public async Task A_trigger_abort_becomes_a_DbRule_guard_failure_not_an_exception()
    {
        var e = NewEnv();
        var r = await new Probe(e.Db, e.Time).IllegalGateUpdate();
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        var f = Assert.Single(r.Failures);
        Assert.Equal(Domain.Services.Guards.DbRule, f.Guard);
        Assert.Equal("Illegal gate status transition", f.Message);
        Assert.Equal("Pending", e.Query("SELECT Status FROM StageGates WHERE Id='g1'").Single()[0]); // rolled back
    }
}
