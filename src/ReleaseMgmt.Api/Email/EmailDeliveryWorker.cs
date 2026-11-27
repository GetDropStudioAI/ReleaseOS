using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Email;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Email;

/// <summary>
/// REOS-167: the background sender. Every <c>Notifications:Email:PollSeconds</c> (and at once after mail was queued) it sends what is due, queues each workspace's daily digests once the hour has come, and once an hour prunes
/// finished messages past retention. A failed pass is logged and raised as a host-wide <c>Notifications/DeliveryFailed</c> alert keyed "email-worker", then retried: never silent (rule 8); a clean pass resolves it.
/// <b>With <c>Notifications:Smtp:Enabled</c> off it does nothing at all</b> (no query, no alert), as it does with <c>Notifications:Email:Worker:Enabled</c> false (tests that drive the processor with a frozen clock).
/// </summary>
public sealed class EmailDeliveryWorker(EmailDeliveryProcessor processor, EmailDigestService digest, IDbContextFactory<ReleaseDbContext> dbf, SyncAlertWriter alerts, EmailOptions options, TimeProvider time,
    ILogger<EmailDeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Enabled || !options.WorkerEnabled) return;
        var nextPrune = time.GetUtcNow();
        var failing = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await WorkspaceLoop.ForEachAsync(dbf, async _ => await digest.QueueAsync(stop), stop);
                    while (await processor.ProcessDueAsync(stop) >= options.Batch) { }   // a full batch: more may be due
                    if (time.GetUtcNow() >= nextPrune)
                    {
                        await WorkspaceLoop.ForEachAsync(dbf, async _ => await processor.PruneAsync(stop), stop);
                        nextPrune = time.GetUtcNow().AddHours(1);
                    }
                    if (failing) { using (WorkspaceScope.HostWide()) await alerts.ResolveAsync("Notifications", "DeliveryFailed", "email-worker", stop); failing = false; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failing = true;
                    log.LogError(ex, "The email sender failed");
                    try { using (WorkspaceScope.HostWide()) await alerts.RaiseAsync("Notifications", "DeliveryFailed", "email-worker", $"The email sender stopped a pass: {ex.GetType().Name}: {ex.Message}", ct: stop); }
                    catch (Exception inner) when (inner is not OperationCanceledException) { log.LogError(inner, "The email sender's failure could not be raised as an alert"); }
                }
                try { await EmailSignal.WaitAsync(options.Poll, stop); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
