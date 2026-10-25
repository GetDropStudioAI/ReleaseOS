namespace ReleaseMgmt.Infrastructure.Analytics;

// One typed row per metric, in the column order of db/analytics.sql. Counts are long, measures double (CLAUDE.md rule 5: no decimal).
// Aggregates over an empty set are NULL in SQL, so they are nullable here and stay null in JSON (never coerced to 0).
public sealed record M1Row(long Completed, long? OnTime, double? OnTimePct);
public sealed record M2Row(string Title, string PlannedReleaseDate, string Actual, long SlipDays);
public sealed record M3Row(string GateName, long Samples, double? MedianH, double? P90H);
public sealed record M4Row(string GateName, long Gates, double? FirstPassPct, long? Waived);
public sealed record M5Row(string GateName, long Gates, long? Late, double? AvgDaysLate);
public sealed record M6Row(string Title, long AtFreeze, long Added, long Removed);
public sealed record M7Row(string Title, string StepCode, string Section, double? StartLateMin, double? OverrunMin);
public sealed record M8Row(string Title, long Steps, double? TotalOverrunMin, string? WorstStep, double? WorstOverrunMin);
public sealed record M9Row(string Severity, long? Lt1d, long? D1_3, long? D3_7, long? Ge7d, double? OldestDays);
public sealed record M10Row(long Completed, long? Successful, long? WithIssues, long? Unsuccessful, double? RollbackPct, double? ChangeFailPct);
public sealed record M11Row(string Name, long Overrides, double? AvgTtlH);
public sealed record M12Row(long Scheduled, long? OnTime, long? Missed, double? OnTimePct);
public sealed record M13Row(string? Month, long ComplianceGates, long? Waived, double? WaivedPct);
public sealed record M14Row(string SourceSystem, long Links, long? InSync, long? Mismatch, long? Broken, double? StalestMin, long OpenAlerts);
public sealed record M15Row(string? Month, long Completed, double? MedianLeadDays);

// REOS-121 (RT6): the executive metrics. Every row starts with the six group columns, '' where the request does not group by one (a quarter such as 2026-Q3, or FY2027-Q1 for a fiscal year).
public sealed record M16Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, long Restores, double? MedianMin, double? MaxMin);
public sealed record M17Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, string Track, long Completed, long Measured, long? OnTime, double? OnTimePct,
    double? MedianSlipDays, long? MaxSlipDays, long? ChangeFailed, double? ChangeFailPct);
public sealed record M18Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, string Exception, long Events, long Releases);
public sealed record M19Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, string Step, long Samples, double? MedianH, double? P90H);
public sealed record M20Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, string Reason, long Moves, long Releases, long? DaysLater, long? DaysEarlier);
public sealed record M21Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, string RiskTier, long OpenReleases, long? NearestDays, long? PastTarget, long? WithBlockers,
    long? OpenBlockers, long? NoRehearsal);
public sealed record M22Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, long Completed, double? MedianLeadDays, double? P90LeadDays);
public sealed record M23Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, long Deployments, long? Successful, long? Failed, long? RolledBack, double? SuccessPct,
    long? Breaches, long? AvailabilityBreaches);
public sealed record M24Row(string Workspace, string Team, string ReleaseType, string Version, string Segment, string Quarter, long Reviews, long? WithDueDate, long? Held, long? HeldByDue, long? Overdue,
    long? Actions, long? ActionsDone, long? ActionsOverdue);
