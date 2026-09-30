using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-36: rollback-rehearsal attestation, PIR (auto-created on a bad close), PIR actions, known issues and hypercare exit.</summary>
public class CloseoutTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private sealed record Ctx(ApiFactory F, HttpClient Rm, HttpClient Rte, HttpClient Gov, HttpClient Viewer, string RteId, string ViewerId) : IDisposable
    {
        public void Dispose() => F.Dispose();
        public string V(string trainId = "t1") => Scalar(F, $"SELECT Version FROM ReleaseTrains WHERE Id='{trainId}'");
    }

    /// <summary>Train t1 with no gates, Gated with a baseline and a Go: only the risk-tier guards stand between it and Executing.</summary>
    private static async Task<Ctx> Setup(string risk = "Low")
    {
        var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Sql(f, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','{risk}','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:capture-baseline")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" })).StatusCode);
        return new(f, rm, rte, gov, viewer, UserId(f, "rte@x.com"), UserId(f, "v@x.com"));
    }

    private static async Task Execute(Ctx c) =>
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Executing" }, c.V())).StatusCode);

    private static async Task<Ctx> Completed(string closeCode)
    {
        var c = await Setup();
        await Execute(c);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:complete", new { closeCode, notes = "n" }, c.V())).StatusCode);
        return c;
    }

    private static string Due(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd");
    private static long Audits(ApiFactory f, string type, string action) => long.Parse(Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{type}' AND Action='{action}'"));

    // ---- rollback attestation ----------------------------------------------------------------------------------------
    [Fact]
    public async Task High_risk_train_cannot_execute_until_a_rollback_rehearsal_is_attested()
    {
        using var c = await Setup("High");
        Assert.Equal("RollbackNotRehearsed", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Executing" }, c.V())));
        var before = await Json(await c.Rte.GetAsync("/api/v1/trains/t1/rollback-attestation"));
        Assert.True(before.GetProperty("required").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("rehearsedAt").ValueKind);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { }, c.V())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { }, "99")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { note = "Rolled back the DB migration in staging" }, c.V())).StatusCode);

        var after = await Json(await c.Rte.GetAsync("/api/v1/trains/t1/rollback-attestation"));
        Assert.Equal(c.RteId, after.GetProperty("rehearsedByUserId").GetString());
        Assert.Equal("rte", after.GetProperty("rehearsedByName").GetString());
        Assert.Equal(1, Audits(c.F, "ReleaseTrain", "RollbackRehearsed"));
        Assert.Contains("staging", Scalar(c.F, "SELECT AfterJson FROM AuditEvents WHERE Action='RollbackRehearsed'"));
        await Execute(c);
        Assert.Equal("Executing", Scalar(c.F, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
    }

    [Fact]
    public async Task Attestation_evidence_must_be_an_ended_rehearsal_run_of_the_same_train()
    {
        using var c = await Setup("VeryHigh");
        Sql(c.F, $@"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t2','R26.12','2026-12-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId,EndedAt,Outcome) VALUES
              ('open','t1','Rehearsal','2026-10-05T10:00:00Z','{c.RteId}',NULL,NULL),
              ('other','t2','Rehearsal','2026-10-05T10:00:00Z','{c.RteId}','2026-10-05T12:00:00Z','Completed'),
              ('good','t1','Rehearsal','2026-10-06T10:00:00Z','{c.RteId}','2026-10-06T12:00:00Z','Completed');");
        foreach (var run in new[] { "open", "other", "nope" })
            Assert.Equal("InvalidRehearsalRun", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { runId = run }, c.V())));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM ReleaseTrains WHERE RollbackRehearsedAt IS NOT NULL"));

        var listed = await Json(await c.Rte.GetAsync("/api/v1/trains/t1/rollback-attestation"));
        Assert.Equal("good", Assert.Single(listed.GetProperty("rehearsalRuns").EnumerateArray()).GetProperty("id").GetString());   // only ended rehearsals of this train
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { runId = "good" }, c.V())).StatusCode);
        Assert.Contains("\"runId\":\"good\"", Scalar(c.F, "SELECT AfterJson FROM AuditEvents WHERE Action='RollbackRehearsed'"));
    }

    [Fact]
    public async Task Attestation_is_refused_on_a_closed_train()
    {
        using var c = await Completed("Successful");
        Assert.Equal("TrainClosed", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { }, c.V())));
    }

    // ---- PIR -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Completing_with_issues_creates_a_PIR_and_a_Successful_close_does_not()
    {
        using var ok = await Completed("Successful");
        Assert.Equal(JsonValueKind.Null, (await Json(await ok.Rm.GetAsync("/api/v1/trains/t1/pir"))).GetProperty("pir").ValueKind);

        using var c = await Completed("SuccessfulWithIssues");
        var pir = (await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/pir"))).GetProperty("pir");
        Assert.Equal("Required", pir.GetProperty("status").GetString());
        Assert.Equal("CloseCode=SuccessfulWithIssues", pir.GetProperty("requiredReason").GetString());
        // the trigger's own audit row carries the closing actor and the service clock (D27)
        Assert.Equal(1, Audits(c.F, "PostImplementationReview", "AutoRequired"));
        Assert.Equal(c.RteId, Scalar(c.F, "SELECT ActorUserId FROM AuditEvents WHERE Action='AutoRequired'"));
        Assert.Equal(Scalar(c.F, "SELECT LastChangedAt FROM ReleaseTrains WHERE Id='t1'"), Scalar(c.F, "SELECT OccurredAt FROM AuditEvents WHERE Action='AutoRequired'"));
    }

    [Fact]
    public async Task A_manual_PIR_needs_a_Complete_train_without_one()
    {
        using var g = await Setup();
        Assert.Equal("PirRequiresComplete", await Guard(await Send(g.Rte, HttpMethod.Post, "/api/v1/trains/t1/pir")));

        using var c = await Completed("Successful");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, "/api/v1/trains/t1/pir")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Gov, HttpMethod.Post, "/api/v1/trains/t1/pir")).StatusCode);
        Assert.Equal("Manual", Scalar(c.F, "SELECT RequiredReason FROM PostImplementationReviews"));
        Assert.Equal("PirAlreadyExists", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/pir")));
        Assert.Equal(1, Audits(c.F, "PostImplementationReview", "CreateManual"));
    }

    [Fact]
    public async Task PIR_moves_Required_Scheduled_Held_Closed_through_actions_and_is_final()
    {
        using var c = await Completed("Unsuccessful");
        string V() => Scalar(c.F, "SELECT Version FROM PostImplementationReviews");
        const string pir = "/api/v1/trains/t1/pir";

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Patch, pir, new { summary = "x" }, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c.Rte, HttpMethod.Patch, pir, new { summary = "x" }, "9")).StatusCode);
        Assert.Equal("IllegalPirTransition", await Guard(await Send(c.Rte, HttpMethod.Post, pir + ":close", null, V())));      // not held yet
        Assert.Equal("PirSummaryRequired", await Guard(await Send(c.Rte, HttpMethod.Post, pir + ":hold", null, V())));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, pir + ":schedule", null, V())).StatusCode);
        Assert.Equal("IllegalPirTransition", await Guard(await Send(c.Rte, HttpMethod.Post, pir + ":schedule", null, V())));   // already scheduled
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Patch, pir, new { summary = "Root cause: missing index" }, V())).StatusCode);

        var act = await Send(c.Rte, HttpMethod.Post, pir + "/actions", new { text = "Add the index", ownerUserId = c.RteId, dueOn = Due(7) });
        Assert.Equal(HttpStatusCode.OK, act.StatusCode);
        var actionId = (await Json(act)).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Gov, HttpMethod.Post, pir + ":hold", null, V())).StatusCode);
        Assert.NotEqual("", Scalar(c.F, "SELECT HeldAt FROM PostImplementationReviews"));
        var open = await Send(c.Rte, HttpMethod.Post, pir + ":close", null, V());
        Assert.Equal("PirActionsOpen", await Guard(open));                                                                   // required-before-close
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/pir-actions/{actionId}:complete", null, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, pir + ":close", null, V())).StatusCode);
        Assert.Equal("Closed", Scalar(c.F, "SELECT Status FROM PostImplementationReviews"));

        Assert.Equal("PirClosed", await Guard(await Send(c.Rte, HttpMethod.Patch, pir, new { summary = "late edit" }, V())));
        Assert.Equal("PirClosed", await Guard(await Send(c.Rte, HttpMethod.Post, pir + "/actions", new { text = "More", ownerUserId = c.RteId, dueOn = Due(3) })));
        Assert.Equal("PirClosed", await Guard(await Send(c.Rte, HttpMethod.Post, $"/api/v1/pir-actions/{actionId}:reopen", null, "2")));
        foreach (var a in new[] { "Schedule", "Hold", "Close" }) Assert.Equal(1, Audits(c.F, "PostImplementationReview", a));   // exactly one service row per write
        Assert.Equal(1, Audits(c.F, "PostImplementationReview", "UpdateSummary"));
    }

    // ---- PIR actions -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task PIR_actions_have_an_owner_and_due_date_and_can_be_completed_reopened_and_edited()
    {
        using var c = await Completed("SuccessfulWithIssues");
        const string add = "/api/v1/trains/t1/pir/actions";
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, add, new { text = "x", ownerUserId = c.RteId, dueOn = Due(5) })).StatusCode);
        Assert.Equal("InvalidPirAction", await Guard(await Send(c.Rte, HttpMethod.Post, add, new { text = " ", ownerUserId = c.RteId, dueOn = Due(5) })));
        Assert.Equal("InvalidPirAction", await Guard(await Send(c.Rte, HttpMethod.Post, add, new { text = "x", ownerUserId = "nobody", dueOn = Due(5) })));
        Assert.Equal("InvalidPirAction", await Guard(await Send(c.Rte, HttpMethod.Post, add, new { text = "x", ownerUserId = c.RteId, dueOn = Due(-2) })));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM PirActions"));

        var id = (await Json(await Send(c.Rte, HttpMethod.Post, add, new { text = "Write the runbook", ownerUserId = c.ViewerId, dueOn = Due(10) }))).GetProperty("id").GetString()!;
        Assert.Equal(1, Audits(c.F, "PirAction", "Add"));

        var edited = await Send(c.Rm, HttpMethod.Patch, $"/api/v1/pir-actions/{id}", new { text = "Write the rollback runbook", dueOn = Due(12) }, "1");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Patch, $"/api/v1/pir-actions/{id}", new { text = "mine" }, "2")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c.Rm, HttpMethod.Patch, $"/api/v1/pir-actions/{id}", new { text = "stale" }, "1")).StatusCode);

        // completion: its owner (a Viewer) may, another Viewer may not
        var stranger = await As(c.F, Roles.Viewer, "other@x.com");
        Assert.Equal("PirActionRole", await Guard(await Send(stranger, HttpMethod.Post, $"/api/v1/pir-actions/{id}:complete", null, "2")));
        Assert.Equal("PirActionNotDone", await Guard(await Send(c.Rte, HttpMethod.Post, $"/api/v1/pir-actions/{id}:reopen", null, "2")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Viewer, HttpMethod.Post, $"/api/v1/pir-actions/{id}:complete", null, "2")).StatusCode);
        Assert.NotEqual("", Scalar(c.F, $"SELECT DoneAt FROM PirActions WHERE Id='{id}'"));
        Assert.Equal("PirActionDone", await Guard(await Send(c.Rte, HttpMethod.Post, $"/api/v1/pir-actions/{id}:complete", null, "3")));
        Assert.Equal("PirActionDone", await Guard(await Send(c.Rm, HttpMethod.Patch, $"/api/v1/pir-actions/{id}", new { text = "edit while done" }, "3")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/pir-actions/{id}:reopen", null, "3")).StatusCode);
        Assert.Equal("", Scalar(c.F, $"SELECT COALESCE(DoneAt,'') FROM PirActions WHERE Id='{id}'"));
        Assert.Equal("4", Scalar(c.F, $"SELECT Version FROM PirActions WHERE Id='{id}'"));
        Assert.Equal(1, Audits(c.F, "PirAction", "Complete"));
        Assert.Equal(1, Audits(c.F, "PirAction", "Reopen"));

        var view = await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/pir"));
        Assert.Equal("Write the rollback runbook", Assert.Single(view.GetProperty("actions").EnumerateArray()).GetProperty("text").GetString());
    }

    // ---- known issues and hypercare ----------------------------------------------------------------------------------
    [Fact]
    public async Task Known_issue_lifecycle_raise_edit_accept_resolve_reopen_with_status_only_through_actions()
    {
        using var c = await Completed("SuccessfulWithIssues");
        const string ki = "/api/v1/trains/t1/known-issues";
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, ki, new { title = "x", severity = "Low" })).StatusCode);
        Assert.Equal("InvalidKnownIssue", await Guard(await Send(c.Rte, HttpMethod.Post, ki, new { title = "", severity = "Low" })));
        Assert.Equal("InvalidKnownIssue", await Guard(await Send(c.Rte, HttpMethod.Post, ki, new { title = "Slow export", severity = "Severe" })));

        var id = (await Json(await Send(c.Rte, HttpMethod.Post, ki, new { title = "Slow export", severity = "Medium", externalKey = "INC0042" }))).GetProperty("id").GetString()!;
        Assert.Equal("Open", Scalar(c.F, $"SELECT Status FROM KnownIssues WHERE Id='{id}'"));

        var patch = await Send(c.Rte, HttpMethod.Patch, $"{ki}/{id}", new { severity = "High", status = "Resolved" }, "1");   // status is not a patchable field
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Open", Scalar(c.F, $"SELECT Status FROM KnownIssues WHERE Id='{id}'"));
        Assert.Equal("High", Scalar(c.F, $"SELECT Severity FROM KnownIssues WHERE Id='{id}'"));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c.Rte, HttpMethod.Patch, $"{ki}/{id}", new { title = "stale" }, "1")).StatusCode);

        Assert.Equal("WorkaroundRequired", await Guard(await Send(c.Rm, HttpMethod.Post, $"{ki}/{id}:accept", null, "2")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Patch, $"{ki}/{id}", new { workaround = "Export in batches" }, "2")).StatusCode);
        Assert.Equal("KnownIssueAcceptRole", await Guard(await Send(c.Rte, HttpMethod.Post, $"{ki}/{id}:accept", null, "3")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Gov, HttpMethod.Post, $"{ki}/{id}:accept", null, "3")).StatusCode);
        Assert.Equal("IllegalKnownIssueTransition", await Guard(await Send(c.Rm, HttpMethod.Post, $"{ki}/{id}:accept", null, "4")));

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"{ki}/{id}:resolve", null, "4")).StatusCode);
        Assert.NotEqual("", Scalar(c.F, $"SELECT COALESCE(ResolvedAt,'') FROM KnownIssues WHERE Id='{id}'"));
        Assert.Equal("KnownIssueResolved", await Guard(await Send(c.Rte, HttpMethod.Patch, $"{ki}/{id}", new { title = "edit" }, "5")));
        Assert.Equal("IllegalKnownIssueTransition", await Guard(await Send(c.Rte, HttpMethod.Post, $"{ki}/{id}:resolve", null, "5")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"{ki}/{id}:reopen", null, "5")).StatusCode);
        Assert.Equal("Open", Scalar(c.F, $"SELECT Status FROM KnownIssues WHERE Id='{id}'"));
        Assert.Equal("", Scalar(c.F, $"SELECT COALESCE(ResolvedAt,'') FROM KnownIssues WHERE Id='{id}'"));

        foreach (var a in new[] { "Raise", "Accept", "Resolve", "Reopen" }) Assert.Equal(1, Audits(c.F, "KnownIssue", a));
        Assert.Equal(2, Audits(c.F, "KnownIssue", "Update"));   // severity, then workaround
        var list = await Json(await c.Viewer.GetAsync(ki));
        Assert.Equal(1, list.GetProperty("issues").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/known-issues/nope:resolve", null, "1")).StatusCode);
    }

    [Fact]
    public async Task Hypercare_exit_needs_a_Complete_train_and_no_open_Critical_or_High_issue_and_freezes_the_log()
    {
        using var g = await Setup();
        Assert.Equal("HypercareRequiresComplete", await Guard(await Send(g.Rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, g.V())));

        using var c = await Completed("Successful");
        const string ki = "/api/v1/trains/t1/known-issues";
        var id = (await Json(await Send(c.Rte, HttpMethod.Post, ki, new { title = "Login loop", severity = "Critical", workaround = "Clear cookies" }))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, c.V())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, "99")).StatusCode);
        var blocked = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, c.V());
        Assert.Equal("OpenKnownIssues", await Guard(blocked));
        Assert.Equal("Login loop", (await Json(blocked)).GetProperty("items")[0].GetString());

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rm, HttpMethod.Post, $"{ki}/{id}:accept", null, "1")).StatusCode);             // accepted with a workaround
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, c.V())).StatusCode);
        Assert.Equal(c.RteId, Scalar(c.F, "SELECT HypercareExitByUserId FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal(1, Audits(c.F, "ReleaseTrain", "ExitHypercare"));
        Assert.Equal("HypercareEnded", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare", null, c.V())));
        Assert.Equal("HypercareEnded", await Guard(await Send(c.Rte, HttpMethod.Post, ki, new { title = "Late", severity = "Low" })));
        Assert.NotEqual(JsonValueKind.Null, (await Json(await c.Viewer.GetAsync(ki))).GetProperty("hypercareExitAt").ValueKind);
    }

    [Fact]
    public async Task Missing_If_Match_is_428_when_required()
    {
        using var f = new ApiFactory(requireIfMatch: true);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        Assert.Equal((HttpStatusCode)428, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:rehearsed-rollback", new { })).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:exit-hypercare")).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Send(rte, HttpMethod.Patch, "/api/v1/trains/t1/pir", new { summary = "x" })).StatusCode);
    }
}
