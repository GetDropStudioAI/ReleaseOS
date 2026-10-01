using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// SEC-E5: behind the runbook's TLS-terminating reverse proxy every request arrived from the proxy's address over http. The app therefore judged the proxy,
/// not the client: the dev sign-in's "this machine only" check (SEC-B2) passed for any remote client of a same-host proxy, the ICS brake counted every
/// client as one, and HSTS was never sent. Forwarded headers are now honoured from trusted proxies only (loopback by default, Proxy:KnownProxies and
/// Proxy:KnownNetworks for others), and HSTS is sent outside Development.
/// SEC-E6: the web page itself had no Content-Security-Policy (only downloads did), so any injected markup could run script or be framed.
/// </summary>
public class ProxyAndBrowserHardeningTests
{
    private sealed class Peer(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip); return n(ctx); });
            next(app);
        };
    }

    private static WebApplicationFactory<Program> From(ApiFactory root, string peer) =>
        root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new Peer(peer))));

    private static HttpRequestMessage DevLogin(string? forwardedFor)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/auth/dev-login") { Content = JsonContent.Create(new { email = "p@x.com", name = "p", role = Roles.RTE }) };
        if (forwardedFor is not null) r.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return r;
    }

    [Fact]
    public async Task A_remote_client_behind_a_same_host_proxy_is_judged_by_its_own_address()
    {
        using var root = new ApiFactory();
        using var f = From(root, "127.0.0.1");   // the proxy on this machine
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(DevLogin("192.0.2.10"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(DevLogin(null))).StatusCode);   // a local caller with no proxy header still works
    }

    [Fact]
    public async Task A_client_that_is_not_a_trusted_proxy_cannot_claim_to_be_local()
    {
        using var root = new ApiFactory();
        using var f = From(root, "192.0.2.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(DevLogin("127.0.0.1"))).StatusCode);
    }

    [Fact]
    public async Task A_configured_proxy_address_is_trusted_and_others_are_not()
    {
        using var root = new ApiFactory();
        using var trusted = From(root, "10.0.0.5").WithWebHostBuilder(b => b.UseSetting("Proxy:KnownProxies:0", "10.0.0.5"));
        Assert.Equal(HttpStatusCode.Forbidden, (await trusted.CreateClient().SendAsync(DevLogin("192.0.2.10"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trusted.CreateClient().SendAsync(DevLogin("127.0.0.1"))).StatusCode);   // the proxy says the client is local
    }

    [Fact]
    public async Task Behind_a_proxy_that_forwards_the_client_address_one_client_guessing_feed_tokens_does_not_lock_out_another()
    {
        using var root = new ApiFactory();
        using var f = From(root, "127.0.0.1");
        var viewer = f.CreateClient();
        (await viewer.PostAsJsonAsync("/auth/dev-login", new { email = "feed@x.com", name = "feed", role = Roles.Viewer })).EnsureSuccessStatusCode();
        var t = System.Text.Json.JsonDocument.Parse(await (await viewer.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" })).Content.ReadAsStringAsync());
        var path = t.RootElement.GetProperty("path").GetString()!;
        HttpRequestMessage Get(string url, string client) { var r = new HttpRequestMessage(HttpMethod.Get, url); r.Headers.TryAddWithoutValidation("X-Forwarded-For", client); return r; }
        var anon = f.CreateClient();
        for (var i = 0; i < 61; i++) await anon.SendAsync(Get($"/api/v1/ics/{new string('A', 43)}.ics", "203.0.113.66"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.SendAsync(Get($"/api/v1/ics/{new string('A', 43)}.ics", "203.0.113.66"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Get(path, "203.0.113.7"))).StatusCode);   // a valid token is served whatever any address has failed (REOS-67)
    }

    [Fact]
    public async Task Outside_Development_https_through_the_proxy_gets_HSTS_and_Development_does_not()
    {
        using var prod = From(new ApiFactory("Production"), "127.0.0.1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz") { Headers = { Host = "releases.example.com" } };
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        var r = await prod.CreateClient().SendAsync(req);
        Assert.True(r.Headers.Contains("Strict-Transport-Security"), "HSTS on an https request outside Development");

        using var dev = From(new ApiFactory(), "127.0.0.1");
        var d = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        d.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        Assert.False((await dev.CreateClient().SendAsync(d)).Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task The_web_page_carries_a_content_security_policy_that_forbids_inline_script_and_framing()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/");
        Assert.Equal("text/html", r.Content.Headers.ContentType?.MediaType);
        var csp = string.Join(";", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotMatch(@"script-src[^;]*unsafe-inline", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.True(r.Headers.Contains("Permissions-Policy"));
        Assert.Equal("same-origin", r.Headers.GetValues("Cross-Origin-Opener-Policy").Single());

        var attachmentCsp = "default-src 'none'; sandbox";   // downloads keep their stricter policy (REOS-53): the page policy never replaces it
        Assert.DoesNotContain(attachmentCsp, csp);
    }
}
