-- Release Management App — analytics queries (SQLite 3.45+; window functions, JSON)
-- Every metric reads from records the app already writes; no pre-aggregation (D16).
-- Parameters bound by the API: :from, :to (ISO dates) and :now (ISO timestamp, the request's as-of time;
-- never SQLite's 'now', so results are reproducible). Every multi-row query has a deterministic ORDER BY.
-- Medians use the row_number pattern.

-- M1  On-time release rate — actual end vs BASELINE planned date (not the current target)
-- name: on_time_rate
SELECT
  count(*)                                                        AS completed,
  sum(date(t.ActualEndAt) <= b.PlannedReleaseDate)                AS on_time,
  round(100.0 * sum(date(t.ActualEndAt) <= b.PlannedReleaseDate) / count(*), 1) AS on_time_pct
FROM ReleaseTrains t
JOIN Baselines b ON b.ReleaseTrainId = t.Id
WHERE t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to;

-- M2  Slip in days per train (positive = late)
-- name: slip_days
SELECT t.Title, b.PlannedReleaseDate, date(t.ActualEndAt) AS actual,
       CAST(julianday(date(t.ActualEndAt)) - julianday(b.PlannedReleaseDate) AS INTEGER) AS slip_days
FROM ReleaseTrains t JOIN Baselines b ON b.ReleaseTrainId = t.Id
WHERE t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
ORDER BY t.ActualEndAt, t.Title;

-- M3  Gate cycle time — first entry to InProgress until first Certified/Waived, per gate name, median and p90 (hours)
-- name: gate_cycle_time
WITH spans AS (
  SELECT g.GateName,
         (julianday(min(CASE WHEN tr.ToStatus IN ('Certified','Waived') THEN tr.OccurredAt END))
        - julianday(min(CASE WHEN tr.ToStatus = 'InProgress' THEN tr.OccurredAt END))) * 24.0 AS hours
  FROM StageGates g
  JOIN GateTransitions tr ON tr.StageGateId = g.Id
  JOIN ReleaseTrains t ON t.Id = g.ReleaseTrainId
  WHERE date(tr.OccurredAt) BETWEEN :from AND :to
  GROUP BY g.Id
  HAVING hours IS NOT NULL
), ranked AS (
  SELECT GateName, hours,
         row_number() OVER (PARTITION BY GateName ORDER BY hours) AS rn,
         count(*)     OVER (PARTITION BY GateName)                AS n
  FROM spans
)
SELECT GateName, n AS samples,
       round(max(CASE WHEN rn = (n + 1) / 2 THEN hours END), 1)              AS median_h,
       round(max(CASE WHEN rn = max(1, CAST(0.9 * n + 0.999 AS INTEGER)) THEN hours END), 1) AS p90_h
FROM ranked GROUP BY GateName ORDER BY median_h DESC, GateName;

-- M4  Gate first-pass rate — certified without ever entering Failed or being waived
-- name: gate_first_pass
SELECT g.GateName, count(*) AS gates,
       round(100.0 * sum(NOT EXISTS (SELECT 1 FROM GateTransitions x WHERE x.StageGateId = g.Id AND x.ToStatus IN ('Failed','Waived'))) / count(*), 1) AS first_pass_pct,
       sum(g.Status = 'Waived') AS waived
FROM StageGates g JOIN ReleaseTrains t ON t.Id = g.ReleaseTrainId
WHERE t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
GROUP BY g.GateName ORDER BY g.GateName;

-- M5  Late certification — gates certified after their DueOn date
-- name: gate_late
SELECT g.GateName, count(*) AS gates, sum(date(g.CertifiedAt) > g.DueOn) AS late,
       round(avg(max(0, julianday(date(g.CertifiedAt)) - julianday(g.DueOn))), 1) AS avg_days_late
FROM StageGates g JOIN ReleaseTrains t ON t.Id = g.ReleaseTrainId
WHERE g.CertifiedAt IS NOT NULL AND date(g.CertifiedAt) BETWEEN :from AND :to
GROUP BY g.GateName ORDER BY g.GateName;

-- M6  Scope churn — products added or removed after the baseline, per train
-- name: scope_churn
WITH base AS (
  SELECT b.ReleaseTrainId, json_extract(p.value, '$.name') AS name
  FROM Baselines b, json_each(json_extract(b.SnapshotJson, '$.products')) p
), now_ AS (
  SELECT ReleaseTrainId, ProductName AS name FROM BundledProducts
)
SELECT t.Title,
       (SELECT count(*) FROM base WHERE base.ReleaseTrainId = t.Id) AS at_freeze,
       (SELECT count(*) FROM now_ n WHERE n.ReleaseTrainId = t.Id AND n.name NOT IN (SELECT name FROM base WHERE base.ReleaseTrainId = t.Id)) AS added,
       (SELECT count(*) FROM base WHERE base.ReleaseTrainId = t.Id AND base.name NOT IN (SELECT name FROM now_ n WHERE n.ReleaseTrainId = t.Id)) AS removed
FROM ReleaseTrains t WHERE EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = t.Id)
ORDER BY t.Title;

-- M7  Runbook variance — per step: start lateness and duration overrun vs plan (minutes), live runs only
-- name: runbook_variance
SELECT t.Title, s.StepCode, s.Section,
       round((julianday(e.ActualStartAt) - julianday(s.PlannedStartAt)) * 1440) AS start_late_min,
       round((julianday(e.ActualEndAt) - julianday(e.ActualStartAt)) * 1440) - s.PlannedDurationMin AS overrun_min
FROM StepExecutions e
JOIN RunbookRuns r ON r.Id = e.RunId AND r.Mode = 'Live'
JOIN RunbookSteps s ON s.Id = e.StepId
JOIN ReleaseTrains t ON t.Id = s.ReleaseTrainId
WHERE e.Status = 'Done' AND date(e.ActualStartAt) BETWEEN :from AND :to
ORDER BY t.Title, s.PlannedStartAt, s.StepCode;

-- M8  Runbook variance summary per train — planned window vs actual, and worst step
-- name: runbook_summary
WITH v AS (
  SELECT s.ReleaseTrainId, s.StepCode,
         (julianday(e.ActualEndAt) - julianday(e.ActualStartAt)) * 1440 - s.PlannedDurationMin AS overrun
  FROM StepExecutions e JOIN RunbookRuns r ON r.Id = e.RunId AND r.Mode = 'Live'
  JOIN RunbookSteps s ON s.Id = e.StepId WHERE e.Status = 'Done'
)
SELECT t.Title, count(*) AS steps, round(sum(overrun)) AS total_overrun_min,
       (SELECT StepCode FROM v v2 WHERE v2.ReleaseTrainId = t.Id ORDER BY overrun DESC, StepCode LIMIT 1) AS worst_step,
       round(max(overrun)) AS worst_overrun_min
FROM v JOIN ReleaseTrains t ON t.Id = v.ReleaseTrainId GROUP BY t.Id ORDER BY t.Title;

-- M9  Blocker aging — open blockers by age bucket and severity (as of now)
-- name: blocker_aging
SELECT Severity,
       sum(age < 1)               AS lt_1d,
       sum(age >= 1 AND age < 3)  AS d1_3,
       sum(age >= 3 AND age < 7)  AS d3_7,
       sum(age >= 7)              AS ge_7d,
       round(max(age), 1)         AS oldest_days
FROM (SELECT Severity, julianday(:now) - julianday(RaisedAt) AS age FROM Blockers WHERE ResolvedAt IS NULL)
GROUP BY Severity
ORDER BY CASE Severity WHEN 'Critical' THEN 1 WHEN 'High' THEN 2 WHEN 'Medium' THEN 3 ELSE 4 END;

-- M10 Rollback rate and close-code mix
-- name: outcomes
SELECT count(*) AS completed,
       sum(CloseCode = 'Successful')            AS successful,
       sum(CloseCode = 'SuccessfulWithIssues')  AS with_issues,
       sum(CloseCode = 'Unsuccessful')          AS unsuccessful,
       round(100.0 * (SELECT count(DISTINCT r.ReleaseTrainId) FROM RunbookRuns r WHERE r.Mode = 'Live' AND r.Outcome = 'RolledBack'
                      AND r.ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to))
             / count(*), 1)                     AS rollback_pct,
       round(100.0 * sum(CloseCode <> 'Successful') / count(*), 1) AS change_fail_pct
FROM ReleaseTrains WHERE CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to;

-- M11 Freeze exceptions — overrides granted, and approval lead time (hours before expiry)
-- name: freeze_exceptions
SELECT f.Name, count(o.Id) AS overrides,
       round(avg((julianday(o.ExpiresAt) - julianday(o.ApprovedAt)) * 24), 1) AS avg_ttl_h
FROM FreezeWindows f LEFT JOIN FreezeOverrides o ON o.FreezeWindowId = f.Id
WHERE date(f.StartsAt) BETWEEN :from AND :to GROUP BY f.Id ORDER BY f.StartsAt, f.Name;

-- M12 Communication timeliness — scheduled comms sent by their due time
-- name: comms_timeliness
SELECT count(*) AS scheduled, sum(SentAt IS NOT NULL AND SentAt <= DueAt) AS on_time,
       sum(SentAt IS NULL AND DueAt < :now) AS missed,
       round(100.0 * sum(SentAt IS NOT NULL AND SentAt <= DueAt) / count(*), 1) AS on_time_pct
FROM CommSchedule WHERE date(DueAt) BETWEEN :from AND :to;

-- M13 Waiver rate — share of Compliance gates passed by waiver (a governance smell when rising)
-- name: waiver_rate
SELECT strftime('%Y-%m', g.CertifiedAt) AS month, count(*) AS compliance_gates,
       sum(g.Status = 'Waived') AS waived, round(100.0 * sum(g.Status = 'Waived') / count(*), 1) AS waived_pct
FROM StageGates g WHERE g.GateClass = 'Compliance' AND g.CertifiedAt IS NOT NULL
  AND date(g.CertifiedAt) BETWEEN :from AND :to GROUP BY month ORDER BY month;

-- M14 Sync health — link states and alert burden per source system
-- name: sync_health
SELECT l.SourceSystem, count(*) AS links,
       sum(l.SyncState = 'InSync') AS in_sync, sum(l.SyncState = 'Mismatch') AS mismatch,
       sum(l.SyncState IN ('NotFound','AuthFailed')) AS broken,
       round(max((julianday(:now) - julianday(l.LastSyncedAt)) * 1440)) AS stalest_min,
       (SELECT count(*) FROM SyncAlerts a WHERE a.SourceSystem = l.SourceSystem AND a.IsResolved = 0) AS open_alerts
FROM ExternalLinks l GROUP BY l.SourceSystem ORDER BY l.SourceSystem;

-- M15 Throughput — trains completed per month and median lead time (created -> complete, days)
-- name: throughput
WITH c AS (
  SELECT strftime('%Y-%m', ActualEndAt) AS month, julianday(ActualEndAt) - julianday(CreatedAt) AS lead_days
  FROM ReleaseTrains WHERE CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to
), r AS (
  SELECT month, lead_days, row_number() OVER (PARTITION BY month ORDER BY lead_days) rn, count(*) OVER (PARTITION BY month) n FROM c
)
SELECT month, max(n) AS completed, round(max(CASE WHEN rn = (n + 1) / 2 THEN lead_days END), 1) AS median_lead_days
FROM r GROUP BY month ORDER BY month;
