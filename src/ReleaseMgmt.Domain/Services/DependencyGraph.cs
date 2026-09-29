namespace ReleaseMgmt.Domain.Services;

/// <summary>Runbook dependency rules that do not need a database (the schema only stops a step depending on itself; longer cycles are the domain's job).</summary>
public static class DependencyGraph
{
    /// <summary>
    /// Looks for a cycle in "step depends on these steps" edges. Returns the steps of one cycle in order, first step repeated at the end
    /// (A -> B -> A), or null if the graph is acyclic. Iterative, so a very long chain cannot overflow the stack.
    /// </summary>
    public static IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOn)
    {
        var state = new Dictionary<string, int>();   // 1 = on the current path, 2 = fully explored
        foreach (var start in dependsOn.Keys.Order(StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(start) == 2) continue;
            var path = new List<string>();
            var stack = new Stack<(string Node, IEnumerator<string> Next)>();
            state[start] = 1; path.Add(start);
            stack.Push((start, Edges(dependsOn, start).GetEnumerator()));
            while (stack.Count > 0)
            {
                var (node, it) = stack.Peek();
                if (!it.MoveNext()) { state[node] = 2; path.RemoveAt(path.Count - 1); stack.Pop(); continue; }
                var next = it.Current;
                switch (state.GetValueOrDefault(next))
                {
                    case 1:
                        var from = path.IndexOf(next);
                        return [.. path.Skip(from), next];
                    case 0:
                        state[next] = 1; path.Add(next);
                        stack.Push((next, Edges(dependsOn, next).GetEnumerator()));
                        break;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> Edges(IReadOnlyDictionary<string, IReadOnlyCollection<string>> g, string node) =>
        (g.TryGetValue(node, out var e) ? e : []).Order(StringComparer.Ordinal);
}
