using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-104 (D45, Q-104a): workspaces on one host. Every root row belongs to one; db/schema.sql creates the default workspace, and until REOS-105 scopes
/// queries and permissions every row is written there (the entities' WorkspaceId defaults to it).
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

    /// <summary>
    /// A new user is a member of the default workspace with their role. Until REOS-105 moves the triggers and policies onto memberships, Users.Role is the
    /// role that counts, so every write that creates a user or changes Users.Role keeps this membership equal to it, in the same transaction.
    /// </summary>
    public static void JoinDefault(ReleaseDbContext db, Users user, DateTime now) =>
        db.Set<WorkspaceMembers>().Add(new WorkspaceMembers { WorkspaceId = WorkspaceIds.Default, UserId = user.Id, Role = user.Role, AddedAt = now });

    /// <summary>After Users.Role changed: the default membership takes the same role (created if a raw-SQL fixture or an import left none).</summary>
    public static async Task MirrorDefaultRoleAsync(ReleaseDbContext db, Users user, DateTime now, CancellationToken ct)
    {
        var m = await db.Set<WorkspaceMembers>().FindAsync([WorkspaceIds.Default, user.Id], ct);
        if (m is null) JoinDefault(db, user, now);
        else if (m.Role != user.Role) { m.Role = user.Role; m.Version++; }
    }
}
