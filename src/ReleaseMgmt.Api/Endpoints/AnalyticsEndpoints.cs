using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Analytics endpoints (REOS-46; PROJECT_SCOPE sections 6 and 8): <c>GET /analytics</c> (index) and <c>GET /analytics/{metric}</c> for M1..M24
/// (also by query name, e.g. <c>on_time_rate</c>). Any signed-in role may read (scope role table: "Read trains, gates, runbooks, analytics").
/// Read-only; the SQL is db/analytics.sql with bound parameters. Decisions in Q-046a..f.
/// REOS-121 (D49): <c>workspaces=</c> reads across the person's own workspaces (never one they are not a member of), and the executive metrics M16..M24 take <c>groupBy=</c>
/// (workspace, team, type, version, segment, quarter); <c>GET /analytics/scope</c> says which workspaces can be asked for and how quarters are counted (Q-121a).
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>Default window when from/to are omitted: the 180 days up to and including the as-of date (Q-046d).</summary>
    public const int DefaultWindowDays = 180;

    private static readonly string[] UnsupportedFilters = ["train", "product", "riskTier", "gateClass"];

    public static void MapAnalytics(this RouteGroupBuilder api)
    {
        // GET /analytics -> [{ id, name, title, unit, columns:[{name,unit}], grouped, snapshot }]
        api.MapGet("/analytics", () => Results.Ok(AnalyticsService.Catalog.Select(m => new { m.Id, m.Name, m.Title, m.Unit, m.Columns, grouped = m.Grouped, snapshot = m.Snapshot })))
            .RequireAuthorization(Policies.Read);

        // GET /analytics/scope -> { workspaces:[{id,slug,name,role,current}], dimensions, fiscalYearStartMonth, quarters }: what this person may read across (D49), and how quarters are labelled
        api.MapGet("/analytics/scope", async (HttpContext http, AnalyticsScope scope, AnalyticsOptions options, CancellationToken ct) =>
        {
            var mine = await scope.MineAsync(http, ct);
            var current = WorkspaceScope.Current;
            return Results.Ok(new
            {
                workspaces = mine.Select(m => new { m.Id, m.Slug, m.Name, m.Role, current = m.Id == current }),
                dimensions = AnalyticsSql.Dimensions,
                fiscalYearStartMonth = options.FiscalYearStartMonth,
                quarters = options.FiscalYearStartMonth == 1 ? "calendar" : "fiscal",
            });
        }).RequireAuthorization(Policies.Read);

        // GET /analytics/{metric}?from&to&now&releaseTypeId&workspaces&groupBy -> { metric, name, title, unit, columns, grouped, from, to, asOf, workspaces[], groupBy[], fiscalYearStartMonth, rows[] }
        api.MapGet("/analytics/{metric}", async (string metric, HttpRequest req, AnalyticsService svc, TimeProvider time, AnalyticsScope scope, CancellationToken ct) =>
        {
            var info = AnalyticsService.Find(metric);
            if (info is null) return Results.NotFound(new { message = $"Unknown metric {metric}; use M1..M24 (GET /api/v1/analytics lists them)" });
            var r = await WindowAsync(req, time, scope, ct);
            if (r.Bad is not null) return r.Bad;
            var w = r.Window!;
            if (w.GroupBy.Count > 0 && !info.Grouped) return Bad($"groupBy applies to the executive metrics M16 to M24; {info.Id} is not grouped");
            var rows = await svc.RunAsync(info.Id, w, ct);
            return Results.Ok(new
            {
                metric = info.Id, name = info.Name, title = info.Title, unit = info.Unit, columns = info.Columns, grouped = info.Grouped, snapshot = info.Snapshot, from = w.FromText, to = w.ToText, asOf = w.NowText,
                workspaces = r.Workspaces, groupBy = w.GroupBy, fiscalYearStartMonth = w.FiscalYearStartMonth, rows,
            });
        }).RequireAuthorization(Policies.Read);
    }

    /// <summary>The scope of one workspace shown in a response: what the figures include.</summary>
    public sealed record ScopeWorkspace(string Id, string Slug, string Name);

    /// <summary>The window of a request with its workspaces resolved against the person's memberships (404 for any other), or the response to send.</summary>
    internal sealed record Resolved(AnalyticsWindow? Window, IReadOnlyList<ScopeWorkspace> Workspaces, IResult? Bad);

    internal static async Task<Resolved> WindowAsync(HttpRequest req, TimeProvider time, AnalyticsScope scope, CancellationToken ct, bool defaultAll = false)
    {
        if (!TryWindow(req, time, out var w, out var bad)) return new(null, [], bad);
        var (chosen, error) = await scope.ResolveAsync(req.HttpContext, ct);
        if (error is not null) return new(null, [], error);
        if (chosen is null && defaultAll) chosen = await scope.MineAsync(req.HttpContext, ct);   // a portfolio report with no workspaces named covers every workspace of the person's
        if (chosen is null)   // the current workspace, as every request read before
        {
            var here = (await scope.MineAsync(req.HttpContext, ct)).FirstOrDefault(m => m.Id == w.WorkspaceId);
            return new(w, [new(w.WorkspaceId, here?.Slug ?? "", here?.Name ?? "")], null);
        }
        // a person who is a member of none reads a workspace that has no rows, not every workspace
        var ids = chosen.Count == 0 ? [WorkspaceResolution.Nowhere] : chosen.Select(c => c.Id).ToList();
        return new(w with { Workspaces = ids }, [.. chosen.Select(c => new ScopeWorkspace(c.Id, c.Slug, c.Name))], null);
    }

    internal static IResult Bad(string message) => Results.Json(new { guard = "InvalidFilter", message }, statusCode: StatusCodes.Status400BadRequest);

    internal static bool TryWindow(HttpRequest req, TimeProvider time, out AnalyticsWindow w, out IResult? bad)
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
        var by = new List<string>();
        foreach (var d in (Get("groupBy") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dim = AnalyticsSql.Dimensions.FirstOrDefault(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase));
            if (dim is null) { bad = Bad($"groupBy is a comma-separated list of {string.Join(", ", AnalyticsSql.Dimensions)}; '{d}' is none of them"); return false; }
            if (!by.Contains(dim)) by.Add(dim);
        }
        var services = req.HttpContext.RequestServices;
        var fiscal = services.GetService<AnalyticsOptions>()?.FiscalYearStartMonth ?? 1;
        var localDay = services.GetService<DisplayClock>()?.LocalDate(now);
        w = new AnalyticsWindow(from, to, now)
        {
            ReleaseTypeId = Get("releaseTypeId"),   // REOS-116: only the releases of that type; an id nobody has gives empty results
            GroupBy = [.. AnalyticsSql.Dimensions.Where(by.Contains)],   // always in the order of the columns, so one request means one SQL text
            FiscalYearStartMonth = fiscal,
            LocalDay = localDay,
        };
        return true;

        static DateOnly DefaultFrom(DateOnly d) => d.AddDays(-DefaultWindowDays);
        static bool TryDate(string s, out DateOnly d) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }
}
