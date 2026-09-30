using System.Globalization;
using System.Text.RegularExpressions;

namespace ReleaseMgmt.Domain.Exchange;

/// <summary>Result of reading a header row: spec column name -> position, the <c>#</c> columns that were skipped, and every header error (row 0).</summary>
public sealed record HeaderResult(IReadOnlyDictionary<string, int> Columns, IReadOnlyList<string> Skipped, IReadOnlyList<ImportError> Errors, int Width)
{
    public int Recognised => Columns.Count;
}

/// <summary>One data row after cell validation. <see cref="Cells"/> holds the canonical text of every column that is in the file (raw text where the cell failed).</summary>
public sealed record ParsedRow(int Row, IReadOnlyDictionary<string, string> Cells, IReadOnlyList<ImportError> Errors);

/// <summary>
/// How much "did you mean" work one import plan may do, in edit-distance cells (input length x candidate length). One plan shares one budget, so a file of
/// ten thousand near-miss names cannot make a preview run for minutes (SEC-D2); once it is spent the remaining errors simply carry no suggestion.
/// The default (50 million cells, well under a second) covers far more suggestions than the 1,000 errors a preview keeps.
/// </summary>
public sealed class SuggestBudget(long cells = SuggestBudget.DefaultCells)
{
    public const long DefaultCells = 50_000_000;
    public long Remaining { get; private set; } = cells;
    public bool Exhausted => Remaining <= 0;
    public bool Spend(long cost) { if (cost > Remaining) { Remaining = 0; return false; } Remaining -= cost; return true; }
}

/// <summary>Header handling and typed cell validation (PROJECT_SCOPE 9). Pure: no I/O, no database. Rows are numbered from 1 (header excluded); row 0 is the header.</summary>
public static partial class RowParser
{
    [GeneratedRegex(@"^[^\s@;,""<>]+@[^\s@;,""<>]+$")] private static partial Regex EmailShape();
    [GeneratedRegex(@"^@?[A-Za-z0-9._-]{1,40}$")] private static partial Regex HandleShape();
    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")] private static partial Regex ZoneSuffix();
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"];

    public static HeaderResult ResolveHeader(ImportKindSpec spec, IReadOnlyList<string> header)
    {
        var errors = new List<ImportError>();
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Replace("﻿", "").Trim();
            if (name.Length == 0) { errors.Add(new(0, $"(column {i + 1})", "This column has no name. Delete the empty column or give it a name; unknown columns are errors")); continue; }
            if (name[0] == '#') { skipped.Add(name); continue; }
            if (!seen.Add(name)) { errors.Add(new(0, name, $"Column {name} appears more than once")); continue; }
            if (spec.Find(name) is { } c) { columns[c.Name] = i; continue; }
            if (ImportKinds.StatusColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                errors.Add(new(0, name, $"{name} cannot be imported: a status changes only through its workflow (certify, advance, complete), never by file. Remove the column, or start its name with # to skip it"));
            else if (header.Count == 1 && (name.Contains(';') || name.Contains('\t')))
                errors.Add(new(0, name, "This looks like a semicolon- or tab-separated file. Save it as comma-separated CSV (UTF-8)"));
            else
                errors.Add(new(0, name, $"Unknown column {name}. Recognised: {string.Join(", ", spec.Columns.Select(x => x.Name))}. Columns starting with # are skipped"));
        }
        foreach (var c in spec.Columns.Where(c => c.Required && !columns.ContainsKey(c.Name)))
            errors.Add(new(0, c.Name, $"Required column {c.Name} is missing from the header"));
        return new HeaderResult(columns, skipped, errors, header.Count);
    }

    /// <summary>Validates one data row against the header. <paramref name="rawCells"/> are the cells exactly as read; the CSV unescape (Q-048b) is applied here.</summary>
    public static ParsedRow Parse(ImportKindSpec spec, HeaderResult header, int row, IReadOnlyList<string> rawCells)
    {
        var errors = new List<ImportError>();
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawCells.Count != header.Width)
        {
            errors.Add(new(row, "(row)", $"This row has {rawCells.Count} cells but the header has {header.Width}"));
            return new ParsedRow(row, cells, errors);
        }
        foreach (var col in spec.Columns)
        {
            if (!header.Columns.TryGetValue(col.Name, out var i)) continue;
            var text = CsvSafety.Unescape(rawCells[i]).Trim();
            var (canonical, error) = Cell(col, text);
            cells[col.Name] = error is null ? canonical : text;
            if (error is not null) errors.Add(new(row, col.Name, error));
        }
        return new ParsedRow(row, cells, errors);
    }

    /// <summary>One cell: the canonical text, or a readable message. Blank optional cells are canonical "".</summary>
    public static (string Value, string? Error) Cell(ColumnSpec c, string text)
    {
        if (text.Length == 0)
            return c.Required && !c.AllowEmpty ? ("", $"{c.Name} is required") : ("", null);
        if (!c.Multiline && text.AsSpan().IndexOfAny('\r', '\n') >= 0) return (text, $"{c.Name} must be on one line");
        if (c.Max > 0 && c.Type is ColumnType.Text or ColumnType.Email && text.Length > c.Max) return (text, $"{c.Name} is longer than {c.Max} characters");
        // An owner is an email or an @handle: nothing longer can resolve, and an unbounded cell was edit-distanced against every user (SEC-D2).
        if (c.Type is ColumnType.Owner && text.Length > MaxEmailLength) return (text, $"{c.Name} is longer than {MaxEmailLength} characters");
        switch (c.Type)
        {
            case ColumnType.Text:
                if (c.Pattern is not null && !Regex.IsMatch(text, c.Pattern)) return (text, $"{c.Name} must be {c.Hint ?? "in the expected format"}");
                return (c.Upper ? text.ToUpperInvariant() : text, null);
            case ColumnType.Int:
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < c.Min || (c.Max > 0 && n > c.Max))
                    return (text, $"{c.Name} must be a whole number from {c.Min} to {c.Max}");
                return (n.ToString(CultureInfo.InvariantCulture), null);
            case ColumnType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return (text, $"{c.Name} must be a date as yyyy-MM-dd (ISO-8601), for example 2026-11-13");
                return (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
            case ColumnType.Timestamp:
                if (!ZoneSuffix().IsMatch(text) || !DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return (text, $"{c.Name} must be an ISO-8601 time with Z or an offset, for example 2026-11-13T01:00:00Z");
                return (t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), null);
            case ColumnType.Enum:
                var hit = c.Allowed!.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
                return hit is null ? (text, $"{c.Name} must be one of {string.Join(", ", c.Allowed!)}") : (hit, null);
            case ColumnType.Email:
                return EmailShape().IsMatch(text) ? (text, null) : (text, $"{c.Name} must be an email address");
            case ColumnType.Handle:
                return HandleShape().IsMatch(text) ? (text.TrimStart('@'), null) : (text, $"{c.Name} is letters, digits, '.', '_' or '-' (optionally starting with @)");
            case ColumnType.Owner:
                if (text[0] == '@') return HandleShape().IsMatch(text) ? (text, null) : (text, $"{c.Name} must be an email or an @handle");
                return EmailShape().IsMatch(text) ? (text, null) : (text, $"{c.Name} must be an email or an @handle");
            case ColumnType.EmailList:
            {
                var parts = Split(text);
                var tooLong = parts.FirstOrDefault(p => p.Length > MaxEmailLength);
                if (tooLong is not null) return (text, $"{c.Name} is a list of emails separated by ';', and one of them is longer than {MaxEmailLength} characters");
                var bad = parts.FirstOrDefault(p => !EmailShape().IsMatch(p));
                if (bad is not null) return (text, $"{c.Name} is a list of emails separated by ';', and \"{bad}\" is not an email");
                return (string.Join(';', parts.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal)), null);
            }
            case ColumnType.CodeList:
            {
                var parts = Split(text).Select(p => p.ToUpperInvariant()).ToList();
                var bad = parts.FirstOrDefault(p => !Regex.IsMatch(p, ImportKinds.Get(ImportKinds.RunbookSteps)!.Find("StepCode")!.Pattern!));
                if (bad is not null) return (text, $"{c.Name} is a list of step codes separated by ';', and \"{bad}\" is not a step code");
                return (string.Join(';', parts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), null);
            }
        }
        return (text, null);
    }

    public static string[] Split(string list) => list.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The closest candidates to <paramref name="input"/> by edit distance (case-insensitive), best first; empty when nothing is near.
    /// Bounded (SEC-D2): an input longer than <see cref="MaxSuggestLength"/> is not a typo and gets no suggestion, and a candidate whose length differs by more
    /// than the allowed distance is skipped without computing it (the length difference is a lower bound of the edit distance), so the cost per call is small.</summary>
    public static IReadOnlyList<string> Suggest(string input, IEnumerable<string> pool, int max = 1, SuggestBudget? budget = null)
    {
        if (input.Length > MaxSuggestLength || budget is { Exhausted: true }) return [];
        var limit = Math.Max(2, input.Length / 3);
        var lower = input.ToLowerInvariant();
        var scored = new List<(string p, int d)>();
        foreach (var p in pool.Distinct())
        {
            if (Math.Abs(p.Length - input.Length) > limit) continue;
            if (budget is not null && !budget.Spend((long)input.Length * p.Length)) break;   // out of budget: suggest from what was scored
            var d = Distance(lower, p.ToLowerInvariant());
            if (d > 0 && d <= limit) scored.Add((p, d));
        }
        return [.. scored.OrderBy(x => x.d).ThenBy(x => x.p, StringComparer.Ordinal).Take(max).Select(x => x.p)];
    }

    /// <summary>RFC 5321 path limit: no email address (and so no owner cell) is longer.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Inputs longer than this get no "did you mean" (titles are at most 300 characters; a typo is short).</summary>
    public const int MaxSuggestLength = 300;

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
