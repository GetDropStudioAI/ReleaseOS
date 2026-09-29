using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Runs (PROJECT_SCOPE section 6): POST /trains/{id}/runs, POST /runs/{id}/steps/{stepId}:start|:done|:fail|:skip, POST /runs/{id}:end, plus the reads a run screen needs.</summary>
public static class RunEndpoints
{
    public sealed record StartRunBody(string Mode);
    public sealed record StepActionBody(string? Note);
    public sealed record EndRunBody(string Outcome);
    public sealed record RunSummary(string Id, string Mode, string StartedAt, string? EndedAt, string? Outcome, int Version);
    public sealed record RunStep(string StepId, string StepCode, string Section, string Title, string? OwnerName, string PlannedStartAt, string PlannedEndAt, int PlannedDurationMin,
                                 string Status, string? ActualStartAt, string? ActualEndAt, string? ActorName, string? Note, string[] DependsOn, bool CanAct, int Version);
    public sealed record RunDetail(string Id, string TrainId, string Mode, string StartedAt, string? EndedAt, string? Outcome, int ShiftMinutes, int Version, RunStep[] Steps);

    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapRuns(this RouteGroupBuilder api)
    {
        api.MapPost("/trains/{id}/runs", async (string id, StartRunBody b, ClaimsPrincipal u, RunService s, CancellationToken ct) =>
        {
            var r = await s.StartRunAsync(id, b.Mode, ActorOf(u), ct);
            return r.IsOk ? Results.Ok(new RunSummary(r.Value!.Id, r.Value.Mode, r.Value.StartedAt.ToString(Iso), null, null, r.Value.Version)) : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Plan);

        api.MapGet("/trains/{id}/runs", async (string id, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == id, ct)) return Results.NotFound(new { message = "Train not found" });
            var runs = (await db.Set<RunbookRuns>().AsNoTracking().Where(r => r.ReleaseTrainId == id).ToListAsync(ct)).OrderByDescending(r => r.StartedAt);
            return Results.Ok(runs.Select(r => new RunSummary(r.Id, r.Mode, r.StartedAt.ToString(Iso), r.EndedAt?.ToString(Iso), r.Outcome, r.Version)));
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/runs/{id}", async (string id, ClaimsPrincipal u, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var run = await db.Set<RunbookRuns>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
            return run is null ? Results.NotFound(new { message = "Run not found" }) : Results.Ok(await DetailAsync(db, run, u, ct));
        }).RequireAuthorization(Policies.Read);

        foreach (var action in new[] { "start", "done", "fail", "skip" })
        {
            var a = action;
            api.MapPost($"/runs/{{id}}/steps/{{stepId}}:{a}", async (string id, string stepId, StepActionBody? b, ClaimsPrincipal u, HttpRequest req, RunService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
            {
                await using (var db = await dbf.CreateDbContextAsync(ct))
                {
                    var step = await db.Set<RunbookSteps>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == stepId, ct);
                    if (step is not null && !await CanActAsync(db, step, u, ct))
                        return Results.Json(new { message = "Only the step owner, an RTE or a Release Manager can do this" }, statusCode: StatusCodes.Status403Forbidden);
                }
                var note = b?.Note; var who = ActorOf(u); var v = req.IfMatch();
                var r = a switch
                {
                    "start" => await s.StartStepAsync(id, stepId, note, who, v, ct),
                    "done" => await s.DoneStepAsync(id, stepId, note, who, v, ct),
                    "fail" => await s.FailStepAsync(id, stepId, note, who, v, ct),
                    _ => await s.SkipStepAsync(id, stepId, note, who, v, ct),
                };
                return r.ToHttp();
            }).RequireAuthorization(Policies.Read);
        }

        api.MapGet("/runs/{id}/forecast", async (string id, ForecastService f, CancellationToken ct) => (await f.ComputeAsync(id, ct)).ToHttp()).RequireAuthorization(Policies.Read);

        api.MapPost("/runs/{id}:end", async (string id, EndRunBody b, ClaimsPrincipal u, HttpRequest req, RunService s, CancellationToken ct) =>
            (await s.EndRunAsync(id, b.Outcome, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Plan);
    }

    /// <summary>Planners always; otherwise the step's owner or a member of its owning team.</summary>
    private static async Task<bool> CanActAsync(ReleaseDbContext db, RunbookSteps step, ClaimsPrincipal u, CancellationToken ct)
    {
        if (u.IsInRole(Roles.RTE) || u.IsInRole(Roles.ReleaseManager)) return true;
        var uid = u.FindFirstValue("uid");
        return step.OwnerUserId == uid || (step.OwnerTeamId is not null && await db.Set<TeamMembers>().AnyAsync(m => m.TeamId == step.OwnerTeamId && m.UserId == uid, ct));
    }

    private static async Task<RunDetail> DetailAsync(ReleaseDbContext db, RunbookRuns run, ClaimsPrincipal u, CancellationToken ct)
    {
        var steps = await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == run.ReleaseTrainId).ToListAsync(ct);
        var execs = (await db.Set<StepExecutions>().AsNoTracking().Where(e => e.RunId == run.Id).ToListAsync(ct)).ToDictionary(e => e.StepId);
        var code = steps.ToDictionary(s => s.Id, s => s.StepCode);
        var deps = (await db.Set<StepDependencies>().AsNoTracking().Where(d => code.Keys.Contains(d.StepId)).ToListAsync(ct)).ToLookup(d => d.StepId);
        var people = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var teams = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
        // D28: a Rehearsal shifts every planned time; a Live run uses the plan's absolute times.
        var shift = run.Mode == "Rehearsal" ? RunPlan.RehearsalShift(run.StartedAt, steps.Select(s => (s.Section, s.PlannedStartAt))) : TimeSpan.Zero;
        var rows = new List<RunStep>();
        foreach (var s in steps.OrderBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode, StringComparer.Ordinal))
        {
            execs.TryGetValue(s.Id, out var e);
            var start = s.PlannedStartAt + shift;
            rows.Add(new RunStep(s.Id, s.StepCode, s.Section, s.Title, s.OwnerUserId is not null ? people.GetValueOrDefault(s.OwnerUserId) : teams.GetValueOrDefault(s.OwnerTeamId ?? ""),
                start.ToString(Iso), start.AddMinutes(s.PlannedDurationMin).ToString(Iso), s.PlannedDurationMin,
                e?.Status ?? "Scheduled", e?.ActualStartAt?.ToString(Iso), e?.ActualEndAt?.ToString(Iso), e?.ActorUserId is null ? null : people.GetValueOrDefault(e.ActorUserId), e?.Note,
                [.. deps[s.Id].Select(d => code[d.DependsOnStepId]).Order(StringComparer.Ordinal)], run.EndedAt is null && await CanActAsync(db, s, u, ct), e?.Version ?? 1));
        }
        return new RunDetail(run.Id, run.ReleaseTrainId, run.Mode, run.StartedAt.ToString(Iso), run.EndedAt?.ToString(Iso), run.Outcome, (int)shift.TotalMinutes, run.Version, [.. rows]);
    }
}
