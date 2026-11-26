using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ApiTokenView(string Id, string Name, string TokenPrefix, IReadOnlyList<string> Scopes, string? ExpiresAt, string State, string OwnerUserId, string? OwnerName,
    string CreatedAt, string? RotatedAt, string? LastUsedAt, string? RevokedAt, string? RevokedBy, int Version);
/// <summary>The only time the token itself exists outside the caller's own storage: it is shown once, and only its SHA-256 is kept.</summary>
public sealed record CreatedApiToken(ApiTokenView Token, string Secret);

/// <summary>Who a valid token is: the person it acts as, in the token's workspace, with the role that person holds there now.</summary>
public sealed record ApiTokenPrincipal(string TokenId, string Name, string WorkspaceId, string OwnerUserId, string OwnerEmail, string OwnerName, string Role, IReadOnlySet<string> Scopes);

public enum ApiTokenDenial { None, Malformed, Unknown, Revoked, Expired, OwnerInactive, OwnerNotMember, WorkspaceArchived }

/// <summary>The outcome of presenting a token. The ids are set when the token exists, so a denial can be audited in its workspace.</summary>
public sealed record ApiTokenAuthentication(ApiTokenPrincipal? Principal, ApiTokenDenial Denial, string? TokenId = null, string? TokenName = null, string? WorkspaceId = null, string? OwnerUserId = null)
{
    public bool Ok => Principal is not null;
}

/// <summary>
/// Scoped service tokens for the public API (REOS-162). A token is <c>rt_</c> and 43 random base64url characters (256 bits); only its SHA-256 is stored, so a copy of the database
/// holds nothing that can be used and a lost token is replaced, not recovered (CLAUDE.md rule 11). A token acts as the person who made it (or last rotated it): the role that person
/// holds in the token's workspace right now limits it, and its scopes narrow it further; when that person is deactivated or leaves the workspace the token stops working. Create, rotate
/// and revoke are Admin actions and each writes an audit row; a denied use of a known token writes one too (bounded by the caller). <see cref="AuthenticateAsync"/> looks the token up on
/// every request, so a revoked token is refused at once, and stamps <c>LastUsedAt</c> at most once a minute.
/// </summary>
public sealed class ApiTokenService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const string Prefix = "rt_";
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
    private const string Entity = "ApiToken";

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True for text shaped like a token; anything else is refused before it is hashed or looked up.</summary>
    public static bool LooksLikeToken(string? token) => token is { Length: >= 20 and <= 128 } && token.StartsWith(Prefix, StringComparison.Ordinal) && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string NewToken() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private string State(ApiTokens t) => t.RevokedAt is not null ? "Revoked" : t.ExpiresAt is { } e && e <= Now ? "Expired" : "Active";

    private ApiTokenView View(ApiTokens t, Dictionary<string, string> names) =>
        new(t.Id, t.Name, t.TokenPrefix, ApiTokenScopes.Split(t.Scopes), t.ExpiresAt is { } e ? Iso(e) : null, State(t), t.OwnerUserId, names.GetValueOrDefault(t.OwnerUserId), Iso(t.CreatedAt),
            t.RotatedAt is { } r ? Iso(r) : null, t.LastUsedAt is { } l ? Iso(l) : null, t.RevokedAt is { } v ? Iso(v) : null, t.RevokedByUserId is not null ? names.GetValueOrDefault(t.RevokedByUserId) : null, t.Version);

    private static async Task<Dictionary<string, string>> NamesAsync(ReleaseDbContext db, IEnumerable<ApiTokens> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(k => new[] { k.OwnerUserId, k.RevokedByUserId }).OfType<string>().Distinct().ToList();
        return await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<List<ApiTokenView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<ApiTokens>().AsNoTracking().OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.CreatedAt).ThenBy(k => k.Id).ToListAsync(ct);
        var names = await NamesAsync(db, rows, ct);
        return [.. rows.Select(k => View(k, names))];
    }

    public async Task<ApiTokenView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<ApiTokens>().AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
        return row is null ? null : View(row, await NamesAsync(db, [row], ct));
    }

    private ServiceResult<T>? ValidateExpiry<T>(DateTime? expiresAt) =>
        expiresAt is { } e && e <= Now ? ServiceResult<T>.Fail(new GuardFailure(ApiGuards.TokenInvalid, "The expiry must be in the future")) : null;

    public Task<ServiceResult<CreatedApiToken>> CreateAsync(string? name, IEnumerable<string>? scopes, DateTime? expiresAt, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            name = (name ?? "").Trim();
            if (name.Length is 0 or > ReleaseModelLimits.MaxKeyName)
                return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.TokenInvalid, $"A token name is 1 to {ReleaseModelLimits.MaxKeyName} characters (say what uses it, e.g. \"Release dashboard\")"));
            var list = ApiTokenScopes.Parse(scopes);
            if (list is null)
                return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.TokenInvalid, $"Name at least one scope, from {string.Join(", ", ApiTokenScopes.All)}", ApiTokenScopes.All));
            if (ValidateExpiry<CreatedApiToken>(expiresAt) is { } bad) return bad;
            if (await RoleOf(db, actor.UserId) is null) return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.OwnerNotMember, "Only a member of the workspace can make a token that acts in it"));
            var secret = NewToken();
            var row = new ApiTokens
            {
                Id = Ids.New(), Name = name, TokenPrefix = secret[..8], TokenSha256 = Hash(secret), Scopes = ApiTokenScopes.Join(list), ExpiresAt = expiresAt?.ToUniversalTime(),
                OwnerUserId = actor.UserId, CreatedAt = Now, WorkspaceId = Memberships.WorkspaceOf(db),
            };
            db.Set<ApiTokens>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, new { row.Name, row.TokenPrefix, scopes = list, expiresAt = row.ExpiresAt is { } e ? Iso(e) : null });   // never the token, never its hash
            await db.SaveChangesAsync(ct);
            return ServiceResult<CreatedApiToken>.Ok(new(View(row, await NamesAsync(db, [row], ct)), secret));
        }, ct);

    /// <summary>
    /// A new secret for the same token: the old one stops working at once. The person who rotates becomes the one the token acts as, so nobody holds a secret that acts as someone with more
    /// rights than their own. A token that has expired needs a new expiry; a revoked one cannot be rotated.
    /// </summary>
    public Task<ServiceResult<CreatedApiToken>> RotateAsync(string id, DateTime? newExpiresAt, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ApiTokens>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return ServiceResult<CreatedApiToken>.NotFound("API token");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<CreatedApiToken>.Conflict(t.ToConflictBody());
            if (t.RevokedAt is not null) return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.ApiTokenRevoked, "A revoked token cannot be rotated; make a new one"));
            if (ValidateExpiry<CreatedApiToken>(newExpiresAt) is { } bad) return bad;
            if (newExpiresAt is null && t.ExpiresAt is { } old && old <= Now)
                return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.ApiTokenExpired, "This token has expired; give the rotated token a new expiry"));
            if (await RoleOf(db, actor.UserId) is null) return ServiceResult<CreatedApiToken>.Fail(new GuardFailure(ApiGuards.OwnerNotMember, "Only a member of the workspace can hold a token that acts in it"));
            var before = new { t.TokenPrefix, owner = t.OwnerUserId, expiresAt = t.ExpiresAt is { } b ? Iso(b) : null };
            var secret = NewToken();
            t.TokenPrefix = secret[..8]; t.TokenSha256 = Hash(secret); t.OwnerUserId = actor.UserId; t.RotatedAt = Now; t.Version++;
            if (newExpiresAt is { } e) t.ExpiresAt = e.ToUniversalTime();
            Audit(db, actor, null, Entity, t.Id, "Rotate", before, new { t.TokenPrefix, owner = t.OwnerUserId, expiresAt = t.ExpiresAt is { } a ? Iso(a) : null });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CreatedApiToken>.Ok(new(View(t, await NamesAsync(db, [t], ct)), secret));
        }, ct);

    public Task<ServiceResult<ApiTokenView>> RevokeAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ApiTokens>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return ServiceResult<ApiTokenView>.NotFound("API token");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ApiTokenView>.Conflict(t.ToConflictBody());
            if (t.RevokedAt is null)   // a repeat is a no-op (keeps who revoked it)
            {
                t.RevokedAt = Now; t.RevokedByUserId = actor.UserId; t.Version++;
                Audit(db, actor, null, Entity, t.Id, "Revoke", new { t.Name, t.TokenPrefix }, new { revoked = true });
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<ApiTokenView>.Ok(View(t, await NamesAsync(db, [t], ct)));
        }, ct);

    /// <summary>
    /// The token a presented secret is, in its workspace, or why not. Runs outside any workspace scope (the token says which one). The person it acts as must be active and a member of the
    /// workspace; their role is read here, from the same membership the database triggers read.
    /// </summary>
    public async Task<ApiTokenAuthentication> AuthenticateAsync(string? secret, CancellationToken ct = default)
    {
        if (!LooksLikeToken(secret)) return new(null, ApiTokenDenial.Malformed);
        var hash = Hash(secret!);
        using var _ = WorkspaceScope.HostWide();
        await using var db = await OpenAsync(ct);
        var t = await db.Set<ApiTokens>().AsNoTracking().SingleOrDefaultAsync(k => k.TokenSha256 == hash, ct);
        if (t is null) return new(null, ApiTokenDenial.Unknown);
        ApiTokenAuthentication Denied(ApiTokenDenial d) => new(null, d, t.Id, t.Name, t.WorkspaceId, t.OwnerUserId);
        if (t.RevokedAt is not null) return Denied(ApiTokenDenial.Revoked);
        var now = Now;
        if (t.ExpiresAt is { } e && e <= now) return Denied(ApiTokenDenial.Expired);
        if (await db.Set<Workspaces>().AsNoTracking().AnyAsync(w => w.Id == t.WorkspaceId && w.ArchivedAt == null, ct) is false) return Denied(ApiTokenDenial.WorkspaceArchived);
        var owner = await db.Set<Users>().AsNoTracking().Where(u => u.Id == t.OwnerUserId).Select(u => new { u.Email, u.DisplayName, u.IsActive }).SingleOrDefaultAsync(ct);
        if (owner is null || !owner.IsActive) return Denied(ApiTokenDenial.OwnerInactive);
        var role = await db.Set<WorkspaceMembers>().IgnoreQueryFilters().AsNoTracking().Where(m => m.WorkspaceId == t.WorkspaceId && m.UserId == t.OwnerUserId).Select(m => m.Role).SingleOrDefaultAsync(ct);
        if (role is null) return Denied(ApiTokenDenial.OwnerNotMember);
        if (t.LastUsedAt is null || now - t.LastUsedAt >= TouchEvery)
            await db.Set<ApiTokens>().Where(x => x.Id == t.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), ct);
        return new(new ApiTokenPrincipal(t.Id, t.Name, t.WorkspaceId, t.OwnerUserId, owner.Email, owner.DisplayName, role, ApiTokenScopes.Split(t.Scopes).ToHashSet(StringComparer.Ordinal)), ApiTokenDenial.None, t.Id, t.Name, t.WorkspaceId, t.OwnerUserId);
    }

    public static string Reason(ApiTokenDenial d) => d switch
    {
        ApiTokenDenial.Malformed => "malformed token",
        ApiTokenDenial.Unknown => "unknown token",
        ApiTokenDenial.Revoked => "revoked token",
        ApiTokenDenial.Expired => "expired token",
        ApiTokenDenial.OwnerInactive => "the person the token acts as is deactivated",
        ApiTokenDenial.OwnerNotMember => "the person the token acts as no longer belongs to the workspace",
        ApiTokenDenial.WorkspaceArchived => "the token's workspace is archived",
        _ => "denied",
    };

    /// <summary>
    /// A denied use of a token that exists (revoked, expired, owner gone, scope or role missing): one audit row in the token's workspace, with the person it acts as as the actor. The caller bounds
    /// how many it writes per token. A token that does not exist is <see cref="RecordRejectedAsync"/> instead: it belongs to no workspace and no person.
    /// </summary>
    public async Task RecordDeniedAsync(ApiTokenAuthentication who, string reason, string method, string path, string? requiredScope, CancellationToken ct = default)
    {
        if (who.TokenId is null || who.WorkspaceId is null) return;
        using var _ = WorkspaceScope.HostWide();
        await using var db = await OpenAsync(ct);
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = Now, ActorUserId = who.OwnerUserId, EntityType = Entity, EntityId = who.TokenId, Action = "Denied", WorkspaceId = who.WorkspaceId,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { reason, method, path, requiredScope, apiToken = who.TokenName }, Json),
        });
        await db.SaveChangesAsync(ct);
        AuditSealSignal.Notify();
    }

    /// <summary>A refused token that does not exist: a host-wide audit row, no token material: the caller's address and why. The caller bounds how many it writes per address.</summary>
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
        AuditSealSignal.Notify();
    }
}

internal static class ApiTokenRows
{
    /// <summary>The current row as a 409 body: never the hash.</summary>
    public static object ToConflictBody(this ApiTokens t) => new { t.Id, t.Name, t.TokenPrefix, scopes = ApiTokenScopes.Split(t.Scopes), t.Version, revoked = t.RevokedAt is not null };
}
