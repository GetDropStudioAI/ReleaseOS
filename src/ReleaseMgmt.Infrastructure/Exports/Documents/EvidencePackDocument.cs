using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Exports.Documents;

/// <summary>
/// The audit evidence pack (PROJECT_SCOPE 9). Page 1 reproduces mockups/EvidencePack.html on Letter: header, title block, sections 1 to 5 (the manifest's first four files)
/// and the footer that names sections 6 to 9. Long fields are clipped on page 1 (the full text follows on the next pages) so that page 1 stays one page.
/// Pages 2+: change record in full, certifications with the SoD check, Go/No-Go decisions, exceptions, the complete manifest with full SHA-256, runbook actuals,
/// the gate transition log, the audit-log extract and the metric snapshot (rows read back from MetricSnapshots).
/// </summary>
public sealed class EvidencePackDocument(ExportModel model, ExportOptions options) : TrainDocument(model, options)
{
    protected override string DocumentName => "Audit evidence pack";

    private const int FirstPageGates = 8, FirstPageExceptions = 4, FirstPageFiles = 4, FirstPageConditions = 2, Clip1 = 190;

    protected override void Footer(IContainer c)
    {
        // Page 1 carries the mockup's footer (which sections follow); later pages carry generated-at / generated-by / train version like every other PDF.
        c.Column(col =>
        {
            col.Item().ShowOnce().Element(x => x.BorderTop(0.75f).BorderColor(PdfStyle.Hair).PaddingTop(4.5f).Row(r =>
            {
                r.RelativeItem().Text("Sections 6–9: runbook actuals · gate transition log · audit events · metric snapshot").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet);
                r.AutoItem().Element(PageNumber);
            }));
            col.Item().SkipOnce().Element(base.Footer);
        });
    }

    protected override void Body(ColumnDescriptor col)
    {
        TitleBlock(col);
        Section(col, "1 · Change record", ChangeRecordSummary);
        var gatesNote = GateSummarySentence();
        Section(col, "2 · Approvals and certifications", Certifications, gatesNote);
        Section(col, "3 · Go/No-Go decision", GoNoGoSummary);
        Section(col, "4 · Exceptions", c => Exceptions(c, FirstPageExceptions));
        var files = M.Attachments;
        var shown = Math.Min(FirstPageFiles, files.Count);
        Section(col, files.Count == 0 ? "5 · Evidence manifest (none)" : files.Count <= FirstPageFiles ? $"5 · Evidence manifest (all {files.Count})" : $"5 · Evidence manifest (first {shown} of {files.Count})",
            c => ManifestShort(c, shown), ManifestNote());

        col.Item().PageBreak();
        Continued(col);
    }

    // ---- page 1 ---------------------------------------------------------------------------------------------------------------------------

    private void TitleBlock(ColumnDescriptor col) =>
        col.Item().Column(c =>
        {
            c.Item().Text(M.Train.Title).FontSize(18f).SemiBold().LetterSpacing(-0.02f);
            c.Item().Text(t =>
            {
                t.DefaultTextStyle(x => x.FontColor(PdfStyle.Quiet));
                t.Span(M.Train.ChangeTicket ?? "no change ticket").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
                t.Span($" · Risk {PdfFmt.Risk(M.Train.RiskTier)} · Target {F.Day(M.Train.TargetDate)} · Close code ");
                t.Span(M.Train.CloseCode ?? "not closed").SemiBold().FontColor(PdfStyle.Ink);
                t.Span($" · train version {M.Train.Version} · records are append-only");
            });
        });

    private DateTime? PlannedFinish()
    {
        var run = M.Steps.Where(s => s.Section != "Rollback").Select(s => (DateTime?)s.PlannedEnd).Max();
        return run ?? M.Window?.EndsAt;
    }

    private void ChangeRecordSummary(IContainer c)
    {
        var products = M.Products.Count == 0 ? "No products bundled" : string.Join(" · ", M.Products.Select(p => $"{p.Name} {p.Version}"));
        if (M.Cis.Count > 0) products += $" · {M.Cis.Count} affected CI{(M.Cis.Count == 1 ? "" : "s")}";
        var runSteps = M.Steps.Count(s => s.Section != "Rollback");
        var plan = $"Runbook, {runSteps} step{(runSteps == 1 ? "" : "s")} (section 6)." + (M.Window is { } w ? $" Planned window {F.DayShort(w.StartsAt)} {F.Time(w.StartsAt)}–{F.Time(w.EndsAt)} {F.Zone(w.StartsAt)}." : " No deployment window set.");
        if (!string.IsNullOrWhiteSpace(M.Change.ImplementationPlan)) plan = Clip(M.Change.ImplementationPlan, Clip1 / 2) + " " + plan;

        string actual;
        if (M.Train.ActualStartAt is not { } s0) actual = "not started";
        else if (M.Train.ActualEndAt is not { } e0) actual = $"started {F.Time(s0)} {F.Zone(s0)}, not finished";
        else
        {
            actual = $"{F.Time(s0)}–{F.Time(e0)} {F.Zone(e0)}";
            if (PlannedFinish() is { } pf) actual += $" · {PdfFmt.Signed((e0 - pf).TotalMinutes)} vs plan finish {F.Time(pf)}";
        }

        var rollback = M.Steps.Where(s => s.Section == "Rollback").ToList();
        var backout = "No rollback steps planned.";
        if (rollback.Count > 0)
        {
            var span = (rollback.Max(s => s.PlannedEnd) - rollback.Min(s => s.PlannedStart)).TotalMinutes;
            backout = $"{rollback.Count} step{(rollback.Count == 1 ? "" : "s")}, {Math.Round(span):0} min. ";
        }
        if (M.Train.RollbackRehearsedAt is { } rh)
        {
            var rehearsal = M.Runs.Where(r => r.Mode == "Rehearsal" && r.EndedAt is not null && r.Outcome == "Completed").OrderByDescending(r => r.EndedAt).FirstOrDefault();
            backout += $"Rehearsed {F.DayShort(rh)}" + (rehearsal is { EndedAt: { } end } ? $" in {Math.Round((end - rehearsal.StartedAt).TotalMinutes):0} min" : "") + (M.Train.RollbackRehearsedBy is { } by ? $" by {by}" : "") + ".";
        }
        else backout += "Not rehearsed.";
        if (!string.IsNullOrWhiteSpace(M.Change.BackoutPlan)) backout += " " + Clip(M.Change.BackoutPlan, Clip1 / 2);

        KeyVal(c,
        [
            ("Justification", Clip(Or(M.Change.Justification), Clip1)),
            ("Scope", Clip(products, Clip1 + 40)),
            ("Implementation plan", Clip(plan, Clip1 + 40)),
            ("Actual", Cell.M(actual)),
            ("Backout plan", Clip(backout, Clip1 + 40)),
            ("Test plan", Clip(Or(M.Change.TestPlan), Clip1)),
        ]);
    }

    private static string Words(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve", _ => n.ToString(),
    };

    private string GateSummarySentence()
    {
        var n = M.Gates.Count;
        if (n == 0) return "No gates are defined for this train.";
        var certified = M.Gates.Count(g => g.Status == "Certified");
        var waived = M.Gates.Count(g => g.Status == "Waived");
        var open = n - certified - waived;
        var late = M.Gates.Count(g => g.CertifiedAt is { } at && F.Clock.LocalDate(at) > g.DueOn);
        var exec = M.Train.ActualStartAt;
        var afterExec = exec is { } ex ? M.Gates.Count(g => g.CertifiedAt is { } at && at > ex) : 0;
        string s;
        if (open == 0 && late == 0 && afterExec == 0)
            s = $"All {Words(n)} gate{(n == 1 ? "" : "s")} certified on or before {(n == 1 ? "its" : "their")} due date" + (exec is { } e1 ? $" and before the train entered Executing ({F.DayTime(e1)})." : ".");
        else
        {
            s = $"{certified + waived} of {n} gates certified or waived";
            if (late > 0) s += $"; {late} after {(late == 1 ? "its" : "their")} due date";
            if (open > 0) s += $"; {open} not yet certified";
            if (afterExec > 0) s += $"; {afterExec} after the train entered Executing ({F.DayTime(exec!.Value)})";
            s += ".";
        }
        return s + (waived == 0 ? " No gate was waived." : $" {waived} gate{(waived == 1 ? " was" : "s were")} waived (section 4).");
    }

    private Cell[] GateRowCells(GateRow g)
    {
        var sod = ExportSupport.EvaluateSod(g.Class, g.Status, g.CertifiedByRole, g.CertifiedByUserId, g.Tasks.Select(t => t.CompletedByUserId).ToList());
        var by = g.CertifiedByName is null ? (Cell)Cell.Q(g.Status switch { "Pending" => "not started", "InProgress" => "in progress", "Failed" => "failed", _ => g.Status })
            : g.Class == "Compliance" && g.CertifiedByRole is not null ? $"{g.CertifiedByName} · {PdfFmt.RoleLabel(g.CertifiedByRole)}" : g.CertifiedByName;
        Cell sodCell = !sod.Applies ? Cell.Q(sod.Text)
            : Cell.R(t => { t.Span(sod.Passed ? "✓ " : "✗ ").FontColor(sod.Passed ? PdfStyle.Good : PdfStyle.Bad); t.Span(sod.Text).FontColor(sod.Passed ? PdfStyle.Ink : PdfStyle.Bad); });
        return [g.Name, g.Class, by, g.CertifiedAt is { } at ? Cell.M(F.DayTime(at)) : Cell.Q("—"), Cell.M(F.DowDay(g.DueOn)), sodCell];
    }

    private static readonly Col[] GateCols = [new("Gate", 1.45f), new("Class", 0.85f), new("Certified by", 1.7f), new("At", 1.4f), new("Due", 0.65f), new("Separation of duties", 1.9f)];

    private void Certifications(IContainer c)
    {
        if (M.Gates.Count == 0) { Empty(c, "No gates."); return; }
        Grid(c, GateCols, M.Gates.Take(FirstPageGates).Select(GateRowCells));
    }

    private void GoNoGoSummary(IContainer c)
    {
        if (M.Decisions.Count == 0)
        {
            KeyVal(c, [("Decision", Cell.Q("No Go/No-Go decision has been recorded for this train."))]);
            return;
        }
        var d = M.Decisions[^1];
        var rows = new List<(string, Cell)>
        {
            ("Decision", Cell.R(t =>
            {
                t.Span(PdfFmt.Decision(d.Decision)).SemiBold();
                t.Span($" · {d.DecidedBy}{(d.DecidedByRole is null ? "" : ", " + (d.DecidedByRole == "ReleaseManager" ? "Release Manager" : PdfFmt.RoleLabel(d.DecidedByRole)))} · ");
                t.Span(F.DayTime(d.DecidedAt)).FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
            })),
            ("Gate states at decision", d.GatesTotal == 0 ? "gate states were not recorded"
                : $"{d.GatesCertified} of {d.GatesTotal} certified · " + (d.OpenBlockersThen == 0 ? "no open blockers" : $"{d.OpenBlockersThen} open blocker{(d.OpenBlockersThen == 1 ? "" : "s")} ({d.WorstOpenSeverity})")),
        };
        var conds = d.Conditions.Take(FirstPageConditions).ToList();
        for (var i = 0; i < conds.Count; i++)
        {
            var k = conds[i];
            rows.Add((i == 0 ? (d.Conditions.Count == 1 ? "Condition" : "Conditions") : "", Cell.R(t =>
            {
                t.Span($"{Clip(k.Text, 110)} · owner {k.OwnerName} · due by {F.DowTime(k.ExpiresAt)} · ");
                if (k.ClosedAt is { } cl) t.Span($"closed {F.DayTime(cl)}").SemiBold();
                else if (k.ExpiresAt < M.GeneratedAt) t.Span("open, expired").SemiBold().FontColor(PdfStyle.Bad);
                else t.Span("open").SemiBold();
            })));
        }
        if (d.Conditions.Count > FirstPageConditions) rows.Add(("", Cell.Q($"and {d.Conditions.Count - FirstPageConditions} more (section 3 in full)")));
        if (!string.IsNullOrWhiteSpace(d.Notes)) rows.Add(("Notes", Clip(d.Notes, 150)));
        if (M.Decisions.Count > 1) rows.Add(("Earlier decisions", Cell.Q($"{M.Decisions.Count - 1} earlier decision{(M.Decisions.Count == 2 ? "" : "s")} (section 3 in full)")));
        KeyVal(c, rows);
    }

    private sealed record ExceptionRow(DateTime At, Cell[] Cells);

    private List<ExceptionRow> ExceptionRows()
    {
        var rows = new List<ExceptionRow>();
        foreach (var o in M.Overrides)
            rows.Add(new(o.ApprovedAt, [$"Freeze override {o.Ref}", Cell.R(t => { t.Span($"{o.WindowName}. “{o.Reason}”"); }), o.RequestedBy,
                Cell.R(t => { t.Span(o.ApprovedBy); t.Line(""); t.Span(F.DowDayTime(o.ApprovedAt)).FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet); }), Cell.M(F.DowDayTime(o.ExpiresAt))]));
        foreach (var w in M.Waivers)
            rows.Add(new(w.RequestedAt, [$"Gate waiver · {w.GateName}", Cell.R(t => t.Span($"“{w.Reason}”")), w.RequestedBy,
                w.ApprovedBy is null ? Cell.Q("pending") : Cell.R(t => { t.Span(w.ApprovedBy); t.Line(""); t.Span(F.DowDayTime(w.ApprovedAt!.Value)).FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize).FontColor(PdfStyle.Quiet); }), Cell.Q("—")]));
        foreach (var e in M.Escalations)
            rows.Add(new(e.At, ["Late start escalation", e.Detail, Cell.Q("system"), Cell.Q("—"), Cell.Q("—")]));
        return rows.OrderBy(r => r.At).ToList();
    }

    private static readonly Col[] ExceptionCols = [new("Type", 1.35f), new("Detail", 2.9f), new("Requested", 0.95f), new("Approved", 1.2f), new("Expiry", 1.1f)];

    private void Exceptions(IContainer c, int max)
    {
        var rows = ExceptionRows();
        if (rows.Count == 0) { Empty(c, "No waivers, freeze overrides or escalations were recorded for this train."); return; }
        c.Column(x =>
        {
            x.Item().Element(g => Grid(g, ExceptionCols, rows.Take(max).Select(r => r.Cells)));
            if (rows.Count > max) x.Item().PaddingTop(3).Text($"and {rows.Count - max} more (section 4 in full)").FontColor(PdfStyle.Quiet);
        });
    }

    private static readonly Col[] ManifestShortCols = [new("#", 0.3f), new("File", 2.4f), new("Gate", 1.2f), new("sha256", 1.35f), new("Uploaded", 1.6f)];

    private void ManifestShort(IContainer c, int count)
    {
        if (M.Attachments.Count == 0) { Empty(c, "No evidence files are attached to this train."); return; }
        Grid(c, ManifestShortCols, M.Attachments.Take(count).Select(a => new Cell[] { Cell.M(a.No.ToString()), a.FileName, a.EntityLabel, Cell.M(ExportSupport.ShortHash(a.Sha256)), $"{a.UploaderName} · {F.DayShort(a.UploadedAt)}" }));
    }

    private string? ManifestNote()
    {
        if (M.Attachments.Count == 0) return null;
        var locked = M.Attachments.Count(a => a.Locked);
        var zip = "Files are in the accompanying ZIP with manifest.csv. ";
        return zip + (locked == M.Attachments.Count ? "Each file was locked when its gate certified." : $"{locked} of {M.Attachments.Count} files are locked (their gate is certified); the rest can still change.");
    }

    // ---- pages 2+ -------------------------------------------------------------------------------------------------------------------------

    private void Continued(ColumnDescriptor col)
    {
        Section(col, "1 · Change record (full text)", c => KeyVal(c,
        [
            ("Justification", Or(M.Change.Justification)),
            ("Implementation plan", Or(M.Change.ImplementationPlan)),
            ("Risk and impact", Or(M.Change.RiskImpactAnalysis)),
            ("Backout plan", Or(M.Change.BackoutPlan)),
            ("Test plan", Or(M.Change.TestPlan)),
            ("Communication plan", Or(M.Change.CommunicationPlan)),
            ("CAB date", M.Change.CabDate is { } cab ? Cell.M(F.Day(cab)) : Cell.Q("—")),
            ("Affected CIs", M.Cis.Count == 0 ? Cell.Q("none recorded") : string.Join(" · ", M.Cis)),
            ("Products", M.Products.Count == 0 ? Cell.Q("none") : string.Join(" · ", M.Products.Select(p => $"{p.Name} {p.Version} ({p.ProjectCode})"))),
        ]));

        Section(col, "2 · Approvals and certifications (all gates, with the separation-of-duties check)", c =>
        {
            if (M.Gates.Count == 0) { Empty(c, "No gates."); return; }
            Grid(c, [new("Gate", 1.35f), new("Class", 0.95f), new("Owner", 0.9f), new("Status", 0.8f), new("Certified by · at", 2.2f), new("Separation of duties", 2.4f)],
                M.Gates.Select(g =>
                {
                    var sod = ExportSupport.EvaluateSod(g.Class, g.Status, g.CertifiedByRole, g.CertifiedByUserId, g.Tasks.Select(t => t.CompletedByUserId).ToList());
                    var doneBy = g.Tasks.Where(t => t.Done).GroupBy(t => t.CompletedByName ?? "?").OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} ({x.Count()})").ToList();
                    var who = g.CertifiedByName is null ? "—" : $"{g.CertifiedByName}{(g.CertifiedByRole is null ? "" : ", " + PdfFmt.RoleLabel(g.CertifiedByRole))} · {F.Iso(g.CertifiedAt!.Value)} {F.Zone(g.CertifiedAt.Value)}";
                    var rule = g.Class == "Compliance"
                        ? (sod.Passed ? "✓ " : "✗ ") + sod.Text + $" (rule: certifier is a Governance Officer who completed none of the gate's tasks). Tasks completed by: {(doneBy.Count == 0 ? "nobody" : string.Join(", ", doneBy))}."
                        : "not required (Standard gate; no separation-of-duties rule applies)";
                    return new Cell[] { g.Name, g.Class, OwnerOrDash(g.OwnerName), g.Status, Cell.R(t => t.Span(who).FontSize(PdfStyle.Small)), Cell.R(t => t.Span(rule).FontSize(PdfStyle.Small).FontColor(g.Class == "Compliance" && !sod.Passed ? PdfStyle.Bad : PdfStyle.Ink)) };
                }));
        });

        Section(col, "3 · Go/No-Go decisions (all)", c =>
        {
            if (M.Decisions.Count == 0) { Empty(c, "No Go/No-Go decision has been recorded for this train."); return; }
            c.Column(x =>
            {
                x.Spacing(8);
                foreach (var d in M.Decisions.OrderBy(d => d.DecidedAt))
                    x.Item().Column(y =>
                    {
                        y.Item().Text(t =>
                        {
                            t.Span(PdfFmt.Decision(d.Decision)).SemiBold();
                            t.Span($" · {d.DecidedBy}{(d.DecidedByRole is null ? "" : ", " + PdfFmt.RoleLabel(d.DecidedByRole))} · ");
                            t.Span($"{F.Iso(d.DecidedAt)} {F.Zone(d.DecidedAt)}").FontFamily(PdfStyle.MonoFamilies).FontSize(PdfStyle.MonoSize);
                            if (d.NewTarget is { } nt) t.Span($" · new target {F.Day(nt)}");
                        });
                        if (!string.IsNullOrWhiteSpace(d.Notes)) y.Item().Text($"Notes: {d.Notes}").FontColor(PdfStyle.Quiet);
                        if (d.GateStates.Count > 0) y.Item().Text("Gates at decision: " + string.Join(" · ", d.GateStates.Select(g => $"{g.Name} {g.Status}")) + $" · {d.OpenBlockersThen} open blocker{(d.OpenBlockersThen == 1 ? "" : "s")}").FontColor(PdfStyle.Quiet);
                        if (d.Conditions.Count > 0)
                            y.Item().PaddingTop(3).Element(g => Grid(g, [new("Condition", 3f), new("Owner", 1.1f), new("Expires", 1.4f), new("Closed", 1.7f)],
                                d.Conditions.Select(k => new Cell[] { k.Text, k.OwnerName, Cell.M(F.Iso(k.ExpiresAt)), k.ClosedAt is { } cl ? Cell.M($"{F.Iso(cl)} by {k.ClosedByName}") : Cell.Q(k.ExpiresAt < M.GeneratedAt ? "open, expired" : "open") })));
                    });
            });
        });

        Section(col, "4 · Exceptions (all: freeze overrides, waivers, escalations)", c => Exceptions(c, int.MaxValue));

        Section(col, $"5 · Evidence manifest (all {M.Attachments.Count})", c =>
        {
            if (M.Attachments.Count == 0) { Empty(c, "No evidence files are attached to this train."); return; }
            Grid(c, [new("#", 0.3f), new("File", 2f), new("Attached to", 1.2f), new("Size", 0.7f), new("Uploaded", 1.6f), new("Locked", 0.55f)],
                M.Attachments.Select(a => new Cell[] { Cell.M(a.No.ToString()), a.FileName, $"{a.EntityType}: {a.EntityLabel}", Cell.M(PdfFmt.Bytes(a.SizeBytes)), $"{a.UploaderName} · {F.Iso(a.UploadedAt)}", a.Locked ? "✓ locked" : "open" }));
        }, "Full SHA-256 of every file as recorded when it was attached. The ZIP form of this pack re-checks each file against its hash before writing it and is not produced if one differs.");
        col.Item().Element(c =>
        {
            if (M.Attachments.Count == 0) return;
            Grid(c, [new("#", 0.3f), new("sha256", 5f)], M.Attachments.Select(a => new Cell[] { Cell.M(a.No.ToString()), new Cell { Text = a.Sha256, Mono = true, Size = PdfStyle.Small } }));
        });

        Section(col, "6 · Runbook actuals", RunbookActuals, RunbookNote());
        Section(col, "7 · Gate transition log", c =>
        {
            if (M.Transitions.Count == 0) { Empty(c, "No gate transitions recorded."); return; }
            Grid(c, [new("At", 1.6f), new("Gate", 2f), new("Change", 1.8f), new("By", 1.4f)],
                M.Transitions.Select(t => new Cell[] { Cell.M($"{F.Iso(t.At)} {F.Zone(t.At)}"), t.GateName, $"{t.From} → {t.To}", t.ActorName ?? "system" }));
        });

        var truncated = M.AuditTotal > M.Audit.Count;
        Section(col, $"8 · Audit events ({M.Audit.Count}{(truncated ? $" most recent of {M.AuditTotal}" : "")})", c =>
        {
            if (M.Audit.Count == 0) { Empty(c, "No audit events recorded for this train."); return; }
            Grid(c, [new("At", 1.35f), new("Actor", 1.05f), new("Record", 1.25f), new("Action", 0.85f), new("Detail", 2.6f)],
                M.Audit.Select(a => new Cell[] { new() { Text = F.Iso(a.At), Mono = true, Size = PdfStyle.Small }, new() { Text = a.Actor ?? "system", Size = PdfStyle.Small }, new() { Text = $"{a.Entity} {a.EntityId[..Math.Min(8, a.EntityId.Length)]}", Size = PdfStyle.Small },
                    new() { Text = a.Action, Size = PdfStyle.Small }, new() { Text = a.Detail ?? "", Size = PdfStyle.Tiny, Quiet = true } }));
        }, truncated ? "The extract is capped; the full log is available from the audit viewer (CSV export)." : null);

        Section(col, "9 · Metric snapshot", MetricSnapshot,
            M.Metrics.Count == 0 ? null : $"Stored in MetricSnapshots for {M.JobRef} at {F.Stamp(M.Metrics[0].CapturedAt)}; the values above are read back from those rows. Window {(M.Metrics[0].PeriodStart is { } ps ? F.Day(ps) : "")} to {(M.Metrics[0].PeriodEnd is { } pe ? F.Day(pe) : "")}. M10 and M15 are release-level, not DORA.");
    }

    private string? RunbookNote()
    {
        if (M.Steps.Count == 0) return "No runbook steps are planned for this train.";
        var live = M.LiveRun;
        return live is null ? "The train has not had a live run: actual columns are empty." : $"Live run started {F.Iso(live.StartedAt)} {F.Zone(live.StartedAt)} by {live.StartedBy}; outcome {live.Outcome ?? "in progress"}. Variance = actual minus planned; the plan is never overwritten.";
    }

    private void RunbookActuals(IContainer c)
    {
        if (M.Steps.Count == 0) { Empty(c, "No runbook steps."); return; }
        var live = M.LiveRun;
        Grid(c, [new("Step", 0.65f), new("Section", 0.75f), new("Title", 2f), new("Planned", 1.05f), new("Actual", 1.3f), new("Status", 0.7f), new("Variance", 0.75f)],
            M.Steps.Select(s =>
            {
                var e = live is not null && live.Executions.TryGetValue(s.Id, out var ex) ? ex : null;
                string actual = e?.ActualStart is { } a ? $"{F.Time(a)}–{(e.ActualEnd is { } z ? F.Time(z) : "…")}" : "—";
                string variance = e?.ActualStart is { } a2 ? PdfFmt.Signed((a2 - s.PlannedStart).TotalMinutes) + " start" + (e.ActualEnd is { } z2 ? $" / {PdfFmt.Signed((z2 - a2).TotalMinutes - s.PlannedMinutes)} run" : "") : "—";
                var title = e?.Note is { Length: > 0 } n ? $"{s.Title} — note: {Clip(n, 90)}" : s.Title;
                return new Cell[] { Cell.M(s.Code), s.Section, new() { Text = title, Size = PdfStyle.Small }, Cell.M($"{F.Time(s.PlannedStart)}–{F.Time(s.PlannedEnd)}"), Cell.M(actual), e?.Status ?? "not run", new() { Text = variance, Mono = true, Size = PdfStyle.Small } };
            }));
    }

    private void MetricSnapshot(IContainer c)
    {
        if (M.Metrics.Count == 0) { Empty(c, "No metric snapshot was stored for this export."); return; }
        Grid(c, [new("Metric", 1.8f), new("Row · column", 2.4f), new("Value", 0.8f), new("Unit", 0.8f)],
            M.Metrics.Select(m => new Cell[] { $"{m.Key.Split('.')[0]} · {m.Title}", m.Label, Cell.M(m.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)), Cell.Q(m.Unit) }));
    }
}
