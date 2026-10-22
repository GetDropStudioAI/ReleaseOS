namespace ReleaseMgmt.Domain.Services;

/// <summary>A planned runbook step as the contingency check sees it.</summary>
public sealed record PlanStep(string Section, DateTime PlannedStart, int PlannedDurationMin);

/// <summary>
/// REOS-88: how much spare time the plan leaves before the rollback deadline. <c>State</c> is <c>ok</c>, <c>low</c> (below the minimum), <c>over</c>
/// (the plan already ends after the deadline) or <c>unknown</c> (no window, or no steps outside Rollback; <c>Reason</c> says which).
/// </summary>
public sealed record ContingencyView(string State, string? Reason, int? PlannedMin, int RollbackMin, DateTime? PlannedStart, DateTime? PlannedFinish,
    DateTime? RollbackDeadline, int? SpareMin, int? Percent, int MinPercent);

/// <summary>
/// The guide's intake rule "execution duration documented with at least 30–50% contingency" measured on the plan, with the same terms as the live
/// forecast (<see cref="RunForecasting"/>): the planned finish is the latest planned end of a step outside the Rollback section, and the rollback
/// deadline is the window end minus the Rollback steps' total planned duration. Planned execution time runs from the earliest of those steps' planned
/// starts to the planned finish. Spare = deadline − planned finish; contingency = spare ÷ planned execution time, rounded down. Advisory only: it
/// never blocks a transition (proposal P1, Q-0880).
/// </summary>
public static class Contingency
{
    public const string Ok = "ok", Low = "low", Over = "over", Unknown = "unknown";

    public static ContingencyView Compute(DateTime? windowStart, DateTime? windowEnd, IReadOnlyCollection<PlanStep> steps, int minPercent)
    {
        var rollbackMin = steps.Where(s => s.Section == "Rollback").Sum(s => s.PlannedDurationMin);
        var main = steps.Where(s => s.Section != "Rollback").ToList();
        if (windowStart is null || windowEnd is null)
            return new(Unknown, "no deployment window", null, rollbackMin, null, null, null, null, null, minPercent);
        if (main.Count == 0)
            return new(Unknown, "no runbook steps outside Rollback", null, rollbackMin, null, null, windowEnd.Value.AddMinutes(-rollbackMin), null, null, minPercent);

        var start = main.Min(s => s.PlannedStart);
        var finish = main.Max(s => s.PlannedStart.AddMinutes(s.PlannedDurationMin));
        var planned = (int)Math.Ceiling((finish - start).TotalMinutes);
        var deadline = windowEnd.Value.AddMinutes(-rollbackMin);
        var spare = (int)Math.Floor((deadline - finish).TotalMinutes);
        var percent = planned <= 0 ? 0 : (int)Math.Floor(100.0 * spare / planned);
        var state = spare < 0 ? Over : percent < minPercent ? Low : Ok;
        return new(state, null, planned, rollbackMin, start, finish, deadline, spare, percent, minPercent);
    }
}

/// <summary>A train with a deployment window, and what it touches: affected CI names and bundled products (name, project code).</summary>
public sealed record WindowedTrain(string TrainId, string Title, string Status, DateTime StartsAt, DateTime EndsAt,
    IReadOnlyCollection<string> Cis, IReadOnlyCollection<(string Name, string? Code)> Products);

/// <summary>REOS-85: another open train whose window overlaps this one's (within the buffer) and that shares a CI or a product.</summary>
public sealed record WindowCollision(string OtherTrainId, string OtherTitle, string OtherStatus, DateTime OtherStartsAt, DateTime OtherEndsAt,
    IReadOnlyList<string> SharedCis, IReadOnlyList<string> SharedProducts);

/// <summary>
/// "Team A deploys a schema change at 02:00; does Team B's 02:30 job depend on it?" Two windows collide when they overlap once each is widened by
/// <c>bufferMin</c> on both sides, and the trains share an affected CI (by name) or a bundled product (by name or project code); names compare
/// trimmed and case-insensitively. A warning only: there is no dependency between trains (cross-train dependencies stay out of v1, Q-0850).
/// </summary>
public static class WindowCollisions
{
    public static IReadOnlyList<WindowCollision> Find(WindowedTrain train, IEnumerable<WindowedTrain> others, int bufferMin)
    {
        var buffer = TimeSpan.FromMinutes(bufferMin);
        var result = new List<WindowCollision>();
        foreach (var o in others)
        {
            if (o.TrainId == train.TrainId) continue;
            if (!(o.StartsAt < train.EndsAt + buffer && train.StartsAt < o.EndsAt + buffer)) continue;
            // What is shared is named as this train spells it: the warning sits on this train's line.
            var theirCis = o.Cis.Select(Key).Where(k => k.Length > 0).ToHashSet();
            var sharedCis = train.Cis.Where(c => theirCis.Contains(Key(c))).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var sharedProducts = train.Products.Where(p => o.Products.Any(q => Same(p, q))).Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (sharedCis.Count == 0 && sharedProducts.Count == 0) continue;
            result.Add(new(o.TrainId, o.Title, o.Status, o.StartsAt, o.EndsAt, sharedCis, sharedProducts));
        }
        return [.. result.OrderBy(c => c.OtherStartsAt).ThenBy(c => c.OtherTitle, StringComparer.Ordinal)];
    }

    private static string Key(string s) => s.Trim().ToUpperInvariant();

    private static bool Same((string Name, string? Code) a, (string Name, string? Code) b) =>
        Key(a.Name) == Key(b.Name) && Key(a.Name).Length > 0
        || !string.IsNullOrWhiteSpace(a.Code) && !string.IsNullOrWhiteSpace(b.Code) && Key(a.Code) == Key(b.Code);
}
