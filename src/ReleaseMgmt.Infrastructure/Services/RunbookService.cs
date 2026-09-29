using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record NewStep(string? StepCode, string Section, string Title, string? Instructions, string? OwnerUserId, string? OwnerTeamId,
                             DateTime PlannedStartAt, int PlannedDurationMin, string? BundledProductId);

/// <summary>Partial update: null means "leave alone". For the two optional text/reference fields an empty string clears them. Setting either owner replaces the other.</summary>
public sealed record StepPatch(string? StepCode, string? Section, string? Title, string? Instructions, string? OwnerUserId, string? OwnerTeamId,
                               DateTime? PlannedStartAt, int? PlannedDurationMin, string? BundledProductId);

/// <summary>
/// The runbook plan (steps, sections, planned start and duration, dependencies). Actuals never land here: they live in StepExecutions (rule 7).
/// Dependency cycles longer than a step depending on itself are rejected here, with the cycle named (schema.sql leaves that to the domain).
/// </summary>
public sealed partial class RunbookService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Sections = ["PreCheck", "Deploy", "Verify", "Rollback", "Hypercare"];
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,19}$")] private static partial Regex CodeShape();

    public Task<ServiceResult<RunbookSteps>> CreateAsync(string trainId, NewStep n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<RunbookSteps>.NotFound("train");
            if (await LockedAsync(db, train, ct) is { } lockedNew) return ServiceResult<RunbookSteps>.Fail(lockedNew);

            var s = new RunbookSteps
            {
                ReleaseTrainId = trainId, Section = n.Section, Title = (n.Title ?? "").Trim(), Instructions = Blank(n.Instructions),
                OwnerUserId = Blank(n.OwnerUserId), OwnerTeamId = Blank(n.OwnerTeamId), BundledProductId = Blank(n.BundledProductId),
                PlannedStartAt = Whole(n.PlannedStartAt), PlannedDurationMin = n.PlannedDurationMin,
            };
            var code = Blank(n.StepCode)?.ToUpperInvariant() ?? await NextCodeAsync(db, trainId, ct);
            s.StepCode = code;
            var bad = await ValidateAsync(db, s, trainId, isNew: true, ct);
            if (bad is not null) return ServiceResult<RunbookSteps>.Fail(bad);

            db.Set<RunbookSteps>().Add(s);
            Audit(db, actor, trainId, "RunbookStep", s.Id, "Add", null, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return ServiceResult<RunbookSteps>.Ok(s);
        }, ct);

    public Task<ServiceResult<RunbookSteps>> UpdateAsync(string stepId, StepPatch p, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<RunbookSteps>().SingleOrDefaultAsync(x => x.Id == stepId, ct);
            if (s is null) return ServiceResult<RunbookSteps>.NotFound("step");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<RunbookSteps>.Conflict(s);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == s.ReleaseTrainId, ct);
            if (await LockedAsync(db, train, ct) is { } lockedUpd) return ServiceResult<RunbookSteps>.Fail(lockedUpd);

            var before = Snapshot(s);
            if (p.StepCode is not null) s.StepCode = p.StepCode.Trim().ToUpperInvariant();
            if (p.Section is not null) s.Section = p.Section;
            if (p.Title is not null) s.Title = p.Title.Trim();
            if (p.Instructions is not null) s.Instructions = Blank(p.Instructions);
            if (p.PlannedStartAt is DateTime start) s.PlannedStartAt = Whole(start);
            if (p.PlannedDurationMin is int d) s.PlannedDurationMin = d;
            if (p.BundledProductId is not null) s.BundledProductId = Blank(p.BundledProductId);
            if (Blank(p.OwnerUserId) is string u) { s.OwnerUserId = u; s.OwnerTeamId = null; }
            else if (Blank(p.OwnerTeamId) is string tm) { s.OwnerTeamId = tm; s.OwnerUserId = null; }

            var bad = await ValidateAsync(db, s, s.ReleaseTrainId, isNew: false, ct);
            if (bad is not null) return ServiceResult<RunbookSteps>.Fail(bad);
            s.Version++;
            Audit(db, actor, s.ReleaseTrainId, "RunbookStep", s.Id, "Update", before, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return ServiceResult<RunbookSteps>.Ok(s);
        }, ct);

    /// <summary>Replaces the step's whole dependency set. Rejected with the cycle named if any dependency chain would loop back.</summary>
    public Task<ServiceResult<RunbookSteps>> SetDependenciesAsync(string stepId, IReadOnlyCollection<string> dependsOnStepIds, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<RunbookSteps>().SingleOrDefaultAsync(x => x.Id == stepId, ct);
            if (s is null) return ServiceResult<RunbookSteps>.NotFound("step");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<RunbookSteps>.Conflict(s);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == s.ReleaseTrainId, ct);
            if (await LockedAsync(db, train, ct) is { } lockedDep) return ServiceResult<RunbookSteps>.Fail(lockedDep);

            var wanted = dependsOnStepIds.Distinct().ToList();
            var steps = await db.Set<RunbookSteps>().Where(x => x.ReleaseTrainId == s.ReleaseTrainId).ToListAsync(ct);
            var byId = steps.ToDictionary(x => x.Id);
            var unknown = wanted.Where(id => !byId.ContainsKey(id)).ToList();
            if (unknown.Count > 0) return ServiceResult<RunbookSteps>.Fail(new GuardFailure(Guards.InvalidStep, "A dependency is not a step of this train", unknown));

            var existing = await db.Set<StepDependencies>().Where(d => byId.Keys.Contains(d.StepId)).ToListAsync(ct);
            var graph = existing.Where(d => d.StepId != stepId).GroupBy(d => d.StepId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(d => d.DependsOnStepId).ToList());
            graph[stepId] = wanted;
            var cycle = DependencyGraph.FindCycle(graph);
            if (cycle is not null)
            {
                var names = cycle.Select(id => byId.TryGetValue(id, out var x) ? x.StepCode : id).ToList();
                return ServiceResult<RunbookSteps>.Fail(new GuardFailure(Guards.DependencyCycle, $"That would make a dependency cycle: {string.Join(" -> ", names)}", names));
            }

            var before = existing.Where(d => d.StepId == stepId).Select(d => byId[d.DependsOnStepId].StepCode).Order().ToList();
            db.Set<StepDependencies>().RemoveRange(existing.Where(d => d.StepId == stepId));
            foreach (var id in wanted) db.Set<StepDependencies>().Add(new StepDependencies { StepId = stepId, DependsOnStepId = id });
            s.Version++;
            Audit(db, actor, s.ReleaseTrainId, "RunbookStep", s.Id, "SetDependencies", new { dependsOn = before }, new { dependsOn = wanted.Select(id => byId[id].StepCode).Order().ToList() });
            await db.SaveChangesAsync(ct);
            return ServiceResult<RunbookSteps>.Ok(s);
        }, ct);

    // ---- rules -----------------------------------------------------------------------------------------------------------------------------
    // Decision (docs/QUESTIONS.md Q-010): the plan is frozen while a Live run is open (its lateness is measured against the plan) and once the train is Complete or Aborted.
    // Rehearsals do not lock it: rehearsing is how the plan gets fixed.
    private static async Task<GuardFailure?> LockedAsync(ReleaseDbContext db, ReleaseTrains t, CancellationToken ct)
    {
        if (t.CurrentStatus is "Complete" or "Aborted") return new GuardFailure(Guards.TrainClosed, $"The train is {t.CurrentStatus}; its runbook can no longer be edited");
        if (await db.Set<RunbookRuns>().AnyAsync(r => r.ReleaseTrainId == t.Id && r.Mode == "Live" && r.EndedAt == null, ct))
            return new GuardFailure(Guards.RunInProgress, "A live run is in progress; the runbook plan is locked until it ends");
        return null;
    }

    private static async Task<GuardFailure?> ValidateAsync(ReleaseDbContext db, RunbookSteps s, string trainId, bool isNew, CancellationToken ct)
    {
        GuardFailure Bad(string m) => new(Guards.InvalidStep, m);
        if (string.IsNullOrWhiteSpace(s.Title)) return Bad("A step needs a title");
        if (!Sections.Contains(s.Section)) return Bad($"Section must be one of {string.Join(", ", Sections)}");
        if (s.PlannedDurationMin <= 0) return Bad("Planned duration must be more than 0 minutes");
        if (!CodeShape().IsMatch(s.StepCode)) return Bad("Step code must be letters, digits or dashes, up to 20 characters (for example R-014)");
        if ((s.OwnerUserId is null) == (s.OwnerTeamId is null)) return Bad("A step needs exactly one owner: a person or a team");
        if (s.OwnerUserId is not null && !await db.Set<Users>().AnyAsync(u => u.Id == s.OwnerUserId, ct)) return Bad("The owner does not exist");
        if (s.OwnerTeamId is not null && !await db.Set<Teams>().AnyAsync(t => t.Id == s.OwnerTeamId, ct)) return Bad("The owning team does not exist");
        if (s.BundledProductId is not null && !await db.Set<BundledProducts>().AnyAsync(p => p.Id == s.BundledProductId && p.ReleaseTrainId == trainId, ct)) return Bad("The product is not part of this train");
        if (await db.Set<RunbookSteps>().AnyAsync(x => x.ReleaseTrainId == trainId && x.StepCode == s.StepCode && x.Id != s.Id, ct))
            return new GuardFailure(Guards.DuplicateStepCode, $"Step code {s.StepCode} is already used in this train");
        return null;
    }

    private static async Task<string> NextCodeAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var codes = await db.Set<RunbookSteps>().Where(x => x.ReleaseTrainId == trainId).Select(x => x.StepCode).ToListAsync(ct);
        var max = codes.Select(c => int.TryParse(c.StartsWith("R-") ? c[2..] : "", out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"R-{max + 1:D3}";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime Whole(DateTime t) { var u = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime(); return new DateTime(u.Ticks - u.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); }
    private static object Snapshot(RunbookSteps s) => new { s.StepCode, s.Section, s.Title, s.OwnerUserId, s.OwnerTeamId, s.BundledProductId, plannedStartAt = s.PlannedStartAt, s.PlannedDurationMin, s.Version };
}
