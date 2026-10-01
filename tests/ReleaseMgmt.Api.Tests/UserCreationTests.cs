using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-83: POST /users adds a person ahead of their first sign-in. Admin policy (RTE or Release Manager, like PATCH /users/{id}); the email is trimmed and
/// lower-cased and is unique; the row is active and can be named as an owner at once; one audit row; the first sign-in lands on the same row.
/// </summary>
public class UserCreationTests
{
    private static async Task<HttpClient> As(ApiFactory f, string role, string email)
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    [Fact]
    public async Task An_admin_adds_a_user_who_is_active_listed_and_offered_as_an_owner_with_one_audit_row()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var r = await rm.PostAsJsonAsync("/api/v1/users", new { email = "  Dana.Ng@Example.COM ", displayName = " Dana Ng ", role = Roles.RTE, handle = "@dana" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var u = await Json(r);
        var id = u.GetProperty("id").GetString()!;
        Assert.Equal(("dana.ng@example.com", "Dana Ng", "RTE", "dana", true, 1),
            (u.GetProperty("email").GetString(), u.GetProperty("displayName").GetString(), u.GetProperty("role").GetString(), u.GetProperty("handle").GetString(), u.GetProperty("isActive").GetBoolean(), u.GetProperty("version").GetInt32()));
        Assert.True(Guid.TryParse(id, out var g) && g.Version == 7);

        Assert.Contains((await Json(await rm.GetAsync("/api/v1/users"))).EnumerateArray(), x => x.GetProperty("id").GetString() == id);
        Assert.Contains((await Json(await rm.GetAsync("/api/v1/owners"))).EnumerateArray(), x => x.GetProperty("id").GetString() == id && x.GetProperty("kind").GetString() == "user");

        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='User' AND EntityId='{id}' AND Action='Create'"));
        Assert.Equal(Scalar(f, "SELECT Id FROM Users WHERE Email='rm@x.com'"), Scalar(f, $"SELECT ActorUserId FROM AuditEvents WHERE EntityId='{id}' AND Action='Create'"));
        Assert.Contains("dana.ng@example.com", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{id}' AND Action='Create'"));
    }

    [Fact]
    public async Task The_email_is_unique_whatever_its_case_and_so_is_the_handle()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/users", new { email = "dana@example.com", displayName = "Dana", role = Roles.Viewer, handle = "dana" })).StatusCode);
        Assert.Equal("UserExists", await Guard(await rte.PostAsJsonAsync("/api/v1/users", new { email = " DANA@example.com", displayName = "Dana again", role = Roles.Viewer })));
        Assert.Equal("UserExists", await Guard(await rte.PostAsJsonAsync("/api/v1/users", new { email = "RTE@x.com", displayName = "Signed in already", role = Roles.Viewer })));
        Assert.Equal("HandleTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/users", new { email = "other@example.com", displayName = "Other", role = Roles.Viewer, handle = "@DANA" })));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Users WHERE Email LIKE 'dana@%'"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='User' AND Action='Create'"));
    }

    [Theory]
    [InlineData("not-an-email", "Name", "Viewer", null)]
    [InlineData("a b@example.com", "Name", "Viewer", null)]
    [InlineData("", "Name", "Viewer", null)]
    [InlineData("x@example.com", "  ", "Viewer", null)]
    [InlineData("x@example.com", "Name", "Admin", null)]
    [InlineData("x@example.com", "Name", null, null)]
    [InlineData("x@example.com", "Name", "Viewer", "bad handle!")]
    public async Task Bad_input_is_a_readable_422_and_writes_nothing(string email, string name, string? role, string? handle)
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal("InvalidInput", await Guard(await rte.PostAsJsonAsync("/api/v1/users", new { email, displayName = name, role, handle })));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Users"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='User' AND Action='Create'"));   // the RTE's own sign-in is audited as SignInCreate (REOS-64)
    }

    [Fact]
    public async Task Only_an_RTE_or_Release_Manager_may_add_users()
    {
        using var f = new ApiFactory();
        var body = new { email = "dana@example.com", displayName = "Dana", role = Roles.Viewer };
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.Viewer, "v@x.com")).PostAsJsonAsync("/api/v1/users", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.GovernanceOfficer, "gov@x.com")).PostAsJsonAsync("/api/v1/users", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/api/v1/users", body)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Users WHERE Email='dana@example.com'"));
        Assert.Equal(HttpStatusCode.OK, (await (await As(f, Roles.RTE, "rte@x.com")).PostAsJsonAsync("/api/v1/users", body)).StatusCode);
    }

    [Fact]
    public async Task The_first_sign_in_lands_on_the_added_row_by_email()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var id = (await Json(await rte.PostAsJsonAsync("/api/v1/users", new { email = "dana@example.com", displayName = "Dana", role = Roles.Viewer }))).GetProperty("id").GetString()!;
        // Sign-in goes through UserProvisioner, which finds the row by email (COLLATE NOCASE): whatever case the identity provider sends, it is the same person.
        // Driven through the sign-in endpoint rather than the provisioner's signature, which REOS-61 extends with the IdP subject.
        var dana = await As(f, Roles.GovernanceOfficer, "Dana@Example.com");
        Assert.Equal(id, (await Json(await dana.GetAsync("/api/v1/me"))).GetProperty("id").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Users WHERE Email='dana@example.com' COLLATE NOCASE"));
    }
}
