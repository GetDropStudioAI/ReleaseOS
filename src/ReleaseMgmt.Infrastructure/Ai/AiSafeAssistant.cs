using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-186 (D58): the safe way for a feature to ask a question that needs data. In order: (1) ask the governed client whether a call would be made at all, so nothing is read for a call that would be refused;
/// (2) check the person: a workspace the request names that is not theirs, or a source their role may not read through the API (<see cref="AiReadPolicy"/>), refuses the whole request before any read, and the refusal
/// is recorded in the audit trail like every other refused call; (3) read each source as the person through <see cref="IAiRetrievalSource"/> (read-only by construction) and check every item's workspace again;
/// (4) fence everything a person or an import wrote as data (<see cref="AiUntrusted"/>) and put the safety rules in the system prompt; (5) call <see cref="IAiClient"/>, which applies the D12 data rules (ticket summaries are
/// refused), redaction, routing and the audit record; (6) sanitise the answer for display. It never calls a status-changing service and cannot: it holds only interfaces (an architecture test checks it). Any refusal fails
/// closed: no model is called with partial data. Logs carry feature, workspace, source names and counts, never text.
/// </summary>
public sealed class AiSafeAssistant(IAiClient client, IAiActionLog actions, IEnumerable<IAiRetrievalSource> sources, TimeProvider time, ILogger<AiSafeAssistant> log) : IAiSafeAssistant
{
    private readonly Dictionary<string, IAiRetrievalSource> _sources = sources.ToDictionary(s => s.Source, StringComparer.Ordinal);

    public async Task<AiSafeAnswer> AskAsync(AiPrincipal principal, AiAskRequest request, CancellationToken ct = default)
    {
        var meta = new AiCallMeta(string.IsNullOrWhiteSpace(principal.UserId) ? null : principal.UserId, request.SubjectType, request.SubjectId, request.ReleaseTrainId, request.PromptTemplateVersion + "+" + AiSafetyPrompt.Version);
        var nonce = AiUntrusted.NewNonce();
        AiRequest Build(IReadOnlyList<AiContextItem>? context) =>
            new(principal.WorkspaceId, request.Feature, AiSafetyPrompt.Preamble(nonce) + "\n\n" + request.SystemPrompt, request.Instruction, context, request.MaxOutputTokens, CacheSystemPrompt: false, meta);

        var refusals = new List<AiRetrievalRefusal>();
        if (string.IsNullOrWhiteSpace(principal.WorkspaceId) || string.IsNullOrWhiteSpace(principal.UserId))
            return await RefusedAsync(principal, request, meta, [new("request", "There is no signed-in person or workspace for this request")], ct);

        // 1. Would a call be made at all? If not, let the governed client record that refusal and read nothing.
        var pre = await client.PreflightAsync(principal.WorkspaceId, request.Feature, ct);
        if (!pre.Allowed) return Answer(await client.CompleteAsync(Build(null), ct), refusals);

        // 2. The person: the workspace named must be theirs, and their role must be able to read every source asked for.
        if (!string.Equals(request.WorkspaceId, principal.WorkspaceId, StringComparison.Ordinal))
            refusals.Add(new("workspace", "The request names a workspace that is not the one the person is working in"));
        var wanted = request.Sources.Distinct(StringComparer.Ordinal).ToList();
        foreach (var s in wanted)
        {
            if (!AiReadPolicy.Allows(principal.Role, s)) refusals.Add(new(s, $"The role {principal.Role ?? "(none)"} may not read '{s}', so AI may not either"));
            else if (!_sources.ContainsKey(s)) refusals.Add(new(s, $"'{s}' is not available to AI"));
        }
        if (refusals.Count > 0) return await RefusedAsync(principal, request, meta, refusals, ct);

        // 3. Read as the person, then check every item once more.
        var context = new List<AiContextItem>();
        var fenced = 0;
        foreach (var s in wanted)
        {
            var items = await _sources[s].ReadAsync(principal, request.SubjectId, ct);
            foreach (var item in items)
            {
                if (!string.Equals(item.WorkspaceId, principal.WorkspaceId, StringComparison.Ordinal) || !string.Equals(item.Source, s, StringComparison.Ordinal))
                {
                    log.LogError("AI source {Source} returned an item for another workspace or source in workspace {Workspace}; the request was refused", s, principal.WorkspaceId);
                    refusals.Add(new(s, "The source returned data that does not belong to this workspace or source"));
                    break;
                }
                // 4. Fence what people wrote. A ticket summary keeps its class (the gateway refuses it, D12); its text is fenced too, in case a later rule lets it through.
                if (item.Untrusted)
                {
                    context.Add(new AiContextItem(AiUntrusted.Label(item.Label), AiUntrusted.Wrap(item.Label, item.Text, nonce, out var removed), item.Class));
                    fenced++;
                    if (removed > 0) log.LogWarning("AI context from {Source} in workspace {Workspace} had {Removed} fence-like or hidden characters removed", s, principal.WorkspaceId, removed);
                }
                else context.Add(new AiContextItem(AiUntrusted.Label(item.Label), item.Text, item.Class));
            }
        }
        if (refusals.Count > 0) return await RefusedAsync(principal, request, meta, refusals, ct);

        log.LogInformation("AI request {Feature} in workspace {Workspace}: {Sources} source(s), {Items} item(s), {Fenced} fenced", request.Feature, principal.WorkspaceId, wanted.Count, context.Count, fenced);

        // 5 and 6. The governed client applies the data rules and audits; the answer is sanitised for display.
        return Answer(await client.CompleteAsync(Build(context), ct), refusals);
    }

    private static AiSafeAnswer Answer(AiResult r, IReadOnlyList<AiRetrievalRefusal> refusals) =>
        new(r, r.IsCompleted ? AiOutputSanitizer.Sanitise(r.Text) : null, refusals);

    /// <summary>A retrieval refusal never reaches the model, so the governed client never sees it: record it here, under the person's own workspace (never the one a cross-workspace attempt named).</summary>
    private async Task<AiSafeAnswer> RefusedAsync(AiPrincipal principal, AiAskRequest request, AiCallMeta meta, IReadOnlyList<AiRetrievalRefusal> refusals, CancellationToken ct)
    {
        var detail = "Refused before any data was read or sent: " + string.Join("; ", refusals.Select(r => $"{r.Source}: {r.Reason}"));
        if (!string.Equals(request.WorkspaceId, principal.WorkspaceId, StringComparison.Ordinal)) detail += $" (named workspace {request.WorkspaceId})";
        var result = new AiResult(AiOutcome.Blocked, request.Feature, principal.WorkspaceId, time.GetUtcNow().UtcDateTime, Detail: detail, Provider: "none");
        try
        {
            result = result with { ActionId = await actions.RecordAsync(new AiRequest(principal.WorkspaceId, request.Feature, "", request.Instruction, null, request.MaxOutputTokens, false, meta), result, CancellationToken.None) };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The refused AI request for feature {Feature} in workspace {Workspace} could not be recorded", request.Feature, principal.WorkspaceId);   // surfaced; the refusal still stands
        }
        log.LogWarning("AI request {Feature} in workspace {Workspace} refused: {Sources}", request.Feature, principal.WorkspaceId, string.Join(",", refusals.Select(r => r.Source)));
        return new AiSafeAnswer(result, null, refusals);
    }
}
