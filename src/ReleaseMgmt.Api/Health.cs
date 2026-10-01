using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Backup;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Api;

/// <summary>
/// <c>GET /healthz</c> (PROJECT_SCOPE: "reporting DB, poller, watchdog"). Anonymous, so it carries states and times only. 200 <c>Healthy</c> unless the database
/// check fails or an <b>enabled</b> background loop has stalled (REOS-69), then 503 <c>Unhealthy</c>. Each loop reports <c>disabled</c> (<c>Sync:Enabled=false</c>),
/// <c>running</c> with <c>lastCycleUtc</c> (null until its first pass) or <c>stalled</c> (no pass for 3 of its intervals, or its loop ended); see
/// <see cref="ServiceHeartbeat"/>. A connector that fails against the ITSM is not a stall: that is a sync alert, and the poller is still alive.
/// </summary>
public static class Health
{
    public static async Task<IResult> CheckAsync(IDbContextFactory<ReleaseDbContext> dbf, BackupService backup, ServiceHeartbeat poller, ServiceHeartbeat watchdog,
        ILogger log, CancellationToken ct)
    {
        string db;
        try
        {
            await using var ctx = await dbf.CreateDbContextAsync(ct);
            await ctx.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            db = "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "healthz DB check failed"); db = "failed"; }

        var p = poller.Read();
        var w = watchdog.Read();
        // The loops log their own end (SyncPollerService / SyncWatchdogService); a probe every few seconds must not add a log line each time.
        var healthy = db == "ok" && p.State != ServiceHeartbeat.Stalled && w.State != ServiceHeartbeat.Stalled;
        var body = new { status = healthy ? "Healthy" : "Unhealthy", db, backup = backup.LastSuccessUtc, poller = View(p), watchdog = View(w) };
        return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static object View(HeartbeatSnapshot s) => new { state = s.State, lastCycleUtc = s.LastCycleUtc, stalledAfterSeconds = s.StalledAfterSeconds };
}
