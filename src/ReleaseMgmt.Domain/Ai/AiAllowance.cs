using System.Globalization;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Ai;

/// <summary>
/// REOS-185 (D57, D58): money for AI is whole micro-US-dollars (1 USD = 1,000,000), never a float or a decimal column (CLAUDE.md rule 5). A credit is the unit people see: one cent, 10,000 of them.
/// </summary>
public static class AiCredits
{
    public const long MicroUsdPerCredit = 10_000;
    public static long FromCredits(long credits) => checked(credits * MicroUsdPerCredit);
    public static double ToCredits(long microUsd) => Math.Round(microUsd / (double)MicroUsdPerCredit, 2);
}

/// <summary>US dollars per million tokens, as providers publish them. One dollar per million tokens is exactly one micro-dollar per token, which is why the arithmetic below needs no scaling.</summary>
public sealed record AiModelPrice(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>
/// What a call costs, from the provider's own usage numbers (pricing-and-infrastructure section 5). Model families are priced by name (Haiku, Sonnet, Opus), so a new model version needs no code; a configured
/// price for the exact model name wins; a model the table does not know is priced as the most expensive family, so an unknown model can never make a call look cheaper than it is. Cache reads cost a
/// fraction of fresh input, cache writes a little more than it, and the batch path half. Every cost is rounded UP to a whole micro-dollar: no call is ever free.
/// </summary>
public sealed class AiCostModel(IReadOnlyDictionary<string, AiModelPrice>? overrides = null, decimal cacheReadMultiplier = 0.05m, decimal cacheWriteMultiplier = 1.25m, decimal batchMultiplier = 0.5m)
{
    public static readonly AiModelPrice Haiku = new(0.10m, 0.50m), Sonnet = new(2m, 10m), Opus = new(4m, 20m);
    /// <summary>The most input characters one token can stand for in the worst case; the reservation assumes this so that it never undercounts. Real text is nearer 3 to 4.</summary>
    public const decimal WorstCaseCharsPerToken = 2m;

    public AiModelPrice PriceFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return Opus;
        var m = model.Trim();
        if (overrides is not null)
            foreach (var (k, v) in overrides) if (string.Equals(k, m, StringComparison.OrdinalIgnoreCase)) return v;
        if (m.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return Haiku;
        if (m.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return Sonnet;
        return Opus;
    }

    public long Cost(string? model, long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0, bool batch = false)
    {
        var p = PriceFor(model);
        var micro = inputTokens * p.InputPerMTok + outputTokens * p.OutputPerMTok + cacheReadTokens * p.InputPerMTok * cacheReadMultiplier + cacheWriteTokens * p.InputPerMTok * cacheWriteMultiplier;
        if (batch) micro *= batchMultiplier;
        return (long)Math.Ceiling(micro);
    }

    public long Cost(AiUsage u) => Cost(u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.Batch);

    /// <summary>The most a call can cost: every input character as its own half token (and, if the system prompt is to be cached, at the cache-write price), and the whole output budget used. At least 1.</summary>
    public long Estimate(string? model, int inputChars, int maxOutputTokens, bool cacheSystem, bool batch)
    {
        var inputTokens = (long)Math.Ceiling(inputChars / WorstCaseCharsPerToken);
        var p = PriceFor(model);
        var micro = inputTokens * p.InputPerMTok * (cacheSystem ? Math.Max(1m, cacheWriteMultiplier) : 1m) + Math.Max(0, maxOutputTokens) * p.OutputPerMTok;
        if (batch) micro *= batchMultiplier;
        return Math.Max(1, (long)Math.Ceiling(micro));
    }
}

/// <summary>Why the allowance stopped a call. HardCap: the month's allowance (included plus packs) is used up. ChatCap: chat is capped first, at its share or at the soft cap. RateLimit: this person asked too often.</summary>
public enum AiCapReason { HardCap, ChatCap, RateLimit }

/// <summary>The allowance policy of a workspace right now. A new month opens with the policy of that moment (and keeps it for the month). Today it comes from configuration; the plans story supplies it per plan.</summary>
/// <param name="IncludedMicroUsd">Included allowance per month.</param>
/// <param name="SoftCapPercent">Chat stops, and administrators are told, at this share of the allowance. Everything else goes on.</param>
/// <param name="ChatSharePercent">The most of the allowance chat may spend. 0 means no chat.</param>
/// <param name="UserPerMinute">Calls one person may start per minute (0 = no limit).</param>
/// <param name="UserPerHour">Calls one person may start per hour (0 = no limit).</param>
public sealed record AiAllowancePolicy(long IncludedMicroUsd, int SoftCapPercent, int ChatSharePercent, int UserPerMinute, int UserPerHour);

public interface IAiAllowancePolicy
{
    Task<AiAllowancePolicy> GetAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>The answer to a reservation. A granted one holds worst-case money until the call settles it. A refused one carries the words the caller shows.</summary>
public sealed record AiReservation(bool Granted, string? EntryId, string PeriodId, long ReservedMicroUsd, AiCapReason? Refusal, string? Message);

/// <summary>What a result carries so the audit trail can settle the reservation in the transaction that records the action (REOS-185).</summary>
public sealed record AiMetering(string? EntryId, string PeriodId, long ReservedMicroUsd, long CostMicroUsd, AiCapReason? Refusal);

/// <summary>Would a call be allowed right now? Read-only: reserves nothing (the preflight and the status panel).</summary>
public sealed record AiCapCheck(bool Allowed, AiCapReason? Reason, string? Message);

public sealed record AiAllowanceFeatureUsage(string Feature, string Name, bool CappedFirst, long Calls, long Refused, long CostMicroUsd, long InputTokens, long OutputTokens);
public sealed record AiAllowanceUserUsage(string UserId, string Name, long Calls, long CostMicroUsd);
public sealed record AiCreditPackView(string Id, long MicroUsd, double Credits, string Source, string? Reference, string? Note, string? GrantedByName, DateTime GrantedAt);

/// <summary>What an administrator sees: the month, what was included and added, what is used and held, the caps, and where it went.</summary>
/// <param name="Level">Ok, Soft (chat is paused) or Hard (nothing runs until the month renews or a pack is added).</param>
public sealed record AiAllowanceView(string PeriodStart, string PeriodEnd, string ResetsOn, long IncludedMicroUsd, long PackMicroUsd, long TotalMicroUsd, long UsedMicroUsd, long ReservedMicroUsd, long RemainingMicroUsd,
    int PercentUsed, int SoftCapPercent, int ChatSharePercent, long ChatUsedMicroUsd, long ChatLimitMicroUsd, bool ChatAvailable, int UserPerMinute, int UserPerHour, string Level, string? Message,
    long MicroUsdPerCredit, IReadOnlyList<AiAllowanceFeatureUsage> Features, IReadOnlyList<AiAllowanceUserUsage> Users, IReadOnlyList<AiCreditPackView> Packs, bool Opened);

public sealed record AiLedgerEntry(string Id, string Feature, string FeatureName, string Class, string? Model, bool Batch, string State, string? RefusalReason, long ReservedMicroUsd, long CostMicroUsd,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, AiPersonRef? RequestedBy, DateTime ReservedAt, DateTime? SettledAt, string? AiActionId);

/// <summary>
/// REOS-185 (D57, D58): the AI allowance. <see cref="ReserveAsync"/> holds a call's worst-case cost before the model is called, in one write transaction, so concurrent calls cannot spend more than there is;
/// the real cost settles it in the transaction that records the AI action (<see cref="IAiActionLog"/>). Nothing here changes a status: AI stays advisory.
/// </summary>
public interface IAiAllowance
{
    /// <summary>Opens the month if it is new, applies the caps and the per-person rate limit, and holds <paramref name="estimateMicroUsd"/>. Throws when it cannot be done (fail fast): the caller then makes no call.</summary>
    Task<AiReservation> ReserveAsync(string workspaceId, string feature, string? userId, string? model, long estimateMicroUsd, bool batch, CancellationToken ct = default);
    /// <summary>Gives a reservation back when its call never reached the audit trail (the caller cancelled, or an unexpected error). Idempotent.</summary>
    Task ReleaseAsync(string workspaceId, string entryId, string reason, CancellationToken ct = default);
    Task<AiCapCheck> CheckAsync(string workspaceId, string feature, CancellationToken ct = default);
    Task<AiAllowanceView> GetAsync(string workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<AiLedgerEntry>> LedgerAsync(string workspaceId, int limit = 100, CancellationToken ct = default);
    /// <summary>The extension point for billing: adds credits to the current month. Source is Manual or Billing; a Billing grant with a Reference already granted is returned as it was, not granted twice.
    /// Not exposed over HTTP: a workspace administrator must not be able to give their own workspace credits.</summary>
    Task<ServiceResult<AiCreditPackView>> GrantPackAsync(string workspaceId, long microUsd, string source, string? reference, string? note, Actor? actor, CancellationToken ct = default);
}

/// <summary>The words people read when the allowance stops something. Plain, specific, and always saying what happens next (CLAUDE.md rule 8: no silent failure).</summary>
public static class AiAllowanceMessages
{
    public static string Date(string isoDate) => DateTime.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + " (UTC)";
    public static string Month(string isoDate) => DateTime.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public static string HardCap(string periodStart, string periodEnd) =>
        $"The AI allowance for this workspace is used up for {Month(periodStart)}. AI is paused until it renews on {Date(periodEnd)}; an administrator can add a credit pack to continue sooner.";

    public static string NotEnough(string periodStart, string periodEnd, double creditsLeft) =>
        $"The AI allowance left for {Month(periodStart)} ({creditsLeft.ToString("0.##", CultureInfo.InvariantCulture)} credits) is not enough for this request. A shorter request may fit; the allowance renews on {Date(periodEnd)}, and an administrator can add a credit pack.";

    public static string ChatCap(string periodStart, string periodEnd, bool softCap) =>
        (softCap ? $"AI chat is paused: this workspace has used its soft cap of the {Month(periodStart)} allowance. Drafts and summaries still run until the allowance is used up"
                 : $"AI chat is paused: chat has used its share of the {Month(periodStart)} allowance. Drafts and summaries still run until the allowance is used up")
        + $". Chat returns on {Date(periodEnd)}, or sooner if an administrator adds a credit pack.";

    public static string RateLimit(int perMinute, int perHour, bool minuteWindow) =>
        $"You have asked for AI help too often: the limit is {perMinute} per minute and {perHour} per hour for each person. Try again in {(minuteWindow ? "a minute" : "a little while")}.";

    public static string SoftCapNotice(int softPercent, string periodStart, string periodEnd) =>
        $"AI usage in this workspace has reached {softPercent}% of the {Month(periodStart)} allowance. AI chat is paused; drafts and summaries continue until the allowance is used up. It renews on {Date(periodEnd)}.";

    public static string HardCapNotice(string periodStart, string periodEnd) => HardCap(periodStart, periodEnd);
}
