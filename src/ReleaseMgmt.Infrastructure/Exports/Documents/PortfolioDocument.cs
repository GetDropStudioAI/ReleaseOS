using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>
/// What the portfolio report prints (REOS-121): the figures of the person's workspaces for a period, from the same M16..M24 statements as the Analytics screen and the workbook, so the three agree.
/// <see cref="Ref"/> is derived from what the report covers, so the same data renders the same file.
/// </summary>
public sealed record PortfolioReport(string Ref, DateTime GeneratedAt, string GeneratedBy, DisplayClock Clock, IReadOnlyList<string> Workspaces, int Memberships, DateOnly From, DateOnly To, DateTime AsOf,
    int FiscalYearStartMonth, IReadOnlyList<M17Row> Total, IReadOnlyList<M17Row> ByQuarter, IReadOnlyList<M17Row> ByWorkspace, IReadOnlyList<M16Row> Restore, IReadOnlyList<M22Row> LeadByWorkspace,
    IReadOnlyList<M18Row> Exceptions, IReadOnlyList<M18Row> ExceptionsByWorkspace, IReadOnlyList<M21Row> Risk, IReadOnlyList<M21Row> RiskByWorkspace);

/// <summary>
/// The portfolio PDF. Same page identity as the other PDFs (Letter, deterministic metadata, header with the document name and the reference and generation stamp, footer with the stamp and page n of m)
/// and the same time rule (D51): every instant is UTC with the display zone's reading beside it. It is not about one train, so it does not derive from <see cref="TrainDocument"/>; it reuses its blocks.
/// </summary>
public sealed class PortfolioDocument(PortfolioReport report, ExportOptions options) : IDocument
{
    private PortfolioReport R { get; } = report;
    private PdfFmt F { get; } = new(report.Clock);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string DocumentName = "Portfolio report";

    private string Scope => string.Join(", ", R.Workspaces);

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"{DocumentName}: {Scope}",
        Author = R.GeneratedBy,
        Subject = $"{R.Ref} for {R.Workspaces.Count} of {R.Memberships} workspaces, {R.From:yyyy-MM-dd} to {R.To:yyyy-MM-dd} (UTC days)",
        Creator = "ReleaseOS",
        Producer = "ReleaseOS (QuestPDF)",
        Language = "en-US",
        CreationDate = new DateTimeOffset(DateTime.SpecifyKind(R.GeneratedAt, DateTimeKind.Utc)),
        ModifiedDate = new DateTimeOffset(DateTime.SpecifyKind(R.GeneratedAt, DateTimeKind.Utc)),
    };

    public DocumentSettings GetSettings() => options.TryPdfA(out var level) is null && level is not null ? PdfASettings.Apply(new DocumentSettings(), level) : DocumentSettings.Default;

    public void Compose(IDocumentContainer container) =>
        container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.MarginTop(39); page.MarginBottom(30); page.MarginHorizontal(42);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontFamily(PdfStyle.SansFamilies).FontSize(PdfStyle.Body).FontColor(PdfStyle.Ink).LineHeight(1.4f));
            page.Header().PaddingBottom(10.5f).Element(Header);
            page.Content().Element(c => c.Column(col => { col.Spacing(10.5f); Body(col); }));
            page.Footer().Element(Footer);
        });

    private void Header(IContainer c) =>
        c.BorderBottom(1.5f).BorderColor(PdfStyle.Ink).PaddingBottom(4.5f).Row(r =>
        {
            r.RelativeItem().Text(t => { t.Span(DocumentName).SemiBold(); t.Span($" · {Scope}").FontColor(PdfStyle.Quiet); });
            r.AutoItem().Text($"{R.Ref} · generated {F.Stamp(R.GeneratedAt)} by {R.GeneratedBy}").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet);
        });

    private void Footer(IContainer c) =>
        c.BorderTop(0.75f).BorderColor(PdfStyle.Hair).PaddingTop(4.5f).Row(r =>
        {
            r.RelativeItem().Text($"{R.Ref} · generated {F.Stamp(R.GeneratedAt)} by {R.GeneratedBy} · {R.Workspaces.Count} of {R.Memberships} workspaces · period {R.From:yyyy-MM-dd} to {R.To:yyyy-MM-dd} UTC")
                .FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet);
            r.AutoItem().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet));
                t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
            });
        });

    // ---- formatting ------------------------------------------------------------------------------------------------------------------------
    private static string N(long? v) => v is long n ? n.ToString(Inv) : "—";
    private static string Pct(double? v) => v is double d ? d.ToString("0.0", Inv) + " %" : "—";
    private static string Num(double? v, string unit = "") => v is double d ? d.ToString("0.#", Inv) + unit : "—";
    private static string Signed(double? v) => v is double d ? (d > 0 ? "+" : d < 0 ? "−" : "") + Math.Abs(d).ToString("0.#", Inv) + " d" : "—";
    private static string Dash(string s) => s.Length == 0 ? "—" : s;
    private static string Of(long? part, long whole) => part is long p ? $"{p} of {whole}" : "—";

    private void Body(ColumnDescriptor col)
    {
        col.Item().Column(c =>
        {
            c.Item().Text("Portfolio report").FontSize(18f).SemiBold().LetterSpacing(-0.02f);
            c.Item().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontColor(PdfStyle.Quiet));
                t.Span($"{R.Workspaces.Count} of your {R.Memberships} workspace(s): {Scope}");
            });
            c.Item().Text($"Period {R.From:yyyy-MM-dd} to {R.To:yyyy-MM-dd} (UTC days) · data as of {F.Stamp(R.AsOf)} · {(R.FiscalYearStartMonth == 1 ? "calendar quarters" : $"fiscal quarters, the year starts in month {R.FiscalYearStartMonth}")}").FontColor(PdfStyle.Quiet);
        });

        var completed = R.Total.Sum(x => x.Completed);
        var failed = R.Total.Sum(x => x.ChangeFailed ?? 0);
        var restore = R.Restore.Sum(x => x.Restores);
        var openRisk = R.Risk.Sum(x => x.OpenReleases);
        var noRehearsal = R.Risk.Sum(x => x.NoRehearsal ?? 0);
        string Track(string t)
        {
            var row = R.Total.FirstOrDefault(x => x.Track == t);
            return row is null || row.Measured == 0 ? "no release measured" : $"{Pct(row.OnTimePct)} ({Of(row.OnTime, row.Measured)}), median slip {Signed(row.MedianSlipDays)}";
        }
        TrainDocument.Section(col, "Headline", c => TrainDocument.KeyVal(c,
        [
            ("Releases completed", completed == 0 ? Cell.Q("none in the period") : Cell.M(completed.ToString(Inv))),
            ("On time, CERT", Track("CERT")),
            ("On time, Production", Track("Production")),
            ("On time, other types", Track("Other")),
            ("Change failure rate", completed == 0 ? Cell.Q("—") : Cell.M($"{Pct(Math.Round(100.0 * failed / completed, 1))} ({failed} of {completed})")),
            ("Time to restore", restore == 0 ? Cell.Q("no rolled-back run in the period") : Cell.M($"median {Num(R.Restore.Count == 1 ? R.Restore[0].MedianMin : null, " min")} over {restore} rolled-back run(s)")),
            ("Exceptions", Cell.M(R.Exceptions.Sum(x => x.Events).ToString(Inv))),
            ("Open releases", Cell.M($"{openRisk}, of which {noRehearsal} High or Very high without a rehearsed rollback")),
        ]), "On time compares the actual end date with the baseline planned date (D25), separately for releases of type CERT and of type Production. Change failure is release-level (a close code other than Successful), not DORA.");

        TrainDocument.Section(col, "By quarter", c =>
        {
            if (R.ByQuarter.Count == 0) { TrainDocument.Empty(c, "No release completed in the period."); return; }
            TrainDocument.Grid(c, [new("Quarter", 1.2f), new("Track", 1f), new("Completed", 0.9f), new("On time", 0.9f), new("On-time %", 0.9f), new("Median slip", 1f), new("Change failed", 1f)],
                R.ByQuarter.Select(x => new Cell[] { Cell.M(Dash(x.Quarter)), x.Track, Cell.M(x.Completed.ToString(Inv)), Cell.M(Of(x.OnTime, x.Measured)), Cell.M(Pct(x.OnTimePct)), Cell.M(Signed(x.MedianSlipDays)), Cell.M($"{N(x.ChangeFailed)} ({Pct(x.ChangeFailPct)})") }));
        });

        TrainDocument.Section(col, "By workspace", c =>
        {
            if (R.ByWorkspace.Count == 0) { TrainDocument.Empty(c, "No release completed in the period."); return; }
            TrainDocument.Grid(c, [new("Workspace", 1.6f), new("Track", 1f), new("Completed", 0.9f), new("On time", 0.9f), new("On-time %", 0.9f), new("Median slip", 1f), new("Change failed", 1f)],
                R.ByWorkspace.Select(x => new Cell[] { Dash(x.Workspace), x.Track, Cell.M(x.Completed.ToString(Inv)), Cell.M(Of(x.OnTime, x.Measured)), Cell.M(Pct(x.OnTimePct)), Cell.M(Signed(x.MedianSlipDays)), Cell.M($"{N(x.ChangeFailed)} ({Pct(x.ChangeFailPct)})") }));
        });

        TrainDocument.Section(col, "Lead time by workspace", c =>
        {
            if (R.LeadByWorkspace.Count == 0) { TrainDocument.Empty(c, "No release completed in the period."); return; }
            TrainDocument.Grid(c, [new("Workspace", 2f), new("Completed", 1f), new("Median lead time", 1.3f), new("90th percentile", 1.3f)],
                R.LeadByWorkspace.Select(x => new Cell[] { Dash(x.Workspace), Cell.M(x.Completed.ToString(Inv)), Cell.M(Num(x.MedianLeadDays, " d")), Cell.M(Num(x.P90LeadDays, " d")) }));
        }, "Lead time runs from creating the release to completing it.");

        TrainDocument.Section(col, "Risk and compliance exceptions", c =>
        {
            if (R.Exceptions.Count == 0) { TrainDocument.Empty(c, "No exception in the period."); return; }
            TrainDocument.Grid(c, [new("Workspace", 1.6f), new("Exception", 3f), new("Events", 0.8f), new("Releases", 0.8f)],
                R.ExceptionsByWorkspace.Select(x => new Cell[] { Dash(x.Workspace), x.Exception, Cell.M(x.Events.ToString(Inv)), Cell.M(x.Releases.ToString(Inv)) }));
        }, "Waivers and freeze overrides granted, Go conditions left open past their expiry, gates decertified, Compliance gates certified after their due date, and post-implementation reviews overdue.");

        TrainDocument.Section(col, "Risk exposure of open releases (as of now)", c =>
        {
            if (R.RiskByWorkspace.Count == 0) { TrainDocument.Empty(c, "No open release."); return; }
            TrainDocument.Grid(c, [new("Workspace", 1.6f), new("Risk tier", 1f), new("Open", 0.7f), new("Nearest target", 1.1f), new("Past target", 0.9f), new("With blockers", 1f), new("No rehearsal", 1f)],
                R.RiskByWorkspace.Select(x => new Cell[] { Dash(x.Workspace), PdfFmt.Risk(x.RiskTier), Cell.M(x.OpenReleases.ToString(Inv)), Cell.M(x.NearestDays is long d ? $"{d} d" : "—"), Cell.M(N(x.PastTarget)), Cell.M(N(x.WithBlockers)), Cell.M(N(x.NoRehearsal)) }));
        }, "Open = Planning, Gated or Executing. No rehearsal counts High and Very high releases whose rollback has not been rehearsed. This section ignores the period.");
    }
}
