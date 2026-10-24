using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-107: a release's key, owner, team, branch, on-call, type and target environments, over HTTP: set at creation or clone, edited by planners with If-Match, audited, per workspace.</summary>
public class ReleaseModelTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static long Audits(ApiFactory f, string entityId, string action) => long.Parse(Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{entityId}' AND Action='{action}'"));

    /// <summary>REOS-116: a host that seeds the release types, so a release must name one (the others start without types, as a database opened before the seeding).</summary>
    private static readonly Dictionary<string, string> Seeded = new() { ["ReleaseTypes:SeedAtStart"] = "true" };

    private static async Task<string> Create(HttpClient c, string title, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["targetReleaseDate"] = Target(), ["riskTier"] = "Low" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        if (!body.ContainsKey("releaseTypeId") && (await c.GetFromJsonAsync<JsonElement>("/api/v1/release-types")) is { } types && types.GetArrayLength() > 0)
            body["releaseTypeId"] = types.EnumerateArray().First(x => x.GetProperty("name").GetString() == "Production").GetProperty("id").GetString();
        var r = await c.PostAsJsonAsync("/api/v1/trains", body);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> Patch(HttpClient c, string url, object body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<string> Env(HttpClient c, string name, string kind = "Staging") =>
        (await Json(await c.PostAsJsonAsync("/api/v1/environments", new { name, kind }))).GetProperty("id").GetString()!;

    [Fact]
    public async Task Releases_get_keys_in_order_and_the_existing_create_flow_works_without_the_new_fields()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var a = await Create(rte, "R27.01"); var b = await Create(rte, "R27.02");
        Assert.Equal("RLS-1", (await Json(await rte.GetAsync($"/api/v1/trains/{a}/release"))).GetProperty("key").GetString());
        Assert.Equal("RLS-2", (await Json(await rte.GetAsync($"/api/v1/trains/{b}/release"))).GetProperty("key").GetString());
        var rel = await Json(await rte.GetAsync($"/api/v1/trains/{a}/release"));
        Assert.Equal(JsonValueKind.Null, rel.GetProperty("owner").ValueKind);
        Assert.Equal(0, rel.GetProperty("environments").GetArrayLength());
        Assert.Contains("RLS-1", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{a}' AND EntityType='ReleaseTrain' AND Action='Create'"));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/trains/nope/release")).StatusCode);
    }

    [Fact]
    public async Task The_release_list_carries_the_key_owner_team_type_and_environments_of_every_row_for_any_role()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rmId = UserId(f, "rte@x.com");
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Operations')");
        var stg = await Env(rte, "Staging"); var prod = await Env(rte, "Production", "Production");
        var certType = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().First(x => x.GetProperty("name").GetString() == "CERT").GetProperty("id").GetString()!;
        var full = await Create(rte, "R27.01", new { OwnerUserId = rmId, OwnerTeamId = "tm1", ReleaseTypeId = certType, EnvironmentIds = new[] { stg, prod } });
        var bare = await Create(rte, "R27.02");

        var rows = (await viewer.GetFromJsonAsync<JsonElement>("/api/v1/trains")).EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        var a = rows[full];
        Assert.Equal("RLS-1", a.GetProperty("key").GetString());
        Assert.Equal("rte", a.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("ops", a.GetProperty("ownerTeam").GetProperty("handle").GetString());
        Assert.Equal("CERT", a.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(new[] { "Staging", "Production" }, a.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());
        var b = rows[bare];   // nothing recorded is null / empty, never invented
        Assert.Equal("RLS-2", b.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("owner").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("ownerTeam").ValueKind);
        Assert.Equal("Production", b.GetProperty("type").GetProperty("name").GetString());   // REOS-116: every release has a type; the helper names Production when a test names none
        Assert.Equal(0, b.GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public async Task A_key_is_never_reused_even_after_an_archive_and_the_prefix_is_a_workspace_setting()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var a = await Create(rte, "R27.01");
        Sql(f, $"UPDATE ReleaseTrains SET ArchivedAt='2026-10-01T00:00:00Z' WHERE Id='{a}'");
        var b = await Create(rte, "R27.02");
        Assert.Equal("RLS-2", Scalar(f, $"SELECT ReleaseKey FROM ReleaseTrains WHERE Id='{b}'"));

        var ws = await Json(await rte.GetAsync("/api/v1/workspace"));
        Assert.Equal("RLS", ws.GetProperty("keyPrefix").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(viewer, "/api/v1/workspace", new { keyPrefix = "ABC" }, 1)).StatusCode);
        var bad = await Patch(rte, "/api/v1/workspace", new { keyPrefix = "a" }, ws.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("KeyPrefixInvalid", (await Json(bad)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(rte, "/api/v1/workspace", new { keyPrefix = "ABC" }, 99)).StatusCode);
        var ok = await Patch(rte, "/api/v1/workspace", new { keyPrefix = "abc" }, ws.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("ABC", (await Json(ok)).GetProperty("keyPrefix").GetString());
        Assert.Equal(1, Audits(f, "default", "KeyPrefix"));

        var c = await Create(rte, "R27.03");
        Assert.Equal("ABC-3", Scalar(f, $"SELECT ReleaseKey FROM ReleaseTrains WHERE Id='{c}'"));
        Assert.Equal("RLS-1", Scalar(f, $"SELECT ReleaseKey FROM ReleaseTrains WHERE Id='{a}'"));   // existing keys keep their prefix
    }

    [Fact]
    public async Task Owner_team_branch_on_call_type_and_environments_are_set_at_creation_edited_with_If_Match_and_audited()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var (rteId, rmId, vId) = (UserId(f, "rte@x.com"), UserId(f, "rm@x.com"), UserId(f, "v@x.com"));
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Operations'),('tm2','sre','SRE')");
        var stg = await Env(rte, "Staging"); var prod = await Env(rte, "Production", "Production");
        var certType = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().First(x => x.GetProperty("name").GetString() == "CERT").GetProperty("id").GetString()!;

        var id = await Create(rte, "R27.01", new { OwnerUserId = rmId, OwnerTeamId = "tm1", ReleaseBranch = "release/27.01", OnCallTeamId = "tm2", ReleaseTypeId = certType, EnvironmentIds = new[] { stg, prod } });
        var rel = await Json(await rte.GetAsync($"/api/v1/trains/{id}/release"));
        Assert.Equal("rm", rel.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("Operations", rel.GetProperty("ownerTeam").GetProperty("name").GetString());
        Assert.Equal("release/27.01", rel.GetProperty("branch").GetString());
        Assert.Equal("SRE", rel.GetProperty("onCallTeam").GetProperty("name").GetString());
        Assert.Equal("CERT", rel.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(new[] { "Staging", "Production" }, rel.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());

        // Edit: a planner, with the train's Version
        var v = rel.GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(viewer, $"/api/v1/trains/{id}/release", new { releaseBranch = "x" }, v)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(rm, $"/api/v1/trains/{id}/release", new { releaseBranch = "x" }, v + 5)).StatusCode);
        var edited = await Patch(rm, $"/api/v1/trains/{id}/release", new { ownerUserId = rteId, releaseBranch = "release/27.01-hotfix", onCallUserId = vId, environmentIds = new[] { stg } }, v);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var after = await Json(edited);
        Assert.Equal("rte", after.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("v", after.GetProperty("onCallUser").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("onCallTeam").ValueKind);   // naming the on-call person replaces the on-call team
        Assert.Equal(1, after.GetProperty("environments").GetArrayLength());
        Assert.Equal(v + 1, after.GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(f, id, "ReleaseFields"));
        Assert.Equal(UserId(f, "rm@x.com"), Scalar(f, $"SELECT LastChangedByUserId FROM ReleaseTrains WHERE Id='{id}'"));
        var audit = Scalar(f, $"SELECT BeforeJson || ' => ' || AfterJson FROM AuditEvents WHERE EntityId='{id}' AND Action='ReleaseFields'");
        Assert.Contains("release/27.01", audit); Assert.Contains("release/27.01-hotfix", audit);

        // A repeat changes nothing: no Version bump, no audit row
        var again = await Patch(rm, $"/api/v1/trains/{id}/release", new { releaseBranch = "release/27.01-hotfix" }, v + 1);
        Assert.Equal(v + 1, (await Json(again)).GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(f, id, "ReleaseFields"));

        // Clearing: an empty string clears an id or the branch, an empty list clears the environments
        var cleared = await Json(await Patch(rm, $"/api/v1/trains/{id}/release", new { ownerTeamId = "", releaseBranch = "", environmentIds = Array.Empty<string>() }, v + 1));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("ownerTeam").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("branch").ValueKind);
        Assert.Equal(0, cleared.GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public async Task Fields_that_name_something_outside_the_workspace_or_an_archived_choice_are_refused_readably()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var outsider = await As(f, Roles.RTE, "outsider@x.com");
        var outsiderId = UserId(f, "outsider@x.com");
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Two','two','2026-10-01T00:00:00Z');
                 UPDATE WorkspaceMembers SET WorkspaceId='w2' WHERE UserId='{outsiderId}';
                 INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tmx','other','Other','w2'),('tm1','ops','Ops','default');
                 INSERT INTO Environments(Id,Name,Kind,CreatedAt,WorkspaceId) VALUES('envx','Elsewhere','Staging','2026-10-01T00:00:00Z','w2');");
        var stg = await Env(rte, "Staging");
        var id = await Create(rte, "R27.01");
        var v = (await Json(await rte.GetAsync($"/api/v1/trains/{id}/release"))).GetProperty("version").GetInt32();

        async Task<string> GuardOf(object body)
        {
            var r = await Patch(rte, $"/api/v1/trains/{id}/release", body, v);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return (await Json(r)).GetProperty("guard").GetString()!;
        }
        Assert.Equal("ReleaseInvalid", await GuardOf(new { ownerUserId = outsiderId }));            // not a member of this workspace
        Assert.Equal("ReleaseInvalid", await GuardOf(new { ownerTeamId = "tmx" }));                 // another workspace's team
        Assert.Equal("EnvironmentInvalid", await GuardOf(new { environmentIds = new[] { "envx" } }));
        Assert.Equal("ReleaseInvalid", await GuardOf(new { onCallUserId = UserId(f, "rte@x.com"), onCallTeamId = "tm1" }));   // a person or a team, not both
        Assert.Equal("FieldTooLong", await GuardOf(new { releaseBranch = new string('b', 201) }));
        Assert.Equal("ReleaseTypeInvalid", await GuardOf(new { releaseTypeId = "nope" }));
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/environments/{stg}:archive", null)).StatusCode);
        Assert.Equal("EnvironmentInvalid", await GuardOf(new { environmentIds = new[] { stg } }));   // archived: not for a new choice
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/trains/{id}/release")).StatusCode);   // another workspace's release does not exist for them
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{id}' AND Action='ReleaseFields'"));   // nothing was written by the refusals
    }

    [Fact]
    public async Task A_clone_keeps_the_team_branch_on_call_type_and_environments_and_gets_a_new_key()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Operations')");
        var stg = await Env(rte, "Staging");
        var src = await Create(rte, "R27.01", new { OwnerTeamId = "tm1", ReleaseBranch = "release/27.01", EnvironmentIds = new[] { stg } });
        var r = await rte.PostAsJsonAsync($"/api/v1/trains/{src}:clone", new { title = "R27.02", targetReleaseDate = Target(90) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var copy = (await Json(r)).GetProperty("id").GetString()!;
        var rel = await Json(await rte.GetAsync($"/api/v1/trains/{copy}/release"));
        Assert.Equal("RLS-2", rel.GetProperty("key").GetString());
        Assert.Equal("release/27.01", rel.GetProperty("branch").GetString());
        Assert.Equal("Operations", rel.GetProperty("ownerTeam").GetProperty("name").GetString());
        Assert.Equal(1, rel.GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public async Task Release_types_are_seeded_per_workspace_managed_by_planners_and_a_types_default_template_starts_the_release()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var types = await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types");
        Assert.Equal(new[] { "Beta", "CERT", "PenTest Cert", "Production" }, types.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/release-types", new { name = "Hotfix" })).StatusCode);
        var dup = await rte.PostAsJsonAsync("/api/v1/release-types", new { name = "cert" });
        Assert.Equal("ReleaseTypeNameTaken", (await Json(dup)).GetProperty("guard").GetString());

        // An Approved template as a type's default: a release of that type starts from it when none is named
        var tpl = await rte.PostAsJsonAsync("/api/v1/templates", new
        {
            name = "CERT release", defaultRiskTier = "High",
            gates = new[] { new { gateName = "Code Freeze", gateClass = "Standard", offsetDays = 5, requiredBeforeStatus = "Gated" } },
            steps = Array.Empty<object>(), schedule = Array.Empty<object>(),
        });
        var tplId = (await Json(tpl)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await rm.PostAsync($"/api/v1/templates/{tplId}:approve", null)).StatusCode);
        var cert = types.EnumerateArray().First(x => x.GetProperty("name").GetString() == "CERT");
        var set = await Patch(rte, $"/api/v1/release-types/{cert.GetProperty("id").GetString()}", new { defaultTemplateId = tplId }, cert.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("CERT release", (await Json(set)).GetProperty("defaultTemplate").GetProperty("name").GetString());

        var id = await Create(rte, "R27.05", new { ReleaseTypeId = cert.GetProperty("id").GetString() });
        Assert.Equal(tplId, Scalar(f, $"SELECT TemplateId FROM ReleaseTrains WHERE Id='{id}'"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{id}'"));

        // Archived types leave the list (and cannot be chosen) but keep their releases
        var archived = await rte.PostAsync($"/api/v1/release-types/{cert.GetProperty("id").GetString()}:archive", null);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.DoesNotContain("CERT", await rte.GetStringAsync("/api/v1/release-types"));
        Assert.Contains("CERT", await rte.GetStringAsync("/api/v1/release-types?includeArchived=true"));
        var refuse = await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.06", targetReleaseDate = Target(), riskTier = "Low", releaseTypeId = cert.GetProperty("id").GetString() });
        Assert.Equal("ReleaseTypeInvalid", (await Json(refuse)).GetProperty("guard").GetString());
        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseType' AND EntityId='{cert.GetProperty("id").GetString()}' AND Action IN ('Update','Archive')"));
    }
}
