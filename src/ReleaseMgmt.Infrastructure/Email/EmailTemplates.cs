using System.Globalization;
using System.Net;
using System.Text;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Email;

/// <summary>One line of a digest: what kind, which release (the key only), when, and where to open it.</summary>
public sealed record DigestItem(string Category, string? ReleaseKey, DateTime At, string? Link);

/// <summary>
/// Everything a template may use. This is the whole of D12: <b>structure only</b>. The workspace name, the release <i>key</i>, the kind of notification, a time and a link. There is deliberately no field for a message,
/// a note, a title, a ticket summary or a customer name, so none can reach a mail body however a caller is written.
/// </summary>
public sealed record EmailContent(string Category, string Workspace, string? ReleaseKey, string? Link, DateTime At, string? SettingsLink, IReadOnlyList<DigestItem>? Items = null, int? TotalItems = null)
{
    /// <summary>How many notifications the digest stands for: more than the lines it lists when it was capped.</summary>
    public int Total => TotalItems ?? Items?.Count ?? 0;
}

public sealed record RenderedEmail(string Subject, string Text, string Html);

/// <summary>
/// REOS-167: the five templates (approval requested, decision made, escalation level 1 and 2, daily digest) and the test mail. Times are UTC and labelled, with the display zone's local time beside them (D44; the
/// person's own browser zone is not known to the server, so the host's <c>Display:TimeZone</c> is used and named). Links come from <c>App:PublicBaseUrl</c> and are absent, not guessed, when it is unset (D52).
/// Every value placed in HTML is encoded; a subject can never contain a line break.
/// </summary>
public static class EmailTemplates
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static RenderedEmail Render(EmailContent c, TimeZoneInfo zone)
    {
        var when = When(c.At, zone);
        var where = c.ReleaseKey is { Length: > 0 } k ? $"{c.Workspace} · {k}" : c.Workspace;
        var (headline, detail, subjectLead) = c.Category switch
        {
            EmailCategories.ApprovalRequested => ("An approval is waiting for your decision.", "Open it to see what is being approved and the decision it needs from you.", "Approval requested"),
            EmailCategories.DecisionMade => ("A decision was made on an approval you requested.", "Open it to see who decided and what they said.", "Decision made"),
            EmailCategories.EscalationLevel1 => ("An item you own is late and has been escalated (level 1).", "Open it to see what is late and what it is waiting for.", "Escalation level 1"),
            EmailCategories.EscalationLevel2 => ("An item you own is still late and has been escalated again (level 2). It needs attention now.", "Open it to see what is late and what it is waiting for.", "Escalation level 2: needs attention"),
            EmailCategories.DailyDigest => ($"Your daily digest: {c.Total} notification{(c.Total == 1 ? "" : "s")} since the last one.", "Open the inbox for the details; this summary lists only what kind and where.", "Your daily digest"),
            _ => ("This is a test email from ReleaseOS.", "If you can read this, email delivery works.", "Test email"),
        };
        var subject = Clean(c.Category == EmailCategories.DailyDigest || c.Category == EmailCategories.Test ? $"[{c.Workspace}] {subjectLead}" : $"[{c.Workspace}] {subjectLead}{(c.ReleaseKey is { Length: > 0 } rk ? $": {rk}" : "")}");

        var text = new StringBuilder();
        text.Append(headline).Append("\n\n").Append("Where: ").Append(where).Append('\n');
        if (c.Items is { Count: > 0 } items) foreach (var i in items) text.Append("  - ").Append(EmailCategories.Label(i.Category)).Append(i.ReleaseKey is { Length: > 0 } ik ? " · " + ik : "").Append(", ").Append(MessageStyle.Utc(i.At)).Append(i.Link is { } l ? " " + l : "").Append('\n');
        if (c.Total > (c.Items?.Count ?? 0)) text.Append("  - and ").Append(c.Total - (c.Items?.Count ?? 0)).Append(" more\n");
        text.Append('\n').Append(detail).Append('\n');
        if (c.Link is { } link) text.Append("\nOpen: ").Append(link).Append('\n');
        text.Append("\nSent: ").Append(when).Append('\n');
        text.Append("\nYou get this because of your notification settings").Append(c.SettingsLink is { } s ? ": " + s : " (Notification settings in ReleaseOS)").Append(".\nThis message holds no ticket text or customer data on purpose.\n");

        var h = new StringBuilder();
        h.Append("<!doctype html><html><body style=\"font-family:system-ui,-apple-system,'Segoe UI',sans-serif;line-height:1.5;color:#1f2933\">");
        h.Append("<p><strong>").Append(E(headline)).Append("</strong></p>");
        h.Append("<p>Where: ").Append(E(where)).Append("</p>");
        if (c.Items is { Count: > 0 } its)
        {
            h.Append("<ul>");
            foreach (var i in its)
            {
                h.Append("<li>").Append(E(EmailCategories.Label(i.Category))).Append(i.ReleaseKey is { Length: > 0 } ik2 ? " · " + E(ik2) : "").Append(", ").Append(E(MessageStyle.Utc(i.At)));
                if (Safe(i.Link) is { } il) h.Append(" · <a href=\"").Append(E(il)).Append("\">Open</a>");
                h.Append("</li>");
            }
            if (c.Total > its.Count) h.Append("<li>and ").Append(c.Total - its.Count).Append(" more</li>");
            h.Append("</ul>");
        }
        h.Append("<p>").Append(E(detail)).Append("</p>");
        if (Safe(c.Link) is { } href) h.Append("<p><a href=\"").Append(E(href)).Append("\">Open in ReleaseOS</a></p>");
        h.Append("<p style=\"color:#52606d\">Sent: ").Append(E(when)).Append("</p>");
        h.Append("<p style=\"color:#52606d\">You get this because of your notification settings");
        h.Append(Safe(c.SettingsLink) is { } sl ? $": <a href=\"{E(sl)}\">Notification settings</a>" : " (Notification settings in ReleaseOS)");
        h.Append(". This message holds no ticket text or customer data on purpose.</p></body></html>");
        return new(subject, text.ToString(), h.ToString());
    }

    /// <summary><c>2026-11-27 18:00 UTC (2026-11-27 12:00 America/Chicago)</c>: UTC first, the local time beside it (D44).</summary>
    public static string When(DateTime utc, TimeZoneInfo zone)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(u, zone);
        return $"{u.ToString("yyyy-MM-dd HH:mm", Inv)} UTC ({local.ToString("yyyy-MM-dd HH:mm", Inv)} {zone.Id})";
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim() is { Length: > 200 } t ? t[..200] : s.Replace('\r', ' ').Replace('\n', ' ').Trim();
    /// <summary>Only an http(s) address becomes a link: a template never emits <c>javascript:</c> whatever it was given.</summary>
    private static string? Safe(string? link) => link is not null && Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? link : null;
}
