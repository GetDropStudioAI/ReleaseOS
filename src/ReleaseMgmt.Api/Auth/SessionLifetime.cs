using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// Session limits for the cookie (docs/security/scan-authn.md, Q-SEC-B4 and Q-SEC-B5).
/// <list type="bullet">
/// <item>SEC-B4 idle timeout: the cookie ticket slides, and lapses after <c>Auth:Session:IdleMinutes</c> (default 60) without a request. An open tab keeps it
/// alive (the app polls every minute).</item>
/// <item>SEC-B4 absolute lifetime: every session ends <c>Auth:Session:AbsoluteHours</c> (default 12) after sign-in, however busy, so the identity provider is
/// asked again at least that often. That is where a leaver disabled at the IdP, or a role changed there (F10), finally reaches this app.</item>
/// <item>SEC-B5 sign-out: the session id of a signed-out cookie is refused until the session could have lived no longer, so a copy of the cookie is refused
/// too. REOS-62: the list is kept in <c>SessionRevocations</c> (rows pruned once past their expiry) and survives a restart; this process keeps it in memory,
/// loaded from the table on first use, so a request never waits on the database for it (single instance, D3).</item>
/// </list>
/// Time comes from the injected <see cref="TimeProvider"/>, the same clock the cookie handler uses.
/// </summary>
public sealed class SessionLifetime(TimeProvider time, IConfiguration config, IDbContextFactory<ReleaseDbContext> dbf, ILogger<SessionLifetime> log)
{
    public const string SignedInItem = ".reos.signedin";
    public const string SessionItem = ".reos.sid";
    private readonly ConcurrentDictionary<string, DateTime> _signedOut = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;

    public static TimeSpan Idle(IConfiguration config) => TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Auth:Session:IdleMinutes", 60.0)));
    public TimeSpan Absolute => TimeSpan.FromHours(Math.Max(1.0 / 60, config.GetValue("Auth:Session:AbsoluteHours", 12.0)));

    /// <summary>Cookie <c>OnSigningIn</c>: stamps the sign-in time and a fresh session id. Renewals keep both (the handler copies the items).</summary>
    public Task OnSigningIn(CookieSigningInContext ctx)
    {
        ctx.Properties.Items[SignedInItem] = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        ctx.Properties.Items[SessionItem] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return Task.CompletedTask;
    }

    /// <summary>Cookie <c>OnValidatePrincipal</c>, before <see cref="SessionValidator"/>. False when the session was ended here (the caller stops).</summary>
    public async Task<bool> ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string? why = null;
        if (SignedInAt(ctx.Properties) is not { } at) why = "it has no sign-in time (issued before session limits existed)";
        else if (now - at >= Absolute) why = "it reached the absolute session lifetime";
        else if (ctx.Properties.Items.TryGetValue(SessionItem, out var sid) && sid is not null && await IsSignedOutAsync(sid, ctx.HttpContext.RequestAborted)) why = "it was signed out";
        if (why is null) return true;
        log.LogInformation("Session cookie refused: {Reason}", why);
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return false;
    }

    /// <summary>
    /// Remembers a signed-out session until it would have expired anyway: in memory at once, then in <c>SessionRevocations</c> with one audit row
    /// (CLAUDE.md rule 3; the table is version-exempt, system-owned). Expired rows are pruned in the same transaction.
    /// </summary>
    public async Task SignedOutAsync(AuthenticationProperties? properties, string? userId, CancellationToken ct = default)
    {
        if (properties is null || !properties.Items.TryGetValue(SessionItem, out var sid) || sid is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        var until = (SignedInAt(properties) ?? now) + Absolute;
        _signedOut[sid] = until;
        foreach (var (k, u) in _signedOut)
            if (u < now) _signedOut.TryRemove(k, out _);

        var stamp = WholeSeconds(now);
        var expires = WholeSeconds(until) < until ? WholeSeconds(until).AddSeconds(1) : until;   // round up: never prune a row before its session ends
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Set<SessionRevocations>().Where(r => r.ExpiresAt < stamp).ExecuteDeleteAsync(ct);
        if (await db.Set<SessionRevocations>().AnyAsync(r => r.SessionId == sid, ct)) { await tx.CommitAsync(ct); return; }
        db.Set<SessionRevocations>().Add(new SessionRevocations { SessionId = sid, UserId = userId, RevokedAt = stamp, ExpiresAt = expires });
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = stamp, ActorUserId = userId, EntityType = "Session", EntityId = sid, Action = "SignedOut",
            AfterJson = JsonSerializer.Serialize(new { expiresAt = expires.ToString(UtcTextConverter.Format, CultureInfo.InvariantCulture) }),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<bool> IsSignedOutAsync(string sid, CancellationToken ct)
    {
        if (!_loaded) await LoadAsync(ct);
        return _signedOut.TryGetValue(sid, out var until) && until > time.GetUtcNow().UtcDateTime;
    }

    /// <summary>Once per process: the sign-outs recorded before this start (a restart must not revive a copied cookie).</summary>
    private async Task LoadAsync(CancellationToken ct)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            var now = WholeSeconds(time.GetUtcNow().UtcDateTime);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var rows = await db.Set<SessionRevocations>().AsNoTracking().Where(r => r.ExpiresAt > now).Select(r => new { r.SessionId, r.ExpiresAt }).ToListAsync(ct);
            foreach (var r in rows) _signedOut.TryAdd(r.SessionId, r.ExpiresAt);
            _loaded = true;
        }
        finally { _loadLock.Release(); }
    }

    private static DateTime WholeSeconds(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static DateTime? SignedInAt(AuthenticationProperties p) =>
        p.Items.TryGetValue(SignedInItem, out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
}
