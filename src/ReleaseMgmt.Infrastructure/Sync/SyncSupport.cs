using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>Reads shared by the poller, the watchdog and the link listing.</summary>
public static class SyncSupport
{
    /// <summary>The earliest start among the deployment windows open at <paramref name="now"/> (StartsAt &lt;= now &lt; EndsAt), or null when none is open.</summary>
    public static async Task<DateTime?> WindowOpenSinceAsync(IDbContextFactory<ReleaseDbContext> dbf, DateTime now, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Set<DeploymentWindows>().AsNoTracking().Where(w => w.StartsAt <= now && w.EndsAt > now).MinAsync(w => (DateTime?)w.StartsAt, ct);
    }
}
