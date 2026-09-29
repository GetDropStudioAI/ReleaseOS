using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-24: products rollups, gate detail with certify eligibility, gate timeline positions, deployment window.</summary>
public class PlanningWorkspaceTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Products_roll_up_tasks_and_blockers_into_health()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        Sql(f, $@"
            INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES
              ('p1','t1','Payments API','4.5.0','PAY'),('p2','t1','Card Portal','2.1.0','CRD'),('p3','t1','Fraud Engine','1.12.0','FRD'),('p4','t1','Empty','0.1','EMP');
            UPDATE ChecklistTasks SET BundledProductId='p1' WHERE Id='k1';                          -- p1: 0/1, no blocker -> On track
            INSERT INTO ChecklistTasks(Id,StageGateId,BundledProductId,TaskDescription,OwnerUserId,SequenceOrder) VALUES
              ('k3','g1','p2','Pen test','{id}',2),('k4','g1','p3','Fraud rules','{id}',3);
            UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-02T10:00:00Z', CompletedByUserId='{id}' WHERE Id='k4';   -- p3: 1/1 -> Ready
            INSERT INTO Blockers(Id,ReleaseTrainId,BundledProductId,Title,Severity,RaisedAt) VALUES ('b1','t1','p2','Pen-test finding','High','2026-10-02T09:00:00Z');   -- p2: At risk
            INSERT INTO Blockers(Id,ReleaseTrainId,BundledProductId,Title,Severity,RaisedAt,ResolvedAt) VALUES ('b2','t1','p3','Old','Medium','2026-10-01T09:00:00Z','2026-10-02T09:00:00Z');");   // resolved: ignored
        var body = await Json(await rte.GetAsync("/api/v1/trains/t1/products"));
        var byName = body.GetProperty("products").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal("On track", byName["Payments API"].GetProperty("health").GetString());
        Assert.Equal("At risk", byName["Card Portal"].GetProperty("health").GetString());
        Assert.Equal(["High"], byName["Card Portal"].GetProperty("openBlockers").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("Ready", byName["Fraud Engine"].GetProperty("health").GetString());
        Assert.Equal("On track", byName["Empty"].GetProperty("health").GetString());   // no tasks is not "Ready"
        Assert.Equal(1, body.GetProperty("tasksDone").GetInt32());
        Assert.Equal(3, body.GetProperty("tasksTotal").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/trains/nope/products")).StatusCode);
    }

    [Fact]
    public async Task Gate_detail_explains_why_certify_is_disabled_and_agrees_with_the_certify_endpoint()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));

        // g2 (Compliance) for the RTE: not allowed, an earlier gate is open, a task is open.
        var asRte = await Json(await rte.GetAsync("/api/v1/gates/g2"));
        var cert = asRte.GetProperty("certify");
        Assert.False(cert.GetProperty("eligible").GetBoolean());
        var reasons = cert.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(reasons, r => r.Contains("Governance Officers"));
        Assert.Contains(reasons, r => r.Contains("earlier gate", StringComparison.OrdinalIgnoreCase) && r.Contains("Code Freeze"));
        Assert.Contains(reasons, r => r.Contains("open checklist tasks") && r.Contains("SOX evidence"));

        // The endpoint refuses for the same reasons (the Inspector cannot disagree with the server).
        var refused = await rte.PostAsJsonAsync("/api/v1/gates/g2:certify", new { });
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);

        // Clear every reason for the Governance Officer: certify g1, complete the task, then it is eligible and the endpoint agrees.
        Sql(f, $"UPDATE StageGates SET Status='InProgress' WHERE Id='g1'");
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/tasks/k1:complete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/gates/g1:certify", new { })).StatusCode);
        Sql(f, $"UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-02T10:00:00Z', CompletedByUserId='{UserId(f, "rte@x.com")}' WHERE Id='k2'");
        var asGov = await Json(await gov.GetAsync("/api/v1/gates/g2"));
        Assert.True(asGov.GetProperty("certify").GetProperty("eligible").GetBoolean(), asGov.GetProperty("certify").ToString());
        Assert.Equal(HttpStatusCode.OK, (await gov.PostAsJsonAsync("/api/v1/gates/g2:certify", new { })).StatusCode);
    }

    [Fact]
    public async Task Gate_detail_lists_tasks_and_train_detail_positions_gates_in_business_days()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        var gate = await Json(await rte.GetAsync("/api/v1/gates/g1"));
        var task = gate.GetProperty("tasks")[0];
        Assert.Equal("Tag repos", task.GetProperty("description").GetString());
        Assert.False(task.GetProperty("done").GetBoolean());

        // Target 2026-10-30 (Fri). g1 due Fri 10-23 = 5 business days out; g2 due Wed 10-28 = 2.
        var train = await Json(await rte.GetAsync("/api/v1/trains/t1"));
        var t = train.GetProperty("gates").EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("tMinus").GetInt32());
        Assert.Equal(5, t["Code Freeze"]);
        Assert.Equal(2, t["Compliance Sign-off"]);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/gates/nope")).StatusCode);
    }

    [Fact]
    public async Task Window_can_be_set_changed_and_is_guarded()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/trains/t1/window")).StatusCode);   // none yet

        var put = await rte.PutAsJsonAsync("/api/v1/trains/t1/window", new { startsAt = "2026-10-30T06:00:00Z", endsAt = "2026-10-30T10:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var w = await Json(put);
        Assert.Equal(1, w.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-30T06:00:00Z", w.GetProperty("startsAt").GetString());

        // change with the right version; then a stale version is a 409 carrying the current row
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/trains/t1/window") { Content = JsonContent.Create(new { startsAt = "2026-10-30T07:00:00Z", endsAt = "2026-10-30T11:00:00Z" }) };
        req.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var changed = await rte.SendAsync(req);
        Assert.Equal(2, (await Json(changed)).GetProperty("version").GetInt32());
        var stale = new HttpRequestMessage(HttpMethod.Put, "/api/v1/trains/t1/window") { Content = JsonContent.Create(new { startsAt = "2026-10-30T08:00:00Z", endsAt = "2026-10-30T12:00:00Z" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var conflict = await rte.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(2, (await Json(conflict)).GetProperty("current").GetProperty("version").GetInt32());

        // end must be after start -> 422 naming the guard; a Viewer cannot write
        var bad = await rte.PutAsJsonAsync("/api/v1/trains/t1/window", new { startsAt = "2026-10-30T10:00:00Z", endsAt = "2026-10-30T10:00:00Z" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("InvalidWindow", (await Json(bad)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/trains/t1/window", new { startsAt = "2026-10-30T06:00:00Z", endsAt = "2026-10-30T10:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/trains/t1/window")).StatusCode);       // but may read it

        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='DeploymentWindow'"));      // Set + Change; refused writes leave no audit row
        Assert.Equal("2026-10-30T07:00:00Z", Scalar(f, "SELECT StartsAt FROM DeploymentWindows WHERE ReleaseTrainId='t1'"));
    }
}
