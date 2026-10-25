using QuestPDF.Fluent;
using System.Security.Cryptography;
using System.Text;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports.Documents;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// REOS-121: gathers what the portfolio PDF prints by running the executive metrics (M16, M17, M18, M21, M22) through <see cref="AnalyticsService"/> with the window the caller built:
/// the workspaces in it are already limited to the person's memberships. The PDF therefore shows what the Analytics screen and the workbook show for the same window.
/// </summary>
public sealed class PortfolioReportBuilder(AnalyticsService analytics, ExportOptions options)
{
    public async Task<PortfolioReport> BuildAsync(AnalyticsWindow w, IReadOnlyList<string> workspaceNames, int memberships, DateTime generatedAt, string generatedBy, DisplayClock clock, CancellationToken ct = default)
    {
        AnalyticsWindow By(params string[] by) => w with { GroupBy = by };
        var total = await analytics.M17Async(By(), ct);
        var byQuarter = await analytics.M17Async(By("quarter"), ct);
        var byWorkspace = await analytics.M17Async(By("workspace"), ct);
        var restore = await analytics.M16Async(By(), ct);
        var lead = await analytics.M22Async(By("workspace"), ct);
        var exceptions = await analytics.M18Async(By(), ct);
        var exceptionsByWs = await analytics.M18Async(By("workspace"), ct);
        var risk = await analytics.M21Async(By(), ct);
        var riskByWs = await analytics.M21Async(By("workspace"), ct);
        return new PortfolioReport(Ref(w, generatedAt), generatedAt, generatedBy, clock, workspaceNames, memberships, w.From, w.To, w.Now, w.FiscalYearStartMonth,
            total, byQuarter, byWorkspace, restore, lead, exceptions, exceptionsByWs, risk, riskByWs);
    }

    /// <summary>PF- and six hex digits of a hash over the workspaces, the period and the generation time: the same report is the same file.</summary>
    public static string Ref(AnalyticsWindow w, DateTime generatedAt)
    {
        var text = string.Join(',', w.Scope.Order(StringComparer.Ordinal)) + $"|{w.FromText}|{w.ToText}|{generatedAt:yyyy-MM-ddTHH:mm:ssZ}";
        return "PF-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..6];
    }

    /// <summary>Renders the report. Throws <see cref="ExportConfigException"/> with a readable message when the QuestPDF licence is missing or not permitted here.</summary>
    public byte[] Render(PortfolioReport report)
    {
        options.ApplyLicense();
        return new PortfolioDocument(report, options).GeneratePdf();
    }
}
