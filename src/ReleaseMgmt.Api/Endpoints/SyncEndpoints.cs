using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Sync health endpoints (REOS-42; PROJECT_SCOPE sections 5.3 and 6). Reads are open to every signed-in role; resolving an alert and the webhook
/// allowlist writes are admin (RTE or Release Manager in v1, D14/D32). Connectors, credentials, run-now and the pollers belong to REOS-39..41.
/// See Q-042a..f.
/// </summary>
public static class SyncEndpoints
{
    public sealed record WebhookBody(string? Name, string? Url, string? Kind);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));
    private static IResult Bad(string message) => Results.Json(new { guard = "InvalidFilter", message }, statusCode: StatusCodes.Status400BadRequest);

    public static void MapSync(this RouteGroupBuilder api)
    {
        // GET /sync/alerts?source&kind&train&state=all|open|resolved&days=7&limit=200
        //   -> { items[{id, trainId, trainTitle, source, kind, fingerprint, message, occurrenceCount, firstOccurredAt, lastOccurredAt, isResolved, resolvedAt, resolvedByUserId, resolvedByName, version}],
        //        openCount, resolvedCount, resolvedWithinDays, limit, asOf }. Open first, each group newest first (by lastOccurredAt); resolved rows only within `days`.
        api.MapGet("/sync/alerts", async (HttpRequest req, SyncHealthService s, CancellationToken ct) =>
        {
            var q = req.Query;
            string? Get(string k) => q[k].ToString() is { Length: > 0 } v ? v : null;
            var state = (Get("state") ?? "all").ToLowerInvariant();
            if (!SyncHealthService.States.Contains(state)) return Bad("state is all, open or resolved");
            var days = SyncHealthService.DefaultResolvedDays;
            if (Get("days") is { } d && (!int.TryParse(d, out days) || days < 1 || days > SyncHealthService.MaxResolvedDays)) return Bad($"days is a whole number from 1 to {SyncHealthService.MaxResolvedDays}");
            var limit = SyncHealthService.DefaultLimit;
            if (Get("limit") is { } l && (!int.TryParse(l, out limit) || limit < 1)) return Bad("limit is a positive whole number");
            return Results.Ok(await s.ListAlertsAsync(new AlertFilter(Get("source"), Get("kind"), Get("train"), state, days, limit), ct));
        }).RequireAuthorization(Policies.Read);

        // POST /sync/alerts/{id}:resolve (If-Match: version) -> the resolved alert row. 422 AlertAlreadyResolved, 409 with the current row.
        api.MapPost("/sync/alerts/{id}:resolve", async (string id, ClaimsPrincipal u, HttpRequest r, SyncHealthService s, CancellationToken ct) =>
            (await s.ResolveAsync(id, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        // GET /sync/state (alias /sync/health) -> { connectorWide, failing[{scope, source, kind, reasons[], message, since, alertId, links}], connectors[{source, baseUrl, isEnabled,
        //   lastCycleStartedAt, lastCycleCompletedAt, lastSuccessAt, consecutiveFailures, version, links{total,inSync,mismatch,broken,unsynced,stale}, openAlerts, state}],
        //   openAlerts, watchdog{stalled, alertId, lastCycleCompletedAt}, asOf }. `connectorWide` drives the banner on every screen.
        Delegate state = async (SyncHealthService s, CancellationToken ct) => Results.Ok(await s.GetStateAsync(ct));
        api.MapGet("/sync/state", state).RequireAuthorization(Policies.Read);
        api.MapGet("/sync/health", state).RequireAuthorization(Policies.Read);

        // GET /sync/mismatches -> [{id, trainId, trainTitle, entityType, entityId, source, externalKey, expected, reported, syncState, lastSyncedAt}] (Mismatch, NotFound, AuthFailed links)
        api.MapGet("/sync/mismatches", async (SyncHealthService s, CancellationToken ct) => Results.Ok(await s.ListProblemLinksAsync(200, ct))).RequireAuthorization(Policies.Read);

        // Webhook allowlist (D13). The stored URL is a secret (its path is the token), so rows carry host and an elided displayUrl only.
        // GET -> [{id, name, kind, host, displayUrl, version, usedByTeams, usedByDispatches}]
        api.MapGet("/sync/webhook-allowlist", async (SyncHealthService s, CancellationToken ct) => Results.Ok(await s.ListWebhooksAsync(ct))).RequireAuthorization(Policies.Read);
        // POST {name, url, kind?} -> the new row. 422 WebhookNotHttps | WebhookCredentialsInUrl | WebhookPrivateTarget | WebhookInvalidUrl | WebhookDuplicate | WebhookInvalidInput
        api.MapPost("/sync/webhook-allowlist", async (WebhookBody b, ClaimsPrincipal u, SyncHealthService s, CancellationToken ct) =>
            (await s.AddWebhookAsync(b.Name, b.Url, b.Kind, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        // DELETE /{id} (If-Match: version) -> the removed row. 422 WebhookInUse (a team or a recorded dispatch names it), 409 stale version.
        api.MapDelete("/sync/webhook-allowlist/{id}", async (string id, ClaimsPrincipal u, HttpRequest r, SyncHealthService s, CancellationToken ct) =>
            (await s.RemoveWebhookAsync(id, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }
}
