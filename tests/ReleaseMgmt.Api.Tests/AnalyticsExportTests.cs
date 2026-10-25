using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Analytics;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-47: GET /api/v1/analytics/{m}/xlsx. Runs on the same seed copy and fixed clock as AnalyticsTests. The workbook must hold exactly the rows the JSON endpoint returns,
/// text must be stored as text (never a formula), any signed-in role may read, anonymous is 401.
/// </summary>
public class AnalyticsExportTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    public static IEnumerable<object[]> Metrics() => Enumerable.Range(1, 24).Select(i => new object[] { $"M{i}" });
    private const string Q = "from=2026-01-01&to=2026-12-31&now=2026-09-28T12%3A00%3A00Z";

    private async Task<HttpClient> As(string role)
    {
        var c = fx.Web.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = $"{role}-x@x.com", name = role, role })).EnsureSuccessStatusCode();
        return c;
    }

    [Theory, MemberData(nameof(Metrics))]
    public async Task Workbook_holds_exactly_the_json_rows(string key)
    {
        var c = await As(Roles.Viewer);
        var json = JsonDocument.Parse(await c.GetStringAsync($"/api/v1/analytics/{key}?{Q}")).RootElement;
        var res = await c.GetAsync($"/api/v1/analytics/{key}/xlsx?{Q}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(AnalyticsExportEndpoints.XlsxContentType, res.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".xlsx", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(key);
        var cols = json.GetProperty("columns").EnumerateArray().ToArray();
        for (var i = 0; i < cols.Length; i++)
        {
            var name = cols[i].GetProperty("name").GetString()!; var unit = cols[i].GetProperty("unit").GetString()!;
            Assert.Equal(unit.Length > 0 ? $"{name} ({unit})" : name, ws.Cell(1, i + 1).GetString());
        }
        var rows = json.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(rows.Length, ws.LastRowUsed()!.RowNumber() - 1);
        for (var r = 0; r < rows.Length; r++)
        {
            var props = rows[r].EnumerateObject().ToArray();
            for (var c2 = 0; c2 < props.Length; c2++)
            {
                var cell = ws.Cell(r + 2, c2 + 1); var v = props[c2].Value;
                switch (v.ValueKind)
                {
                    case JsonValueKind.Null: Assert.True(cell.IsEmpty(), $"{key} r{r} c{c2} should be empty"); break;
                    case JsonValueKind.Number: Assert.Equal(v.GetDouble(), cell.GetDouble(), 9); break;
                    default: Assert.Equal(v.GetString(), cell.GetString()); Assert.Equal(XLDataType.Text, cell.DataType); break;
                }
            }
        }
        Assert.Equal("2026-09-28T12:00:00Z", wb.Worksheet("About").Cell(5, 2).GetString());
    }

    [Fact]
    public void Formula_like_text_is_stored_as_text_not_a_formula()
    {
        var info = AnalyticsService.Find("M11")!;
        var w = new AnalyticsWindow(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), DateTime.Parse("2026-09-28T12:00:00Z").ToUniversalTime());
        var evil = new[] { "=HYPERLINK(\"http://evil\",\"x\")", "+1+1", "-2+3", "@SUM(A1)", "\t=cmd" };
        var bytes = AnalyticsExportEndpoints.Workbook(info, w, evil.Select(e => (object)new M11Row(e, 1, null)).ToList());
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("M11");
        for (var i = 0; i < evil.Length; i++)
        {
            var cell = ws.Cell(i + 2, 1);
            Assert.False(cell.HasFormula, evil[i]);
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal(evil[i], cell.GetString());
            Assert.True(ws.Cell(i + 2, 3).IsEmpty());   // NULL stays an empty cell
        }
    }

    [Theory, InlineData(Roles.Viewer), InlineData(Roles.RTE), InlineData(Roles.ReleaseManager), InlineData(Roles.GovernanceOfficer)]
    public async Task Any_signed_in_role_may_read(string role)
    {
        Assert.Equal(HttpStatusCode.OK, (await (await As(role)).GetAsync($"/api/v1/analytics/M1/xlsx?{Q}")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Web.CreateClient().GetAsync($"/api/v1/analytics/M1/xlsx?{Q}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_metric_is_404_and_a_bad_window_is_400()
    {
        var c = await As(Roles.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/analytics/M99/xlsx")).StatusCode);
        var bad = await c.GetAsync("/api/v1/analytics/M2/xlsx?from=2026-12-31&to=2026-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("InvalidFilter", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/analytics/M2/xlsx?product=x")).StatusCode);
    }

    [Fact]
    public async Task The_default_window_works_and_the_file_is_named_for_it()
    {
        var res = await (await As(Roles.Viewer)).GetAsync("/api/v1/analytics/M2/xlsx");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("analytics-m2-2026-04-01_2026-09-28.xlsx", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }

    // ---- REOS-141 (G19): the one-workbook download ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_workbook_has_a_parameters_sheet_first_then_one_sheet_per_metric_equal_to_its_own_export()
    {
        var c = await As(Roles.Viewer);
        var res = await c.GetAsync($"/api/v1/analytics/xlsx?{Q}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(AnalyticsExportEndpoints.XlsxContentType, res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("analytics-workbook-2026-01-01_2026-12-31.xlsx", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        Assert.Equal(["Parameters", .. Enumerable.Range(1, 24).Select(i => $"M{i}")], wb.Worksheets.Select(w => w.Name).ToArray());
        foreach (var id in Enumerable.Range(1, 24).Select(i => $"M{i}"))
        {
            using var one = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/{id}/xlsx?{Q}"));
            WorkbookAssert.SameCells(one.Worksheet(id), wb.Worksheet(id), id);
        }
        var rowsOn = wb.Worksheet("Parameters").RowsUsed().Where(r => r.Cell(1).GetString() is ['M', ..]).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(4).GetDouble());
        Assert.Equal(24, rowsOn.Count);
        foreach (var (id, n) in rowsOn) Assert.Equal(wb.Worksheet(id).LastRowUsed()!.RowNumber() - 1, (int)n);
    }

    [Fact]
    public async Task The_parameters_sheet_gives_workspace_period_filters_and_generated_at_in_utc_with_the_display_zone_beside_it()
    {
        var c = await As(Roles.Viewer);
        using var wb = new XLWorkbook(await c.GetStreamAsync($"/api/v1/analytics/xlsx?{Q}"));
        var p = wb.Worksheet("Parameters");
        string V(string name) => p.RowsUsed().First(r => r.Cell(1).GetString() == name).Cell(2).GetString();
        string Beside(string name) => p.RowsUsed().First(r => r.Cell(1).GetString() == name).Cell(3).GetString();
        Assert.Equal("2026-09-28 12:00 UTC", V("Generated at (UTC)"));   // the injected clock
        Assert.Equal("07:00 CT", Beside("Generated at (UTC)"));        // CDT in September
        Assert.Equal("04:09 PT", Beside2(new DateTime(2026, 11, 2, 12, 9, 0, DateTimeKind.Utc), "America/Los_Angeles"));
        Assert.Equal("2026-01-01", V("Period from (UTC day)"));
        Assert.Equal("2026-12-31", V("Period to (UTC day)"));
        Assert.Equal("2026-09-28 12:00 UTC", V("Data as of (UTC)"));
        Assert.Equal("America/Chicago", V("Display zone"));
        Assert.False(string.IsNullOrEmpty(V("Workspace")));
        Assert.Contains("None", V("Filters"));
        Assert.All(p.RowsUsed().Where(r => r.Cell(1).GetString() is ['M', ..]), r => Assert.Equal(r.Cell(1).GetString() is "M6" or "M9" or "M14" or "M21" ? "As of now" : "In the period", r.Cell(5).GetString()));
    }

    private static string Beside2(DateTime generated, string zone)
    {
        var w = new AnalyticsWindow(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), generated);
        var bytes = AnalyticsExportEndpoints.FullWorkbook([], w, "W", null, generated, new ReleaseMgmt.Infrastructure.Reminders.DisplayClock(TimeZoneInfo.FindSystemTimeZoneById(zone)));
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        return wb.Worksheet("Parameters").Cell(3, 3).GetString();
    }

    [Theory, InlineData(Roles.Viewer), InlineData(Roles.RTE), InlineData(Roles.ReleaseManager), InlineData(Roles.GovernanceOfficer)]
    public async Task Any_signed_in_role_may_download_the_workbook(string role)
    {
        Assert.Equal(HttpStatusCode.OK, (await (await As(role)).GetAsync($"/api/v1/analytics/xlsx?{Q}")).StatusCode);
    }

    [Fact]
    public async Task The_workbook_is_401_anonymous_400_on_a_bad_window_and_defaults_to_the_usual_window()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Web.CreateClient().GetAsync($"/api/v1/analytics/xlsx?{Q}")).StatusCode);
        var c = await As(Roles.Viewer);
        var bad = await c.GetAsync("/api/v1/analytics/xlsx?from=2026-12-31&to=2026-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("InvalidFilter", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/analytics/xlsx?product=x")).StatusCode);
        var ok = await c.GetAsync("/api/v1/analytics/xlsx");
        Assert.Equal("analytics-workbook-2026-04-01_2026-09-28.xlsx", ok.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public void Formula_like_text_in_the_workspace_name_and_in_rows_is_stored_as_text_in_the_workbook()
    {
        var w = new AnalyticsWindow(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), DateTime.Parse("2026-09-28T12:00:00Z").ToUniversalTime());
        var evil = new[] { "=HYPERLINK(\"http://evil\",\"x\")", "+1+1", "-2+3", "@SUM(A1)" };
        IReadOnlyList<(MetricInfo, IReadOnlyList<object>)> metrics = [(AnalyticsService.Find("M11")!, evil.Select(e => (object)new M11Row(e, 1, null)).ToList())];
        var bytes = AnalyticsExportEndpoints.FullWorkbook(metrics, w, "=cmd|' /C calc'!A0", null, w.Now, new ReleaseMgmt.Infrastructure.Reminders.DisplayClock(TimeZoneInfo.Utc));
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        foreach (var ws in wb.Worksheets) foreach (var cell in ws.CellsUsed()) Assert.False(cell.HasFormula, $"{ws.Name}!{cell.Address}");
        Assert.Equal("=cmd|' /C calc'!A0", wb.Worksheet("Parameters").Cell(2, 2).GetString());
        Assert.Equal(XLDataType.Text, wb.Worksheet("Parameters").Cell(2, 2).DataType);
        for (var i = 0; i < evil.Length; i++) Assert.Equal(evil[i], wb.Worksheet("M11").Cell(i + 2, 1).GetString());
        Assert.True(wb.Worksheet("Parameters").Cell(3, 3).IsEmpty(), "no second reading when the display zone is UTC (D51)");
    }
}

/// <summary>Cell-for-cell comparison of two sheets: same used range, same type and value in every cell.</summary>
internal static class WorkbookAssert
{
    public static void SameCells(IXLWorksheet expected, IXLWorksheet actual, string what)
    {
        Assert.Equal(expected.LastRowUsed()?.RowNumber() ?? 0, actual.LastRowUsed()?.RowNumber() ?? 0);
        Assert.Equal(expected.LastColumnUsed()?.ColumnNumber() ?? 0, actual.LastColumnUsed()?.ColumnNumber() ?? 0);
        foreach (var cell in expected.CellsUsed())
        {
            var other = actual.Cell(cell.Address);
            Assert.True(cell.DataType == other.DataType, $"{what}!{cell.Address} type {cell.DataType} vs {other.DataType}");
            Assert.True(cell.DataType == XLDataType.Number ? cell.GetDouble() == other.GetDouble() : cell.GetString() == other.GetString(), $"{what}!{cell.Address}: '{cell.GetString()}' vs '{other.GetString()}'");
        }
        Assert.Equal(expected.CellsUsed().Count(), actual.CellsUsed().Count());
    }
}
