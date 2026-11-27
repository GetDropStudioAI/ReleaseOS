using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record CabAgendaItemView(string Id, string ChangeRequestId, string ChangeKey, string ChangeTitle, string ChangeStatus, int SequenceOrder, string? Notes, int Version);
public sealed record CabAttendeeView(NamedRef Person, string AttendeeRole, string Attendance, bool IsChair, int Version);
public sealed record CabMeetingView(string Id, string Title, string StartsAt, string EndsAt, NamedRef Chair, string Status, string? Location, string? Minutes, string CreatedAt, string? LastChangedAt, int Version,
    IReadOnlyList<CabAgendaItemView> Agenda, IReadOnlyList<CabAttendeeView> Attendees, IReadOnlyList<ChangeDecisionView> Decisions, string? CompletedAt = null);

public sealed record NewMeeting(string? Title, DateTime StartsAt, DateTime EndsAt, string? ChairUserId = null, string? Location = null);
/// <summary>Null leaves a field as it is. <see cref="ClearLocation"/> and <see cref="ClearMinutes"/> empty those two, since null cannot say "empty".</summary>
public sealed record MeetingPatch(string? Title = null, DateTime? StartsAt = null, DateTime? EndsAt = null, string? ChairUserId = null, string? Location = null, string? Minutes = null,
    bool ClearLocation = false, bool ClearMinutes = false);

/// <summary>
/// REOS-175 (D56, D63): CAB meetings, their agenda of changes, attendees and the decisions recorded under them. Status moves (<c>Planned &gt; InProgress &gt; Completed</c>, or Cancelled)
/// only here. Every write is one transaction with one audit row (<c>CabMeeting</c>, <c>CabAgendaItem</c> or <c>CabAttendee</c>), clock from the TimeProvider, UUIDv7 ids, If-Match with the
/// current row on 409. Decisions themselves are recorded by <see cref="ChangeRequestService.DecideAsync"/> (the separation-of-duties rules live with the change).
/// </summary>
public sealed class CabService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private readonly IRealtimePublisher? _realtime = realtime;
    private static string Iso(DateTime t) => ChangeRequestService.Iso(t);
    private static GuardFailure Bad(string m) => new(CabGuards.CabMeetingInvalid, m);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- reads -----------------------------------------------------------------------------------------------------------------------

    public async Task<List<CabMeetingView>> ListAsync(string? status = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<CabMeetings>().AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(m => m.Status == status);
        var rows = await q.OrderByDescending(m => m.StartsAt).ThenBy(m => m.Id).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<CabMeetingView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<CabMeetings>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<CabMeetingView>> ViewsAsync(ReleaseDbContext db, List<CabMeetings> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var agenda = (await db.Set<CabAgendaItems>().AsNoTracking().Where(a => ids.Contains(a.MeetingId)).OrderBy(a => a.SequenceOrder).ThenBy(a => a.Id).ToListAsync(ct)).ToLookup(a => a.MeetingId);
        var attendees = (await db.Set<CabAttendees>().AsNoTracking().Where(a => ids.Contains(a.MeetingId)).ToListAsync(ct)).ToLookup(a => a.MeetingId);
        var decisions = (await db.Set<CabDecisions>().AsNoTracking().Where(d => d.MeetingId != null && ids.Contains(d.MeetingId)).OrderBy(d => d.DecidedAt).ThenBy(d => d.Id).ToListAsync(ct)).ToLookup(d => d.MeetingId!);
        var changeIds = agenda.SelectMany(g => g.Select(a => a.ChangeRequestId)).Distinct().ToList();
        var changes = await db.Set<ChangeRequests>().AsNoTracking().Where(c => changeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var userIds = rows.Select(r => r.ChairUserId).Concat(attendees.SelectMany(g => g.Select(a => a.UserId))).Concat(decisions.SelectMany(g => g.Select(d => d.DecidedByUserId))).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        NamedRef Who(string id) => new(id, names.GetValueOrDefault(id, id));
        // REOS-179: the end time of a held meeting is when it was completed (the audit row of the move; EndsAt is only the scheduled end)
        var doneIds = rows.Where(r => r.Status == CabMeetingStatuses.Completed).Select(r => r.Id).ToList();
        var completed = doneIds.Count == 0 ? [] : (await db.Set<AuditEvents>().AsNoTracking().Where(a => a.EntityType == "CabMeeting" && a.Action == "Complete" && doneIds.Contains(a.EntityId)).ToListAsync(ct))
            .GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.Max(a => a.OccurredAt));
        return [.. rows.Select(m => new CabMeetingView(m.Id, m.Title, Iso(m.StartsAt), Iso(m.EndsAt), Who(m.ChairUserId), m.Status, m.Location, m.Minutes, Iso(m.CreatedAt),
            m.LastChangedAt is { } lc ? Iso(lc) : null, m.Version,
            [.. agenda[m.Id].Select(a => new CabAgendaItemView(a.Id, a.ChangeRequestId, changes[a.ChangeRequestId].ChangeKey, changes[a.ChangeRequestId].Title, changes[a.ChangeRequestId].Status, a.SequenceOrder, a.Notes, a.Version))],
            [.. attendees[m.Id].OrderByDescending(a => a.UserId == m.ChairUserId).ThenBy(a => names.GetValueOrDefault(a.UserId, a.UserId)).Select(a => new CabAttendeeView(Who(a.UserId), a.AttendeeRole, a.Attendance, a.UserId == m.ChairUserId, a.Version))],
            [.. decisions[m.Id].Select(d => new ChangeDecisionView(d.Id, d.Decision, d.Retroactive, d.MeetingId, d.Rationale, Who(d.DecidedByUserId), Iso(d.DecidedAt)))],
            completed.TryGetValue(m.Id, out var doneAt) ? Iso(doneAt) : null))];
    }

    private static object Snapshot(CabMeetings m) => new { m.Title, startsAt = Iso(m.StartsAt), endsAt = Iso(m.EndsAt), m.ChairUserId, m.Status, m.Location, m.Version };

    // ---- meetings --------------------------------------------------------------------------------------------------------------------

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, CabMeetings m, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (m.Title.Length is 0 or > 200) f.Add(Bad("A title is 1 to 200 characters"));
        if (m.EndsAt <= m.StartsAt) f.Add(Bad("The meeting must end after it starts"));
        if (m.Location is { Length: > 200 }) f.Add(Bad("A location is at most 200 characters"));
        if (m.Minutes is { Length: > 8000 }) f.Add(Bad("Minutes are at most 8000 characters"));
        if (await Memberships.RoleOf(db, m.ChairUserId, ct) is null) f.Add(new(CabGuards.CabNotMember, "The chair is not a member of this workspace"));
        return f;
    }

    /// <summary>The chair always has an attendee row (a member, invited), so the roll is complete without a second step.</summary>
    private static async Task EnsureChairAttendsAsync(ReleaseDbContext db, string meetingId, string chairId, Actor actor, DateTime now, CancellationToken ct)
    {
        if (await db.Set<CabAttendees>().AnyAsync(a => a.MeetingId == meetingId && a.UserId == chairId, ct)) return;
        db.Set<CabAttendees>().Add(new CabAttendees { MeetingId = meetingId, UserId = chairId, AttendeeRole = CabAttendeeRoles.Member, Attendance = CabAttendance.Invited, LastChangedByUserId = actor.UserId, LastChangedAt = now });
    }

    public Task<ServiceResult<CabMeetingView>> CreateAsync(NewMeeting n, Actor actor, CancellationToken ct = default) =>
        Announce(RunAsync(async db =>
        {
            var now = Now;
            var m = new CabMeetings
            {
                Id = Ids.New(), Title = (n.Title ?? "").Trim(), StartsAt = n.StartsAt, EndsAt = n.EndsAt, ChairUserId = Blank(n.ChairUserId) ?? actor.UserId, Location = Blank(n.Location),
                CreatedByUserId = actor.UserId, CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
            };
            var f = await CheckAsync(db, m, ct);
            if (f.Count > 0) return ServiceResult<CabMeetingView>.Fail(f);
            db.Set<CabMeetings>().Add(m);
            await EnsureChairAttendsAsync(db, m.Id, m.ChairUserId, actor, now, ct);
            Audit(db, actor, null, "CabMeeting", m.Id, "Create", null, Snapshot(m));
            await db.SaveChangesAsync(ct);
            return ServiceResult<CabMeetingView>.Ok((await ViewsAsync(db, [m], ct))[0]);
        }, ct), db => Memberships.WorkspaceOf(db));

    public Task<ServiceResult<CabMeetingView>> UpdateAsync(string id, MeetingPatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(id, actor, expectedVersion, "Update", ct, async (db, m, now) =>
        {
            if (m.Status is CabMeetingStatuses.Completed or CabMeetingStatuses.Cancelled)
            {
                // minutes may still be written after the meeting; everything else is fixed once it is over
                if (p.Title is not null || p.StartsAt is not null || p.EndsAt is not null || p.ChairUserId is not null || p.Location is not null || p.ClearLocation)
                    return new GuardFailure(CabGuards.CabMeetingClosed, $"A {m.Status} meeting keeps its title, time, chair and place; only its minutes can still be written");
            }
            if (p.Title is not null) m.Title = p.Title.Trim();
            if (p.StartsAt is { } s) m.StartsAt = s;
            if (p.EndsAt is { } e) m.EndsAt = e;
            if (p.Location is not null) m.Location = Blank(p.Location);
            if (p.ClearLocation) m.Location = null;
            if (p.Minutes is not null) m.Minutes = Blank(p.Minutes);
            if (p.ClearMinutes) m.Minutes = null;
            if (Blank(p.ChairUserId) is { } chair && chair != m.ChairUserId) m.ChairUserId = chair;
            var f = await CheckAsync(db, m, ct);
            if (f.Count > 0) return f[0];
            await EnsureChairAttendsAsync(db, m.Id, m.ChairUserId, new Actor(m.LastChangedByUserId ?? ""), now, ct);
            return null;
        });

    public Task<ServiceResult<CabMeetingView>> StartAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => MoveAsync(id, CabMeetingStatuses.InProgress, "Start", actor, expectedVersion, ct);
    public Task<ServiceResult<CabMeetingView>> CompleteAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => MoveAsync(id, CabMeetingStatuses.Completed, "Complete", actor, expectedVersion, ct);
    public Task<ServiceResult<CabMeetingView>> CancelAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => MoveAsync(id, CabMeetingStatuses.Cancelled, "Cancel", actor, expectedVersion, ct);

    private Task<ServiceResult<CabMeetingView>> MoveAsync(string id, string to, string action, Actor actor, int? expectedVersion, CancellationToken ct) =>
        Mutate(id, actor, expectedVersion, action, ct, (db, m, now) =>
        {
            if (!CabRules.IsLegalMeetingMove(m.Status, to)) return Task.FromResult<GuardFailure?>(new(CabGuards.IllegalMeetingTransition, $"A CAB meeting cannot move from {m.Status} to {to}"));
            m.Status = to;
            return Task.FromResult<GuardFailure?>(null);
        });

    /// <summary>Load, version check, change, version bump, one audit row, save: the shape of every meeting write.</summary>
    private Task<ServiceResult<CabMeetingView>> Mutate(string id, Actor actor, int? expectedVersion, string action, CancellationToken ct, Func<ReleaseDbContext, CabMeetings, DateTime, Task<GuardFailure?>> change) =>
        Announce(RunAsync(async db =>
        {
            var m = await db.Set<CabMeetings>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return ServiceResult<CabMeetingView>.NotFound("meeting");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<CabMeetingView>.Conflict((await ViewsAsync(db, [m], ct))[0]);
            var now = Now;
            var before = Snapshot(m);
            m.LastChangedByUserId = actor.UserId;   // set before the change: the chair's attendee row takes it
            if (await change(db, m, now) is { } f) { db.ChangeTracker.Clear(); return ServiceResult<CabMeetingView>.Fail(f); }
            m.Version++; m.LastChangedAt = now;
            Audit(db, actor, null, "CabMeeting", m.Id, action, before, Snapshot(m));
            await db.SaveChangesAsync(ct);
            return ServiceResult<CabMeetingView>.Ok((await ViewsAsync(db, [m], ct))[0]);
        }, ct), db => Memberships.WorkspaceOf(db));

    // ---- agenda ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Puts a submitted change in front of a meeting that has not been held yet or is being held. Returns the meeting. <paramref name="expectedVersion"/> is the meeting's.</summary>
    public Task<ServiceResult<CabMeetingView>> AddAgendaItemAsync(string meetingId, string changeId, string? notes, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(meetingId, actor, expectedVersion, "AddAgendaItem", ct, async (db, m, now) =>
        {
            if (m.Status is not (CabMeetingStatuses.Planned or CabMeetingStatuses.InProgress)) return new GuardFailure(CabGuards.CabMeetingClosed, $"The agenda of a {m.Status} meeting is closed");
            var c = await db.Set<ChangeRequests>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == changeId, ct);
            if (c is null) return new GuardFailure(CabGuards.CabMeetingInvalid, "The change request does not exist in this workspace");
            if (c.Status != ChangeStatuses.Submitted) return new GuardFailure(CabGuards.CabChangeNotSubmitted, $"Only a Submitted change goes on an agenda; {c.ChangeKey} is {c.Status}");
            if (await db.Set<CabAgendaItems>().AnyAsync(a => a.MeetingId == m.Id && a.ChangeRequestId == c.Id, ct)) return new GuardFailure(CabGuards.CabDuplicate, $"{c.ChangeKey} is already on this agenda");
            if (notes is { Length: > 2000 }) return Bad("Agenda notes are at most 2000 characters");
            var next = (await db.Set<CabAgendaItems>().Where(a => a.MeetingId == m.Id).Select(a => (int?)a.SequenceOrder).MaxAsync(ct) ?? 0) + 1;
            var item = new CabAgendaItems { Id = Ids.New(), MeetingId = m.Id, ChangeRequestId = c.Id, SequenceOrder = next, Notes = Blank(notes), LastChangedByUserId = actor.UserId, LastChangedAt = now };
            db.Set<CabAgendaItems>().Add(item);
            Audit(db, actor, c.ReleaseTrainId, "CabAgendaItem", item.Id, "Create", null, new { meetingId = m.Id, changeRequestId = c.Id, c.ChangeKey, item.SequenceOrder });
            return null;
        });

    /// <summary>Edits an agenda item's notes or position. <paramref name="expectedVersion"/> is the item's.</summary>
    public Task<ServiceResult<CabAgendaItemView>> UpdateAgendaItemAsync(string meetingId, string itemId, string? notes, int? sequenceOrder, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var item = await db.Set<CabAgendaItems>().SingleOrDefaultAsync(a => a.Id == itemId && a.MeetingId == meetingId, ct);
            if (item is null) return ServiceResult<CabAgendaItemView>.NotFound("agenda item");
            if (await ClosedAsync(db, meetingId, "agenda", ct) is { } closed) return ServiceResult<CabAgendaItemView>.Fail(closed);
            if (VersionMismatch(expectedVersion, item.Version)) return ServiceResult<CabAgendaItemView>.Conflict(await ItemViewAsync(db, item, ct));
            if (notes is { Length: > 2000 }) return ServiceResult<CabAgendaItemView>.Fail(Bad("Agenda notes are at most 2000 characters"));
            if (sequenceOrder is < 1) return ServiceResult<CabAgendaItemView>.Fail(Bad("An agenda position starts at 1"));
            var before = new { item.SequenceOrder, item.Notes, item.Version };
            if (notes is not null) item.Notes = Blank(notes);
            if (sequenceOrder is int so) item.SequenceOrder = so;
            item.Version++; item.LastChangedByUserId = actor.UserId; item.LastChangedAt = Now;
            Audit(db, actor, null, "CabAgendaItem", item.Id, "Update", before, new { item.SequenceOrder, item.Notes, item.Version });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CabAgendaItemView>.Ok(await ItemViewAsync(db, item, ct));
        }, ct);

    /// <summary>REOS-179: a Completed or Cancelled meeting is the record of what happened; its agenda and roll no longer change (minutes can still be written).</summary>
    private static async Task<GuardFailure?> ClosedAsync(ReleaseDbContext db, string meetingId, string what, CancellationToken ct)
    {
        var status = await db.Set<CabMeetings>().AsNoTracking().Where(m => m.Id == meetingId).Select(m => m.Status).SingleOrDefaultAsync(ct);
        return status is CabMeetingStatuses.Completed or CabMeetingStatuses.Cancelled ? new GuardFailure(CabGuards.CabMeetingClosed, $"The {what} of a {status} meeting is closed") : null;
    }

    /// <summary>
    /// REOS-179: puts one agenda item at <paramref name="position"/> (1 is first; past the end means last) and renumbers the others 1..n in the same transaction, so two chairs reordering at once
    /// cannot leave duplicate positions. <paramref name="expectedVersion"/> is the meeting's. Returns the meeting.
    /// </summary>
    public Task<ServiceResult<CabMeetingView>> MoveAgendaItemAsync(string meetingId, string itemId, int position, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(meetingId, actor, expectedVersion, "ReorderAgenda", ct, async (db, m, now) =>
        {
            if (m.Status is not (CabMeetingStatuses.Planned or CabMeetingStatuses.InProgress)) return new GuardFailure(CabGuards.CabMeetingClosed, $"The agenda of a {m.Status} meeting is closed");
            if (position < 1) return Bad("An agenda position starts at 1");
            var items = await db.Set<CabAgendaItems>().Where(a => a.MeetingId == m.Id).OrderBy(a => a.SequenceOrder).ThenBy(a => a.Id).ToListAsync(ct);
            var item = items.SingleOrDefault(a => a.Id == itemId);
            if (item is null) return new GuardFailure(CabGuards.CabMeetingInvalid, "That change is not on this agenda");
            var before = items.Select(a => a.ChangeRequestId).ToList();
            items.Remove(item);
            items.Insert(Math.Min(position, items.Count + 1) - 1, item);
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].SequenceOrder == i + 1) continue;
                items[i].SequenceOrder = i + 1; items[i].Version++; items[i].LastChangedByUserId = actor.UserId; items[i].LastChangedAt = now;
            }
            Audit(db, actor, null, "CabAgendaItem", item.Id, "Reorder", new { meetingId = m.Id, order = before }, new { meetingId = m.Id, order = items.Select(a => a.ChangeRequestId).ToList() });
            return null;
        });

    private static async Task<CabAgendaItemView> ItemViewAsync(ReleaseDbContext db, CabAgendaItems a, CancellationToken ct)
    {
        var c = await db.Set<ChangeRequests>().AsNoTracking().SingleAsync(x => x.Id == a.ChangeRequestId, ct);
        return new CabAgendaItemView(a.Id, a.ChangeRequestId, c.ChangeKey, c.Title, c.Status, a.SequenceOrder, a.Notes, a.Version);
    }

    /// <summary>Takes a change off an agenda (hard delete, audited). Allowed until the meeting is over.</summary>
    public Task<ServiceResult<CabMeetingView>> RemoveAgendaItemAsync(string meetingId, string itemId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(meetingId, actor, expectedVersion, "RemoveAgendaItem", ct, async (db, m, now) =>
        {
            if (m.Status is not (CabMeetingStatuses.Planned or CabMeetingStatuses.InProgress)) return new GuardFailure(CabGuards.CabMeetingClosed, $"The agenda of a {m.Status} meeting is closed");
            var item = await db.Set<CabAgendaItems>().SingleOrDefaultAsync(a => a.Id == itemId && a.MeetingId == m.Id, ct);
            if (item is null) return new GuardFailure(CabGuards.CabMeetingInvalid, "That change is not on this agenda");
            db.Set<CabAgendaItems>().Remove(item);
            Audit(db, actor, null, "CabAgendaItem", item.Id, "Delete", new { meetingId = m.Id, item.ChangeRequestId, item.SequenceOrder }, null);
            return null;
        });

    // ---- attendees -------------------------------------------------------------------------------------------------------------------

    public Task<ServiceResult<CabMeetingView>> InviteAsync(string meetingId, string userId, string? role, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(meetingId, actor, expectedVersion, "Invite", ct, async (db, m, now) =>
        {
            if (m.Status is CabMeetingStatuses.Completed or CabMeetingStatuses.Cancelled) return new GuardFailure(CabGuards.CabMeetingClosed, $"A {m.Status} meeting takes no more invitations");
            var r = role ?? CabAttendeeRoles.Member;
            if (!CabAttendeeRoles.All.Contains(r)) return Bad("Role must be Member or Guest");
            if (await Memberships.RoleOf(db, userId, ct) is null) return new GuardFailure(CabGuards.CabNotMember, "That person is not a member of this workspace");
            if (await db.Set<CabAttendees>().AnyAsync(a => a.MeetingId == m.Id && a.UserId == userId, ct)) return new GuardFailure(CabGuards.CabDuplicate, "That person is already invited");
            db.Set<CabAttendees>().Add(new CabAttendees { MeetingId = m.Id, UserId = userId, AttendeeRole = r, Attendance = CabAttendance.Invited, LastChangedByUserId = actor.UserId, LastChangedAt = now });
            Audit(db, actor, null, "CabAttendee", $"{m.Id}:{userId}", "Invite", null, new { meetingId = m.Id, userId, role = r });
            return null;
        });

    /// <summary>Records who came: Invited, Attended or Absent. <paramref name="expectedVersion"/> is the attendee row's.</summary>
    public Task<ServiceResult<CabAttendeeView>> SetAttendanceAsync(string meetingId, string userId, string attendance, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var a = await db.Set<CabAttendees>().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.UserId == userId, ct);
            if (a is null) return ServiceResult<CabAttendeeView>.NotFound("attendee");
            if (await ClosedAsync(db, meetingId, "roll", ct) is { } closed) return ServiceResult<CabAttendeeView>.Fail(closed);
            var chair = await db.Set<CabMeetings>().AsNoTracking().Where(m => m.Id == meetingId).Select(m => m.ChairUserId).SingleAsync(ct);
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<CabAttendeeView>.Conflict(await AttendeeViewAsync(db, a, chair, ct));
            if (!CabAttendance.All.Contains(attendance)) return ServiceResult<CabAttendeeView>.Fail(Bad("Attendance must be Invited, Attended or Absent"));
            var before = new { a.Attendance, a.Version };
            a.Attendance = attendance; a.Version++; a.LastChangedByUserId = actor.UserId; a.LastChangedAt = Now;
            Audit(db, actor, null, "CabAttendee", $"{meetingId}:{userId}", "Attendance", before, new { a.Attendance, a.Version });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CabAttendeeView>.Ok(await AttendeeViewAsync(db, a, chair, ct));
        }, ct);

    private static async Task<CabAttendeeView> AttendeeViewAsync(ReleaseDbContext db, CabAttendees a, string chair, CancellationToken ct)
    {
        var name = await db.Set<Users>().AsNoTracking().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        return new CabAttendeeView(new NamedRef(a.UserId, name ?? a.UserId), a.AttendeeRole, a.Attendance, a.UserId == chair, a.Version);
    }

    /// <summary>Takes someone off the roll. The chair stays: change the chair first.</summary>
    public Task<ServiceResult<CabMeetingView>> RemoveAttendeeAsync(string meetingId, string userId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        Mutate(meetingId, actor, expectedVersion, "RemoveAttendee", ct, async (db, m, now) =>
        {
            if (m.Status is CabMeetingStatuses.Completed or CabMeetingStatuses.Cancelled) return new GuardFailure(CabGuards.CabMeetingClosed, $"The roll of a {m.Status} meeting is closed");
            if (userId == m.ChairUserId) return new GuardFailure(CabGuards.CabMeetingInvalid, "The chair stays on the roll; make someone else the chair first");
            var a = await db.Set<CabAttendees>().SingleOrDefaultAsync(x => x.MeetingId == m.Id && x.UserId == userId, ct);
            if (a is null) return new GuardFailure(CabGuards.CabMeetingInvalid, "That person is not on the roll");
            db.Set<CabAttendees>().Remove(a);
            Audit(db, actor, null, "CabAttendee", $"{m.Id}:{userId}", "Remove", new { meetingId = m.Id, userId, a.AttendeeRole, a.Attendance }, null);
            return null;
        });

    // ---- live push -------------------------------------------------------------------------------------------------------------------

    private async Task<ServiceResult<CabMeetingView>> Announce(Task<ServiceResult<CabMeetingView>> task, Func<ReleaseDbContext, string> workspaceOf)
    {
        // The workspace is the caller's: read it from a fresh context (scoped like the call), which is cheap and keeps the transaction body free of capture state.
        var r = await task;
        if (r.IsOk && _realtime is not null)
        {
            await using var db = await OpenAsync(default);
            await _realtime.ModuleEventAsync(workspaceOf(db), ModuleKeys.Cab, ChangeRequestService.ModuleEvent, [r.Value!.Id, r.Value.Version]);
        }
        return r;
    }
}
