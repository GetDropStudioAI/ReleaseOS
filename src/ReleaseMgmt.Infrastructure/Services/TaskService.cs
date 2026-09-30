using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Checklist tasks. Reopening or adding a task decertifies a Certified gate via trigger (which writes its own audit row);
/// the service sets LastChangedByUserId/LastChangedAt so that row carries the right actor and time (D27).</summary>
public sealed class TaskService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public Task<ServiceResult<ChecklistTasks>> AddAsync(string gateId, string description, string? ownerUserId, string? ownerTeamId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<ChecklistTasks>.NotFound("gate");
            // Owner defaults to the gate owner (the parser flags this as a warning in M3); exactly one of user/team.
            var ownerUser = ownerUserId ?? (ownerTeamId is null ? g.OwnerUserId : null);
            var ownerTeam = ownerUser is null ? ownerTeamId ?? g.OwnerTeamId : null;
            var next = (await db.Set<ChecklistTasks>().Where(t => t.StageGateId == gateId).MaxAsync(t => (int?)t.SequenceOrder, ct) ?? 0) + 1;
            var t = new ChecklistTasks
            {
                Id = Ids.New(), StageGateId = gateId, TaskDescription = description, OwnerUserId = ownerUser, OwnerTeamId = ownerTeam,
                SequenceOrder = next, LastChangedByUserId = actor.UserId, LastChangedAt = Now,
            };
            db.Set<ChecklistTasks>().Add(t);
            Audit(db, actor, g.ReleaseTrainId, "ChecklistTask", t.Id, "Add", null, new { gateId, description });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChecklistTasks>.Ok(t);
        }, ct);

    public Task<ServiceResult<ChecklistTasks>> CompleteAsync(string taskId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        SetCompletedAsync(taskId, true, actor, expectedVersion, ct);

    public Task<ServiceResult<ChecklistTasks>> ReopenAsync(string taskId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        SetCompletedAsync(taskId, false, actor, expectedVersion, ct);

    private Task<ServiceResult<ChecklistTasks>> SetCompletedAsync(string taskId, bool completed, Actor actor, int? expectedVersion, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ChecklistTasks>().SingleOrDefaultAsync(x => x.Id == taskId, ct);
            if (t is null) return ServiceResult<ChecklistTasks>.NotFound("task");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ChecklistTasks>.Conflict(t);
            // SEC-A2: idempotent. Completing a completed task used to overwrite CompletedByUserId/CompletedAt (no decertify: IsCompleted did not change), which is the
            // column the Compliance segregation-of-duties rule reads; reopening an open task bumped Version and wrote an audit row for nothing. Neither changes anything now.
            if (t.IsCompleted == completed) return ServiceResult<ChecklistTasks>.Ok(t);
            var trainId = await db.Set<StageGates>().Where(g => g.Id == t.StageGateId).Select(g => g.ReleaseTrainId).SingleAsync(ct);
            var now = Now;
            t.IsCompleted = completed;
            t.CompletedAt = completed ? now : null;
            t.CompletedByUserId = completed ? actor.UserId : null;
            t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now; t.Version++;
            Audit(db, actor, trainId, "ChecklistTask", t.Id, completed ? "Complete" : "Reopen");
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChecklistTasks>.Ok(t);
        }, ct);
}
