using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using nietras.SeparatedValues;
using ReleaseMgmt.Domain.Exchange;

namespace ReleaseMgmt.Infrastructure.Exchange;

/// <summary>A grid ready to be written: header names, which columns are numbers, and the rows as text (already canonical; escaping is applied by the writers).</summary>
public sealed record GridTable(string Grid, string Title, IReadOnlyList<string> Columns, IReadOnlyList<bool> Numeric, IReadOnlyList<string[]> Rows, long Total, bool Truncated)
{
    public const string TruncationMarker = "# truncated: ";
    public string? TruncationNote => Truncated ? $"{TruncationMarker}{Rows.Count:N0} of {Total:N0} rows exported; narrow the filter to export the rest" : null;
}

/// <summary>
/// CSV and XLSX writers for grid exports (PROJECT_SCOPE 9, REOS-49).
/// <para><b>CSV</b>: Sep, RFC 4180 quoting, CRLF line ends, UTF-8 <b>with</b> a BOM so Excel reads non-ASCII text correctly (Q-048d); the importer accepts it. Every text cell goes through <see cref="CsvSafety.Escape"/>.</para>
/// <para><b>XLSX</b>: ClosedXML, one sheet, bold frozen filtered header, whole-number columns as numbers and everything else as text cells (so "007", "2026-11-13" and "=1+1" stay what they are), with the same escaping.</para>
/// </summary>
public static class GridFiles
{
    public static byte[] ToCsv(GridTable t)
    {
        var sw = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        using (var writer = Sep.New(',').Writer(o => o with { Escape = true, WriteHeader = false }).To(sw))
        {
            void Write(IReadOnlyList<string> cells, bool escape)
            {
                using var row = writer.NewRow();
                for (var i = 0; i < t.Columns.Count; i++) row[i].Set(escape ? CsvSafety.Escape(i < cells.Count ? cells[i] : "") : (i < cells.Count ? cells[i] : ""));
            }
            // header first, by hand: Sep would otherwise take it from the first row's column names and an empty grid would have no header
            using (var h = writer.NewRow()) for (var i = 0; i < t.Columns.Count; i++) h[i].Set(t.Columns[i]);
            foreach (var r in t.Rows) Write(r, escape: true);
            if (t.TruncationNote is { } note) Write([note], escape: false);
        }
        var body = Encoding.UTF8.GetBytes(sw.ToString());
        return [.. Encoding.UTF8.GetPreamble(), .. body];
    }

    public static byte[] ToXlsx(GridTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName(t.Title));
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var h = ws.Cell(1, c + 1);
            h.SetValue(t.Columns[c]);
            h.Style.Font.Bold = true;
        }
        var rowNo = 2;
        foreach (var r in t.Rows) { WriteRow(ws, t, r, rowNo++, escape: true); }
        if (t.TruncationNote is { } note) WriteRow(ws, t, [note], rowNo, escape: false);
        ws.SheetView.FreezeRows(1);
        if (t.Columns.Count > 0) ws.Range(1, 1, Math.Max(1, rowNo - 1), t.Columns.Count).SetAutoFilter();
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var width = Math.Min(60, Math.Max(t.Columns[c].Length + 2, t.Rows.Take(200).Select(r => c < r.Length ? Math.Min(r[c].Length, 60) : 0).DefaultIfEmpty(0).Max() + 2));
            ws.Column(c + 1).Width = width;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteRow(IXLWorksheet ws, GridTable t, IReadOnlyList<string> cells, int rowNo, bool escape)
    {
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var text = c < cells.Count ? cells[c] : "";
            if (text.Length == 0) continue;
            var cell = ws.Cell(rowNo, c + 1);
            if (t.Numeric[c] && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { cell.SetValue(n); continue; }
            var safe = escape ? CsvSafety.Escape(text) : text;
            cell.Style.NumberFormat.Format = "@";          // text format: Excel never re-reads it as a number, date or formula
            // ClosedXML treats ONE leading apostrophe as Excel's "quote prefix" (strips it and sets a flag), which would hide the OWASP prefix from every other reader.
            // Adding one more and clearing the flag stores the escaped text literally, exactly as the CSV carries it.
            cell.SetValue(safe.StartsWith('\'') ? "'" + safe : safe);
            if (safe.StartsWith('\'')) cell.Style.IncludeQuotePrefix = false;
            cell.Style.Alignment.WrapText = safe.Contains('\n');
        }
    }

    private static string SheetName(string title)
    {
        var s = new string([.. title.Where(ch => !"[]:*?/\\".Contains(ch))]).Trim();
        return s.Length == 0 ? "Export" : s.Length > 31 ? s[..31] : s;
    }
}
