using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-104 (D45, Q-104a): the default workspace and its memberships. Until REOS-105 moves the triggers and policies onto memberships, Users.Role is the
/// role that counts, and every path that creates a user or changes Users.Role keeps the default membership equal to it. The default workspace's name and
/// region come from Workspace:DefaultName and Workspace:Region when they are set.
/// </summary>
public class WorkspaceTests
{
    private static string Member(ApiFactory f, string email) =>
        Scalar(f, $"SELECT COALESCE((SELECT m.Role || ',' || m.Version FROM WorkspaceMembers m JOIN Users u ON u.Id = m.UserId WHERE u.Email = '{email}' AND m.WorkspaceId = 'default'), 'none')");

    [Fact]
    public async Task A_user_created_by_sign_in_or_by_an_admin_is_a_member_of_the_default_workspace_with_their_role()
    {
        using var f = new ApiFactory();
        var rm = f.CreateClient();
        (await rm.PostAsJsonAsync("/auth/dev-login", new { email = "rm@x.com", name = "Rel Mgr", role = Roles.ReleaseManager })).EnsureSuccessStatusCode();
        Assert.Equal("ReleaseManager,1", Member(f, "rm@x.com"));

        Assert.Equal(HttpStatusCode.OK, (await rm.PostAsJsonAsync("/api/v1/users", new { email = "dana@example.com", displayName = "Dana", role = Roles.RTE })).StatusCode);
        Assert.Equal("RTE,1", Member(f, "dana@example.com"));

        // the identity provider sends another role at the next sign-in: Users.Role and the membership change together; the same role again changes nothing
        var dana = f.CreateClient();
        (await dana.PostAsJsonAsync("/auth/dev-login", new { email = "dana@example.com", name = "Dana", role = Roles.GovernanceOfficer })).EnsureSuccessStatusCode();
        Assert.Equal("GovernanceOfficer", Scalar(f, "SELECT Role FROM Users WHERE Email = 'dana@example.com'"));
        Assert.Equal("GovernanceOfficer,2", Member(f, "dana@example.com"));
        (await dana.PostAsJsonAsync("/auth/dev-login", new { email = "dana@example.com", name = "Dana", role = Roles.GovernanceOfficer })).EnsureSuccessStatusCode();
        Assert.Equal("GovernanceOfficer,2", Member(f, "dana@example.com"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Users u WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = u.Id AND m.WorkspaceId = 'default' AND m.Role = u.Role)"));
    }

    [Fact]
    public async Task A_user_left_without_a_membership_gets_one_when_their_role_next_changes()
    {
        using var f = new ApiFactory();
        (await f.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();
        Sql(f, "INSERT INTO Users (Id, Email, DisplayName, Role) VALUES ('u-raw', 'raw@x.com', 'Raw', 'Viewer')");   // a row written by SQL, as fixtures do
        Assert.Equal("none", Member(f, "raw@x.com"));
        (await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "raw@x.com", name = "Raw", role = Roles.RTE })).EnsureSuccessStatusCode();
        Assert.Equal("RTE,1", Member(f, "raw@x.com"));
    }

    [Fact]
    public async Task The_default_workspace_is_named_from_configuration_once_and_audited()
    {
        using var plain = new ApiFactory();
        (await plain.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();
        Assert.Equal("Default|default||1", Scalar(plain, "SELECT Name || '|' || Slug || '|' || COALESCE(Region, '') || '|' || Version FROM Workspaces WHERE Id = 'default'"));
        Assert.Equal("0", Scalar(plain, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType = 'Workspace'"));

        using var f = new ApiFactory();
        Func<IWebHostBuilder, IWebHostBuilder> settings = b => b.UseSetting("Workspace:DefaultName", " Payments Org ").UseSetting("Workspace:Region", "EU West");
        using (var first = f.WithWebHostBuilder(b => settings(b))) (await first.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();
        Assert.Equal("Payments Org|EU West|2", Scalar(f, "SELECT Name || '|' || Region || '|' || Version FROM Workspaces WHERE Id = 'default'"));
        Assert.Equal("default|Configure|default|", Scalar(f, "SELECT EntityId || '|' || Action || '|' || WorkspaceId || '|' || COALESCE(ActorUserId, '') FROM AuditEvents WHERE EntityType = 'Workspace'"));
        Assert.Contains("\"Name\":\"Default\"", Scalar(f, "SELECT BeforeJson FROM AuditEvents WHERE EntityType = 'Workspace'"));

        using (var again = f.WithWebHostBuilder(b => settings(b))) (await again.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();   // a restart with the same settings writes nothing
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType = 'Workspace'"));
        Assert.Equal("2", Scalar(f, "SELECT Version FROM Workspaces WHERE Id = 'default'"));
    }

    [Fact]
    public void A_name_longer_than_the_schema_allows_stops_the_start()
    {
        using var f = new ApiFactory();
        using var bad = f.WithWebHostBuilder(b => b.UseSetting("Workspace:DefaultName", new string('x', 101)));
        var e = Assert.ThrowsAny<Exception>(() => bad.CreateClient());
        Assert.Contains("Workspace:DefaultName", e.ToString());
    }
}
