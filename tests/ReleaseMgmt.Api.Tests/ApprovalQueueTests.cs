using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-94 (B1): GET /approvals lists what waits for the caller's decision; the shell's Approvals badge reads its total. Eligibility follows the action
/// endpoints' own role and separation-of-duties rules, the counts match the list, and an item leaves the queue once it is decided.
/// </summary>
public class ApprovalQueueTests
{
    private static async Task<JsonElement> Queue(HttpClient c)
    {
        var r = await c.GetAsync("/api/v1/approvals");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>"Kind:id" for every item, after checking that the counts agree with the list.</summary>
    private static async Task<List<string>> Items(HttpClient c)
    {
        var q = await Queue(c);
        var items = q.GetProperty("items").EnumerateArray().ToList();
        var counts = q.GetProperty("counts");
        Assert.Equal(items.Count, counts.GetProperty("total").GetInt32());
        foreach (var (kind, prop) in new[] { ("CertifyGate", "certify"), ("ApproveWaiver", "waivers"), ("GrantOverride", "overrides"), ("RecordGoNoGo", "goNoGo"), ("CloseCondition", "conditions") })
            Assert.Equal(items.Count(i => i.GetProperty("kind").GetString() == kind), counts.GetProperty(prop).GetInt32());
        return [.. items.Select(i => $"{i.GetProperty("kind").GetString()}:{i.GetProperty("id").GetString()}")];
    }

    private static async Task Ok(HttpResponseMessage r) => Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");

    [Fact]
    public async Task Each_person_sees_only_the_decisions_they_may_take_and_an_item_leaves_once_decided()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov1 = await As(f, Roles.GovernanceOfficer, "gov1@x.com");
        var gov2 = await As(f, Roles.GovernanceOfficer, "gov2@x.com");
        var viewer = await As(f, Roles.Viewer, "viewer@x.com");
        string r = UserId(f, "rte@x.com"), rmId = UserId(f, "rm@x.com"), g1 = UserId(f, "gov1@x.com"), g2 = UserId(f, "gov2@x.com");
        SeedTrain(f, r, g1);
        var everyone = new[] { rte, rm, gov1, gov2, viewer };
        foreach (var c in everyone) Assert.Empty(await Items(c));   // the compliance gate still has an open task and Code Freeze is not certified

        // Code Freeze certified and the SOX task done by gov2: the Compliance gate is ready, for a Governance Officer who did none of its tasks
        Sql(f, $@"
            UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-20T10:00:00Z', CompletedByUserId='{r}', LastChangedByUserId='{r}' WHERE Id='k1';
            UPDATE StageGates SET Status='InProgress', LastChangedByUserId='{r}' WHERE Id='g1';
            UPDATE StageGates SET Status='Certified', CertifiedByUserId='{r}', CertifiedAt='2026-10-20T11:00:00Z', LastChangedByUserId='{r}', LastChangedAt='2026-10-20T11:00:00Z' WHERE Id='g1';
            UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-20T12:00:00Z', CompletedByUserId='{g2}', LastChangedByUserId='{g2}' WHERE Id='k2';
            INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('w1','g2','The auditor holds the evidence this cycle','{g2}','2026-10-20T13:00:00Z');");
        Assert.Equal(["CertifyGate:g2", "ApproveWaiver:w1"], await Items(gov1));
        foreach (var i in (await Queue(gov1)).GetProperty("items").EnumerateArray()) Assert.Equal("g2", i.GetProperty("gateId").GetString());   // the screen finds the gate (and the waiver's version) from it
        Assert.Empty(await Items(gov2));   // completed a task in the gate (SoD), and requested the waiver
        foreach (var c in new[] { rte, rm, viewer }) Assert.Empty(await Items(c));
        var item = (await Queue(gov1)).GetProperty("items")[0];
        Assert.Equal(("Compliance Sign-off", "R26.10", "2026-10-28"), (item.GetProperty("title").GetString(), item.GetProperty("trainTitle").GetString(), item.GetProperty("dueOn").GetString()));

        await Ok(await gov1.PostAsync("/api/v1/gates/g2:certify", null));
        Assert.Empty(await Items(gov1));   // certified: the gate and its pending waiver are both decided

        // A freeze-override request from the RTE: Release Managers and Governance Officers may grant it; the requester may not
        Sql(f, $"INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId) VALUES('fz','Year end','2026-10-01T00:00:00Z','2099-01-01T00:00:00Z','{rmId}')");
        await Ok(await rte.PostAsJsonAsync("/api/v1/freezes/fz/override-requests", new { trainId = "t1", reason = "Payments fix must ship inside the freeze" }));
        var request = $"GrantOverride:fz:t1:{r}";
        Assert.Equal([request], await Items(rm));
        Assert.Equal([request], await Items(gov2));
        Assert.Empty(await Items(rte));
        await Ok(await rm.PostAsJsonAsync("/api/v1/freezes/fz/overrides", new { trainId = "t1", requestedByUserId = r, reason = "Payments fix must ship inside the freeze", expiresAt = "2099-01-01T00:00:00Z" }));
        Assert.Empty(await Items(rm));
        Assert.Empty(await Items(gov2));

        // A Gated train waits for the Release Manager's Go/No-Go; a conditional Go hands its condition to the owner
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t2','R26.11','2026-11-20','Gated','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        Assert.Equal(["RecordGoNoGo:t2"], await Items(rm));
        foreach (var c in new[] { rte, gov1, viewer }) Assert.Empty(await Items(c));
        await Ok(await rm.PostAsJsonAsync("/api/v1/trains/t2/gonogo",
            new { decision = "GoWithConditions", conditions = new[] { new { text = "Load test signed off", ownerUserId = rmId, expiresAt = "2099-01-01T00:00:00Z" } } }));
        var after = await Items(rm);
        Assert.Single(after);
        Assert.StartsWith("CloseCondition:", after[0]);
        Assert.Equal("2099-01-01T00:00:00Z", (await Queue(rm)).GetProperty("items")[0].GetProperty("dueAt").GetString());
    }

    [Fact]
    public async Task The_current_workspace_names_the_default_one()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "viewer@x.com");
        var w = JsonDocument.Parse(await viewer.GetStringAsync("/api/v1/workspace")).RootElement;
        Assert.Equal(("default", "Default", "default"), (w.GetProperty("id").GetString(), w.GetProperty("name").GetString(), w.GetProperty("slug").GetString()));
        Assert.Equal(JsonValueKind.Null, w.GetProperty("region").ValueKind);
    }
}
