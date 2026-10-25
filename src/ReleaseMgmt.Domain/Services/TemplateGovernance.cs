namespace ReleaseMgmt.Domain.Services;

// REOS-122 (RT7): template governance. Pure rules and the diff between two versions of a template; no EF, no clock.

public sealed record TemplateTaskSnap(string Description, string? OwnerTeamId);
public sealed record TemplateGateSnap(string Name, string Class, int OffsetDays, string RequiredBefore, string? OwnerTeamId, IReadOnlyList<TemplateTaskSnap> Tasks);
public sealed record TemplateStepSnap(string Code, string Section, string Title, int OffsetMinutes, int DurationMin, string? OwnerTeamId);
public sealed record TemplateScheduleSnap(string LibraryTemplateId, string LibraryName, int OffsetDays);
public sealed record TemplateMilestoneSnap(string Name, int OffsetDays, string? OwnerTeamId);

/// <summary>What a template says, without ids: what two versions are compared on.</summary>
public sealed record TemplateSnapshot(string DefaultRiskTier, IReadOnlyList<TemplateGateSnap> Gates, IReadOnlyList<TemplateStepSnap> Steps,
    IReadOnlyList<TemplateScheduleSnap> Schedule, IReadOnlyList<TemplateMilestoneSnap> Milestones);

/// <summary>One difference between a version and the one it replaces. <see cref="Kind"/> is Added, Removed or Changed; <see cref="Compliance"/> marks a change to a Compliance gate (or its tasks).</summary>
public sealed record TemplateChange(string Area, string Kind, string Subject, string Detail, bool Compliance);

public static class TemplateDiff
{
    /// <summary>Differences from <paramref name="previous"/> to <paramref name="current"/>. With no previous version, everything in the template is Added.</summary>
    public static IReadOnlyList<TemplateChange> Between(TemplateSnapshot? previous, TemplateSnapshot current)
    {
        var prev = previous ?? new TemplateSnapshot(current.DefaultRiskTier, [], [], [], []);
        var changes = new List<TemplateChange>();
        if (previous is not null && prev.DefaultRiskTier != current.DefaultRiskTier)
            changes.Add(new("Template", "Changed", "Default risk", $"{prev.DefaultRiskTier} to {current.DefaultRiskTier}", false));

        Compare("Gate", prev.Gates, current.Gates, g => g.Name, (a, b) => Same(a, b, withTasks: false), Describe, (g, c) => c || g.Class == "Compliance", changes);
        // Tasks of a gate that exists on both sides; the tasks of an added or removed gate belong to that gate's own line.
        foreach (var g in current.Gates)
        {
            var old = prev.Gates.FirstOrDefault(x => Eq(x.Name, g.Name));
            if (old is null) continue;
            var compliance = g.Class == "Compliance" || old.Class == "Compliance";
            Compare("Task", old.Tasks, g.Tasks, t => t.Description, (a, b) => a.OwnerTeamId == b.OwnerTeamId, t => $"owner team {t.OwnerTeamId ?? "none"}", (_, _) => compliance, changes, subject: t => $"{g.Name}: {t.Description}");
        }
        Compare("Milestone", prev.Milestones, current.Milestones, m => m.Name, (a, b) => a == b, m => $"{Offset(m.OffsetDays)} from the target, owner team {m.OwnerTeamId ?? "none"}", (_, _) => false, changes);
        Compare("Step", prev.Steps, current.Steps, s => s.Code, (a, b) => a == b, s => $"{s.Section} {s.Title}, {s.OffsetMinutes} min from the window start, {s.DurationMin} min", (_, _) => false, changes);
        Compare("Message", prev.Schedule, current.Schedule, c => $"{c.LibraryTemplateId}|{c.OffsetDays}", (a, b) => a == b, c => $"{c.LibraryName} at {Offset(c.OffsetDays)}", (_, _) => false, changes, subject: c => c.LibraryName);
        return changes;
    }

    /// <summary>
    /// True when the Compliance gates of <paramref name="current"/> differ from those of <paramref name="previous"/>: a Compliance gate added, removed, downgraded, or changed in name, offset,
    /// required-before status, owner team or tasks. A first version (no previous) with any Compliance gate counts as a change. This is what asks for a Governance Officer's approval (Q10).
    /// </summary>
    public static bool ComplianceChanged(TemplateSnapshot? previous, TemplateSnapshot current)
    {
        var now = current.Gates.Where(g => g.Class == "Compliance").ToList();
        var before = previous?.Gates.Where(g => g.Class == "Compliance").ToList() ?? [];
        return now.Any(n => !before.Any(o => Same(o, n, withTasks: true))) || before.Any(o => !now.Any(n => Same(o, n, withTasks: true)));
    }

    private static bool Same(TemplateGateSnap a, TemplateGateSnap b, bool withTasks) =>
        Eq(a.Name, b.Name) && a.Class == b.Class && a.OffsetDays == b.OffsetDays && a.RequiredBefore == b.RequiredBefore && a.OwnerTeamId == b.OwnerTeamId
        && (!withTasks || a.Tasks.Select(t => t.Description.Trim()).Order(StringComparer.Ordinal).SequenceEqual(b.Tasks.Select(t => t.Description.Trim()).Order(StringComparer.Ordinal)));

    private static string Describe(TemplateGateSnap g) => $"{g.Class}, {g.OffsetDays} business days before the target, required before {g.RequiredBefore}";
    private static string Offset(int days) => days == 0 ? "on the target" : days < 0 ? $"{-days} days before" : $"{days} days after";
    private static bool Eq(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Compare<T>(string area, IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> key, Func<T, T, bool> same, Func<T, string> describe,
        Func<T, bool, bool> compliance, List<TemplateChange> into, Func<T, string>? subject = null)
    {
        var name = subject ?? key;
        var oldByKey = before.GroupBy(x => key(x).Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var newByKey = after.GroupBy(x => key(x).Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        foreach (var (k, n) in newByKey)
        {
            if (!oldByKey.TryGetValue(k, out var o)) into.Add(new(area, "Added", name(n), describe(n), compliance(n, false)));
            else if (!same(o, n)) into.Add(new(area, "Changed", name(n), $"{describe(o)} to {describe(n)}", compliance(n, compliance(o, false))));
        }
        foreach (var (k, o) in oldByKey)
            if (!newByKey.ContainsKey(k)) into.Add(new(area, "Removed", name(o), describe(o), compliance(o, false)));
    }
}
