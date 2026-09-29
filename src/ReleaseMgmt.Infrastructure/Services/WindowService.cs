using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>The deployment window (exactly one per train in v1). Setting it creates the row the first time, then edits it under If-Match.</summary>
public sealed class WindowService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public Task<ServiceResult<DeploymentWindows>> SetAsync(string trainId, DateTime startsAt, DateTime endsAt, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return ServiceResult<DeploymentWindows>.NotFound("train");
            var start = Whole(startsAt); var end = Whole(endsAt);
            if (end <= start) return ServiceResult<DeploymentWindows>.Fail(new GuardFailure(Guards.InvalidWindow, "The window must end after it starts"));

            var w = await db.Set<DeploymentWindows>().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
            if (w is null)
            {
                w = new DeploymentWindows { ReleaseTrainId = trainId, StartsAt = start, EndsAt = end };
                db.Set<DeploymentWindows>().Add(w);
                Audit(db, actor, trainId, "DeploymentWindow", w.Id, "Set", after: new { startsAt = start, endsAt = end });
            }
            else
            {
                if (VersionMismatch(expectedVersion, w.Version)) return ServiceResult<DeploymentWindows>.Conflict(w);
                var before = new { startsAt = w.StartsAt, endsAt = w.EndsAt, version = w.Version };
                w.StartsAt = start; w.EndsAt = end; w.Version++;
                Audit(db, actor, trainId, "DeploymentWindow", w.Id, "Change", before, new { startsAt = start, endsAt = end, version = w.Version });
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<DeploymentWindows>.Ok(w);
        }, ct);

    private static DateTime Whole(DateTime t)
    {
        var u = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
        return new DateTime(u.Ticks - u.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
