using System.Security.Claims;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Ai;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-183 (D58): the AI platform's admin surface. Status and the controls are for an RTE or Release Manager and answer while the module is off (the settings panel reads them to say so). The
/// endpoints under <c>/ai</c> that use a model are behind <c>RequireModule("ai")</c>: off means 404. Nothing here approves or changes a status (advisory only). The key is write-only: it is
/// accepted once, protected with Data Protection, and never returned, logged or echoed.
/// </summary>
public static class AiEndpoints
{
    public sealed record KeyBody(string? Key);

    public static void MapAi(this RouteGroupBuilder api)
    {
        static string Ws(ClaimsPrincipal u) => u.FindFirstValue(WorkspaceResolution.Claim) is { } ws && ws != WorkspaceResolution.Nowhere ? ws : throw new InvalidOperationException("No current workspace");

        // GET /ai/status -> { moduleEnabled, moduleEntitled, provider, keyPresent, keyRequired, globalKillSwitch, workspacePaused, workspaceOptedOut, models, features[], timeoutSeconds, maxRetries, canCall, blockedBecause }
        api.MapGet("/ai/status", async (ClaimsPrincipal u, AiStatusService s, CancellationToken ct) => Results.Ok(await s.GetAsync(Ws(u), ct))).RequireAuthorization(Policies.Admin);

        // The per-workspace kill switch and opt-out: POST actions, like every other state change (never a PATCH of a flag).
        api.MapPost("/ai/workspace:pause", async (ClaimsPrincipal u, IAiWorkspaceSettings w, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, w, s, lf, "paused", x => x.SetPaused(Ws(u), true), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:resume", async (ClaimsPrincipal u, IAiWorkspaceSettings w, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, w, s, lf, "resumed", x => x.SetPaused(Ws(u), false), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:opt-out", async (ClaimsPrincipal u, IAiWorkspaceSettings w, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, w, s, lf, "opted out", x => x.SetOptedOut(Ws(u), true), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:opt-in", async (ClaimsPrincipal u, IAiWorkspaceSettings w, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, w, s, lf, "opted in", x => x.SetOptedOut(Ws(u), false), ct)).RequireAuthorization(Policies.Admin);

        // PUT /ai/key {key} -> 204; DELETE /ai/key -> 204. Write-only.
        api.MapPut("/ai/key", async (KeyBody body, IAiKeyStore keys, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Key)) return Results.UnprocessableEntity(new { guard = "KeyRequired", message = "The key is empty" });
            await keys.SetKeyAsync(body.Key, ct);
            lf.CreateLogger("ReleaseMgmt.Ai").LogInformation("The AI provider key was set");
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
        api.MapDelete("/ai/key", async (IAiKeyStore keys, ILoggerFactory lf, CancellationToken ct) =>
        {
            await keys.ClearKeyAsync(ct);
            lf.CreateLogger("ReleaseMgmt.Ai").LogInformation("The AI provider key was removed");
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // Everything that can reach a model sits behind the module guard: off means 404, exactly as if the route did not exist.
        var guarded = api.MapGroup("/ai").RequireModule(ModuleKeys.Ai);
        // GET /ai/preflight?feature=risk-score -> { allowed, outcome, detail, model }: would a call be made now? Makes no call and costs nothing.
        guarded.MapGet("/preflight", async (string? feature, ClaimsPrincipal u, IAiClient ai, CancellationToken ct) =>
            Results.Ok(await ai.PreflightAsync(Ws(u), feature ?? AiFeatures.RiskScore, ct))).RequireAuthorization(Policies.Read);
    }

    private static async Task<IResult> Change(ClaimsPrincipal u, IAiWorkspaceSettings w, AiStatusService s, ILoggerFactory lf, string what, Action<IAiWorkspaceSettings> apply, CancellationToken ct)
    {
        apply(w);
        var ws = u.FindFirstValue(WorkspaceResolution.Claim)!;
        lf.CreateLogger("ReleaseMgmt.Ai").LogWarning("AI for workspace {Workspace} {Change} by user {User}", ws, what, u.FindFirstValue("uid"));
        return Results.Ok(await s.GetAsync(ws, ct));
    }
}
