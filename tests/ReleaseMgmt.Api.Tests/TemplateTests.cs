using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-38: train templates Draft -> Approved -> Retired, edit only while Draft, approve/retire roles, If-Match, audit.</summary>
public class TemplateTests
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

    private static object Body(string name = "Payments release", int gates = 2, object? schedule = null) => new
    {
        name,
        defaultRiskTier = "High",
        gates = Enumerable.Range(1, gates).Select(n => new { gateName = $"Gate {n}", gateClass = n == 2 ? "Compliance" : "Standard", offsetDays = 6 - n, requiredBeforeStatus = n == 2 ? "Executing" : "Gated" }).ToArray(),
        steps = new[] { new { stepCode = "PRE-1", section = "PreCheck", title = "Health check", offsetMinutes = -30, plannedDurationMin = 15 } },
        schedule = schedule ?? Array.Empty<object>(),
    };

    private static async Task<(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer)> Setup(bool requireIfMatch = false)
    {
        var f = new ApiFactory(requireIfMatch: requireIfMatch);
        return (f, await As(f, Roles.RTE, "rte@x.com"), await As(f, Roles.ReleaseManager, "rm@x.com"), await As(f, Roles.GovernanceOfficer, "gov@x.com"), await As(f, Roles.Viewer, "v@x.com"));
    }

    private static async Task<(string Id, int Version)> Create(HttpClient c, object body)
    {
        var r = await Send(c, HttpMethod.Post, "/api/v1/templates", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        return (j.GetProperty("id").GetString()!, j.GetProperty("version").GetInt32());
    }

    private static long Audits(ApiFactory f, string action, string id) => long.Parse(Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='TrainTemplate' AND EntityId='{id}' AND Action='{action}'"));

    [Fact]
    public async Task Lifecycle_draft_approved_retired_with_review_date_a_year_out()
    {
        var (f, rte, rm, gov, _) = await Setup(); using var _f = f;
        var (id, v) = await Create(rte, Body());
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM TemplateGates WHERE TemplateId='{id}'"));

        var before = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(12);
        var ok = await Send(gov, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var after = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(12);
        var t = (await Json(ok)).GetProperty("template");
        Assert.Equal("Approved", t.GetProperty("status").GetString());
        Assert.Equal("gov", t.GetProperty("approvedByName").GetString());
        var due = DateOnly.Parse(t.GetProperty("reviewDueOn").GetString()!);
        Assert.True(due == before || due == after, $"review due {due} is not approval + 12 months");
        Assert.False(t.GetProperty("reviewOverdue").GetBoolean());
        Assert.Equal(v + 1, t.GetProperty("version").GetInt32());

        var retire = await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, (v + 1).ToString());
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        Assert.Equal("Retired", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
        Assert.Equal("", Scalar(f, $"SELECT IFNULL(ApprovedByUserId,'') FROM TrainTemplates WHERE Id='{id}'"));   // schema CHECK: only Approved carries an approver
        Assert.Equal(1, Audits(f, "Create", id)); Assert.Equal(1, Audits(f, "Approve", id)); Assert.Equal(1, Audits(f, "Retire", id));
        Assert.Contains((await gov.GetFromJsonAsync<JsonElement>("/api/v1/templates")).EnumerateArray(), x => x.GetProperty("id").GetString() == id && x.GetProperty("status").GetString() == "Retired");
    }

    [Fact]
    public async Task Illegal_transitions_are_refused_with_a_readable_guard()
    {
        var (f, rte, rm, _, _) = await Setup(); using var _f = f;
        var (id, v) = await Create(rte, Body());
        Assert.Equal("IllegalTemplateTransition", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, v.ToString())));   // Draft cannot retire
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())).StatusCode);
        var again = await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, (v + 1).ToString());
        Assert.Equal("IllegalTemplateTransition", await Guard(again));
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, (v + 1).ToString())).StatusCode);
        Assert.Equal("IllegalTemplateTransition", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, (v + 2).ToString())));   // Retired is final
        Assert.Equal("IllegalTemplateTransition", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, (v + 2).ToString())));
        Assert.Equal(1, Audits(f, "Approve", id)); Assert.Equal(1, Audits(f, "Retire", id));   // refusals write nothing
    }

    [Fact]
    public async Task A_template_needs_a_gate_before_approval()
    {
        var (f, rte, rm, _, _) = await Setup(); using var _f = f;
        var (id, v) = await Create(rte, Body(gates: 0));
        Assert.Equal("TemplateNeedsGate", await Guard(await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())));
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
    }

    [Fact]
    public async Task Only_a_draft_can_be_edited_and_an_edit_replaces_the_rows()
    {
        var (f, rte, rm, _, _) = await Setup(); using var _f = f;
        var (id, v) = await Create(rte, Body());
        var edit = await Send(rte, HttpMethod.Put, $"/api/v1/templates/{id}", Body("Payments release v2", gates: 3), v.ToString());
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM TemplateGates WHERE TemplateId='{id}'"));
        Assert.Equal("Payments release v2", Scalar(f, $"SELECT Name FROM TrainTemplates WHERE Id='{id}'"));
        Assert.Equal(1, Audits(f, "Update", id));

        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, (v + 1).ToString())).StatusCode);
        var late = await Send(rte, HttpMethod.Put, $"/api/v1/templates/{id}", Body("Sneaky", gates: 1), (v + 2).ToString());
        Assert.Equal("TemplateNotDraft", await Guard(late));
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM TemplateGates WHERE TemplateId='{id}'"));
        Assert.Equal(1, Audits(f, "Update", id));
    }

    [Fact]
    public async Task Roles_read_for_all_draft_for_admin_approve_and_retire_for_RM_and_governance()
    {
        var (f, rte, rm, gov, viewer) = await Setup(); using var _f = f;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, "/api/v1/templates", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(gov, HttpMethod.Post, "/api/v1/templates", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/templates")).StatusCode);
        var (id, v) = await Create(rte, Body());
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/templates/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(rte, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(gov, HttpMethod.Put, $"/api/v1/templates/{id}", Body("x"), v.ToString())).StatusCode);
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(rte, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, (v + 1).ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(gov, HttpMethod.Post, $"/api/v1/templates/{id}:retire", null, (v + 1).ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/templates/nope")).StatusCode);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_input_is_validated()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        await Create(rte, Body("Standard change"));
        Assert.Equal("TemplateNameTaken", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("standard CHANGE"))));
        Assert.Equal("TemplateNameTaken", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("Standard release"))));   // the seeded default
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("  "))));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Bad gate", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = -1, requiredBeforeStatus = "Gated" } } })));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Bad gate2", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = 1, requiredBeforeStatus = "Nowhere" } } })));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Bad step", steps = new[] { new { stepCode = "A", section = "Deploy", title = "t", offsetMinutes = 0, plannedDurationMin = 0 } } })));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Bad lib", schedule = new[] { new { libraryTemplateId = "none", offsetDays = -7 } } })));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM TrainTemplates WHERE Name IN ('  ','Bad gate','Bad gate2','Bad step','Bad lib')"));
    }

    [Fact]
    public async Task If_Match_stale_gives_409_with_the_current_row_and_writes_nothing()
    {
        var (f, rte, rm, gov, _) = await Setup(); using var _f = f;
        var (id, v) = await Create(rte, Body());
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Put, $"/api/v1/templates/{id}", Body("Renamed"), v.ToString())).StatusCode);
        var stale = await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var cur = (await Json(stale)).GetProperty("current");
        Assert.Equal(v + 1, cur.GetProperty("version").GetInt32());
        Assert.Equal("Draft", cur.GetProperty("status").GetString());
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Put, $"/api/v1/templates/{id}", Body("Again"), v.ToString())).StatusCode);
        Assert.Equal(0, Audits(f, "Approve", id));
    }

    [Fact]
    public async Task If_Match_is_required_when_configured()
    {
        var (f, rte, rm, _, _) = await Setup(requireIfMatch: true); using var _f = f;
        var (id, v) = await Create(rte, Body());   // create has no version to match
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(rte, HttpMethod.Put, $"/api/v1/templates/{id}", Body("Renamed"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, $"\"{v}\"")).StatusCode);
    }

    [Fact]
    public async Task Detail_carries_gates_runbook_skeleton_and_the_T_minus_plan_and_flags_an_overdue_review()
    {
        var (f, rte, rm, _, viewer) = await Setup(); using var _f = f;
        Sql(f, "INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody) VALUES('lib1','Tminus7','T-7 heads-up','All','s','b');");
        var (id, v) = await Create(rte, Body(schedule: new[] { new { libraryTemplateId = "lib1", offsetDays = -7 } }));
        var d = await Json(await viewer.GetAsync($"/api/v1/templates/{id}"));
        Assert.Equal(2, d.GetProperty("gates").GetArrayLength());
        Assert.Equal("Gate 1", d.GetProperty("gates")[0].GetProperty("gateName").GetString());
        Assert.Equal("PRE-1", d.GetProperty("steps")[0].GetProperty("stepCode").GetString());
        Assert.Equal("T-7 heads-up", d.GetProperty("schedule")[0].GetProperty("libraryName").GetString());
        Assert.Equal(-7, d.GetProperty("schedule")[0].GetProperty("offsetDays").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:approve", null, v.ToString())).StatusCode);
        Sql(f, $"UPDATE TrainTemplates SET ReviewDueOn='2020-01-01' WHERE Id='{id}';");
        var list = await Json(await viewer.GetAsync("/api/v1/templates"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetString() == id && x.GetProperty("reviewOverdue").GetBoolean());
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("name").GetString() == "Standard release" && !x.GetProperty("reviewOverdue").GetBoolean());
    }
}
