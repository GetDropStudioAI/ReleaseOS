using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Freeze windows, immutable overrides (renew = new row, D29) and the waiver list for the gate Inspector (REOS-34).</summary>
public static class FreezeEndpoints
{
    public sealed record FreezeBody(string Name, string? Kind, DateTime StartsAt, DateTime EndsAt, string? ProductPattern);
    public sealed record OverrideBody(string TrainId, string RequestedByUserId, string Reason, DateTime ExpiresAt);
    public sealed record OverrideRequestBody(string TrainId, string Reason, DateTime? ExpiresAt);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapFreezes(this RouteGroupBuilder api)
    {
        api.MapGet("/freezes", async (string? trainId, FreezeService s, CancellationToken ct) => Results.Ok(await s.ListAsync(trainId, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/freezes", (FreezeBody b, ClaimsPrincipal u, FreezeService s, CancellationToken ct) =>
            s.CreateAsync(new(b.Name, b.Kind, b.StartsAt, b.EndsAt, b.ProductPattern), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.FreezeApprove);
        // Immutable: there is deliberately no PUT/PATCH/DELETE. A renewal is another POST.
        api.MapPost("/freezes/{id}/overrides", (string id, OverrideBody b, ClaimsPrincipal u, FreezeService s, CancellationToken ct) =>
            s.GrantOverrideAsync(id, new(b.TrainId, b.RequestedByUserId, b.Reason, b.ExpiresAt), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.FreezeApprove);
        api.MapPost("/freezes/{id}/override-requests", async (string id, OverrideRequestBody b, ClaimsPrincipal u, FreezeService s, CancellationToken ct) =>
        {
            var r = await s.RequestOverrideAsync(id, b.TrainId, b.Reason, b.ExpiresAt, ActorOf(u), ct);
            return r.IsOk ? Results.Ok(new { notified = r.Value }) : r.ToHttp();
        }).RequireAuthorization(Policies.Plan);

        api.MapGet("/gates/{id}/waivers", async (string id, WaiverService s, CancellationToken ct) => Results.Ok(await s.ListAsync(id, ct))).RequireAuthorization(Policies.Read);
    }
}
