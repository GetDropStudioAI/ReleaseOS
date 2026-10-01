using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Api.Realtime;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Comms;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Security scan, OWASP API Security Top 10 2023 (API1 object-level, API3 property-level, API5 function-level authorization, API6 sensitive business flows).
/// Each finding in docs/security/scan-authz.md (SEC-A1..) has a test here that failed against the unfixed code; the "Pin_" tests hold properties that were
/// checked and found sound so they cannot quietly regress.
/// </summary>
public class AuthzScanTests
{
    private const string Reason = "Hotfix for the payment outage, approved by the incident commander";
    private static string At(int hours) => DateTime.UtcNow.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("guard").GetString()!;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    // ---- SEC-A1 ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// SEC-A1 (API3 + API6): the two-person rule on freeze overrides was satisfied by whatever <c>requestedByUserId</c> the approver typed. A Release Manager
    /// could grant their own exception by naming any active RTE or Release Manager (the Freezes screen even preselects the first other person), or a user they
    /// pre-provisioned by CSV import, and the immutable override row and the evidence pack then said someone asked who never did. The named requester must now
    /// have filed the request (<c>POST /freezes/{id}/override-requests</c>) for that window and train since the last override for the pair.
    /// </summary>
    [Fact]
    public async Task SEC_A1_A_freeze_override_needs_a_request_the_named_requester_actually_filed()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var rteId = UserId(f, "rte@x.com");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var fz = (await Ok(await rm.PostAsJsonAsync("/api/v1/freezes", new { name = "Q4 close", kind = "Freeze", startsAt = At(-1), endsAt = At(2) }))).GetProperty("id").GetString()!;
        Task<HttpResponseMessage> Grant(string requester) =>
            rm.PostAsJsonAsync($"/api/v1/freezes/{fz}/overrides", new { trainId = "t3", requestedByUserId = requester, reason = Reason, expiresAt = At(3) });

        // 1. The RM names a colleague who never asked: refused, nothing written.
        Assert.Equal("OverrideNotRequested", await Guard(await Grant(rteId)));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM FreezeOverrides"));

        // 2. Nor can the RM invent a requester: a user pre-provisioned by CSV import (Admin policy, which an RM holds) never signs in and never asks.
        var csv = "Email,DisplayName,Role\r\nghost@x.com,Ghost,RTE\r\n";
        var preview = await Ok(await rm.PostAsync("/api/v1/imports/Users:preview?mode=Append&fileName=u.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv")));
        var commit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/imports/{preview.GetProperty("jobId").GetString()}:commit") { Content = JsonContent.Create(new { acknowledgeDecertify = false }) };
        commit.Headers.TryAddWithoutValidation("If-Match", preview.GetProperty("version").GetInt32().ToString());
        await Ok(await rm.SendAsync(commit));
        Assert.Equal("OverrideNotRequested", await Guard(await Grant(UserId(f, "ghost@x.com"))));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM FreezeOverrides"));

        // 3. A request for another train does not count.
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t4','Other','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        await Ok(await rte.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t4", reason = Reason }));
        Assert.Equal("OverrideNotRequested", await Guard(await Grant(rteId)));

        // 4. Once the RTE has asked for this train, the RM grants it; the row names the real requester.
        await Ok(await rte.PostAsJsonAsync($"/api/v1/freezes/{fz}/override-requests", new { trainId = "t3", reason = Reason }));
        await Ok(await Grant(rteId));
        Assert.Equal(rteId, Scalar(f, "SELECT RequestedByUserId FROM FreezeOverrides WHERE ReleaseTrainId='t3'"));

        // 5. One request, one grant: a renewal needs a new request.
        Assert.Equal("OverrideNotRequested", await Guard(await Grant(rteId)));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM FreezeOverrides"));
    }

    /// <summary>SEC-A1 escape hatch (Q-SEC-A1): <c>Freeze:OverrideRequiresRequest=false</c> restores the earlier rule (requester named by the approver); self-approval stays refused.</summary>
    [Fact]
    public async Task SEC_A1_The_request_rule_can_be_switched_off_by_configuration()
    {
        using var root = new ApiFactory();
        using var web = root.WithWebHostBuilder(b => b.UseSetting("Freeze:OverrideRequiresRequest", "false"));
        async Task<HttpClient> Login(string role, string email)
        {
            var c = web.CreateClient();
            (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
            return c;
        }
        await Login(Roles.RTE, "rte@x.com");
        var rm = await Login(Roles.ReleaseManager, "rm@x.com");
        Sql(root, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var fz = (await Ok(await rm.PostAsJsonAsync("/api/v1/freezes", new { name = "Q4 close", kind = "Freeze", startsAt = At(-1), endsAt = At(2) }))).GetProperty("id").GetString()!;
        Task<HttpResponseMessage> Grant(string requester) =>
            rm.PostAsJsonAsync($"/api/v1/freezes/{fz}/overrides", new { trainId = "t3", requestedByUserId = requester, reason = Reason, expiresAt = At(3) });
        Assert.Equal("OverrideSelfApproval", await Guard(await Grant(UserId(root, "rm@x.com"))));
        await Ok(await Grant(UserId(root, "rte@x.com")));
    }

    // ---- SEC-A2 ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// SEC-A2 (API6 + API3): <c>:complete</c> on a task that is already complete overwrote <c>CompletedByUserId</c> and <c>CompletedAt</c> (no decertify, since
    /// IsCompleted did not change). The Compliance segregation-of-duties rule reads exactly that column, so a Governance Officer who did a gate's work could have
    /// it "re-completed" by anyone allowed to complete tasks and then certify the gate; on a Certified gate it silently rewrote who did the work after the fact.
    /// Completing a completed task is now a no-op (idempotent, as marking a notification read is): nothing changes, no Version bump, no audit row.
    /// </summary>
    [Fact]
    public async Task SEC_A2_Completing_an_already_completed_task_does_not_rewrite_who_completed_it()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var (rteId, govId) = (UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
        SeedTrain(f, rteId, govId);   // g2 is a Compliance gate owned by gov, with task k2 owned by gov

        await Ok(await gov.PostAsync("/api/v1/tasks/k2:complete", null));   // the officer does the work
        var completedAt = Scalar(f, "SELECT CompletedAt FROM ChecklistTasks WHERE Id='k2'");
        var version = Scalar(f, "SELECT Version FROM ChecklistTasks WHERE Id='k2'");
        var audits = Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityId='k2'");

        await Ok(await rte.PostAsync("/api/v1/tasks/k2:complete", null));   // a planner "completes" it again
        Assert.Equal(govId, Scalar(f, "SELECT CompletedByUserId FROM ChecklistTasks WHERE Id='k2'"));
        Assert.Equal(completedAt, Scalar(f, "SELECT CompletedAt FROM ChecklistTasks WHERE Id='k2'"));
        Assert.Equal(version, Scalar(f, "SELECT Version FROM ChecklistTasks WHERE Id='k2'"));
        Assert.Equal(audits, Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityId='k2'"));

        // So the officer who did the work still cannot certify it.
        await Ok(await rte.PostAsync("/api/v1/gates/g1:start", null));
        await Ok(await rte.PostAsync("/api/v1/tasks/k1:complete", null));
        await Ok(await rte.PostAsync("/api/v1/gates/g1:certify", null));
        var r = await gov.PostAsync("/api/v1/gates/g2:certify", null);
        Assert.Equal("SegregationOfDuties", await Guard(r));
        Assert.Equal("InProgress", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g2'"));

        // Reopening an open task is a no-op too (it used to bump Version and write a "Reopen" audit row for nothing).
        var v1 = Scalar(f, "SELECT Version FROM ChecklistTasks WHERE Id='k1'");
        await Ok(await rte.PostAsync("/api/v1/tasks/k1:reopen", null));
        var v2 = Scalar(f, "SELECT Version FROM ChecklistTasks WHERE Id='k1'");
        Assert.NotEqual(v1, v2);   // a real reopen still works (and decertified g1 by trigger)
        Assert.Equal("InProgress", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));
        await Ok(await rte.PostAsync("/api/v1/tasks/k1:reopen", null));
        Assert.Equal(v2, Scalar(f, "SELECT Version FROM ChecklistTasks WHERE Id='k1'"));
    }

    // ---- SEC-A3 ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// SEC-A3 (API1/API5): a calendar feed token is a per-user bearer credential on an anonymous route. Q-053c made a deactivated user's session stop working,
    /// but the feed only checked the token, so a deactivated (off-boarded) user's feed kept serving every train, gate date and window indefinitely.
    /// </summary>
    [Fact]
    public async Task SEC_A3_A_deactivated_users_calendar_feed_stops_working()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var leaver = await As(f, Roles.Viewer, "leaver@x.com");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var tok = await Ok(await leaver.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }));
        var token = tok.GetProperty("token").GetString()!;
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(tok.GetProperty("path").GetString())).StatusCode);

        await Ok(await rte.PatchAsJsonAsync($"/api/v1/users/{UserId(f, "leaver@x.com")}", new { isActive = false }));

        foreach (var path in new[] { $"/api/v1/ics/{token}.ics", $"/api/v1/ics/{token}/all.ics", $"/api/v1/ics/{token}/mine.ics", $"/api/v1/ics/{token}/freezes.ics", $"/api/v1/ics/{token}/trains/t1.ics" })
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(path)).StatusCode);
    }

    // ---- SEC-A4 ------------------------------------------------------------------------------------------------------------------------------------

    private sealed class PlainRenderer(string dbPath) : ICommDispatchRenderer
    {
        public Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct)
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Version FROM ReleaseTrains WHERE Id=$t";
            cmd.Parameters.AddWithValue("$t", trainId);
            return Task.FromResult(new RenderedComm(text ?? "Release is go", [], DateTime.UtcNow, Convert.ToInt32(cmd.ExecuteScalar())));
        }
    }

    /// <summary>Holds each delivery until a second one arrives (or 2 s pass), so two racing dispatches are both inside the send at the same time if they can be.</summary>
    private sealed class SlowSender : ICommWebhookSender
    {
        private int _calls;
        private readonly TaskCompletionSource _two = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);
        public async Task<CommWebhookResult> SendAsync(CommWebhookTarget target, string jsonBody, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) >= 2) _two.TrySetResult();
            await Task.WhenAny(_two.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));
            return new CommWebhookResult(true, "hooks.example.test", null);
        }
    }

    /// <summary>
    /// SEC-A4 (API6, race): "a schedule item is fulfilled once" was checked before the webhook call and only re-read, without refusing, after it. Two
    /// dispatches of the same T-minus item (a double click, two tabs, a retry) both passed the check, both posted to the Teams/Slack channel and both wrote a
    /// dispatch row. Dispatches of one schedule item are now serialised, so the second one sees the item sent and is refused before anything is posted.
    /// </summary>
    [Fact]
    public async Task SEC_A4_Two_concurrent_dispatches_of_one_schedule_item_post_to_the_channel_once()
    {
        using var root = new ApiFactory();
        var sender = new SlowSender();
        using var web = root.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<ICommDispatchRenderer>(new PlainRenderer(root.DbPath));
            s.AddSingleton<ICommWebhookSender>(sender);
        }));
        var a = web.CreateClient();
        (await a.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        var b = web.CreateClient();
        (await b.PostAsJsonAsync("/auth/dev-login", new { email = "rm@x.com", name = "rm", role = Roles.ReleaseManager })).EnsureSuccessStatusCode();
        Sql(root, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.24','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','Go/No-Go','Release is go');
            INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('s1','t1','c1','2026-10-29T17:00:00Z');
            INSERT INTO {WebhookTestRows.Into} VALUES {WebhookTestRows.Row(web.Services, "w1", "release-ops", "https://hooks.example.test/services/T0/B0/x", "Teams")};");

        var body = new { scheduleItemId = "s1", channel = "Webhook", webhookDestinationId = "w1" };
        var results = await Task.WhenAll(a.PostAsJsonAsync("/api/v1/trains/t1/comms:dispatch", body), b.PostAsJsonAsync("/api/v1/trains/t1/comms:dispatch", body));

        Assert.Equal(1, sender.Calls);
        Assert.Equal("1", Scalar(root, "SELECT COUNT(*) FROM CommDispatches"));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(results, r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(CommGuards.ScheduleAlreadySent, await Guard(refused));
        Assert.Equal(Scalar(root, "SELECT Id FROM CommDispatches"), Scalar(root, "SELECT DispatchId FROM CommSchedule WHERE Id='s1'"));
    }

    // ---- pins: checked, no issue ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// API5: the hub is server-to-client only. It declares no method a client can invoke (so there is no "join group" a user could call for a train or another
    /// user), and an invocation from a signed-in Viewer is refused. A hub method added later must come with an object-level check and a change to this test.
    /// </summary>
    [Fact]
    public async Task Pin_the_hub_exposes_no_client_callable_methods()
    {
        var declared = typeof(TrainsHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name)
            .Where(n => n is not (nameof(Hub.OnConnectedAsync) or nameof(Hub.OnDisconnectedAsync))).ToList();
        Assert.Empty(declared);

        using var f = new ApiFactory();
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "v@x.com", name = "v", role = Roles.Viewer });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(f.Server.BaseAddress, TrainsHub.Path), o =>
        {
            o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers["Cookie"] = cookie;
        }).Build();
        await hub.StartAsync();
        foreach (var name in new[] { "JoinTrain", "AddToGroupAsync", "OnConnectedAsync", "SendAsync" })
            await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(name, "t1"));
    }

    /// <summary>API1: per-user objects addressed by id are refused for another user of the same role (the existing isolation tests cover one role each).</summary>
    [Fact]
    public async Task Pin_per_user_objects_are_not_reachable_by_another_planner()
    {
        using var f = new ApiFactory();
        var a = await As(f, Roles.RTE, "a@x.com");
        var b = await As(f, Roles.RTE, "b@x.com");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");

        // Calendar token
        var tok = await Ok(await a.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }));
        var tokId = tok.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/v1/me/ics-tokens/{tokId}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/v1/me/ics-tokens/{tokId}:rotate", new { scope = "all" })).StatusCode);

        // Bulk-parser preview
        var parsed = await Ok(await a.PostAsJsonAsync("/api/v1/trains/t1/tasks:parse", new { text = "// nothing yet" }));
        var previewId = parsed.GetProperty("previewId").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync("/api/v1/trains/t1/tasks:commit", new { previewId, acknowledgeDecertify = false })).StatusCode);

        // CSV import preview: B may read the shared import history but cannot commit A's preview
        var csv = "Day,Name\r\n2026-12-25,Christmas\r\n";
        var p = await Ok(await a.PostAsync("/api/v1/imports/Holidays:preview?mode=Append&fileName=h.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv")));
        var commit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/imports/{p.GetProperty("jobId").GetString()}:commit") { Content = JsonContent.Create(new { acknowledgeDecertify = false }) };
        commit.Headers.TryAddWithoutValidation("If-Match", p.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(commit)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Holidays WHERE Name='Christmas'"));

        // UI state: the same clientId under B is B's own (empty) state, never A's
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/session/tab-000001") { Content = JsonContent.Create(new { schemaVersion = 1, activeTrainId = "t1", ui = new { draft = "A's secret draft" } }) };
        Assert.Equal(HttpStatusCode.NoContent, (await a.SendAsync(put)).StatusCode);
        var other = await b.GetAsync("/api/v1/me/session/tab-000001");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.DoesNotContain("secret draft", await other.Content.ReadAsStringAsync());

        // Notifications: B's export of "my notifications" never holds A's rows
        var notifId = "n-a-1";
        Sql(f, $"INSERT INTO Notifications(Id,UserId,Kind,EntityType,EntityId,Message,CreatedAt) VALUES('{notifId}','{UserId(f, "a@x.com")}','GateReminder','ReleaseTrain','t1','for A only','2026-10-01T00:00:00Z')");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsync($"/api/v1/notifications/{notifId}:read", null)).StatusCode);
        Assert.DoesNotContain("for A only", await (await b.GetAsync("/api/v1/exports/notifications.csv")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("for A only", await (await b.GetAsync("/api/v1/me/notifications")).Content.ReadAsStringAsync());
    }
}
