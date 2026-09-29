using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ForecastStepDto(string Step, string State, string? Start, string? End, int? EndVarianceMin);
public sealed record RunForecastDto(string RunId, string Mode, string AsOf, string? WindowStart, string? WindowEnd, string? ForecastFinish, string PlannedFinish, int RollbackPlannedMin, string? RollbackDeadline,
                                    int? CrossesDeadlineByMin, bool AlertRaised, int? WindowClosesInSec, IReadOnlyList<string> BlockedByFailed, IReadOnlyList<ForecastStepDto> Steps);

/// <summary>Reads a run and its plan and applies <see cref="RunForecasting"/> at the injected clock. A Rehearsal shifts the plan and the window by the same D28 offset. Read-only.</summary>
public sealed class ForecastService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time)
{
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public async Task<ServiceResult<RunForecastDto>> ComputeAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var run = await db.Set<RunbookRuns>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return ServiceResult<RunForecastDto>.NotFound("run");

        var steps = await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == run.ReleaseTrainId).ToListAsync(ct);
        var ids = steps.Select(s => s.Id).ToList();
        var code = steps.ToDictionary(s => s.Id, s => s.StepCode);
        var deps = (await db.Set<StepDependencies>().AsNoTracking().Where(d => ids.Contains(d.StepId)).ToListAsync(ct)).ToLookup(d => d.StepId, d => code[d.DependsOnStepId]);
        var execs = (await db.Set<StepExecutions>().AsNoTracking().Where(e => e.RunId == runId).ToListAsync(ct)).ToDictionary(e => e.StepId);
        var window = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(w => w.ReleaseTrainId == run.ReleaseTrainId, ct);

        var shift = run.Mode == "Rehearsal" ? RunPlan.RehearsalShift(run.StartedAt, steps.Select(s => (s.Section, s.PlannedStartAt))) : TimeSpan.Zero;
        var input = steps.Select(s =>
        {
            execs.TryGetValue(s.Id, out var e);
            return new ForecastStep(s.StepCode, s.Section, s.PlannedStartAt + shift, s.PlannedDurationMin, [.. deps[s.Id]], e?.Status ?? "Scheduled", e?.ActualStartAt, e?.ActualEndAt);
        }).ToList();

        var now = time.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var r = RunForecasting.Compute(now, window is null ? null : window.EndsAt + shift, input);
        string? F(DateTime? d) => d?.ToString(Iso, CultureInfo.InvariantCulture);
        return ServiceResult<RunForecastDto>.Ok(new RunForecastDto(run.Id, run.Mode, F(now)!, F(window is null ? null : window.StartsAt + shift), F(window is null ? null : window.EndsAt + shift), F(r.ForecastFinish), F(r.PlannedFinish)!, r.RollbackPlannedMin, F(r.RollbackDeadline),
            r.CrossesDeadlineByMin, r.AlertRaised, r.WindowClosesInSec, r.BlockedByFailed, [.. r.Steps.Select(x => new ForecastStepDto(x.Step, x.State, F(x.Start), F(x.End), x.EndVarianceMin))]));
    }
}
