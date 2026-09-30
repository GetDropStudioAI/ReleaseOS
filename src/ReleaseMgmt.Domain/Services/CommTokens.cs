using System.Text;
using System.Text.Encodings.Web;

namespace ReleaseMgmt.Domain.Services;

/// <summary>Where a hydrated message is going. Values are escaped for the target (PROJECT_SCOPE 5.2); template text is never re-scanned.</summary>
public enum CommTarget
{
    /// <summary>Subject lines and <c>mailto:</c> bodies. Bold markers are dropped, values are verbatim.</summary>
    PlainText,
    /// <summary>Markdown source (the library format). Values have Markdown syntax neutralised.</summary>
    Markdown,
    /// <summary>Rich-text copy: template text and values are HTML-escaped, <c>**bold**</c> becomes strong, line breaks become br.</summary>
    Html,
    /// <summary>Webhook payloads: the Markdown rendering, then the whole text escaped as JSON string content (no surrounding quotes).</summary>
    JsonString,
}

/// <summary>What a token's value looks like when it is more than one word.</summary>
public enum TokenShape
{
    /// <summary>One value.</summary>
    Scalar,
    /// <summary>A short list printed on one line, separated by " · ". Empty prints "None".</summary>
    Inline,
    /// <summary>One line per item with a bullet. Empty prints "None".</summary>
    Bullets,
    /// <summary>Pre-aligned monospace lines (the gate table). Empty prints "None".</summary>
    Table,
}

/// <summary>A token's raw (unescaped) value. Escaping happens in <see cref="TokenRenderer"/>, per target.</summary>
public sealed record TokenValue(TokenShape Shape, IReadOnlyList<string> Items)
{
    public static TokenValue Scalar(string s) => new(TokenShape.Scalar, [s]);
    public static TokenValue Inline(IEnumerable<string> items) => new(TokenShape.Inline, [.. items]);
    public static TokenValue Bullets(IEnumerable<string> items) => new(TokenShape.Bullets, [.. items]);
    public static TokenValue Table(IEnumerable<string> lines) => new(TokenShape.Table, [.. lines]);
}

/// <summary>Kinds of template problem. Any of them blocks dispatch.</summary>
public static class TokenErrorKinds
{
    public const string UnknownToken = "UnknownToken";
    public const string MalformedToken = "MalformedToken";
    public const string UnclosedBrace = "UnclosedBrace";
    public const string StrayBrace = "StrayBrace";
}

/// <summary>One template problem. <see cref="Part"/> is "subject" or "body"; line and column are 1-based positions in that part.</summary>
public sealed record TokenError(string Kind, string Token, string Part, int Line, int Column, string Message, string? Suggestion = null);

/// <summary>A piece of a parsed template: literal text or an allowlisted token.</summary>
public abstract record TemplateSegment
{
    public sealed record Literal(string Text) : TemplateSegment;
    public sealed record Token(string Name, int Line, int Column) : TemplateSegment;
}

public sealed record ParsedTemplate(IReadOnlyList<TemplateSegment> Segments, IReadOnlyList<TokenError> Errors)
{
    /// <summary>Allowlisted token names used, in order, with repeats.</summary>
    public IEnumerable<string> Tokens => Segments.OfType<TemplateSegment.Token>().Select(t => t.Name);
    public bool IsValid => Errors.Count == 0;
}

/// <summary>The token allowlist (PROJECT_SCOPE 5.2). Nothing outside it ever expands.</summary>
public static class CommTokens
{
    public static readonly IReadOnlyList<string> Allowlist =
    [
        "ReleaseTitle", "TargetDate", "DaysToTarget", "Status", "Window", "ChangeTicket", "ProductList", "ProductCount", "GateTable",
        "NextGate", "NextGateDue", "TasksDone", "TasksTotal", "PercentComplete", "BlockerCount", "CriticalBlockers", "BlockerList",
        "Owners", "GoNoGoDecision", "Conditions", "KnownIssues", "CloseCode",
    ];

    /// <summary>One line per token for the editor's token list.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["ReleaseTitle"] = "Train title, for example R26.24 Q4 Payments Consolidated",
        ["TargetDate"] = "Target release date, for example Fri 30 Oct",
        ["DaysToTarget"] = "Business days from today to the target date (T-n); a minus sign once past",
        ["Status"] = "Train status: Planning, Gated, Executing, Complete or Aborted",
        ["Window"] = "Deployment window in the display time zone, or Not scheduled",
        ["ChangeTicket"] = "ServiceNow change number, or Not assigned",
        ["ProductList"] = "Bundled products with versions on one line, or None",
        ["ProductCount"] = "Number of bundled products",
        ["GateTable"] = "One line per gate with its state and date, or None",
        ["NextGate"] = "First gate that is not Certified or Waived, or None",
        ["NextGateDue"] = "Due date of the next gate, or None",
        ["TasksDone"] = "Completed checklist tasks across all gates",
        ["TasksTotal"] = "All checklist tasks across all gates",
        ["PercentComplete"] = "Completed share of checklist tasks, for example 85%",
        ["BlockerCount"] = "Number of open blockers",
        ["CriticalBlockers"] = "Open Critical blockers, one per line, or None",
        ["BlockerList"] = "All open blockers, worst first, or None",
        ["Owners"] = "Gate owners, blocker owners and the Go/No-Go decider on one line, or None",
        ["GoNoGoDecision"] = "Latest Go/No-Go decision, or Not yet recorded",
        ["Conditions"] = "Open conditions of the latest Go/No-Go with owner and expiry, or None",
        ["KnownIssues"] = "Known issues that are not resolved, or None",
        ["CloseCode"] = "Close code once complete, or Not closed",
    };

    private static readonly HashSet<string> Set = new(Allowlist, StringComparer.Ordinal);
    public static bool IsAllowed(string name) => Set.Contains(name);

    /// <summary>The closest allowlisted token to a typo (case differences, or an edit distance of at most 2), else null.</summary>
    public static string? Suggest(string name)
    {
        string? best = null; var bestD = 3;
        foreach (var t in Allowlist)
        {
            var d = Distance(t.ToLowerInvariant(), name.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>
/// Reads <c>{Token}</c> placeholders. A token is <c>{</c>, an identifier (ASCII letters, digits, underscore, starting with a letter), <c>}</c>.
/// Any other brace is an error: an identifier that is not on the allowlist (a wrong case included), anything else between braces, an unclosed
/// <c>{</c> or a stray <c>}</c>. There is no escape for a literal brace; the messages say so. Errored text stays in the output as literal
/// text so a preview shows it, but dispatch is refused while any error exists.
/// </summary>
public static class TokenParser
{
    public static ParsedTemplate Parse(string? text, string part = "body")
    {
        text ??= "";
        var segments = new List<TemplateSegment>(); var errors = new List<TokenError>();
        var lit = new StringBuilder();
        var line = 1; var col = 1;
        void Flush() { if (lit.Length > 0) { segments.Add(new TemplateSegment.Literal(lit.ToString())); lit.Clear(); } }
        void Advance(char c) { if (c == '\n') { line++; col = 1; } else col++; }
        void Emit(string raw) { lit.Append(raw); foreach (var ch in raw) Advance(ch); }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1); var open = text.IndexOf('{', i + 1);
                if (close < 0 || (open >= 0 && open < close))
                {
                    var raw = Clip(text.Substring(i, Math.Min(text.Length - i, 24)).Split('\n')[0]);
                    errors.Add(new(TokenErrorKinds.UnclosedBrace, raw, part, line, col,
                        $"'{{' at line {line}, column {col} has no closing '}}'. Braces are only for tokens; there is no way to print a literal brace"));
                    Emit("{"); i++; continue;
                }
                var name = text.Substring(i + 1, close - i - 1);
                var rawToken = text.Substring(i, close - i + 1);
                if (!IsIdentifier(name))
                {
                    errors.Add(new(TokenErrorKinds.MalformedToken, Clip(rawToken), part, line, col,
                        $"{Clip(rawToken)} at line {line}, column {col} is not a token. A token is a name in braces with no spaces, for example {{Status}}"));
                    Emit(rawToken);
                }
                else if (CommTokens.IsAllowed(name))
                {
                    Flush(); segments.Add(new TemplateSegment.Token(name, line, col));
                    foreach (var ch in rawToken) Advance(ch);
                }
                else
                {
                    var s = CommTokens.Suggest(name);
                    errors.Add(new(TokenErrorKinds.UnknownToken, rawToken, part, line, col,
                        $"Unknown token {rawToken} at line {line}, column {col}" + (s is null ? "" : $". Did you mean {{{s}}}?"), s is null ? null : $"{{{s}}}"));
                    Emit(rawToken);
                }
                i = close + 1;
                continue;
            }
            if (c == '}')
                errors.Add(new(TokenErrorKinds.StrayBrace, "}", part, line, col,
                    $"'}}' at line {line}, column {col} closes nothing. Braces are only for tokens; there is no way to print a literal brace"));
            Emit(c.ToString()); i++;
        }
        Flush();
        return new ParsedTemplate(segments, errors);
    }

    private static bool IsIdentifier(string s) =>
        s.Length > 0 && char.IsAsciiLetter(s[0]) && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "…";
}

/// <summary>Escaping per target. Every function is total and never throws.</summary>
public static class CommEscaper
{
    /// <summary>Drops control characters other than tab and newline (CR and CRLF become \n). Applied to every value before escaping.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') i++; sb.Append('\n'); }
            else if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Html(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
        return sb.ToString();
    }

    private const string MdSpecial = "\\`*_[]<>|~";

    /// <summary>Neutralises Markdown syntax: emphasis, code, links and images (via '['), raw HTML and autolinks (via '&lt;'), tables, and a line that would start a heading, list or setext rule.</summary>
    public static string Markdown(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        var lineStart = true;
        foreach (var c in s)
        {
            if (MdSpecial.Contains(c) || (lineStart && c is '#' or '-' or '+' or '=')) sb.Append('\\');
            sb.Append(c);
            lineStart = c == '\n';
        }
        return sb.ToString();
    }

    /// <summary>JSON string content: '"', '\' and control characters are escaped. The relaxed encoder leaves '&lt;', '&gt;', '&amp;' and non-ASCII alone, which JSON allows inside a string.</summary>
    public static string JsonString(string s) => JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(s);
}

/// <summary>Turns a parsed template plus token values into text. Pure: no clock, no database.</summary>
public static class TokenRenderer
{
    public const string None = "None";

    /// <summary>Renders <paramref name="template"/>. Literal text is copied (Html: escaped); token values are escaped for <paramref name="target"/>;
    /// the result is never scanned again, so a value containing "{Status}" stays as typed. A token with no value entry renders "None" (the hydrator
    /// always supplies all of them). <paramref name="singleLine"/> is for subject lines: line breaks in values collapse to spaces and lists join with "; ".</summary>
    public static string Render(ParsedTemplate template, IReadOnlyDictionary<string, TokenValue> values, CommTarget target, bool singleLine = false)
    {
        var sb = new StringBuilder();
        var bold = false;
        foreach (var seg in template.Segments)
        {
            switch (seg)
            {
                case TemplateSegment.Literal l:
                    switch (target)
                    {
                        case CommTarget.PlainText: sb.Append(l.Text.Replace("**", "")); break;
                        case CommTarget.Html:
                            var parts = l.Text.Split("**");
                            for (var i = 0; i < parts.Length; i++)
                            {
                                if (i > 0) { sb.Append(bold ? "</strong>" : "<strong>"); bold = !bold; }
                                sb.Append(CommEscaper.Html(parts[i]).Replace("\r\n", "\n").Replace("\n", "<br>"));
                            }
                            break;
                        default: sb.Append(l.Text); break;
                    }
                    break;
                case TemplateSegment.Token t:
                    sb.Append(Value(values.TryGetValue(t.Name, out var v) ? v : TokenValue.Scalar(None), target, singleLine));
                    break;
            }
        }
        if (bold) sb.Append("</strong>");
        var text = sb.ToString();
        return target == CommTarget.JsonString ? CommEscaper.JsonString(text) : text;
    }

    private static string Value(TokenValue v, CommTarget target, bool singleLine)
    {
        var items = v.Items.Select(CommEscaper.Clean).Where(s => s.Length > 0).ToList();
        var shape = v.Shape;
        if (items.Count == 0) { items = [None]; shape = TokenShape.Scalar; }
        if (singleLine) { shape = TokenShape.Inline; items = [.. items.Select(s => s.Replace('\t', ' ').Replace('\n', ' '))]; }

        string Esc(string s) => target switch
        {
            CommTarget.Html => CommEscaper.Html(s).Replace("\n", "<br>"),
            CommTarget.Markdown or CommTarget.JsonString => CommEscaper.Markdown(s),
            _ => s,
        };
        var br = target == CommTarget.Html ? "<br>" : "\n";
        switch (shape)
        {
            case TokenShape.Inline: return string.Join(singleLine ? "; " : " · ", items.Select(Esc));
            case TokenShape.Bullets:
                var bullet = target is CommTarget.Markdown or CommTarget.JsonString ? "- " : "• ";
                return string.Join(br, items.Select(s => bullet + Esc(s)));
            case TokenShape.Table:
                return target switch
                {
                    CommTarget.Html => "<code>" + string.Join("<br>", items.Select(CommEscaper.Html)) + "</code>",
                    CommTarget.Markdown or CommTarget.JsonString => "```\n" + string.Join("\n", items.Select(s => s.Replace('`', '\''))) + "\n```",
                    _ => string.Join("\n", items),
                };
            default: return Esc(items[0]);
        }
    }
}
