using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record CalWindow(string StartsAt, string EndsAt);
/// <summary>REOS-85: another open train whose window overlaps this one's and that shares a CI or product (names of what is shared).</summary>
public sealed record CalCollision(string TrainId, string Title, IReadOnlyList<string> Shared);
public sealed record CalTrain(string Id, string Title, string Status, string RiskTier, string TargetDate, CalWindow? Window, IReadOnlyList<CalCollision> Collisions, NamedRef? Type = null, int? BetaNumber = null, ChainBrief? Chain = null,
    string? ClientKind = null, IReadOnlyList<string>? Segments = null);
public sealed record CalGate(string Id, string Name, string Class, string Status, string DueOn, string TrainId, string TrainTitle, string TrainStatus);
public sealed record CalFreeze(string Id, string Name, string Kind, string StartsAt, string EndsAt, string Scope);
public sealed record CalMilestone(string Id, string Name, string DueOn, bool Done, string TrainId, string TrainTitle, string TrainStatus);
/// <summary>RT5 (D47): the day some of a release's clients get it. <c>Segment</c> is "Standard" or "OnDemand"; <c>Kind</c> is "Cert" or "Production"; <c>Wave</c> is "Standard" when the day is the release's Standard date. One entry per release, segment and day.</summary>
public sealed record CalClientDate(string TrainId, string TrainTitle, string TrainStatus, string Kind, string Segment, string Date, string Wave, int Count, IReadOnlyList<string> Clients);
public sealed record CalendarPayload(string From, string To, IReadOnlyList<CalTrain> Trains, IReadOnlyList<CalGate> Gates, IReadOnlyList<CalFreeze> FreezeWindows, IReadOnlyList<CalMilestone> Milestones,
    IReadOnlyList<CalClientDate>? ClientDates = null);

/// <summary>
/// Read side of the Calendar screen (REOS-51): everything visible in [from, to] in one payload. Dates are 'yyyy-MM-dd' (inclusive both ends);
/// instants are UTC ISO-8601. A train is listed when its target date or deployment window touches the range; a gate when its due date is in the range
/// (it carries its train's id, title and status), and likewise a train milestone (Q-0844); a freeze or chill window when it overlaps the range. Archived trains are never listed.
/// RT2 (D48): a release in a version carries its <see cref="ChainBrief"/> (the whole chain, even where other releases fall outside the range or the type filter). RT5: a CERT or Production release also carries the days its clients get it (<see cref="CalClientDate"/>, from <see cref="ClientScheduleService.DaysAsync"/>), and
/// the segments it reaches; a release is listed when one of those days is in the range, even if its own dates are not. Read-only.
/// </summary>
public sealed class CalendarService(IDbContextFactory<ReleaseDbContext> dbf, AdvisoryService? advisories = null, IConfiguration? config = null)
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

    /// <summary>The client-segment filter's values: a segment, or "Combined" (Standard and On-Demand together: the releases that have any client date).</summary>
    public const string Combined = "Combined";

    /// <summary>Null when the filter is absent or one of Standard, OnDemand, Combined; otherwise the readable reason (400 InvalidFilter).</summary>
    public static string? ValidateSegment(string? segment) =>
        string.IsNullOrWhiteSpace(segment) || segment == Combined || ClientSegments.All.Contains(segment) ? null : "segment must be Standard, OnDemand or Combined";

    /// <summary>REOS-116: <paramref name="releaseTypeId"/> limits the releases (and so their gates and milestones) to that type; freeze windows are the workspace's and always show.
    /// RT5: <paramref name="segment"/> (Standard, OnDemand or Combined) limits them to the releases that reach that segment's clients, and the client dates to that segment.</summary>
    public async Task<CalendarPayload> GetAsync(DateOnly from, DateOnly to, string? releaseTypeId = null, string? segment = null, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);   // exclusive

        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ArchivedAt == null && (releaseTypeId == null || t.ReleaseTypeId == releaseTypeId)).ToListAsync(ct);
        var offset = ClientScheduleKinds.OffsetFrom(config?[ClientScheduleKinds.OffsetKey]);
        var clientDays = await ClientScheduleService.DaysAsync(db, trains, offset, ct);
        bool Reaches(ClientScheduleService.ClientDay d) => segment is null or "" or Combined || d.Segment == segment;
        if (!string.IsNullOrWhiteSpace(segment)) trains = [.. trains.Where(t => clientDays.TryGetValue(t.Id, out var ds) && ds.Any(Reaches))];
        var typeIds = trains.Select(t => t.ReleaseTypeId).OfType<string>().Distinct().ToList();
        var types = await db.Set<ReleaseTypes>().AsNoTracking().Where(x => typeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => new NamedRef(x.Id, x.Name), ct);
        var byId = trains.ToDictionary(t => t.Id);
        var windows = (await db.Set<DeploymentWindows>().AsNoTracking().ToListAsync(ct)).Where(w => byId.ContainsKey(w.ReleaseTrainId)).ToDictionary(w => w.ReleaseTrainId);
        var gates = (await db.Set<StageGates>().AsNoTracking().Where(g => g.DueOn >= from && g.DueOn <= to).ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId));
        var milestones = (await db.Set<TrainMilestones>().AsNoTracking().Where(m => m.DueOn >= from && m.DueOn <= to).ToListAsync(ct)).Where(m => byId.ContainsKey(m.ReleaseTrainId));
        var freezes = (await db.Set<FreezeWindows>().AsNoTracking().ToListAsync(ct)).Where(w => w.StartsAt < end && w.EndsAt > start);

        var collisions = advisories is null ? [] : await advisories.CollisionsAsync(db, null, ct);
        var chains = advisories is null ? [] : await advisories.ChainBriefsAsync(db, ct);
        IReadOnlyList<CalCollision> Clashes(string id) => collisions.TryGetValue(id, out var list)
            ? [.. list.Select(c => new CalCollision(c.OtherTrainId, c.OtherTitle, [.. c.SharedCis.Select(x => $"CI {x}"), .. c.SharedProducts]))] : [];
        var calClients = clientDays.Where(kv => byId.ContainsKey(kv.Key)).SelectMany(kv => kv.Value.Where(d => Reaches(d) && d.Date >= from && d.Date <= to).Select(d => (Train: byId[kv.Key], Day: d)))
            .GroupBy(x => (x.Day.TrainId, x.Day.Segment, x.Day.Date))
            .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Segment == ClientSegments.Standard ? 0 : 1).ThenBy(g => g.First().Train.Title, StringComparer.Ordinal).ThenBy(g => g.Key.TrainId, StringComparer.Ordinal)
            .Select(g => new CalClientDate(g.Key.TrainId, g.First().Train.Title, g.First().Train.CurrentStatus, g.First().Day.Kind, g.Key.Segment, g.Key.Date.ToString("yyyy-MM-dd"), g.First().Day.Wave, g.Count(),
                [.. g.Select(x => x.Day.Name).Order(StringComparer.OrdinalIgnoreCase)])).ToList();
        var withClientDay = calClients.Select(c => c.TrainId).ToHashSet();
        var calTrains = trains
            .Where(t => (t.TargetReleaseDate >= from && t.TargetReleaseDate <= to) || (windows.TryGetValue(t.Id, out var w) && w.StartsAt < end && w.EndsAt > start) || withClientDay.Contains(t.Id))
            .OrderBy(t => t.TargetReleaseDate).ThenBy(t => t.Title).ThenBy(t => t.Id)
            .Select(t => new CalTrain(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                windows.TryGetValue(t.Id, out var w) ? new CalWindow(w.StartsAt.ToString(Iso), w.EndsAt.ToString(Iso)) : null, Clashes(t.Id), t.ReleaseTypeId is { } ty && types.TryGetValue(ty, out var type) ? type : null, t.BetaNumber, chains.GetValueOrDefault(t.Id),
                clientDays.TryGetValue(t.Id, out var cd) ? cd[0].Kind : null, clientDays.TryGetValue(t.Id, out var sg) ? [.. ClientSegments.All.Where(x => sg.Any(d => d.Segment == x))] : [])).ToList();
        var calGates = gates.OrderBy(g => g.DueOn).ThenBy(g => g.SequenceOrder).ThenBy(g => g.Id)
            .Select(g => { var t = byId[g.ReleaseTrainId]; return new CalGate(g.Id, g.GateName, g.GateClass, g.Status, g.DueOn.ToString("yyyy-MM-dd"), t.Id, t.Title, t.CurrentStatus); }).ToList();
        var calFreezes = freezes.OrderBy(w => w.StartsAt).ThenBy(w => w.Id)
            .Select(w => new CalFreeze(w.Id, w.Name, w.Kind, w.StartsAt.ToString(Iso), w.EndsAt.ToString(Iso), w.ProductPattern is null ? "All products" : $"Products matching {w.ProductPattern}")).ToList();
        var calMilestones = milestones.OrderBy(m => m.DueOn).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => { var t = byId[m.ReleaseTrainId]; return new CalMilestone(m.Id, m.Name, m.DueOn.ToString("yyyy-MM-dd"), m.IsDone, t.Id, t.Title, t.CurrentStatus); }).ToList();
        return new CalendarPayload(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), calTrains, calGates, calFreezes, calMilestones, calClients);
    }
}
