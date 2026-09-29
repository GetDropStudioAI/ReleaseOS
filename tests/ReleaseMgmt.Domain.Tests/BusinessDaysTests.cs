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
}
