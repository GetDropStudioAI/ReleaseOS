using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-109: environments and the service catalog over HTTP: planners create, edit and archive; everything is audited; a product links to a catalog service; per workspace.</summary>
public class EnvironmentAndCatalogTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Guard(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static string Audits(ApiFactory f, string type, string id, string action) => Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{type}' AND EntityId='{id}' AND Action='{action}'");

    private static async Task<HttpResponseMessage> Patch(HttpClient c, string url, object body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Environments_are_created_edited_and_archived_by_planners_and_every_change_is_audited()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Sql(f, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('wh1','Deploy tool','deploy.example.com','CfDJ8X',printf('%064d',1),'Generic')");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/environments", new { name = "Staging", kind = "Staging" })).StatusCode);
        var created = await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production EU", kind = "Production", region = "eu-west-1", controlWebhookDestinationId = "wh1", errorRateMaxPct = 2.5, sloAvailabilityPct = 99.9 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var env = await Json(created);
        var id = env.GetProperty("id").GetString()!;
        Assert.Equal("Deploy tool", env.GetProperty("controlWebhook").GetProperty("name").GetString());
        Assert.Equal("deploy.example.com", env.GetProperty("controlWebhook").GetProperty("host").GetString());   // the host only: the address is never shown
        Assert.Equal(1, int.Parse(Audits(f, "Environment", id, "Create")));

        Assert.Equal("EnvironmentNameTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "production eu", kind = "Staging" })));
        Assert.Equal("EnvironmentInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Dev", kind = "Development" })));
        Assert.Equal("EnvironmentInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Dev", kind = "Staging", errorRateMaxPct = 120 })));
        Assert.Equal("EnvironmentInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Dev", kind = "Staging", controlWebhookDestinationId = "nope" })));

        var v = env.GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(rte, $"/api/v1/environments/{id}", new { region = "eu-central-1" }, v + 3)).StatusCode);
        var edited = await Json(await Patch(rte, $"/api/v1/environments/{id}", new { region = "eu-central-1", latencyP95MaxMs = 800, clear = new[] { "errorRateMaxPct", "controlWebhookDestinationId" } }, v));
        Assert.Equal("eu-central-1", edited.GetProperty("region").GetString());
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("errorRateMaxPct").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("controlWebhook").ValueKind);
        Assert.Equal(800, edited.GetProperty("latencyP95MaxMs").GetDouble());
        Assert.Equal(v + 1, edited.GetProperty("version").GetInt32());
        Assert.Equal(UserId(f, "rte@x.com"), Scalar(f, $"SELECT LastChangedByUserId FROM Environments WHERE Id='{id}'"));
        Assert.Equal("EnvironmentInvalid", await Guard(await Patch(rte, $"/api/v1/environments/{id}", new { clear = new[] { "name" } }, v + 1)));
        // a repeat writes nothing
        var same = await Json(await Patch(rte, $"/api/v1/environments/{id}", new { region = "eu-central-1" }, v + 1));
        Assert.Equal(v + 1, same.GetProperty("version").GetInt32());
        Assert.Equal("1", Audits(f, "Environment", id, "Update"));

        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/environments/{id}:archive", null)).StatusCode);
        Assert.DoesNotContain(id, await rte.GetStringAsync("/api/v1/environments"));
        Assert.Contains(id, await rte.GetStringAsync("/api/v1/environments?includeArchived=true"));
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/environments/{id}:unarchive", null)).StatusCode);
        Assert.Equal("1", Audits(f, "Environment", id, "Archive")); Assert.Equal("1", Audits(f, "Environment", id, "Unarchive"));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/environments/nope")).StatusCode);
    }

    [Fact]
    public async Task An_environment_with_deployments_in_progress_cannot_be_archived()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var env = (await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Staging", kind = "Staging" }))).GetProperty("id").GetString()!;
        Sql(f, $@"INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','2026-10-01T00:00:00Z');
                 INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseVersion,State,StartedAt,CreatedAt,UpdatedAt) VALUES('d1','ci-1','sv1','{env}','1.0','Running','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        Assert.Equal("EnvironmentInvalid", await Guard(await rte.PostAsync($"/api/v1/environments/{env}:archive", null)));
        Assert.Equal(1, (await rte.GetFromJsonAsync<JsonElement>($"/api/v1/environments/{env}")).GetProperty("activeDeployments").GetInt32());
    }

    [Fact]
    public async Task The_service_catalog_is_managed_by_planners_and_a_product_links_to_a_service_by_key_or_id()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','pay','Payments')");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/services", new { serviceKey = "x", name = "X" })).StatusCode);
        var created = await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API", ownerTeamId = "tm1", repository = "git.example/pay/api", projectCode = "PAY" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var svc = await Json(created);
        var id = svc.GetProperty("id").GetString()!;
        Assert.Equal("Payments", svc.GetProperty("ownerTeam").GetProperty("name").GetString());
        Assert.Equal("1", Audits(f, "CatalogService", id, "Create"));
        Assert.Equal("ServiceKeyTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "PAYMENTS-API", name = "Dup" })));
        Assert.Equal("ServiceInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "has space", name = "Bad" })));
        Assert.Equal("ServiceInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "ok", name = "Bad", ownerTeamId = "nope" })));

        var edited = await Json(await Patch(rte, $"/api/v1/services/{id}", new { name = "Payments API v2", repository = "" }, svc.GetProperty("version").GetInt32()));
        Assert.Equal("Payments API v2", edited.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("repository").ValueKind);
        Assert.Equal("payments-api", edited.GetProperty("serviceKey").GetString());   // the key never changes
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(rte, $"/api/v1/services/{id}", new { name = "Again" }, 1)).StatusCode);

        // A train's product links to the service: at creation by key, afterwards by key or id, and unlinks with {}
        var train = await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01", targetReleaseDate = Target(), riskTier = "Low",
            products = new[] { new { productName = "Payments API", versionTag = "4.5.0", projectCode = "PAY", serviceKey = "PAYMENTS-api" }, new { productName = "Card Portal", versionTag = "2.1.0", projectCode = "CRD", serviceKey = "" } } });
        Assert.Equal(HttpStatusCode.OK, train.StatusCode);
        var trainId = (await Json(train)).GetProperty("id").GetString()!;
        Assert.Equal(id, Scalar(f, $"SELECT CatalogServiceId FROM BundledProducts WHERE ReleaseTrainId='{trainId}' AND ProductName='Payments API'"));
        Assert.Equal("UnknownService", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.02", targetReleaseDate = Target(), riskTier = "Low",
            products = new[] { new { productName = "X", versionTag = "1", projectCode = "X", serviceKey = "nope" } } })));

        var portal = Scalar(f, $"SELECT Id FROM BundledProducts WHERE ReleaseTrainId='{trainId}' AND ProductName='Card Portal'");
        async Task<HttpResponseMessage> Link(object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{portal}/service") { Content = JsonContent.Create(body) };
            return await rte.SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.OK, (await Link(new { serviceId = id })).StatusCode);
        Assert.Equal(id, Scalar(f, $"SELECT CatalogServiceId FROM BundledProducts WHERE Id='{portal}'"));
        Assert.Equal("1", Audits(f, "BundledProduct", portal, "LinkService"));
        Assert.Equal(HttpStatusCode.OK, (await Link(new { serviceId = id })).StatusCode);   // a repeat writes nothing
        Assert.Equal("1", Audits(f, "BundledProduct", portal, "LinkService"));
        Assert.Equal(HttpStatusCode.NotFound, (await Link(new { serviceKey = "nope" })).StatusCode);
        Assert.Equal("ServiceInvalid", await Guard(await Link(new { serviceId = id, serviceKey = "payments-api" })));
        Assert.Equal(HttpStatusCode.OK, (await Link(new { })).StatusCode);
        Assert.Equal("", Scalar(f, $"SELECT COALESCE(CatalogServiceId,'') FROM BundledProducts WHERE Id='{portal}'"));
        Assert.Equal("1", Audits(f, "BundledProduct", portal, "UnlinkService"));
        var listed = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/services")).EnumerateArray().Single();
        Assert.Equal(1, listed.GetProperty("products").GetInt32());

        // An archived service cannot be linked; the clone copies the links
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/services/{id}:archive", null)).StatusCode);
        Assert.Equal("ServiceInvalid", await Guard(await Link(new { serviceKey = "payments-api" })));
        Assert.DoesNotContain(id, await rte.GetStringAsync("/api/v1/services"));
        var clone = await rte.PostAsJsonAsync($"/api/v1/trains/{trainId}:clone", new { title = "R27.03", targetReleaseDate = Target(90) });
        var cloneId = (await Json(clone)).GetProperty("id").GetString()!;
        Assert.Equal(id, Scalar(f, $"SELECT CatalogServiceId FROM BundledProducts WHERE ReleaseTrainId='{cloneId}' AND ProductName='Payments API'"));
    }

    [Fact]
    public async Task Two_workspaces_can_each_have_a_service_and_an_environment_of_the_same_name_and_see_only_their_own()
    {
        using var f = new ApiFactory();
        var a = await As(f, Roles.RTE, "a@x.com");
        var b = await WorkspaceIsolationTests.InB(f, Roles.RTE, "b@x.com");
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "ZQALPHA service" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production", region = "ZQALPHA" })).StatusCode);
        Assert.DoesNotContain("ZQALPHA", await b.GetStringAsync("/api/v1/services"));
        Assert.DoesNotContain("ZQALPHA", await b.GetStringAsync("/api/v1/environments"));
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Beta service" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" })).StatusCode);
        Assert.Contains("ZQALPHA", await a.GetStringAsync("/api/v1/services"));
        Assert.DoesNotContain("Beta service", await a.GetStringAsync("/api/v1/services"));
        // B cannot point its release at A's environment or its product at A's service, even knowing the ids
        var envA = (await a.GetFromJsonAsync<JsonElement>("/api/v1/environments")).EnumerateArray().Single().GetProperty("id").GetString()!;
        var svcA = (await a.GetFromJsonAsync<JsonElement>("/api/v1/services")).EnumerateArray().Single().GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/environments/{envA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/services/{svcA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/v1/environments/{envA}:archive", null)).StatusCode);
    }
}
