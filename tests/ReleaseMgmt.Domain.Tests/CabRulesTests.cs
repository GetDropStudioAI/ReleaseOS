using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-175/177: the pure CAB rules (status machine and the three separation-of-duties rules). The services and the triggers must agree with these.</summary>
public class CabRulesTests
{
    [Theory]
    [InlineData("Draft", "Submitted", true)]
    [InlineData("Draft", "Cancelled", true)]
    [InlineData("Submitted", "Approved", true)]
    [InlineData("Submitted", "Rejected", true)]
    [InlineData("Submitted", "Cancelled", true)]
    [InlineData("Submitted", "Implemented", true)]   // legal as a move; only Emergency may take it (checked apart)
    [InlineData("Approved", "Implemented", true)]
    [InlineData("Approved", "Cancelled", true)]
    [InlineData("Implemented", "Closed", true)]
    [InlineData("Draft", "Approved", false)]
    [InlineData("Draft", "Implemented", false)]
    [InlineData("Submitted", "Draft", false)]
    [InlineData("Submitted", "Closed", false)]
    [InlineData("Approved", "Closed", false)]
    [InlineData("Approved", "Rejected", false)]
    [InlineData("Implemented", "Cancelled", false)]
    [InlineData("Rejected", "Draft", false)]
    [InlineData("Closed", "Draft", false)]
    [InlineData("Cancelled", "Submitted", false)]
    public void The_change_status_machine(string from, string to, bool legal) => Assert.Equal(legal, CabRules.IsLegalChangeMove(from, to));

    [Fact]
    public void Every_move_between_distinct_statuses_not_listed_as_legal_is_refused()
    {
        var legal = 9;
        var n = ChangeStatuses.All.SelectMany(a => ChangeStatuses.All.Where(b => b != a).Select(b => (a, b))).Count(m => CabRules.IsLegalChangeMove(m.a, m.b));
        Assert.Equal(legal, n);
    }

    [Theory]
    [InlineData("Planned", "InProgress", true)]
    [InlineData("Planned", "Cancelled", true)]
    [InlineData("InProgress", "Completed", true)]
    [InlineData("InProgress", "Cancelled", true)]
    [InlineData("Planned", "Completed", false)]
    [InlineData("InProgress", "Planned", false)]
    [InlineData("Completed", "Cancelled", false)]
    [InlineData("Cancelled", "InProgress", false)]
    public void The_meeting_status_machine(string from, string to, bool legal) => Assert.Equal(legal, CabRules.IsLegalMeetingMove(from, to));

    [Theory]
    [InlineData("rte", "rte", "Approved", true)]
    [InlineData("rte", "rte", "Rejected", true)]
    [InlineData("rte", "rte", "Deferred", false)]
    [InlineData("rte", "gov1", "Approved", false)]
    public void Rule_1_a_requester_does_not_approve_or_reject_their_own_change(string requester, string decider, string decision, bool selfApproval) =>
        Assert.Equal(selfApproval, CabRules.IsSelfApproval(requester, decider, decision));

    [Fact]
    public void Rule_2_the_chair_who_raised_the_change_is_not_its_only_approver()
    {
        Assert.True(CabRules.ChairIsSoleApprover("rm", true, "dev", "rm", []));                   // keyed it in, no one else approved
        Assert.True(CabRules.ChairIsSoleApprover("rm", true, "dev", "rm", ["dev"]));              // the only other approver is also one who raised it
        Assert.False(CabRules.ChairIsSoleApprover("rm", true, "dev", "rm", ["gov1"]));            // a second approver who raised nothing
        Assert.False(CabRules.ChairIsSoleApprover("rm", false, "dev", "rm", []));                 // not the chair of this change
        Assert.False(CabRules.ChairIsSoleApprover("rm", true, "dev", "rte", []));                 // the chair raised nothing
    }

    [Fact]
    public void The_rule_texts_are_the_words_the_triggers_raise()
    {
        var schema = File.ReadAllText(Path.Combine(RepoRoot(), "db", "schema.sql"));
        foreach (var text in new[] { CabRuleText.SelfApproval, CabRuleText.ChairSoleApprover, CabRuleText.EmergencyNeedsReview })
            Assert.Contains($"'{text}'", schema);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "db", "schema.sql"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
