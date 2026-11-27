using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Email;

public static class EmailGuards
{
    public const string EmailOff = "EmailOff", PreferenceInvalid = "PreferenceInvalid", AddressInvalid = "AddressInvalid", AlreadySuppressed = "AlreadySuppressed", CredentialInvalid = "CredentialInvalid",
        NotNeeded = "CredentialNotNeeded", NoAddress = "NoAddress", BadEvent = "BadEvent";

    /// <summary>One plain address that the table's CHECK accepts: the same rule, so the service answers before the database has to.</summary>
    public static bool IsAddress(string? s) =>
        s is { Length: >= 3 and <= 320 } && s.Contains('@') && s.IndexOfAny([' ', '<', '>', ',', ';', '"', '\r', '\n', '\t']) < 0 && MailAddress.TryCreate(s, out var a) && a.Address == s;
}

/// <summary>
/// REOS-167: turns something that should be mailed into <c>EmailOutbox</c> rows, in the caller's transaction (it adds rows and never saves, so a message exists exactly when the change that caused it does, and nothing is
/// queued for a rollback). With <c>Notifications:Smtp:Enabled</c> off it returns nothing: no mail and no error. An address on the suppression list is recorded as <c>Suppressed</c> and never sent. The body comes from
/// <see cref="EmailTemplates"/> out of structure only (D12). The caller audits the rows it gets back that are already final.
/// </summary>
public sealed record PersonRef(string Id, string Email);

/// <summary>Raises the "undeliverable address" alert for an address that cannot be mailed at all (it is not an address). Called after a commit; it never throws (it logs what it could not raise).</summary>
public interface IEmailProblemSink
{
    Task UndeliverableAsync(string address, string why, CancellationToken ct);
}

public sealed class EmailQueue(EmailOptions options, PublicLinks links, DisplayClock clock)
{
    public bool Enabled => options.Enabled;
    public TimeZoneInfo Zone => clock.Zone;
    public string? SettingsLink => links.To("/notification-settings");

    /// <summary>The mail an in-app notification calls for: none when email is off, the kind is not mailed, the person has no usable account, or their choice for it is not "immediately".</summary>
    public async Task<List<EmailOutbox>> ForNotificationAsync(ReleaseDbContext db, Notifications n, MessageSubject subject, DateTime now, List<string> unmailable, CancellationToken ct)
    {
        if (!options.Enabled) return [];
        var category = EmailCategories.Of(n.Kind, n.EscalationLevel);
        if (category is null) return [];
        var user = await MemberAsync(db, n.UserId, ct);
        if (user is null) return [];   // not an active member of this workspace: the inbox row stays, there is nobody to mail here
        if (await ModeAsync(db, user.Id, category, "Email", ct) != "Immediate") return [];
        if (!EmailGuards.IsAddress(user.Email)) { unmailable.Add(user.Email); return []; }   // the table would refuse it; the caller raises it after the commit (never silent, never a lost inbox row)
        var content = new EmailContent(category, subject.Workspace, subject.Key, subject.Link, now, SettingsLink);
        return [await BuildAsync(db, user.Id, user.Email, content, n.EntityType, n.EntityId, null, now, ct)];
    }

    /// <summary>The person, if they are an active member of the context's workspace (the table's trigger refuses mail to anyone else).</summary>
    public static async Task<PersonRef?> MemberAsync(ReleaseDbContext db, string userId, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        return await db.Set<Users>().AsNoTracking().Where(u => u.Id == userId && u.IsActive && db.Set<WorkspaceMembers>().IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.WorkspaceId == ws))
            .Select(u => new PersonRef(u.Id, u.Email)).SingleOrDefaultAsync(ct);
    }

    /// <summary>A person's mode for a kind and channel: their row, or the default.</summary>
    public static async Task<string> ModeAsync(ReleaseDbContext db, string userId, string category, string channel, CancellationToken ct) =>
        await db.Set<NotificationPreferences>().AsNoTracking().Where(p => p.UserId == userId && p.Category == category && p.Channel == channel).Select(p => p.Mode).SingleOrDefaultAsync(ct) ?? EmailCategories.DefaultMode(channel);

    public async Task<EmailOutbox> BuildAsync(ReleaseDbContext db, string? userId, string to, EmailContent content, string? entityType, string? entityId, string? dedupKey, DateTime now, CancellationToken ct)
    {
        var mail = EmailTemplates.Render(content, clock.Zone);
        var suppression = await db.Set<EmailSuppressions>().AsNoTracking().Where(s => s.Address == to).Select(s => s.Reason).FirstOrDefaultAsync(ct);   // the column compares without regard to capitals
        var row = new EmailOutbox
        {
            UserId = userId, ToAddress = to, Category = content.Category, Subject = mail.Subject, BodyText = mail.Text, BodyHtml = mail.Html, EntityType = entityType, EntityId = entityId, DedupKey = dedupKey,
            CreatedAt = now, WorkspaceId = Memberships.WorkspaceOf(db),
        };
        if (suppression is null) { row.Status = "Pending"; row.NextAttemptAt = now; }
        else { row.Status = "Suppressed"; row.NextAttemptAt = null; row.FinishedAt = now; row.LastError = $"The address is on the suppression list ({suppression})"; }
        db.Set<EmailOutbox>().Add(row);
        return row;
    }
}
