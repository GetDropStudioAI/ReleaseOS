using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Train status changes (PROJECT_SCOPE §3). Every guard mirrors a trigger so callers get a readable 422 first.</summary>
public sealed class TrainLifecycleService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] CloseCodes = ["Successful", "SuccessfulWithIssues", "Unsuccessful"];

    /// <summary>Guards for moving a train to <paramref name="to"/>. Also drives GET /trains/{id}/readiness (M2), so both agree by construction.</summary>
    public async Task<List<GuardFailure>> EvaluateAsync(ReleaseDbContext db, ReleaseTrains t, string to, string? closeCode = null)
    {
        var failures = new List<GuardFailure>();
        if (!Transitions.TrainLegal(t.CurrentStatus, to))
        {
            failures.Add(new(Guards.IllegalTransition, $"Illegal train status transition {t.CurrentStatus} -> {to}"));
            return failures;
        }
        if (to is "Gated" or "Executing" or "Complete")
        {
            var blocking = await db.Set<StageGates>()
                .Where(g => g.ReleaseTrainId == t.Id && g.Status != "Certified" && g.Status != "Waived").ToListAsync();
            var names = blocking.Where(g => Transitions.Rank(g.RequiredBeforeStatus) <= Transitions.Rank(to)).Select(g => g.GateName).ToList();
            if (names.Count > 0) failures.Add(new(Guards.GateLockout, "Gate lockout: an uncertified gate blocks this transition", names));
        }
        if (t.CurrentStatus == "Gated" && to == "Executing")
        {
            var latest = await db.Set<GoNoGoDecisions>().Where(d => d.ReleaseTrainId == t.Id).OrderByDescending(d => d.DecidedAt).FirstOrDefaultAsync();
            if (latest is null || latest.Decision == "NoGo")
                failures.Add(new(Guards.ExecutingRequiresGo, "Executing requires a recorded Go decision"));

            var now = Now;
            var expired = await (from c in db.Set<GoNoGoConditions>()
                                 join d in db.Set<GoNoGoDecisions>() on c.DecisionId equals d.Id
                                 where d.ReleaseTrainId == t.Id && c.ClosedAt == null && c.ExpiresAt <= now
                                 select c.Text).ToListAsync();
            if (expired.Count > 0) failures.Add(new(Guards.ConditionExpired, "A Go/No-Go condition expired without being closed", expired));

            if (!await db.Set<Baselines>().AnyAsync(b => b.ReleaseTrainId == t.Id))
                failures.Add(new(Guards.ExecutingRequiresBaseline, "Executing requires a captured baseline"));
            if (t.RiskTier is "High" or "VeryHigh" && t.RollbackRehearsedAt is null)
                failures.Add(new(Guards.RollbackNotRehearsed, "High-risk train requires a rehearsed rollback"));
        }
        if (to == "Complete" && (closeCode is null || !CloseCodes.Contains(closeCode)))
            failures.Add(new(Guards.CloseCodeRequired, "Complete requires a close code", CloseCodes));
        return failures;
    }

    public Task<ServiceResult<ReleaseTrains>> AdvanceAsync(string trainId, string to, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        ChangeStatusAsync(trainId, to, actor, expectedVersion, closeCode: null, notes: null, "Advance", ct);

    public Task<ServiceResult<ReleaseTrains>> AbortAsync(string trainId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        ChangeStatusAsync(trainId, "Aborted", actor, expectedVersion, null, null, "Abort", ct);

    public Task<ServiceResult<ReleaseTrains>> CompleteAsync(string trainId, string closeCode, string? notes, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        ChangeStatusAsync(trainId, "Complete", actor, expectedVersion, closeCode, notes, "Complete", ct);

    private Task<ServiceResult<ReleaseTrains>> ChangeStatusAsync(string trainId, string to, Actor actor, int? expectedVersion, string? closeCode, string? notes, string action, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseTrains>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseTrains>.Conflict(t);
            var failures = await EvaluateAsync(db, t, to, closeCode);
            if (failures.Count > 0) return ServiceResult<ReleaseTrains>.Fail(failures);

            var before = new { status = t.CurrentStatus, version = t.Version };
            var now = Now;
            if (t.CurrentStatus == "Gated" && to == "Executing") t.ActualStartAt = now;
            if (to == "Complete") { t.CloseCode = closeCode; t.CloseNotes = notes; t.ActualEndAt = now; }
            t.CurrentStatus = to;
            Stamp(t, actor, now);
            Audit(db, actor, t.Id, "ReleaseTrain", t.Id, action, before, new { status = t.CurrentStatus, version = t.Version, closeCode });
            await db.SaveChangesAsync(ct);
            await db.Entry(t).ReloadAsync(ct);
            return ServiceResult<ReleaseTrains>.Ok(t);
        }, ct);

    private static void Stamp(ReleaseTrains t, Actor actor, DateTime now)
    {
        t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now; t.UpdatedAt = now; t.Version++;
    }

    /// <summary>Records the rollback rehearsal attestation (needed for High/VeryHigh trains before Executing).</summary>
    public Task<ServiceResult<ReleaseTrains>> RecordRollbackRehearsedAsync(string trainId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseTrains>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseTrains>.Conflict(t);
            var now = Now;
            t.RollbackRehearsedAt = now; t.RollbackRehearsedByUserId = actor.UserId;
            Stamp(t, actor, now);
            Audit(db, actor, t.Id, "ReleaseTrain", t.Id, "RollbackRehearsed", null, new { at = now });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTrains>.Ok(t);
        }, ct);
}
