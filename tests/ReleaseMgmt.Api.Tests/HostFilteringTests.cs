using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// DNS rebinding (SEC-E1): a web page on attacker.example can make its own name resolve to 127.0.0.1 and then call the locally running app as
/// "same origin". The cookie of the real app is not sent, but in Development the dev login is open, so the page could sign itself in as any
/// role and read or change everything. Host filtering refuses any Host header that is not loopback in Development.
/// </summary>
public class HostFilteringTests
{
    private static HttpRequestMessage DevLogin(string host) => new(HttpMethod.Post, "/auth/dev-login")
    {
        Headers = { Host = host },
        Content = JsonContent.Create(new { email = "attacker@x.com", name = "attacker", role = "RTE" }),
    };

    [Fact]
    public async Task A_rebinding_page_cannot_reach_the_development_app_under_its_own_host_name()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        var r = await c.SendAsync(DevLogin("attacker.example:6080"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"), "no session may be issued to a foreign host");

        var health = new HttpRequestMessage(HttpMethod.Get, "/healthz") { Headers = { Host = "attacker.example" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(health)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:6080")]
    [InlineData("localhost:6273")]
    [InlineData("[::1]:6080")]
    public async Task Loopback_host_names_still_work(string host)
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(DevLogin(host));
        Assert.True(r.IsSuccessStatusCode, $"{host}: {(int)r.StatusCode}");
    }
    // ---- REOS-68 (Q-SEC-E1 option b): outside Development the app does not start until AllowedHosts names its host --------------------------------

    [Theory]
    [InlineData("*", "\"*\"")]
    [InlineData("localhost;*", "\"*\"")]
    [InlineData("", "empty")]
    [InlineData(" ; ", "empty")]
    public void Outside_Development_a_wildcard_or_empty_AllowedHosts_stops_start_up_with_a_message_naming_the_key(string value, string says)
    {
        using var root = new ApiFactory("Production");
        using var f = root.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", value));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        var inner = ex as InvalidOperationException ?? ex.InnerException as InvalidOperationException ?? ex.GetBaseException() as InvalidOperationException;
        Assert.NotNull(inner);
        Assert.StartsWith("AllowedHosts is ", inner.Message);
        Assert.Contains(says, inner.Message);
    }

    [Fact]
    public void The_shipped_configuration_does_not_start_outside_Development()
    {
        // appsettings.json no longer carries AllowedHosts; a Production host started without setting it is refused.
        using var root = new ApiFactory("Production");
        using var f = root.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", null));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("AllowedHosts is empty", ex.GetBaseException().Message);
    }

    [Fact]
    public async Task Outside_Development_a_named_host_starts_and_every_other_host_is_refused_400()
    {
        using var root = new ApiFactory("Production");
        using var f = root.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "releases.example.com"));
        var c = f.CreateClient();
        var ok = new HttpRequestMessage(HttpMethod.Get, "/healthz") { Headers = { Host = "releases.example.com" } };
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ok)).StatusCode);
        foreach (var host in new[] { "attacker.example", "localhost", "127.0.0.1:6080", "releases.example.com.attacker.example" })
        {
            var bad = new HttpRequestMessage(HttpMethod.Get, "/healthz") { Headers = { Host = host } };
            Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(bad)).StatusCode);
        }
    }

    [Fact]
    public void Development_is_not_checked_so_start_py_keeps_working()
    {
        using var root = new ApiFactory();
        using var f = root.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "*"));
        f.CreateClient();   // starts
    }
}
