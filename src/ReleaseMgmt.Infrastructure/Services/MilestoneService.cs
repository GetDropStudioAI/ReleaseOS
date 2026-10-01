using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>A milestone as the workspace shows it. <c>TMinus</c> is business days from <c>DueOn</c> to the train's target (the gate timeline's axis).</summary>
public sealed record MilestoneView(string Id, string TrainId, string Name, string DueOn, int TMinus, string? OwnerUserId, string? OwnerTeamId, string? OwnerName,
    string? Note, bool Done, string? DoneAt, string? DoneByUserId, string? DoneBy, string? LastChangedByUserId, string? LastChangedAt, int Version);

/// <summary>New milestone. Owner is optional: a person, a team, or nobody (Q-0841).</summary>
public sealed record NewMilestone(string? Name, DateOnly? DueOn, string? OwnerUserId, string? OwnerTeamId, string? Note);

/// <summary>Edit. Null leaves a field as it is; <c>Note = ""</c> clears the note; <c>ClearOwner</c> removes the owner; a new owner replaces the old one.</summary>
public sealed record MilestonePatch(string? Name, DateOnly? DueOn, string? OwnerUserId, string? OwnerTeamId, bool? ClearOwner, string? Note);

/// <summary>
/// Train milestones (decision 2026-10-01, Q-0840..Q-0846): named key dates on a train. Informational: never certified, never a guard on a gate or train move.
/// Every write is one transaction with one AuditEvents row (entity "Milestone"), stamps LastChangedByUserId/LastChangedAt from the service clock and bumps Version;
/// edits, done/undone and delete check If-Match (409 with the current row). Done/undone repeat as no-ops, as tasks do (SEC-A2).
/// </summary>
public sealed class MilestoneService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "Milestone";

    /// <summary>Null when the train does not exist. Ordered by due date, then name.</summary>
    public async Task<List<MilestoneView>?> ListAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainId, ct);
        if (train is null) return null;
        var rows = await db.Set<TrainMilestones>().AsNoTracking().Where(m => m.ReleaseTrainId == trainId).ToListAsync(ct);
        return await ViewsAsync(db, train, rows, ct);
    }

    public async Task<MilestoneView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var m = await db.Set<TrainMilestones>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;
        var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleAsync(t => t.Id == m.ReleaseTrainId, ct);
        return (await ViewsAsync(db, train, [m], ct))[0];
    }

    private static async Task<List<MilestoneView>> ViewsAsync(ReleaseDbContext db, ReleaseTrains train, List<TrainMilestones> rows, CancellationToken ct)
    {
        var holidays = (await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        var userIds = rows.SelectMany(m => new[] { m.OwnerUserId, m.DoneByUserId }).OfType<string>().Distinct().ToList();
        var teamIds = rows.Select(m => m.OwnerTeamId).OfType<string>().Distinct().ToList();
        var people = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var teams = await db.Set<Teams>().AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return [.. rows.OrderBy(m => m.DueOn).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => new MilestoneView(
            m.Id, m.ReleaseTrainId, m.Name, m.DueOn.ToString("yyyy-MM-dd"), BusinessDays.Between(m.DueOn, train.TargetReleaseDate, holidays),
            m.OwnerUserId, m.OwnerTeamId,
            m.OwnerUserId is not null ? people.GetValueOrDefault(m.OwnerUserId) : m.OwnerTeamId is not null ? teams.GetValueOrDefault(m.OwnerTeamId) : null,
            m.Note, m.IsDone, Iso(m.DoneAt), m.DoneByUserId, m.DoneByUserId is not null ? people.GetValueOrDefault(m.DoneByUserId) : null,
            m.LastChangedByUserId, Iso(m.LastChangedAt), m.Version))];
    }

    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static async Task<GuardFailure?> OwnerFailure(ReleaseDbContext db, string? userId, string? teamId, CancellationToken ct)
    {
        if (userId is not null && !await db.Set<Users>().AnyAsync(u => u.Id == userId && u.IsActive, ct))
            return new(MilestoneGuards.InvalidMilestone, "The owner is not an active user");
        if (teamId is not null && !await db.Set<Teams>().AnyAsync(t => t.Id == teamId, ct))
            return new(MilestoneGuards.InvalidMilestone, "The owning team does not exist");
        return null;
    }

    private static object Snapshot(TrainMilestones m) => new
    {
        name = m.Name, dueOn = m.DueOn.ToString("yyyy-MM-dd"), ownerUserId = m.OwnerUserId, ownerTeamId = m.OwnerTeamId, note = m.Note, done = m.IsDone, version = m.Version,
    };

    private void Stamp(TrainMilestones m, Actor actor, DateTime now, bool bump = true)
    {
        m.LastChangedByUserId = actor.UserId; m.LastChangedAt = now;
        if (bump) m.Version++;
    }

    public Task<ServiceResult<TrainMilestones>> AddAsync(string trainId, NewMilestone n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return ServiceResult<TrainMilestones>.NotFound("train");
            if (n.DueOn is null) return ServiceResult<TrainMilestones>.Fail(new GuardFailure(MilestoneGuards.InvalidMilestone, "A milestone needs a date"));
            var f = MilestoneRules.Validate(n.Name, n.Note, n.OwnerUserId, n.OwnerTeamId) ?? await OwnerFailure(db, n.OwnerUserId, n.OwnerTeamId, ct);
            if (f is not null) return ServiceResult<TrainMilestones>.Fail(f);
            var m = new TrainMilestones
            {
                Id = Ids.New(), ReleaseTrainId = trainId, Name = n.Name!.Trim(), DueOn = n.DueOn.Value, OwnerUserId = n.OwnerUserId, OwnerTeamId = n.OwnerTeamId,
                Note = MilestoneRules.NormaliseNote(n.Note),
            };
            Stamp(m, actor, Now, bump: false);
            db.Set<TrainMilestones>().Add(m);
            Audit(db, actor, trainId, Entity, m.Id, "Add", null, Snapshot(m));
            await db.SaveChangesAsync(ct);
            return ServiceResult<TrainMilestones>.Ok(m);
        }, ct);

    public Task<ServiceResult<TrainMilestones>> UpdateAsync(string id, MilestonePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var m = await db.Set<TrainMilestones>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return ServiceResult<TrainMilestones>.NotFound("milestone");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<TrainMilestones>.Conflict(m);
            if (p.ClearOwner == true && (p.OwnerUserId is not null || p.OwnerTeamId is not null))
                return ServiceResult<TrainMilestones>.Fail(new GuardFailure(MilestoneGuards.InvalidMilestone, "Either clear the owner or name a new one, not both"));
            var (ownerUser, ownerTeam) = p.ClearOwner == true ? (null, null)
                : p.OwnerUserId is not null || p.OwnerTeamId is not null ? (p.OwnerUserId, p.OwnerTeamId) : (m.OwnerUserId, m.OwnerTeamId);
            var name = p.Name ?? m.Name;
            var note = p.Note is null ? m.Note : MilestoneRules.NormaliseNote(p.Note);
            var f = MilestoneRules.Validate(name, note, ownerUser, ownerTeam) ?? await OwnerFailure(db, ownerUser, ownerTeam, ct);
            if (f is not null) return ServiceResult<TrainMilestones>.Fail(f);
            var before = Snapshot(m);
            var (newName, newDue) = (name.Trim(), p.DueOn ?? m.DueOn);
            if (newName == m.Name && newDue == m.DueOn && ownerUser == m.OwnerUserId && ownerTeam == m.OwnerTeamId && note == m.Note)
                return ServiceResult<TrainMilestones>.Ok(m);   // nothing to change: no Version bump, no audit row
            m.Name = newName; m.DueOn = newDue; m.OwnerUserId = ownerUser; m.OwnerTeamId = ownerTeam; m.Note = note;
            Stamp(m, actor, Now);
            Audit(db, actor, m.ReleaseTrainId, Entity, m.Id, "Update", before, Snapshot(m));
            await db.SaveChangesAsync(ct);
            return ServiceResult<TrainMilestones>.Ok(m);
        }, ct);

    public Task<ServiceResult<TrainMilestones>> MarkDoneAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetDoneAsync(id, true, actor, expectedVersion, ct);

    public Task<ServiceResult<TrainMilestones>> MarkUndoneAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetDoneAsync(id, false, actor, expectedVersion, ct);

    private Task<ServiceResult<TrainMilestones>> SetDoneAsync(string id, bool done, Actor actor, int? expectedVersion, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var m = await db.Set<TrainMilestones>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return ServiceResult<TrainMilestones>.NotFound("milestone");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<TrainMilestones>.Conflict(m);
            if (m.IsDone == done) return ServiceResult<TrainMilestones>.Ok(m);   // repeat is a no-op (keeps who marked it done)
            var now = Now;
            m.IsDone = done; m.DoneAt = done ? now : null; m.DoneByUserId = done ? actor.UserId : null;
            Stamp(m, actor, now);
            Audit(db, actor, m.ReleaseTrainId, Entity, m.Id, done ? "Done" : "Undone", null, new { done, version = m.Version });
            await db.SaveChangesAsync(ct);
            return ServiceResult<TrainMilestones>.Ok(m);
        }, ct);

    /// <summary>Removes the row. The audit row keeps what it was (BeforeJson), since the milestone itself is gone.</summary>
    public Task<ServiceResult<object>> DeleteAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var m = await db.Set<TrainMilestones>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return ServiceResult<object>.NotFound("milestone");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<object>.Conflict(m);
            Audit(db, actor, m.ReleaseTrainId, Entity, m.Id, "Delete", Snapshot(m), null);
            db.Set<TrainMilestones>().Remove(m);
            await db.SaveChangesAsync(ct);
            return ServiceResult<object>.Ok(new { id = m.Id, deleted = true });
        }, ct);
}
