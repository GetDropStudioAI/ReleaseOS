using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Email;

/// <summary>
/// REOS-167 (D61): sends the mail that is due, at-least-once. A message is Pending until the provider accepts it; each try updates the row (attempt count, last error, next attempt with exponential backoff
/// <see cref="EmailOptions.Delay"/>); the last failed try makes it a DeadLetter. A provider that says the recipient can never be reached (<see cref="EmailOutcome.Rejected"/>) makes it Bounced and puts the address on the suppression
/// list at once; the address is checked against the list again just before sending, so a bounce that arrived while a message waited stops it. Every way a message ends is one audit row (rule 3). An undeliverable address or a
/// dead letter raises ONE <c>Notifications/DeliveryFailed</c> alert per address (never one per attempt) that resolves itself when a later send to it succeeds (rule 8). Times come from the injected clock (rule 4).
/// A record is written after the call, so a host that dies in between sends the message once more (at-least-once; a person may see a repeat, never a loss).
/// </summary>
public sealed class EmailDeliveryProcessor(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, EmailOptions options, IEmailProvider provider, SyncAlertWriter alerts, ILogger<EmailDeliveryProcessor> log,
    IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private sealed record Due(string Id, string WorkspaceId, string To, string Subject, string Text, string Html, string Category);

    /// <summary>Sends up to <c>Batch</c> messages whose time has come, oldest first. Returns how many were handled. Does nothing when email is off.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        if (!options.Enabled) return 0;
        List<Due> due;
        using (WorkspaceScope.HostWide())
        {
            await using var db = await OpenAsync(ct);
            var now = Now;
            due = await db.Set<EmailOutbox>().AsNoTracking().Where(m => m.Status == "Pending" && m.NextAttemptAt <= now).OrderBy(m => m.NextAttemptAt).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(options.Batch)
                .Select(m => new Due(m.Id, m.WorkspaceId, m.ToAddress, m.Subject, m.BodyText, m.BodyHtml, m.Category)).ToListAsync(ct);
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
        bool suppressed;
        await using (var db = await OpenAsync(ct)) suppressed = await db.Set<EmailSuppressions>().AsNoTracking().AnyAsync(s => s.Address == d.To, ct);
        EmailSendResult? result = null;
        if (!suppressed) result = await provider.SendAsync(new EmailEnvelope(d.To, options.FromAddress, options.FromName, d.Subject, d.Text, d.Html, d.Id), ct);

        string? state = null;
        var outcome = await RunAsync<string?>(async db =>
        {
            var row = await db.Set<EmailOutbox>().SingleOrDefaultAsync(x => x.Id == d.Id && x.Status == "Pending", ct);
            if (row is null) return ServiceResult<string?>.Ok(null);   // finished or removed meanwhile
            var now = Now;
            if (result is null)
            {
                row.Status = "Suppressed"; row.NextAttemptAt = null; row.FinishedAt = now; row.LastError = "The address is on the suppression list"; row.Version++;
                Audit(db, null, null, "EmailMessage", row.Id, "Suppressed", null, new { row.Category, row.UserId });
                state = "Suppressed";
            }
            else
            {
                var reason = result.Reason is { Length: > 500 } r ? r[..500] : result.Reason;
                row.AttemptCount++; row.LastAttemptAt = now; row.Version++;
                switch (result.Outcome)
                {
                    case EmailOutcome.Sent:
                        row.Status = "Sent"; row.NextAttemptAt = null; row.SentAt = now; row.FinishedAt = now; row.LastError = null; row.Provider = provider.Name; row.ProviderMessageId = result.ProviderMessageId;
                        Audit(db, null, null, "EmailMessage", row.Id, "Sent", null, new { row.Category, row.UserId, attempts = row.AttemptCount, provider = provider.Name });
                        state = "Sent";
                        break;
                    case EmailOutcome.Rejected:
                        row.Status = "Bounced"; row.NextAttemptAt = null; row.BouncedAt = now; row.FinishedAt = now; row.LastError = reason ?? "The recipient was refused"; row.Provider = provider.Name;
                        Audit(db, null, null, "EmailMessage", row.Id, "Bounced", null, new { row.Category, row.UserId, attempts = row.AttemptCount, error = reason });
                        if (!await db.Set<EmailSuppressions>().AnyAsync(s => s.Address == row.ToAddress, ct))
                        {
                            var s = EmailSuppressionService.Add(db, row.ToAddress, "Bounce", reason, row.Id, null, now);
                            Audit(db, null, null, "EmailSuppression", s.Id, "Add", null, new { reason = "Bounce" });
                        }
                        state = "Bounced";
                        break;
                    case EmailOutcome.Failed:
                        row.Status = "DeadLetter"; row.NextAttemptAt = null; row.FinishedAt = now; row.LastError = reason ?? "The provider refused the message";
                        Audit(db, null, null, "EmailMessage", row.Id, "DeadLetter", null, new { row.Category, row.UserId, attempts = row.AttemptCount, error = reason, permanent = true });
                        state = "DeadLetter";
                        break;
                    default:
                        row.LastError = reason ?? "The provider did not accept the message";
                        if (row.AttemptCount >= options.MaxAttempts)
                        {
                            row.Status = "DeadLetter"; row.NextAttemptAt = null; row.FinishedAt = now;
                            Audit(db, null, null, "EmailMessage", row.Id, "DeadLetter", null, new { row.Category, row.UserId, attempts = row.AttemptCount, error = reason });
                            state = "DeadLetter";
                        }
                        else { row.NextAttemptAt = now + options.Delay(row.AttemptCount); state = "Retry"; }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<string?>.Ok(state);
        }, ct);
        if (!outcome.IsOk) throw new InvalidOperationException($"Email {d.Id} was handled but the result could not be recorded: " + (outcome.Failures.Count > 0 ? outcome.Failures[0].Message : outcome.Missing));

        try
        {
            switch (state)
            {
                case "Sent": await alerts.ResolveAsync("Notifications", "DeliveryFailed", EmailSuppressionService.AlertKey(d.To), ct); break;
                case "Bounced":
                    await alerts.RaiseAsync("Notifications", "DeliveryFailed", EmailSuppressionService.AlertKey(d.To),
                        $"The address {d.To} is undeliverable ({result?.Reason ?? "refused"}). It is on the suppression list and gets no more mail. Correct the person's address, then remove it from Email delivery > Suppressed addresses.", ct: ct);
                    break;
                case "DeadLetter":
                    await alerts.RaiseAsync("Notifications", "DeliveryFailed", EmailSuppressionService.AlertKey(d.To),
                        $"A {EmailCategories.Label(d.Category).ToLowerInvariant()} email to {d.To} could not be delivered and was given up on ({result?.Reason ?? "unknown"}). Check Email delivery for the provider's state.", ct: ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message is recorded and visible in the log; only its alert failed. Nothing further can carry this, so it is logged loudly (never the address).
            log.LogError(ex, "Email {Message} was recorded as {State} but its alert could not be written or resolved", d.Id, state);
        }
    }

    /// <summary>Removes finished messages older than <c>RetentionDays</c> from the workspace of the current scope; one audit row says how many.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var r = await RunAsync<int>(async db =>
        {
            var cutoff = Now.AddDays(-options.RetentionDays);
            var n = await db.Set<EmailOutbox>().Where(m => m.Status != "Pending" && m.FinishedAt < cutoff).ExecuteDeleteAsync(ct);
            if (n > 0) { Audit(db, null, null, "EmailMessage", "retention", "Pruned", null, new { removed = n, olderThanDays = options.RetentionDays }); await db.SaveChangesAsync(ct); }
            return ServiceResult<int>.Ok(n);
        }, ct);
        return r.IsOk ? r.Value : throw new InvalidOperationException("Could not prune the email log: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
    }
}

/// <summary>
/// REOS-167: the daily digest. Once a day after <c>DigestHourUtc</c>, for every person with at least one category set to "in the daily digest", the in-app notifications created since their previous digest
/// (or the last 24 hours) whose category they chose for the digest become ONE message: counts by kind and one line each (kind, release key, UTC time, link). Nothing is sent when there is nothing to say. The message
/// carries a dedup key (person and UTC day) under a unique index, so a restart or a second pass cannot send two. As everywhere, no ticket or customer text (D12).
/// </summary>
public sealed class EmailDigestService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, EmailOptions options, EmailQueue queue, PublicLinks links, IEmailProblemSink? problems = null, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const int MaxLines = 25;

    /// <summary>Queues today's digests in the current workspace scope. Returns how many messages were queued.</summary>
    public async Task<int> QueueAsync(CancellationToken ct = default)
    {
        if (!options.Enabled) return 0;
        var now = Now;
        if (now.Hour < options.DigestHourUtc) return 0;
        List<string> users;
        await using (var db = await OpenAsync(ct))
            users = await db.Set<NotificationPreferences>().AsNoTracking().Where(p => p.Channel == "Email" && p.Mode == "Daily").Select(p => p.UserId).Distinct().OrderBy(u => u).ToListAsync(ct);
        var queued = 0;
        foreach (var userId in users)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await QueueForAsync(userId, now, ct)) queued++; }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.Ordinal) == true) { /* another pass queued today's digest first: exactly the point of the key */ }
        }
        return queued;
    }

    private async Task<bool> QueueForAsync(string userId, DateTime now, CancellationToken ct)
    {
        string? unmailable = null;
        var r = await RunAsync<bool>(async db =>
        {
            var key = $"digest:{userId}:{now:yyyyMMdd}";
            if (await db.Set<EmailOutbox>().AnyAsync(m => m.DedupKey == key, ct)) return ServiceResult<bool>.Ok(false);
            var user = await EmailQueue.MemberAsync(db, userId, ct);
            if (user is null) return ServiceResult<bool>.Ok(false);
            if (!EmailGuards.IsAddress(user.Email)) { unmailable = user.Email; return ServiceResult<bool>.Ok(false); }
            var since = await db.Set<EmailOutbox>().AsNoTracking().Where(m => m.UserId == userId && m.Category == EmailCategories.DailyDigest).OrderByDescending(m => m.CreatedAt).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync(ct)
                        ?? now.AddHours(-24);
            var chosen = await db.Set<NotificationPreferences>().AsNoTracking().Where(p => p.UserId == userId && p.Channel == "Email" && p.Mode == "Daily").Select(p => p.Category).ToListAsync(ct);
            var candidates = await db.Set<Notifications>().AsNoTracking().Where(n => n.UserId == userId && n.CreatedAt > since && n.CreatedAt <= now).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
            var items = candidates.Select(n => (n, cat: EmailCategories.Of(n.Kind, n.EscalationLevel))).Where(x => x.cat is not null && chosen.Contains(x.cat!)).ToList();
            if (items.Count == 0) return ServiceResult<bool>.Ok(false);

            var workspace = await ReleaseFacts.WorkspaceNameAsync(db, ct);
            var lines = new List<DigestItem>();
            foreach (var (n, cat) in items.Take(MaxLines))
            {
                var subject = await MessageContexts.ForEntityAsync(db, links, n.EntityType, n.EntityId, null, ct);
                lines.Add(new DigestItem(cat!, subject.Key, n.CreatedAt, subject.Link));
            }
            var content = new EmailContent(EmailCategories.DailyDigest, workspace, null, links.To("/inbox"), now, queue.SettingsLink, lines, items.Count);
            var row = await queue.BuildAsync(db, user.Id, user.Email, content, null, null, key, now, ct);
            if (row.Status == "Suppressed") Audit(db, null, null, "EmailMessage", row.Id, "Suppressed", null, new { row.Category, row.UserId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not queue the daily digest: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
        if (unmailable is not null && problems is not null) await problems.UndeliverableAsync(unmailable, "it is not a valid email address", ct);
        if (r.Value) EmailSignal.Notify();
        return r.Value;
    }
}
