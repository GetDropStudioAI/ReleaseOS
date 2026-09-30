using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>
/// Shared look of the PDFs (mockups/EvidencePack.html on paper): Letter, ink #1d1d1f, secondary #5a5a5f, hairline #c7c7cc, 9 pt body (= 12 px), section titles 10 pt semibold.
/// Fonts are QuestPDF's defaults (the bundled Lato; nothing else is embedded from this repo) with a monospaced family chain for ids, times and hashes that resolves to whatever
/// the OS has (Cascadia Mono / Consolas on Windows, Menlo on macOS, DejaVu / Liberation Mono on Linux). SF Pro is never bundled (CLAUDE.md rule 10).
/// The colours are print colours, not UI tokens: the no-hard-coded-colours rule is about the web UI.
/// </summary>
public static class PdfStyle
{
    public const string Ink = "#1d1d1f", Quiet = "#5a5a5f", Hair = "#c7c7cc", Bad = "#b3261e", Good = "#1d6b34";
    /// <summary>Glyphs Lato lacks (the ✓ ✗ of the SoD column) come from the first of these the OS has. If none exists the glyph renders as a replacement mark; the words beside it carry the result.</summary>
    private static readonly string[] SymbolFallbacks = ["Segoe UI Symbol", "Apple Symbols", "DejaVu Sans", "Arial Unicode MS"];
    /// <summary>Body text: QuestPDF's bundled Lato (always available, so layout is the same on every host) with OS symbol fonts as glyph fallback.</summary>
    public static readonly string[] SansFamilies = ["Lato", .. SymbolFallbacks];
    /// <summary>Ids, times and hashes: the OS monospace font (Cascadia Mono / Consolas on Windows, SF Mono / Menlo on macOS, DejaVu / Liberation Mono on Linux), then Lato so a host with none still renders.</summary>
    public static readonly string[] MonoFamilies = ["Cascadia Mono", "SF Mono", "Menlo", "Consolas", "DejaVu Sans Mono", "Liberation Mono", "Courier New", "Lato", .. SymbolFallbacks];
    public const float Body = 9f, Small = 8f, Tiny = 7f, MonoSize = 8.25f;   // the OS mono fonts are wider than SF Mono, so mono runs a little smaller

    public static TextStyle Mono(this TextStyle s) => s.FontFamily(MonoFamilies);

    /// <summary>Section heading: "1 · Change record" (10 pt semibold).</summary>
    public static void SectionTitle(this IContainer c, string text) => c.PaddingBottom(3).Text(text).FontSize(10f).SemiBold();

    public static IContainer HeadCell(this IContainer c) => c.BorderBottom(0.75f).BorderColor(Ink).PaddingVertical(2.25f).PaddingRight(6).DefaultTextStyle(x => x.FontSize(8.25f).SemiBold().FontColor(Quiet));
    public static IContainer BodyCell(this IContainer c) => c.BorderBottom(0.75f).BorderColor(Hair).PaddingVertical(2.25f).PaddingRight(6);

    public static void HeadText(this IContainer c, string text) => c.HeadCell().Text(text);
    public static void QuietText(this TextDescriptor t, string text) => t.Span(text).FontColor(Quiet);
}

/// <summary>Formats times for a document in <c>Display:TimeZone</c> (D24), English day/month names whatever the server culture.</summary>
public sealed class PdfFmt(DisplayClock clock)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public DisplayClock Clock => clock;

    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.Zone);

    /// <summary>Fri 30 Oct 2026</summary>
    public string Day(DateTime utc) => Local(utc).ToString("ddd d MMM yyyy", Inv);
    /// <summary>Fri 30 Oct 2026 for a calendar date.</summary>
    public string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", Inv);
    /// <summary>Fri 30 Oct</summary>
    public string DayShort(DateTime utc) => Local(utc).ToString("ddd d MMM", Inv);
    public string DayShort(DateOnly d) => d.ToString("ddd d MMM", Inv);
    /// <summary>Fri 23 (the day of month with its weekday, as the "Due" column of the mockup)</summary>
    public string DowDay(DateOnly d) => d.ToString("ddd d", Inv);
    /// <summary>Thu 22 Oct 17:05</summary>
    public string DayTime(DateTime utc) => Local(utc).ToString("ddd d MMM HH:mm", Inv);
    /// <summary>Wed 28 10:02</summary>
    public string DowDayTime(DateTime utc) => Local(utc).ToString("ddd d HH:mm", Inv);
    /// <summary>Fri 00:30</summary>
    public string DowTime(DateTime utc) => Local(utc).ToString("ddd HH:mm", Inv);
    public string Time(DateTime utc) => Local(utc).ToString("HH:mm", Inv);
    /// <summary>2026-10-30 01:02 (unambiguous, for the audit log and manifests)</summary>
    public string Iso(DateTime utc) => Local(utc).ToString("yyyy-MM-dd HH:mm", Inv);

    /// <summary>CT, ET, MT, PT, UTC for the common zones; otherwise the UTC offset at that instant (UTC-03:30).</summary>
    public string Zone(DateTime utc)
    {
        var id = clock.Zone.Id;
        var local = Local(utc);
        var dst = clock.Zone.IsDaylightSavingTime(local);
        var abbr = id switch
        {
            "America/Chicago" or "US/Central" or "Central Standard Time" => "CT",
            "America/New_York" or "US/Eastern" or "Eastern Standard Time" => "ET",
            "America/Denver" or "US/Mountain" or "Mountain Standard Time" => "MT",
            "America/Los_Angeles" or "US/Pacific" or "Pacific Standard Time" => "PT",
            "UTC" or "Etc/UTC" or "Etc/GMT" => "UTC",
            _ => null,
        };
        if (abbr is not null) return abbr;
        _ = dst;
        var off = clock.Zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return "UTC" + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString(@"hh\:mm", Inv);
    }

    /// <summary>Mon 2 Nov 2026 09:14 CT</summary>
    public string Stamp(DateTime utc) => $"{Local(utc).ToString("ddd d MMM yyyy HH:mm", Inv)} {Zone(utc)}";

    public static string Minutes(double min) => min < 60 ? $"{Math.Round(min):0} min" : $"{(int)(min / 60)} h {Math.Round(min % 60):0} min";
    public static string Signed(double min) => (min >= 0 ? "+" : "−") + $"{Math.Round(Math.Abs(min)):0} min";
    public static string Risk(string tier) => tier switch { "VeryHigh" => "Very high", _ => tier };
    public static string Decision(string d) => d switch { "GoWithConditions" => "Go with conditions", "NoGo" => "No-Go", _ => d };
    public static string RoleLabel(string? role) => role switch { "GovernanceOfficer" => "Gov. Officer", "ReleaseManager" => "Release Manager", "RTE" => "RTE", _ => role ?? "" };
    public static string Bytes(long n) => n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{n / 1024.0:0.#} KB" : $"{n / 1048576.0:0.#} MB";
}
