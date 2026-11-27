using System.Security.Cryptography;
using System.Text;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Ai;

/// <summary>The human's decision on an AI draft (D58): it was used as it stood, used after an edit, or thrown away.</summary>
public static class AiDecisions
{
    public const string Pending = "Pending", Accepted = "Accepted", Edited = "Edited", Rejected = "Rejected", NotApplicable = "NotApplicable";
    /// <summary>What a person may record (Pending and NotApplicable are states, not decisions).</summary>
    public static readonly IReadOnlyList<string> Decidable = [Accepted, Edited, Rejected];
}

public static class AiGuards
{
    public const string AiActionDecision = "AiActionDecision";          // a decision that is not Accepted, Edited or Rejected, or an Edited decision with no changed text
    public const string AiActionDecided = "AiActionDecided";            // the action already has its human decision (it is final)
    public const string AiActionNotDecidable = "AiActionNotDecidable";  // a refused or failed call has no draft to decide on
    public const string AiActionRole = "AiActionRole";                  // only the person who asked, or an administrator, records the decision
}

/// <summary>The audit trail's view of one action. Output and edit are the full texts; inputs are references and hashes only.</summary>
public sealed record AiActionView(string Id, string Feature, string FeatureName, string Provider, string? Model, string? ServedModel, string PromptTemplateVersion, string? PromptHash,
    IReadOnlyList<AiInputRef> Inputs, string? InputHash, string Outcome, string? Detail, string? OutputText, string? OutputHash, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens,
    int Attempts, int Redactions, bool Batch, string? SubjectType, string? SubjectId, string? ReleaseTrainId, AiPersonRef? RequestedBy, DateTime RequestedAt,
    string Decision, string? FinalText, string? FinalHash, string? DecisionNote, AiPersonRef? DecidedBy, DateTime? DecidedAt, int Version, AiActionIntegrity Integrity);

public sealed record AiPersonRef(string Id, string Name);

/// <summary>Whether the stored texts still match their hashes and the audit rows that carry those hashes in the chain (REOS-184: "audit chain verification still passes").</summary>
public sealed record AiActionIntegrity(bool OutputMatchesHash, bool FinalMatchesHash, bool GeneratedAuditRow, bool DecisionAuditRow, bool Ok);

/// <summary>A line of the list on the AI inventory screen: no texts, only what a reviewer scans.</summary>
public sealed record AiActionSummary(string Id, string Feature, string FeatureName, string? Model, string Outcome, string Decision, long InputTokens, long OutputTokens, bool Edited,
    string? SubjectType, string? SubjectId, AiPersonRef? RequestedBy, DateTime RequestedAt, AiPersonRef? DecidedBy, DateTime? DecidedAt);

public sealed record AiActionFilter(string? Feature = null, string? Decision = null, string? Outcome = null, string? RequestedByUserId = null, string? SubjectType = null, string? SubjectId = null, int Limit = 100);

/// <summary>
/// REOS-184 (D58): the audit trail of AI-assisted actions. <see cref="RecordAsync"/> is what the governed client calls for every result, refusals included: it writes one AiActions row and one audit row
/// in a single transaction. <see cref="DecideAsync"/> adds the human decision once. Nothing here, and nothing an AI path can reach, changes a status: the rows are separate from every status table.
/// </summary>
public interface IAiActionLog
{
    /// <summary>Records one call and returns the action's id. Throws when it cannot be recorded (fail fast, rule 8): the governed client then withholds the output.</summary>
    Task<string> RecordAsync(AiRequest request, AiResult result, CancellationToken ct = default);
    Task<ServiceResult<AiActionView>> DecideAsync(string actionId, string decision, string? finalText, string? note, Actor actor, bool isAdmin, int? expectedVersion, CancellationToken ct = default);
    Task<AiActionView?> GetAsync(string actionId, CancellationToken ct = default);
    Task<IReadOnlyList<AiActionSummary>> ListAsync(AiActionFilter filter, CancellationToken ct = default);
}

/// <summary>Changing the workspace controls (REOS-184): each change is versioned, written in one transaction with its audit row, and kept across restarts.</summary>
public interface IAiControls
{
    Task<AiWorkspaceState> SetPausedAsync(string workspaceId, bool paused, Actor actor, CancellationToken ct = default);
    Task<AiWorkspaceState> SetOptedOutAsync(string workspaceId, bool optedOut, Actor actor, CancellationToken ct = default);
    /// <summary>The provider key was stored or removed. Audited without the key (host-wide row, under the actor).</summary>
    Task RecordKeyChangeAsync(string action, Actor actor, CancellationToken ct = default);
}

/// <summary>The one hash the trail uses (SHA-256, lower-case hex, over UTF-8): the same function writes a hash and checks it, so a verifier cannot drift from the writer.</summary>
public static class AiHash
{
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
