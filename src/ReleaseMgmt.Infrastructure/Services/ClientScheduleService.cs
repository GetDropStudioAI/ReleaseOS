using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ClientFreezeNote(string Id, string Name, string Kind, string StartsAt, string EndsAt);
/// <summary>One client on a release. <c>Date</c> is the day the client gets the release: the release's Standard date for a Standard client, the chosen date else the offered date for an On-Demand one.
/// <c>Wave</c> is "Standard" when that day is the Standard date, so an On-Demand client that takes it is in the Standard wave. <c>Scheduled</c> is false for an On-Demand client with no row yet: its offered date is the default.</summary>
public sealed record ClientDateRow(string ClientId, string Name, string Segment, string Date, string Wave, string? OfferedDate, string? ChosenDate, NamedRef? ChosenBy, string? ChosenAt,
    bool Scheduled, bool Archived, int Version, bool BeforeStandardDate, IReadOnlyList<ClientFreezeNote> Freezes);
/// <summary><c>Kind</c> is "Cert" or "Production", or null when the release's type carries no client dates (<c>Note</c> says so). <c>Open</c> is false once the release is complete, aborted or archived.</summary>
public sealed record ClientScheduleView(string TrainId, string? Kind, string? TypeName, string StandardDate, int OffsetDays, bool Open, string? Note, IReadOnlyList<ClientDateRow> Rows, int Unscheduled, int Warnings);
/// <summary>Null leaves a date. <c>ChosenDate = ""</c> clears the chosen date. A date is yyyy-MM-dd.</summary>
public sealed record ClientDatePatch(string? OfferedDate = null, string? ChosenDate = null);

/// <summary>
/// RT4 (D47): the dates of the clients on a CERT or Production release. One date for all Standard clients (the release's target date); each On-Demand client has an offered date (the Standard date plus
/// <c>Release:OnDemandOffsetDays</c> when its row is made) and may have a chosen date. Moving the Standard date moves every offered date by the same days and leaves chosen dates alone
/// (<see cref="ShiftOfferedAsync"/>, called by every path that moves it). Who recorded a chosen date, and when, is on the row and in an audit row. Every write is one transaction with one
/// <c>ReleaseClientDate</c> audit row per row it touches and a Version bump (If-Match; 0 means "no row yet"). A date inside a freeze or chill window is advisory, never refused.
/// Rules, provisional (Q-119a): a chosen date is not before the Standard date; clients may choose CERT dates too.
/// </summary>
public sealed class ClientScheduleService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    /// <summary>The audit entity type of a client date row.</summary>
    public const string Entity = "ReleaseClientDate";
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.ClientDateInvalid, m);

    /// <summary>Calendar days added to the Standard date for an offered date. Read at each use, so a changed value applies to rows made afterwards; rows already made keep the date they were offered.</summary>
    public int OffsetDays => ClientScheduleKinds.OffsetFrom(config[ClientScheduleKinds.OffsetKey]);

    private sealed record Context(ReleaseTrains Train, string? Kind, string? TypeName);

    private static async Task<Context?> ContextAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        var name = t.ReleaseTypeId is null ? null : await db.Set<ReleaseTypes>().AsNoTracking().Where(x => x.Id == t.ReleaseTypeId).Select(x => x.Name).SingleOrDefaultAsync(ct);
        return new(t, ClientScheduleKinds.Of(name), name);
    }

    private static bool IsOpen(ReleaseTrains t) => t.ArchivedAt is null && t.CurrentStatus is not ("Complete" or "Aborted");

    // ---- read ----------------------------------------------------------------------------------------------------------------------------------
    public async Task<ClientScheduleView?> GetAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var cx = await ContextAsync(db, trainId, ct);
        return cx is null ? null : await ViewAsync(db, cx, OffsetDays, ct);
    }

    /// <summary>
    /// Who is on a release and on which day (RT4 rules, one place): Standard clients take the Standard date on a Production release only; an On-Demand client takes its chosen date, else its offered
    /// date, else the default (Standard date plus the offset) while it has no row. An archived client is on the release only when it already has a row. Null kind (a type with no client dates): nobody.
    /// </summary>
    public static List<(Clients Client, ReleaseClientDates? Dates, DateOnly Date)> Plan(string? kind, DateOnly standard, int offset, IEnumerable<Clients> clients, IReadOnlyDictionary<string, ReleaseClientDates> dates)
    {
        var rows = new List<(Clients Client, ReleaseClientDates? Dates, DateOnly Date)>();
        if (kind is null) return rows;
        foreach (var c in clients)
        {
            dates.TryGetValue(c.Id, out var d);
            if (c.Segment == ClientSegments.Standard) { if (kind == ClientScheduleKinds.Production && c.ArchivedAt is null) rows.Add((c, null, standard)); continue; }
            if (c.ArchivedAt is not null && d is null) continue;   // an archived client with no row is not on this release
            rows.Add((c, d, d is null ? standard.AddDays(offset) : d.ChosenDate ?? d.OfferedDate));
        }
        return rows;
    }

    /// <summary>One client's day on one release, for the calendar, the overview and the ICS feeds (RT5). <c>Changed</c> is when its row last changed (null: no row yet).</summary>
    public sealed record ClientDay(string TrainId, string Kind, string ClientId, string Name, string Segment, DateOnly Date, string Wave, int Version, DateTime? Changed);

    /// <summary>The client days of many releases in four queries (clients, rows, nothing per release). Releases of a type without client dates are absent from the result.</summary>
    public static async Task<Dictionary<string, List<ClientDay>>> DaysAsync(ReleaseDbContext db, IReadOnlyCollection<ReleaseTrains> trains, int offset, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<ClientDay>>();
        var typeIds = trains.Select(t => t.ReleaseTypeId).OfType<string>().Distinct().ToList();
        var kinds = (await db.Set<ReleaseTypes>().AsNoTracking().Where(x => typeIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct))
            .Select(x => (x.Id, Kind: ClientScheduleKinds.Of(x.Name))).Where(x => x.Kind is not null).ToDictionary(x => x.Id, x => x.Kind!);
        var withKind = trains.Where(t => t.ReleaseTypeId is not null && kinds.ContainsKey(t.ReleaseTypeId)).ToList();
        if (withKind.Count == 0) return result;
        var clients = await db.Set<Clients>().AsNoTracking().ToListAsync(ct);
        if (clients.Count == 0) return result;
        var ids = withKind.Select(t => t.Id).ToList();
        var rows = (await db.Set<ReleaseClientDates>().AsNoTracking().Where(d => ids.Contains(d.ReleaseTrainId)).ToListAsync(ct)).ToLookup(d => d.ReleaseTrainId);
        foreach (var t in withKind)
        {
            var kind = kinds[t.ReleaseTypeId!];
            var mine = rows[t.Id].ToDictionary(d => d.ClientId);
            var days = Plan(kind, t.TargetReleaseDate, offset, clients, mine)
                .Select(r => new ClientDay(t.Id, kind, r.Client.Id, r.Client.Name, r.Client.Segment, r.Date, r.Date == t.TargetReleaseDate ? "Standard" : "On-Demand", r.Dates?.Version ?? 0, r.Dates?.LastChangedAt ?? r.Dates?.CreatedAt))
                .OrderBy(d => d.Date).ThenBy(d => d.Segment == ClientSegments.Standard ? 0 : 1).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.ClientId, StringComparer.Ordinal).ToList();
            if (days.Count > 0) result[t.Id] = days;
        }
        return result;
    }

    private async Task<ClientScheduleView> ViewAsync(ReleaseDbContext db, Context cx, int offset, CancellationToken ct)
    {
        var t = cx.Train;
        var standard = t.TargetReleaseDate;
        if (cx.Kind is null)
            return new(t.Id, null, cx.TypeName, Day(standard), offset, IsOpen(t), "Client dates apply to CERT and Production releases. This release's type carries none.", [], 0, 0);

        var clients = await db.Set<Clients>().AsNoTracking().ToListAsync(ct);
        var dates = (await db.Set<ReleaseClientDates>().AsNoTracking().Where(d => d.ReleaseTrainId == t.Id).ToListAsync(ct)).ToDictionary(d => d.ClientId);
        var who = dates.Values.Select(d => d.ChosenByUserId).OfType<string>().Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => who.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // One query for every window that touches any shown day; each row then tests its own UTC day.
        var rows = Plan(cx.Kind, standard, offset, clients, dates);
        var windows = new List<FreezeWindows>();
        if (rows.Count > 0)
        {
            var lo = rows.Min(r => r.Date); var hi = rows.Max(r => r.Date);
            windows = await FreezeService.OverlappingAsync(db, t.Id, lo.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), hi.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ct);
        }
        var view = rows.Select(r =>
        {
            var start = r.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); var end = start.AddDays(1);
            var freezes = windows.Where(w => w.StartsAt < end && w.EndsAt > start).Select(w => new ClientFreezeNote(w.Id, w.Name, w.Kind, Iso(w.StartsAt), Iso(w.EndsAt))).ToList();
            var onDemand = r.Client.Segment == ClientSegments.OnDemand;
            var d = r.Dates;
            return new ClientDateRow(r.Client.Id, r.Client.Name, r.Client.Segment, Day(r.Date), r.Date == standard ? "Standard" : "On-Demand",
                onDemand ? Day(d?.OfferedDate ?? standard.AddDays(offset)) : null, d?.ChosenDate is { } ch ? Day(ch) : null,
                d?.ChosenByUserId is { } u ? new NamedRef(u, names.GetValueOrDefault(u, u)) : null, d?.ChosenAt is { } at ? Iso(at) : null,
                !onDemand || d is not null, r.Client.ArchivedAt is not null, d?.Version ?? 0, onDemand && r.Date < standard, freezes);
        })
        .OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Segment == ClientSegments.Standard ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ClientId, StringComparer.Ordinal).ToList();
        return new(t.Id, cx.Kind, cx.TypeName, Day(standard), offset, IsOpen(t), null, view, view.Count(r => !r.Scheduled), view.Count(r => r.Freezes.Count > 0 || r.BeforeStandardDate));
    }

    // ---- write ---------------------------------------------------------------------------------------------------------------------------------
    private static object Snapshot(ReleaseClientDates d, string clientName) => new
    {
        client = clientName, d.ClientId, offeredDate = Day(d.OfferedDate), chosenDate = d.ChosenDate is { } c ? Day(c) : null, chosenBy = d.ChosenByUserId,
        chosenAt = d.ChosenAt is { } a ? Iso(a) : null, d.Version,
    };

    private static List<GuardFailure> CheckRelease(Context cx)
    {
        if (cx.Kind is null) return [Bad($"Client dates apply to CERT and Production releases; this release's type is {cx.TypeName ?? "not set"}")];
        if (!IsOpen(cx.Train)) return [Bad("This release is complete, aborted or archived; its client dates no longer change")];
        return [];
    }

    /// <summary>Sets an On-Demand client's offered and/or chosen date on a release, making its row when it has none. <paramref name="expectedVersion"/> is the row's Version, or 0 when the client has no row yet.</summary>
    public Task<ServiceResult<ReleaseClientDates>> SetAsync(string trainId, string clientId, ClientDatePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var cx = await ContextAsync(db, trainId, ct);
            if (cx is null) return ServiceResult<ReleaseClientDates>.NotFound("train");
            var client = await db.Set<Clients>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, ct);
            if (client is null) return ServiceResult<ReleaseClientDates>.NotFound("client");
            var row = await db.Set<ReleaseClientDates>().SingleOrDefaultAsync(d => d.ReleaseTrainId == trainId && d.ClientId == clientId, ct);
            if (row is null ? expectedVersion is > 0 : VersionMismatch(expectedVersion, row.Version))
                return row is null ? ServiceResult<ReleaseClientDates>.NotFound("client date") : ServiceResult<ReleaseClientDates>.Conflict(row);
            if (row is not null && expectedVersion == 0) return ServiceResult<ReleaseClientDates>.Conflict(row);

            var f = CheckRelease(cx);
            if (client.Segment != ClientSegments.OnDemand) f.Add(Bad($"{client.Name} is a Standard client: it takes the release's Standard date and has no date of its own"));
            else if (client.ArchivedAt is not null && row is null) f.Add(Bad($"{client.Name} is archived; restore it before giving it a date"));
            var standard = cx.Train.TargetReleaseDate;
            DateOnly? offered = null, chosen = null; var clearChosen = false;
            if (p.OfferedDate is not null)
            {
                if (!ClientScheduleKinds.TryParseDay(p.OfferedDate, out var o)) f.Add(Bad("The offered date is a calendar date written yyyy-MM-dd"));
                else if (o < standard) f.Add(Bad($"An offered date cannot be earlier than the Standard date ({Day(standard)})"));
                else offered = o;
            }
            if (p.ChosenDate is not null)
            {
                if (p.ChosenDate.Trim().Length == 0) clearChosen = true;
                else if (!ClientScheduleKinds.TryParseDay(p.ChosenDate, out var c)) f.Add(Bad("The chosen date is a calendar date written yyyy-MM-dd"));
                else if (c < standard) f.Add(Bad($"A chosen date cannot be earlier than the Standard date ({Day(standard)})"));
                else chosen = c;
            }
            if (p.OfferedDate is null && p.ChosenDate is null) f.Add(Bad("Give a chosen date, an offered date, or clear the chosen date"));
            if (f.Count > 0) return ServiceResult<ReleaseClientDates>.Fail(f);

            var now = Now;
            var created = row is null;
            row ??= new ReleaseClientDates { Id = Ids.New(), ReleaseTrainId = trainId, ClientId = clientId, OfferedDate = standard.AddDays(OffsetDays), CreatedAt = now };
            var before = created ? null : Snapshot(row, client.Name);
            var beforeChosen = row.ChosenDate; var beforeOffered = row.OfferedDate;
            if (offered is { } ov) row.OfferedDate = ov;
            string action;
            if (chosen is { } cv && cv != beforeChosen) { row.ChosenDate = cv; row.ChosenByUserId = actor.UserId; row.ChosenAt = now; action = beforeChosen is null ? "RecordChosen" : "ChangeChosen"; }
            else if (clearChosen && beforeChosen is not null) { row.ChosenDate = null; row.ChosenByUserId = null; row.ChosenAt = null; action = "ClearChosen"; }
            else if (row.OfferedDate != beforeOffered) action = "ChangeOffered";
            else if (created) action = "Schedule";
            else return ServiceResult<ReleaseClientDates>.Ok(row);   // nothing changed: no version, no audit row
            row.LastChangedByUserId = actor.UserId; row.LastChangedAt = now;
            if (created) db.Set<ReleaseClientDates>().Add(row); else row.Version++;
            Audit(db, actor, trainId, Entity, row.Id, action, before, Snapshot(row, client.Name));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseClientDates>.Ok(row);
        }, ct);

    /// <summary>Makes a row, with the default offered date, for every active On-Demand client that has none on this release. Returns how many rows it made (0 writes nothing).</summary>
    public Task<ServiceResult<int>> ScheduleAllAsync(string trainId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var cx = await ContextAsync(db, trainId, ct);
            if (cx is null) return ServiceResult<int>.NotFound("train");
            var f = CheckRelease(cx);
            if (f.Count > 0) return ServiceResult<int>.Fail(f);
            var have = await db.Set<ReleaseClientDates>().Where(d => d.ReleaseTrainId == trainId).Select(d => d.ClientId).ToListAsync(ct);
            var missing = await db.Set<Clients>().Where(c => c.Segment == ClientSegments.OnDemand && c.ArchivedAt == null && !have.Contains(c.Id)).ToListAsync(ct);
            var now = Now; var offered = cx.Train.TargetReleaseDate.AddDays(OffsetDays);
            foreach (var c in missing.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                var row = new ReleaseClientDates { Id = Ids.New(), ReleaseTrainId = trainId, ClientId = c.Id, OfferedDate = offered, CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now };
                db.Set<ReleaseClientDates>().Add(row);
                Audit(db, actor, trainId, Entity, row.Id, "Schedule", null, Snapshot(row, c.Name));
            }
            if (missing.Count > 0) await db.SaveChangesAsync(ct);
            return ServiceResult<int>.Ok(missing.Count);
        }, ct);

    /// <summary>
    /// The Standard date of <paramref name="trainId"/> moved from <paramref name="oldTarget"/> to <paramref name="newTarget"/>: every offered date moves by the same calendar days, chosen dates stay.
    /// Called inside the caller's transaction by each path that changes a release's date. <paramref name="audit"/> writes one row per client date moved. Returns the rows moved.
    /// </summary>
    public static async Task<int> ShiftOfferedAsync(ReleaseDbContext db, string trainId, DateOnly oldTarget, DateOnly newTarget, DateTime now, string actorUserId,
        Action<ReleaseClientDates, string, string> audit, CancellationToken ct = default)
    {
        var delta = newTarget.DayNumber - oldTarget.DayNumber;
        if (delta == 0) return 0;
        var rows = await db.Set<ReleaseClientDates>().Where(d => d.ReleaseTrainId == trainId).OrderBy(d => d.Id).ToListAsync(ct);
        foreach (var d in rows)
        {
            var was = Day(d.OfferedDate);
            d.OfferedDate = d.OfferedDate.AddDays(delta);
            d.Version++; d.LastChangedByUserId = actorUserId; d.LastChangedAt = now;
            audit(d, was, Day(d.OfferedDate));
        }
        return rows.Count;
    }
}
