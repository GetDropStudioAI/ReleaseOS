using System.Runtime.CompilerServices;
using System.Text.Json;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>Golden-file tests for the bulk checklist parser (REOS-28): a valid paste, a paste with every error type, and seeded fuzzing.
/// Regenerate the expected files with UPDATE_GOLDEN=1 and READ THE DIFF before committing.</summary>
public class ChecklistParserGoldenTests
{
    private static string Dir([CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!, "Golden");

    public static readonly ParseContext Ctx = new(
        DefaultGateName: null,
        Gates:
        [
            new("g-freeze", "Code Freeze", true, "Rae Tran", []),
            new("g-comp", "Compliance Sign-off", true, "Priya Nair", ["Evidence attached"]),
            new("g-cab", "CAB Approval", true, "Sam Okafor", []),
            new("g-orphan", "Orphan Gate", false, null, []),
        ],
        Teams: [new("t-client", "client-svc"), new("t-desk", "desk"), new("t-ops", "ops")],
        Users: [new("u-alice", "alice", "alice@corp.com", "Alice Adler"), new("u-bob", null, "bob@corp.com", "Bob Brandt")],
        Products: [new("p-pay", "Payments API"), new("p-card", "Card Portal")]);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Render(ParseResult r) => JsonSerializer.Serialize(new { r.ErrorCount, r.WarningCount, r.Tasks, r.Issues }, Pretty).Replace("\r\n", "\n") + "\n";

    private static void Check(string name, ParseContext? ctx = null)
    {
        var input = File.ReadAllText(Path.Combine(Dir(), name + ".in.txt"));
        var actual = Render(ChecklistParser.Parse(input, ctx ?? Ctx));
        var expectedPath = Path.Combine(Dir(), name + ".expected.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") { File.WriteAllText(expectedPath, actual); return; }
        Assert.True(File.Exists(expectedPath), $"Missing golden file {expectedPath}; run once with UPDATE_GOLDEN=1 and review it");
        Assert.Equal(File.ReadAllText(expectedPath).Replace("\r\n", "\n"), actual);
    }

    [Fact] public void Valid_paste_matches_the_golden_file() => Check("valid");
    [Fact] public void Every_error_type_matches_the_golden_file() => Check("errors");

    [Fact]
    public void Valid_paste_has_no_errors_and_the_expected_warnings()
    {
        var r = ChecklistParser.Parse(File.ReadAllText(Path.Combine(Dir(), "valid.in.txt")), Ctx);
        Assert.Equal(0, r.ErrorCount);
        Assert.Equal(9, r.Tasks.Count);
        Assert.Contains(r.Tasks, t => t.Description == "Evidence attached" && t.Warnings.Any(w => w.Contains("already has a task")));       // exists in the gate already
        Assert.Contains(r.Tasks, t => t.Description == "Evidence attached" && t.Warnings.Any(w => w.Contains("gate owner")));               // no @owner
        Assert.Contains(r.Tasks, t => t.Line == 12 && t.Warnings.Any(w => w.Contains("Same task as line 11")));                             // duplicate inside the paste
        Assert.Equal("Tag repositories with extra spaces", r.Tasks[^1].Description);                                                       // whitespace collapsed
    }

    [Theory]
    [InlineData("UnknownGate", 2)] [InlineData("UnknownOwner", 5)] [InlineData("UnknownProduct", 7)] [InlineData("MultipleOwners", 8)] [InlineData("MultipleProducts", 9)]
    [InlineData("EmptyTask", 10)] [InlineData("BadLine", 12)] [InlineData("NoTargetGate", 1)] [InlineData("NoOwner", 16)]
    public void Each_error_type_is_reported_on_the_right_line(string code, int line)
    {
        var r = ChecklistParser.Parse(File.ReadAllText(Path.Combine(Dir(), "errors.in.txt")), Ctx);
        Assert.Contains(r.Issues, i => i.Code == code && i.Line == line && i.Severity == "Error");
        Assert.True(r.ErrorCount > 0);
    }

    [Fact]
    public void Too_many_lines_is_one_error_and_nothing_is_parsed()
    {
        var text = string.Join("\n", Enumerable.Range(0, ChecklistParser.MaxLines + 1).Select(i => $"- task {i} @alice")) ;
        var r = ChecklistParser.Parse("# CAB Approval\n" + text, Ctx);
        Assert.Equal("TooManyLines", Assert.Single(r.Issues).Code);
        Assert.Empty(r.Tasks);
        Assert.Empty(ChecklistParser.Parse(string.Join("\n", Enumerable.Range(0, ChecklistParser.MaxLines).Select(i => i == 0 ? "# CAB Approval" : $"- t{i} @alice")), Ctx).Issues);   // exactly 500 lines is fine
    }

    [Fact]
    public void Unknown_names_come_with_the_closest_matches()
    {
        var r = ChecklistParser.Parse("# CAB Aproval\n# CAB Approval\n- x @clint-svc\n- y [Payments Ap]", Ctx);
        Assert.Contains("CAB Approval", r.Issues.Single(i => i.Code == "UnknownGate").Suggestions);
        Assert.Contains("@client-svc", r.Issues.Single(i => i.Code == "UnknownOwner").Suggestions);
        Assert.Contains("Payments API", r.Issues.Single(i => i.Code == "UnknownProduct").Suggestions);
    }

    [Fact]
    public void The_gate_chosen_in_the_UI_takes_lines_before_any_heading()
    {
        var r = ChecklistParser.Parse("- first @alice\n# Code Freeze\n- second @alice", Ctx with { DefaultGateName = "CAB Approval" });
        Assert.Equal(0, r.ErrorCount);
        Assert.Equal(["CAB Approval", "Code Freeze"], r.Tasks.Select(t => t.GateName).ToArray());
    }

    [Fact]
    public void Owner_resolution_order_is_team_then_user_handle_and_a_dot_or_second_at_means_email()
    {
        var ctx = Ctx with { Teams = [.. Ctx.Teams, new("t-clash", "alice")] };                          // a team and a user both called alice: the team wins
        var r = ChecklistParser.Parse("# CAB Approval\n- a @alice\n- b @bob@corp.com\n- c @alice@corp.com\n- d @bob.brandt", ctx);
        Assert.Equal(["Team", "User", "User"], r.Tasks.Take(3).Select(t => t.OwnerKind).ToArray());
        Assert.Equal("t-clash", r.Tasks[0].OwnerId);
        Assert.Contains(r.Issues, i => i.Code == "UnknownOwner" && i.Line == 5);                        // "bob.brandt" has a dot, so it is an email and matches nobody
    }

    // ---- fuzz: whatever is pasted, the parser answers, deterministically, and no line is silently lost ---------------------------------------------
    [Fact]
    public void Fuzzed_input_never_throws_is_deterministic_and_accounts_for_every_task_line()
    {
        var rnd = new Random(20260929);
        string[] atoms = ["#", "# ", "// ", "-", "- ", "@", "@alice", "@client-svc", "@x@y.z", "[", "]", "[Payments API]", "[", "CAB Approval", "Code Freeze", "nope", "\t", "  ", "\r\n", "\n", "\r",
                          "é", "日本語", "😀", "\u0000", "​", new string('x', 300), "@@", "[[]]", "- @", "-[]", "#[x]"];
        for (var round = 0; round < 3000; round++)
        {
            var text = string.Concat(Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => atoms[rnd.Next(atoms.Length)]));
            ParseResult a = null!, b = null!;
            var ex = Record.Exception(() => { a = ChecklistParser.Parse(text, Ctx); b = ChecklistParser.Parse(text, Ctx); });
            Assert.True(ex is null, $"threw on input #{round}: {text.Replace("\n", "\\n")}: {ex}");
            Assert.Equal(Render(a), Render(b));                                                                                    // deterministic

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var issue in a.Issues) Assert.InRange(issue.Line, 1, lines.Length + 1);
            foreach (var t in a.Tasks) { Assert.False(string.IsNullOrWhiteSpace(t.Description)); Assert.False(string.IsNullOrEmpty(t.GateId)); }
            for (var i = 0; i < lines.Length; i++)                                                                                 // every task line is either a task or an error, never lost
                if (lines[i].TrimStart().StartsWith('-'))
                    Assert.True(a.Tasks.Any(t => t.Line == i + 1) || a.Issues.Any(x => x.Line == i + 1 && x.Severity == "Error"), $"line {i + 1} vanished in input #{round}: {text.Replace("\n", "\\n")}");
            Assert.Equal(a.Issues.Any(x => x.Severity == "Error"), a.ErrorCount > 0);
        }
    }
}
