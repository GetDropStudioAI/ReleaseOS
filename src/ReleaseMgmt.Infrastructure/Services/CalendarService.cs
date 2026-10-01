using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record CalWindow(string StartsAt, string EndsAt);
public sealed record CalTrain(string Id, string Title, string Status, string RiskTier, string TargetDate, CalWindow? Window);
public sealed record CalGate(string Id, string Name, string Class, string Status, string DueOn, string TrainId, string TrainTitle, string TrainStatus);
public sealed record CalFreeze(string Id, string Name, string Kind, string StartsAt, string EndsAt, string Scope);
public sealed record CalMilestone(string Id, string Name, string DueOn, bool Done, string TrainId, string TrainTitle, string TrainStatus);
public sealed record CalendarPayload(string From, string To, IReadOnlyList<CalTrain> Trains, IReadOnlyList<CalGate> Gates, IReadOnlyList<CalFreeze> FreezeWindows, IReadOnlyList<CalMilestone> Milestones);

/// <summary>
/// Read side of the Calendar screen (REOS-51): everything visible in [from, to] in one payload. Dates are 'yyyy-MM-dd' (inclusive both ends);
/// instants are UTC ISO-8601. A train is listed when its target date or deployment window touches the range; a gate when its due date is in the range
/// (it carries its train's id, title and status), and likewise a train milestone (Q-0844); a freeze or chill window when it overlaps the range. Archived trains are never listed. Read-only.
/// </summary>
public sealed class CalendarService(IDbContextFactory<ReleaseDbContext> dbf)
{
    public const int MaxRangeDays = 400;
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Null when both dates parse and the range is sane; otherwise the readable reason (400 InvalidFilter).</summary>
    public static string? Validate(string? from, string? to, out DateOnly f, out DateOnly t)
    {
        f = default; t = default;
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f)) return "from must be a date like 2026-10-01";
        if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return "to must be a date like 2026-10-31";
        if (t < f) return "to must not be before from";
        if (t.DayNumber - f.DayNumber > MaxRangeDays) return $"The range may span at most {MaxRangeDays} days";
        return null;
    }

    public async Task<CalendarPayload> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);   // exclusive

        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ArchivedAt == null).ToListAsync(ct);
        var byId = trains.ToDictionary(t => t.Id);
        var windows = (await db.Set<DeploymentWindows>().AsNoTracking().ToListAsync(ct)).Where(w => byId.ContainsKey(w.ReleaseTrainId)).ToDictionary(w => w.ReleaseTrainId);
        var gates = (await db.Set<StageGates>().AsNoTracking().Where(g => g.DueOn >= from && g.DueOn <= to).ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId));
        var milestones = (await db.Set<TrainMilestones>().AsNoTracking().Where(m => m.DueOn >= from && m.DueOn <= to).ToListAsync(ct)).Where(m => byId.ContainsKey(m.ReleaseTrainId));
        var freezes = (await db.Set<FreezeWindows>().AsNoTracking().ToListAsync(ct)).Where(w => w.StartsAt < end && w.EndsAt > start);

        var calTrains = trains
            .Where(t => (t.TargetReleaseDate >= from && t.TargetReleaseDate <= to) || (windows.TryGetValue(t.Id, out var w) && w.StartsAt < end && w.EndsAt > start))
            .OrderBy(t => t.TargetReleaseDate).ThenBy(t => t.Title).ThenBy(t => t.Id)
            .Select(t => new CalTrain(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                windows.TryGetValue(t.Id, out var w) ? new CalWindow(w.StartsAt.ToString(Iso), w.EndsAt.ToString(Iso)) : null)).ToList();
        var calGates = gates.OrderBy(g => g.DueOn).ThenBy(g => g.SequenceOrder).ThenBy(g => g.Id)
            .Select(g => { var t = byId[g.ReleaseTrainId]; return new CalGate(g.Id, g.GateName, g.GateClass, g.Status, g.DueOn.ToString("yyyy-MM-dd"), t.Id, t.Title, t.CurrentStatus); }).ToList();
        var calFreezes = freezes.OrderBy(w => w.StartsAt).ThenBy(w => w.Id)
            .Select(w => new CalFreeze(w.Id, w.Name, w.Kind, w.StartsAt.ToString(Iso), w.EndsAt.ToString(Iso), w.ProductPattern is null ? "All products" : $"Products matching {w.ProductPattern}")).ToList();
        var calMilestones = milestones.OrderBy(m => m.DueOn).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => { var t = byId[m.ReleaseTrainId]; return new CalMilestone(m.Id, m.Name, m.DueOn.ToString("yyyy-MM-dd"), m.IsDone, t.Id, t.Title, t.CurrentStatus); }).ToList();
        return new CalendarPayload(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), calTrains, calGates, calFreezes, calMilestones);
    }
}
