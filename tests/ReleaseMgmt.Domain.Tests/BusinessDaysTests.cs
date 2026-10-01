using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

public class BusinessDaysTests
{
    private static readonly IReadOnlySet<DateOnly> None = new HashSet<DateOnly>();
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact] public void Same_day_is_zero() => Assert.Equal(0, BusinessDays.Between(D("2026-10-27"), D("2026-10-27"), None));
    [Fact] public void Tuesday_to_Friday_is_three() => Assert.Equal(3, BusinessDays.Between(D("2026-10-27"), D("2026-10-30"), None));   // Wed, Thu, Fri
    [Fact] public void Weekends_are_skipped() => Assert.Equal(1, BusinessDays.Between(D("2026-10-30"), D("2026-11-02"), None));          // Fri -> Mon
    [Fact] public void Past_targets_are_negative() => Assert.Equal(-3, BusinessDays.Between(D("2026-10-30"), D("2026-10-27"), None));
    [Fact] public void Holidays_are_skipped() => Assert.Equal(2, BusinessDays.Between(D("2026-10-27"), D("2026-10-30"), new HashSet<DateOnly> { D("2026-10-28") }));

    [Theory]   // the inverse of SubtractBusinessDays: T-n from the gate's due date back to the target
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(9)]
    public void Round_trips_with_SubtractBusinessDays(int n)
    {
        var hol = new HashSet<DateOnly> { D("2026-10-12") };
        var target = D("2026-10-30");
        var due = BusinessDays.SubtractBusinessDays(target, n, hol);
        Assert.Equal(n, BusinessDays.Between(due, target, hol));
    }

    // REOS-81: a gate given a due date stores the offset that lands on it
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)] [InlineData(5)] [InlineData(13)]
    public void OffsetOf_inverts_SubtractBusinessDays(int n)
    {
        var hol = new HashSet<DateOnly> { D("2026-10-12"), D("2026-10-28") };
        foreach (var target in new[] { D("2026-10-30"), D("2026-10-31"), D("2026-10-28") })   // a Friday, a Saturday and a holiday as the target
            Assert.Equal(n, BusinessDays.OffsetOf(target, BusinessDays.SubtractBusinessDays(target, n, hol), hol, 500));
    }

    [Fact] public void OffsetOf_refuses_weekends_holidays_later_dates_and_out_of_range()
    {
        var hol = new HashSet<DateOnly> { D("2026-10-12") };
        Assert.Null(BusinessDays.OffsetOf(D("2026-10-30"), D("2026-10-25"), hol, 500));   // Sunday
        Assert.Null(BusinessDays.OffsetOf(D("2026-10-30"), D("2026-10-12"), hol, 500));   // holiday
        Assert.Null(BusinessDays.OffsetOf(D("2026-10-30"), D("2026-11-02"), hol, 500));   // after the target
        Assert.Null(BusinessDays.OffsetOf(D("2026-10-30"), D("2026-10-23"), hol, 4));     // 5 business days back, cap 4
        Assert.Equal(0, BusinessDays.OffsetOf(D("2026-10-31"), D("2026-10-31"), hol, 500));   // the target itself, even on a Saturday
    }
}
