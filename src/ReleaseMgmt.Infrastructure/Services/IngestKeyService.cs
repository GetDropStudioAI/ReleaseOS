using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record IngestKeyView(string Id, string Name, string KeyPrefix, string CreatedAt, string? LastUsedAt, string? RevokedAt, string? CreatedBy, string? RevokedBy, int Version);
/// <summary>The only time the key itself exists outside the deployment tool: it is shown once, and only its SHA-256 is kept.</summary>
public sealed record CreatedIngestKey(IngestKeyView Key, string Secret);
public sealed record IngestPrincipal(string KeyId, string KeyName, string WorkspaceId);

/// <summary>
/// Per-workspace API keys for the push ingest API (REOS-110, D42). A key is <c>rk_</c> and 43 random base64url characters (256 bits); only its SHA-256 is stored
/// (<c>IngestKeys.KeySha256</c>, the way calendar tokens are), so a copy of the database holds nothing that can be used, and a lost key is replaced, not recovered (CLAUDE.md rule 11).
/// Admin (RTE and Release Manager) create and revoke; both are audited. <see cref="AuthenticateAsync"/> looks a presented key up by its hash and stamps
/// <c>LastUsedAt</c> at most once a minute, so a busy job does not turn every call into a write.
/// </summary>
public sealed class IngestKeyService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const string Prefix = "rk_";
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
    private const string Entity = "IngestKey";

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>True for text shaped like a key; anything else is refused before it is hashed or looked up.</summary>
    public static bool LooksLikeKey(string? key) => key is { Length: >= 20 and <= 128 } && key.StartsWith(Prefix, StringComparison.Ordinal) && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string NewKey() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public async Task<List<IngestKeyView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<IngestKeys>().AsNoTracking().OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.CreatedAt).ToListAsync(ct);
        var ids = rows.SelectMany(k => new[] { k.CreatedByUserId, k.RevokedByUserId }).OfType<string>().Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. rows.Select(k => View(k, names))];
    }

    private static IngestKeyView View(IngestKeys k, Dictionary<string, string> names) =>
        new(k.Id, k.Name, k.KeyPrefix, Iso(k.CreatedAt), k.LastUsedAt is { } l ? Iso(l) : null, k.RevokedAt is { } r ? Iso(r) : null,
            k.CreatedByUserId is not null ? names.GetValueOrDefault(k.CreatedByUserId) : null, k.RevokedByUserId is not null ? names.GetValueOrDefault(k.RevokedByUserId) : null, k.Version);

    public Task<ServiceResult<CreatedIngestKey>> CreateAsync(string? name, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            name = (name ?? "").Trim();
            if (name.Length is 0 or > ReleaseModelLimits.MaxKeyName)
                return ServiceResult<CreatedIngestKey>.Fail(new GuardFailure(ReleaseModelGuards.IngestKeyInvalid, $"A key name is 1 to {ReleaseModelLimits.MaxKeyName} characters (say what uses it, e.g. \"CI payments\")"));
            var secret = NewKey();
            var row = new IngestKeys { Id = Ids.New(), Name = name, KeyPrefix = secret[..8], KeySha256 = Hash(secret), CreatedByUserId = actor.UserId, CreatedAt = Now };
            db.Set<IngestKeys>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, new { row.Name, row.KeyPrefix });   // never the key, never its hash
            await db.SaveChangesAsync(ct);
            var by = await db.Set<Users>().AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
            return ServiceResult<CreatedIngestKey>.Ok(new(View(row, by is null ? [] : new() { [actor.UserId] = by }), secret));
        }, ct);

    public Task<ServiceResult<IngestKeys>> RevokeAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var k = await db.Set<IngestKeys>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (k is null) return ServiceResult<IngestKeys>.NotFound("ingest key");
            if (VersionMismatch(expectedVersion, k.Version)) return ServiceResult<IngestKeys>.Conflict(k);
            if (k.RevokedAt is not null) return ServiceResult<IngestKeys>.Ok(k);   // repeat is a no-op (keeps who revoked it)
            k.RevokedAt = Now; k.RevokedByUserId = actor.UserId; k.Version++;
            Audit(db, actor, null, Entity, k.Id, "Revoke", new { k.Name, k.KeyPrefix }, new { revoked = true });
            await db.SaveChangesAsync(ct);
            return ServiceResult<IngestKeys>.Ok(k);
        }, ct);

    /// <summary>The key a presented secret is, in its workspace, or null when it is unknown, revoked, or its workspace is archived. Runs outside any workspace scope (the key says which one).</summary>
    public async Task<IngestPrincipal?> AuthenticateAsync(string secret, CancellationToken ct = default)
    {
        if (!LooksLikeKey(secret)) return null;
        var hash = Hash(secret);
        using var _ = WorkspaceScope.HostWide();
        await using var db = await OpenAsync(ct);
        var k = await (from key in db.Set<IngestKeys>().AsNoTracking()
                       join w in db.Set<Workspaces>().AsNoTracking() on key.WorkspaceId equals w.Id
                       where key.KeySha256 == hash && key.RevokedAt == null && w.ArchivedAt == null
                       select new { key.Id, key.Name, key.WorkspaceId, key.LastUsedAt }).SingleOrDefaultAsync(ct);
        if (k is null) return null;
        var now = Now;
        if (k.LastUsedAt is null || now - k.LastUsedAt >= TouchEvery)
            await db.Set<IngestKeys>().Where(x => x.Id == k.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), ct);
        return new IngestPrincipal(k.Id, k.Name, k.WorkspaceId);
    }

    /// <summary>
    /// A refused key (A bad key gets 401 and is audited, REOS-110). Host-wide audit row, no key material: the caller's address and why. The caller bounds how many it writes per address.
    /// </summary>
    public async Task RecordRejectedAsync(string remoteAddress, string reason, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();
        await using var db = await OpenAsync(ct);
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = Now, EntityType = Entity, EntityId = "unknown", Action = "Rejected", WorkspaceId = null,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { remoteAddress, reason }),
        });
        await db.SaveChangesAsync(ct);
    }
}
