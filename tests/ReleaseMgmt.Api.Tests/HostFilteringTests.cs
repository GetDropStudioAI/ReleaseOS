using System.Net;
using System.Net.Http.Json;

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
}
