using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Email;

public sealed record EmailStatusView(bool Enabled, string Provider, string FromAddress, string? FromName, string? SmtpHost, int? SmtpPort, string? SmtpSecurity, string? SmtpUserName, bool NeedsCredential, bool HasCredential,
    bool HasEventsSecret, int MaxAttempts, int RetentionDays, int DigestHourUtc, bool PublicBaseUrlSet, EmailCounts Counts);
public sealed record EmailCounts(int Pending, int SentLast7Days, int BouncedLast7Days, int DeadLettersLast7Days, int Suppressed);
public sealed record EmailMessageView(string Id, string To, string Category, string CategoryLabel, string Subject, string Status, int AttemptCount, string? NextAttemptAt, string? LastAttemptAt, string? LastError, string? Provider,
    string? SentAt, string? BouncedAt, string CreatedAt, int Version);
public sealed record EmailMessageDetail(EmailMessageView Message, string BodyText);

/// <summary>
/// REOS-167 (D61): what an administrator (RTE, Release Manager) sees and does under Email delivery: the state of the feature, the send log, a test message to their own address, the provider credential and the secret that
/// signs inbound bounce events. Credentials are written, never read: the status says only whether one is stored, and the events secret is shown once, when it is made. Every change is one transaction with one audit row (rule 3);
/// the credential changes are host-wide (the provider is configured once per host), so their audit rows are host-wide too, and never carry a value.
/// </summary>
public sealed class EmailAdminService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, EmailOptions options, EmailQueue queue, IEmailCredentials credentials, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string? Iso(DateTime? t) => t is { } v ? Iso(v) : null;

    public async Task<EmailStatusView> StatusAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var since = Now.AddDays(-7);
        var by = await db.Set<EmailOutbox>().AsNoTracking().Where(m => m.Status == "Pending" || m.CreatedAt >= since).GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int N(string s) => by.Where(x => x.Key == s).Sum(x => x.N);
        var suppressed = await db.Set<EmailSuppressions>().AsNoTracking().CountAsync(ct);
        var smtp = options.Provider == "Smtp";
        var needs = options.Provider == "SendGrid" || options.SmtpUserName is not null;
        return new(options.Enabled, options.Provider, options.FromAddress, options.FromName, smtp ? options.SmtpHost : null, smtp ? options.SmtpPort : null, smtp ? options.SmtpSecurity : null, smtp ? options.SmtpUserName : null,
            needs, await credentials.HasAsync(IEmailCredentials.Provider, ct), await credentials.HasAsync(IEmailCredentials.Events, ct), options.MaxAttempts, options.RetentionDays, options.DigestHourUtc, queue.SettingsLink is not null,
            new(N("Pending"), N("Sent"), N("Bounced"), N("DeadLetter"), suppressed));
    }

    public async Task<List<EmailMessageView>> ListAsync(string? status, int take, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<EmailOutbox>().AsNoTracking().AsQueryable();
        if (status is { Length: > 0 }) q = q.Where(m => m.Status == status);
        var rows = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return [.. rows.Select(View)];
    }

    public async Task<EmailMessageDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<EmailOutbox>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        return row is null ? null : new(View(row), row.BodyText);
    }

    private static EmailMessageView View(EmailOutbox m) => new(m.Id, m.ToAddress, m.Category, EmailCategories.Label(m.Category), m.Subject, m.Status, m.AttemptCount, Iso(m.NextAttemptAt), Iso(m.LastAttemptAt), m.LastError, m.Provider,
        Iso(m.SentAt), Iso(m.BouncedAt), Iso(m.CreatedAt), m.Version);

    /// <summary>Queues a test message to the signed-in person's own address (never to an address typed in: this is not a way to mail anyone). Refused while email is off.</summary>
    public async Task<ServiceResult<EmailMessageView>> SendTestAsync(Actor actor, CancellationToken ct = default)
    {
        if (!options.Enabled) return ServiceResult<EmailMessageView>.Fail(new GuardFailure(EmailGuards.EmailOff, "Email delivery is off. Set Notifications:Smtp:Enabled to true and restart to turn it on."));
        var r = await RunAsync(async db =>
        {
            var user = await db.Set<Users>().AsNoTracking().Where(u => u.Id == actor.UserId && u.IsActive).Select(u => new { u.Id, u.Email }).SingleOrDefaultAsync(ct);
            if (user is null) return ServiceResult<EmailMessageView>.NotFound("User");
            if (!EmailGuards.IsAddress(user.Email)) return ServiceResult<EmailMessageView>.Fail(new GuardFailure(EmailGuards.NoAddress, "Your account's email address is not a valid address, so a test cannot be sent to it. Correct it under Teams & settings."));
            var now = Now;
            var workspace = await Exports.ReleaseFacts.WorkspaceNameAsync(db, ct);
            var row = await queue.BuildAsync(db, user.Id, user.Email, new EmailContent(EmailCategories.Test, workspace, null, null, now, queue.SettingsLink), null, null, null, now, ct);
            Audit(db, actor, null, "EmailMessage", row.Id, row.Status == "Suppressed" ? "Suppressed" : "TestQueued", null, new { row.Category, row.UserId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<EmailMessageView>.Ok(View(row));
        }, ct);
        if (r.IsOk) EmailSignal.Notify();
        return r;
    }

    /// <summary>Stores the provider's password or API key (Data Protection). Empty is refused; the value is never audited, logged or returned.</summary>
    public async Task<ServiceResult<bool>> SetCredentialAsync(string? value, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return ServiceResult<bool>.Fail(new GuardFailure(EmailGuards.CredentialInvalid, "The credential is empty"));
        if (!(options.Provider == "SendGrid" || options.SmtpUserName is not null))
            return ServiceResult<bool>.Fail(new GuardFailure(EmailGuards.NotNeeded, "This configuration needs no credential (the SMTP relay has no user name set in Notifications:Email:Smtp:UserName)"));
        await credentials.SetAsync(IEmailCredentials.Provider, value, ct);
        return await RecordAsync("Credential", "Set", actor, ct);
    }

    public async Task<ServiceResult<bool>> ClearCredentialAsync(Actor actor, CancellationToken ct = default)
    {
        if (!await credentials.ClearAsync(IEmailCredentials.Provider, ct)) return ServiceResult<bool>.Ok(false);
        return await RecordAsync("Credential", "Removed", actor, ct).ContinueWith(t => t.Result.IsOk ? ServiceResult<bool>.Ok(true) : t.Result, ct);
    }

    /// <summary>Makes (or replaces) the secret an email provider signs bounce and complaint events with. Returned once; only a person who copies it now ever has it.</summary>
    public async Task<ServiceResult<string>> RotateEventsSecretAsync(Actor actor, CancellationToken ct = default)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await credentials.SetAsync(IEmailCredentials.Events, secret, ct);
        var r = await RecordAsync("EventsSecret", "Rotated", actor, ct);
        return r.IsOk ? ServiceResult<string>.Ok(secret) : r.Failed<string>();
    }

    private Task<ServiceResult<bool>> RecordAsync(string what, string action, Actor actor, CancellationToken ct) =>
        RunAsync(async db =>
        {
            Audit(db, actor, null, "Email" + what, "host", action, null, null).WorkspaceId = null;   // host-wide, like the AI provider key; never a value
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
}

/// <summary>What an email provider tells us about a message after it accepted it: <c>bounce</c> (a hard bounce) or <c>complaint</c> (the person marked it as spam).</summary>
public sealed record EmailProviderEvent(string? Type, string? Address, string? MessageId, string? Reason);

/// <summary>
/// REOS-167: bounce and complaint handling for what happens after the provider accepted a message. The event finds the message by the provider's id (or, without one, the address's recent Sent messages), marks it
/// Bounced, puts the address on the suppression list of every workspace that mailed it and raises the "undeliverable address" alert (rule 8). An event that matches nothing is logged and answered, not an error:
/// providers repeat and reorder. Host-wide work (the provider knows no workspace), so it runs outside any workspace scope and enters each message's own.
/// </summary>
public sealed class EmailEventService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, SyncAlertWriter alerts, ILogger<EmailEventService> log, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Types = ["bounce", "complaint"];

    public async Task<ServiceResult<int>> RecordAsync(EmailProviderEvent ev, CancellationToken ct = default)
    {
        var type = ev.Type?.Trim().ToLowerInvariant();
        if (type is null || !Types.Contains(type)) return ServiceResult<int>.Fail(new GuardFailure(EmailGuards.BadEvent, "The event type is bounce or complaint", [.. Types]));
        var address = ev.Address?.Trim();
        if (!EmailGuards.IsAddress(address) && string.IsNullOrWhiteSpace(ev.MessageId)) return ServiceResult<int>.Fail(new GuardFailure(EmailGuards.BadEvent, "The event needs the recipient address or the provider's message id"));
        var reason = ev.Reason is { Length: > 0 } r ? (r.Length > 400 ? r[..400] : r) : (type == "bounce" ? "Hard bounce" : "Spam complaint");

        List<(string Id, string WorkspaceId, string To, string Status)> hits;
        using (WorkspaceScope.HostWide())
        {
            await using var db = await OpenAsync(ct);
            var q = db.Set<EmailOutbox>().AsNoTracking().Where(m => m.Status == "Sent" || m.Status == "Bounced");
            q = !string.IsNullOrWhiteSpace(ev.MessageId) ? q.Where(m => m.ProviderMessageId == ev.MessageId) : q.Where(m => m.ToAddress == address && m.CreatedAt >= Now.AddDays(-90));
            hits = (await q.Select(m => new { m.Id, m.WorkspaceId, m.ToAddress, m.Status }).ToListAsync(ct)).Select(m => (m.Id, m.WorkspaceId, m.ToAddress, m.Status)).ToList();
        }
        if (hits.Count == 0) { log.LogInformation("An email {Type} event matched no message sent from here", type); return ServiceResult<int>.Ok(0); }

        foreach (var g in hits.GroupBy(h => h.WorkspaceId))
        {
            using var scope = WorkspaceScope.Enter(g.Key);
            var to = g.First().To;
            var res = await RunAsync<bool>(async db =>
            {
                var now = Now;
                foreach (var h in g.Where(h => h.Status == "Sent"))
                {
                    var row = await db.Set<EmailOutbox>().SingleAsync(m => m.Id == h.Id && m.Status == "Sent", ct);
                    row.Status = "Bounced"; row.BouncedAt = now; row.LastError = reason; row.Version++;
                    Audit(db, null, null, "EmailMessage", row.Id, type == "bounce" ? "Bounced" : "Complained", null, new { row.Category, row.UserId, reason = type });
                }
                if (!await db.Set<EmailSuppressions>().AnyAsync(s => s.Address == to, ct))
                {
                    var s = EmailSuppressionService.Add(db, to, type == "bounce" ? "Bounce" : "Complaint", reason, g.First().Id, null, now);
                    Audit(db, null, null, "EmailSuppression", s.Id, "Add", null, new { reason = s.Reason });
                }
                await db.SaveChangesAsync(ct);
                return ServiceResult<bool>.Ok(true);
            }, ct);
            if (!res.IsOk) throw new InvalidOperationException("Could not record the email event: " + (res.Failures.Count > 0 ? res.Failures[0].Message : res.Missing));
            await alerts.RaiseAsync("Notifications", "DeliveryFailed", EmailSuppressionService.AlertKey(to),
                $"The address {to} is undeliverable ({(type == "bounce" ? "hard bounce" : "spam complaint")}: {reason}). It is on the suppression list and gets no more mail. Correct the person's address, then remove it from Email delivery > Suppressed addresses.", ct: ct);
        }
        return ServiceResult<int>.Ok(hits.Count);
    }
}
