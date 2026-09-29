using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-30: Rehearsal and Live runs, step actions, dependency ordering, one open Live run per train, actuals never touching the plan.</summary>
public class RunTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Guard(JsonElement b) => b.GetProperty("guard").GetString()!;

    /// <summary>Train t1 is Planning; train t3 is Executing (the schema only allows a Live run on an Executing train). Both get the same 3-step plan: A (PreCheck), B (Deploy, after A), R (Rollback).</summary>
    private static async Task<(ApiFactory F, HttpClient Rte, string Owner)> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var o = UserId(f, "rte@x.com");
        SeedTrain(f, o, o);
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        return (f, rte, o);
    }

    private static async Task<(string A, string B, string R)> Plan(HttpClient c, string train, string owner)
    {
        async Task<string> Add(string title, string section, string start, int min) =>
            (await Json(await c.PostAsJsonAsync($"/api/v1/trains/{train}/steps", new { title, section, ownerUserId = owner, plannedStartAt = start, plannedDurationMin = min }))).GetProperty("id").GetString()!;
        var a = await Add("Stop traffic", "PreCheck", "2026-10-30T06:00:00Z", 10);
        var b = await Add("Deploy API", "Deploy", "2026-10-30T06:10:00Z", 30);
        var r = await Add("Restore snapshot", "Rollback", "2026-10-30T05:00:00Z", 45);          // earlier than the rest on purpose: rebasing must ignore Rollback
        var dep = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/steps/{b}/dependencies") { Content = JsonContent.Create(new { dependsOn = new[] { a } }) };
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(dep)).StatusCode);
        return (a, b, r);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, string run, string step, string action, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runs/{run}/steps/{step}:{action}") { Content = JsonContent.Create(body ?? new { }) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(req);
    }

    private static async Task<string> StartRun(HttpClient c, string train, string mode) => (await Json(await c.PostAsJsonAsync($"/api/v1/trains/{train}/runs", new { mode }))).GetProperty("id").GetString()!;

    [Fact]
    public async Task Starting_a_step_before_its_dependency_is_422_and_works_once_the_dependency_is_done()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, _) = await Plan(c, "t3", o);
        var run = await StartRun(c, "t3", "Live");

        var early = await Act(c, run, b, "start");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        var body = await Json(early);
        Assert.Equal("DependencyNotDone", Guard(body));
        Assert.Contains("R-001", body.GetProperty("message").GetString());                                   // names what is missing
        Assert.Equal("Scheduled", Scalar(f, $"SELECT Status FROM StepExecutions WHERE StepId='{b}'"));       // and nothing moved

        Assert.Equal(HttpStatusCode.OK, (await Act(c, run, a, "start")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(c, run, a, "done")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(c, run, b, "start")).StatusCode);                          // now allowed
        Assert.Equal("Running", Scalar(f, $"SELECT Status FROM StepExecutions WHERE StepId='{b}'"));
    }

    [Fact]
    public async Task A_skipped_dependency_counts_as_satisfied_but_only_with_a_comment()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, _) = await Plan(c, "t3", o);
        var run = await StartRun(c, "t3", "Live");
        var noNote = await Act(c, run, a, "skip");
        Assert.Equal("SkipNeedsNote", Guard(await Json(noNote)));
        Assert.Equal(HttpStatusCode.OK, (await Act(c, run, a, "skip", new { note = "Traffic already drained by ops" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(c, run, b, "start")).StatusCode);
        Assert.Equal("Traffic already drained by ops", Scalar(f, $"SELECT Note FROM StepExecutions WHERE StepId='{a}'"));
    }

    [Fact]
    public async Task There_is_at_most_one_open_live_run_per_train_and_a_live_run_needs_an_executing_train()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        await Plan(c, "t3", o); await Plan(c, "t1", o);

        var planning = await c.PostAsJsonAsync("/api/v1/trains/t1/runs", new { mode = "Live" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, planning.StatusCode);
        Assert.Equal("LiveRequiresExecuting", Guard(await Json(planning)));
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t1/runs", new { mode = "Rehearsal" })).StatusCode);   // rehearsing needs no Executing train
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t1/runs", new { mode = "Rehearsal" })).StatusCode);   // and several rehearsals may exist

        var first = await StartRun(c, "t3", "Live");
        var second = await c.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Live" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("LiveRunOpen", Guard(await Json(second)));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM RunbookRuns WHERE ReleaseTrainId='t3'"));

        var end = await c.PostAsJsonAsync($"/api/v1/runs/{first}:end", new { outcome = "Aborted" });
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Live" })).StatusCode);         // a new Live run once the first ended

        Assert.Equal("InvalidRun", Guard(await Json(await c.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Dress" }))));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/trains/nope/runs", new { mode = "Live" })).StatusCode);
    }

    [Fact]
    public async Task A_run_needs_steps()
    {
        var (f, c, _) = await Setup(); using var _f = f;
        var r = await c.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Live" });
        Assert.Equal("InvalidRun", Guard(await Json(r)));
    }

    [Fact]
    public async Task Step_states_follow_start_then_done_or_fail_and_never_touch_the_plan()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, _) = await Plan(c, "t3", o);
        var planBefore = Scalar(f, $"SELECT PlannedStartAt || '|' || PlannedDurationMin || '|' || Version FROM RunbookSteps WHERE Id='{a}'");
        var run = await StartRun(c, "t3", "Live");

        Assert.Equal("IllegalStepTransition", Guard(await Json(await Act(c, run, a, "done"))));                 // cannot finish what never started
        var started = await Json(await Act(c, run, a, "start"));
        Assert.Equal("Running", started.GetProperty("status").GetString());
        Assert.Equal("IllegalStepTransition", Guard(await Json(await Act(c, run, a, "start"))));                // cannot start twice
        Assert.Equal("IllegalStepTransition", Guard(await Json(await Act(c, run, a, "skip", new { note = "x" }))));   // a running step cannot be skipped
        var failed = await Json(await Act(c, run, a, "fail", new { note = "Health check red" }));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("IllegalStepTransition", Guard(await Json(await Act(c, run, a, "done"))));                  // a failed step stays failed

        // actuals are in StepExecutions; the plan row is byte-for-byte what it was
        Assert.Equal(planBefore, Scalar(f, $"SELECT PlannedStartAt || '|' || PlannedDurationMin || '|' || Version FROM RunbookSteps WHERE Id='{a}'"));
        Assert.Equal("Failed", Scalar(f, $"SELECT Status FROM StepExecutions WHERE StepId='{a}' AND RunId='{run}'"));
        Assert.Equal("Health check red", Scalar(f, $"SELECT Note FROM StepExecutions WHERE StepId='{a}'"));
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='StepExecution'"));      // start and fail: refused actions leave no audit row
    }

    [Fact]
    public async Task A_rehearsal_rebases_planned_times_to_its_own_start_and_a_live_run_keeps_absolute_times()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        await Plan(c, "t1", o); await Plan(c, "t3", o);
        var reh = await StartRun(c, "t1", "Rehearsal");
        var live = await StartRun(c, "t3", "Live");

        var rd = await Json(await c.GetAsync($"/api/v1/runs/{reh}"));
        var started = DateTime.Parse(rd.GetProperty("startedAt").GetString()!).ToUniversalTime();
        var steps = rd.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("stepCode").GetString()!);
        DateTime P(string code) => DateTime.Parse(steps[code].GetProperty("plannedStartAt").GetString()!).ToUniversalTime();
        Assert.True(Math.Abs((P("R-001") - started).TotalSeconds) <= 1, "the earliest non-rollback step starts when the rehearsal starts");
        Assert.Equal(TimeSpan.FromMinutes(10), P("R-002") - P("R-001"));                                         // relative spacing kept
        Assert.Equal(TimeSpan.FromHours(-1), P("R-003") - P("R-001"));                                           // the rollback step keeps its offset (05:00 vs 06:00), it does not set the anchor
        Assert.True(rd.GetProperty("shiftMinutes").GetInt32() != 0);

        var ld = await Json(await c.GetAsync($"/api/v1/runs/{live}"));
        Assert.Equal(0, ld.GetProperty("shiftMinutes").GetInt32());
        Assert.Equal("2026-10-30T06:00:00Z", ld.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("stepCode").GetString() == "R-001").GetProperty("plannedStartAt").GetString());
    }

    [Fact]
    public async Task Ending_a_run_needs_no_running_steps_and_Completed_needs_everything_but_Rollback_done()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, r) = await Plan(c, "t3", o);
        await Plan(c, "t1", o);                                                                                   // t1 needs steps for the rehearsal at the end
        var run = await StartRun(c, "t3", "Live");
        await Act(c, run, a, "start");

        Assert.Equal("StepsStillRunning", Guard(await Json(await c.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "Aborted" }))));
        await Act(c, run, a, "done");
        var open = await c.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "Completed" });
        Assert.Equal("StepsIncomplete", Guard(await Json(open)));
        Assert.Contains("R-002", (await Json(open)).GetProperty("message").GetString());                        // B is not done
        Assert.DoesNotContain("R-003", (await Json(open)).GetProperty("message").GetString());                  // the rollback step is exempt

        await Act(c, run, b, "start"); await Act(c, run, b, "done");
        var done = await c.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "Completed" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("Completed", Scalar(f, $"SELECT Outcome FROM RunbookRuns WHERE Id='{run}'"));

        Assert.Equal("RunEnded", Guard(await Json(await Act(c, run, r, "start"))));                               // an ended run takes no more actions
        Assert.Equal("RunEnded", Guard(await Json(await c.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "Completed" }))));
        Assert.Equal("InvalidRun", Guard(await Json(await c.PostAsJsonAsync($"/api/v1/runs/{await StartRun(c, "t1", "Rehearsal")}:end", new { outcome = "Vanished" }))));
    }

    [Fact]
    public async Task Rolled_back_can_end_with_open_steps_and_step_actions_are_versioned_and_audited()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, _, r) = await Plan(c, "t3", o);
        var run = await StartRun(c, "t3", "Live");
        var s1 = await Json(await Act(c, run, a, "start", ifMatch: "\"1\""));
        Assert.Equal(2, s1.GetProperty("version").GetInt32());
        var stale = await Act(c, run, a, "done", ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Running", (await Json(stale)).GetProperty("current").GetProperty("status").GetString());
        await Act(c, run, a, "fail", new { note = "bad build" });
        await Act(c, run, r, "start"); await Act(c, run, r, "done");                                             // run the rollback section
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "RolledBack" })).StatusCode);
        Assert.True(int.Parse(Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType IN ('StepExecution','RunbookRun')")) >= 6);
    }

    [Fact]
    public async Task Owners_can_act_on_their_own_steps_but_other_people_cannot()
    {
        var (f, rte, o) = await Setup(); using var _ = f;
        var ownerClient = await As(f, Roles.Viewer, "owner@x.com");
        var otherClient = await As(f, Roles.Viewer, "other@x.com");
        var ownerId = UserId(f, "owner@x.com");
        var (a, _, _) = await Plan(rte, "t3", ownerId);                                                          // steps owned by the Viewer-role user
        var run = await StartRun(rte, "t3", "Live");
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(otherClient, run, a, "start")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(ownerClient, run, a, "start")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(rte, run, a, "done")).StatusCode);                             // planners can always act
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync("/api/v1/trains/t3/runs", new { mode = "Rehearsal" })).StatusCode);   // starting/ending runs is a planner job
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync($"/api/v1/runs/{run}:end", new { outcome = "Aborted" })).StatusCode);
    }

    [Fact]
    public async Task The_freeze_trigger_backstop_surfaces_as_a_readable_422_not_a_500()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, _) = await Plan(c, "t3", o);
        var run = await StartRun(c, "t3", "Live");
        await Act(c, run, a, "start"); await Act(c, run, a, "done");
        var now = DateTime.UtcNow;
        Sql(f, $"INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('fz','Q4 close','Freeze','{now.AddHours(-1):yyyy-MM-dd'T'HH:mm:ss'Z'}','{now.AddHours(1):yyyy-MM-dd'T'HH:mm:ss'Z'}','{o}')");
        var r = await Act(c, run, b, "start");                                                                    // Deploy step, Live run, active freeze, no override
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("DbRule", Guard(body));
        Assert.Contains("Freeze window", body.GetProperty("message").GetString());
        Assert.Equal("Scheduled", Scalar(f, $"SELECT Status FROM StepExecutions WHERE StepId='{b}'"));
    }

    [Fact]
    public async Task Forecast_endpoint_reports_finish_rollback_deadline_and_per_step_variance()
    {
        var (f, c, o) = await Setup(); using var _ = f;
        var (a, b, r) = await Plan(c, "t3", o);
        Sql(f, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w3','t3','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z')");
        var run = await StartRun(c, "t3", "Live");
        var fc = await Json(await c.GetAsync($"/api/v1/runs/{run}/forecast"));
        Assert.Equal("Live", fc.GetProperty("mode").GetString());
        Assert.Equal(45, fc.GetProperty("rollbackPlannedMin").GetInt32());                                   // the single Rollback step (45 min)
        Assert.Equal("2026-10-30T09:15:00Z", fc.GetProperty("rollbackDeadline").GetString());                // window end 10:00 minus 45
        Assert.Equal(2, fc.GetProperty("steps").GetArrayLength());                                            // Rollback steps are not part of the forecast itself
        Assert.Equal("R-001", fc.GetProperty("steps")[0].GetProperty("step").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/runs/nope/forecast")).StatusCode);
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/runs/{run}/forecast")).StatusCode);   // anyone signed in can read it
    }
}
