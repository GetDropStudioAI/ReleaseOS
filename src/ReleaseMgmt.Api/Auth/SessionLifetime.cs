using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// Session limits for the cookie (docs/security/scan-authn.md, Q-SEC-B4 and Q-SEC-B5).
/// <list type="bullet">
/// <item>SEC-B4 idle timeout: the cookie ticket slides, and lapses after <c>Auth:Session:IdleMinutes</c> (default 60) without a request. An open tab keeps it
/// alive (the app polls every minute).</item>
/// <item>SEC-B4 absolute lifetime: every session ends <c>Auth:Session:AbsoluteHours</c> (default 12) after sign-in, however busy, so the identity provider is
/// asked again at least that often. That is where a leaver disabled at the IdP, or a role changed there (F10), finally reaches this app.</item>
/// <item>SEC-B5 sign-out: the session id of a signed-out cookie is remembered until the session could have lived no longer, so a copy of the cookie is refused
/// too. The list is in memory: after a restart a copied cookie works again until its absolute lifetime ends (a durable list needs a table: Q-SEC-B5).</item>
/// </list>
/// REOS-63: the session id and sign-in time are also claims, so a SignalR connection (which sees the principal, not the ticket) can be matched to its session;
/// a sign-out or a refused session closes that session's live connections (<see cref="Realtime.HubConnections"/>).
/// Time comes from the injected <see cref="TimeProvider"/>, the same clock the cookie handler uses.
/// </summary>
public sealed class SessionLifetime(TimeProvider time, IConfiguration config, ILogger<SessionLifetime> log, Realtime.HubConnections hubs)
{
    public const string SignedInItem = ".reos.signedin";
    public const string SessionItem = ".reos.sid";
    public const string SessionClaim = "reos_sid";
    public const string SignedInClaim = "reos_signedin";
    private readonly ConcurrentDictionary<string, DateTime> _signedOut = new();

    public static TimeSpan Idle(IConfiguration config) => TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Auth:Session:IdleMinutes", 60.0)));
    public TimeSpan Absolute => TimeSpan.FromHours(Math.Max(1.0 / 60, config.GetValue("Auth:Session:AbsoluteHours", 12.0)));

    /// <summary>Cookie <c>OnSigningIn</c>: stamps the sign-in time and a fresh session id. Renewals keep both (the handler copies the items).</summary>
    public Task OnSigningIn(CookieSigningInContext ctx)
    {
        var at = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        ctx.Properties.Items[SignedInItem] = at;
        ctx.Properties.Items[SessionItem] = sid;
        if (ctx.Principal?.Identity is ClaimsIdentity id)
        {
            foreach (var c in id.FindAll(c => c.Type is SessionClaim or SignedInClaim).ToList()) id.RemoveClaim(c);
            id.AddClaim(new Claim(SessionClaim, sid));
            id.AddClaim(new Claim(SignedInClaim, at));
        }
        return Task.CompletedTask;
    }

    /// <summary>Cookie <c>OnValidatePrincipal</c>, before <see cref="SessionValidator"/>. False when the session was ended here (the caller stops).</summary>
    public async Task<bool> ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string? why = null;
        ctx.Properties.Items.TryGetValue(SessionItem, out var sid);
        if (SignedInAt(ctx.Properties) is not { } at) why = "it has no sign-in time (issued before session limits existed)";
        else if (now - at >= Absolute) why = "it reached the absolute session lifetime";
        else if (sid is not null && _signedOut.ContainsKey(sid)) why = "it was signed out";
        if (why is null) return true;
        log.LogInformation("Session cookie refused: {Reason}", why);
        hubs.AbortSession(sid, ctx.Principal?.FindFirst("uid")?.Value, "session cookie refused: " + why);   // REOS-63
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return false;
    }

    /// <summary>Remembers a signed-out session until it would have expired anyway, and closes its live connections (REOS-63).</summary>
    public void SignedOut(AuthenticationProperties? properties, ClaimsPrincipal? who = null)
    {
        string? sid = null;
        properties?.Items.TryGetValue(SessionItem, out sid);
        if (sid is not null || who?.FindFirst("uid")?.Value is not null) hubs.AbortSession(sid, who?.FindFirst("uid")?.Value, "signed out");
        if (properties is null || sid is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        _signedOut[sid] = (SignedInAt(properties) ?? now) + Absolute;
        foreach (var (k, until) in _signedOut)
            if (until < now) _signedOut.TryRemove(k, out _);
    }

    /// <summary>True while a signed-out session id is remembered (REOS-63: the hub monitor closes connections of such a session).</summary>
    public bool IsSignedOut(string sessionId) => _signedOut.ContainsKey(sessionId);

    private static DateTime? SignedInAt(AuthenticationProperties p) =>
        p.Items.TryGetValue(SignedInItem, out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
}
