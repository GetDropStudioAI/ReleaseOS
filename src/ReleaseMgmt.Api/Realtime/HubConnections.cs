using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Realtime;

/// <summary>
/// REOS-63: the open <see cref="TrainsHub"/> connections, by user and session. A WebSocket is authorised once, when it opens, so without this a socket
/// outlived sign-out, session expiry and deactivation. Sign-out, a rejected session cookie and deactivation abort the connections at once; the
/// <see cref="HubSessionMonitor"/> re-checks every connection on a timer as the backstop for paths that do not call in here (a direct database change,
/// an import, a restart of the sign-out list).
/// </summary>
public sealed class HubConnections(ILogger<HubConnections> log)
{
    public sealed record Entry(HubCallerContext Context, string? UserId, string? SessionId, DateTime? SignedInAt);

    private readonly ConcurrentDictionary<string, Entry> _open = new();

    public int Count => _open.Count;

    public void Add(HubCallerContext ctx)
    {
        var u = ctx.User;
        _open[ctx.ConnectionId] = new Entry(ctx, u?.FindFirst("uid")?.Value, u?.FindFirst(SessionLifetime.SessionClaim)?.Value, SignedInAt(u));
    }

    public void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    public IReadOnlyCollection<Entry> Snapshot() => [.. _open.Values];

    /// <summary>Aborts every connection of <paramref name="userId"/> (deactivated, deleted, or its session refused as inactive).</summary>
    public int AbortUser(string userId, string reason) => Abort(e => e.UserId == userId, reason);

    /// <summary>
    /// Aborts the connections opened with one session cookie. Other sessions of the same user (another browser or device) stay open. A connection whose
    /// principal carries no session id (issued before REOS-63) cannot be told apart, so it is matched by user (Q-SEC-F1).
    /// </summary>
    public int AbortSession(string? sessionId, string? userId, string reason) =>
        Abort(e => (sessionId is not null && e.SessionId == sessionId) || (userId is not null && e.UserId == userId && e.SessionId is null), reason);

    public int Abort(Entry e, string reason) => Abort(x => ReferenceEquals(x, e), reason);

    private int Abort(Func<Entry, bool> match, string reason)
    {
        var n = 0;
        foreach (var (id, e) in _open)
        {
            if (!match(e)) continue;
            _open.TryRemove(id, out _);
            e.Context.Abort();
            n++;
        }
        if (n > 0) log.LogInformation("Closed {Count} live connection(s): {Reason}", n, reason);
        return n;
    }

    private static DateTime? SignedInAt(ClaimsPrincipal? u) =>
        u?.FindFirst(SessionLifetime.SignedInClaim)?.Value is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
}

/// <summary>
/// REOS-63 backstop: every <c>Realtime:SessionRecheckSeconds</c> (default 30) re-checks each open hub connection and aborts it when its user is no longer
/// active, its session was signed out, or its session passed the absolute lifetime. A failed check is logged and raised through <see cref="IAlertSink"/>,
/// then retried on the next tick (rule 8).
/// </summary>
public sealed class HubSessionMonitor(HubConnections connections, SessionLifetime sessions, IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time,
    IAlertSink alerts, IConfiguration config, ILogger<HubSessionMonitor> log) : BackgroundService
{
    public static TimeSpan Interval(IConfiguration config) => TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Realtime:SessionRecheckSeconds", 30.0)));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Interval(config), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try { await CheckAsync(stop); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Live connection session check failed");
                    await alerts.RaiseAsync("Realtime", "SessionCheckFailed", "hub", "Live connection session check failed: " + ex.Message, stop);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>One pass over the open connections. Returns how many were closed.</summary>
    public async Task<int> CheckAsync(CancellationToken ct = default)
    {
        var open = connections.Snapshot();
        if (open.Count == 0) return 0;
        var ids = open.Select(e => e.UserId).OfType<string>().Distinct().ToList();
        HashSet<string> active;
        await using (var db = await dbf.CreateDbContextAsync(ct))
            active = [.. await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct)];
        var now = time.GetUtcNow().UtcDateTime;
        var closed = 0;
        foreach (var e in open)
        {
            string? why = null;
            if (e.UserId is not null && !active.Contains(e.UserId)) why = "the user is deactivated or deleted";
            else if (e.SessionId is not null && sessions.IsSignedOut(e.SessionId)) why = "its session was signed out";
            else if (e.SignedInAt is { } at && now - at >= sessions.Absolute) why = "its session reached the absolute lifetime";
            if (why is not null) closed += connections.Abort(e, why);
        }
        return closed;
    }
}
