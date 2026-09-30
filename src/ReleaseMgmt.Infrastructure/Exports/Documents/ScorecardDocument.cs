using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>Post-release scorecard (PROJECT_SCOPE 9): planned vs actual, on-time, churn, variance, incidents/rollback and the PIR. Definitions follow db/analytics.sql.</summary>
public sealed class ScorecardDocument(ExportModel model, ExportOptions options) : TrainDocument(model, options)
{
    protected override string DocumentName => "Post-release scorecard";

    protected override void Body(ColumnDescriptor col)
    {
        var fig = ScorecardFigures.Compute(M, F);
        col.Item().Column(c =>
        {
            c.Item().Text(M.Train.Title).FontSize(18f).SemiBold().LetterSpacing(-0.02f);
            c.Item().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontColor(PdfStyle.Quiet));
                t.Span(M.Train.ChangeTicket ?? "no change ticket").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
                t.Span($" · Status {M.Train.Status} · Close code {M.Train.CloseCode ?? "not closed"} · train version {M.Train.Version}");
            });
        });

        Section(col, "Headline", c => KeyVal(c,
        [
            ("On time", fig.OnTime is bool ot ? Cell.B(ot ? "Yes" : "No") : Cell.Q("not measurable until the train is complete and has a baseline")),
            ("Slip", fig.SlipDays is int d ? Cell.M(d == 0 ? "0 days" : d < 0 ? $"{-d} day(s) early" : $"{d} day(s) late") : Cell.Q("—")),
            ("Gates late", $"{fig.GatesLate} of {fig.GatesTotal}"),
            ("Scope churn", fig.AtFreeze is int n ? $"+{fig.Added.Count} / −{fig.Removed.Count} vs {n} at freeze" : Cell.Q("no baseline")),
            ("Runbook overrun", fig.TotalOverrunMin is double t ? Cell.M(PdfFmt.Signed(t)) : Cell.Q("no live run")),
            ("Rollback", fig.RolledBack ? Cell.B("performed") : Cell.Q("none")),
        ]), "On time compares the actual end date with the baseline planned date (D25), the same rule as the Analytics screen.");

        Section(col, "Planned vs actual", c =>
        {
            var rows = new List<Cell[]>
            {
                new Cell[] { "Release date", fig.Planned is { } p ? Cell.M(F.Day(p)) : Cell.Q("no baseline"), fig.Actual is { } a ? Cell.M(F.Day(a)) : Cell.Q("—"), fig.SlipDays is int s ? Cell.M($"{s:+#;-#;0} d") : Cell.Q("—") },
            };
            if (M.Window is { } w)
            {
                rows.Add(["Window start", Cell.M($"{F.DayTime(w.StartsAt)} {F.Zone(w.StartsAt)}"), M.Train.ActualStartAt is { } st ? Cell.M($"{F.DayTime(st)} {F.Zone(st)}") : Cell.Q("—"), M.Train.ActualStartAt is { } st2 ? Cell.M(PdfFmt.Signed((st2 - w.StartsAt).TotalMinutes)) : Cell.Q("—")]);
                rows.Add(["Window end", Cell.M($"{F.DayTime(w.EndsAt)} {F.Zone(w.EndsAt)}"), M.Train.ActualEndAt is { } en ? Cell.M($"{F.DayTime(en)} {F.Zone(en)}") : Cell.Q("—"), M.Train.ActualEndAt is { } en2 ? Cell.M(PdfFmt.Signed((en2 - w.EndsAt).TotalMinutes)) : Cell.Q("—")]);
            }
            foreach (var g in M.Gates)
                rows.Add([$"Gate: {g.Name}", Cell.M(F.Day(g.DueOn)), g.CertifiedAt is { } at ? Cell.M(F.Day(at)) : Cell.Q(g.Status),
                    g.CertifiedAt is { } at2 ? Cell.M($"{F.Clock.LocalDate(at2).DayNumber - g.DueOn.DayNumber:+#;-#;0} d") : Cell.Q("—")]);
            Grid(c, [new("Item", 2.4f), new("Planned", 1.9f), new("Actual", 1.9f), new("Variance", 0.9f)], rows);
        });

        Section(col, "Scope churn", c =>
        {
            if (fig.AtFreeze is null) { Empty(c, "No baseline was captured, so scope churn cannot be computed."); return; }
            KeyVal(c,
            [
                ("At freeze", $"{fig.AtFreeze} product(s) (baseline captured {F.Iso(M.Baseline!.CapturedAt)} {F.Zone(M.Baseline.CapturedAt)})"),
                ("Added since", fig.Added.Count == 0 ? Cell.Q("none") : string.Join(" · ", fig.Added)),
                ("Removed since", fig.Removed.Count == 0 ? Cell.Q("none") : string.Join(" · ", fig.Removed)),
            ]);
        });

        Section(col, "Runbook variance (live run)", c =>
        {
            if (M.LiveRun is null) { Empty(c, "The train has not had a live run."); return; }
            Grid(c, [new("Step", 0.7f), new("Title", 3f), new("Start late", 1f), new("Overrun", 1f)],
                fig.Variances.Take(12).Select(v => new Cell[] { Cell.M(v.Code), v.Title, v.StartLateMin is double s ? Cell.M(PdfFmt.Signed(s)) : Cell.Q("—"), v.OverrunMin is double o ? Cell.M(PdfFmt.Signed(o)) : Cell.Q("running") }));
        }, M.LiveRun is null ? null : $"Largest overruns first, at most 12 of {fig.Variances.Count} steps. {fig.StepsSkipped} step(s) skipped, {fig.StepsFailed} failed.");

        Section(col, "Incidents and rollback", c => KeyVal(c,
        [
            ("Live run outcome", fig.RunOutcome ?? "no live run"),
            ("Rollback", fig.RolledBack ? Cell.B("The live run was rolled back") : Cell.Q("no rollback")),
            ("Rollback rehearsal", M.Train.RollbackRehearsedAt is { } r ? Cell.M($"{F.Day(r)}{(M.Train.RollbackRehearsedBy is { } by ? " by " + by : "")}") : Cell.Q("not rehearsed")),
            ("Blockers", $"{fig.BlockersRaised} raised, {fig.BlockersOpen} still open"),
            ("Known issues", M.KnownIssues.Count == 0 ? Cell.Q("none") : string.Join(" · ", M.KnownIssues.Select(k => $"{k.Title} ({k.Severity}, {k.Status})"))),
        ]));

        Section(col, "Post-implementation review", c =>
        {
            if (M.Pir is not { } pir) { Empty(c, "No review is required for this train."); return; }
            c.Column(x =>
            {
                x.Item().Element(k => KeyVal(k,
                [
                    ("Status", pir.Status),
                    ("Required because", pir.RequiredReason),
                    ("Held", pir.HeldAt is { } h ? Cell.M(F.Day(h)) : Cell.Q("not held")),
                    ("Summary", Or(pir.Summary)),
                ]));
                if (pir.Actions.Count > 0)
                    x.Item().PaddingTop(4).Element(g => Grid(g, [new("Action", 3.2f), new("Owner", 1.2f), new("Due", 1f), new("Done", 1f)],
                        pir.Actions.Select(a => new Cell[] { a.Text, a.OwnerName, Cell.M(F.DayShort(a.DueOn)), a.DoneAt is { } d ? Cell.M(F.DayShort(d)) : Cell.B("open") })));
            });
        });
    }
}
