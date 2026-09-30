using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-50 pure rules: kinds, formats, the SoD result printed in the pack, CSV escaping for manifest.csv, short hashes and file stems.</summary>
public class ExportSupportTests
{
    [Theory]
    [InlineData("ReleaseReport", ExportKinds.ReleaseReport)] [InlineData("release-report", ExportKinds.ReleaseReport)] [InlineData("ReleaseReportPdf", ExportKinds.ReleaseReport)]
    [InlineData("RunSheet", ExportKinds.RunSheet)] [InlineData("run_sheet", ExportKinds.RunSheet)] [InlineData("RunbookPdf", ExportKinds.RunSheet)]
    [InlineData("EvidencePack", ExportKinds.EvidencePack)] [InlineData("evidence-pack", ExportKinds.EvidencePack)] [InlineData("EvidencePackPdf", ExportKinds.EvidencePack)]
    [InlineData("Scorecard", ExportKinds.Scorecard)] [InlineData("ScorecardPdf", ExportKinds.Scorecard)]
    public void Kinds_parse_from_the_api_and_the_schema_names(string name, string expected) => Assert.Equal(expected, ExportKinds.Parse(name));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Csv")] [InlineData("Xlsx")] [InlineData("Ics")] [InlineData("Nope")]
    public void Grid_and_unknown_kinds_are_not_pdf_jobs(string? name) => Assert.Null(ExportKinds.Parse(name));

    [Fact]
    public void Only_the_evidence_pack_has_a_zip_form_and_only_it_needs_audit_rights()
    {
        Assert.Equal("pdf", ExportKinds.ParseFormat(ExportKinds.ReleaseReport, null));
        Assert.Equal("pdf", ExportKinds.ParseFormat(ExportKinds.EvidencePack, "PDF"));
        Assert.Equal("zip", ExportKinds.ParseFormat(ExportKinds.EvidencePack, "zip"));
        Assert.Null(ExportKinds.ParseFormat(ExportKinds.Scorecard, "zip"));
        Assert.Null(ExportKinds.ParseFormat(ExportKinds.EvidencePack, "docx"));
        Assert.True(ExportKinds.NeedsAuditRead(ExportKinds.EvidencePack));
        Assert.All(new[] { ExportKinds.ReleaseReport, ExportKinds.RunSheet, ExportKinds.Scorecard }, k => Assert.False(ExportKinds.NeedsAuditRead(k)));
    }

    [Fact]
    public void Sod_is_not_required_for_standard_gates()
    {
        var r = ExportSupport.EvaluateSod("Standard", "Certified", "RTE", "u1", ["u1", "u1"]);
        Assert.False(r.Applies); Assert.True(r.Passed); Assert.Equal("not required", r.Text);
    }

    [Fact]
    public void Sod_compliance_passes_for_a_governance_officer_who_completed_none_of_the_tasks()
    {
        var r = ExportSupport.EvaluateSod("Compliance", "Certified", "GovernanceOfficer", "gov", ["rte", "rte", null, "dev", "dev"]);
        Assert.True(r.Applies); Assert.True(r.Passed); Assert.Equal("completed none of 5 tasks", r.Text);
        Assert.Equal("completed none of 1 task", ExportSupport.EvaluateSod("Compliance", "Certified", "GovernanceOfficer", "gov", ["rte"]).Text);
    }

    [Fact]
    public void Sod_compliance_fails_when_the_certifier_did_some_of_the_work_or_is_not_a_governance_officer()
    {
        var did = ExportSupport.EvaluateSod("Compliance", "Certified", "GovernanceOfficer", "gov", ["gov", "rte", "gov"]);
        Assert.True(did.Applies); Assert.False(did.Passed); Assert.Equal("certifier completed 2 of 3 tasks", did.Text);
        var role = ExportSupport.EvaluateSod("Compliance", "Certified", "RTE", "rte", ["dev"]);
        Assert.False(role.Passed); Assert.Contains("not a Governance Officer", role.Text);
    }

    [Fact]
    public void Sod_for_uncertified_and_waived_compliance_gates_says_so_instead_of_claiming_a_pass()
    {
        Assert.False(ExportSupport.EvaluateSod("Compliance", "InProgress", null, null, []).Passed);
        var waived = ExportSupport.EvaluateSod("Compliance", "Waived", "GovernanceOfficer", "gov", []);
        Assert.Contains("waiver", waived.Text);
    }

    [Theory]
    [InlineData(null, "")] [InlineData("plain", "plain")] [InlineData("a,b", "\"a,b\"")] [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")] [InlineData("+1", "'+1")] [InlineData("-1", "'-1")] [InlineData("@x", "'@x")]
    [InlineData("\tx", "'\tx")] [InlineData("\rx", "\"'\rx\"")]
    [InlineData("=cmd|' /C calc'!A0,evil", "\"'=cmd|' /C calc'!A0,evil\"")]
    [InlineData("a=b", "a=b")]
    public void Csv_fields_follow_rfc_4180_and_the_owasp_injection_rule(string? input, string expected) => Assert.Equal(expected, ExportSupport.CsvField(input));

    [Fact]
    public void Csv_rows_join_escaped_fields() => Assert.Equal("a,\"b,c\",'=d", ExportSupport.CsvRow(["a", "b,c", "=d"]));

    [Fact]
    public void Hashes_and_references_print_the_way_the_mockup_does()
    {
        Assert.Equal("9f3c1a…77e1a4", ExportSupport.ShortHash("9f3c1a" + new string('0', 52) + "77e1a4"));
        Assert.Equal("—", ExportSupport.ShortHash(null));
        Assert.Equal("EX-0C1D2E".Length, ExportSupport.JobRef("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b").Length);
        Assert.Equal("EX-3F4A5B", ExportSupport.JobRef("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"));
    }

    [Theory]
    [InlineData("R26.24 Q4 Payments Consolidated", "R26.24-Q4-Payments-Consolidated")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a\\b:c*d?e", "a-b-c-d-e")]
    [InlineData("", "train")] [InlineData("///", "train")] [InlineData("Ünïcode", "n-code")]
    public void File_stems_are_safe_for_disk_and_headers(string input, string expected) => Assert.Equal(expected, ExportSupport.SafeFileStem(input));

    [Fact]
    public void File_stems_are_capped() => Assert.True(ExportSupport.SafeFileStem(new string('x', 500)).Length <= 48);
}
