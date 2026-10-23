using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-106 (D45): the workspace switcher's list, creating a workspace and managing the current workspace's members. The list is the person's own
/// (any signed-in role); creating and every member endpoint are Admin (RTE or Release Manager in the current workspace, D14/D32). A membership change
/// takes effect on the person's next request: the workspace and role are read from the membership on every request (Q-105a).
/// </summary>
public static class WorkspaceAdminEndpoints
{
    public sealed record NewWorkspace(string? Name, string? Slug, string? Region);
    public sealed record NewMember(string? Email, string? DisplayName, string? Role);
    public sealed record RoleBody(string? Role);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapWorkspaceAdmin(this RouteGroupBuilder api)
    {
        // GET /workspaces -> [{ id, name, slug, region, role }]: the workspaces the caller belongs to (the switcher)
        api.MapGet("/workspaces", async (ClaimsPrincipal u, WorkspaceMembersService s, CancellationToken ct) => Results.Ok(await s.ListMineAsync(ActorOf(u).UserId, ct)))
            .RequireAuthorization(Policies.Read);
        // POST /workspaces { name, slug, region } -> the new workspace; the caller is its first member
        api.MapPost("/workspaces", async (NewWorkspace b, ClaimsPrincipal u, WorkspaceMembersService s, CancellationToken ct) =>
            (await s.CreateAsync(b.Name, b.Slug, b.Region, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        api.MapGet("/workspace/members", async (WorkspaceMembersService s, CancellationToken ct) => Results.Ok(await s.ListMembersAsync(ct))).RequireAuthorization(Policies.Admin);
        api.MapPost("/workspace/members", async (NewMember b, ClaimsPrincipal u, WorkspaceMembersService s, CancellationToken ct) =>
            (await s.AddMemberAsync(b.Email, b.DisplayName, b.Role, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPatch("/workspace/members/{userId}", async (string userId, RoleBody b, ClaimsPrincipal u, HttpRequest r, WorkspaceMembersService s, CancellationToken ct) =>
            (await s.ChangeRoleAsync(userId, b.Role, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/workspace/members/{userId}", async (string userId, ClaimsPrincipal u, HttpRequest r, WorkspaceMembersService s, CancellationToken ct) =>
            (await s.RemoveMemberAsync(userId, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }
}
