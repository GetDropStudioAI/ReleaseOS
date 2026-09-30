namespace ReleaseMgmt.Domain.Exchange;

/// <summary>
/// OWASP CSV-injection escaping for exports and the matching unescape for imports (PROJECT_SCOPE 9, Q-048b).
/// <para><b>Export</b>: a cell a spreadsheet could read as a formula (first character = + - @ tab or CR) gets one leading apostrophe.
/// A cell that itself starts with an apostrophe gets one more, so the pair is reversible.</para>
/// <para><b>Import</b>: when a cell starts with an apostrophe <i>and</i> the character after it is one of those five or another apostrophe,
/// exactly one leading apostrophe is removed. Any other leading apostrophe (<c>'tis</c>) is data and stays.</para>
/// <para>So <c>Unescape(Escape(x)) == x</c> for every string: <c>-5</c> exports as <c>'-5</c>, <c>'-5</c> as <c>''-5</c>, <c>'tis</c> as <c>''tis</c>.
/// A hand-written file that contains <c>'=x</c> imports as <c>=x</c>, which is the reading a person means.</para>
/// </summary>
public static class CsvSafety
{
    private static bool Dangerous(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    public static string Escape(string? cell)
    {
        if (string.IsNullOrEmpty(cell)) return "";
        return Dangerous(cell[0]) || cell[0] == '\'' ? "'" + cell : cell;
    }

    public static string Unescape(string? cell)
    {
        if (string.IsNullOrEmpty(cell)) return "";
        return cell.Length > 1 && cell[0] == '\'' && (Dangerous(cell[1]) || cell[1] == '\'') ? cell[1..] : cell;
    }
}
