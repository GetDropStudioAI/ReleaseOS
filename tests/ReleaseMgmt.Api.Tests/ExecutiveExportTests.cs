using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using UglyToad.PdfPig;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-121: the analytics workbook carries the executive grouping and the portfolio PDF carries the same figures, for the workspaces the person belongs to and no others.
/// The PDF follows the page identity of the train PDFs (REOS-137): a header with the name and the reference, a footer with the generation stamp and page n of m, and every instant in UTC with
/// the display zone's reading beside it (D51).
/// </summary>
[Collection("PdfEnvironment")]
public class ExecutiveExportTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    private const string Window = "from=2026-01-01&to=2026-12-31&now=2026-09-28T12%3A00%3A00Z";
    private const string Licence = "Pdf__QuestPdfLicense";

    private Task<HttpClient> Member(string email, params string[] ws) => Members.SignInAsync(fx, fx.Web, email, ws);

    private static string Text(byte[] pdf)
    {
        using var d = PdfDocument.Open(pdf);
        return string.Join("\n", d.GetPages().Select(p => p.Text));
    }

    /// <summary>A host built after the licence variable is set (QuestPDF is configured when the options are read), and a member of the given workspaces on it.</summary>
    private async Task<(HttpClient Client, IDisposable Host)> PdfClient(string? licence, string email, params string[] ws)
    {
        Environment.SetEnvironmentVariable(Licence, licence);
        var host = fx.Web.WithWebHostBuilder(_ => { });
        var client = await Members.SignInAsync(fx, host, email, ws);
        return (client, host);
    }

    // ---- workbook ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_workbook_takes_the_workspaces_and_the_grouping_and_every_executive_sheet_equals_its_own_export()
    {
        var c = await Member("wb-two@x.com", "default", "ws-cards");
        var res = await c.GetAsync($"/api/v1/analytics/xlsx?{Window}&workspaces=all&groupBy=type,team");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        Assert.Equal(["Parameters", .. Enumerable.Range(1, 24).Select(i => $"M{i}")], wb.Worksheets.Select(w => w.Name).ToArray());

        var p = wb.Worksheet("Parameters");
        string V(string name) => p.RowsUsed().First(r => r.Cell(1).GetString() == name).Cell(2).GetString();
        Assert.Equal("Cards, Default", V("Workspaces"));   // by name: only the two the person belongs to
        Assert.DoesNotContain("Payments", string.Join(" ", p.CellsUsed().Select(x => x.GetString())));
        Assert.Equal("team, type (M16 to M24; M1 to M15 are not grouped)", V("Grouped by"));
        Assert.Contains("Calendar quarters", V("Quarters"));
        Assert.Contains("M6, M9, M14 and M21", V("Period"));
        Assert.Contains("member", V("Scope"));

        foreach (var id in Enumerable.Range(16, 9).Select(i => $"M{i}"))
        {
            using var one = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/{id}/xlsx?{Window}&workspaces=all&groupBy=type,team"));
            WorkbookAssert.SameCells(one.Worksheet(id), wb.Worksheet(id), id);
        }
        // the sheet is the JSON the screen holds: the six group columns first, then the measures
        var json = JsonDocument.Parse(await c.GetStringAsync($"/api/v1/analytics/M17?{Window}&workspaces=all&groupBy=type,team")).RootElement;
        var ws = wb.Worksheet("M17");
        Assert.Equal(["workspace", "team", "release_type", "version", "segment", "quarter", "track"], Enumerable.Range(1, 7).Select(i => ws.Cell(1, i).GetString()).ToArray());
        Assert.Equal(json.GetProperty("rows").GetArrayLength(), ws.LastRowUsed()!.RowNumber() - 1);
        Assert.Equal(json.GetProperty("rows")[0].GetProperty("team").GetString(), ws.Cell(2, 2).GetString());
    }

    [Fact]
    public async Task The_workbook_refuses_a_workspace_the_person_does_not_belong_to_and_without_the_parameter_reads_one()
    {
        var c = await Member("wb-one@x.com", "default", "ws-cards");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/xlsx?{Window}&workspaces=payments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/M18/xlsx?{Window}&workspaces=all,payments")).StatusCode);
        using var wb = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/xlsx?{Window}"));
        var p = wb.Worksheet("Parameters");
        Assert.Equal("Default", p.RowsUsed().First(r => r.Cell(1).GetString() == "Workspace").Cell(2).GetString());
        Assert.Equal("One workspace", p.RowsUsed().First(r => r.Cell(1).GetString() == "Scope").Cell(2).GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/analytics/M3/xlsx?{Window}&groupBy=quarter")).StatusCode);
    }

    [Fact]
    public async Task A_grouped_per_metric_workbook_says_how_it_was_grouped_and_how_quarters_count()
    {
        var c = await Member("wb-about@x.com", "default", "ws-cards", "ws-pay");
        using var wb = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/M17/xlsx?{Window}&workspaces=all&groupBy=quarter"));
        var about = wb.Worksheet("About");
        string V(string name) => about.RowsUsed().First(r => r.Cell(1).GetString() == name).Cell(2).GetString();
        Assert.Equal("quarter", V("Grouped by"));
        Assert.Contains("Calendar quarters", V("Quarters"));
        Assert.Equal("Cards, Default, Payments", V("Workspace"));
    }

    // ---- portfolio PDF ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_portfolio_pdf_is_anonymous_401_and_a_pdf_for_every_signed_in_role()
    {
        var (c, host) = await PdfClient("Community", "pdf-all@x.com", "default", "ws-cards", "ws-pay");
        using var _ = host;
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Web.CreateClient().GetAsync("/api/v1/analytics/portfolio.pdf")).StatusCode);
        var res = await c.GetAsync($"/api/v1/analytics/portfolio.pdf?{Window}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("portfolio-report-2026-01-01_2026-12-31.pdf", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages >= 1);
    }

    [Fact]
    public async Task The_portfolio_pdf_carries_the_page_identity_and_the_utc_rule_of_the_other_pdfs()
    {
        var (c, host) = await PdfClient("Community", "pdf-id@x.com", "default", "ws-cards", "ws-pay");
        using var _ = host;
        var bytes = await c.GetByteArrayAsync($"/api/v1/analytics/portfolio.pdf?{Window}");
        var text = Text(bytes);
        Assert.Contains("Portfolio report", text);
        Assert.Matches(@"PF-[0-9A-F]{6}", text);                                  // the reference, as the train PDFs print EX-xxxxxx
        Assert.Contains("12:00 UTC (07:00 CT)", text);                          // D51: UTC with the display zone beside it (the injected clock, CDT in September)
        Assert.Contains("Page 1 of", text);
        Assert.Contains("3 of 3 workspaces", text);
        Assert.Contains("Period 2026-01-01 to 2026-12-31 (UTC days)", text);
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal("Portfolio report: Cards, Default, Payments", doc.Information.Title);
        Assert.Equal("ReleaseOS", doc.Information.Creator);
        Assert.Equal(612, (int)doc.GetPage(1).Width);   // Letter
        // the same data renders the same file
        Assert.Equal(bytes, await c.GetByteArrayAsync($"/api/v1/analytics/portfolio.pdf?{Window}"));
    }

    [Fact]
    public async Task The_portfolio_pdf_covers_only_the_workspaces_the_person_belongs_to()
    {
        var (c, host) = await PdfClient("Community", "pdf-two@x.com", "default", "ws-cards");
        using var _ = host;
        var text = Text(await c.GetByteArrayAsync($"/api/v1/analytics/portfolio.pdf?{Window}"));   // no workspaces named: all of the person's own
        Assert.Contains("Cards", text);
        Assert.Contains("2 of your 2 workspace(s)", text);
        Assert.DoesNotContain("Payments", text);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/portfolio.pdf?{Window}&workspaces=payments")).StatusCode);
        var one = Text(await c.GetByteArrayAsync($"/api/v1/analytics/portfolio.pdf?{Window}&workspaces=cards"));
        Assert.Contains("1 of your 2 workspace(s)", one);
        Assert.DoesNotContain("Default", one);
    }

    [Fact]
    public async Task The_portfolio_pdf_prints_the_figures_of_the_metrics()
    {
        var (c, host) = await PdfClient("Community", "pdf-fig@x.com", "default", "ws-cards", "ws-pay");
        using var _ = host;
        var text = Text(await c.GetByteArrayAsync($"/api/v1/analytics/portfolio.pdf?{Window}"));
        var m17 = JsonDocument.Parse(await c.GetStringAsync($"/api/v1/analytics/M17?{Window}&workspaces=all")).RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var completed = m17.Sum(r => r.GetProperty("completed").GetInt64());
        Assert.Matches($@"Releases completed\s*{completed}(?!\d)", text);   // the text layer puts no space between a label and its value
        foreach (var r in m17.Where(r => r.GetProperty("track").GetString() is "CERT" or "Production"))
            Assert.Contains($"{r.GetProperty("onTimePct").GetDouble():0.0} %", text);   // on-time % of CERT and of Production, each on its own
        var failed = m17.Sum(r => r.GetProperty("changeFailed").GetInt64());
        Assert.Contains($"({failed} of {completed})", text);
        var risk = JsonDocument.Parse(await c.GetStringAsync($"/api/v1/analytics/M21?{Window}&workspaces=all")).RootElement.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("openReleases").GetInt64());
        Assert.Matches($@"Open releases\s*{risk}(?!\d)", text);
        var exceptions = JsonDocument.Parse(await c.GetStringAsync($"/api/v1/analytics/M18?{Window}&workspaces=all")).RootElement.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("events").GetInt64());
        Assert.Matches($@"Exceptions\s*{exceptions}(?!\d)", text);
        Assert.Contains("Waiver granted", text);
        Assert.Contains("Compliance gate certified late", text);
    }

    [Fact]
    public async Task Without_a_licence_the_portfolio_pdf_is_the_same_readable_422_as_the_train_exports()
    {
        var (c, host) = await PdfClient(null, "pdf-nolic@x.com", "default");
        using var _ = host;
        var res = await c.GetAsync($"/api/v1/analytics/portfolio.pdf?{Window}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ExportLicence", body.GetProperty("guard").GetString());
        Assert.Contains("Pdf:QuestPdfLicense", body.GetProperty("message").GetString());
    }
}
