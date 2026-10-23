using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-113 over HTTP: policies, approvals with per-seat decisions and reasons, Request changes and resubmit, the queue, exceptions with a declined state, coverage, audit rows.</summary>
public class ApprovalApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Guard(HttpResponseMessage r, HttpStatusCode status = HttpStatusCode.UnprocessableEntity) { Assert.Equal(status, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));

    private sealed record Ctx(HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer, string EnvId, string PolicyId);

    /// <summary>Train t1 (High risk) owned by the release manager; a Production environment; the policy "Production" with a release-owner seat and a governance seat.</summary>
    private static async Task<Ctx> Setup(ApiFactory f)
    {
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "view@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
        Sql(f, $"UPDATE ReleaseTrains SET OwnerUserId='{UserId(f, "rm@x.com")}' WHERE Id='t1'");
        var env = await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" }));
        var envId = env.GetProperty("id").GetString()!;
        var made = await rm.PostAsJsonAsync("/api/v1/approval-policies", new
        {
            name = "Production", environmentKind = "Production", minRiskTier = "High",
            seats = new object[] { new { label = "Release owner", kind = "ReleaseOwner", dueHours = 24 }, new { label = "Governance", kind = "WorkspaceRole", value = Roles.GovernanceOfficer, dueHours = 8 } },
        });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        return new(rte, rm, gov, viewer, envId, (await Json(made)).GetProperty("id").GetString()!);
    }

    private static Task<HttpResponseMessage> Request(Ctx c, HttpClient who) => who.PostAsJsonAsync("/api/v1/approval-requests", new { trainId = "t1", environmentId = c.EnvId });
    private static Task<HttpResponseMessage> Decide(HttpClient who, string id, string seat, string decision, string? note = null, int? version = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approval-requests/{id}:decide") { Content = JsonContent.Create(new { seat, decision, note }) };
        if (version is not null) req.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return who.SendAsync(req);
    }

    [Fact]
    public async Task Policies_are_managed_by_planners_and_governance_with_names_unique_and_every_write_audited()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var list = await Json(await c.Rte.GetAsync("/api/v1/approval-policies"));
        Assert.Equal(["Production"], list.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, list[0].GetProperty("seats").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Viewer.PostAsJsonAsync("/api/v1/approval-policies", new { name = "X", seats = new[] { new { label = "A", kind = "ReleaseOwner" } } })).StatusCode);
        Assert.Equal("PolicyNameTaken", await Guard(await c.Rm.PostAsJsonAsync("/api/v1/approval-policies", new { name = "production", seats = new[] { new { label = "A", kind = "ReleaseOwner" } } })));
        Assert.Equal("PolicyInvalid", await Guard(await c.Rm.PostAsJsonAsync("/api/v1/approval-policies", new { name = "No seats" })));

        var one = await Json(await c.Rte.GetAsync($"/api/v1/approval-policies/{c.PolicyId}"));
        var version = one.GetProperty("version").GetInt32();
        var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/approval-policies/{c.PolicyId}") { Content = JsonContent.Create(new { name = "Prod" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{version + 5}\"");
        Assert.Equal(HttpStatusCode.Conflict, (await c.Rm.SendAsync(stale)).StatusCode);
        var ok = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/approval-policies/{c.PolicyId}") { Content = JsonContent.Create(new { requireGatesCertified = true }) };
        ok.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, (await c.Rm.SendAsync(ok)).StatusCode);
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApprovalPolicy' AND Action='Create'"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApprovalPolicy' AND Action='Update'"));

        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/approval-policies/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Rm.PostAsync($"/api/v1/approval-policies/{c.PolicyId}:archive", null)).StatusCode);
        Assert.Equal(0, (await Json(await c.Rte.GetAsync("/api/v1/approval-policies"))).GetArrayLength());
        Assert.Equal(1, (await Json(await c.Rte.GetAsync("/api/v1/approval-policies?includeArchived=true"))).GetArrayLength());
    }

    [Fact]
    public async Task An_approval_is_requested_decided_seat_by_seat_with_reasons_and_Request_changes_needs_a_note_and_a_resubmit()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(c, c.Viewer)).StatusCode);
        var made = await Request(c, c.Rte);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var a = await Json(made);
        var id = a.GetProperty("id").GetString()!;
        Assert.Equal(("Pending", 1), (a.GetProperty("status").GetString(), a.GetProperty("round").GetInt32()));
        Assert.Equal(2, a.GetProperty("decisions").GetArrayLength());
        Assert.Equal("ApprovalAlreadyOpen", await Guard(await Request(c, c.Rm)));

        // the reasons a person cannot decide are in the view, in words
        var asRte = await Json(await c.Rte.GetAsync($"/api/v1/approval-requests/{id}"));
        Assert.All(asRte.GetProperty("decisions").EnumerateArray(), d => { Assert.False(d.GetProperty("canDecide").GetBoolean()); Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("whyNot").GetString())); });
        Assert.Equal("NotEligibleApprover", await Guard(await Decide(c.Rte, id, "Governance", "Approved")));   // the requester holds no seat (separation of duties itself is covered by the service tests)
        Assert.Equal("NotEligibleApprover", await Guard(await Decide(c.Gov, id, "Release owner", "Approved")));
        Assert.Equal("SeatNotFound", await Guard(await Decide(c.Gov, id, "Nope", "Approved")));
        Assert.Equal("NoteRequired", await Guard(await Decide(c.Gov, id, "Governance", "ChangesRequested")));
        Assert.Equal("NotEligibleApprover", await Guard(await Decide(c.Viewer, id, "Governance", "Approved")));

        // the queue shows the waiting seat to the one who holds it
        var queue = await Json(await c.Gov.GetAsync("/api/v1/approvals"));
        Assert.True(queue.GetProperty("counts").GetProperty("seats").GetInt32() >= 1);
        Assert.Contains(queue.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "DecideApproval");

        // Request changes ends the round; nobody decides until it is resubmitted
        var changes = await Json(await Decide(c.Gov, id, "Governance", "ChangesRequested", "Attach the SOX evidence"));
        Assert.Equal("ChangesRequested", changes.GetProperty("status").GetString());
        Assert.Equal("ApprovalNotOpen", await Guard(await Decide(c.Rm, id, "Release owner", "Approved")));
        Assert.Equal("ApprovalRole", await Guard(await c.Gov.PostAsync($"/api/v1/approval-requests/{id}:resubmit", null)));
        var again = await Json(await c.Rte.PostAsync($"/api/v1/approval-requests/{id}:resubmit", null));
        Assert.Equal(("Pending", 2), (again.GetProperty("status").GetString(), again.GetProperty("round").GetInt32()));

        // a stale If-Match is a 409 with the current row
        var stale = await Decide(c.Rm, id, "Release owner", "Approved", version: 99);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        Assert.Equal("Pending", (await Json(await Decide(c.Rm, id, "Release owner", "Approved"))).GetProperty("status").GetString());
        Assert.Equal("SeatDecided", await Guard(await Decide(c.Rm, id, "Release owner", "Approved")));
        var done = await Json(await Decide(c.Gov, id, "Governance", "Approved"));
        Assert.Equal("Approved", done.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("decidedAt").ValueKind);

        foreach (var (entity, action) in new[] { ("Approval", "Request"), ("Approval", "ChangesRequested"), ("Approval", "Resubmit"), ("Approval", "Approved") })
            Assert.True(Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{entity}' AND Action='{action}'") >= 1, action);
        Assert.True(Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE Action IN ('Approve','RequestChanges') AND EntityType<>'Approval'") >= 3);   // one per seat decision
        Assert.Equal(1, (await Json(await c.Rte.GetAsync("/api/v1/trains/t1/approval-requests"))).GetArrayLength());
        Assert.Equal(1, (await Json(await c.Rte.GetAsync("/api/v1/approval-requests?status=Approved"))).GetArrayLength());
        Assert.Equal(0, (await Json(await c.Rte.GetAsync("/api/v1/approval-requests?status=Pending"))).GetArrayLength());
    }

    [Fact]
    public async Task A_request_can_be_cancelled_by_its_requester_and_no_policy_means_no_request()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var id = (await Json(await Request(c, c.Rte))).GetProperty("id").GetString()!;
        Assert.Equal("ApprovalRole", await Guard(await c.Gov.PostAsync($"/api/v1/approval-requests/{id}:cancel", null)));
        Assert.Equal("Cancelled", (await Json(await c.Rte.PostAsync($"/api/v1/approval-requests/{id}:cancel", null))).GetProperty("status").GetString());
        Assert.Equal("ApprovalNotOpen", await Guard(await c.Rte.PostAsync($"/api/v1/approval-requests/{id}:cancel", null)));
        Assert.Equal(HttpStatusCode.Created, (await Request(c, c.Rte)).StatusCode);   // a cancelled approval frees the slot
        Assert.Equal("ApprovalAlreadyOpen", await Guard(await Request(c, c.Rm)));
    }

    [Fact]
    public async Task Coverage_counts_the_releases_the_policies_apply_to_and_the_exceptions_list_shows_declined_waivers()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var cov = await Json(await c.Rte.GetAsync("/api/v1/approval-policies/coverage"));
        Assert.Equal(JsonValueKind.Object, cov.ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Rte.GetAsync("/api/v1/approval-policies/coverage?from=yesterday")).StatusCode);

        // a waiver is asked for and declined by someone other than its requester, with a note
        var asked = await c.Gov.PostAsJsonAsync("/api/v1/gates/g2/waivers", new { reason = "Vendor audit slips a week" });
        Assert.True(asked.IsSuccessStatusCode, $"{(int)asked.StatusCode} {await asked.Content.ReadAsStringAsync()}");
        var waiverId = Scalar(f, "SELECT Id FROM GateWaivers LIMIT 1");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Rm.PostAsJsonAsync($"/api/v1/waivers/{waiverId}:decline", new { note = "No" })).StatusCode);
        Assert.Equal("WaiverDeclineSelf", await Guard(await c.Gov.PostAsJsonAsync($"/api/v1/waivers/{waiverId}:decline", new { note = "Evidence is overdue" })));
        var gov2 = await As(f, Roles.GovernanceOfficer, "gov2@x.com");
        Assert.Equal("DeclineNote", await Guard(await gov2.PostAsJsonAsync($"/api/v1/waivers/{waiverId}:decline", new { note = "" })));
        Assert.Equal(HttpStatusCode.OK, (await gov2.PostAsJsonAsync($"/api/v1/waivers/{waiverId}:decline", new { note = "Evidence is overdue" })).StatusCode);
        Assert.Equal("WaiverAlreadyDecided", await Guard(await gov2.PostAsJsonAsync($"/api/v1/waivers/{waiverId}:decline", new { note = "Again" })));
        var ex = await Json(await c.Gov.GetAsync("/api/v1/exceptions"));
        Assert.Equal(1, ex.GetProperty("declined").GetInt32());
        Assert.Equal("Declined", ex.GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Equal(0, (await Json(await c.Gov.GetAsync("/api/v1/exceptions?state=Pending"))).GetProperty("items").GetArrayLength());
    }
}
