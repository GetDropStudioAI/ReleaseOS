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
