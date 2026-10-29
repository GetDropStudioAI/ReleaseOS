using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ReleaseMgmt.Infrastructure.Exports.Documents;
using ReleaseMgmt.Infrastructure.Services;
using UglyToad.PdfPig;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;
using static ReleaseMgmt.Api.Tests.PdfExportTests;
using IcsCalendar = Ical.Net.Calendar;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-142 (QA of the exported files): every file the app hands out is readable by a person and by the program that opens it.
///   PDF  : opens, has a title and an author, every page says "Page n of m", page 1 carries an instant labelled UTC, the text is only in the house palette
///          (PdfStyle: ink, quiet, bad, good) and every colour reads on the white page at 4.5:1 or better.
///   XLSX : a header row of words (bold, none empty), frozen so it stays in view, and instants written as UTC text.
///   CSV  : UTF-8 with a BOM, CRLF, the same header as the workbook, instants written with the Z.
///   ICS  : parses, CRLF, folded lines, every event has a UID, a summary and a UTC stamp, and a timed event is UTC or a whole day: never a floating local time.
/// The numbers inside the files are tested by the classes that own them (PdfExportTests, ExecutiveExportTests, AnalyticsExportTests, ImportExportTests, CalendarTests).
/// </summary>
[Collection("PdfEnvironment")]
public class ExportQualityTests
{
    private static readonly string[] Palette = [PdfStyle.Ink, PdfStyle.Quiet, PdfStyle.Bad, PdfStyle.Good];
    private static readonly Regex Instant = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?$", RegexOptions.Compiled);

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------------

    private static double Lum((double R, double G, double B) c)
    {
        static double L(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static double ContrastOnWhite((double R, double G, double B) c) => 1.05 / (Lum(c) + 0.05);

    private static string Hex((double R, double G, double B) c) => "#" + string.Concat(new[] { c.R, c.G, c.B }.Select(v => ((int)Math.Round(v * 255)).ToString("x2", CultureInfo.InvariantCulture)));

    /// <summary>What a person sees of a PDF, asserted per page so a failure names the page.</summary>
    internal static void AssertReadablePdf(byte[] bytes, string what)
    {
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 5));
        using var d = PdfDocument.Open(bytes);
        Assert.True(d.NumberOfPages >= 1, $"{what}: no pages");
        Assert.False(string.IsNullOrWhiteSpace(d.Information.Title), $"{what}: no document title (the viewer's tab would read the file name)");
        Assert.False(string.IsNullOrWhiteSpace(d.Information.Author), $"{what}: no author");
        Assert.Matches(@"\d\d:\d\d UTC", d.GetPage(1).Text);                       // an instant on page 1, labelled (D51: UTC with the display zone beside it)
        var seen = new HashSet<string>();
        for (var n = 1; n <= d.NumberOfPages; n++)
        {
            var page = d.GetPage(n);
            Assert.Matches($@"Page {n} of {d.NumberOfPages}", page.Text);           // the footer, so a loose sheet can be put back in order
            Assert.Equal(612, Math.Round(page.Width)); Assert.Equal(792, Math.Round(page.Height));   // Letter
            foreach (var l in page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
            {
                var rgb = l.Color.ToRGBValues();
                var hex = Hex(rgb);
                if (!seen.Add(hex)) continue;
                Assert.True(Palette.Contains(hex, StringComparer.OrdinalIgnoreCase), $"{what} p{n}: text colour {hex} is not in the PdfStyle palette ({string.Join(", ", Palette)}), so it is hard-coded somewhere");
                Assert.True(ContrastOnWhite(rgb) >= 4.5, $"{what} p{n}: {hex} on the white page is {ContrastOnWhite(rgb):0.00}:1, below 4.5:1");
            }
        }
    }

    /// <summary>A header row a person can use: words, none empty across the used width, bold, frozen under it; instants written as text with the Z.</summary>
    internal static void AssertUsableSheet(IXLWorksheet ws, string what, bool filter = false)
    {
        var last = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        Assert.True(last > 0, $"{what}: empty sheet");
        for (var c = 1; c <= last; c++)
        {
            var h = ws.Cell(1, c);
            Assert.False(string.IsNullOrWhiteSpace(h.GetString()), $"{what}!{h.Address}: the header row has an empty cell");
            Assert.True(h.Style.Font.Bold, $"{what}!{h.Address}: header '{h.GetString()}' is not bold");
        }
        Assert.Equal(1, ws.SheetView.SplitRow);                                    // FreezeRows(1): the header stays while the rows scroll
        if (filter) Assert.NotNull(ws.AutoFilter.Range);
        foreach (var cell in ws.RowsUsed().Skip(1).SelectMany(r => r.CellsUsed()).Where(c => c.DataType == XLDataType.Text))
        {
            var t = cell.GetString();
            if (Instant.IsMatch(t)) Assert.True(t.EndsWith('Z'), $"{what}!{cell.Address}: '{t}' is an instant with no UTC mark");
        }
    }

    /// <summary>The person's one calendar link and the four feeds behind it (all, mine, freezes, one train), read without signing in as a calendar app does.</summary>
    private static async Task<Dictionary<string, string>> Feeds(ApiFactory f, HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var path = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString()!;
        var baseUrl = path[..^".ics".Length];
        using var anon = f.CreateClient();
        var feeds = new Dictionary<string, string>();
        foreach (var (name, url) in new[] { ("all", path), ("mine", baseUrl + "/mine.ics"), ("freezes", baseUrl + "/freezes.ics"), ("train", baseUrl + "/trains/t1.ics") })
        {
            var feed = await anon.GetAsync(url);
            Assert.True(feed.StatusCode == HttpStatusCode.OK, $"{name}: {feed.StatusCode}");
            feeds[name] = await feed.Content.ReadAsStringAsync();
        }
        return feeds;
    }

    // ---- PDF ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ReleaseReport")]
    [InlineData("RunSheet")]
    [InlineData("EvidencePack")]
    [InlineData("Scorecard")]
    public async Task A_train_pdf_is_readable_titled_paged_utc_labelled_and_uses_only_the_house_palette(string kind)
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, kind)).GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, _) = await Download(e.Rte, id);
        AssertReadablePdf(bytes, kind);
    }

    [Fact]
    public async Task The_evidence_pack_backout_line_reads_as_sentences_with_a_space_between_them()
    {
        var e = await Setup(); using var _f = e.F;
        Sql(e.F, "DELETE FROM RunbookSteps WHERE Section='Rollback'");               // the branch with no rollback steps
        var id = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, _) = await Download(e.Rte, id);
        using var d = PdfDocument.Open(bytes);
        var text = string.Join("\n", d.GetPages().Select(p => p.Text));
        Assert.Contains("No rollback steps planned. Not rehearsed.", text);          // it read "planned.Not rehearsed." until the REOS-142 read-through
    }

    // ---- grids: CSV and XLSX of every grid -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_grid_is_a_usable_CSV_and_a_usable_XLSX_with_the_same_header_and_instants_in_UTC()
    {
        var e = await Setup(); using var _f = e.F;
        var grids = GridExportService.Grids.Select(g => g.Key).Append("audit").Distinct().ToList();
        Assert.True(grids.Count >= 18, string.Join(",", grids));
        foreach (var grid in grids)
        {
            var csvRes = await e.Gov.GetAsync($"/api/v1/exports/{grid}.csv");
            Assert.True(csvRes.StatusCode == HttpStatusCode.OK, $"{grid}.csv: {csvRes.StatusCode}");
            var csv = await csvRes.Content.ReadAsByteArrayAsync();
            Assert.True(csv.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), $"{grid}.csv: no UTF-8 BOM (Excel would misread non-ASCII text)");
            var text = Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
            Assert.True(!text.Replace("\r\n", "").Contains('\n') || text.Contains("\"") , $"{grid}.csv: bare line feeds outside quoted cells");
            var csvHeader = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(',');
            Assert.All(csvHeader, h => Assert.False(string.IsNullOrWhiteSpace(h), $"{grid}.csv: empty header name"));
            // an instant that is a whole cell must carry the Z (a quoted JSON blob is not a whole cell)
            foreach (Match m in Regex.Matches(text, @"(?:^|,|\r\n)(\d{4}-\d\d-\d\dT[\d:.]+)(?=,|\r\n|$)"))
                Assert.Fail($"{grid}.csv: instant '{m.Groups[1].Value}' has no UTC mark");

            var xlsx = await e.Gov.GetByteArrayAsync($"/api/v1/exports/{grid}.xlsx");
            using var wb = new XLWorkbook(new MemoryStream(xlsx));
            var ws = wb.Worksheets.Single();
            AssertUsableSheet(ws, $"{grid}.xlsx", filter: true);
            Assert.Equal(csvHeader, Enumerable.Range(1, csvHeader.Length).Select(c => ws.Cell(1, c).GetString()).ToArray());
        }
    }

    // ---- ICS -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_calendar_feeds_parse_have_CRLF_folded_lines_and_never_a_floating_local_time()
    {
        var e = await Setup(); using var _f = e.F;
        foreach (var (scope, ics) in await Feeds(e.F, e.Rte))
        {
            Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
            Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
            Assert.All(ics.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"{scope}: line over 75 octets, not folded: {line}"));
            var cal = IcsCalendar.Load(ics)!;
            Assert.True(scope == "mine" || cal.Events.Count > 0, $"{scope}: no events");
            foreach (var ev in cal.Events)
            {
                Assert.False(string.IsNullOrWhiteSpace(ev.Uid), $"{scope}: event with no UID");
                Assert.False(string.IsNullOrWhiteSpace(ev.Summary), $"{scope}: {ev.Uid} has no summary");
                Assert.NotNull(ev.DtStamp);
                Assert.True(ev.DtStamp!.IsUtc, $"{scope}: {ev.Uid} DTSTAMP is not UTC");
                if (ev.DtStart!.HasTime) Assert.True(ev.DtStart.IsUtc, $"{scope}: {ev.Uid} has a timed start that is not UTC (a floating or zoned time moves with the reader)");
            }
            Assert.DoesNotContain("TZID=", ics);                                       // everything timed is UTC, so there are no zone definitions to get wrong
        }
    }
}

/// <summary>REOS-142: the analytics workbook and the portfolio PDF, on the seeded history of <see cref="AnalyticsFixture"/>.</summary>
[Collection("PdfEnvironment")]
public class ExportQualityPortfolioTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    private const string Window = "from=2026-01-01&to=2026-12-31&now=2026-09-28T12%3A00%3A00Z";

    [Fact]
    public async Task The_workbook_has_a_Parameters_sheet_in_UTC_and_every_metric_sheet_has_a_bold_frozen_header()
    {
        var c = await Members.SignInAsync(fx, fx.Web, "xq-wb@x.com", "default", "ws-cards");
        using var wb = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/xlsx?{Window}&workspaces=all&groupBy=type,team"));
        var p = wb.Worksheet("Parameters");
        var labels = p.Column(1).CellsUsed().Select(x => x.GetString()).ToList();
        Assert.Contains("Generated at (UTC)", labels);
        Assert.Contains("Data as of (UTC)", labels);
        Assert.Contains("Period from (UTC day)", labels);
        Assert.Contains("UTC", p.Cell(p.Column(1).CellsUsed().First(x => x.GetString() == "Generated at (UTC)").Address.RowNumber, 2).GetString());
        foreach (var ws in wb.Worksheets.Where(w => w.Name != "Parameters"))
            ExportQualityTests.AssertUsableSheet(ws, $"workbook {ws.Name}");
    }

    [Fact]
    public async Task The_portfolio_pdf_is_readable_titled_paged_utc_labelled_and_uses_only_the_house_palette()
    {
        Environment.SetEnvironmentVariable("Pdf__QuestPdfLicense", "Community");
        var host = fx.Web.WithWebHostBuilder(_ => { });
        var c = await Members.SignInAsync(fx, host, "xq-pdf@x.com", "default", "ws-cards");
        var res = await c.GetAsync($"/api/v1/analytics/portfolio.pdf?{Window}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        ExportQualityTests.AssertReadablePdf(await res.Content.ReadAsByteArrayAsync(), "portfolio.pdf");
    }
}
