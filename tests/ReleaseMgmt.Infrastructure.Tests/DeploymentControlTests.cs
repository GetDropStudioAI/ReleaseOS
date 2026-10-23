using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Infrastructure.Tests.OpsKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-112: Pause, Resume, Promote and Rollback through the environment's signed webhook; automatic rollback, once per breach; failures that are recorded, alerted and visible; run pause.</summary>
public sealed class DeploymentControlTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly Actor Rte = new("rte"), Rm = new("rm"), Gov = new("gov1"), Dev = new("dev");
    private static readonly ControlRequest Yes = new(true, "Error rate is climbing");
    private OpsKit Kit(string strategy = "Canary", bool webhook = true, bool signingKey = true) => new OpsKit(fx).Seed(strategy, webhook, signingKey);

    [Fact]
    public async Task A_control_call_needs_a_confirmation_a_planner_a_fitting_state_a_webhook_and_its_key()
    {
        var k = Kit();
        Task<ServiceResult<ControlActionView>> Do(string a, ControlRequest r, Actor who) => k.Control.ControlAsync("d1", a, r, who, null);
        Assert.Contains(DeploymentGuards.ConfirmationRequired, GuardsOf(await Do("Pause", new(false), Rte)));
        Assert.Contains(DeploymentGuards.ControlRole, GuardsOf(await Do("Pause", Yes, Dev)));
        Assert.Contains(DeploymentGuards.ControlRole, GuardsOf(await Do("Pause", Yes, Gov)));
        Assert.Contains(DeploymentGuards.IllegalControlAction, GuardsOf(await Do("Resume", Yes, Rte)));   // not paused
        Assert.Contains(DeploymentGuards.ControlInvalid, GuardsOf(await Do("Rollback", new(true), Rte)));   // a rollback says why
        Assert.Contains(DeploymentGuards.ControlInvalid, GuardsOf(await Do("Abort", Yes, Rte)));
        Assert.Equal(ResultKind.NotFound, (await k.Control.ControlAsync("nope", "Pause", Yes, Rte, null)).Kind);
        Assert.Equal(ResultKind.Conflict, (await k.Control.ControlAsync("d1", "Pause", Yes, Rte, 99)).Kind);
        Assert.Empty(k.Sender.Sent);
        Assert.Equal(0, k.Count("SELECT count(*) FROM DeploymentControlActions"));   // a refused call leaves no action and no audit row
        Assert.Equal(0, k.Audits("DeploymentControl", "Request"));

        var noHook = Kit(webhook: false);
        Assert.Equal([DeploymentGuards.NoControlWebhook], GuardsOf(await noHook.Control.ControlAsync("d1", "Pause", Yes, Rte, null)));
        var noKey = Kit(signingKey: false);
        Assert.Equal([DeploymentGuards.NoSigningSecret], GuardsOf(await noKey.Control.ControlAsync("d1", "Pause", Yes, Rte, null)));
    }

    [Fact]
    public async Task A_call_is_signed_with_the_environments_key_and_carries_a_delivery_id_and_a_timestamp_for_replay_protection()
    {
        var k = Kit();
        var r = await k.Control.ControlAsync("d1", "Pause", Yes, Rm, null);
        Assert.True(r.IsOk, string.Join(";", r.Failures.Select(f => f.Message)));
        Assert.Equal(("Succeeded", "Manual", "Pause"), (r.Value!.Status, r.Value.Source, r.Value.Action));
        var (target, d) = Assert.Single(k.Sender.Sent);
        Assert.Equal(("deploy.example.com", "Deploy tool"), (target.Host, target.Name));
        Assert.Equal(r.Value.Id, d.DeliveryId);   // the action's id is the delivery id the receiver remembers
        Assert.Equal(new DateTimeOffset(T0).ToUnixTimeSeconds(), d.UnixSeconds);
        Assert.Equal(ControlSignature.Sign("whs_test_secret", d.UnixSeconds, d.DeliveryId, d.Body), d.Signature);
        Assert.StartsWith("v1=", d.Signature);
        Assert.NotEqual(ControlSignature.Sign("another-key", d.UnixSeconds, d.DeliveryId, d.Body), d.Signature);
        Assert.NotEqual(ControlSignature.Sign("whs_test_secret", d.UnixSeconds + 1, d.DeliveryId, d.Body), d.Signature);   // the timestamp is covered
        using var body = JsonDocument.Parse(d.Body);
        var root = body.RootElement;
        Assert.Equal(("pause", "manual", "Error rate is climbing", "default"), (root.GetProperty("action").GetString(), root.GetProperty("source").GetString(), root.GetProperty("reason").GetString(), root.GetProperty("workspace").GetString()));
        Assert.Equal(("ci-1", "payments-api", "Production", "4.5.0"), (root.GetProperty("deployment").GetProperty("externalId").GetString(), root.GetProperty("deployment").GetProperty("service").GetString(),
            root.GetProperty("deployment").GetProperty("environment").GetString(), root.GetProperty("deployment").GetProperty("version").GetString()));
        Assert.Equal("rm", root.GetProperty("requestedBy").GetProperty("id").GetString());
        Assert.DoesNotContain("whs_test_secret", d.Body);   // the key is never in the payload
        Assert.DoesNotContain("CfDJ8", d.Body);             // nor is the address

        // a second call is another delivery
        var again = await k.Control.ControlAsync("d1", "Pause", Yes, Rm, null);
        Assert.NotEqual(r.Value.Id, again.Value!.Id);
        Assert.Equal(2, k.Sender.Sent.Select(s => s.Delivery.DeliveryId).Distinct().Count());

        // audited: the request and its result, on the release; the deployment's Version moved so open screens refetch
        Assert.Equal((2, 2), (k.Audits("DeploymentControl", "Request"), k.Audits("DeploymentControl", "Succeeded")));
        Assert.Equal("t1", k.Text("SELECT ReleaseTrainId FROM AuditEvents WHERE EntityType='DeploymentControl' AND Action='Succeeded' LIMIT 1"));
        Assert.Equal(3, k.Count("SELECT Version FROM Deployments WHERE Id='d1'"));
        Assert.DoesNotContain("whs_test_secret", k.Text("SELECT group_concat(coalesce(AfterJson,''), ' ') FROM AuditEvents"));
        // the deployment's own state is the tool's to report
        Assert.Equal("Running", k.Text("SELECT State FROM Deployments WHERE Id='d1'"));
        Assert.Equal(2, (await k.Control.ActionsAsync("d1")).Count);
    }

    [Fact]
    public async Task A_failed_call_is_recorded_alerted_and_returned_never_silent_and_a_later_success_clears_the_alert()
    {
        var k = Kit();
        k.Sender.Answer = _ => new(false, 503, "deploy.example.com", "HTTP 503");
        var r = await k.Control.ControlAsync("d1", "Pause", Yes, Rte, null);
        Assert.True(r.IsOk);   // the request was handled; its outcome is the recorded action
        Assert.Equal(("Failed", 503, "HTTP 503"), (r.Value!.Status, r.Value.HttpStatus, r.Value.Error));
        Assert.Equal(1, k.Audits("DeploymentControl", "Failed"));
        var alert = k.Query("SELECT SourceSystem, Kind, ErrorMessage, ReleaseTrainId, IsResolved FROM SyncAlerts").Single();
        Assert.Equal(("Deployment", "ControlFailed", "t1", 0L), ((string)alert[0]!, (string)alert[1]!, (string)alert[3]!, Convert.ToInt64(alert[4])));
        Assert.Contains("Pause", (string)alert[2]!); Assert.Contains("HTTP 503", (string)alert[2]!);
        Assert.DoesNotContain("CfDJ8", (string)alert[2]!);

        k.Sender.Answer = _ => throw new InvalidOperationException("boom with a secret url https://hooks.example/abc");
        var thrown = await k.Control.ControlAsync("d1", "Pause", Yes, Rte, null);
        Assert.Equal("Failed", thrown.Value!.Status);
        Assert.DoesNotContain("hooks.example", thrown.Value.Error);   // only the exception type
        Assert.Equal(1, k.Count("SELECT count(*) FROM SyncAlerts WHERE IsResolved=0"));   // the same failure, one open alert

        k.Sender.Answer = _ => new(true, 202, "deploy.example.com", null);
        Assert.Equal("Succeeded", (await k.Control.ControlAsync("d1", "Pause", Yes, Rte, null)).Value!.Status);
        Assert.Equal(0, k.Count("SELECT count(*) FROM SyncAlerts WHERE IsResolved=0"));
    }

    [Fact]
    public async Task Only_one_call_per_deployment_is_in_flight_at_a_time_and_a_lost_result_is_closed_as_failed()
    {
        var k = Kit();
        k.Sender.Delay = TimeSpan.FromMilliseconds(600);
        var first = k.Control.ControlAsync("d1", "Pause", Yes, Rte, null);
        for (var i = 0; i < 100 && k.Count("SELECT count(*) FROM DeploymentControlActions WHERE Status='Pending'") == 0; i++) await Task.Delay(20);
        var second = await k.Control.ControlAsync("d1", "Pause", Yes, Rm, null);
        Assert.Equal([DeploymentGuards.ControlInFlight], GuardsOf(second));
        Assert.Equal("Succeeded", (await first).Value!.Status);

        // a host that stopped mid-call leaves a Pending row: after the stale period it is closed as Failed, with an alert
        k.Sql($"INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt) VALUES('lost','d1','Pause','Manual','rte','{Iso(T0.AddMinutes(-10))}')");
        Assert.Equal(1, await k.Control.FailStalePendingAsync());
        Assert.Equal("Failed", k.Text("SELECT Status FROM DeploymentControlActions WHERE Id='lost'"));
        Assert.Equal(1, k.Count("SELECT count(*) FROM SyncAlerts WHERE Kind='ControlFailed' AND IsResolved=0"));
        Assert.Equal(0, await k.Control.FailStalePendingAsync());
    }

    [Fact]
    public async Task Promote_moves_a_healthy_running_canary_to_its_next_step_and_nothing_else()
    {
        var k = Kit();
        Assert.Equal("Succeeded", (await k.Control.ControlAsync("d1", "Promote", new(true), Rte, null)).Value!.Status);
        // a failing canary is not promoted
        await k.Telemetry.IngestAsync("ci-1", OpsKit.Run("Canary", T0, 5, er: 0.9), Key);
        var refused = await k.Control.ControlAsync("d1", "Promote", new(true), Rte, null);
        Assert.Equal([DeploymentGuards.IllegalControlAction], GuardsOf(refused));
        Assert.Contains("failing", refused.Failures[0].Message);
        // the last step has no next one; a rolling deployment is not a canary; a paused one is resumed first
        k.Sql("UPDATE ThresholdBreaches SET ResolvedAt=StartedAt; UPDATE Deployments SET TrafficPercent=100");
        Assert.Contains("last step", (await k.Control.ControlAsync("d1", "Promote", new(true), Rte, null)).Failures[0].Message);
        var rolling = Kit("Rolling");
        Assert.Contains("not a canary", (await rolling.Control.ControlAsync("d1", "Promote", new(true), Rte, null)).Failures[0].Message);
    }

    [Fact]
    public async Task Automatic_rollback_is_armed_per_deployment_and_needs_the_webhook_and_its_key()
    {
        var k = Kit();
        var r = await k.Control.SetArmedAsync("d1", true, Rte, null);
        Assert.True(r.IsOk);
        Assert.Equal((true, true, true), (r.Value!.Armed, r.Value.WebhookConfigured, r.Value.SigningSecretSet));
        Assert.Equal(1, k.Audits("DeploymentControl", "Arm"));
        var version = k.Count("SELECT Version FROM Deployments WHERE Id='d1'");
        Assert.True((await k.Control.SetArmedAsync("d1", true, Rte, null)).IsOk);   // arming an armed one changes nothing
        Assert.Equal((version, 1), (k.Count("SELECT Version FROM Deployments WHERE Id='d1'"), k.Audits("DeploymentControl", "Arm")));
        Assert.Contains(DeploymentGuards.ControlRole, GuardsOf(await k.Control.SetArmedAsync("d1", false, Dev, null)));
        Assert.True((await k.Control.SetArmedAsync("d1", false, Rm, null)).IsOk);
        Assert.False((await k.Control.RollbackStatusAsync("d1"))!.Armed);

        Assert.Equal([DeploymentGuards.NoControlWebhook], GuardsOf(await Kit(webhook: false).Control.SetArmedAsync("d1", true, Rte, null)));
        Assert.Equal([DeploymentGuards.NoSigningSecret], GuardsOf(await Kit(signingKey: false).Control.SetArmedAsync("d1", true, Rte, null)));
        k.Sql("UPDATE Deployments SET State='Successful', FinishedAt=StartedAt");
        Assert.Contains(DeploymentGuards.ControlInvalid, GuardsOf(await k.Control.SetArmedAsync("d1", true, Rte, null)));
    }

    [Fact]
    public async Task Automatic_rollback_fires_exactly_once_per_breach_and_records_it()
    {
        var k = Kit();
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        for (var i = 0; i < 4; i++) await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(30 * i), er: 0.9)], Key);
        Assert.Empty(k.Sender.Sent);   // watching is not a breach yet
        var fifth = new[] { S("Canary", T0.AddSeconds(120), er: 1.4) };
        Assert.Equal(1, (await k.Telemetry.IngestAsync("ci-1", fifth, Key)).Value!.BreachesConfirmed);
        var (_, d) = Assert.Single(k.Sender.Sent);
        var action = k.Query("SELECT Id, Action, Source, RequestedByUserId, BreachId, Status, Reason FROM DeploymentControlActions").Single();
        Assert.Equal(("Rollback", "Automatic", null, "Succeeded"), ((string)action[1]!, (string)action[2]!, (string?)action[3], (string)action[5]!));
        Assert.Equal(k.Text("SELECT Id FROM ThresholdBreaches"), (string)action[4]!);
        Assert.Contains("ErrorRate", (string)action[6]!);
        Assert.Equal((string)action[0]!, d.DeliveryId);
        using (var body = JsonDocument.Parse(d.Body))
        {
            Assert.Equal(("rollback", "automatic"), (body.RootElement.GetProperty("action").GetString(), body.RootElement.GetProperty("source").GetString()));
            Assert.Equal("ErrorRate", body.RootElement.GetProperty("breach").GetProperty("metric").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.TryGetProperty("requestedBy", out var rb) ? rb.ValueKind : JsonValueKind.Null);
        }
        Assert.Equal(1, k.Audits("DeploymentControl", "Succeeded"));
        Assert.False((await k.Control.RollbackStatusAsync("d1"))!.Armed);   // on its way out: disarmed

        // the same sample again (a replay), and the same breach reported again: still one call
        await k.Telemetry.IngestAsync("ci-1", fifth, Key);
        await k.Control.OnBreachConfirmedAsync(k.Text("SELECT Id FROM ThresholdBreaches"));
        Assert.Single(k.Sender.Sent);
        Assert.Equal(1, k.Count("SELECT count(*) FROM DeploymentControlActions"));
    }

    [Fact]
    public async Task Racing_reports_of_one_breach_make_one_call()
    {
        var k = Kit();
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        k.Sender.Delay = TimeSpan.FromMilliseconds(200);
        k.Sql($@"INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,ConfirmedAt,DetectedAt,PeakValue,LastValue,SampleCount)
                 VALUES('b1','d1','Canary','ErrorRate',0.5,120,'{Iso(T0)}','{Iso(T0.AddMinutes(2))}','{Iso(T0.AddMinutes(2))}',1.4,1.4,5)");
        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => k.Control.OnBreachConfirmedAsync("b1"))).Select(async t => { try { await t; return true; } catch { return false; } }));
        Assert.Single(k.Sender.Sent);
        Assert.Equal(1, k.Count("SELECT count(*) FROM DeploymentControlActions WHERE BreachId='b1'"));
        Assert.Equal(1, k.Count("SELECT count(*) FROM DeploymentControlActions WHERE BreachId='b1' AND Status='Succeeded'"));
        Assert.Contains(true, all);
    }

    [Fact]
    public async Task A_breach_that_is_not_the_canarys_or_on_an_unarmed_deployment_calls_nothing()
    {
        var k = Kit();   // not armed
        await k.Telemetry.IngestAsync("ci-1", OpsKit.Run("Canary", T0, 5, er: 0.9), Key);
        Assert.Empty(k.Sender.Sent);
        Assert.Equal(1, k.Count("SELECT count(*) FROM ThresholdBreaches WHERE ConfirmedAt IS NOT NULL"));

        var armed = Kit();
        await armed.Control.SetArmedAsync("d1", true, Rte, null);
        await armed.Telemetry.IngestAsync("ci-1", OpsKit.Run("Stable", T0, 5, er: 0.9), Key);   // the baseline degrading is not the canary's doing
        Assert.Empty(armed.Sender.Sent);
        Assert.Equal(1, armed.Count("SELECT count(*) FROM ThresholdBreaches WHERE ConfirmedAt IS NOT NULL"));

        var rolling = Kit("Rolling");   // no canary: any series counts
        await rolling.Control.SetArmedAsync("d1", true, Rte, null);
        await rolling.Telemetry.IngestAsync("ci-1", OpsKit.Run("Stable", T0, 5, er: 0.9), Key);
        Assert.Single(rolling.Sender.Sent);

        var paused = Kit();
        await paused.Control.SetArmedAsync("d1", true, Rte, null);
        paused.Sql("UPDATE Deployments SET State='Paused'");
        await paused.Telemetry.IngestAsync("ci-1", OpsKit.Run("Canary", T0, 5, er: 0.9), Key);
        Assert.Single(paused.Sender.Sent);   // a paused rollout can still be rolled back
    }

    [Fact]
    public async Task A_failed_automatic_rollback_is_recorded_and_alerted_stays_armed_and_is_not_repeated_for_the_same_breach()
    {
        var k = Kit();
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        k.Sender.Answer = _ => new(false, null, "deploy.example.com", "no answer within 10s");
        await k.Telemetry.IngestAsync("ci-1", OpsKit.Run("Canary", T0, 5, er: 0.9), Key);
        var a = k.Query("SELECT Status, ErrorMessage, Source FROM DeploymentControlActions").Single();
        Assert.Equal(("Failed", "no answer within 10s", "Automatic"), ((string)a[0]!, (string)a[1]!, (string)a[2]!));
        Assert.Equal(1, k.Count("SELECT count(*) FROM SyncAlerts WHERE SourceSystem='Deployment' AND Kind='ControlFailed' AND IsResolved=0"));
        Assert.True((await k.Control.RollbackStatusAsync("d1"))!.Armed);
        await k.Control.OnBreachConfirmedAsync(k.Text("SELECT Id FROM ThresholdBreaches"));
        Assert.Single(k.Sender.Sent);   // once for this breach; a person decides what next (manual rollback is open)

        // the webhook went away after arming: the attempt is a failed action and an alert, not a skipped one
        var gone = Kit();
        await gone.Control.SetArmedAsync("d1", true, Rte, null);
        gone.Sql("UPDATE Environments SET ControlWebhookDestinationId=NULL");
        await gone.Telemetry.IngestAsync("ci-1", OpsKit.Run("Canary", T0, 5, er: 0.9), Key);
        Assert.Empty(gone.Sender.Sent);
        Assert.Contains("no control webhook", gone.Text("SELECT ErrorMessage FROM DeploymentControlActions"));
        Assert.Equal(1, gone.Count("SELECT count(*) FROM SyncAlerts WHERE Kind='ControlFailed'"));
    }

    [Fact]
    public async Task A_new_breach_after_the_first_resolved_can_fire_again_once_armed_again()
    {
        var k = Kit("Rolling");
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        await k.Telemetry.IngestAsync("ci-1", OpsKit.Run("Stable", T0, 5, er: 0.9), Key);
        Assert.Single(k.Sender.Sent);
        await k.Telemetry.IngestAsync("ci-1", [S("Stable", T0.AddSeconds(150), er: 0.1)], Key);   // recovered
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        k.Time.Advance(TimeSpan.FromMinutes(10));
        await k.Telemetry.IngestAsync("ci-1", OpsKit.Run("Stable", T0.AddMinutes(8), 5, er: 0.9), Key);
        Assert.Equal(2, k.Sender.Sent.Count);
        Assert.Equal(2, k.Count("SELECT count(DISTINCT BreachId) FROM DeploymentControlActions"));
    }

    [Fact]
    public async Task The_signing_key_is_created_once_replaced_on_request_and_never_audited()
    {
        var k = Kit(signingKey: false);
        Assert.False((await k.Environments.GetAsync("e1"))!.ControlWebhookSigned);
        var made = await k.Environments.RotateSigningSecretAsync("e1", Rte);
        Assert.True(made.IsOk);
        Assert.StartsWith("whs_", made.Value);
        Assert.True((await k.Environments.GetAsync("e1"))!.ControlWebhookSigned);
        var replaced = await k.Environments.RotateSigningSecretAsync("e1", Rte);
        Assert.NotEqual(made.Value, replaced.Value);
        Assert.Equal((1, 1), (k.Audits("Environment", "CreateSigningKey"), k.Audits("Environment", "RotateSigningKey")));
        Assert.DoesNotContain(replaced.Value!, k.Text("SELECT group_concat(coalesce(AfterJson,'') || coalesce(BeforeJson,''), ' ') FROM AuditEvents"));
        // a call is then signed with the new one
        await k.Control.ControlAsync("d1", "Pause", Yes, Rte, null);
        var (_, d) = Assert.Single(k.Sender.Sent);
        Assert.Equal(ControlSignature.Sign(replaced.Value!, d.UnixSeconds, d.DeliveryId, d.Body), d.Signature);
        // no webhook, no key
        Assert.Equal([DeploymentGuards.NoControlWebhook], GuardsOf(await Kit(webhook: false).Environments.RotateSigningSecretAsync("e1", Rte)));
    }

    // ---- run pause ----------------------------------------------------------------------------------------------------------------

    private async Task<(OpsKit Kit, string RunId)> LiveRehearsal()
    {
        var k = Kit();
        var run = await k.Runs.StartRunAsync("t1", "Rehearsal", Rte);
        Assert.True(run.IsOk, string.Join(";", run.Failures.Select(f => f.Message)));
        return (k, run.Value!.Id);
    }

    [Fact]
    public async Task A_paused_run_starts_no_step_in_the_service_or_the_database_and_resuming_lets_it_go_on()
    {
        var (k, run) = await LiveRehearsal();
        Assert.Contains(DeploymentGuards.ControlRole, GuardsOf(await k.Runs.PauseAsync(run, "Incident on the payments path", Dev)));
        Assert.Contains(DeploymentGuards.PauseInvalid, GuardsOf(await k.Runs.PauseAsync(run, "  ", Rte)));
        var paused = await k.Runs.PauseAsync(run, "Incident on the payments path", Rte);
        Assert.True(paused.IsOk);
        Assert.Equal(("rte", "Incident on the payments path"), (paused.Value!.PausedByUserId, paused.Value.PauseReason));
        Assert.Contains(DeploymentGuards.RunAlreadyPaused, GuardsOf(await k.Runs.PauseAsync(run, "again", Rm)));

        var start = await k.Runs.StartStepAsync(run, "s1", null, Rte);
        Assert.Equal([DeploymentGuards.RunPaused], GuardsOf(start));
        Assert.Contains("Incident on the payments path", start.Failures[0].Message);
        Assert.Equal(0, k.Count("SELECT count(*) FROM StepExecutions WHERE Status='Running'"));
        // the trigger is the backstop: the same start written straight to the table is refused with its own message
        var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => k.Sql($"UPDATE StepExecutions SET Status='Running', ActualStartAt='{Iso(T0)}', ActorUserId='rte' WHERE RunId='{run}' AND StepId='s1'"));
        Assert.Contains("The run is paused", ex.Message);
        Assert.Equal((1, 0), (k.Audits("RunbookRun", "Pause"), k.Audits("StepExecution", "Start")));

        Assert.True((await k.Runs.ResumeAsync(run, Rte)).IsOk);
        Assert.Contains(DeploymentGuards.RunNotPaused, GuardsOf(await k.Runs.ResumeAsync(run, Rte)));
        Assert.True((await k.Runs.StartStepAsync(run, "s1", null, Rte)).IsOk);
        Assert.Equal((1, 1), (k.Audits("RunbookRun", "Resume"), k.Audits("StepExecution", "Start")));
    }

    [Fact]
    public async Task A_step_that_is_running_can_still_finish_while_the_run_is_paused_and_ending_a_run_clears_the_pause()
    {
        var (k, run) = await LiveRehearsal();
        Assert.True((await k.Runs.StartStepAsync(run, "s1", null, Rte)).IsOk);
        await k.Runs.PauseAsync(run, "Hold", Rm);
        Assert.True((await k.Runs.DoneStepAsync(run, "s1", null, Rte)).IsOk);   // finishing is not starting
        Assert.Equal([DeploymentGuards.RunPaused], GuardsOf(await k.Runs.StartStepAsync(run, "s2", null, Rte)));
        Assert.True((await k.Runs.SkipStepAsync(run, "s2", "not needed", Rte)).IsOk);   // nor is skipping
        var end = await k.Runs.EndRunAsync(run, "Aborted", Rte);
        Assert.True(end.IsOk);
        Assert.Equal((null, null, null), (end.Value!.PausedAt, end.Value.PausedByUserId, end.Value.PauseReason));
        Assert.Contains(Guards.RunEnded, GuardsOf(await k.Runs.ResumeAsync(run, Rte)));
        Assert.Contains(Guards.RunEnded, GuardsOf(await k.Runs.PauseAsync(run, "late", Rte)));
    }

    [Fact]
    public async Task Stage_summaries_count_the_steps_of_each_section_and_show_the_pause()
    {
        var (k, run) = await LiveRehearsal();
        await k.Runs.StartStepAsync(run, "s1", null, Rte);
        await k.Runs.DoneStepAsync(run, "s1", null, Rte);
        await k.Runs.PauseAsync(run, "Hold for approval", Rte);
        var s = (await k.Runs.StagesAsync(run))!;
        Assert.True(s.Paused);
        Assert.Equal(("Hold for approval", 2, 1), (s.PauseReason, s.Steps, s.StepsDone));
        Assert.Equal(["PreCheck", "Deploy"], s.Stages.Select(x => x.Section));
        var pre = s.Stages[0]; var deploy = s.Stages[1];
        Assert.Equal(("Done", 1, 1), (pre.Status, pre.Steps, pre.Done));
        Assert.Equal(("Pending", 1, 1), (deploy.Status, deploy.Steps, deploy.Scheduled));
        Assert.NotNull(pre.ActualStartAt); Assert.NotNull(pre.ActualEndAt); Assert.Null(deploy.ActualStartAt);
        Assert.Null(await k.Runs.StagesAsync("nope"));
    }

    [Fact]
    public async Task The_deployment_view_carries_the_step_start_and_the_rollback_arming_and_linked_runs_are_listed()
    {
        var k = Kit();
        var v = (await k.Deployments.GetAsync("d1"))!.Deployment;
        Assert.Equal(("2026-10-20T14:00:00Z", false), (v.StepStartedAt, v.AutoRollbackArmed));
        await k.Control.SetArmedAsync("d1", true, Rte, null);
        Assert.True((await k.Deployments.GetAsync("d1"))!.Deployment.AutoRollbackArmed);
        var run = await k.Runs.StartRunAsync("t1", "Rehearsal", Rte);
        k.Sql($"UPDATE Deployments SET RunId='{run.Value!.Id}' WHERE Id='d1'");
        Assert.Single(await k.Deployments.ListAsync(new(RunId: run.Value.Id)));
        Assert.Empty(await k.Deployments.ListAsync(new(RunId: "other")));
    }
}
