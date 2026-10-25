using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-122 (RT7): the difference between two versions of a template, and what counts as a change to a Compliance gate (which asks for a Governance Officer).</summary>
public class TemplateDiffTests
{
    private static TemplateGateSnap Gate(string name, string cls = "Standard", int offset = 3, string before = "Gated", string? team = null, params string[] tasks) =>
        new(name, cls, offset, before, team, [.. tasks.Select(t => new TemplateTaskSnap(t, null))]);

    private static TemplateSnapshot Snap(IReadOnlyList<TemplateGateSnap>? gates = null, IReadOnlyList<TemplateMilestoneSnap>? milestones = null, string tier = "Moderate",
        IReadOnlyList<TemplateStepSnap>? steps = null, IReadOnlyList<TemplateScheduleSnap>? schedule = null) => new(tier, gates ?? [], steps ?? [], schedule ?? [], milestones ?? []);

    private static readonly TemplateSnapshot V1 = Snap([Gate("Code Freeze", offset: 5, tasks: ["Tag repos"]), Gate("CAB", "Compliance", 1, "Executing", null, "SOX evidence")], [new("Code complete", -14, null)]);

    [Fact]
    public void Identical_versions_have_no_changes_and_no_Compliance_change()
    {
        Assert.Empty(TemplateDiff.Between(V1, V1));
        Assert.False(TemplateDiff.ComplianceChanged(V1, V1));
    }

    [Fact]
    public void A_first_version_with_a_Compliance_gate_counts_as_a_change_and_one_without_does_not()
    {
        Assert.True(TemplateDiff.ComplianceChanged(null, V1));
        Assert.False(TemplateDiff.ComplianceChanged(null, Snap([Gate("QA")])));
        Assert.False(TemplateDiff.ComplianceChanged(null, Snap()));
    }

    [Fact]
    public void A_Standard_gate_change_is_listed_but_is_not_a_Compliance_change()
    {
        var v2 = Snap([Gate("Code Freeze", offset: 4, tasks: ["Tag repos", "Freeze branches"]), V1.Gates[1]], V1.Milestones);
        var changes = TemplateDiff.Between(V1, v2);
        Assert.Contains(changes, c => c is { Area: "Gate", Kind: "Changed", Subject: "Code Freeze", Compliance: false });
        Assert.Contains(changes, c => c is { Area: "Task", Kind: "Added", Compliance: false } && c.Subject == "Code Freeze: Freeze branches");
        Assert.False(TemplateDiff.ComplianceChanged(V1, v2));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("before")]
    [InlineData("team")]
    [InlineData("task")]
    [InlineData("downgrade")]
    [InlineData("removed")]
    [InlineData("added")]
    [InlineData("renamed")]
    public void Any_difference_in_a_Compliance_gate_is_a_Compliance_change(string what)
    {
        var cab = what switch
        {
            "offset" => Gate("CAB", "Compliance", 2, "Executing", null, "SOX evidence"),
            "before" => Gate("CAB", "Compliance", 1, "Complete", null, "SOX evidence"),
            "team" => Gate("CAB", "Compliance", 1, "Executing", "team-1", "SOX evidence"),
            "task" => Gate("CAB", "Compliance", 1, "Executing", null, "SOX evidence", "Second sign-off"),
            "downgrade" => Gate("CAB", "Standard", 1, "Executing", null, "SOX evidence"),
            "renamed" => Gate("Change board", "Compliance", 1, "Executing", null, "SOX evidence"),
            _ => null,
        };
        var gates = new List<TemplateGateSnap> { V1.Gates[0] };
        if (cab is not null) gates.Add(cab);
        if (what == "added") { gates.Add(V1.Gates[1]); gates.Add(Gate("Pen test", "Compliance", 4, "Executing", null, "Report filed")); }
        var v2 = Snap(gates, V1.Milestones);
        Assert.True(TemplateDiff.ComplianceChanged(V1, v2), what);
        Assert.Contains(TemplateDiff.Between(V1, v2), c => c.Compliance);
    }

    [Fact]
    public void Task_order_and_surrounding_spaces_do_not_make_a_Compliance_change()
    {
        var a = Snap([Gate("CAB", "Compliance", 1, "Executing", null, "One", "Two")]);
        var b = Snap([Gate("CAB", "Compliance", 1, "Executing", null, " Two ", "One")]);
        Assert.False(TemplateDiff.ComplianceChanged(a, b));
    }

    [Fact]
    public void Milestones_steps_messages_and_the_risk_tier_are_listed_and_never_Compliance()
    {
        var v2 = Snap(V1.Gates, [new("Code complete", -10, null), new("Launch review", 1, null)], "High",
            [new("PRE-1", "PreCheck", "Health check", -30, 15, null)], [new("lib1", "T-7", -7)]);
        var changes = TemplateDiff.Between(V1, v2);
        Assert.Contains(changes, c => c is { Area: "Milestone", Kind: "Changed", Subject: "Code complete" });
        Assert.Contains(changes, c => c is { Area: "Milestone", Kind: "Added", Subject: "Launch review" });
        Assert.Contains(changes, c => c is { Area: "Step", Kind: "Added", Subject: "PRE-1" });
        Assert.Contains(changes, c => c is { Area: "Message", Kind: "Added" });
        Assert.Contains(changes, c => c is { Area: "Template", Subject: "Default risk" });
        Assert.DoesNotContain(changes, c => c.Compliance);
        var removed = TemplateDiff.Between(v2, V1);
        Assert.Contains(removed, c => c is { Area: "Milestone", Kind: "Removed", Subject: "Launch review" });
    }

    [Fact]
    public void Gate_names_match_ignoring_case()
    {
        var v2 = Snap([Gate("code freeze", offset: 5, tasks: ["Tag repos"]), V1.Gates[1]], V1.Milestones);
        Assert.DoesNotContain(TemplateDiff.Between(V1, v2), c => c.Area == "Gate");
    }
}
