using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

public class RunPlanTests
{
    private static DateTime T(string s) => DateTime.SpecifyKind(DateTime.Parse(s), DateTimeKind.Utc);

    [Fact]
    public void Shift_is_run_start_minus_the_earliest_non_rollback_step()
    {
        var steps = new[] { ("Deploy", T("2026-10-30T06:00:00")), ("PreCheck", T("2026-10-30T05:30:00")), ("Rollback", T("2026-10-30T04:00:00")) };   // the earlier Rollback step must be ignored
        Assert.Equal(TimeSpan.FromDays(-3) + TimeSpan.FromHours(4.5), RunPlan.RehearsalShift(T("2026-10-27T10:00:00"), steps));                       // 10:00 on the 27th vs 05:30 on the 30th
    }

    [Fact] public void A_run_that_starts_exactly_on_time_has_no_shift() => Assert.Equal(TimeSpan.Zero, RunPlan.RehearsalShift(T("2026-10-30T05:30:00"), [("PreCheck", T("2026-10-30T05:30:00"))]));
    [Fact] public void Only_rollback_steps_means_no_shift() => Assert.Equal(TimeSpan.Zero, RunPlan.RehearsalShift(T("2026-10-27T10:00:00"), [("Rollback", T("2026-10-30T04:00:00"))]));
    [Fact] public void No_steps_means_no_shift() => Assert.Equal(TimeSpan.Zero, RunPlan.RehearsalShift(T("2026-10-27T10:00:00"), []));
}
