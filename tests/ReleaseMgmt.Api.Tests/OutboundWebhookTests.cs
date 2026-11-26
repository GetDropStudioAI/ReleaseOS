using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Api.Webhooks;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Webhooks;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-164 (D61): outbound webhooks. Subscriptions (admin only, https, allow-listed, private targets refused), the secret shown once and never stored in clear, an outbox row written with the audit row
/// (including the rows a trigger writes), at-least-once delivery with exponential backoff on a frozen clock, a dead letter raising ONE alert with a count, replay, rotation, pause and delete: each audited.
/// </summary>
public class OutboundWebhookTests
{
    private const string Url = "https://hooks.example.com/releaseos/tok3n-8f2d41c9";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-26T12:00:00Z");

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string entity, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{entity}' AND Action='{action}'");
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private sealed record Env(ApiFactory F, FakeTimeProvider Clock, HttpClient Rte, string RteId) : IDisposable
    {
        public void Dispose() => F.Dispose();
        public WebhookDeliveryProcessor Processor => F.Services.GetRequiredService<WebhookDeliveryProcessor>();
        public WebhookSubscriptionService Service => F.Services.GetRequiredService<WebhookSubscriptionService>();
    }

    private static async Task<Env> Start(params (string, string)[] settings)
    {
        var clock = new FakeTimeProvider(T0);
        var f = new ApiFactory(settings: settings.ToDictionary(s => s.Item1, s => s.Item2)) { Clock = clock };
        var rte = await As(f, Roles.RTE, "rte@x.com");
        return new(f, clock, rte, UserId(f, "rte@x.com"));
    }

    private static async Task<(string Id, string Secret, JsonElement Body)> Subscribe(Env e, string name = "Dashboard", string url = Url, params string[] events)
    {
        var r = await e.Rte.PostAsJsonAsync("/api/v1/webhooks", new { name, url, eventTypes = events.Length == 0 ? new[] { "*" } : events });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        return (j.GetProperty("subscription").GetProperty("id").GetString()!, j.GetProperty("secret").GetString()!, j);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    // ---- who can see it, what is accepted ----

    [Fact]
    public async Task Only_planners_see_webhooks_a_viewer_and_a_governance_officer_get_403()
    {
        using var f = new ApiFactory();
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(f, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/webhooks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/webhooks", new { name = "x", url = Url, eventTypes = new[] { "*" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/webhooks/deliveries")).StatusCode);
        }
        foreach (var role in new[] { Roles.RTE, Roles.ReleaseManager })
        {
            var c = await As(f, role, $"{role}@x.com");
            var r = await c.GetAsync("/api/v1/webhooks");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var j = await Json(r);
            Assert.Equal(14, j.GetProperty("events").GetArrayLength());
            Assert.Equal("open", j.GetProperty("allowList").GetProperty("mode").GetString());   // Development with no list: any public host
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/webhooks")).StatusCode);
    }

    [Theory]
    [InlineData("http://hooks.example.com/in", "https")]
    [InlineData("https://user:pw@hooks.example.com/in", "credentials")]
    [InlineData("https://127.0.0.1/in", "private")]
    [InlineData("https://10.1.2.3/in", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private")]
    [InlineData("https://intranet/in", "private")]
    [InlineData("https://hooks.internal/in", "private")]
    [InlineData("https://hooks.example.com/in#frag", "fragment")]
    [InlineData("not a url", "address")]
    public async Task An_address_that_is_not_https_public_and_clean_is_refused_with_a_readable_reason(string url, string word)
    {
        using var e = await Start();
        var r = await e.Rte.PostAsJsonAsync("/api/v1/webhooks", new { name = "x", url, eventTypes = new[] { "release.created" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await Json(r);
        Assert.Equal(WebhookGuards.UrlRefused, j.GetProperty("guard").GetString());
        Assert.Contains(word, j.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM WebhookSubscriptions"));
    }

    [Fact]
    public async Task The_administrators_allow_list_decides_the_host_and_the_port()
    {
        using var e = await Start(("Webhooks:Out:AllowedHosts", "hooks.example.com, *.receivers.example.org, hooks.example.net:8443"));
        var list = await Json(await e.Rte.GetAsync("/api/v1/webhooks"));
        Assert.Equal("restricted", list.GetProperty("allowList").GetProperty("mode").GetString());
        async Task<HttpResponseMessage> Try(string u) => await e.Rte.PostAsJsonAsync("/api/v1/webhooks", new { name = u, url = u, eventTypes = new[] { "*" } });
        Assert.Equal(HttpStatusCode.Created, (await Try("https://hooks.example.com/a")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Try("https://ci.receivers.example.org/a")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Try("https://hooks.example.net:8443/a")).StatusCode);
        foreach (var bad in new[] { "https://evil.example.com/a", "https://hooks.example.net/a", "https://hooks.example.com:8443/a", "https://receivers.example.org/a" })
        {
            var r = await Try(bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            var j = await Json(r);
            Assert.Equal(WebhookGuards.UrlRefused, j.GetProperty("guard").GetString());
            Assert.Contains("allowed list", j.GetProperty("message").GetString());
        }
    }

    [Fact]
    public void With_no_list_outside_development_no_host_is_allowed_and_a_star_means_any_public_host()
    {
        static WebhookOutOptions O(bool dev, string? hosts) => WebhookOutOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
            hosts is null ? [] : new Dictionary<string, string?> { ["Webhooks:Out:AllowedHosts"] = hosts }).Build(), dev);
        Assert.Equal("closed", O(false, null).Mode);
        Assert.Equal("open", O(true, null).Mode);
        Assert.Equal("open", O(false, "*").Mode);
        Assert.Equal("restricted", O(false, "a.example.com").Mode);
        Assert.Equal("restricted", O(true, "a.example.com").Mode);
        Assert.False(O(false, null).AllowPrivateTargets);
    }

    [Fact]
    public async Task A_closed_list_refuses_every_address_with_the_way_out()
    {
        using var f = new ApiFactory
        {
            ConfigureServices = services => { services.RemoveAll<WebhookOutOptions>(); services.AddSingleton(WebhookOutOptions.From(new ConfigurationBuilder().Build(), isDevelopment: false)); },   // what a Production host with no list has
        };
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var r = await rte.PostAsJsonAsync("/api/v1/webhooks", new { name = "x", url = Url, eventTypes = new[] { "*" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("Webhooks:Out:AllowedHosts", (await Json(r)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Name_events_duplicates_and_the_limit_are_checked()
    {
        using var e = await Start(("Webhooks:Out:MaxPerWorkspace", "2"));
        async Task<string> Guard(object body) { var r = await e.Rte.PostAsJsonAsync("/api/v1/webhooks", body); Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
        Assert.Equal(WebhookGuards.Invalid, await Guard(new { name = " ", url = Url, eventTypes = new[] { "*" } }));
        Assert.Equal("FieldTooLong", await Guard(new { name = new string('x', 101), url = Url, eventTypes = new[] { "*" } }));   // the API-wide field limit answers first
        Assert.Equal(WebhookGuards.Invalid, await Guard(new { name = "x", url = Url, eventTypes = Array.Empty<string>() }));
        Assert.Equal(WebhookGuards.Invalid, await Guard(new { name = "x", url = Url, eventTypes = new[] { "release.exploded" } }));
        Assert.Equal(WebhookGuards.Invalid, await Guard(new { name = "x", url = Url, eventTypes = new[] { "*", "release.created" } }));
        await Subscribe(e);
        Assert.Equal(WebhookGuards.DuplicateUrl, await Guard(new { name = "again", url = Url, eventTypes = new[] { "*" } }));
        await Subscribe(e, "Second", "https://hooks.example.com/other");
        Assert.Equal(WebhookGuards.TooMany, await Guard(new { name = "third", url = "https://hooks.example.com/third", eventTypes = new[] { "*" } }));
    }

    // ---- the secret ----

    [Fact]
    public async Task The_secret_is_shown_once_and_is_nowhere_in_clear_not_the_database_the_audit_log_the_list_the_logs_or_the_key_files()
    {
        using var e = await Start();
        var (id, secret, created) = await Subscribe(e);
        Assert.StartsWith("whsec_", secret);
        Assert.True(secret.Length >= 40);
        Assert.False(created.GetProperty("subscription").TryGetProperty("secret", out _));

        var list = await e.Rte.GetStringAsync("/api/v1/webhooks");
        Assert.DoesNotContain(secret, list);
        Assert.DoesNotContain("tok3n-8f2d41c9", list);                  // the address is a secret too: only its host is shown
        Assert.Contains("hooks.example.com", list);
        Assert.DoesNotContain(secret, await e.Rte.GetStringAsync($"/api/v1/webhooks/deliveries"));

        // a test event goes through the whole path, so every row and log line it can leave is written before the search
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test")).StatusCode);
        e.F.WebhookSender.Answer = _ => new(false, 500, "HTTP 500");
        await e.Processor.ProcessDueAsync();
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:rotate-secret")).StatusCode);

        SqliteCheckpoint(e.F);
        var dir = Path.GetDirectoryName(e.F.DbPath)!;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[s.Length]; s.ReadExactly(bytes); }
            var text = Encoding.UTF8.GetString(bytes);
            Assert.False(text.Contains(secret), $"the first secret is in clear in {Path.GetFileName(file)}");
            Assert.False(text.Contains("tok3n-8f2d41c9"), $"the address token is in clear in {Path.GetFileName(file)}");
        }
        var secretFile = Directory.EnumerateFiles(Path.Combine(dir, "secrets", "outbound-webhooks"), "*.secret", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("whsec_", await File.ReadAllTextAsync(secretFile));   // Data Protection payload, not the secret
    }

    private static void SqliteCheckpoint(ApiFactory f)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_receiver_verifies_the_signature_with_the_secret_and_a_tampered_or_old_request_fails()
    {
        using var e = await Start();
        var (id, secret, _) = await Subscribe(e, events: "release.created");
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test")).StatusCode);
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        var (target, req) = e.F.WebhookSender.Sent.Single();
        Assert.Equal("Dashboard", target.Name);
        Assert.Equal("hooks.example.com", target.Host);
        Assert.Equal(WebhookCatalog.Ping, req.EventType);
        Assert.Equal(1, req.SecretVersion);
        Assert.StartsWith("v1=", req.Signature);
        var now = T0.ToUnixTimeSeconds();
        Assert.Equal(now, req.UnixSeconds);
        Assert.True(WebhookSignature.Verify(secret, req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now));
        Assert.False(WebhookSignature.Verify(secret, req.UnixSeconds, req.DeliveryId, req.Body + " ", req.Signature, now));                 // body changed
        Assert.False(WebhookSignature.Verify(secret, req.UnixSeconds, "another-delivery", req.Body, req.Signature, now));                   // delivery id changed
        Assert.False(WebhookSignature.Verify("whsec_wrong", req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now));                // wrong secret
        Assert.False(WebhookSignature.Verify(secret, req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now + 301));                 // replayed after the window
        Assert.True(WebhookSignature.Verify(secret, req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now + 300));                  // inside it
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("webhook.ping", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("default", body.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal("Delivered", Scalar(e.F, "SELECT Status FROM WebhookDeliveries"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveryAttempts WHERE StatusCode=200 AND AttemptNo=1"));
    }

    // ---- events become outbox rows with the audit row ----

    [Fact]
    public async Task A_release_event_is_queued_for_the_active_subscriptions_that_asked_for_it_and_for_no_one_else()
    {
        using var e = await Start();
        var (all, _, _) = await Subscribe(e, "Everything", "https://hooks.example.com/all");
        var (created, _, _) = await Subscribe(e, "Created only", "https://hooks.example.com/created", "release.created");
        var (gates, _, _) = await Subscribe(e, "Gates only", "https://hooks.example.com/gates", "gate.failed", "gate.certified");
        var (paused, _, _) = await Subscribe(e, "Paused", "https://hooks.example.com/paused");
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{paused}:pause")).StatusCode);

        var r = await e.Rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01 New year", targetReleaseDate = "2027-01-31", riskTier = "Low", products = new[] { new { productName = "Payments API", versionTag = "6.0.0", projectCode = "PAY" } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var trainId = (await Json(r)).GetProperty("id").GetString()!;

        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{all}' AND EventType='release.created'"));
        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{created}' AND EventType='release.created'"));
        Assert.Equal("0", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{gates}'"));
        Assert.Equal("0", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{paused}'"));
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries WHERE EventType NOT IN ('release.created')"));   // the gates, tasks and steps it also audited are not catalogue events

        // the same event for both subscriptions: one event id (the audit row), two delivery ids, one body
        var rows = Scalar(e.F, "SELECT COUNT(DISTINCT EventId) || '/' || COUNT(DISTINCT Id) || '/' || COUNT(DISTINCT PayloadJson) FROM WebhookDeliveries");
        Assert.Equal("1/2/1", rows);
        var auditId = Scalar(e.F, $"SELECT Id FROM AuditEvents WHERE EntityType='ReleaseTrain' AND Action='Create' AND EntityId='{trainId}'");
        Assert.Equal($"evt_{auditId}", Scalar(e.F, $"SELECT EventId FROM WebhookDeliveries WHERE SubscriptionId='{all}'"));
        Assert.Equal(auditId, Scalar(e.F, $"SELECT AuditEventId FROM WebhookDeliveries WHERE SubscriptionId='{all}'"));
        var payload = JsonDocument.Parse(Scalar(e.F, $"SELECT PayloadJson FROM WebhookDeliveries WHERE SubscriptionId='{all}'")).RootElement;
        Assert.Equal("release.created", payload.GetProperty("type").GetString());
        Assert.Equal($"evt_{auditId}", payload.GetProperty("id").GetString());
        Assert.Equal("default", payload.GetProperty("workspaceId").GetString());
        Assert.Equal(trainId, payload.GetProperty("data").GetProperty("entityId").GetString());
        Assert.Equal(trainId, payload.GetProperty("data").GetProperty("releaseId").GetString());
        Assert.Equal(e.RteId, payload.GetProperty("data").GetProperty("actorUserId").GetString());
        Assert.Equal("2026-11-26T12:00:00Z", payload.GetProperty("occurredAt").GetString());   // the injected clock, through the audit row
        Assert.Equal("Pending", Scalar(e.F, $"SELECT Status FROM WebhookDeliveries WHERE SubscriptionId='{all}'"));
        Assert.Equal("2026-11-26T12:00:00Z", Scalar(e.F, $"SELECT NextAttemptAt FROM WebhookDeliveries WHERE SubscriptionId='{all}'"));
    }

    [Fact]
    public async Task A_change_that_is_refused_queues_nothing_and_a_gate_event_is_queued_with_its_audit_row()
    {
        using var e = await Start();
        var (id, _, _) = await Subscribe(e, events: "gate.failed");
        SeedTrain(e.F, e.RteId, e.RteId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail")).StatusCode);   // Pending cannot fail
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries"));
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:start")).StatusCode);
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries"));                                                      // started: not an event anyone asked for
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail")).StatusCode);
        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{id}' AND EventType='gate.failed'"));
        var d = JsonDocument.Parse(Scalar(e.F, "SELECT PayloadJson FROM WebhookDeliveries")).RootElement.GetProperty("data");
        Assert.Equal("StageGate", d.GetProperty("entityType").GetString());
        Assert.Equal("g1", d.GetProperty("entityId").GetString());
        Assert.Equal("Fail", d.GetProperty("action").GetString());
        Assert.Equal("Failed", d.GetProperty("after").GetProperty("status").GetString());
        Assert.Equal("InProgress", d.GetProperty("before").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_that_a_trigger_writes_are_queued_too_the_PIR_of_an_unsuccessful_release_and_a_status_change_a_Go_and_the_PIR_all_arrive()
    {
        using var e = await Start();
        await Subscribe(e, events: "*");
        var rm = await As(e.F, Roles.ReleaseManager, "rm@x.com");
        Sql(e.F, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        string V() => Scalar(e.F, "SELECT Version FROM ReleaseTrains WHERE Id='t1'");
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" }, V())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/trains/t1:capture-baseline")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Executing" }, V())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/trains/t1:complete", new { closeCode = "Unsuccessful", notes = "n" }, V())).StatusCode);

        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='PostImplementationReview' AND Action='AutoRequired'"));   // written by the trigger, not by EF
        var types = Scalar(e.F, "SELECT group_concat(EventType, ',') FROM (SELECT EventType FROM WebhookDeliveries ORDER BY AuditEventId)");
        Assert.Equal("release.status_changed,gonogo.recorded,release.status_changed,release.status_changed,pir.created", types);
        // every delivery has the audit row it came from, in the same workspace, and the audit rows carry the same time
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries d LEFT JOIN AuditEvents a ON a.Id = d.AuditEventId WHERE a.Id IS NULL OR a.WorkspaceId <> d.WorkspaceId OR a.OccurredAt <> d.CreatedAt"));
    }

    [Fact]
    public async Task A_failure_alert_of_the_webhooks_themselves_is_not_posted_to_the_receiver_that_is_failing()
    {
        Assert.Equal("sync_alert.raised", WebhookCatalog.EventFor("SyncAlert", "Raised", "{\"source\":\"Jira\",\"kind\":\"Stalled\",\"key\":\"x\"}"));
        Assert.Null(WebhookCatalog.EventFor("SyncAlert", "Raised", "{\"source\":\"Webhook\",\"kind\":\"DeliveryFailed\",\"key\":\"x\"}"));
        Assert.Null(WebhookCatalog.EventFor("SyncAlert", "Repeated", "{\"occurrenceCount\":3}"));
        await Task.CompletedTask;
    }

    // ---- delivery: retries, backoff, dead letter, one alert ----

    [Fact]
    public async Task A_dead_receiver_gets_retries_with_exponential_backoff_then_the_delivery_is_dead_lettered_and_ONE_alert_row_says_how_many()
    {
        using var e = await Start(("Webhooks:Out:MaxAttempts", "3"));
        var (id, secret, _) = await Subscribe(e, events: "gate.failed");
        e.F.WebhookSender.Answer = _ => new(false, null, "HttpRequestException");   // the receiver is killed: connection refused
        SeedTrain(e.F, e.RteId, e.RteId);
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:start");
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail");

        // attempt 1 now; the next is 60 s later (base), and nothing is due before then
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Pending|1|2026-11-26T12:01:00Z|HttpRequestException", Scalar(e.F, "SELECT Status||'|'||AttemptCount||'|'||NextAttemptAt||'|'||LastError FROM WebhookDeliveries"));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());
        e.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        // attempt 2; the wait doubles to 120 s
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Pending|2|2026-11-26T12:03:00Z", Scalar(e.F, "SELECT Status||'|'||AttemptCount||'|'||NextAttemptAt FROM WebhookDeliveries"));
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts"));                // retries are not alerts
        e.Clock.Advance(TimeSpan.FromSeconds(120));
        // attempt 3 is the last: dead letter
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("DeadLetter|3|2026-11-26T12:03:00Z", Scalar(e.F, "SELECT Status||'|'||AttemptCount||'|'||FinishedAt FROM WebhookDeliveries"));
        Assert.Equal("1|2|3", Scalar(e.F, "SELECT MIN(AttemptNo)||'|'||(SELECT COUNT(*) - 1 FROM WebhookDeliveryAttempts)||'|'||MAX(AttemptNo) FROM WebhookDeliveryAttempts"));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());                              // finished: never tried again

        // each attempt was signed afresh with this attempt's time and the same delivery id
        var sent = e.F.WebhookSender.Sent.Select(s => s.Request).ToList();
        Assert.Equal(3, sent.Count);
        Assert.Single(sent.Select(s => s.DeliveryId).Distinct());
        Assert.Equal([T0.ToUnixTimeSeconds(), T0.ToUnixTimeSeconds() + 60, T0.ToUnixTimeSeconds() + 180], sent.Select(s => s.UnixSeconds));
        Assert.All(sent, s => Assert.True(WebhookSignature.Verify(secret, s.UnixSeconds, s.DeliveryId, s.Body, s.Signature, s.UnixSeconds)));

        // ONE alert, with the count, not one per attempt
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Webhook'"));
        Assert.Equal("Webhook|DeliveryFailed|1|0", Scalar(e.F, "SELECT SourceSystem||'|'||Kind||'|'||OccurrenceCount||'|'||IsResolved FROM SyncAlerts"));
        var msg = Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts");
        Assert.Contains("1 webhook delivery", msg);
        Assert.Contains("\"Dashboard\" (hooks.example.com)", msg);
        Assert.DoesNotContain("tok3n", msg);                                               // the address is a secret; the host is not
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='SyncAlert' AND Action='Raised'"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDelivery' AND Action='DeadLetter'"));
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDelivery' AND Action='Delivered'"));

        // a second event dead-letters: the SAME alert row, count 2, text says 2
        e.Clock.Advance(TimeSpan.FromMinutes(1));
        Sql(e.F, "UPDATE StageGates SET Status='InProgress' WHERE Id='g1'");
        var v = Scalar(e.F, "SELECT Version FROM StageGates WHERE Id='g1'");
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail", ifMatch: v)).StatusCode);
        for (var i = 0; i < 3; i++) { await e.Processor.ProcessDueAsync(); e.Clock.Advance(TimeSpan.FromSeconds(200)); }
        Assert.Equal("2", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries WHERE Status='DeadLetter'"));
        Assert.Equal("1|2", Scalar(e.F, "SELECT COUNT(*)||'|'||MAX(OccurrenceCount) FROM SyncAlerts WHERE SourceSystem='Webhook'"));
        Assert.Contains("2 webhook deliveries", Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts"));
        // the alert is not posted back to the failing receiver
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries WHERE EventType='sync_alert.raised'"));
        _ = id;
    }

    [Fact]
    public async Task When_the_receiver_comes_back_the_alert_resolves_and_replaying_the_dead_letters_works_and_is_audited()
    {
        using var e = await Start(("Webhooks:Out:MaxAttempts", "1"));
        var (id, _, _) = await Subscribe(e, events: "gate.failed");
        e.F.WebhookSender.Answer = _ => new(false, 503, "HTTP 503");
        SeedTrain(e.F, e.RteId, e.RteId);
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:start");
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail");
        await e.Processor.ProcessDueAsync();
        var original = Scalar(e.F, "SELECT Id FROM WebhookDeliveries");
        Assert.Equal("DeadLetter|503", Scalar(e.F, "SELECT Status||'|'||LastStatusCode FROM WebhookDeliveries"));
        Assert.Equal("0", Scalar(e.F, "SELECT IsResolved FROM SyncAlerts"));

        // replay while the receiver is still down: a new delivery, the original untouched
        e.Clock.Advance(TimeSpan.FromMinutes(5));
        var replay = await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/deliveries/{original}:replay");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var rj = await Json(replay);
        Assert.NotEqual(original, rj.GetProperty("id").GetString());
        Assert.Equal(original, rj.GetProperty("replayOfDeliveryId").GetString());
        Assert.Equal("Pending", rj.GetProperty("status").GetString());
        Assert.Equal("DeadLetter", Scalar(e.F, $"SELECT Status FROM WebhookDeliveries WHERE Id='{original}'"));
        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDelivery' AND Action='Replay' AND EntityId='{rj.GetProperty("id").GetString()}' AND ActorUserId='{e.RteId}'"));
        Assert.Contains($"\"replayOf\":\"{original}\"", Scalar(e.F, "SELECT AfterJson FROM AuditEvents WHERE Action='Replay'"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/deliveries/{rj.GetProperty("id").GetString()}:replay")).StatusCode);   // still pending

        // the receiver is back: the replay is delivered with the same event id and body, and the alert resolves itself
        e.F.WebhookSender.Answer = _ => new(true, 200, null);
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Delivered|1", Scalar(e.F, $"SELECT Status||'|'||AttemptCount FROM WebhookDeliveries WHERE Id='{rj.GetProperty("id").GetString()}'"));
        Assert.Equal("1|1", Scalar(e.F, "SELECT COUNT(*)||'|'||SUM(IsResolved) FROM SyncAlerts WHERE SourceSystem='Webhook'"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(DISTINCT EventId) FROM WebhookDeliveries"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(DISTINCT PayloadJson) FROM WebhookDeliveries"));
        var sent = e.F.WebhookSender.Sent.Select(s => s.Request).ToList();
        Assert.NotEqual(sent[0].DeliveryId, sent[1].DeliveryId);   // a replay is its own delivery

        // a delivered one can be replayed too; a paused subscription refuses; a finished one never changes back
        Assert.Equal(HttpStatusCode.Created, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/deliveries/{rj.GetProperty("id").GetString()}:replay")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:pause")).StatusCode);
        var paused = await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/deliveries/{original}:replay");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, paused.StatusCode);
        Assert.Equal(WebhookGuards.Paused, (await Json(paused)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(e.Rte, HttpMethod.Post, "/api/v1/webhooks/deliveries/nope:replay")).StatusCode);
        Assert.Equal(2, Count(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDelivery' AND Action='Replay'"));
    }

    [Fact]
    public async Task The_log_lists_deliveries_with_their_attempts_and_the_body_that_was_signed()
    {
        using var e = await Start(("Webhooks:Out:MaxAttempts", "2"));
        var (id, _, _) = await Subscribe(e);
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test");
        e.F.WebhookSender.Answer = _ => new(false, 500, "HTTP 500");
        await e.Processor.ProcessDueAsync();
        var list = await Json(await e.Rte.GetAsync($"/api/v1/webhooks/deliveries?subscriptionId={id}"));
        var d = list.GetProperty("deliveries").EnumerateArray().Single();
        Assert.Equal(("webhook.ping", "Pending", 1, 500, "HTTP 500", "Dashboard"), (d.GetProperty("eventType").GetString(), d.GetProperty("status").GetString(), d.GetProperty("attemptCount").GetInt32(), d.GetProperty("lastStatusCode").GetInt32(), d.GetProperty("lastError").GetString(), d.GetProperty("subscriptionName").GetString()));
        Assert.Equal(0, (await Json(await e.Rte.GetAsync("/api/v1/webhooks/deliveries?status=Delivered"))).GetProperty("deliveries").GetArrayLength());
        var detail = await Json(await e.Rte.GetAsync($"/api/v1/webhooks/deliveries/{d.GetProperty("id").GetString()}"));
        Assert.Equal(1, detail.GetProperty("attempts").GetArrayLength());
        Assert.Equal(500, detail.GetProperty("attempts")[0].GetProperty("statusCode").GetInt32());
        Assert.Equal("webhook.ping", JsonDocument.Parse(detail.GetProperty("payloadJson").GetString()!).RootElement.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await e.Rte.GetAsync("/api/v1/webhooks/deliveries/nope")).StatusCode);
    }

    // ---- pause, rotate, update, delete, prune ----

    [Fact]
    public async Task Pause_queues_nothing_new_and_holds_what_is_queued_and_resume_releases_it()
    {
        using var e = await Start();
        var (id, _, _) = await Subscribe(e, events: "gate.failed");
        SeedTrain(e.F, e.RteId, e.RteId);
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:start");
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail");
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries"));
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:pause")).StatusCode);
        Assert.Equal(0, await e.Processor.ProcessDueAsync());   // held
        Sql(e.F, "UPDATE StageGates SET Status='InProgress' WHERE Id='g1'");
        await Send(e.Rte, HttpMethod.Post, "/api/v1/gates/g1:fail", ifMatch: Scalar(e.F, "SELECT Version FROM StageGates WHERE Id='g1'"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM WebhookDeliveries"));   // nothing new while paused
        var again = await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:pause");   // already paused: no second audit row, no version
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, Audits(e.F, "WebhookSubscription", "Pause"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity), (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:resume")).StatusCode);
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Delivered", Scalar(e.F, "SELECT Status FROM WebhookDeliveries"));
        Assert.Equal(1, Audits(e.F, "WebhookSubscription", "Resume"));
    }

    [Fact]
    public async Task Rotating_the_secret_shows_the_new_one_once_the_old_one_stops_verifying_and_the_version_goes_up()
    {
        using var e = await Start();
        var (id, first, created) = await Subscribe(e);
        Assert.Equal(1, created.GetProperty("subscription").GetProperty("secretVersion").GetInt32());
        var r = await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:rotate-secret");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        var second = j.GetProperty("secret").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(2, j.GetProperty("subscription").GetProperty("secretVersion").GetInt32());
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test");
        await e.Processor.ProcessDueAsync();
        var req = e.F.WebhookSender.Sent.Single().Request;
        Assert.Equal(2, req.SecretVersion);
        var now = T0.ToUnixTimeSeconds();
        Assert.True(WebhookSignature.Verify(second, req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now));
        Assert.False(WebhookSignature.Verify(first, req.UnixSeconds, req.DeliveryId, req.Body, req.Signature, now));
        Assert.Equal(1, Audits(e.F, "WebhookSubscription", "RotateSecret"));
        Assert.DoesNotContain(second, Scalar(e.F, "SELECT group_concat(COALESCE(BeforeJson,'')||COALESCE(AfterJson,'')) FROM AuditEvents"));
        Assert.DoesNotContain(first, Scalar(e.F, "SELECT group_concat(COALESCE(BeforeJson,'')||COALESCE(AfterJson,'')) FROM AuditEvents"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(e.Rte, HttpMethod.Post, "/api/v1/webhooks/nope:rotate-secret")).StatusCode);
    }

    [Fact]
    public async Task Changes_check_If_Match_audit_once_and_a_repeat_is_a_no_op()
    {
        using var e = await Start();
        var (id, _, created) = await Subscribe(e);
        var v = created.GetProperty("subscription").GetProperty("version").GetInt32();
        var upd = await Send(e.Rte, HttpMethod.Put, $"/api/v1/webhooks/{id}", new { name = "Renamed", eventTypes = new[] { "gate.failed", "release.created" } }, v.ToString());
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var j = await Json(upd);
        Assert.Equal("Renamed", j.GetProperty("name").GetString());
        Assert.Equal(["release.created", "gate.failed"], j.GetProperty("eventTypes").EnumerateArray().Select(x => x.GetString()!).ToArray());   // catalogue order
        Assert.Equal(v + 1, j.GetProperty("version").GetInt32());
        var stale = await Send(e.Rte, HttpMethod.Put, $"/api/v1/webhooks/{id}", new { name = "Again", eventTypes = new[] { "*" } }, v.ToString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(v + 1, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        var same = await Send(e.Rte, HttpMethod.Put, $"/api/v1/webhooks/{id}", new { name = "Renamed", eventTypes = new[] { "release.created", "gate.failed" } }, (v + 1).ToString());
        Assert.Equal(v + 1, (await Json(same)).GetProperty("version").GetInt32());   // nothing changed: no new version
        Assert.Equal(1, Audits(e.F, "WebhookSubscription", "Update"));
        var moved = await Send(e.Rte, HttpMethod.Put, $"/api/v1/webhooks/{id}", new { name = "Renamed", url = "https://hooks.example.com/moved", eventTypes = new[] { "*" } }, (v + 1).ToString());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(2, Audits(e.F, "WebhookSubscription", "Update"));
        Assert.DoesNotContain("tok3n", Scalar(e.F, "SELECT group_concat(COALESCE(BeforeJson,'')||COALESCE(AfterJson,'')) FROM AuditEvents"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(e.Rte, HttpMethod.Put, $"/api/v1/webhooks/{id}", new { name = "x", url = "http://nope.example.com", eventTypes = new[] { "*" } })).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_subscription_its_log_and_its_secret_and_is_audited_with_the_count()
    {
        using var e = await Start();
        var (id, _, created) = await Subscribe(e);
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test");
        await e.Processor.ProcessDueAsync();
        var secretDir = Path.Combine(Path.GetDirectoryName(e.F.DbPath)!, "secrets", "outbound-webhooks");
        Assert.Single(Directory.EnumerateFiles(secretDir, "*.secret", SearchOption.AllDirectories));
        var stale = await Send(e.Rte, HttpMethod.Delete, $"/api/v1/webhooks/{id}", ifMatch: "99");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Rte, HttpMethod.Delete, $"/api/v1/webhooks/{id}", ifMatch: created.GetProperty("subscription").GetProperty("version").GetInt32().ToString())).StatusCode);
        Assert.Equal("0|0|0", Scalar(e.F, "SELECT (SELECT COUNT(*) FROM WebhookSubscriptions)||'|'||(SELECT COUNT(*) FROM WebhookDeliveries)||'|'||(SELECT COUNT(*) FROM WebhookDeliveryAttempts)"));
        Assert.Empty(Directory.EnumerateFiles(secretDir, "*.secret", SearchOption.AllDirectories));
        Assert.Contains("\"deliveriesRemoved\":1", Scalar(e.F, "SELECT AfterJson FROM AuditEvents WHERE EntityType='WebhookSubscription' AND Action='Delete'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(e.Rte, HttpMethod.Delete, $"/api/v1/webhooks/{id}")).StatusCode);
    }

    [Fact]
    public async Task Finished_deliveries_older_than_the_retention_are_pruned_and_pending_ones_never()
    {
        using var e = await Start(("Webhooks:Out:RetentionDays", "7"), ("Webhooks:Out:MaxAttempts", "1"));
        var (id, _, _) = await Subscribe(e);
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test");
        await e.Processor.ProcessDueAsync();                                   // delivered at T0
        e.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, await e.Service.PruneAsync());
        e.Clock.Advance(TimeSpan.FromDays(2));
        e.F.WebhookSender.Answer = _ => new(false, 500, "HTTP 500");
        Assert.True((await e.Service.TestAsync(id, new Actor(e.RteId))).IsOk);   // a new pending one (the clock moved days: the session cookie is old, so not through HTTP)
        Assert.Equal(1, await e.Service.PruneAsync());
        Assert.Equal("1|Pending", Scalar(e.F, "SELECT COUNT(*)||'|'||MIN(Status) FROM WebhookDeliveries"));
        Assert.Equal(1, Audits(e.F, "WebhookDelivery", "Pruned"));
    }

    [Fact]
    public async Task Every_change_to_a_subscription_writes_one_audit_row_by_the_person_in_the_same_transaction()
    {
        using var e = await Start();
        var (id, _, _) = await Subscribe(e);
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:test");
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:pause");
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:resume");
        await Send(e.Rte, HttpMethod.Post, $"/api/v1/webhooks/{id}:rotate-secret");
        await Send(e.Rte, HttpMethod.Delete, $"/api/v1/webhooks/{id}");
        Assert.Equal("Create,Delete,Pause,Resume,RotateSecret", Scalar(e.F, "SELECT group_concat(Action) FROM (SELECT Action FROM AuditEvents WHERE EntityType='WebhookSubscription' ORDER BY Action)"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDelivery' AND Action='Test'"));
        Assert.Equal("0", Scalar(e.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType IN ('WebhookSubscription','WebhookDelivery') AND (ActorUserId IS NULL OR ActorUserId <> '{e.RteId}')"));
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType IN ('WebhookSubscription','WebhookDelivery') AND OccurredAt <> '2026-11-26T12:00:00Z'"));   // the injected clock
    }

    [Fact]
    public async Task A_workspaces_events_are_not_posted_to_another_workspaces_subscription()
    {
        using var e = await Start();
        var (id, _, _) = await Subscribe(e, events: "*");
        Sql(e.F, $"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Payments EU','payments-eu','2026-10-01T00:00:00Z'); INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','{e.RteId}','RTE','2026-10-01T00:00:00Z');");
        var other = await Send(e.Rte, HttpMethod.Get, "/api/v1/webhooks");
        Assert.Single((await Json(other)).GetProperty("subscriptions").EnumerateArray());
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/webhooks"); req.Headers.Add("X-Workspace", "w2");
        Assert.Equal(0, (await Json(await e.Rte.SendAsync(req))).GetProperty("subscriptions").GetArrayLength());   // w2 sees none of the default workspace's
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/trains") { Content = JsonContent.Create(new { title = "EU R1", targetReleaseDate = "2027-01-31", riskTier = "Low", products = new[] { new { productName = "EU API", versionTag = "1.0.0", projectCode = "EU" } } }) };
        post.Headers.Add("X-Workspace", "w2");
        Assert.Equal(HttpStatusCode.OK, (await e.Rte.SendAsync(post)).StatusCode);
        Assert.Equal("0", Scalar(e.F, $"SELECT COUNT(*) FROM WebhookDeliveries WHERE SubscriptionId='{id}'"));   // the default workspace's subscription saw nothing of w2's release
    }
}
