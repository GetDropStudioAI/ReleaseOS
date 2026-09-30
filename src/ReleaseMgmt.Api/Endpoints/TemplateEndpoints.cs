using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Train templates (REOS-38, PROJECT_SCOPE section 6): read for every role, draft create/edit for RTE and Release Manager (admin, v1),
/// approve and retire for Release Manager and Governance Officer (Q-038t1). No endpoint sets a Status: approve and retire are actions.
/// </summary>
public static class TemplateEndpoints
{
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static void Approvers(AuthorizationPolicyBuilder p) => p.RequireRole(Roles.ReleaseManager, Roles.GovernanceOfficer);

    public static void MapTemplates(this RouteGroupBuilder api)
    {
        api.MapGet("/templates", async (TemplateService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/templates/library-options", async (TemplateService s, CancellationToken ct) => Results.Ok(await s.LibraryOptionsAsync(ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/templates/{id}", async (string id, TemplateService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "template not found" })).RequireAuthorization(Policies.Read);

        api.MapPost("/templates", (TemplateInput b, ClaimsPrincipal u, TemplateService s, CancellationToken ct) =>
            s.CreateAsync(b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Admin);
        api.MapPut("/templates/{id}", (string id, TemplateInput b, ClaimsPrincipal u, HttpRequest r, TemplateService s, CancellationToken ct) =>
            s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Admin);
        api.MapPost("/templates/{id}:approve", (string id, ClaimsPrincipal u, HttpRequest r, TemplateService s, CancellationToken ct) =>
            s.ApproveAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Approvers);
        api.MapPost("/templates/{id}:retire", (string id, ClaimsPrincipal u, HttpRequest r, TemplateService s, CancellationToken ct) =>
            s.RetireAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Approvers);
    }
}
