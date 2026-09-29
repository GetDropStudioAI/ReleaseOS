using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Tests;

public class HostTests
{
    private static async Task<HttpClient> SignedIn(ApiFactory f, string role)
    {
        var c = f.CreateClient();
        var r = await c.PostAsJsonAsync("/auth/dev-login", new { email = "dev@example.com", name = "Dev User", role });
        r.EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task Healthz_is_green_and_anonymous()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Healthy", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Spa_assets_are_served_before_authorization()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/probe.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/some/deep/link")).StatusCode);
    }

    [Fact]
    public async Task Api_requires_sign_in()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Dev_login_signs_in_with_the_chosen_role()
    {
        using var f = new ApiFactory();
        var c = await SignedIn(f, Roles.RTE);
        var me = await c.GetFromJsonAsync<Me>("/api/v1/me");
        Assert.Equal("dev@example.com", me!.Email);
        Assert.Equal([Roles.RTE], me.Roles);
    }

    [Fact]
    public async Task Dev_login_rejects_unknown_role()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "a@b.c", name = "x", role = "Root" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Dev_login_is_not_available_outside_Development()
    {
        using var f = new ApiFactory("Production");
        var r = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "a@b.c", name = "x", role = Roles.RTE });
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    // Policy matrix (D14, D32): rows are policies, columns Viewer, RTE, ReleaseManager, GovernanceOfficer.
    [Theory]
    [InlineData(Policies.Read, true, true, true, true)]
    [InlineData(Policies.Plan, false, true, true, false)]
    [InlineData(Policies.Admin, false, true, true, false)]
    [InlineData(Policies.ReleaseManager, false, false, true, false)]
    [InlineData(Policies.GovernanceOfficer, false, false, false, true)]
    [InlineData(Policies.AuditRead, false, true, true, true)]
    [InlineData(Policies.FreezeApprove, false, false, true, true)]
    public async Task Policies_match_the_role_matrix(string policy, bool viewer, bool rte, bool rm, bool go)
    {
        using var f = new ApiFactory();
        var authz = f.Services.GetRequiredService<IAuthorizationService>();
        async Task<bool> Can(string role) => (await authz.AuthorizeAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test")), policy)).Succeeded;
        Assert.Equal(viewer, await Can(Roles.Viewer));
        Assert.Equal(rte, await Can(Roles.RTE));
        Assert.Equal(rm, await Can(Roles.ReleaseManager));
        Assert.Equal(go, await Can(Roles.GovernanceOfficer));
    }

    [Fact]
    public void Role_map_translates_groups_and_defaults_to_Viewer()
    {
        var map = new Dictionary<string, string> { ["grp-rte"] = Roles.RTE, ["grp-bad"] = "Root" };
        Assert.Equal([Roles.RTE], RoleMapper.Map(["grp-rte", "other"], map));
        Assert.Equal([Roles.Viewer], RoleMapper.Map(["other"], map));
        Assert.Equal([Roles.Viewer], RoleMapper.Map(["grp-bad"], map));
    }

    private record Me(string Email, string Name, string[] Roles);

    // REOS-59 / Q-006 option a: password reset is the IdP's; the app only exposes the link, anonymously, https only.
    [Theory]
    [InlineData("https://login.example.com/reset", "https://login.example.com/reset")]
    [InlineData("http://login.example.com/reset", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData(null, null)]
    public async Task Auth_config_exposes_only_an_https_reset_link(string? configured, string? expected)
    {
        using var f = new ApiFactory(passwordResetUrl: configured);
        var r = await f.CreateClient().GetAsync("/auth/config");   // no sign-in: the sign-in page needs it
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var got = body.GetProperty("passwordResetUrl");
        if (expected is null) Assert.Equal(System.Text.Json.JsonValueKind.Null, got.ValueKind);
        else Assert.Equal(expected, got.GetString());
    }
}
