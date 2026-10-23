using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Infrastructure.Tests.OpsKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-113: approval policies, approvals created from them with per-seat decisions, Request changes, separation of duties, policy checks, exceptions with a declined state, policy coverage.</summary>
public sealed class ApprovalTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly Actor Rte = new("rte"), Rte2 = new("rte2"), Rm = new("rm"), Gov1 = new("gov1"), Gov2 = new("gov2"), Dev = new("dev");

    private static readonly SeatInput[] ThreeSeats =
    [
        new("Security reviewer", "Team", "@sec", 24), new("Release owner", "ReleaseOwner", null, 24), new("SRE approver", "WorkspaceRole", Roles.RTE, 8),
    ];

    /// <summary>Production environment e1, team @sec (dev and gov1), a second RTE, train t1 owned by rm, and the policy "Production" for High risk and above.</summary>
    private async Task<(OpsKit Kit, string PolicyId)> Setup(bool gatesCertified = true, bool openWindow = false)
    {
        var k = new OpsKit(fx).Seed();
        k.Sql($@"
            INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('rte2','rte2@x.com','Rae Two','RTE');
            INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('default','rte2','RTE','{Iso(T0)}');
            INSERT INTO Teams(Id,Handle,Name) VALUES('tmsec','sec','Security');
            INSERT INTO TeamMembers(TeamId,UserId) VALUES('tmsec','dev'),('tmsec','gov1');
            UPDATE ReleaseTrains SET OwnerUserId='rm' WHERE Id='t1';
            INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e2','Staging','Staging','{Iso(T0)}');");
        var p = await k.Policies.CreateAsync(new("Production", "Production", "High", gatesCertified, openWindow, ThreeSeats), Rm);
        Assert.True(p.IsOk, string.Join(";", p.Failures.Select(f => f.Message)));
        if (gatesCertified) k.Sql("DELETE FROM StageGates WHERE ReleaseTrainId='t1'");   // no gate is left to certify: the check passes
        return (k, p.Value!.Id);
    }

    private static string Why(ServiceResult<ApprovalView> r) => string.Join(" | ", r.Failures.Select(f => f.Message));

    // ---- policies -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_policy_names_the_environment_the_risk_tier_the_checks_and_the_seats_and_is_validated()
    {
        var (k, id) = await Setup();
        var p = (await k.Policies.GetAsync(id))!;
        Assert.Equal(("Production", "Production", "High", true, false), (p.Name, p.EnvironmentKind, p.MinRiskTier, p.RequireGatesCertified, p.RequireOpenWindow));
        Assert.Equal(["Security reviewer", "Release owner", "SRE approver"], p.Seats.Select(s => s.Label));
        Assert.Equal(("@sec", 24), (p.Seats[0].ValueName, p.Seats[0].DueHours));
        Assert.Equal((null, 8), (p.Seats[1].Value, p.Seats[2].DueHours));
        Assert.Equal(1, k.Audits("ApprovalPolicy", "Create"));

        Task<ServiceResult<PolicyView>> Make(NewPolicy n, Actor who) => k.Policies.CreateAsync(n, who);
        Assert.Contains(ApprovalGuards.PolicyNameTaken, GuardsOf(await Make(new("production", Seats: ThreeSeats), Rm)));   // any case
        Assert.Contains(ApprovalGuards.PolicyRole, GuardsOf(await Make(new("Other", Seats: ThreeSeats), Dev)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Nothing"), Rm)));   // no seats
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Kind", "Dev", Seats: ThreeSeats), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Tier", MinRiskTier: "Severe", Seats: ThreeSeats), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Dup", Seats: [new("A", "ReleaseOwner"), new("a", "ReleaseOwner")]), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Role", Seats: [new("SRE", "WorkspaceRole", "SRE")]), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Team", Seats: [new("T", "Team", "@nobody")]), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Owner", Seats: [new("O", "ReleaseOwner", "rm")]), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Due", Seats: [new("O", "ReleaseOwner", null, 0)]), Rm)));
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await Make(new("Many", Seats: [.. Enumerable.Range(0, 11).Select(i => new SeatInput($"S{i}", "ReleaseOwner"))]), Rm)));
        Assert.Equal(1, k.Count("SELECT count(*) FROM ApprovalPolicies"));
        // governance officers manage policies too
        Assert.True((await Make(new("By governance", Seats: [new("Owner", "ReleaseOwner")]), Gov1)).IsOk);
    }

    [Fact]
    public async Task Editing_a_policy_replaces_its_seats_needs_the_version_and_a_no_op_writes_nothing()
    {
        var (k, id) = await Setup();
        var v = (await k.Policies.GetAsync(id))!.Version;
        Assert.Equal(ResultKind.Conflict, (await k.Policies.UpdateAsync(id, new(Name: "Prod"), Rm, v + 5)).Kind);
        var same = await k.Policies.UpdateAsync(id, new(Name: "Production"), Rm, v);
        Assert.True(same.IsOk);
        Assert.Equal((v, 0), ((await k.Policies.GetAsync(id))!.Version, k.Audits("ApprovalPolicy", "Update")));   // nothing changed: no Version bump, no audit row
        var changed = await k.Policies.UpdateAsync(id, new(MinRiskTier: "Low", RequireOpenWindow: true, Seats: [new("Owner", "ReleaseOwner", null, 12), new("SRE approver", "WorkspaceRole", Roles.RTE, 4)]), Rm, v);
        Assert.True(changed.IsOk, string.Join(";", changed.Failures.Select(f => f.Message)));
        Assert.Equal(["Owner", "SRE approver"], changed.Value!.Seats.Select(s => s.Label));
        Assert.Equal((v + 1, "Low", true), (changed.Value.Version, changed.Value.MinRiskTier, changed.Value.RequireOpenWindow));
        Assert.Equal(1, k.Audits("ApprovalPolicy", "Update"));
        var cleared = await k.Policies.UpdateAsync(id, new(Clear: ["minRiskTier"]), Rm, v + 1);
        Assert.Null(cleared.Value!.MinRiskTier);
        Assert.Contains(ApprovalGuards.PolicyInvalid, GuardsOf(await k.Policies.UpdateAsync(id, new(Clear: ["name"]), Rm, v + 2)));
        Assert.True((await k.Policies.SetArchivedAsync(id, true, Rm, null)).Value!.Archived);
        Assert.Empty(await k.Policies.ListAsync());
        Assert.Single(await k.Policies.ListAsync(includeArchived: true));
    }

    // ---- creating an approval from the policies -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approval_takes_its_seats_and_due_times_from_the_policies_that_match_the_release_and_the_environment()
    {
        var (k, _) = await Setup();
        var r = await k.Approvals.RequestAsync(new("t1", "e1"), Rte);
        Assert.True(r.IsOk, Why(r));
        var a = r.Value!;
        Assert.Equal(("Pending", 1, "rte", "Production"), (a.Status, a.Round, a.RequestedByUserId, a.EnvironmentName));
        Assert.Equal(new[] { "Release owner", "SRE approver", "Security reviewer" }, a.Decisions.Select(d => d.Label).Order(StringComparer.Ordinal).ToArray());
        var due = a.Decisions.ToDictionary(d => d.Label, d => d.DueAt);
        Assert.Equal(("2026-10-20T22:00:00Z", "2026-10-21T14:00:00Z", "2026-10-21T14:00:00Z"), (due["SRE approver"], due["Security reviewer"], due["Release owner"]));
        Assert.Equal("2026-10-20T22:00:00Z", a.DueAt);   // the earliest of its seats
        Assert.Equal(["Production"], a.PolicyNames);
        Assert.Equal(1, k.Audits("Approval", "Request"));
        Assert.Equal("t1", k.Text("SELECT ReleaseTrainId FROM AuditEvents WHERE EntityType='Approval' AND Action='Request'"));

        // refusals, each with its reason
        Assert.Contains(ApprovalGuards.ApprovalAlreadyOpen, GuardsOf(await k.Approvals.RequestAsync(new("t1", "e1"), Rm)));
        Assert.Contains(ApprovalGuards.ApprovalRole, GuardsOf(await k.Approvals.RequestAsync(new("t1", "e1"), Dev)));
        Assert.Contains(ApprovalGuards.ApprovalRole, GuardsOf(await k.Approvals.RequestAsync(new("t1", "e1"), Gov1)));
        var staging = await k.Approvals.RequestAsync(new("t1", "e2"), Rte);   // the policy is for Production
        Assert.Equal([ApprovalGuards.NoApprovalPolicy], GuardsOf(staging));
        Assert.Contains("Staging", staging.Failures[0].Message);
        k.Sql("UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'");   // below the policy's minimum
        k.Sql("UPDATE Approvals SET Status='Cancelled'");
        Assert.Equal([ApprovalGuards.NoApprovalPolicy], GuardsOf(await k.Approvals.RequestAsync(new("t1", "e1"), Rte)));
        Assert.Equal(ResultKind.NotFound, (await k.Approvals.RequestAsync(new("nope", "e1"), Rte)).Kind);
    }

    [Fact]
    public async Task A_seat_nobody_can_fill_is_refused_in_words_before_it_holds_a_release()
    {
        var (k, _) = await Setup();
        k.Sql("UPDATE ReleaseTrains SET OwnerUserId=NULL WHERE Id='t1'");
        var noOwner = await k.Approvals.RequestAsync(new("t1", "e1"), Rte);
        Assert.Equal([ApprovalGuards.SeatUnfillable], GuardsOf(noOwner));
        Assert.Contains("no owner", noOwner.Failures[0].Message);
        k.Sql("UPDATE ReleaseTrains SET OwnerUserId='rm' WHERE Id='t1'");
        var self = await k.Approvals.RequestAsync(new("t1", "e1"), Rm);   // the owner asking for their own release's approval: nobody else holds the owner seat
        Assert.Equal([ApprovalGuards.SeatUnfillable], GuardsOf(self));
        Assert.Contains("Release owner", self.Failures[0].Message);
        k.Sql("DELETE FROM TeamMembers WHERE TeamId='tmsec'");
        Assert.Contains("Security reviewer: nobody holds it", (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Failures[0].Message);
        Assert.Equal(0, k.Count("SELECT count(*) FROM Approvals"));
    }

    // ---- deciding -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_seat_must_approve_by_an_eligible_person_and_each_decision_is_audited()
    {
        var (k, _) = await Setup();
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        Task<ServiceResult<ApprovalView>> Approve(string seat, Actor who, int? v = null) => k.Approvals.DecideAsync(a.Id, new(seat, "Approved"), who, v);

        // not eligible: the reason names the seat and who holds it
        var gov = await Approve("SRE approver", Gov1);
        Assert.Contains(ApprovalGuards.NotEligibleApprover, GuardsOf(gov));
        Assert.Contains("RTE", gov.Failures[0].Message);
        Assert.Contains("@sec", (await Approve("Security reviewer", Rte2)).Failures[0].Message);
        Assert.Contains("release owner", (await Approve("Release owner", Dev)).Failures[0].Message);
        Assert.Contains(ApprovalGuards.SeatNotFound, GuardsOf(await Approve("Legal", Dev)));
        // the requester cannot approve what they requested, even in a role the seat names
        k.Sql("UPDATE Approvals SET RequestedByUserId='rte2'");
        Assert.Contains(ApprovalGuards.SeparationOfDuties, GuardsOf(await Approve("SRE approver", Rte2)));
        k.Sql("UPDATE Approvals SET RequestedByUserId='rte'");
        // the release owner on a Production change approves only the owner seat
        k.Sql("INSERT INTO TeamMembers(TeamId,UserId) VALUES('tmsec','rm')");
        var owner = await Approve("Security reviewer", Rm);
        Assert.Contains(ApprovalGuards.SeparationOfDuties, GuardsOf(owner));
        Assert.Contains("release owner approves only the release owner seat", string.Join(" ", owner.Failures.Select(f => f.Message)));
        Assert.Equal(0, k.Audits("ApprovalDecision", "Approve"));

        var v = a.Version;
        Assert.Equal(ResultKind.Conflict, (await Approve("Security reviewer", Dev, v + 7)).Kind);
        var first = await Approve("Security reviewer", Dev, v);
        Assert.True(first.IsOk, Why(first));
        Assert.Equal("Pending", first.Value!.Status);
        Assert.Equal(("Approved", "dev"), (first.Value.Decisions.Single(d => d.Label == "Security reviewer").Decision, first.Value.Decisions.Single(d => d.Label == "Security reviewer").DecidedByUserId));
        Assert.Contains(ApprovalGuards.SeatDecided, GuardsOf(await Approve("Security reviewer", Dev)));
        Assert.True((await Approve("Release owner", Rm)).IsOk);
        var last = await Approve("SRE approver", Rte2);
        Assert.Equal(("Approved", true), (last.Value!.Status, last.Value.DecidedAt is not null));
        Assert.Equal(3, k.Audits("ApprovalDecision", "Approve"));
        Assert.Equal(1, k.Audits("Approval", "Approved"));
        var by = k.Query("SELECT ActorUserId FROM AuditEvents WHERE EntityType='ApprovalDecision' AND Action='Approve' ORDER BY Id").Select(r => (string)r[0]!).ToList();
        Assert.Equal(["dev", "rm", "rte2"], by);
        Assert.Contains(ApprovalGuards.ApprovalNotOpen, GuardsOf(await Approve("Release owner", Rm)));   // closed
        Assert.Contains(ApprovalGuards.ApprovalNotOpen, GuardsOf(await k.Approvals.CancelAsync(a.Id, Rte, null)));
    }

    [Fact]
    public async Task One_person_cannot_hold_two_seats_of_a_Production_change()
    {
        var (k, _) = await Setup();
        k.Sql("INSERT INTO TeamMembers(TeamId,UserId) VALUES('tmsec','rte2')");   // rte2 now fills both the RTE seat and the team's
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        Assert.True((await k.Approvals.DecideAsync(a.Id, new("SRE approver", "Approved"), Rte2, null)).IsOk);
        var second = await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "Approved"), Rte2, null);
        Assert.Contains(ApprovalGuards.SeparationOfDuties, GuardsOf(second));
        Assert.Contains("SRE approver", second.Failures[0].Message);
        // on Staging the same person may: separation is for Production
        k.Sql("UPDATE Environments SET Kind='Staging' WHERE Id='e1'");
        Assert.True((await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "Approved"), Rte2, null)).IsOk);
    }

    [Fact]
    public async Task Approve_is_refused_until_every_policy_check_passes_with_the_reason_in_words()
    {
        var (k, _) = await Setup(gatesCertified: false, openWindow: true);
        // the policy asks for every gate to be certified or waived, and for a deployment window that has not ended
        k.Sql("UPDATE Approvals SET Status='Cancelled'");
        var policy = (await k.Policies.ListAsync()).Single();
        await k.Policies.UpdateAsync(policy.Id, new(RequireGatesCertified: true, RequireOpenWindow: true), Rm, null);
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        var checks = a.Checks.ToDictionary(c => c.Name);
        Assert.Equal((true, false), (checks["Evidence complete"].Required, checks["Evidence complete"].Passed));
        Assert.Contains("Code Freeze", checks["Evidence complete"].Detail);
        Assert.Equal((true, false, "The release has no deployment window"), (checks["Within change window"].Required, checks["Within change window"].Passed, checks["Within change window"].Detail));

        var refused = await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "Approved"), Dev, null);
        Assert.Equal([ApprovalGuards.PolicyCheckFailed], GuardsOf(refused));
        Assert.Contains("Evidence complete", refused.Failures[0].Message);
        Assert.Contains("Within change window", refused.Failures[0].Message);
        Assert.Contains("Code Freeze", refused.Failures[0].Message);
        Assert.Equal(["Evidence complete", "Within change window"], refused.Failures[0].Items!.Order());
        Assert.Equal(0, k.Audits("ApprovalDecision", "Approve"));   // refused: nothing written
        // Request changes is not an approval: the checks do not stand in its way
        Assert.True((await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "ChangesRequested", "Gates are not done"), Dev, null)).IsOk);

        // gates passed, a window that has not ended
        k.Sql("DELETE FROM StageGates WHERE ReleaseTrainId='t1'");
        k.Sql($"INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','{Iso(T0.AddHours(1))}','{Iso(T0.AddHours(5))}')");
        await k.Approvals.ResubmitAsync(a.Id, Rte, null);
        var ok = await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "Approved"), Dev, null);
        Assert.True(ok.IsOk, Why(ok));
        k.Time.Advance(TimeSpan.FromHours(6));   // the window has ended
        var late = await k.Approvals.DecideAsync(a.Id, new("Release owner", "Approved"), Rm, null);
        Assert.Contains("ended", late.Failures[0].Message);
    }

    [Fact]
    public async Task Request_changes_needs_a_note_sends_the_approval_back_and_a_resubmit_opens_a_new_round()
    {
        var (k, _) = await Setup();
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        Assert.Contains(ApprovalGuards.NoteRequired, GuardsOf(await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "ChangesRequested", "  "), Dev, null)));
        Assert.True((await k.Approvals.DecideAsync(a.Id, new("Release owner", "Approved"), Rm, null)).IsOk);
        var changes = await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "ChangesRequested", "The threat model is out of date"), Dev, null);
        Assert.True(changes.IsOk, Why(changes));
        Assert.Equal("ChangesRequested", changes.Value!.Status);
        Assert.Equal("The threat model is out of date", changes.Value.Decisions.Single(d => d.Label == "Security reviewer").Note);
        Assert.Equal((1, 1), (k.Audits("ApprovalDecision", "RequestChanges"), k.Audits("Approval", "ChangesRequested")));
        // while it is sent back nobody decides, and the requester is the one to act
        var blocked = await k.Approvals.DecideAsync(a.Id, new("SRE approver", "Approved"), Rte2, null);
        Assert.Contains(ApprovalGuards.ApprovalNotOpen, GuardsOf(blocked));
        Assert.Contains("resubmit", blocked.Failures[0].Message);
        Assert.Contains(ApprovalGuards.ApprovalRole, GuardsOf(await k.Approvals.ResubmitAsync(a.Id, Dev, null)));
        Assert.Contains(ApprovalGuards.ApprovalAlreadyOpen, GuardsOf(await k.Approvals.RequestAsync(new("t1", "e1"), Rm)));   // still the open one

        k.Time.Advance(TimeSpan.FromHours(2));
        var again = await k.Approvals.ResubmitAsync(a.Id, Rte, null);
        Assert.True(again.IsOk, Why(again));
        Assert.Equal(("Pending", 2), (again.Value!.Status, again.Value.Round));
        Assert.All(again.Value.Decisions, d => Assert.Null(d.Decision));
        Assert.Equal("2026-10-21T00:00:00Z", again.Value.DueAt);   // 8 h after the new round began (16:00)
        Assert.Equal(1, k.Audits("Approval", "Resubmit"));
        Assert.Contains(ApprovalGuards.ApprovalNotOpen, GuardsOf(await k.Approvals.ResubmitAsync(a.Id, Rte, null)));   // nothing to resubmit
        foreach (var (seat, who) in new[] { ("Security reviewer", Dev), ("Release owner", Rm), ("SRE approver", Rte2) })
            Assert.True((await k.Approvals.DecideAsync(a.Id, new(seat, "Approved"), who, null)).IsOk);
        Assert.Equal("Approved", (await k.Approvals.GetAsync(a.Id, "rte"))!.Status);
    }

    [Fact]
    public async Task The_database_holds_the_rules_too_so_a_second_client_cannot_get_past_them()
    {
        var (k, _) = await Setup();
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        var seat = k.Text("SELECT Id FROM ApprovalDecisions WHERE Label='SRE approver'");
        string Try(string who, string decision = "Approved", string note = "NULL") => Assert.Throws<SqliteException>(() =>
            k.Sql($"UPDATE ApprovalDecisions SET Decision='{decision}', DecidedByUserId='{who}', DecidedAt='{Iso(T0)}', Note={note} WHERE Id='{seat}'")).Message;
        Assert.Contains("Not an eligible approver", Try("gov1"));
        Assert.Contains("you cannot approve a change you requested", Try("rte"));
        Assert.Contains("CHECK", Try("rte2", "ChangesRequested"));   // a note is required
        Assert.Contains("Every required approver must approve first", Assert.Throws<SqliteException>(() => k.Sql("UPDATE Approvals SET Status='Approved', DecidedAt='2026-10-20T14:00:00Z'")).Message);
        k.Sql($"UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rte2', DecidedAt='{Iso(T0)}' WHERE Id='{seat}'");
        Assert.Contains("A decision is final", Try("rte2", "ChangesRequested", "'changed my mind'"));
        Assert.Equal(a.Id, k.Text("SELECT Id FROM Approvals"));
    }

    // ---- the queue ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_approval_queue_lists_the_seats_a_person_may_decide_now()
    {
        var (k, _) = await Setup();
        var queue = new ApprovalQueueService(k.Db, k.Time, k.Config, k.Approvals);
        var a = (await k.Approvals.RequestAsync(new("t1", "e1"), Rte)).Value!;
        var dev = await queue.GetAsync("dev");
        var item = Assert.Single(dev.Items, i => i.Kind == ApprovalQueueService.DecideApproval);
        Assert.Equal(($"{a.Id}:{a.Decisions.Single(d => d.Label == "Security reviewer").Id}", "Approval: Security reviewer", "t1"), (item.Id, item.Title, item.TrainId));
        Assert.Equal(1, dev.Counts.Seats);
        Assert.Contains((await queue.GetAsync("rm")).Items, i => i.Title == "Approval: Release owner");
        Assert.Equal(1, (await queue.GetAsync("rte2")).Counts.Seats);
        Assert.Equal(0, (await queue.GetAsync("rte")).Counts.Seats);   // the requester has nothing to decide
        await k.Approvals.DecideAsync(a.Id, new("Security reviewer", "Approved"), Dev, null);
        Assert.Equal(0, (await queue.GetAsync("dev")).Counts.Seats);
        var views = await k.Approvals.ListAsync(new(Mine: true), "rm");
        Assert.Single(views);
        Assert.Contains(views[0].Decisions, d => d.Label == "Release owner" && d.CanDecide);
        Assert.Contains(views[0].Decisions, d => d.Label == "SRE approver" && !d.CanDecide && d.WhyNot!.Contains("RTE"));
    }

    // ---- exceptions: waivers and freeze overrides, with a declined state -----------------------------------------------------------

    [Fact]
    public async Task A_waiver_can_be_declined_by_another_governance_officer_with_a_reason_and_a_declined_one_cannot_be_approved()
    {
        var k = new OpsKit(fx).Seed();
        const string reason = "The vendor slipped; we accept the risk in writing";
        var w = (await k.Waivers.RequestAsync("g1", reason, Gov1)).Value!;
        var w2 = (await k.Waivers.RequestAsync("g1", reason, Gov1)).Value!;
        Assert.Contains(ApprovalGuards.WaiverDeclineSelf, GuardsOf(await k.Waivers.DeclineAsync(w.Id, "Withdrawn", Gov1)));
        Assert.Contains(ApprovalGuards.WaiverDeclineRole, GuardsOf(await k.Waivers.DeclineAsync(w.Id, "No", Rm)));
        Assert.Contains(ApprovalGuards.DeclineNote, GuardsOf(await k.Waivers.DeclineAsync(w.Id, " ", Gov2)));
        var declined = await k.Waivers.DeclineAsync(w.Id, "The risk is not covered by the rollback plan", Gov2);
        Assert.True(declined.IsOk);
        Assert.Equal(("gov2", "The risk is not covered by the rollback plan"), (declined.Value!.DeclinedByUserId, declined.Value.DeclineNote));
        Assert.Equal(1, k.Audits("GateWaiver", "Decline"));
        Assert.Equal("t1", k.Text("SELECT ReleaseTrainId FROM AuditEvents WHERE Action='Decline'"));
        Assert.Contains(ApprovalGuards.WaiverAlreadyDecided, GuardsOf(await k.Waivers.DeclineAsync(w.Id, "again", Gov2)));
        Assert.Contains(Guards.WaiverAlreadyDecided, GuardsOf(await k.Waivers.ApproveAsync(w.Id, Gov2)));   // a declined waiver is final
        // the existing rules keep working: the other waiver is approved by a different officer
        Assert.True((await k.Waivers.ApproveAsync(w2.Id, Gov2)).IsOk);
        Assert.Contains(ApprovalGuards.WaiverAlreadyDecided, GuardsOf(await k.Waivers.DeclineAsync(w2.Id, "too late", Gov2)));

        var list = await k.Exceptions.ListAsync();
        Assert.Equal((0, 1, 1), (list.Pending, list.Approved, list.Declined));
        var item = list.Items.Single(i => i.State == "Declined");
        Assert.Equal(("Waiver", "Code Freeze", "gov1", "gov2", "The risk is not covered by the rollback plan", reason), (item.Kind, item.Subject, item.RequestedByUserId, item.DecidedByUserId, item.Note, item.Reason));
        // and the queue no longer asks anyone to approve it
        var queue = new ApprovalQueueService(k.Db, k.Time, k.Config, k.Approvals);
        Assert.DoesNotContain((await queue.GetAsync("gov2")).Items, i => i.Id == w.Id);
    }

    [Fact]
    public async Task A_freeze_override_request_can_be_declined_and_waivers_and_overrides_are_listed_together()
    {
        var k = new OpsKit(fx).Seed();
        k.Sql($"INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('f1','Year end','Freeze','{Iso(T0)}','{Iso(T0.AddDays(10))}','gov1')");
        const string reason = "The payments fix has to ship before the quarter closes";
        Assert.True((await k.Freezes.RequestOverrideAsync("f1", "t1", reason, null, Rte)).IsOk);
        Assert.True((await k.Freezes.RequestOverrideAsync("f1", "t1", reason, null, Rm)).IsOk);
        Assert.Equal(2, (await k.Exceptions.ListAsync(kind: "FreezeOverride")).Pending);

        Assert.Contains(ApprovalGuards.OverrideDeclineRole, GuardsOf(await k.Freezes.DeclineOverrideAsync("f1", "t1", "rte", "No", Dev)));
        Assert.Contains(ApprovalGuards.OverrideDeclineSelf, GuardsOf(await k.Freezes.DeclineOverrideAsync("f1", "t1", "rm", "No", Rm)));
        Assert.Contains(ApprovalGuards.DeclineNote, GuardsOf(await k.Freezes.DeclineOverrideAsync("f1", "t1", "rte", "", Rm)));
        Assert.Contains(ApprovalGuards.OverrideRequestNotFound, GuardsOf(await k.Freezes.DeclineOverrideAsync("f1", "t1", "dev", "No request from this person", Rm)));
        Assert.True((await k.Freezes.DeclineOverrideAsync("f1", "t1", "rte", "Not for this freeze", Rm)).IsOk);
        Assert.Contains(ApprovalGuards.OverrideRequestNotFound, GuardsOf(await k.Freezes.DeclineOverrideAsync("f1", "t1", "rte", "twice", Rm)));
        Assert.Equal(1, k.Audits("FreezeWindow", "DeclineOverride"));
        // the declined request is closed: it cannot be granted, and the other is still open and can
        Assert.Contains(FreezeService.OverrideNotRequested, GuardsOf(await k.Freezes.GrantOverrideAsync("f1", new("t1", "rte", reason, T0.AddDays(2)), Gov1)));
        Assert.True((await k.Freezes.GrantOverrideAsync("f1", new("t1", "rm", reason, T0.AddDays(2)), Gov1)).IsOk);
        // a waiver beside them
        var w = (await k.Waivers.RequestAsync("g1", "The vendor slipped; accepted in writing", Gov1)).Value!;

        var all = await k.Exceptions.ListAsync();
        Assert.Equal((1, 1, 1, 0), (all.Pending, all.Approved, all.Declined, all.Expired));   // the waiver is pending, the override granted, the other request declined
        Assert.Equal(["Waiver", "FreezeOverride"], all.Items.Where(i => i.State == "Pending").Select(i => i.Kind).Concat(all.Items.Where(i => i.State == "Approved").Select(i => i.Kind)).Take(2));
        var declined = Assert.Single((await k.Exceptions.ListAsync("Declined")).Items);
        Assert.Equal(("FreezeOverride", "Year end", "rte", "rm", "Not for this freeze"), (declined.Kind, declined.Subject, declined.RequestedByUserId, declined.DecidedByUserId, declined.Note));
        k.Time.Advance(TimeSpan.FromDays(3));   // the granted override ends
        Assert.Equal(1, (await k.Exceptions.ListAsync()).Expired);
        Assert.Single((await k.Exceptions.ListAsync(kind: "Waiver")).Items);
        Assert.Empty((await k.Exceptions.ListAsync(trainId: "nope")).Items);
        Assert.NotNull(w);
    }

    // ---- policy coverage ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Policy_coverage_is_the_share_of_production_releases_an_active_policy_governs()
    {
        var (k, _) = await Setup();
        string Train(string id, string risk, string date) => $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('{id}','{id}','{date}','{risk}','{Iso(T0)}','{Iso(T0)}');";
        k.Sql(Train("p1", "High", "2026-11-10") + Train("p2", "VeryHigh", "2026-11-11") + Train("p3", "Low", "2026-11-12") + Train("p4", "Moderate", "2026-11-13") + Train("s1", "High", "2026-11-14")
            + "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES ('p1','e1'),('p2','e1'),('p3','e1'),('p4','e1'),('s1','e2');");   // s1 targets Staging only
        var c = await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal((4, 2, 50.0), (c.ProductionReleases, c.Governed, c.CoveragePct));   // High and VeryHigh are governed, Low and Moderate are not
        Assert.Equal(["p3", "p4"], c.Ungoverned.Select(u => u.Title).Order());
        Assert.Equal(0, c.WithApproval);
        // a policy for every Production release raises it; an archived one counts for nothing
        await k.Policies.CreateAsync(new("Everything in production", "Production", Seats: [new("Owner", "ReleaseOwner")]), Rm);
        Assert.Equal((4, 4, 100.0), ((await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).ProductionReleases, (await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).Governed, (await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).CoveragePct));
        foreach (var p in await k.Policies.ListAsync()) await k.Policies.SetArchivedAsync(p.Id, true, Rm, null);
        Assert.Equal((4, 0, 0.0), ((await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).ProductionReleases, (await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).Governed, (await k.Policies.CoverageAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).CoveragePct));
        // no production releases: no percentage rather than a made-up one
        Assert.Null((await k.Policies.CoverageAsync(new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31))).CoveragePct);
    }
}
