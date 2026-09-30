using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public static class IcsScopes
{
    public const string All = "all", Train = "train", Mine = "mine", Freezes = "freezes";
    public static readonly string[] Valid = [All, Train, Mine, Freezes];
}

/// <summary>
/// ICS feeds (REOS-51, D21, Q-051*). Titles and times only. Determinism is the design:
/// <list type="bullet">
/// <item>UID = <c>{entityId}-{kind}@releasemgmt</c>, never derived from a date, so a moved gate updates the same event in the subscriber's calendar.</item>
/// <item>SEQUENCE = sum of the (Version - 1) of the event's row and its train: the row Versions only ever grow, so SEQUENCE never decreases and rises exactly when the row or its train was edited (RFC 5545 3.8.7.4).</item>
/// <item>DTSTAMP and LAST-MODIFIED come from stored timestamps (train UpdatedAt, gate LastChangedAt), never from the clock: an unchanged database yields a byte-identical file.</item>
/// <item>Events are written in UID order.</item>
/// </list>
/// Timed events (deployment windows, runbook steps) are UTC in the file; gate due dates, target dates and freeze/chill ranges are all-day (VALUE=DATE).
/// </summary>
public sealed class IcsFeedService(IDbContextFactory<ReleaseDbContext> dbf)
{
    public const string ProdId = "-//ReleaseMgmt//Release calendar//EN";
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Ev(string Uid, string Summary, CalDateTime Start, CalDateTime End, int Sequence, DateTime Stamp);

    /// <summary>Null when the scope names a train that does not exist (the endpoint answers 404).</summary>
    public async Task<string?> BuildAsync(string scope, string? trainId, string userId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var trainsQ = db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ArchivedAt == null && t.CurrentStatus != "Aborted");
        string name;
        var events = new List<Ev>();

        if (scope == IcsScopes.Freezes)
        {
            name = "Freeze and chill windows";
            events.AddRange(await FreezeEvents(db, ct));
        }
        else if (scope == IcsScopes.Mine)
        {
            name = "My release work";
            var open = await trainsQ.Where(t => t.CurrentStatus != "Complete").ToListAsync(ct);
            var byId = open.ToDictionary(t => t.Id);
            var teamIds = await db.Set<TeamMembers>().AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct);
            bool Mine(string? uid, string? tid) => uid == userId || (tid is not null && teamIds.Contains(tid));
            foreach (var g in (await db.Set<StageGates>().AsNoTracking().Where(g => g.Status != "Certified" && g.Status != "Waived").ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId) && Mine(g.OwnerUserId, g.OwnerTeamId)))
                events.Add(GateEvent(g, byId[g.ReleaseTrainId]));
            foreach (var s in (await db.Set<RunbookSteps>().AsNoTracking().ToListAsync(ct)).Where(s => byId.ContainsKey(s.ReleaseTrainId) && Mine(s.OwnerUserId, s.OwnerTeamId)))
            {
                var t = byId[s.ReleaseTrainId];
                events.Add(new Ev($"{s.Id}-step@releasemgmt", $"{t.Title}: {s.StepCode} {s.Title}", Utc(s.PlannedStartAt), Utc(s.PlannedStartAt.AddMinutes(Math.Max(1, s.PlannedDurationMin))),
                    (s.Version - 1) + (t.Version - 1), t.UpdatedAt));
            }
        }
        else
        {
            var trains = await trainsQ.ToListAsync(ct);
            if (scope == IcsScopes.Train)
            {
                trains = [.. trains.Where(t => t.Id == trainId)];
                if (trains.Count == 0) return null;
                name = trains[0].Title;
            }
            else name = "Release trains";
            var byId = trains.ToDictionary(t => t.Id);
            var windows = (await db.Set<DeploymentWindows>().AsNoTracking().ToListAsync(ct)).Where(w => byId.ContainsKey(w.ReleaseTrainId));
            var gates = (await db.Set<StageGates>().AsNoTracking().ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId));
            foreach (var t in trains)
                events.Add(new Ev($"{t.Id}-target@releasemgmt", $"{t.Title}: target release date", Day(t.TargetReleaseDate), Day(t.TargetReleaseDate.AddDays(1)), t.Version - 1, t.UpdatedAt));
            foreach (var w in windows)
            {
                var t = byId[w.ReleaseTrainId];
                events.Add(new Ev($"{w.Id}-window@releasemgmt", $"{t.Title}: deployment window", Utc(w.StartsAt), Utc(w.EndsAt), (w.Version - 1) + (t.Version - 1), t.UpdatedAt));
            }
            foreach (var g in gates) events.Add(GateEvent(g, byId[g.ReleaseTrainId]));
            // A train feed and the all-trains feed carry the freezes too, so the calendar shows why a date is blocked.
            events.AddRange(await FreezeEvents(db, ct));
        }

        var cal = new Calendar { ProductId = ProdId, Version = "2.0" };
        cal.AddProperty("X-WR-CALNAME", name);
        cal.AddProperty("X-PUBLISHED-TTL", "PT1H");
        foreach (var e in events.OrderBy(x => x.Uid, StringComparer.Ordinal))
            cal.Events.Add(new CalendarEvent
            {
                Uid = e.Uid, Summary = e.Summary, DtStart = e.Start, DtEnd = e.End, Sequence = e.Sequence,
                DtStamp = Utc(e.Stamp), LastModified = Utc(e.Stamp), Transparency = "TRANSPARENT",
            });
        return new CalendarSerializer().SerializeToString(cal);
    }

    private static Ev GateEvent(StageGates g, ReleaseTrains t) =>
        new($"{g.Id}-gate@releasemgmt", $"{t.Title}: {g.GateName} due", Day(g.DueOn), Day(g.DueOn.AddDays(1)), (g.Version - 1) + (t.Version - 1),
            g.LastChangedAt is DateTime c && c > t.UpdatedAt ? c : t.UpdatedAt);

    private static async Task<IEnumerable<Ev>> FreezeEvents(ReleaseDbContext db, CancellationToken ct) =>
        (await db.Set<FreezeWindows>().AsNoTracking().ToListAsync(ct)).Select(w =>
        {
            var first = DateOnly.FromDateTime(w.StartsAt);
            var endDay = DateOnly.FromDateTime(w.EndsAt);
            var lastExclusive = w.EndsAt.TimeOfDay == TimeSpan.Zero ? endDay : endDay.AddDays(1);   // DTEND of an all-day event is exclusive
            var kind = w.Kind == "Chill" ? "chill" : "freeze";
            return new Ev($"{w.Id}-{kind}@releasemgmt", $"{(kind == "chill" ? "Chill" : "Freeze")}: {w.Name}", Day(first), Day(lastExclusive), w.Version - 1, w.StartsAt);
        });

    private static CalDateTime Day(DateOnly d) => new(d.Year, d.Month, d.Day);
    private static CalDateTime Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc), "UTC");
}
