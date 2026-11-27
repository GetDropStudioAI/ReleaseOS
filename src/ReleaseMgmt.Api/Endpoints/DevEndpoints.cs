using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Ai;
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
        // REOS-184 test support: a pending AI draft for the signed-in person, recorded through the audit trail exactly as the governed client records one (no model is called, the AI module need not be on),
        // so the inventory screen and the human decision can be driven end to end before any AI feature exists. POST /dev/ai/draft?text=...&feature=release-notes-draft -> { id, version }.
        api.MapPost("/dev/ai/draft", async (string? text, string? feature, ClaimsPrincipal u, IAiActionLog log, TimeProvider time, CancellationToken ct) =>
        {
            var ws = u.FindFirstValue(WorkspaceResolution.Claim) is { } w && w != WorkspaceResolution.Nowhere ? w : WorkspaceIds.Default;
            var f = AiFeatures.Find(feature) ?? AiFeatures.All.First(x => x.Key == AiFeatures.ReleaseNotes);
            var draft = string.IsNullOrWhiteSpace(text) ? "Release notes draft: this release ships the payment gateway fix." : text;
            var request = new AiRequest(ws, f.Key, "You draft release notes.", "Write the notes.", [new AiContextItem("Dev sample", draft)], 512, false, new AiCallMeta(u.FindFirstValue("uid"), PromptTemplateVersion: "dev-sample/1"));
            var result = new AiResult(AiOutcome.Completed, f.Key, ws, time.GetUtcNow().UtcDateTime, Text: draft, Model: "dev-sample-model", Usage: new AiUsage(f.Key, "dev-sample-model", 120, 40, 0, 0, false), Attempts: 1,
                ProviderCalled: true, Provider: "fake", ServedModel: "dev-sample-model-2026-11", Inputs: [new AiInputRef("Dev sample", nameof(AiDataClass.ReleaseData), AiHash.Sha256(draft), draft.Length)],
                InputHash: AiHash.Sha256("dev-sample " + draft), PromptHash: AiHash.Sha256("You draft release notes."));
            var id = await log.RecordAsync(request, result, ct);
            return Results.Ok(new { id, version = 1 });
        });

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
                var production = await db.Set<ReleaseTypes>().AsNoTracking().Where(x => x.Name == "Production").Select(x => x.Id).FirstOrDefaultAsync(ct);   // REOS-116: every release has a type
                db.Set<ReleaseTrains>().Add(new ReleaseTrains { Id = trainId, Title = $"R26.9{tag} Live drill", TargetReleaseDate = DateOnly.FromDateTime(now), RiskTier = "Moderate", CurrentStatus = "Executing", CreatedAt = now, UpdatedAt = now, LastChangedByUserId = uid, LastChangedAt = now, ReleaseTypeId = production });
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
