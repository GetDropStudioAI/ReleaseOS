using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-53 antiforgery review (Q-053b). Authentication is a cookie, so a browser on another site could make a signed-in user's browser send a state change.
/// Defences under test: the cookie is HttpOnly and SameSite=Lax; <see cref="Auth.CrossSiteRequestGuard"/> refuses any state-changing request under /api, /auth and /hub
/// that a browser says came from another site; security headers stop framing and sniffing; a deactivated user's cookie stops working.
/// </summary>
public class CsrfTests
{
    private const string Evil = "https://evil.example";

    private static HttpRequestMessage Req(HttpMethod m, string url, string? origin = null, string? fetchSite = null, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body ?? (m == HttpMethod.Get ? null : new StringContent("{}", Encoding.UTF8, "application/json")) };
        if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (fetchSite is not null) r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        return r;
    }

    private static async Task<HttpClient> Login(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f, string role, string email)
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_and_SameSite_Lax()
    {
        using var f = new ApiFactory();
        var res = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "a@x.com", name = "A", role = Roles.RTE });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("releasemgmt.auth=")).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("expires=", cookie);   // a session cookie, not a persistent one (the ticket itself still expires server-side)
    }

    [Fact]
    public async Task A_cross_site_state_change_is_refused_and_changes_nothing()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));

        foreach (var (origin, site) in new (string?, string?)[] { (Evil, "cross-site"), (Evil, null), (null, "cross-site"), (Evil, "same-site"), ("null", null), ("file:///x.html", null), ("http://localhost.evil.example", null) })
        {
            var res = await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/gates/g1:start", origin, site));
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"Origin={origin} Sec-Fetch-Site={site} gave {(int)res.StatusCode}");
            Assert.Contains("CrossSiteRequest", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal("Pending", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));   // the write never reached the handler
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityId='g1'"));

        // The same request from this site, or from a client that is not a browser, goes through.
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/gates/g1:start", origin: "http://localhost", fetchSite: "same-origin"))).StatusCode);
    }

    [Theory]
    [InlineData(null, null)]                       // curl, a service, a test client: no browser headers, so no ambient-cookie risk
    [InlineData(null, "same-origin")]
    [InlineData(null, "none")]
    [InlineData("http://localhost", null)]         // the test host's own origin (Host: localhost)
    [InlineData("http://LOCALHOST", "same-origin")]
    [InlineData(Evil, "same-origin")]              // a proxy that rewrote Host: the browser's own statement wins
    public async Task Same_site_and_non_browser_requests_pass(string? origin, string? site)
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var res = await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/notifications/x-1:read", origin, site));
        Assert.NotEqual(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Reads_are_not_affected_and_allowed_origins_can_be_configured()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Get, "/api/v1/me", Evil, "cross-site"))).StatusCode);   // reads change nothing; the browser's CORS rules keep the body from the other site

        using var allowed = f.WithWebHostBuilder(b => b.UseSetting("Security:AllowedOrigins:0", "https://tools.example.com/"));
        var c = await Login(allowed, Roles.RTE, "rte@x.com");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await c.SendAsync(Req(HttpMethod.Post, "/api/v1/notifications/x-1:read", "https://tools.example.com", "cross-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Req(HttpMethod.Post, "/api/v1/notifications/x-1:read", Evil, "cross-site"))).StatusCode);
    }

    [Fact]
    public async Task Every_state_changing_endpoint_refuses_a_cross_site_request_including_uploads_login_and_logout()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        f.CreateClient();
        var unsafeEndpoints = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Method: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY", Path: e.RoutePattern.RawText!))
            .Where(e => e.Method is "POST" or "PUT" or "PATCH" or "DELETE" || e.Path.StartsWith("/hub/trains/negotiate"))
            .ToList();
        Assert.True(unsafeEndpoints.Count > 90, $"only {unsafeEndpoints.Count} state-changing endpoints found");

        var leaked = new List<string>();
        foreach (var (method, path) in unsafeEndpoints)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(path, @"\{\*?(\w+)(:[^}]*)?\}", "x-1");
            var m = path.Contains("negotiate") ? HttpMethod.Post : new HttpMethod(method);
            HttpContent? body = url == "/api/v1/attachments"
                ? new MultipartFormDataContent { { new StringContent("Gate"), "entityType" }, { new StringContent("g1"), "entityId" }, { new ByteArrayContent([1]), "file", "a.txt" } }
                : null;   // the multipart form is what a cross-site <form enctype=multipart/form-data> can send without CORS
            var res = await rte.SendAsync(Req(m, url, Evil, "cross-site", body));
            if (res.StatusCode != HttpStatusCode.Forbidden) leaked.Add($"{method} {path} -> {(int)res.StatusCode}");
        }
        Assert.True(leaked.Count == 0, "Cross-site requests that were not refused:\n  " + string.Join("\n  ", leaked));

        // and the cookie survived all of that: logout was refused too
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_nosniff_no_framing_and_no_referrer_headers()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        foreach (var res in new[] { await f.CreateClient().GetAsync("/"), await f.CreateClient().GetAsync("/probe.js"), await rte.GetAsync("/api/v1/me"), await f.CreateClient().GetAsync("/api/v1/me") })
        {
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    [Fact]
    public async Task A_deactivated_users_existing_session_stops_working()
    {
        using var f = new ApiFactory();
        using var strict = f.WithWebHostBuilder(b => b.UseSetting("Auth:SessionRecheckSeconds", "0"));   // re-check on every request so the test needs no clock
        var rm = await Login(strict, Roles.ReleaseManager, "rm@x.com");
        var victim = await Login(strict, Roles.RTE, "gone@x.com");
        var victimId = UserId(f, "gone@x.com");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/me")).StatusCode);

        var users = await rm.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var version = Scalar(f, $"SELECT Version FROM Users WHERE Id='{victimId}'");
        var patch = Req(HttpMethod.Patch, $"/api/v1/users/{victimId}", body: JsonContent.Create(new { isActive = false }));
        patch.Headers.TryAddWithoutValidation("If-Match", version);
        Assert.Equal(HttpStatusCode.OK, (await rm.SendAsync(patch)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.SendAsync(Req(HttpMethod.Post, "/api/v1/notifications/x-1:read"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rm.GetAsync("/api/v1/me")).StatusCode);   // others are unaffected

        // signing in again reactivates the account (the identity provider is the authority on who is a user), as UserProvisioner always did
        var back = await Login(strict, Roles.RTE, "gone@x.com");
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_session_for_a_user_who_no_longer_exists_is_rejected()
    {
        using var f = new ApiFactory();
        using var strict = f.WithWebHostBuilder(b => b.UseSetting("Auth:SessionRecheckSeconds", "0"));
        var c = await Login(strict, Roles.Viewer, "ghost@x.com");
        Sql(f, "PRAGMA foreign_keys=ON; DELETE FROM Users WHERE Email='ghost@x.com';");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }
}
