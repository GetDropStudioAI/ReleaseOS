using ReleaseMgmt.Domain.Exchange;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-48/49: the pure part of import and export: OWASP escaping that round-trips exactly, header rules and typed cell validation (row, column, message).</summary>
public class ExchangeTests
{
    // ---- CSV-injection escaping -------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+41", "'+41")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tTab", "'\tTab")]
    [InlineData("\rCR", "'\rCR")]
    [InlineData("plain", "plain")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Escape_prefixes_one_apostrophe_on_the_five_dangerous_first_characters_only(string input, string expected) => Assert.Equal(expected, CsvSafety.Escape(input));

    [Fact]
    public void Escape_of_null_is_empty() => Assert.Equal("", CsvSafety.Escape(null));

    [Theory]
    [InlineData("'tis", "''tis")]                  // a legitimate leading apostrophe is doubled so it survives the trip
    [InlineData("'", "''")]
    [InlineData("'=x", "''=x")]                    // a value that IS an apostrophe followed by a formula char
    [InlineData("''", "'''")]
    [InlineData("-", "'-")]
    public void A_leading_apostrophe_in_the_data_is_doubled(string input, string expected) => Assert.Equal(expected, CsvSafety.Escape(input));

    [Theory]
    [InlineData("'=x", "=x")]                      // hand-written file: the apostrophe is the OWASP prefix
    [InlineData("'-5", "-5")]
    [InlineData("''tis", "'tis")]
    [InlineData("'tis", "'tis")]                   // not followed by a dangerous character or apostrophe: it is data
    [InlineData("'", "'")]
    [InlineData("x'=", "x'=")]
    [InlineData("", "")]
    public void Unescape_strips_exactly_one_apostrophe_only_where_escape_would_have_added_it(string cell, string expected) => Assert.Equal(expected, CsvSafety.Unescape(cell));

    [Fact]
    public void Every_string_round_trips_exactly_through_escape_and_unescape()
    {
        string[] atoms = ["", "'", "=", "+", "-", "@", "\t", "\r", "\n", "a", "0", " ", "\"", ",", ";", "é", "'=", "=1+1", "--", "''"];
        var cases = new List<string>(atoms);
        foreach (var a in atoms) foreach (var b in atoms) { cases.Add(a + b); foreach (var c in atoms) cases.Add(a + b + c); }
        foreach (var s in cases)
        {
            var escaped = CsvSafety.Escape(s);
            Assert.Equal(s, CsvSafety.Unescape(escaped));
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') Assert.StartsWith("'", escaped);   // never left live for a spreadsheet
        }
    }

    // ---- headers ----------------------------------------------------------------------------------------------------------------------
    private static ImportKindSpec Kind(string k) => ImportKinds.Get(k)!;

    [Fact]
    public void The_nine_kinds_are_exactly_the_sections_list()
    {
        Assert.Equal(["Trains", "Products", "Gates", "Tasks", "RunbookSteps", "ExternalLinks", "Holidays", "Users", "Teams"], ImportKinds.All.Select(k => k.Kind));
        Assert.Equal(["Title", "TargetReleaseDate", "RiskTier", "Template", "ChangeTicketNumber", "WindowStart", "WindowEnd"], Kind("Trains").Columns.Select(c => c.Name));
        Assert.Equal(["Train", "GateName", "SequenceOrder", "OffsetDays", "RequiredBefore", "Owner", "GateClass"], Kind("Gates").Columns.Select(c => c.Name));
        Assert.Equal(["Train", "StepCode", "Title", "Section", "PlannedStart", "DurationMin", "Owner", "Product", "DependsOn", "Instructions"], Kind("RunbookSteps").Columns.Select(c => c.Name));
        Assert.Equal(["Handle", "Name", "Members"], Kind("Teams").Columns.Select(c => c.Name));
    }

    [Fact]
    public void Headers_match_case_insensitively_and_a_BOM_and_padding_are_ignored()
    {
        var h = RowParser.ResolveHeader(Kind("Holidays"), ["﻿day", "  NAME "]);
        Assert.Empty(h.Errors);
        Assert.Equal(0, h.Columns["Day"]); Assert.Equal(1, h.Columns["Name"]);
    }

    [Fact]
    public void An_unknown_column_is_an_error_naming_the_column_at_row_zero()
    {
        var h = RowParser.ResolveHeader(Kind("Holidays"), ["Day", "Name", "Colour"]);
        var e = Assert.Single(h.Errors);
        Assert.Equal(0, e.Row); Assert.Equal("Colour", e.Column); Assert.Contains("Unknown column", e.Message);
    }

    [Fact]
    public void Columns_starting_with_hash_are_skipped_whatever_they_are_called()
    {
        var h = RowParser.ResolveHeader(Kind("Holidays"), ["#Version", "Day", "#Nonsense", "Name"]);
        Assert.Empty(h.Errors);
        Assert.Equal(["#Version", "#Nonsense"], h.Skipped);
        Assert.Equal(2, h.Recognised);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("currentstatus")]
    [InlineData("CloseCode")]
    public void A_column_that_would_set_a_status_is_refused_with_its_own_message(string column)
    {
        var h = RowParser.ResolveHeader(Kind("Trains"), ["Title", "TargetReleaseDate", "RiskTier", column]);
        var e = Assert.Single(h.Errors);
        Assert.Equal(column, e.Column); Assert.Contains("workflow", e.Message); Assert.Contains("#", e.Message);
    }

    [Fact]
    public void A_missing_required_column_and_a_duplicate_column_are_header_errors()
    {
        var h = RowParser.ResolveHeader(Kind("Users"), ["Email", "email", "Role"]);
        Assert.Contains(h.Errors, e => e.Column == "email" && e.Message.Contains("more than once"));
        Assert.Contains(h.Errors, e => e.Column == "DisplayName" && e.Message.StartsWith("Required column"));
    }

    [Fact]
    public void An_empty_header_cell_is_an_error_and_a_semicolon_file_is_recognised()
    {
        Assert.Contains(RowParser.ResolveHeader(Kind("Holidays"), ["Day", "Name", ""]).Errors, e => e.Column == "(column 3)");
        var semi = RowParser.ResolveHeader(Kind("Holidays"), ["Day;Name"]);
        Assert.Contains(semi.Errors, e => e.Message.Contains("semicolon"));
    }

    // ---- cells ------------------------------------------------------------------------------------------------------------------------
    private static ParsedRow Row(string kind, string[] header, params string[] cells)
    {
        var spec = Kind(kind);
        return RowParser.Parse(spec, RowParser.ResolveHeader(spec, header), 7, cells);
    }

    [Fact]
    public void A_bad_cell_reports_its_row_and_column_and_a_good_row_has_none()
    {
        var bad = Row("Holidays", ["Day", "Name"], "13/11/2026", "Founders");
        var e = Assert.Single(bad.Errors);
        Assert.Equal(7, e.Row); Assert.Equal("Day", e.Column); Assert.Contains("yyyy-MM-dd", e.Message);
        Assert.Empty(Row("Holidays", ["Day", "Name"], "2026-11-13", "Founders").Errors);
    }

    [Fact]
    public void Timestamps_need_Z_or_an_offset_and_are_normalised_to_UTC()
    {
        Assert.Equal("2026-11-13T01:00:00Z", Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "deploy", "2026-11-13T02:00:00+01:00", "5", "a@b.c").Cells["PlannedStart"]);
        Assert.Equal("2026-11-13T01:00:00Z", Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "deploy", "2026-11-13T01:00Z", "5", "a@b.c").Cells["PlannedStart"]);
        foreach (var bad in new[] { "2026-11-13T01:00:00", "2026-11-13", "13 Nov 2026 01:00Z", "2026-11-13 01:00:00Z", "2026-11-13T25:00:00Z" })
            Assert.Contains(Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "deploy", bad, "5", "a@b.c").Errors, e => e.Column == "PlannedStart");
    }

    private static readonly string[] RunbookHeader = ["Train", "StepCode", "Title", "Section", "PlannedStart", "DurationMin", "Owner"];

    [Fact]
    public void Enums_are_case_insensitive_and_canonicalised_and_ints_are_plain_digits()
    {
        var r = Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "PRECHECK", "2026-11-13T01:00:00Z", "15", "a@b.c");
        Assert.Empty(r.Errors);
        Assert.Equal("PreCheck", r.Cells["Section"]); Assert.Equal("R-001", r.Cells["StepCode"]);
        foreach (var bad in new[] { "0", "-5", "1.5", "1,000", "abc", "1e3" })
            Assert.Contains(Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "Deploy", "2026-11-13T01:00:00Z", bad, "a@b.c").Errors, e => e.Column == "DurationMin");
        Assert.Contains(Row("RunbookSteps", RunbookHeader, "T", "r-001", "t", "Deployy", "2026-11-13T01:00:00Z", "5", "a@b.c").Errors, e => e.Column == "Section" && e.Message.Contains("PreCheck"));
    }

    [Fact]
    public void A_required_cell_may_not_be_empty_but_an_optional_one_and_an_empty_member_list_may()
    {
        Assert.Contains(Row("Holidays", ["Day", "Name"], "2026-11-13", "").Errors, e => e.Column == "Name" && e.Message.Contains("required"));
        Assert.Empty(Row("Teams", ["Handle", "Name", "Members"], "desk", "Desk", "").Errors);
        Assert.Empty(Row("Users", ["Email", "DisplayName", "Role", "Handle"], "a@b.c", "A", "Viewer", "").Errors);
    }

    [Fact]
    public void Owners_are_an_email_or_an_at_handle_and_lists_are_split_sorted_and_deduplicated()
    {
        Assert.Empty(Row("RunbookSteps", RunbookHeader, "T", "R-1", "t", "Deploy", "2026-11-13T01:00:00Z", "5", "@onboarding").Errors);
        Assert.Contains(Row("RunbookSteps", RunbookHeader, "T", "R-1", "t", "Deploy", "2026-11-13T01:00:00Z", "5", "just a name").Errors, e => e.Column == "Owner");
        var team = Row("Teams", ["Handle", "Name", "Members"], "@desk", "Desk", "b@x.com; A@x.com ;b@x.com;;");
        Assert.Equal("A@x.com;b@x.com", team.Cells["Members"]);
        Assert.Equal("desk", team.Cells["Handle"]);
        Assert.Contains(Row("Teams", ["Handle", "Name", "Members"], "desk", "Desk", "a@x.com;nope").Errors, e => e.Column == "Members" && e.Message.Contains("nope"));
        var steps = Row("RunbookSteps", [.. RunbookHeader, "DependsOn"], "T", "R-3", "t", "Deploy", "2026-11-13T01:00:00Z", "5", "a@b.c", "r-2; R-1;r-2");
        Assert.Equal("R-1;R-2", steps.Cells["DependsOn"]);
    }

    [Fact]
    public void The_CSV_escape_is_undone_before_a_cell_is_validated_and_a_ragged_row_is_an_error()
    {
        Assert.Equal("-5", Row("Holidays", ["Day", "Name"], "2026-11-13", "'-5").Cells["Name"]);
        var ragged = Row("Holidays", ["Day", "Name"], "2026-11-13");
        var e = Assert.Single(ragged.Errors);
        Assert.Equal("(row)", e.Column);
    }

    [Fact]
    public void Single_line_columns_refuse_line_breaks_but_instructions_take_them()
    {
        Assert.Contains(Row("Holidays", ["Day", "Name"], "2026-11-13", "a\nb").Errors, e => e.Column == "Name" && e.Message.Contains("one line"));
        var r = Row("RunbookSteps", [.. RunbookHeader, "Instructions"], "T", "R-1", "t", "Deploy", "2026-11-13T01:00:00Z", "5", "a@b.c", "step 1\nstep 2");
        Assert.Empty(r.Errors); Assert.Equal("step 1\nstep 2", r.Cells["Instructions"]);
    }

    [Fact]
    public void Suggest_finds_the_near_miss_and_nothing_for_a_stranger()
    {
        Assert.Equal(["R-012"], RowParser.Suggest("R-013", ["R-001", "R-012", "R-901"]));
        Assert.Empty(RowParser.Suggest("Zebra", ["R-001", "R-012"]));
    }

    [Fact]
    public void Limits_are_the_documented_ones() { Assert.Equal(10_000, ImportLimits.MaxRows); Assert.Equal(5 * 1024 * 1024, ImportLimits.MaxBytes); }
}
