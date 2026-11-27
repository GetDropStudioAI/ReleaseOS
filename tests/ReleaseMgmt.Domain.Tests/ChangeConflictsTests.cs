using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-180: the pure conflict rules, one case per kind. Words carry the UTC time; every conflict names the rule that fired.</summary>
public class ChangeConflictsTests
{
    private static readonly DateTime S = new(2033, 3, 5, 2, 0, 0, DateTimeKind.Utc), E = S.AddHours(2);
    private static ConflictInput Input(IReadOnlyList<ConflictFreeze>? f = null, IReadOnlyList<ConflictHoliday>? h = null, IReadOnlyList<ConflictChange>? c = null, IReadOnlyList<WindowCollision>? w = null, bool window = true) =>
        new("me", window ? S : null, window ? E : null, f ?? [], h ?? [], c ?? [], w ?? []);

    [Fact]
    public void No_planned_window_means_no_conflicts() =>
        Assert.Empty(ChangeConflictRules.Evaluate(Input([new("f", "x", "Freeze", S, E)], window: false)));

    [Fact]
    public void A_freeze_comes_before_a_chill_and_both_say_the_rule_and_the_times()
    {
        var r = ChangeConflictRules.Evaluate(Input([new("c", "Quiet week", "Chill", S.AddHours(-1), E), new("f", "Year end", "Freeze", S, E.AddDays(1))]));
        Assert.Equal([ChangeConflictRules.FreezeOverlap, ChangeConflictRules.ChillOverlap], r.Select(x => x.Rule));
        Assert.All(r, x => Assert.Equal(ChangeConflictTypes.Freeze, x.Type));
        Assert.Contains("freeze window \"Year end\"", r[0].Summary);
        Assert.Contains("2033-03-05 02:00 UTC", r[0].Summary);
    }

    [Fact]
    public void A_window_that_only_touches_the_edge_does_not_overlap() =>
        Assert.Empty(ChangeConflictRules.Evaluate(Input([new("f", "Before", "Freeze", S.AddHours(-3), S), new("g", "After", "Freeze", E, E.AddHours(3))])));

    [Fact]
    public void A_holiday_inside_the_window_is_a_blackout_day_and_midnight_is_not_the_next_day()
    {
        var r = ChangeConflictRules.Evaluate(Input(h: [new(new DateOnly(2033, 3, 5), "Founders Day"), new(new DateOnly(2033, 3, 6), "Next day")]));
        Assert.Single(r);
        Assert.Equal(ChangeConflictRules.BlackoutDay, r[0].Rule);
        Assert.Contains("Founders Day", r[0].Summary);
    }

    [Fact]
    public void Another_change_is_named_by_what_it_shares_and_one_that_shares_nothing_is_ignored_as_is_the_change_itself()
    {
        var r = ChangeConflictRules.Evaluate(Input(c:
        [
            new("a", "CHG-0002", "Rotate keys", "Approved", S, E, true, ["Payments API"], ["Prod EU"]),
            new("b", "CHG-0003", "Unrelated", "Submitted", S, E, false, [], []),
            new("me", "CHG-0001", "Me", "Submitted", S, E, true, [], []),
        ]));
        Assert.Single(r);
        Assert.Equal(ChangeConflictRules.SharedWindow, r[0].Rule);
        Assert.Contains("the same release, service Payments API, environment Prod EU", r[0].Summary);
    }

    [Fact]
    public void A_release_window_names_what_is_shared()
    {
        var r = ChangeConflictRules.Evaluate(Input(w: [new("t2", "R26.11", "Executing", S, E, ["payments-prod"], ["Payments API"])]));
        Assert.Single(r);
        Assert.Equal(ChangeConflictRules.ReleaseWindowOverlap, r[0].Rule);
        Assert.Contains("shares CI payments-prod, Payments API", r[0].Summary);
    }

    [Fact]
    public void The_order_is_freeze_blackout_change_release()
    {
        var r = ChangeConflictRules.Evaluate(Input([new("f", "F", "Freeze", S, E)], [new(new DateOnly(2033, 3, 5), "H")], [new("a", "CHG-2", "T", "Approved", S, E, true, [], [])], [new("t2", "R", "Planning", S, E, [], ["P"])]));
        Assert.Equal([ChangeConflictTypes.Freeze, ChangeConflictTypes.Blackout, ChangeConflictTypes.ChangeOverlap, ChangeConflictTypes.ReleaseWindow], r.Select(x => x.Type));
    }

    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData("   short   ", false)] [InlineData("nineteen chars here", false)] [InlineData("exactly twenty chars", true)] [InlineData("   exactly twenty chars   ", true)]
    public void A_reason_needs_twenty_characters_once_trimmed(string? reason, bool enough) => Assert.Equal(enough, ChangeConflictRules.ReasonIsEnough(reason));

    [Fact]
    public void The_catalogue_lists_the_five_rules_in_order()
    {
        var c = ChangeConflictRules.Catalogue(45);
        Assert.Equal(5, c.Rules.Count);
        Assert.Equal(45, c.BufferMinutes);
        Assert.Contains("45 minutes", c.Rules[3].Description);
    }
}
