using System.Text.RegularExpressions;

namespace ReleaseMgmt.Domain.Ai;

/// <summary>
/// REOS-183 (D58, D12): the AI platform's contracts. Every AI call goes through <see cref="IAiClient"/>; nothing else may reach a model provider (an architecture test fails if it does).
/// AI is advisory only: nothing here, and no AI code path, calls a status-changing domain service (a second architecture test). REOS-184 (D58) records every call as an AiActions row with its audit row
/// (<see cref="IAiActionLog"/>), keeps the per-workspace controls in the database (<see cref="IAiWorkspaceSettings"/>, <see cref="IAiControls"/>) and stores AI output apart from every status row.
/// </summary>
public enum AiModelTier { Small, Drafting }

/// <summary>An AI feature the platform routes. Small models are the default; only drafting features use the (optional, larger) drafting model.
/// <paramref name="CappedFirst"/> (REOS-185): chat and query features are the unbounded cost (85 to 90% of AI spend), so they stop at the soft cap and at their share of the allowance, before everything else.</summary>
public sealed record AiFeatureDefinition(string Key, string Name, AiModelTier Tier, bool CappedFirst = false);

public static class AiFeatures
{
    public const string RiskScore = "risk-score";
    public const string ReleaseNotes = "release-notes-draft";
    public const string CommsDraft = "comms-draft";
    public const string GateSummary = "gate-summary";
    public const string RunbookDraft = "runbook-draft";
    public const string PirDraft = "pir-draft";
    public const string Chat = "chat";

    /// <summary>The routing table (saas-market-and-gap-analysis section 6; pricing-and-infrastructure section 5: Haiku-class for most calls, larger only for drafting).</summary>
    public static readonly IReadOnlyList<AiFeatureDefinition> All =
    [
        new(RiskScore, "Release risk score", AiModelTier.Small),
        new(GateSummary, "Gate evidence summary", AiModelTier.Small),
        new(Chat, "Question about release data", AiModelTier.Small, CappedFirst: true),
        new(ReleaseNotes, "Release notes draft", AiModelTier.Drafting),
        new(CommsDraft, "Stakeholder message draft", AiModelTier.Drafting),
        new(RunbookDraft, "Runbook and rollback draft", AiModelTier.Drafting),
        new(PirDraft, "Post-implementation review draft", AiModelTier.Drafting),
    ];

    public static AiFeatureDefinition? Find(string? key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>What a piece of context is. Ticket summaries and customer data are never sent (D12 stands); the gateway refuses a request that carries either.</summary>
public enum AiDataClass { ReleaseData, FreeText, TicketSummary, CustomerData }

public sealed record AiContextItem(string Label, string Text, AiDataClass Class = AiDataClass.ReleaseData);

/// <param name="CacheSystemPrompt">Ask the provider to cache the (large, stable) system prompt: a repeat costs about 5% of the fresh input price.</param>
/// <param name="MaxOutputTokens">Clamped to the platform's cap.</param>
/// <param name="Meta">Who asked, what the answer is about and which prompt version was used: what the audit trail records (REOS-184). Optional for a caller with nothing to say; the call is recorded either way.</param>
public sealed record AiRequest(string WorkspaceId, string Feature, string SystemPrompt, string Instruction, IReadOnlyList<AiContextItem>? Context = null,
    int MaxOutputTokens = 1024, bool CacheSystemPrompt = false, AiCallMeta? Meta = null);

/// <summary>What the audit trail needs to know about a call that the request text does not say (REOS-184). Nothing here is sent to a model.</summary>
/// <param name="RequestedByUserId">The person who asked; null for a scheduled or batch run (the system).</param>
/// <param name="SubjectType">What the answer is about ('Train', 'Gate', ...) together with <paramref name="SubjectId"/>: a plain reference, never a link that could carry a status.</param>
/// <param name="PromptTemplateVersion">The version of the prompt text the feature used, e.g. 'release-notes/1'. Bump it whenever the template changes.</param>
public sealed record AiCallMeta(string? RequestedByUserId = null, string? SubjectType = null, string? SubjectId = null, string? ReleaseTrainId = null, string PromptTemplateVersion = AiCallMeta.Untracked)
{
    public const string Untracked = "untracked";
}

/// <summary>One piece of context as the trail records it: its label, class, hash and size. Never its text.</summary>
public sealed record AiInputRef(string Label, string Class, string Sha256, int Chars);

/// <remarks><c>AuditFailed</c> (REOS-184): the call was made but the action could not be recorded, so its output is withheld: an AI answer that is not in the trail must not be used.
/// REOS-185: <c>AllowanceReached</c> (a cap stopped the call: the month's allowance is used up, or chat's share or the soft cap is reached) and <c>RateLimited</c> (this person asked too often) are refusals:
/// no model was called. In the audit trail they are recorded as <c>Blocked</c> with the message as the detail (the table's outcome list is unchanged); the usage ledger records the exact reason (Q-185a).
/// An unmeterable call (the allowance could not be reserved) is <c>AuditFailed</c> too: a call that is not on the books is not made.</remarks>
public enum AiOutcome { Completed, ModuleOff, KillSwitch, WorkspacePaused, OptedOut, NotConfigured, NoKey, Blocked, TimedOut, ProviderError, AuditFailed, AllowanceReached, RateLimited }

/// <summary>The cost inputs of one call (pricing-and-infrastructure section 5), recorded for the metering story. Cached tokens are counted apart because they price differently.</summary>
public sealed record AiUsage(string Feature, string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, bool Batch);

/// <summary>The answer to a call. <c>ProviderCalled</c> is false for every outcome except Completed, TimedOut and ProviderError: a refusal never reaches the network.</summary>
/// <param name="ActionId">The AiActions row that records this call (REOS-184); a feature keeps it to record the human's decision on the draft.</param>
public sealed record AiResult(AiOutcome Outcome, string Feature, string WorkspaceId, DateTime At, string? Text = null, string? Detail = null, string? Model = null,
    AiUsage? Usage = null, int Attempts = 0, bool ProviderCalled = false, int Redactions = 0, bool Batch = false,
    string? ActionId = null, string? Provider = null, string? ServedModel = null, IReadOnlyList<AiInputRef>? Inputs = null, string? InputHash = null, string? PromptHash = null,
    AiMetering? Metering = null)
{
    public bool IsCompleted => Outcome == AiOutcome.Completed;
}

/// <summary>Why a call would or would not be made right now, without making it (the preflight endpoint, the settings panel).</summary>
public sealed record AiPreflight(bool Allowed, AiOutcome Outcome, string? Detail, string? Model);

/// <summary>The only way to use a model. Implemented by the governed client, which applies the module guard, kill switches, opt-out, data rules, redaction, routing, timeouts and retries.</summary>
public interface IAiClient
{
    Task<AiPreflight> PreflightAsync(string workspaceId, string feature, CancellationToken ct = default);
    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default);
    /// <summary>The batch path for scheduled work (half price, answered later). Results come back in request order; each request passes the same rules as <see cref="CompleteAsync"/>.</summary>
    Task<IReadOnlyList<AiResult>> CompleteBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken ct = default);
}

/// <summary>What a provider adapter is asked to do, after every rule has been applied.</summary>
public sealed record AiProviderRequest(string Model, string Feature, string System, string User, int MaxOutputTokens, bool CacheSystem);

/// <param name="ServedModel">The exact model version the provider says answered, when it says (recorded as the model version in the trail).</param>
public sealed record AiProviderResponse(string Text, long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0, string? ServedModel = null);

/// <summary>An adapter failure. <c>Transient</c> failures (timeouts, 429, 5xx, network) are retried a bounded number of times; the others are not.</summary>
public sealed class AiProviderException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>A provider adapter. Reached only through the governed client. It never sees a request the rules refused.</summary>
public interface IAiProvider
{
    string Name { get; }
    /// <summary>False for the deterministic fake, which needs no key.</summary>
    bool RequiresKey { get; }
    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct);
    /// <summary>One entry per request, in order; a null entry is a request the provider could not answer.</summary>
    Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct);
}

/// <summary>The provider key (CLAUDE.md rule 11, D12): Data Protection or the OS secret store, never appsettings or SQLite. The key is never returned by any endpoint or written to a log.</summary>
public interface IAiKeyStore
{
    Task<bool> HasKeyAsync(CancellationToken ct = default);
    Task<string?> GetKeyAsync(CancellationToken ct = default);
    Task SetKeyAsync(string key, CancellationToken ct = default);
    Task<bool> ClearKeyAsync(CancellationToken ct = default);
}

public sealed record AiWorkspaceState(bool Paused, bool OptedOut);

/// <summary>The per-workspace kill switch and opt-out, read from the database (REOS-184): they survive a restart, so an opt-out never fails open. Changing them is <see cref="IAiControls"/>.</summary>
public interface IAiWorkspaceSettings
{
    Task<AiWorkspaceState> GetAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Receives every result, refusals included, for the later metering and audit story. The default does nothing.</summary>
public interface IAiUsageSink
{
    void Record(AiResult result);
}

/// <summary>Token redaction before anything is sent: API keys, bearer tokens, JWTs, cloud and source-host tokens, passwords in assignments, and e-mail addresses.</summary>
public static class AiRedactor
{
    public const string Mask = "[redacted]";

    private static readonly Regex[] Patterns =
    [
        new(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),                                  // provider API keys (sk-ant-..., sk-...)
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled),                             // GitHub tokens
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
        new(@"\bxox[abpr]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled),                           // Slack
        new(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled),                            // AWS access key ids
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),   // JWTs
        new(@"(?i)\b(?:password|passwd|secret|token|api[_-]?key|authorization)\b\s*[:=]\s*[^\s,;]{4,}", RegexOptions.Compiled),
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),          // e-mail addresses
    ];

    public static string Redact(string? text, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var n = 0;
        var result = text;
        foreach (var p in Patterns) result = p.Replace(result, _ => { n++; return Mask; });
        count = n;
        return result;
    }
}
