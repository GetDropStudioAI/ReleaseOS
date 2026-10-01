using Microsoft.Extensions.Configuration;
using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// REOS-50 settings. Config keys: <c>Exports:Directory</c> (default: <c>exports</c> beside the database, i.e. data/exports; never under wwwroot),
/// <c>Exports:PollSeconds</c> (5), <c>Exports:StaleRunningSeconds</c> (300: a job left Running longer than this is retried), <c>Exports:MaxAttempts</c> (2),
/// <c>Exports:AuditRowLimit</c> (5000 audit events in the pack's extract), <c>Exports:MaxOpenJobsPerUser</c> (10), <c>Pdf:QuestPdfLicense</c> (OI-2: Community | Professional | Enterprise; never committed, never defaulted),
/// <c>Pdf:PdfA</c> (OI-8 spike: unset/false = off (default); true or 2b = PDF/A-2b; 3b = PDF/A-3b; see docs/PDFA_SPIKE.md),
/// <c>Exports:RetentionDays</c> (365, REOS-72: a generated file is deleted that many days after it was completed; the job row and its audit trail stay).
/// </summary>
public sealed record ExportOptions(string Directory, string? License, bool AllowEvaluationLicense, int PollSeconds, TimeSpan StaleAfter, int MaxAttempts, int AuditRowLimit, string? PdfA, bool UseSystemFonts = true,
    int MaxOpenJobsPerUser = ExportOptions.DefaultMaxOpenJobsPerUser, int RetentionDays = ExportOptions.DefaultRetentionDays)
{
    /// <summary>Default for <c>Exports:RetentionDays</c> (REOS-72, Q-050c, decided 2026-09-30 by John): every generated file, evidence packs and ZIPs included, is kept 365 days.</summary>
    public const int DefaultRetentionDays = 365;
    /// <summary>How often the worker looks for files past retention (at start-up, then this often).</summary>
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    public const string DefaultLicenseKey = "Pdf:QuestPdfLicense";
    /// <summary>Default for <c>Exports:MaxOpenJobsPerUser</c> (security review SEC-D8, Q-SEC-D3): Queued plus Running jobs one requester may have at a time.</summary>
    public const int DefaultMaxOpenJobsPerUser = 10;

    public static ExportOptions From(IConfiguration config, string databaseDirectory, bool isDevelopment)
    {
        static int Int(string? s, int d, int min) => int.TryParse(s, out var v) ? Math.Max(min, v) : d;
        var dir = config["Exports:Directory"];
        return new ExportOptions(
            Path.GetFullPath(string.IsNullOrWhiteSpace(dir) ? Path.Combine(databaseDirectory, "exports") : dir),
            config[DefaultLicenseKey], isDevelopment,
            Int(config["Exports:PollSeconds"], 5, 1), TimeSpan.FromSeconds(Int(config["Exports:StaleRunningSeconds"], 300, 1)),
            Int(config["Exports:MaxAttempts"], 2, 1), Int(config["Exports:AuditRowLimit"], 5000, 1),
            config["Pdf:PdfA"],
            !bool.TryParse(config["Pdf:UseSystemFonts"], out var usf) || usf,
            Int(config["Exports:MaxOpenJobsPerUser"], DefaultMaxOpenJobsPerUser, 1),
            ReadRetentionDays(config));
    }

    /// <summary><c>Exports:RetentionDays</c>: a whole number of days, at least 1. A value that is not one stops the start-up naming the key (fail fast: a typo must not delete evidence early).</summary>
    private static int ReadRetentionDays(IConfiguration config)
    {
        var raw = config["Exports:RetentionDays"];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultRetentionDays;
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 1
            ? d : throw new InvalidOperationException($"Exports:RetentionDays is '{raw}'. Use a whole number of days, 1 or more (default {DefaultRetentionDays}).");
    }

    /// <summary>The licence problem in words, or null when a valid tier is configured. Evaluation is refused outside Development ("not permitted in production").</summary>
    public string? LicenseProblem => TryLicense(out _);

    /// <summary>The Pdf:PdfA value in words when it is not one of false, true, 2b, 3b; else null.</summary>
    public string? PdfAProblem => TryPdfA(out _);

    /// <summary>Resolves Pdf:PdfA: null result = fine. <paramref name="level"/> is "2b", "3b" or null (off).</summary>
    public string? TryPdfA(out string? level)
    {
        level = null;
        var v = (PdfA ?? "").Trim().ToLowerInvariant();
        switch (v)
        {
            case "" or "false" or "off" or "0": return null;
            case "true" or "2b" or "pdfa-2b" or "pdf/a-2b": level = "2b"; return null;
            case "3b" or "pdfa-3b" or "pdf/a-3b": level = "3b"; return null;
            default: return $"Pdf:PdfA is '{PdfA}'. Use false (off), true or 2b (PDF/A-2b), or 3b (PDF/A-3b); see docs/PDFA_SPIKE.md.";
        }
    }

    /// <summary>Resolves the configured tier. Returns null on success, else why not. Nothing is defaulted: the deployer chooses (OI-2).</summary>
    public string? TryLicense(out LicenseType type)
    {
        type = LicenseType.Community;
        if (string.IsNullOrWhiteSpace(License))
            return $"The QuestPDF licence is not configured. Set {DefaultLicenseKey} to Community, Professional or Enterprise (the deployer's choice, DECISIONS OI-2); PDFs cannot be generated until it is.";
        if (!Enum.TryParse(License.Trim(), true, out LicenseType parsed) || !Enum.IsDefined(parsed))
            return $"{DefaultLicenseKey} is '{License}', which is not a QuestPDF licence tier. Use Community, Professional or Enterprise.";
        if (parsed == LicenseType.Evaluation && !AllowEvaluationLicense)
            return $"{DefaultLicenseKey}=Evaluation is not permitted outside Development. Use Community, Professional or Enterprise.";
        type = parsed;
        return null;
    }

    /// <summary>
    /// Applies the configured licence and font policy to QuestPDF (process-wide settings). Throws <see cref="ExportConfigException"/> with a readable message when the licence
    /// is missing or invalid. Since QuestPDF 2026.9 system fonts are off by default: they are switched on (Pdf:UseSystemFonts, default true; rule 10, fonts come from the OS),
    /// and a missing font family or glyph does not stop an export (the style chains end in the bundled Lato).
    /// </summary>
    public void ApplyLicense()
    {
        var problem = TryLicense(out var type);
        if (problem is not null) throw new ExportConfigException(problem);
        if (PdfAProblem is { } pa) throw new ExportConfigException(pa);
        QuestPDF.Settings.License = type;
        QuestPDF.Settings.UseSystemFonts = UseSystemFonts;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }
}

/// <summary>A configuration problem that stops an export (missing licence, ...). The message is shown to the user as the job's error.</summary>
public sealed class ExportConfigException(string message) : Exception(message);

/// <summary>An integrity failure: stored evidence no longer matches its recorded SHA-256 (or is missing). The pack is never produced.</summary>
public sealed class ExportIntegrityException(string message) : Exception(message);
