using System.Globalization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-108 (B2): <c>GET /overview?from&amp;to</c>, the Organization overview's metric cards, attention items, daily completions and release activity in one
/// read (<see cref="OverviewService"/>; definitions in QUESTIONS Q-108a). Dates are 'yyyy-MM-dd', inclusive, UTC days (D44); both optional: <c>to</c>
/// defaults to today (UTC), <c>from</c> to the 14 days ending at <c>to</c>. Any signed-in role may read.
/// </summary>
public static class OverviewEndpoints
{
    public static void MapOverview(this RouteGroupBuilder api) =>
        api.MapGet("/overview", async (string? from, string? to, OverviewService s, TimeProvider time, CancellationToken ct) =>
            Range(from, to, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), out var f, out var t) is { } why ? AnalyticsEndpoints.Bad(why) : Results.Ok(await s.GetAsync(f, t, ct)))
            .RequireAuthorization(Policies.Read);

    /// <summary>Null when the range is usable; otherwise the readable reason (400 InvalidFilter).</summary>
    internal static string? Range(string? from, string? to, DateOnly today, out DateOnly f, out DateOnly t)
    {
        f = default; t = today;
        if (!string.IsNullOrWhiteSpace(to) && !TryDate(to, out t)) return "to must be a date like 2026-10-23";
        f = t.AddDays(1 - OverviewService.DefaultDays);
        if (!string.IsNullOrWhiteSpace(from) && !TryDate(from, out f)) return "from must be a date like 2026-10-10";
        if (t < f) return "to must not be before from";
        if (t.DayNumber - f.DayNumber + 1 > OverviewService.MaxRangeDays) return $"The range may span at most {OverviewService.MaxRangeDays} days";
        return null;

        static bool TryDate(string s, out DateOnly d) => DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }
}
