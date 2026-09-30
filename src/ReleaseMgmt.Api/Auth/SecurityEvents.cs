using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-E4 (OWASP Top 10:2025 A09, ASVS V16): the security events an operator needs to see, logged under this category whatever the framework log
/// levels are. Values are ids, roles, methods and paths only: never tokens, cookies or request bodies (SEC-B10 and SEC-D5 still apply on top).
/// </summary>
public static class SecurityEvents
{
    private static ILogger Log(HttpContext http) => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReleaseMgmt.Security");
    private static string? Uid(ClaimsPrincipal? p) => p?.FindFirst("uid")?.Value;

    public static void SignedIn(CookieSigningInContext ctx) =>
        Log(ctx.HttpContext).LogInformation("Signed in {UserId} as {Role} via {Method}", Uid(ctx.Principal), ctx.Principal?.FindFirst(ClaimTypes.Role)?.Value,
            ctx.HttpContext.Request.Path.StartsWithSegments("/auth/dev-login") ? "dev-login" : "organisation");

    public static void SignedOut(HttpContext http, ClaimsPrincipal? who) =>
        Log(http).LogInformation("Signed out {UserId}", Uid(who));

    /// <summary>403 from a policy or a handler's Forbid: who asked for what. Warning, so a burst of them stands out.</summary>
    public static Task AccessDenied(RedirectContext<CookieAuthenticationOptions> ctx)
    {
        Log(ctx.HttpContext).LogWarning("Access denied to {UserId} ({Role}) for {Method} {Path}", Uid(ctx.HttpContext.User),
            ctx.HttpContext.User.FindFirst(ClaimTypes.Role)?.Value, ctx.Request.Method, ctx.Request.Path.Value);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>401: no session or an expired one. Debug only: every page load before sign-in asks /api/v1/me.</summary>
    public static Task Unauthenticated(RedirectContext<CookieAuthenticationOptions> ctx)
    {
        Log(ctx.HttpContext).LogDebug("Unauthenticated {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value);
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public static void SessionEndedForInactiveUser(HttpContext http, string userId) =>
        Log(http).LogWarning("Session ended for {UserId}: the user is deactivated or deleted", userId);
}
