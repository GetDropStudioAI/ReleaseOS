namespace ReleaseMgmt.Infrastructure.Exports.Documents;

public sealed record StepVariance(string Code, string Title, double? StartLateMin, double? OverrunMin);

/// <summary>Derived figures shared by the release report and the scorecard. Same definitions as db/analytics.sql (on time = actual end date on or before the BASELINE planned date, D25).</summary>
public sealed record ScorecardFigures(
    DateOnly? Planned, DateOnly? Actual, int? SlipDays, bool? OnTime, int GatesLate, int GatesTotal, double? AvgGateDaysLate,
    int? AtFreeze, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    double? TotalOverrunMin, IReadOnlyList<StepVariance> Variances, int StepsSkipped, int StepsFailed,
    bool RolledBack, string? RunOutcome, int BlockersRaised, int BlockersOpen, int KnownIssues)
{
    public static ScorecardFigures Compute(ExportModel m, PdfFmt f)
    {
        DateOnly? actual = m.Train.ActualEndAt is { } end ? f.Clock.LocalDate(end) : null;
        // analytics.sql M1 uses date(ActualEndAt) in UTC; the report prints the same rule so the numbers agree with the Analytics screen.
        DateOnly? actualUtc = m.Train.ActualEndAt is { } e2 ? DateOnly.FromDateTime(e2) : null;
        DateOnly? planned = m.Baseline?.PlannedReleaseDate;
        int? slip = planned is { } p && actualUtc is { } a ? a.DayNumber - p.DayNumber : null;
        bool? onTime = slip is int s ? s <= 0 : null;

        var cert = m.Gates.Where(g => g.CertifiedAt is not null).ToList();
        var late = cert.Where(g => DateOnly.FromDateTime(g.CertifiedAt!.Value) > g.DueOn).ToList();
        double? avgLate = cert.Count == 0 ? null : Math.Round(cert.Average(g => Math.Max(0, DateOnly.FromDateTime(g.CertifiedAt!.Value).DayNumber - g.DueOn.DayNumber)), 1);

        List<string> added = [], removed = [];
        int? atFreeze = null;
        if (m.Baseline is { } b)
        {
            atFreeze = b.ProductNames.Count;
            var now = m.Products.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            added = m.Products.Select(x => x.Name).Where(n => !b.ProductNames.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            removed = b.ProductNames.Where(n => !now.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        var live = m.LiveRun;
        var variances = new List<StepVariance>();
        int skipped = 0, failed = 0;
        if (live is not null)
            foreach (var s2 in m.Steps)
            {
                if (!live.Executions.TryGetValue(s2.Id, out var e)) continue;
                if (e.Status == "Skipped") skipped++;
                if (e.Status == "Failed") failed++;
                if (e.ActualStart is { } st)
                {
                    double? over = e.ActualEnd is { } en ? Math.Round((en - st).TotalMinutes - s2.PlannedMinutes, 1) : null;
                    variances.Add(new(s2.Code, s2.Title, Math.Round((st - s2.PlannedStart).TotalMinutes, 1), over));
                }
            }
        double? total = variances.Any(v => v.OverrunMin is not null) ? variances.Sum(v => v.OverrunMin ?? 0) : null;
        var rolledBack = live?.Outcome == "RolledBack";
        _ = actual;
        return new(planned, actualUtc, slip, onTime, late.Count, m.Gates.Count, avgLate, atFreeze, added, removed, total,
            variances.OrderByDescending(v => v.OverrunMin ?? double.MinValue).ThenBy(v => v.Code, StringComparer.Ordinal).ToList(), skipped, failed,
            rolledBack, live?.Outcome, m.Blockers.Count, m.Blockers.Count(x => x.ResolvedAt is null), m.KnownIssues.Count);
    }
}
