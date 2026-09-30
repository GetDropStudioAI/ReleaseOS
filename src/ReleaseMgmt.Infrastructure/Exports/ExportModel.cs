using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Exports;

// The read model every export document renders from (REOS-50). Loaded once, in one read transaction, then rendered with no further database access,
// so a document is a consistent snapshot of the train at GeneratedAt. All times are UTC; the documents format them in Display:TimeZone through DisplayClock.

public sealed record TrainInfo(string Id, string Title, string Status, string RiskTier, DateOnly TargetDate, string? ChangeTicket, string? CloseCode, string? CloseNotes,
    DateTime? ActualStartAt, DateTime? ActualEndAt, int Version, DateTime CreatedAt, DateTime? RollbackRehearsedAt, string? RollbackRehearsedBy);

public sealed record ChangeInfo(string? Justification, string? ImplementationPlan, string? RiskImpactAnalysis, string? BackoutPlan, string? TestPlan, string? CommunicationPlan, DateOnly? CabDate);
public sealed record WindowInfo(DateTime StartsAt, DateTime EndsAt);
public sealed record ProductRow(string Name, string Version, string ProjectCode);

public sealed record TaskRow(string Description, string? OwnerName, bool Done, DateTime? CompletedAt, string? CompletedByUserId, string? CompletedByName);
public sealed record GateRow(string Id, int Order, string Name, string Class, string Status, DateOnly DueOn, string? OwnerName, string? OwnerUserId, string? CertifiedByUserId, string? CertifiedByName,
    string? CertifiedByRole, DateTime? CertifiedAt, IReadOnlyList<TaskRow> Tasks, double? CycleHours);
public sealed record TransitionRow(DateTime At, string GateName, string From, string To, string? ActorName);
public sealed record WaiverRow(string GateName, string Reason, string RequestedBy, string? ApprovedBy, DateTime RequestedAt, DateTime? ApprovedAt);
public sealed record OverrideRow(string Ref, string WindowName, string Reason, string RequestedBy, string ApprovedBy, DateTime ApprovedAt, DateTime ExpiresAt);
public sealed record EscalationRow(string Detail, DateTime At);
public sealed record ConditionRow(string Text, string OwnerName, DateTime ExpiresAt, DateTime? ClosedAt, string? ClosedByName);
public sealed record DecisionRow(string Decision, string DecidedBy, string? DecidedByRole, DateTime DecidedAt, string? Notes, DateOnly? NewTarget, int GatesCertified, int GatesTotal,
    IReadOnlyList<(string Name, string Status)> GateStates, int OpenBlockersThen, string? WorstOpenSeverity, IReadOnlyList<ConditionRow> Conditions);
public sealed record BlockerRow(string Title, string Severity, string? ProductName, string? GateName, string? OwnerName, DateTime RaisedAt, DateTime? ResolvedAt);
public sealed record StepRow(string Id, string Code, string Section, string Title, string? Instructions, string? OwnerName, string? ProductName, DateTime PlannedStart, int PlannedMinutes, IReadOnlyList<string> DependsOn)
{
    public DateTime PlannedEnd => PlannedStart.AddMinutes(PlannedMinutes);
}
public sealed record ExecRow(string StepId, string Status, DateTime? ActualStart, DateTime? ActualEnd, string? ActorName, string? Note);
public sealed record RunRow(string Id, string Mode, DateTime StartedAt, DateTime? EndedAt, string? Outcome, string StartedBy, IReadOnlyDictionary<string, ExecRow> Executions);
public sealed record AttachmentRow(int No, string Id, string FileName, string ContentType, long SizeBytes, string Sha256, bool Locked, string EntityType, string EntityLabel, string UploaderName, DateTime UploadedAt);
public sealed record AuditLine(DateTime At, string? Actor, string Entity, string EntityId, string Action, string? Detail);
public sealed record CommRow(string Type, string Audience, string Channel, string Outcome, bool Rehearsal, string By, DateTime At);
public sealed record BaselineInfo(DateTime CapturedAt, DateOnly PlannedReleaseDate, IReadOnlyList<string> ProductNames);
public sealed record PirActionRow(string Text, string OwnerName, DateOnly DueOn, DateTime? DoneAt);
public sealed record PirInfo(string Status, string RequiredReason, DateTime? HeldAt, string? Summary, IReadOnlyList<PirActionRow> Actions);
public sealed record KnownIssueRow(string Title, string Severity, string Status, DateTime RaisedAt, DateTime? ResolvedAt, string? ExternalKey);
public sealed record MetricRow(string Key, string Title, string Label, double Value, string Unit, DateOnly? PeriodStart, DateOnly? PeriodEnd, DateTime CapturedAt);

public sealed record ExportModel(
    string JobId, string JobRef, string Kind, DateTime GeneratedAt, string GeneratedBy, DisplayClock Clock,
    TrainInfo Train, ChangeInfo Change, IReadOnlyList<string> Cis, WindowInfo? Window, IReadOnlyList<ProductRow> Products,
    IReadOnlyList<GateRow> Gates, IReadOnlyList<TransitionRow> Transitions, IReadOnlyList<WaiverRow> Waivers, IReadOnlyList<OverrideRow> Overrides, IReadOnlyList<EscalationRow> Escalations,
    IReadOnlyList<DecisionRow> Decisions, IReadOnlyList<BlockerRow> Blockers, IReadOnlyList<StepRow> Steps, IReadOnlyList<RunRow> Runs, IReadOnlyList<AttachmentRow> Attachments,
    IReadOnlyList<AuditLine> Audit, long AuditTotal, IReadOnlyList<CommRow> Comms, BaselineInfo? Baseline, PirInfo? Pir, IReadOnlyList<KnownIssueRow> KnownIssues)
{
    /// <summary>Rows written to MetricSnapshots for this job (evidence pack only); the document prints these, not a fresh query.</summary>
    public IReadOnlyList<MetricRow> Metrics { get; init; } = [];

    /// <summary>The latest Live run: its executions are the "actuals" of the run sheet and the pack. Null before the train has gone live.</summary>
    public RunRow? LiveRun => Runs.Where(r => r.Mode == "Live").OrderByDescending(r => r.StartedAt).FirstOrDefault();
}
