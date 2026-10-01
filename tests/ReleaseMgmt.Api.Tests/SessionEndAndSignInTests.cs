using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Realtime;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-63: a live (SignalR) connection ends with its session: sign-out, deactivation, and the periodic re-check.
/// REOS-64: sign-in provisioning writes one audit row per real change and bumps Version.
/// REOS-65: a refused or failed organisation sign-in ends on the sign-in page with a reason code, never a 500.
/// </summary>
public class SessionEndAndSignInTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static WebApplicationFactory<Program> With(WebApplicationFactory<Program> f, params (string Key, string Value)[] settings) =>
        f.WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); });

    private static async Task<string> SignIn(WebApplicationFactory<Program> f, string email, string role)
    {
        var r = await f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
        r.EnsureSuccessStatusCode();
        var raw = r.Headers.GetValues("Set-Cookie").Single(c => c.Contains("releasemgmt.auth=", StringComparison.Ordinal));
        return raw[..raw.IndexOf(';')];
    }

    private static HttpRequestMessage WithCookie(HttpMethod m, string url, string cookie, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.TryAddWithoutValidation("Cookie", cookie);
        return r;
    }

    private sealed record Live(HubConnection Hub, Channel<string> Events, Task Closed) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    /// <summary>A real SignalR client over the test server: WebSockets (authorised once, the case REOS-63 is about) or long polling.</summary>
    private static async Task<Live> Connect(WebApplicationFactory<Program> f, string cookie, HttpTransportType transport = HttpTransportType.WebSockets)
    {
        var events = Channel.CreateUnbounded<string>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, TrainsHub.Path), o =>
            {
                o.Transports = transport;
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Headers["Cookie"] = cookie;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = f.Server.CreateWebSocketClient();
                    ws.ConfigureRequest = r => r.Headers.Cookie = cookie;
                    return await ws.ConnectAsync(ctx.Uri, ct);
                };
            }).Build();
        hub.On<string, int>(TrainsHub.TrainChanged, (id, _) => events.Writer.TryWrite(TrainsHub.TrainChanged + ":" + id));
        hub.On<string>(TrainsHub.ServerTime, _ => events.Writer.TryWrite(TrainsHub.ServerTime));
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        await Next(events, TrainsHub.ServerTime);   // the hub greets every connection with the clock: it is open and registered
        return new Live(hub, events, closed.Task);
    }

    private static async Task Next(Channel<string> ch, string prefix)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (await ch.Reader.WaitToReadAsync(cts.Token))
            while (ch.Reader.TryRead(out var e))
                if (e.StartsWith(prefix, StringComparison.Ordinal)) return;
    }

    /// <summary>After the close: a push to everyone reaches nobody on this connection.</summary>
    private static async Task AssertNoFurtherPushes(WebApplicationFactory<Program> f, Live live)
    {
        while (live.Events.Reader.TryRead(out _)) { }
        await f.Services.GetRequiredService<IRealtimePublisher>().TrainChangedAsync("t-after", 7);
        await Task.Delay(TimeSpan.FromSeconds(1.5));   // the factory pushes ServerTime every second too
        Assert.False(live.Events.Reader.TryRead(out var e), $"a closed connection received {e}");
        Assert.Equal(HubConnectionState.Disconnected, live.Hub.State);
    }

    private static async Task AssertClosesWithin(Live live, TimeSpan within)
    {
        var done = await Task.WhenAny(live.Closed, Task.Delay(within));
        Assert.True(done == live.Closed, $"the live connection was still open after {within.TotalSeconds} s");
    }

    // ------------------------------------------------------------------ REOS-63

    [Fact]
    public async Task REOS_63_sign_out_closes_the_users_websocket_at_once_and_no_push_follows()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "3600"));   // the sign-out itself closes it, not the timer
        var cookie = await SignIn(f, "out@x.com", Roles.RTE);
        await using var live = await Connect(f, cookie);
        Assert.Equal(1, f.Services.GetRequiredService<HubConnections>().Count);

        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(WithCookie(HttpMethod.Post, "/auth/logout", cookie))).StatusCode);

        await AssertClosesWithin(live, TimeSpan.FromSeconds(5));
        await AssertNoFurtherPushes(f, live);
        Assert.Equal(0, f.Services.GetRequiredService<HubConnections>().Count);
        // and it cannot come back with the signed-out cookie
        await Assert.ThrowsAsync<HttpRequestException>(() => Connect(f, cookie));
    }

    [Fact]
    public async Task REOS_63_sign_out_closes_a_long_polling_connection_too()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "3600"));
        var cookie = await SignIn(f, "poll@x.com", Roles.Viewer);
        await using var live = await Connect(f, cookie, HttpTransportType.LongPolling);
        (await f.CreateClient().SendAsync(WithCookie(HttpMethod.Post, "/auth/logout", cookie))).EnsureSuccessStatusCode();
        await AssertClosesWithin(live, TimeSpan.FromSeconds(5));
        await AssertNoFurtherPushes(f, live);
    }

    [Fact]
    public async Task REOS_63_sign_out_leaves_the_same_users_other_sessions_open()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "3600"));
        var laptop = await SignIn(f, "two@x.com", Roles.RTE);
        var phone = await SignIn(f, "two@x.com", Roles.RTE);
        await using var a = await Connect(f, laptop);
        await using var b = await Connect(f, phone);

        (await f.CreateClient().SendAsync(WithCookie(HttpMethod.Post, "/auth/logout", laptop))).EnsureSuccessStatusCode();

        await AssertClosesWithin(a, TimeSpan.FromSeconds(5));
        Assert.Equal(HubConnectionState.Connected, b.Hub.State);
        await f.Services.GetRequiredService<IRealtimePublisher>().TrainChangedAsync("t-still", 1);
        await Next(b.Events, TrainsHub.TrainChanged + ":t-still");
    }

    [Fact]
    public async Task REOS_63_deactivation_by_an_admin_closes_the_users_websocket_at_once()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "3600"));
        var rm = await SignIn(f, "rm@x.com", Roles.ReleaseManager);
        var victim = await SignIn(f, "leaver@x.com", Roles.RTE);
        await using var theirs = await Connect(f, victim);
        await using var mine = await Connect(f, rm);

        var id = UserId(root, "leaver@x.com");
        var patch = WithCookie(HttpMethod.Patch, $"/api/v1/users/{id}", rm, JsonContent.Create(new { isActive = false }));
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(patch)).StatusCode);

        await AssertClosesWithin(theirs, TimeSpan.FromSeconds(5));
        await AssertNoFurtherPushes(f, theirs);
        Assert.Equal(HubConnectionState.Connected, mine.Hub.State);   // the admin's own connection is untouched
    }

    [Fact]
    public async Task REOS_63_the_periodic_recheck_closes_a_deactivated_users_websocket_within_the_interval()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "1"));
        var cookie = await SignIn(f, "quiet@x.com", Roles.Viewer);
        await using var live = await Connect(f, cookie);

        // deactivated behind the app's back (an import, a database fix): no sign-out, no admin call, no HTTP request from the user
        Sql(root, "UPDATE Users SET IsActive=0, Version=Version+1 WHERE Email='quiet@x.com'");
        var sw = Stopwatch.StartNew();
        await AssertClosesWithin(live, TimeSpan.FromSeconds(1 + 4));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"closed after {sw.Elapsed}");
        await AssertNoFurtherPushes(f, live);
    }

    [Fact]
    public async Task REOS_63_a_session_refused_as_inactive_on_any_request_closes_its_connections()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Realtime:SessionRecheckSeconds", "3600"), ("Auth:SessionRecheckSeconds", "0"));
        var cookie = await SignIn(f, "req@x.com", Roles.RTE);
        await using var live = await Connect(f, cookie);
        Sql(root, "UPDATE Users SET IsActive=0 WHERE Email='req@x.com'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(WithCookie(HttpMethod.Get, "/api/v1/me", cookie))).StatusCode);
        await AssertClosesWithin(live, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task REOS_63_the_recheck_interval_defaults_to_30_seconds()
    {
        using var f = new ApiFactory();
        Assert.Equal(TimeSpan.FromSeconds(30), HubSessionMonitor.Interval(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        Assert.Equal(TimeSpan.FromSeconds(30), HubSessionMonitor.Interval(f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
    }

    // ------------------------------------------------------------------ REOS-64

    private static List<(string Action, string? Actor, JsonElement? Before, JsonElement After)> SignInAudit(ApiFactory f, string userId)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Action, ActorUserId, BeforeJson, AfterJson FROM AuditEvents WHERE EntityType='User' AND EntityId=$id AND Action LIKE 'SignIn%' ORDER BY Id";
        cmd.Parameters.AddWithValue("$id", userId);
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string?, JsonElement?, JsonElement)>();
        while (r.Read())
            rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : JsonDocument.Parse(r.GetString(2)).RootElement, JsonDocument.Parse(r.GetString(3)).RootElement));
        return rows;
    }

    [Fact]
    public async Task REOS_64_first_sign_in_is_audited_and_signing_in_again_unchanged_writes_nothing()
    {
        using var f = new ApiFactory();
        await As(f, Roles.RTE, "same@x.com");
        var id = UserId(f, "same@x.com");
        var rows = SignInAudit(f, id);
        var created = Assert.Single(rows);
        Assert.Equal(UserProvisioner.CreateAction, created.Action);
        Assert.Equal(id, created.Actor);
        Assert.Equal(Roles.RTE, created.After.GetProperty("Role").GetString());

        await As(f, Roles.RTE, "same@x.com");   // nothing changed
        Assert.Single(SignInAudit(f, id));
        Assert.Equal("1", Scalar(f, $"SELECT Version FROM Users WHERE Id='{id}'"));
    }

    [Fact]
    public async Task REOS_64_a_role_change_at_sign_in_is_one_audit_row_with_old_and_new_values_and_a_version_bump()
    {
        using var f = new ApiFactory();
        await As(f, Roles.RTE, "mover@x.com");
        var id = UserId(f, "mover@x.com");
        await As(f, Roles.ReleaseManager, "mover@x.com");

        var update = SignInAudit(f, id).Where(r => r.Action == UserProvisioner.UpdateAction).ToList();
        var row = Assert.Single(update);
        Assert.Equal(id, row.Actor);
        Assert.Equal("""{"Role":"RTE"}""", row.Before!.Value.GetRawText());
        Assert.Equal("""{"Role":"ReleaseManager"}""", row.After.GetRawText());
        Assert.Equal("2", Scalar(f, $"SELECT Version FROM Users WHERE Id='{id}'"));
        Assert.Equal(Roles.ReleaseManager, Scalar(f, $"SELECT Role FROM Users WHERE Id='{id}'"));
    }

    [Fact]
    public async Task REOS_64_reactivation_at_sign_in_is_audited_and_bumps_the_version()
    {
        using var f = new ApiFactory();
        await As(f, Roles.Viewer, "back@x.com");
        var id = UserId(f, "back@x.com");
        Sql(f, $"UPDATE Users SET IsActive=0, Version=5 WHERE Id='{id}'");
        await As(f, Roles.Viewer, "back@x.com");

        var row = Assert.Single(SignInAudit(f, id), r => r.Action == UserProvisioner.UpdateAction);
        Assert.Equal("""{"IsActive":false}""", row.Before!.Value.GetRawText());
        Assert.Equal("""{"IsActive":true}""", row.After.GetRawText());
        Assert.Equal("6", Scalar(f, $"SELECT Version FROM Users WHERE Id='{id}'"));
        Assert.Equal("1", Scalar(f, $"SELECT IsActive FROM Users WHERE Id='{id}'"));
    }

    // ------------------------------------------------------------------ REOS-65: a real /signin-oidc round trip against a static, in-test identity provider

    private const string Issuer = "https://idp.example.test/";
    private const string ClientId = "reos-test";

    private sealed class TestIdp
    {
        public readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

        public string IdToken(params (string Type, object Value)[] claims)
        {
            var now = DateTime.UtcNow;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = ClientId, IssuedAt = now, NotBefore = now.AddMinutes(-1), Expires = now.AddMinutes(5),
                Claims = new Dictionary<string, object>(claims.Select(c => KeyValuePair.Create(c.Type, c.Value))) { ["sub"] = Guid.NewGuid().ToString() },
                SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
            });
        }
    }

    private static WebApplicationFactory<Program> OidcHost(ApiFactory root, TestIdp idp, params (string, string)[] settings) =>
        root.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Oidc:Authority", Issuer);
            b.UseSetting("Auth:Oidc:ClientId", ClientId);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureTestServices(s => s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var cfg = new OpenIdConnectConfiguration { Issuer = Issuer, AuthorizationEndpoint = Issuer + "authorize", TokenEndpoint = Issuer + "token" };
                cfg.SigningKeys.Add(idp.Key);
                o.Configuration = cfg;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(cfg);   // no metadata fetch: there is no IdP here
            }));
        });

    /// <summary>A state value and correlation cookie exactly as the handler would have made them at the challenge.</summary>
    private static (string State, string Cookie) Challenge(WebApplicationFactory<Program> f)
    {
        var o = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var correlation = Guid.NewGuid().ToString("N");
        var props = new AuthenticationProperties { RedirectUri = "/" };
        props.Items[".xsrf"] = correlation;
        return (o.StateDataFormat.Protect(props), $"{o.CorrelationCookie.Name}{correlation}=N");
    }

    private static async Task<HttpResponseMessage> Callback(WebApplicationFactory<Program> f, Dictionary<string, string> form, string? cookie)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc") { Content = new FormUrlEncodedContent(form) };
        if (cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await c.SendAsync(req);
    }

    private static void AssertSentToSignIn(HttpResponseMessage r, string reason)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal($"/?signin={reason}", r.Headers.Location?.OriginalString);
        var set = r.Headers.TryGetValues("Set-Cookie", out var all) ? all : [];
        Assert.DoesNotContain(set, c => c.Contains("releasemgmt.auth=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));   // no session
    }

    [Fact]
    public async Task REOS_65_an_identity_with_no_mapped_role_is_sent_to_the_sign_in_page_not_a_500()
    {
        using var root = new ApiFactory();
        var idp = new TestIdp();
        using var f = OidcHost(root, idp, ("Auth:RoleMap:grp-rm", Roles.ReleaseManager));
        var (state, cookie) = Challenge(f);
        var r = await Callback(f, new() { ["state"] = state, ["id_token"] = idp.IdToken(("email", "guest@elsewhere.example"), ("groups", "all-staff")) }, cookie);
        AssertSentToSignIn(r, OidcSignIn.Reasons.NoRole);
        Assert.Equal("0", Scalar(root, "SELECT COUNT(*) FROM Users WHERE Email='guest@elsewhere.example'"));
    }

    [Fact]
    public async Task REOS_65_an_unverified_email_is_sent_to_the_sign_in_page_not_a_500()
    {
        using var root = new ApiFactory();
        var idp = new TestIdp();
        using var f = OidcHost(root, idp, ("Auth:RoleMap:grp-staff", Roles.Viewer));
        var (state, cookie) = Challenge(f);
        var r = await Callback(f, new() { ["state"] = state, ["id_token"] = idp.IdToken(("email", "gov@corp.example"), ("email_verified", false), ("groups", "grp-staff")) }, cookie);
        AssertSentToSignIn(r, OidcSignIn.Reasons.EmailUnverified);
    }

    [Fact]
    public async Task REOS_65_an_error_from_the_identity_provider_is_sent_to_the_sign_in_page_not_a_500()
    {
        using var root = new ApiFactory();
        using var f = OidcHost(root, new TestIdp());
        var (state, cookie) = Challenge(f);
        var denied = await Callback(f, new() { ["state"] = state, ["error"] = "access_denied", ["error_description"] = "AADSTS50105: user <script> not assigned" }, cookie);
        AssertSentToSignIn(denied, OidcSignIn.Reasons.Denied);
        Assert.DoesNotContain("AADSTS", denied.Headers.Location!.OriginalString);   // a code, never the provider's text

        (state, cookie) = Challenge(f);
        AssertSentToSignIn(await Callback(f, new() { ["state"] = state, ["error"] = "server_error" }, cookie), OidcSignIn.Reasons.Failed);
    }

    [Fact]
    public async Task REOS_65_a_callback_without_state_or_correlation_and_a_bad_token_are_sent_to_the_sign_in_page()
    {
        using var root = new ApiFactory();
        var idp = new TestIdp();
        using var f = OidcHost(root, idp, ("Auth:RoleMap:grp-rm", Roles.ReleaseManager));
        AssertSentToSignIn(await Callback(f, new() { ["error"] = "access_denied" }, null), OidcSignIn.Reasons.Expired);   // replayed or stale callback
        var (state, _) = Challenge(f);
        AssertSentToSignIn(await Callback(f, new() { ["state"] = state, ["id_token"] = idp.IdToken(("email", "a@corp.example")) }, null), OidcSignIn.Reasons.Expired);   // no correlation cookie
        var (s2, c2) = Challenge(f);
        var forged = new TestIdp().IdToken(("email", "a@corp.example"), ("groups", "grp-rm"));   // signed by a key the IdP does not publish
        AssertSentToSignIn(await Callback(f, new() { ["state"] = s2, ["id_token"] = forged }, c2), OidcSignIn.Reasons.Failed);
    }

    [Fact]
    public async Task REOS_65_a_missing_email_maps_to_its_own_reason()
    {
        using var root = new ApiFactory();
        var idp = new TestIdp();
        using var f = OidcHost(root, idp, ("Auth:RoleMap:grp-rm", Roles.ReleaseManager));
        var (state, cookie) = Challenge(f);
        AssertSentToSignIn(await Callback(f, new() { ["state"] = state, ["id_token"] = idp.IdToken(("groups", "grp-rm")) }, cookie), OidcSignIn.Reasons.NoEmail);
    }
}
