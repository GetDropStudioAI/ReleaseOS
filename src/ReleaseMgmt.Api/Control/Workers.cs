using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Control;

/// <summary>
/// Seals the audit chain (REOS-114): right after commits (<see cref="AuditSealSignal"/>), every <c>Audit:Seal:IntervalSeconds</c> (default 5) for rows written by triggers or outside the services,
/// and once at start for the rows from before the chain; and verifies every chain each <c>Audit:Verify:IntervalMinutes</c> (default 60), raising <c>Audit/IntegrityFailed</c> when one does not verify.
/// <c>Audit:Seal:Enabled</c> false stops it (tests that compare whole databases). A failed pass is logged and raised as a host-wide alert, then retried: never silent (rule 8).
/// </summary>
public sealed class AuditChainWorker(AuditChainService chain, SyncAlertWriter alerts, IConfiguration config, TimeProvider time, ILogger<AuditChainWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!config.GetValue("Audit:Seal:Enabled", true)) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Audit:Seal:IntervalSeconds", 5), 1, 3600));
        var verifyEvery = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Audit:Verify:IntervalMinutes", 60), 1, 24 * 60));
        var nextVerify = time.GetUtcNow();
        var failing = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Audit:Seal:StartDelaySeconds", 2), 0, 600)), stop);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await chain.SealPendingAsync(stop);
                    if (time.GetUtcNow() >= nextVerify) { await chain.VerifyAllAsync(stop); nextVerify = time.GetUtcNow() + verifyEvery; }
                    if (failing) { using (WorkspaceScope.HostWide()) await alerts.ResolveAsync("Audit", "IntegrityFailed", "worker", stop); failing = false; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failing = true;
                    log.LogError(ex, "Audit chain sealing or verification failed");
                    using (WorkspaceScope.HostWide()) await alerts.RaiseAsync("Audit", "IntegrityFailed", "worker", $"The audit chain could not be sealed or verified: {ex.GetType().Name}: {ex.Message}", ct: stop);
                }
                try { if (await AuditSealSignal.WaitAsync(interval, stop)) await Task.Delay(TimeSpan.FromMilliseconds(200), stop); }   // a little after a signal, so one pass takes a burst of writes
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>
/// Telemetry housekeeping (REOS-111, 112), per workspace: samples past <c>Telemetry:RetentionDays</c> are summarised and dropped each <c>Telemetry:MaintenanceMinutes</c> (default 60), and control calls left
/// Pending by a host that stopped mid-call are closed as Failed every minute. A failure raises <c>Telemetry/RetentionFailed</c> (or the control alert) and is retried next pass. <c>Telemetry:Maintenance:Enabled</c> false stops it.
/// </summary>
public sealed class TelemetryMaintenanceWorker(IDbContextFactory<ReleaseDbContext> dbf, TelemetryService telemetry, DeploymentControlService control, SyncAlertWriter alerts, IConfiguration config,
    TimeProvider time, ILogger<TelemetryMaintenanceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!config.GetValue("Telemetry:Maintenance:Enabled", true)) return;
        var every = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Telemetry:MaintenanceMinutes", 60), 1, 24 * 60));
        var nextRetention = time.GetUtcNow().AddSeconds(Math.Clamp(config.GetValue("Telemetry:Maintenance:StartDelaySeconds", 30), 0, 3600));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Control:SweepSeconds", 60), 1, 3600)));
        try
        {
            do
            {
                try
                {
                    await WorkspaceLoop.ForEachAsync(dbf, async ws =>
                    {
                        try { await control.FailStalePendingAsync(stop); await alerts.ResolveAsync("Deployment", "ControlFailed", "sweeper", stop); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            log.LogError(ex, "Closing stale control calls failed in workspace {Workspace}", ws);
                            await alerts.RaiseAsync("Deployment", "ControlFailed", "sweeper", $"Control calls left pending could not be closed: {ex.GetType().Name}: {ex.Message}", ct: stop);
                        }
                        if (time.GetUtcNow() < nextRetention) return;
                        try
                        {
                            var r = await telemetry.RunRetentionAsync(stop);
                            if (!r.IsOk) throw new InvalidOperationException(r.Failures.Count > 0 ? r.Failures[0].Message : "retention was refused");
                            await alerts.ResolveAsync("Telemetry", "RetentionFailed", "retention", stop);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            log.LogError(ex, "Telemetry retention failed in workspace {Workspace}", ws);
                            await alerts.RaiseAsync("Telemetry", "RetentionFailed", "retention", $"Summarising old telemetry failed: {ex.GetType().Name}: {ex.Message}", ct: stop);
                        }
                    }, stop);
                    if (time.GetUtcNow() >= nextRetention) nextRetention = time.GetUtcNow() + every;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Telemetry maintenance failed");
                    using (WorkspaceScope.HostWide()) await alerts.RaiseAsync("Telemetry", "RetentionFailed", "maintenance", $"Telemetry maintenance failed: {ex.GetType().Name}: {ex.Message}", ct: stop);
                }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
