using System.Text.RegularExpressions;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>
/// The 15 analytics statements, single-sourced from <c>db/analytics.sql</c> (embedded at build time as resource "analytics.sql", the same way
/// schema.sql is; see Q-046a). Parsed exactly like tests/reference/seed_and_query.py: a block is "-- Mn title", "-- name: x", then the statement.
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
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < blocks.Length; i += 3) d[blocks[i + 1]] = blocks[i + 2].Trim().TrimEnd(';');
        return d;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var s = typeof(AnalyticsSql).Assembly.GetManifestResourceStream("analytics.sql") ?? throw new InvalidOperationException("analytics.sql is not embedded");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}
