namespace ReleaseMgmt.Domain.Services;

/// <summary>One release of a version as the chain sees it: its type (and the type's place in the workspace's order), its Beta number, status and target date.</summary>
public sealed record ChainRelease(string TrainId, string Title, string? Key, string TypeId, string TypeName, int TypeSortOrder, int? BetaNumber, string Status, DateOnly Target);

/// <summary>
/// RT2 (D48): a later release of a version whose target date comes too soon after the release before it. <see cref="EarliestOn"/> is the predecessor's target plus the
/// minimum gap (<c>Versions:MinGapDays</c>); <see cref="ShortByDays"/> is how many days the date falls short of it. Advisory only: it never blocks a move.
/// </summary>
public sealed record ChainWarning(string TrainId, string Label, DateOnly Target, string AfterTrainId, string AfterLabel, DateOnly AfterTarget, DateOnly EarliestOn, int ShortByDays, string Message);

/// <summary>
/// The chain of one product version (D48): Beta 1, 2 and 3 when the product has betas, then CERT, PenTest Cert and Production. The order is the workspace's release type order
/// (<c>ReleaseTypes.SortOrder</c>, which the seed sets to Beta, CERT, PenTest Cert, Production), then the Beta number, then the target date. An aborted release stays in the chain
/// (it happened) but is skipped when dates are compared: it is neither a predecessor nor flagged. A Complete or Aborted release is never flagged, since its date can no longer move.
/// </summary>
public static class VersionChain
{
    public const int DefaultMinGapDays = 1;
    public static readonly string[] OpenStatuses = ["Planning", "Gated", "Executing"];

    public static string Label(string typeName, int? betaNumber) => betaNumber is null ? typeName : $"{typeName} {betaNumber}";
    public static string Label(ChainRelease r) => Label(r.TypeName, r.BetaNumber);

    public static IReadOnlyList<ChainRelease> Order(IEnumerable<ChainRelease> releases) =>
        [.. releases.OrderBy(r => r.TypeSortOrder).ThenBy(r => r.BetaNumber ?? 0).ThenBy(r => r.Target).ThenBy(r => r.Title, StringComparer.Ordinal)];

    /// <summary>For each open release, compared with the nearest earlier release in the chain that is not aborted: flagged when its date is before that release's date plus the gap.</summary>
    public static IReadOnlyList<ChainWarning> Warnings(IEnumerable<ChainRelease> releases, int minGapDays)
    {
        var ordered = Order(releases).Where(r => r.Status != "Aborted").ToList();
        var result = new List<ChainWarning>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var r = ordered[i];
            if (!OpenStatuses.Contains(r.Status)) continue;
            var before = ordered[i - 1];
            var earliest = before.Target.AddDays(minGapDays);
            if (r.Target >= earliest) continue;
            var short_ = earliest.DayNumber - r.Target.DayNumber;
            var gap = minGapDays == 0 ? "" : $" and the release after it should come at least {(minGapDays == 1 ? "1 day" : $"{minGapDays} days")} later";
            result.Add(new(r.TrainId, Label(r), r.Target, before.TrainId, Label(before), before.Target, earliest, short_,
                $"{Label(r)} is planned for {r.Target:yyyy-MM-dd}, but {Label(before)} is due {before.Target:yyyy-MM-dd}{gap}: {Label(r)} comes {(short_ == 1 ? "1 day" : $"{short_} days")} too soon"));
        }
        return result;
    }

    /// <summary>A release type as the "next release" choice sees it.</summary>
    public sealed record TypeInfo(string Id, string Name, int SortOrder, bool IsBeta, bool Archived);

    /// <summary>A place in the chain a new release could take: a type and, for a Beta type, the number.</summary>
    public sealed record NextOption(string TypeId, string TypeName, int? BetaNumber, string Label);

    /// <summary>
    /// Where the chain can go on from <paramref name="from"/>: the next Beta number of the same Beta type (up to 3), then every later active type in the workspace's order that
    /// has no live release in the version yet (a later Beta type takes its lowest free number). The first option is what "Create the next release" offers; a person may pick another
    /// (a product with no betas goes from CERT straight to Production, or skips PenTest Cert). Empty when the chain has nowhere left to go.
    /// </summary>
    public static IReadOnlyList<NextOption> NextOptions(ChainRelease from, IEnumerable<ChainRelease> chain, IEnumerable<TypeInfo> types)
    {
        var live = chain.Where(r => r.Status != "Aborted").ToList();
        bool Taken(string typeId, int? n) => live.Any(r => r.TypeId == typeId && (r.BetaNumber ?? 0) == (n ?? 0));
        var result = new List<NextOption>();
        var ordered = types.Where(t => !t.Archived).OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        var current = ordered.FirstOrDefault(t => t.Id == from.TypeId);
        if (current is { IsBeta: true } && from.BetaNumber is { } n && n < 3 && !Taken(current.Id, n + 1))
            result.Add(new(current.Id, current.Name, n + 1, Label(current.Name, n + 1)));
        foreach (var t in ordered.Where(t => t.SortOrder > from.TypeSortOrder || t.SortOrder == from.TypeSortOrder && t.Id != from.TypeId && string.CompareOrdinal(t.Name, from.TypeName) > 0))
        {
            if (t.IsBeta)
            {
                var free = Enumerable.Range(1, 3).Where(k => !Taken(t.Id, k)).Cast<int?>().FirstOrDefault();
                if (free is { } k) result.Add(new(t.Id, t.Name, k, Label(t.Name, k)));
            }
            else if (!Taken(t.Id, null)) result.Add(new(t.Id, t.Name, null, t.Name));
        }
        return result;
    }
}
