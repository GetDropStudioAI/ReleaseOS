using Microsoft.EntityFrameworkCore;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>
/// Q-SEC-B8m: the two migrations execute db/schema.sql verbatim (Q-001), so a schema change re-scaffolds them under new ids and an existing database is
/// brought forward by a manual script in <c>db/upgrades/</c>. Without this check such a database fails start-up deep inside EF ("table Users already
/// exists"); with it, the app stops before migrating and names the script to run.
/// </summary>
public static class UpgradeGuard
{
    public static async Task EnsureNoManualUpgradePendingAsync(DbContext db, CancellationToken ct = default)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var foreign = applied.Where(m => !known.Contains(m)).ToList();
        if (foreign.Count == 0) return;
        throw new InvalidOperationException(
            $"The database was created by an older build (migrations {string.Join(", ", foreign)}). Stop here: back it up, run the scripts in db/upgrades/ " +
            "that follow that version, in order, then start the app again (docs/RUNBOOK_OPERATIONS.md §8).");
    }
}
