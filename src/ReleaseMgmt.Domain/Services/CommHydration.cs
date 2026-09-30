using System.Globalization;

namespace ReleaseMgmt.Domain.Services;

public sealed record SnapProduct(string Name, string VersionTag);
public sealed record SnapGate(string Name, string Status, int Sequence, DateOnly DueOn, DateTime? CertifiedAt);
public sealed record SnapBlocker(string Severity, string Title, string? Owner, DateTime RaisedAt);
public sealed record SnapCondition(string Text, string Owner, DateTime ExpiresAt);
public sealed record SnapDecision(string Decision, string DecidedBy, DateTime DecidedAt, IReadOnlyList<SnapCondition> OpenConditions);
public sealed record SnapKnownIssue(string Severity, string Title, string? Workaround, string Status, string? ExternalKey);

/// <summary>
/// Every input a token can read, loaded in one read transaction (so the view is consistent) and frozen. Rendering from it is pure:
/// no database, no clock. <see cref="AsOf"/> is the service clock at load time; <see cref="TrainVersion"/> is the train row's Version in the same snapshot.
/// </summary>
public sealed record HydrationSnapshot(
    string TrainId, int TrainVersion, DateTime AsOf,
    string Title, DateOnly TargetDate, string Status, string? ChangeTicket, string? CloseCode,
    DateTime? WindowStart, DateTime? WindowEnd,
    IReadOnlyList<SnapProduct> Products, IReadOnlyList<SnapGate> Gates,
    int TasksDone, int TasksTotal,
    IReadOnlyList<SnapBlocker> OpenBlockers, SnapDecision? Decision, IReadOnlyList<SnapKnownIssue> KnownIssues,
    IReadOnlyList<string> Owners, IReadOnlySet<DateOnly> Holidays);

/// <summary>Display formatting for messages. Dates are in the display zone (D24), never UTC, in the same "Fri 30 Oct" style as the screens.</summary>
public static class CommFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"Fri 30 Oct"; the year is added when it differs from <paramref name="reference"/>'s year.</summary>
    public static string Day(DateOnly d, DateOnly reference) =>
        d.ToString("ddd d MMM", Inv) + (d.Year == reference.Year ? "" : " " + d.Year.ToString(Inv));

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>"Fri 30 Oct 00:30" in the display zone.</summary>
    public static string DayTime(DateTime utc, TimeZoneInfo zone, DateOnly reference)
    {
        var l = ToLocal(utc, zone);
        return Day(DateOnly.FromDateTime(l), reference) + " " + l.ToString("HH:mm", Inv);
    }

    /// <summary>"CT" for the common US zones, otherwise the UTC offset ("UTC+01:00") at that instant. Never guesses an abbreviation.</summary>
    public static string ZoneLabel(TimeZoneInfo zone, DateTime utc)
    {
        switch (zone.Id)
        {
            case "UTC" or "Etc/UTC": return "UTC";
            case "America/Chicago" or "US/Central": return "CT";
            case "America/New_York" or "US/Eastern": return "ET";
            case "America/Denver" or "US/Mountain": return "MT";
            case "America/Los_Angeles" or "US/Pacific": return "PT";
        }
        var o = zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.ToString(@"hh\:mm", Inv);
    }

    /// <summary>"Fri 30 Oct 01:00–05:00 CT", or "Fri 30 Oct 22:00 – Sat 31 Oct 02:00 CT" when the window crosses midnight.</summary>
    public static string Window(DateTime startUtc, DateTime endUtc, TimeZoneInfo zone, DateOnly reference)
    {
        var s = ToLocal(startUtc, zone); var e = ToLocal(endUtc, zone);
        var label = ZoneLabel(zone, startUtc);
        return s.Date == e.Date
            ? $"{Day(DateOnly.FromDateTime(s), reference)} {s.ToString("HH:mm", Inv)}–{e.ToString("HH:mm", Inv)} {label}"
            : $"{DayTime(startUtc, zone, reference)} – {DayTime(endUtc, zone, reference)} {label}";
    }

    /// <summary>Whole business days from <paramref name="asOf"/>'s local date to the target ("T-n"); negative once past. Unicode minus so Markdown never sees a list marker.</summary>
    public static string SignedDays(int n) => n < 0 ? "−" + (-n).ToString(Inv) : n.ToString(Inv);
}

/// <summary>Builds the 22 token values from a snapshot (pure) and hydrates templates with them.</summary>
public static class CommHydrator
{
    public static readonly IReadOnlyDictionary<string, int> SeverityRank = new Dictionary<string, int> { ["Critical"] = 0, ["High"] = 1, ["Medium"] = 2, ["Low"] = 3 };

    // Glyph and word, never colour alone (CLAUDE.md rule 9 applies to text too). Waived is not Certified in any report.
    private static (string Glyph, string Word) GateMark(string status) => status switch
    {
        "Certified" => ("✓", "Certified"), "Waived" => ("▲", "Waived"), "Failed" => ("✗", "Failed"), "InProgress" => ("◐", "In progress"), _ => ("○", "Pending"),
    };

    private static bool GateDone(string status) => status is "Certified" or "Waived";

    public static IReadOnlyDictionary<string, TokenValue> Values(HydrationSnapshot s, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(CommFormat.ToLocal(s.AsOf, zone));
        string Day(DateOnly d) => CommFormat.Day(d, today);
        string DayTime(DateTime t) => CommFormat.DayTime(t, zone, today);

        var gates = s.Gates.OrderBy(g => g.Sequence).ToList();
        var width = gates.Count == 0 ? 0 : gates.Max(g => g.Name.Length);
        var next = gates.FirstOrDefault(g => !GateDone(g.Status));
        var blockers = s.OpenBlockers.OrderBy(b => SeverityRank.GetValueOrDefault(b.Severity, 9)).ThenBy(b => b.RaisedAt).ToList();
        string Blocker(SnapBlocker b, bool withSeverity) => string.Join(" · ", new[] { withSeverity ? b.Severity : null, b.Title, b.Owner }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var percent = s.TasksTotal == 0 ? 0 : (int)Math.Round(100.0 * s.TasksDone / s.TasksTotal, MidpointRounding.AwayFromZero);

        return new Dictionary<string, TokenValue>
        {
            ["ReleaseTitle"] = TokenValue.Scalar(s.Title),
            ["TargetDate"] = TokenValue.Scalar(Day(s.TargetDate)),
            ["DaysToTarget"] = TokenValue.Scalar(CommFormat.SignedDays(BusinessDays.Between(today, s.TargetDate, s.Holidays))),
            ["Status"] = TokenValue.Scalar(s.Status),
            ["Window"] = TokenValue.Scalar(s.WindowStart is { } a && s.WindowEnd is { } b ? CommFormat.Window(a, b, zone, today) : "Not scheduled"),
            ["ChangeTicket"] = TokenValue.Scalar(string.IsNullOrWhiteSpace(s.ChangeTicket) ? "Not assigned" : s.ChangeTicket),
            ["ProductList"] = TokenValue.Inline(s.Products.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Name} {p.VersionTag}")),
            ["ProductCount"] = TokenValue.Scalar(s.Products.Count.ToString(CultureInfo.InvariantCulture)),
            ["GateTable"] = TokenValue.Table(gates.Select(g =>
            {
                var (glyph, word) = GateMark(g.Status);
                var when = g.Status == "Certified" && g.CertifiedAt is { } c ? Day(DateOnly.FromDateTime(CommFormat.ToLocal(c, zone))) : "due " + Day(g.DueOn);
                return $"{g.Name} {new string('.', width - g.Name.Length + 3)} {glyph} {word} {when}";
            })),
            ["NextGate"] = TokenValue.Scalar(next?.Name ?? "None"),
            ["NextGateDue"] = TokenValue.Scalar(next is null ? "None" : Day(next.DueOn)),
            ["TasksDone"] = TokenValue.Scalar(s.TasksDone.ToString(CultureInfo.InvariantCulture)),
            ["TasksTotal"] = TokenValue.Scalar(s.TasksTotal.ToString(CultureInfo.InvariantCulture)),
            ["PercentComplete"] = TokenValue.Scalar(percent.ToString(CultureInfo.InvariantCulture) + "%"),
            ["BlockerCount"] = TokenValue.Scalar(blockers.Count.ToString(CultureInfo.InvariantCulture)),
            ["CriticalBlockers"] = TokenValue.Bullets(blockers.Where(b => b.Severity == "Critical").Select(b => Blocker(b, false))),
            ["BlockerList"] = TokenValue.Bullets(blockers.Select(b => Blocker(b, true))),
            ["Owners"] = TokenValue.Inline(s.Owners),
            ["GoNoGoDecision"] = TokenValue.Scalar(s.Decision is null ? "Not yet recorded" : s.Decision.Decision switch { "Go" => "Go", "NoGo" => "No-Go", "GoWithConditions" => "Go with conditions", var d => d }),
            ["Conditions"] = TokenValue.Bullets((s.Decision?.OpenConditions ?? []).OrderBy(c => c.ExpiresAt)
                .Select(c => $"{c.Text} · {c.Owner} · by {DayTime(c.ExpiresAt)}" + (c.ExpiresAt <= s.AsOf ? " (expired)" : ""))),
            ["KnownIssues"] = TokenValue.Bullets(s.KnownIssues.Where(k => k.Status != "Resolved")
                .OrderBy(k => SeverityRank.GetValueOrDefault(k.Severity, 9)).ThenBy(k => k.Title, StringComparer.OrdinalIgnoreCase)
                .Select(k => string.Join(" · ", new[] { k.Severity, k.Title, k.ExternalKey, k.Status == "Accepted" ? "accepted" : null, string.IsNullOrWhiteSpace(k.Workaround) ? null : "workaround: " + k.Workaround }.Where(x => !string.IsNullOrWhiteSpace(x))))),
            ["CloseCode"] = TokenValue.Scalar(s.CloseCode switch { null => "Not closed", "SuccessfulWithIssues" => "Successful with issues", var c => c }),
        };
    }

    /// <summary>Parses subject and body, renders them for <paramref name="target"/> and collects every template error. The subject is rendered
    /// single-line and (for Html and Markdown) as plain text: a subject line is never markup.</summary>
    public static HydratedMessage Hydrate(HydrationSnapshot snapshot, TimeZoneInfo zone, string? subject, string? body, CommTarget target)
    {
        var values = Values(snapshot, zone);
        var subj = TokenParser.Parse(subject, "subject");
        var text = TokenParser.Parse(body, "body");
        var subjectTarget = target == CommTarget.JsonString ? CommTarget.JsonString : CommTarget.PlainText;
        return new HydratedMessage(
            subject is null ? null : TokenRenderer.Render(subj, values, subjectTarget, singleLine: true),
            TokenRenderer.Render(text, values, target),
            [.. subj.Errors, .. text.Errors],
            [.. subj.Tokens.Concat(text.Tokens).Distinct()]);
    }
}

/// <summary>The rendered message. <see cref="CanDispatch"/> is false while any template error exists: dispatch refuses (PROJECT_SCOPE 5.2).</summary>
public sealed record HydratedMessage(string? Subject, string Text, IReadOnlyList<TokenError> TokenErrors, IReadOnlyList<string> TokensUsed)
{
    public bool CanDispatch => TokenErrors.Count == 0;
}
