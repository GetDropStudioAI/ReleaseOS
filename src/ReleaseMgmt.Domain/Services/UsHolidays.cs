namespace ReleaseMgmt.Domain.Services;

/// <summary>US federal holidays with the weekend "observed" rule (Saturday -> Friday, Sunday -> Monday). Seed data for the Holidays table (open item 13).</summary>
public static class UsHolidays
{
    public static IReadOnlyList<(DateOnly Day, string Name)> ForYear(int y)
    {
        var list = new List<(DateOnly, string)>
        {
            (Observed(new DateOnly(y, 1, 1)), "New Year's Day"),
            (Nth(y, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day"),
            (Nth(y, 2, DayOfWeek.Monday, 3), "Presidents' Day"),
            (Last(y, 5, DayOfWeek.Monday), "Memorial Day"),
            (Observed(new DateOnly(y, 6, 19)), "Juneteenth"),
            (Observed(new DateOnly(y, 7, 4)), "Independence Day"),
            (Nth(y, 9, DayOfWeek.Monday, 1), "Labor Day"),
            (Nth(y, 10, DayOfWeek.Monday, 2), "Columbus Day"),
            (Observed(new DateOnly(y, 11, 11)), "Veterans Day"),
            (Nth(y, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day"),
            (Observed(new DateOnly(y, 12, 25)), "Christmas Day"),
        };
        // 1 Jan of next year falling on a Saturday is observed on 31 Dec of this year
        var nextNewYear = new DateOnly(y + 1, 1, 1);
        if (nextNewYear.DayOfWeek == DayOfWeek.Saturday) list.Add((new DateOnly(y, 12, 31), "New Year's Day (observed)"));
        return list.Where(h => h.Item1.Year == y).OrderBy(h => h.Item1).ToList(); // a Saturday 1 Jan is observed on 31 Dec of the previous year, added by that year
    }

    private static DateOnly Observed(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(-1),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    private static DateOnly Nth(int y, int m, DayOfWeek dow, int n)
    {
        var d = new DateOnly(y, m, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateOnly Last(int y, int m, DayOfWeek dow)
    {
        var d = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
        while (d.DayOfWeek != dow) d = d.AddDays(-1);
        return d;
    }
}
