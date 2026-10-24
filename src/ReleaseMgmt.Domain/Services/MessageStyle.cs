using System.Globalization;

namespace ReleaseMgmt.Domain.Services;

/// <summary>Where a message comes from (REOS-139, D45): the workspace, the release if there is one, and a deep link back to the app if one is configured (D52).</summary>
public sealed record MessageSubject(string Workspace, string? Key, string? Title, string? Link);

/// <summary>
/// One message style for every text the app sends (PROJECT_SCOPE 5.2 and 5.5): in-app notifications, team webhooks and the default communications.
/// <c>Payments · RLS-284 Card portal: Gate 'Code Freeze' is overdue (was due 2026-10-30 UTC). Open: https://releaseos.example.com/trains/…</c>
/// The header names the workspace, the key and the title; times are UTC and say so; the link is present only when <c>App:PublicBaseUrl</c> is set (D52), never invented.
/// </summary>
public static class MessageStyle
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary><c>Payments · RLS-284 Card portal</c>; just the workspace for a message that is not about a release; the key or the title alone when the other is missing.</summary>
    public static string Header(MessageSubject s)
    {
        var release = string.Join(' ', new[] { s.Key, s.Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(s.Workspace) ? release : release.Length == 0 ? s.Workspace : $"{s.Workspace} · {release}";
    }

    /// <summary>The header, a colon, the body, and (when there is a link) <c> Open: &lt;link&gt;</c>.</summary>
    public static string Compose(MessageSubject s, string body)
    {
        var header = Header(s);
        var text = header.Length == 0 ? body : $"{header}: {body}";
        return s.Link is { Length: > 0 } link ? $"{text} Open: {link}" : text;
    }

    /// <summary><c>2026-10-30 14:00 UTC</c></summary>
    public static string Utc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm", Inv) + " UTC";

    /// <summary>A calendar date (a due day, a target): <c>2026-10-30 UTC</c>, as the screens label them (D44).</summary>
    public static string Utc(DateOnly day) => day.ToString("yyyy-MM-dd", Inv) + " UTC";

    /// <summary>An ISO-8601 instant as <see cref="Utc(DateTime)"/>; text that is not an instant is returned as it came, never guessed at.</summary>
    public static string Utc(string? iso) =>
        iso is not null && DateTime.TryParse(iso, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? Utc(t) : iso ?? "";
}
