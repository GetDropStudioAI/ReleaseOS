using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>What the app shell reads (REOS-94): the current workspace for the sidebar and breadcrumb, and the approval queue behind the Approvals badge.</summary>
public static class ShellEndpoints
{
    public static void MapShell(this RouteGroupBuilder api)
    {
        // GET /workspace -> { id, name, slug, region }
        api.MapGet("/workspace", async (WorkspaceService s, CancellationToken ct) => Results.Ok(await s.CurrentAsync(ct))).RequireAuthorization(Policies.Read);

        // GET /approvals -> { counts: { certify, waivers, overrides, goNoGo, conditions, total }, items[] }: what waits for the caller's decision
        api.MapGet("/approvals", async (ClaimsPrincipal u, ApprovalQueueService s, CancellationToken ct) =>
            Results.Ok(await s.GetAsync(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"), ct))).RequireAuthorization(Policies.Read);
    }
}
