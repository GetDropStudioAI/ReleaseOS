using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-183 (D58, D12): THE governed place every AI call goes through. In order, a call is refused before any network use when: the feature is unknown, the 'ai' module is off in the workspace, the
/// global or the workspace kill switch is on, the workspace opted out, no model is configured, the provider needs a key and none is stored, or the request carries ticket summaries or customer data
/// (D12 stands) or is too large. Otherwise tokens are redacted from every text, the feature is routed to a model (small by default, the optional drafting model for drafting features), and the
/// provider is called with a per-attempt timeout and a bounded number of retries for transient failures only. Every outcome is returned as an <see cref="AiResult"/> carrying the cost inputs
/// (tokens in and out, cached tokens, model, feature) for the metering story, and is handed to <see cref="IAiUsageSink"/>. Nothing here changes a status; AI is advisory.
/// Logs carry feature, workspace, outcome, model and counts, never prompt text, output text or a key. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class GovernedAiClient(IAiProvider provider, IAiKeyStore keys, IOptionsMonitor<AiOptions> options, IAiWorkspaceSettings workspaces, IModuleGate modules,
    IAiUsageSink sink, TimeProvider time, ILogger<GovernedAiClient> log) : IAiClient
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The model a feature routes to, or null when none is configured. Drafting features use the drafting model when one is set, otherwise the small one.</summary>
    public static string? RouteModel(AiOptions o, AiFeatureDefinition f)
    {
        var small = string.IsNullOrWhiteSpace(o.Models.Small) ? null : o.Models.Small.Trim();
        var drafting = string.IsNullOrWhiteSpace(o.Models.Drafting) ? null : o.Models.Drafting.Trim();
        return f.Tier == AiModelTier.Drafting ? drafting ?? small : small;
    }

    public async Task<AiPreflight> PreflightAsync(string workspaceId, string feature, CancellationToken ct = default)
    {
        var (refusal, model) = await CheckAsync(workspaceId, feature, ct);
        return refusal is null ? new AiPreflight(true, AiOutcome.Completed, null, model) : new AiPreflight(false, refusal.Value.Outcome, refusal.Value.Detail, model);
    }

    private async Task<((AiOutcome Outcome, string Detail)? Refusal, string? Model)> CheckAsync(string workspaceId, string feature, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var def = AiFeatures.Find(feature);
        if (def is null) return ((AiOutcome.Blocked, $"'{feature}' is not an AI feature"), null);
        var model = RouteModel(o, def);
        if (!await modules.IsEnabledAsync(workspaceId, ModuleKeys.Ai, ct)) return ((AiOutcome.ModuleOff, "The AI module is off in this workspace"), model);
        if (o.KillSwitch) return ((AiOutcome.KillSwitch, "The global AI kill switch is on"), model);
        var ws = workspaces.Get(workspaceId);
        if (ws.Paused) return ((AiOutcome.WorkspacePaused, "AI is paused for this workspace"), model);
        if (ws.OptedOut) return ((AiOutcome.OptedOut, "This workspace opted out of AI"), model);
        if (model is null) return ((AiOutcome.NotConfigured, "No model is configured (Ai:Models:Small)"), null);
        if (provider.RequiresKey && !await keys.HasKeyAsync(ct)) return ((AiOutcome.NoKey, "No provider key is stored"), model);
        return (null, model);
    }

    private AiResult Done(AiResult r)
    {
        try { sink.Record(r); }
        catch (Exception ex) { log.LogError(ex, "The AI usage sink failed for feature {Feature}", r.Feature); }   // surfaced, never swallowed; the result still reaches the caller
        log.LogInformation("AI call {Feature} in workspace {Workspace}: {Outcome}, model {Model}, tokens in {In} out {Out}, attempts {Attempts}, redactions {Redactions}",
            r.Feature, r.WorkspaceId, r.Outcome, r.Model, r.Usage?.InputTokens, r.Usage?.OutputTokens, r.Attempts, r.Redactions);
        return r;
    }

    private AiResult Refused(AiRequest req, AiOutcome outcome, string detail, string? model, int redactions = 0, bool batch = false) =>
        Done(new AiResult(outcome, req.Feature, req.WorkspaceId, Now, Detail: detail, Model: model, Redactions: redactions, Batch: batch));

    /// <summary>Applies the data rules and redaction. Returns the provider request, or a refusal.</summary>
    private (AiProviderRequest? Request, AiResult? Refusal, int Redactions) Prepare(AiRequest req, string model, bool batch)
    {
        var o = options.CurrentValue;
        var bad = req.Context?.FirstOrDefault(c => c.Class is AiDataClass.TicketSummary or AiDataClass.CustomerData);
        if (bad is not null)
            return (null, Refused(req, AiOutcome.Blocked, $"Context '{bad.Label}' is {bad.Class}: ticket summaries and customer data are never sent to a model", model, batch: batch), 0);
        var redactions = 0;
        string R(string? s) { var t = AiRedactor.Redact(s, out var n); redactions += n; return t; }
        var system = R(req.SystemPrompt);
        var user = new StringBuilder(R(req.Instruction));
        foreach (var c in req.Context ?? []) user.Append("\n\n## ").Append(R(c.Label)).Append('\n').Append(R(c.Text));
        if (system.Length + user.Length > o.MaxInputChars)
            return (null, Refused(req, AiOutcome.Blocked, $"The prompt is larger than the {o.MaxInputChars} characters allowed", model, redactions, batch), redactions);
        var max = Math.Clamp(req.MaxOutputTokens, 1, Math.Max(1, o.MaxOutputTokens));
        return (new AiProviderRequest(model, req.Feature, system, user.ToString(), max, req.CacheSystemPrompt), null, redactions);
    }

    private AiResult Completed(AiRequest req, AiProviderRequest p, AiProviderResponse r, int attempts, int redactions, bool batch) =>
        Done(new AiResult(AiOutcome.Completed, req.Feature, req.WorkspaceId, Now, Text: r.Text, Model: p.Model,
            Usage: new AiUsage(req.Feature, p.Model, r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, batch),
            Attempts: attempts, ProviderCalled: true, Redactions: redactions, Batch: batch));

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var (refusal, model) = await CheckAsync(request.WorkspaceId, request.Feature, ct);
        if (refusal is not null) return Refused(request, refusal.Value.Outcome, refusal.Value.Detail, model);
        var (p, blocked, redactions) = Prepare(request, model!, batch: false);
        if (blocked is not null) return blocked;

        var o = options.CurrentValue;
        var retries = o.EffectiveRetries;
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(o.Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var answer = await provider.CompleteAsync(p!, linked.Token);
                return Completed(request, p!, answer, attempt, redactions, batch: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                if (attempt > retries) return Failed(request, AiOutcome.TimedOut, $"No answer within {o.TimeoutSeconds} seconds, {attempt} attempt(s)", p!, attempt, redactions);
                log.LogWarning("AI call {Feature} timed out on attempt {Attempt}; retrying", request.Feature, attempt);
            }
            catch (AiProviderException ex)
            {
                if (!ex.Transient || attempt > retries) return Failed(request, AiOutcome.ProviderError, ex.Message, p!, attempt, redactions);
                log.LogWarning("AI call {Feature} failed on attempt {Attempt} ({Reason}); retrying", request.Feature, attempt, ex.Message);
            }
            var delay = TimeSpan.FromMilliseconds(Math.Max(0, o.RetryBaseDelayMilliseconds) * Math.Pow(2, attempt - 1));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);
        }
    }

    private AiResult Failed(AiRequest req, AiOutcome outcome, string detail, AiProviderRequest p, int attempts, int redactions, bool batch = false)
    {
        log.LogError("AI call {Feature} in workspace {Workspace} failed: {Outcome} after {Attempts} attempt(s)", req.Feature, req.WorkspaceId, outcome, attempts);
        return Done(new AiResult(outcome, req.Feature, req.WorkspaceId, Now, Detail: detail, Model: p.Model, Attempts: attempts, ProviderCalled: true, Redactions: redactions, Batch: batch));
    }

    public async Task<IReadOnlyList<AiResult>> CompleteBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken ct = default)
    {
        var results = new AiResult?[requests.Count];
        var send = new List<(int Index, AiProviderRequest Request, int Redactions)>();
        for (var i = 0; i < requests.Count; i++)
        {
            var (refusal, model) = await CheckAsync(requests[i].WorkspaceId, requests[i].Feature, ct);
            if (refusal is not null) { results[i] = Refused(requests[i], refusal.Value.Outcome, refusal.Value.Detail, model, batch: true); continue; }
            var (p, blocked, redactions) = Prepare(requests[i], model!, batch: true);
            if (blocked is not null) results[i] = blocked; else send.Add((i, p!, redactions));
        }
        if (send.Count > 0)
        {
            var retries = options.CurrentValue.EffectiveRetries;
            IReadOnlyList<AiProviderResponse?>? answers = null;
            string? failure = null;
            for (var attempt = 1; ; attempt++)
            {
                try { answers = await provider.CompleteBatchAsync(send.Select(s => s.Request).ToList(), ct); break; }
                catch (AiProviderException ex)
                {
                    failure = ex.Message;
                    if (!ex.Transient || attempt > retries) break;
                    var delay = TimeSpan.FromMilliseconds(Math.Max(0, options.CurrentValue.RetryBaseDelayMilliseconds) * Math.Pow(2, attempt - 1));
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);
                }
            }
            for (var k = 0; k < send.Count; k++)
            {
                var (i, p, redactions) = send[k];
                var a = answers is not null && k < answers.Count ? answers[k] : null;
                results[i] = a is not null ? Completed(requests[i], p, a, 1, redactions, batch: true)
                    : Failed(requests[i], AiOutcome.ProviderError, failure ?? "The batch had no answer for this request", p, 1, redactions, batch: true);
            }
        }
        return results!;
    }
}
