using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>
/// REOS-105: background services (sync poller and watchdog, reminders, export worker) run once per workspace, inside it, so every query, alert,
/// notification and live push of a pass belongs to that workspace alone. Archived workspaces are skipped. A pass already inside a workspace (a request's
/// "sync now", a test that entered one) runs for that workspace only.
/// </summary>
public static class WorkspaceLoop
{
    public static async Task<List<string>> ActiveAsync(IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct)
    {
        if (WorkspaceScope.Current is { } current) return [current];
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Set<Workspaces>().AsNoTracking().Where(w => w.ArchivedAt == null).OrderBy(w => w.Id).Select(w => w.Id).ToListAsync(ct);
    }

    /// <summary>Runs <paramref name="body"/> inside each active workspace in turn. A failure in one workspace propagates (the caller's error handling and
    /// alerting apply, CLAUDE.md rule 8); callers that must carry on with the next workspace catch per call.</summary>
    public static async Task ForEachAsync(IDbContextFactory<ReleaseDbContext> dbf, Func<string, Task> body, CancellationToken ct)
    {
        foreach (var ws in await ActiveAsync(dbf, ct))
        {
            ct.ThrowIfCancellationRequested();
            using (WorkspaceScope.Enter(ws)) await body(ws);
        }
    }
}
