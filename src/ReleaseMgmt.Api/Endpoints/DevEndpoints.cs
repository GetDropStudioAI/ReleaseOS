using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Development-only test support (mapped only when the environment is Development, like the fake login). Never exists in Production.</summary>
public static class DevEndpoints
{
    public static void MapDev(this RouteGroupBuilder api)
    {
        // A believable drill for the live runbook screen: an Executing train whose window opened 30 minutes ago and closes in 90, a 6-step plan
        // (one Rollback step), and a Live run in which R-001 is done and R-002 is running. Times are relative to the server clock.
        api.MapPost("/dev/live-drill", async (ClaimsPrincipal u, IDbContextFactory<ReleaseDbContext> dbf, RunService runs, TimeProvider time, CancellationToken ct) =>
        {
            var uid = u.FindFirstValue("uid")!;
            var now = time.GetUtcNow().UtcDateTime; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var start = now.AddMinutes(-30);
            var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            var trainId = Ids.New();
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                db.Set<ReleaseTrains>().Add(new ReleaseTrains { Id = trainId, Title = $"R26.9{tag} Live drill", TargetReleaseDate = DateOnly.FromDateTime(now), RiskTier = "Moderate", CurrentStatus = "Executing", CreatedAt = now, UpdatedAt = now, LastChangedByUserId = uid, LastChangedAt = now });
                db.Set<DeploymentWindows>().Add(new DeploymentWindows { ReleaseTrainId = trainId, StartsAt = start, EndsAt = now.AddMinutes(90) });
                (string Code, string Section, string Title, int At, int Min, string? After, string? Instr)[] plan =
                [
                    ("R-001", "PreCheck", "Confirm Go and freeze override", 0, 5, null, "Confirm the Go decision is recorded and the override is unexpired."),
                    ("R-002", "Deploy", "Deploy Payments API", 5, 30, "R-001", "Promote the build to the green slot\nRun the health probe until 3 consecutive passes\nFlip 10% then 100% of traffic"),
                    ("R-003", "Deploy", "Deploy Card Portal", 35, 20, "R-002", null),
                    ("R-004", "Verify", "Smoke test card authorisation", 55, 20, "R-003", null),
                    ("R-005", "Verify", "Business verification sign-off", 75, 15, "R-004", null),
                    ("R-901", "Rollback", "Redeploy previous Payments API", 90, 25, null, "Only if the drill is called off."),
                ];
                var ids = plan.ToDictionary(p => p.Code, _ => Ids.New());
                foreach (var p in plan)
                {
                    db.Set<RunbookSteps>().Add(new RunbookSteps { Id = ids[p.Code], ReleaseTrainId = trainId, StepCode = p.Code, Section = p.Section, Title = p.Title, Instructions = p.Instr, OwnerUserId = uid, PlannedStartAt = start.AddMinutes(p.At), PlannedDurationMin = p.Min });
                }
                foreach (var p in plan.Where(p => p.After is not null)) db.Set<StepDependencies>().Add(new StepDependencies { StepId = ids[p.Code], DependsOnStepId = ids[p.After!] });
                await db.SaveChangesAsync(ct);
                var run = await runs.StartRunAsync(trainId, "Live", new Actor(uid), ct);
                if (!run.IsOk) return Results2.ToHttp(run);
                foreach (var r in new[] { await runs.StartStepAsync(run.Value!.Id, ids["R-001"], null, new Actor(uid), null, ct) })
                    if (!r.IsOk) return Results2.ToHttp(r);
                if (!(await runs.DoneStepAsync(run.Value.Id, ids["R-001"], null, new Actor(uid), null, ct)).IsOk) return Results.Problem("drill: R-001 done failed");
                if (!(await runs.StartStepAsync(run.Value.Id, ids["R-002"], null, new Actor(uid), null, ct)).IsOk) return Results.Problem("drill: R-002 start failed");
                return Results.Ok(new { trainId, runId = run.Value.Id, steps = ids });
            }
        }).RequireAuthorization(Policies.Plan);
    }
}
