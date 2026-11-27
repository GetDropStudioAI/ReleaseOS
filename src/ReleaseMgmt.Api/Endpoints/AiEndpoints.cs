using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
/// REOS-184: the controls are audited and kept in the database; <c>/ai/inventory</c> and <c>/ai/actions</c> are the audit trail for an RTE, Release Manager or Governance Officer (they answer while the module
/// is off: a reviewer must still be able to read what was done), and <c>/ai/actions/{id}:decide</c> records the human's decision on a draft (Accepted, Edited or Rejected) once. Decisions are an action, not a PATCH.
/// </summary>
public static class AiEndpoints
{
    public sealed record KeyBody(string? Key);
    public sealed record DecisionBody(string? Decision, string? FinalText, string? Note);

    public static void MapAi(this RouteGroupBuilder api)
    {
        static string Ws(ClaimsPrincipal u) => u.FindFirstValue(WorkspaceResolution.Claim) is { } ws && ws != WorkspaceResolution.Nowhere ? ws : throw new InvalidOperationException("No current workspace");

        // GET /ai/status -> { moduleEnabled, moduleEntitled, provider, keyPresent, keyRequired, globalKillSwitch, workspacePaused, workspaceOptedOut, models, features[], timeoutSeconds, maxRetries, canCall, blockedBecause }
        api.MapGet("/ai/status", async (ClaimsPrincipal u, AiStatusService s, CancellationToken ct) => Results.Ok(await s.GetAsync(Ws(u), ct))).RequireAuthorization(Policies.Admin);

        // The per-workspace kill switch and opt-out: POST actions, like every other state change (never a PATCH of a flag). Each is audited (AiWorkspace) and kept in the database.
        api.MapPost("/ai/workspace:pause", async (ClaimsPrincipal u, IAiControls c, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, s, lf, "paused", (ws, a) => c.SetPausedAsync(ws, true, a, ct), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:resume", async (ClaimsPrincipal u, IAiControls c, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, s, lf, "resumed", (ws, a) => c.SetPausedAsync(ws, false, a, ct), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:opt-out", async (ClaimsPrincipal u, IAiControls c, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, s, lf, "opted out", (ws, a) => c.SetOptedOutAsync(ws, true, a, ct), ct)).RequireAuthorization(Policies.Admin);
        api.MapPost("/ai/workspace:opt-in", async (ClaimsPrincipal u, IAiControls c, AiStatusService s, ILoggerFactory lf, CancellationToken ct) =>
            await Change(u, s, lf, "opted in", (ws, a) => c.SetOptedOutAsync(ws, false, a, ct), ct)).RequireAuthorization(Policies.Admin);

        // ---- REOS-185: the allowance. Administrators see what is included, used and left, the caps, where it went, and every ledger entry. They answer while the module is off (what was spent stays readable).
        // There is deliberately NO endpoint that adds credits: a workspace administrator must not be able to give their own workspace credits. Credit packs are granted through IAiAllowance by billing (Q-185a).
        // GET /ai/usage -> AiAllowanceView { periodStart, periodEnd, resetsOn, included/pack/total/used/reserved/remainingMicroUsd, percentUsed, softCapPercent, chatSharePercent, level (Ok|Soft|Hard), message, features[], users[], packs[] }
        api.MapGet("/ai/usage", async (ClaimsPrincipal u, IAiAllowance a, CancellationToken ct) => Results.Ok(await a.GetAsync(Ws(u), ct))).RequireAuthorization(Policies.Admin);
        // GET /ai/usage/ledger?limit= -> [AiLedgerEntry], newest first: one row per call that asked for allowance (refusals by a cap included)
        api.MapGet("/ai/usage/ledger", async (int? limit, ClaimsPrincipal u, IAiAllowance a, CancellationToken ct) => Results.Ok(await a.LedgerAsync(Ws(u), limit ?? 100, ct))).RequireAuthorization(Policies.Admin);

        // PUT /ai/key {key} -> 204; DELETE /ai/key -> 204. Write-only.
        api.MapPut("/ai/key", async (KeyBody body, ClaimsPrincipal u, IAiKeyStore keys, IAiControls controls, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Key)) return Results.UnprocessableEntity(new { guard = "KeyRequired", message = "The key is empty" });
            await keys.SetKeyAsync(body.Key, ct);
            await controls.RecordKeyChangeAsync("Set", ActorOf(u), ct);
            lf.CreateLogger("ReleaseMgmt.Ai").LogInformation("The AI provider key was set");
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
        api.MapDelete("/ai/key", async (ClaimsPrincipal u, IAiKeyStore keys, IAiControls controls, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (await keys.ClearKeyAsync(ct)) await controls.RecordKeyChangeAsync("Removed", ActorOf(u), ct);
            lf.CreateLogger("ReleaseMgmt.Ai").LogInformation("The AI provider key was removed");
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // ---- REOS-184: the inventory and the audit trail. Not behind the module guard: they say what was done, and must be readable after the module is switched off.
        // GET /ai/inventory -> { controls (as /ai/status), features[], models[], data, decisions } for an RTE, Release Manager or Governance Officer.
        api.MapGet("/ai/inventory", async (ClaimsPrincipal u, AiInventoryService s, CancellationToken ct) => Results.Ok(await s.GetAsync(Ws(u), ct))).RequireAuthorization(Policies.AuditRead);
        // GET /ai/actions?feature=&decision=&outcome=&subjectType=&subjectId=&limit= -> [AiActionSummary], newest first (no texts)
        api.MapGet("/ai/actions", async (string? feature, string? decision, string? outcome, string? subjectType, string? subjectId, int? limit, IAiActionLog log, CancellationToken ct) =>
            Results.Ok(await log.ListAsync(new AiActionFilter(feature, decision, outcome, null, subjectType, subjectId, limit ?? 100), ct))).RequireAuthorization(Policies.AuditRead);
        // GET /ai/actions/{id} -> AiActionView: the draft, the human edit, the hashes, and whether the stored texts still match the audit rows the chain covers. Any signed-in member (a feature shows the draft to its requester).
        api.MapGet("/ai/actions/{id}", async (string id, IAiActionLog log, CancellationToken ct) =>
            await log.GetAsync(id, ct) is { } a ? Results.Ok(a) : Results.NotFound(new { message = "AI action not found" })).RequireAuthorization(Policies.Read);
        // POST /ai/actions/{id}:decide { decision: Accepted|Edited|Rejected, finalText?, note? } with If-Match -> AiActionView. The person who asked, or an administrator, once. 422 AiActionDecision, AiActionDecided, AiActionNotDecidable, AiActionRole.
        api.MapPost("/ai/actions/{id}:decide", async (string id, DecisionBody b, ClaimsPrincipal u, HttpRequest r, IAuthorizationService auth, IAiActionLog log, CancellationToken ct) =>
        {
            var isAdmin = (await auth.AuthorizeAsync(u, Policies.Admin)).Succeeded;
            return await log.DecideAsync(id, b.Decision ?? "", b.FinalText, b.Note, ActorOf(u), isAdmin, r.IfMatch(), ct).ToHttpAsync();
        }).RequireAuthorization(Policies.Read);

        // Everything that can reach a model sits behind the module guard: off means 404, exactly as if the route did not exist.
        var guarded = api.MapGroup("/ai").RequireModule(ModuleKeys.Ai);
        // GET /ai/preflight?feature=risk-score -> { allowed, outcome, detail, model }: would a call be made now? Makes no call and costs nothing.
        guarded.MapGet("/preflight", async (string? feature, ClaimsPrincipal u, IAiClient ai, CancellationToken ct) =>
            Results.Ok(await ai.PreflightAsync(Ws(u), feature ?? AiFeatures.RiskScore, ct))).RequireAuthorization(Policies.Read);
    }

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static async Task<IResult> Change(ClaimsPrincipal u, AiStatusService s, ILoggerFactory lf, string what, Func<string, Actor, Task> apply, CancellationToken ct)
    {
        var ws = u.FindFirstValue(WorkspaceResolution.Claim)!;
        await apply(ws, ActorOf(u));
        lf.CreateLogger("ReleaseMgmt.Ai").LogWarning("AI for workspace {Workspace} {Change} by user {User}", ws, what, u.FindFirstValue("uid"));
        return Results.Ok(await s.GetAsync(ws, ct));
    }
}
