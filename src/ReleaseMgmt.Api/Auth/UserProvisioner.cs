using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>Just-in-time provisioning (Q-003): keeps Users (read by the SoD triggers) in step with the role the user signs in with.</summary>
public sealed class UserProvisioner(IDbContextFactory<ReleaseDbContext> dbf)
{
    public async Task<string> UpsertAsync(string email, string displayName, string role, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var u = await db.Set<Users>().SingleOrDefaultAsync(x => x.Email == email, ct); // Email is COLLATE NOCASE in the schema
        if (u is null)
        {
            u = new Users { Id = Ids.New(), Email = email, DisplayName = displayName, Role = role };
            db.Set<Users>().Add(u);
        }
        else if (u.Role != role || u.DisplayName != displayName || !u.IsActive)
        {
            u.Role = role; u.DisplayName = displayName; u.IsActive = true; u.Version++;
        }
        await db.SaveChangesAsync(ct);
        return u.Id;
    }
}
