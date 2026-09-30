using System.Text;
using nietras.SeparatedValues;
using ReleaseMgmt.Domain.Exchange;

namespace ReleaseMgmt.Infrastructure.Exchange;

public sealed record CsvContent(IReadOnlyList<string> Header, IReadOnlyList<(int Row, string[] Cells)> Rows, int RecordCount);

/// <summary>Reads an import file (PROJECT_SCOPE 9): UTF-8 with or without BOM, RFC 4180 quoting (embedded commas, quotes and line breaks), comma separated. Sep does the parsing.</summary>
public static class CsvFile
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>The content, or a message that says why the file cannot be read at all. Blank lines and all-empty rows are skipped but still counted in row numbers.
    /// Stops reading as soon as the row limit is passed: a 10,000-row cap is not a reason to parse a 4 MB file to the end.</summary>
    public static (CsvContent? Content, string? Fatal) Read(byte[] bytes, int maxColumns = ImportLimits.DefaultMaxColumns)
    {
        if (bytes.Length == 0) return (null, "The file is empty");
        if (bytes.Length > ImportLimits.MaxBytes) return (null, $"The file is larger than {ImportLimits.MaxBytes / (1024 * 1024)} MB");
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4)
            return (null, "This is an Excel (.xlsx) file. Excel files are not imported: save it as CSV UTF-8 and upload that");
        if (Array.IndexOf(bytes, (byte)0) >= 0) return (null, "This is not a text file (it contains binary data). Upload comma-separated CSV, UTF-8");
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try { text = Strict.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { return (null, "The file is not valid UTF-8. In Excel choose Save as > CSV UTF-8 (Comma delimited)"); }

        using var reader = Sep.New(',').Reader(o => o with { HasHeader = false, Unescape = true, DisableColCountCheck = true }).From(new StringReader(text));
        string[]? header = null;
        var rows = new List<(int, string[])>();
        var record = 0;
        foreach (var r in reader)
        {
            // Checked before a cell is copied: a row of a million commas is refused, not turned into a million strings and errors (SEC-D3).
            if (r.ColCount > maxColumns)
                return (null, $"{(header is null ? "The header row" : $"Row {record + 1}")} has {r.ColCount:N0} columns; an import file has at most {maxColumns:N0}. Check that it is comma-separated and remove unused columns");
            var cells = new string[r.ColCount];
            for (var i = 0; i < cells.Length; i++) cells[i] = r[i].ToString();
            if (header is null) { header = cells; continue; }
            record++;
            if (cells.All(c => c.Length == 0)) continue;
            if (rows.Count >= ImportLimits.MaxRows) return (null, $"The file has more than {ImportLimits.MaxRows:N0} rows. Split it and import the parts one after the other");
            rows.Add((record, cells));
        }
        if (header is null) return (null, "The file has no header row");
        return (new CsvContent(header, rows, record), null);
    }
}
