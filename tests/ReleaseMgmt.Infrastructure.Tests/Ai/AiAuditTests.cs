using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>
/// REOS-184 (D58): the AI audit trail on a migrated database with the test clock, the real action log, the real controls and the real governed client over the fake provider.
/// Users rte, rm, gov1, gov2, dev are members of the default workspace. Covers recording (hashes, not text), refusals, the one-time human decision (accepted, edited, rejected), the guards, the audit
/// chain still verifying after an edit, tamper detection, and the persisted controls. The database rules themselves are in <see cref="AiAuditTriggerTests"/> and tests/reference/test_schema.py.
/// </summary>
public sealed class AiAuditTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Ws = "default";
    private static readonly Actor Rte = new("rte"), Dev = new("dev"), Gov1 = new("gov1");

    private sealed record Kit(OpsKit K, FakeAiClient Provider, AiActionService Log, AiControlService Controls, DbAiWorkspaceSettings Settings, StubGate Gate, TestMonitor<AiOptions> Options, GovernedAiClient Client);

    private Kit Make(bool moduleOn = true)
    {
        var k = new OpsKit(fx);
        var provider = new FakeAiClient();
        var log = new AiActionService(k.Db, k.Time);
        var controls = new AiControlService(k.Db, k.Time);
        var settings = new DbAiWorkspaceSettings(k.Db);
        var gate = moduleOn ? new StubGate(Ws) : new StubGate();
        var o = new AiOptions { Provider = "fake", RetryBaseDelayMilliseconds = 0 };
        o.Models.Small = "small-model"; o.Models.Drafting = "drafting-model";
        var options = new TestMonitor<AiOptions>(o);
        return new Kit(k, provider, log, controls, settings, gate, options, new GovernedAiClient(provider, new MemoryKeys(), options, settings, gate, new NoopAiUsageSink(), log, k.Time, NullLogger<GovernedAiClient>.Instance));
    }

    private static AiRequest Draft(string requester = "dev", string text = "Release R26.12 has two open blockers.") =>
        new(Ws, AiFeatures.ReleaseNotes, "You draft release notes.", "Write the notes.", [new AiContextItem("Gate evidence", text)], 512, false,
            new AiCallMeta(requester, "Train", "t1", "t1", "release-notes/1"));

    private static async Task<AiActionView> Ok(Task<ServiceResult<AiActionView>> t)
    {
        var r = await t;
        Assert.True(r.IsOk, r.Kind + ": " + string.Join(" | ", r.Failures.Select(f => $"{f.Guard}: {f.Message}")));
        return r.Value!;
    }

    private static async Task<string> Guard(Task<ServiceResult<AiActionView>> t)
    {
        var r = await t;
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        return r.Failures[0].Guard;
    }

    private static string Id(AiResult r) => Assert.IsType<string>(r.ActionId);

    // ---- recording ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_call_is_recorded_with_hashes_and_not_the_context_text_and_one_audit_row()
    {
        var s = Make();
        const string secretish = "customer-facing text that must not be stored in the trail";
        var r = await s.Client.CompleteAsync(Draft(text: secretish));
        Assert.True(r.IsCompleted);
        var a = (await s.Log.GetAsync(Id(r)))!;
        Assert.Equal(("release-notes-draft", "fake", "drafting-model", "drafting-model", "release-notes/1"), (a.Feature, a.Provider, a.Model, a.ServedModel, a.PromptTemplateVersion));
        Assert.Equal(("Completed", "Pending", r.Text), (a.Outcome, a.Decision, a.OutputText));
        Assert.Equal(AiHash.Sha256(r.Text!), a.OutputHash);
        Assert.Equal(("Train", "t1", "t1"), (a.SubjectType, a.SubjectId, a.ReleaseTrainId));
        Assert.Equal("dev", a.RequestedBy!.Id);
        Assert.Equal(64, a.InputHash!.Length); Assert.Equal(64, a.PromptHash!.Length);
        var input = Assert.Single(a.Inputs);
        Assert.Equal(("Gate evidence", "ReleaseData", secretish.Length, AiHash.Sha256(secretish)), (input.Label, input.Class, input.Chars, input.Sha256));
        // nothing in the trail carries the text: not the row, not the audit row
        Assert.Equal(0, s.K.Count("SELECT count(*) FROM AiActions WHERE InputRefsJson LIKE ?", $"%{secretish}%"));
        Assert.Equal(0, s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='AiAction' AND (AfterJson LIKE ? OR BeforeJson LIKE ?)", $"%{secretish}%", $"%{secretish}%"));
        var audit = s.K.Query("SELECT Action, ActorUserId, ReleaseTrainId, WorkspaceId FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=?", a.Id);
        Assert.Equal(["Generated", "dev", "t1", "default"], audit.Single());
        Assert.True(a.Integrity.Ok);
        // the input hash is the hash of what the provider was actually sent (the redacted prompt), so the trail can be matched to the wire
        var sent = Assert.Single(s.Provider.Sent);
        Assert.Equal(AiHash.Sha256(sent.System + "\n---\n" + sent.User), a.InputHash);
    }

    [Fact]
    public async Task A_refused_call_is_recorded_too_with_nothing_to_decide_and_no_provider_call()
    {
        var s = Make(moduleOn: false);
        var r = await s.Client.CompleteAsync(Draft());
        Assert.Equal(AiOutcome.ModuleOff, r.Outcome);
        Assert.Equal(0, s.Provider.CallCount);
        var a = (await s.Log.GetAsync(Id(r)))!;
        Assert.Equal(("ModuleOff", "NotApplicable", null), (a.Outcome, a.Decision, a.OutputText));
        Assert.Contains("module is off", a.Detail);
        Assert.Equal("Refused", s.K.Query("SELECT Action FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=?", a.Id).Single()[0]);
        Assert.Equal("AiActionNotDecidable", await Guard(s.Log.DecideAsync(a.Id, "Accepted", null, null, Dev, false, null)));
    }

    [Fact]
    public async Task A_system_request_has_no_requester_and_only_an_administrator_decides_it()
    {
        var s = Make();
        var r = await s.Client.CompleteAsync(Draft(requester: ""));
        var a = (await s.Log.GetAsync(Id(r)))!;
        Assert.Null(a.RequestedBy);
        Assert.Null(s.K.Query("SELECT ActorUserId FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=?", a.Id).Single()[0]);
        Assert.Equal("AiActionRole", await Guard(s.Log.DecideAsync(a.Id, "Accepted", null, null, Dev, false, null)));
        Assert.Equal("Accepted", (await Ok(s.Log.DecideAsync(a.Id, "Accepted", null, null, Rte, true, null))).Decision);
    }

    // ---- the human decision ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_accepted_draft_keeps_the_draft_and_writes_one_audit_row_with_the_decider()
    {
        var s = Make();
        var id = Id(await s.Client.CompleteAsync(Draft()));
        var v = await Ok(s.Log.DecideAsync(id, "Accepted", null, "Looks right", Dev, false, 1));
        Assert.Equal(("Accepted", 2, "dev", "Looks right"), (v.Decision, v.Version, v.DecidedBy!.Id, v.DecisionNote));
        Assert.Null(v.FinalText);
        Assert.True(v.Integrity.Ok);
        var row = s.K.Query("SELECT Action, ActorUserId, BeforeJson, AfterJson FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=? AND Action='Accepted'", id).Single();
        Assert.Equal(("Accepted", "dev"), ((string)row[0]!, (string)row[1]!));
        Assert.Equal(v.OutputText, JsonDocument.Parse((string)row[2]!).RootElement.GetProperty("output").GetString());
    }

    [Fact]
    public async Task An_edited_draft_shows_both_the_draft_and_the_human_edit_in_the_audit_row()
    {
        var s = Make();
        var r = await s.Client.CompleteAsync(Draft());
        var id = Id(r);
        var v = await Ok(s.Log.DecideAsync(id, "Edited", "Release R26.12 ships after the two blockers close.", null, Dev, false, null));
        Assert.Equal(("Edited", r.Text, "Release R26.12 ships after the two blockers close."), (v.Decision, v.OutputText, v.FinalText));
        Assert.Equal(AiHash.Sha256(v.FinalText!), v.FinalHash);
        var row = s.K.Query("SELECT BeforeJson, AfterJson FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=? AND Action='Edited'", id).Single();
        Assert.Equal(r.Text, JsonDocument.Parse((string)row[0]!).RootElement.GetProperty("output").GetString());      // the draft
        var after = JsonDocument.Parse((string)row[1]!).RootElement;
        Assert.Equal(v.FinalText, after.GetProperty("final").GetString());                                            // the human edit
        Assert.Equal(v.FinalHash, after.GetProperty("finalHash").GetString());
        Assert.True(v.Integrity.Ok);
    }

    [Fact]
    public async Task A_rejected_draft_is_final_and_a_second_decision_is_refused()
    {
        var s = Make();
        var id = Id(await s.Client.CompleteAsync(Draft()));
        var v = await Ok(s.Log.DecideAsync(id, "rejected", null, "Wrong release", Dev, false, null));   // the word is not case sensitive
        Assert.Equal("Rejected", v.Decision);
        Assert.Equal("AiActionDecided", await Guard(s.Log.DecideAsync(id, "Accepted", null, null, Dev, false, null)));
        Assert.Equal("Rejected", (await s.Log.GetAsync(id))!.Decision);
    }

    [Fact]
    public async Task The_decision_guards()
    {
        var s = Make();
        var id = Id(await s.Client.CompleteAsync(Draft()));
        var draft = (await s.Log.GetAsync(id))!.OutputText!;
        Assert.Equal("AiActionDecision", await Guard(s.Log.DecideAsync(id, "Maybe", null, null, Dev, false, null)));
        Assert.Equal("AiActionDecision", await Guard(s.Log.DecideAsync(id, "Edited", "  ", null, Dev, false, null)));
        Assert.Equal("AiActionDecision", await Guard(s.Log.DecideAsync(id, "Edited", draft, null, Dev, false, null)));        // the same text is Accepted, not an edit
        Assert.Equal("AiActionDecision", await Guard(s.Log.DecideAsync(id, "Accepted", "some text", null, Dev, false, null)));   // only an edit carries text
        Assert.Equal("AiActionDecision", await Guard(s.Log.DecideAsync(id, "Rejected", null, new string('x', 1001), Dev, false, null)));
        Assert.Equal("AiActionRole", await Guard(s.Log.DecideAsync(id, "Accepted", null, null, Gov1, false, null)));         // not the requester, not an administrator
        Assert.Equal(ResultKind.NotFound, (await s.Log.DecideAsync("nope", "Accepted", null, null, Dev, false, null)).Kind);
        var stale = await s.Log.DecideAsync(id, "Accepted", null, null, Dev, false, 7);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.Equal(1, Assert.IsType<AiActionView>(stale.Current).Version);
        Assert.Equal("Pending", (await s.Log.GetAsync(id))!.Decision);   // none of the refusals changed anything
        Assert.Equal(1, s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='AiAction' AND EntityId=?", id));
        Assert.Equal("Accepted", (await Ok(s.Log.DecideAsync(id, "Accepted", null, null, Rte, true, null))).Decision);   // an administrator may
    }

    // ---- the chain ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_audit_chain_still_verifies_after_a_generated_draft_an_edit_a_rejection_and_a_refusal()
    {
        var s = Make();
        var edited = Id(await s.Client.CompleteAsync(Draft()));
        var rejected = Id(await s.Client.CompleteAsync(Draft(text: "Second draft context")));
        await Ok(s.Log.DecideAsync(edited, "Edited", "A human wrote this instead.", null, Dev, false, null));
        await Ok(s.Log.DecideAsync(rejected, "Rejected", null, null, Dev, false, null));
        await s.Controls.SetPausedAsync(Ws, true, Rte);
        await s.Client.CompleteAsync(Draft());   // refused: the workspace is paused, and that is recorded too
        Assert.True(await s.K.Chain.SealPendingAsync() >= 6);
        var v = await s.K.Chain.VerifyAsync(Ws);
        Assert.Equal("Verified", v.Status);
        Assert.Equal(0, v.PendingRows);
        Assert.True(s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType IN ('AiAction','AiWorkspace') AND Hash IS NOT NULL") >= 6);   // the AI rows are in the chain
        // the chain covers the draft and the edit: changing an AI audit row afterwards is found by the same verification
        s.K.Sql("DROP TRIGGER trg_Audit_NoUpdate");
        s.K.Sql("UPDATE AuditEvents SET AfterJson = REPLACE(AfterJson, 'A human wrote this instead.', 'Someone else wrote this.') WHERE EntityType='AiAction' AND EntityId=? AND Action='Edited'", edited);
        Assert.Equal("Failed", (await s.K.Chain.VerifyAsync(Ws)).Status);
    }

    [Fact]
    public async Task A_record_whose_text_no_longer_matches_its_hash_or_audit_row_is_reported_as_not_ok()
    {
        var s = Make();
        var id = Id(await s.Client.CompleteAsync(Draft()));
        Assert.True((await s.Log.GetAsync(id))!.Integrity.Ok);
        s.K.Sql("DROP TRIGGER trg_AiAction_RecordIsImmutable");   // what a person with write access to the file could do
        s.K.Sql("UPDATE AiActions SET OutputText = 'tampered' WHERE Id = ?", id);
        var a = (await s.Log.GetAsync(id))!;
        Assert.False(a.Integrity.OutputMatchesHash);
        Assert.False(a.Integrity.Ok);
        Assert.Equal("Pending", a.Decision);
    }

    // ---- when the trail is down ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_action_cannot_be_recorded_the_output_is_withheld()
    {
        var k = new OpsKit(fx);
        var down = new RecordingActionLog { Fail = true };
        var o = new AiOptions { Provider = "fake", RetryBaseDelayMilliseconds = 0 }; o.Models.Small = "small-model";
        var log = new CapturingLogger<GovernedAiClient>();
        var client = new GovernedAiClient(new FakeAiClient(), new MemoryKeys(), new TestMonitor<AiOptions>(o), new DbAiWorkspaceSettings(k.Db), new StubGate(Ws), new NoopAiUsageSink(), down, k.Time, log);
        var r = await client.CompleteAsync(new AiRequest(Ws, AiFeatures.RiskScore, "sys", "Assess"));
        Assert.Equal(AiOutcome.AuditFailed, r.Outcome);
        Assert.Null(r.Text);
        Assert.Null(r.ActionId);
        Assert.Contains(log.Lines, l => l.Contains("could not be recorded"));
    }

    // ---- the controls ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_pause_and_the_opt_out_are_kept_in_the_database_audited_and_a_repeat_writes_nothing()
    {
        var s = Make();
        Assert.Equal(new AiWorkspaceState(false, false), await s.Settings.GetAsync(Ws));
        Assert.Equal(new AiWorkspaceState(true, false), await s.Controls.SetPausedAsync(Ws, true, Rte));
        Assert.Equal(new AiWorkspaceState(true, true), await s.Controls.SetOptedOutAsync(Ws, true, Rte));
        var audits = s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='AiWorkspace'");
        await s.Controls.SetPausedAsync(Ws, true, Rte);   // already paused
        Assert.Equal(audits, s.K.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='AiWorkspace'"));
        // a new settings reader (as after a restart) sees both, and the governed client refuses, and records the refusal
        Assert.Equal(new AiWorkspaceState(true, true), await new DbAiWorkspaceSettings(s.K.Db).GetAsync(Ws));
        Assert.Equal(new AiWorkspaceState(false, false), await new DbAiWorkspaceSettings(s.K.Db).GetAsync("elsewhere"));
        var refused = await s.Client.CompleteAsync(Draft());
        Assert.Equal(AiOutcome.WorkspacePaused, refused.Outcome);
        Assert.Equal("WorkspacePaused", (await s.Log.GetAsync(Id(refused)))!.Outcome);
        await s.Controls.SetPausedAsync(Ws, false, Rte);
        Assert.Equal(AiOutcome.OptedOut, (await s.Client.CompleteAsync(Draft())).Outcome);
        await s.Controls.SetOptedOutAsync(Ws, false, Rte);
        Assert.True((await s.Client.CompleteAsync(Draft())).IsCompleted);
        Assert.Equal(["Paused", "OptedOut", "Resumed", "OptedIn"], s.K.Query("SELECT Action FROM AuditEvents WHERE EntityType='AiWorkspace' ORDER BY Id").Select(r => (string)r[0]!).ToArray());
        var row = s.K.Query("SELECT Version, LastChangedByUserId FROM AiWorkspaceSettings WHERE WorkspaceId=?", Ws).Single();
        Assert.Equal((4L, "rte"), (Convert.ToInt64(row[0]), (string)row[1]!));
        var first = s.K.Query("SELECT BeforeJson, AfterJson FROM AuditEvents WHERE EntityType='AiWorkspace' AND Action='Paused'").Single();
        Assert.Equal("{\"paused\":false,\"optedOut\":false}", (string)first[0]!);
        Assert.Equal("{\"paused\":true,\"optedOut\":false}", (string)first[1]!);
    }

    [Fact]
    public async Task A_key_change_is_audited_as_a_host_wide_row_that_never_holds_the_key()
    {
        var s = Make();
        await s.Controls.RecordKeyChangeAsync("Set", Rte);
        await s.Controls.RecordKeyChangeAsync("Removed", Rte);
        var rows = s.K.Query("SELECT Action, ActorUserId, WorkspaceId, AfterJson FROM AuditEvents WHERE EntityType='AiKey' ORDER BY Id");
        Assert.Equal(["Set", "Removed"], rows.Select(r => (string)r[0]!).ToArray());
        Assert.All(rows, r => { Assert.Equal("rte", r[1]); Assert.Null(r[2]); Assert.DoesNotContain("sk-", (string)r[3]!); });
    }

    // ---- the inventory -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_inventory_reports_features_models_data_rules_decisions_and_the_kill_switch_state()
    {
        var s = Make();
        var a1 = Id(await s.Client.CompleteAsync(Draft()));
        var a2 = Id(await s.Client.CompleteAsync(Draft(text: "another context")));
        await s.Client.CompleteAsync(new AiRequest(Ws, AiFeatures.RiskScore, "sys", "Assess", null, 256, false, new AiCallMeta("dev")));
        await Ok(s.Log.DecideAsync(a1, "Edited", "edited text", null, Dev, false, null));
        await Ok(s.Log.DecideAsync(a2, "Accepted", null, null, Dev, false, null));
        await s.Controls.SetPausedAsync(Ws, true, Rte);
        await s.Client.CompleteAsync(Draft());   // refused: paused
        var status = new AiStatusService(s.Provider, new MemoryKeys(), s.Options, s.Settings, s.Gate, new AllowAllModuleEntitlements(), s.Client);
        var inv = await new AiInventoryService(s.K.Db, status, s.Client, s.Options).GetAsync(Ws);
        Assert.True(inv.Controls.WorkspacePaused);
        Assert.False(inv.Controls.CanCall);
        Assert.Equal(7, inv.Features.Count);
        var notes = inv.Features.Single(f => f.Key == AiFeatures.ReleaseNotes);
        Assert.Equal(("drafting-model", false, 3L, 2L, 1L, 1L), (notes.Model, notes.Enabled, notes.Calls, notes.Completed, notes.Accepted, notes.Edited));
        Assert.Contains("paused", notes.BlockedBecause);
        Assert.Equal(["release-notes/1"], notes.PromptTemplateVersions);
        Assert.Equal(["Gate evidence"], notes.InputLabels);
        Assert.Equal(("small-model", 1L), (inv.Models.Single(m => m.Model == "small-model").Model, inv.Models.Single(m => m.Model == "small-model").Calls));
        Assert.Equal(2, inv.Models.Single(m => m.Model == "drafting-model").Calls);
        Assert.Equal(new AiInventoryDecisions(4, 1, 1, 1, 0, 1), inv.Decisions);
        Assert.Contains(inv.Data.NeverSent, d => d.StartsWith("CustomerData")); Assert.Contains(inv.Data.NeverSent, d => d.StartsWith("TicketSummary"));
        Assert.True(inv.Data.HashesOnly);
    }

    [Fact]
    public async Task The_list_filters_and_never_carries_a_text()
    {
        var s = Make();
        var a1 = Id(await s.Client.CompleteAsync(Draft()));
        await s.Client.CompleteAsync(new AiRequest(Ws, AiFeatures.RiskScore, "sys", "Assess", null, 256, false, new AiCallMeta("dev", "Gate", "g1")));
        await Ok(s.Log.DecideAsync(a1, "Rejected", null, null, Dev, false, null));
        Assert.Equal(2, (await s.Log.ListAsync(new AiActionFilter())).Count);
        Assert.Equal(a1, Assert.Single(await s.Log.ListAsync(new AiActionFilter(Decision: "Rejected"))).Id);
        Assert.Equal("risk-score", Assert.Single(await s.Log.ListAsync(new AiActionFilter(Feature: "risk-score"))).Feature);
        Assert.Equal("g1", Assert.Single(await s.Log.ListAsync(new AiActionFilter(SubjectType: "Gate", SubjectId: "g1"))).SubjectId);
        Assert.Empty(await s.Log.ListAsync(new AiActionFilter(Outcome: "NoKey")));
        Assert.Equal("Dev", (await s.Log.ListAsync(new AiActionFilter(RequestedByUserId: "dev")))[0].RequestedBy!.Name);
    }
}
