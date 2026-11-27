using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>REOS-196 vertical slice: Trains, Gates and the Compliance separation-of-duties rules, run through the REAL shipped services (TrainLifecycleService, GateService,
/// WaiverService, TaskService) on Npgsql. The scenarios are the ones in Infrastructure.Tests/ServiceTests.cs, same seed, same assertions; only the raw-SQL helpers differ
/// (quoted identifiers, Npgsql parameters). The SQLite twins stay in ServiceTests.cs and keep running in CI, so "green on both" is two suites over one set of scenarios.</summary>
public sealed class SliceServiceTests(PgFixture fx) : IClassFixture<PgFixture>
{
    private static readonly Actor Rte = new("rte"), Rm = new("rm"), Gov1 = new("gov1"), Gov2 = new("gov2"), Dev = new("dev");
    private static string[] Guards(ServiceResult<ReleaseTrains> r) => [.. r.Failures.Select(f => f.Guard)];

    [PgFact]
    public async Task Illegal_train_transition_names_its_guard()
    {
        var e = await fx.NewEnvAsync();
        var r = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal([Domain.Services.Guards.IllegalTransition], Guards(r));
    }

    [PgFact]
    public async Task Uncertified_gate_blocks_Gated_and_names_the_gate()
    {
        var e = await fx.NewEnvAsync();
        var r = await e.Trains.AdvanceAsync("t1", "Gated", Rte);
        var f = Assert.Single(r.Failures);
        Assert.Equal(Domain.Services.Guards.GateLockout, f.Guard);
        Assert.Equal(["Code Freeze"], f.Items);
        Assert.Equal(0, e.Count("SELECT count(*) FROM \"AuditEvents\" WHERE \"Action\"='Advance'"));
    }

    [PgFact]
    public async Task Illegal_gate_transitions_name_their_guard()
    {
        var e = await fx.NewEnvAsync();
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.CertifyAsync("g1", Rte)).Failures.Select(f => f.Guard));
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.FailAsync("g1", Rte)).Failures.Select(f => f.Guard));
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.ReopenAsync("g1", Rte)).Failures.Select(f => f.Guard));
    }

    [PgFact]
    public async Task Certify_reports_every_reason_at_once()
    {
        var e = await fx.NewEnvAsync();
        var r = await e.Gates.CertifyAsync("g2", Rm);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        var g = r.Failures.Select(f => f.Guard).ToHashSet();
        Assert.Contains(Domain.Services.Guards.GateOpenTasks, g);
        Assert.Contains(Domain.Services.Guards.EarlierGateOpen, g);
        Assert.Contains(Domain.Services.Guards.ComplianceCertifierRole, g);
    }

    [PgFact]
    public async Task Segregation_of_duties_blocks_a_certifier_who_completed_a_task()
    {
        var e = await fx.NewEnvAsync();
        await Certify(e, "g1");
        Assert.True((await e.Tasks.CompleteAsync("k2", Gov1)).IsOk);
        var r = await e.Gates.CertifyAsync("g2", Gov1);
        Assert.Equal([Domain.Services.Guards.SegregationOfDuties], r.Failures.Select(f => f.Guard));
        Assert.True((await e.Gates.CertifyAsync("g2", Gov2)).IsOk);
    }

    private static async Task Certify(PgEnv e, string gate, string task = "k1")
    {
        Assert.True((await e.Tasks.CompleteAsync(task, Rte)).IsOk);
        Assert.True((await e.Gates.StartAsync(gate, Rte)).IsOk);
        Assert.True((await e.Gates.CertifyAsync(gate, Rte)).IsOk);
    }

    [PgFact]
    public async Task Each_write_makes_one_service_audit_row_and_each_cascade_one_trigger_row_with_the_right_actor()
    {
        var e = await fx.NewEnvAsync();
        Assert.True((await e.Tasks.CompleteAsync("k1", Rte)).IsOk);
        Assert.True((await e.Gates.StartAsync("g1", Rte)).IsOk);
        e.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await e.Gates.CertifyAsync("g1", Rte)).IsOk);

        Assert.Equal(1, e.Count("SELECT count(*) FROM \"AuditEvents\" WHERE \"Action\"='Certify' AND \"ActorUserId\"='rte'"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM \"AuditEvents\" WHERE \"Action\"='Start' AND \"EntityId\"='g1'"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM \"AuditEvents\" WHERE \"Action\"='Complete' AND \"EntityId\"='k1'"));
        foreach (var cascade in new[] { "AutoCaptured", "EvidenceLocked" })
        {
            var row = Assert.Single(e.Query("SELECT \"ActorUserId\", \"OccurredAt\" FROM \"AuditEvents\" WHERE \"Action\"=$1", cascade));
            Assert.Equal("rte", row[0]);
            Assert.Equal("2026-10-20T14:05:00Z", row[1]);
        }
        var log = e.Query("SELECT \"ActorUserId\", \"OccurredAt\" FROM \"GateTransitions\" WHERE \"ToStatus\"='Certified'").Single();
        Assert.Equal(["rte", "2026-10-20T14:05:00Z"], [(string)log[0]!, (string)log[1]!]);
    }

    [PgFact]
    public async Task Reopening_a_task_decertifies_its_gate_with_the_reopener_as_actor()
    {
        var e = await fx.NewEnvAsync();
        await Certify(e, "g1");
        e.Time.Advance(TimeSpan.FromHours(2));
        Assert.True((await e.Tasks.ReopenAsync("k1", Dev)).IsOk);
        var gate = e.Query("SELECT \"Status\", \"LastChangedByUserId\", \"LastChangedAt\" FROM \"StageGates\" WHERE \"Id\"='g1'").Single();
        Assert.Equal(["InProgress", "dev", "2026-10-20T16:00:00Z"], [(string)gate[0]!, (string)gate[1]!, (string)gate[2]!]);
        var dec = Assert.Single(e.Query("SELECT \"ActorUserId\", \"OccurredAt\" FROM \"AuditEvents\" WHERE \"Action\"='Decertified'"));
        Assert.Equal(["dev", "2026-10-20T16:00:00Z"], [(string)dec[0]!, (string)dec[1]!]);
    }

    [PgFact]
    public async Task Adding_a_task_to_a_certified_gate_decertifies_it()
    {
        var e = await fx.NewEnvAsync();
        await Certify(e, "g1");
        var r = await e.Tasks.AddAsync("g1", "Late add", null, null, Rm);
        Assert.True(r.IsOk);
        Assert.Equal("rte", r.Value!.OwnerUserId);
        var gate = e.Query("SELECT \"Status\", \"LastChangedByUserId\" FROM \"StageGates\" WHERE \"Id\"='g1'").Single();
        Assert.Equal(["InProgress", "rm"], [(string)gate[0]!, (string)gate[1]!]);
    }

    [PgFact]
    public async Task Stale_version_returns_conflict_with_the_current_row()
    {
        var e = await fx.NewEnvAsync();
        Assert.True((await e.Gates.StartAsync("g1", Rte, expectedVersion: 1)).IsOk);
        var stale = await e.Gates.FailAsync("g1", Rte, expectedVersion: 1);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        var current = Assert.IsType<StageGates>(stale.Current);
        Assert.Equal(2, current.Version);
        Assert.Equal("InProgress", current.Status);
    }

    [PgFact]
    public async Task Every_write_bumps_Version_and_stamps_LastChanged()
    {
        var e = await fx.NewEnvAsync();
        e.Time.Advance(TimeSpan.FromMinutes(1));
        var r = await e.Gates.StartAsync("g1", Dev);
        Assert.Equal(2, r.Value!.Version);
        Assert.Equal("dev", r.Value.LastChangedByUserId);
        Assert.Equal(new DateTime(2026, 10, 20, 14, 1, 0, DateTimeKind.Utc), r.Value.LastChangedAt);
    }

    [PgFact]
    public async Task Waiver_needs_a_reason_a_different_person_and_a_Governance_Officer()
    {
        var e = await fx.NewEnvAsync();
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
        Assert.Equal([Domain.Services.Guards.IllegalGateTransition], (await e.Gates.ReopenAsync("g2", Gov1)).Failures.Select(f => f.Guard));
    }

    [PgFact]
    public async Task Train_walks_Planning_to_Complete_only_when_every_guard_is_met()
    {
        var e = await fx.NewEnvAsync();
        await Certify(e, "g1");
        Assert.True((await e.Trains.AdvanceAsync("t1", "Gated", Rte)).IsOk);
        var g = Guards(await e.Trains.AdvanceAsync("t1", "Executing", Rte)).ToHashSet();
        Assert.Contains(Domain.Services.Guards.GateLockout, g);
        Assert.Contains(Domain.Services.Guards.ExecutingRequiresGo, g);
        Assert.Contains(Domain.Services.Guards.RollbackNotRehearsed, g);
        Assert.DoesNotContain(Domain.Services.Guards.ExecutingRequiresBaseline, g);

        Assert.True((await e.Tasks.CompleteAsync("k2", Gov1)).IsOk);
        Assert.True((await e.Gates.CertifyAsync("g2", Gov2)).IsOk);
        e.Sql("INSERT INTO \"GoNoGoDecisions\"(\"Id\",\"ReleaseTrainId\",\"Decision\",\"DecidedByUserId\",\"DecidedAt\",\"GateSnapshotJson\") VALUES('d1','t1','Go','rm','2026-10-20T13:00:00Z','{}')");
        Assert.Equal([Domain.Services.Guards.RollbackNotRehearsed], Guards(await e.Trains.AdvanceAsync("t1", "Executing", Rte)));
        Assert.True((await e.Trains.RecordRollbackRehearsedAsync("t1", Rte)).IsOk);

        var exec = await e.Trains.AdvanceAsync("t1", "Executing", Rte);
        Assert.True(exec.IsOk);
        Assert.Equal("Executing", exec.Value!.CurrentStatus);
        Assert.NotNull(exec.Value.ActualStartAt);

        Assert.Equal([Domain.Services.Guards.CloseCodeRequired], Guards(await e.Trains.CompleteAsync("t1", "Bogus", null, Rte)));
        Assert.True((await e.Trains.CompleteAsync("t1", "SuccessfulWithIssues", "rollback of one product", Rte)).IsOk);
        Assert.Equal(1, e.Count("SELECT count(*) FROM \"PostImplementationReviews\" WHERE \"ReleaseTrainId\"='t1' AND \"RequiredReason\"='CloseCode=SuccessfulWithIssues'"));
        Assert.Equal("rte", e.Query("SELECT \"ActorUserId\" FROM \"AuditEvents\" WHERE \"Action\"='AutoRequired'").Single()[0]);
    }
}
