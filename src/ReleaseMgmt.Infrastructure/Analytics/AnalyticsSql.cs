using System.Text.RegularExpressions;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>
/// The analytics statements (M1..M24), single-sourced from <c>db/analytics.sql</c> (embedded at build time as resource "analytics.sql", the same way
/// schema.sql is; see Q-046a). Parsed exactly like tests/reference/seed_and_query.py: a block is "-- Mn title", "-- name: x", then the statement.
/// REOS-121: the file's preamble defines fragments (<c>-- @fragment rel</c> ... <c>-- @end</c>) and the statements use <c>{{rel}}</c>, <c>{{quarter:EXPR}}</c>,
/// <c>{{keys:EXPR}}</c> and <c>{{keycols}}</c>; they are expanded here, once, into plain SQL that is still bound with parameters only.
/// </summary>
public static class AnalyticsSql
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ByName = new(Load);

    /// <summary>Statement text (no trailing semicolon) by query name, e.g. "on_time_rate".</summary>
    public static string Get(string name) => ByName.Value.TryGetValue(name, out var q) ? q : throw new KeyNotFoundException($"No analytics query named {name}");

    public static IReadOnlyCollection<string> Names => ByName.Value.Keys.ToArray();

    /// <summary>Parses analytics.sql text. Public so a test can parse the file on disk and prove the embedded copy is the same.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string sql)
    {
        var blocks = Regex.Split(sql.Replace("\r\n", "\n"), @"\n-- (M\d+ .*)\n-- name: (\w+)\n");
        var fragments = Regex.Matches(blocks[0], @"-- @fragment (\w+)\n(.*?)\n-- @end", RegexOptions.Singleline).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < blocks.Length; i += 3) d[blocks[i + 1]] = Expand(blocks[i + 2].Trim().TrimEnd(';'), fragments);
        return d;
    }

    /// <summary>The group columns of M16..M24, in the order every such metric returns them.</summary>
    public static readonly string[] KeyColumns = ["workspace", "team", "release_type", "version", "segment", "quarter"];
    /// <summary>The names a request may group by (<c>groupBy=</c>); each is one of <see cref="KeyColumns"/> (release_type is asked for as "type").</summary>
    public static readonly string[] Dimensions = ["workspace", "team", "type", "version", "segment", "quarter"];

    private static readonly (string Dimension, string Alias, string Column)[] KeyParts =
    [
        ("workspace", "workspace", "rel.ws_name"), ("team", "team", "rel.team"), ("type", "release_type", "rel.rtype"), ("version", "version", "rel.version"), ("segment", "segment", "rel.segment"),
    ];

    /// <summary>The quarter label of a date expression: <c>2026-Q3</c>, or <c>FY2027-Q1</c> when :fy is not 1 (the fiscal year is named for the year it ends in).</summary>
    private static string Quarter(string x) =>
        $"(CASE WHEN :fy = 1 THEN strftime('%Y', {x}) ELSE 'FY' || (CAST(strftime('%Y', {x}) AS INTEGER) + (CAST(strftime('%m', {x}) AS INTEGER) >= :fy)) END " +
        $"|| '-Q' || (((CAST(strftime('%m', {x}) AS INTEGER) - :fy + 12) % 12) / 3 + 1))";

    private static string Expand(string sql, IReadOnlyDictionary<string, string> fragments)
    {
        sql = Regex.Replace(sql, @"\{\{keys:(.*?)\}\}", m =>
            string.Join(",\n       ", KeyParts.Select(k => $"CASE WHEN instr(:by, ',{k.Dimension},') > 0 THEN {k.Column} ELSE '' END AS {k.Alias}")
                .Append($"CASE WHEN instr(:by, ',quarter,') > 0 THEN {Quarter(m.Groups[1].Value)} ELSE '' END AS quarter")), RegexOptions.Singleline);
        sql = Regex.Replace(sql, @"\{\{quarter:(.*?)\}\}", m => Quarter(m.Groups[1].Value), RegexOptions.Singleline);
        sql = sql.Replace("{{keycols}}", string.Join(", ", KeyColumns));
        return Regex.Replace(sql, @"\{\{(\w+)\}\}", m => fragments.TryGetValue(m.Groups[1].Value, out var f) ? f : throw new InvalidOperationException($"analytics.sql: no fragment named {m.Groups[1].Value}"));
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var s = typeof(AnalyticsSql).Assembly.GetManifestResourceStream("analytics.sql") ?? throw new InvalidOperationException("analytics.sql is not embedded");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}
