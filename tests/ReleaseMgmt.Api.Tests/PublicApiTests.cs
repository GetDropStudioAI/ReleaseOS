using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-162 (API tokens), REOS-163 (conventions) and REOS-161 (the public API) over HTTP. Time is a frozen clock: expiry, the one-minute use stamp and the rate limits move only when a test moves it.
/// </summary>
public class PublicApiTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-05T08:00:00Z");
    private const string Pub = "/api/public/v1";

    private static ApiFactory Host(FakeTimeProvider clock, bool ifMatch = false, params (string, string)[] settings) =>
        new(requireIfMatch: ifMatch, settings: settings.ToDictionary(s => s.Item1, s => s.Item2)) { Clock = clock };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private sealed record Made(string Id, string Secret, JsonElement Body);

    private static async Task<Made> MakeToken(HttpClient admin, string name = "CI", string[]? scopes = null, DateTimeOffset? expires = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/api-tokens", new { name, scopes = scopes ?? ["read"], expiresAt = expires is null ? null : Iso(expires.Value) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await Json(r);
        return new(body.GetProperty("token").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!, body);
    }

    private static HttpClient Bearer(ApiFactory f, string secret)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return c;
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, object? body = null, string? ifMatch = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (ifMatch is not null) r.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (key is not null) r.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return r;
    }

    private static async Task<(HttpClient Admin, string RteId)> Setup(ApiFactory f)
    {
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        return (rte, id);
    }

    // ---- REOS-162: tokens ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_is_shown_once_and_nothing_recoverable_is_stored()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, _) = await Setup(f);
        var t = await MakeToken(rte, "Dashboard", ["read", "write"]);
        Assert.StartsWith("rt_", t.Secret);
        Assert.True(t.Secret.Length >= 40);
        Assert.Equal(t.Secret[..8], t.Body.GetProperty("token").GetProperty("tokenPrefix").GetString());
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(t.Secret)));
        Assert.Equal(hash, Scalar(f, "SELECT TokenSha256 FROM ApiTokens"));
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{t.Secret}%' OR AfterJson LIKE '%{hash}%' OR BeforeJson LIKE '%{t.Secret}%'"));
        var list = await rte.GetStringAsync("/api/v1/api-tokens");
        Assert.DoesNotContain(t.Secret, list); Assert.DoesNotContain(hash, list); Assert.DoesNotContain("\"secret\"", list);
        Assert.Contains("Dashboard", list);
        // the secret is in no byte of the database file or its write-ahead log
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { f.DbPath, f.DbPath + "-wal" }.Where(File.Exists))
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream(); s.CopyTo(ms);
            Assert.True(ms.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(t.Secret)) < 0, $"the token is readable in {Path.GetFileName(file)}");
            Assert.True(ms.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(t.Secret[3..])) < 0, $"the token's random part is readable in {Path.GetFileName(file)}");
        }
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Create'"));
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.GovernanceOfficer)]
    public async Task Only_an_RTE_or_Release_Manager_can_manage_tokens(string role)
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var made = await MakeToken(rte);
        var other = await As(f, role, role.ToLower() + "@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/api-tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/v1/api-tokens", new { name = "x", scopes = new[] { "read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{made.Id}:rotate"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{made.Id}:revoke"))).StatusCode);
    }

    [Fact]
    public async Task A_token_cannot_manage_tokens_or_reach_the_internal_api()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var t = await MakeToken(rte, scopes: ["read", "write", "approvals", "deployments"]);
        var c = Bearer(f, t.Secret);
        foreach (var url in new[] { "/api/v1/api-tokens", "/api/v1/trains", "/api/v1/me", "/api/v1/ingest-keys" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/ingest/deployments", new { externalId = "x" })).StatusCode);   // an API token is not an ingest key
    }

    [Fact]
    public async Task Creating_a_token_validates_its_name_scopes_and_expiry()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, _) = await Setup(f);
        async Task<string> Refused(object body) { var r = await rte.PostAsJsonAsync("/api/v1/api-tokens", body); Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
        Assert.Equal("ApiTokenInvalid", await Refused(new { name = "", scopes = new[] { "read" } }));
        Assert.Equal("ApiTokenInvalid", await Refused(new { name = "x", scopes = Array.Empty<string>() }));
        Assert.Equal("ApiTokenInvalid", await Refused(new { name = "x", scopes = new[] { "read", "admin" } }));
        Assert.Equal("ApiTokenInvalid", await Refused(new { name = "x", scopes = new[] { "read" }, expiresAt = Iso(Start.AddMinutes(-1)) }));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM ApiTokens"));
    }

    [Fact]
    public async Task A_revoked_token_is_refused_at_once_and_the_revoke_is_audited()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, rteId) = await Setup(f);
        var t = await MakeToken(rte);
        var c = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        var stale = await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:revoke", ifMatch: "7"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        Assert.DoesNotContain("tokenSha256", await stale.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var revoke = await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:revoke", ifMatch: "1"));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("Revoked", (await Json(revoke)).GetProperty("state").GetString());
        var after = await c.GetAsync(Pub + "/me");   // same instant: the limit is "within one minute", the lookup is on every request
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("application/problem+json", after.Content.Headers.ContentType!.MediaType);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Pub + "/me")).StatusCode);
        await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:revoke"));   // a repeat is a no-op
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Revoke'"));
        Assert.Equal(rteId, Scalar(f, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Revoke'"));
        Assert.True(Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'") >= 1, "the refused use of the revoked token is audited");
    }

    [Fact]
    public async Task Rotating_replaces_the_secret_and_hands_the_token_to_the_person_who_rotates()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, rteId) = await Setup(f);
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var t = await MakeToken(rte, "Dashboard", ["read"]);
        var old = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.OK, (await old.GetAsync(Pub + "/me")).StatusCode);
        var rotated = await rm.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:rotate"));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await Json(rotated);
        var fresh = body.GetProperty("secret").GetString()!;
        Assert.NotEqual(t.Secret, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync(Pub + "/me")).StatusCode);
        var me = await Bearer(f, fresh).GetAsync(Pub + "/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(Roles.ReleaseManager, await me.Content.ReadAsStringAsync());   // acts as who rotated it, with their role
        Assert.Equal(UserId(f, "rm@x.com"), Scalar(f, "SELECT OwnerUserId FROM ApiTokens"));
        Assert.Equal(2, int.Parse(Scalar(f, "SELECT Version FROM ApiTokens")));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Rotate'"));
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{fresh}%'"));
        Assert.NotEqual(rteId, Scalar(f, "SELECT OwnerUserId FROM ApiTokens"));
        // a revoked token cannot be rotated
        await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:revoke"));
        var again = await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:rotate"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("ApiTokenRevoked", (await Json(again)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_token_expires_on_the_clock_and_a_rotation_can_give_it_a_new_expiry()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, _) = await Setup(f);
        var t = await MakeToken(rte, expires: Start.AddHours(1));
        var c = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Pub + "/me")).StatusCode);
        var list = await Json(await rte.GetAsync("/api/v1/api-tokens"));
        Assert.Equal("Expired", list.GetProperty("tokens")[0].GetProperty("state").GetString());
        var noExpiry = await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:rotate"));
        Assert.Equal("ApiTokenExpired", (await Json(noExpiry)).GetProperty("guard").GetString());
        var ok = await rte.SendAsync(Req(HttpMethod.Post, $"/api/v1/api-tokens/{t.Id}:rotate", new { expiresAt = Iso(clock.GetUtcNow().AddDays(30)) }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(f, (await Json(ok)).GetProperty("secret").GetString()!).GetAsync(Pub + "/me")).StatusCode);
    }

    [Fact]
    public async Task Last_used_is_stamped_at_most_once_a_minute()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock);
        var (rte, _) = await Setup(f);
        var t = await MakeToken(rte);
        Assert.Equal("", Scalar(f, "SELECT COALESCE(LastUsedAt,'') FROM ApiTokens"));
        var c = Bearer(f, t.Secret);
        await c.GetAsync(Pub + "/me");
        Assert.Equal(Iso(Start), Scalar(f, "SELECT LastUsedAt FROM ApiTokens"));
        clock.Advance(TimeSpan.FromSeconds(30));
        await c.GetAsync(Pub + "/me");
        Assert.Equal(Iso(Start), Scalar(f, "SELECT LastUsedAt FROM ApiTokens"));
        clock.Advance(TimeSpan.FromSeconds(31));
        await c.GetAsync(Pub + "/me");
        Assert.Equal(Iso(Start.AddSeconds(61)), Scalar(f, "SELECT LastUsedAt FROM ApiTokens"));
    }

    [Fact]
    public async Task Unknown_malformed_and_missing_tokens_are_401_and_the_audit_rows_are_bounded()
    {
        using var f = Host(new FakeTimeProvider(Start));
        await Setup(f);
        var none = f.CreateClient();
        var r = await none.GetAsync(Pub + "/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("Bearer", r.Headers.WwwAuthenticate.ToString());
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Rejected'"));
        for (var i = 0; i < 25; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(f, "rt_" + new string('a', 40 + i % 3)).GetAsync(Pub + "/me")).StatusCode);
        var rejected = Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Rejected'");
        Assert.InRange(rejected, 1, 10);   // Ingest:RejectionAuditsPerMinute, the same budget the ingest keys use
        Assert.DoesNotContain("rt_aaaa", Scalar(f, "SELECT COALESCE(group_concat(AfterJson),'') FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Rejected'"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(f, "rk_" + new string('a', 43)).GetAsync(Pub + "/me")).StatusCode);   // an ingest key is not a token
    }

    [Fact]
    public async Task A_token_stops_when_the_person_it_acts_as_is_deactivated_or_leaves_the_workspace()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var t = await MakeToken(rte);
        var c = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        Sql(f, "UPDATE Users SET IsActive = 0 WHERE Email = 'rte@x.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Pub + "/me")).StatusCode);
        Sql(f, "UPDATE Users SET IsActive = 1 WHERE Email = 'rte@x.com'");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        Sql(f, "DELETE FROM WorkspaceMembers WHERE UserId = (SELECT Id FROM Users WHERE Email = 'rte@x.com')");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Pub + "/me")).StatusCode);
    }

    [Fact]
    public async Task The_workspace_is_the_tokens_and_the_list_shows_only_its_own()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, rteId) = await Setup(f);
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Other','other','2026-10-01T00:00:00Z');
                  INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','{rteId}','RTE','2026-10-01T00:00:00Z');
                  INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tw2','Other release','2026-11-30','High','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','w2');");
        var t = await MakeToken(rte, scopes: ["read"]);   // made in the default workspace
        var c = Bearer(f, t.Secret);
        var releases = await Json(await c.GetAsync(Pub + "/releases"));
        Assert.Equal(["t1"], releases.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Pub + "/releases/tw2")).StatusCode);   // a row of another workspace does not exist here
        c.DefaultRequestHeaders.Add("X-Workspace", "w2");   // the header cannot move a token
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Pub + "/releases/tw2")).StatusCode);
        var inW2 = await rte.GetAsync("/api/v1/w/other/api-tokens");
        Assert.Equal(0, (await Json(inW2)).GetProperty("tokens").GetArrayLength());
    }

    // ---- scopes and roles --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scope_violation_is_403_naming_the_scope_and_is_audited()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, rteId) = await Setup(f);
        var t = await MakeToken(rte, scopes: ["read"]);
        var c = Bearer(f, t.Secret);
        var r = await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var p = await Json(r);
        Assert.Equal("ScopeDenied", p.GetProperty("guard").GetString());
        Assert.Equal("write", p.GetProperty("requiredScope").GetString());
        Assert.Contains("'write'", p.GetProperty("detail").GetString());
        Assert.Equal(403, p.GetProperty("status").GetInt32());
        Assert.Equal("Pending", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));   // nothing ran
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'"));
        Assert.Equal(rteId, Scalar(f, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'"));
        Assert.Contains("write", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'"));
        // the four scopes are separate: approvals and deployments are not implied by write
        var w = Bearer(f, (await MakeToken(rte, "writer", ["read", "write"])).Secret);
        Assert.Equal("approvals", (await Json(await w.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g2:certify")))).GetProperty("requiredScope").GetString());
        Assert.Equal("deployments", (await Json(await w.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", new { externalId = "x" })))).GetProperty("requiredScope").GetString());
        Assert.Equal("read", (await Json(await Bearer(f, (await MakeToken(rte, "w", ["write"])).Secret).GetAsync(Pub + "/releases"))).GetProperty("requiredScope").GetString());
    }

    [Fact]
    public async Task The_role_of_the_person_still_applies_a_scope_never_widens_it()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        // the token is made by an RTE; the person's role is lowered afterwards
        var t = await MakeToken(rte, scopes: ["read", "write"]);
        var c = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start"))).StatusCode);
        Sql(f, "UPDATE WorkspaceMembers SET Role = 'Viewer' WHERE UserId = (SELECT Id FROM Users WHERE Email = 'rte@x.com')");   // the person's role now
        var r = await c.SendAsync(Req(HttpMethod.Post, Pub + "/releases/t1:abort", new { reason = "no" }));   // planners only
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var p = await Json(r);
        Assert.Equal("RoleDenied", p.GetProperty("guard").GetString());
        Assert.Equal("Planning", Scalar(f, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/releases")).StatusCode);   // reading is still allowed to a Viewer
        Assert.True(Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ApiToken' AND Action='Denied'") >= 1, "a denied use by role is audited");
    }

    [Fact]
    public async Task A_write_through_a_token_is_audited_with_the_person_as_actor_and_names_the_token()
    {
        using var f = Host(new FakeTimeProvider(Start), ifMatch: true);
        var (rte, rteId) = await Setup(f);
        var t = await MakeToken(rte, "Release bot", ["read", "write"]);
        var c = Bearer(f, t.Secret);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start"))).StatusCode);
        var ok = await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start", ifMatch: "1"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("InProgress", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));
        Assert.Equal(rteId, Scalar(f, "SELECT LastChangedByUserId FROM StageGates WHERE Id='g1'"));
        var rows = Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ActorUserId='{rteId}' AND AfterJson LIKE '%\"_via\":{{\"apiTokenId\":\"{t.Id}\",\"apiToken\":\"Release bot\"}}%'");
        Assert.True(rows >= 1, "the service's audit row names the token");
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ActorUserId='{rteId}' AND AfterJson LIKE '%{t.Secret}%'"));
        // the person's own click carries no token
        await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/gates/g1:fail", ifMatch: "2"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Gate' AND Action LIKE '%Fail%' AND AfterJson LIKE '%apiTokenId%'"));
    }

    // ---- REOS-163: conventions -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Errors_are_problem_details_and_keep_the_guard_the_failures_and_the_current_row()
    {
        using var f = Host(new FakeTimeProvider(Start), ifMatch: true);
        var (rte, _) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte, scopes: ["read", "write"])).Secret);
        // 422: a guard
        var illegal = await c.SendAsync(Req(HttpMethod.Post, Pub + "/releases/t1:advance", new { to = "Executing" }, ifMatch: "1"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, illegal.StatusCode);
        Assert.Equal("application/problem+json", illegal.Content.Headers.ContentType!.MediaType);
        var g = await Json(illegal);
        Assert.Equal("IllegalTransition", g.GetProperty("guard").GetString());
        Assert.Equal(g.GetProperty("message").GetString(), g.GetProperty("detail").GetString());
        Assert.Equal("urn:releaseos:problem:IllegalTransition", g.GetProperty("type").GetString());
        Assert.Equal("Unprocessable Entity", g.GetProperty("title").GetString());
        Assert.Equal(Pub + "/releases/t1:advance", g.GetProperty("instance").GetString());
        Assert.True(g.TryGetProperty("failures", out _));
        // 422 with the gates named, as the internal body has them
        var lockout = await Json(await c.SendAsync(Req(HttpMethod.Post, Pub + "/releases/t1:advance", new { to = "Gated" }, ifMatch: "1")));
        Assert.Equal("GateLockout", lockout.GetProperty("guard").GetString());
        Assert.Equal(["Code Freeze"], lockout.GetProperty("gates").EnumerateArray().Select(x => x.GetString()!).ToArray());
        // 409: the current row is inside the problem
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start", ifMatch: "1"))).StatusCode);
        var stale = await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:fail", ifMatch: "\"1\""));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await Json(stale);
        Assert.Equal("VersionConflict", conflict.GetProperty("guard").GetString());
        Assert.Equal(2, conflict.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal("InProgress", conflict.GetProperty("current").GetProperty("status").GetString());
        // 428 and 404
        var noMatch = await Json(await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:fail")));
        Assert.Equal("PreconditionRequired", noMatch.GetProperty("guard").GetString());
        var missing = await c.SendAsync(Req(HttpMethod.Get, Pub + "/releases/nope"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_database_rule_that_escapes_a_service_is_a_422_DbRule_problem()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte, scopes: ["read", "deployments"])).Secret);
        // A deployment for a service and an environment that do not exist is refused by the service; a rule the service does not know is the trigger's. Both come out as problems with a guard.
        var r = await c.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", new { externalId = "d1", serviceKey = "nope", environment = "Nowhere", releaseVersion = "1" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var p = await Json(r);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        Assert.False(string.IsNullOrEmpty(p.GetProperty("guard").GetString()));
        Assert.Equal(422, p.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Lists_are_pages_with_opaque_cursors()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var sql = new StringBuilder();
        for (var i = 1; i <= 6; i++) sql.Append($"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('p{i}','R{i}','2026-11-0{i}','High','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        Sql(f, sql.ToString());   // seven releases with t1
        var c = Bearer(f, (await MakeToken(rte)).Secret);
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{Pub}/releases?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await Json(r);
            var items = body.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
            Assert.True(items.Count <= 3);
            seen.AddRange(items);
            var page = body.GetProperty("page");
            Assert.Equal(3, page.GetProperty("limit").GetInt32());
            cursor = page.GetProperty("nextCursor").GetString();
            Assert.Equal(cursor is not null, page.GetProperty("hasMore").GetBoolean());
            pages++;
        } while (cursor is not null);
        Assert.Equal(3, pages);
        Assert.Equal(7, seen.Count); Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(["t1", "p1", "p2", "p3", "p4", "p5", "p6"], seen.OrderBy(x => x == "t1" ? "" : x).ToArray().Length == 7 ? seen.ToArray() : []);   // the internal order (target date), kept
        // refusals
        async Task<string> Refused(string url) { var r = await c.GetAsync(url); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
        Assert.Equal("InvalidLimit", await Refused(Pub + "/releases?limit=0"));
        Assert.Equal("InvalidLimit", await Refused(Pub + "/releases?limit=201"));
        Assert.Equal("InvalidLimit", await Refused(Pub + "/releases?limit=abc"));
        Assert.Equal("InvalidCursor", await Refused(Pub + "/releases?cursor=not-a-cursor"));
        var first = await Json(await c.GetAsync(Pub + "/releases?limit=2"));
        var cur = first.GetProperty("page").GetProperty("nextCursor").GetString()!;
        Assert.Equal("InvalidCursor", await Refused(Pub + "/releases?limit=2&releaseTypeId=x&cursor=" + Uri.EscapeDataString(cur)));   // another filter
        // default size
        var all = await Json(await c.GetAsync(Pub + "/releases"));
        Assert.Equal(50, all.GetProperty("page").GetProperty("limit").GetInt32());
        Assert.Equal(7, all.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("page").GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_cursor_to_a_record_that_has_gone_is_refused_not_guessed()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('p1','R1','2026-11-01','High','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),('p2','R2','2026-11-02','High','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var c = Bearer(f, (await MakeToken(rte)).Secret);
        var first = await Json(await c.GetAsync(Pub + "/releases?limit=1"));
        var cursor = first.GetProperty("page").GetProperty("nextCursor").GetString()!;
        var last = first.GetProperty("data")[0].GetProperty("id").GetString()!;
        Sql(f, $"DELETE FROM ReleaseTrains WHERE Id='{last}'");
        var r = await c.GetAsync(Pub + "/releases?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("InvalidCursor", (await Json(r)).GetProperty("guard").GetString());
    }

    private static async Task<(ApiFactory F, HttpClient Rte, HttpClient Tool)> DeploymentSetup(FakeTimeProvider clock, params (string, string)[] settings)
    {
        var f = Host(clock, false, settings);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API" })).StatusCode);
        return (f, rte, Bearer(f, (await MakeToken(rte, "CI", ["read", "deployments"])).Secret));
    }

    private static object Dep(string ext = "ci-100", string version = "4.5.0") => new { externalId = ext, serviceKey = "payments-api", environment = "Production", releaseVersion = version };

    [Fact]
    public async Task A_deployment_recorded_with_a_token_is_one_row_and_its_audit_names_the_token()
    {
        var (f, rte, tool) = await DeploymentSetup(new FakeTimeProvider(Start));
        using var host = f;
        var r = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep()));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep()))).StatusCode);   // the tool's own id: the same deployment
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Deployments"));
        Assert.Equal("", Scalar(f, "SELECT COALESCE(IngestKeyId,'') FROM Deployments"));   // made by a token, not an ingest key
        Assert.Contains("apiToken", Scalar(f, "SELECT group_concat(AfterJson) FROM AuditEvents WHERE EntityType='Deployment'"));
        var events = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments/events", new { deploymentExternalId = "ci-100", events = new[] { new { level = "Info", message = "Started", externalId = "e1" } } }));
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal(1, (await Json(events)).GetProperty("accepted").GetInt32());
        // the ingest services write with no actor; under a token the person it acts as is the audit actor
        var rteId = UserId(f, "rte@x.com");
        Assert.Equal(rteId, Scalar(f, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='DeploymentEvent'"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType IN ('Deployment','DeploymentEvent') AND ActorUserId IS NULL"));
        var list = await Json(await tool.GetAsync(Pub + "/deployments"));
        Assert.Equal(1, list.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task A_repeated_POST_with_the_same_Idempotency_Key_returns_the_first_result_and_writes_once()
    {
        var (f, _, tool) = await DeploymentSetup(new FakeTimeProvider(Start));
        using var host = f;
        var first = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-7"), key: "job-7"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var audits = Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Deployment'");
        Assert.Equal(1, audits);
        var again = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-7"), key: "job-7"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);   // the original status, not the 200 a replay of the id would give
        Assert.Equal(firstBody, await again.Content.ReadAsStringAsync());
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Deployments"));
        Assert.Equal(audits, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Deployment'"));   // one audit row
        // the same key for another request is refused, and nothing runs
        var other = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-8"), key: "job-7"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal("IdempotencyKeyReused", (await Json(other)).GetProperty("guard").GetString());
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Deployments"));
        // a new key is a new request
        Assert.Equal(HttpStatusCode.Created, (await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-8"), key: "job-8"))).StatusCode);
        Assert.Equal(2, Count(f, "SELECT COUNT(*) FROM Deployments"));
        // the key belongs to the token: another token may use the same text
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var tool2 = Bearer(f, (await MakeToken(rte, "CI 2", ["deployments"])).Secret);
        Assert.Equal(HttpStatusCode.Created, (await tool2.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-9"), key: "job-7"))).StatusCode);
        // bad keys
        foreach (var bad in new[] { new string('k', 256), "has space", "caf\u00e9" })
        {
            var rr = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-10"), key: bad));
            Assert.Equal(HttpStatusCode.BadRequest, rr.StatusCode);
            Assert.Equal("IdempotencyKeyInvalid", (await Json(rr)).GetProperty("guard").GetString());
        }
    }

    [Fact]
    public async Task A_failed_request_does_not_keep_its_Idempotency_Key_so_the_retry_runs()
    {
        var (f, _, tool) = await DeploymentSetup(new FakeTimeProvider(Start));
        using var host = f;
        var bad = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", new { externalId = "ci-1", serviceKey = "nope", environment = "Production", releaseVersion = "1" }, key: "retry-me"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM IdempotencyKeys"));
        var good = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", new { externalId = "ci-1", serviceKey = "payments-api", environment = "Production", releaseVersion = "1" }, key: "retry-me"));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM IdempotencyKeys"));
    }

    [Fact]
    public async Task Idempotency_keys_expire_on_the_clock()
    {
        var clock = new FakeTimeProvider(Start);
        var (f, _, tool) = await DeploymentSetup(clock);
        using var host = f;
        Assert.Equal(HttpStatusCode.Created, (await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-1"), key: "k"))).StatusCode);
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal("true", (await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-1"), key: "k"))).Headers.GetValues("Idempotent-Replayed").Single());
        clock.Advance(TimeSpan.FromHours(2));
        var after = await tool.SendAsync(Req(HttpMethod.Post, Pub + "/deployments", Dep("ci-1"), key: "k"));
        Assert.False(after.Headers.Contains("Idempotent-Replayed"));   // forgotten: it ran again (and the tool's own id made it a no-op: 200)
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Requests_beyond_the_limit_get_429_with_Retry_After_and_the_clock_refills_the_bucket()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock, false, ("PublicApi:TokenBurst", "3"), ("PublicApi:TokenRequestsPerSecond", "1"), ("PublicApi:WorkspaceBurst", "100"));
        var (rte, _) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte)).Secret);
        var limits = new List<(int Limit, int Remaining)>();
        for (var i = 0; i < 3; i++)
        {
            var ok = await c.GetAsync(Pub + "/me");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            limits.Add((int.Parse(ok.Headers.GetValues("X-RateLimit-Limit").Single()), int.Parse(ok.Headers.GetValues("X-RateLimit-Remaining").Single())));
            Assert.True(ok.Headers.Contains("X-RateLimit-Reset"));
        }
        Assert.Equal([(3, 2), (3, 1), (3, 0)], limits);
        var limited = await c.GetAsync(Pub + "/me");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("1", limited.Headers.GetValues("Retry-After").Single());
        Assert.Equal("0", limited.Headers.GetValues("X-RateLimit-Remaining").Single());
        var p = await Json(limited);
        Assert.Equal("RateLimited", p.GetProperty("guard").GetString());
        Assert.Equal("token", p.GetProperty("limit").GetString());
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
        Assert.Equal((HttpStatusCode)429, (await c.GetAsync(Pub + "/me")).StatusCode);   // a frozen clock refills nothing
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/me")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await c.GetAsync(Pub + "/me")).StatusCode);
        // a token's limit is its own: another token is not slowed
        var other = Bearer(f, (await MakeToken(rte, "other")).Secret);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(Pub + "/me")).StatusCode);
    }

    [Fact]
    public async Task The_workspace_has_a_limit_too_and_it_is_shared_by_its_tokens()
    {
        var clock = new FakeTimeProvider(Start);
        using var f = Host(clock, false, ("PublicApi:WorkspaceBurst", "4"), ("PublicApi:WorkspaceRequestsPerSecond", "1"));
        var (rte, _) = await Setup(f);
        var a = Bearer(f, (await MakeToken(rte, "a")).Secret);
        var b = Bearer(f, (await MakeToken(rte, "b")).Secret);
        for (var i = 0; i < 2; i++) { Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(Pub + "/me")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await b.GetAsync(Pub + "/me")).StatusCode); }
        var r = await a.GetAsync(Pub + "/me");
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Equal("workspace", (await Json(r)).GetProperty("limit").GetString());
        Assert.Equal("1", r.Headers.GetValues("Retry-After").Single());
        Assert.Equal((HttpStatusCode)429, (await b.GetAsync(Pub + "/me")).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync(Pub + "/me")).StatusCode);
    }

    // ---- REOS-161: the surface -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_outside_the_subset_is_reachable_with_a_token()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte, scopes: ["read", "write", "approvals", "deployments"])).Secret);
        foreach (var path in new[] { "/users", "/audit", "/ingest-keys", "/api-tokens", "/workspaces", "/templates", "/trains", "/me/ics-tokens", "/connectors", "/imports", "/exports/trains.csv", "/ingest/deployments", "/admin", "/releases/t1/window", "/releases/t1/change-record" })
        {
            var r = await c.GetAsync(Pub + path);
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"GET {path} answered {(int)r.StatusCode}");
            Assert.Equal("NotInPublicApi", (await Json(r)).GetProperty("guard").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/users", new { }))).StatusCode);
        // a path the API has, with a method it does not: 405 and what it does accept. No PATCH, PUT or DELETE anywhere, so no status can be set directly.
        foreach (var m in new[] { HttpMethod.Patch, HttpMethod.Put, HttpMethod.Delete })
        {
            var r = await c.SendAsync(Req(m, Pub + "/releases/t1", new { currentStatus = "Complete", targetReleaseDate = "2026-12-01" }));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
            Assert.Equal("GET", r.Content.Headers.Allow.Single());
            Assert.Equal("MethodNotAllowed", (await Json(r)).GetProperty("guard").GetString());
        }
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.GetAsync(Pub + "/releases/t1:advance")).StatusCode);
        Assert.Equal("Planning", Scalar(f, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
        // cookies and workspace headers are not honoured for a token request
        var viaCookie = f.CreateClient();
        (await viaCookie.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await viaCookie.GetAsync(Pub + "/me")).StatusCode);   // a session is not a token
    }

    [Fact]
    public async Task The_read_surface_answers_like_the_internal_api()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var (rte, _) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte)).Secret);
        var me = await Json(await c.GetAsync(Pub + "/me"));
        Assert.Equal("rte@x.com", me.GetProperty("email").GetString());
        Assert.Equal("default", me.GetProperty("workspaceId").GetString());
        Assert.Equal([Roles.RTE], me.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var rel = await Json(await c.GetAsync(Pub + "/releases/t1"));
        var internalRel = await Json(await rte.GetAsync("/api/v1/trains/t1"));
        Assert.Equal(internalRel.ToString(), rel.ToString());   // the same handler, the same body
        Assert.Equal((await rte.GetStringAsync("/api/v1/trains/t1/readiness")), await c.GetStringAsync(Pub + "/releases/t1/readiness"));
        Assert.Equal((await rte.GetStringAsync("/api/v1/gates/g1")), await c.GetStringAsync(Pub + "/gates/g1"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/calendar?from=2026-10-01&to=2026-10-31")).StatusCode);
        var metrics = await Json(await c.GetAsync(Pub + "/analytics"));
        Assert.True(metrics.GetArrayLength() >= 24);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/analytics/M1?from=2026-01-01&to=2026-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/approval-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/releases/t1/approval-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Pub + "/releases/t1/deployments")).StatusCode);
    }

    [Fact]
    public async Task A_status_change_is_a_service_action_and_runs_the_same_rules_and_audit()
    {
        using var f = Host(new FakeTimeProvider(Start), ifMatch: true);
        var (rte, rteId) = await Setup(f);
        var c = Bearer(f, (await MakeToken(rte, scopes: ["read", "write"])).Secret);
        var before = Count(f, "SELECT COUNT(*) FROM AuditEvents");
        var r = await c.SendAsync(Req(HttpMethod.Post, Pub + "/releases/t1:advance", new { to = "Gated" }, ifMatch: "1"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);   // GateLockout, as internally
        Assert.Equal(before, Count(f, "SELECT COUNT(*) FROM AuditEvents"));
        Assert.Equal("Planning", Scalar(f, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
        // finish the gate by the same rules, through the token, then advance
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:start", ifMatch: "1"))).StatusCode);
        Assert.Equal(rteId, Scalar(f, "SELECT LastChangedByUserId FROM StageGates WHERE Id='g1'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Req(HttpMethod.Post, Pub + "/gates/g1:certify", ifMatch: "2"))).StatusCode);   // certify is the approvals scope
    }

    [Fact]
    public async Task The_OpenAPI_document_is_served_without_a_token_and_matches_the_committed_copy()
    {
        using var f = Host(new FakeTimeProvider(Start));
        var r = await f.CreateClient().GetAsync(Pub + "/openapi.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var served = await r.Content.ReadAsStringAsync();
        var committed = File.ReadAllText(PublicApiContractTests.DocPath);
        Assert.True(PublicApiContractTests.Lf(served) == PublicApiContractTests.Lf(committed), "docs/api/public-v1.openapi.json is out of date: run `UPDATE_OPENAPI=1 dotnet test --filter PublicApiContractTests` and commit it");
        var doc = JsonDocument.Parse(served).RootElement;
        Assert.Equal("3.1.0", doc.GetProperty("openapi").GetString());
        Assert.Equal(Pub, doc.GetProperty("servers")[0].GetProperty("url").GetString());
    }
}
