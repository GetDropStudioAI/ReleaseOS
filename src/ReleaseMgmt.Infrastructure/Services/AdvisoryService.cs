using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>What a train's header shows beside its readiness: never a guard, so readiness and :advance stay the same list (REOS-85, REOS-88).</summary>
public sealed record TrainAdvisories(ContingencyView? Contingency, IReadOnlyList<WindowCollision> Collisions);

/// <summary>
/// Advisories for open trains (Planning, Gated, Executing; not archived): the runbook's contingency before the rollback deadline (REOS-88,
/// <see cref="Contingency"/>, config <c>Runbook:MinContingencyPct</c>, default 30) and deployment-window collisions with other open trains that share a
/// CI or a product (REOS-85, <see cref="WindowCollisions"/>, config <c>Calendar:CollisionBufferMin</c>, default 30). A setting outside its range is an
/// error at start-up, never a silent default (rule 8). Read-only.
/// </summary>
public sealed class AdvisoryService(IDbContextFactory<ReleaseDbContext> dbf, IConfiguration? config = null)
{
    public static readonly string[] OpenStatuses = ["Planning", "Gated", "Executing"];
    public int MinContingencyPct { get; } = Setting(config, "Runbook:MinContingencyPct", 30, 0, 100);
    public int CollisionBufferMin { get; } = Setting(config, "Calendar:CollisionBufferMin", 30, 0, 1440);

    public static int Setting(IConfiguration? config, string key, int fallback, int min, int max)
    {
        var raw = config?[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw, out var n) && n >= min && n <= max) return n;
        throw new InvalidOperationException($"{key} '{raw}' must be a whole number from {min} to {max}");
    }

    /// <summary>Null for an unknown train. A Complete, Aborted or archived train has nothing to warn about.</summary>
    public async Task<TrainAdvisories?> GetAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        if (!OpenStatuses.Contains(t.CurrentStatus) || t.ArchivedAt is not null) return new(null, []);
        var w = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
        var steps = await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == trainId)
            .Select(s => new PlanStep(s.Section, s.PlannedStartAt, s.PlannedDurationMin)).ToListAsync(ct);
        var contingency = Contingency.Compute(w?.StartsAt, w?.EndsAt, steps, MinContingencyPct);
        var collisions = w is null ? [] : (await CollisionsAsync(db, [trainId], ct)).GetValueOrDefault(trainId) ?? [];
        return new(contingency, collisions);
    }

    /// <summary>Collisions of every open train with a window (or only of <paramref name="onlyFor"/>), keyed by train id.</summary>
    public async Task<Dictionary<string, IReadOnlyList<WindowCollision>>> CollisionsAsync(ReleaseDbContext db, IReadOnlyCollection<string>? onlyFor, CancellationToken ct = default)
    {
        var rows = await (from t in db.Set<ReleaseTrains>().AsNoTracking()
                          join w in db.Set<DeploymentWindows>() on t.Id equals w.ReleaseTrainId
                          where t.ArchivedAt == null && OpenStatuses.Contains(t.CurrentStatus)
                          select new { t.Id, t.Title, t.CurrentStatus, w.StartsAt, w.EndsAt }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var cis = (await db.Set<AffectedCIs>().AsNoTracking().Where(c => ids.Contains(c.ReleaseTrainId)).Select(c => new { c.ReleaseTrainId, c.CiName }).ToListAsync(ct))
            .ToLookup(c => c.ReleaseTrainId, c => c.CiName);
        var products = (await db.Set<BundledProducts>().AsNoTracking().Where(p => ids.Contains(p.ReleaseTrainId)).Select(p => new { p.ReleaseTrainId, p.ProductName, p.ProjectCode }).ToListAsync(ct))
            .ToLookup(p => p.ReleaseTrainId, p => (p.ProductName, p.ProjectCode));
        var all = rows.Select(r => new WindowedTrain(r.Id, r.Title, r.CurrentStatus, r.StartsAt, r.EndsAt, [.. cis[r.Id]], [.. products[r.Id].Select(p => (p.ProductName, (string?)p.ProjectCode))])).ToList();
        return all.Where(x => onlyFor is null || onlyFor.Contains(x.TrainId)).ToDictionary(x => x.TrainId, x => WindowCollisions.Find(x, all, CollisionBufferMin));
    }

    public async Task<Dictionary<string, IReadOnlyList<WindowCollision>>> CollisionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await CollisionsAsync(db, null, ct);
    }
}
