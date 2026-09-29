using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

public class DependencyGraphTests
{
    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> G(params (string Step, string[] Deps)[] edges) =>
        edges.ToDictionary(e => e.Step, e => (IReadOnlyCollection<string>)e.Deps);

    [Fact] public void Empty_graph_has_no_cycle() => Assert.Null(DependencyGraph.FindCycle(G()));
    [Fact] public void A_chain_is_fine() => Assert.Null(DependencyGraph.FindCycle(G(("C", ["B"]), ("B", ["A"]), ("A", []))));
    [Fact] public void A_diamond_is_not_a_cycle() => Assert.Null(DependencyGraph.FindCycle(G(("D", ["B", "C"]), ("B", ["A"]), ("C", ["A"]), ("A", []))));   // A reached twice, no back edge

    [Fact]
    public void Two_step_cycle_is_reported_in_order()
    {
        var c = DependencyGraph.FindCycle(G(("A", ["B"]), ("B", ["A"])))!;
        Assert.Equal(["A", "B", "A"], c);
    }

    [Fact]
    public void A_long_cycle_hidden_behind_a_tail_is_found()
    {
        // X -> A -> B -> C -> A ; X itself is not on the cycle
        var c = DependencyGraph.FindCycle(G(("X", ["A"]), ("A", ["B"]), ("B", ["C"]), ("C", ["A"])))!;
        Assert.Equal(c[0], c[^1]);
        Assert.Equal(new[] { "A", "B", "C" }, c.Take(c.Count - 1).Order().ToArray());
        Assert.DoesNotContain("X", c);
    }

    [Fact] public void A_self_dependency_is_a_cycle() => Assert.Equal(["A", "A"], DependencyGraph.FindCycle(G(("A", ["A"]))));

    [Fact]
    public void Dependencies_on_steps_with_no_entry_are_ignored()
        => Assert.Null(DependencyGraph.FindCycle(G(("A", ["ghost"]))));

    [Fact]
    public void A_very_long_chain_does_not_overflow_the_stack_and_a_closing_edge_is_found()
    {
        var n = 50_000;
        var g = Enumerable.Range(0, n).ToDictionary(i => $"s{i:D6}", i => (IReadOnlyCollection<string>)(i == 0 ? [] : [$"s{i - 1:D6}"]));
        Assert.Null(DependencyGraph.FindCycle(g));
        g["s000000"] = [$"s{n - 1:D6}"];                       // close the loop
        Assert.NotNull(DependencyGraph.FindCycle(g));
    }
}
