using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-106 (D45): the switcher's list, creating a workspace and managing members. Admin-gated and workspace-scoped; each write has one audit row in the
/// workspace it concerns; a role change or removal takes effect on the person's next request; the last RTE/Release Manager and the last Governance
/// Officer cannot be removed or moved away from that duty. Judgement calls: QUESTIONS Q-106a to Q-106g.
/// </summary>
public class WorkspaceMembersTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Guard(HttpResponseMessage r, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? workspace, object? body = null, int? ifMatch = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (workspace is not null) r.Headers.Add("X-Workspace", workspace);
        if (ifMatch is not null) r.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    /// <summary>The signed-in person creates a workspace; returns its id.</summary>
    private static async Task<string> Create(HttpClient c, string name = "Payments EMEA", string slug = "payments-emea", string? region = "EU")
    {
        var r = await c.PostAsJsonAsync("/api/v1/workspaces", new { name, slug, region });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_switcher_lists_the_persons_own_workspaces_with_the_role_held_in_each_and_leaves_out_archived_ones()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('wb','Beta','beta','2026-10-01T00:00:00Z'),('wz','Zeta','zeta','2026-10-01T00:00:00Z');
                 UPDATE Workspaces SET ArchivedAt='2026-10-02T00:00:00Z' WHERE Id='wz';
                 INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('wb','{id}','Viewer','2026-10-01T00:00:00Z'),('wz','{id}','RTE','2026-10-01T00:00:00Z');
                 INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('wo','Other','other','2026-10-01T00:00:00Z');");   // not a member of 'other'
        var list = (await Json(await rte.GetAsync("/api/v1/workspaces"))).EnumerateArray().Select(w => (w.GetProperty("slug").GetString(), w.GetProperty("role").GetString())).ToList();
        Assert.Equal([("beta", "Viewer"), ("default", "RTE")], list);   // by name: Beta, then Default
    }

    [Fact]
    public async Task A_person_in_several_workspaces_and_not_the_default_one_can_still_ask_who_they_are_and_where_they_belong()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "multi@x.com");
        var id = UserId(f, "multi@x.com");
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w1','One','one','2026-10-01T00:00:00Z'),('w2','Two','two','2026-10-01T00:00:00Z');
                 INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w1','{id}','Viewer','2026-10-01T00:00:00Z'),('w2','{id}','RTE','2026-10-01T00:00:00Z');
                 DELETE FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{id}'");
        Assert.Equal("WorkspaceRequired", await Guard(await c.GetAsync("/api/v1/trains"), HttpStatusCode.BadRequest));   // every other request must still name one (Q-105a)
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
        var mine = (await Json(await c.GetAsync("/api/v1/workspaces"))).EnumerateArray().Select(w => w.GetProperty("slug").GetString()).ToList();
        Assert.Equal(["one", "two"], mine);
        // naming one works, and the role is that workspace's
        var me = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v1/me", "two")));
        Assert.Equal("w2", me.GetProperty("workspaceId").GetString());
        Assert.Equal(["RTE"], me.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).ToList());
    }

    [Fact]
    public async Task The_remembered_workspace_cookie_names_a_workspace_for_requests_that_cannot_carry_the_header_and_a_stale_one_is_ignored()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "cookie@x.com");
        var ws = await Create(c);
        var viaCookie = Req(HttpMethod.Get, "/api/v1/workspace", null); viaCookie.Headers.Add("Cookie", "reos.workspace=payments-emea");
        Assert.Equal(ws, (await Json(await c.SendAsync(viaCookie))).GetProperty("id").GetString());
        var stale = Req(HttpMethod.Get, "/api/v1/workspace", null); stale.Headers.Add("Cookie", "reos.workspace=gone");
        Assert.Equal("default", (await Json(await c.SendAsync(stale))).GetProperty("id").GetString());   // never an error; the usual rules apply
        // the header outranks the cookie
        var both = Req(HttpMethod.Get, "/api/v1/workspace", "default"); both.Headers.Add("Cookie", "reos.workspace=payments-emea");
        Assert.Equal("default", (await Json(await c.SendAsync(both))).GetProperty("id").GetString());
    }

    [Fact]
    public async Task An_admin_creates_a_workspace_and_becomes_its_first_member_with_one_audit_row_in_it_and_it_starts_empty()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var r = await rm.PostAsJsonAsync("/api/v1/workspaces", new { name = "  Payments EMEA ", slug = " Payments-EMEA ", region = " EU " });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var w = await Json(r);
        var id = w.GetProperty("id").GetString()!;
        Assert.Equal(("Payments EMEA", "payments-emea", "EU", "ReleaseManager"),
            (w.GetProperty("name").GetString(), w.GetProperty("slug").GetString(), w.GetProperty("region").GetString(), w.GetProperty("role").GetString()));
        Assert.True(Guid.TryParse(id, out var g) && g.Version == 7);
        Assert.Equal("1", Scalar(f, $"SELECT Version FROM Workspaces WHERE Id='{id}'"));
        Assert.Equal("ReleaseManager", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='{id}' AND UserId='{UserId(f, "rm@x.com")}'"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId='{id}' AND EntityType='Workspace' AND EntityId='{id}' AND Action='Create'"));
        Assert.Equal(UserId(f, "rm@x.com"), Scalar(f, $"SELECT ActorUserId FROM AuditEvents WHERE EntityType='Workspace' AND EntityId='{id}'"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId='default' AND EntityId='{id}'"));   // the default workspace's auditors do not see it
        Assert.Contains((await Json(await rm.GetAsync("/api/v1/workspaces"))).EnumerateArray(), x => x.GetProperty("id").GetString() == id);
        // empty: none of the default workspace's rows
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('td','Default train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        Assert.Equal(0, (await Json(await rm.SendAsync(Req(HttpMethod.Get, "/api/v1/trains", "payments-emea")))).GetArrayLength());
        Assert.Equal(1, (await Json(await rm.SendAsync(Req(HttpMethod.Get, "/api/v1/trains", "default")))).GetArrayLength());
    }

    [Fact]
    public async Task Creating_a_workspace_refuses_a_bad_name_slug_or_region_and_a_slug_that_is_taken_and_a_viewer_cannot_create_one()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        foreach (var bad in new object[]
        {
            new { name = "", slug = "ok", region = "EU" }, new { name = "x", slug = "", region = "EU" }, new { name = "x", slug = "Has Space", region = "EU" },
            new { name = "x", slug = "-lead", region = "EU" }, new { name = "x", slug = "trail-", region = "EU" }, new { name = "x", slug = "under_score", region = "EU" },
            new { name = "x", slug = new string('a', 41), region = "EU" }, new { name = new string('n', 101), slug = "ok", region = "EU" }, new { name = "x", slug = "ok", region = new string('r', 61) },
        })
            Assert.Equal("InvalidInput", await Guard(await rte.PostAsJsonAsync("/api/v1/workspaces", bad)));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Workspaces WHERE Id<>'default'"));

        await Create(rte, "One", "one");
        Assert.Equal("SlugTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/workspaces", new { name = "Again", slug = "ONE", region = (string?)null })));
        Assert.Equal("SlugTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/workspaces", new { name = "Default", slug = "default", region = (string?)null })));
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/workspaces", new { name = "No region", slug = "no-region" })).StatusCode);
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM Workspaces WHERE Id<>'default' AND Slug IN ('one','no-region')"));

        var viewer = await As(f, Roles.Viewer, "viewer@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/workspaces", new { name = "Nope", slug = "nope" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_adds_a_member_by_email_a_new_account_or_an_existing_one_in_the_same_shape_and_a_current_member_is_refused()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var ws = await Create(rte);
        await As(f, Roles.Viewer, "elsewhere@x.com");   // an account in the default workspace only

        var added = await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = " New.Person@Example.com ", displayName = "New Person", role = Roles.ReleaseManager }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var a = await Json(added);
        Assert.Equal(("new.person@example.com", "New Person", "ReleaseManager", 1), (a.GetProperty("email").GetString(), a.GetProperty("displayName").GetString(), a.GetProperty("role").GetString(), a.GetProperty("version").GetInt32()));
        var existing = await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "elsewhere@x.com", displayName = "Ignored Name", role = Roles.GovernanceOfficer }));
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        var e = await Json(existing);
        Assert.Equal(("elsewhere", "GovernanceOfficer"), (e.GetProperty("displayName").GetString(), e.GetProperty("role").GetString()));   // the account keeps its own name
        Assert.Equal("Viewer", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{UserId(f, "elsewhere@x.com")}'"));   // the default membership is untouched
        Assert.Equal("UserExists", await Guard(await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "elsewhere@x.com", displayName = "x", role = Roles.Viewer }))));
        Assert.Equal("InvalidInput", await Guard(await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "bad", displayName = "x", role = Roles.Viewer }))));
        Assert.Equal("InvalidInput", await Guard(await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "ok@example.com", displayName = "x", role = "Admin" }))));

        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId='{ws}' AND EntityType='WorkspaceMember' AND Action='Add' AND EntityId<>'{UserId(f, "rte@x.com")}'"));   // a new account and an existing one: one membership row each, in this workspace
        var list = (await Json(await rte.SendAsync(Req(HttpMethod.Get, "/api/v1/workspace/members", "payments-emea")))).EnumerateArray().Select(m => m.GetProperty("email").GetString()).ToList();
        Assert.Equal(["elsewhere@x.com", "new.person@example.com", "rte@x.com"], list);   // by name; the default workspace's other people are not here
    }

    [Fact]
    public async Task Changing_a_role_stamps_the_version_audits_before_and_after_checks_if_match_and_takes_effect_on_the_next_request()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Create(rte);
        var ws = Scalar(f, "SELECT Id FROM Workspaces WHERE Slug='payments-emea'");
        var pat = await As(f, Roles.Viewer, "pat@x.com");
        var patId = UserId(f, "pat@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "pat@x.com", displayName = "Pat", role = Roles.Viewer }))).StatusCode);
        // a Viewer cannot call an admin endpoint
        Assert.Equal(HttpStatusCode.Forbidden, (await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/workspace/members", "payments-emea"))).StatusCode);

        var stale = await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{patId}", "payments-emea", new { role = Roles.RTE }, ifMatch: 7));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Viewer", (await Json(stale)).GetProperty("current").GetProperty("role").GetString());

        var ok = await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{patId}", "payments-emea", new { role = Roles.RTE }, ifMatch: 1));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(("RTE", 2), ((await Json(ok)).GetProperty("role").GetString(), (await Json(ok)).GetProperty("version").GetInt32()));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId='{ws}' AND EntityType='WorkspaceMember' AND EntityId='{patId}' AND Action='ChangeRole'"));
        var row = Scalar(f, $"SELECT BeforeJson || ' -> ' || AfterJson FROM AuditEvents WHERE EntityId='{patId}' AND Action='ChangeRole'");
        Assert.Contains("\"Role\":\"Viewer\"", row); Assert.Contains("\"Role\":\"RTE\"", row);
        Assert.Equal(UserId(f, "rte@x.com"), Scalar(f, $"SELECT ActorUserId FROM AuditEvents WHERE EntityId='{patId}' AND Action='ChangeRole'"));
        Assert.Equal("Viewer", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{patId}'"));   // the other workspace is untouched

        // effect on the very next request: now an admin there, a Viewer elsewhere
        Assert.Equal(HttpStatusCode.OK, (await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/workspace/members", "payments-emea"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/workspace/members", "default"))).StatusCode);
        // the same role again changes nothing: no Version bump, no audit row
        var same = await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{patId}", "payments-emea", new { role = Roles.RTE }, ifMatch: 2));
        Assert.Equal(2, (await Json(same)).GetProperty("version").GetInt32());
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{patId}' AND Action='ChangeRole'"));
        Assert.Equal("InvalidInput", await Guard(await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{patId}", "payments-emea", new { role = "Owner" }, ifMatch: 2))));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.SendAsync(Req(HttpMethod.Patch, "/api/v1/workspace/members/nobody", "payments-emea", new { role = Roles.Viewer }, ifMatch: 1))).StatusCode);
    }

    [Fact]
    public async Task In_the_default_workspace_a_role_change_also_moves_the_accounts_role_and_a_change_elsewhere_does_not()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.Viewer, "pat@x.com");
        var pat = UserId(f, "pat@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{pat}", "default", new { role = Roles.GovernanceOfficer }, ifMatch: 1))).StatusCode);
        Assert.Equal(("GovernanceOfficer", "GovernanceOfficer"), (Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{pat}'"), Scalar(f, $"SELECT Role FROM Users WHERE Id='{pat}'")));

        await Create(rte);
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "pat@x.com", displayName = "Pat", role = Roles.Viewer }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{pat}", "payments-emea", new { role = Roles.RTE }, ifMatch: 1))).StatusCode);
        Assert.Equal("GovernanceOfficer", Scalar(f, $"SELECT Role FROM Users WHERE Id='{pat}'"));
    }

    [Fact]
    public async Task The_last_RTE_or_Release_Manager_cannot_be_removed_or_demoted_until_another_active_one_exists()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var me = UserId(f, "rte@x.com");
        // default workspace: the only admin
        Assert.Equal("LastAdmin", await Guard(await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{me}", "default", ifMatch: 1))));
        Assert.Equal("LastAdmin", await Guard(await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{me}", "default", new { role = Roles.Viewer }, ifMatch: 1))));
        Assert.Equal("LastAdmin", await Guard(await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{me}", "default", new { role = Roles.GovernanceOfficer }, ifMatch: 1))));
        Assert.Equal("RTE", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{me}'"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceMember' AND Action IN ('Remove','ChangeRole')"));   // a refusal writes nothing
        // an RTE may become a Release Manager: still an admin
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{me}", "default", new { role = Roles.ReleaseManager }, ifMatch: 1))).StatusCode);

        // a deactivated admin does not count as someone who can step in
        await As(f, Roles.RTE, "off@x.com");
        var off = UserId(f, "off@x.com");
        Sql(f, $"UPDATE Users SET IsActive=0 WHERE Id='{off}'");
        Assert.Equal("LastAdmin", await Guard(await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{me}", "default", ifMatch: 2))));
        // an active second admin lets the first go
        await As(f, Roles.RTE, "second@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{me}", "default", ifMatch: 2))).StatusCode);
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{me}'"));
    }

    [Fact]
    public async Task The_last_Governance_Officer_cannot_be_removed_or_demoted_either_but_a_workspace_with_none_may_remove_anyone_else()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var govId = UserId(f, "gov@x.com");
        await As(f, Roles.Viewer, "v@x.com");
        var viewerId = UserId(f, "v@x.com");
        Assert.Equal("LastGovernance", await Guard(await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{govId}", "default", ifMatch: 1))));
        Assert.Equal("LastGovernance", await Guard(await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{govId}", "default", new { role = Roles.RTE }, ifMatch: 1))));
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{viewerId}", "default", ifMatch: 1))).StatusCode);   // anyone else goes freely
        await As(f, Roles.GovernanceOfficer, "gov2@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{govId}", "default", ifMatch: 1))).StatusCode);
        // a new workspace has no Governance Officer at all: that is not a reason to refuse anything about its other members
        await Create(rte);
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "v@x.com", displayName = "V", role = Roles.Viewer }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{UserId(f, "v@x.com")}", "payments-emea", ifMatch: 1))).StatusCode);
    }

    [Fact]
    public async Task Removing_a_member_audits_it_leaves_their_teams_and_other_workspaces_and_ends_their_access_on_the_next_request()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Create(rte);
        var ws = Scalar(f, "SELECT Id FROM Workspaces WHERE Slug='payments-emea'");
        var pat = await As(f, Roles.RTE, "pat@x.com");
        var patId = UserId(f, "pat@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/workspace/members", "payments-emea", new { email = "pat@x.com", displayName = "Pat", role = Roles.RTE }))).StatusCode);
        var team = await Json(await rte.SendAsync(Req(HttpMethod.Post, "/api/v1/teams", "payments-emea", new { handle = "pay", name = "Pay", memberIds = new[] { patId } })));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM TeamMembers WHERE UserId='{patId}' AND TeamId='{team.GetProperty("id").GetString()}'"));
        Assert.Equal(HttpStatusCode.OK, (await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/trains", "payments-emea"))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{patId}", "payments-emea", ifMatch: 9))).StatusCode);
        var gone = await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{patId}", "payments-emea", ifMatch: 1));
        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        Assert.Equal("pat@x.com", (await Json(gone)).GetProperty("email").GetString());
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceId='{ws}' AND UserId='{patId}'"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM TeamMembers WHERE UserId='{patId}'"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM Users WHERE Id='{patId}'"));   // the account stays
        Assert.Equal("RTE", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{patId}'"));   // so does the other membership
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId='{ws}' AND EntityType='WorkspaceMember' AND EntityId='{patId}' AND Action='Remove'"));
        Assert.Contains("\"removedFromTeams\":1", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{patId}' AND Action='Remove'"));
        // next request: the workspace is not theirs any more, the same 404 as one that does not exist
        Assert.Equal("WorkspaceNotFound", await Guard(await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/trains", "payments-emea")), HttpStatusCode.NotFound));
        Assert.Equal(HttpStatusCode.OK, (await pat.SendAsync(Req(HttpMethod.Get, "/api/v1/trains", "default"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{patId}", "payments-emea", ifMatch: 1))).StatusCode);   // already gone
    }

    [Fact]
    public async Task Member_management_never_reaches_another_workspaces_people_and_a_viewer_is_refused_everywhere()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Create(rte);
        var other = await As(f, Roles.RTE, "other@x.com");   // an admin of the default workspace only
        var otherId = UserId(f, "other@x.com");
        // from the new workspace, the default workspace's people are not found
        Assert.Equal(HttpStatusCode.NotFound, (await rte.SendAsync(Req(HttpMethod.Patch, $"/api/v1/workspace/members/{otherId}", "payments-emea", new { role = Roles.Viewer }, ifMatch: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.SendAsync(Req(HttpMethod.Delete, $"/api/v1/workspace/members/{otherId}", "payments-emea", ifMatch: 1))).StatusCode);
        Assert.Equal("RTE", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{otherId}'"));
        // a member of no workspace named holds no role there
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(Req(HttpMethod.Get, "/api/v1/workspace/members", "payments-emea"))).StatusCode);
        var viewer = await As(f, Roles.Viewer, "viewer@x.com");
        foreach (var (m, url) in new[] { (HttpMethod.Get, "/api/v1/workspace/members"), (HttpMethod.Post, "/api/v1/workspace/members"), (HttpMethod.Patch, $"/api/v1/workspace/members/{otherId}"), (HttpMethod.Delete, $"/api/v1/workspace/members/{otherId}"), (HttpMethod.Post, "/api/v1/workspaces") })
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(Req(m, url, "default", new { }, 1))).StatusCode);
    }
}
