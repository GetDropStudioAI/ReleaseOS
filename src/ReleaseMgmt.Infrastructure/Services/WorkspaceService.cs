using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-104 (D45, Q-104a): workspaces on one host. Every root row belongs to one; db/schema.sql creates the default workspace. REOS-105 scopes every
/// query, permission and live update to the current workspace (<see cref="WorkspaceScope"/>) and reads roles from the memberships kept here.
/// </summary>
public sealed class WorkspaceService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const int MaxNameLength = 100, MaxRegionLength = 60;

    /// <summary>
    /// Names the default workspace from <c>Workspace:DefaultName</c> and <c>Workspace:Region</c> at start (Q-090e). A key that is set wins over the stored
    /// value and the change is audited; a key that is not set leaves the stored value as it is. A value outside the schema's limits stops the start.
    /// </summary>
    public Task<ServiceResult<Workspaces>> ApplyDefaultSettingsAsync(string? name, string? region, CancellationToken ct = default)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        if (name is { Length: > MaxNameLength }) throw new InvalidOperationException($"Workspace:DefaultName is longer than {MaxNameLength} characters");
        if (region is { Length: > MaxRegionLength }) throw new InvalidOperationException($"Workspace:Region is longer than {MaxRegionLength} characters");
        return RunAsync(async db =>
        {
            var w = await db.Set<Workspaces>().SingleAsync(x => x.Id == WorkspaceIds.Default, ct);
            if ((name is null || name == w.Name) && (region is null || region == w.Region)) return ServiceResult<Workspaces>.Ok(w);
            var before = new { w.Name, w.Region, w.Version };
            if (name is not null) w.Name = name;
            if (region is not null) w.Region = region;
            w.Version++;
            Audit(db, null, null, "Workspace", w.Id, "Configure", before, new { w.Name, w.Region, w.Version, fromConfig = true });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Workspaces>.Ok(w);
        }, ct);
    }

    public sealed record WorkspaceView(string Id, string Name, string Slug, string? Region, string KeyPrefix = ReleaseKeys.DefaultPrefix, int Version = 1);

    /// <summary>The workspace the signed-in person is working in: the one the request resolved (REOS-105), or the default one outside any.</summary>
    public async Task<WorkspaceView> CurrentAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var ws = Memberships.WorkspaceOf(db);
        return await db.Set<Workspaces>().AsNoTracking().Where(w => w.Id == ws).Select(w => new WorkspaceView(w.Id, w.Name, w.Slug, w.Region, w.KeyPrefix, w.Version)).SingleAsync(ct);
    }

    /// <summary>
    /// REOS-107: the prefix of the workspace's release keys (RLS in RLS-284): 2 to 10 capital letters or digits. It applies to keys given from now on; the keys that exist
    /// keep theirs (a key never changes). One audit row; If-Match is the workspace's Version.
    /// </summary>
    public Task<ServiceResult<WorkspaceView>> SetKeyPrefixAsync(string? prefix, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            prefix = prefix?.Trim().ToUpperInvariant();
            if (!ReleaseKeys.IsValidPrefix(prefix))
                return ServiceResult<WorkspaceView>.Fail(new GuardFailure(ReleaseModelGuards.KeyPrefixInvalid, $"A key prefix is {ReleaseKeys.MinPrefix} to {ReleaseKeys.MaxPrefix} capital letters or digits"));
            var ws = Memberships.WorkspaceOf(db);
            var w = await db.Set<Workspaces>().SingleAsync(x => x.Id == ws, ct);
            if (VersionMismatch(expectedVersion, w.Version)) return ServiceResult<WorkspaceView>.Conflict(w);
            if (w.KeyPrefix != prefix)
            {
                var before = new { w.KeyPrefix };
                w.KeyPrefix = prefix!; w.Version++;
                Audit(db, actor, null, "Workspace", w.Id, "KeyPrefix", before, new { w.KeyPrefix, w.NextKeyNumber });
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<WorkspaceView>.Ok(new(w.Id, w.Name, w.Slug, w.Region, w.KeyPrefix, w.Version));
        }, ct);

    /// <summary>A user created by sign-in is a member of the default workspace with the role the identity provider gave (Users.Role).</summary>
    public static void JoinDefault(ReleaseDbContext db, Users user, DateTime now) => Join(db, user, WorkspaceIds.Default, user.Role, now);

    /// <summary>
    /// REOS-105: a person joins a workspace with a role there. The triggers and policies read this role (Users.Role is the role from sign-in, which is the
    /// default workspace's: Q-105e).
    /// </summary>
    public static WorkspaceMembers Join(ReleaseDbContext db, Users user, string workspaceId, string role, DateTime now) =>
        db.Set<WorkspaceMembers>().Add(new WorkspaceMembers { WorkspaceId = workspaceId, UserId = user.Id, Role = role, AddedAt = now }).Entity;

    /// <summary>
    /// After sign-in changed Users.Role: the default membership takes the same role. A person who is a member of no workspace at all (a raw-SQL fixture or
    /// an older import) joins the default one; a person who belongs only to other workspaces is not added to the default one by signing in (Q-105e).
    /// </summary>
    public static async Task MirrorDefaultRoleAsync(ReleaseDbContext db, Users user, DateTime now, CancellationToken ct)
    {
        var mine = await db.Set<WorkspaceMembers>().IgnoreQueryFilters().Where(m => m.UserId == user.Id).ToListAsync(ct);
        var m = mine.FirstOrDefault(x => x.WorkspaceId == WorkspaceIds.Default);
        if (m is null) { if (mine.Count == 0) JoinDefault(db, user, now); }
        else if (m.Role != user.Role) { m.Role = user.Role; m.Version++; }
    }
}
