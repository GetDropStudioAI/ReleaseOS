using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Rehearsal and Live runs of a train's runbook, and the step actions inside them. Actuals live in StepExecutions and never touch the plan
/// (rule 7). The service checks each rule first for a readable 422; the triggers (Live needs an Executing train, dependencies Done, freeze
/// lockout) are the backstop. Step and run writes are audited and stamped with Version in the same transaction.
/// </summary>
public sealed class RunService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null, INotifier? notifier = null, IAlertSink? alerts = null)
    : ServiceBase(dbf, time, realtime)
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;
    private readonly IRealtimePublisher? _realtime = realtime;
    private readonly TimeProvider _time = time;
    /// <summary>Late escalation (PROJECT_SCOPE 3): a Live step that starts this late after its plan warns its owner and the RTEs; this late, it also pages the Release Managers.</summary>
    public const int WarnLateMin = 5, CriticalLateMin = 30;
    private sealed record StepEvent(string TrainId, string RunId, string Mode, string Action, string StepCode, string? OwnerUserId, string? OwnerTeamId, int LateMin);

    public static readonly string[] Outcomes = ["Completed", "RolledBack", "Aborted"];

    public Task<ServiceResult<RunbookRuns>> StartRunAsync(string trainId, string mode, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<RunbookRuns>.NotFound("train");
            if (mode is not ("Rehearsal" or "Live")) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.InvalidRun, "Mode must be Rehearsal or Live"));
            if (train.CurrentStatus is "Complete" or "Aborted") return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.TrainClosed, $"The train is {train.CurrentStatus}; it cannot be run"));
            if (mode == "Live")
            {
                if (train.CurrentStatus != "Executing")
                    return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.LiveRequiresExecuting, $"A live run needs the train to be Executing (it is {train.CurrentStatus})"));
                if (await db.Set<RunbookRuns>().AnyAsync(r => r.ReleaseTrainId == trainId && r.Mode == "Live" && r.EndedAt == null, ct))
                    return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.LiveRunOpen, "This train already has an open live run; end it first"));
            }
            var steps = await db.Set<RunbookSteps>().Where(s => s.ReleaseTrainId == trainId).ToListAsync(ct);
            if (steps.Count == 0) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.InvalidRun, "The runbook has no steps to run"));

            var run = new RunbookRuns { ReleaseTrainId = trainId, Mode = mode, StartedAt = Now, StartedByUserId = actor.UserId };
            db.Set<RunbookRuns>().Add(run);
            foreach (var s in steps) db.Set<StepExecutions>().Add(new StepExecutions { RunId = run.Id, StepId = s.Id });
            Audit(db, actor, trainId, "RunbookRun", run.Id, "Start", null, new { mode, steps = steps.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<RunbookRuns>.Ok(run);
        }, ct);

    public Task<ServiceResult<StepExecutions>> StartStepAsync(string runId, string stepId, string? note, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        StepAsync(runId, stepId, "Start", note, actor, expectedVersion, ct);
    public Task<ServiceResult<StepExecutions>> DoneStepAsync(string runId, string stepId, string? note, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        StepAsync(runId, stepId, "Done", note, actor, expectedVersion, ct);
    public Task<ServiceResult<StepExecutions>> FailStepAsync(string runId, string stepId, string? note, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        StepAsync(runId, stepId, "Fail", note, actor, expectedVersion, ct);
    public Task<ServiceResult<StepExecutions>> SkipStepAsync(string runId, string stepId, string? note, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        StepAsync(runId, stepId, "Skip", note, actor, expectedVersion, ct);

    private async Task<ServiceResult<StepExecutions>> StepAsync(string runId, string stepId, string action, string? note, Actor actor, int? expectedVersion, CancellationToken ct)
    {
        StepEvent? evt = null;
        var result = await StepCoreAsync(runId, stepId, action, note, actor, expectedVersion, e => evt = e, ct);
        if (result.IsOk && evt is not null) await AfterEventAsync(evt, ct);
        return result;
    }

    private Task<ServiceResult<StepExecutions>> StepCoreAsync(string runId, string stepId, string action, string? note, Actor actor, int? expectedVersion, Action<StepEvent> captured, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var run = await db.Set<RunbookRuns>().SingleOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null) return ServiceResult<StepExecutions>.NotFound("run");
            var step = await db.Set<RunbookSteps>().SingleOrDefaultAsync(s => s.Id == stepId && s.ReleaseTrainId == run.ReleaseTrainId, ct);
            if (step is null) return ServiceResult<StepExecutions>.NotFound("step");
            if (run.EndedAt is not null) return ServiceResult<StepExecutions>.Fail(new GuardFailure(Guards.RunEnded, "This run has ended"));

            var ex = await db.Set<StepExecutions>().SingleOrDefaultAsync(e => e.RunId == runId && e.StepId == stepId, ct);
            if (ex is null)   // a step added to the plan after this (rehearsal) run began
            {
                ex = new StepExecutions { RunId = runId, StepId = stepId };
                db.Set<StepExecutions>().Add(ex);
                await db.SaveChangesAsync(ct);
            }
            if (VersionMismatch(expectedVersion, ex.Version)) return ServiceResult<StepExecutions>.Conflict(ex);

            var (from, to) = action switch { "Start" => ("Scheduled", "Running"), "Done" => ("Running", "Done"), "Fail" => ("Running", "Failed"), _ => ("Scheduled", "Skipped") };
            if (ex.Status != from)
                return ServiceResult<StepExecutions>.Fail(new GuardFailure(Guards.IllegalStepTransition, $"Cannot {action.ToLowerInvariant()} step {step.StepCode}: it is {ex.Status} (a step must be {from} to be {action.ToLowerInvariant()}ed)"));
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (action == "Skip" && note is null)
                return ServiceResult<StepExecutions>.Fail(new GuardFailure(Guards.SkipNeedsNote, "Skipping a step needs a comment saying why"));
            if (action == "Start")
            {
                var blocked = await UnfinishedDependenciesAsync(db, runId, stepId, ct);
                if (blocked.Count > 0)
                    return ServiceResult<StepExecutions>.Fail(new GuardFailure(Guards.DependencyNotDone, $"Step {step.StepCode} cannot start until these are Done or Skipped: {string.Join(", ", blocked)}", blocked));
                var frozen = await FreezeService.FindBlockingAsync(db, step, run.Mode, Now, ct);   // readable 422 before trg_Step_FreezeLockout (the backstop)
                if (frozen is not null) return ServiceResult<StepExecutions>.Fail(FreezeService.LockoutFailure(frozen, step.StepCode));
            }

            var before = new { status = ex.Status, version = ex.Version };
            var now = Now;
            ex.Status = to; ex.ActorUserId = actor.UserId; ex.Version++;
            if (note is not null) ex.Note = note;
            if (action == "Start") ex.ActualStartAt = now;
            if (action is "Done" or "Fail") ex.ActualEndAt = now;
            Audit(db, actor, run.ReleaseTrainId, "StepExecution", ex.Id, action, before, new { status = ex.Status, version = ex.Version, step = step.StepCode, run = run.Mode });
            await db.SaveChangesAsync(ct);

            var late = 0;
            if (action == "Start")   // how far behind its (D28-effective) plan the step actually started
            {
                var all = await db.Set<RunbookSteps>().AsNoTracking().Where(x => x.ReleaseTrainId == run.ReleaseTrainId).ToListAsync(ct);
                var shift = run.Mode == "Rehearsal" ? RunPlan.RehearsalShift(run.StartedAt, all.Select(x => (x.Section, x.PlannedStartAt))) : TimeSpan.Zero;
                late = (int)Math.Floor((now - (step.PlannedStartAt + shift)).TotalMinutes);
            }
            captured(new StepEvent(run.ReleaseTrainId, run.Id, run.Mode, action, step.StepCode, step.OwnerUserId, step.OwnerTeamId, late));
            return ServiceResult<StepExecutions>.Ok(ex);
        }, ct);

    public Task<ServiceResult<RunbookRuns>> EndRunAsync(string runId, string outcome, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var run = await db.Set<RunbookRuns>().SingleOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null) return ServiceResult<RunbookRuns>.NotFound("run");
            if (VersionMismatch(expectedVersion, run.Version)) return ServiceResult<RunbookRuns>.Conflict(run);
            if (run.EndedAt is not null) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.RunEnded, "This run has already ended"));
            if (!Outcomes.Contains(outcome)) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.InvalidRun, $"Outcome must be one of {string.Join(", ", Outcomes)}"));

            var rows = await (from e in db.Set<StepExecutions>() join s in db.Set<RunbookSteps>() on e.StepId equals s.Id where e.RunId == runId select new { e.Status, s.StepCode, s.Section }).ToListAsync(ct);
            var running = rows.Where(r => r.Status == "Running").Select(r => r.StepCode).Order().ToList();
            if (running.Count > 0) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.StepsStillRunning, $"Finish or fail the running steps first: {string.Join(", ", running)}", running));
            if (outcome == "Completed")
            {
                // The Rollback section only runs when needed; every other step must have been Done or Skipped for a run to be Completed.
                var open = rows.Where(r => r.Section != "Rollback" && r.Status is not ("Done" or "Skipped")).Select(r => r.StepCode).Order().ToList();
                if (open.Count > 0) return ServiceResult<RunbookRuns>.Fail(new GuardFailure(Guards.StepsIncomplete, $"These steps are not Done or Skipped: {string.Join(", ", open)}", open));
            }

            var before = new { run.Version };
            run.EndedAt = Now; run.Outcome = outcome; run.Version++;
            Audit(db, actor, run.ReleaseTrainId, "RunbookRun", run.Id, "End", before, new { outcome, run.Version, run.Mode });
            await db.SaveChangesAsync(ct);
            return ServiceResult<RunbookRuns>.Ok(run);
        }, ct);

    // ---- after a committed step event: push the new forecast, and escalate ------------------------------------------------------------------
    // The step is already saved, so nothing here may undo it; but nothing is swallowed either: a failed push or notification is raised through IAlertSink (rule 8).
    private async Task AfterEventAsync(StepEvent e, CancellationToken ct)
    {
        try
        {
            if (_realtime is not null) await _realtime.ForecastChangedAsync(e.TrainId, e.RunId, ct);
            if (notifier is null || e.Mode != "Live") return;   // rehearsals never page anyone

            if (e.Action == "Start" && e.LateMin >= WarnLateMin)
            {
                var critical = e.LateMin >= CriticalLateMin;
                var who = await RecipientsAsync(e.OwnerUserId, e.OwnerTeamId, includeRte: true, includeManagers: critical, ct);
                var msg = $"Step {e.StepCode} started {e.LateMin} min after its plan" + (critical ? " (critical)" : "");
                foreach (var u in who) await notifier.NotifyAsync(new NotificationRequest(u, "StepLate", "RunbookRun", e.RunId, msg, critical ? 2 : 1), ct);
            }

            await NotifyIfDeadlineCrossedAsync(e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (alerts is not null) await alerts.RaiseAsync("Runbook", "EscalationFailed", e.RunId, $"Step {e.StepCode} was saved but its forecast push or escalation failed: {ex.Message}", ct);
            else throw;
        }
    }

    /// <summary>The first time the forecast finish passes the rollback deadline, tell the RTEs and Release Managers, once per run.</summary>
    private async Task NotifyIfDeadlineCrossedAsync(StepEvent e, CancellationToken ct)
    {
        var forecast = await new ForecastService(_dbf, _time).ComputeAsync(e.RunId, ct);
        if (!forecast.IsOk || !forecast.Value!.AlertRaised) return;
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (await db.Set<Notifications>().AnyAsync(n => n.Kind == "RollbackDeadline" && n.EntityId == e.RunId, ct)) return;
        var f = forecast.Value;
        var msg = $"Forecast finish {f.ForecastFinish} is {f.CrossesDeadlineByMin} min past the rollback deadline {f.RollbackDeadline}: a rollback might not fit in the window";
        foreach (var u in await RecipientsAsync(null, null, includeRte: true, includeManagers: true, ct))
            await notifier!.NotifyAsync(new NotificationRequest(u, "RollbackDeadline", "RunbookRun", e.RunId, msg, 2), ct);
    }

    private async Task<List<string>> RecipientsAsync(string? ownerUserId, string? ownerTeamId, bool includeRte, bool includeManagers, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var ids = new HashSet<string>();
        if (ownerUserId is not null) ids.Add(ownerUserId);
        if (ownerTeamId is not null) foreach (var m in await db.Set<TeamMembers>().Where(x => x.TeamId == ownerTeamId).Select(x => x.UserId).ToListAsync(ct)) ids.Add(m);
        var roles = new List<string>();
        if (includeRte) roles.Add(Roles.RTE);
        if (includeManagers) roles.Add(Roles.ReleaseManager);
        foreach (var u in await db.Set<Users>().Where(x => x.IsActive && roles.Contains(x.Role)).Select(x => x.Id).ToListAsync(ct)) ids.Add(u);
        return [.. ids.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> UnfinishedDependenciesAsync(ReleaseDbContext db, string runId, string stepId, CancellationToken ct)
    {
        var deps = await (from d in db.Set<StepDependencies>() join s in db.Set<RunbookSteps>() on d.DependsOnStepId equals s.Id where d.StepId == stepId select new { s.Id, s.StepCode }).ToListAsync(ct);
        var done = (await db.Set<StepExecutions>().Where(e => e.RunId == runId && (e.Status == "Done" || e.Status == "Skipped")).Select(e => e.StepId).ToListAsync(ct)).ToHashSet();
        return [.. deps.Where(d => !done.Contains(d.Id)).Select(d => d.StepCode).Order()];
    }
}
