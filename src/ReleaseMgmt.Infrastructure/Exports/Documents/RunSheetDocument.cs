using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>
/// Runbook run sheet (PROJECT_SCOPE 9), built to be printed and used with a pen: steps in planned order grouped by section, each with owner, planned time, dependencies and its full
/// instructions, and write-in boxes for actual start, end and initials. The Rollback section starts on its own page. When a live run exists its actuals are printed in those columns
/// (with variance and skip notes); the plan is never overwritten.
/// </summary>
public sealed class RunSheetDocument(ExportModel model, ExportOptions options) : TrainDocument(model, options)
{
    protected override string DocumentName => "Run sheet";
    private static readonly string[] Order = ["PreCheck", "Deploy", "Verify", "Hypercare"];
    private static readonly Col[] Cols = [new("Step", 0.62f), new("What to do", 2.85f), new("Owner", 0.9f), new("Planned", 1.1f), new("Needs", 0.7f), new("Actual start – end", 1.55f), new("Initials", 0.75f)];

    protected override void Body(ColumnDescriptor col)
    {
        col.Item().Column(c =>
        {
            c.Item().Text(M.Train.Title).FontSize(18f).SemiBold().LetterSpacing(-0.02f);
            c.Item().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontColor(PdfStyle.Quiet));
                t.Span(M.Train.ChangeTicket ?? "no change ticket").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
                t.Span($" · Risk {PdfFmt.Risk(M.Train.RiskTier)} · train version {M.Train.Version}");
                if (M.Window is { } w) { t.Span(" · Window "); t.Span($"{F.DayShort(w.StartsAt)} {F.Time(w.StartsAt)}–{F.Time(w.EndsAt)} {F.Zone(w.StartsAt)}").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize); }
            });
        });

        if (M.Steps.Count == 0) { col.Item().Text("No runbook steps are planned for this train.").FontColor(PdfStyle.Quiet); return; }

        var live = M.LiveRun;
        col.Item().Text(live is null
                ? "Plan only: no live run has started. Write actual times and initials in the boxes as each step completes."
                : $"Live run started {F.DayTime(live.StartedAt)} {F.Zone(live.StartedAt)} by {live.StartedBy}; outcome {live.Outcome ?? "in progress"}. Actual columns show the recorded times; the plan is unchanged.")
            .FontColor(PdfStyle.Quiet);

        foreach (var section in Order)
        {
            var steps = M.Steps.Where(s => s.Section == section).ToList();
            if (steps.Count == 0) continue;
            Section(col, section switch { "PreCheck" => "Pre-checks", "Deploy" => "Deploy", "Verify" => "Verify", _ => "Hypercare" }, c => Steps(c, steps, live));
        }

        var rollback = M.Steps.Where(s => s.Section == "Rollback").ToList();
        col.Item().PageBreak();
        Section(col, "Rollback: only if the rollback decision has been made", c =>
        {
            if (rollback.Count == 0) { Empty(c, "No rollback steps are planned. Do not proceed without a backout plan."); return; }
            Steps(c, rollback, live);
        }, M.Train.RollbackRehearsedAt is { } r ? $"Rehearsed {F.Day(r)}{(M.Train.RollbackRehearsedBy is { } by ? " by " + by : "")}." : "This rollback has not been rehearsed.");
    }

    private void Steps(IContainer c, List<StepRow> steps, RunRow? live) =>
        Grid(c, Cols, steps.Select(s =>
        {
            var e = live is not null && live.Executions.TryGetValue(s.Id, out var ex) ? ex : null;
            Cell what = Cell.R(t =>
            {
                t.Span(s.Title).SemiBold();
                if (s.ProductName is not null) t.Span($"  ({s.ProductName})").FontColor(PdfStyle.Quiet);
                if (!string.IsNullOrWhiteSpace(s.Instructions)) { t.Line(""); t.Span(s.Instructions.Trim()).FontSize(PdfStyle.Small); }
                if (e is { Status: "Skipped" or "Failed", Note: { Length: > 0 } note }) { t.Line(""); t.Span($"{e.Status}: {note}").FontSize(PdfStyle.Small).SemiBold(); }
            });
            Cell actual = e?.ActualStart is { } a
                ? Cell.R(t =>
                {
                    t.Span($"{F.Time(a)}–{(e.ActualEnd is { } z ? F.Time(z) : "…")}").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
                    t.Line(""); t.Span($"{e.Status} · {PdfFmt.Signed((a - s.PlannedStart).TotalMinutes)} start").FontSize(PdfStyle.Tiny).FontColor(PdfStyle.Quiet);
                })
                : e is { Status: "Skipped" } ? Cell.Q("skipped") : Cell.Q("__:__ – __:__");
            return new Cell[]
            {
                Cell.M(s.Code), what, OwnerOrDash(s.OwnerName), Cell.M($"{F.Time(s.PlannedStart)}–{F.Time(s.PlannedEnd)}\n{s.PlannedMinutes} min"),
                s.DependsOn.Count == 0 ? Cell.Q("—") : Cell.M(string.Join(", ", s.DependsOn)), actual, e?.ActorName is { } who ? who : Cell.Q("______"),
            };
        }));
}
