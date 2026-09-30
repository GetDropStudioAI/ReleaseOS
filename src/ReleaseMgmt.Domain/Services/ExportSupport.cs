using System.Text;

namespace ReleaseMgmt.Domain.Services;

/// <summary>Kinds and formats of the PDF/ZIP export jobs (REOS-50). The API names map onto the ExportJobs.Kind values the schema allows.</summary>
public static class ExportKinds
{
    public const string ReleaseReport = "ReleaseReportPdf", RunSheet = "RunbookPdf", EvidencePack = "EvidencePackPdf", Scorecard = "ScorecardPdf";
    public const string Pdf = "pdf", Zip = "zip";

    public static readonly string[] All = [ReleaseReport, RunSheet, EvidencePack, Scorecard];

    /// <summary>Accepts the schema name (ReleaseReportPdf) or the short API name (ReleaseReport, release-report, RunSheet, run-sheet, EvidencePack, ...), any case.</summary>
    public static string? Parse(string? name)
    {
        var n = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return n switch
        {
            "releasereport" or "releasereportpdf" => ReleaseReport,
            "runsheet" or "runbook" or "runbookpdf" or "runsheetpdf" => RunSheet,
            "evidencepack" or "evidencepackpdf" or "evidencepackzip" => EvidencePack,
            "scorecard" or "scorecardpdf" => Scorecard,
            _ => null,
        };
    }

    public static string? ParseFormat(string? kind, string? format)
    {
        var f = (format ?? "").Trim().ToLowerInvariant();
        if (f.Length == 0) f = Pdf;
        if (f == Pdf) return Pdf;
        return f == Zip && kind == EvidencePack ? Zip : null;   // only the evidence pack has a ZIP form
    }

    public static string Slug(string kind) => kind switch
    {
        ReleaseReport => "release-report", RunSheet => "run-sheet", EvidencePack => "evidence-pack", Scorecard => "scorecard", _ => "export",
    };

    public static string Label(string kind) => kind switch
    {
        ReleaseReport => "Release report", RunSheet => "Run sheet", EvidencePack => "Audit evidence pack", Scorecard => "Post-release scorecard", _ => kind,
    };

    /// <summary>The evidence pack (and its ZIP) is audit material: RTE, Release Manager and Governance Officer only. The other three are readable by any signed-in role.</summary>
    public static bool NeedsAuditRead(string kind) => kind == EvidencePack;
}

/// <summary>The separation-of-duties result printed in the evidence pack (PROJECT_SCOPE section 1 and 4).</summary>
public sealed record SodResult(bool Applies, bool Passed, string Text);

/// <summary>Pure helpers for the exports, kept out of the renderer so they can be unit-tested without QuestPDF or a database.</summary>
public static class ExportSupport
{
    /// <summary>
    /// Compliance gates only: the certifier must be a Governance Officer who completed none of the gate's tasks (the same rule the certify guard and the database trigger enforce).
    /// Standard gates have no such rule: "not required". A gate that is not certified has no certifier to check.
    /// </summary>
    public static SodResult EvaluateSod(string gateClass, string status, string? certifierRole, string? certifierUserId, IReadOnlyCollection<string?> taskCompletedBy)
    {
        if (gateClass != "Compliance") return new(false, true, "not required");
        if (status is not ("Certified" or "Waived")) return new(true, false, "not certified yet");
        if (status == "Waived") return new(true, true, "waiver: approver checked separately (section 4)");
        var mine = certifierUserId is null ? 0 : taskCompletedBy.Count(u => u == certifierUserId);
        var total = taskCompletedBy.Count;
        if (certifierRole != "GovernanceOfficer") return new(true, false, "certifier is not a Governance Officer");
        if (mine > 0) return new(true, false, $"certifier completed {mine} of {total} tasks");
        return new(true, true, $"completed none of {total} task{(total == 1 ? "" : "s")}");
    }

    /// <summary>First six and last six hex characters, as the mockup prints them: 9f3c1a…77e1a4.</summary>
    public static string ShortHash(string? sha256) =>
        string.IsNullOrEmpty(sha256) ? "—" : sha256.Length <= 12 ? sha256 : sha256[..6] + "…" + sha256[^6..];

    /// <summary>The display reference of a job in the pack header ("EX-2291" in the mockup): EX- plus the last six hex digits of the job id (the random tail of the UUIDv7).</summary>
    public static string JobRef(string jobId) => "EX-" + new string(jobId.Where(Uri.IsHexDigit).ToArray())[^6..].ToUpperInvariant();

    /// <summary>
    /// One CSV field, RFC 4180 quoting plus the OWASP CSV-injection rule (PROJECT_SCOPE section 9): a cell starting with = + - @, tab or CR is prefixed with an apostrophe.
    /// </summary>
    public static string CsvField(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string CsvRow(IEnumerable<string?> fields) => string.Join(',', fields.Select(CsvField));

    /// <summary>File name safe on Windows and POSIX and in a Content-Disposition header: ASCII letters, digits, dot, dash, underscore.</summary>
    public static string SafeFileStem(string? text, int max = 48)
    {
        var sb = new StringBuilder();
        foreach (var c in text ?? "")
            sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' ? c : '-');
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-', '.');
        if (s.Length > max) s = s[..max].TrimEnd('-', '.');
        return s.Length == 0 ? "train" : s;
    }
}
