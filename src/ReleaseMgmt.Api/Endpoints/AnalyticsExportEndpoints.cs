using System.Text.Json;
using ClosedXML.Excel;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Analytics;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-47: <c>GET /api/v1/analytics/{metric}/xlsx?from&amp;to&amp;now</c> returns the same rows as the JSON endpoint as a workbook (Q-047b). Same window rules, same roles
/// (any signed-in role reads, anonymous 401), read-only. CSV is produced in the browser from the JSON the screen already holds (Q-047c), so only XLSX needs a server.
/// </summary>
public static class AnalyticsExportEndpoints
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapAnalyticsExport(this RouteGroupBuilder api)
    {
        api.MapGet("/analytics/{metric}/xlsx", async (string metric, HttpRequest req, AnalyticsService svc, TimeProvider time, CancellationToken ct) =>
        {
            var info = AnalyticsService.Find(metric);
            if (info is null) return Results.NotFound(new { message = $"Unknown metric {metric}; use M1..M15 (GET /api/v1/analytics lists them)" });
            if (!AnalyticsEndpoints.TryWindow(req, time, out var w, out var bad)) return bad!;
            var rows = await svc.RunAsync(info.Id, w, ct);
            var bytes = Workbook(info, w, rows);
            var snapshot = info.Id is "M6" or "M9" or "M14";
            var name = $"analytics-{info.Id.ToLowerInvariant()}-{(snapshot ? "asof-" + w.ToText : w.FromText + "_" + w.ToText)}.xlsx";
            req.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, XlsxContentType, name);
        }).RequireAuthorization(Policies.Read);
    }

    /// <summary>Header text shared with the screen's CSV and data table: "median_h (hours)".</summary>
    public static string Header(MetricColumn c) => c.Unit.Length > 0 ? $"{c.Name} ({c.Unit})" : c.Name;

    /// <summary>
    /// Sheet 1 = the metric (header row then one row per record, values by position in SQL column order); sheet 2 = the window and as-of. Text is always stored as a text cell
    /// (never parsed into a formula), so a value such as =HYPERLINK(...) is data, not a formula (OWASP CSV/formula injection). NULL is an empty cell.
    /// </summary>
    public static byte[] Workbook(MetricInfo info, AnalyticsWindow w, IReadOnlyList<object> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(info.Id);
        for (var c = 0; c < info.Columns.Count; c++) ws.Cell(1, c + 1).Value = Header(info.Columns[c]);
        ws.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            var c = 1;
            foreach (var p in JsonSerializer.SerializeToElement(row).EnumerateObject())
            {
                var cell = ws.Cell(r, c++);
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Number: cell.Value = p.Value.GetDouble(); break;
                    case JsonValueKind.String: cell.Value = p.Value.GetString()!; break;
                    case JsonValueKind.True or JsonValueKind.False: cell.Value = p.Value.GetBoolean(); break;
                    default: break; // null: leave the cell empty
                }
            }
            r++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        var about = wb.AddWorksheet("About");
        (string, string)[] meta = [("Metric", $"{info.Id} {info.Title}"), ("Unit", info.Unit), ("From", w.FromText), ("To", w.ToText), ("Data as of", w.NowText)];
        for (var i = 0; i < meta.Length; i++) { about.Cell(i + 1, 1).Value = meta[i].Item1; about.Cell(i + 1, 1).Style.Font.Bold = true; about.Cell(i + 1, 2).Value = meta[i].Item2; }
        about.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
