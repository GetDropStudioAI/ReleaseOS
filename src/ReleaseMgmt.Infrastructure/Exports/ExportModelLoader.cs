using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// Loads everything a train export prints, inside ONE deferred read transaction (WAL: the snapshot is fixed by the first read, writers are never blocked),
/// so a document cannot mix the state before and after a concurrent certify. The audit extract comes from <see cref="AuditQueryService"/>, which is append-only.
/// </summary>
public sealed class ExportModelLoader(IDbContextFactory<ReleaseDbContext> dbf, AuditQueryService audit, DisplayClock clock, ExportOptions options)
{
    private static readonly string[] SeverityOrder = ["Critical", "High", "Medium", "Low"];

    /// <summary>Null when the train no longer exists.</summary>
    public async Task<ExportModel?> LoadAsync(string trainId, string jobId, string kind, string requestedByUserId, DateTime now, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        await using var tx = await db.Database.UseTransactionAsync(conn.BeginTransaction(IsolationLevel.ReadCommitted, deferred: true), ct)
                             ?? throw new InvalidOperationException("The read transaction could not be attached");

        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        var users = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var teams = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        string? Name(string? id) => id is not null && users.TryGetValue(id, out var u) ? u.DisplayName : id;
        string? Role(string? id) => id is not null && users.TryGetValue(id, out var u) ? u.Role : null;
        string? Owner(string? userId, string? teamId) => userId is not null ? Name(userId) : teamId is not null && teams.TryGetValue(teamId, out var tn) ? tn : null;

        var cr = await db.Set<ChangeRecords>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
        var cis = await db.Set<AffectedCIs>().AsNoTracking().Where(x => x.ReleaseTrainId == trainId).OrderBy(x => x.CiName).Select(x => x.CiName).ToListAsync(ct);
        var win = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
        var productRows = await db.Set<BundledProducts>().AsNoTracking().Where(x => x.ReleaseTrainId == trainId).OrderBy(x => x.ProductName).ToListAsync(ct);
        var productNames = productRows.ToDictionary(p => p.Id, p => p.ProductName);

        var gateRows = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
        var gateIds = gateRows.Select(g => g.Id).ToList();
        var tasks = await db.Set<ChecklistTasks>().AsNoTracking().Where(x => gateIds.Contains(x.StageGateId)).OrderBy(x => x.SequenceOrder).ToListAsync(ct);
        var transitions = await db.Set<GateTransitions>().AsNoTracking().Where(x => gateIds.Contains(x.StageGateId)).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(ct);
        var gateName = gateRows.ToDictionary(g => g.Id, g => g.GateName);

        static double? Cycle(IEnumerable<GateTransitions> tr)
        {
            var list = tr.ToList();
            var start = list.Where(x => x.ToStatus == "InProgress").Select(x => (DateTime?)x.OccurredAt).Min();
            var end = list.Where(x => x.ToStatus is "Certified" or "Waived").Select(x => (DateTime?)x.OccurredAt).Min();
            return start is DateTime s && end is DateTime e && e >= s ? Math.Round((e - s).TotalHours, 1) : null;
        }
        var gates = gateRows.Select(g => new GateRow(g.Id, g.SequenceOrder, g.GateName, g.GateClass, g.Status, g.DueOn, Owner(g.OwnerUserId, g.OwnerTeamId), g.OwnerUserId,
            g.CertifiedByUserId, Name(g.CertifiedByUserId), Role(g.CertifiedByUserId), g.CertifiedAt,
            tasks.Where(x => x.StageGateId == g.Id).Select(x => new TaskRow(x.TaskDescription, Owner(x.OwnerUserId, x.OwnerTeamId), x.IsCompleted, x.CompletedAt, x.CompletedByUserId, Name(x.CompletedByUserId))).ToList(),
            Cycle(transitions.Where(x => x.StageGateId == g.Id)))).ToList();
        var transRows = transitions.Select(x => new TransitionRow(x.OccurredAt, gateName[x.StageGateId], x.FromStatus, x.ToStatus, Name(x.ActorUserId))).ToList();

        var waivers = (await db.Set<GateWaivers>().AsNoTracking().Where(w => gateIds.Contains(w.StageGateId)).OrderBy(w => w.RequestedAt).ToListAsync(ct))
            .Select(w => new WaiverRow(gateName[w.StageGateId], w.Reason, Name(w.RequestedByUserId) ?? "?", Name(w.ApprovedByUserId), w.RequestedAt, w.ApprovedAt)).ToList();

        var ovs = await db.Set<FreezeOverrides>().AsNoTracking().Where(o => o.ReleaseTrainId == trainId).OrderBy(o => o.ApprovedAt).ThenBy(o => o.Id).ToListAsync(ct);
        var windowNames = await db.Set<FreezeWindows>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var overrides = ovs.Select((o, i) => new OverrideRow($"FO-{i + 1}", windowNames.GetValueOrDefault(o.FreezeWindowId, "Freeze window"), o.Reason,
            Name(o.RequestedByUserId) ?? "?", Name(o.ApprovedByUserId) ?? "?", o.ApprovedAt, o.ExpiresAt)).ToList();

        var blockerEntities = await db.Set<Blockers>().AsNoTracking().Where(b => b.ReleaseTrainId == trainId).OrderBy(b => b.RaisedAt).ThenBy(b => b.Id).ToListAsync(ct);
        var blockers = blockerEntities.Select(b => new BlockerRow(b.Title, b.Severity, b.BundledProductId is not null ? productNames.GetValueOrDefault(b.BundledProductId) : null,
            b.StageGateId is not null ? gateName.GetValueOrDefault(b.StageGateId) : null, Name(b.OwnerUserId), b.RaisedAt, b.ResolvedAt)).ToList();

        var decisionEntities = await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => d.ReleaseTrainId == trainId).OrderBy(d => d.DecidedAt).ThenBy(d => d.Id).ToListAsync(ct);
        var decisionIds = decisionEntities.Select(d => d.Id).ToList();
        var conditions = await db.Set<GoNoGoConditions>().AsNoTracking().Where(c => decisionIds.Contains(c.DecisionId)).OrderBy(c => c.ExpiresAt).ToListAsync(ct);
        var decisions = decisionEntities.Select(d =>
        {
            var states = ParseGateStates(d.GateSnapshotJson);
            var open = blockerEntities.Where(b => b.RaisedAt <= d.DecidedAt && (b.ResolvedAt is null || b.ResolvedAt > d.DecidedAt)).ToList();
            var worst = open.Select(b => b.Severity).OrderBy(s => Array.IndexOf(SeverityOrder, s)).FirstOrDefault();
            return new DecisionRow(d.Decision, Name(d.DecidedByUserId) ?? "?", Role(d.DecidedByUserId), d.DecidedAt, d.Notes, d.NewTargetReleaseDate,
                states.Count(s => s.Status is "Certified" or "Waived"), states.Count, states, open.Count, worst,
                conditions.Where(c => c.DecisionId == d.Id).Select(c => new ConditionRow(c.Text, Name(c.OwnerUserId) ?? "?", c.ExpiresAt, c.ClosedAt, Name(c.ClosedByUserId))).ToList());
        }).ToList();

        var stepEntities = await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == trainId).OrderBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode).ToListAsync(ct);
        var stepCode = stepEntities.ToDictionary(s => s.Id, s => s.StepCode);
        var stepIds = stepCode.Keys.ToList();
        var deps = await db.Set<StepDependencies>().AsNoTracking().Where(d => stepIds.Contains(d.StepId)).ToListAsync(ct);
        var steps = stepEntities.Select(s => new StepRow(s.Id, s.StepCode, s.Section, s.Title, s.Instructions, Owner(s.OwnerUserId, s.OwnerTeamId),
            s.BundledProductId is not null ? productNames.GetValueOrDefault(s.BundledProductId) : null, s.PlannedStartAt, s.PlannedDurationMin,
            deps.Where(d => d.StepId == s.Id).Select(d => stepCode.GetValueOrDefault(d.DependsOnStepId, "?")).OrderBy(x => x, StringComparer.Ordinal).ToList())).ToList();

        var runEntities = await db.Set<RunbookRuns>().AsNoTracking().Where(r => r.ReleaseTrainId == trainId).OrderBy(r => r.StartedAt).ToListAsync(ct);
        var runIds = runEntities.Select(r => r.Id).ToList();
        var execs = await db.Set<StepExecutions>().AsNoTracking().Where(e => runIds.Contains(e.RunId)).ToListAsync(ct);
        var runs = runEntities.Select(r => new RunRow(r.Id, r.Mode, r.StartedAt, r.EndedAt, r.Outcome, Name(r.StartedByUserId) ?? "?",
            execs.Where(e => e.RunId == r.Id).ToDictionary(e => e.StepId, e => new ExecRow(e.StepId, e.Status, e.ActualStartAt, e.ActualEndAt, Name(e.ActorUserId), e.Note)))).ToList();

        var execIds = execs.Select(e => e.Id).ToList();
        var lateNotices = await db.Set<Notifications>().AsNoTracking()
            .Where(n => n.Kind == "StepLate" && n.EscalationLevel == 2 && ((n.EntityType == "StepExecution" && execIds.Contains(n.EntityId)) || (n.EntityType == "RunbookRun" && runIds.Contains(n.EntityId))))
            .OrderBy(n => n.CreatedAt).ToListAsync(ct);
        var escalations = lateNotices.GroupBy(n => (n.EntityType, n.EntityId, n.Message)).Select(g => new EscalationRow(g.Key.Message, g.Min(x => x.CreatedAt))).OrderBy(x => x.At).ToList();

        var attachmentEntities = await db.Set<Attachments>().AsNoTracking().Where(a => a.ReleaseTrainId == trainId).OrderBy(a => a.UploadedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var taskGate = tasks.ToDictionary(x => x.Id, x => gateName[x.StageGateId]);
        string Label(Attachments a) => a.EntityType switch
        {
            "Gate" => gateName.GetValueOrDefault(a.EntityId, "Gate"),
            "Task" => taskGate.GetValueOrDefault(a.EntityId, "Task"),
            "Train" => "Train",
            _ => a.EntityType,
        };
        var attachments = attachmentEntities.Select((a, i) => new AttachmentRow(i + 1, a.Id, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.IsLocked, a.EntityType, Label(a), Name(a.UploadedByUserId) ?? "?", a.UploadedAt)).ToList();

        var comms = await (from d in db.Set<CommDispatches>().AsNoTracking()
                           join ct2 in db.Set<CommTemplates>().AsNoTracking() on d.CommTemplateId equals ct2.Id
                           where ct2.ReleaseTrainId == trainId
                           orderby d.DispatchedAt
                           select new { ct2.TemplateType, ct2.Audience, d.Channel, d.Outcome, d.IsRehearsal, d.DispatchedByUserId, d.DispatchedAt }).ToListAsync(ct);

        var b = await db.Set<Baselines>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
        var pir = await db.Set<PostImplementationReviews>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
        PirInfo? pirInfo = null;
        if (pir is not null)
        {
            var actions = await db.Set<PirActions>().AsNoTracking().Where(a => a.PirId == pir.Id).OrderBy(a => a.DueOn).ToListAsync(ct);
            pirInfo = new PirInfo(pir.Status, pir.RequiredReason, pir.HeldAt, pir.Summary, actions.Select(a => new PirActionRow(a.Text, Name(a.OwnerUserId) ?? "?", a.DueOn, a.DoneAt)).ToList());
        }
        var issues = (await db.Set<KnownIssues>().AsNoTracking().Where(k => k.ReleaseTrainId == trainId).OrderBy(k => k.RaisedAt).ToListAsync(ct))
            .Select(k => new KnownIssueRow(k.Title, k.Severity, k.Status, k.RaisedAt, k.ResolvedAt, k.ExternalKey)).ToList();
        await tx.CommitAsync(ct);   // read-only: ends the snapshot

        // Audit extract: the most recent options.AuditRowLimit events for the train, printed oldest first. The count says how many exist.
        var filter = new AuditFilter(Train: trainId, ToInclusive: now);
        var total = await audit.CountAsync(filter, ct);
        var lines = new List<AuditLine>();
        await foreach (var batch in audit.StreamAsync(filter, options.AuditRowLimit, ct: ct))
            lines.AddRange(batch.Select(r => new AuditLine(r.OccurredAt, r.ActorName ?? (r.ActorUserId is null ? "system" : r.ActorUserId), r.EntityType, r.EntityId, r.Action, Short(r.AfterJson ?? r.BeforeJson))));
        lines.Reverse();

        var generatedBy = users.TryGetValue(requestedByUserId, out var gb) ? gb.DisplayName : requestedByUserId;
        return new ExportModel(jobId, ExportSupport.JobRef(jobId), kind, now, generatedBy, clock,
            new TrainInfo(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate, t.ChangeTicketNumber, t.CloseCode, t.CloseNotes, t.ActualStartAt, t.ActualEndAt, t.Version, t.CreatedAt,
                t.RollbackRehearsedAt, Name(t.RollbackRehearsedByUserId)),
            new ChangeInfo(cr?.Justification, cr?.ImplementationPlan, cr?.RiskImpactAnalysis, cr?.BackoutPlan, cr?.TestPlan, cr?.CommunicationPlan, cr?.CabDate),
            cis, win is null ? null : new WindowInfo(win.StartsAt, win.EndsAt), productRows.Select(p => new ProductRow(p.ProductName, p.VersionTag, p.ProjectCode)).ToList(),
            gates, transRows, waivers, overrides, escalations, decisions, blockers, steps, runs, attachments, lines, total,
            comms.Select(c => new CommRow(c.TemplateType, c.Audience, c.Channel, c.Outcome, c.IsRehearsal, Name(c.DispatchedByUserId) ?? "?", c.DispatchedAt)).ToList(),
            b is null ? null : new BaselineInfo(b.CapturedAt, b.PlannedReleaseDate, ParseBaselineProducts(b.SnapshotJson)), pirInfo, issues);
    }

    private static string? Short(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var s = string.Join(' ', json.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 110 ? s[..109] + "…" : s;
    }

    private static List<(string Name, string Status)> ParseGateStates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("gates", out var gs) || gs.ValueKind != JsonValueKind.Array) return [];
            return gs.EnumerateArray().Select(g => (g.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?", g.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?")).ToList();
        }
        catch (JsonException) { return []; }   // the column is CHECK json_valid, and a different shape simply prints no gate list
    }

    private static List<string> ParseBaselineProducts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var ps) || ps.ValueKind != JsonValueKind.Array) return [];
            return ps.EnumerateArray().Select(p => p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?").ToList();
        }
        catch (JsonException) { return []; }
    }
}
