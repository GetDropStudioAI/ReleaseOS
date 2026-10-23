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

    /// <summary>The triggers ON one table, verbatim and in file order: what a rebuild of that table must re-apply, since dropping a table drops its triggers.</summary>
    public static IReadOnlyList<string> TriggersOn(string table) =>
        [.. Triggers().Where(t => Regex.IsMatch(t, $@"^CREATE TRIGGER \w+ [^\n]*\bON {Regex.Escape(table)}\b"))];

    /// <summary>INSERT statements: rows every database starts with (the default workspace), applied after the tables and indexes.</summary>
    public static IReadOnlyList<string> Rows() => [.. RowRegex().Matches(Full).Select(m => m.Value)];

    /// <summary>CREATE TRIGGER statements, verbatim and in file order (same-event triggers fire newest-first).</summary>
    public static IReadOnlyList<string> Triggers() => [.. TriggerRegex().Matches(Full).Select(m => m.Value)];

    [GeneratedRegex(@"^CREATE TABLE \w+ \(.*?^\);", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"^CREATE (UNIQUE )?INDEX [^;]*;", RegexOptions.Multiline)]
    private static partial Regex IndexRegex();

    [GeneratedRegex(@"^INSERT INTO \w+ [^;]*;", RegexOptions.Multiline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"^CREATE TRIGGER \w+.*?\bEND;", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex TriggerRegex();
}
