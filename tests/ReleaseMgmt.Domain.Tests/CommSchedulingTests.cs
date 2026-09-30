using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-43: the T-minus plan's DueAt (business days, holidays, window, DST) and the sent/late states.</summary>
public class CommSchedulingTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly CommScheduling.Options Opt = CommScheduling.Options.Default(Chicago);
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

    private static readonly DateOnly Target = D("2026-10-30");   // Friday
    private static readonly DateTime WinStart = U("2026-10-30T06:00:00Z"), WinEnd = U("2026-10-30T10:00:00Z");   // 01:00-05:00 CDT

    private static DateTime Due(int offset, string type = "Tminus", IReadOnlySet<DateOnly>? h = null, bool window = true) =>
        CommScheduling.DueAtUtc(Target, offset, type, window ? WinStart : null, window ? WinEnd : null, h ?? NoHolidays, Opt);

    [Fact] public void T_minus_7_is_seven_business_days_back_at_0900_display_time() => Assert.Equal(U("2026-10-21T14:00:00Z"), Due(-7));
    [Fact] public void T_minus_3() => Assert.Equal(U("2026-10-27T14:00:00Z"), Due(-3));
    [Fact] public void T_minus_1() => Assert.Equal(U("2026-10-29T14:00:00Z"), Due(-1));
    [Fact] public void A_holiday_is_not_a_business_day() => Assert.Equal(U("2026-10-26T14:00:00Z"), Due(-3, h: new HashSet<DateOnly> { D("2026-10-28") }));
    [Fact] public void A_weekend_is_skipped() => Assert.Equal(U("2026-10-23T14:00:00Z"), Due(-5));   // Fri 23: 29,28,27,26,23
    [Fact] public void Cutover_start_is_due_fifteen_minutes_before_the_window() => Assert.Equal(U("2026-10-30T05:45:00Z"), Due(0, "Cutover"));
    [Fact] public void Complete_is_due_when_the_window_closes() => Assert.Equal(U("2026-10-30T10:00:00Z"), Due(0, "Complete"));
    [Fact] public void Cutover_and_Complete_without_a_window_fall_back_to_the_send_time() { Assert.Equal(U("2026-10-30T14:00:00Z"), Due(0, "Cutover", window: false)); Assert.Equal(U("2026-10-30T14:00:00Z"), Due(0, "Complete", window: false)); }
    [Fact] public void Other_messages_on_the_target_day_use_the_send_time() => Assert.Equal(U("2026-10-30T14:00:00Z"), Due(0, "Custom"));
    [Fact] public void The_window_rule_applies_only_on_the_target_day() => Assert.Equal(U("2026-10-29T14:00:00Z"), Due(-1, "Cutover"));

    [Fact]
    public void After_the_target_counts_business_days_forward_and_follows_DST_to_standard_time()
    {
        Assert.Equal(U("2026-11-06T15:00:00Z"), Due(5, "HypercareExit"));   // Fri 6 Nov 09:00 CST (UTC-6; DST ended Sun 1 Nov)
        Assert.Equal(U("2026-11-09T15:00:00Z"), Due(5, "HypercareExit", new HashSet<DateOnly> { D("2026-11-03") }));
    }

    [Fact]
    public void A_local_time_skipped_by_the_spring_forward_moves_to_the_next_valid_hour() =>
        Assert.Equal(U("2026-03-08T08:30:00Z"), CommScheduling.LocalToUtc(D("2026-03-08"), new TimeOnly(2, 30), Chicago));

    [Fact] public void Send_time_and_zone_come_from_the_options() =>
        Assert.Equal(U("2026-10-29T07:30:00Z"), CommScheduling.DueAtUtc(Target, -1, "x", null, null, NoHolidays, new(TimeZoneInfo.Utc, new TimeOnly(7, 30), 15)));

    [Theory]
    [InlineData(-7, 7, "T−7")] [InlineData(-3, 3, "T−3")] [InlineData(-1, 1, "T−1")] [InlineData(0, 0, "T0")] [InlineData(5, -5, "T+5")]
    public void The_label_is_read_back_from_the_due_time(int offset, int expected, string label)
    {
        var h = new HashSet<DateOnly> { D("2026-10-28") };
        var due = Due(offset, "HypercareExit", h);
        var t = CommScheduling.TMinus(due, Target, h, Chicago);
        Assert.Equal(expected, t);
        Assert.Equal(label, CommScheduling.Label(t));
    }

    [Fact] public void An_early_morning_cutover_is_labelled_by_its_local_day() =>
        Assert.Equal(0, CommScheduling.TMinus(U("2026-10-30T05:45:00Z"), Target, NoHolidays, Chicago));   // 00:45 local on the 30th, though 05:45Z

    private static readonly DateTime Now = U("2026-10-29T20:12:00Z");

    [Fact] public void Sent_before_due_is_Sent() { Assert.Equal(CommScheduling.Sent, CommScheduling.State(Now, Now.AddMinutes(-9), Now)); Assert.Equal(0, CommScheduling.LateMinutes(Now, Now.AddMinutes(-9))); }
    [Fact] public void Sent_exactly_at_due_is_on_time() => Assert.Equal(CommScheduling.Sent, CommScheduling.State(Now, Now, Now));
    [Fact] public void Sent_after_due_is_SentLate_and_counts_minutes_rounded_up() { Assert.Equal(CommScheduling.SentLate, CommScheduling.State(Now.AddMinutes(-100), Now, Now)); Assert.Equal(100, CommScheduling.LateMinutes(Now.AddMinutes(-100), Now)); Assert.Equal(1, CommScheduling.LateMinutes(Now.AddSeconds(-1), Now)); }
    [Fact] public void Unsent_and_past_due_is_Overdue() => Assert.Equal(CommScheduling.Overdue, CommScheduling.State(Now.AddSeconds(-1), null, Now));
    [Fact] public void Unsent_due_within_a_day_is_Ready() { Assert.Equal(CommScheduling.Ready, CommScheduling.State(Now.AddHours(24), null, Now)); Assert.Equal(CommScheduling.Ready, CommScheduling.State(Now, null, Now)); }
    [Fact] public void Unsent_later_is_Scheduled() => Assert.Equal(CommScheduling.Scheduled, CommScheduling.State(Now.AddHours(24).AddSeconds(1), null, Now));
}
