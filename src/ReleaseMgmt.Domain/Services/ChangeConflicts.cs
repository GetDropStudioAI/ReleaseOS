namespace ReleaseMgmt.Domain.Services;

/// <summary>REOS-180 (D56, D9): the four kinds of collision a change can have. Every one is a warning; none blocks (approving over one needs a reason, see <see cref="ChangeConflictRules.MinReasonLength"/>).</summary>
public static class ChangeConflictTypes
{
    public const string Freeze = "Freeze", Blackout = "Blackout", ChangeOverlap = "ChangeOverlap", ReleaseWindow = "ReleaseWindow";
}

/// <summary>Service guard name of the conflict rule (422 <c>{guard, message}</c>).</summary>
public static class ChangeConflictGuards
{
    public const string NeedsReason = "ChangeConflictNeedsReason";
}

/// <summary>A freeze or chill window that covers the change's products, as the conflict rules see it.</summary>
public sealed record ConflictFreeze(string Id, string Name, string Kind, DateTime StartsAt, DateTime EndsAt);

/// <summary>A blackout day: a row of the workspace's Holidays table (the same calendar the gate offsets use, D9).</summary>
public sealed record ConflictHoliday(DateOnly Day, string Name);

/// <summary>Another change that is waiting for, or has, approval, with the window it plans and what it shares with this one (the same release, services, environments).</summary>
public sealed record ConflictChange(string Id, string Key, string Title, string Status, DateTime StartsAt, DateTime EndsAt, bool SameRelease, IReadOnlyList<string> SharedServices, IReadOnlyList<string> SharedEnvironments);

/// <summary>What the change collides with, each as its own list; the pure rules turn these into <see cref="ChangeConflict"/> rows in words.</summary>
public sealed record ConflictInput(string ChangeId, DateTime? PlannedStartAt, DateTime? PlannedEndAt, IReadOnlyList<ConflictFreeze> Freezes, IReadOnlyList<ConflictHoliday> Holidays,
    IReadOnlyList<ConflictChange> Changes, IReadOnlyList<WindowCollision> ReleaseWindows);

/// <summary>
/// One collision. <c>Rule</c> is the stable code of the rule that fired, <c>RuleLabel</c> its name, <c>Summary</c> says in words what was found (with UTC times). <c>RefId</c>/<c>RefLabel</c> name the
/// other thing (the window, the change, the release) so the screen can link to it; null for a blackout day.
/// </summary>
public sealed record ChangeConflict(string Type, string Rule, string RuleLabel, string Summary, string? RefId, string? RefLabel, DateTime? StartsAt, DateTime? EndsAt);

/// <summary>One rule as the screens list it: what it looks at and when it fires.</summary>
public sealed record ConflictRuleInfo(string Type, string Rule, string Label, string Description);

public sealed record ConflictRulesView(IReadOnlyList<ConflictRuleInfo> Rules, int BufferMinutes, int MinReasonLength, string ApprovalRule);

/// <summary>
/// REOS-180: the conflicts of a change are always derived from the freeze windows, holidays, other changes and release windows as they stand now; nothing is stored as truth, so a window that
/// moves changes the answer at once. A conflict warns and never blocks (consistent with D9); approving a change over one needs a reason.
/// </summary>
public static class ChangeConflictRules
{
    public const string FreezeOverlap = "FreezeOverlap", ChillOverlap = "ChillOverlap", BlackoutDay = "BlackoutDay", SharedWindow = "SharedWindow", ReleaseWindowOverlap = "ReleaseWindowOverlap";

    /// <summary>The shortest reason that lets a change be approved over a conflict: the same length a freeze override needs (FreezeOverrides.Reason), fixed in code (complexity budget, D56).</summary>
    public const int MinReasonLength = 20;

    public const string ApprovalRuleText = "A conflict warns and never blocks. Approving a change that has one needs a reason of at least 20 characters, recorded with the decision.";

    public static ConflictRulesView Catalogue(int bufferMinutes) => new(
    [
        new(ChangeConflictTypes.Freeze, FreezeOverlap, "Inside a freeze window", "The planned window overlaps a freeze window that covers the linked release's products (a change with no release is matched only by windows that cover all products)."),
        new(ChangeConflictTypes.Freeze, ChillOverlap, "Inside a chill window", "The planned window overlaps a chill window, under the same product rule. A chill only warns."),
        new(ChangeConflictTypes.Blackout, BlackoutDay, "On a blackout day", "A UTC day of the planned window is a holiday in the workspace's Holidays calendar."),
        new(ChangeConflictTypes.ChangeOverlap, SharedWindow, "Another change on the same release, service or environment", $"Another Submitted or Approved change plans a window that overlaps this one (within {bufferMinutes} minutes) and is on the same release, or on a release that ships a shared catalog service or targets a shared environment."),
        new(ChangeConflictTypes.ReleaseWindow, ReleaseWindowOverlap, "Another release's deployment window", $"An open release other than the linked one has a deployment window that overlaps this one (within {bufferMinutes} minutes) and shares a configuration item or product with the linked release (the cross-train collision rule)."),
    ], bufferMinutes, MinReasonLength, ApprovalRuleText);

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-dd HH:mm") + " UTC";
    private static string Range(DateTime a, DateTime b) => $"{Utc(a)} to {Utc(b)}";
    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    /// <summary>True when the two windows overlap once each is widened by <paramref name="buffer"/> on both sides.</summary>
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd, TimeSpan buffer) => bStart < aEnd + buffer && aStart < bEnd + buffer;

    /// <summary>The conflicts in a stable order: freeze windows (freeze before chill), blackout days, other changes, release windows; each group by start. A change with no planned window has none.</summary>
    public static IReadOnlyList<ChangeConflict> Evaluate(ConflictInput i)
    {
        if (i.PlannedStartAt is not { } s || i.PlannedEndAt is not { } e) return [];
        var result = new List<ChangeConflict>();

        foreach (var f in i.Freezes.Where(f => f.StartsAt < e && f.EndsAt > s).OrderBy(f => f.Kind == "Freeze" ? 0 : 1).ThenBy(f => f.StartsAt).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var freeze = f.Kind == "Freeze";
            result.Add(new(ChangeConflictTypes.Freeze, freeze ? FreezeOverlap : ChillOverlap, freeze ? "Inside a freeze window" : "Inside a chill window",
                $"The planned window ({Range(s, e)}) overlaps the {(freeze ? "freeze" : "chill")} window \"{f.Name}\" ({Range(f.StartsAt, f.EndsAt)}).", f.Id, f.Name, f.StartsAt, f.EndsAt));
        }

        foreach (var h in i.Holidays.Where(h => DayStart(h.Day) < e && DayStart(h.Day).AddDays(1) > s).OrderBy(h => h.Day))
            result.Add(new(ChangeConflictTypes.Blackout, BlackoutDay, "On a blackout day",
                $"The planned window ({Range(s, e)}) includes {h.Day:yyyy-MM-dd}, a blackout day: {h.Name}.", null, h.Name, DayStart(h.Day), DayStart(h.Day).AddDays(1)));

        foreach (var c in i.Changes.Where(c => c.Id != i.ChangeId).OrderBy(c => c.StartsAt).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var shared = new List<string>();
            if (c.SameRelease) shared.Add("the same release");
            if (c.SharedServices.Count > 0) shared.Add($"{(c.SharedServices.Count == 1 ? "service" : "services")} {Join(c.SharedServices)}");
            if (c.SharedEnvironments.Count > 0) shared.Add($"{(c.SharedEnvironments.Count == 1 ? "environment" : "environments")} {Join(c.SharedEnvironments)}");
            if (shared.Count == 0) continue;
            result.Add(new(ChangeConflictTypes.ChangeOverlap, SharedWindow, "Another change on the same release, service or environment",
                $"{c.Key} \"{c.Title}\" ({c.Status}) plans {Range(c.StartsAt, c.EndsAt)} on {Join(shared)}.", c.Id, c.Key, c.StartsAt, c.EndsAt));
        }

        foreach (var w in i.ReleaseWindows.OrderBy(w => w.OtherStartsAt).ThenBy(w => w.OtherTitle, StringComparer.Ordinal))
        {
            var shared = w.SharedCis.Select(x => $"CI {x}").Concat(w.SharedProducts).ToList();
            result.Add(new(ChangeConflictTypes.ReleaseWindow, ReleaseWindowOverlap, "Another release's deployment window",
                $"The release \"{w.OtherTitle}\" ({w.OtherStatus}) deploys {Range(w.OtherStartsAt, w.OtherEndsAt)} and shares {Join(shared)}.", w.OtherTrainId, w.OtherTitle, w.OtherStartsAt, w.OtherEndsAt));
        }
        return result;
    }

    private static DateTime DayStart(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>The words of the 422 when a change with conflicts is approved without a reason.</summary>
    public static string NeedsReasonText(int count, IEnumerable<string> ruleLabels) =>
        $"This change has {count} conflict{(count == 1 ? "" : "s")} ({Join(ruleLabels.Distinct())}). A conflict does not block approval, but approving over one needs a reason of at least {MinReasonLength} characters in the rationale.";

    /// <summary>True when the reason is long enough to approve over a conflict.</summary>
    public static bool ReasonIsEnough(string? reason) => reason is not null && reason.Trim().Length >= MinReasonLength;
}
