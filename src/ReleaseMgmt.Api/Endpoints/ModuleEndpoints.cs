using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-174 (D56, D57, D63): add-on modules per workspace. Anyone signed in reads the list (the navigation needs it); enabling and disabling are Admin (RTE or Release Manager in the
/// current workspace), with If-Match. A module is enabled by a button and a confirmation dialog in the UI, never a toggle (CLAUDE.md rule 9); the API is two POST actions like the
/// other state changes (<c>:enable</c>, <c>:disable</c>), not a PATCH of a flag.
/// </summary>
public static class ModuleEndpoints
{
    public static void MapModules(this RouteGroupBuilder api)
    {
        // GET /modules -> [{ key, name, summary, turnsOn[], enabled, entitled, entitlementReason, planSource, enabledBy, enabledAt, lastChangedAt, version }]
        api.MapGet("/modules", async (ModuleService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/modules/{key}:enable", async (string key, ClaimsPrincipal u, HttpRequest r, ModuleService s, CancellationToken ct) =>
            (await s.EnableAsync(key, new Actor(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim")), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/modules/{key}:disable", async (string key, ClaimsPrincipal u, HttpRequest r, ModuleService s, CancellationToken ct) =>
            (await s.DisableAsync(key, new Actor(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim")), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }

    /// <summary>
    /// The API guard: while the module is off in the caller's workspace the endpoint answers 404, exactly as a route that does not exist (the body is empty too). Put it on a route group or an
    /// endpoint of an add-on module: <c>api.MapGroup("/cab").RequireModule(ModuleKeys.Cab)</c>. It runs after authorisation, so a caller who may not use the endpoint at all still gets 401 or 403.
    /// </summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, string moduleKey) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var ws = http.User.FindFirstValue(WorkspaceResolution.Claim);
            if (ws is null || ws == WorkspaceResolution.Nowhere || !await http.RequestServices.GetRequiredService<IModuleGate>().IsEnabledAsync(ws, moduleKey, http.RequestAborted))
                return Results.NotFound();
            return await next(ctx);
        });
}
