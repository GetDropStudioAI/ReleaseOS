using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ReleaseMgmt.Api.Webhooks;

/// <summary>
/// REOS-164: the background sender. Every <c>Webhooks:Out:PollSeconds</c> (and at once after a test event or a replay) it sends what is due (<see cref="WebhookDeliveryProcessor"/>), and once an hour it prunes finished deliveries older than
/// <c>Webhooks:Out:RetentionDays</c>. A failed pass is logged and raised as a host-wide <c>Webhook/DeliveryFailed</c> alert keyed "worker", then retried: never silent (rule 8); a clean pass resolves it.
/// <c>Webhooks:Out:Worker:Enabled</c> false stops it (tests that drive the processor with a frozen clock).
/// </summary>
public sealed class WebhookDeliveryWorker(WebhookDeliveryProcessor processor, WebhookSubscriptionService subscriptions, IDbContextFactory<ReleaseDbContext> dbf, SyncAlertWriter alerts, WebhookOutOptions options,
    TimeProvider time, ILogger<WebhookDeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.WorkerEnabled) return;
        var nextPrune = time.GetUtcNow();
        var failing = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    while (await processor.ProcessDueAsync(stop) >= options.Batch) { }   // a full batch: more may be due
                    if (time.GetUtcNow() >= nextPrune)
                    {
                        await WorkspaceLoop.ForEachAsync(dbf, async _ => await subscriptions.PruneAsync(stop), stop);
                        nextPrune = time.GetUtcNow().AddHours(1);
                    }
                    if (failing) { using (WorkspaceScope.HostWide()) await alerts.ResolveAsync("Webhook", "DeliveryFailed", "worker", stop); failing = false; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failing = true;
                    log.LogError(ex, "The webhook sender failed");
                    try { using (WorkspaceScope.HostWide()) await alerts.RaiseAsync("Webhook", "DeliveryFailed", "worker", $"The webhook sender stopped a pass: {ex.GetType().Name}: {ex.Message}", ct: stop); }
                    catch (Exception inner) when (inner is not OperationCanceledException) { log.LogError(inner, "The webhook sender's failure could not be raised as an alert"); }
                }
                try { await WebhookSignal.WaitAsync(options.Poll, stop); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
