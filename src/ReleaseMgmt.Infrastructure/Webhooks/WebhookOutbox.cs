using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Webhooks;

/// <summary>
/// REOS-164: the outbox. <see cref="ReleaseDbContext"/> calls <see cref="FanOutAsync"/> inside every save, in the same transaction, right after the rows were written: every audit row that arrived with
/// this save (a service's own, and the ones SQLite triggers wrote: the PIR, the decertification) that stands for a catalogue event becomes one <c>WebhookDeliveries</c> row per active subscription of its workspace that asked
/// for it. So an event exists exactly when its audit row does (CLAUDE.md rule 3) and nothing is queued for a change that rolled back. Times come from the audit row, which got them from the injected clock
/// (rule 4). The sender is a separate background service (at-least-once); nothing here makes a network call.
/// </summary>
public static class WebhookOutbox
{
    /// <summary>The highest audit id now. Read before the save: inside the transaction nobody else can have committed audit rows between this read and the save's write (SQLite refuses the write upgrade of a stale read snapshot).</summary>
    public static async Task<long> AuditHighWaterAsync(ReleaseDbContext db, CancellationToken ct) =>
        await db.Set<AuditEvents>().AsNoTracking().IgnoreQueryFilters().MaxAsync(a => (long?)a.Id, ct) ?? 0;

    /// <summary>Queues the deliveries for the audit rows after <paramref name="afterAuditId"/>. Returns the rows to add; the caller saves them.</summary>
    public static async Task<List<WebhookDeliveries>> FanOutAsync(ReleaseDbContext db, long afterAuditId, CancellationToken ct)
    {
        var fresh = await db.Set<AuditEvents>().AsNoTracking().IgnoreQueryFilters().Where(a => a.Id > afterAuditId && a.WorkspaceId != null).OrderBy(a => a.Id).ToListAsync(ct);
        var events = fresh.Select(a => (Row: a, Type: WebhookCatalog.EventFor(a.EntityType, a.Action, a.AfterJson))).Where(x => x.Type is not null).ToList();
        if (events.Count == 0) return [];
        var workspaces = events.Select(e => e.Row.WorkspaceId!).Distinct().ToList();
        var subs = await db.Set<WebhookSubscriptions>().AsNoTracking().IgnoreQueryFilters().Where(s => s.Status == "Active" && workspaces.Contains(s.WorkspaceId)).OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);
        var rows = new List<WebhookDeliveries>();
        foreach (var (a, type) in events)
        {
            string? payload = null;
            foreach (var s in subs.Where(s => s.WorkspaceId == a.WorkspaceId && WebhookCatalog.Matches(s.EventTypes, type!)))
            {
                var eventId = WebhookCatalog.EventId(a.Id);
                payload ??= WebhookCatalog.BuildPayload(eventId, type!, a.OccurredAt, a.WorkspaceId!, a.EntityType, a.EntityId, a.Action, a.ReleaseTrainId, a.ActorUserId, a.BeforeJson, a.AfterJson);
                rows.Add(new WebhookDeliveries
                {
                    Id = Ids.New(), SubscriptionId = s.Id, EventId = eventId, EventType = type!, AuditEventId = a.Id, PayloadJson = payload,
                    Status = "Pending", NextAttemptAt = a.OccurredAt, CreatedAt = a.OccurredAt, WorkspaceId = s.WorkspaceId,
                });
            }
        }
        return rows;
    }
}

/// <summary>Wakes the delivery worker after a commit that queued a delivery (a test event, a replay), so the person does not wait for the next poll. Same idea as <see cref="AuditSealSignal"/>.</summary>
public static class WebhookSignal
{
    private static readonly SemaphoreSlim Gate = new(0, 1);
    public static void Notify() { try { Gate.Release(); } catch (SemaphoreFullException) { /* one is already pending */ } }
    public static Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct) => Gate.WaitAsync(interval, ct);
}
