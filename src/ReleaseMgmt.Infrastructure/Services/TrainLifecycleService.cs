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

    private static readonly string[] HopPath = ["Planning", "Gated", "Executing", "Complete"];

    /// <summary>
    /// How many guards block each train's next hop (Planning to Gated, Gated to Executing, Executing to Complete): the same count as
    /// <c>EvaluateAsync(t, next).Count</c> per train, computed with five set-based queries for all trains instead of about six queries per train
    /// (the Stream lists every train, and the per-train form took ~230 ms for 200 trains on its own; REOS-52). The equivalence with EvaluateAsync is
    /// asserted by a test, so a guard added to EvaluateAsync must be added here too. Trains that are Complete, Aborted or unknown count 0.
    /// </summary>
    public async Task<Dictionary<string, int>> NextHopBlockerCountsAsync(ReleaseDbContext db, IReadOnlyCollection<ReleaseTrains> trains, CancellationToken ct = default)
    {
        var counts = trains.ToDictionary(t => t.Id, _ => 0);
        var hop = trains.Select(t => (Train: t, From: Array.IndexOf(HopPath, t.CurrentStatus))).Where(x => x.From is >= 0 and < 3).ToList();
        if (hop.Count == 0) return counts;
        var ids = hop.Select(x => x.Train.Id).ToList();
        var gatedIds = hop.Where(x => x.Train.CurrentStatus == "Gated").Select(x => x.Train.Id).ToList();

        var open = (await db.Set<StageGates>().AsNoTracking().Where(g => ids.Contains(g.ReleaseTrainId) && g.Status != "Certified" && g.Status != "Waived")
            .Select(g => new { g.ReleaseTrainId, g.RequiredBeforeStatus }).ToListAsync(ct)).ToLookup(g => g.ReleaseTrainId, g => g.RequiredBeforeStatus);
        var latest = new Dictionary<string, string>();
        var expired = new HashSet<string>();
        var baselined = new HashSet<string>();
        if (gatedIds.Count > 0)
        {
            foreach (var d in (await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => gatedIds.Contains(d.ReleaseTrainId)).Select(d => new { d.ReleaseTrainId, d.Decision, d.DecidedAt }).ToListAsync(ct))
                .OrderByDescending(d => d.DecidedAt))
                latest.TryAdd(d.ReleaseTrainId, d.Decision);   // newest first: the first per train is the latest decision
            var now = Now;
            expired = [.. await (from c in db.Set<GoNoGoConditions>().AsNoTracking()
                                 join d in db.Set<GoNoGoDecisions>().AsNoTracking() on c.DecisionId equals d.Id
                                 where gatedIds.Contains(d.ReleaseTrainId) && c.ClosedAt == null && c.ExpiresAt <= now
                                 select d.ReleaseTrainId).Distinct().ToListAsync(ct)];
            baselined = [.. await db.Set<Baselines>().AsNoTracking().Where(b => gatedIds.Contains(b.ReleaseTrainId)).Select(b => b.ReleaseTrainId).ToListAsync(ct)];
        }

        foreach (var (t, from) in hop)
        {
            var to = HopPath[from + 1];
            var n = 0;
            if (!Transitions.TrainLegal(t.CurrentStatus, to)) { counts[t.Id] = 1; continue; }
            if (open[t.Id].Any(req => Transitions.Rank(req) <= Transitions.Rank(to))) n++;                       // gate lockout
            if (t.CurrentStatus == "Gated" && to == "Executing")
            {
                if (!latest.TryGetValue(t.Id, out var decision) || decision == "NoGo") n++;                        // needs a recorded Go
                if (expired.Contains(t.Id)) n++;                                                                    // a Go/No-Go condition expired
                if (!baselined.Contains(t.Id)) n++;                                                                 // needs a captured baseline
                if (t.RiskTier is "High" or "VeryHigh" && t.RollbackRehearsedAt is null) n++;                       // needs a rehearsed rollback
            }
            counts[t.Id] = n;
        }
        return counts;
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

    /// <summary>
    /// Records the rollback-rehearsal attestation (needed for High/VeryHigh trains before Executing). The attester is the acting RTE/Release Manager;
    /// <paramref name="rehearsalRunId"/> optionally links the ended Rehearsal run that is the evidence, and <paramref name="note"/> says what was rehearsed.
    /// Both go into the audit row (Q-036a). Attesting again replaces the earlier attestation; the audit row keeps what it replaced.
    /// </summary>
    public Task<ServiceResult<ReleaseTrains>> RecordRollbackRehearsedAsync(string trainId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RecordRollbackRehearsedAsync(trainId, actor, expectedVersion, null, null, ct);

    public Task<ServiceResult<ReleaseTrains>> RecordRollbackRehearsedAsync(string trainId, Actor actor, int? expectedVersion, string? rehearsalRunId, string? note, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseTrains>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseTrains>.Conflict(t);
            if (t.CurrentStatus is "Complete" or "Aborted")
                return ServiceResult<ReleaseTrains>.Fail(new GuardFailure(Guards.TrainClosed, $"The train is {t.CurrentStatus}; a rollback rehearsal can no longer be attested"));
            if (!string.IsNullOrWhiteSpace(rehearsalRunId))
            {
                var run = await db.Set<RunbookRuns>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == rehearsalRunId, ct);
                if (run is null || run.ReleaseTrainId != trainId || run.Mode != "Rehearsal")
                    return ServiceResult<ReleaseTrains>.Fail(new GuardFailure(CloseoutGuards.InvalidRehearsalRun, "The evidence must be a Rehearsal run of this train"));
                if (run.EndedAt is null)
                    return ServiceResult<ReleaseTrains>.Fail(new GuardFailure(CloseoutGuards.InvalidRehearsalRun, "The rehearsal run is still open; end it before attesting"));
            }
            var now = Now;
            var before = t.RollbackRehearsedAt is null ? null : new { at = t.RollbackRehearsedAt, byUserId = t.RollbackRehearsedByUserId };
            t.RollbackRehearsedAt = now; t.RollbackRehearsedByUserId = actor.UserId;
            Stamp(t, actor, now);
            Audit(db, actor, t.Id, "ReleaseTrain", t.Id, "RollbackRehearsed", before,
                new { at = now, runId = string.IsNullOrWhiteSpace(rehearsalRunId) ? null : rehearsalRunId, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTrains>.Ok(t);
        }, ct);
}
