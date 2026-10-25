using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>
/// REOS-121 (Q-121a). <c>Analytics:FiscalYearStartMonth</c>, 1 to 12, default 1. 1 means calendar quarters (Q1 = January to March, labelled 2026-Q3); any other month starts the fiscal year
/// there, and quarters are labelled FY2027-Q1 (the fiscal year is named for the calendar year it ends in). A value that is not a whole number from 1 to 12 stops the start and names the key.
/// </summary>
public sealed record AnalyticsOptions(int FiscalYearStartMonth)
{
    public const string FiscalYearKey = "Analytics:FiscalYearStartMonth";

    public static AnalyticsOptions From(IConfiguration config)
    {
        var raw = config[FiscalYearKey];
        if (string.IsNullOrWhiteSpace(raw)) return new(1);
        return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
            ? new(m) : throw new InvalidOperationException($"{FiscalYearKey} is '{raw}'. Use the number of the month the fiscal year starts in, 1 to 12 (1 = calendar quarters, the default).");
    }

    /// <summary>The label of the quarter a date falls in, as the SQL labels it: <c>2026-Q3</c>, or <c>FY2027-Q1</c> for a fiscal year that starts in October.</summary>
    public string QuarterOf(DateOnly day)
    {
        if (FiscalYearStartMonth == 1) return $"{day.Year}-Q{(day.Month - 1) / 3 + 1}";
        return $"FY{day.Year + (day.Month >= FiscalYearStartMonth ? 1 : 0)}-Q{(day.Month - FiscalYearStartMonth + 12) % 12 / 3 + 1}";
    }
}
