using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-174 (D56, D57, D63): add-on modules per workspace. Off by default; an admin enables one with a confirmed action (never a toggle); the plan can allow or deny it; every change is audited,
/// versioned (If-Match) and pushed to every open session of the workspace; a disabled module's endpoints answer 404 and its events are not sent. Two modules exist for now, 'cab' and 'ai', with no
/// feature behind them yet.
/// </summary>
public class ModuleTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string key, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceModule' AND EntityId='{key}' AND Action='{action}'");

    private static async Task<HttpResponseMessage> Post(HttpClient c, string url, int? ifMatch, string? workspace = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        if (workspace is not null) req.Headers.Add("X-Workspace", workspace);
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Module(HttpClient c, string key, string? workspace = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/modules");
        if (workspace is not null) req.Headers.Add("X-Workspace", workspace);
        var list = await Json(await c.SendAsync(req));
        return list.EnumerateArray().Single(m => m.GetProperty("key").GetString() == key);
    }

    private sealed class DenyCab : IModuleEntitlements
    {
        public Task<ModuleEntitlement> CheckAsync(string workspaceId, string moduleKey, CancellationToken ct = default) =>
            Task.FromResult(moduleKey == ModuleKeys.Cab ? new ModuleEntitlement(false, PlanSources.Plan, "The Team plan does not include the CAB module. Ask your account owner to upgrade.") : new ModuleEntitlement(true, PlanSources.Plan));
    }

    [Fact]
    public async Task Every_module_is_off_by_default_and_anyone_signed_in_can_read_the_list()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var list = await Json(await viewer.GetAsync("/api/v1/modules"));
        Assert.Equal(new[] { "cab", "ai" }, list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToArray());
        foreach (var m in list.EnumerateArray())
        {
            Assert.False(m.GetProperty("enabled").GetBoolean());
            Assert.True(m.GetProperty("entitled").GetBoolean());
            Assert.Equal(0, m.GetProperty("version").GetInt32());   // never enabled: no row, version 0 is the If-Match value for the first enable
            Assert.Equal(JsonValueKind.Null, m.GetProperty("enabledBy").ValueKind);
            Assert.True(m.GetProperty("turnsOn").GetArrayLength() > 0);   // the confirmation dialog states what turns on
        }
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM WorkspaceModules"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceModule'"));
        Assert.False(await f.Services.GetRequiredService<IModuleGate>().IsEnabledAsync("default", "cab"));
    }

    [Fact]
    public async Task Enable_then_disable_are_each_versioned_audited_and_disabling_keeps_the_row()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var enabled = await Json(await Post(rte, "/api/v1/modules/cab:enable", 0));
        Assert.True(enabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, enabled.GetProperty("version").GetInt32());
        Assert.Equal("rte", enabled.GetProperty("enabledBy").GetProperty("name").GetString());
        Assert.Equal("Default", enabled.GetProperty("planSource").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", enabled.GetProperty("enabledAt").GetString()!);
        Assert.Equal(1, Audits(f, "cab", "Enable"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM WorkspaceModules WHERE ModuleKey='cab' AND Enabled=1 AND EnabledByUserId IS NOT NULL AND LastChangedByUserId IS NOT NULL AND LastChangedAt IS NOT NULL AND Version=1"));
        Assert.Equal(UserId(f, "rte@x.com"), Scalar(f, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='WorkspaceModule' AND Action='Enable'"));
        Assert.Contains("\"enabled\":true", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityType='WorkspaceModule' AND Action='Enable'"));
        Assert.Contains("\"enabled\":false", Scalar(f, "SELECT BeforeJson FROM AuditEvents WHERE EntityType='WorkspaceModule' AND Action='Enable'"));
        Assert.True(await f.Services.GetRequiredService<IModuleGate>().IsEnabledAsync("default", "cab"));
        Assert.False((await Module(rte, "ai")).GetProperty("enabled").GetBoolean());   // the other module is unaffected

        var disabled = await Json(await Post(rte, "/api/v1/modules/cab:disable", 1));
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, disabled.GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(f, "cab", "Disable"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM WorkspaceModules WHERE ModuleKey='cab' AND Enabled=0 AND Version=2"));   // the row stays: the module's data is kept
        Assert.False(await f.Services.GetRequiredService<IModuleGate>().IsEnabledAsync("default", "cab"));

        var again = await Json(await Post(rte, "/api/v1/modules/cab:enable", 2));   // enabling again is a new audited change, not a repeat
        Assert.Equal(3, again.GetProperty("version").GetInt32());
        Assert.Equal(2, Audits(f, "cab", "Enable"));
    }

    [Fact]
    public async Task A_repeat_of_the_state_a_module_has_changes_nothing()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Post(rm, "/api/v1/modules/ai:disable", 0)).StatusCode);   // never enabled: already off
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM WorkspaceModules"));
        await Post(rm, "/api/v1/modules/ai:enable", 0);
        var repeat = await Json(await Post(rm, "/api/v1/modules/ai:enable", 1));
        Assert.Equal(1, repeat.GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(f, "ai", "Enable"));
        Assert.Equal(0, Audits(f, "ai", "Disable"));
    }

    [Fact]
    public async Task A_stale_version_is_409_with_the_current_state_and_a_missing_one_is_428_when_required()
    {
        using var f = new ApiFactory(requireIfMatch: true);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var other = await As(f, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Post(rte, "/api/v1/modules/cab:enable", 0)).StatusCode);

        var stale = await Post(other, "/api/v1/modules/cab:disable", 0);   // the other admin loaded it before the enable
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await Json(stale);
        Assert.True(body.GetProperty("current").GetProperty("enabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(0, Audits(f, "cab", "Disable"));
        Assert.True((await Module(rte, "cab")).GetProperty("enabled").GetBoolean());

        Assert.Equal((HttpStatusCode)428, (await Post(rte, "/api/v1/modules/cab:disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(other, "/api/v1/modules/cab:disable", 1)).StatusCode);
    }

    [Fact]
    public async Task Only_admins_enable_or_disable_and_every_role_can_read()
    {
        using var f = new ApiFactory();
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anon, "/api/v1/modules/cab:enable", 0)).StatusCode);
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(f, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/modules")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(c, "/api/v1/modules/cab:enable", 0)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(c, "/api/v1/modules/cab:disable", 0)).StatusCode);
        }
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM WorkspaceModules"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceModule'"));
        foreach (var role in new[] { Roles.RTE, Roles.ReleaseManager })
        {
            var c = await As(f, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/v1/modules/ai:enable", (await Module(c, "ai")).GetProperty("version").GetInt32())).StatusCode);
            await Post(c, "/api/v1/modules/ai:disable", 1);
        }
    }

    [Fact]
    public async Task An_unknown_module_is_404_and_writes_nothing()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rte, "/api/v1/modules/servicenow:enable", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rte, "/api/v1/modules/CAB:enable", 0)).StatusCode);
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM WorkspaceModules"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceModule'"));
    }

    [Fact]
    public async Task The_plan_can_deny_a_module_with_a_readable_422_and_can_never_stop_it_being_switched_off()
    {
        using var f = new ApiFactory { ConfigureServices = s => { s.RemoveAll<IModuleEntitlements>(); s.AddSingleton<IModuleEntitlements>(new DenyCab()); } };
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var cab = await Module(rte, "cab");
        Assert.False(cab.GetProperty("entitled").GetBoolean());
        Assert.Contains("does not include the CAB module", cab.GetProperty("entitlementReason").GetString());
        Assert.True((await Module(rte, "ai")).GetProperty("entitled").GetBoolean());

        var denied = await Post(rte, "/api/v1/modules/cab:enable", 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        var b = await Json(denied);
        Assert.Equal("ModuleNotEntitled", b.GetProperty("guard").GetString());
        Assert.Contains("does not include the CAB module", b.GetProperty("message").GetString());
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM WorkspaceModules"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WorkspaceModule'"));

        var ai = await Json(await Post(rte, "/api/v1/modules/ai:enable", 0));
        Assert.Equal("Plan", ai.GetProperty("planSource").GetString());   // the entitlement's source is recorded

        // a workspace that already has the module when the plan stops allowing it keeps it on, and can still switch it off
        Sql(f, $"INSERT INTO WorkspaceModules(WorkspaceId,ModuleKey,Enabled,EnabledByUserId,EnabledAt) VALUES('default','cab',1,'{UserId(f, "rte@x.com")}','2026-11-25T00:00:00Z')");
        Assert.Equal(HttpStatusCode.OK, (await Post(rte, "/api/v1/modules/cab:disable", 1)).StatusCode);
        Assert.Equal(1, Audits(f, "cab", "Disable"));
    }

    [Fact]
    public async Task A_module_is_per_workspace_and_a_member_of_another_workspace_cannot_reach_it()
    {
        using var f = new ApiFactory();
        var both = await As(f, Roles.RTE, "both@x.com");
        var onlyDefault = await As(f, Roles.RTE, "a@x.com");
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w-beta','Beta','beta','2026-10-01T00:00:00Z');
                 INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w-beta','{UserId(f, "both@x.com")}','RTE','2026-10-01T00:00:00Z');");

        Assert.Equal(HttpStatusCode.OK, (await Post(both, "/api/v1/modules/cab:enable", 0, "beta")).StatusCode);
        Assert.True((await Module(both, "cab", "beta")).GetProperty("enabled").GetBoolean());
        Assert.False((await Module(both, "cab")).GetProperty("enabled").GetBoolean());   // the default workspace is untouched
        Assert.False((await Module(onlyDefault, "cab")).GetProperty("enabled").GetBoolean());
        Assert.Equal("w-beta", Scalar(f, "SELECT WorkspaceId FROM WorkspaceModules WHERE ModuleKey='cab'"));
        Assert.Equal("w-beta", Scalar(f, "SELECT WorkspaceId FROM AuditEvents WHERE EntityType='WorkspaceModule' AND Action='Enable'"));
        var gate = f.Services.GetRequiredService<IModuleGate>();
        Assert.True(await gate.IsEnabledAsync("w-beta", "cab"));
        Assert.False(await gate.IsEnabledAsync("default", "cab"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/modules/cab:enable") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("X-Workspace", "beta");
        Assert.Equal(HttpStatusCode.NotFound, (await onlyDefault.SendAsync(req)).StatusCode);   // not a member of beta
    }

    /// <summary>The guard on a route group of a module: 404 with an empty body, like a route that does not exist, while the module is off in the caller's workspace.</summary>
    [Fact]
    public async Task A_disabled_modules_endpoints_answer_404_and_an_enabled_modules_answer_normally()
    {
        var enabled = new HashSet<(string, string)>();
        var gate = new FakeGate(enabled);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IModuleGate>(gate);
        await using var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var ws = ctx.Request.Headers["X-Test-Workspace"].ToString();
            if (ws.Length > 0) ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkspaceResolution.Claim, ws)], "test"));
            await next();
        });
        var cab = app.MapGroup("/cab").RequireModule(ModuleKeys.Cab);
        cab.MapGet("/changes", () => Results.Ok(new { changes = 0 }));
        app.MapGet("/ai/ask", () => "answer").RequireModule(ModuleKeys.Ai);
        app.MapGet("/core", () => "core");
        await app.StartAsync();
        var client = app.GetTestClient();
        Task<HttpResponseMessage> Get(string url, string? ws) { var r = new HttpRequestMessage(HttpMethod.Get, url); if (ws is not null) r.Headers.Add("X-Test-Workspace", ws); return client.SendAsync(r); }

        Assert.Equal(HttpStatusCode.OK, (await Get("/core", "default")).StatusCode);   // the core never depends on a module
        var off = await Get("/cab/changes", "default");
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Equal("", await off.Content.ReadAsStringAsync());
        Assert.Equal((await client.GetAsync("/nothing-here")).StatusCode, off.StatusCode);   // indistinguishable from a route that does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/cab/changes", null)).StatusCode);   // no workspace resolved
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/cab/changes", WorkspaceResolution.Nowhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/ai/ask", "default")).StatusCode);

        enabled.Add(("default", ModuleKeys.Cab));
        Assert.Equal(HttpStatusCode.OK, (await Get("/cab/changes", "default")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/cab/changes", "beta")).StatusCode);   // another workspace's module state
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/ai/ask", "default")).StatusCode);   // the other module is still off
        enabled.Remove(("default", ModuleKeys.Cab));
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/cab/changes", "default")).StatusCode);
    }

    private sealed class FakeGate(HashSet<(string, string)> enabled) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(string workspaceId, string moduleKey, CancellationToken ct = default) => Task.FromResult(enabled.Contains((workspaceId, moduleKey)));
    }

    private static async Task<(HubConnection Hub, Channel<string> Events)> Connect(ApiFactory f, string role, string email, string? workspace = null)
    {
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var events = Channel.CreateUnbounded<string>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains" + (workspace is null ? "" : "?workspace=" + workspace)), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;    // the in-memory test server has no real socket
                o.Headers["Cookie"] = cookie;
            }).Build();
        hub.On("ModulesChanged", () => events.Writer.TryWrite("ModulesChanged"));
        hub.On<string>("CabThing", s => events.Writer.TryWrite("CabThing:" + s));
        await hub.StartAsync();
        return (hub, events);
    }

    private static async Task<string?> Next(Channel<string> ch, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try { return await ch.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
    }

    [Fact]
    public async Task Every_open_session_of_the_workspace_is_told_when_a_module_changes_and_a_disabled_modules_events_are_not_sent()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var (watcher, events) = await Connect(f, Roles.Viewer, "watcher@x.com");   // another person, another open session
        await using (watcher)
        {
            var publisher = f.Services.GetRequiredService<IRealtimePublisher>();
            await publisher.ModuleEventAsync("default", ModuleKeys.Cab, "CabThing", ["while off"]);
            Assert.Null(await Next(events, TimeSpan.FromSeconds(1.5)));   // off: the event is dropped

            await Post(rte, "/api/v1/modules/cab:enable", 0);
            Assert.Equal("ModulesChanged", await Next(events, TimeSpan.FromSeconds(10)));
            await publisher.ModuleEventAsync("default", ModuleKeys.Cab, "CabThing", ["while on"]);
            Assert.Equal("CabThing:while on", await Next(events, TimeSpan.FromSeconds(10)));
            await publisher.ModuleEventAsync("default", ModuleKeys.Ai, "CabThing", ["ai is off"]);   // another module that is off
            Assert.Null(await Next(events, TimeSpan.FromSeconds(1.5)));

            await Post(rte, "/api/v1/modules/cab:disable", 1);
            Assert.Equal("ModulesChanged", await Next(events, TimeSpan.FromSeconds(10)));   // switching off is announced too: the entry leaves everyone's navigation
            await publisher.ModuleEventAsync("default", ModuleKeys.Cab, "CabThing", ["off again"]);
            Assert.Null(await Next(events, TimeSpan.FromSeconds(1.5)));

            await Post(rte, "/api/v1/modules/ai:disable", 0);   // a repeat changes nothing, so nobody is told
            Assert.Null(await Next(events, TimeSpan.FromSeconds(1.5)));
        }
    }
}
