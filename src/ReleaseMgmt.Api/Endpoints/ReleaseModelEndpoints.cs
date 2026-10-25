using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// The release model (REOS-107): a release's key, owner, team, branch, on-call, type and target environments, the workspace's key prefix, the release types (RT1, D46), the clients (RT3, D47) and the versions that chain a product version's releases (RT2, D48).
/// Read: any signed-in role. Edit: planners (RTE, Release Manager); the key prefix is an Admin setting (same two roles in v1). Every edit needs If-Match (Q-004) and answers 409 with the current row when stale.
/// </summary>
public static class ReleaseModelEndpoints
{
    public sealed record KeyPrefixBody(string? KeyPrefix);
    public sealed record VersionNameBody(string? Name);
    public sealed record TrainVersionBody(string? VersionId);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    /// <summary>Runs a write, then answers with the row's view (the shape a screen reads) instead of the bare entity.</summary>
    internal static async Task<IResult> Viewed<T, V>(Task<ServiceResult<T>> write, Func<T, Task<V?>> view) where V : class
    {
        var r = await write;
        return r.IsOk ? Results.Ok(await view(r.Value!)) : r.ToHttp();
    }

    public static void MapReleaseModel(this RouteGroupBuilder api)
    {
        api.MapGet("/trains/{id}/release", async (string id, ReleaseModelService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPatch("/trains/{id}/release", (string id, ReleasePatch b, ClaimsPrincipal u, HttpRequest r, ReleaseModelService s, CancellationToken ct) =>
            s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapPatch("/workspace", (KeyPrefixBody b, ClaimsPrincipal u, HttpRequest r, WorkspaceService s, CancellationToken ct) =>
            s.SetKeyPrefixAsync(b.KeyPrefix, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Admin);

        api.MapGet("/release-types", async (bool? includeArchived, ReleaseTypeService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/release-types", (NewReleaseType b, ClaimsPrincipal u, ReleaseTypeService s, CancellationToken ct) =>
            Viewed(s.CreateAsync(b, ActorOf(u), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPatch("/release-types/{id}", (string id, ReleaseTypePatch b, ClaimsPrincipal u, HttpRequest r, ReleaseTypeService s, CancellationToken ct) =>
            Viewed(s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/release-types/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, ReleaseTypeService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/release-types/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, ReleaseTypeService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);

        // RT2 (D48): versions. Read: any signed-in role. Edit: planners; every edit needs If-Match and answers 409 with the current row when stale.
        api.MapGet("/versions", async (bool? includeArchived, ReleaseVersionService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/versions/{id}", async (string id, ReleaseVersionService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "version not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/versions", (VersionNameBody b, ClaimsPrincipal u, ReleaseVersionService s, CancellationToken ct) =>
            Viewed(s.CreateAsync(b.Name, ActorOf(u), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPatch("/versions/{id}", (string id, VersionNameBody b, ClaimsPrincipal u, HttpRequest r, ReleaseVersionService s, CancellationToken ct) =>
            Viewed(s.RenameAsync(id, b.Name, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/versions/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, ReleaseVersionService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/versions/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, ReleaseVersionService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapGet("/trains/{id}/version", async (string id, ReleaseVersionService s, CancellationToken ct) =>
            await s.ForTrainAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPut("/trains/{id}/version", (string id, TrainVersionBody b, ClaimsPrincipal u, HttpRequest r, ReleaseVersionService s, CancellationToken ct) =>
            s.SetTrainVersionAsync(id, b.VersionId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:create-next", (string id, NextReleaseInput? b, ClaimsPrincipal u, ReleaseVersionService s, CancellationToken ct) =>
            s.CreateNextAsync(id, b ?? new NextReleaseInput(), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        // RT3 (D47): the workspace's clients, Standard or On-Demand. Read: any signed-in role. Create, edit, archive: planners. Edits need If-Match.
        api.MapGet("/clients", async (bool? includeArchived, ClientService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/clients/{id}", async (string id, ClientService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "client not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/clients", (NewClient b, ClaimsPrincipal u, ClientService s, CancellationToken ct) =>
            Viewed(s.CreateAsync(b, ActorOf(u), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPatch("/clients/{id}", (string id, ClientPatch b, ClaimsPrincipal u, HttpRequest r, ClientService s, CancellationToken ct) =>
            Viewed(s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/clients/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, ClientService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
        api.MapPost("/clients/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, ClientService s, CancellationToken ct) =>
            Viewed(s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct), x => s.GetAsync(x.Id, ct))).RequireAuthorization(Policies.Plan);
    }
}
