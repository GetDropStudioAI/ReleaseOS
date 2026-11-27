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
/// REOS-184 (D58): every result, refusals included, is recorded through <see cref="IAiActionLog"/> before it is returned (an AiActions row and its audit row, one transaction). The result carries the
/// action's id so the feature can record the human's decision on the draft. If the action cannot be recorded the call becomes <see cref="AiOutcome.AuditFailed"/> and the output is withheld.
/// REOS-185 (D57): when an <see cref="IAiAllowance"/> is present every call RESERVES its worst-case cost before the model is called (after the data rules, which size the prompt) and is refused, with a message, when
/// the month's allowance, chat's share or the per-person rate limit says no; the reservation travels in <see cref="AiResult.Metering"/> and is settled with the real cost in the audit-trail transaction. A call whose
/// allowance cannot be reserved is not made (<see cref="AiOutcome.AuditFailed"/>): a call that is not on the books must not happen. A caller's cancellation gives the reservation back.
/// </summary>
public sealed class GovernedAiClient(IAiProvider provider, IAiKeyStore keys, IOptionsMonitor<AiOptions> options, IAiWorkspaceSettings workspaces, IModuleGate modules,
    IAiUsageSink sink, IAiActionLog actions, TimeProvider time, ILogger<GovernedAiClient> log, IAiAllowance? allowance = null) : IAiClient
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
        if (refusal is not null) return new AiPreflight(false, refusal.Value.Outcome, refusal.Value.Detail, model);
        if (allowance is not null)
        {
            var cap = await allowance.CheckAsync(workspaceId, feature, ct);   // read-only: reserves nothing, costs nothing
            if (!cap.Allowed) return new AiPreflight(false, AiOutcome.AllowanceReached, cap.Message, model);
        }
        return new AiPreflight(true, AiOutcome.Completed, null, model);
    }

    private async Task<((AiOutcome Outcome, string Detail)? Refusal, string? Model)> CheckAsync(string workspaceId, string feature, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var def = AiFeatures.Find(feature);
        if (def is null) return ((AiOutcome.Blocked, $"'{feature}' is not an AI feature"), null);
        var model = RouteModel(o, def);
        if (!await modules.IsEnabledAsync(workspaceId, ModuleKeys.Ai, ct)) return ((AiOutcome.ModuleOff, "The AI module is off in this workspace"), model);
        if (o.KillSwitch) return ((AiOutcome.KillSwitch, "The global AI kill switch is on"), model);
        var ws = await workspaces.GetAsync(workspaceId, ct);
        if (ws.Paused) return ((AiOutcome.WorkspacePaused, "AI is paused for this workspace"), model);
        if (ws.OptedOut) return ((AiOutcome.OptedOut, "This workspace opted out of AI"), model);
        if (model is null) return ((AiOutcome.NotConfigured, "No model is configured (Ai:Models:Small)"), null);
        if (provider.RequiresKey && !await keys.HasKeyAsync(ct)) return ((AiOutcome.NoKey, "No provider key is stored"), model);
        return (null, model);
    }

    private async Task<AiResult> DoneAsync(AiRequest req, AiResult r)
    {
        r = r with { Provider = provider.Name };
        try { r = r with { ActionId = await actions.RecordAsync(req, r, CancellationToken.None) }; }   // not the caller's token: the model already answered, so the record must complete
        catch (Exception ex)
        {
            log.LogError(ex, "The AI action for feature {Feature} in workspace {Workspace} could not be recorded; its output is withheld", r.Feature, r.WorkspaceId);   // surfaced, never swallowed
            await ReleaseAsync(req, r.Metering, "The AI action could not be recorded in the audit trail");   // nothing was settled (one transaction): the reserved money goes back
            r = r with { Outcome = AiOutcome.AuditFailed, Text = null, ActionId = null, Detail = "The AI action could not be recorded in the audit trail, so its output was withheld" };
        }
        try { sink.Record(r); }
        catch (Exception ex) { log.LogError(ex, "The AI usage sink failed for feature {Feature}", r.Feature); }   // surfaced, never swallowed; the result still reaches the caller
        log.LogInformation("AI call {Feature} in workspace {Workspace}: {Outcome}, model {Model}, tokens in {In} out {Out}, attempts {Attempts}, redactions {Redactions}",
            r.Feature, r.WorkspaceId, r.Outcome, r.Model, r.Usage?.InputTokens, r.Usage?.OutputTokens, r.Attempts, r.Redactions);
        return r;
    }

    private Task<AiResult> Refused(AiRequest req, AiOutcome outcome, string detail, string? model, int redactions = 0, bool batch = false, Prepared? prep = null, AiMetering? metering = null) =>
        DoneAsync(req, new AiResult(outcome, req.Feature, req.WorkspaceId, Now, Detail: detail, Model: model, Redactions: redactions, Batch: batch,
            Inputs: prep?.Inputs, InputHash: prep?.InputHash, PromptHash: prep?.PromptHash, Metering: metering));

    /// <summary>
    /// REOS-185: reserves the call's worst-case cost. Returns the metering the result will carry, or the refusal (a cap or the rate limit, already recorded) or the failure (not reservable: no call is made).
    /// With no allowance wired (a unit test of another rule) the call is simply unmetered.
    /// </summary>
    private async Task<(AiMetering? Metering, AiResult? Refusal)> MeterAsync(AiRequest req, Prepared prep, bool batch, CancellationToken ct)
    {
        if (allowance is null) return (null, null);
        var cost = options.CurrentValue.Pricing.ToCostModel();
        var estimate = cost.Estimate(prep.Request.Model, prep.Request.System.Length + prep.Request.User.Length, prep.Request.MaxOutputTokens, prep.Request.CacheSystem, batch);
        AiReservation r;
        try { r = await allowance.ReserveAsync(req.WorkspaceId, req.Feature, req.Meta?.RequestedByUserId, prep.Request.Model, estimate, batch, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The AI allowance for feature {Feature} in workspace {Workspace} could not be reserved; no call is made", req.Feature, req.WorkspaceId);   // surfaced, never swallowed
            return (null, new AiResult(AiOutcome.AuditFailed, req.Feature, req.WorkspaceId, Now, Detail: "The AI allowance could not be checked, so no call was made", Model: prep.Request.Model, Batch: batch));
        }
        if (!r.Granted)
        {
            var outcome = r.Refusal == AiCapReason.RateLimit ? AiOutcome.RateLimited : AiOutcome.AllowanceReached;
            return (null, await Refused(req, outcome, r.Message ?? "The AI allowance does not allow this call", prep.Request.Model, prep.Redactions, batch, prep, new AiMetering(null, r.PeriodId, 0, 0, r.Refusal)));
        }
        return (new AiMetering(r.EntryId, r.PeriodId, r.ReservedMicroUsd, 0, null), null);
    }

    /// <summary>A reservation whose call never reached the audit trail (cancelled, or an unexpected error) is given back; failing to give it back is logged and left to the stale sweep.</summary>
    private async Task ReleaseAsync(AiRequest req, AiMetering? m, string reason)
    {
        if (allowance is null || m?.EntryId is not { } id) return;
        try { await allowance.ReleaseAsync(req.WorkspaceId, id, reason, CancellationToken.None); }
        catch (Exception ex) { log.LogError(ex, "The AI reservation {Entry} for feature {Feature} could not be given back; the stale sweep will release it", id, req.Feature); }
    }

    /// <summary>What was sent, as the trail records it: hashes of the redacted texts (the model saw the redacted text, so that is what is fingerprinted), never the texts.</summary>
    private sealed record Prepared(AiProviderRequest Request, IReadOnlyList<AiInputRef> Inputs, string InputHash, string PromptHash, int Redactions);

    /// <summary>Applies the data rules and redaction. Returns what is to be sent, or a refusal.</summary>
    private async Task<(Prepared? Prepared, AiResult? Refusal)> PrepareAsync(AiRequest req, string model, bool batch)
    {
        var o = options.CurrentValue;
        var bad = req.Context?.FirstOrDefault(c => c.Class is AiDataClass.TicketSummary or AiDataClass.CustomerData);
        if (bad is not null)
            return (null, await Refused(req, AiOutcome.Blocked, $"Context '{bad.Label}' is {bad.Class}: ticket summaries and customer data are never sent to a model", model, batch: batch));
        var redactions = 0;
        string R(string? s) { var t = AiRedactor.Redact(s, out var n); redactions += n; return t; }
        var system = R(req.SystemPrompt);
        var user = new StringBuilder(R(req.Instruction));
        var inputs = new List<AiInputRef>();
        foreach (var c in req.Context ?? [])
        {
            var label = R(c.Label); var text = R(c.Text);
            user.Append("\n\n## ").Append(label).Append('\n').Append(text);
            inputs.Add(new AiInputRef(label.Length > 100 ? label[..100] : label, c.Class.ToString(), AiHash.Sha256(text), text.Length));
        }
        var max = Math.Clamp(req.MaxOutputTokens, 1, Math.Max(1, o.MaxOutputTokens));
        var prompt = user.ToString();
        var prepared = new Prepared(new AiProviderRequest(model, req.Feature, system, prompt, max, req.CacheSystemPrompt), inputs, AiHash.Sha256(system + "\n---\n" + prompt), AiHash.Sha256(system), redactions);
        if (system.Length + user.Length > o.MaxInputChars)
            return (null, await Refused(req, AiOutcome.Blocked, $"The prompt is larger than the {o.MaxInputChars} characters allowed", model, redactions, batch, prepared));
        return (prepared, null);
    }

    private Task<AiResult> Completed(AiRequest req, Prepared prep, AiProviderResponse r, int attempts, bool batch, AiMetering? metering)
    {
        var usage = new AiUsage(req.Feature, prep.Request.Model, r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, batch);
        // Priced from the model we routed to, which is what the provider bills (ServedModel is the exact version of the same model).
        var priced = metering is null ? null : metering with { CostMicroUsd = options.CurrentValue.Pricing.ToCostModel().Cost(usage) };
        return DoneAsync(req, new AiResult(AiOutcome.Completed, req.Feature, req.WorkspaceId, Now, Text: r.Text, Model: prep.Request.Model, Usage: usage,
            Attempts: attempts, ProviderCalled: true, Redactions: prep.Redactions, Batch: batch, ServedModel: r.ServedModel, Inputs: prep.Inputs, InputHash: prep.InputHash, PromptHash: prep.PromptHash, Metering: priced));
    }

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        var (refusal, model) = await CheckAsync(request.WorkspaceId, request.Feature, ct);
        if (refusal is not null) return await Refused(request, refusal.Value.Outcome, refusal.Value.Detail, model);
        var (prep, blocked) = await PrepareAsync(request, model!, batch: false);
        if (blocked is not null) return blocked;
        var (metering, capped) = await MeterAsync(request, prep!, batch: false, ct);
        if (capped is not null) return capped;
        var p = prep!.Request;

        var o = options.CurrentValue;
        var retries = o.EffectiveRetries;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var timeout = new CancellationTokenSource(o.Timeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                try
                {
                    var answer = await provider.CompleteAsync(p, linked.Token);
                    return await Completed(request, prep, answer, attempt, batch: false, metering);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    if (attempt > retries) return await Failed(request, AiOutcome.TimedOut, $"No answer within {o.TimeoutSeconds} seconds, {attempt} attempt(s)", prep, attempt, metering: metering);
                    log.LogWarning("AI call {Feature} timed out on attempt {Attempt}; retrying", request.Feature, attempt);
                }
                catch (AiProviderException ex)
                {
                    if (!ex.Transient || attempt > retries) return await Failed(request, AiOutcome.ProviderError, ex.Message, prep, attempt, metering: metering);
                    log.LogWarning("AI call {Feature} failed on attempt {Attempt} ({Reason}); retrying", request.Feature, attempt, ex.Message);
                }
                var delay = TimeSpan.FromMilliseconds(Math.Max(0, o.RetryBaseDelayMilliseconds) * Math.Pow(2, attempt - 1));
                if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);
            }
        }
        catch (Exception ex) when (metering is not null)
        {
            await ReleaseAsync(request, metering, ex is OperationCanceledException ? "The caller cancelled the call" : "The call ended with an unexpected error");   // the answer never reached the trail: give the money back
            throw;
        }
    }

    private Task<AiResult> Failed(AiRequest req, AiOutcome outcome, string detail, Prepared prep, int attempts, bool batch = false, AiMetering? metering = null)
    {
        log.LogError("AI call {Feature} in workspace {Workspace} failed: {Outcome} after {Attempts} attempt(s)", req.Feature, req.WorkspaceId, outcome, attempts);
        return DoneAsync(req, new AiResult(outcome, req.Feature, req.WorkspaceId, Now, Detail: detail, Model: prep.Request.Model, Attempts: attempts, ProviderCalled: true, Redactions: prep.Redactions, Batch: batch,
            Inputs: prep.Inputs, InputHash: prep.InputHash, PromptHash: prep.PromptHash, Metering: metering));
    }

    public async Task<IReadOnlyList<AiResult>> CompleteBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken ct = default)
    {
        var results = new AiResult?[requests.Count];
        var send = new List<(int Index, Prepared Prep, AiMetering? Metering)>();
        try
        {
            for (var i = 0; i < requests.Count; i++)
            {
                var (refusal, model) = await CheckAsync(requests[i].WorkspaceId, requests[i].Feature, ct);
                if (refusal is not null) { results[i] = await Refused(requests[i], refusal.Value.Outcome, refusal.Value.Detail, model, batch: true); continue; }
                var (prep, blocked) = await PrepareAsync(requests[i], model!, batch: true);
                if (blocked is not null) { results[i] = blocked; continue; }
                var (metering, capped) = await MeterAsync(requests[i], prep!, batch: true, ct);   // each request is held, and capped, on its own
                if (capped is not null) results[i] = capped; else send.Add((i, prep!, metering));
            }
            if (send.Count > 0)
            {
                var retries = options.CurrentValue.EffectiveRetries;
                IReadOnlyList<AiProviderResponse?>? answers = null;
                string? failure = null;
                for (var attempt = 1; ; attempt++)
                {
                    try { answers = await provider.CompleteBatchAsync(send.Select(s => s.Prep.Request).ToList(), ct); break; }
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
                    var (i, prep, metering) = send[k];
                    var a = answers is not null && k < answers.Count ? answers[k] : null;
                    results[i] = a is not null ? await Completed(requests[i], prep, a, 1, batch: true, metering)
                        : await Failed(requests[i], AiOutcome.ProviderError, failure ?? "The batch had no answer for this request", prep, 1, batch: true, metering: metering);
                }
            }
        }
        catch (Exception ex)
        {
            // Cancelled or failed unexpectedly part-way: every reservation not yet turned into a record goes back.
            foreach (var (i, _, metering) in send)
                if (results[i] is null) await ReleaseAsync(requests[i], metering, ex is OperationCanceledException ? "The caller cancelled the batch" : "The batch ended with an unexpected error");
            throw;
        }
        return results!;
    }
}
