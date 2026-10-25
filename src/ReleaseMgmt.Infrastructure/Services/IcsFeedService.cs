using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
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
/// The workspace's name leads every feed name and event title ("Payments · RLS-284 Card portal: Code Freeze due"), so feeds from two workspaces differ in a subscriber's calendar (REOS-138, Q-138a);
/// the UIDs are untouched, since they are the subscriber's event identity. PRODID names the product.
/// RT5 (REOS-120): every release event carries the release's type and version as CATEGORIES and in its DESCRIPTION, and a CERT or Production release adds its client dates: one all-day event for all Standard
/// clients (<c>{train}-standard-clients@releasemgmt</c>) and one per On-Demand client (<c>{train}-{client}-clientdate@releasemgmt</c>, so a moved date updates the same event; SEQUENCE counts the client row's Version).
/// Timed events (deployment windows, runbook steps) are UTC in the file; gate due dates, milestones, target dates and freeze/chill ranges are all-day (VALUE=DATE).
/// </summary>
public sealed class IcsFeedService(IDbContextFactory<ReleaseDbContext> dbf, IConfiguration? config = null)
{
    public const string ProdId = "-//ReleaseOS//Release calendar//EN";
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Ev(string Uid, string Summary, CalDateTime Start, CalDateTime End, int Sequence, DateTime Stamp, string? TrainId = null, string? Note = null);

    /// <summary>Null when the scope names a train that does not exist (the endpoint answers 404).</summary>
    public async Task<string?> BuildAsync(string scope, string? trainId, string userId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var workspaceId = WorkspaceScope.Current ?? WorkspaceIds.Default;
        var workspace = await db.Set<Workspaces>().AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.Name).SingleOrDefaultAsync(ct) ?? workspaceId;
        var trainsQ = db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ArchivedAt == null && t.CurrentStatus != "Aborted");
        string name;
        var events = new List<Ev>();
        List<ReleaseTrains> described = [];   // the releases whose events carry a type and a version

        if (scope == IcsScopes.Freezes)
        {
            name = "Freeze and chill windows";
            events.AddRange(await FreezeEvents(db, ct));
        }
        else if (scope == IcsScopes.Mine)
        {
            name = "My release work";
            var open = await trainsQ.Where(t => t.CurrentStatus != "Complete").ToListAsync(ct);
            described = open;
            var byId = open.ToDictionary(t => t.Id);
            var teamIds = await db.Set<TeamMembers>().AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct);
            bool Mine(string? uid, string? tid) => uid == userId || (tid is not null && teamIds.Contains(tid));
            foreach (var g in (await db.Set<StageGates>().AsNoTracking().Where(g => g.Status != "Certified" && g.Status != "Waived").ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId) && Mine(g.OwnerUserId, g.OwnerTeamId)))
                events.Add(GateEvent(g, byId[g.ReleaseTrainId]));
            foreach (var m in (await db.Set<TrainMilestones>().AsNoTracking().Where(m => !m.IsDone).ToListAsync(ct)).Where(m => byId.ContainsKey(m.ReleaseTrainId) && Mine(m.OwnerUserId, m.OwnerTeamId)))
                events.Add(MilestoneEvent(m, byId[m.ReleaseTrainId]));
            foreach (var s in (await db.Set<RunbookSteps>().AsNoTracking().ToListAsync(ct)).Where(s => byId.ContainsKey(s.ReleaseTrainId) && Mine(s.OwnerUserId, s.OwnerTeamId)))
            {
                var t = byId[s.ReleaseTrainId];
                events.Add(new Ev($"{s.Id}-step@releasemgmt", $"{Name(t)}: {s.StepCode} {s.Title}", Utc(s.PlannedStartAt), Utc(s.PlannedStartAt.AddMinutes(Math.Max(1, s.PlannedDurationMin))),
                    (s.Version - 1) + (t.Version - 1), t.UpdatedAt, t.Id));
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
            described = trains;
            var byId = trains.ToDictionary(t => t.Id);
            var windows = (await db.Set<DeploymentWindows>().AsNoTracking().ToListAsync(ct)).Where(w => byId.ContainsKey(w.ReleaseTrainId));
            var gates = (await db.Set<StageGates>().AsNoTracking().ToListAsync(ct)).Where(g => byId.ContainsKey(g.ReleaseTrainId));
            foreach (var t in trains)
                events.Add(new Ev($"{t.Id}-target@releasemgmt", $"{Name(t)}: target release date", Day(t.TargetReleaseDate), Day(t.TargetReleaseDate.AddDays(1)), t.Version - 1, t.UpdatedAt, t.Id));
            foreach (var w in windows)
            {
                var t = byId[w.ReleaseTrainId];
                events.Add(new Ev($"{w.Id}-window@releasemgmt", $"{Name(t)}: deployment window", Utc(w.StartsAt), Utc(w.EndsAt), (w.Version - 1) + (t.Version - 1), t.UpdatedAt, t.Id));
            }
            foreach (var g in gates) events.Add(GateEvent(g, byId[g.ReleaseTrainId]));
            foreach (var m in (await db.Set<TrainMilestones>().AsNoTracking().ToListAsync(ct)).Where(m => byId.ContainsKey(m.ReleaseTrainId))) events.Add(MilestoneEvent(m, byId[m.ReleaseTrainId]));
            foreach (var (tid, days) in await ClientScheduleService.DaysAsync(db, trains, ClientScheduleKinds.OffsetFrom(config?[ClientScheduleKinds.OffsetKey]), ct))
                events.AddRange(ClientEvents(byId[tid], days));
            // A train feed and the all-trains feed carry the freezes too, so the calendar shows why a date is blocked.
            events.AddRange(await FreezeEvents(db, ct));
        }

        var notes = await DescribeAsync(db, described, ct);
        var cal = new Calendar { ProductId = ProdId, Version = "2.0" };
        cal.AddProperty("X-WR-CALNAME", $"{workspace} · {name}");
        cal.AddProperty("X-PUBLISHED-TTL", "PT1H");
        foreach (var e in events.OrderBy(x => x.Uid, StringComparer.Ordinal))
        {
            var note = e.TrainId is not null && notes.TryGetValue(e.TrainId, out var n) ? n : null;
            var ev = new CalendarEvent
            {
                Uid = e.Uid, Summary = $"{workspace} · {e.Summary}", DtStart = e.Start, DtEnd = e.End, Sequence = e.Sequence,
                DtStamp = Utc(e.Stamp), LastModified = Utc(e.Stamp), Transparency = "TRANSPARENT",
                Description = string.Join("\n", new[] { note?.Text, e.Note }.Where(x => !string.IsNullOrEmpty(x))) is { Length: > 0 } d ? d : null,
            };
            if (note?.Type is { } type) ev.Categories.Add(type);
            cal.Events.Add(ev);
        }
        return new CalendarSerializer().SerializeToString(cal);
    }

    private sealed record ReleaseNote(string? Type, string Text);

    /// <summary>RT5: the release's type and, when it is in a version, its place in the chain ("Version: Orion 5.2 · Beta 2, 2 of 4"), for the description of each of its events. Titles stay as they were.</summary>
    private static async Task<Dictionary<string, ReleaseNote>> DescribeAsync(ReleaseDbContext db, IReadOnlyCollection<ReleaseTrains> trains, CancellationToken ct)
    {
        var typeIds = trains.Select(t => t.ReleaseTypeId).OfType<string>().Distinct().ToList();
        var types = await db.Set<ReleaseTypes>().AsNoTracking().Where(x => typeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var chains = await VersionChains.BriefsAsync(db, 0, ct);   // the gap only decides warnings, which a feed does not carry
        var result = new Dictionary<string, ReleaseNote>();
        foreach (var t in trains)
        {
            var type = t.ReleaseTypeId is { } id && types.TryGetValue(id, out var n) ? n : null;
            var lines = new List<string>();
            if (type is not null) lines.Add($"Release type: {type}{(t.BetaNumber is { } b ? $" {b}" : "")}");
            if (chains.TryGetValue(t.Id, out var c)) lines.Add($"Version: {c.VersionName} · {c.Label}, {c.Position} of {c.Count}");
            if (lines.Count > 0) result[t.Id] = new(type, string.Join("\n", lines));
        }
        return result;
    }

    /// <summary>RT5 (D47): the days a release's clients get it. Standard clients share one all-day event on the Standard date; each On-Demand client has its own, with a stable UID, so a moved or chosen date updates the same event.</summary>
    private static IEnumerable<Ev> ClientEvents(ReleaseTrains t, List<ClientScheduleService.ClientDay> days)
    {
        var kind = days[0].Kind == ClientScheduleKinds.Cert ? "CERT" : days[0].Kind;
        var standard = days.Where(d => d.Segment == ClientSegments.Standard).ToList();
        if (standard.Count > 0)
            yield return new($"{t.Id}-standard-clients@releasemgmt", $"{Name(t)}: Standard clients get {kind}", Day(standard[0].Date), Day(standard[0].Date.AddDays(1)), t.Version - 1, t.UpdatedAt, t.Id,
                $"Standard clients: {string.Join(", ", standard.Select(d => d.Name))}");
        foreach (var d in days.Where(d => d.Segment == ClientSegments.OnDemand))
            yield return new($"{t.Id}-{d.ClientId}-clientdate@releasemgmt", $"{Name(t)}: On-Demand {kind} for {d.Name}", Day(d.Date), Day(d.Date.AddDays(1)), d.Version + (t.Version - 1),
                d.Changed is DateTime c && c > t.UpdatedAt ? c : t.UpdatedAt, t.Id, $"On-Demand client: {d.Name} ({(d.Wave == "Standard" ? "takes the Standard date" : "own date")})");
    }

    /// <summary>REOS-107: a release's events are titled with its key in front ("RLS-12 R26.10: Code Freeze due"), so a calendar search for the key finds them. The feeds stay titles and times only:
    /// the owner, team, branch and on-call are not put in a link that anyone holding the token can read.</summary>
    private static string Name(ReleaseTrains t) => t.ReleaseKey is null ? t.Title : $"{t.ReleaseKey} {t.Title}";

    private static Ev GateEvent(StageGates g, ReleaseTrains t) =>
        new($"{g.Id}-gate@releasemgmt", $"{Name(t)}: {g.GateName} due", Day(g.DueOn), Day(g.DueOn.AddDays(1)), (g.Version - 1) + (t.Version - 1),
            g.LastChangedAt is DateTime c && c > t.UpdatedAt ? c : t.UpdatedAt, t.Id);

    /// <summary>All-day on the milestone's date; UID <c>{id}-milestone@releasemgmt</c> (Q-0844). The title says "done" once it is, so a subscriber sees it without opening the app.</summary>
    private static Ev MilestoneEvent(TrainMilestones m, ReleaseTrains t) =>
        new($"{m.Id}-milestone@releasemgmt", $"{Name(t)}: {m.Name}{(m.IsDone ? " (done)" : "")}", Day(m.DueOn), Day(m.DueOn.AddDays(1)), (m.Version - 1) + (t.Version - 1),
            m.LastChangedAt is DateTime c && c > t.UpdatedAt ? c : t.UpdatedAt, t.Id);

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
