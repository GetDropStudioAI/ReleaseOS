using System.Text.Json;
using ClosedXML.Excel;
using ReleaseMgmt.Api.Auth;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Persistence;

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
        api.MapGet("/analytics/{metric}/xlsx", async (string metric, HttpRequest req, AnalyticsService svc, TimeProvider time, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var info = AnalyticsService.Find(metric);
            if (info is null) return Results.NotFound(new { message = $"Unknown metric {metric}; use M1..M15 (GET /api/v1/analytics lists them)" });
            if (!AnalyticsEndpoints.TryWindow(req, time, out var w, out var bad)) return bad!;
            var rows = await svc.RunAsync(info.Id, w, ct);
            // REOS-137: the workbook says which workspace it is from and, for the per-release metrics, which release each row is (key, owner, team, environments)
            await using var db = await dbf.CreateDbContextAsync(ct);
            var workspace = await ReleaseFacts.WorkspaceNameAsync(db, ct);
            var releases = HasTitle(info) ? await ReleaseFacts.LoadAsync(db, null, ct) : null;
            var bytes = Workbook(info, w, rows, workspace, releases);
            var snapshot = info.Id is "M6" or "M9" or "M14";
            var name = $"analytics-{info.Id.ToLowerInvariant()}-{(snapshot ? "asof-" + w.ToText : w.FromText + "_" + w.ToText)}.xlsx";
            req.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, XlsxContentType, name);
        }).RequireAuthorization(Policies.Read);
    }

    private static bool HasTitle(MetricInfo info) => info.Columns.Any(c => c.Name == "Title");

    private static string CarryText(bool perRelease) => perRelease ? "One row per release; key, owner, team and environments are in the last four columns" : "All releases in the workspace";

    /// <summary>Header text shared with the screen's CSV and data table: "median_h (hours)".</summary>
    public static string Header(MetricColumn c) => c.Unit.Length > 0 ? $"{c.Name} ({c.Unit})" : c.Name;

    /// <summary>
    /// Sheet 1 = the metric (header row then one row per record, values by position in SQL column order); sheet 2 = the window and as-of. Text is always stored as a text cell
    /// (never parsed into a formula), so a value such as =HYPERLINK(...) is data, not a formula (OWASP CSV/formula injection). NULL is an empty cell.
    /// </summary>
    public static byte[] Workbook(MetricInfo info, AnalyticsWindow w, IReadOnlyList<object> rows, string workspace = "", IReadOnlyDictionary<string, ReleaseFact>? releases = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(info.Id);
        for (var c = 0; c < info.Columns.Count; c++) ws.Cell(1, c + 1).Value = Header(info.Columns[c]);
        var titleAt = HasTitle(info) ? info.Columns.ToList().FindIndex(c => c.Name == "Title") : -1;
        var carry = titleAt >= 0 && releases is not null;
        string[] carried = ["release_key", "owner", "team", "environments"];
        if (carry) for (var i = 0; i < carried.Length; i++) ws.Cell(1, info.Columns.Count + 1 + i).Value = carried[i];
        ws.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            var c = 1;
            string? title = null;
            foreach (var p in JsonSerializer.SerializeToElement(row).EnumerateObject())
            {
                if (c - 1 == titleAt && p.Value.ValueKind == JsonValueKind.String) title = p.Value.GetString();
                var cell = ws.Cell(r, c++);
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Number: cell.Value = p.Value.GetDouble(); break;
                    case JsonValueKind.String: cell.Value = p.Value.GetString()!; break;
                    case JsonValueKind.True or JsonValueKind.False: cell.Value = p.Value.GetBoolean(); break;
                    default: break; // null: leave the cell empty
                }
            }
            if (carry && title is not null)
            {
                // a title names a release only when exactly one release in the workspace has it; otherwise the cells stay empty rather than guess
                var hits = releases!.Values.Where(f => f.Title == title).Take(2).ToList();
                if (hits.Count == 1)
                {
                    string?[] cells = [hits[0].Key, hits[0].Owner, hits[0].Team, hits[0].EnvironmentsText];
                    for (var i = 0; i < cells.Length; i++) if (!string.IsNullOrEmpty(cells[i])) ws.Cell(r, info.Columns.Count + 1 + i).Value = cells[i]!;
                }
            }
            r++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        var about = wb.AddWorksheet("About");
        (string, string)[] meta = [("Metric", $"{info.Id} {info.Title}"), ("Unit", info.Unit), ("From", w.FromText), ("To", w.ToText), ("Data as of", w.NowText), ("Workspace", workspace),
            ("Releases", CarryText(titleAt >= 0))];
        for (var i = 0; i < meta.Length; i++) { about.Cell(i + 1, 1).Value = meta[i].Item1; about.Cell(i + 1, 1).Style.Font.Bold = true; about.Cell(i + 1, 2).Value = meta[i].Item2; }
        about.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
