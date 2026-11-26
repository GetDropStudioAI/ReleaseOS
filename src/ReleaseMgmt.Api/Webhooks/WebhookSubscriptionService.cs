using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Sync;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Webhooks;

public sealed record WebhookSubscriptionView(string Id, string Name, string Host, IReadOnlyList<string> EventTypes, string Status, int SecretVersion, bool HasSecret, string? SecretRotatedAt,
    string CreatedAt, string? CreatedBy, string? LastChangedAt, int Pending, int DeadLetters, int Version);
/// <summary>The only time the secret exists outside the receiver's own storage: it is shown once, and the database never holds it.</summary>
public sealed record CreatedWebhookSubscription(WebhookSubscriptionView Subscription, string Secret);
public sealed record WebhookDeliveryView(string Id, string SubscriptionId, string SubscriptionName, string EventId, string EventType, string Status, int AttemptCount, string? NextAttemptAt,
    string? LastAttemptAt, int? LastStatusCode, string? LastError, string? DeliveredAt, string? FinishedAt, string? ReplayOfDeliveryId, string CreatedAt, int Version);
public sealed record WebhookAttemptView(int AttemptNo, string AttemptedAt, int? StatusCode, int DurationMs, string? Error);
public sealed record WebhookDeliveryDetail(WebhookDeliveryView Delivery, string PayloadJson, IReadOnlyList<WebhookAttemptView> Attempts);

/// <summary>
/// REOS-164 (D61): the workspace's outbound webhook subscriptions, the Admin screen's service (RTE and Release Manager; the endpoints enforce the policy). An address must be https, carry no credentials, not be a private or local
/// target and be on the administrator's allow-list (<see cref="WebhookOutOptions"/>); it is stored encrypted (Data Protection, like the other webhook addresses) and only its host is ever shown. The HMAC secret is made here, written
/// to a Data Protection file (<see cref="IWebhookSigningSecrets"/>), shown once in the answer to create or rotate and never kept in the database or any log. Every change is one transaction with one audit row (rule 3) and a Version bump
/// (If-Match). Pause keeps the subscription and its log but queues nothing new; delete removes the subscription, its log and its secret after asking in a confirmation dialog. A replay is a new delivery row that points at the one it
/// repeats; the original is history and never changes.
/// </summary>
public sealed class WebhookSubscriptionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, WebhookOutOptions options, IWebhookSigningSecrets secrets, IWebhookUrlVault urlVault,
    OutboundAddressPolicy addressPolicy, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "WebhookSubscription", DeliveryEntity = "WebhookDelivery";
    public const int MaxName = 100;
    private static readonly UrlLabels Labels = new("The address", "https://hooks.example.com/releaseos", "Webhooks:Out:AllowedHosts", "Webhooks:Out:AllowPrivateTargets", AllowQuery: true);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string? Iso(DateTime? t) => t is { } v ? Iso(v) : null;

    // ---- reading ----

    public async Task<List<WebhookSubscriptionView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<WebhookSubscriptions>().AsNoTracking().OrderBy(s => s.Name).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<WebhookSubscriptionView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<WebhookSubscriptions>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private async Task<List<WebhookSubscriptionView>> ViewsAsync(ReleaseDbContext db, List<WebhookSubscriptions> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.Set<WebhookDeliveries>().AsNoTracking().Where(d => ids.Contains(d.SubscriptionId) && d.Status != "Delivered")
            .GroupBy(d => new { d.SubscriptionId, d.Status }).Select(g => new { g.Key.SubscriptionId, g.Key.Status, N = g.Count() }).ToListAsync(ct);
        var userIds = rows.Select(r => r.CreatedByUserId).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var views = new List<WebhookSubscriptionView>();
        foreach (var r in rows)
            views.Add(new(r.Id, r.Name, r.Host, WebhookCatalog.Split(r.EventTypes), r.Status, r.SecretVersion, await secrets.ExistsAsync(r.Id, ct), Iso(r.SecretRotatedAt), Iso(r.CreatedAt),
                names.GetValueOrDefault(r.CreatedByUserId), Iso(r.LastChangedAt),
                counts.Where(c => c.SubscriptionId == r.Id && c.Status == "Pending").Sum(c => c.N), counts.Where(c => c.SubscriptionId == r.Id && c.Status == "DeadLetter").Sum(c => c.N), r.Version));
        return views;
    }

    private static object Conflict(WebhookSubscriptions s) => new { s.Id, s.Name, s.Host, s.Status, s.Version };
    private static object Snapshot(WebhookSubscriptions s) => new { s.Name, s.Host, eventTypes = s.EventTypes, s.Status, s.SecretVersion };   // never the address, never a secret

    // ---- checking input ----

    private ServiceResult<CheckedWebhookUrl> CheckUrl(string? raw)
    {
        var basic = WebhookUrlPolicy.Check(raw, options.AllowPrivateTargets, addressPolicy);
        if (!basic.IsOk) return ServiceResult<CheckedWebhookUrl>.Fail(new GuardFailure(WebhookGuards.UrlRefused, basic.Failures[0].Message));
        if (options.Closed)
            return ServiceResult<CheckedWebhookUrl>.Fail(new GuardFailure(WebhookGuards.UrlRefused, "No host is on the outbound allow-list yet, so nothing can be posted. Ask the administrator to add the receiver's host to Webhooks:Out:AllowedHosts"));
        var refused = ConnectorUrlPolicy.Validate(basic.Value!.Normalised, isDevelopment: false, options.AllowPrivateTargets, options.Open ? [] : options.AllowedHosts, addressPolicy.IsBlocked, Labels);
        return refused is null ? basic : ServiceResult<CheckedWebhookUrl>.Fail(new GuardFailure(WebhookGuards.UrlRefused, refused));
    }

    private static GuardFailure? CheckName(ref string? name)
    {
        name = (name ?? "").Trim();
        return name.Length is 0 or > MaxName ? new GuardFailure(WebhookGuards.Invalid, $"A name is 1 to {MaxName} characters (say what receives it, e.g. \"Release dashboard\")") : null;
    }

    private static GuardFailure NeedEvents() => new(WebhookGuards.Invalid, "Choose at least one event, from the list or All events", [.. WebhookCatalog.All.Select(e => e.Key), WebhookCatalog.AllEvents]);

    // ---- writing ----

    public Task<ServiceResult<CreatedWebhookSubscription>> CreateAsync(string? name, string? url, IEnumerable<string>? eventTypes, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (CheckName(ref name) is { } badName) return ServiceResult<CreatedWebhookSubscription>.Fail(badName);
            if (WebhookCatalog.Normalise(eventTypes) is not { } events) return ServiceResult<CreatedWebhookSubscription>.Fail(NeedEvents());
            var checkedUrl = CheckUrl(url);
            if (!checkedUrl.IsOk) return checkedUrl.Failed<CreatedWebhookSubscription>();
            if (await db.Set<WebhookSubscriptions>().CountAsync(ct) >= options.MaxPerWorkspace)
                return ServiceResult<CreatedWebhookSubscription>.Fail(new GuardFailure(WebhookGuards.TooMany, $"A workspace has at most {options.MaxPerWorkspace} webhook subscriptions; delete one first"));
            var stored = urlVault.Protect(checkedUrl.Value!.Normalised);
            if (await db.Set<WebhookSubscriptions>().AnyAsync(s => s.UrlHmac == stored.UrlHmac, ct))
                return ServiceResult<CreatedWebhookSubscription>.Fail(new GuardFailure(WebhookGuards.DuplicateUrl, "This address is already a subscription of this workspace; change that one instead"));
            var now = Now;
            var row = new WebhookSubscriptions
            {
                Id = Ids.New(), Name = name!, ProtectedUrl = stored.ProtectedUrl, UrlHmac = stored.UrlHmac, Host = checkedUrl.Value.Host, EventTypes = events, CreatedByUserId = actor.UserId, CreatedAt = now,
                LastChangedByUserId = actor.UserId, LastChangedAt = now, WorkspaceId = Memberships.WorkspaceOf(db),
            };
            db.Set<WebhookSubscriptions>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, Snapshot(row));
            var secret = await secrets.RotateAsync(row.Id, ct);
            try { await db.SaveChangesAsync(ct); }
            catch { await secrets.ClearAsync(row.Id, CancellationToken.None); throw; }   // no row, so no secret to keep
            return ServiceResult<CreatedWebhookSubscription>.Ok(new(View(row), secret));
        }, ct);

    private WebhookSubscriptionView View(WebhookSubscriptions r) =>
        new(r.Id, r.Name, r.Host, WebhookCatalog.Split(r.EventTypes), r.Status, r.SecretVersion, true, Iso(r.SecretRotatedAt), Iso(r.CreatedAt), null, Iso(r.LastChangedAt), 0, 0, r.Version);

    private async Task<ServiceResult<WebhookSubscriptionView>> ViewResult(ReleaseDbContext db, WebhookSubscriptions r, CancellationToken ct) =>
        ServiceResult<WebhookSubscriptionView>.Ok((await ViewsAsync(db, [r], ct))[0]);

    /// <summary>Name, events and (optionally) address. A new address is checked like a new subscription's; the secret is not touched.</summary>
    public Task<ServiceResult<WebhookSubscriptionView>> UpdateAsync(string id, string? name, string? url, IEnumerable<string>? eventTypes, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<WebhookSubscriptionView>.NotFound("Webhook subscription");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<WebhookSubscriptionView>.Conflict(Conflict(s));
            if (CheckName(ref name) is { } badName) return ServiceResult<WebhookSubscriptionView>.Fail(badName);
            if (WebhookCatalog.Normalise(eventTypes) is not { } events) return ServiceResult<WebhookSubscriptionView>.Fail(NeedEvents());
            var before = Snapshot(s);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var checkedUrl = CheckUrl(url);
                if (!checkedUrl.IsOk) return checkedUrl.Failed<WebhookSubscriptionView>();
                var stored = urlVault.Protect(checkedUrl.Value!.Normalised);
                if (stored.UrlHmac != s.UrlHmac)
                {
                    if (await db.Set<WebhookSubscriptions>().AnyAsync(x => x.UrlHmac == stored.UrlHmac && x.Id != id, ct))
                        return ServiceResult<WebhookSubscriptionView>.Fail(new GuardFailure(WebhookGuards.DuplicateUrl, "This address is already a subscription of this workspace"));
                    s.ProtectedUrl = stored.ProtectedUrl; s.UrlHmac = stored.UrlHmac; s.Host = checkedUrl.Value.Host;
                }
            }
            if (s.Name == name && s.EventTypes == events && db.Entry(s).State == EntityState.Unchanged) return await ViewResult(db, s, ct);   // nothing changed: no version, no audit row
            s.Name = name!; s.EventTypes = events;
            Stamp(s, actor);
            Audit(db, actor, null, Entity, s.Id, "Update", before, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return await ViewResult(db, s, ct);
        }, ct);

    private void Stamp(WebhookSubscriptions s, Actor actor) { s.LastChangedByUserId = actor.UserId; s.LastChangedAt = Now; s.Version++; }

    public Task<ServiceResult<WebhookSubscriptionView>> PauseAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetStatusAsync(id, "Paused", actor, expectedVersion, ct);
    public Task<ServiceResult<WebhookSubscriptionView>> ResumeAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetStatusAsync(id, "Active", actor, expectedVersion, ct);

    private Task<ServiceResult<WebhookSubscriptionView>> SetStatusAsync(string id, string status, Actor actor, int? expectedVersion, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<WebhookSubscriptionView>.NotFound("Webhook subscription");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<WebhookSubscriptionView>.Conflict(Conflict(s));
            if (s.Status == status) return await ViewResult(db, s, ct);   // already so: nothing to record
            var before = Snapshot(s);
            s.Status = status;
            Stamp(s, actor);
            Audit(db, actor, null, Entity, s.Id, status == "Paused" ? "Pause" : "Resume", before, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return await ViewResult(db, s, ct);
        }, ct);

    /// <summary>A new secret: the old one stops verifying at once (each request names the secret version it was signed with). Shown once.</summary>
    public Task<ServiceResult<CreatedWebhookSubscription>> RotateSecretAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<CreatedWebhookSubscription>.NotFound("Webhook subscription");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<CreatedWebhookSubscription>.Conflict(Conflict(s));
            var before = Snapshot(s);
            string? previous = null;
            try { previous = await secrets.GetAsync(s.Id, ct); } catch (InvalidOperationException) { /* unreadable: that is why it is being rotated */ }
            var secret = await secrets.RotateAsync(s.Id, ct);
            s.SecretVersion++; s.SecretRotatedAt = Now;
            Stamp(s, actor);
            Audit(db, actor, null, Entity, s.Id, "RotateSecret", before, Snapshot(s));   // the version, never the secret
            try { await db.SaveChangesAsync(ct); }
            catch { if (previous is not null) await secrets.SetAsync(s.Id, previous, CancellationToken.None); throw; }
            var view = (await ViewsAsync(db, [s], ct))[0];
            return ServiceResult<CreatedWebhookSubscription>.Ok(new(view, secret));
        }, ct);

    public async Task<ServiceResult<WebhookSubscriptionView>> DeleteAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        var r = await RunAsync(async db =>
        {
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<WebhookSubscriptionView>.NotFound("Webhook subscription");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<WebhookSubscriptionView>.Conflict(Conflict(s));
            var view = (await ViewsAsync(db, [s], ct))[0];
            var deliveries = await db.Set<WebhookDeliveries>().CountAsync(d => d.SubscriptionId == id, ct);
            Audit(db, actor, null, Entity, s.Id, "Delete", Snapshot(s), new { deleted = true, deliveriesRemoved = deliveries });
            db.Set<WebhookSubscriptions>().Remove(s);   // the schema cascades to its deliveries and their attempts
            await db.SaveChangesAsync(ct);
            return ServiceResult<WebhookSubscriptionView>.Ok(view);
        }, ct);
        if (r.IsOk) await secrets.ClearAsync(id, ct);
        return r;
    }

    /// <summary>Queues a <c>webhook.ping</c> to the subscription now (same path, same signature, same log as a real event).</summary>
    public async Task<ServiceResult<WebhookDeliveryView>> TestAsync(string id, Actor actor, CancellationToken ct = default)
    {
        var r = await RunAsync(async db =>
        {
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<WebhookDeliveryView>.NotFound("Webhook subscription");
            if (s.Status != "Active") return ServiceResult<WebhookDeliveryView>.Fail(new GuardFailure(WebhookGuards.Paused, "Resume the subscription before sending a test event"));
            var now = Now;
            var eventId = "evt_test_" + Ids.New();
            var d = new WebhookDeliveries
            {
                Id = Ids.New(), SubscriptionId = s.Id, EventId = eventId, EventType = WebhookCatalog.Ping, PayloadJson = WebhookCatalog.BuildPing(eventId, now, s.WorkspaceId, s.Name),
                Status = "Pending", NextAttemptAt = now, CreatedAt = now, WorkspaceId = s.WorkspaceId,
            };
            db.Set<WebhookDeliveries>().Add(d);
            Audit(db, actor, null, DeliveryEntity, d.Id, "Test", null, new { subscription = s.Name, s.Host, eventId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<WebhookDeliveryView>.Ok(DeliveryView(d, s.Name));
        }, ct);
        if (r.IsOk) WebhookSignal.Notify();
        return r;
    }

    // ---- the delivery log ----

    private static WebhookDeliveryView DeliveryView(WebhookDeliveries d, string subscriptionName) =>
        new(d.Id, d.SubscriptionId, subscriptionName, d.EventId, d.EventType, d.Status, d.AttemptCount, Iso(d.NextAttemptAt), Iso(d.LastAttemptAt), d.LastStatusCode, d.LastError, Iso(d.DeliveredAt),
            Iso(d.FinishedAt), d.ReplayOfDeliveryId, Iso(d.CreatedAt), d.Version);

    public async Task<List<WebhookDeliveryView>> ListDeliveriesAsync(string? subscriptionId, string? status, int take = 100, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<WebhookDeliveries>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(subscriptionId)) q = q.Where(d => d.SubscriptionId == subscriptionId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(d => d.Status == status);
        var rows = await q.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var ids = rows.Select(r => r.SubscriptionId).Distinct().ToList();
        var names = await db.Set<WebhookSubscriptions>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return [.. rows.Select(d => DeliveryView(d, names.GetValueOrDefault(d.SubscriptionId) ?? "(deleted)"))];
    }

    public async Task<WebhookDeliveryDetail?> GetDeliveryAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var d = await db.Set<WebhookDeliveries>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return null;
        var name = await db.Set<WebhookSubscriptions>().AsNoTracking().Where(s => s.Id == d.SubscriptionId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "(deleted)";
        var attempts = await db.Set<WebhookDeliveryAttempts>().AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.AttemptNo).ToListAsync(ct);
        return new(DeliveryView(d, name), d.PayloadJson, [.. attempts.Select(a => new WebhookAttemptView(a.AttemptNo, Iso(a.AttemptedAt), a.StatusCode, a.DurationMs, a.Error))]);
    }

    /// <summary>Sends an event again: a new delivery (new delivery id, same event id, same body) that points at the one it repeats. Only a finished delivery can be replayed.</summary>
    public async Task<ServiceResult<WebhookDeliveryView>> ReplayAsync(string deliveryId, Actor actor, CancellationToken ct = default)
    {
        var r = await RunAsync(async db =>
        {
            var o = await db.Set<WebhookDeliveries>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
            if (o is null) return ServiceResult<WebhookDeliveryView>.NotFound("Webhook delivery");
            if (o.Status == "Pending") return ServiceResult<WebhookDeliveryView>.Fail(new GuardFailure(WebhookGuards.NotReplayable, "This delivery is still being tried; it can be replayed once it is delivered or dead-lettered"));
            var s = await db.Set<WebhookSubscriptions>().SingleOrDefaultAsync(x => x.Id == o.SubscriptionId, ct);
            if (s is null) return ServiceResult<WebhookDeliveryView>.NotFound("Webhook subscription");
            if (s.Status != "Active") return ServiceResult<WebhookDeliveryView>.Fail(new GuardFailure(WebhookGuards.Paused, "Resume the subscription before replaying a delivery"));
            var now = Now;
            var copy = new WebhookDeliveries
            {
                Id = Ids.New(), SubscriptionId = o.SubscriptionId, EventId = o.EventId, EventType = o.EventType, AuditEventId = o.AuditEventId, PayloadJson = o.PayloadJson,
                Status = "Pending", NextAttemptAt = now, CreatedAt = now, ReplayOfDeliveryId = o.Id, WorkspaceId = o.WorkspaceId,
            };
            db.Set<WebhookDeliveries>().Add(copy);
            Audit(db, actor, null, DeliveryEntity, copy.Id, "Replay", null, new { replayOf = o.Id, subscription = s.Name, o.EventId, o.EventType, originalStatus = o.Status });
            await db.SaveChangesAsync(ct);
            return ServiceResult<WebhookDeliveryView>.Ok(DeliveryView(copy, s.Name));
        }, ct);
        if (r.IsOk) WebhookSignal.Notify();
        return r;
    }

    /// <summary>Deletes finished deliveries (and their attempts) older than the retention, in the current workspace. One audit row says how many. Returns the count.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var removed = 0;
        var r = await RunAsync(async db =>
        {
            var cutoff = Now.AddDays(-options.RetentionDays);
            removed = await db.Set<WebhookDeliveries>().Where(d => d.Status != "Pending" && d.FinishedAt < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0)
            {
                Audit(db, null, null, DeliveryEntity, "retention", "Pruned", null, new { removed, olderThanDays = options.RetentionDays });
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<int>.Ok(removed);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not prune the webhook log: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
        return removed;
    }
}
