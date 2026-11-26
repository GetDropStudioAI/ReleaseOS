using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>REOS-183 (D58, D12): the governed client with the fake clock and the fake provider. No real model is ever called.</summary>
public class GovernedAiClientTests
{
    [Fact]
    public async Task A_call_is_made_when_every_rule_allows_it_and_the_result_carries_the_cost_inputs_and_the_fake_clock_time()
    {
        var rig = new AiRig();
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.Completed, r.Outcome);
        Assert.True(r.ProviderCalled);
        Assert.Equal(1, r.Attempts);
        Assert.Equal("small-model", r.Model);
        Assert.Equal(new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc), r.At);
        Assert.Equal(AiFeatures.RiskScore, r.Usage!.Feature);
        Assert.Equal("small-model", r.Usage.Model);
        Assert.True(r.Usage.InputTokens > 0 && r.Usage.OutputTokens > 0);
        Assert.False(r.Usage.Batch);
        Assert.Equal(1, rig.Provider.CallCount);
        Assert.Same(r, Assert.Single(rig.Sink.Results));
        rig.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(new DateTime(2026, 11, 26, 11, 0, 0, DateTimeKind.Utc), (await rig.Client.CompleteAsync(AiRig.Req())).At);
    }

    [Fact]
    public async Task When_the_ai_module_is_off_no_call_is_made()
    {
        var rig = new AiRig();
        rig.Gate.Enabled.Clear();
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.ModuleOff, r.Outcome);
        Assert.False(r.ProviderCalled);
        Assert.Equal(0, rig.Provider.CallCount);
        var batch = await rig.Client.CompleteBatchAsync([AiRig.Req(), AiRig.Req()]);
        Assert.All(batch, x => Assert.Equal(AiOutcome.ModuleOff, x.Outcome));
        Assert.Equal(0, rig.Provider.CallCount);
        var pre = await rig.Client.PreflightAsync(AiRig.Ws, AiFeatures.RiskScore);
        Assert.False(pre.Allowed);
        Assert.Equal(AiOutcome.ModuleOff, pre.Outcome);
    }

    [Fact]
    public async Task The_module_is_checked_per_workspace()
    {
        var rig = new AiRig();
        Assert.Equal(AiOutcome.ModuleOff, (await rig.Client.CompleteAsync(AiRig.Req(ws: "other"))).Outcome);
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        Assert.Equal(1, rig.Provider.CallCount);
    }

    [Fact]
    public async Task The_global_kill_switch_stops_every_call_at_once_and_a_config_change_takes_effect_without_a_restart()
    {
        var rig = new AiRig();
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        rig.Options.CurrentValue.KillSwitch = true;   // the options monitor reloads config live; same object here
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.KillSwitch, r.Outcome);
        Assert.False(r.ProviderCalled);
        Assert.Equal(1, rig.Provider.CallCount);
        Assert.All(await rig.Client.CompleteBatchAsync([AiRig.Req()]), x => Assert.Equal(AiOutcome.KillSwitch, x.Outcome));
        rig.Options.CurrentValue.KillSwitch = false;
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
    }

    [Fact]
    public async Task The_workspace_kill_switch_and_opt_out_stop_only_that_workspace()
    {
        var rig = new AiRig();
        rig.Gate.Enabled.Add("ws-2");
        rig.Workspaces.SetPaused(AiRig.Ws, true);
        Assert.Equal(AiOutcome.WorkspacePaused, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req(ws: "ws-2"))).Outcome);
        rig.Workspaces.SetPaused(AiRig.Ws, false);
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        rig.Workspaces.SetOptedOut(AiRig.Ws, true);
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.OptedOut, r.Outcome);
        Assert.False(r.ProviderCalled);
        rig.Workspaces.SetOptedOut(AiRig.Ws, false);
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        Assert.Equal(3, rig.Provider.CallCount);   // ws-2 once, ws-1 twice; the two refusals made none
    }

    [Fact]
    public async Task A_provider_that_needs_a_key_is_refused_while_none_is_stored()
    {
        var fake = new KeyedProvider();
        var rig = new AiRig(provider: fake);
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.NoKey, r.Outcome);
        Assert.Equal(0, fake.Calls);
        await rig.Keys.SetKeyAsync("sk-ant-test-key-0123456789abcdef");
        Assert.Equal(AiOutcome.Completed, (await rig.Client.CompleteAsync(AiRig.Req())).Outcome);
        Assert.Equal(1, fake.Calls);
    }

    private sealed class KeyedProvider : IAiProvider
    {
        public int Calls;
        public string Name => "keyed";
        public bool RequiresKey => true;
        public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct) { Calls++; return Task.FromResult(new AiProviderResponse("ok", 1, 1)); }
        public Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Ticket_summaries_and_customer_data_are_never_sent()
    {
        var rig = new AiRig();
        foreach (var cls in new[] { AiDataClass.TicketSummary, AiDataClass.CustomerData })
        {
            var r = await rig.Client.CompleteAsync(AiRig.Req(context: [new AiContextItem("Release", "R-1 plan", AiDataClass.ReleaseData), new AiContextItem("Item", "Payment fails for Acme", cls)]));
            Assert.Equal(AiOutcome.Blocked, r.Outcome);
            Assert.False(r.ProviderCalled);
            Assert.Contains(cls.ToString(), r.Detail);
            Assert.DoesNotContain("Acme", r.Detail);
        }
        Assert.Equal(0, rig.Provider.CallCount);
        var batch = await rig.Client.CompleteBatchAsync([AiRig.Req(context: [new AiContextItem("x", "y", AiDataClass.CustomerData)]), AiRig.Req()]);
        Assert.Equal([AiOutcome.Blocked, AiOutcome.Completed], batch.Select(b => b.Outcome));
        Assert.Single(rig.Provider.Sent);
    }

    [Fact]
    public async Task Tokens_are_redacted_before_anything_is_sent_and_the_count_is_reported()
    {
        var rig = new AiRig();
        var secret = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123";
        var r = await rig.Client.CompleteAsync(AiRig.Req(instruction: $"Use key {secret} please", context:
        [
            new AiContextItem("Header", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", AiDataClass.FreeText),
            new AiContextItem("Contact", "mail jane.doe@example.com or use ghp_abcdefghijklmnopqrstuvwxyz0123456789", AiDataClass.FreeText),
            new AiContextItem("Config", "password=hunter2hunter2", AiDataClass.FreeText),
        ]));
        Assert.True(r.IsCompleted);
        Assert.True(r.Redactions >= 5);
        var sent = Assert.Single(rig.Provider.Sent);
        foreach (var leak in new[] { secret, "eyJhbGci", "jane.doe", "ghp_abc", "hunter2" }) Assert.DoesNotContain(leak, sent.User);
        Assert.Contains(AiRedactor.Mask, sent.User);
        Assert.Contains("Use key", sent.User);
    }

    [Fact]
    public async Task A_prompt_over_the_size_limit_is_refused_not_truncated()
    {
        var rig = new AiRig(o => o.MaxInputChars = 100);
        var r = await rig.Client.CompleteAsync(AiRig.Req(instruction: new string('a', 500)));
        Assert.Equal(AiOutcome.Blocked, r.Outcome);
        Assert.Equal(0, rig.Provider.CallCount);
    }

    [Fact]
    public async Task Features_route_to_the_small_model_by_default_and_only_drafting_uses_the_larger_one()
    {
        var rig = new AiRig();
        Assert.Equal("small-model", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.RiskScore))).Model);
        Assert.Equal("small-model", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.Chat))).Model);
        Assert.Equal("drafting-model", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.ReleaseNotes))).Model);
        Assert.Equal("drafting-model", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.RunbookDraft))).Model);
        rig.Options.CurrentValue.Models.Drafting = "";   // the larger model is optional
        Assert.Equal("small-model", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.ReleaseNotes))).Model);
        rig.Options.CurrentValue.Models.Small = "renamed-small";   // names are configuration, not code
        Assert.Equal("renamed-small", (await rig.Client.CompleteAsync(AiRig.Req(AiFeatures.RiskScore))).Model);
        rig.Options.CurrentValue.Models.Small = "";
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.NotConfigured, r.Outcome);
    }

    [Fact]
    public async Task An_unknown_feature_is_refused()
    {
        var rig = new AiRig();
        var r = await rig.Client.CompleteAsync(AiRig.Req("approve-the-train"));
        Assert.Equal(AiOutcome.Blocked, r.Outcome);
        Assert.Equal(0, rig.Provider.CallCount);
    }

    [Fact]
    public async Task Output_tokens_are_capped_and_the_system_prompt_cache_option_reaches_the_provider_and_the_usage()
    {
        var rig = new AiRig(o => o.MaxOutputTokens = 100);
        var r = await rig.Client.CompleteAsync(new AiRequest(AiRig.Ws, AiFeatures.RiskScore, new string('s', 400), "go", null, 99999, CacheSystemPrompt: true));
        var sent = Assert.Single(rig.Provider.Sent);
        Assert.Equal(100, sent.MaxOutputTokens);
        Assert.True(sent.CacheSystem);
        Assert.True(r.Usage!.CacheWriteTokens > 0);
    }

    [Fact]
    public async Task Transient_failures_are_retried_a_bounded_number_of_times_then_the_call_succeeds()
    {
        var rig = new AiRig(o => o.MaxRetries = 2);
        rig.Provider.FailWith = n => n <= 2 ? new AiProviderException("429", transient: true) : null;
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.Completed, r.Outcome);
        Assert.Equal(3, r.Attempts);
        Assert.Equal(3, rig.Provider.CallCount);
    }

    [Fact]
    public async Task Retries_stop_at_the_bound_and_the_failure_is_returned_not_thrown()
    {
        var rig = new AiRig(o => o.MaxRetries = 2);
        rig.Provider.FailWith = _ => new AiProviderException("503", transient: true);
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.ProviderError, r.Outcome);
        Assert.Equal(3, r.Attempts);
        Assert.True(r.ProviderCalled);
        Assert.Equal(3, rig.Provider.CallCount);
        Assert.Contains(rig.Log.Lines, l => l.Contains("ProviderError"));   // surfaced in the log, never silent
    }

    [Fact]
    public async Task The_retry_bound_itself_is_capped_whatever_the_config_says()
    {
        var rig = new AiRig(o => o.MaxRetries = 1000);
        rig.Provider.FailWith = _ => new AiProviderException("503", transient: true);
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOptions.MaxRetriesCap + 1, r.Attempts);
    }

    [Fact]
    public async Task A_final_failure_is_not_retried()
    {
        var rig = new AiRig(o => o.MaxRetries = 3);
        rig.Provider.FailWith = _ => new AiProviderException("400", transient: false);
        var r = await rig.Client.CompleteAsync(AiRig.Req());
        Assert.Equal(AiOutcome.ProviderError, r.Outcome);
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task A_call_that_outlasts_the_timeout_is_cancelled_by_the_fake_clock_and_reported_as_timed_out()
    {
        var hang = new HangingProvider();
        var rig = new AiRig(o => { o.TimeoutSeconds = 10; o.MaxRetries = 0; }, hang);
        var task = rig.Client.CompleteAsync(AiRig.Req());
        await hang.Started.Task;
        Assert.False(task.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(11));
        var r = await task;
        Assert.Equal(AiOutcome.TimedOut, r.Outcome);
        Assert.Equal(1, r.Attempts);
        Assert.True(r.ProviderCalled);
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_reported_as_a_timeout()
    {
        var hang = new HangingProvider();
        var rig = new AiRig(o => o.MaxRetries = 0, hang);
        using var cts = new CancellationTokenSource();
        var task = rig.Client.CompleteAsync(AiRig.Req(), cts.Token);
        await hang.Started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class HangingProvider : IAiProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "hang";
        public bool RequiresKey => false;
        public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null!; }
        public Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_batch_path_answers_in_request_order_marks_usage_as_batch_and_applies_the_same_rules_to_each_request()
    {
        var rig = new AiRig();
        rig.Workspaces.SetOptedOut("ws-2", true);
        rig.Gate.Enabled.Add("ws-2");
        var results = await rig.Client.CompleteBatchAsync(
        [
            AiRig.Req(AiFeatures.GateSummary, "first"),
            AiRig.Req(AiFeatures.RiskScore, "second", ws: "ws-2"),
            AiRig.Req(AiFeatures.ReleaseNotes, "third", cache: true),
        ]);
        Assert.Equal([AiOutcome.Completed, AiOutcome.OptedOut, AiOutcome.Completed], results.Select(r => r.Outcome));
        Assert.All(results, r => Assert.True(r.Batch));
        Assert.True(results[0].Usage!.Batch);
        Assert.Equal("drafting-model", results[2].Model);
        Assert.Equal(2, rig.Provider.Sent.Count);
        Assert.Equal(3, rig.Sink.Results.Count);
    }

    [Fact]
    public async Task A_failed_batch_gives_every_request_a_provider_error_and_an_empty_batch_gives_nothing()
    {
        var rig = new AiRig();
        Assert.Empty(await rig.Client.CompleteBatchAsync([]));
        var rig2 = new AiRig(provider: new ThrowingBatchProvider());
        var r = await rig2.Client.CompleteBatchAsync([AiRig.Req(), AiRig.Req()]);
        Assert.All(r, x => Assert.Equal(AiOutcome.ProviderError, x.Outcome));
    }

    private sealed class ThrowingBatchProvider : IAiProvider
    {
        public string Name => "t";
        public bool RequiresKey => false;
        public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct) => throw new AiProviderException("final", transient: false);
    }

    [Fact]
    public async Task Logs_carry_counts_and_names_but_never_the_prompt_or_the_answer()
    {
        var rig = new AiRig();
        await rig.Client.CompleteAsync(AiRig.Req(instruction: "SECRET-PROMPT-TEXT"));
        var all = string.Join('\n', rig.Log.Lines);
        Assert.Contains("risk-score", all);
        Assert.DoesNotContain("SECRET-PROMPT-TEXT", all);
        Assert.DoesNotContain("characters received", all);
    }

    [Fact]
    public async Task A_failing_usage_sink_never_loses_the_result()
    {
        var rig = new AiRig();
        var client = new GovernedAiClient(rig.Provider, rig.Keys, rig.Options, rig.Workspaces, rig.Gate, new ThrowingSink(), rig.Clock, rig.Log);
        Assert.True((await client.CompleteAsync(AiRig.Req())).IsCompleted);
        Assert.Contains(rig.Log.Lines, l => l.Contains("usage sink failed"));
    }

    private sealed class ThrowingSink : IAiUsageSink { public void Record(AiResult result) => throw new InvalidOperationException("sink down"); }

    [Fact]
    public void The_fake_client_is_deterministic()
    {
        var p = new AiProviderRequest("m", "risk-score", "sys", "user text", 100, false);
        Assert.Equal(FakeAiClient.Answer(p), FakeAiClient.Answer(p));
    }
}
