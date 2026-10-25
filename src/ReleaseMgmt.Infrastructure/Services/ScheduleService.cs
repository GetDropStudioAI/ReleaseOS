using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Changing TargetReleaseDate recomputes every gate's DueOn in business days (D9) and audits the change. A release in a version (RT2) also hints the rest of its chain to refresh: the advisory against a later release is derived from the dates.</summary>
public sealed class ScheduleService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public Task<ServiceResult<ReleaseTrains>> ChangeTargetDateAsync(string trainId, DateOnly newTarget, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseTrains>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseTrains>.Conflict(t);

            var holidays = (await db.Set<Holidays>().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
            var gates = await db.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
            var before = new { target = t.TargetReleaseDate.ToString("yyyy-MM-dd"), dueOn = gates.ToDictionary(g => g.GateName, g => g.DueOn.ToString("yyyy-MM-dd")) };
            var now = Now;
            var oldTarget = t.TargetReleaseDate;
            t.TargetReleaseDate = newTarget;
            foreach (var g in gates)
            {
                g.DueOn = BusinessDays.SubtractBusinessDays(newTarget, g.OffsetDays, holidays);
                g.Version++; // no Status change, so no trigger fires; LastChanged* stay untouched
            }
            // RT4: every On-Demand client's offered date moves with the Standard date; a chosen date stays where the client put it.
            var moved = await ClientScheduleService.ShiftOfferedAsync(db, trainId, oldTarget, newTarget, now, actor.UserId,
                (d, was, nowDate) => Audit(db, actor, trainId, ClientScheduleService.Entity, d.Id, "OfferedMoved", new { offeredDate = was }, new { offeredDate = nowDate, d.Version }), ct);
            t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now; t.UpdatedAt = now; t.Version++;
            Audit(db, actor, trainId, "ReleaseTrain", trainId, "ChangeTargetDate", before,
                new { target = newTarget.ToString("yyyy-MM-dd"), dueOn = gates.ToDictionary(g => g.GateName, g => g.DueOn.ToString("yyyy-MM-dd")), clientOfferedDatesMoved = moved == 0 ? (int?)null : moved });
            if (t.ReleaseVersionId is not null) await ReleaseVersionService.TouchChainAsync(db, t.ReleaseVersionId, ct);   // RT2: a later release of the chain may now come too soon; its screen refetches
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTrains>.Ok(t);
        }, ct);
}
