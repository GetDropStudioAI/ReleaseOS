namespace ReleaseMgmt.Domain.Services;

/// <summary>Business-day arithmetic for gate DueOn (D9): weekends and Holidays are skipped.</summary>
public static class BusinessDays
{
    public static bool IsBusinessDay(DateOnly d, IReadOnlySet<DateOnly> holidays) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d);

    /// <summary>The date that is <paramref name="days"/> business days before <paramref name="target"/>.
    /// Literal reading of "target minus N business days": step back one calendar day at a time, counting only business days;
    /// the target itself is not counted, and offset 0 returns the target unchanged.</summary>
    public static DateOnly SubtractBusinessDays(DateOnly target, int days, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        var d = target;
        for (var left = days; left > 0;)
        {
            d = d.AddDays(-1);
            if (IsBusinessDay(d, holidays)) left--;
        }
        return d;
    }

    /// <summary>The next business day strictly after <paramref name="from"/> (escalation level 2 is +1 business day).</summary>
    public static DateOnly AddBusinessDays(DateOnly from, int days, IReadOnlySet<DateOnly> holidays)
    {
        var d = from;
        for (var left = days; left > 0;)
        {
            d = d.AddDays(1);
            if (IsBusinessDay(d, holidays)) left--;
        }
        return d;
    }
}
