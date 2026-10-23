using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Roles are held per workspace (D45, REOS-105). These read the role of the context's workspace, or of the default workspace for a context created outside
/// any workspace (start-up and the tests that drive services directly). Only active users count where a list of people is wanted.
/// </summary>
public static class Memberships
{
    public static string WorkspaceOf(ReleaseDbContext db) => db.WorkspaceId ?? WorkspaceIds.Default;

    public static async Task<string?> RoleOf(ReleaseDbContext db, string? userId, CancellationToken ct = default)
    {
        if (userId is null) return null;
        var ws = WorkspaceOf(db);
        return await db.Set<WorkspaceMembers>().IgnoreQueryFilters().Where(m => m.WorkspaceId == ws && m.UserId == userId).Select(m => m.Role).SingleOrDefaultAsync(ct);
    }

    /// <summary>Active people holding one of <paramref name="roles"/> in the context's workspace.</summary>
    public static IQueryable<Users> ActiveWithRole(ReleaseDbContext db, params string[] roles)
    {
        var ws = WorkspaceOf(db);
        return db.Set<Users>().Where(u => u.IsActive && db.Set<WorkspaceMembers>().IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.WorkspaceId == ws && roles.Contains(m.Role)));
    }

    /// <summary>Active and inactive members of the context's workspace.</summary>
    public static IQueryable<Users> Members(ReleaseDbContext db)
    {
        var ws = WorkspaceOf(db);
        return db.Set<Users>().Where(u => db.Set<WorkspaceMembers>().IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.WorkspaceId == ws));
    }
}
