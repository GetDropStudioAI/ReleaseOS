using ReleaseMgmt.Domain.Entities;
using System.Text.Json;
using ClosedXML.Excel;
using ReleaseMgmt.Api.Auth;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Exports.Documents;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-47: <c>GET /api/v1/analytics/{metric}/xlsx?from&amp;to&amp;now</c> (and, REOS-121, <c>workspaces</c> and <c>groupBy</c>) returns the same rows as the JSON endpoint as a workbook (Q-047b). Same window rules, same roles
/// (any signed-in role reads, anonymous 401), read-only. CSV is produced in the browser from the JSON the screen already holds (Q-047c), so only XLSX needs a server.
/// </summary>
public static class AnalyticsExportEndpoints
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapAnalyticsExport(this RouteGroupBuilder api)
    {
        api.MapGet("/analytics/{metric}/xlsx", async (string metric, HttpRequest req, AnalyticsService svc, TimeProvider time, AnalyticsScope scope, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var info = AnalyticsService.Find(metric);
            if (info is null) return Results.NotFound(new { message = $"Unknown metric {metric}; use M1..M24 (GET /api/v1/analytics lists them)" });
            var r = await AnalyticsEndpoints.WindowAsync(req, time, scope, ct);
            if (r.Bad is not null) return r.Bad;
            var w = r.Window!;
            if (w.GroupBy.Count > 0 && !info.Grouped) return AnalyticsEndpoints.Bad($"groupBy applies to the executive metrics M16 to M24; {info.Id} is not grouped");
            var rows = await svc.RunAsync(info.Id, w, ct);
            // REOS-137: the workbook says which workspace it is from and, for the per-release metrics, which release each row is (key, owner, team, environments)
            await using var db = await dbf.CreateDbContextAsync(ct);
            var workspace = await WorkspaceTextAsync(db, req, r, ct);
            var releases = HasTitle(info) && r.Workspaces.Count == 1 ? await ReleaseFacts.LoadAsync(db, null, ct) : null;   // a title names a release only within one workspace
            var bytes = Workbook(info, w, rows, workspace, releases, await TypeNameAsync(db, w, ct));
            var name = $"analytics-{info.Id.ToLowerInvariant()}-{(info.Snapshot ? "asof-" + w.ToText : w.FromText + "_" + w.ToText)}.xlsx";
            req.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, XlsxContentType, name);
        }).RequireAuthorization(Policies.Read);

        // REOS-141 (G19): every metric in one workbook. A literal segment, so it never collides with /analytics/{metric}.
        api.MapGet("/analytics/xlsx", async (HttpRequest req, AnalyticsService svc, TimeProvider time, DisplayClock clock, AnalyticsScope scope, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var r = await AnalyticsEndpoints.WindowAsync(req, time, scope, ct);
            if (r.Bad is not null) return r.Bad;
            var w = r.Window!;
            var results = new List<(MetricInfo Info, IReadOnlyList<object> Rows)>();
            foreach (var info in AnalyticsService.Catalog) results.Add((info, await svc.RunAsync(info.Id, w, ct)));
            await using var db = await dbf.CreateDbContextAsync(ct);
            var workspace = await WorkspaceTextAsync(db, req, r, ct);
            var releases = r.Workspaces.Count == 1 ? await ReleaseFacts.LoadAsync(db, null, ct) : null;
            var bytes = FullWorkbook(results, w, workspace, releases, time.GetUtcNow().UtcDateTime, clock, await TypeNameAsync(db, w, ct), r.Workspaces.Count);
            req.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, XlsxContentType, $"analytics-workbook-{w.FromText}_{w.ToText}.xlsx");
        }).RequireAuthorization(Policies.Read);

        // REOS-121: the portfolio report, one PDF for the workspaces asked for (all the person's own by default). Same page identity and UTC rule as the train PDFs (REOS-137, D51).
        // Rendered in the request (a handful of tables, no queue): a licence that is not set is the same readable 422 as the train exports.
        api.MapGet("/analytics/portfolio.pdf", async (HttpRequest req, AnalyticsService svc, TimeProvider time, AnalyticsScope scope, PortfolioReportBuilder builder, DisplayClock clock, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var r = await AnalyticsEndpoints.WindowAsync(req, time, scope, ct, defaultAll: true);
            if (r.Bad is not null) return r.Bad;
            var w = r.Window! with { GroupBy = [] };
            var mine = await scope.MineAsync(req.HttpContext, ct);
            var uid = req.HttpContext.User.FindFirst("uid")?.Value;
            string by;
            await using (var db = await dbf.CreateDbContextAsync(ct))
                by = uid is null ? "unknown" : await db.Set<Users>().AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? uid;
            var now = time.GetUtcNow().UtcDateTime;
            var report = await builder.BuildAsync(w, [.. r.Workspaces.Select(x => x.Name)], mine.Count, now, by, clock, ct);
            byte[] pdf;
            try { pdf = builder.Render(report); }
            catch (ExportConfigException ex) { return Results.Json(new { guard = ExportGuards.Licence, message = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity); }
            req.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(pdf, "application/pdf", $"portfolio-report-{w.FromText}_{w.ToText}.pdf");
        }).RequireAuthorization(Policies.Read);
    }

    /// <summary>The workspace line of a workbook: the current workspace's name as before, or the names of the workspaces the request asked for.</summary>
    private static async Task<string> WorkspaceTextAsync(ReleaseDbContext db, HttpRequest req, AnalyticsEndpoints.Resolved r, CancellationToken ct) =>
        req.Query.ContainsKey(AnalyticsScope.Query) ? string.Join(", ", r.Workspaces.Select(x => x.Name)) : await ReleaseFacts.WorkspaceNameAsync(db, ct);

    /// <summary>REOS-116: the name of the release type the window is limited to (the id when it no longer exists), or null when it is not limited.</summary>
    private static async Task<string?> TypeNameAsync(ReleaseDbContext db, AnalyticsWindow w, CancellationToken ct) =>
        w.ReleaseTypeId is null ? null : await db.Set<ReleaseTypes>().AsNoTracking().Where(x => x.Id == w.ReleaseTypeId).Select(x => x.Name).SingleOrDefaultAsync(ct) ?? w.ReleaseTypeId;

    private static bool HasTitle(MetricInfo info) => info.Columns.Any(c => c.Name == "Title");

    private static string CarryText(bool perRelease) => perRelease ? "One row per release; key, owner, team and environments are in the last four columns" : "All releases in the workspace";

    /// <summary>Header text shared with the screen's CSV and data table: "median_h (hours)".</summary>
    public static string Header(MetricColumn c) => c.Unit.Length > 0 ? $"{c.Name} ({c.Unit})" : c.Name;

    /// <summary>
    /// One metric's sheet, named by its id: header row (frozen, bold), then one row per record by position in SQL column order, and for the per-release metrics the four release columns.
    /// Shared by the per-metric download and the workbook, so the two cannot disagree.
    /// </summary>
    internal static IXLWorksheet AddMetricSheet(XLWorkbook wb, MetricInfo info, IReadOnlyList<object> rows, IReadOnlyDictionary<string, ReleaseFact>? releases)
    {
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
        return ws;
    }

    /// <summary>
    /// Sheet 1 = the metric (header row then one row per record, values by position in SQL column order); sheet 2 = the window and as-of. Text is always stored as a text cell
    /// (never parsed into a formula), so a value such as =HYPERLINK(...) is data, not a formula (OWASP CSV/formula injection). NULL is an empty cell.
    /// </summary>
    public static byte[] Workbook(MetricInfo info, AnalyticsWindow w, IReadOnlyList<object> rows, string workspace = "", IReadOnlyDictionary<string, ReleaseFact>? releases = null, string? releaseType = null)
    {
        using var wb = new XLWorkbook();
        AddMetricSheet(wb, info, rows, releases);
        var titleAt = HasTitle(info) ? info.Columns.ToList().FindIndex(c => c.Name == "Title") : -1;

        var about = wb.AddWorksheet("About");
        (string, string)[] meta = [("Metric", $"{info.Id} {info.Title}"), ("Unit", info.Unit), ("From", w.FromText), ("To", w.ToText), ("Data as of", w.NowText), ("Workspace", workspace),
            ("Releases", CarryText(titleAt >= 0))];
        if (releaseType is not null) meta = [.. meta, ("Release type", releaseType)];   // REOS-116: only when the export is limited to one type
        if (info.Grouped) meta = [.. meta, ("Grouped by", GroupText(w)), ("Quarters", QuarterText(w))];   // REOS-121
        for (var i = 0; i < meta.Length; i++) { about.Cell(i + 1, 1).Value = meta[i].Item1; about.Cell(i + 1, 1).Style.Font.Bold = true; about.Cell(i + 1, 2).Value = meta[i].Item2; }
        about.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>What the executive metrics were grouped by, in words (REOS-121).</summary>
    private static string GroupText(AnalyticsWindow w) => w.GroupBy.Count == 0 ? "Nothing: one row per measure for the whole scope" : string.Join(", ", w.GroupBy);

    private static string QuarterText(AnalyticsWindow w) => w.FiscalYearStartMonth == 1 ? "Calendar quarters (2026-Q3 = July to September)" : $"Fiscal quarters, the year starts in month {w.FiscalYearStartMonth} (FY2027-Q1 = the first quarter of the year that ends in 2027)";

    /// <summary>
    /// REOS-141 (G19): the whole analytics set as one workbook. Sheet 1 "Parameters" says what the file is (workspace, period, filters, data as-of, generated-at in UTC with the display zone beside it, D51)
    /// and lists the metric sheets; then one sheet per metric (M1..M24), each built by <see cref="AddMetricSheet"/>, the same code as the per-metric download, from the same query run
    /// with the same window, so every sheet holds exactly the per-metric export's rows. Text is stored as text, never as a formula (Q-047b). <paramref name="generatedAt"/> comes from the injected clock.
    /// </summary>
    public static byte[] FullWorkbook(IReadOnlyList<(MetricInfo Info, IReadOnlyList<object> Rows)> metrics, AnalyticsWindow w, string workspace, IReadOnlyDictionary<string, ReleaseFact>? releases, DateTime generatedAt, DisplayClock clock, string? releaseType = null,
        int workspaceCount = 1)
    {
        using var wb = new XLWorkbook();
        var fmt = new PdfFmt(clock);
        var ps = wb.AddWorksheet("Parameters");
        string Utc(DateTime t) => $"{DateTime.SpecifyKind(t, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC";
        (string Name, string Value, string Beside)[] meta =
        [
            ("Workbook", "ReleaseOS analytics", ""),
            (workspaceCount > 1 ? "Workspaces" : "Workspace", workspace, ""),
            ("Generated at (UTC)", Utc(generatedAt), fmt.Beside(generatedAt)),
            ("Display zone", clock.Zone.Id, ""),
            ("Period from (UTC day)", w.FromText, ""),
            ("Period to (UTC day)", w.ToText, ""),
            ("Data as of (UTC)", Utc(w.Now), fmt.Beside(w.Now)),
            ("Filters", releaseType is null ? workspaceCount > 1 ? "None: every metric reads the whole of each workspace. Product, risk tier and gate class are not supported (Q-046e)" : "None: every metric reads the whole workspace. Product, risk tier and gate class are not supported (Q-046e)" : $"Release type: {releaseType}. M11 (freeze windows) has no type. Product, risk tier and gate class are not supported (Q-046e)", ""),
            ("Period", "M6, M9, M14 and M21 are as of now and ignore the period", ""),
            ("Grouped by", $"{GroupText(w)} (M16 to M24; M1 to M15 are not grouped)", ""),
            ("Quarters", QuarterText(w), ""),
            ("Scope", workspaceCount > 1 ? "Only workspaces you are a member of are included (D49). The release columns (key, owner, team, environments) are given only when one workspace is read" : "One workspace", ""),
        ];
        for (var i = 0; i < meta.Length; i++)
        {
            ps.Cell(i + 1, 1).Value = meta[i].Name; ps.Cell(i + 1, 1).Style.Font.Bold = true;
            ps.Cell(i + 1, 2).Value = meta[i].Value;
            if (meta[i].Beside.Length > 0) ps.Cell(i + 1, 3).Value = meta[i].Beside;
        }
        var head = meta.Length + 2;
        string[] heads = ["Sheet", "Metric", "Unit", "Rows", "Period", "Releases"];
        for (var c = 0; c < heads.Length; c++) { ps.Cell(head, c + 1).Value = heads[c]; ps.Cell(head, c + 1).Style.Font.Bold = true; }
        var r = head + 1;
        foreach (var (info, rows) in metrics)
        {
            AddMetricSheet(wb, info, rows, releases);
            ps.Cell(r, 1).Value = info.Id; ps.Cell(r, 2).Value = info.Title; ps.Cell(r, 3).Value = info.Unit; ps.Cell(r, 4).Value = rows.Count;
            ps.Cell(r, 5).Value = info.Snapshot ? "As of now" : "In the period";
            ps.Cell(r, 6).Value = CarryText(HasTitle(info));
            r++;
        }
        ps.SheetView.FreezeRows(head);
        ps.Columns(1, 6).AdjustToContents();
        ps.Column(2).Width = Math.Min(ps.Column(2).Width, 60);
        ps.Position = 1;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
