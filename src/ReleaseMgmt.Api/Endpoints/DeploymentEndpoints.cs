using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Deployments (REOS-110, D42). People read them (any signed-in role). The deployment tool pushes them to <c>/api/v1/ingest/*</c> with a per-workspace API key (<see cref="Policies.Ingest"/>: no
/// cookie, no role). Keys are created and revoked by Admin (RTE, Release Manager); the secret is returned once, at creation.
/// </summary>
public static class DeploymentEndpoints
{
    public sealed record IngestKeyBody(string? Name);
    public sealed record IngestEventsBody(string? DeploymentExternalId, IReadOnlyList<IngestEvent>? Events);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static IngestPrincipal Key(ClaimsPrincipal u) => IngestKeyAuthentication.PrincipalOf(u) ?? throw new InvalidOperationException("Ingest request without a key principal");

    public static void MapDeployments(this RouteGroupBuilder api)
    {
        api.MapGet("/deployments", async (string? environmentId, string? serviceId, string? trainId, string? state, int? take, DeploymentService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(new(environmentId, serviceId, trainId, state, take ?? 100), ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/deployments/{id}", async (string id, int? events, DeploymentService s, CancellationToken ct) =>
            await s.GetAsync(id, events ?? 200, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "deployment not found" })).RequireAuthorization(Policies.Read);
        api.MapGet("/trains/{id}/deployments", async (string id, DeploymentService s, CancellationToken ct) => Results.Ok(await s.ListAsync(new(TrainId: id, Take: 500), ct)))
            .RequireAuthorization(Policies.Read).TrainInRoute();
        // Per-environment state of each service in the release (B6).
        api.MapGet("/trains/{id}/environments", async (string id, DeploymentService s, CancellationToken ct) =>
            await s.ReleaseStateAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);

        api.MapGet("/ingest-keys", async (IngestKeyService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequireAuthorization(Policies.Admin);
        api.MapPost("/ingest-keys", async (IngestKeyBody b, ClaimsPrincipal u, IngestKeyService s, CancellationToken ct) =>
        {
            var r = await s.CreateAsync(b.Name, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);
        api.MapPost("/ingest-keys/{id}:revoke", async (string id, ClaimsPrincipal u, HttpRequest r, IngestKeyService s, CancellationToken ct) =>
        {
            var res = await s.RevokeAsync(id, ActorOf(u), r.IfMatch(), ct);
            return res.IsOk ? Results.Ok((await s.ListAsync(ct)).First(k => k.Id == id)) : res.ToHttp();
        }).RequireAuthorization(Policies.Admin);

        // The push ingest API. Idempotent: the deployment by its externalId, an event by its externalId (when it has one); a replay changes nothing.
        api.MapPost("/ingest/deployments", async (IngestDeployment b, ClaimsPrincipal u, DeploymentService s, CancellationToken ct) =>
        {
            var r = await s.UpsertAsync(b, Key(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: r.Value!.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK) : r.ToHttp();
        }).RequireAuthorization(Policies.Ingest);
        api.MapPost("/ingest/events", (IngestEventsBody b, ClaimsPrincipal u, DeploymentService s, CancellationToken ct) =>
            s.AddEventsAsync(b.DeploymentExternalId, b.Events, Key(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Ingest);
    }
}
