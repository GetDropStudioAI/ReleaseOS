using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Reminders;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-42: Sync health endpoints (alert list and filters, history kept, resolve), the connector-wide test behind the banner, the live push, and the webhook allowlist admin.</summary>
public class SyncHealthTests
{
    private const string Secret = "T00K3N-s3cr3t";
    private static readonly string Now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string Ago(TimeSpan t) => DateTime.UtcNow.Subtract(t).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<JsonElement> GetJson(HttpClient c, string url) { var r = await c.GetAsync(url); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Json(r); }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var r = await c.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement : default);
    }

    private static string Q(string s) => s.Replace("'", "''");

    private static void Alert(ApiFactory f, string id, string source, string kind, string key, string msg = "boom", int count = 1, string? train = null, string? first = null, string? last = null,
        string? resolvedAt = null, string? resolvedBy = null) =>
        Sql(f, $@"INSERT INTO SyncAlerts(Id,ReleaseTrainId,SourceSystem,Kind,Fingerprint,ErrorMessage,OccurrenceCount,FirstOccurredAt,LastOccurredAt,IsResolved,ResolvedAt,ResolvedByUserId)
                  VALUES('{id}',{(train is null ? "NULL" : $"'{train}'")},'{source}','{kind}','{SyncAlertWriter.Fingerprint(source, kind, key)}','{Q(msg)}',{count},'{first ?? Now}','{last ?? Now}',{(resolvedAt is null ? 0 : 1)},
                  {(resolvedAt is null ? "NULL" : $"'{resolvedAt}'")},{(resolvedBy is null ? "NULL" : $"'{resolvedBy}'")})");

    private static void Connector(ApiFactory f, string source, int failures = 0, bool enabled = true, string? success = null) =>
        Sql(f, $@"INSERT INTO ConnectorState(SourceSystem,BaseUrl,IsEnabled,LastCycleStartedAt,LastCycleCompletedAt,LastSuccessAt,ConsecutiveFailures)
                  VALUES('{source}','https://{source.ToLowerInvariant()}.example.com',{(enabled ? 1 : 0)},'{Now}','{Now}',{(success is null ? "NULL" : $"'{success}'")},{failures})");

    private static void Link(ApiFactory f, string id, string source, string state, string? syncedAt = null) =>
        Sql(f, $@"INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey,SyncState,LastSyncedAt) VALUES('{id}','t1','Train','t1','{source}','K-{id}','{state}',{(syncedAt is null ? "NULL" : $"'{syncedAt}'")})");

    private static async Task<(ApiFactory F, HttpClient Rte, string RteId)> Setup(bool train = false)
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        if (train) { await As(f, Roles.GovernanceOfficer, "gov@x.com"); SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com")); }
        return (f, rte, UserId(f, "rte@x.com"));
    }

    // ---- roles ------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("/api/v1/sync/alerts")]
    [InlineData("/api/v1/sync/state")]
    [InlineData("/api/v1/sync/health")]
    [InlineData("/api/v1/sync/mismatches")]
    [InlineData("/api/v1/sync/webhook-allowlist")]
    public async Task Every_signed_in_role_reads_and_anonymous_is_refused(string url)
    {
        using var f = new ApiFactory();
        foreach (var role in Roles.All) Assert.Equal(HttpStatusCode.OK, (await (await As(f, role, $"{role}@x.com")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Writes_are_admin_only_RTE_and_ReleaseManager_may_and_Viewer_and_GovernanceOfficer_may_not()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");   // starts the host (and migrates the database) before rows are seeded
        Alert(f, "a1", "Jira", "Unreachable", "x");
        var body =new { name = "#ops", url = "https://hooks.slack.com/services/T0/B0/x" };
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(f, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Post, "/api/v1/sync/webhook-allowlist", body)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Delete, "/api/v1/sync/webhook-allowlist/nope")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Post, "/api/v1/sync/alerts/a1:resolve")).Status);
        }
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/sync/webhook-allowlist", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/sync/alerts/a1:resolve")).Status);
    }

    // ---- alerts: filters, order, history ----------------------------------------------------------------------------------------------
    [Fact]
    public async Task Alerts_filter_by_source_kind_train_and_state_and_list_open_first_newest_first()
    {
        var (f, rte, rteId) = await Setup(train: true); using var _f = f;
        Alert(f, "old", "Jira", "NotFound", "LED-1", count: 4, train: "t1", first: Ago(TimeSpan.FromHours(30)), last: Ago(TimeSpan.FromHours(3)));
        Alert(f, "new", "ServiceNow", "AuthFailed", "cr", count: 10, first: Ago(TimeSpan.FromHours(2)), last: Ago(TimeSpan.FromMinutes(1)));
        Alert(f, "mid", "Jira", "RateLimited", "search", last: Ago(TimeSpan.FromMinutes(30)));
        Alert(f, "done", "Jira", "Unreachable", "x", last: Ago(TimeSpan.FromHours(6)), resolvedAt: Ago(TimeSpan.FromHours(5)), resolvedBy: rteId);
        Alert(f, "ancient", "Backup", "BackupFailed", "b", last: Ago(TimeSpan.FromDays(20)), resolvedAt: Ago(TimeSpan.FromDays(19)));

        var all = await GetJson(rte, "/api/v1/sync/alerts");
        string[] Ids(JsonElement p) => [.. p.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
        Assert.Equal(["new", "mid", "old", "done"], Ids(all));                    // open first, each newest first; the 19-day-old resolved row is outside the 7-day window
        Assert.Equal(3, all.GetProperty("openCount").GetInt32());
        Assert.Equal(1, all.GetProperty("resolvedCount").GetInt32());
        Assert.Equal(["new", "mid", "old"], Ids(await GetJson(rte, "/api/v1/sync/alerts?state=open")));
        Assert.Equal(["done"], Ids(await GetJson(rte, "/api/v1/sync/alerts?state=resolved")));
        Assert.Equal(["done", "ancient"], Ids(await GetJson(rte, "/api/v1/sync/alerts?state=resolved&days=30")));
        Assert.Equal(["mid", "old", "done"], Ids(await GetJson(rte, "/api/v1/sync/alerts?source=Jira")));
        Assert.Equal(["new"], Ids(await GetJson(rte, "/api/v1/sync/alerts?kind=AuthFailed")));
        Assert.Equal(["old"], Ids(await GetJson(rte, "/api/v1/sync/alerts?train=t1")));
        Assert.Equal(["mid"], Ids(await GetJson(rte, "/api/v1/sync/alerts?source=Jira&state=open&kind=RateLimited")));
        Assert.Equal(["new"], Ids(await GetJson(rte, "/api/v1/sync/alerts?limit=1")));

        var row = all.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == "old");
        Assert.Equal(4, row.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal("R26.10", row.GetProperty("trainTitle").GetString());
        Assert.False(row.GetProperty("isResolved").GetBoolean());
        var resolved = all.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == "done");
        Assert.Equal("rte", resolved.GetProperty("resolvedByName").GetString());
        Assert.True(resolved.GetProperty("isResolved").GetBoolean());
        foreach (var k in new[] { "firstOccurredAt", "lastOccurredAt", "fingerprint", "message", "source", "kind", "version" }) Assert.True(row.TryGetProperty(k, out _), k);
    }

    [Theory]
    [InlineData("state=pending")]
    [InlineData("days=0")]
    [InlineData("days=abc")]
    [InlineData("limit=0")]
    public async Task A_malformed_filter_is_400_not_500(string query)
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        var (status, body) = await Send(rte, HttpMethod.Get, $"/api/v1/sync/alerts?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("InvalidFilter", body.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Repeated_failures_through_the_writer_keep_one_row_with_the_count_and_a_resolve_keeps_the_history()
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        var writer = f.Services.GetRequiredService<SyncAlertWriter>();
        string id = "";
        for (var i = 0; i < 5; i++) id = await writer.RaiseAsync("ServiceNow", "AuthFailed", "change_request", "401 on GET /api/now/table/change_request");
        var open = await GetJson(rte, "/api/v1/sync/alerts?state=open");
        Assert.Equal(1, open.GetProperty("items").GetArrayLength());
        Assert.Equal(5, open.GetProperty("items")[0].GetProperty("occurrenceCount").GetInt32());

        var version = open.GetProperty("items")[0].GetProperty("version").GetInt32();
        var (status, body) = await Send(rte, HttpMethod.Post, $"/api/v1/sync/alerts/{id}:resolve", ifMatch: version.ToString());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("isResolved").GetBoolean());
        Assert.Equal("rte", body.GetProperty("resolvedByName").GetString());

        // history kept: gone from open, present as resolved with its count, and the row still exists
        Assert.Equal(0, (await GetJson(rte, "/api/v1/sync/alerts?state=open")).GetProperty("items").GetArrayLength());
        var hist = await GetJson(rte, "/api/v1/sync/alerts?state=resolved");
        Assert.Equal(id, hist.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(5, hist.GetProperty("items")[0].GetProperty("occurrenceCount").GetInt32());
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM SyncAlerts WHERE Id='{id}' AND IsResolved=1 AND ResolvedByUserId IS NOT NULL AND ResolvedAt IS NOT NULL"));
        // a repeat after the resolve is a NEW open row (the unique index is on open fingerprints), so the history is not rewritten
        var again = await writer.RaiseAsync("ServiceNow", "AuthFailed", "change_request", "401 again");
        Assert.NotEqual(id, again);
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts"));
    }

    [Fact]
    public async Task Resolve_audits_once_refuses_twice_conflicts_on_a_stale_version_and_404s_an_unknown_id()
    {
        var (f, rte, rteId) = await Setup(); using var _f = f;
        Alert(f, "a1", "Jira", "Unreachable", "x", count: 3);
        var (s0, b0) = await Send(rte, HttpMethod.Post, "/api/v1/sync/alerts/a1:resolve", ifMatch: "7");
        Assert.Equal(HttpStatusCode.Conflict, s0);
        Assert.Equal(1, b0.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND Action='Resolve'"));

        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/sync/alerts/a1:resolve", ifMatch: "1")).Status);
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND EntityId='a1' AND Action='Resolve' AND ActorUserId='{rteId}'"));
        Assert.Equal("2", Scalar(f, "SELECT Version FROM SyncAlerts WHERE Id='a1'"));

        var (s1, b1) = await Send(rte, HttpMethod.Post, "/api/v1/sync/alerts/a1:resolve");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, s1);
        Assert.Equal(SyncGuards.AlertAlreadyResolved, b1.GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rte, HttpMethod.Post, "/api/v1/sync/alerts/nope:resolve")).Status);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND Action='Resolve'"));
    }

    // ---- connector state and the connector-wide flag ------------------------------------------------------------------------------------
    private static async Task<JsonElement> State(HttpClient c) => await GetJson(c, "/api/v1/sync/state");

    [Fact]
    public async Task With_no_connectors_or_a_healthy_one_nothing_is_connector_wide()
    {
        var (f, rte, _) = await Setup(train: true); using var _f = f;
        var empty = await State(rte);
        Assert.False(empty.GetProperty("connectorWide").GetBoolean());
        Assert.Equal(0, empty.GetProperty("connectors").GetArrayLength());

        Connector(f, "Jira", success: Now);
        Link(f, "l1", "Jira", "InSync", Now); Link(f, "l2", "Jira", "Mismatch", Now); Link(f, "l3", "Jira", "NotFound", Now); Link(f, "l4", "Jira", "InSync", Ago(TimeSpan.FromHours(2)));
        Alert(f, "nf", "Jira", "NotFound", "LED-902", train: "t1");   // one broken link is not a connector failure
        var s = await State(rte);
        Assert.False(s.GetProperty("connectorWide").GetBoolean());
        Assert.Equal(0, s.GetProperty("failing").GetArrayLength());
        var jira = s.GetProperty("connectors")[0];
        Assert.Equal("Jira", jira.GetProperty("source").GetString());
        Assert.Equal("Degraded", jira.GetProperty("state").GetString());
        var links = jira.GetProperty("links");
        Assert.Equal((4, 2, 1, 1, 1), (links.GetProperty("total").GetInt32(), links.GetProperty("inSync").GetInt32(), links.GetProperty("mismatch").GetInt32(), links.GetProperty("broken").GetInt32(), links.GetProperty("stale").GetInt32()));
        Assert.Equal(1, jira.GetProperty("openAlerts").GetInt32());
        Assert.Equal(1, s.GetProperty("openAlerts").GetInt32());
    }

    [Fact]
    public async Task An_open_AuthFailed_or_Unreachable_alert_makes_that_connector_wide_and_resolving_it_clears_the_flag()
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        Connector(f, "Jira", success: Now); Connector(f, "ServiceNow", success: Ago(TimeSpan.FromMinutes(49)));
        Alert(f, "auth", "ServiceNow", "AuthFailed", "change_request", msg: "401 on GET /api/now/table/change_request", count: 10, first: Ago(TimeSpan.FromMinutes(45)));
        var s = await State(rte);
        Assert.True(s.GetProperty("connectorWide").GetBoolean());
        var e = s.GetProperty("failing")[0];
        Assert.Equal("connector", e.GetProperty("scope").GetString());
        Assert.Equal("ServiceNow", e.GetProperty("source").GetString());
        Assert.Equal("AuthFailed", e.GetProperty("kind").GetString());
        Assert.Contains("AuthFailed", e.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("401 on GET /api/now/table/change_request", e.GetProperty("message").GetString());
        Assert.Equal("auth", e.GetProperty("alertId").GetString());
        Assert.Equal("Failing", s.GetProperty("connectors").EnumerateArray().First(c => c.GetProperty("source").GetString() == "ServiceNow").GetProperty("state").GetString());
        Assert.Equal("Healthy", s.GetProperty("connectors").EnumerateArray().First(c => c.GetProperty("source").GetString() == "Jira").GetProperty("state").GetString());

        Sql(f, "UPDATE SyncAlerts SET Kind='Unreachable' WHERE Id='auth'");   // the other connector-wide kind
        Assert.True((await State(rte)).GetProperty("connectorWide").GetBoolean());
        Sql(f, "UPDATE SyncAlerts SET Kind='RateLimited' WHERE Id='auth'");    // backing off is degraded, not down
        Assert.False((await State(rte)).GetProperty("connectorWide").GetBoolean());
        Sql(f, "UPDATE SyncAlerts SET Kind='AuthFailed' WHERE Id='auth'");
        Assert.True((await State(rte)).GetProperty("connectorWide").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, "/api/v1/sync/alerts/auth:resolve")).Status);
        Assert.False((await State(rte)).GetProperty("connectorWide").GetBoolean());
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(12, true)]
    public async Task Three_failed_cycles_in_a_row_with_no_alert_yet_is_connector_wide(int failures, bool wide)
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        Connector(f, "Jira", failures: failures);
        var s = await State(rte);
        Assert.Equal(wide, s.GetProperty("connectorWide").GetBoolean());
        if (wide) Assert.Contains("ConsecutiveFailures", s.GetProperty("failing")[0].GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Every_link_of_a_connector_failing_auth_is_connector_wide_but_one_failing_link_is_not()
    {
        var (f, rte, _) = await Setup(train: true); using var _f = f;
        Connector(f, "ServiceNow", success: Now);
        Link(f, "l1", "ServiceNow", "AuthFailed"); Link(f, "l2", "ServiceNow", "InSync", Now);
        Assert.False((await State(rte)).GetProperty("connectorWide").GetBoolean());
        Sql(f, "UPDATE ExternalLinks SET SyncState='AuthFailed' WHERE Id='l2'");
        var s = await State(rte);
        Assert.True(s.GetProperty("connectorWide").GetBoolean());
        Assert.Equal(2, s.GetProperty("failing")[0].GetProperty("links").GetInt32());
    }

    [Fact]
    public async Task A_disabled_connector_never_counts_and_a_stalled_engine_does()
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        Connector(f, "ServiceNow", failures: 9, enabled: false);
        Alert(f, "auth", "ServiceNow", "AuthFailed", "cr");
        var s = await State(rte);
        Assert.False(s.GetProperty("connectorWide").GetBoolean());
        Assert.Equal("Disabled", s.GetProperty("connectors")[0].GetProperty("state").GetString());

        Alert(f, "stall", "SyncEngine", "Stalled", "poller", msg: "No sync cycle completed for 16 minutes");
        s = await State(rte);
        Assert.True(s.GetProperty("connectorWide").GetBoolean());
        Assert.Equal("engine", s.GetProperty("failing")[0].GetProperty("scope").GetString());
        Assert.True(s.GetProperty("watchdog").GetProperty("stalled").GetBoolean());
        // other background failures (Backup, Export, Webhook) are alerts on the screen but not a connector-wide failure
        Sql(f, "UPDATE SyncAlerts SET IsResolved=1, ResolvedAt='" + Now + "' WHERE Id='stall'");
        Alert(f, "bk", "Backup", "BackupFailed", "b"); Alert(f, "wh", "Webhook", "DeliveryFailed", "w");
        Assert.False((await State(rte)).GetProperty("connectorWide").GetBoolean());
    }

    [Fact]
    public async Task Mismatched_and_broken_links_are_listed_with_the_train_and_both_sides()
    {
        var (f, rte, _) = await Setup(train: true); using var _f = f;
        Link(f, "l1", "Jira", "InSync", Now); Link(f, "l2", "Jira", "Mismatch", Now); Link(f, "l3", "ServiceNow", "NotFound");
        Sql(f, "UPDATE ExternalLinks SET ExpectedStatus='Released', LastSyncedStatus='Unreleased' WHERE Id='l2'");
        var rows = (await GetJson(rte, "/api/v1/sync/mismatches")).EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var m = rows.First(r => r.GetProperty("id").GetString() == "l2");
        Assert.Equal("R26.10", m.GetProperty("trainTitle").GetString());
        Assert.Equal("Released", m.GetProperty("expected").GetString());
        Assert.Equal("Unreleased", m.GetProperty("reported").GetString());
    }

    // ---- live push --------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_raised_and_a_resolved_alert_are_pushed_to_connected_clients()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "viewer@x.com", name = "viewer", role = "Viewer" });
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var events = Channel.CreateUnbounded<string>();
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains"), o =>
        {
            o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; o.Headers["Cookie"] = cookie;
        }).Build();
        hub.On<string>("SyncAlertRaised", id => events.Writer.TryWrite(id));
        await hub.StartAsync();

        var id = await f.Services.GetRequiredService<SyncAlertWriter>().RaiseAsync("Jira", "Unreachable", "https://x", "connect timeout");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(id, await events.Reader.ReadAsync(cts.Token));
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, $"/api/v1/sync/alerts/{id}:resolve")).Status);
        Assert.Equal(id, await events.Reader.ReadAsync(cts.Token));   // a resolve is announced too, so every banner clears
    }

    // ---- webhook allowlist ----------------------------------------------------------------------------------------------------------
    private static Task<(HttpStatusCode Status, JsonElement Body)> AddHook(HttpClient c, string url, string name = "#release-ops", string? kind = null) =>
        Send(c, HttpMethod.Post, "/api/v1/sync/webhook-allowlist", new { name, url, kind });

    [Fact]
    public async Task An_https_webhook_is_added_normalised_audited_and_its_secret_path_is_never_sent_back_or_audited()
    {
        var (f, rte, rteId) = await Setup(); using var _f = f;
        var (status, body) = await AddHook(rte, $"  https://Hooks.Slack.com:443/services/T0/B0/{Secret}  ");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("hooks.slack.com", body.GetProperty("host").GetString());
        Assert.Equal("Slack", body.GetProperty("kind").GetString());          // inferred from the host
        Assert.Equal("https://hooks.slack.com/…", body.GetProperty("displayUrl").GetString());
        var stored = Scalar(f, "SELECT ProtectedUrl FROM WebhookDestinations");   // Q-053e: encrypted at rest ...
        Assert.DoesNotContain(Secret, stored);
        Assert.Equal($"https://hooks.slack.com/services/T0/B0/{Secret}", WebhookTestRows.Vault(f.Services).Unprotect(stored));   // ... normalised: lower-case host, default port dropped
        Assert.Equal("hooks.slack.com", Scalar(f, "SELECT Host FROM WebhookDestinations"));

        var listText = await (await rte.GetAsync("/api/v1/sync/webhook-allowlist")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, listText);
        Assert.DoesNotContain(Secret, body.ToString());
        var list = JsonDocument.Parse(listText).RootElement;
        Assert.Equal("#release-ops", list[0].GetProperty("name").GetString());

        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination' AND Action='Create' AND ActorUserId='{rteId}'"));
        Assert.DoesNotContain(Secret, Scalar(f, "SELECT COALESCE(group_concat(AfterJson),'') FROM AuditEvents WHERE EntityType='WebhookDestination'"));
        Assert.Contains("hooks.slack.com", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityType='WebhookDestination'"));
    }

    [Theory]
    [InlineData("http://example.webhook.office.com/x", "WebhookNotHttps")]
    [InlineData("ftp://example.com/x", "WebhookNotHttps")]
    [InlineData("javascript:alert(1)", "WebhookNotHttps")]
    [InlineData("https://user:pass@example.com/x", "WebhookCredentialsInUrl")]
    [InlineData("https://user@example.com/x", "WebhookCredentialsInUrl")]
    [InlineData("https://127.0.0.1/x", "WebhookPrivateTarget")]
    [InlineData("https://10.1.2.3/x", "WebhookPrivateTarget")]
    [InlineData("https://192.168.0.5/x", "WebhookPrivateTarget")]
    [InlineData("https://172.16.0.1/x", "WebhookPrivateTarget")]
    [InlineData("https://169.254.169.254/latest/meta-data", "WebhookPrivateTarget")]
    [InlineData("https://[::1]/x", "WebhookPrivateTarget")]
    [InlineData("https://2130706433/x", "WebhookPrivateTarget")]
    [InlineData("https://localhost:5001/x", "WebhookPrivateTarget")]
    [InlineData("https://api.localhost/x", "WebhookPrivateTarget")]
    [InlineData("https://intranet/x", "WebhookPrivateTarget")]
    [InlineData("https://build.corp.internal/x", "WebhookPrivateTarget")]
    [InlineData("https://example.com/x#frag", "WebhookInvalidUrl")]
    [InlineData("https://exa mple.com/x", "WebhookInvalidUrl")]
    [InlineData("not a url", "WebhookInvalidUrl")]
    [InlineData("example.com/hook", "WebhookInvalidUrl")]
    [InlineData("", "WebhookInvalidUrl")]
    public async Task An_unsafe_or_malformed_address_is_refused_with_a_named_guard_and_nothing_is_stored(string url, string guard)
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        var (status, body) = await AddHook(rte, url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(guard, body.GetProperty("guard").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination'"));
    }

    [Fact]
    public async Task Private_targets_are_allowed_only_when_the_deployment_says_so_but_http_and_credentials_never_are()
    {
        using var f0 = new ApiFactory();
        using var f = f0.WithWebHostBuilder(b => b.UseSetting("Notifications:Webhooks:AllowPrivateTargets", "true"));
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte2@x.com", name = "rte2", role = "RTE" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await AddHook(c, "https://10.0.0.5:8443/hook", "internal relay")).Status);
        Assert.Equal("WebhookNotHttps", (await AddHook(c, "http://10.0.0.6/hook")).Body.GetProperty("guard").GetString());
        Assert.Equal("WebhookCredentialsInUrl", (await AddHook(c, "https://a:b@10.0.0.7/hook")).Body.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_duplicate_is_refused_even_when_written_differently()
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        Assert.Equal(HttpStatusCode.OK, (await AddHook(rte, "https://example.webhook.office.com/webhookb2/abc")).Status);
        foreach (var same in new[] { "https://example.webhook.office.com/webhookb2/abc", "HTTPS://EXAMPLE.WEBHOOK.OFFICE.COM:443/webhookb2/abc", " https://example.webhook.office.com/webhookb2/abc " })
        {
            var (status, body) = await AddHook(rte, same, "again");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal("WebhookDuplicate", body.GetProperty("guard").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await AddHook(rte, "https://example.webhook.office.com/webhookb2/other", "second")).Status);   // a different path is a different channel
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
        Assert.Equal("Teams", Scalar(f, "SELECT Kind FROM WebhookDestinations WHERE Name='second'"));
    }

    [Theory]
    [InlineData("", "https://example.com/hook", null, "WebhookInvalidInput")]
    [InlineData("  ", "https://example.com/hook", null, "WebhookInvalidInput")]
    [InlineData("ok", "https://example.com/hook", "Discord", "WebhookInvalidInput")]
    public async Task A_missing_name_or_unknown_kind_is_refused(string name, string url, string? kind, string guard)
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        var (status, body) = await AddHook(rte, url, name, kind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(guard, body.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_row_audits_it_and_honours_If_Match_and_404()
    {
        var (f, rte, rteId) = await Setup(); using var _f = f;
        var (_, added) = await AddHook(rte, $"https://example.com/hook/{Secret}", "#ops");
        var id = added.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Delete, $"/api/v1/sync/webhook-allowlist/{id}", ifMatch: "9")).Status);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
        var (status, removed) = await Send(rte, HttpMethod.Delete, $"/api/v1/sync/webhook-allowlist/{id}", ifMatch: "1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("#ops", removed.GetProperty("name").GetString());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination' AND EntityId='{id}' AND Action='Delete' AND ActorUserId='{rteId}'"));
        Assert.DoesNotContain(Secret, Scalar(f, "SELECT COALESCE(group_concat(COALESCE(BeforeJson,'')||COALESCE(AfterJson,'')),'') FROM AuditEvents WHERE EntityType='WebhookDestination'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rte, HttpMethod.Delete, $"/api/v1/sync/webhook-allowlist/{id}")).Status);
        // and it can be added again once removed
        Assert.Equal(HttpStatusCode.OK, (await AddHook(rte, $"https://example.com/hook/{Secret}", "#ops")).Status);
    }

    [Fact]
    public async Task A_destination_a_team_uses_cannot_be_removed_and_says_why()
    {
        var (f, rte, _) = await Setup(); using var _f = f;
        var (_, added) = await AddHook(rte, "https://example.com/hook/a", "#ops");
        var id = added.GetProperty("id").GetString()!;
        Sql(f, $"INSERT INTO Teams(Id,Handle,Name,WebhookDestinationId) VALUES('tm1','platform','Platform','{id}')");
        var (status, body) = await Send(rte, HttpMethod.Delete, $"/api/v1/sync/webhook-allowlist/{id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("WebhookInUse", body.GetProperty("guard").GetString());
        Assert.Contains("1 team", body.GetProperty("message").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
        var list = await GetJson(rte, "/api/v1/sync/webhook-allowlist");
        Assert.Equal(1, list[0].GetProperty("usedByTeams").GetInt32());
    }

    // ---- the URL policy on its own -----------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("https://Example.COM/Path?x=1", "https://example.com/Path?x=1", "example.com")]
    [InlineData("https://example.com", "https://example.com/", "example.com")]
    [InlineData("https://example.com:443/a", "https://example.com/a", "example.com")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a", "example.com:8443")]
    public void The_policy_normalises_case_default_port_and_empty_path(string raw, string expected, string host)
    {
        var r = WebhookUrlPolicy.Check(raw, allowPrivate: false);
        Assert.True(r.IsOk);
        Assert.Equal((expected, host), (r.Value!.Normalised, r.Value.Host));
    }

    [Theory]
    [InlineData("hooks.slack.com", "Slack")]
    [InlineData("contoso.webhook.office.com", "Teams")]
    [InlineData("prod-12.westus.logic.azure.com", "Teams")]
    [InlineData("chat.example.com", "Generic")]
    public void The_kind_is_inferred_from_the_host(string host, string kind) => Assert.Equal(kind, WebhookUrlPolicy.InferKind(host));
}
