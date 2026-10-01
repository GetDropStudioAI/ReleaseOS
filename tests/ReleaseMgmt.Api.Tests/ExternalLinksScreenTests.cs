using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-82: what the train workspace's Links section relies on. A link attaches to the train, a product, a gate or a runbook step of that train
/// (never another train's), the list says what it is attached to and its sync state, and add and remove are admin writes with Version, If-Match and one audit row each.
/// </summary>
public class ExternalLinksScreenTests
{
    private static async Task<HttpClient> As(ApiFactory f, string role, string email)
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static void Seed(ApiFactory f, string ownerId) => Sql(f, $"""
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11 Payments','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','R26.12 Ledger','2026-12-15','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
        INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t1','Payments API','4.5.0','PAY');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId) VALUES('g1','t1','QA Sign-off',1,3,'2026-11-25','{ownerId}');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId) VALUES('g2','t2','QA Sign-off',1,3,'2026-12-10','{ownerId}');
        INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t1','R-001','Deploy API','{ownerId}','2026-11-30T02:00:00Z',30);
        """);

    [Fact]
    public async Task A_link_attaches_to_the_train_a_product_a_gate_or_a_runbook_step_of_that_train_only()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Seed(f, Scalar(f, "SELECT Id FROM Users WHERE Email='rte@x.com'"));

        foreach (var (type, id, source, key) in new[] { ("Train", "t1", "ServiceNow", "CHG0030001"), ("Product", "p1", "Jira", "PAY/4.5.0"), ("Gate", "g1", "Jira", "PAY-12"), ("RunbookStep", "s1", "ServiceNow", "CTASK0010001") })
            Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/trains/t1/links", new { entityType = type, entityId = id, sourceSystem = source, externalKey = key })).StatusCode);

        // another train's gate, a made-up product and an entity type the schema does not have are refused with a readable 422
        foreach (var (type, id) in new[] { ("Gate", "g2"), ("Product", "nope"), ("Task", "t1") })
        {
            var r = await rte.PostAsJsonAsync("/api/v1/trains/t1/links", new { entityType = type, entityId = id, sourceSystem = "Jira", externalKey = "PAY-13" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("LinkInvalid", (await Json(r)).GetProperty("guard").GetString());
        }

        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var list = (await Json(await viewer.GetAsync("/api/v1/trains/t1/links"))).EnumerateArray().ToList();
        Assert.Equal(4, list.Count);
        Assert.Equal(["Gate:g1", "Product:p1", "RunbookStep:s1", "Train:t1"],
            list.Select(l => $"{l.GetProperty("entityType").GetString()}:{l.GetProperty("entityId").GetString()}").Order().ToArray());
        // never checked yet: Unsynced, no time, not stale (the screen says "not yet checked")
        Assert.All(list, l => Assert.Equal(("Unsynced", JsonValueKind.Null, false), (l.GetProperty("syncState").GetString(), l.GetProperty("lastSyncedAt").ValueKind, l.GetProperty("stale").GetBoolean())));
        Assert.Empty((await Json(await viewer.GetAsync("/api/v1/trains/t2/links"))).EnumerateArray());
        Assert.Equal("4", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExternalLink' AND Action='Add' AND ReleaseTrainId='t1'"));
    }

    [Fact]
    public async Task Remove_needs_the_admin_policy_and_If_Match_and_writes_one_audit_row()
    {
        using var f = new ApiFactory(requireIfMatch: true);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        Seed(f, Scalar(f, "SELECT Id FROM Users WHERE Email='rte@x.com'"));
        var add = await rte.PostAsJsonAsync("/api/v1/trains/t1/links", new { entityType = "Gate", entityId = "g1", sourceSystem = "Jira", externalKey = "pay-12" });
        var link = await Json(add);
        Assert.Equal("PAY-12", link.GetProperty("externalKey").GetString());
        var id = link.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await gov.PostAsJsonAsync("/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "Jira", externalKey = "PAY-1" })).StatusCode);
        var govDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/links/{id}"); govDelete.Headers.TryAddWithoutValidation("If-Match", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await gov.SendAsync(govDelete)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await rte.DeleteAsync($"/api/v1/links/{id}")).StatusCode);   // Q-004: If-Match required

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/links/{id}"); del.Headers.TryAddWithoutValidation("If-Match", "1");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(del)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ExternalLinks"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExternalLink' AND EntityId='{id}' AND Action='Remove'"));
        Assert.Equal(Scalar(f, "SELECT Id FROM Users WHERE Email='rte@x.com'"), Scalar(f, $"SELECT ActorUserId FROM AuditEvents WHERE EntityId='{id}' AND Action='Remove'"));
    }
}
