using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Infrastructure.Tests.OpsKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-175 and REOS-177 (D56, D63): change requests, CAB meetings and the separation-of-duties rules through the domain services, on a migrated database with the test clock.
/// Users: rte, rm, gov1, gov2, dev (all members of the default workspace). Every transition and every refusal has a case; the trigger backstop is in <see cref="CabTriggerBackstopTests"/>.
/// </summary>
public sealed class CabServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly Actor Rte = new("rte"), Rm = new("rm"), Gov1 = new("gov1"), Gov2 = new("gov2"), Dev = new("dev");

    private sealed class Recorder : IRealtimePublisher
    {
        public List<(string Ws, string Module, string Event, object?[] Args)> Events { get; } = [];
        public Task TrainChangedAsync(string workspaceId, string trainId, int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotificationCreatedAsync(string workspaceId, string userId, string notificationId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForecastChangedAsync(string workspaceId, string trainId, string runId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAlertRaisedAsync(string? workspaceId, string alertId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeploymentChangedAsync(string workspaceId, string deploymentId, int version, string? trainId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ModuleEventAsync(string workspaceId, string moduleKey, string eventName, object?[] args, CancellationToken ct = default) { Events.Add((workspaceId, moduleKey, eventName, args)); return Task.CompletedTask; }
    }

    private sealed record Kit(OpsKit K, ChangeRequestService Changes, CabService Cab, Recorder Live);

    private Kit Make()
    {
        var k = new OpsKit(fx);
        var live = new Recorder();
        return new Kit(k, new ChangeRequestService(k.Db, k.Time, live), new CabService(k.Db, k.Time, live), live);
    }

    private static readonly DateTime W0 = new(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc);
    private static NewChange Normal(string title = "Patch the payment gateway", string? requester = null, string type = "Normal") =>
        new(title, type, RollbackNote: "Redeploy 4.4.9", PlannedStartAt: W0, PlannedEndAt: W0.AddHours(2), RequestedByUserId: requester);

    private static async Task<ChangeView> Ok(Task<ServiceResult<ChangeView>> t)
    {
        var r = await t;
        Assert.True(r.IsOk, r.Kind + ": " + string.Join(" | ", r.Failures.Select(f => $"{f.Guard}: {f.Message}")));
        return r.Value!;
    }

    private static async Task<string> Guard<T>(Task<ServiceResult<T>> t)
    {
        var r = await t;
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        return r.Failures[0].Guard;
    }

    private static async Task<ChangeView> Submitted(Kit s, string requester = "dev", string type = "Normal", Actor? by = null)
    {
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: requester, type: type), by ?? Rte));
        return await Ok(s.Changes.SubmitAsync(c.Id, by ?? Rte, c.Version));
    }

    private static DecisionInput Approve(string? meeting = null, bool retro = false) => new("Approved", "Looks fine", meeting, retro);

    // ---- create, read, edit ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_is_numbered_per_workspace_starts_as_a_Draft_and_is_audited_with_the_service_clock()
    {
        var s = Make();
        var a = await Ok(s.Changes.CreateAsync(new("Add index", "Standard"), Rte));
        var b = await Ok(s.Changes.CreateAsync(Normal(), Rm));
        Assert.Equal(["CHG-0001", "CHG-0002"], [a.Key, b.Key]);
        Assert.Equal(("Draft", "Medium", 1, "Standard"), (a.Status, a.RiskLevel, a.Version, a.ChangeType));
        Assert.Equal(("rte", "rte"), (a.RequestedBy.Id, a.CreatedBy.Id));
        Assert.Equal(Iso(T0), a.CreatedAt);
        Assert.Equal(2, s.K.Audits("ChangeRequest", "Create"));   // one audit row per write
        Assert.Equal(Iso(T0), s.K.Text("SELECT OccurredAt FROM AuditEvents WHERE EntityId=?", a.Id));
        Assert.Equal("rte", s.K.Text("SELECT ActorUserId FROM AuditEvents WHERE EntityId=?", a.Id));
        Assert.Equal("rte", s.K.Text("SELECT LastChangedByUserId FROM ChangeRequests WHERE Id=?", a.Id));
        Assert.Equal(Iso(T0), s.K.Text("SELECT LastChangedAt FROM ChangeRequests WHERE Id=?", a.Id));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7", a.Id);   // UUIDv7
    }

    [Fact]
    public async Task A_change_raised_on_someone_elses_behalf_names_both_people()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rm));
        Assert.Equal(("dev", "rm"), (c.RequestedBy.Id, c.CreatedBy.Id));
        Assert.Equal(CabGuards.CabNotMember, await Guard(s.Changes.CreateAsync(Normal(requester: "ghost"), Rm)));
    }

    [Fact]
    public async Task Creating_validates_the_fields_and_links_a_release_and_its_GoNoGo_decision()
    {
        var s = Make();
        s.K.Sql("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('gn1','t1','Go','rm',?,'[]')", Iso(T0));
        s.K.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t9','Other','2026-11-30',?,?)", Iso(T0), Iso(T0));
        foreach (var bad in new[]
        {
            new NewChange("  ", "Normal"), new NewChange("T", "Major"), new NewChange("T", "Normal", RiskLevel: "Extreme"), new NewChange("T", "Normal", PlannedStartAt: W0),
            new NewChange("T", "Normal", PlannedStartAt: W0, PlannedEndAt: W0), new NewChange("T", "Normal", ReleaseTrainId: "nope"), new NewChange("T", "Normal", GoNoGoDecisionId: "nope"),
            new NewChange("T", "Normal", ReleaseTrainId: "t9", GoNoGoDecisionId: "gn1"), new NewChange(new string('x', 201), "Normal"),
        })
            Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.CreateAsync(bad, Rte)));
        Assert.Equal(0, s.K.Count("SELECT count(*) FROM ChangeRequests"));
        var c = await Ok(s.Changes.CreateAsync(new("Linked", "Normal", GoNoGoDecisionId: "gn1"), Rte));   // the decision names its release
        Assert.Equal(("t1", "gn1"), (c.Release!.Id, c.GoNoGoDecisionId));
        Assert.Equal("R26.10", c.Release.Title);
        Assert.Equal(1, s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='ChangeRequest' AND ReleaseTrainId='t1'"));   // filed under the release too
    }

    [Fact]
    public async Task A_draft_is_edited_with_If_Match_and_a_stale_version_answers_with_the_current_row()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(Normal(), Rte));
        s.K.Time.Advance(TimeSpan.FromMinutes(5));
        var u = await Ok(s.Changes.UpdateAsync(c.Id, new(Title: "Patch gateway v2", RiskLevel: "High", RiskFactors: "Touches card flows"), Rm, 1));
        Assert.Equal(("Patch gateway v2", "High", 2), (u.Title, u.RiskLevel, u.Version));
        Assert.Equal(("rm", Iso(T0.AddMinutes(5))), (s.K.Text("SELECT LastChangedByUserId FROM ChangeRequests WHERE Id=?", c.Id), s.K.Text("SELECT LastChangedAt FROM ChangeRequests WHERE Id=?", c.Id)));
        var stale = await s.Changes.UpdateAsync(c.Id, new(Title: "Late"), Rte, 1);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.Equal(2, ((ChangeView)stale.Current!).Version);
        Assert.Equal("Patch gateway v2", s.K.Text("SELECT Title FROM ChangeRequests WHERE Id=?", c.Id));
        var cleared = await Ok(s.Changes.UpdateAsync(c.Id, new(Clear: ["riskFactors", "window"]), Rte, 2));
        Assert.Equal((null, null, null), (cleared.RiskFactors, cleared.PlannedStartAt, cleared.PlannedEndAt));
        Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.UpdateAsync(c.Id, new(Clear: ["status"]), Rte, 3)));
        Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.UpdateAsync(c.Id, new(RiskLevel: "Nope"), Rte, 3)));
        Assert.Equal(3, s.K.Count("SELECT Version FROM ChangeRequests WHERE Id=?", c.Id));   // refused edits changed nothing
        Assert.Equal(2, s.K.Audits("ChangeRequest", "Update"));
        Assert.Equal(ResultKind.NotFound, (await s.Changes.UpdateAsync("nope", new(Title: "x"), Rte, 1)).Kind);
    }

    [Fact]
    public async Task Only_a_Draft_is_edited()
    {
        var s = Make();
        var c = await Submitted(s);
        Assert.Equal(CabGuards.ChangeNotEditable, await Guard(s.Changes.UpdateAsync(c.Id, new(Title: "x"), Rte, c.Version)));
    }

    [Fact]
    public async Task List_filters_by_status_type_and_release_and_get_returns_the_decisions()
    {
        var s = Make();
        var a = await Submitted(s);
        var b = await Ok(s.Changes.CreateAsync(new("Hotfix", "Emergency"), Rte));
        Assert.Equal(2, (await s.Changes.ListAsync()).Count);
        Assert.Equal([a.Id], (await s.Changes.ListAsync(status: "Submitted")).Select(c => c.Id));
        Assert.Equal([b.Id], (await s.Changes.ListAsync(type: "Emergency")).Select(c => c.Id));
        Assert.Empty(await s.Changes.ListAsync(releaseTrainId: "t1"));
        await Ok(s.Changes.DecideAsync(a.Id, new("Deferred", "Need rollback test"), Gov1, a.Version));
        var got = await s.Changes.GetAsync(a.Id);
        Assert.Equal(["Deferred"], got!.Decisions.Select(d => d.Decision));
        Assert.Null(await s.Changes.GetAsync("nope"));
    }

    // ---- the status machine ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_goes_Draft_Submitted_Approved_Implemented_Closed_each_step_versioned_and_audited_by_whoever_did_it()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rte));
        s.K.Time.Advance(TimeSpan.FromMinutes(1));
        c = await Ok(s.Changes.SubmitAsync(c.Id, Rte, 1));
        Assert.Equal(("Submitted", 2, Iso(T0.AddMinutes(1))), (c.Status, c.Version, c.SubmittedAt));
        s.K.Time.Advance(TimeSpan.FromMinutes(1));
        c = await Ok(s.Changes.DecideAsync(c.Id, Approve(), Gov1, 2));
        Assert.Equal(("Approved", 3, "gov1", Iso(T0.AddMinutes(2))), (c.Status, c.Version, c.DecidedBy!.Id, c.DecidedAt));
        s.K.Time.Advance(TimeSpan.FromMinutes(1));
        c = await Ok(s.Changes.ImplementAsync(c.Id, Rm, 3));
        Assert.Equal(("Implemented", 4, Iso(T0.AddMinutes(3))), (c.Status, c.Version, c.ImplementedAt));
        s.K.Time.Advance(TimeSpan.FromMinutes(1));
        c = await Ok(s.Changes.CloseAsync(c.Id, Rm, 4));
        Assert.Equal(("Closed", 5, Iso(T0.AddMinutes(4))), (c.Status, c.Version, c.ClosedAt));
        foreach (var (action, actor) in new[] { ("Create", "rte"), ("Submit", "rte"), ("Decide", "gov1"), ("Implement", "rm"), ("Close", "rm") })
            Assert.Equal(actor, s.K.Text("SELECT ActorUserId FROM AuditEvents WHERE EntityType='ChangeRequest' AND EntityId=? AND Action=?", c.Id, action));
        Assert.Equal(5, s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='ChangeRequest' AND EntityId=?", c.Id));   // exactly one row per write
        Assert.Equal("rm", s.K.Text("SELECT LastChangedByUserId FROM ChangeRequests WHERE Id=?", c.Id));
        Assert.Contains("\"status\":\"Approved\"", s.K.Text("SELECT AfterJson FROM AuditEvents WHERE EntityId=? AND Action='Decide'", c.Id));
    }

    [Fact]
    public async Task Submitting_needs_a_rollback_note_and_for_a_Standard_or_Normal_change_a_planned_window()
    {
        var s = Make();
        var noNote = await Ok(s.Changes.CreateAsync(new("T", "Normal", PlannedStartAt: W0, PlannedEndAt: W0.AddHours(1)), Rte));
        Assert.Equal(CabGuards.ChangeNeedsRollbackNote, await Guard(s.Changes.SubmitAsync(noNote.Id, Rte, 1)));
        var noWindow = await Ok(s.Changes.CreateAsync(new("T", "Standard", RollbackNote: "undo"), Rte));
        Assert.Equal(CabGuards.ChangeNeedsWindow, await Guard(s.Changes.SubmitAsync(noWindow.Id, Rte, 1)));
        var emergency = await Ok(s.Changes.CreateAsync(new("T", "Emergency", RollbackNote: "undo"), Rte));
        Assert.Equal("Submitted", (await Ok(s.Changes.SubmitAsync(emergency.Id, Rte, 1))).Status);   // an emergency has no time to plan a window
        Assert.Equal(1, s.K.Audits("ChangeRequest", "Submit"));
    }

    [Fact]
    public async Task Every_illegal_move_is_refused_with_a_readable_422_and_changes_nothing()
    {
        var s = Make();
        var draft = await Ok(s.Changes.CreateAsync(Normal(), Rte));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.ImplementAsync(draft.Id, Rte, 1)));   // Draft > Implemented
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.CloseAsync(draft.Id, Rte, 1)));       // Draft > Closed
        Assert.Equal(CabGuards.ChangeNotAwaitingDecision, await Guard(s.Changes.DecideAsync(draft.Id, Approve(), Gov1, 1)));   // a Draft is not waiting for the CAB
        var sub = await Ok(s.Changes.SubmitAsync(draft.Id, Rte, 1));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.SubmitAsync(sub.Id, Rte, sub.Version)));    // Submitted > Submitted
        Assert.Equal(CabGuards.OnlyEmergencyBeforeApproval, await Guard(s.Changes.ImplementAsync(sub.Id, Rte, sub.Version)));   // Normal: Submitted > Implemented
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.CloseAsync(sub.Id, Rte, sub.Version)));
        var appr = await Ok(s.Changes.DecideAsync(sub.Id, Approve(), Gov1, sub.Version));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.SubmitAsync(appr.Id, Rte, appr.Version)));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.CloseAsync(appr.Id, Rte, appr.Version)));    // Approved > Closed
        Assert.Equal(CabGuards.ChangeNotAwaitingDecision, await Guard(s.Changes.DecideAsync(appr.Id, Approve(), Gov2, appr.Version)));   // already decided
        var impl = await Ok(s.Changes.ImplementAsync(appr.Id, Rte, appr.Version));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.CancelAsync(impl.Id, Rte, impl.Version)));   // Implemented > Cancelled
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.ImplementAsync(impl.Id, Rte, impl.Version)));
        var closed = await Ok(s.Changes.CloseAsync(impl.Id, Rte, impl.Version));
        foreach (var t in new[] { s.Changes.SubmitAsync(closed.Id, Rte, closed.Version), s.Changes.CancelAsync(closed.Id, Rte, closed.Version), s.Changes.ImplementAsync(closed.Id, Rte, closed.Version), s.Changes.CloseAsync(closed.Id, Rte, closed.Version) })
            Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(t));   // Closed is final
        Assert.Equal(5, s.K.Count("SELECT Version FROM ChangeRequests WHERE Id=?", closed.Id));
        Assert.Equal(ResultKind.NotFound, (await s.Changes.SubmitAsync("nope", Rte, 1)).Kind);
    }

    [Fact]
    public async Task A_change_is_cancelled_from_Draft_Submitted_or_Approved_and_Cancelled_is_final()
    {
        var s = Make();
        var d = await Ok(s.Changes.CreateAsync(Normal(), Rte));
        Assert.Equal("Cancelled", (await Ok(s.Changes.CancelAsync(d.Id, Rte, 1))).Status);
        var sub = await Submitted(s);
        Assert.Equal("Cancelled", (await Ok(s.Changes.CancelAsync(sub.Id, Rte, sub.Version))).Status);
        var a = await Submitted(s);
        var appr = await Ok(s.Changes.DecideAsync(a.Id, Approve(), Gov1, a.Version));
        var cancelled = await Ok(s.Changes.CancelAsync(appr.Id, Rte, appr.Version));
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.SubmitAsync(cancelled.Id, Rte, cancelled.Version)));
        Assert.Equal(3, s.K.Audits("ChangeRequest", "Cancel"));
    }

    [Fact]
    public async Task A_rejection_needs_a_rationale_is_final_and_names_who_rejected()
    {
        var s = Make();
        var c = await Submitted(s);
        Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.DecideAsync(c.Id, new("Rejected"), Gov1, c.Version)));
        Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.DecideAsync(c.Id, new("Maybe", "x"), Gov1, c.Version)));
        var r = await Ok(s.Changes.DecideAsync(c.Id, new("Rejected", "No rollback test"), Gov1, c.Version));
        Assert.Equal(("Rejected", "gov1"), (r.Status, r.DecidedBy!.Id));
        Assert.Equal("No rollback test", r.Decisions.Single().Rationale);
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.CancelAsync(r.Id, Rte, r.Version)));
        Assert.Equal(CabGuards.IllegalChangeTransition, await Guard(s.Changes.SubmitAsync(r.Id, Rte, r.Version)));
    }

    [Fact]
    public async Task A_deferral_is_recorded_and_the_change_keeps_waiting_unchanged_in_version()
    {
        var s = Make();
        var c = await Submitted(s);
        var d = await Ok(s.Changes.DecideAsync(c.Id, new("Deferred", "Bring the test results"), Gov1, c.Version));
        Assert.Equal(("Submitted", c.Version), (d.Status, d.Version));
        Assert.Equal(1, s.K.Count("SELECT count(*) FROM CabDecisions WHERE ChangeRequestId=? AND Decision='Deferred'", c.Id));
        Assert.Equal(1, s.K.Audits("ChangeRequest", "Decide"));
        var again = await Ok(s.Changes.DecideAsync(c.Id, Approve(), Gov2, d.Version));   // and it can still be decided
        Assert.Equal("Approved", again.Status);
        Assert.Equal(["Deferred", "Approved"], again.Decisions.Select(x => x.Decision));
    }

    [Fact]
    public async Task A_stale_If_Match_on_any_move_answers_409_with_the_current_row_and_changes_nothing()
    {
        var s = Make();
        var c = await Submitted(s);
        foreach (var t in new[] { s.Changes.DecideAsync(c.Id, Approve(), Gov1, 1), s.Changes.CancelAsync(c.Id, Rte, 1), s.Changes.ImplementAsync(c.Id, Rte, 1), s.Changes.CloseAsync(c.Id, Rte, 1), s.Changes.SubmitAsync(c.Id, Rte, 1) })
        {
            var r = await t;
            Assert.Equal(ResultKind.Conflict, r.Kind);
            Assert.Equal(2, ((ChangeView)r.Current!).Version);
        }
        Assert.Equal(("Submitted", 2), (s.K.Text("SELECT Status FROM ChangeRequests WHERE Id=?", c.Id), (int)s.K.Count("SELECT Version FROM ChangeRequests WHERE Id=?", c.Id)));
        Assert.Equal(0, s.K.Count("SELECT count(*) FROM CabDecisions"));
    }

    [Fact]
    public async Task A_person_outside_the_workspace_cannot_move_or_decide()
    {
        var s = Make();
        var c = await Submitted(s);
        s.K.Sql("INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('ghost','g@x.com','Ghost','Viewer')");
        Assert.Equal(CabGuards.CabNotMember, await Guard(s.Changes.DecideAsync(c.Id, Approve(), new("ghost"), c.Version)));
        Assert.Equal(CabGuards.CabNotMember, await Guard(s.Changes.CancelAsync(c.Id, new("ghost"), c.Version)));
    }

    // ---- REOS-177 rule 1: a requester cannot approve or reject their own change ------------------------------------------------------------

    [Fact]
    public async Task Rule_1_the_requester_cannot_approve_or_reject_their_own_change_and_the_refusal_is_audited()
    {
        var s = Make();
        var c = await Submitted(s, requester: "rte");   // rte asked for it
        var r = await s.Changes.DecideAsync(c.Id, Approve(), Rte, c.Version);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal((CabGuards.ChangeSelfApproval, CabRuleText.SelfApproval), (r.Failures[0].Guard, r.Failures[0].Message));
        Assert.Equal(CabGuards.ChangeSelfApproval, await Guard(s.Changes.DecideAsync(c.Id, new("Rejected", "I changed my mind"), Rte, c.Version)));
        Assert.Equal(("Submitted", 0), (s.K.Text("SELECT Status FROM ChangeRequests WHERE Id=?", c.Id), (int)s.K.Count("SELECT count(*) FROM CabDecisions")));
        Assert.Equal(2, s.K.Audits("ChangeRequest", "DecisionRefused"));   // each attempt is on record, with who tried and which rule said no
        Assert.Equal("rte", s.K.Text("SELECT ActorUserId FROM AuditEvents WHERE Action='DecisionRefused' LIMIT 1"));
        Assert.Contains(CabGuards.ChangeSelfApproval, s.K.Text("SELECT AfterJson FROM AuditEvents WHERE Action='DecisionRefused' LIMIT 1"));
        Assert.Equal(Iso(T0), s.K.Text("SELECT OccurredAt FROM AuditEvents WHERE Action='DecisionRefused' LIMIT 1"));
        Assert.Equal("Approved", (await Ok(s.Changes.DecideAsync(c.Id, Approve(), Gov1, c.Version))).Status);   // someone else can
    }

    [Fact]
    public async Task Rule_1_holds_for_a_change_raised_on_behalf_of_the_requester_by_someone_else()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "gov1"), Rm));   // rm keyed it in, gov1 is the requester
        c = await Ok(s.Changes.SubmitAsync(c.Id, Rm, c.Version));
        Assert.Equal(CabGuards.ChangeSelfApproval, await Guard(s.Changes.DecideAsync(c.Id, Approve(), Gov1, c.Version)));
        Assert.Equal("Approved", (await Ok(s.Changes.DecideAsync(c.Id, Approve(), Rm, c.Version))).Status);   // the person who merely keyed it in is not the requester (rule 2 covers the chair)
    }

    [Fact]
    public async Task Rule_1_does_not_stop_the_requester_deferring_or_the_service_checking_before_the_state()
    {
        var s = Make();
        var c = await Submitted(s, requester: "rte");
        Assert.Equal("Submitted", (await Ok(s.Changes.DecideAsync(c.Id, new("Deferred", "Waiting for the vendor"), Rte, c.Version))).Status);
        var draft = await Ok(s.Changes.CreateAsync(Normal(requester: "rte"), Rte));
        Assert.Equal(CabGuards.ChangeSelfApproval, await Guard(s.Changes.DecideAsync(draft.Id, Approve(), Rte, 1)));   // the person's own approval is refused before the state is looked at
    }

    // ---- REOS-177 rule 2: the chair is not the only approver of a change they raised -------------------------------------------------------

    private async Task<(Kit S, string Meeting)> ChairedByRm()
    {
        var s = Make();
        var m = await s.Cab.CreateAsync(new("Weekly CAB", W0.AddDays(-1), W0.AddDays(-1).AddHours(1), "rm"), Rm);
        Assert.True(m.IsOk);
        return (s, m.Value!.Id);
    }

    [Fact]
    public async Task Rule_2_the_chair_who_raised_the_change_cannot_approve_it_before_a_second_approver_has()
    {
        var (s, meeting) = await ChairedByRm();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rm));   // the chair keyed it in for dev
        c = await Ok(s.Changes.SubmitAsync(c.Id, Rm, c.Version));
        var r = await s.Changes.DecideAsync(c.Id, Approve(meeting), Rm, c.Version);
        Assert.Equal((CabGuards.ChangeChairSoleApprover, CabRuleText.ChairSoleApprover), (r.Failures[0].Guard, r.Failures[0].Message));
        Assert.Equal(("Submitted", 0), (s.K.Text("SELECT Status FROM ChangeRequests WHERE Id=?", c.Id), (int)s.K.Count("SELECT count(*) FROM CabDecisions")));
        Assert.Equal(1, s.K.Audits("ChangeRequest", "DecisionRefused"));
        Assert.Equal("rm", s.K.Text("SELECT ActorUserId FROM AuditEvents WHERE Action='DecisionRefused'"));
        Assert.Equal("Approved", (await Ok(s.Changes.DecideAsync(c.Id, Approve(meeting), Gov1, c.Version))).Status);   // a second person approves
    }

    [Fact]
    public async Task Rule_2_applies_to_recording_outside_the_meeting_when_the_change_is_on_the_chairs_agenda()
    {
        var (s, meeting) = await ChairedByRm();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rm));
        c = await Ok(s.Changes.SubmitAsync(c.Id, Rm, c.Version));
        Assert.True((await s.Cab.AddAgendaItemAsync(meeting, c.Id, null, Rm, 1)).IsOk);
        Assert.Equal(CabGuards.ChangeChairSoleApprover, await Guard(s.Changes.DecideAsync(c.Id, Approve(), Rm, c.Version)));   // no meetingId: still the chair of the meeting it is on
    }

    [Fact]
    public async Task Rule_2_does_not_stop_a_chair_who_did_not_raise_the_change_or_a_chairs_rejection()
    {
        var (s, meeting) = await ChairedByRm();
        var c = await Submitted(s, requester: "dev", by: Rte);   // keyed in by rte, requested by dev: the chair raised nothing
        Assert.Equal("Approved", (await Ok(s.Changes.DecideAsync(c.Id, Approve(meeting), Rm, c.Version))).Status);
        var d = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rm));
        d = await Ok(s.Changes.SubmitAsync(d.Id, Rm, d.Version));
        Assert.Equal("Rejected", (await Ok(s.Changes.DecideAsync(d.Id, new("Rejected", "Too risky", meeting), Rm, d.Version))).Status);   // the rule guards approvals, not refusals
        var e = await Ok(s.Changes.CreateAsync(Normal(requester: "dev"), Rm));
        e = await Ok(s.Changes.SubmitAsync(e.Id, Rm, e.Version));
        Assert.Equal("Submitted", (await Ok(s.Changes.DecideAsync(e.Id, new("Deferred", "Next week", meeting), Rm, e.Version))).Status);
    }

    // ---- REOS-177 rule 3: Emergency changes still need a second person ---------------------------------------------------------------

    [Fact]
    public async Task Rule_3_an_Emergency_change_may_be_implemented_first_but_not_closed_until_a_second_person_has_reviewed_it()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(new("Hotfix payment outage", "Emergency", RollbackNote: "Redeploy 4.4.9", RequestedByUserId: "rte"), Rte));
        c = await Ok(s.Changes.SubmitAsync(c.Id, Rte, c.Version));
        c = await Ok(s.Changes.ImplementAsync(c.Id, Rte, c.Version));   // before approval: allowed for an Emergency
        Assert.Equal(("Implemented", true), (c.Status, c.NeedsReview));
        var refused = await s.Changes.CloseAsync(c.Id, Rte, c.Version);
        Assert.Equal((CabGuards.ChangeEmergencyNeedsReview, CabRuleText.EmergencyNeedsReview), (refused.Failures[0].Guard, refused.Failures[0].Message));
        Assert.Equal(1, s.K.Audits("ChangeRequest", "CloseRefused"));
        Assert.Equal(CabGuards.ChangeSelfApproval, await Guard(s.Changes.DecideAsync(c.Id, Approve(retro: true), Rte, c.Version)));   // the requester cannot review their own emergency
        Assert.Equal(CabGuards.ChangeNotAwaitingDecision, await Guard(s.Changes.DecideAsync(c.Id, Approve(), Gov1, c.Version)));      // not a normal decision either
        Assert.Equal(CabGuards.ChangeEmergencyNeedsReview, await Guard(s.Changes.CloseAsync(c.Id, Rm, c.Version)));   // nor can anyone else close it yet
        var reviewed = await Ok(s.Changes.DecideAsync(c.Id, new("Approved", "Justified, runbook followed", null, true), Gov1, c.Version));
        Assert.Equal(("Implemented", false, c.Version), (reviewed.Status, reviewed.NeedsReview, reviewed.Version));   // a review moves nothing
        Assert.True(reviewed.Decisions.Single().Retroactive);
        Assert.Equal(1, s.K.Audits("ChangeRequest", "RetroactiveReview"));
        var closed = await Ok(s.Changes.CloseAsync(c.Id, Rte, reviewed.Version));
        Assert.Equal("Closed", closed.Status);
        Assert.Equal((1, 2), (s.K.Audits("ChangeRequest", "DecisionRefused"), s.K.Audits("ChangeRequest", "CloseRefused")));   // the refused review by the requester, and two refused closes
    }

    [Fact]
    public async Task A_retroactive_rejection_is_recorded_and_visible_and_the_change_can_then_be_closed()
    {
        var s = Make();
        var c = await Submitted(s, requester: "rte", type: "Emergency", by: Rte);
        c = await Ok(s.Changes.ImplementAsync(c.Id, Rte, c.Version));
        var r = await Ok(s.Changes.DecideAsync(c.Id, new("Rejected", "Not an emergency: it could have waited", null, true), Gov2, c.Version));
        Assert.Equal(("Implemented", "Rejected", true), (r.Status, r.Decisions.Single().Decision, r.Decisions.Single().Retroactive));
        Assert.Equal("Closed", (await Ok(s.Changes.CloseAsync(c.Id, Rte, r.Version))).Status);
    }

    [Fact]
    public async Task A_retroactive_review_is_only_for_an_implemented_Emergency_change_and_must_be_a_verdict()
    {
        var s = Make();
        var normal = await Submitted(s);
        Assert.Equal(CabGuards.ChangeNotAwaitingDecision, await Guard(s.Changes.DecideAsync(normal.Id, Approve(retro: true), Gov1, normal.Version)));
        var em = await Submitted(s, type: "Emergency");   // Submitted, not yet implemented
        Assert.Equal(CabGuards.ChangeNotAwaitingDecision, await Guard(s.Changes.DecideAsync(em.Id, Approve(retro: true), Gov1, em.Version)));
        var impl = await Ok(s.Changes.ImplementAsync(em.Id, Rte, em.Version));
        Assert.Equal(CabGuards.ChangeInvalid, await Guard(s.Changes.DecideAsync(impl.Id, new("Deferred", "later", null, true), Gov1, impl.Version)));
    }

    [Fact]
    public async Task An_Emergency_change_that_was_approved_the_normal_way_closes_without_a_retroactive_review()
    {
        var s = Make();
        var c = await Submitted(s, requester: "dev", type: "Emergency");
        c = await Ok(s.Changes.DecideAsync(c.Id, Approve(), Gov1, c.Version));
        c = await Ok(s.Changes.ImplementAsync(c.Id, Rte, c.Version));
        Assert.False(c.NeedsReview);
        Assert.Equal("Closed", (await Ok(s.Changes.CloseAsync(c.Id, Rte, c.Version))).Status);
    }

    // ---- live events -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_committed_write_pushes_a_CAB_module_event_and_a_refusal_pushes_nothing()
    {
        var s = Make();
        var c = await Ok(s.Changes.CreateAsync(Normal(requester: "rte"), Rte));
        var (ws, module, name, args) = s.Live.Events.Single();
        Assert.Equal(("default", "cab", "CabChanged", c.Id), (ws, module, name, args[0]));
        await Ok(s.Changes.SubmitAsync(c.Id, Rte, 1));
        Assert.Equal(2, s.Live.Events.Count);
        var before = s.Live.Events.Count;
        await s.Changes.DecideAsync(c.Id, Approve(), Rte, 2);   // refused (rule 1)
        Assert.Equal(before, s.Live.Events.Count);
    }

    // ---- meetings --------------------------------------------------------------------------------------------------------------------

    private static async Task<CabMeetingView> Meeting(Kit s, string chair = "rm", Actor? by = null)
    {
        var r = await s.Cab.CreateAsync(new("Weekly CAB", W0.AddDays(2), W0.AddDays(2).AddHours(1), chair, "Room 4"), by ?? Rm);
        Assert.True(r.IsOk, string.Join(";", r.Failures.Select(f => f.Message)));
        return r.Value!;
    }

    private static async Task<CabMeetingView> Ok(Task<ServiceResult<CabMeetingView>> t)
    {
        var r = await t;
        Assert.True(r.IsOk, r.Kind + ": " + string.Join(" | ", r.Failures.Select(f => $"{f.Guard}: {f.Message}")));
        return r.Value!;
    }

    [Fact]
    public async Task A_meeting_has_a_chair_who_is_on_the_roll_and_every_write_is_audited()
    {
        var s = Make();
        var m = await Meeting(s);
        Assert.Equal(("Planned", 1, "rm", "Room 4"), (m.Status, m.Version, m.Chair.Id, m.Location));
        Assert.Equal(("rm", true, "Member", "Invited"), (m.Attendees.Single().Person.Id, m.Attendees.Single().IsChair, m.Attendees.Single().AttendeeRole, m.Attendees.Single().Attendance));
        Assert.Equal(1, s.K.Audits("CabMeeting", "Create"));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7", m.Id);
        Assert.Equal("rm", s.K.Text("SELECT LastChangedByUserId FROM CabMeetings WHERE Id=?", m.Id));
        Assert.Equal(Iso(T0), s.K.Text("SELECT LastChangedAt FROM CabMeetings WHERE Id=?", m.Id));
        Assert.Equal("rte", (await Meeting(s, chair: "", by: Rte)).Chair.Id);   // no chair named: the person creating it
        foreach (var bad in new[] { new NewMeeting(" ", W0, W0.AddHours(1)), new NewMeeting("x", W0, W0), new NewMeeting("x", W0, W0.AddHours(1), "ghost") })
            Assert.Equal(bad.ChairUserId == "ghost" ? CabGuards.CabNotMember : CabGuards.CabMeetingInvalid, await Guard(s.Cab.CreateAsync(bad, Rm)));
        Assert.Equal(2, s.K.Count("SELECT count(*) FROM CabMeetings"));
    }

    [Fact]
    public async Task A_meeting_is_edited_with_If_Match_and_a_new_chair_joins_the_roll()
    {
        var s = Make();
        var m = await Meeting(s);
        var u = await Ok(s.Cab.UpdateAsync(m.Id, new(Title: "Thursday CAB", ChairUserId: "gov1", Location: "Zoom"), Rte, 1));
        Assert.Equal(("Thursday CAB", "gov1", "Zoom", 2), (u.Title, u.Chair.Id, u.Location, u.Version));
        Assert.Equal(["gov1", "rm"], u.Attendees.Select(a => a.Person.Id).Order());
        Assert.Equal(ResultKind.Conflict, (await s.Cab.UpdateAsync(m.Id, new(Title: "Late"), Rte, 1)).Kind);
        Assert.Equal(2, ((CabMeetingView)(await s.Cab.UpdateAsync(m.Id, new(Title: "Late"), Rte, 1)).Current!).Version);
        Assert.Equal(CabGuards.CabNotMember, await Guard(s.Cab.UpdateAsync(m.Id, new(ChairUserId: "ghost"), Rte, 2)));
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.UpdateAsync(m.Id, new(EndsAt: W0), Rte, 2)));   // ends before it starts
        var cleared = await Ok(s.Cab.UpdateAsync(m.Id, new(ClearLocation: true), Rte, 2));
        Assert.Null(cleared.Location);
        Assert.Equal(3, s.K.Count("SELECT Version FROM CabMeetings WHERE Id=?", m.Id));
        Assert.Equal(2, s.K.Audits("CabMeeting", "Update"));
    }

    [Fact]
    public async Task A_meeting_moves_Planned_InProgress_Completed_or_Cancelled_and_nothing_else()
    {
        var s = Make();
        var m = await Meeting(s);
        Assert.Equal(CabGuards.IllegalMeetingTransition, await Guard(s.Cab.CompleteAsync(m.Id, Rm, 1)));   // not held yet
        var started = await Ok(s.Cab.StartAsync(m.Id, Rm, 1));
        Assert.Equal(("InProgress", 2), (started.Status, started.Version));
        Assert.Equal(CabGuards.IllegalMeetingTransition, await Guard(s.Cab.StartAsync(m.Id, Rm, 2)));
        var done = await Ok(s.Cab.CompleteAsync(m.Id, Rm, 2));
        Assert.Equal("Completed", done.Status);
        Assert.Equal(CabGuards.IllegalMeetingTransition, await Guard(s.Cab.CancelAsync(m.Id, Rm, 3)));
        Assert.Equal(CabGuards.IllegalMeetingTransition, await Guard(s.Cab.StartAsync(m.Id, Rm, 3)));
        var other = await Meeting(s);
        Assert.Equal("Cancelled", (await Ok(s.Cab.CancelAsync(other.Id, Rm, 1))).Status);
        Assert.Equal(CabGuards.IllegalMeetingTransition, await Guard(s.Cab.StartAsync(other.Id, Rm, 2)));
        Assert.Equal(1, s.K.Audits("CabMeeting", "Start") + s.K.Audits("CabMeeting", "Complete") - 1);
        Assert.Equal(ResultKind.NotFound, (await s.Cab.StartAsync("nope", Rm, 1)).Kind);
        // minutes can still be written after the meeting, nothing else
        var minutes = await Ok(s.Cab.UpdateAsync(m.Id, new(Minutes: "Approved CHG-0001"), Rm, 3));
        Assert.Equal("Approved CHG-0001", minutes.Minutes);
        Assert.Equal(CabGuards.CabMeetingClosed, await Guard(s.Cab.UpdateAsync(m.Id, new(Title: "Renamed"), Rm, 4)));
    }

    [Fact]
    public async Task The_agenda_takes_submitted_changes_once_in_order_and_closes_with_the_meeting()
    {
        var s = Make();
        var m = await Meeting(s);
        var a = await Submitted(s);
        var b = await Submitted(s);
        var draft = await Ok(s.Changes.CreateAsync(Normal(), Rte));
        m = await Ok(s.Cab.AddAgendaItemAsync(m.Id, a.Id, "Discuss rollback", Rm, 1));
        m = await Ok(s.Cab.AddAgendaItemAsync(m.Id, b.Id, null, Rm, 2));
        Assert.Equal(["CHG-0001", "CHG-0002"], m.Agenda.Select(i => i.ChangeKey));
        Assert.Equal([1, 2], m.Agenda.Select(i => i.SequenceOrder));
        Assert.Equal("Discuss rollback", m.Agenda[0].Notes);
        Assert.Equal(CabGuards.CabDuplicate, await Guard(s.Cab.AddAgendaItemAsync(m.Id, a.Id, null, Rm, m.Version)));
        Assert.Equal(CabGuards.CabChangeNotSubmitted, await Guard(s.Cab.AddAgendaItemAsync(m.Id, draft.Id, null, Rm, m.Version)));
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.AddAgendaItemAsync(m.Id, "nope", null, Rm, m.Version)));
        Assert.Equal(ResultKind.Conflict, (await s.Cab.AddAgendaItemAsync(m.Id, draft.Id, null, Rm, 1)).Kind);
        var item = m.Agenda[0];
        var edited = await s.Cab.UpdateAgendaItemAsync(m.Id, item.Id, "Rollback tested", 3, Rm, item.Version);
        Assert.True(edited.IsOk);
        Assert.Equal(("Rollback tested", 3, 2), (edited.Value!.Notes, edited.Value.SequenceOrder, edited.Value.Version));
        var stale = await s.Cab.UpdateAgendaItemAsync(m.Id, item.Id, "x", null, Rm, item.Version);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.Equal(2, ((CabAgendaItemView)stale.Current!).Version);
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.UpdateAgendaItemAsync(m.Id, item.Id, null, 0, Rm, 2)));
        m = await Ok(s.Cab.RemoveAgendaItemAsync(m.Id, m.Agenda[1].Id, Rm, (await s.Cab.GetAsync(m.Id))!.Version));
        Assert.Single(m.Agenda);
        Assert.Equal(1, s.K.Audits("CabAgendaItem", "Delete"));
        Assert.Equal(2, s.K.Audits("CabAgendaItem", "Create"));
        m = await Ok(s.Cab.StartAsync(m.Id, Rm, m.Version));
        m = await Ok(s.Cab.CompleteAsync(m.Id, Rm, m.Version));
        Assert.Equal(CabGuards.CabMeetingClosed, await Guard(s.Cab.AddAgendaItemAsync(m.Id, b.Id, null, Rm, m.Version)));
        Assert.Equal(CabGuards.CabMeetingClosed, await Guard(s.Cab.RemoveAgendaItemAsync(m.Id, m.Agenda[0].Id, Rm, m.Version)));
    }

    [Fact]
    public async Task Attendees_are_invited_once_marked_present_or_absent_and_the_chair_stays_on_the_roll()
    {
        var s = Make();
        var m = await Meeting(s);
        m = await Ok(s.Cab.InviteAsync(m.Id, "gov1", null, Rm, 1));
        m = await Ok(s.Cab.InviteAsync(m.Id, "dev", "Guest", Rm, 2));
        Assert.Equal(["dev", "gov1", "rm"], m.Attendees.Select(a => a.Person.Id).Order());
        Assert.Equal("rm", m.Attendees[0].Person.Id);   // the chair is listed first
        Assert.True(m.Attendees[0].IsChair);
        Assert.Equal("Guest", m.Attendees.Single(a => a.Person.Id == "dev").AttendeeRole);
        Assert.Equal(CabGuards.CabDuplicate, await Guard(s.Cab.InviteAsync(m.Id, "gov1", null, Rm, m.Version)));
        Assert.Equal(CabGuards.CabNotMember, await Guard(s.Cab.InviteAsync(m.Id, "ghost", null, Rm, m.Version)));
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.InviteAsync(m.Id, "gov2", "Chair", Rm, m.Version)));
        Assert.Equal(ResultKind.Conflict, (await s.Cab.InviteAsync(m.Id, "gov2", null, Rm, 1)).Kind);
        var row = m.Attendees.Single(a => a.Person.Id == "gov1");
        var present = await s.Cab.SetAttendanceAsync(m.Id, "gov1", "Attended", Rm, row.Version);
        Assert.True(present.IsOk);
        Assert.Equal(("Attended", 2), (present.Value!.Attendance, present.Value.Version));
        Assert.Equal(ResultKind.Conflict, (await s.Cab.SetAttendanceAsync(m.Id, "gov1", "Absent", Rm, row.Version)).Kind);
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.SetAttendanceAsync(m.Id, "gov1", "Maybe", Rm, 2)));
        Assert.Equal(ResultKind.NotFound, (await s.Cab.SetAttendanceAsync(m.Id, "gov2", "Attended", Rm, 1)).Kind);
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Cab.RemoveAttendeeAsync(m.Id, "rm", Rm, (await s.Cab.GetAsync(m.Id))!.Version)));   // the chair stays
        m = await Ok(s.Cab.RemoveAttendeeAsync(m.Id, "dev", Rm, (await s.Cab.GetAsync(m.Id))!.Version));
        Assert.DoesNotContain(m.Attendees, a => a.Person.Id == "dev");
        Assert.Equal(1, s.K.Audits("CabAttendee", "Attendance"));
        Assert.Equal(2, s.K.Audits("CabAttendee", "Invite"));
        Assert.Equal(1, s.K.Audits("CabAttendee", "Remove"));
    }

    [Fact]
    public async Task Decisions_recorded_under_a_meeting_show_on_the_meeting_and_a_cancelled_meeting_records_none()
    {
        var s = Make();
        var m = await Meeting(s);
        var c = await Submitted(s, requester: "dev");
        c = await Ok(s.Changes.DecideAsync(c.Id, Approve(m.Id), Gov1, c.Version));
        var view = (await s.Cab.GetAsync(m.Id))!;
        Assert.Equal(("Approved", "gov1"), (view.Decisions.Single().Decision, view.Decisions.Single().DecidedBy.Id));
        var d = await Submitted(s, requester: "dev");
        var cancelled = await Meeting(s);
        await Ok(s.Cab.CancelAsync(cancelled.Id, Rm, 1));
        Assert.Equal(CabGuards.CabMeetingClosed, await Guard(s.Changes.DecideAsync(d.Id, Approve(cancelled.Id), Gov1, d.Version)));
        Assert.Equal(CabGuards.CabMeetingInvalid, await Guard(s.Changes.DecideAsync(d.Id, Approve("nope"), Gov1, d.Version)));
        Assert.Single(await s.Cab.ListAsync(), x => x.Decisions.Count > 0);
        Assert.Equal(2, (await s.Cab.ListAsync(status: "Planned")).Count + (await s.Cab.ListAsync(status: "Cancelled")).Count);
    }

    // ---- the database is the backstop, and what it refuses is a readable 422 DbRule, never a 500 ------------------------------------------------

    [Fact]
    public async Task A_rule_the_database_enforces_that_the_service_did_not_catch_is_a_DbRule_422_and_rolls_everything_back()
    {
        var s = Make();
        var c = await Submitted(s, requester: "dev");
        s.K.Sql("CREATE TRIGGER trg_test_race BEFORE INSERT ON CabDecisions BEGIN SELECT RAISE(ABORT, 'Separation of duties: a competing write got there first'); END;");   // stands in for a race or clock skew
        var r = await s.Changes.DecideAsync(c.Id, Approve(), Gov1, c.Version);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal((Guards.DbRule, "Separation of duties: a competing write got there first"), (r.Failures[0].Guard, r.Failures[0].Message));
        Assert.Equal(("Submitted", 0), (s.K.Text("SELECT Status FROM ChangeRequests WHERE Id=?", c.Id), (int)s.K.Count("SELECT count(*) FROM CabDecisions")));
        Assert.Equal(0, s.K.Audits("ChangeRequest", "Decide"));   // the audit row rolled back with the write
        await Task.CompletedTask;
    }
}
