using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Security scan B: authentication and session management (OWASP API2:2023, Top 10:2025 A07 and A04, ASVS 5.0 V6/V7/V9/V10). Tests are named after the
/// finding SEC-B&lt;n&gt; in docs/security/scan-authn.md that they reproduce; each reproduction failed against the code at c9a1180 before its fix. The few that
/// passed there (loopback callers still sign in, Development keeps its plain cookie, the explicit opt-in) pin what the fixes must not break.
/// </summary>
public class AuthnSecurityTests
{
    private const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;

    private static WebApplicationFactory<Program> With(WebApplicationFactory<Program> f, params (string Key, string Value)[] settings) =>
        f.WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); });

    private static WebApplicationFactory<Program> WithServices(WebApplicationFactory<Program> f, Action<IServiceCollection> services, params (string Key, string Value)[] settings) =>
        f.WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); b.ConfigureServices(services); });

    internal static Task<HttpResponseMessage> DevLogin(HttpClient c, string email, string role) =>
        c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });

    /// <summary>Makes every request look as if it arrived from <paramref name="ip"/> (the in-process test server has no socket).</summary>
    private sealed class RemoteAddress(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip); return nxt(ctx); });
            next(app);
        };
    }

    /// <summary>A sign-in that uses the host's real cookie configuration in an environment where dev-login does not exist (the OIDC callback cannot run here).</summary>
    private sealed class TestSignIn : IStartupFilter
    {
        public const string Path = "/__test/sign-in";
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Path != Path) { await nxt(ctx); return; }
                await ctx.SignInAsync(Cookie, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "t@x.com"), new Claim(ClaimTypes.Role, Roles.Viewer)], Cookie)));
            });
            next(app);
        };
    }

    private static string SetCookie(HttpResponseMessage r, string prefix) =>
        r.Headers.TryGetValues("Set-Cookie", out var all) ? all.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal)) ?? "" : "";

    /// <summary>The session cookie a response set, as "name=value" ready for a Cookie header.</summary>
    private static string SessionCookie(HttpResponseMessage r)
    {
        var raw = r.Headers.GetValues("Set-Cookie").Single(c => c.Contains("releasemgmt.auth=", StringComparison.Ordinal));
        return raw[..raw.IndexOf(';')];
    }

    private static HttpRequestMessage WithCookie(HttpMethod m, string url, string cookie)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.TryAddWithoutValidation("Cookie", cookie);
        return r;
    }

    // ------------------------------------------------------------------ SEC-B1: dev-login beside organisation sign-in

    private static readonly (string, string)[] Oidc = [("Auth:Oidc:Authority", "https://idp.example.test/"), ("Auth:Oidc:ClientId", "reos-test")];

    [Fact]
    public async Task SEC_B1_dev_login_and_dev_tools_are_not_mapped_when_organisation_sign_in_is_configured()
    {
        using var root = new ApiFactory();   // Development, which is what start.py always runs
        using var f = With(root, Oidc);
        var c = f.CreateClient();
        var r = await DevLogin(c, "anyone@x.com", Roles.ReleaseManager);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("", SetCookie(r, "releasemgmt.auth"));
        var routes = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "").ToList();
        Assert.DoesNotContain(routes, p => p.StartsWith("/auth/dev-login") || p.StartsWith("/api/v1/dev/"));
    }

    [Fact]
    public async Task SEC_B1_a_developer_can_still_opt_in_to_dev_login_beside_organisation_sign_in()
    {
        using var root = new ApiFactory();
        using var f = With(root, [.. Oidc, ("Auth:DevLogin:Enabled", "true")]);
        Assert.Equal(HttpStatusCode.OK, (await DevLogin(f.CreateClient(), "dev@x.com", Roles.RTE)).StatusCode);
    }

    [Fact]
    public async Task SEC_B1_outside_Development_the_opt_in_does_not_bring_dev_login_back()
    {
        using var root = new ApiFactory("Production");
        using var f = With(root, ("Auth:DevLogin:Enabled", "true"));
        Assert.NotEqual(HttpStatusCode.OK, (await DevLogin(f.CreateClient(), "dev@x.com", Roles.RTE)).StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B2: dev-login from another machine or through DNS rebinding

    [Fact]
    public async Task SEC_B2_dev_login_refuses_a_Host_that_is_not_this_machine_so_DNS_rebinding_cannot_sign_in()
    {
        // Host filtering (SEC-E1, HostFilteringTests) already answers 400 to this request in Development. Switch it off here so this test proves the
        // dev login's own check, the second layer, which still holds if AllowedHosts is ever widened.
        using var root = new ApiFactory();
        using var f = root.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "*"));
        var c = f.CreateClient();
        // A page on rebind.attacker.example whose name now resolves to 127.0.0.1: to the browser the request is same-origin, so the cross-site guard lets it through.
        var req = new HttpRequestMessage(HttpMethod.Post, "/auth/dev-login") { Content = JsonContent.Create(new { email = "rm@x.com", name = "rm", role = Roles.ReleaseManager }) };
        req.Headers.Host = "rebind.attacker.example:6080";
        req.Headers.TryAddWithoutValidation("Origin", "http://rebind.attacker.example:6080");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("", SetCookie(r, "releasemgmt.auth"));
    }

    [Theory]
    [InlineData("192.0.2.10", false)]     // another machine on the network (a Development host bound to 0.0.0.0, or a port forward)
    [InlineData("10.1.2.3", false)]
    [InlineData("127.0.0.1", true)]       // start.py, the Vite proxy, the e2e and DR-drill clients
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    public async Task SEC_B2_dev_login_answers_only_callers_on_this_machine(string ip, bool allowed)
    {
        using var root = new ApiFactory();
        using var f = WithServices(root, s => s.AddSingleton<IStartupFilter>(new RemoteAddress(ip)));
        var r = await DevLogin(f.CreateClient(), "rm@x.com", Roles.ReleaseManager);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B3: cookie Secure flag and prefix outside Development

    [Fact]
    public async Task SEC_B3_outside_Development_the_session_cookie_is_Secure_and_host_only_even_behind_a_plain_http_proxy_hop()
    {
        using var root = new ApiFactory("Production");
        using var f = WithServices(root, s => s.AddSingleton<IStartupFilter>(new TestSignIn()));
        var r = await f.CreateClient().GetAsync(TestSignIn.Path);   // the test server speaks http, as the app does behind the runbook's TLS-terminating proxy
        var cookie = SetCookie(r, "__Host-releasemgmt.auth=").ToLowerInvariant();
        Assert.NotEqual("", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
    }

    [Fact]
    public async Task SEC_B3_Development_keeps_the_plain_cookie_so_start_py_over_http_still_works()
    {
        using var f = new ApiFactory();
        var cookie = SetCookie(await DevLogin(f.CreateClient(), "a@x.com", Roles.RTE), "releasemgmt.auth=").ToLowerInvariant();
        Assert.NotEqual("", cookie);
        Assert.DoesNotContain("secure", cookie);
    }

    // ------------------------------------------------------------------ SEC-B4: idle timeout and absolute session lifetime

    private static (WebApplicationFactory<Program> F, FakeTimeProvider Clock) Clocked(ApiFactory root, params (string, string)[] settings)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        return (WithServices(root, s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock); }, settings), clock);
    }

    [Fact]
    public async Task SEC_B4_a_session_nobody_uses_ends_after_the_idle_timeout()
    {
        using var root = new ApiFactory();
        var (f, clock) = Clocked(root);
        using var _ = f;
        var c = f.CreateClient();
        (await DevLogin(c, "idle@x.com", Roles.RTE)).EnsureSuccessStatusCode();
        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);   // in use: the cookie slides
        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(61));                                        // default Auth:Session:IdleMinutes = 60
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task SEC_B4_an_active_session_still_ends_at_the_absolute_lifetime_so_the_identity_provider_is_asked_again()
    {
        using var root = new ApiFactory();
        var (f, clock) = Clocked(root);
        using var _ = f;
        var c = f.CreateClient();
        (await DevLogin(c, "busy@x.com", Roles.ReleaseManager)).EnsureSuccessStatusCode();
        for (var i = 0; i < 23; i++)   // 11.5 hours of steady use
        {
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
        }
        clock.Advance(TimeSpan.FromMinutes(31));                                        // default Auth:Session:AbsoluteHours = 12
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
        (await DevLogin(c, "busy@x.com", Roles.ReleaseManager)).EnsureSuccessStatusCode();   // signing in again starts a new session
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task SEC_B4_both_limits_are_configurable()
    {
        using var root = new ApiFactory();
        var (f, clock) = Clocked(root, ("Auth:Session:IdleMinutes", "10"), ("Auth:Session:AbsoluteHours", "1"));
        using var _ = f;
        var c = f.CreateClient();
        (await DevLogin(c, "cfg@x.com", Roles.Viewer)).EnsureSuccessStatusCode();
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
        (await DevLogin(c, "cfg@x.com", Roles.Viewer)).EnsureSuccessStatusCode();
        for (var i = 0; i < 6; i++) { clock.Advance(TimeSpan.FromMinutes(9)); Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode); }
        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B5: sign-out ends the session on the server

    [Fact]
    public async Task SEC_B5_signing_out_ends_the_session_for_every_copy_of_the_cookie()
    {
        using var f = new ApiFactory();
        var raw = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var cookie = SessionCookie(await DevLogin(raw, "out@x.com", Roles.ReleaseManager));
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(WithCookie(HttpMethod.Get, "/api/v1/me", cookie))).StatusCode);
        var copy = cookie;   // what a shared machine's browser profile, a proxy log or malware kept
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(WithCookie(HttpMethod.Post, "/auth/logout", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(WithCookie(HttpMethod.Get, "/api/v1/me", copy))).StatusCode);
        // the user's other sessions are theirs to end: a second sign-in is unaffected
        var other = SessionCookie(await DevLogin(raw, "out@x.com", Roles.ReleaseManager));
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(WithCookie(HttpMethod.Get, "/api/v1/me", other))).StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B6: calendar feed of a deactivated user

    [Fact]
    public async Task SEC_B6_a_deactivated_users_calendar_feed_stops_working_and_comes_back_if_they_are_reactivated()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var leaver = await As(f, Roles.RTE, "leaver@x.com");
        var leaverId = UserId(f, "leaver@x.com");
        var issued = JsonDocument.Parse(await (await leaver.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" })).Content.ReadAsStringAsync()).RootElement;
        var path = issued.GetProperty("path").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync(path)).StatusCode);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/users/{leaverId}") { Content = JsonContent.Create(new { isActive = false }) };
        patch.Headers.TryAddWithoutValidation("If-Match", Scalar(f, $"SELECT Version FROM Users WHERE Id='{leaverId}'"));
        Assert.Equal(HttpStatusCode.OK, (await rm.SendAsync(patch)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().GetAsync(path)).StatusCode);   // same bare 404 as an unknown token

        await As(f, Roles.RTE, "leaver@x.com");   // the identity provider still vouches for them: sign-in reactivates (Q-053c), and so does their link
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync(path)).StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B7, B8, B9: OpenID Connect sign-in (the configured OnTokenValidated, run without an IdP)

    private static WebApplicationFactory<Program> OidcHost(ApiFactory root, params (string, string)[] settings) => With(root, [.. Oidc, .. settings]);

    private static async Task<(TokenValidatedContext Ctx, ClaimsIdentity Id)> OidcSignIn(WebApplicationFactory<Program> f, params Claim[] claims)
    {
        var options = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        await using var scope = f.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var id = new ClaimsIdentity(claims, "oidc-test", "name", ClaimTypes.Role);
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        var ctx = new TokenValidatedContext(http, scheme, options, new ClaimsPrincipal(id), new AuthenticationProperties());
        await options.Events.TokenValidated(ctx);
        return (ctx, id);
    }

    private static string[] AppRoles(ClaimsIdentity id) => [.. id.FindAll(ClaimTypes.Role).Select(c => c.Value).Order()];

    [Fact]
    public async Task SEC_B7_a_role_claim_from_the_identity_provider_does_not_bypass_the_role_map()
    {
        using var root = new ApiFactory();
        using var f = OidcHost(root, ("Auth:RoleMap:grp-rte", Roles.RTE), ("Auth:DefaultRole", Roles.Viewer));
        // An IdP "roles" claim (inbound-mapped to ClaimTypes.Role by the handler) that happens to be spelled like an app role, and no mapped group.
        var (ctx, id) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "someone@corp.example"), new Claim(ClaimTypes.Role, Roles.GovernanceOfficer), new Claim("groups", "grp-other"));
        Assert.Null(ctx.Result?.Failure);
        Assert.Equal([Roles.Viewer], AppRoles(id));
        Assert.Equal(Roles.Viewer, Scalar(root, "SELECT Role FROM Users WHERE Email='someone@corp.example'"));
    }

    [Fact]
    public async Task SEC_B7_identity_provider_roles_are_mapped_through_the_role_map_like_groups()
    {
        using var root = new ApiFactory();
        using var f = OidcHost(root, ("Auth:RoleMap:ReleaseMgmt.RM", Roles.ReleaseManager));
        var (ctx, id) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "rm@corp.example"), new Claim(ClaimTypes.Role, "ReleaseMgmt.RM"));
        Assert.Null(ctx.Result?.Failure);
        Assert.Equal([Roles.ReleaseManager], AppRoles(id));
        Assert.Equal(Roles.ReleaseManager, Scalar(root, "SELECT Role FROM Users WHERE Email='rm@corp.example'"));
    }

    [Fact]
    public async Task SEC_B8_an_email_the_identity_provider_marks_unverified_does_not_sign_in_as_the_account_with_that_email()
    {
        using var root = new ApiFactory();
        using var f = OidcHost(root, ("Auth:RoleMap:grp-staff", Roles.Viewer));
        var gov = await As(root, Roles.GovernanceOfficer, "gov@corp.example");   // the real account (made through dev-login on the unconfigured host)
        var (ctx, id) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "gov@corp.example"), new Claim("email_verified", "false"), new Claim("groups", "grp-staff"));
        Assert.NotNull(ctx.Result?.Failure);
        Assert.Null(id.FindFirst("uid"));
        Assert.Equal(Roles.GovernanceOfficer, Scalar(root, "SELECT Role FROM Users WHERE Email='gov@corp.example'"));   // not overwritten
        // a verified one (or an IdP that does not send the claim at all) signs in as before
        var (ok, okId) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "new@corp.example"), new Claim("email_verified", "true"), new Claim("groups", "grp-staff"));
        Assert.Null(ok.Result?.Failure);
        Assert.NotNull(okId.FindFirst("uid"));
    }

    [Fact]
    public async Task SEC_B9_an_identity_in_no_mapped_group_is_refused_unless_a_default_role_is_configured()
    {
        using var root = new ApiFactory();
        using (var f = OidcHost(root, ("Auth:RoleMap:grp-rm", Roles.ReleaseManager)))
        {
            var (ctx, id) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "guest@elsewhere.example"), new Claim("groups", "all-staff"));
            Assert.NotNull(ctx.Result?.Failure);
            Assert.Null(id.FindFirst("uid"));
            Assert.Equal("0", Scalar(root, "SELECT COUNT(*) FROM Users WHERE Email='guest@elsewhere.example'"));   // nobody is provisioned
        }
        using (var f = OidcHost(root, ("Auth:RoleMap:grp-rm", Roles.ReleaseManager), ("Auth:DefaultRole", Roles.Viewer)))
        {
            var (ctx, id) = await OidcSignIn(f, new Claim(ClaimTypes.Email, "staff@corp.example"), new Claim("groups", "all-staff"));
            Assert.Null(ctx.Result?.Failure);
            Assert.Equal([Roles.Viewer], AppRoles(id));
        }
    }

    // ------------------------------------------------------------------ SEC-B11: a cookie minted by a Development copy of the same key ring

    [Fact]
    public async Task SEC_B11_a_session_minted_by_a_Development_copy_is_refused_by_the_Production_instance_it_was_restored_from()
    {
        // The DR runbook restores the database together with keys/ onto another host; a developer who runs that copy through start.py (Development) has dev-login.
        using var devCopy = new ApiFactory();
        var raw = devCopy.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var minted = SessionCookie(await DevLogin(raw, "gov@corp.example", Roles.GovernanceOfficer));
        var value = minted[(minted.IndexOf('=') + 1)..];

        using var prodRoot = new ApiFactory("Production");
        using var prod = With(prodRoot, ("Db:Path", devCopy.DbPath));   // same database, and so the same keys/ directory beside it
        var client = prod.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        foreach (var name in new[] { "releasemgmt.auth", "__Host-releasemgmt.auth" })   // whatever the production cookie is called: the attacker sets the header by hand
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithCookie(HttpMethod.Get, "/api/v1/me", $"{name}={value}"))).StatusCode);
    }

    // ------------------------------------------------------------------ SEC-B13: API calls without a session under organisation sign-in

    [Fact]
    public async Task SEC_B13_with_organisation_sign_in_an_api_call_without_a_session_is_401_not_a_redirect_to_the_identity_provider()
    {
        using var root = new ApiFactory("Production");
        using var f = OidcHost(root);
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/v1/me"), (HttpMethod.Get, "/api/v1/trains"), (HttpMethod.Post, "/hub/trains/negotiate?negotiateVersion=1") })
        {
            var r = await c.SendAsync(new HttpRequestMessage(method, url));
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} gave {(int)r.StatusCode}");   // the SPA shows its sign-in page on 401
            Assert.False(r.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(s => s.Contains("OpenIdConnect", StringComparison.Ordinal)),
                "no nonce or correlation cookie is planted by a background call");
        }
        Assert.NotNull(f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().SingleOrDefault(e => e.RoutePattern.RawText == "/auth/login"));   // sign-in starts there, explicitly
    }

    // ------------------------------------------------------------------ SEC-B12: key ring file permissions (POSIX)

    [Fact]
    public async Task SEC_B12_the_key_ring_that_signs_sessions_is_readable_by_the_service_account_only()
    {
        if (OperatingSystem.IsWindows()) return;   // ACLs on Windows: runbook section 5
        using var f = new ApiFactory();
        (await DevLogin(f.CreateClient(), "k@x.com", Roles.RTE)).EnsureSuccessStatusCode();   // the first protect creates the ring
        var dir = Path.Combine(Path.GetDirectoryName(f.DbPath)!, "keys");
        Assert.True(Directory.Exists(dir));
        const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(dir) & groupOrOther);
        var keys = Directory.GetFiles(dir, "*.xml");
        Assert.NotEmpty(keys);
        foreach (var k in keys) Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(k) & groupOrOther);
    }
}

/// <summary>Reads this host's own log file; runs in parallel with the rest since each host has its own Serilog logger (REOS-70).</summary>
public class AuthnLogRedactionTests
{
    // ------------------------------------------------------------------ SEC-B10: feed tokens in logs when request logging is raised

    [Fact]
    public async Task SEC_B10_feed_tokens_stay_out_of_the_log_even_when_request_logging_is_turned_up()
    {
        using var root = new ApiFactory();
        using var f = root.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Information",   // the usual first step when troubleshooting
        })));
        var rte = f.CreateClient();
        (await AuthnSecurityTests.DevLogin(rte, "rte@x.com", Roles.RTE)).EnsureSuccessStatusCode();
        var issued = JsonDocument.Parse(await (await rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" })).Content.ReadAsStringAsync()).RootElement;
        var token = issued.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync($"/api/v1/ics/{token}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().GetAsync($"/api/v1/ics/{token}/trains/nope.ics?x=1")).StatusCode);   // a 404 and a query string are logged too

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var logs = Directory.EnumerateFiles(Path.GetDirectoryName(root.DbPath)!, "log-*.txt").Select(p =>
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }).ToList();
        var all = string.Join("\n", logs);
        Assert.Contains("/api/v1/ics/", all);   // request logging really was on: the test is not vacuous
        Assert.DoesNotContain(token, all);
    }

    private sealed class Collect : Serilog.Core.ILogEventSink
    {
        public readonly List<Serilog.Events.LogEvent> Events = [];
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void SEC_B10_the_redactor_masks_the_token_in_scalars_sequences_and_structures_and_leaves_other_paths_alone()
    {
        const string tok = "zbidFWil4UMDQe9BdQOEXwLly7-qpWuHGXnmksdFq_w";
        var sink = new Collect();
        using (var log = new Serilog.LoggerConfiguration().Enrich.With<Auth.IcsTokenRedactor>().WriteTo.Sink(sink).CreateLogger())
        {
            log.ForContext("RequestPath", $"/api/v1/ics/{tok}/trains/t1.ics")
               .Information("Request starting {Method} {Path} {Scope} {@Shape} {Other}", "GET", $"/api/v1/ics/{tok}.ics", new[] { $"GET /api/v1/ICS/{tok}/mine.ics" }, new { Url = $"http://h/api/v1/ics/{tok}.ics?x=1" }, "/api/v1/me/ics-tokens");
        }
        var e = Assert.Single(sink.Events);
        var text = e.RenderMessage() + string.Join(" ", e.Properties.Select(p => p.Value.ToString()));
        Assert.DoesNotContain(tok, text);
        Assert.Contains("/api/v1/ics/(redacted)/trains/t1.ics", text);
        Assert.Contains("/api/v1/me/ics-tokens", text);   // not a feed path: untouched
    }
}
