using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-44: every token of the PROJECT_SCOPE 5.2 allowlist has its own test (value and, for lists and optional values, the empty rendering).</summary>
public class CommTokenTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static DateTime Utc(string s) => DateTime.SpecifyKind(DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);
    private static DateOnly D(string s) => DateOnly.Parse(s);

    /// <summary>Thu 29 Oct 2026 15:12 CDT; target Fri 30 Oct; window 01:00-05:00 CT; four gates; 85 of 100 tasks.</summary>
    private static HydrationSnapshot Full() => new(
        "t1", 51, Utc("2026-10-29T20:12:00Z"), "R26.24 Q4 Payments Consolidated", D("2026-10-30"), "Gated", "CHG0041872", null,
        Utc("2026-10-30T06:00:00Z"), Utc("2026-10-30T10:00:00Z"),
        [new("Payments API", "4.5.0"), new("Card Portal", "2.1.0"), new("Ledger Svc", "3.8.2"), new("Fraud Engine", "1.12.0")],
        [
            new("Code Freeze", "Certified", 1, D("2026-10-23"), Utc("2026-10-22T20:00:00Z")),
            new("QA Sign-off", "Certified", 2, D("2026-10-27"), Utc("2026-10-26T20:00:00Z")),
            new("Compliance Sign-off", "InProgress", 3, D("2026-10-28"), null),
            new("CAB Approval", "Pending", 4, D("2026-10-29"), null),
        ],
        85, 100,
        [
            new("Medium", "Migration 0042 slow in staging (14 vs 8 min)", "Dana Ortiz", Utc("2026-10-27T15:00:00Z")),
            new("Critical", "Card Portal pen-test exception unsigned", "Priya Nair", Utc("2026-10-28T15:00:00Z")),
            new("Low", "Typo in release notes", null, Utc("2026-10-26T15:00:00Z")),
        ],
        new("GoWithConditions", "Marcus Bell", Utc("2026-10-29T20:00:00Z"),
            [new("Card Portal pen-test exceptions signed by CISO", "Priya Nair", Utc("2026-10-30T05:30:00Z"))]),
        [
            new("High", "Refund report slow", "Use the nightly export", "Open", "INC-77"),
            new("Low", "Cosmetic glitch", null, "Accepted", null),
            new("Critical", "Old defect", null, "Resolved", "INC-1"),
        ],
        ["Rae Tanaka", "Marcus Bell"], new HashSet<DateOnly>());

    private static string Tok(string name, HydrationSnapshot? s = null, CommTarget target = CommTarget.PlainText) =>
        CommHydrator.Hydrate(s ?? Full(), Chicago, null, "{" + name + "}", target).Text;

    private static HydrationSnapshot Empty() => Full() with
    {
        Products = [], Gates = [], OpenBlockers = [], Decision = null, KnownIssues = [], Owners = [], TasksDone = 0, TasksTotal = 0,
        ChangeTicket = null, WindowStart = null, WindowEnd = null,
    };

    // ---- the allowlist itself
    [Fact]
    public void Allowlist_is_exactly_the_scope_list()
    {
        var scope = "ReleaseTitle TargetDate DaysToTarget Status Window ChangeTicket ProductList ProductCount GateTable NextGate NextGateDue TasksDone TasksTotal PercentComplete BlockerCount CriticalBlockers BlockerList Owners GoNoGoDecision Conditions KnownIssues CloseCode".Split(' ');
        Assert.Equal(scope.Order(), CommTokens.Allowlist.Order());
        Assert.Equal(22, CommTokens.Allowlist.Count);
        Assert.Equal(scope.Order(), CommHydrator.Values(Full(), Chicago).Keys.Order());   // the hydrator supplies a value for every token
        Assert.All(CommTokens.Allowlist, n => Assert.False(string.IsNullOrWhiteSpace(CommTokens.Descriptions[n])));
    }

    // ---- one test per token
    [Fact] public void ReleaseTitle() => Assert.Equal("R26.24 Q4 Payments Consolidated", Tok("ReleaseTitle"));
    [Fact] public void TargetDate() => Assert.Equal("Fri 30 Oct", Tok("TargetDate"));
    [Fact] public void TargetDate_shows_the_year_when_it_is_not_this_year() => Assert.Equal("Fri 8 Jan 2027", Tok("TargetDate", Full() with { TargetDate = D("2027-01-08") }));
    [Fact] public void DaysToTarget_is_business_days_in_the_display_zone() => Assert.Equal("1", Tok("DaysToTarget"));
    [Fact] public void DaysToTarget_skips_weekends_and_holidays() =>
        Assert.Equal("1", Tok("DaysToTarget", Full() with { AsOf = Utc("2026-10-30T20:00:00Z"), TargetDate = D("2026-11-03"), Holidays = new HashSet<DateOnly> { D("2026-11-02") } }));   // Fri -> Tue, Mon is a holiday
    [Fact] public void DaysToTarget_is_zero_on_the_day_and_minus_after() { Assert.Equal("0", Tok("DaysToTarget", Full() with { AsOf = Utc("2026-10-30T14:00:00Z") })); Assert.Equal("−1", Tok("DaysToTarget", Full() with { AsOf = Utc("2026-11-02T14:00:00Z") })); }
    [Fact] public void DaysToTarget_uses_the_local_date_not_UTC() =>
        Assert.Equal("0", Tok("DaysToTarget", Full() with { AsOf = Utc("2026-10-31T02:00:00Z") }));   // 21:00 Fri 30 Oct in Chicago, still the target day
    [Fact] public void Status() => Assert.Equal("Gated", Tok("Status"));
    [Fact] public void Window_same_day() => Assert.Equal("Fri 30 Oct 01:00–05:00 CT", Tok("Window"));
    [Fact] public void Window_across_midnight() => Assert.Equal("Fri 30 Oct 22:00 – Sat 31 Oct 02:00 CT", Tok("Window", Full() with { WindowStart = Utc("2026-10-31T03:00:00Z"), WindowEnd = Utc("2026-10-31T07:00:00Z") }));
    [Fact] public void Window_not_scheduled() => Assert.Equal("Not scheduled", Tok("Window", Empty()));
    [Fact] public void Window_in_an_unlisted_zone_shows_the_utc_offset() =>
        Assert.Equal("Fri 30 Oct 07:00–11:00 UTC+01:00", CommHydrator.Hydrate(Full(), TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos"), null, "{Window}", CommTarget.PlainText).Text);
    [Fact] public void ChangeTicket() => Assert.Equal("CHG0041872", Tok("ChangeTicket"));
    [Fact] public void ChangeTicket_none() => Assert.Equal("Not assigned", Tok("ChangeTicket", Empty()));
    [Fact] public void ProductList_is_one_line_sorted_by_name() => Assert.Equal("Card Portal 2.1.0 · Fraud Engine 1.12.0 · Ledger Svc 3.8.2 · Payments API 4.5.0", Tok("ProductList"));
    [Fact] public void ProductList_empty_is_None() => Assert.Equal("None", Tok("ProductList", Empty()));
    [Fact] public void ProductCount() { Assert.Equal("4", Tok("ProductCount")); Assert.Equal("0", Tok("ProductCount", Empty())); }

    [Fact]
    public void GateTable_aligns_names_and_marks_state_with_a_glyph_and_a_word() =>
        Assert.Equal(
            "Code Freeze ........... ✓ Certified Thu 22 Oct\nQA Sign-off ........... ✓ Certified Mon 26 Oct\nCompliance Sign-off ... ◐ In progress due Wed 28 Oct\nCAB Approval .......... ○ Pending due Thu 29 Oct",
            Tok("GateTable"));
    [Fact] public void GateTable_no_gates_is_None() => Assert.Equal("None", Tok("GateTable", Empty()));
    [Fact] public void GateTable_waived_and_failed_never_read_as_certified() =>
        Assert.Equal("A ... ▲ Waived due Fri 23 Oct\nB ... ✗ Failed due Mon 26 Oct",
            Tok("GateTable", Full() with { Gates = [new("A", "Waived", 1, D("2026-10-23"), null), new("B", "Failed", 2, D("2026-10-26"), null)] }));

    [Fact] public void NextGate_is_the_first_gate_not_certified_or_waived() => Assert.Equal("Compliance Sign-off", Tok("NextGate"));
    [Fact] public void NextGate_skips_a_waived_gate() => Assert.Equal("B", Tok("NextGate", Full() with { Gates = [new("A", "Waived", 1, D("2026-10-23"), null), new("B", "Pending", 2, D("2026-10-26"), null)] }));
    [Fact] public void NextGate_none_when_every_gate_is_done() =>
        Assert.Equal("None", Tok("NextGate", Full() with { Gates = [new("A", "Certified", 1, D("2026-10-23"), Utc("2026-10-22T20:00:00Z"))] }));
    [Fact] public void NextGate_no_gates_is_None() => Assert.Equal("None", Tok("NextGate", Empty()));
    [Fact] public void NextGateDue() => Assert.Equal("Wed 28 Oct", Tok("NextGateDue"));
    [Fact] public void NextGateDue_none() => Assert.Equal("None", Tok("NextGateDue", Empty()));
    [Fact] public void TasksDone() { Assert.Equal("85", Tok("TasksDone")); Assert.Equal("0", Tok("TasksDone", Empty())); }
    [Fact] public void TasksTotal() { Assert.Equal("100", Tok("TasksTotal")); Assert.Equal("0", Tok("TasksTotal", Empty())); }
    [Fact] public void PercentComplete() => Assert.Equal("85%", Tok("PercentComplete"));
    [Fact] public void PercentComplete_with_no_tasks_is_0_not_a_division_error() => Assert.Equal("0%", Tok("PercentComplete", Empty()));
    [Fact] public void PercentComplete_rounds_half_up() { Assert.Equal("67%", Tok("PercentComplete", Full() with { TasksDone = 2, TasksTotal = 3 })); Assert.Equal("13%", Tok("PercentComplete", Full() with { TasksDone = 1, TasksTotal = 8 })); }
    [Fact] public void BlockerCount() { Assert.Equal("3", Tok("BlockerCount")); Assert.Equal("0", Tok("BlockerCount", Empty())); }
    [Fact] public void CriticalBlockers_lists_only_Critical() => Assert.Equal("• Card Portal pen-test exception unsigned · Priya Nair", Tok("CriticalBlockers"));
    [Fact] public void CriticalBlockers_empty_is_None() { Assert.Equal("None", Tok("CriticalBlockers", Empty())); Assert.Equal("None", Tok("CriticalBlockers", Full() with { OpenBlockers = [new("High", "x", null, Utc("2026-10-27T15:00:00Z"))] })); }
    [Fact]
    public void BlockerList_is_worst_first_and_leaves_out_a_missing_owner() =>
        Assert.Equal("• Critical · Card Portal pen-test exception unsigned · Priya Nair\n• Medium · Migration 0042 slow in staging (14 vs 8 min) · Dana Ortiz\n• Low · Typo in release notes", Tok("BlockerList"));
    [Fact] public void BlockerList_empty_is_None() => Assert.Equal("None", Tok("BlockerList", Empty()));
    [Fact] public void Owners_is_one_line() => Assert.Equal("Rae Tanaka · Marcus Bell", Tok("Owners"));
    [Fact] public void Owners_empty_is_None() => Assert.Equal("None", Tok("Owners", Empty()));
    [Fact] public void GoNoGoDecision_reads_like_words() { Assert.Equal("Go with conditions", Tok("GoNoGoDecision")); Assert.Equal("Go", Tok("GoNoGoDecision", Full() with { Decision = Full().Decision! with { Decision = "Go" } })); Assert.Equal("No-Go", Tok("GoNoGoDecision", Full() with { Decision = Full().Decision! with { Decision = "NoGo" } })); }
    [Fact] public void GoNoGoDecision_none_yet() => Assert.Equal("Not yet recorded", Tok("GoNoGoDecision", Empty()));
    [Fact] public void Conditions_lists_open_conditions_with_owner_and_expiry_in_the_display_zone() => Assert.Equal("• Card Portal pen-test exceptions signed by CISO · Priya Nair · by Fri 30 Oct 00:30", Tok("Conditions"));
    [Fact]
    public void Conditions_marks_an_expired_one() =>
        Assert.Equal("• Card Portal pen-test exceptions signed by CISO · Priya Nair · by Fri 30 Oct 00:30 (expired)", Tok("Conditions", Full() with { AsOf = Utc("2026-10-30T05:31:00Z") }));
    [Fact] public void Conditions_empty_is_None() { Assert.Equal("None", Tok("Conditions", Empty())); Assert.Equal("None", Tok("Conditions", Full() with { Decision = Full().Decision! with { OpenConditions = [] } })); }
    [Fact]
    public void KnownIssues_leaves_out_resolved_and_shows_workaround_and_key() =>
        Assert.Equal("• High · Refund report slow · INC-77 · workaround: Use the nightly export\n• Low · Cosmetic glitch · accepted", Tok("KnownIssues"));
    [Fact] public void KnownIssues_empty_is_None() { Assert.Equal("None", Tok("KnownIssues", Empty())); Assert.Equal("None", Tok("KnownIssues", Full() with { KnownIssues = [new("High", "x", null, "Resolved", null)] })); }
    [Fact] public void CloseCode() { Assert.Equal("Successful", Tok("CloseCode", Full() with { CloseCode = "Successful" })); Assert.Equal("Successful with issues", Tok("CloseCode", Full() with { CloseCode = "SuccessfulWithIssues" })); Assert.Equal("Unsuccessful", Tok("CloseCode", Full() with { CloseCode = "Unsuccessful" })); }
    [Fact] public void CloseCode_not_closed() => Assert.Equal("Not closed", Tok("CloseCode"));

    // ---- escaping per target, and no re-expansion
    private const string Hostile = "<img src=x onerror=alert(1)> & \"q\" 'a' *b* _c_ [d](http://e) `f` {Status} | # x\\y";

    [Fact]
    public void PlainText_values_are_verbatim() => Assert.Equal(Hostile, Tok("ReleaseTitle", Full() with { Title = Hostile }));

    [Fact]
    public void Markdown_neutralises_emphasis_code_links_html_and_tables()
    {
        var s = Tok("ReleaseTitle", Full() with { Title = Hostile }, CommTarget.Markdown);
        Assert.Equal("\\<img src=x onerror=alert(1)\\> & \"q\" 'a' \\*b\\* \\_c\\_ \\[d\\](http://e) \\`f\\` {Status} \\| # x\\\\y", s);
    }

    [Theory]
    [InlineData("# heading")] [InlineData("- item")] [InlineData("+ item")] [InlineData("=== ")]
    public void Markdown_neutralises_a_value_that_would_start_a_block(string title) =>
        Assert.StartsWith("\\" + title[0], Tok("ReleaseTitle", Full() with { Title = title }, CommTarget.Markdown));

    [Fact]
    public void Html_escapes_values_and_cannot_inject_markup()
    {
        var s = Tok("ReleaseTitle", Full() with { Title = Hostile }, CommTarget.Html);
        Assert.Equal("&lt;img src=x onerror=alert(1)&gt; &amp; &quot;q&quot; &#39;a&#39; *b* _c_ [d](http://e) `f` {Status} | # x\\y", s);
        Assert.DoesNotContain("<img", s);
    }

    [Fact]
    public void Html_template_text_is_escaped_too_and_bold_becomes_strong()
    {
        var m = CommHydrator.Hydrate(Full(), Chicago, null, "<script>x</script> **{ReleaseTitle}** ok\nnext", CommTarget.Html);
        Assert.Equal("&lt;script&gt;x&lt;/script&gt; <strong>R26.24 Q4 Payments Consolidated</strong> ok<br>next", m.Text);
    }

    [Fact]
    public void Html_bold_markers_inside_a_value_never_toggle_bold() =>
        Assert.Equal("<strong>a **b</strong> c", CommHydrator.Hydrate(Full() with { Title = "a **b" }, Chicago, null, "**{ReleaseTitle}** c", CommTarget.Html).Text);

    [Fact]
    public void Html_lists_become_lines_and_the_gate_table_is_monospace()
    {
        Assert.Equal("• Critical · Card Portal pen-test exception unsigned · Priya Nair<br>• Medium · Migration 0042 slow in staging (14 vs 8 min) · Dana Ortiz<br>• Low · Typo in release notes", Tok("BlockerList", target: CommTarget.Html));
        Assert.StartsWith("<code>Code Freeze ........... ✓ Certified Thu 22 Oct<br>", Tok("GateTable", target: CommTarget.Html));
    }

    [Fact]
    public void Markdown_lists_are_real_list_items_and_the_gate_table_is_a_code_fence_that_cannot_be_closed_by_a_name()
    {
        Assert.Equal("- Card Portal pen-test exception unsigned · Priya Nair", Tok("CriticalBlockers", target: CommTarget.Markdown).Replace("\\", ""));
        var t = Tok("GateTable", Full() with { Gates = [new("evil ``` gate", "Pending", 1, D("2026-10-29"), null)] }, CommTarget.Markdown);
        Assert.StartsWith("```\n", t); Assert.EndsWith("\n```", t);
        Assert.Equal(2, t.Split("```").Length - 1);   // only our own fence pair
    }

    [Fact]
    public void JsonString_result_can_sit_between_quotes_in_a_payload()
    {
        var text = CommHydrator.Hydrate(Full() with { Title = "say \"hi\"\\\n{Status}\t</script>" }, Chicago, null, "Title: {ReleaseTitle}\nline2", CommTarget.JsonString).Text;
        var doc = System.Text.Json.JsonDocument.Parse("{\"text\":\"" + text + "\"}");   // valid JSON: nothing escaped the string
        var decoded = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("say \"hi\"", decoded);
        Assert.Contains("{Status}", decoded);   // not expanded
        Assert.EndsWith("\nline2", decoded);
        Assert.DoesNotContain("\n", text);       // raw newlines are escaped
        Assert.DoesNotContain("\"hi\"", text);   // raw quotes are escaped
    }

    [Fact]
    public void JsonString_covers_control_characters_and_keeps_unicode()
    {
        var text = Tok("ReleaseTitle", Full() with { Title = "a\u0001b ✓" }, CommTarget.JsonString);
        Assert.Equal("ab ✓", System.Text.Json.JsonDocument.Parse("{\"t\":\"" + text + "\"}").RootElement.GetProperty("t").GetString());   // control chars are dropped by Clean
    }

    [Theory]
    [InlineData(CommTarget.PlainText)] [InlineData(CommTarget.Markdown)] [InlineData(CommTarget.Html)] [InlineData(CommTarget.JsonString)]
    public void A_value_that_looks_like_a_token_is_never_expanded(CommTarget target)
    {
        var m = CommHydrator.Hydrate(Full() with { Title = "{Status}{BlockerCount}{Nope}" }, Chicago, "S {ReleaseTitle}", "B {ReleaseTitle} / {Status}", target);
        Assert.Contains("{Status}{BlockerCount}{Nope}", m.Text);
        Assert.EndsWith(" / Gated", m.Text);
        Assert.Contains("{Status}{BlockerCount}{Nope}", m.Subject);
        Assert.True(m.CanDispatch);   // the value's braces are not template errors
    }

    [Fact]
    public void Control_characters_in_values_are_dropped_and_line_endings_normalised()
    {
        Assert.Equal("ab", Tok("ReleaseTitle", Full() with { Title = "a\u0000\u0007b" }));
        Assert.Equal("a\nb\nc", Tok("KnownIssues", Full() with { KnownIssues = [new("High", "a\r\nb\rc", null, "Open", null)] }).Replace("• High · ", ""));
    }

    // ---- subject lines
    [Fact]
    public void Subject_is_single_line_plain_and_lists_join_with_semicolons()
    {
        var m = CommHydrator.Hydrate(Full(), Chicago, "[{ReleaseTitle}] **{BlockerList}**", "x", CommTarget.Html);
        Assert.DoesNotContain("\n", m.Subject); Assert.DoesNotContain("<", m.Subject);
        Assert.Equal("[R26.24 Q4 Payments Consolidated] Critical · Card Portal pen-test exception unsigned · Priya Nair; Medium · Migration 0042 slow in staging (14 vs 8 min) · Dana Ortiz; Low · Typo in release notes", m.Subject);
    }

    [Fact]
    public void Subject_of_a_gate_table_stays_on_one_line() =>
        Assert.DoesNotContain("\n", CommHydrator.Hydrate(Full(), Chicago, "{GateTable}", "x", CommTarget.Markdown).Subject);

    // ---- parser: unknown tokens, malformed braces, positions
    [Fact]
    public void Unknown_token_is_an_error_with_a_suggestion_and_blocks_dispatch()
    {
        var m = CommHydrator.Hydrate(Full(), Chicago, "s", "Hello {ReleaseTitel} and {Status}", CommTarget.Markdown);
        var e = Assert.Single(m.TokenErrors);
        Assert.Equal(TokenErrorKinds.UnknownToken, e.Kind); Assert.Equal("{ReleaseTitel}", e.Token); Assert.Equal("{ReleaseTitle}", e.Suggestion);
        Assert.Equal(("body", 1, 7), (e.Part, e.Line, e.Column));
        Assert.Contains("Unknown token {ReleaseTitel}", e.Message);
        Assert.False(m.CanDispatch);
        Assert.Equal("Hello {ReleaseTitel} and Gated", m.Text);   // still rendered so the preview shows where the problem is
    }

    [Fact] public void Token_names_are_case_sensitive() { var e = Assert.Single(TokenParser.Parse("{status}").Errors); Assert.Equal("{Status}", e.Suggestion); }
    [Fact] public void A_token_with_no_near_match_has_no_suggestion() => Assert.Null(Assert.Single(TokenParser.Parse("{Completely_Different}").Errors).Suggestion);

    [Theory]
    [InlineData("{}")] [InlineData("{ Status }")] [InlineData("{Re leaseTitle}")] [InlineData("{1Status}")] [InlineData("{Status.Now}")] [InlineData("{a-b}")]
    public void Anything_else_between_braces_is_malformed(string text) => Assert.Equal(TokenErrorKinds.MalformedToken, Assert.Single(TokenParser.Parse(text).Errors).Kind);

    [Fact] public void Unclosed_brace() { var e = Assert.Single(TokenParser.Parse("a {ReleaseTitle").Errors); Assert.Equal(TokenErrorKinds.UnclosedBrace, e.Kind); Assert.Equal((1, 3), (e.Line, e.Column)); }
    [Fact] public void Open_brace_before_the_close_is_unclosed_and_the_inner_token_still_counts() { var p = TokenParser.Parse("{ {Status}"); Assert.Equal(TokenErrorKinds.UnclosedBrace, Assert.Single(p.Errors).Kind); Assert.Equal(["Status"], p.Tokens); }
    [Fact] public void Stray_close_brace() { var e = Assert.Single(TokenParser.Parse("x }").Errors); Assert.Equal(TokenErrorKinds.StrayBrace, e.Kind); Assert.Equal(3, e.Column); }
    [Fact] public void Doubled_braces_are_not_an_escape() => Assert.NotEmpty(TokenParser.Parse("{{Status}}").Errors);
    [Fact]
    public void Positions_count_lines_and_columns_from_one()
    {
        var e = Assert.Single(TokenParser.Parse("line one\nline {Two}\n", "body").Errors);
        Assert.Equal((2, 6), (e.Line, e.Column));
    }
    [Fact]
    public void Errors_in_the_subject_are_reported_as_the_subject()
    {
        var m = CommHydrator.Hydrate(Full(), Chicago, "{Nope}", "{Nada}", CommTarget.PlainText);
        Assert.Equal(["subject", "body"], m.TokenErrors.Select(e => e.Part));
    }
    [Fact] public void Text_without_braces_has_no_tokens_and_no_errors() { var p = TokenParser.Parse("plain text"); Assert.True(p.IsValid); Assert.Empty(p.Tokens); }
    [Fact] public void Null_template_is_empty_not_an_error() { Assert.True(TokenParser.Parse(null).IsValid); Assert.Equal("", CommHydrator.Hydrate(Full(), Chicago, null, null, CommTarget.PlainText).Text); }
    [Fact] public void Every_allowlisted_token_parses_clean() => Assert.All(CommTokens.Allowlist, n => Assert.True(TokenParser.Parse("{" + n + "}").IsValid));
    [Fact] public void Tokens_used_lists_each_once() => Assert.Equal(["Status", "ReleaseTitle"], CommHydrator.Hydrate(Full(), Chicago, "{Status}", "{ReleaseTitle} {Status}", CommTarget.PlainText).TokensUsed);

    [Fact] public void PlainText_drops_bold_markers() => Assert.Equal("Gated now", CommHydrator.Hydrate(Full(), Chicago, null, "**{Status}** now", CommTarget.PlainText).Text);
    [Fact] public void Markdown_keeps_bold_markers() => Assert.Equal("**Gated** now", CommHydrator.Hydrate(Full(), Chicago, null, "**{Status}** now", CommTarget.Markdown).Text);

    [Fact]
    public void The_library_default_templates_use_only_allowlisted_tokens()
    {
        // guards the default library shipped by the seed: a typo there would block every dispatch
        // (the entries live in Infrastructure; the shape is checked there too, this covers the tokens used in the mockup template)
        var body = "Subject: [{ReleaseTitle}] Go/No-Go: {GoNoGoDecision}\n**{ReleaseTitle}** is {GoNoGoDecision}.\nWindow: {Window}\nChange: {ChangeTicket}\n{Conditions}\n({ProductCount} products)\n{ProductList}\n{GateTable}\n({BlockerCount})\n{BlockerList}\n{KnownIssues}\nQuestions: {Owners}";
        var m = CommHydrator.Hydrate(Full(), Chicago, null, body, CommTarget.Markdown);
        Assert.True(m.CanDispatch);
        Assert.DoesNotContain("{", m.Text.Replace("\\{", ""));
    }
}
