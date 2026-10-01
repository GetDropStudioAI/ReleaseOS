using System.Diagnostics;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>
/// Security review (resources and injection, docs/security/scan-resources-injection.md): pure parsers that run on user text must cost time and memory in
/// proportion to their input, and must not accept values the rest of the system refuses. Each test reproduces one finding (SEC-D numbers in the report).
/// The time bounds are generous (the fixed code takes milliseconds); they exist so the unfixed quadratic or unbounded code fails visibly.
/// </summary>
public class ResourceBoundsTests
{
    // ---- SEC-D1: the comm template parser -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Token_parser_is_linear_and_caps_its_error_list_on_a_template_of_unclosed_braces()
    {
        var text = new string('{', 400_000);   // every '{' is unclosed: the old code scanned to the end once per brace (quadratic) and kept one error per brace
        var sw = Stopwatch.StartNew();
        var p = TokenParser.Parse(text);
        sw.Stop();
        Assert.False(p.IsValid);
        Assert.True(p.Errors.Count <= 101, $"{p.Errors.Count} errors kept for one template");
        Assert.Contains(p.Errors, e => e.Message.Contains("more problems", StringComparison.Ordinal));   // the cut is said, not silent
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"parsing took {sw.Elapsed}");   // ~0.2 s alone; the quadratic code takes minutes. 2 s failed under a loaded parallel run
        Assert.Equal(text, string.Concat(p.Segments.OfType<TemplateSegment.Literal>().Select(l => l.Text)));   // errored text still stays literal
    }

    [Fact]
    public void Token_parser_still_reports_each_problem_of_a_normal_template()
    {
        var p = TokenParser.Parse("{Status} { {Nope} }x{");
        Assert.Equal([TokenErrorKinds.UnclosedBrace, TokenErrorKinds.UnknownToken, TokenErrorKinds.StrayBrace, TokenErrorKinds.UnclosedBrace], p.Errors.Select(e => e.Kind));
        Assert.Equal(["Status"], p.Tokens);
    }

    // ---- SEC-D2: import suggestions and owner cells ---------------------------------------------------------------------------------------------------

    private static readonly string[] Pool = [.. Enumerable.Range(0, 300).Select(i => $"person{i:D3}@example.com")];

    [Fact]
    public void A_suggestion_for_an_absurdly_long_cell_costs_nothing()
    {
        var sw = Stopwatch.StartNew();
        var near = RowParser.Suggest("a@" + new string('b', 1_000_000), Pool, 2);   // edit distance of 1M x 20 characters, 300 times, was ~minutes of CPU
        sw.Stop();
        Assert.Empty(near);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"suggesting took {sw.Elapsed}");
    }

    [Fact]
    public void Suggestions_still_find_the_near_miss()
    {
        Assert.Equal(["person007@example.com"], RowParser.Suggest("person007@exampel.com", Pool));
        Assert.Equal(["Code Freeze"], RowParser.Suggest("Code Freez", ["Code Freeze", "QA Sign-off", "CAB Approval"]));
    }

    [Fact]
    public void One_import_plan_shares_one_suggestion_budget()
    {
        var budget = new SuggestBudget(cells: 21 * 21 * 300 + 1);   // exactly one full pass of a 21-character name over the 300 names
        Assert.Single(RowParser.Suggest("person007@exampel.com", Pool, 1, budget));
        Assert.Empty(RowParser.Suggest("person008@exampel.com", Pool, 1, budget));   // spent: the rest of this plan's errors carry no suggestion
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void Owner_and_member_cells_are_capped_at_the_length_of_an_email_address()
    {
        var owner = new ColumnSpec("Owner", true, ColumnType.Owner);
        var (_, error) = RowParser.Cell(owner, "a@" + new string('b', 300));
        Assert.Equal("Owner is longer than 254 characters", error);
        Assert.Null(RowParser.Cell(owner, "dana@x.com").Error);

        var members = new ColumnSpec("Members", true, ColumnType.EmailList, AllowEmpty: true);
        var (_, listError) = RowParser.Cell(members, "dana@x.com;a@" + new string('b', 300));
        Assert.NotNull(listError);
        Assert.Contains("longer than 254 characters", listError);
    }

    // ---- SEC-D4: the bulk checklist paste parser ------------------------------------------------------------------------------------------------------

    private static ParseContext Ctx() => new(null,
        [new ParseGate("g1", "Code Freeze", true, "Rae", [])],
        [.. Enumerable.Range(0, 50).Select(i => new ParseTeam($"tm{i}", $"team{i:D2}"))],
        [.. Enumerable.Range(0, 100).Select(i => new ParseUser($"u{i}", $"user{i:D3}", $"user{i:D3}@example.com", $"User {i}"))],
        [new ParseProduct("p1", "Payments API")]);

    [Fact]
    public void A_pasted_line_longer_than_the_limit_is_an_error_and_is_not_resolved()
    {
        var sw = Stopwatch.StartNew();
        var r = ChecklistParser.Parse("# Code Freeze\n- Tag repos @" + new string('x', 200_000), Ctx());   // the owner token was edit-distanced against 250 names
        sw.Stop();
        Assert.Empty(r.Tasks);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("LineTooLong", issue.Code);
        Assert.Equal(2, issue.Line);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"parsing took {sw.Elapsed}");
    }

    [Fact]
    public void A_pasted_task_description_is_held_to_the_import_limit_of_2000_characters()
    {
        var r = ChecklistParser.Parse("# Code Freeze\n- " + new string('d', 2001) + " @user001", Ctx());
        Assert.Empty(r.Tasks);
        Assert.Equal("TaskTooLong", Assert.Single(r.Issues).Code);
        Assert.Single(ChecklistParser.Parse("# Code Freeze\n- " + new string('d', 2000) + " @user001", Ctx()).Tasks);
    }

    [Fact]
    public void Closest_match_ignores_a_typed_name_too_long_to_be_a_typo()
    {
        var sw = Stopwatch.StartNew();
        Assert.Empty(ChecklistParser.Closest(new string('x', 500_000), Ctx().Users.Select(u => u.Email)));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"closest took {sw.Elapsed}");
        Assert.Equal(["user001@example.com"], ChecklistParser.Closest("user001@exampel.com", Ctx().Users.Select(u => u.Email)).Take(1));
    }
}
