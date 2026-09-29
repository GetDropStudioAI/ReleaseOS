using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Realtime;

/// <summary>Drops client states idle for more than 30 days (PROJECT_SCOPE 5.6). A failed run is logged and raised through IAlertSink, then retried next cycle.</summary>
public sealed class SessionStateJanitor(SessionService sessions, IAlertSink alerts, IConfiguration config, ILogger<SessionStateJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var idle = TimeSpan.FromDays(config.GetValue("Session:IdleDays", 30));
        var every = TimeSpan.FromHours(config.GetValue("Session:JanitorHours", 6));
        using var timer = new PeriodicTimer(every);
        try
        {
            do
            {
                try { var n = await sessions.PurgeIdleAsync(idle, stop); if (n > 0) log.LogInformation("Session janitor removed {Count} idle client states", n); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Session janitor failed");
                    await alerts.RaiseAsync("Session", "JanitorFailed", "SessionStateJanitor", "Session state cleanup failed: " + ex.Message, stop);
                }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
