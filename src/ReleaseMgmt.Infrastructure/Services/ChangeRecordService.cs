using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ChangeRecordPatch(string? Justification, string? ImplementationPlan, string? RiskImpactAnalysis, string? BackoutPlan, string? TestPlan, string? CommunicationPlan, DateOnly? CabDate);

/// <summary>The audit field set (1:1 with the train) and the affected configuration items. Edits are RTE/RM work; a closed train is read-only.</summary>
public sealed class ChangeRecordService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public async Task<ChangeRecords?> GetAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.Set<ChangeRecords>().AsNoTracking().SingleOrDefaultAsync(c => c.ReleaseTrainId == trainId, ct);
    }

    public async Task<List<AffectedCIs>> ListCisAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.Set<AffectedCIs>().AsNoTracking().Where(c => c.ReleaseTrainId == trainId).OrderBy(c => c.CiName).ToListAsync(ct);
    }

    /// <summary>Full replace of the fields (PUT); creates the row on first save. Blank text is stored as null.</summary>
    public Task<ServiceResult<ChangeRecords>> SaveAsync(string trainId, ChangeRecordPatch p, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ChangeRecords>.NotFound("train");
            if (t.CurrentStatus is "Complete" or "Aborted")
                return ServiceResult<ChangeRecords>.Fail(new GuardFailure(Guards.TrainClosed, $"The train is {t.CurrentStatus}; the change record is read-only"));
            var c = await db.Set<ChangeRecords>().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
            if (c is not null && VersionMismatch(expectedVersion, c.Version)) return ServiceResult<ChangeRecords>.Conflict(c);
            var isNew = c is null;
            var before = c is null ? null : Snapshot(c);
            c ??= new ChangeRecords { ReleaseTrainId = trainId };
            c.Justification = Clean(p.Justification); c.ImplementationPlan = Clean(p.ImplementationPlan); c.RiskImpactAnalysis = Clean(p.RiskImpactAnalysis);
            c.BackoutPlan = Clean(p.BackoutPlan); c.TestPlan = Clean(p.TestPlan); c.CommunicationPlan = Clean(p.CommunicationPlan); c.CabDate = p.CabDate;
            if (isNew) db.Set<ChangeRecords>().Add(c); else c.Version++;
            Audit(db, actor, trainId, "ChangeRecord", trainId, isNew ? "Create" : "Update", before, Snapshot(c));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChangeRecords>.Ok(c);
        }, ct);

    public Task<ServiceResult<AffectedCIs>> AddCiAsync(string trainId, string ciName, string? externalId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<AffectedCIs>.NotFound("train");
            var name = (ciName ?? "").Trim();
            if (name.Length == 0) return ServiceResult<AffectedCIs>.Fail(new GuardFailure(Guards.InvalidCi, "A configuration item needs a name"));
            if (t.CurrentStatus is "Complete" or "Aborted") return ServiceResult<AffectedCIs>.Fail(new GuardFailure(Guards.TrainClosed, $"The train is {t.CurrentStatus}"));
            if (await db.Set<AffectedCIs>().AnyAsync(x => x.ReleaseTrainId == trainId && x.CiName == name, ct))
                return ServiceResult<AffectedCIs>.Fail(new GuardFailure(Guards.DuplicateCi, $"{name} is already listed"));
            var ci = new AffectedCIs { Id = Ids.New(), ReleaseTrainId = trainId, CiName = name, CiExternalId = Clean(externalId) };
            db.Set<AffectedCIs>().Add(ci);
            Audit(db, actor, trainId, "AffectedCI", ci.Id, "Add", null, new { name, externalId = ci.CiExternalId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<AffectedCIs>.Ok(ci);
        }, ct);

    public Task<ServiceResult<AffectedCIs>> RemoveCiAsync(string trainId, string ciId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var ci = await db.Set<AffectedCIs>().SingleOrDefaultAsync(x => x.Id == ciId && x.ReleaseTrainId == trainId, ct);
            if (ci is null) return ServiceResult<AffectedCIs>.NotFound("configuration item");
            var status = await db.Set<ReleaseTrains>().Where(x => x.Id == trainId).Select(x => x.CurrentStatus).SingleAsync(ct);
            if (status is "Complete" or "Aborted") return ServiceResult<AffectedCIs>.Fail(new GuardFailure(Guards.TrainClosed, $"The train is {status}"));
            db.Set<AffectedCIs>().Remove(ci);
            Audit(db, actor, trainId, "AffectedCI", ci.Id, "Remove", new { name = ci.CiName }, null);
            await db.SaveChangesAsync(ct);
            return ServiceResult<AffectedCIs>.Ok(ci);
        }, ct);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static object Snapshot(ChangeRecords c) => new { c.Justification, c.ImplementationPlan, c.RiskImpactAnalysis, c.BackoutPlan, c.TestPlan, c.CommunicationPlan, cabDate = c.CabDate?.ToString("yyyy-MM-dd") };
}
