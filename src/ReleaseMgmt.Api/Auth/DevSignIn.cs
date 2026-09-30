using System.Net;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-B1/B2 (docs/security/scan-authn.md, Q-SEC-B1): <c>POST /auth/dev-login</c> lets anyone pick any email and any role, so it and the dev tools under
/// <c>/api/v1/dev</c> exist only when all of these hold:
/// <list type="bullet">
/// <item>the environment is Development (never elsewhere, whatever the configuration says);</item>
/// <item>organisation sign-in (<c>Auth:Oidc:Authority</c>) is not configured, unless <c>Auth:DevLogin:Enabled=true</c> opts in explicitly
/// (<c>Auth:DevLogin:Enabled=false</c> turns them off in Development too);</item>
/// <item>per request, the caller is on this machine: a loopback peer address <b>and</b> a loopback Host name. The Host check is what stops DNS rebinding,
/// where a web page whose name now resolves to 127.0.0.1 is same-origin to the browser and so passes the cross-site guard.</item>
/// </list>
/// </summary>
public static class DevSignIn
{
    public const string Guard = "DevLoginLocalOnly";

    public static bool Enabled(IHostEnvironment env, IConfiguration config, ILogger log)
    {
        var wanted = config.GetValue<bool?>("Auth:DevLogin:Enabled");
        if (!env.IsDevelopment())
        {
            if (wanted == true) log.LogWarning("Auth:DevLogin:Enabled is ignored: the development sign-in exists only in the Development environment");
            return false;
        }
        var organisation = !string.IsNullOrWhiteSpace(config["Auth:Oidc:Authority"]);
        var on = wanted ?? !organisation;
        if (on) log.LogWarning("Development sign-in is enabled (POST /auth/dev-login, callers on this machine only){Beside}", organisation ? " beside organisation sign-in, by Auth:DevLogin:Enabled" : "");
        return on;
    }

    /// <summary>True when the request comes from this machine and names it. No peer address means the in-process test server or a Unix socket.</summary>
    public static bool IsLocal(HttpContext http)
    {
        if (http.Connection.RemoteIpAddress is { } peer && !IsLoopback(peer)) return false;
        var host = http.Request.Host.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)   // RFC 6761: browsers resolve these to loopback themselves
            || (IPAddress.TryParse(host, out var ip) && IsLoopback(ip));
    }

    private static bool IsLoopback(IPAddress a) => IPAddress.IsLoopback(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);

    public static IResult Refuse(HttpContext http, ILogger log)
    {
        log.LogWarning("Refused a development sign-in from {Peer} for host {Host}: callers on this machine only", http.Connection.RemoteIpAddress?.ToString() ?? "(none)", http.Request.Host.Value);
        return Results.Json(new { guard = Guard, message = "The development sign-in answers only on this machine (localhost or 127.0.0.1)." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
