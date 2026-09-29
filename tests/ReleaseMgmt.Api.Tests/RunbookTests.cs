using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-29: runbook plan editing, dependency cycle detection, rollback section, plan lock while a Live run is open.</summary>
public class RunbookTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static object Step(string title, string owner, string section = "Deploy", string start = "2026-10-30T06:00:00Z", int minutes = 15) =>
        new { title, ownerUserId = owner, section, plannedStartAt = start, plannedDurationMin = minutes };

    private static async Task<(ApiFactory F, HttpClient C, string Owner)> Setup(string role = Roles.RTE)
    {
        var f = new ApiFactory();
        var c = await As(f, role, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        return (f, c, id);
    }

    private static async Task<string> Add(HttpClient c, object body) => (await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/steps", body))).GetProperty("id").GetString()!;

    private static Task<HttpResponseMessage> PutDeps(HttpClient c, string stepId, string[] deps, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/steps/{stepId}/dependencies") { Content = JsonContent.Create(new { dependsOn = deps }) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Steps_get_sequential_codes_sections_and_a_rollback_section_is_supported()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var a = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/steps", Step("Stop traffic", o, "PreCheck")));
        var b = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/steps", Step("Deploy API", o)));
        var r = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/steps", Step("Restore snapshot", o, "Rollback", "2026-10-30T07:00:00Z", 30)));
        Assert.Equal(["R-001", "R-002", "R-003"], new[] { a, b, r }.Select(x => x.GetProperty("stepCode").GetString()!).ToArray());
        Assert.Equal("Rollback", r.GetProperty("section").GetString());
        Assert.Equal("2026-10-30T07:30:00Z", r.GetProperty("plannedEndAt").GetString());
        Assert.Equal("Deploy", b.GetProperty("section").GetString());                      // default section

        var list = await Json(await c.GetAsync("/api/v1/trains/t1/steps"));
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal("3", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='RunbookStep' AND Action='Add'"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/trains/nope/steps")).StatusCode);
    }

    [Fact]
    public async Task Invalid_steps_are_refused_with_a_readable_422()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        async Task<(HttpStatusCode, string)> Try(object body) { var r = await c.PostAsJsonAsync("/api/v1/trains/t1/steps", body); return (r.StatusCode, (await Json(r)).GetProperty("guard").GetString()!); }

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(Step("  ", o)));                                    // no title
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(Step("x", o, "Lunch")));                           // unknown section
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(Step("x", o, minutes: 0)));                        // zero duration
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(new { title = "x", plannedStartAt = "2026-10-30T06:00:00Z", plannedDurationMin = 5 }));   // no owner
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(new { title = "x", ownerUserId = o, ownerTeamId = "t", plannedStartAt = "2026-10-30T06:00:00Z", plannedDurationMin = 5 }));   // two owners
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(Step("x", "nobody")));                             // unknown owner
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidStep"), await Try(new { stepCode = "bad code!", title = "x", ownerUserId = o, plannedStartAt = "2026-10-30T06:00:00Z", plannedDurationMin = 5 }));

        await c.PostAsJsonAsync("/api/v1/trains/t1/steps", new { stepCode = "r-010", title = "first", ownerUserId = o, plannedStartAt = "2026-10-30T06:00:00Z", plannedDurationMin = 5 });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "DuplicateStepCode"), await Try(new { stepCode = "R-010", title = "again", ownerUserId = o, plannedStartAt = "2026-10-30T06:00:00Z", plannedDurationMin = 5 }));
        var next = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/steps", Step("auto after R-010", o)));
        Assert.Equal("R-011", next.GetProperty("stepCode").GetString());                                                               // auto code continues from the highest
        Assert.True(Scalar(f, "SELECT COUNT(*) FROM RunbookSteps") == "2", "refused steps must leave nothing behind (only R-010 and R-011 exist)");
    }

    [Fact]
    public async Task Dependencies_are_set_replaced_listed_and_versioned()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var a = await Add(c, Step("A", o)); var b = await Add(c, Step("B", o)); var d = await Add(c, Step("C", o));
        var r1 = await PutDeps(c, d, [a, b]);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var row = await Json(r1);
        Assert.Equal(["R-001", "R-002"], row.GetProperty("dependsOn").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(2, row.GetProperty("version").GetInt32());

        var r2 = await Json(await PutDeps(c, d, [b]));                                         // replaces, not appends
        Assert.Equal(["R-002"], r2.GetProperty("dependsOn").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToArray());
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM StepDependencies WHERE StepId='{d}'"));

        Assert.Equal(HttpStatusCode.Conflict, (await PutDeps(c, d, [a], ifMatch: "\"1\"")).StatusCode);   // stale version
        var clear = await Json(await PutDeps(c, d, []));
        Assert.Equal(0, clear.GetProperty("dependsOn").GetArrayLength());
    }

    [Fact]
    public async Task A_dependency_cycle_is_rejected_with_422_naming_the_cycle_and_changes_nothing()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var a = await Add(c, Step("A", o)); var b = await Add(c, Step("B", o)); var d = await Add(c, Step("C", o));   // R-001, R-002, R-003
        Assert.Equal(HttpStatusCode.OK, (await PutDeps(c, b, [a])).StatusCode);      // B after A
        Assert.Equal(HttpStatusCode.OK, (await PutDeps(c, d, [b])).StatusCode);      // C after B

        var loop = await PutDeps(c, a, [d]);                                          // A after C would close A -> C -> B -> A
        Assert.Equal(HttpStatusCode.UnprocessableEntity, loop.StatusCode);
        var body = await Json(loop);
        Assert.Equal("DependencyCycle", body.GetProperty("guard").GetString());
        Assert.Contains("R-001", body.GetProperty("message").GetString());
        Assert.Contains("R-003", body.GetProperty("message").GetString());
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM StepDependencies WHERE StepId='{a}'"));                   // nothing was written
        Assert.Equal("1", Scalar(f, $"SELECT Version FROM RunbookSteps WHERE Id='{a}'"));

        var self = await PutDeps(c, a, [a]);                                          // the schema CHECK stops a self loop; the domain names it first
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Equal("DependencyCycle", (await Json(self)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Dependencies_must_belong_to_the_same_train()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var a = await Add(c, Step("A", o));
        Sql(f, $@"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','Other','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
                  INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('foreign','t2','R-001','Deploy','Foreign','{o}','2026-11-30T06:00:00Z',5);");
        var r = await PutDeps(c, a, ["foreign"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("InvalidStep", (await Json(r)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Patch_changes_fields_owner_and_code_under_if_match_and_audits_before_and_after()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var a = await Add(c, Step("Old title", o));
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/steps/{a}") { Content = JsonContent.Create(new { title = "New title", plannedDurationMin = 45, section = "Verify", instructions = "Check the dashboards" }) };
        req.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var ok = await Json(await c.SendAsync(req));
        Assert.Equal("New title", ok.GetProperty("title").GetString());
        Assert.Equal(45, ok.GetProperty("plannedDurationMin").GetInt32());
        Assert.Equal(2, ok.GetProperty("version").GetInt32());

        var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/steps/{a}") { Content = JsonContent.Create(new { title = "Lost update" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var conflict = await c.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("New title", (await Json(conflict)).GetProperty("current").GetProperty("title").GetString());
        Assert.Equal("New title", Scalar(f, $"SELECT Title FROM RunbookSteps WHERE Id='{a}'"));

        var audit = Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='RunbookStep' AND Action='Update' AND BeforeJson LIKE '%Old title%' AND AfterJson LIKE '%New title%'");
        Assert.Equal("1", audit);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PatchAsJsonAsync("/api/v1/steps/nope", new { title = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_plan_is_locked_while_a_live_run_is_open_but_not_for_rehearsals_and_locked_when_the_train_is_closed()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        // The schema only lets a Live run exist on an Executing train, so this train starts there.
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        async Task<string> AddT3(string title) => (await Json(await c.PostAsJsonAsync("/api/v1/trains/t3/steps", Step(title, o)))).GetProperty("id").GetString()!;
        var a = await AddT3("A"); var b = await AddT3("B");

        Sql(f, $"INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('rehearsal','t3','Rehearsal','2026-10-29T10:00:00Z','{o}')");
        Assert.Equal(HttpStatusCode.OK, (await c.PatchAsJsonAsync($"/api/v1/steps/{a}", new { title = "fixed during rehearsal" })).StatusCode);   // rehearsals do not lock

        Sql(f, $"INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('live','t3','Live','2026-10-30T06:00:00Z','{o}')");
        foreach (var r in new[] { await c.PostAsJsonAsync("/api/v1/trains/t3/steps", Step("late add", o)), await c.PatchAsJsonAsync($"/api/v1/steps/{a}", new { title = "x" }), await PutDeps(c, b, [a]) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("RunInProgress", (await Json(r)).GetProperty("guard").GetString());
        }
        Assert.Equal("fixed during rehearsal", Scalar(f, $"SELECT Title FROM RunbookSteps WHERE Id='{a}'"));
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM RunbookSteps WHERE ReleaseTrainId='t3'"));                                             // the refused add left nothing

        Sql(f, "UPDATE RunbookRuns SET EndedAt='2026-10-30T09:00:00Z', Outcome='Completed' WHERE Id='live'");                                   // run ended: editable again
        Assert.Equal(HttpStatusCode.OK, (await c.PatchAsJsonAsync($"/api/v1/steps/{a}", new { title = "after the run" })).StatusCode);

        Sql(f, "UPDATE ReleaseTrains SET CurrentStatus='Aborted' WHERE Id='t3'");
        var closed = await c.PatchAsJsonAsync($"/api/v1/steps/{a}", new { title = "too late" });
        Assert.Equal("TrainClosed", (await Json(closed)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Only_planners_can_write_and_anyone_signed_in_can_read()
    {
        var (f, rte, o) = await Setup(); using var _ = f;
        var a = await Add(rte, Step("A", o));
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/trains/t1/steps", Step("nope", o))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"/api/v1/steps/{a}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutDeps(viewer, a, [])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/trains/t1/steps")).StatusCode);
    }
}
