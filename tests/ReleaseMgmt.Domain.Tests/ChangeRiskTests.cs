using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-176 (D56): each visible risk rule has a case, and the factor list always explains the level.</summary>
public class ChangeRiskTests
{
    private static readonly DateTime S = new(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc);
    private static readonly ChangeRiskOptions O = new();

    private static RiskInput Quiet(string type = "Normal") => new(type, S, S.AddHours(2), "Redeploy the previous build", true, 1, [], []);
    private static RiskFactor F(RiskAssessment a, string rule) => a.Factors.Single(f => f.Rule == rule);

    [Fact]
    public void A_quiet_change_is_Low_and_every_rule_still_reports_what_it_found()
    {
        var a = ChangeRiskRules.Assess(Quiet(), O);
        Assert.Equal(("Low", 0), (a.Level, a.Score));
        Assert.Equal(5, a.Factors.Count);
        Assert.All(a.Factors, f => { Assert.False(f.Applied); Assert.Equal(0, f.Points); Assert.NotEmpty(f.Why); });
    }

    [Fact]
    public void Rule_Emergency_type_is_three_points()
    {
        var a = ChangeRiskRules.Assess(Quiet("Emergency"), O);
        Assert.True(F(a, ChangeRiskRules.Emergency).Applied);
        Assert.Equal(("High", 3), (a.Level, a.Score));
        Assert.False(F(ChangeRiskRules.Assess(Quiet("Standard"), O), ChangeRiskRules.Emergency).Applied);
    }

    [Fact]
    public void Rule_no_rollback_note_is_two_points_and_a_blank_note_counts()
    {
        foreach (var note in new string?[] { null, "", "   " })
        {
            var a = ChangeRiskRules.Assess(Quiet() with { RollbackNote = note }, O);
            Assert.True(F(a, ChangeRiskRules.NoRollbackNote).Applied);
            Assert.Equal(("Medium", 2), (a.Level, a.Score));
        }
    }

    [Fact]
    public void Rule_inside_a_freeze_is_three_points_inside_a_chill_two_and_near_one()
    {
        RiskFreeze Win(string kind, double fromHours, double toHours) => new($"{kind} window", kind, S.AddHours(fromHours), S.AddHours(toHours));
        var inside = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Freeze", 1, 10)] }, O);
        Assert.Equal(("High", 3), (inside.Level, inside.Score));
        Assert.Contains("overlaps the freeze window \"Freeze window\"", F(inside, ChangeRiskRules.FreezeWindow).Why);
        var chill = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Chill", -5, 1)] }, O);
        Assert.Equal(2, chill.Score);
        Assert.Contains("chill window", F(chill, ChangeRiskRules.FreezeWindow).Why);
        var near = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Freeze", 20, 30)] }, O);   // starts 18 h after the window ends: inside the 48 h margin
        Assert.Equal(1, near.Score);
        Assert.Contains("within 48 hours", F(near, ChangeRiskRules.FreezeWindow).Why);
        var far = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Freeze", 60, 70)] }, O);
        Assert.False(F(far, ChangeRiskRules.FreezeWindow).Applied);
        var tight = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Freeze", 20, 30)] }, O with { NearFreezeHours = 12 });   // the margin is a setting
        Assert.False(F(tight, ChangeRiskRules.FreezeWindow).Applied);
        var both = ChangeRiskRules.Assess(Quiet() with { Freezes = [Win("Chill", 1, 10), Win("Freeze", 1, 10)] }, O);
        Assert.Equal(3, both.Score);   // the worst window counts once, never summed
    }

    [Fact]
    public void Rule_freeze_cannot_be_checked_without_a_planned_window_and_says_so()
    {
        var a = ChangeRiskRules.Assess(Quiet() with { PlannedStartAt = null, PlannedEndAt = null, Freezes = [new("x", "Freeze", S, S.AddHours(1))] }, O);
        Assert.False(F(a, ChangeRiskRules.FreezeWindow).Applied);
        Assert.Contains("no planned window", F(a, ChangeRiskRules.FreezeWindow).Why);
    }

    [Fact]
    public void Rule_prior_failed_change_on_the_same_service_is_two_points_and_needs_a_release()
    {
        var failure = new RiskPriorFailure("CHG-0007", "Payments API", "R26.09", "Unsuccessful", S.AddDays(-9));
        var a = ChangeRiskRules.Assess(Quiet() with { PriorFailures = [failure, failure with { ChangeKey = "CHG-0003", At = S.AddDays(-30) }] }, O);
        Assert.True(F(a, ChangeRiskRules.PriorFailure).Applied);
        Assert.Equal(2, a.Score);
        Assert.Contains("CHG-0007 on Payments API", F(a, ChangeRiskRules.PriorFailure).Why);   // the most recent one is named
        Assert.Contains("and 1 more", F(a, ChangeRiskRules.PriorFailure).Why);
        var none = ChangeRiskRules.Assess(Quiet() with { HasRelease = false, PriorFailures = [failure] }, O);
        Assert.False(F(none, ChangeRiskRules.PriorFailure).Applied);
        Assert.Contains("No release is linked", F(none, ChangeRiskRules.PriorFailure).Why);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(12, true)]
    public void Rule_large_scope_starts_at_the_threshold_number_of_products(int products, bool applies)
    {
        var a = ChangeRiskRules.Assess(Quiet() with { ProductCount = products }, O);
        Assert.Equal(applies, F(a, ChangeRiskRules.LargeScope).Applied);
        Assert.Equal(applies ? 2 : 0, a.Score);
        Assert.True(ChangeRiskRules.Assess(Quiet() with { ProductCount = 3 }, O with { LargeScopeProducts = 3 }).Factors.Single(f => f.Rule == ChangeRiskRules.LargeScope).Applied);
        Assert.False(ChangeRiskRules.Assess(Quiet() with { HasRelease = false, ProductCount = 99 }, O).Factors.Single(f => f.Rule == ChangeRiskRules.LargeScope).Applied);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(1, "Medium")]
    [InlineData(2, "Medium")]
    [InlineData(3, "High")]
    [InlineData(4, "High")]
    [InlineData(5, "Critical")]
    [InlineData(12, "Critical")]
    public void The_score_bands(int score, string level) => Assert.Equal(level, ChangeRiskRules.LevelFor(score));

    [Fact]
    public void The_factor_list_always_adds_up_to_the_level()
    {
        var failure = new RiskPriorFailure("CHG-1", "S", "R", "Aborted", S.AddDays(-1));
        foreach (var type in ChangeTypes.All)
            foreach (var note in new string?[] { null, "undo" })
                foreach (var win in new[] { true, false })
                    foreach (var freezes in new[] { 0, 1 })
                        foreach (var products in new[] { 1, 6 })
                        {
                            var i = new RiskInput(type, win ? S : null, win ? S.AddHours(1) : null, note, true, products, freezes == 0 ? [] : [new("F", "Freeze", S, S.AddHours(1))], products > 1 ? [failure] : []);
                            var a = ChangeRiskRules.Assess(i, O);
                            Assert.Equal(a.Factors.Where(f => f.Applied).Sum(f => f.Points), a.Score);
                            Assert.Equal(ChangeRiskRules.LevelFor(a.Score), a.Level);
                            Assert.All(a.Factors, f => Assert.Equal(f.Applied, f.Points > 0));
                        }
    }
}
