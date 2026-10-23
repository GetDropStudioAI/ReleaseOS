using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-111 and REOS-112 over HTTP: the metrics ingest, the reads the monitoring screen binds to, control calls and their failures, automatic rollback, run pause, the signing key, and workspace scope.</summary>
public class DeploymentOpsApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Guard(HttpResponseMessage r, HttpStatusCode status = HttpStatusCode.UnprocessableEntity) { Assert.Equal(status, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private sealed record Ctx(HttpClient Rte, HttpClient Ingest, string EnvId, string DeploymentId, string Secret);

    /// <summary>A Production environment with thresholds and a control webhook with a signing key, a catalog service, an ingest key, and a running canary deployment "ci-1" (5, 25, 50, 100; at 5 %).</summary>
    private static async Task<Ctx> Setup(ApiFactory f, string strategy = "Canary")
    {
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var env = await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production", errorRateMaxPct = 0.5, latencyP95MaxMs = 300, availabilityMinPct = 99.5, sloAvailabilityPct = 99.9, thresholdForSeconds = 120, canaryObservationSeconds = 600 }));
        var envId = env.GetProperty("id").GetString()!;
        Assert.Equal(120, env.GetProperty("thresholdForSeconds").GetInt32());
        Sql(f, $"INSERT INTO {WebhookTestRows.Into} VALUES {WebhookTestRows.Row(f.Services, "wh1", "Deploy tool", "https://deploy.example.com/hooks/abc123", "Generic")}");
        Assert.Equal(HttpStatusCode.OK, (await rte.PatchAsJsonAsync($"/api/v1/environments/{envId}", new { controlWebhookDestinationId = "wh1" })).StatusCode);
        var key = await rte.PostAsync($"/api/v1/environments/{envId}/control-webhook:signing-key", null);
        Assert.Equal(HttpStatusCode.Created, key.StatusCode);
        var secret = (await Json(key)).GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API" })).StatusCode);
        var made = await rte.PostAsJsonAsync("/api/v1/ingest-keys", new { name = "CI payments" });
        var ingest = IngestTestKit.Client(f, (await Json(made)).GetProperty("secret").GetString()!);
        var dep = new Dictionary<string, object?> { ["externalId"] = "ci-1", ["serviceKey"] = "payments-api", ["environment"] = "Production", ["releaseVersion"] = "4.5.0", ["strategy"] = strategy, ["state"] = "Running", ["trafficPercent"] = 5 };
        if (strategy == "Canary") dep["canarySteps"] = new[] { 5, 25, 50, 100 };
        var created = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", dep);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return new(rte, ingest, envId, (await Json(created)).GetProperty("id").GetString()!, secret);
    }

    private static object Sample(string series, DateTime at, double? er = null, double? lat = null, double? av = null, long? req = null) => new { series, at = Iso(at), errorRatePct = er, latencyP95Ms = lat, availabilityPct = av, requestCount = req };
    private static object[] Breaching(string series, int count = 5, double er = 0.9)
    {
        var from = DateTime.UtcNow.AddMinutes(-5);   // five samples 30 s apart: the last is 3 minutes ago, 120 s after the first
        return [.. Enumerable.Range(0, count).Select(i => Sample(series, from.AddSeconds(30 * i), er: er))];
    }

    // ---- telemetry -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_ingest_stores_samples_detects_the_breach_and_the_monitoring_reads_return_what_the_charts_need()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var posted = await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var r = await Json(posted);
        Assert.Equal((5, 0, 1), (r.GetProperty("accepted").GetInt32(), r.GetProperty("duplicates").GetInt32(), r.GetProperty("breachesConfirmed").GetInt32()));
        // a replay: nothing accepted, nothing written
        var audits = Count(f, "SELECT COUNT(*) FROM AuditEvents");
        var replay = await Json(await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") }));
        Assert.True(replay.GetProperty("accepted").GetInt32() == 0 || replay.GetProperty("duplicates").GetInt32() > 0);

        var metrics = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/metrics"));
        Assert.Equal("Canary", metrics.GetProperty("strategy").GetString());
        Assert.NotEqual(JsonValueKind.Null, metrics.GetProperty("canaryStartedAt").ValueKind);
        Assert.Equal(0.5, metrics.GetProperty("thresholds").GetProperty("errorRateMaxPct").GetDouble());
        Assert.Equal(120, metrics.GetProperty("thresholds").GetProperty("forSeconds").GetInt32());
        Assert.Equal(99.9, metrics.GetProperty("slo").GetProperty("availabilityPct").GetDouble());
        Assert.Equal(5, metrics.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("series").GetString() == "Canary").GetProperty("points").GetArrayLength());
        Assert.Equal("Breached", metrics.GetProperty("breaches")[0].GetProperty("status").GetString());
        var one = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/metrics?series=Stable"));
        Assert.Equal(["Stable"], one.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("series").GetString()!).ToArray());
        Assert.Equal("MetricInvalid", await Guard(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/metrics?series=Beta")));
        Assert.Equal("MetricInvalid", await Guard(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/metrics?from=yesterday")));

        var canary = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/canary"));
        Assert.Equal((1, 4, "Fail"), (canary.GetProperty("currentStep").GetInt32(), canary.GetProperty("stepCount").GetInt32(), canary.GetProperty("result").GetString()));
        Assert.Equal(25.0, canary.GetProperty("nextTrafficPercent").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, canary.GetProperty("nextPromotionAt").ValueKind);
        var breaches = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/breaches"));
        Assert.Equal(("ErrorRate", "Canary"), (breaches[0].GetProperty("metric").GetString(), breaches[0].GetProperty("series").GetString()));
        var mon = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/monitoring"));
        foreach (var part in new[] { "deployment", "thresholds", "slo", "canary", "breaches", "rollback", "actions", "latest", "sloStatus" }) Assert.True(mon.TryGetProperty(part, out _), part);
        Assert.False(mon.GetProperty("rollback").GetProperty("armed").GetBoolean());
        Assert.True(mon.GetProperty("rollback").GetProperty("canArm").GetBoolean());
        var slo = await Json(await c.Rte.GetAsync($"/api/v1/environments/{c.EnvId}/slo"));
        Assert.Equal(30, slo.GetProperty("days").GetInt32());
        Assert.Equal(99.9, slo.GetProperty("availability").GetProperty("objective").GetDouble());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/deployments/nope/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/deployments/nope/monitoring")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/environments/nope/slo")).StatusCode);
        Assert.True(Count(f, "SELECT COUNT(*) FROM AuditEvents") >= audits);
        // any signed-in role reads them
        Assert.Equal(HttpStatusCode.OK, (await (await As(f, Roles.Viewer, "v@x.com")).GetAsync($"/api/v1/deployments/{c.DeploymentId}/monitoring")).StatusCode);
    }

    [Fact]
    public async Task The_ingest_refuses_bad_samples_unknown_deployments_and_everything_without_a_key()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        Assert.Equal("MetricInvalid", await Guard(await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = new[] { Sample("Canary", DateTime.UtcNow, er: 140) } })));
        Assert.Equal("MetricInvalid", await Guard(await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Array.Empty<object>() })));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "nope", samples = new[] { Sample("Canary", DateTime.UtcNow, er: 1) } })).StatusCode);
        var longId = new string('x', 201);
        Assert.Equal("FieldTooLong", await Guard(await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = longId, samples = new[] { Sample("Canary", DateTime.UtcNow, er: 1) } })));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM MetricSamples"));
        // no key, a wrong key and a signed-in person's cookie all get 401
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await IngestTestKit.Client(f, "rk_" + new string('a', 43)).PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Rte.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") })).StatusCode);
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM MetricSamples"));
    }

    [Fact]
    public async Task A_key_of_another_workspace_cannot_post_samples_for_this_workspaces_deployment_and_its_members_cannot_read_them()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") });
        var beta = await WorkspaceIsolationTests.InB(f, Roles.RTE, "rte@beta.test");
        var betaKey = IngestTestKit.Client(f, IngestTestKit.Key(f, WorkspaceIsolationTests.B, "beta key"));
        Assert.Equal(HttpStatusCode.NotFound, (await betaKey.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = new[] { Sample("Canary", DateTime.UtcNow, er: 9) } })).StatusCode);
        Assert.Equal(5, Count(f, "SELECT COUNT(*) FROM MetricSamples"));
        foreach (var url in new[] { $"/api/v1/deployments/{c.DeploymentId}/metrics", $"/api/v1/deployments/{c.DeploymentId}/canary", $"/api/v1/deployments/{c.DeploymentId}/breaches", $"/api/v1/deployments/{c.DeploymentId}/monitoring",
                     $"/api/v1/deployments/{c.DeploymentId}/control-actions", $"/api/v1/deployments/{c.DeploymentId}/rollback", $"/api/v1/environments/{c.EnvId}/slo" })
            Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync("/api/v1/w/beta" + url[7..])).StatusCode);
        foreach (var action in new[] { "pause", "resume", "promote", "rollback", "arm-rollback", "disarm-rollback" })
            Assert.Equal(HttpStatusCode.NotFound, (await beta.PostAsJsonAsync($"/api/v1/w/beta/deployments/{c.DeploymentId}:{action}", new { confirm = true, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await beta.PostAsync($"/api/v1/w/beta/environments/{c.EnvId}/control-webhook:signing-key", null)).StatusCode);
        Assert.Empty(f.ControlSender.Sent);
    }

    // ---- control ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_pause_calls_the_signed_webhook_and_is_listed_audited_and_role_checked()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        Assert.Equal("ConfirmationRequired", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { })));
        Assert.Equal("ConfirmationRequired", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { confirm = false })));
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
            Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, role, $"{role}@x.com")).PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { confirm = true })).StatusCode);
        Assert.Empty(f.ControlSender.Sent);

        var ok = await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { confirm = true, reason = "Error rate is climbing" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var action = await Json(ok);
        Assert.Equal(("Succeeded", "Manual", "Pause"), (action.GetProperty("status").GetString(), action.GetProperty("source").GetString(), action.GetProperty("action").GetString()));
        var (target, delivery) = Assert.Single(f.ControlSender.Sent);
        Assert.Equal("deploy.example.com", target.Host);
        Assert.Equal(action.GetProperty("id").GetString(), delivery.DeliveryId);
        Assert.Equal(ControlSignature.Sign(c.Secret, delivery.UnixSeconds, delivery.DeliveryId, delivery.Body), delivery.Signature);
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='DeploymentControl' AND Action='Request'"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='DeploymentControl' AND Action='Succeeded'"));
        var listed = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/control-actions"));
        Assert.Equal("Pause", listed[0].GetProperty("action").GetString());
        Assert.Equal("rte", listed[0].GetProperty("requestedByName").GetString());
        // an action the state does not allow, and one that is not an action
        Assert.Equal("IllegalControlAction", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:resume", new { confirm = true })));
        Assert.Equal("ControlInvalid", await Guard(await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:rollback", new { confirm = true })));   // a rollback says why
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.PostAsJsonAsync("/api/v1/deployments/nope:pause", new { confirm = true })).StatusCode);
    }

    [Fact]
    public async Task A_control_call_that_fails_answers_502_with_the_action_raises_an_alert_and_pushes_it_live()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var events = Channel.CreateUnbounded<string>();
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email = "watch@x.com", name = "watch", role = Roles.RTE });
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var hub = new HubConnectionBuilder().WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains"), o => { o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; o.Headers["Cookie"] = cookie; }).Build();
        hub.On<string, int, string?>("DeploymentChanged", (id, _, _) => events.Writer.TryWrite("DeploymentChanged " + id));
        hub.On<string>("SyncAlertRaised", id => events.Writer.TryWrite("SyncAlertRaised " + id));
        await hub.StartAsync();
        try
        {
            f.ControlSender.Answer = _ => new(false, 503, "deploy.example.com", "HTTP 503");
            var res = await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { confirm = true, reason = "Hold" });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            var body = await Json(res);
            Assert.Equal("ControlFailed", body.GetProperty("guard").GetString());
            Assert.Contains("HTTP 503", body.GetProperty("message").GetString());
            Assert.Equal(("Failed", 503), (body.GetProperty("action").GetProperty("status").GetString(), body.GetProperty("action").GetProperty("httpStatus").GetInt32()));
            // visible afterwards: the action, the alert and the live pushes
            var listed = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/control-actions"));
            Assert.Equal("Failed", listed[0].GetProperty("status").GetString());
            var alerts = await Json(await c.Rte.GetAsync("/api/v1/sync/alerts"));
            Assert.Contains(alerts.GetProperty("items").EnumerateArray(), a => a.GetProperty("source").GetString() == "Deployment" && a.GetProperty("kind").GetString() == "ControlFailed" && !a.GetProperty("isResolved").GetBoolean());
            var seen = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { while (!(seen.Any(e => e.StartsWith("SyncAlertRaised")) && seen.Any(e => e.StartsWith("DeploymentChanged"))) && await events.Reader.WaitToReadAsync(cts.Token)) while (events.Reader.TryRead(out var e)) seen.Add(e); }
            catch (OperationCanceledException) { }
            Assert.Contains(seen, e => e == "DeploymentChanged " + c.DeploymentId);
            Assert.Contains(seen, e => e.StartsWith("SyncAlertRaised"));
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task Automatic_rollback_is_armed_over_http_and_fires_once_when_a_breach_is_confirmed()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.Viewer, "v@x.com")).PostAsync($"/api/v1/deployments/{c.DeploymentId}:arm-rollback", null)).StatusCode);
        var armed = await Json(await c.Rte.PostAsync($"/api/v1/deployments/{c.DeploymentId}:arm-rollback", null));
        Assert.True(armed.GetProperty("armed").GetBoolean());
        Assert.True((await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/rollback"))).GetProperty("armed").GetBoolean());
        Assert.True((await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}"))).GetProperty("deployment").GetProperty("autoRollbackArmed").GetBoolean());

        await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") });
        var (_, delivery) = Assert.Single(f.ControlSender.Sent);
        using (var body = JsonDocument.Parse(delivery.Body))
            Assert.Equal(("rollback", "automatic"), (body.RootElement.GetProperty("action").GetString(), body.RootElement.GetProperty("source").GetString()));
        Assert.Equal(ControlSignature.Sign(c.Secret, delivery.UnixSeconds, delivery.DeliveryId, delivery.Body), delivery.Signature);
        await c.Ingest.PostAsJsonAsync("/api/v1/ingest/metrics", new { deploymentExternalId = "ci-1", samples = Breaching("Canary") });   // the same samples again
        Assert.Single(f.ControlSender.Sent);
        var actions = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/control-actions"));
        Assert.Equal(("Rollback", "Automatic", "Succeeded"), (actions[0].GetProperty("action").GetString(), actions[0].GetProperty("source").GetString(), actions[0].GetProperty("status").GetString()));
        Assert.False((await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/rollback"))).GetProperty("armed").GetBoolean());
        var mon = await Json(await c.Rte.GetAsync($"/api/v1/deployments/{c.DeploymentId}/monitoring"));
        Assert.Equal("Rollback", mon.GetProperty("actions")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task The_signing_key_is_shown_once_replaced_on_request_and_never_appears_in_the_audit_log_or_the_environment()
    {
        using var f = new ApiFactory();
        var c = await Setup(f);
        var env = await c.Rte.GetStringAsync($"/api/v1/environments/{c.EnvId}");
        Assert.DoesNotContain(c.Secret, env);
        Assert.True((await Json(await c.Rte.GetAsync($"/api/v1/environments/{c.EnvId}"))).GetProperty("controlWebhookSigned").GetBoolean());
        var again = await c.Rte.PostAsync($"/api/v1/environments/{c.EnvId}/control-webhook:signing-key", null);
        var replaced = await Json(again);
        Assert.NotEqual(c.Secret, replaced.GetProperty("secret").GetString());
        Assert.Equal("X-ReleaseOS-Signature", replaced.GetProperty("header").GetString());
        Assert.Equal(300, replaced.GetProperty("toleranceSeconds").GetInt32());
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{c.Secret}%' OR AfterJson LIKE '%{replaced.GetProperty("secret").GetString()}%'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.Viewer, "v@x.com")).PostAsync($"/api/v1/environments/{c.EnvId}/control-webhook:signing-key", null)).StatusCode);
        // a call is signed with the new key, not the old
        await c.Rte.PostAsJsonAsync($"/api/v1/deployments/{c.DeploymentId}:pause", new { confirm = true });
        var (_, d) = Assert.Single(f.ControlSender.Sent);
        Assert.Equal(ControlSignature.Sign(replaced.GetProperty("secret").GetString()!, d.UnixSeconds, d.DeliveryId, d.Body), d.Signature);
    }

    // ---- run pause --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_is_paused_and_resumed_over_http_and_no_step_starts_while_it_is_paused()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        Sql(f, $"INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t1','R-001','PreCheck','Health check','{UserId(f, "rte@x.com")}','2026-10-30T02:00:00Z',10)");
        var run = await Json(await rte.PostAsJsonAsync("/api/v1/trains/t1/runs", new { mode = "Rehearsal" }));
        var id = run.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/runs/{id}:pause", new { reason = "x" })).StatusCode);
        Assert.Equal("PauseInvalid", await Guard(await rte.PostAsJsonAsync($"/api/v1/runs/{id}:pause", new { reason = " " })));
        var paused = await Json(await rte.PostAsJsonAsync($"/api/v1/runs/{id}:pause", new { reason = "Incident on the payments path" }));
        Assert.NotEqual(JsonValueKind.Null, paused.GetProperty("pausedAt").ValueKind);
        var detail = await Json(await rte.GetAsync($"/api/v1/runs/{id}"));
        Assert.Equal(("Incident on the payments path", "rte"), (detail.GetProperty("pauseReason").GetString(), detail.GetProperty("pausedBy").GetString()));
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("pausedAt").ValueKind);
        Assert.Equal("RunAlreadyPaused", await Guard(await rte.PostAsJsonAsync($"/api/v1/runs/{id}:pause", new { reason = "again" })));
        var blocked = await rte.PostAsJsonAsync($"/api/v1/runs/{id}/steps/s1:start", new { });
        Assert.Equal("RunPaused", await Guard(blocked));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM StepExecutions WHERE Status='Running'"));

        var stages = await Json(await rte.GetAsync($"/api/v1/runs/{id}/stages"));
        Assert.True(stages.GetProperty("run").GetProperty("paused").GetBoolean());
        Assert.Equal("PreCheck", stages.GetProperty("run").GetProperty("stages")[0].GetProperty("section").GetString());
        Assert.Equal(JsonValueKind.Array, stages.GetProperty("deployments").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/runs/nope/stages")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/runs/{id}:resume", null)).StatusCode);
        Assert.Equal("RunNotPaused", await Guard(await rte.PostAsync($"/api/v1/runs/{id}:resume", null)));
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync($"/api/v1/runs/{id}/steps/s1:start", new { })).StatusCode);
        Assert.Equal((1, 1), (Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='RunbookRun' AND Action='Pause'"), Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='RunbookRun' AND Action='Resume'")));
    }
}
