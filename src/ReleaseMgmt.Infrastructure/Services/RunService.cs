using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Rehearsal and Live runs of a train's runbook, and the step actions inside them. Actuals live in StepExecutions and never touch the plan
/// (rule 7). The service checks each rule first for a readable 422; the triggers (Live needs an Executing train, dependencies Done, freeze
/// lockout) are the backstop. Step and run writes are audited and stamped with Version in the same transaction.
/// </summary>
public sealed class RunService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
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

    private Task<ServiceResult<StepExecutions>> StepAsync(string runId, string stepId, string action, string? note, Actor actor, int? expectedVersion, CancellationToken ct) =>
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
            }

            var before = new { status = ex.Status, version = ex.Version };
            var now = Now;
            ex.Status = to; ex.ActorUserId = actor.UserId; ex.Version++;
            if (note is not null) ex.Note = note;
            if (action == "Start") ex.ActualStartAt = now;
            if (action is "Done" or "Fail") ex.ActualEndAt = now;
            Audit(db, actor, run.ReleaseTrainId, "StepExecution", ex.Id, action, before, new { status = ex.Status, version = ex.Version, step = step.StepCode, run = run.Mode });
            await db.SaveChangesAsync(ct);
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

    private static async Task<List<string>> UnfinishedDependenciesAsync(ReleaseDbContext db, string runId, string stepId, CancellationToken ct)
    {
        var deps = await (from d in db.Set<StepDependencies>() join s in db.Set<RunbookSteps>() on d.DependsOnStepId equals s.Id where d.StepId == stepId select new { s.Id, s.StepCode }).ToListAsync(ct);
        var done = (await db.Set<StepExecutions>().Where(e => e.RunId == runId && (e.Status == "Done" || e.Status == "Skipped")).Select(e => e.StepId).ToListAsync(ct)).ToHashSet();
        return [.. deps.Where(d => !done.Contains(d.Id)).Select(d => d.StepCode).Order()];
    }
}
