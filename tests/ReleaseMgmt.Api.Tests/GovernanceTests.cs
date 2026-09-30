using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-33: change record, affected CIs, immutable Go/No-Go decisions with expiring conditions (D29).</summary>
public class GovernanceTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    /// <summary>A Low-risk Gated train with no gates and a baseline: every Executing guard except the Go/No-Go ones is satisfied.</summary>
    private static async Task<(ApiFactory F, HttpClient Rm, HttpClient Rte, HttpClient Viewer, string RteId)> Setup()
    {
        var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rteId = UserId(f, "rte@x.com");
        Sql(f, @"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1:capture-baseline")).StatusCode);
        return (f, rm, rte, viewer, rteId);
    }

    private static string In(int hours) => DateTime.UtcNow.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    [Fact]
    public async Task Only_a_release_manager_records_a_decision()
    {
        var (f, rm, rte, viewer, _) = await Setup(); using var _f = f;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(rte, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" }, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" }, "1")).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM GoNoGoDecisions"));
        var v = (await Json(await rm.GetAsync("/api/v1/trains/t1"))).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go", notes = "All clear" }, v.ToString())).StatusCode);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM GoNoGoDecisions WHERE Decision='Go' AND Notes='All clear'"));
    }

    [Fact]
    public async Task Decision_rules_conditions_belong_to_GoWithConditions_and_must_expire_in_the_future()
    {
        var (f, rm, _, _, rteId) = await Setup(); using var _f = f;
        string V() => Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'");
        async Task<HttpResponseMessage> Rec(object b) { await Task.Delay(1100); return await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", b, V()); }
        Assert.Equal("InvalidDecision", await Guard(await Rec(new { decision = "Maybe" })));
        Assert.Equal("ConditionsRequired", await Guard(await Rec(new { decision = "GoWithConditions" })));
        Assert.Equal("ConditionsNotAllowed", await Guard(await Rec(new { decision = "Go", conditions = new[] { new { text = "x", ownerUserId = rteId, expiresAt = In(2) } } })));
        Assert.Equal("InvalidCondition", await Guard(await Rec(new { decision = "GoWithConditions", conditions = new[] { new { text = "Patch", ownerUserId = rteId, expiresAt = In(-1) } } })));
        Assert.Equal("InvalidCondition", await Guard(await Rec(new { decision = "GoWithConditions", conditions = new[] { new { text = "Patch", ownerUserId = "nobody", expiresAt = In(2) } } })));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM GoNoGoDecisions"));   // nothing half-written

        var ok = await Rec(new { decision = "GoWithConditions", conditions = new[] { new { text = "Patch the gateway", ownerUserId = rteId, expiresAt = In(4) } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var snap = Scalar(f, "SELECT GateSnapshotJson FROM GoNoGoDecisions");
        Assert.Contains("\"trainStatus\":\"Gated\"", snap);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM GoNoGoConditions"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='GoNoGo' AND Action='Record'"));
    }

    [Fact]
    public async Task A_decision_not_later_than_the_latest_is_refused_so_the_latest_is_never_ambiguous()
    {
        var (f, rm, _, _, rteId) = await Setup(); using var _f = f;
        Sql(f, $"INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('d0','t1','Go','{rteId}','{In(1)}','{{}}');");   // same second or later
        Assert.Equal("DecisionTooSoon", await Guard(await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "NoGo" }, Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"))));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM GoNoGoDecisions"));
    }

    [Fact]
    public async Task Decisions_are_immutable_in_the_database()
    {
        var (f, rm, _, _, _) = await Setup(); using var _f = f;
        await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" }, Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"));
        Assert.ThrowsAny<Exception>(() => Sql(f, "UPDATE GoNoGoDecisions SET Decision='NoGo'"));
        Assert.ThrowsAny<Exception>(() => Sql(f, "DELETE FROM GoNoGoDecisions"));
    }

    [Fact]
    public async Task Executing_needs_a_Go_and_no_expired_open_condition()
    {
        var (f, rm, rte, _, rteId) = await Setup(); using var _f = f;
        string V() => Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'");
        Task<HttpResponseMessage> Advance() => Send(rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Executing" }, V());

        Assert.Equal("ExecutingRequiresGo", await Guard(await Advance()));                                                  // no decision yet
        await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "NoGo", notes = "Slip a week", newTargetReleaseDate = "2026-12-07" }, V());
        Assert.Equal("ExecutingRequiresGo", await Guard(await Advance()));                                                  // latest is NoGo
        Assert.Equal("2026-12-07", Scalar(f, "SELECT NewTargetReleaseDate FROM GoNoGoDecisions WHERE Decision='NoGo'"));

        await Task.Delay(1100);                                                                                            // DecidedAt has whole-second resolution
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "GoWithConditions", conditions = new[] { new { text = "Patch the gateway", ownerUserId = rteId, expiresAt = In(4) } } }, V())).StatusCode);
        var cid = Scalar(f, "SELECT Id FROM GoNoGoConditions");
        Sql(f, $"UPDATE GoNoGoConditions SET ExpiresAt='{In(-1)}' WHERE Id='{cid}'");                                       // the condition lapses
        Assert.Equal("ConditionExpired", await Guard(await Advance()));

        var closed = await Send(rte, HttpMethod.Post, $"/api/v1/conditions/{cid}:close", null, "1");                       // its owner closes it
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(rteId, Scalar(f, $"SELECT ClosedByUserId FROM GoNoGoConditions WHERE Id='{cid}'"));
        Assert.Equal(HttpStatusCode.OK, (await Advance()).StatusCode);
        Assert.Equal("Executing", Scalar(f, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
    }

    [Fact]
    public async Task Closing_a_condition_is_for_its_owner_or_a_release_manager_and_only_once()
    {
        var (f, rm, rte, viewer, rteId) = await Setup(); using var _f = f;
        await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "GoWithConditions", conditions = new[] { new { text = "Patch", ownerUserId = rteId, expiresAt = In(4) } } }, Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"));
        var cid = Scalar(f, "SELECT Id FROM GoNoGoConditions");
        Assert.Equal("ConditionCloseRole", await Guard(await Send(viewer, HttpMethod.Post, $"/api/v1/conditions/{cid}:close", null, "1")));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Post, $"/api/v1/conditions/{cid}:close", null, "7")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/conditions/{cid}:close", null, "1")).StatusCode);
        Assert.Equal("ConditionClosed", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/conditions/{cid}:close", null, "2")));
    }

    [Fact]
    public async Task A_condition_can_be_added_to_the_latest_conditional_decision_only()
    {
        var (f, rm, _, _, rteId) = await Setup(); using var _f = f;
        string V() => Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'");
        await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "GoWithConditions", conditions = new[] { new { text = "One", ownerUserId = rteId, expiresAt = In(4) } } }, V());
        var d1 = Scalar(f, "SELECT Id FROM GoNoGoDecisions");
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/gonogo/{d1}/conditions", new { text = "Two", ownerUserId = rteId, expiresAt = In(5) })).StatusCode);
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM GoNoGoConditions"));
        await Task.Delay(1100);                                                                                            // DecidedAt has whole-second resolution
        await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" }, V());
        Assert.Equal("NotLatestDecision", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/gonogo/{d1}/conditions", new { text = "Three", ownerUserId = rteId, expiresAt = In(5) })));
        var list = await Json(await rm.GetAsync("/api/v1/trains/t1/gonogo"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Go", list[0].GetProperty("decision").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Change_record_is_created_on_first_save_versioned_and_audited()
    {
        var (f, _, rte, viewer, _) = await Setup(); using var _f = f;
        var empty = await Json(await viewer.GetAsync("/api/v1/trains/t1/change-record"));
        Assert.Equal(0, empty.GetProperty("version").GetInt32());
        var put = await Send(rte, HttpMethod.Put, "/api/v1/trains/t1/change-record", new { justification = "  Quarter close fix  ", backoutPlan = "Roll back to 4.4", cabDate = "2026-11-20" }, "0");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Quarter close fix", Scalar(f, "SELECT Justification FROM ChangeRecords"));
        Assert.Equal("2026-11-20", Scalar(f, "SELECT CabDate FROM ChangeRecords"));
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Put, "/api/v1/trains/t1/change-record", new { justification = "Updated", testPlan = "  " }, "1")).StatusCode);
        Assert.Equal("2", Scalar(f, "SELECT Version FROM ChangeRecords"));
        Assert.Equal("", Scalar(f, "SELECT TestPlan FROM ChangeRecords"));                                                 // blank stored as null
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Put, "/api/v1/trains/t1/change-record", new { justification = "Stale" }, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Put, "/api/v1/trains/t1/change-record", new { justification = "no" }, "2")).StatusCode);
        Assert.Equal("Create,Update", Scalar(f, "SELECT group_concat(Action) FROM (SELECT Action FROM AuditEvents WHERE EntityType='ChangeRecord' ORDER BY Id)"));
    }

    [Fact]
    public async Task Affected_CIs_are_unique_per_train_and_removable()
    {
        var (f, _, rte, viewer, _) = await Setup(); using var _f = f;
        var a = await Send(rte, HttpMethod.Post, "/api/v1/trains/t1/cis", new { ciName = " Payments DB ", ciExternalId = "sys-1" });
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        var id = (await Json(a)).GetProperty("id").GetString()!;
        Assert.Equal("DuplicateCi", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/trains/t1/cis", new { ciName = "Payments DB" })));
        Assert.Equal("InvalidCi", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/trains/t1/cis", new { ciName = "  " })));
        Assert.Equal(1, (await Json(await viewer.GetAsync("/api/v1/trains/t1/cis"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Delete, $"/api/v1/trains/t1/cis/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Delete, $"/api/v1/trains/t1/cis/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rte, HttpMethod.Delete, $"/api/v1/trains/t1/cis/{id}")).StatusCode);
        Assert.Equal("Add,Remove", Scalar(f, "SELECT group_concat(Action) FROM (SELECT Action FROM AuditEvents WHERE EntityType='AffectedCI' ORDER BY Id)"));
    }
}
