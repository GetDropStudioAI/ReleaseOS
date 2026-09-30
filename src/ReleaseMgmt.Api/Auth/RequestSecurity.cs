using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// REOS-53 (Q-053b): CSRF defence for cookie authentication. The session cookie is SameSite=Lax and HttpOnly; on top of that a browser request that
/// changes state (POST/PUT/PATCH/DELETE under /api, /auth or /hub) must come from this site. The browser states that itself in <c>Sec-Fetch-Site</c>
/// (same-origin, or none for a typed URL); when the header is absent the <c>Origin</c> host must be this host or listed in <c>Security:AllowedOrigins</c>.
/// A request with neither header is not a browser (the cookie is not ambient authority there) and passes. Refusals are 403 <c>CrossSiteRequest</c>.
/// The OIDC callback (/signin-oidc, a cross-site form post from the identity provider) is outside these prefixes on purpose.
/// </summary>
public sealed class CrossSiteRequestGuard(RequestDelegate next, IConfiguration config, ILogger<CrossSiteRequestGuard> log)
{
    public const string Guard = "CrossSiteRequest";
    private static readonly string[] Prefixes = ["/api/", "/auth/", "/hub"];
    private readonly HashSet<string> _allowed = new((config.GetSection("Security:AllowedOrigins").Get<string[]>() ?? []).Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext http)
    {
        var req = http.Request;
        if (!(HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method) || HttpMethods.IsOptions(req.Method) || HttpMethods.IsTrace(req.Method))
            && Prefixes.Any(p => (req.Path.Value ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase))
            && !IsSameSite(req))
        {
            log.LogWarning("Refused a cross-site {Method} {Path} (Origin {Origin}, Sec-Fetch-Site {Site})", req.Method, req.Path, req.Headers.Origin.ToString(), req.Headers["Sec-Fetch-Site"].ToString());
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { guard = Guard, message = "This request came from another site and was refused" });
            return;
        }
        await next(http);
    }

    private bool IsSameSite(HttpRequest req)
    {
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (site is "same-origin" or "none") return true;
        var origin = req.Headers.Origin.ToString();
        if (site.Length == 0 && origin.Length == 0) return true;   // not a browser
        if (origin.Length == 0) return false;                       // a browser says cross-site/same-site and gives no Origin to check
        if (_allowed.Contains(origin.TrimEnd('/'))) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || (o.Scheme != Uri.UriSchemeHttp && o.Scheme != Uri.UriSchemeHttps)) return false;   // "null" and file:// are refused
        var host = req.Host;
        return string.Equals(o.Host, host.Host, StringComparison.OrdinalIgnoreCase) && (host.Port is null || host.Port == o.Port);
    }
}

/// <summary>Headers every response gets unless a handler already set them: no MIME sniffing, no framing (clickjacking), no referrer leak.</summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h.TryAdd("X-Content-Type-Options", "nosniff");
            h.TryAdd("X-Frame-Options", "DENY");
            h.TryAdd("Referrer-Policy", "no-referrer");
            return Task.CompletedTask;
        });
        return next(http);
    }
}

/// <summary>
/// REOS-53 (Q-053c): a session cookie outlives a deactivated (or deleted) user. Every cookie-authenticated request re-checks that the <c>uid</c> claim still
/// names an active user, at most every <c>Auth:SessionRecheckSeconds</c> (default 10) per user; otherwise the cookie is rejected and the next request is 401.
/// </summary>
public sealed class SessionValidator(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config)
{
    private readonly ConcurrentDictionary<string, (bool Active, DateTime At)> _seen = new();
    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(0, config.GetValue("Auth:SessionRecheckSeconds", 10)));

    public async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var uid = ctx.Principal?.FindFirst("uid")?.Value;
        if (uid is null) return;   // not one of ours (the OIDC handler always adds uid); authorization decides
        var now = time.GetUtcNow().UtcDateTime;
        if (!(_seen.TryGetValue(uid, out var s) && now - s.At < Ttl))
        {
            await using var db = await dbf.CreateDbContextAsync(ctx.HttpContext.RequestAborted);
            var active = await db.Set<Users>().AsNoTracking().AnyAsync(u => u.Id == uid && u.IsActive, ctx.HttpContext.RequestAborted);
            _seen[uid] = s = (active, now);
        }
        if (s.Active) return;
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
