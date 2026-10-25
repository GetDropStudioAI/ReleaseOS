using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-116 (RT1, D46, D48): every release has a type. A release cannot be created without one (a readable 422 from the service, the trigger behind it), a Beta release carries its
/// number, a new release of a type starts from that type's default template, existing releases become Production, a new workspace is seeded, and the lists, the calendar and analytics
/// filter by type. Each service write stamps Version and writes its audit row.
/// </summary>
public class ReleaseTypeRuleTests
{
    private static readonly Dictionary<string, string> Seeded = new() { ["ReleaseTypes:SeedAtStart"] = "true" };
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));

    private static async Task<Dictionary<string, JsonElement>> Types(HttpClient c, bool all = false) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/v1/release-types{(all ? "?includeArchived=true" : "")}")).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);

    private static Task<HttpResponseMessage> CreateTrain(HttpClient c, string title, string? typeId, int? beta = null, string? templateId = null) =>
        c.PostAsJsonAsync("/api/v1/trains", new { title, targetReleaseDate = Target(), riskTier = "Low", releaseTypeId = typeId, betaNumber = beta, templateId });

    private static async Task<HttpResponseMessage> Patch(HttpClient c, string url, object body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<string> Approved(HttpClient planner, HttpClient approver, string name, params string[] gates)
    {
        var made = await planner.PostAsJsonAsync("/api/v1/templates", new
        {
            name, defaultRiskTier = "High",
            gates = gates.Select((g, i) => new { gateName = g, gateClass = "Standard", offsetDays = 5 - i, requiredBeforeStatus = "Gated" }).ToArray(),
            steps = Array.Empty<object>(), schedule = Array.Empty<object>(),
        });
        var id = (await Json(made)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsync($"/api/v1/templates/{id}:approve", null)).StatusCode);
        return id;
    }

    [Fact]
    public async Task A_host_seeds_the_four_types_once_and_a_second_start_changes_nothing()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var types = await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types");
        Assert.Equal(new[] { "Beta", "CERT", "PenTest Cert", "Production" }, types.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { true, false, false, false }, types.EnumerateArray().Select(x => x.GetProperty("isBeta").GetBoolean()).ToArray());   // only Beta carries a number

        var svc = f.Services.GetRequiredService<ReleaseTypeService>();
        Assert.Equal(0, await svc.SeedAllAsync());
        Assert.Equal(4, Count(f, "SELECT COUNT(*) FROM ReleaseTypes"));
    }

    [Fact]
    public async Task A_release_cannot_be_created_without_a_type_the_422_says_so_and_nothing_is_written()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var none = await CreateTrain(rte, "R27.01", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        var body = await Json(none);
        Assert.Equal("ReleaseTypeRequired", body.GetProperty("guard").GetString());
        Assert.Contains("Choose a release type", body.GetProperty("message").GetString());
        var blank = await CreateTrain(rte, "R27.01", "");
        Assert.Equal("ReleaseTypeRequired", (await Json(blank)).GetProperty("guard").GetString());
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM ReleaseTrains"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseTrain'"));

        var unknown = await CreateTrain(rte, "R27.01", "nope");
        Assert.Equal("ReleaseTypeInvalid", (await Json(unknown)).GetProperty("guard").GetString());

        var prod = (await Types(rte))["Production"].GetProperty("id").GetString();
        var ok = await CreateTrain(rte, "R27.01", prod);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var id = (await Json(ok)).GetProperty("id").GetString()!;
        Assert.Equal(prod, Scalar(f, $"SELECT ReleaseTypeId FROM ReleaseTrains WHERE Id='{id}'"));
        Assert.Contains(prod!, Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{id}' AND EntityType='ReleaseTrain' AND Action='Create'"));   // the same transaction wrote the audit row, with the type
    }

    [Fact]
    public async Task The_trigger_is_the_backstop_a_row_inserted_by_SQL_without_a_type_is_refused_in_a_workspace_with_types()
    {
        using var f = new ApiFactory(settings: Seeded);
        await As(f, Roles.RTE, "rte@x.com");
        var ex = Assert.ThrowsAny<SqliteException>(() => Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('x1','x','2026-12-01','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')"));
        Assert.Contains("A release needs a release type", ex.Message);
        var prod = Scalar(f, "SELECT Id FROM ReleaseTypes WHERE Name='Production'");
        Sql(f, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId) VALUES('x1','x','2026-12-01','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','{prod}')");
        var lose = Assert.ThrowsAny<SqliteException>(() => Sql(f, "UPDATE ReleaseTrains SET ReleaseTypeId=NULL WHERE Id='x1'"));
        Assert.Contains("cannot lose its release type", lose.Message);
    }

    [Fact]
    public async Task A_Beta_release_carries_its_number_one_to_three_and_no_other_release_does()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var types = await Types(rte);
        var (beta, cert) = (types["Beta"].GetProperty("id").GetString()!, types["CERT"].GetProperty("id").GetString()!);

        async Task<string> Guard(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
        Assert.Equal("BetaNumberInvalid", await Guard(await CreateTrain(rte, "B", beta)));
        Assert.Equal("BetaNumberInvalid", await Guard(await CreateTrain(rte, "B", beta, 4)));
        Assert.Equal("BetaNumberInvalid", await Guard(await CreateTrain(rte, "B", beta, 0)));
        Assert.Equal("BetaNumberInvalid", await Guard(await CreateTrain(rte, "C", cert, 1)));

        var b2 = await CreateTrain(rte, "R27.01 Beta 2", beta, 2);
        Assert.Equal(HttpStatusCode.OK, b2.StatusCode);
        var id = (await Json(b2)).GetProperty("id").GetString()!;
        var model = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{id}/release");
        Assert.Equal("Beta", model.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(2, model.GetProperty("betaNumber").GetInt32());
        var row = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/trains")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        Assert.Equal(2, row.GetProperty("betaNumber").GetInt32());

        // changing the type: to a type with no number drops it; to a Beta type needs one
        var v = model.GetProperty("version").GetInt32();
        var toCert = await Patch(rte, $"/api/v1/trains/{id}/release", new { releaseTypeId = cert }, v);
        Assert.Equal(HttpStatusCode.OK, toCert.StatusCode);
        var after = await Json(toCert);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("betaNumber").ValueKind);
        Assert.Equal("BetaNumberInvalid", await Guard(await Patch(rte, $"/api/v1/trains/{id}/release", new { releaseTypeId = beta }, v + 1)));
        var back = await Patch(rte, $"/api/v1/trains/{id}/release", new { releaseTypeId = beta, betaNumber = 3 }, v + 1);
        Assert.Equal(3, (await Json(back)).GetProperty("betaNumber").GetInt32());
        Assert.Equal("BetaNumberInvalid", await Guard(await Patch(rte, $"/api/v1/trains/{id}/release", new { betaNumber = 9 }, v + 2)));
        Assert.Equal(2, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{id}' AND Action='ReleaseFields'"));

        // a release cannot lose its type
        var lose = await Patch(rte, $"/api/v1/trains/{id}/release", new { releaseTypeId = "" }, v + 2);
        Assert.Equal("ReleaseTypeRequired", await Guard(lose));
    }

    [Fact]
    public async Task A_new_release_of_a_type_starts_from_that_types_default_template_and_an_explicit_template_wins()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var types = await Types(rte);
        var (cert, pen, prod) = (types["CERT"], types["PenTest Cert"], types["Production"]);
        var certTpl = await Approved(rte, rm, "CERT release", "Code Freeze", "CAB");
        var otherTpl = await Approved(rte, rm, "Other release", "Only gate");
        Assert.Equal(HttpStatusCode.OK, (await Patch(rte, $"/api/v1/release-types/{cert.GetProperty("id").GetString()}", new { defaultTemplateId = certTpl }, cert.GetProperty("version").GetInt32())).StatusCode);

        var fromType = await Json(await CreateTrain(rte, "R27.01 CERT", cert.GetProperty("id").GetString()));
        Assert.Equal(certTpl, fromType.GetProperty("templateId").GetString());
        Assert.Equal(2, fromType.GetProperty("created").GetProperty("gates").GetInt32());
        Assert.Equal(2, Count(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{fromType.GetProperty("id").GetString()}'"));
        Assert.Contains("\"fromReleaseType\":true", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{fromType.GetProperty("id").GetString()}' AND EntityType='ReleaseTrain' AND Action='Create'"));

        var explicitTpl = await Json(await CreateTrain(rte, "R27.02 CERT other", cert.GetProperty("id").GetString(), templateId: otherTpl));
        Assert.Equal(otherTpl, explicitTpl.GetProperty("templateId").GetString());
        Assert.Equal(1, explicitTpl.GetProperty("created").GetProperty("gates").GetInt32());

        var noDefault = await Json(await CreateTrain(rte, "R27.03 PenTest", pen.GetProperty("id").GetString()));   // a type without a default template: blank, as before
        Assert.Equal(JsonValueKind.Null, noDefault.GetProperty("templateId").ValueKind);
        Assert.Equal(0, noDefault.GetProperty("created").GetProperty("gates").GetInt32());

        // a default template that is no longer Approved is not used: the release starts blank, and says why
        Assert.Equal(HttpStatusCode.OK, (await rm.PostAsync($"/api/v1/templates/{certTpl}:retire", null)).StatusCode);
        var retired = await Json(await CreateTrain(rte, "R27.04 CERT retired", cert.GetProperty("id").GetString()));
        Assert.Equal(0, retired.GetProperty("created").GetProperty("gates").GetInt32());
        Assert.Contains("started blank", string.Join(" ", retired.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
        Assert.NotNull(prod.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_type_list_is_managed_by_planners_with_If_Match_and_every_write_is_audited()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var made = await rte.PostAsJsonAsync("/api/v1/release-types", new { name = "Hotfix" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var hotfix = await Json(made);
        var id = hotfix.GetProperty("id").GetString()!;
        Assert.Equal(1, hotfix.GetProperty("version").GetInt32());
        Assert.Equal(50, hotfix.GetProperty("sortOrder").GetInt32());   // after Production (40)
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(viewer, $"/api/v1/release-types/{id}", new { name = "x" }, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(rte, $"/api/v1/release-types/{id}", new { name = "Hot fix" }, 7)).StatusCode);
        var renamed = await Patch(rte, $"/api/v1/release-types/{id}", new { name = "Hot fix", sortOrder = 5 }, 1);
        Assert.Equal(2, (await Json(renamed)).GetProperty("version").GetInt32());
        Assert.Equal("Hot fix", (await Types(rte))["Hot fix"].GetProperty("name").GetString());
        Assert.Equal("Hot fix", (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().First().GetProperty("name").GetString());   // order 5 comes first
        var longName = await rte.PostAsJsonAsync("/api/v1/release-types", new { name = new string('x', 101) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longName.StatusCode);
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseType' AND EntityId='{id}' AND Action='Create'"));
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseType' AND EntityId='{id}' AND Action='Update'"));

        // Beta numbering is locked once releases use the type, in words, with the trigger behind it
        var prod = (await Types(rte))["Production"];
        Assert.Equal(HttpStatusCode.OK, (await CreateTrain(rte, "R27.01", prod.GetProperty("id").GetString())).StatusCode);
        var lock1 = await Patch(rte, $"/api/v1/release-types/{prod.GetProperty("id").GetString()}", new { isBeta = true }, prod.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lock1.StatusCode);
        Assert.Contains("cannot become a Beta type", (await Json(lock1)).GetProperty("message").GetString());
        var trig = Assert.ThrowsAny<SqliteException>(() => Sql(f, $"UPDATE ReleaseTypes SET IsBeta=1 WHERE Id='{prod.GetProperty("id").GetString()}'"));
        Assert.Contains("cannot become or stop being a Beta type", trig.Message);

        // Archiving the last active type is refused: no new release could be created
        foreach (var t in (await Types(rte)).Values.Where(t => t.GetProperty("id").GetString() != prod.GetProperty("id").GetString()))
            Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/release-types/{t.GetProperty("id").GetString()}:archive", null)).StatusCode);
        var last = await rte.PostAsync($"/api/v1/release-types/{prod.GetProperty("id").GetString()}:archive", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, last.StatusCode);
        Assert.Contains("At least one release type must stay active", (await Json(last)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Existing_releases_become_Production_at_start_each_audited_and_a_second_run_does_nothing()
    {
        using var f = new ApiFactory();   // types are not seeded: this is the database as the upgrade left it
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Sql(f, @"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('old1','R26.10','2026-10-30','2026-09-01T00:00:00Z','2026-09-01T00:00:00Z'),('old2','R26.11','2026-11-30','2026-09-02T00:00:00Z','2026-09-02T00:00:00Z')");
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM ReleaseTypes"));
        var svc = f.Services.GetRequiredService<ReleaseTypeService>();
        Assert.Equal(1, await svc.SeedAllAsync());   // one workspace changed

        var prod = Scalar(f, "SELECT Id FROM ReleaseTypes WHERE Name='Production'");
        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM ReleaseTrains WHERE ReleaseTypeId='{prod}'"));
        Assert.Equal("2", Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='old1'"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityId='old1' AND Action='ReleaseFields'"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseType' AND Action='Seed'"));
        Assert.Equal(0, await svc.SeedAllAsync());
        Assert.Equal("2", Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='old1'"));

        var rows = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/trains")).EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal("Production", r.GetProperty("type").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_workspace_that_renamed_Production_gets_a_Production_type_added_when_a_release_has_none()
    {
        using var f = new ApiFactory();
        await As(f, Roles.RTE, "rte@x.com");
        Sql(f, "INSERT INTO ReleaseTypes(Id,Name,SortOrder) VALUES('only','Live',10)");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId) VALUES('typed','R1','2026-10-30','2026-09-01T00:00:00Z','2026-09-01T00:00:00Z','only')");
        Sql(f, "DROP TRIGGER trg_Train_TypeRules_Insert");   // a legacy row that predates the type rule: the trigger would refuse it
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('old','R0','2026-10-30','2026-09-01T00:00:00Z','2026-09-01T00:00:00Z')");
        Assert.Equal(1, await f.Services.GetRequiredService<ReleaseTypeService>().SeedAllAsync());
        Assert.Equal("Production", Scalar(f, "SELECT t.Name FROM ReleaseTrains r JOIN ReleaseTypes t ON t.Id=r.ReleaseTypeId WHERE r.Id='old'"));
        Assert.Equal("only", Scalar(f, "SELECT ReleaseTypeId FROM ReleaseTrains WHERE Id='typed'"));   // a release that has a type keeps it
    }

    [Fact]
    public async Task A_new_workspace_starts_with_the_four_types()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var made = await rte.PostAsJsonAsync("/api/v1/workspaces", new { name = "Payments EMEA", slug = "payments-emea", region = "EU" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var wid = (await Json(made)).GetProperty("id").GetString()!;
        Assert.Equal(4, Count(f, $"SELECT COUNT(*) FROM ReleaseTypes WHERE WorkspaceId='{wid}'"));
        Assert.Equal("Beta", Scalar(f, $"SELECT Name FROM ReleaseTypes WHERE WorkspaceId='{wid}' AND IsBeta=1"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM ReleaseTypes WHERE WorkspaceId='default'"));   // the other workspace is untouched
        var types = await rte.GetFromJsonAsync<JsonElement>("/api/v1/w/payments-emea/release-types");
        Assert.Equal(new[] { "Beta", "CERT", "PenTest Cert", "Production" }, types.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        // and a release there needs a type from there
        var created = await rte.PostAsJsonAsync("/api/v1/w/payments-emea/trains", new { title = "R1", targetReleaseDate = Target(), riskTier = "Low" });
        Assert.Equal("ReleaseTypeRequired", (await Json(created)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task The_release_list_the_calendar_and_a_clone_follow_the_type()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var types = await Types(rte);
        string Id(string n) => types[n].GetProperty("id").GetString()!;
        async Task<string> Make(string title, string type, int? beta = null) => (await Json(await CreateTrain(rte, title, Id(type), beta))).GetProperty("id").GetString()!;
        var b1 = await Make("R27.01 Beta 1", "Beta", 1); var cert = await Make("R27.01 CERT", "CERT"); var prod = await Make("R27.01 Production", "Production");

        async Task<string[]> Titles(string url) => (await rte.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(r => r.GetProperty("title").GetString()!).Order().ToArray();
        Assert.Equal(new[] { "R27.01 Beta 1", "R27.01 CERT", "R27.01 Production" }, await Titles("/api/v1/trains"));
        Assert.Equal(new[] { "R27.01 Beta 1" }, await Titles($"/api/v1/trains?releaseTypeId={Id("Beta")}"));
        Assert.Equal(new[] { "R27.01 Production" }, await Titles($"/api/v1/trains?releaseTypeId={Id("Production")}"));
        Assert.Empty(await Titles($"/api/v1/trains?releaseTypeId={Id("PenTest Cert")}"));
        Assert.Empty(await Titles("/api/v1/trains?releaseTypeId=nope"));

        var from = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(120).ToString("yyyy-MM-dd");
        async Task<(string[] Trains, int Gates)> Cal(string? type)
        {
            var p = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={from}&to={to}{(type is null ? "" : "&releaseTypeId=" + type)}");
            return (p.GetProperty("trains").EnumerateArray().Select(t => t.GetProperty("title").GetString()!).Order().ToArray(), p.GetProperty("gates").GetArrayLength());
        }
        Assert.Equal(3, (await Cal(null)).Trains.Length);
        var onlyCert = await Cal(Id("CERT"));
        Assert.Equal(new[] { "R27.01 CERT" }, onlyCert.Trains);
        var calTrain = (await rte.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={from}&to={to}&releaseTypeId={Id("Beta")}")).GetProperty("trains")[0];
        Assert.Equal("Beta", calTrain.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(1, calTrain.GetProperty("betaNumber").GetInt32());
        Assert.Empty((await Cal(Id("PenTest Cert"))).Trains);

        // a clone keeps the type and the number
        var copy = await rte.PostAsJsonAsync($"/api/v1/trains/{b1}:clone", new { title = "R27.02 Beta 1 again", targetReleaseDate = Target(90) });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        var model = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{(await Json(copy)).GetProperty("id").GetString()}/release");
        Assert.Equal("Beta", model.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(1, model.GetProperty("betaNumber").GetInt32());
        Assert.NotNull(cert); Assert.NotNull(prod);
    }

    [Fact]
    public async Task The_trains_CSV_asks_for_a_type_on_a_new_release_and_a_number_on_a_Beta_one()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        async Task<JsonElement> Preview(string csv) =>
            await Json(await rte.PostAsync("/api/v1/imports/Trains:preview?mode=Append&fileName=trains.csv", new StringContent(csv, new System.Text.UTF8Encoding(false), "text/csv")));
        var d = Target();
        var noType = await Preview($"Title,TargetReleaseDate,RiskTier\nR1,{d},Low\n");
        Assert.Contains("A release needs a release type", noType.GetRawText());
        var betaNoNumber = await Preview($"Title,TargetReleaseDate,RiskTier,ReleaseType\nR2,{d},Low,Beta\n");
        Assert.Contains("needs its number, 1 to 3", betaNoNumber.GetRawText());
        var certNumber = await Preview($"Title,TargetReleaseDate,RiskTier,ReleaseType,BetaNumber\nR3,{d},Low,CERT,2\n");
        Assert.Contains("Only a Beta release carries a number", certNumber.GetRawText());
    }
}

/// <summary>REOS-116: every analytics metric limited to one release type. Two types split the seed's trains; each metric's two parts add up to the whole, and an unknown type gives nothing.</summary>
public class AnalyticsTypeFilterTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    private HttpClient? _client;
    private async Task<HttpClient> Client()
    {
        if (_client is not null) return _client;
        using (var db = new SqliteConnection($"Data Source={fx.SeedCopy};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ReleaseTypes(Id,Name) VALUES('tA','Type A'),('tB','Type B'); UPDATE ReleaseTrains SET ReleaseTypeId = CASE WHEN rowid % 2 = 0 THEN 'tA' ELSE 'tB' END WHERE WorkspaceId = 'default';";
            cmd.ExecuteNonQuery();
        }
        var c = fx.Web.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        using (var app = new SqliteConnection($"Data Source={Path.Combine(fx.Dir, "app.db")};Pooling=False"))   // the host's own database holds the type names the workbook shows
        {
            app.Open();
            using var cmd = app.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ReleaseTypes(Id,Name) VALUES('tA','Type A'),('tB','Type B')";
            cmd.ExecuteNonQuery();
        }
        return _client = c;
    }

    private async Task<JsonElement> Rows(string metric, string? type)
    {
        var p = fx.Expected.GetProperty("params");
        var q = $"from={p.GetProperty("from").GetString()}&to={p.GetProperty("to").GetString()}&now={Uri.EscapeDataString(p.GetProperty("now").GetString()!)}{(type is null ? "" : "&releaseTypeId=" + type)}";
        var r = await (await Client()).GetAsync($"/api/v1/analytics/{metric}?{q}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("rows");
    }

    [Fact]
    public async Task The_two_types_add_up_to_the_whole_for_the_release_level_metrics_and_an_unknown_type_is_empty()
    {
        foreach (var m in new[] { "M1", "M10" })   // aggregate rows: completed trains
        {
            var whole = (await Rows(m, null))[0].GetProperty("completed").GetInt64();
            var a = (await Rows(m, "tA"))[0].GetProperty("completed").GetInt64();
            var b = (await Rows(m, "tB"))[0].GetProperty("completed").GetInt64();
            Assert.True(whole > 0, $"{m}: the seed has completed trains");
            Assert.True(a > 0 && b > 0, $"{m}: both types have some");
            Assert.Equal(whole, a + b);
            Assert.Equal(0, (await Rows(m, "nope"))[0].GetProperty("completed").GetInt64());
        }
        foreach (var m in new[] { "M2", "M6", "M7", "M8" })   // one row per release or step
        {
            var whole = (await Rows(m, null)).GetArrayLength();
            Assert.Equal(whole, (await Rows(m, "tA")).GetArrayLength() + (await Rows(m, "tB")).GetArrayLength());
            Assert.Equal(0, (await Rows(m, "nope")).GetArrayLength());
        }
        var months = (await Rows("M15", null)).EnumerateArray().Sum(r => r.GetProperty("completed").GetInt64());
        Assert.Equal(months, (await Rows("M15", "tA")).EnumerateArray().Sum(r => r.GetProperty("completed").GetInt64()) + (await Rows("M15", "tB")).EnumerateArray().Sum(r => r.GetProperty("completed").GetInt64()));
    }

    [Fact]
    public async Task The_workbook_names_the_type_it_is_limited_to()
    {
        var c = await Client();
        var p = fx.Expected.GetProperty("params");
        var q = $"from={p.GetProperty("from").GetString()}&to={p.GetProperty("to").GetString()}&now={Uri.EscapeDataString(p.GetProperty("now").GetString()!)}";
        using var all = new ClosedXML.Excel.XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/xlsx?{q}"));
        Assert.Contains("None", all.Worksheet("Parameters").Cells().Select(x => x.GetString()).First(s => s.StartsWith("None: every metric")));
        using var typed = new ClosedXML.Excel.XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/xlsx?{q}&releaseTypeId=tA"));
        Assert.Contains(typed.Worksheet("Parameters").Cells().Select(x => x.GetString()), s => s.StartsWith("Release type: Type A."));
        using var one = new ClosedXML.Excel.XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/M1/xlsx?{q}&releaseTypeId=tA"));
        Assert.Contains("Type A", one.Worksheet("About").Cells().Select(x => x.GetString()));
    }
}
