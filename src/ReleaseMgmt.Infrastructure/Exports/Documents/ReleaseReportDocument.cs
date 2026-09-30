using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>Release report (PROJECT_SCOPE 9): summary, products, gate timeline with certification times and cycle times, blockers, communications sent, close code and the scorecard figures.</summary>
public sealed class ReleaseReportDocument(ExportModel model, ExportOptions options) : TrainDocument(model, options)
{
    protected override string DocumentName => "Release report";

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
                t.Span($" · Risk {PdfFmt.Risk(M.Train.RiskTier)} · Target {F.Day(M.Train.TargetDate)} · Status {M.Train.Status} · train version {M.Train.Version}");
            });
        });

        Section(col, "Summary", c => KeyVal(c,
        [
            ("Status", M.Train.Status),
            ("Close code", M.Train.CloseCode is null ? Cell.Q("not closed") : Cell.B(M.Train.CloseCode)),
            ("Deployment window", M.Window is { } w ? Cell.M($"{F.DayShort(w.StartsAt)} {F.Time(w.StartsAt)}–{F.Time(w.EndsAt)} {F.Zone(w.StartsAt)}") : Cell.Q("not set")),
            ("Actual", M.Train.ActualStartAt is { } s ? Cell.M($"{F.DayShort(s)} {F.Time(s)}–{(M.Train.ActualEndAt is { } e ? F.Time(e) + " " + F.Zone(e) : "not finished")}") : Cell.Q("not started")),
            ("Planned release date", fig.Planned is { } p ? Cell.M(F.Day(p)) : Cell.Q("no baseline captured yet")),
            ("Slip", fig.SlipDays is int d ? Cell.M(d <= 0 ? $"on time ({(d == 0 ? "on the day" : $"{-d} day(s) early")})" : $"{d} day(s) late") : Cell.Q("—")),
            ("Products", M.Products.Count == 0 ? Cell.Q("none") : string.Join(" · ", M.Products.Select(x => $"{x.Name} {x.Version}"))),
            ("Notes", Or(M.Train.CloseNotes)),
        ]));

        Section(col, "Gate timeline", c =>
        {
            if (M.Gates.Count == 0) { Empty(c, "No gates."); return; }
            Grid(c, [new("Gate", 1.6f), new("Class", 0.85f), new("Due", 0.95f), new("Status", 0.8f), new("Certified", 1.9f), new("Cycle time", 0.9f)],
                M.Gates.Select(g => new Cell[]
                {
                    g.Name, g.Class, Cell.M(F.DayShort(g.DueOn)), g.Status,
                    g.CertifiedAt is { } at ? Cell.M($"{F.DayTime(at)} {F.Zone(at)} · {g.CertifiedByName}") : Cell.Q("—"),
                    g.CycleHours is double h ? Cell.M($"{h:0.#} h") : Cell.Q("—"),
                }));
        });

        Section(col, "Timeline", c =>
        {
            var ev = new List<(DateTime At, string Text)> { (M.Train.CreatedAt, "Train created") };
            if (M.Baseline is { } b) ev.Add((b.CapturedAt, $"Baseline captured (planned release {F.Day(b.PlannedReleaseDate)})"));
            foreach (var g in M.Gates.Where(g => g.CertifiedAt is not null)) ev.Add((g.CertifiedAt!.Value, $"Gate {g.Name} {g.Status.ToLowerInvariant()} by {g.CertifiedByName}"));
            foreach (var d in M.Decisions) ev.Add((d.DecidedAt, $"Go/No-Go: {PdfFmt.Decision(d.Decision)} ({d.DecidedBy})"));
            if (M.Train.ActualStartAt is { } st) ev.Add((st, "Execution started"));
            if (M.Train.ActualEndAt is { } en) ev.Add((en, $"Execution ended, close code {M.Train.CloseCode ?? "not set"}"));
            if (M.Pir is { HeldAt: { } held }) ev.Add((held, "Post-implementation review held"));
            Grid(c, [new("When", 1.6f), new("Event", 5f)], ev.OrderBy(x => x.At).Select(x => new Cell[] { Cell.M($"{F.Iso(x.At)} {F.Zone(x.At)}"), x.Text }));
        });

        Section(col, "Blockers", c =>
        {
            if (M.Blockers.Count == 0) { Empty(c, "No blockers were raised."); return; }
            Grid(c, [new("Blocker", 2.6f), new("Severity", 0.8f), new("Raised", 1.3f), new("Resolved", 1.3f), new("Open for", 0.9f)],
                M.Blockers.Select(x => new Cell[]
                {
                    x.Title, x.Severity, Cell.M(F.Iso(x.RaisedAt)), x.ResolvedAt is { } r ? Cell.M(F.Iso(r)) : Cell.B("open"),
                    Cell.M(PdfFmt.Minutes(((x.ResolvedAt ?? M.GeneratedAt) - x.RaisedAt).TotalMinutes)),
                }));
        });

        Section(col, "Communications sent", c =>
        {
            var sent = M.Comms.Where(x => !x.Rehearsal).ToList();
            if (sent.Count == 0) { Empty(c, "No communications were dispatched."); return; }
            Grid(c, [new("Sent", 1.5f), new("Message", 2f), new("Audience", 1.4f), new("Channel", 0.9f), new("Outcome", 0.9f), new("By", 1.2f)],
                sent.Select(x => new Cell[] { Cell.M(F.Iso(x.At)), x.Type, x.Audience, x.Channel, x.Outcome, x.By }));
        });

        Section(col, "Scorecard", c => KeyVal(c,
        [
            ("On time", fig.OnTime is bool ot ? (ot ? "Yes: actual end on or before the baseline planned date" : "No: actual end after the baseline planned date") : Cell.Q("not measurable until the train is complete and has a baseline")),
            ("Gates late", $"{fig.GatesLate} of {fig.GatesTotal} certified after their due date" + (fig.AvgGateDaysLate is double a ? $" (average {a:0.#} day(s) late)" : "")),
            ("Scope churn", fig.AtFreeze is int n ? $"{n} product(s) at freeze; {fig.Added.Count} added, {fig.Removed.Count} removed" : Cell.Q("no baseline")),
            ("Runbook variance", fig.TotalOverrunMin is double t ? $"total overrun {t:0.#} min; {fig.StepsSkipped} skipped, {fig.StepsFailed} failed" : Cell.Q("no live run")),
            ("Rollback", fig.RolledBack ? "The live run was rolled back" : Cell.Q("no rollback")),
        ]), "Full detail is in the post-release scorecard.");
    }
}
