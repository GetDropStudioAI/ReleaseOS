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

    /// <summary>The named tables and indexes of schema.sql, in file order, each with IF NOT EXISTS: what an incremental migration runs to add objects that the
    /// Schema migration already creates on a fresh database (Q-0840). SQLite stores the text without IF NOT EXISTS, so the stored DDL still equals schema.sql.</summary>
    public static IReadOnlyList<string> IfNotExists(params string[] names)
    {
        var found = TablesAndIndexes().Where(s => names.Contains(NameRegex().Match(s).Groups[2].Value)).ToList();
        if (found.Count != names.Length) throw new InvalidOperationException($"schema.sql does not define all of: {string.Join(", ", names)}");
        return [.. found.Select(s => NameRegex().Replace(s, m => $"{m.Groups[1].Value}IF NOT EXISTS {m.Groups[2].Value}", 1))];
    }

    [GeneratedRegex(@"^(CREATE (?:TABLE|(?:UNIQUE )?INDEX) )(\w+)")]
    private static partial Regex NameRegex();
}
