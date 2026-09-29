using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// "What does this train still need to reach X?" Every hop from the current status to the target is evaluated with
/// <see cref="TrainLifecycleService.EvaluateAsync"/>, the same method <c>:advance</c> runs, so the readiness line and the
/// action can never disagree. Later hops are evaluated as if the earlier ones had already happened.
/// </summary>
public sealed class ReadinessService(IDbContextFactory<ReleaseDbContext> dbf, TrainLifecycleService lifecycle) : IReadinessService
{
    private static readonly string[] Path = ["Planning", "Gated", "Executing", "Complete"];

    public async Task<ServiceResult<Readiness>> GetAsync(string trainId, string target = "Executing", CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return ServiceResult<Readiness>.NotFound("train");

        var to = Array.IndexOf(Path, target);
        if (to < 1) return ServiceResult<Readiness>.Fail(new GuardFailure(Guards.IllegalTransition, $"'{target}' is not a status a train can be advanced to"));

        var blockers = new List<ReadinessBlocker>();
        var from = Array.IndexOf(Path, t.CurrentStatus);
        if (from < 0 || from >= to)
        {
            // Aborted, Complete, or already at/after the target: nothing to reach
            var already = from >= to && from >= 0;
            if (!already) blockers.Add(new("", new GuardFailure(Guards.IllegalTransition, $"A train that is {t.CurrentStatus} cannot be advanced")));
            return ServiceResult<Readiness>.Ok(new(t.Id, t.Version, t.CurrentStatus, target, already, blockers));
        }

        for (var i = from; i < to; i++)
        {
            var hop = Path[i + 1];
            var stand = Clone(t, Path[i]);   // evaluate the hop as if the train were already at its start
            var failures = await lifecycle.EvaluateAsync(db, stand, hop, closeCode: hop == "Complete" ? "Successful" : null); // a close code is chosen at completion, not a readiness item
            blockers.AddRange(failures.Select(f => new ReadinessBlocker(hop, f)));
        }
        return ServiceResult<Readiness>.Ok(new(t.Id, t.Version, t.CurrentStatus, target, blockers.Count == 0, blockers));
    }

    private static ReleaseTrains Clone(ReleaseTrains t, string status) => new()
    {
        Id = t.Id, CurrentStatus = status, RiskTier = t.RiskTier, RollbackRehearsedAt = t.RollbackRehearsedAt,
        TargetReleaseDate = t.TargetReleaseDate, Version = t.Version,
    };
}
