using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-88 contingency on the plan and REOS-85 window collisions (pure rules; the API and screens are tested elsewhere).</summary>
public class AdvisoriesTests
{
    private static readonly DateTime W0 = new(2026, 11, 6, 6, 0, 0, DateTimeKind.Utc);   // window 06:00–10:00 UTC
    private static readonly DateTime W1 = W0.AddHours(4);
    private static PlanStep S(string section, int atMin, int min) => new(section, W0.AddMinutes(atMin), min);

    [Fact]
    public void Contingency_is_the_spare_time_before_the_rollback_deadline_over_the_planned_execution_time()
    {
        // Execution 06:00–08:00 (120 min), rollback 60 min: deadline 09:00, spare 60 min = 50%.
        var c = Contingency.Compute(W0, W1, [S("PreCheck", 0, 15), S("Deploy", 15, 75), S("Verify", 90, 30), S("Rollback", 0, 45), S("Rollback", 45, 15)], 30);
        Assert.Equal((Contingency.Ok, 120, 60, 60, 50), (c.State, c.PlannedMin, c.RollbackMin, c.SpareMin, c.Percent));
        Assert.Equal(W0.AddHours(3), c.RollbackDeadline);
        Assert.Equal(W0.AddHours(2), c.PlannedFinish);
    }

    [Fact]
    public void Below_the_minimum_is_low_and_a_plan_past_the_deadline_is_over()
    {
        var low = Contingency.Compute(W0, W1, [S("Deploy", 0, 150), S("Rollback", 0, 60)], 30);   // spare 30 of 150 = 20%
        Assert.Equal((Contingency.Low, 20), (low.State, low.Percent));
        var over = Contingency.Compute(W0, W1, [S("Deploy", 0, 200), S("Rollback", 0, 60)], 30);  // ends 09:20, deadline 09:00
        Assert.Equal((Contingency.Over, -20), (over.State, over.SpareMin));
        Assert.Equal(Contingency.Ok, Contingency.Compute(W0, W1, [S("Deploy", 0, 150), S("Rollback", 0, 60)], 20).State);   // the minimum is a setting
    }

    [Fact]
    public void Without_a_window_or_steps_outside_Rollback_nothing_is_computed_and_the_reason_says_why()
    {
        var a = Contingency.Compute(null, null, [S("Deploy", 0, 30)], 30);
        Assert.Equal((Contingency.Unknown, "no deployment window"), (a.State, a.Reason));
        var b = Contingency.Compute(W0, W1, [S("Rollback", 0, 30)], 30);
        Assert.Equal((Contingency.Unknown, "no runbook steps outside Rollback"), (b.State, b.Reason));
        Assert.Null(b.Percent);
    }

    [Fact]
    public void A_gap_between_steps_counts_as_execution_time_and_steps_before_the_window_count_too()
    {
        // PreCheck 05:30–05:45 before the window opens, Deploy 06:00–07:00: execution 05:30–07:00 = 90 min; no rollback: deadline 10:00, spare 180.
        var c = Contingency.Compute(W0, W1, [S("PreCheck", -30, 15), S("Deploy", 0, 60)], 30);
        Assert.Equal((90, 180, 200), (c.PlannedMin, c.SpareMin, c.Percent));
    }

    private static WindowedTrain T(string id, int fromMin, int toMin, string[]? cis = null, (string, string?)[]? products = null) =>
        new(id, $"R26.{id}", "Gated", W0.AddMinutes(fromMin), W0.AddMinutes(toMin), cis ?? [], products ?? []);

    [Fact]
    public void Overlapping_windows_collide_only_when_the_trains_share_a_CI_or_a_product()
    {
        var a = T("1", 0, 120, ["ledger-db"], [("Payments API", "PAY")]);
        var shareCi = T("2", 90, 180, [" LEDGER-DB "]);
        var shareCode = T("3", 30, 60, products: [("Payments gateway", "pay")]);
        var shareName = T("4", 60, 70, products: [("payments api", null)]);
        var nothing = T("5", 0, 120, ["other-db"], [("Card Portal", "CRD")]);
        var hits = WindowCollisions.Find(a, [a, shareCi, shareCode, shareName, nothing], 0);
        Assert.Equal(["3", "4", "2"], hits.Select(h => h.OtherTrainId).ToArray());   // sorted by the other window's start
        Assert.Equal(["ledger-db"], hits.Single(h => h.OtherTrainId == "2").SharedCis);   // the other train wrote " LEDGER-DB "
        Assert.Equal(["Payments API"], hits.Single(h => h.OtherTrainId == "3").SharedProducts);   // named as this train spells it (matched by project code)
    }

    [Fact]
    public void The_buffer_widens_both_windows_so_back_to_back_windows_collide_within_it()
    {
        var a = T("1", 0, 120, ["ledger-db"]);
        var after = T("2", 150, 200, ["ledger-db"]);           // starts 30 min after a ends
        Assert.Empty(WindowCollisions.Find(a, [after], 29));
        Assert.Single(WindowCollisions.Find(a, [after], 31));
        Assert.Empty(WindowCollisions.Find(a, [T("3", 120, 180, ["ledger-db"])], 0));   // touching ends do not overlap
    }
}
