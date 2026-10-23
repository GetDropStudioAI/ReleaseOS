using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Environments (REOS-109, B6, D42) and the service catalog (B8). Read: any signed-in role. Create, edit, archive and link: planners (RTE, Release Manager). Edits need If-Match.</summary>
public static class EnvironmentEndpoints
{
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapEnvironmentsAndServices(this RouteGroupBuilder api)
    {
        api.MapGet("/environments", async (bool? includeArchived, EnvironmentService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/environments/{id}", async (string id, EnvironmentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "environment not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/environments", (NewEnvironment b, ClaimsPrincipal u, EnvironmentService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.CreateAsync(b, ActorOf(u), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPatch("/environments/{id}", (string id, EnvironmentPatch b, ClaimsPrincipal u, HttpRequest r, EnvironmentService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/environments/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, EnvironmentService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/environments/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, EnvironmentService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);

        api.MapGet("/services", async (bool? includeArchived, string? q, ServiceCatalogService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, q, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/services/{id}", async (string id, ServiceCatalogService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "service not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/services", (NewCatalogService b, ClaimsPrincipal u, ServiceCatalogService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.CreateAsync(b, ActorOf(u), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPatch("/services/{id}", (string id, CatalogServicePatch b, ClaimsPrincipal u, HttpRequest r, ServiceCatalogService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/services/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, ServiceCatalogService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/services/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, ServiceCatalogService s, CancellationToken ct) =>
            ReleaseModelEndpoints.Viewed(s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);

        // A product of a train is a service in the catalog: link it by id or key, or send {} to remove the link. If-Match is the product's Version.
        api.MapPut("/products/{id}/service", (string id, ProductServiceLink b, ClaimsPrincipal u, HttpRequest r, ServiceCatalogService s, CancellationToken ct) =>
            s.LinkProductAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
