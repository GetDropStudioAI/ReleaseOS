using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Webhooks;

/// <summary>
/// REOS-164: sends the deliveries that are due, at-least-once. A delivery is Pending until a 2xx answer; each try is one <c>WebhookDeliveryAttempts</c> row; a failed try schedules the next with exponential backoff
/// (<see cref="WebhookBackoff"/>); the last try that fails makes it a DeadLetter. A delivery is recorded after the call, so a host that dies in between sends it once more (hence at-least-once: the delivery id is stable and a
/// receiver drops a repeat). Persistent failure is ONE alert per subscription (<c>Webhook/DeliveryFailed</c>, keyed by the subscription) whose count and text say how many deliveries have been dead-lettered since the last good one;
/// it is raised when a delivery is dead-lettered, never per attempt, and resolves itself when a delivery succeeds again (rule 8). Times come from the injected clock; the audit rows record only the end of a delivery
/// (delivered, dead-lettered), the attempt rows are the log of the rest.
/// </summary>
public sealed class WebhookDeliveryProcessor(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, WebhookOutOptions options, IWebhookSigningSecrets secrets, IWebhookSender sender,
    SyncAlertWriter alerts, ILogger<WebhookDeliveryProcessor> log, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private readonly TimeProvider _time = time;

    private sealed record Due(string Id, string WorkspaceId, string SubscriptionId, string Name, string Host, string ProtectedUrl, int SecretVersion, string EventType, string Payload, int AttemptCount);

    /// <summary>Sends up to <c>Batch</c> deliveries whose time has come, oldest first. Returns how many were attempted.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        List<Due> due;
        using (WorkspaceScope.HostWide())
        {
            await using var db = await OpenAsync(ct);
            var now = Now;
            due = await (from d in db.Set<WebhookDeliveries>().AsNoTracking()
                         join s in db.Set<WebhookSubscriptions>().AsNoTracking() on d.SubscriptionId equals s.Id
                         where d.Status == "Pending" && d.NextAttemptAt <= now && s.Status == "Active"   // a paused subscription holds its queue until it is resumed
                         orderby d.NextAttemptAt, d.CreatedAt, d.Id
                         select new Due(d.Id, d.WorkspaceId, s.Id, s.Name, s.Host, s.ProtectedUrl, s.SecretVersion, d.EventType, d.PayloadJson, d.AttemptCount)).Take(options.Batch).ToListAsync(ct);
        }
        foreach (var d in due)
        {
            ct.ThrowIfCancellationRequested();
            using (WorkspaceScope.Enter(d.WorkspaceId)) await AttemptAsync(d, ct);
        }
        return due.Count;
    }

    private async Task AttemptAsync(Due d, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        WebhookSendResult result;
        string? secret = null; string? secretProblem = null;
        try { secret = await secrets.GetAsync(d.SubscriptionId, ct); }
        catch (InvalidOperationException ex) { secretProblem = ex.Message; }
        if (secret is null) result = new(false, null, secretProblem ?? "The subscription has no signing secret; rotate it");
        else
        {
            var ts = _time.GetUtcNow().ToUnixTimeSeconds();   // this attempt's time: a retry is signed afresh, so the receiver's replay window never refuses a late retry
            var request = new WebhookRequest(d.Id, ts, d.EventType, d.SecretVersion, d.Payload, WebhookSignature.Sign(secret, ts, d.Id, d.Payload));
            result = await sender.SendAsync(new(d.SubscriptionId, d.Name, d.Host, d.ProtectedUrl), request, ct);
        }
        var ms = (int)Math.Min(int.MaxValue, _time.GetElapsedTime(started).TotalMilliseconds);

        var outcome = await RunAsync<string?>(async db =>
        {
            var row = await db.Set<WebhookDeliveries>().SingleOrDefaultAsync(x => x.Id == d.Id && x.Status == "Pending", ct);
            if (row is null) return ServiceResult<string?>.Ok(null);   // deleted or finished meanwhile (the subscription was removed)
            var now = Now;
            var no = row.AttemptCount + 1;
            var reason = result.Reason is { Length: > 500 } r ? r[..500] : result.Reason;
            db.Set<WebhookDeliveryAttempts>().Add(new WebhookDeliveryAttempts { Id = Ids.New(), DeliveryId = row.Id, AttemptNo = no, AttemptedAt = now, StatusCode = result.HttpStatus, DurationMs = ms, Error = result.Delivered ? null : reason, WorkspaceId = row.WorkspaceId });
            row.AttemptCount = no; row.LastAttemptAt = now; row.LastStatusCode = result.HttpStatus; row.LastError = result.Delivered ? null : reason; row.Version++;
            string state;
            if (result.Delivered)
            {
                row.Status = "Delivered"; row.NextAttemptAt = null; row.DeliveredAt = now; row.FinishedAt = now; state = "Delivered";
                Audit(db, null, null, "WebhookDelivery", row.Id, "Delivered", null, new { subscription = d.Name, row.EventType, row.EventId, attempts = no, status = result.HttpStatus });
            }
            else if (no >= options.MaxAttempts)
            {
                row.Status = "DeadLetter"; row.NextAttemptAt = null; row.FinishedAt = now; state = "DeadLetter";
                Audit(db, null, null, "WebhookDelivery", row.Id, "DeadLetter", null, new { subscription = d.Name, row.EventType, row.EventId, attempts = no, status = result.HttpStatus, error = reason });
            }
            else { row.NextAttemptAt = now + WebhookBackoff.Delay(no, options.BackoffBase, options.BackoffCap); state = "Retry"; }
            await db.SaveChangesAsync(ct);
            return ServiceResult<string?>.Ok(state);
        }, ct);
        if (!outcome.IsOk) throw new InvalidOperationException($"Delivery {d.Id} was attempted but the result could not be recorded: " + (outcome.Failures.Count > 0 ? outcome.Failures[0].Message : outcome.Missing));

        try
        {
            if (outcome.Value == "DeadLetter") await RaiseAsync(d, result.Reason, ct);
            else if (outcome.Value == "Delivered") await alerts.ResolveAsync("Webhook", "DeliveryFailed", d.SubscriptionId, ct);   // a later success clears the earlier failure
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The delivery is recorded and visible in the log; only its alert failed. Nothing further can carry this, so it is logged loudly (never the address).
            log.LogError(ex, "Webhook subscription {Subscription}: the delivery was recorded as {State} but its alert could not be written or resolved", d.SubscriptionId, outcome.Value);
        }
    }

    private async Task RaiseAsync(Due d, string? lastError, CancellationToken ct)
    {
        int count;
        await using (var db = await OpenAsync(ct))
        {
            var lastOk = await db.Set<WebhookDeliveries>().AsNoTracking().Where(x => x.SubscriptionId == d.SubscriptionId && x.Status == "Delivered").OrderByDescending(x => x.DeliveredAt).Select(x => x.DeliveredAt).FirstOrDefaultAsync(ct);
            count = await db.Set<WebhookDeliveries>().AsNoTracking().CountAsync(x => x.SubscriptionId == d.SubscriptionId && x.Status == "DeadLetter" && (lastOk == null || x.FinishedAt > lastOk), ct);
        }
        var noun = count == 1 ? "delivery" : "deliveries";
        await alerts.RaiseAsync("Webhook", "DeliveryFailed", d.SubscriptionId,
            $"{count} webhook {noun} to \"{d.Name}\" ({d.Host}) could not be delivered after {options.MaxAttempts} attempts and {(count == 1 ? "was" : "were")} dead-lettered since the last good one. Last error: {lastError ?? "unknown"}. Fix the receiver, then replay them from Webhooks.", ct: ct);
    }
}
