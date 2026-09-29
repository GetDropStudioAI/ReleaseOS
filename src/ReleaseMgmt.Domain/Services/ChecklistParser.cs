using System.Text.RegularExpressions;

namespace ReleaseMgmt.Domain.Services;

public sealed record ParseGate(string Id, string Name, bool HasOwner, string? OwnerName, IReadOnlyCollection<string> ExistingTasks);
public sealed record ParseTeam(string Id, string Handle);
public sealed record ParseUser(string Id, string? Handle, string Email, string Name);
public sealed record ParseProduct(string Id, string Name);

/// <summary>Everything the parser may resolve names against. The parser itself never touches a database.</summary>
public sealed record ParseContext(string? DefaultGateName, IReadOnlyList<ParseGate> Gates, IReadOnlyList<ParseTeam> Teams, IReadOnlyList<ParseUser> Users, IReadOnlyList<ParseProduct> Products);

/// <summary>One task the commit would insert. OwnerKind is "User", "Team" or "Gate" (falls back to the gate's owner, flagged as a warning).</summary>
public sealed record ParsedTask(int Line, string GateId, string GateName, string Description, string OwnerKind, string? OwnerId, string? OwnerName, string? ProductId, string? ProductName, IReadOnlyList<string> Warnings);

public sealed record ParseIssue(int Line, string Severity, string Code, string Message, IReadOnlyList<string> Suggestions);

public sealed record ParseResult(IReadOnlyList<ParsedTask> Tasks, IReadOnlyList<ParseIssue> Issues)
{
    public int ErrorCount => Issues.Count(i => i.Severity == "Error");
    public int WarningCount => Issues.Count(i => i.Severity == "Warning") + Tasks.Sum(t => t.Warnings.Count);
}

/// <summary>
/// The bulk checklist grammar (PROJECT_SCOPE 5.1):
/// <code>
/// # Gate name                    switch target gate (case-insensitive, must exist)
/// - task text @owner [Product]   a task
/// // comment                     ignored, as are blank lines
/// </code>
/// Any error blocks a commit. Errors carry the line number and, for unknown names, the closest matches.
/// </summary>
public static partial class ChecklistParser
{
    public const int MaxLines = 500;
    public const string ExpectedForms = "Expected `# Gate name`, `- task text @owner [Product]`, `// comment` or a blank line";

    [GeneratedRegex(@"\[([^\[\]]*)\]")] private static partial Regex ProductTag();
    [GeneratedRegex(@"(?<=^|\s)@\S*")] private static partial Regex OwnerTag();

    public static ParseResult Parse(string? text, ParseContext ctx)
    {
        var issues = new List<ParseIssue>();
        var tasks = new List<ParsedTask>();
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > MaxLines)
            return new ParseResult([], [new ParseIssue(MaxLines + 1, "Error", "TooManyLines", $"At most {MaxLines} lines can be pasted at once; this has {lines.Length}. Split it into batches", [])]);

        ParseGate? gate = null;
        var gateAnnounced = false;
        if (!string.IsNullOrWhiteSpace(ctx.DefaultGateName))
            gate = ctx.Gates.FirstOrDefault(g => string.Equals(g.Name, ctx.DefaultGateName, StringComparison.OrdinalIgnoreCase));
        var seen = new Dictionary<(string Gate, string Text), int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            if (line[0] == '#')
            {
                var name = line[1..].Trim();
                if (name.Length == 0) { issues.Add(Err(n, "BadLine", "A `#` line needs a gate name after it", [])); gate = null; gateAnnounced = true; continue; }
                var found = ctx.Gates.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (found is null) { issues.Add(Err(n, "UnknownGate", $"There is no gate called \"{name}\" on this train", Closest(name, ctx.Gates.Select(g => g.Name)))); gate = null; gateAnnounced = true; continue; }
                gate = found; gateAnnounced = true;
                continue;
            }

            if (line[0] != '-') { issues.Add(Err(n, "BadLine", $"Cannot read this line. {ExpectedForms}", [])); continue; }

            // ---- a task line -----------------------------------------------------------------------------------------------------------
            var body = line[1..].Trim();
            var lineIssues = new List<ParseIssue>();

            string? productName = null;
            var tags = ProductTag().Matches(body);
            if (tags.Count > 1) lineIssues.Add(Err(n, "MultipleProducts", "A task can link to one product; found more than one [..]", []));
            if (tags.Count >= 1) { productName = tags[0].Groups[1].Value.Trim(); body = ProductTag().Replace(body, " "); }

            string? ownerToken = null;
            var owners = OwnerTag().Matches(body);
            if (owners.Count > 1) lineIssues.Add(Err(n, "MultipleOwners", "A task has one owner; found more than one @..", []));
            if (owners.Count >= 1) { ownerToken = owners[0].Value[1..]; body = OwnerTag().Replace(body, " "); }

            var description = Regex.Replace(body, @"\s+", " ").Trim();
            if (description.Length == 0) lineIssues.Add(Err(n, "EmptyTask", "This task has no description", []));
            if (gate is null && lineIssues.All(x => x.Code != "UnknownGate"))
            {
                // Either no `#` line yet and no gate chosen in the UI, or the last `#` line named a gate that does not exist (already reported).
                if (!gateAnnounced) lineIssues.Add(Err(n, "NoTargetGate", "No gate for this task: put a `# Gate name` line before it, or pick a gate in the drawer", Closest("", ctx.Gates.Select(g => g.Name))));
                else lineIssues.Add(Err(n, "NoTargetGate", "No valid gate for this task (the last `#` line was not a known gate)", []));
            }

            string ownerKind = "Gate"; string? ownerId = null, ownerName = null;
            var warnings = new List<string>();
            if (ownerToken is not null)
            {
                var (kind, id, name) = ResolveOwner(ownerToken, ctx);
                if (kind is null)
                {
                    var pool = ctx.Teams.Select(t => "@" + t.Handle).Concat(ctx.Users.Where(u => u.Handle != null).Select(u => "@" + u.Handle!)).Concat(ctx.Users.Select(u => u.Email));
                    lineIssues.Add(Err(n, "UnknownOwner", $"No team or user matches @{ownerToken}", Closest("@" + ownerToken, pool)));
                }
                else { ownerKind = kind; ownerId = id; ownerName = name; }
            }
            else if (gate is not null)
            {
                if (!gate.HasOwner) lineIssues.Add(Err(n, "NoOwner", $"This task has no @owner and gate \"{gate.Name}\" has no owner to fall back to", []));
                else { ownerName = gate.OwnerName; warnings.Add($"No @owner: the gate owner ({gate.OwnerName ?? "unknown"}) will own this task"); }
            }

            string? productId = null;
            if (productName is not null)
            {
                var p = ctx.Products.FirstOrDefault(x => string.Equals(x.Name, productName, StringComparison.OrdinalIgnoreCase));
                if (p is null) lineIssues.Add(Err(n, "UnknownProduct", $"No bundled product called \"{productName}\" on this train", Closest(productName, ctx.Products.Select(x => x.Name))));
                else { productId = p.Id; productName = p.Name; }
            }

            if (gate is not null && description.Length > 0)
            {
                var key = (gate.Id, description.ToLowerInvariant());
                if (seen.TryGetValue(key, out var first)) warnings.Add($"Same task as line {first} in this gate");
                else seen[key] = n;
                if (gate.ExistingTasks.Any(t => string.Equals(t.Trim(), description, StringComparison.OrdinalIgnoreCase))) warnings.Add("This gate already has a task with this description");
            }

            issues.AddRange(lineIssues);
            if (lineIssues.Count == 0 && gate is not null) tasks.Add(new ParsedTask(n, gate.Id, gate.Name, description, ownerKind, ownerId, ownerName, productId, productName, warnings));
        }
        return new ParseResult(tasks, [.. issues.OrderBy(x => x.Line)]);
    }

    private static ParseIssue Err(int line, string code, string message, IReadOnlyList<string> suggestions) => new(line, "Error", code, message, suggestions);

    /// <summary>@handle matches Teams.Handle, then Users.Handle; a token with a second @ or a dot is a Users.Email (5.1). Case-insensitive.</summary>
    private static (string? Kind, string? Id, string? Name) ResolveOwner(string token, ParseContext ctx)
    {
        if (token.Length == 0) return (null, null, null);
        if (token.Contains('@') || token.Contains('.'))
        {
            var u = ctx.Users.FirstOrDefault(x => string.Equals(x.Email, token, StringComparison.OrdinalIgnoreCase));
            return u is null ? (null, null, null) : ("User", u.Id, u.Name);
        }
        var t = ctx.Teams.FirstOrDefault(x => string.Equals(x.Handle, token, StringComparison.OrdinalIgnoreCase));
        if (t is not null) return ("Team", t.Id, "@" + t.Handle);
        var h = ctx.Users.FirstOrDefault(x => x.Handle != null && string.Equals(x.Handle, token, StringComparison.OrdinalIgnoreCase));
        return h is null ? (null, null, null) : ("User", h.Id, h.Name);
    }

    /// <summary>Up to three names within a small edit distance (or containing the text), nearest first: the "closest matches" of 5.1.</summary>
    public static IReadOnlyList<string> Closest(string typed, IEnumerable<string> candidates)
    {
        var t = typed.Trim().ToLowerInvariant();
        var pool = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (t.Length == 0) return [.. pool.Take(3)];
        var limit = Math.Max(2, t.Length / 3);
        return [.. pool.Select(c => (c, d: c.ToLowerInvariant().Contains(t) || t.Contains(c.ToLowerInvariant()) ? 0 : Distance(t, c.ToLowerInvariant())))
            .Where(x => x.d <= limit).OrderBy(x => x.d).ThenBy(x => x.c, StringComparer.OrdinalIgnoreCase).Take(3).Select(x => x.c)];
    }

    private static int Distance(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1]; cur[0] = i;
            for (var j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }
}
