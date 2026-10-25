-- Release Management App — analytics queries (SQLite 3.45+; window functions, JSON)
-- Every metric reads from records the app already writes; no pre-aggregation (D16).
-- Parameters bound by the API: :from, :to (ISO dates), :now (ISO timestamp, the request's as-of time;
-- never SQLite's 'now', so results are reproducible), :ws (a JSON array of workspace ids, D45/D49/REOS-121: every metric reads the records of those
-- workspaces only, and the API only ever passes workspaces the signed-in person is a member of) and :type (REOS-116: a release type id, or NULL for all
-- types; M11 reads the workspace's freeze windows, which have no type). Every multi-row query has a deterministic ORDER BY.
-- M16 to M24 (REOS-121, RT6) also take :by (the grouping, a comma-wrapped list such as ',workspace,quarter,'), :fy (the month a fiscal year starts, 1 = calendar
-- quarters), :localday (today's date in the display zone, for "overdue") and :pir_due (a JSON object {pir id: due date}, computed by the app from PirDue).
-- Medians use the row_number pattern.
--
-- Shared text. The API (AnalyticsSql) and the oracle (tests/reference/seed_and_query.py) expand these before running a statement:
--   {{rel}}            the release dimensions: one row per release in the workspaces, with its workspace, team, type, track (CERT / Production / Other, read from the type's
--                      name as RT4 does), version and client segment. Without segment in :by a release is one row; with it, a release is one row per segment it reaches:
--                      Standard (a Production release: every Standard client takes its date, D47), On-Demand (it has at least one On-Demand client date), or
--                      No client segment. A release that reaches both segments is in both rows, so segment rows are not additive.
--   {{quarter:EXPR}}   the quarter label of a date expression: 2026-Q3, or FY2027-Q1 when :fy is not 1 (the fiscal year is named for the year it ends in)
--   {{keys:EXPR}}      the six group columns (workspace, team, release_type, version, segment, quarter): '' when not in :by, else the value; quarter is of EXPR
--   {{keycols}}        the six group column names, for GROUP BY / PARTITION BY / ORDER BY
-- @fragment rel
rel AS (
  SELECT t.Id AS train_id, t.WorkspaceId AS ws_id, w.Name AS ws_name,
         COALESCE(tm.Name, '(no team)') AS team,
         COALESCE(ty.Name, '(no type)') AS rtype,
         CASE WHEN ty.Name = 'CERT' THEN 'CERT' WHEN ty.Name = 'Production' THEN 'Production' ELSE 'Other' END AS track,
         COALESCE(v.Name, '(no version)') AS version,
         s.segment AS segment
  FROM ReleaseTrains t
  JOIN Workspaces w ON w.Id = t.WorkspaceId
  LEFT JOIN Teams tm ON tm.Id = t.OwnerTeamId
  LEFT JOIN ReleaseTypes ty ON ty.Id = t.ReleaseTypeId
  LEFT JOIN ReleaseVersions v ON v.Id = t.ReleaseVersionId
  JOIN (SELECT 'All' AS segment UNION ALL SELECT 'Standard' UNION ALL SELECT 'On-Demand' UNION ALL SELECT 'No client segment') s
    ON CASE s.segment
         WHEN 'All' THEN instr(:by, ',segment,') = 0
         WHEN 'Standard' THEN instr(:by, ',segment,') > 0 AND ty.Name = 'Production'
         WHEN 'On-Demand' THEN instr(:by, ',segment,') > 0 AND EXISTS (SELECT 1 FROM ReleaseClientDates d WHERE d.ReleaseTrainId = t.Id)
         ELSE instr(:by, ',segment,') > 0 AND COALESCE(ty.Name, '') <> 'Production' AND NOT EXISTS (SELECT 1 FROM ReleaseClientDates d WHERE d.ReleaseTrainId = t.Id)
       END
  WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type)
)
-- @end

-- M1  On-time release rate — actual end vs BASELINE planned date (not the current target)
-- name: on_time_rate
SELECT
  count(*)                                                        AS completed,
  sum(date(t.ActualEndAt) <= b.PlannedReleaseDate)                AS on_time,
  round(100.0 * sum(date(t.ActualEndAt) <= b.PlannedReleaseDate) / count(*), 1) AS on_time_pct
FROM ReleaseTrains t
JOIN Baselines b ON b.ReleaseTrainId = t.Id
WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to;

-- M2  Slip in days per train (positive = late)
-- name: slip_days
SELECT t.Title, b.PlannedReleaseDate, date(t.ActualEndAt) AS actual,
       CAST(julianday(date(t.ActualEndAt)) - julianday(b.PlannedReleaseDate) AS INTEGER) AS slip_days
FROM ReleaseTrains t JOIN Baselines b ON b.ReleaseTrainId = t.Id
WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
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
  WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND date(tr.OccurredAt) BETWEEN :from AND :to
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
WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
GROUP BY g.GateName ORDER BY g.GateName;

-- M5  Late certification — gates certified after their DueOn date
-- name: gate_late
SELECT g.GateName, count(*) AS gates, sum(date(g.CertifiedAt) > g.DueOn) AS late,
       round(avg(max(0, julianday(date(g.CertifiedAt)) - julianday(g.DueOn))), 1) AS avg_days_late
FROM StageGates g JOIN ReleaseTrains t ON t.Id = g.ReleaseTrainId
WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND g.CertifiedAt IS NOT NULL AND date(g.CertifiedAt) BETWEEN :from AND :to
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
FROM ReleaseTrains t WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = t.Id)
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
WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) AND e.Status = 'Done' AND date(e.ActualStartAt) BETWEEN :from AND :to
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
FROM v JOIN ReleaseTrains t ON t.Id = v.ReleaseTrainId WHERE t.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR t.ReleaseTypeId = :type) GROUP BY t.Id ORDER BY t.Title;

-- M9  Blocker aging — open blockers by age bucket and severity (as of now)
-- name: blocker_aging
SELECT Severity,
       sum(age < 1)               AS lt_1d,
       sum(age >= 1 AND age < 3)  AS d1_3,
       sum(age >= 3 AND age < 7)  AS d3_7,
       sum(age >= 7)              AS ge_7d,
       round(max(age), 1)         AS oldest_days
FROM (SELECT Severity, julianday(:now) - julianday(RaisedAt) AS age FROM Blockers
      WHERE ResolvedAt IS NULL AND ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type)))
GROUP BY Severity
ORDER BY CASE Severity WHEN 'Critical' THEN 1 WHEN 'High' THEN 2 WHEN 'Medium' THEN 3 ELSE 4 END;

-- M10 Rollback rate and close-code mix
-- name: outcomes
SELECT count(*) AS completed,
       sum(CloseCode = 'Successful')            AS successful,
       sum(CloseCode = 'SuccessfulWithIssues')  AS with_issues,
       sum(CloseCode = 'Unsuccessful')          AS unsuccessful,
       round(100.0 * (SELECT count(DISTINCT r.ReleaseTrainId) FROM RunbookRuns r WHERE r.Mode = 'Live' AND r.Outcome = 'RolledBack'
                      AND r.ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type) AND CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to))
             / count(*), 1)                     AS rollback_pct,
       round(100.0 * sum(CloseCode <> 'Successful') / count(*), 1) AS change_fail_pct
FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type) AND CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to;

-- M11 Freeze exceptions — overrides granted, and approval lead time (hours before expiry)
-- name: freeze_exceptions
SELECT f.Name, count(o.Id) AS overrides,
       round(avg((julianday(o.ExpiresAt) - julianday(o.ApprovedAt)) * 24), 1) AS avg_ttl_h
FROM FreezeWindows f LEFT JOIN FreezeOverrides o ON o.FreezeWindowId = f.Id
WHERE f.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND date(f.StartsAt) BETWEEN :from AND :to GROUP BY f.Id ORDER BY f.StartsAt, f.Name;

-- M12 Communication timeliness — scheduled comms sent by their due time
-- name: comms_timeliness
SELECT count(*) AS scheduled, sum(SentAt IS NOT NULL AND SentAt <= DueAt) AS on_time,
       sum(SentAt IS NULL AND DueAt < :now) AS missed,
       round(100.0 * sum(SentAt IS NOT NULL AND SentAt <= DueAt) / count(*), 1) AS on_time_pct
FROM CommSchedule WHERE ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type)) AND date(DueAt) BETWEEN :from AND :to;

-- M13 Waiver rate — share of Compliance gates passed by waiver (a governance smell when rising)
-- name: waiver_rate
SELECT strftime('%Y-%m', g.CertifiedAt) AS month, count(*) AS compliance_gates,
       sum(g.Status = 'Waived') AS waived, round(100.0 * sum(g.Status = 'Waived') / count(*), 1) AS waived_pct
FROM StageGates g WHERE g.ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type)) AND g.GateClass = 'Compliance' AND g.CertifiedAt IS NOT NULL
  AND date(g.CertifiedAt) BETWEEN :from AND :to GROUP BY month ORDER BY month;

-- M14 Sync health — link states and alert burden per source system
-- name: sync_health
SELECT l.SourceSystem, count(*) AS links,
       sum(l.SyncState = 'InSync') AS in_sync, sum(l.SyncState = 'Mismatch') AS mismatch,
       sum(l.SyncState IN ('NotFound','AuthFailed')) AS broken,
       round(max((julianday(:now) - julianday(l.LastSyncedAt)) * 1440)) AS stalest_min,
       (SELECT count(*) FROM SyncAlerts a WHERE a.WorkspaceId IN (SELECT value FROM json_each(:ws)) AND a.SourceSystem = l.SourceSystem AND a.IsResolved = 0) AS open_alerts
FROM ExternalLinks l WHERE l.ReleaseTrainId IN (SELECT Id FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type)) GROUP BY l.SourceSystem ORDER BY l.SourceSystem;

-- M15 Throughput — trains completed per month and median lead time (created -> complete, days)
-- name: throughput
WITH c AS (
  SELECT strftime('%Y-%m', ActualEndAt) AS month, julianday(ActualEndAt) - julianday(CreatedAt) AS lead_days
  FROM ReleaseTrains WHERE WorkspaceId IN (SELECT value FROM json_each(:ws)) AND (:type IS NULL OR ReleaseTypeId = :type) AND CurrentStatus = 'Complete' AND date(ActualEndAt) BETWEEN :from AND :to
), r AS (
  SELECT month, lead_days, row_number() OVER (PARTITION BY month ORDER BY lead_days) rn, count(*) OVER (PARTITION BY month) n FROM c
)
SELECT month, max(n) AS completed, round(max(CASE WHEN rn = (n + 1) / 2 THEN lead_days END), 1) AS median_lead_days
FROM r GROUP BY month ORDER BY month;


-- M16 Time to restore — first failed step to the end of a rolled-back Live run, per group (minutes)
-- name: time_to_restore
WITH {{rel}}, f AS (
  SELECT r.ReleaseTrainId AS train_id, r.EndedAt AS ended_at, min(e.ActualEndAt) AS failed_at
  FROM RunbookRuns r JOIN StepExecutions e ON e.RunId = r.Id AND e.Status = 'Failed'
  WHERE r.Mode = 'Live' AND r.Outcome = 'RolledBack' AND r.EndedAt IS NOT NULL AND date(r.EndedAt) BETWEEN :from AND :to
  GROUP BY r.Id
), v AS (
  SELECT {{keys:f.ended_at}}, (julianday(f.ended_at) - julianday(f.failed_at)) * 1440.0 AS minutes
  FROM f JOIN rel ON rel.train_id = f.train_id
), ranked AS (
  SELECT *, row_number() OVER (PARTITION BY {{keycols}} ORDER BY minutes) AS rn, count(*) OVER (PARTITION BY {{keycols}}) AS n FROM v
)
SELECT {{keycols}}, n AS restores,
       round(max(CASE WHEN rn = (n + 1) / 2 THEN minutes END), 1) AS median_min,
       round(max(minutes), 1) AS max_min
FROM ranked GROUP BY {{keycols}}
ORDER BY quarter, workspace, team, release_type, version, segment;

-- M17 Predictability by period — M1 and M2 by group, CERT and production measured separately, with the change failure rate (release-level, not DORA)
-- name: predictability
WITH {{rel}}, c AS (
  SELECT {{keys:t.ActualEndAt}}, rel.track AS track, t.CloseCode AS close_code,
         CASE WHEN b.Id IS NULL THEN NULL ELSE CAST(julianday(date(t.ActualEndAt)) - julianday(b.PlannedReleaseDate) AS INTEGER) END AS slip
  FROM ReleaseTrains t JOIN rel ON rel.train_id = t.Id LEFT JOIN Baselines b ON b.ReleaseTrainId = t.Id
  WHERE t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
), ranked AS (
  SELECT *, row_number() OVER (PARTITION BY {{keycols}}, track ORDER BY slip) AS rn, count(slip) OVER (PARTITION BY {{keycols}}, track) AS n FROM c WHERE slip IS NOT NULL
), med AS (
  SELECT {{keycols}}, track, max(CASE WHEN rn = (n + 1) / 2 THEN slip END) AS median_slip FROM ranked GROUP BY {{keycols}}, track
)
SELECT {{keycols}}, track, count(*) AS completed, count(slip) AS measured, sum(slip <= 0) AS on_time,
       round(100.0 * sum(slip <= 0) / count(slip), 1) AS on_time_pct,
       (SELECT m.median_slip FROM med m WHERE m.workspace = c.workspace AND m.team = c.team AND m.release_type = c.release_type AND m.version = c.version
                                          AND m.segment = c.segment AND m.quarter = c.quarter AND m.track = c.track) AS median_slip_days,
       max(slip) AS max_slip_days,
       sum(close_code <> 'Successful') AS change_failed,
       round(100.0 * sum(close_code <> 'Successful') / count(*), 1) AS change_fail_pct
FROM c GROUP BY {{keycols}}, track
ORDER BY quarter, workspace, team, release_type, version, segment, track;

-- M18 Exceptions summary — waivers granted, freeze overrides granted, Go conditions left open past expiry, gates decertified, Compliance gates certified late, PIRs overdue
-- name: exceptions_summary
WITH {{rel}}, ev AS (
  SELECT g.ReleaseTrainId AS train_id, 'Waiver granted' AS exception, 1 AS ord, w.ApprovedAt AS at
  FROM GateWaivers w JOIN StageGates g ON g.Id = w.StageGateId WHERE w.ApprovedAt IS NOT NULL
  UNION ALL
  SELECT o.ReleaseTrainId, 'Freeze override granted', 2, o.ApprovedAt FROM FreezeOverrides o
  UNION ALL
  SELECT d.ReleaseTrainId, 'Go condition open past expiry', 3, c.ExpiresAt
  FROM GoNoGoConditions c JOIN GoNoGoDecisions d ON d.Id = c.DecisionId
  WHERE c.ExpiresAt < :now AND (c.ClosedAt IS NULL OR c.ClosedAt > c.ExpiresAt)
  UNION ALL
  SELECT a.ReleaseTrainId, 'Gate decertified', 4, a.OccurredAt FROM AuditEvents a WHERE a.EntityType = 'StageGate' AND a.Action = 'Decertified' AND a.ReleaseTrainId IS NOT NULL
  UNION ALL
  SELECT g.ReleaseTrainId, 'Compliance gate certified late', 5, g.CertifiedAt FROM StageGates g WHERE g.GateClass = 'Compliance' AND g.CertifiedAt IS NOT NULL AND date(g.CertifiedAt) > g.DueOn
  UNION ALL
  SELECT p.ReleaseTrainId, 'PIR overdue', 6, due.value
  FROM PostImplementationReviews p JOIN json_each(:pir_due) due ON due.key = p.Id
  WHERE p.Status IN ('Required', 'Scheduled') AND due.value < :localday
), x AS (
  SELECT {{keys:ev.at}}, ev.exception, ev.ord, ev.train_id FROM ev JOIN rel ON rel.train_id = ev.train_id WHERE date(ev.at) BETWEEN :from AND :to
)
SELECT {{keycols}}, exception, count(*) AS events, count(DISTINCT train_id) AS releases
FROM x GROUP BY {{keycols}}, exception, ord
ORDER BY quarter, workspace, team, release_type, version, segment, ord;

-- M19 Approval latency — waiver requested to approved; freeze override requested to granted; a gate's last task done to certified (hours, median and p90)
-- name: approval_latency
WITH {{rel}}, lat AS (
  SELECT g.ReleaseTrainId AS train_id, 'Waiver: requested to approved' AS step, 1 AS ord, w.ApprovedAt AS at, (julianday(w.ApprovedAt) - julianday(w.RequestedAt)) * 24.0 AS hours
  FROM GateWaivers w JOIN StageGates g ON g.Id = w.StageGateId WHERE w.ApprovedAt IS NOT NULL
  UNION ALL
  SELECT o.ReleaseTrainId, 'Freeze override: requested to granted', 2, o.ApprovedAt,
         (julianday(o.ApprovedAt) - julianday((SELECT max(a.OccurredAt) FROM AuditEvents a
            WHERE a.EntityType = 'FreezeWindow' AND a.Action = 'RequestOverride' AND a.EntityId = o.FreezeWindowId AND a.ReleaseTrainId = o.ReleaseTrainId AND a.OccurredAt <= o.ApprovedAt))) * 24.0
  FROM FreezeOverrides o
  UNION ALL
  SELECT g.ReleaseTrainId, 'Gate: last task done to certified', 3, g.CertifiedAt,
         (julianday(g.CertifiedAt) - julianday((SELECT max(k.CompletedAt) FROM ChecklistTasks k WHERE k.StageGateId = g.Id))) * 24.0
  FROM StageGates g WHERE g.Status = 'Certified'
), v AS (
  SELECT {{keys:lat.at}}, lat.step, lat.ord, lat.hours FROM lat JOIN rel ON rel.train_id = lat.train_id
  WHERE lat.hours IS NOT NULL AND lat.hours >= 0 AND date(lat.at) BETWEEN :from AND :to
), ranked AS (
  SELECT *, row_number() OVER (PARTITION BY {{keycols}}, step ORDER BY hours) AS rn, count(*) OVER (PARTITION BY {{keycols}}, step) AS n FROM v
)
SELECT {{keycols}}, step, n AS samples,
       round(max(CASE WHEN rn = (n + 1) / 2 THEN hours END), 1) AS median_h,
       round(max(CASE WHEN rn = max(1, CAST(0.9 * n + 0.999 AS INTEGER)) THEN hours END), 1) AS p90_h
FROM ranked GROUP BY {{keycols}}, step, ord
ORDER BY quarter, workspace, team, release_type, version, segment, ord;

-- M20 Slip reasons — target-date moves after the baseline, and the days they moved by, by the reason the records show
-- name: slip_reasons
-- The reason is what the records show: a No-Go decision with that new date. No reason code is stored for a manual move (that needs a TargetChanges table, an owner
-- decision: proposal section 1.6, question 3), so those are "Not recorded".
WITH {{rel}}, mv AS (
  SELECT a.ReleaseTrainId AS train_id, a.OccurredAt AS at,
         CAST(julianday(json_extract(a.AfterJson, '$.target')) - julianday(json_extract(a.BeforeJson, '$.target')) AS INTEGER) AS days,
         CASE WHEN EXISTS (SELECT 1 FROM GoNoGoDecisions d WHERE d.ReleaseTrainId = a.ReleaseTrainId AND d.Decision = 'NoGo' AND d.NewTargetReleaseDate = json_extract(a.AfterJson, '$.target'))
              THEN 'No-Go decision' ELSE 'Not recorded' END AS reason
  FROM AuditEvents a JOIN Baselines b ON b.ReleaseTrainId = a.ReleaseTrainId
  WHERE a.EntityType = 'ReleaseTrain' AND a.Action = 'ChangeTargetDate' AND a.OccurredAt >= b.CapturedAt AND date(a.OccurredAt) BETWEEN :from AND :to
), x AS (
  SELECT {{keys:mv.at}}, mv.reason, mv.days, mv.train_id FROM mv JOIN rel ON rel.train_id = mv.train_id
)
SELECT {{keycols}}, reason, count(*) AS moves, count(DISTINCT train_id) AS releases,
       sum(CASE WHEN days > 0 THEN days ELSE 0 END) AS days_later, sum(CASE WHEN days < 0 THEN -days ELSE 0 END) AS days_earlier
FROM x GROUP BY {{keycols}}, reason
ORDER BY quarter, workspace, team, release_type, version, segment, reason;

-- M21 Risk exposure — open releases by risk tier as of now: nearest target, past target, open blockers, High/VeryHigh without a rehearsed rollback
-- name: risk_exposure
WITH {{rel}}, o AS (
  SELECT {{keys:t.TargetReleaseDate}}, t.RiskTier AS risk_tier,
         CAST(julianday(t.TargetReleaseDate) - julianday(date(:now)) AS INTEGER) AS days_to_target,
         (SELECT count(*) FROM Blockers b WHERE b.ReleaseTrainId = t.Id AND b.ResolvedAt IS NULL) AS open_blockers,
         t.RollbackRehearsedAt AS rehearsed_at
  FROM ReleaseTrains t JOIN rel ON rel.train_id = t.Id
  WHERE t.CurrentStatus NOT IN ('Complete', 'Aborted') AND t.ArchivedAt IS NULL
)
SELECT {{keycols}}, risk_tier, count(*) AS open_releases, min(days_to_target) AS nearest_days, sum(days_to_target < 0) AS past_target,
       sum(open_blockers > 0) AS with_blockers, sum(open_blockers) AS open_blockers,
       sum(risk_tier IN ('High', 'VeryHigh') AND rehearsed_at IS NULL) AS no_rehearsal
FROM o GROUP BY {{keycols}}, risk_tier
ORDER BY quarter, workspace, team, release_type, version, segment, CASE risk_tier WHEN 'VeryHigh' THEN 1 WHEN 'High' THEN 2 WHEN 'Moderate' THEN 3 ELSE 4 END;

-- M22 Throughput by group — releases completed and lead time (created to complete, days), median and p90
-- name: throughput_by_group
WITH {{rel}}, c AS (
  SELECT {{keys:t.ActualEndAt}}, julianday(t.ActualEndAt) - julianday(t.CreatedAt) AS lead_days
  FROM ReleaseTrains t JOIN rel ON rel.train_id = t.Id
  WHERE t.CurrentStatus = 'Complete' AND date(t.ActualEndAt) BETWEEN :from AND :to
), ranked AS (
  SELECT *, row_number() OVER (PARTITION BY {{keycols}} ORDER BY lead_days) AS rn, count(*) OVER (PARTITION BY {{keycols}}) AS n FROM c
)
SELECT {{keycols}}, n AS completed,
       round(max(CASE WHEN rn = (n + 1) / 2 THEN lead_days END), 1) AS median_lead_days,
       round(max(CASE WHEN rn = max(1, CAST(0.9 * n + 0.999 AS INTEGER)) THEN lead_days END), 1) AS p90_lead_days
FROM ranked GROUP BY {{keycols}}
ORDER BY quarter, workspace, team, release_type, version, segment;

-- M23 Deployment success and availability — deployments linked to a release that finished in the period, and the confirmed threshold breaches on them
-- name: deployment_success
WITH {{rel}}, d AS (
  SELECT {{keys:p.FinishedAt}}, p.State AS state,
         (SELECT count(*) FROM ThresholdBreaches b WHERE b.DeploymentId = p.Id AND b.ConfirmedAt IS NOT NULL) AS breaches,
         (SELECT count(*) FROM ThresholdBreaches b WHERE b.DeploymentId = p.Id AND b.ConfirmedAt IS NOT NULL AND b.Metric = 'Availability') AS availability_breaches
  FROM Deployments p JOIN rel ON rel.train_id = p.ReleaseTrainId
  WHERE p.FinishedAt IS NOT NULL AND date(p.FinishedAt) BETWEEN :from AND :to
)
SELECT {{keycols}}, count(*) AS deployments, sum(state = 'Successful') AS successful, sum(state = 'Failed') AS failed, sum(state = 'RolledBack') AS rolled_back,
       round(100.0 * sum(state = 'Successful') / count(*), 1) AS success_pct, sum(breaches) AS breaches, sum(availability_breaches) AS availability_breaches
FROM d GROUP BY {{keycols}}
ORDER BY quarter, workspace, team, release_type, version, segment;

-- M24 PIR follow-through — reviews of releases that ended in the period: held by their due date, still overdue, and review actions done or overdue
-- name: pir_follow_through
WITH {{rel}}, p AS (
  SELECT {{keys:COALESCE(t.ActualEndAt, t.UpdatedAt)}}, pr.Status AS status, pr.HeldAt AS held_at,
         (SELECT value FROM json_each(:pir_due) WHERE key = pr.Id) AS due,
         (SELECT count(*) FROM PirActions a WHERE a.PirId = pr.Id) AS actions,
         (SELECT count(*) FROM PirActions a WHERE a.PirId = pr.Id AND a.DoneAt IS NOT NULL) AS actions_done,
         (SELECT count(*) FROM PirActions a WHERE a.PirId = pr.Id AND a.DoneAt IS NULL AND a.DueOn < :localday) AS actions_overdue
  FROM PostImplementationReviews pr JOIN ReleaseTrains t ON t.Id = pr.ReleaseTrainId JOIN rel ON rel.train_id = t.Id
  WHERE date(COALESCE(t.ActualEndAt, t.UpdatedAt)) BETWEEN :from AND :to
)
SELECT {{keycols}}, count(*) AS reviews, sum(due IS NOT NULL) AS with_due_date, sum(status IN ('Held', 'Closed')) AS held,
       sum(due IS NOT NULL AND held_at IS NOT NULL AND date(held_at) <= due) AS held_by_due,
       sum(due IS NOT NULL AND status IN ('Required', 'Scheduled') AND due < :localday) AS overdue,
       sum(actions) AS actions, sum(actions_done) AS actions_done, sum(actions_overdue) AS actions_overdue
FROM p GROUP BY {{keycols}}
ORDER BY quarter, workspace, team, release_type, version, segment;
