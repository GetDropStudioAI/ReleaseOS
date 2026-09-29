using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Manual "capture baseline" (D15) for trains without a *Freeze* gate. Baselines are immutable and one per train (D25).
/// The snapshot has the same shape the auto-capture trigger writes: products, gates + DueOn, steps + planned times.</summary>
public sealed class BaselineService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    public Task<ServiceResult<Baselines>> CaptureAsync(string trainId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<Baselines>.NotFound("train");
            if (await db.Set<Baselines>().AnyAsync(b => b.ReleaseTrainId == trainId, ct))
                return ServiceResult<Baselines>.Fail(new GuardFailure(Guards.BaselineExists, "This train already has a baseline; baselines are immutable (D25)"));

            var products = await db.Set<BundledProducts>().Where(p => p.ReleaseTrainId == trainId).OrderBy(p => p.ProductName).Select(p => new { name = p.ProductName, version = p.VersionTag }).ToListAsync(ct);
            var gates = (await db.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct)).Select(g => new { name = g.GateName, dueOn = g.DueOn.ToString("yyyy-MM-dd") });
            var steps = (await db.Set<RunbookSteps>().Where(s => s.ReleaseTrainId == trainId).OrderBy(s => s.StepCode).ToListAsync(ct))
                .Select(s => new { code = s.StepCode, plannedStart = s.PlannedStartAt.ToString(Persistence.UtcTextConverter.Format), plannedMin = s.PlannedDurationMin });
            var b = new Baselines
            {
                Id = Ids.New(), ReleaseTrainId = trainId, CapturedAt = Now, PlannedReleaseDate = t.TargetReleaseDate,
                SnapshotJson = JsonSerializer.Serialize(new { products, gates, steps }), CapturedByUserId = actor.UserId,
            };
            db.Set<Baselines>().Add(b);
            Audit(db, actor, trainId, "Baseline", trainId, "Capture", null, new { plannedReleaseDate = t.TargetReleaseDate.ToString("yyyy-MM-dd") });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Baselines>.Ok(b);
        }, ct);
}
