using System.Reflection;
using System.Text.RegularExpressions;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>db/schema.sql, embedded verbatim, split into the statements the two migrations apply.</summary>
public static partial class SchemaSql
{
    private static readonly string Full = Load();

    private static string Load()
    {
        using var s = typeof(SchemaSql).Assembly.GetManifestResourceStream("schema.sql")
                      ?? throw new InvalidOperationException("schema.sql is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>CREATE TABLE and CREATE INDEX statements, in file order.</summary>
    public static IReadOnlyList<string> TablesAndIndexes() =>
        [.. TableRegex().Matches(Full).Select(m => m.Value), .. IndexRegex().Matches(Full).Select(m => m.Value)];

    /// <summary>CREATE TRIGGER statements, verbatim and in file order (same-event triggers fire newest-first).</summary>
    public static IReadOnlyList<string> Triggers() => [.. TriggerRegex().Matches(Full).Select(m => m.Value)];

    [GeneratedRegex(@"^CREATE TABLE \w+ \(.*?^\);", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"^CREATE (UNIQUE )?INDEX [^;]*;", RegexOptions.Multiline)]
    private static partial Regex IndexRegex();

    [GeneratedRegex(@"^CREATE TRIGGER \w+.*?\bEND;", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex TriggerRegex();
}
