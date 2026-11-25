using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public enum IdempotencyOutcome { Started, Replay, InProgress, Mismatch }

/// <summary>The first request with a key is <see cref="IdempotencyOutcome.Started"/> (the caller runs it and then <see cref="IdempotencyService.CompleteAsync"/>); a repeat gets the stored result.</summary>
public sealed record IdempotencyBegin(IdempotencyOutcome Outcome, string? Id = null, int StatusCode = 0, string? ContentType = null, string? Body = null);

/// <summary>
/// REOS-163: <c>Idempotency-Key</c> on a POST to the public API. The key is scoped to the token. The first request stores its fingerprint (method, path, query, body) before it runs; when it finishes
/// its status and body are stored, and a repeat with the same key and fingerprint returns them without running anything, so a service write (and its one audit row) happens once. The same key
/// with another request is refused; a repeat while the first is still running is told so. A result that is a server error is not kept, so a retry runs again. Keys are kept
/// <c>PublicApi:IdempotencyHours</c> (default 24). The clock is the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class IdempotencyService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, PublicApiOptions options)
{
    private TimeSpan Keep => TimeSpan.FromHours(Math.Max(1, options.IdempotencyHours));
    private DateTime Now { get { var t = time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }
    private long _lastPurge;

    public async Task<IdempotencyBegin> BeginAsync(string workspaceId, string tokenId, string key, string requestSha256, CancellationToken ct)
    {
        using var scope = WorkspaceScope.Enter(workspaceId);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = Now;
        await PurgeAsync(db, now, ct);
        var row = new IdempotencyKeys { Id = Ids.New(), ApiTokenId = tokenId, KeyValue = key, RequestSha256 = requestSha256, CreatedAt = now, ExpiresAt = now + Keep, WorkspaceId = workspaceId };
        db.Set<IdempotencyKeys>().Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return new(IdempotencyOutcome.Started, row.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } && ex.InnerException.Message.Contains("UNIQUE", StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
        }
        var found = await db.Set<IdempotencyKeys>().AsNoTracking().SingleOrDefaultAsync(k => k.ApiTokenId == tokenId && k.KeyValue == key, ct);
        if (found is null) return await BeginAsync(workspaceId, tokenId, key, requestSha256, ct);   // it expired and was purged between the insert and the read: start again
        if (found.ExpiresAt <= now)
        {
            await db.Set<IdempotencyKeys>().Where(k => k.Id == found.Id).ExecuteDeleteAsync(ct);
            return await BeginAsync(workspaceId, tokenId, key, requestSha256, ct);
        }
        if (found.RequestSha256 != requestSha256) return new(IdempotencyOutcome.Mismatch);
        if (found.StatusCode is not { } status) return new(IdempotencyOutcome.InProgress);
        return new(IdempotencyOutcome.Replay, found.Id, status, found.ContentType, found.ResponseBody);
    }

    public async Task CompleteAsync(string workspaceId, string id, int statusCode, string? contentType, string body, CancellationToken ct)
    {
        using var scope = WorkspaceScope.Enter(workspaceId);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Set<IdempotencyKeys>().Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.StatusCode, statusCode).SetProperty(k => k.ContentType, contentType).SetProperty(k => k.ResponseBody, body), ct);
    }

    /// <summary>The first request ended without a result worth keeping (a server error, or a result too large to store): forget the key, so a retry runs again.</summary>
    public async Task AbandonAsync(string workspaceId, string id, CancellationToken ct)
    {
        using var scope = WorkspaceScope.Enter(workspaceId);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Set<IdempotencyKeys>().Where(k => k.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Expired keys go, at most once a minute (the unique key already treats an expired one as absent).</summary>
    private async Task PurgeAsync(ReleaseDbContext db, DateTime now, CancellationToken ct)
    {
        var stamp = time.GetTimestamp();
        var last = Interlocked.Read(ref _lastPurge);
        if (last != 0 && time.GetElapsedTime(last, stamp) < TimeSpan.FromMinutes(1)) return;
        Interlocked.Exchange(ref _lastPurge, stamp);
        await db.Set<IdempotencyKeys>().IgnoreQueryFilters().Where(k => k.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// The public API's settings (<c>PublicApi:*</c>, each with a default that works untouched, D56): requests a second and burst per token and per workspace, how long an Idempotency-Key is kept,
/// and the page sizes of cursor pagination.
/// </summary>
public sealed record PublicApiOptions
{
    public double TokenRequestsPerSecond { get; init; } = 20;
    public double TokenBurst { get; init; } = 40;
    public double WorkspaceRequestsPerSecond { get; init; } = 100;
    public double WorkspaceBurst { get; init; } = 200;
    public int IdempotencyHours { get; init; } = 24;
    public int DefaultPageSize { get; init; } = 50;
    public int MaxPageSize { get; init; } = 200;
    /// <summary>Denied-use audit rows written per token per minute (the rest are counted in the log only).</summary>
    public int DeniedAuditsPerMinute { get; init; } = 10;
}
