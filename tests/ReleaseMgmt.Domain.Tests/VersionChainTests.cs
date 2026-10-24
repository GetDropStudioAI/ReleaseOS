using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Domain.Services.VersionChain;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-117 (RT2, D48): the order of a version's chain, the advisory against a release that comes too soon after the one before it, and the places the chain can go on to.</summary>
public class VersionChainTests
{
    private static readonly TypeInfo Beta = new("tyB", "Beta", 10, true, false), Cert = new("tyC", "CERT", 20, false, false), Pen = new("tyN", "PenTest Cert", 30, false, false), Prod = new("tyP", "Production", 40, false, false);
    private static ChainRelease R(TypeInfo t, string target, int? beta = null, string status = "Planning") =>
        new($"{t.Name}{beta}", $"R {t.Name} {beta}".Trim(), null, t.Id, t.Name, t.SortOrder, beta, status, DateOnly.Parse(target));

    [Fact]
    public void The_chain_runs_Beta_numbers_then_CERT_PenTest_Cert_and_Production_whatever_order_they_were_made_in()
    {
        var chain = Order([R(Prod, "2026-12-20"), R(Beta, "2026-11-10", 2), R(Cert, "2026-11-30"), R(Beta, "2026-11-01", 1), R(Pen, "2026-12-05")]);
        Assert.Equal(["Beta 1", "Beta 2", "CERT", "PenTest Cert", "Production"], chain.Select(Label));
    }

    [Fact]
    public void A_later_release_planned_before_the_one_ahead_of_it_is_flagged_and_the_message_names_both()
    {
        var w = Assert.Single(Warnings([R(Beta, "2026-11-12", 1), R(Beta, "2026-11-10", 2), R(Cert, "2026-12-01")], 1));
        Assert.Equal("Beta 2", w.Label);
        Assert.Equal("Beta 1", w.AfterLabel);
        Assert.Equal(new DateOnly(2026, 11, 13), w.EarliestOn);
        Assert.Equal(3, w.ShortByDays);
        Assert.Contains("Beta 2 is planned for 2026-11-10, but Beta 1 is due 2026-11-12", w.Message);
        Assert.Contains("3 days too soon", w.Message);
    }

    [Fact]
    public void The_gap_is_the_minimum_the_date_must_be_after_the_previous_one()
    {
        Assert.Empty(Warnings([R(Beta, "2026-11-01", 1), R(Beta, "2026-11-02", 2)], 1));
        Assert.Single(Warnings([R(Beta, "2026-11-01", 1), R(Beta, "2026-11-01", 2)], 1));
        Assert.Empty(Warnings([R(Beta, "2026-11-01", 1), R(Beta, "2026-11-01", 2)], 0));
        Assert.Single(Warnings([R(Beta, "2026-11-01", 1), R(Beta, "2026-11-03", 2)], 3));
    }

    [Fact]
    public void A_slip_flags_only_the_next_release_each_release_is_compared_with_the_one_before_it()
    {
        // Beta 1 slips to the 20th: Beta 2 (15th) now comes too soon. CERT (16th) is compared with Beta 2's own date, so it is fine until Beta 2 moves.
        var w = Assert.Single(Warnings([R(Beta, "2026-11-20", 1), R(Beta, "2026-11-15", 2), R(Cert, "2026-11-16"), R(Prod, "2026-11-30")], 1));
        Assert.Equal("Beta 2", w.Label);
        Assert.Equal("Beta 1", w.AfterLabel);
        // once Beta 2 is moved on to the 25th, CERT is the one that comes too soon
        var after = Assert.Single(Warnings([R(Beta, "2026-11-20", 1), R(Beta, "2026-11-25", 2), R(Cert, "2026-11-16"), R(Prod, "2026-11-30")], 1));
        Assert.Equal("CERT", after.Label);
        Assert.Equal("Beta 2", after.AfterLabel);
    }

    [Fact]
    public void Closed_releases_are_never_flagged_and_an_aborted_one_is_skipped_as_a_predecessor()
    {
        Assert.Empty(Warnings([R(Beta, "2026-11-20", 1), R(Beta, "2026-11-15", 2, "Complete")], 1));
        Assert.Empty(Warnings([R(Beta, "2026-11-20", 1), R(Beta, "2026-11-15", 2, "Aborted")], 1));
        // Beta 2 was aborted with a late date: CERT is compared with Beta 1, the nearest live release before it
        Assert.Empty(Warnings([R(Beta, "2026-11-01", 1), R(Beta, "2026-12-15", 2, "Aborted"), R(Cert, "2026-11-20")], 1));
    }

    [Fact]
    public void A_release_alone_in_its_version_has_nothing_to_compare_with()
    {
        Assert.Empty(Warnings([R(Cert, "2026-11-01")], 1));
        Assert.Empty(Warnings([], 1));
    }

    [Fact]
    public void After_Beta_1_the_chain_offers_Beta_2_then_the_later_types()
    {
        var b1 = R(Beta, "2026-11-01", 1);
        var next = NextOptions(b1, [b1], [Beta, Cert, Pen, Prod]);
        Assert.Equal(["Beta 2", "CERT", "PenTest Cert", "Production"], next.Select(o => o.Label));
        Assert.Equal(2, next[0].BetaNumber);
    }

    [Fact]
    public void After_Beta_3_and_for_a_product_without_betas_the_chain_goes_to_CERT_and_on_skipping_what_is_taken()
    {
        var b3 = R(Beta, "2026-11-01", 3);
        Assert.Equal(["CERT", "PenTest Cert", "Production"], NextOptions(b3, [b3], [Beta, Cert, Pen, Prod]).Select(o => o.Label));
        var cert = R(Cert, "2026-11-10");
        var prod = R(Prod, "2026-12-01");
        Assert.Equal(["PenTest Cert"], NextOptions(cert, [cert, prod], [Beta, Cert, Pen, Prod]).Select(o => o.Label));
        Assert.Empty(NextOptions(prod, [cert, prod], [Beta, Cert, Pen, Prod]));
    }

    [Fact]
    public void An_aborted_release_gives_its_place_back_and_an_archived_type_is_not_offered()
    {
        var b1 = R(Beta, "2026-11-01", 1);
        var b2 = R(Beta, "2026-11-08", 2, "Aborted");
        Assert.Equal(2, NextOptions(b1, [b1, b2], [Beta, Cert]).First().BetaNumber);
        var b2live = R(Beta, "2026-11-08", 2);
        Assert.Equal("CERT", NextOptions(b1, [b1, b2live], [Beta, Cert]).First().Label);
        Assert.Equal(["Production"], NextOptions(b1, [b1], [Beta, Cert with { Archived = true }, Prod with { Name = "Production" }]).Where(o => o.TypeId != "tyB").Select(o => o.Label));
    }
}
