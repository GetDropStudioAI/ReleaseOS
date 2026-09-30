using System.Text.RegularExpressions;

namespace ReleaseMgmt.Domain.Sync;

/// <summary>
/// The shapes of external keys a link may hold (Q-039a). Keys end up in request URLs and queries, so the allowed characters are deliberately narrow:
/// Jira issue "PAY-123"; Jira fix version "PAY/4.5.0" (project code + version name, OI-4); ServiceNow change "CHG0030001" or change task "CTASK0010001".
/// </summary>
public static partial class ExternalKeys
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{1,19}-[0-9]{1,9}$")] private static partial Regex JiraIssue();
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{1,19}/[A-Za-z0-9][A-Za-z0-9._ -]{0,59}$")] private static partial Regex JiraVersion();
    [GeneratedRegex(@"^(CHG|CTASK)[0-9]{4,12}$")] private static partial Regex ServiceNow();

    public static readonly string[] Sources = ["Jira", "ServiceNow"];

    public static bool IsJiraIssue(string key) => JiraIssue().IsMatch(key);
    public static bool IsJiraVersion(string key) => JiraVersion().IsMatch(key);

    /// <summary>Null when valid, else a readable reason. The key is trimmed and upper-cased by <see cref="Normalize"/> before it is checked.</summary>
    public static string? Validate(string source, string key) => source switch
    {
        "Jira" => JiraIssue().IsMatch(key) || JiraVersion().IsMatch(key) ? null : "A Jira key is an issue key such as PAY-123 or a fix version such as PAY/4.5.0 (project code, slash, version name)",
        "ServiceNow" => ServiceNow().IsMatch(key) ? null : "A ServiceNow key is a change number such as CHG0030001 or a change task such as CTASK0010001",
        _ => "The source system must be Jira or ServiceNow",
    };

    /// <summary>Jira project codes and ServiceNow numbers are upper case; a fix version's name keeps its case.</summary>
    public static string Normalize(string source, string key)
    {
        key = key.Trim();
        if (source == "ServiceNow") return key.ToUpperInvariant();
        var slash = key.IndexOf('/');
        return slash < 0 ? key.ToUpperInvariant() : key[..slash].ToUpperInvariant() + key[slash..];
    }

    /// <summary>The longest status name kept from a source system. Jira and ServiceNow status names are a few words; anything longer is not a status (SEC-C4).</summary>
    public const int MaxStateLength = 100;

    /// <summary>
    /// Whether a status name another system sent is plausible enough to store, audit, show and quote in alerts (OI-12 keeps keys, states and dates only):
    /// not blank, at most <see cref="MaxStateLength"/> characters, and no control, format (bidi override, zero width), line or paragraph separator,
    /// private-use or unpaired surrogate character. A value that fails is refused, never trimmed or altered, and never repeated in a message.
    /// </summary>
    public static bool IsPlausibleState(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > MaxStateLength) return false;
        for (var i = 0; i < s.Length; i++)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(s, i);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate)
                return false;
            if (char.IsHighSurrogate(s[i])) i++;   // a valid pair was classified as one code point above
        }
        return true;
    }

    /// <summary>ServiceNow change_request.state codes to their names; names pass through (a display-value response).</summary>
    public static string ChangeStateName(string raw) => raw.Trim() switch
    {
        "-5" => "New", "-4" => "Assess", "-3" => "Authorize", "-2" => "Scheduled", "-1" => "Implement", "0" => "Review", "3" => "Closed", "4" => "Canceled",
        var other => other,
    };

    /// <summary>ServiceNow change_task.state codes.</summary>
    public static string ChangeTaskStateName(string raw) => raw.Trim() switch
    {
        "-5" => "Pending", "1" => "Open", "2" => "Work in progress", "3" => "Closed Complete", "4" => "Closed Incomplete", "7" => "Closed Skipped",
        var other => other,
    };
}
