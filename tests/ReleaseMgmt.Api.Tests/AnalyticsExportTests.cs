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
    public static IEnumerable<object[]> Metrics() => Enumerable.Range(1, 15).Select(i => new object[] { $"M{i}" });
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
}
