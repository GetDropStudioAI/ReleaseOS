using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// The watchdog (PROJECT_SCOPE 5.3.5, D30): a timer separate from the poller checks every enabled connector's <c>LastCycleCompletedAt</c>. When no cycle has completed
/// for 3 poll intervals (<see cref="SyncIntervals.StallThreshold"/>: 15 min, or 3 min once a deployment window has been open for a full slow interval) it raises
/// <c>SyncEngine/Stalled</c> (key "poller:{source}") and resolves it when a cycle completes again. A cycle counts even when it failed against the ITSM:
/// this alert is about the poller being dead, not the connector being down. The watchdog's own start is the earliest reference, so a restart is not a stall.
/// </summary>
public sealed class SyncWatchdogService : BackgroundService
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf;
    private readonly TimeProvider _time;
    private readonly SyncAlertWriter _alerts;
    private readonly IAlertSink _sink;
    private readonly SyncOptions _o;
    private readonly ILogger<SyncWatchdogService> _log;
    private readonly DateTime _startedAt;

    public SyncWatchdogService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, SyncAlertWriter alerts, IAlertSink sink, SyncOptions options, ILogger<SyncWatchdogService> log)
    {
        _dbf = dbf; _time = time; _alerts = alerts; _sink = sink; _o = options; _log = log;
        _startedAt = Now;
    }

    private DateTime Now { get { var t = _time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_o.Enabled) return;
        using var timer = new PeriodicTimer(_o.Watchdog, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try { await CheckOnceAsync(stop); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // If the store itself is failing there is no table to alert in; the log is all that is left.
                    _log.LogError(ex, "The sync watchdog's check failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>One check. Returns the sources that are stalled now.</summary>
    public async Task<IReadOnlyList<string>> CheckOnceAsync(CancellationToken ct = default)
    {
        var now = Now;
        List<ConnectorState> states;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            states = await db.Set<ConnectorState>().AsNoTracking().Where(s => s.IsEnabled).ToListAsync(ct);
        if (states.Count == 0) return [];

        var since = await SyncSupport.WindowOpenSinceAsync(_dbf, now, ct);
        var threshold = SyncIntervals.StallThreshold(now, since, _o.Poll, _o.WindowPoll);
        var stalled = new List<string>();
        foreach (var s in states)
        {
            var key = SyncPollerService.StallKey(s.SourceSystem);
            if (SyncIntervals.IsStalled(s.LastCycleCompletedAt, _startedAt, now, threshold))
            {
                stalled.Add(s.SourceSystem);
                var last = s.LastCycleCompletedAt is DateTime t ? $"since {t:yyyy-MM-dd HH:mm:ss}Z" : "since the app started";
                var msg = $"No {s.SourceSystem} sync cycle has completed {last} (more than 3 poll intervals of {(threshold / SyncIntervals.StallIntervals).TotalMinutes:0.##} min); the poller is stopped or stuck";
                _log.LogError("{Message}", msg);
                await _sink.RaiseAsync("SyncEngine", "Stalled", key, msg, ct);
                await _alerts.RaiseAsync("SyncEngine", "Stalled", key, msg, null, ct);
            }
            else await _alerts.ResolveAsync("SyncEngine", "Stalled", key, ct);
        }
        return stalled;
    }
}
