using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Webhooks;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Outbound webhooks (REOS-164, D61), the Admin screen's endpoints: RTE and Release Manager list, make, change, pause, rotate, test, delete and read the delivery log and replay deliveries of the workspace's subscriptions
/// (a Viewer or Governance Officer gets 403). The secret is returned once, in the answer to make or rotate; nothing else ever has it. Nothing here sets a status: pause and resume are actions of the service.
/// </summary>
public static class WebhookEndpoints
{
    public sealed record CreateBody(string? Name, string? Url, IReadOnlyList<string>? EventTypes);
    public sealed record UpdateBody(string? Name, string? Url, IReadOnlyList<string>? EventTypes);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapWebhooks(this RouteGroupBuilder api)
    {
        api.MapGet("/webhooks", async (WebhookSubscriptionService s, WebhookOutOptions o, CancellationToken ct) => Results.Ok(new
        {
            events = WebhookCatalog.All, allEvents = WebhookCatalog.AllEvents,
            allowList = new { mode = o.Mode, hosts = o.AllowedHosts, allowPrivateTargets = o.AllowPrivateTargets },
            limits = new { maxSubscriptions = o.MaxPerWorkspace, maxAttempts = o.MaxAttempts, timeoutSeconds = o.Timeout.TotalSeconds, retentionDays = o.RetentionDays, replayWindowSeconds = ControlSignature.ToleranceSeconds },
            subscriptions = await s.ListAsync(ct),
        })).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks", async (CreateBody b, ClaimsPrincipal u, WebhookSubscriptionService s, CancellationToken ct) =>
        {
            var r = await s.CreateAsync(b.Name, b.Url, b.EventTypes, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);
        api.MapPut("/webhooks/{id}", async (string id, UpdateBody b, ClaimsPrincipal u, HttpRequest req, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.UpdateAsync(id, b.Name, b.Url, b.EventTypes, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks/{id}:pause", async (string id, ClaimsPrincipal u, HttpRequest req, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.PauseAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks/{id}:resume", async (string id, ClaimsPrincipal u, HttpRequest req, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.ResumeAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks/{id}:rotate-secret", async (string id, ClaimsPrincipal u, HttpRequest req, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.RotateSecretAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks/{id}:test", async (string id, ClaimsPrincipal u, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.TestAsync(id, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/webhooks/{id}", async (string id, ClaimsPrincipal u, HttpRequest req, WebhookSubscriptionService s, CancellationToken ct) =>
            (await s.DeleteAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapGet("/webhooks/deliveries", async (string? subscriptionId, string? status, int? take, WebhookSubscriptionService s, CancellationToken ct) =>
            Results.Ok(new { deliveries = await s.ListDeliveriesAsync(subscriptionId, status, take ?? 100, ct) })).RequireAuthorization(Policies.Admin);
        api.MapGet("/webhooks/deliveries/{id}", async (string id, WebhookSubscriptionService s, CancellationToken ct) =>
            await s.GetDeliveryAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound(new { message = "Webhook delivery not found" })).RequireAuthorization(Policies.Admin);
        api.MapPost("/webhooks/deliveries/{id}:replay", async (string id, ClaimsPrincipal u, WebhookSubscriptionService s, CancellationToken ct) =>
        {
            var r = await s.ReplayAsync(id, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);
    }
}
