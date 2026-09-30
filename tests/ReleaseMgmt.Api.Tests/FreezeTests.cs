using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-34: freeze windows, immutable overrides (renew = new row, D29), separation of duties (D32), readable 422 before the trigger.</summary>
public class FreezeTests
{
    private const string Reason = "Hotfix for the payment outage, approved by the incident commander";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string At(int hours) => DateTime.UtcNow.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private sealed record Ctx(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Go, HttpClient Go2, HttpClient Viewer, string RteId, string RmId, string GoId, string ViewerId, string StepA, string StepB, string Run);

    /// <summary>Executing train t3 with steps A (PreCheck) then B (Deploy), and an open Live run.</summary>
    private static async Task<Ctx> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var go = await As(f, Roles.GovernanceOfficer, "go@x.com");
        var go2 = await As(f, Roles.GovernanceOfficer, "go2@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rteId = UserId(f, "rte@x.com");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        async Task<string> Add(string title, string section, string start, int min) =>
            (await Json(await rte.PostAsJsonAsync("/api/v1/trains/t3/steps", new { title, section, ownerUserId = rteId, plannedStartAt = start, plannedDurationMin = min }))).GetProperty("id").GetString()!;
        var a = await Add("Stop traffic", "PreCheck", "2026-10-30T06:00:00Z", 10);
        var b = await Add("Deploy API", "Deploy", "2026-10-30T06:10:00Z", 30);
        var dep = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/steps/{b}/dependencies") { Content = JsonContent.Create(new { dependsOn = new[] { a } }) };
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(dep)).StatusCode);
        var run = (await Json(await rte.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Live" }))).GetProperty("id").GetString()!;
        var ctx = new Ctx(f, rte, rm, go, go2, viewer, rteId, UserId(f, "rm@x.com"), UserId(f, "go@x.com"), UserId(f, "v@x.com"), a, b, run);
        Assert.Equal(HttpStatusCode.OK, (await Step(ctx, a, "start")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Step(ctx, a, "done")).StatusCode);
        return ctx;
    }

    private static Task<HttpResponseMessage> Step(Ctx c, string step, string action) =>
        c.Rte.PostAsJsonAsync($"/api/v1/runs/{c.Run}/steps/{step}:{action}", new { });

    private static async Task<string> Guard(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(expected, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private static async Task<string> NewFreeze(HttpClient c, string name = "Q4 close", string kind = "Freeze", string? pattern = null, int fromH = -1, int toH = 2)
    {
        var r = await c.PostAsJsonAsync("/api/v1/freezes", new { name, kind, startsAt = At(fromH), endsAt = At(toH), productPattern = pattern });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> Grant(HttpClient c, string window, string requester, string? reason = null, string? expires = null, string train = "t3") =>
        c.PostAsJsonAsync($"/api/v1/freezes/{window}/overrides", new { trainId = train, requestedByUserId = requester, reason = reason ?? Reason, expiresAt = expires ?? At(3) });

    [Fact]
    public async Task Creating_a_window_is_for_release_managers_and_governance_officers_and_is_audited()
    {
        var c = await Setup(); using var _ = c.F;
        var body = new { name = "Q4 close", kind = "Freeze", startsAt = At(-1), endsAt = At(2), productPattern = (string?)null };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Rte.PostAsJsonAsync("/api/v1/freezes", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Viewer.PostAsJsonAsync("/api/v1/freezes", body)).StatusCode);
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM FreezeWindows"));
        var id = await NewFreeze(c.Rm);
        await NewFreeze(c.Go, "Audit season", "Chill");
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM FreezeWindows"));
        Assert.Equal("1", Scalar(c.F, $"SELECT Version FROM FreezeWindows WHERE Id='{id}'"));
        Assert.Equal(c.RmId, Scalar(c.F, $"SELECT CreatedByUserId FROM FreezeWindows WHERE Id='{id}'"));
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='FreezeWindow' AND Action='Create'"));
        Assert.Equal(c.RmId, Scalar(c.F, $"SELECT ActorUserId FROM AuditEvents WHERE EntityId='{id}' AND Action='Create'"));

        Assert.Equal("FreezeInvalid", await Guard(await c.Rm.PostAsJsonAsync("/api/v1/freezes", new { name = "Backwards", startsAt = At(2), endsAt = At(1) })));
        Assert.Equal("FreezeInvalid", await Guard(await c.Rm.PostAsJsonAsync("/api/v1/freezes", new { name = " ", startsAt = At(1), endsAt = At(2) })));
        Assert.Equal("FreezeInvalid", await Guard(await c.Rm.PostAsJsonAsync("/api/v1/freezes", new { name = "Odd", kind = "Blackout", startsAt = At(1), endsAt = At(2) })));
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM FreezeWindows"));

        var list = await Json(await c.Viewer.GetAsync("/api/v1/freezes"));   // anyone signed in can read
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task A_freeze_blocks_the_deploy_step_with_a_readable_422_until_an_unexpired_override_exists()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);

        var blocked = await Step(c, c.StepB, "start");
        Assert.Equal("FreezeLockout", await Guard(blocked));                                       // the service guard, not the trigger's DbRule
        Assert.Contains("Q4 close", (await Json(blocked)).GetProperty("message").GetString());
        Assert.Equal("Scheduled", Scalar(c.F, $"SELECT Status FROM StepExecutions WHERE StepId='{c.StepB}'"));

        // an override that already lapsed does not count (ApprovedAt in the past keeps the schema CHECK happy)
        Sql(c.F, $"INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES('old','{fz}','t3','{Reason}','{c.RteId}','{c.RmId}','{At(-3)}','{At(-2)}')");
        Assert.Equal("FreezeLockout", await Guard(await Step(c, c.StepB, "start")));

        Assert.Equal(HttpStatusCode.OK, (await Grant(c.Rm, fz, c.RteId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Step(c, c.StepB, "start")).StatusCode);
        Assert.Equal("Running", Scalar(c.F, $"SELECT Status FROM StepExecutions WHERE StepId='{c.StepB}'"));
    }

    [Fact]
    public async Task Only_a_live_deploy_step_inside_an_active_Freeze_covering_the_train_is_blocked()
    {
        var c = await Setup(); using var _ = c.F;
        await NewFreeze(c.Rm, "Advisory chill", "Chill");                                           // Chill never blocks
        await NewFreeze(c.Rm, "Later", "Freeze", null, 5, 8);                                       // not started yet
        await NewFreeze(c.Rm, "Earlier", "Freeze", null, -8, -5);                                   // already over
        await NewFreeze(c.Rm, "Other product", "Freeze", "Billing*");                               // pattern set, this step has no product: not covered (trigger semantics)
        Assert.Equal(HttpStatusCode.OK, (await Step(c, c.StepB, "start")).StatusCode);
    }

    [Fact]
    public async Task An_override_covers_only_the_train_it_names()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t4','Other','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var fz = await NewFreeze(c.Rm);
        Assert.Equal(HttpStatusCode.OK, (await Grant(c.Rm, fz, c.RteId, train: "t4")).StatusCode);
        Assert.Equal("FreezeLockout", await Guard(await Step(c, c.StepB, "start")));
    }

    [Fact]
    public async Task Separation_of_duties_the_requester_cannot_approve_and_only_RM_or_GO_approve()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);

        Assert.Equal("OverrideSelfApproval", await Guard(await Grant(c.Rm, fz, c.RmId)));          // an RM requesting and approving is one person
        Assert.Equal("OverrideSelfApproval", await Guard(await Grant(c.Go, fz, c.GoId)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(c.Rte, fz, c.RmId)).StatusCode);        // an RTE cannot approve (policy)
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(c.Viewer, fz, c.RteId)).StatusCode);
        Assert.Equal("OverrideRequesterRole", await Guard(await Grant(c.Rm, fz, c.ViewerId)));      // a Viewer cannot be the requester
        Assert.Equal("OverrideRequesterRole", await Guard(await Grant(c.Rm, fz, c.GoId)));          // nor a Governance Officer (D32: RTE or RM request)
        Assert.Equal("OverrideRequesterRole", await Guard(await Grant(c.Rm, fz, "nobody")));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM FreezeOverrides"));

        Assert.Equal(HttpStatusCode.OK, (await Grant(c.Go, fz, c.RteId)).StatusCode);               // a GO approves an RTE's request
        Assert.Equal(HttpStatusCode.OK, (await Grant(c.Go2, fz, c.RmId)).StatusCode);               // and an RM's
        Assert.Equal(HttpStatusCode.OK, (await Grant(c.Rm, fz, c.RteId)).StatusCode);
        Assert.Equal(c.GoId, Scalar(c.F, $"SELECT ApprovedByUserId FROM FreezeOverrides WHERE RequestedByUserId='{c.RteId}' ORDER BY ApprovedAt, Id LIMIT 1"));
    }

    [Fact]
    public async Task An_override_needs_a_written_reason_and_a_future_expiry()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);
        Assert.Equal("OverrideReason", await Guard(await Grant(c.Rm, fz, c.RteId, reason: "too short")));
        Assert.Equal("OverrideExpiry", await Guard(await Grant(c.Rm, fz, c.RteId, expires: At(-1))));
        Assert.Equal(HttpStatusCode.NotFound, (await Grant(c.Rm, "nope", c.RteId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Grant(c.Rm, fz, c.RteId, train: "nope")).StatusCode);
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM FreezeOverrides"));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='FreezeOverride'"));
    }

    [Fact]
    public async Task Overrides_are_immutable_and_renewal_inserts_a_new_row_with_its_own_audit()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);
        var first = await Json(await Grant(c.Go, fz, c.RteId, expires: At(1)));
        var second = await Json(await Grant(c.Go, fz, c.RteId, expires: At(6)));   // renewal
        Assert.NotEqual(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM FreezeOverrides"));
        Assert.Equal("Grant,Renew", Scalar(c.F, "SELECT group_concat(Action) FROM (SELECT Action FROM AuditEvents WHERE EntityType='FreezeOverride' ORDER BY Id)"));
        Assert.Equal(c.GoId, Scalar(c.F, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='FreezeOverride' AND Action='Renew'"));
        Assert.Equal("t3", Scalar(c.F, "SELECT ReleaseTrainId FROM AuditEvents WHERE EntityType='FreezeOverride' AND Action='Renew'"));

        // no way to edit or delete one over HTTP ...
        var id = first.GetProperty("id").GetString();
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var r = await c.Go.SendAsync(new HttpRequestMessage(m, $"/api/v1/freezes/{fz}/overrides/{id}") { Content = JsonContent.Create(new { reason = "rewritten history for the record" }) });
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{m} returned {r.StatusCode}");
        }
        // ... and the database refuses too (trigger backstop)
        Assert.ThrowsAny<Exception>(() => Sql(c.F, "UPDATE FreezeOverrides SET ExpiresAt='2099-01-01T00:00:00Z'"));
        Assert.ThrowsAny<Exception>(() => Sql(c.F, "DELETE FROM FreezeOverrides"));
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM FreezeOverrides"));
    }

    [Fact]
    public async Task The_trigger_is_still_the_backstop_when_the_service_is_bypassed()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);
        var ex = Assert.ThrowsAny<Exception>(() => Sql(c.F, $"UPDATE StepExecutions SET Status='Running', ActualStartAt='{At(0)}' WHERE StepId='{c.StepB}'"));
        Assert.Contains("Freeze window", ex.Message);
        var role = Assert.ThrowsAny<Exception>(() => Sql(c.F, $"INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES('x','{fz}','t3','{Reason}','{c.RmId}','{c.RteId}','{At(0)}','{At(2)}')"));
        Assert.Contains("approved by a Release Manager or Governance Officer", role.Message);
    }

    [Fact]
    public async Task Requesting_an_override_notifies_the_other_approvers_and_is_audited()
    {
        var c = await Setup(); using var _ = c.F;
        var fz = await NewFreeze(c.Rm);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Viewer.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Go.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = Reason })).StatusCode);
        Assert.Equal("OverrideReason", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = "short" })));
        Assert.Equal("OverrideExpiry", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = Reason, expiresAt = At(-1) })));

        var ok = await c.Rte.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = Reason, expiresAt = At(4) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(3, (await Json(ok)).GetProperty("notified").GetInt32());                       // rm, go, go2
        Assert.Equal("3", Scalar(c.F, "SELECT COUNT(*) FROM Notifications WHERE Kind='FreezeOverrideRequested'"));
        Assert.Equal("0", Scalar(c.F, $"SELECT COUNT(*) FROM Notifications WHERE Kind='FreezeOverrideRequested' AND UserId='{c.RteId}'"));
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='FreezeWindow' AND Action='RequestOverride' AND ActorUserId='{c.RteId}'"));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM FreezeOverrides"));                    // a request grants nothing
    }

    [Fact]
    public async Task The_list_says_whether_a_freeze_covers_a_train_and_whether_it_has_a_valid_override()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, "INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t3','Billing API','1.0','BIL')");
        var all = await NewFreeze(c.Rm, "All products");
        var billing = await NewFreeze(c.Rm, "Billing only", "Freeze", "Billing*");
        var ledger = await NewFreeze(c.Rm, "Ledger only", "Freeze", "Ledger*");
        await Grant(c.Rm, all, c.RteId);

        var rows = (await Json(await c.Viewer.GetAsync("/api/v1/freezes?trainId=t3"))).EnumerateArray().ToDictionary(e => e.GetProperty("window").GetProperty("id").GetString()!);
        Assert.True(rows[all].GetProperty("coversTrain").GetBoolean());
        Assert.True(rows[all].GetProperty("trainHasValidOverride").GetBoolean());
        Assert.True(rows[all].GetProperty("active").GetBoolean());
        Assert.True(rows[billing].GetProperty("coversTrain").GetBoolean());
        Assert.False(rows[billing].GetProperty("trainHasValidOverride").GetBoolean());
        Assert.False(rows[ledger].GetProperty("coversTrain").GetBoolean());
        Assert.Equal(1, rows[all].GetProperty("overrides").GetArrayLength());
    }

    [Fact]
    public async Task Waivers_list_per_gate_and_the_approver_must_be_a_different_governance_officer()
    {
        var c = await Setup(); using var _ = c.F;
        SeedTrain(c.F, c.RteId, c.GoId);
        var req = await c.Go.PostAsJsonAsync("/api/v1/gates/g2/waivers", new { reason = "Vendor audit slipped; risk accepted by the CISO" });
        Assert.Equal(HttpStatusCode.OK, req.StatusCode);
        var wid = (await Json(req)).GetProperty("id").GetString();
        var list = await Json(await c.Viewer.GetAsync("/api/v1/gates/g2/waivers"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("approvedByUserId").ValueKind);

        var self = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/waivers/{wid}:approve") { Content = JsonContent.Create(new { }) };
        Assert.Equal("WaiverSelfApproval", await Guard(await c.Go.SendAsync(self)));
        var byRm = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/waivers/{wid}:approve") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Rm.SendAsync(byRm)).StatusCode);
        var other = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/waivers/{wid}:approve") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.OK, (await c.Go2.SendAsync(other)).StatusCode);
        Assert.Equal("Request,Approve", Scalar(c.F, "SELECT group_concat(Action) FROM (SELECT Action FROM AuditEvents WHERE EntityType='GateWaiver' ORDER BY Id)"));
    }
}
