-- Upgrade 005 (REOS-105, D45): the role triggers read the role a person holds in the train's workspace (WorkspaceMembers.Role), not Users.Role.
-- From: migrations 20261023032932_Schema + 20261023032948_Triggers (after 004).
-- To:   migrations 20261023045651_Schema + 20261023045703_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md §8):  sqlite3 releaseos.db < db/upgrades/005_workspace_roles.sql
-- One transaction. No table changes. 004 made every user a member of the default workspace with their Users.Role, and the app has kept the two
-- equal since, so on a database with one workspace nobody's rights change; a user that a raw-SQL import left without a membership gets one here.
-- Four triggers change (trg_Gate_ComplianceCertifierRole, trg_Waiver_ApproverRole, trg_Waiver_ApproverRoleInsert, trg_Override_ApproverRole). Same-event
-- triggers fire newest-first, so every trigger is dropped and all of db/schema.sql's triggers are created again, verbatim and in file order.

PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 004's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261023032932_Schema,20261023032948_Triggers';
DROP TABLE _upgrade_guard;

-- Every user keeps the role they had in the default workspace (a no-op unless a user was written without a membership).
INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role, AddedAt)
SELECT 'default', u.Id, u.Role, strftime('%Y-%m-%dT%H:%M:%SZ','now') FROM Users u
 WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.WorkspaceId = 'default' AND m.UserId = u.Id);

DROP TRIGGER trg_Audit_NoUpdate;
DROP TRIGGER trg_Audit_NoDelete;
DROP TRIGGER trg_GateTrans_NoUpdate;
DROP TRIGGER trg_GateTrans_NoDelete;
DROP TRIGGER trg_Baseline_NoUpdate;
DROP TRIGGER trg_Baseline_NoDelete;
DROP TRIGGER trg_GoNoGo_NoUpdate;
DROP TRIGGER trg_GoNoGo_NoDelete;
DROP TRIGGER trg_Dispatch_NoUpdate;
DROP TRIGGER trg_Dispatch_NoDelete;
DROP TRIGGER trg_Attachment_LockedNoDelete;
DROP TRIGGER trg_Attachment_LockedNoUpdate;
DROP TRIGGER trg_Gate_Transition;
DROP TRIGGER trg_Train_Transition;
DROP TRIGGER trg_Train_GateLockout;
DROP TRIGGER trg_Train_ExecutingRequiresGo;
DROP TRIGGER trg_Train_ExecutingNoExpiredConditions;
DROP TRIGGER trg_Train_ExecutingRequiresBaseline;
DROP TRIGGER trg_Train_ExecutingRequiresRollback;
DROP TRIGGER trg_Train_CompleteRequiresPir;
DROP TRIGGER trg_Gate_CertifyCapturesBaseline;
DROP TRIGGER trg_Gate_RequireActor;
DROP TRIGGER trg_Gate_LogTransition;
DROP TRIGGER trg_Gate_CertifyRequiresTasks;
DROP TRIGGER trg_Gate_ComplianceNeedsTask;
DROP TRIGGER trg_Gate_CertifyInOrder;
DROP TRIGGER trg_Gate_ComplianceCertifierRole;
DROP TRIGGER trg_Gate_CertifierNotTaskAuthor;
DROP TRIGGER trg_Gate_WaiveRequiresApproval;
DROP TRIGGER trg_Gate_CertifyLocksEvidence;
DROP TRIGGER trg_Waiver_ApproverRole;
DROP TRIGGER trg_Waiver_ApproverRoleInsert;
DROP TRIGGER trg_Override_NoUpdate;
DROP TRIGGER trg_Override_NoDelete;
DROP TRIGGER trg_Override_ApproverRole;
DROP TRIGGER trg_Task_ReopenDecertifies;
DROP TRIGGER trg_Task_InsertDecertifies;
DROP TRIGGER trg_Run_LiveRequiresExecuting;
DROP TRIGGER trg_Step_DependenciesDone;
DROP TRIGGER trg_Step_FreezeLockout;
DROP TRIGGER trg_Condition_RequiresConditionalGo;
DROP TRIGGER trg_Import_NoCommitWithErrors;
DROP TRIGGER trg_Ws_Holidays_Update;
DROP TRIGGER trg_Ws_TrainTemplates_Update;
DROP TRIGGER trg_Ws_CommTemplateLibrary_Update;
DROP TRIGGER trg_Ws_FreezeWindows_Update;
DROP TRIGGER trg_Ws_WebhookDestinations_Update;
DROP TRIGGER trg_Ws_Notifications_Update;
DROP TRIGGER trg_Ws_IcsTokens_Update;
DROP TRIGGER trg_Ws_ConnectorState_Update;
DROP TRIGGER trg_Ws_ReleaseTrains_Insert;
DROP TRIGGER trg_Ws_ReleaseTrains_Update;
DROP TRIGGER trg_Ws_Teams_Insert;
DROP TRIGGER trg_Ws_Teams_Update;
DROP TRIGGER trg_Ws_SyncAlerts_Insert;
DROP TRIGGER trg_Ws_SyncAlerts_Update;
DROP TRIGGER trg_Ws_ImportJobs_Insert;
DROP TRIGGER trg_Ws_ImportJobs_Update;
DROP TRIGGER trg_Ws_ExportJobs_Insert;
DROP TRIGGER trg_Ws_ExportJobs_Update;
DROP TRIGGER trg_Ws_AuditEvents_Insert;
DROP TRIGGER trg_Ws_TemplateGates_Insert;
DROP TRIGGER trg_Ws_TemplateGates_Update;
DROP TRIGGER trg_Ws_TemplateSteps_Insert;
DROP TRIGGER trg_Ws_TemplateSteps_Update;
DROP TRIGGER trg_Ws_TemplateCommSchedule_Insert;
DROP TRIGGER trg_Ws_TemplateCommSchedule_Update;
DROP TRIGGER trg_Ws_StageGates_Insert;
DROP TRIGGER trg_Ws_StageGates_Update;
DROP TRIGGER trg_Ws_RunbookSteps_Insert;
DROP TRIGGER trg_Ws_RunbookSteps_Update;
DROP TRIGGER trg_Ws_TrainMilestones_Insert;
DROP TRIGGER trg_Ws_TrainMilestones_Update;
DROP TRIGGER trg_Ws_Blockers_Insert;
DROP TRIGGER trg_Ws_Blockers_Update;
DROP TRIGGER trg_Ws_CommTemplates_Insert;
DROP TRIGGER trg_Ws_CommTemplates_Update;
DROP TRIGGER trg_Ws_CommSchedule_Insert;
DROP TRIGGER trg_Ws_CommSchedule_Update;
DROP TRIGGER trg_Ws_BundledProducts_Update;
DROP TRIGGER trg_Ws_RunbookRuns_Update;
DROP TRIGGER trg_Ws_ChecklistTasks_Insert;
DROP TRIGGER trg_Ws_ChecklistTasks_Update;
DROP TRIGGER trg_Ws_StepExecutions_Insert;
DROP TRIGGER trg_Ws_StepExecutions_Update;
DROP TRIGGER trg_Ws_StepDependencies_Insert;
DROP TRIGGER trg_Ws_StepDependencies_Update;
DROP TRIGGER trg_Ws_CommDispatches_Insert;
DROP TRIGGER trg_Ws_FreezeOverrides_Insert;
DROP TRIGGER trg_Ws_MetricSnapshots_Insert;
DROP TRIGGER trg_Ws_MetricSnapshots_Update;

CREATE TRIGGER trg_Audit_NoUpdate BEFORE UPDATE ON AuditEvents BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
CREATE TRIGGER trg_Audit_NoDelete BEFORE DELETE ON AuditEvents BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
CREATE TRIGGER trg_GateTrans_NoUpdate BEFORE UPDATE ON GateTransitions BEGIN SELECT RAISE(ABORT, 'GateTransitions is append-only'); END;
CREATE TRIGGER trg_GateTrans_NoDelete BEFORE DELETE ON GateTransitions BEGIN SELECT RAISE(ABORT, 'GateTransitions is append-only'); END;
CREATE TRIGGER trg_Baseline_NoUpdate BEFORE UPDATE ON Baselines BEGIN SELECT RAISE(ABORT, 'Baselines are immutable'); END;
CREATE TRIGGER trg_Baseline_NoDelete BEFORE DELETE ON Baselines BEGIN SELECT RAISE(ABORT, 'Baselines are immutable'); END;
CREATE TRIGGER trg_GoNoGo_NoUpdate BEFORE UPDATE ON GoNoGoDecisions BEGIN SELECT RAISE(ABORT, 'Go/No-Go decisions are immutable'); END;
CREATE TRIGGER trg_GoNoGo_NoDelete BEFORE DELETE ON GoNoGoDecisions BEGIN SELECT RAISE(ABORT, 'Go/No-Go decisions are immutable'); END;
CREATE TRIGGER trg_Dispatch_NoUpdate BEFORE UPDATE ON CommDispatches BEGIN SELECT RAISE(ABORT, 'Dispatches are immutable'); END;
CREATE TRIGGER trg_Dispatch_NoDelete BEFORE DELETE ON CommDispatches BEGIN SELECT RAISE(ABORT, 'Dispatches are immutable'); END;
CREATE TRIGGER trg_Attachment_LockedNoDelete BEFORE DELETE ON Attachments WHEN OLD.IsLocked = 1
BEGIN SELECT RAISE(ABORT, 'Attachment is locked evidence'); END;
CREATE TRIGGER trg_Attachment_LockedNoUpdate BEFORE UPDATE ON Attachments WHEN OLD.IsLocked = 1
BEGIN SELECT RAISE(ABORT, 'Attachment is locked evidence'); END;
CREATE TRIGGER trg_Gate_Transition BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status AND NOT (
       (OLD.Status = 'Pending'    AND NEW.Status = 'InProgress')
    OR (OLD.Status = 'InProgress' AND NEW.Status IN ('Certified','Failed','Waived'))
    OR (OLD.Status = 'Failed'     AND NEW.Status = 'InProgress')
    OR (OLD.Status = 'Certified'  AND NEW.Status = 'InProgress')
)
BEGIN SELECT RAISE(ABORT, 'Illegal gate status transition'); END;
CREATE TRIGGER trg_Train_Transition BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus <> OLD.CurrentStatus AND NOT (
       (OLD.CurrentStatus = 'Planning'  AND NEW.CurrentStatus IN ('Gated','Aborted'))
    OR (OLD.CurrentStatus = 'Gated'     AND NEW.CurrentStatus IN ('Planning','Executing','Aborted'))
    OR (OLD.CurrentStatus = 'Executing' AND NEW.CurrentStatus IN ('Complete','Aborted'))
)
BEGIN SELECT RAISE(ABORT, 'Illegal train status transition'); END;
CREATE TRIGGER trg_Train_GateLockout BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus IN ('Gated','Executing','Complete') AND NEW.CurrentStatus <> OLD.CurrentStatus AND (
       (OLD.CurrentStatus = 'Planning'  AND NEW.CurrentStatus IN ('Gated','Aborted'))
    OR (OLD.CurrentStatus = 'Gated'     AND NEW.CurrentStatus IN ('Planning','Executing','Aborted'))
    OR (OLD.CurrentStatus = 'Executing' AND NEW.CurrentStatus IN ('Complete','Aborted')))
 AND EXISTS (
    SELECT 1 FROM StageGates g
    WHERE g.ReleaseTrainId = NEW.Id
      AND g.Status NOT IN ('Certified','Waived')
      AND (CASE g.RequiredBeforeStatus WHEN 'Gated' THEN 1 WHEN 'Executing' THEN 2 ELSE 3 END)
       <= (CASE NEW.CurrentStatus      WHEN 'Gated' THEN 1 WHEN 'Executing' THEN 2 ELSE 3 END)
 )
BEGIN SELECT RAISE(ABORT, 'Gate lockout: an uncertified gate blocks this transition'); END;
CREATE TRIGGER trg_Train_ExecutingRequiresGo BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated' AND NOT EXISTS (
    SELECT 1 FROM GoNoGoDecisions d WHERE d.ReleaseTrainId = NEW.Id AND d.Decision IN ('Go','GoWithConditions')
      AND d.DecidedAt = (SELECT max(DecidedAt) FROM GoNoGoDecisions WHERE ReleaseTrainId = NEW.Id))
BEGIN SELECT RAISE(ABORT, 'Executing requires a recorded Go decision'); END;
CREATE TRIGGER trg_Train_ExecutingNoExpiredConditions BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated' AND EXISTS (
    SELECT 1 FROM GoNoGoConditions c JOIN GoNoGoDecisions d ON d.Id = c.DecisionId
    WHERE d.ReleaseTrainId = NEW.Id AND c.ClosedAt IS NULL
      AND c.ExpiresAt <= COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')))
BEGIN SELECT RAISE(ABORT, 'A Go/No-Go condition expired without being closed'); END;
CREATE TRIGGER trg_Train_ExecutingRequiresBaseline BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated'
 AND NOT EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = NEW.Id)
BEGIN SELECT RAISE(ABORT, 'Executing requires a captured baseline'); END;
CREATE TRIGGER trg_Train_ExecutingRequiresRollback BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated'
 AND NEW.RiskTier IN ('High','VeryHigh') AND NEW.RollbackRehearsedAt IS NULL
BEGIN SELECT RAISE(ABORT, 'High-risk train requires a rehearsed rollback'); END;
CREATE TRIGGER trg_Train_CompleteRequiresPir AFTER UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Complete' AND NEW.CloseCode IN ('SuccessfulWithIssues','Unsuccessful')
 AND NOT EXISTS (SELECT 1 FROM PostImplementationReviews p WHERE p.ReleaseTrainId = NEW.Id)
BEGIN
    INSERT INTO PostImplementationReviews(Id, ReleaseTrainId, RequiredReason, Status)
    VALUES (lower(hex(randomblob(16))), NEW.Id, 'CloseCode=' || NEW.CloseCode, 'Required');
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, AfterJson, WorkspaceId)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId, NEW.Id, 'PostImplementationReview',
            (SELECT Id FROM PostImplementationReviews WHERE ReleaseTrainId = NEW.Id),
            'AutoRequired', json_object('reason', 'CloseCode=' || NEW.CloseCode), NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Gate_CertifyCapturesBaseline AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND OLD.Status <> 'Certified' AND NEW.GateName LIKE '%Freeze%'
 AND NOT EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = NEW.ReleaseTrainId)
BEGIN
    INSERT INTO Baselines(Id, ReleaseTrainId, CapturedAt, CapturedAtGateId, PlannedReleaseDate, SnapshotJson, CapturedByUserId)
    SELECT lower(hex(randomblob(16))), t.Id, NEW.CertifiedAt, NEW.Id, t.TargetReleaseDate,
           json_object(
             'products', (SELECT json_group_array(json_object('name',ProductName,'version',VersionTag)) FROM BundledProducts WHERE ReleaseTrainId = t.Id),
             'gates',    (SELECT json_group_array(json_object('name',GateName,'dueOn',DueOn)) FROM StageGates WHERE ReleaseTrainId = t.Id),
             'steps',    (SELECT json_group_array(json_object('code',StepCode,'plannedStart',PlannedStartAt,'plannedMin',PlannedDurationMin)) FROM RunbookSteps WHERE ReleaseTrainId = t.Id)
           ),
           NEW.CertifiedByUserId
    FROM ReleaseTrains t WHERE t.Id = NEW.ReleaseTrainId;
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.CertifiedByUserId,
            NEW.ReleaseTrainId, 'Baseline', NEW.ReleaseTrainId, 'AutoCaptured', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId));
END;
CREATE TRIGGER trg_Gate_RequireActor BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status AND NEW.LastChangedByUserId IS NULL
BEGIN SELECT RAISE(ABORT, 'Gate status change requires LastChangedByUserId'); END;
CREATE TRIGGER trg_Gate_LogTransition AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status
BEGIN
    INSERT INTO GateTransitions(StageGateId, FromStatus, ToStatus, ActorUserId, OccurredAt)
    VALUES (NEW.Id, OLD.Status, NEW.Status, NEW.LastChangedByUserId, COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')));
END;
CREATE TRIGGER trg_Gate_CertifyRequiresTasks BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id AND t.IsCompleted = 0)
BEGIN SELECT RAISE(ABORT, 'Gate has open checklist tasks'); END;
CREATE TRIGGER trg_Gate_ComplianceNeedsTask BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND NEW.GateClass = 'Compliance'
 AND NOT EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id)
BEGIN SELECT RAISE(ABORT, 'Compliance gate needs at least one task'); END;
CREATE TRIGGER trg_Gate_CertifyInOrder BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived') AND EXISTS (
    SELECT 1 FROM StageGates p WHERE p.ReleaseTrainId = NEW.ReleaseTrainId
      AND p.SequenceOrder < NEW.SequenceOrder AND p.Status NOT IN ('Certified','Waived'))
BEGIN SELECT RAISE(ABORT, 'An earlier gate is not certified'); END;
CREATE TRIGGER trg_Gate_ComplianceCertifierRole BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived') AND NEW.GateClass = 'Compliance' AND NEW.CertifiedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId
               WHERE t.Id = NEW.ReleaseTrainId AND m.UserId = NEW.CertifiedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Compliance gates are certified by Governance Officers only'); END;
CREATE TRIGGER trg_Gate_CertifierNotTaskAuthor BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND NEW.GateClass = 'Compliance'
 AND EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id AND t.CompletedByUserId = NEW.CertifiedByUserId)
BEGIN SELECT RAISE(ABORT, 'Segregation of duties: certifier completed a task in this gate'); END;
CREATE TRIGGER trg_Gate_WaiveRequiresApproval BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Waived' AND NOT EXISTS (
    SELECT 1 FROM GateWaivers w WHERE w.StageGateId = NEW.Id AND w.ApprovedByUserId IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'Waiver requires a second approver'); END;
CREATE TRIGGER trg_Gate_CertifyLocksEvidence AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived')
BEGIN
    UPDATE Attachments SET IsLocked = 1
     WHERE IsLocked = 0 AND ((EntityType = 'Gate' AND EntityId = NEW.Id)
        OR (EntityType = 'Task' AND EntityId IN (SELECT Id FROM ChecklistTasks WHERE StageGateId = NEW.Id)));
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           NEW.ReleaseTrainId, 'StageGate', NEW.Id, 'EvidenceLocked', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId)
    WHERE OLD.Status NOT IN ('Certified','Waived');
END;
CREATE TRIGGER trg_Waiver_ApproverRole BEFORE UPDATE OF ApprovedByUserId ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.ApprovedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;
CREATE TRIGGER trg_Waiver_ApproverRoleInsert BEFORE INSERT ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.ApprovedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;
CREATE TRIGGER trg_Override_NoUpdate BEFORE UPDATE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;
CREATE TRIGGER trg_Override_NoDelete BEFORE DELETE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;
CREATE TRIGGER trg_Override_ApproverRole BEFORE INSERT ON FreezeOverrides
WHEN COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId
               WHERE t.Id = NEW.ReleaseTrainId AND m.UserId = NEW.ApprovedByUserId), '') NOT IN ('ReleaseManager','GovernanceOfficer')
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are approved by a Release Manager or Governance Officer'); END;
CREATE TRIGGER trg_Task_ReopenDecertifies AFTER UPDATE OF IsCompleted ON ChecklistTasks
WHEN OLD.IsCompleted = 1 AND NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = g.ReleaseTrainId)
      FROM StageGates g WHERE g.Id = NEW.StageGateId AND g.Status = 'Certified';
    UPDATE StageGates SET Status = 'InProgress', CertifiedByUserId = NULL, CertifiedAt = NULL,
           LastChangedByUserId = NEW.LastChangedByUserId, LastChangedAt = NEW.LastChangedAt, Version = Version + 1
     WHERE Id = NEW.StageGateId AND Status = 'Certified';
END;
CREATE TRIGGER trg_Task_InsertDecertifies AFTER INSERT ON ChecklistTasks
WHEN NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = g.ReleaseTrainId)
      FROM StageGates g WHERE g.Id = NEW.StageGateId AND g.Status = 'Certified';
    UPDATE StageGates SET Status = 'InProgress', CertifiedByUserId = NULL, CertifiedAt = NULL,
           LastChangedByUserId = NEW.LastChangedByUserId, LastChangedAt = NEW.LastChangedAt, Version = Version + 1
     WHERE Id = NEW.StageGateId AND Status = 'Certified';
END;
CREATE TRIGGER trg_Run_LiveRequiresExecuting BEFORE INSERT ON RunbookRuns
WHEN NEW.Mode = 'Live' AND (SELECT CurrentStatus FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId) <> 'Executing'
BEGIN SELECT RAISE(ABORT, 'Live run requires the train to be Executing'); END;
CREATE TRIGGER trg_Step_DependenciesDone BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running' AND EXISTS (
    SELECT 1 FROM StepDependencies d
    LEFT JOIN StepExecutions e ON e.StepId = d.DependsOnStepId AND e.RunId = NEW.RunId
    WHERE d.StepId = NEW.StepId AND COALESCE(e.Status,'Scheduled') NOT IN ('Done','Skipped'))
BEGIN SELECT RAISE(ABORT, 'Dependency not complete'); END;
CREATE TRIGGER trg_Step_FreezeLockout BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running'
 AND (SELECT Mode FROM RunbookRuns WHERE Id = NEW.RunId) = 'Live'
 AND (SELECT Section FROM RunbookSteps WHERE Id = NEW.StepId) = 'Deploy'
 AND EXISTS (
    SELECT 1 FROM FreezeWindows f
    JOIN RunbookSteps s ON s.Id = NEW.StepId
    LEFT JOIN BundledProducts p ON p.Id = s.BundledProductId
    WHERE f.Kind = 'Freeze'
      AND f.WorkspaceId = (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = s.ReleaseTrainId)   -- another workspace's freeze never applies
      AND NEW.ActualStartAt >= f.StartsAt AND NEW.ActualStartAt < f.EndsAt
      AND (f.ProductPattern IS NULL OR (p.ProductName IS NOT NULL AND p.ProductName GLOB f.ProductPattern))
      AND NOT EXISTS (
          SELECT 1 FROM FreezeOverrides o
          WHERE o.FreezeWindowId = f.Id AND o.ReleaseTrainId = s.ReleaseTrainId AND o.ExpiresAt > NEW.ActualStartAt))
BEGIN SELECT RAISE(ABORT, 'Freeze window: no valid override for this train'); END;
CREATE TRIGGER trg_Condition_RequiresConditionalGo BEFORE INSERT ON GoNoGoConditions
WHEN (SELECT Decision FROM GoNoGoDecisions WHERE Id = NEW.DecisionId) <> 'GoWithConditions'
BEGIN SELECT RAISE(ABORT, 'Conditions belong to a GoWithConditions decision'); END;
CREATE TRIGGER trg_Import_NoCommitWithErrors BEFORE UPDATE OF Status ON ImportJobs
WHEN NEW.Status = 'Committed' AND NEW.ErrorCount > 0
BEGIN SELECT RAISE(ABORT, 'Import has row errors; fix the file and re-preview'); END;
CREATE TRIGGER trg_Ws_Holidays_Update BEFORE UPDATE OF WorkspaceId ON Holidays WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_TrainTemplates_Update BEFORE UPDATE OF WorkspaceId ON TrainTemplates WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_CommTemplateLibrary_Update BEFORE UPDATE OF WorkspaceId ON CommTemplateLibrary WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_FreezeWindows_Update BEFORE UPDATE OF WorkspaceId ON FreezeWindows WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_WebhookDestinations_Update BEFORE UPDATE OF WorkspaceId ON WebhookDestinations WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_Notifications_Update BEFORE UPDATE OF WorkspaceId ON Notifications WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_IcsTokens_Update BEFORE UPDATE OF WorkspaceId ON IcsTokens WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ConnectorState_Update BEFORE UPDATE OF WorkspaceId ON ConnectorState WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ReleaseTrains_Insert BEFORE INSERT ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseTrains_Update BEFORE UPDATE OF WorkspaceId, TemplateId, ClonedFromTrainId ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Teams_Insert BEFORE INSERT ON Teams
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Teams_Update BEFORE UPDATE OF WorkspaceId, WebhookDestinationId ON Teams
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_SyncAlerts_Insert BEFORE INSERT ON SyncAlerts
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_SyncAlerts_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON SyncAlerts
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ImportJobs_Insert BEFORE INSERT ON ImportJobs
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ImportJobs_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON ImportJobs
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ExportJobs_Insert BEFORE INSERT ON ExportJobs
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ExportJobs_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON ExportJobs
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_AuditEvents_Insert BEFORE INSERT ON AuditEvents
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateGates_Insert BEFORE INSERT ON TemplateGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateGates_Update BEFORE UPDATE OF TemplateId, OwnerTeamId ON TemplateGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateSteps_Insert BEFORE INSERT ON TemplateSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateSteps_Update BEFORE UPDATE OF TemplateId, OwnerTeamId ON TemplateSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateCommSchedule_Insert BEFORE INSERT ON TemplateCommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateCommSchedule_Update BEFORE UPDATE OF TemplateId, LibraryTemplateId ON TemplateCommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_StageGates_Insert BEFORE INSERT ON StageGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_StageGates_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId ON StageGates
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_RunbookSteps_Insert BEFORE INSERT ON RunbookSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_RunbookSteps_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId, BundledProductId ON RunbookSteps
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_TrainMilestones_Insert BEFORE INSERT ON TrainMilestones
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TrainMilestones_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId ON TrainMilestones
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Blockers_Insert BEFORE INSERT ON Blockers
BEGIN
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_Blockers_Update BEFORE UPDATE OF ReleaseTrainId, BundledProductId, StageGateId ON Blockers
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommTemplates_Insert BEFORE INSERT ON CommTemplates
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommTemplates_Update BEFORE UPDATE OF ReleaseTrainId, LibraryTemplateId, StageGateId ON CommTemplates
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommSchedule_Insert BEFORE INSERT ON CommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The communication template belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommTemplates x WHERE x.Id = NEW.CommTemplateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The dispatch belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommDispatches x JOIN CommTemplates c ON c.Id = x.CommTemplateId
                    WHERE x.Id = NEW.DispatchId AND c.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommSchedule_Update BEFORE UPDATE OF ReleaseTrainId, CommTemplateId, DispatchId ON CommSchedule
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The communication template belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommTemplates x WHERE x.Id = NEW.CommTemplateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The dispatch belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommDispatches x JOIN CommTemplates c ON c.Id = x.CommTemplateId
                    WHERE x.Id = NEW.DispatchId AND c.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_BundledProducts_Update BEFORE UPDATE OF ReleaseTrainId ON BundledProducts WHEN NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another train'); END;
CREATE TRIGGER trg_Ws_RunbookRuns_Update BEFORE UPDATE OF ReleaseTrainId ON RunbookRuns WHEN NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another train'); END;
CREATE TRIGGER trg_Ws_ChecklistTasks_Insert BEFORE INSERT ON ChecklistTasks
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN StageGates g ON g.Id = NEW.StageGateId JOIN ReleaseTrains p ON p.Id = g.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x JOIN StageGates g ON g.Id = NEW.StageGateId
                    WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT g.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_ChecklistTasks_Update BEFORE UPDATE OF StageGateId, OwnerTeamId, BundledProductId ON ChecklistTasks
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x JOIN StageGates o ON o.Id = OLD.StageGateId
                    WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT o.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN StageGates g ON g.Id = NEW.StageGateId JOIN ReleaseTrains p ON p.Id = g.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x JOIN StageGates g ON g.Id = NEW.StageGateId
                    WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT g.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepExecutions_Insert BEFORE INSERT ON StepExecutions
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookRuns r ON r.Id = NEW.RunId
                    WHERE x.Id = NEW.StepId AND x.ReleaseTrainId IS NOT r.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepExecutions_Update BEFORE UPDATE OF RunId, StepId ON StepExecutions
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookRuns r ON r.Id = NEW.RunId
                    WHERE x.Id = NEW.StepId AND x.ReleaseTrainId IS NOT r.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepDependencies_Insert BEFORE INSERT ON StepDependencies
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookSteps s ON s.Id = NEW.StepId
                    WHERE x.Id = NEW.DependsOnStepId AND x.ReleaseTrainId IS NOT s.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepDependencies_Update BEFORE UPDATE OF StepId, DependsOnStepId ON StepDependencies
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookSteps s ON s.Id = NEW.StepId
                    WHERE x.Id = NEW.DependsOnStepId AND x.ReleaseTrainId IS NOT s.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommDispatches_Insert BEFORE INSERT ON CommDispatches
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x JOIN CommTemplates c ON c.Id = NEW.CommTemplateId JOIN ReleaseTrains p ON p.Id = c.ReleaseTrainId
                    WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_FreezeOverrides_Insert BEFORE INSERT ON FreezeOverrides
BEGIN
    SELECT RAISE(ABORT, 'The freeze window belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM FreezeWindows x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.FreezeWindowId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricSnapshots_Insert BEFORE INSERT ON MetricSnapshots
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x JOIN ExportJobs p ON p.Id = NEW.ExportJobId
                    WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricSnapshots_Update BEFORE UPDATE OF ExportJobId, ReleaseTrainId ON MetricSnapshots
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x JOIN ExportJobs p ON p.Id = NEW.ExportJobId
                    WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261023032932_Schema', '20261023032948_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261023045651_Schema', '10.0.12'),
    ('20261023045703_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
