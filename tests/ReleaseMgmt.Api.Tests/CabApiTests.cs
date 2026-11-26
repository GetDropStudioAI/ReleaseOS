using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-175 and REOS-177 (D56, D63): the CAB module over HTTP. Off by default: every route answers 404 and the rest of the app is unchanged. On: change requests, meetings and decisions with
/// If-Match, 409 with the current row, 422 {guard, message}, and the separation-of-duties rules refused through the API and, with a direct SQL write, by the database.
/// </summary>
public class CabApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private const string W = "2026-12-01T02:00:00Z";
    private const string W2 = "2026-12-01T04:00:00Z";

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(string Guard, string Message)> Refused(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)r.StatusCode}: {text}");
        var j = JsonDocument.Parse(text).RootElement;
        return (j.GetProperty("guard").GetString()!, j.GetProperty("message").GetString()!);
    }

    private sealed record World(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer, string RteId, string RmId, string GovId, string ViewerId) : IDisposable
    {
        public void Dispose() => F.Dispose();
    }

    private static async Task<World> Enabled()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        (await Send(rte, HttpMethod.Post, "/api/v1/modules/cab:enable", new { }, 0)).EnsureSuccessStatusCode();
        return new World(f, rte, rm, gov, viewer, UserId(f, "rte@x.com"), UserId(f, "rm@x.com"), UserId(f, "gov@x.com"), UserId(f, "v@x.com"));
    }

    private static async Task<JsonElement> Draft(HttpClient c, string? requester = null, string type = "Normal", string title = "Patch the gateway") =>
        await Ok(await Send(c, HttpMethod.Post, "/api/v1/cab/changes", new { title, changeType = type, rollbackNote = "Redeploy the previous build", plannedStartAt = W, plannedEndAt = W2, requestedByUserId = requester }));

    private static async Task<JsonElement> Submit(HttpClient c, JsonElement ch) =>
        await Ok(await Send(c, HttpMethod.Post, $"/api/v1/cab/changes/{ch.GetProperty("id").GetString()}:submit", new { }, ch.GetProperty("version").GetInt32()));

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static int Ver(JsonElement e) => e.GetProperty("version").GetInt32();
    private static Task<HttpResponseMessage> Decide(HttpClient c, JsonElement ch, string decision, string? rationale = "Reviewed", string? meetingId = null, bool retro = false, int? version = null) =>
        Send(c, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}/decisions", new { decision, rationale, meetingId, retroactive = retro }, version ?? Ver(ch));

    // ---- the module guard ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_module_off_every_CAB_route_answers_404_and_the_rest_of_the_app_is_unchanged()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        foreach (var (m, url) in new[]
        {
            (HttpMethod.Get, "/api/v1/cab/changes"), (HttpMethod.Get, "/api/v1/cab/changes/x"), (HttpMethod.Post, "/api/v1/cab/changes"), (HttpMethod.Post, "/api/v1/cab/changes/x:submit"),
            (HttpMethod.Post, "/api/v1/cab/changes/x/decisions"), (HttpMethod.Get, "/api/v1/cab/meetings"), (HttpMethod.Post, "/api/v1/cab/meetings"), (HttpMethod.Post, "/api/v1/cab/meetings/x/agenda"),
        })
        {
            var r = await Send(rte, m, url, m == HttpMethod.Get ? null : new { });
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{m} {url}: {(int)r.StatusCode}");
            Assert.Equal("", await r.Content.ReadAsStringAsync());   // the same empty 404 as a route that does not exist
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, "/api/v1/cab/changes", new { })).StatusCode);   // authorisation still comes first
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(f.CreateClient(), HttpMethod.Get, "/api/v1/cab/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync("/api/v1/trains")).StatusCode);   // the core is untouched
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM ChangeRequests") + Count(f, "SELECT COUNT(*) FROM CabMeetings"));
    }

    [Fact]
    public async Task Switching_the_module_off_hides_the_routes_again_and_keeps_the_data()
    {
        using var w = await Enabled();
        var ch = await Draft(w.Rte);
        Assert.Equal(HttpStatusCode.OK, (await w.Viewer.GetAsync($"/api/v1/cab/changes/{Id(ch)}")).StatusCode);   // any signed-in role reads
        (await Send(w.Rte, HttpMethod.Post, "/api/v1/modules/cab:disable", new { }, 1)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await w.Viewer.GetAsync($"/api/v1/cab/changes/{Id(ch)}")).StatusCode);
        Assert.Equal(1, Count(w.F, "SELECT COUNT(*) FROM ChangeRequests"));   // the module's data is kept
        (await Send(w.Rte, HttpMethod.Post, "/api/v1/modules/cab:enable", new { }, 2)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await w.Viewer.GetAsync($"/api/v1/cab/changes/{Id(ch)}")).StatusCode);
    }

    // ---- change requests ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_is_created_listed_edited_and_moved_with_If_Match_and_every_write_is_audited()
    {
        using var w = await Enabled();
        var ch = await Draft(w.Rte);
        Assert.Equal(("CHG-0001", "Draft", 1), (ch.GetProperty("key").GetString(), ch.GetProperty("status").GetString(), Ver(ch)));
        Assert.Equal(w.RteId, ch.GetProperty("requestedBy").GetProperty("id").GetString());
        var list = await Ok(await w.Viewer.GetAsync("/api/v1/cab/changes?status=Draft"));
        Assert.Single(list.EnumerateArray());
        Assert.Empty((await Ok(await w.Viewer.GetAsync("/api/v1/cab/changes?status=Closed"))).EnumerateArray());
        var upd = await Ok(await Send(w.Rte, new HttpMethod("PATCH"), $"/api/v1/cab/changes/{Id(ch)}", new { riskLevel = "High", riskFactors = "Card flows" }, 1));
        Assert.Equal(("High", 2), (upd.GetProperty("riskLevel").GetString(), Ver(upd)));
        var stale = await Send(w.Rte, new HttpMethod("PATCH"), $"/api/v1/cab/changes/{Id(ch)}", new { title = "x" }, 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(1, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ChangeRequest' AND Action='Update'"));
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rte.GetAsync("/api/v1/cab/changes/nope")).StatusCode);
        Assert.Equal("ChangeInvalid", (await Refused(await Send(w.Rte, HttpMethod.Post, "/api/v1/cab/changes", new { title = " ", changeType = "Normal" }))).Guard);
        Assert.Equal("ChangeNeedsRollbackNote", (await Refused(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(await Ok(await Send(w.Rte, HttpMethod.Post, "/api/v1/cab/changes", new { title = "No rollback", changeType = "Emergency" })))}:submit", new { }, 1))).Guard);
    }

    [Fact]
    public async Task Writes_need_an_If_Match_where_the_host_requires_one()
    {
        using var f = new ApiFactory(requireIfMatch: true);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        (await Send(rte, HttpMethod.Post, "/api/v1/modules/cab:enable", new { }, 0)).EnsureSuccessStatusCode();
        var ch = await Draft(rte);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:submit", new { })).StatusCode);
    }

    // ---- REOS-177 through the API ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Self_approval_is_refused_through_the_API_and_by_the_database_and_the_refused_attempt_is_audited()
    {
        using var w = await Enabled();
        var ch = await Submit(w.Rte, await Draft(w.Rte));   // rte is the requester
        var (guard, message) = await Refused(await Decide(w.Rte, ch, "Approved"));
        Assert.Equal(("ChangeSelfApproval", CabRuleText.SelfApproval), (guard, message));
        Assert.Equal("ChangeSelfApproval", (await Refused(await Decide(w.Rte, ch, "Rejected", "No thanks"))).Guard);
        Assert.Equal(("Submitted", 0), (Scalar(w.F, $"SELECT Status FROM ChangeRequests WHERE Id='{Id(ch)}'"), (int)Count(w.F, "SELECT COUNT(*) FROM CabDecisions")));
        // the refused attempts are on record: who, what, which rule
        Assert.Equal(2, Count(w.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ChangeRequest' AND EntityId='{Id(ch)}' AND Action='DecisionRefused' AND ActorUserId='{w.RteId}'"));
        Assert.Contains("ChangeSelfApproval", Scalar(w.F, "SELECT AfterJson FROM AuditEvents WHERE Action='DecisionRefused' LIMIT 1"));
        // and the database says no to the same write when no service is in the way
        var e1 = Assert.Throws<SqliteException>(() => Sql(w.F, $"INSERT INTO CabDecisions(Id,ChangeRequestId,Decision,DecidedByUserId,DecidedAt) VALUES('x','{Id(ch)}','Approved','{w.RteId}','2026-11-26T00:00:00Z')"));
        Assert.Contains(CabRuleText.SelfApproval, e1.Message);
        var e2 = Assert.Throws<SqliteException>(() => Sql(w.F, $"UPDATE ChangeRequests SET Status='Approved', DecidedByUserId='{w.RteId}', DecidedAt='2026-11-26T00:00:00Z', LastChangedByUserId='{w.RteId}' WHERE Id='{Id(ch)}'"));
        Assert.Contains(CabRuleText.SelfApproval, e2.Message);
        Assert.Equal("Submitted", Scalar(w.F, $"SELECT Status FROM ChangeRequests WHERE Id='{Id(ch)}'"));
        // a second person can, and a Viewer cannot even try
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(w.Viewer, ch, "Approved")).StatusCode);
        var approved = await Ok(await Decide(w.Gov, ch, "Approved"));
        Assert.Equal(("Approved", w.GovId), (approved.GetProperty("status").GetString(), approved.GetProperty("decidedBy").GetProperty("id").GetString()));
        Assert.Equal(3, Ver(approved));
    }

    [Fact]
    public async Task The_chair_who_raised_a_change_cannot_be_its_only_approver_over_HTTP()
    {
        using var w = await Enabled();
        var meeting = await Ok(await Send(w.Rm, HttpMethod.Post, "/api/v1/cab/meetings", new { title = "Weekly CAB", startsAt = W, endsAt = W2, chairUserId = w.RmId }));
        var ch = await Submit(w.Rm, await Draft(w.Rm, requester: w.RteId));   // the chair keyed it in for rte
        var added = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(meeting)}/agenda", new { changeRequestId = Id(ch) }, 1));
        Assert.Equal("CHG-0001", added.GetProperty("agenda")[0].GetProperty("changeKey").GetString());
        var r = await Refused(await Decide(w.Rm, ch, "Approved", meetingId: Id(meeting)));
        Assert.Equal(("ChangeChairSoleApprover", CabRuleText.ChairSoleApprover), r);
        Assert.Equal(1, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE Action='DecisionRefused' AND ActorUserId='" + w.RmId + "'"));
        Assert.Equal("Approved", (await Ok(await Decide(w.Gov, ch, "Approved", meetingId: Id(meeting)))).GetProperty("status").GetString());
        var m = await Ok(await w.Viewer.GetAsync($"/api/v1/cab/meetings/{Id(meeting)}"));
        Assert.Equal("Approved", m.GetProperty("decisions")[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task An_Emergency_change_is_implemented_first_reviewed_by_a_second_person_and_only_then_closed()
    {
        using var w = await Enabled();
        var ch = await Draft(w.Rte, type: "Emergency", title: "Hotfix the outage");
        ch = await Submit(w.Rte, ch);
        ch = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:implement", new { }, Ver(ch)));
        Assert.True(ch.GetProperty("needsReview").GetBoolean());
        var close = await Refused(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:close", new { }, Ver(ch)));
        Assert.Equal(("ChangeEmergencyNeedsReview", CabRuleText.EmergencyNeedsReview), close);
        Assert.Equal(1, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE Action='CloseRefused'"));
        Assert.Equal("ChangeSelfApproval", (await Refused(await Decide(w.Rte, ch, "Approved", retro: true))).Guard);
        ch = await Ok(await Decide(w.Gov, ch, "Approved", "Justified", retro: true));
        Assert.False(ch.GetProperty("needsReview").GetBoolean());
        Assert.True(ch.GetProperty("decisions")[0].GetProperty("retroactive").GetBoolean());
        ch = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:close", new { }, Ver(ch)));
        Assert.Equal("Closed", ch.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_stale_decision_answers_409_with_the_current_row_and_a_normal_change_runs_the_whole_road()
    {
        using var w = await Enabled();
        var ch = await Submit(w.Rte, await Draft(w.Rte, requester: w.RmId));
        var stale = await Decide(w.Gov, ch, "Approved", version: 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Submitted", (await Json(stale)).GetProperty("current").GetProperty("status").GetString());
        ch = await Ok(await Decide(w.Gov, ch, "Approved"));
        ch = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:implement", new { }, Ver(ch)));
        ch = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:close", new { }, Ver(ch)));
        Assert.Equal(("Closed", 5), (ch.GetProperty("status").GetString(), Ver(ch)));
        var illegal = await Refused(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:cancel", new { }, Ver(ch)));
        Assert.Equal("IllegalChangeTransition", illegal.Guard);
        Assert.Equal(5, Count(w.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ChangeRequest' AND EntityId='{Id(ch)}'"));
    }

    // ---- meetings -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_meeting_runs_from_planning_to_completion_with_agenda_attendees_and_conflicts()
    {
        using var w = await Enabled();
        var m = await Ok(await Send(w.Rm, HttpMethod.Post, "/api/v1/cab/meetings", new { title = "Weekly CAB", startsAt = W, endsAt = W2, location = "Room 4" }));
        Assert.Equal(w.RmId, m.GetProperty("chair").GetProperty("id").GetString());   // no chair named: the person creating it
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/attendees", new { userId = w.GovId, role = "Member" }, Ver(m)));
        Assert.Equal(2, m.GetProperty("attendees").GetArrayLength());
        var att = await Ok(await Send(w.Rm, new HttpMethod("PATCH"), $"/api/v1/cab/meetings/{Id(m)}/attendees/{w.GovId}", new { attendance = "Attended" }, 1));
        Assert.Equal("Attended", att.GetProperty("attendance").GetString());
        var ch = await Submit(w.Rte, await Draft(w.Rte));
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda", new { changeRequestId = Id(ch), notes = "Rollback tested" }, Ver(m)));
        var item = m.GetProperty("agenda")[0];
        var edited = await Ok(await Send(w.Rm, new HttpMethod("PATCH"), $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(item)}", new { notes = "Tested twice" }, Ver(item)));
        Assert.Equal("Tested twice", edited.GetProperty("notes").GetString());
        var stale = await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:start", new { }, 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:start", new { }, Ver(m)));
        Assert.Equal("InProgress", m.GetProperty("status").GetString());
        Assert.Equal("IllegalMeetingTransition", (await Refused(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:start", new { }, Ver(m)))).Guard);
        await Ok(await Decide(w.Gov, ch, "Approved", meetingId: Id(m)));
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:complete", new { }, Ver(m)));
        Assert.Equal("Completed", m.GetProperty("status").GetString());
        Assert.Equal(1, m.GetProperty("decisions").GetArrayLength());
        var removed = await Send(w.Rm, HttpMethod.Delete, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(item)}", null, Ver(m));
        Assert.Equal("CabMeetingClosed", (await Refused(removed)).Guard);
    }
}
