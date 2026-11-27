namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// REOS-176 (D56): the thresholds of the change-risk rules. Settings with defaults that work untouched (<c>Cab:Risk:NearFreezeHours</c>, <c>Cab:Risk:LargeScopeProducts</c>,
/// <c>Cab:Risk:PriorFailureLookbackDays</c>). The points of each rule and the score bands are fixed in code and shown on screen, not configurable (complexity budget).
/// </summary>
public sealed record ChangeRiskOptions
{
    public const string Section = "Cab:Risk";
    /// <summary>A freeze or chill window that starts or ends within this many hours of the planned window counts as "near".</summary>
    public int NearFreezeHours { get; init; } = 48;
    /// <summary>A change whose release bundles at least this many products is "large scope".</summary>
    public int LargeScopeProducts { get; init; } = 5;
    /// <summary>A release that failed within this many days, on a service this change's release also ships, counts as a prior failed change.</summary>
    public int PriorFailureLookbackDays { get; init; } = 90;
}

/// <summary>A freeze or chill window that matters to the change (it overlaps the planned window, or is near it).</summary>
public sealed record RiskFreeze(string Name, string Kind, DateTime StartsAt, DateTime EndsAt);

/// <summary>An earlier change on a service this change's release also ships, whose release failed (closed Unsuccessful, or aborted).</summary>
public sealed record RiskPriorFailure(string ChangeKey, string ServiceName, string ReleaseTitle, string Outcome, DateTime At);

public sealed record RiskInput(string ChangeType, DateTime? PlannedStartAt, DateTime? PlannedEndAt, string? RollbackNote, bool HasRelease, int ProductCount,
    IReadOnlyList<RiskFreeze> Freezes, IReadOnlyList<RiskPriorFailure> PriorFailures);

/// <summary><c>Rule</c> is the stable code; <c>Why</c> says in words what was found, whether or not the rule applied.</summary>
public sealed record RiskFactor(string Rule, string Label, bool Applied, int Points, string Why);

public sealed record RiskAssessment(string Level, int Score, IReadOnlyList<RiskFactor> Factors);

/// <summary>One rule as the screens list it before any change exists: what it looks at and what it adds. <c>Points</c> is words ("3 inside a freeze, 2 inside a chill, 1 near one").</summary>
public sealed record RiskRuleInfo(string Rule, string Label, string Points, string Description);

public sealed record RiskRulesView(IReadOnlyList<RiskRuleInfo> Rules, string LevelRule, ChangeRiskOptions Thresholds);

public static class ChangeRiskRules
{
    public const string Emergency = "Emergency", FreezeWindow = "FreezeWindow", NoRollbackNote = "NoRollbackNote", PriorFailure = "PriorFailure", LargeScope = "LargeScope";

    public const int EmergencyPoints = 3, InsideFreezePoints = 3, InsideChillPoints = 2, NearWindowPoints = 1, NoRollbackPoints = 2, PriorFailurePoints = 2, LargeScopePoints = 2;

    /// <summary>The score bands, in words: shown beside the level on the detail screen.</summary>
    public const string LevelRuleText = "Score 0 is Low, 1 to 2 Medium, 3 to 4 High, 5 or more Critical.";

    /// <summary>The rules in the order they are shown, with the thresholds in force: the one place the screens read the wording from.</summary>
    public static RiskRulesView Catalogue(ChangeRiskOptions o) => new(
    [
        new(Emergency, "Emergency change", $"{EmergencyPoints}", "The change is of type Emergency: it may be implemented before the CAB approves it."),
        new(FreezeWindow, "Change inside or near a freeze", $"{InsideFreezePoints} inside a freeze window, {InsideChillPoints} inside a chill window, {NearWindowPoints} within {o.NearFreezeHours} hours of one",
            "The planned window overlaps, or lies within the margin of, a freeze or chill window that covers the linked release's products (a change with no release is matched only by windows that cover all products). The worst one counts once."),
        new(NoRollbackNote, "No rollback note", $"{NoRollbackPoints}", "No rollback note says how the change is undone if it goes wrong."),
        new(PriorFailure, "Prior failed change on the same service", $"{PriorFailurePoints}", $"Another change, linked to a release that closed Unsuccessful or was aborted in the last {o.PriorFailureLookbackDays} days, shares a catalog service with this change's release."),
        new(LargeScope, "Large scope", $"{LargeScopePoints}", $"The linked release bundles {o.LargeScopeProducts} or more products."),
    ], LevelRuleText, o);

    public static string LevelFor(int score) => score switch { <= 0 => ChangeRiskLevels.Low, <= 2 => ChangeRiskLevels.Medium, <= 4 => ChangeRiskLevels.High, _ => ChangeRiskLevels.Critical };

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-dd HH:mm") + " UTC";

    /// <summary>All five rules, always in this order, each with what it found. The level comes only from the factors that applied (their points add up), so the list always explains the level.</summary>
    public static RiskAssessment Assess(RiskInput i, ChangeRiskOptions o)
    {
        var factors = new List<RiskFactor>
        {
            i.ChangeType == ChangeTypes.Emergency
                ? new(Emergency, "Emergency change", true, EmergencyPoints, "An Emergency change may be implemented before the CAB approves it, so it starts at High.")
                : new(Emergency, "Emergency change", false, 0, $"This is a {i.ChangeType} change."),
            FreezeFactor(i, o),
            string.IsNullOrWhiteSpace(i.RollbackNote)
                ? new(NoRollbackNote, "No rollback note", true, NoRollbackPoints, "No rollback note says how the change is undone if it goes wrong.")
                : new(NoRollbackNote, "No rollback note", false, 0, "A rollback note is recorded."),
            PriorFailureFactor(i, o),
            ScopeFactor(i, o),
        };
        var score = factors.Where(f => f.Applied).Sum(f => f.Points);
        return new(LevelFor(score), score, factors);
    }

    private static RiskFactor FreezeFactor(RiskInput i, ChangeRiskOptions o)
    {
        const string label = "Change inside or near a freeze";
        if (i.PlannedStartAt is not { } s || i.PlannedEndAt is not { } e)
            return new(FreezeWindow, label, false, 0, "There is no planned window yet, so freeze windows cannot be checked.");
        var near = TimeSpan.FromHours(o.NearFreezeHours);
        var inside = i.Freezes.Where(f => f.StartsAt < e && f.EndsAt > s).OrderBy(f => f.StartsAt).ToList();
        var freeze = inside.FirstOrDefault(f => f.Kind == "Freeze");
        if (freeze is not null)
            return new(FreezeWindow, label, true, InsideFreezePoints, $"The planned window overlaps the freeze window \"{freeze.Name}\" ({Utc(freeze.StartsAt)} to {Utc(freeze.EndsAt)}).");
        var chill = inside.FirstOrDefault();
        if (chill is not null)
            return new(FreezeWindow, label, true, InsideChillPoints, $"The planned window overlaps the chill window \"{chill.Name}\" ({Utc(chill.StartsAt)} to {Utc(chill.EndsAt)}).");
        var nearby = i.Freezes.Where(f => f.StartsAt < e + near && f.EndsAt > s - near).OrderBy(f => f.StartsAt).FirstOrDefault();
        if (nearby is not null)
            return new(FreezeWindow, label, true, NearWindowPoints, $"The planned window is within {o.NearFreezeHours} hours of the {(nearby.Kind == "Freeze" ? "freeze" : "chill")} window \"{nearby.Name}\" ({Utc(nearby.StartsAt)} to {Utc(nearby.EndsAt)}).");
        return new(FreezeWindow, label, false, 0, $"No freeze or chill window overlaps the planned window or lies within {o.NearFreezeHours} hours of it.");
    }

    private static RiskFactor PriorFailureFactor(RiskInput i, ChangeRiskOptions o)
    {
        const string label = "Prior failed change on the same service";
        if (!i.HasRelease)
            return new(PriorFailure, label, false, 0, "No release is linked, so the services this change touches are not known.");
        if (i.PriorFailures.Count == 0)
            return new(PriorFailure, label, false, 0, $"No earlier change on a service of this release failed in the last {o.PriorFailureLookbackDays} days.");
        var p = i.PriorFailures.OrderByDescending(x => x.At).First();
        var more = i.PriorFailures.Count > 1 ? $" and {i.PriorFailures.Count - 1} more" : "";
        return new(PriorFailure, label, true, PriorFailurePoints, $"{p.ChangeKey} on {p.ServiceName} failed ({p.ReleaseTitle}: {p.Outcome}, {Utc(p.At)}){more}.");
    }

    private static RiskFactor ScopeFactor(RiskInput i, ChangeRiskOptions o)
    {
        const string label = "Large scope";
        if (!i.HasRelease)
            return new(LargeScope, label, false, 0, "No release is linked, so the scope is not known.");
        return i.ProductCount >= o.LargeScopeProducts
            ? new(LargeScope, label, true, LargeScopePoints, $"The linked release bundles {i.ProductCount} products; {o.LargeScopeProducts} or more is large.")
            : new(LargeScope, label, false, 0, $"The linked release bundles {i.ProductCount} product{(i.ProductCount == 1 ? "" : "s")}; large starts at {o.LargeScopeProducts}.");
    }
}
