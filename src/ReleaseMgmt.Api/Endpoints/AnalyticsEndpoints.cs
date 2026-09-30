using System.Globalization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Analytics;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Analytics endpoints (REOS-46; PROJECT_SCOPE sections 6 and 8): <c>GET /analytics</c> (index) and <c>GET /analytics/{metric}</c> for M1..M15
/// (also by query name, e.g. <c>on_time_rate</c>). Any signed-in role may read (scope role table: "Read trains, gates, runbooks, analytics").
/// Read-only; the SQL is db/analytics.sql with bound parameters. Decisions in Q-046a..f.
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>Default window when from/to are omitted: the 180 days up to and including the as-of date (Q-046d).</summary>
    public const int DefaultWindowDays = 180;

    private static readonly string[] UnsupportedFilters = ["train", "product", "riskTier", "gateClass"];

    public static void MapAnalytics(this RouteGroupBuilder api)
    {
        // GET /analytics -> [{ id, name, title, unit, columns:[{name,unit}] }]
        api.MapGet("/analytics", () => Results.Ok(AnalyticsService.Catalog.Select(m => new { m.Id, m.Name, m.Title, m.Unit, m.Columns })))
            .RequireAuthorization(Policies.Read);

        // GET /analytics/{metric}?from&to&now -> { metric, name, title, unit, columns, from, to, asOf, rows[] }
        api.MapGet("/analytics/{metric}", async (string metric, HttpRequest req, AnalyticsService svc, TimeProvider time, CancellationToken ct) =>
        {
            var info = AnalyticsService.Find(metric);
            if (info is null) return Results.NotFound(new { message = $"Unknown metric {metric}; use M1..M15 (GET /api/v1/analytics lists them)" });
            if (!TryWindow(req, time, out var w, out var bad)) return bad!;
            var rows = await svc.RunAsync(info.Id, w, ct);
            return Results.Ok(new { metric = info.Id, name = info.Name, title = info.Title, unit = info.Unit, columns = info.Columns, from = w.FromText, to = w.ToText, asOf = w.NowText, rows });
        }).RequireAuthorization(Policies.Read);
    }

    private static IResult Bad(string message) => Results.Json(new { guard = "InvalidFilter", message }, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryWindow(HttpRequest req, TimeProvider time, out AnalyticsWindow w, out IResult? bad)
    {
        w = null!; bad = null;
        string? Get(string k) => req.Query[k].ToString() is { Length: > 0 } v ? v.Trim() : null;
        foreach (var k in UnsupportedFilters)
            if (Get(k) is not null) { bad = Bad($"The {k} filter is not supported: the analytics queries are release-wide (Q-046e)"); return false; }

        DateTime now;
        if (Get("now") is { } n)
        {
            if (!DateTimeOffset.TryParse(n, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)) { bad = Bad("now is a UTC instant such as 2026-09-28T12:00:00Z"); return false; }
            now = o.UtcDateTime;
        }
        else now = time.GetUtcNow().UtcDateTime;

        var today = DateOnly.FromDateTime(now);
        DateOnly from, to;
        if (Get("from") is { } f) { if (!TryDate(f, out from)) { bad = Bad("from is a date such as 2026-01-01"); return false; } }
        else from = DefaultFrom(today);
        if (Get("to") is { } t) { if (!TryDate(t, out to)) { bad = Bad("to is a date such as 2026-12-31"); return false; } }
        else to = today;
        if (from > to) { bad = Bad("from must not be after to"); return false; }
        w = new AnalyticsWindow(from, to, now);
        return true;

        static DateOnly DefaultFrom(DateOnly d) => d.AddDays(-DefaultWindowDays);
        static bool TryDate(string s, out DateOnly d) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }
}
