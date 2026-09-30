using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record IcsTokenRow(string Id, string CreatedAt, string? RevokedAt, bool Active, string? LastUsedAt);
/// <summary>The one time the secret is visible. <see cref="Path"/> is the feed path for the requested scope; the client prefixes its own origin.</summary>
public sealed record IcsTokenIssued(string Id, string Token, string Path, string Scope, string CreatedAt);

/// <summary>
/// Per-user calendar feed secret (REOS-51, D21, Q-051*). 256-bit random, base64url; only SHA-256(token) is stored (IcsTokens.TokenSha256), so neither the
/// database, the audit log nor a backup can reproduce a URL. One active token per user (the schema's unique index): rotate = revoke the old row and insert the
/// new one in one transaction, so the old URL stops working at commit. Every write audits one row (ids and scope only, never the secret or its hash).
/// The token is never logged. "Last used" is best effort and in memory (the table has no column for it); a failure to stamp it can never fail a feed.
/// </summary>
public sealed class IcsTokenService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private static readonly ConcurrentDictionary<string, DateTime> LastUsed = new();
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly ConcurrentDictionary<string, (long Minute, int Count)> _failures = new();

    /// <summary>Brute-force brake for the anonymous feed: failed lookups per client address per minute (TimeProvider minute). True when over <paramref name="max"/>.</summary>
    public bool Throttle(string client, int max, bool failed)
    {
        var minute = new DateTimeOffset(Now).ToUnixTimeSeconds() / 60;
        (long Minute, int Count) cur = failed
            ? _failures.AddOrUpdate(client, (minute, 1), (_, v) => v.Minute == minute ? (minute, v.Count + 1) : (minute, 1))
            : _failures.TryGetValue(client, out var v0) && v0.Minute == minute ? v0 : (minute, 0);
        if (_failures.Count > 10_000) foreach (var k in _failures.Where(kv => kv.Value.Minute < minute).Select(kv => kv.Key).ToList()) _failures.TryRemove(k, out _);
        return cur.Count > max;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The feed path (no origin, no host) for a token and scope. Contains the secret: never log it.</summary>
    public static string PathFor(string token, string scope, string? trainId) => scope switch
    {
        IcsScopes.Mine => $"/api/v1/ics/{token}/mine.ics",
        IcsScopes.Freezes => $"/api/v1/ics/{token}/freezes.ics",
        IcsScopes.Train => $"/api/v1/ics/{token}/trains/{Uri.EscapeDataString(trainId ?? "")}.ics",
        _ => $"/api/v1/ics/{token}.ics",
    };

    public async Task<IReadOnlyList<IcsTokenRow>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<IcsTokens>().AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct);
        return [.. rows.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(20)
            .Select(t => new IcsTokenRow(t.Id, t.CreatedAt.ToString(Iso), t.RevokedAt?.ToString(Iso), t.RevokedAt is null,
                LastUsed.TryGetValue(t.Id, out var u) ? u.ToString(Iso) : null))];
    }

    private async Task<GuardFailure?> CheckScope(ReleaseDbContext db, string scope, string? trainId, CancellationToken ct)
    {
        if (!IcsScopes.Valid.Contains(scope)) return new GuardFailure(CalendarGuards.InvalidIcsScope, $"Scope must be one of {string.Join(", ", IcsScopes.Valid)}");
        if (scope == IcsScopes.Train && (string.IsNullOrWhiteSpace(trainId) || !await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)))
            return new GuardFailure(CalendarGuards.InvalidIcsScope, "A train feed needs the id of an existing train");
        return null;
    }

    public Task<ServiceResult<IcsTokenIssued>> CreateAsync(string scope, string? trainId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (await CheckScope(db, scope, trainId, ct) is { } bad) return ServiceResult<IcsTokenIssued>.Fail(bad);
            if (await db.Set<IcsTokens>().AnyAsync(t => t.UserId == actor.UserId && t.RevokedAt == null, ct))
                return ServiceResult<IcsTokenIssued>.Fail(new GuardFailure(CalendarGuards.IcsTokenExists, "You already have a calendar link. Rotate it to get a new one; the old link stops working."));
            var issued = Issue(db, actor, scope, trainId, "Create", null);
            await db.SaveChangesAsync(ct);
            return ServiceResult<IcsTokenIssued>.Ok(issued);
        }, ct);

    /// <summary>Revokes the caller's active token and issues a new one. The old URL is dead as soon as this returns.</summary>
    public Task<ServiceResult<IcsTokenIssued>> RotateAsync(string id, string scope, string? trainId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var old = await db.Set<IcsTokens>().SingleOrDefaultAsync(t => t.Id == id && t.UserId == actor.UserId, ct);
            if (old is null) return ServiceResult<IcsTokenIssued>.NotFound("calendar link");
            if (old.RevokedAt is not null) return ServiceResult<IcsTokenIssued>.Fail(new GuardFailure(CalendarGuards.IcsTokenRevoked, "That link is already revoked. Refresh the page."));
            if (await CheckScope(db, scope, trainId, ct) is { } bad) return ServiceResult<IcsTokenIssued>.Fail(bad);
            old.RevokedAt = Now;
            await db.SaveChangesAsync(ct);   // frees the one-active-per-user index before the new row goes in (same transaction)
            var issued = Issue(db, actor, scope, trainId, "Rotate", old.Id);
            await db.SaveChangesAsync(ct);
            return ServiceResult<IcsTokenIssued>.Ok(issued);
        }, ct);

    public Task<ServiceResult<IcsTokenRow>> RevokeAsync(string id, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<IcsTokens>().SingleOrDefaultAsync(x => x.Id == id && x.UserId == actor.UserId, ct);
            if (t is null) return ServiceResult<IcsTokenRow>.NotFound("calendar link");
            if (t.RevokedAt is null)
            {
                t.RevokedAt = Now;
                Audit(db, actor, null, "IcsToken", t.Id, "Revoke", new { revoked = false }, new { revoked = true });
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<IcsTokenRow>.Ok(new IcsTokenRow(t.Id, t.CreatedAt.ToString(Iso), t.RevokedAt?.ToString(Iso), false, null));
        }, ct);

    private IcsTokenIssued Issue(ReleaseDbContext db, Actor actor, string scope, string? trainId, string action, string? replaces)
    {
        var token = NewToken();
        var row = new IcsTokens { UserId = actor.UserId, TokenSha256 = Hash(token), CreatedAt = Now };
        db.Set<IcsTokens>().Add(row);
        Audit(db, actor, null, "IcsToken", row.Id, action, replaces is null ? null : new { tokenId = replaces }, new { tokenId = row.Id, scope });
        return new IcsTokenIssued(row.Id, token, PathFor(token, scope, trainId), scope, row.CreatedAt.ToString(Iso));
    }

    /// <summary>
    /// The user a feed token belongs to, or null (unknown, revoked, malformed, or its user deactivated: indistinguishable to the caller). The lookup is by the SHA-256 of the presented
    /// value, so the database compares hashes the caller cannot steer byte by byte, and the hash is compared again in constant time. Best-effort last-used stamp.
    /// </summary>
    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        if (token.Length is < 20 or > 100) return null;
        var hash = Hash(token);
        await using var db = await OpenAsync(ct);
        var row = await db.Set<IcsTokens>().AsNoTracking().Where(t => t.TokenSha256 == hash).Select(t => new { t.Id, t.UserId, t.TokenSha256, t.RevokedAt }).SingleOrDefaultAsync(ct);
        var stored = Encoding.ASCII.GetBytes(row?.TokenSha256 ?? new string('0', 64));
        var same = CryptographicOperations.FixedTimeEquals(stored, Encoding.ASCII.GetBytes(hash));
        if (row is null || !same || row.RevokedAt is not null) return null;
        // SEC-B6: a deactivated user's link stops with their session (Q-053c); it is not revoked, so it works again if the IdP signs them back in.
        if (!await db.Set<Users>().AsNoTracking().AnyAsync(u => u.Id == row.UserId && u.IsActive, ct)) return null;
        LastUsed[row.Id] = Now;
        return row.UserId;
    }
}
