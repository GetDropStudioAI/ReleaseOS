using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Infrastructure.Exchange;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Grid exports (PROJECT_SCOPE 9, REOS-49): <c>GET /exports/{grid}.csv</c> and <c>.xlsx</c> (also under <c>/export/</c>, the spelling PROJECT_SCOPE 6 uses), <c>GET /exports/grids</c> for the catalogue.
/// Any signed-in role may export a grid; the audit grid needs AuditRead like <c>/audit.csv</c>; notifications are always the caller's own. Filters: train (id or title), status (trains), open (blockers, notifications),
/// and the audit filter set. Files are attachments with <c>nosniff</c> and <c>no-store</c>; a cut at <c>Export:MaxRows</c> (default 50,000) is announced in headers and a final row.
/// </summary>
public static class ExportEndpoints
{
    public const string TotalHeader = "X-Export-Total-Rows", TruncatedHeader = "X-Export-Truncated";
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapGridExports(this RouteGroupBuilder api)
    {
        api.MapGet("/exports/grids", () => Results.Ok(GridExportService.Grids.Select(g => new { g.Key, g.Label, g.Description, g.ImportKind, filters = g.Filters, formats = new[] { "csv", "xlsx" }, g.Policy })))
            .RequireAuthorization(Policies.Read);

        foreach (var prefix in new[] { "/exports", "/export" })
        {
            api.MapGet(prefix + "/{grid}.csv", (string grid, HttpContext http, ClaimsPrincipal u, IAuthorizationService authz, GridExportService s, IConfiguration cfg, TimeProvider time, CancellationToken ct) =>
                Export(grid, "csv", http, u, authz, s, cfg, time, ct)).RequireAuthorization(Policies.Read);
            api.MapGet(prefix + "/{grid}.xlsx", (string grid, HttpContext http, ClaimsPrincipal u, IAuthorizationService authz, GridExportService s, IConfiguration cfg, TimeProvider time, CancellationToken ct) =>
                Export(grid, "xlsx", http, u, authz, s, cfg, time, ct)).RequireAuthorization(Policies.Read);
        }
    }

    private static IResult Bad(string message) => Results.Json(new { guard = "InvalidFilter", message }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> Export(string grid, string format, HttpContext http, ClaimsPrincipal u, IAuthorizationService authz, GridExportService s, IConfiguration cfg, TimeProvider time, CancellationToken ct)
    {
        var info = GridExportService.Find(grid);
        if (info is null) return Results.NotFound(new { guard = ExchangeGuards.UnknownGrid, message = $"{grid} is not a grid" });
        if (info.Policy == Policies.AuditRead && !(await authz.AuthorizeAsync(u, Policies.AuditRead)).Succeeded) return Results.Forbid();

        var q = http.Request.Query;
        string? Get(string k) => q[k].ToString() is { Length: > 0 } v ? v : null;
        var filter = new GridFilter(Get("train"), Get("status"), Get("open") is { } o ? string.Equals(o, "true", StringComparison.OrdinalIgnoreCase) : null, u.FindFirstValue("uid"));
        AuditFilter? af = null;
        if (info.Key == "audit")
        {
            if (!AuditQueryService.TryParseBound(Get("from"), false, out var from)) return Bad("from is a UTC date (2026-10-01) or instant (2026-10-01T10:00:00Z)");
            if (!AuditQueryService.TryParseBound(Get("to"), true, out var to)) return Bad("to is a UTC date (2026-10-01) or instant (2026-10-01T10:00:00Z)");
            af = new AuditFilter(Get("train"), Get("entity"), Get("entityId"), Get("actor"), Get("action"), from, to);
        }
        var max = Math.Max(1, cfg.GetValue("Export:MaxRows", GridExportService.DefaultMaxRows));
        var r = await s.TableAsync(grid, filter, af, max, ct);
        if (!r.IsOk) return r.Failures[0].Guard == "InvalidFilter" ? Bad(r.Failures[0].Message) : Results.NotFound(new { guard = r.Failures[0].Guard, message = r.Failures[0].Message });

        var table = r.Value!;
        var bytes = format == "csv" ? GridFiles.ToCsv(table) : GridFiles.ToXlsx(table);
        var res = http.Response;
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers.CacheControl = "no-store";
        res.Headers[TotalHeader] = table.Total.ToString(CultureInfo.InvariantCulture);
        res.Headers[TruncatedHeader] = table.Truncated ? "true" : "false";
        return Results.File(bytes, format == "csv" ? "text/csv; charset=utf-8" : XlsxType, $"{info.Key}-{time.GetUtcNow():yyyyMMdd-HHmmss}.{format}");
    }
}
