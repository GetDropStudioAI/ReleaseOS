-- Upgrade 010 (REOS-117, RT2; D48): versions link a product version's releases (Beta 1 to 3, CERT, PenTest Cert, Production).
-- From: migrations 20261024211850_Schema + 20261024211901_Triggers (after 009).
-- To:   migrations 20261025001928_Schema + 20261025001939_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/010_release_versions.sql
-- One transaction. Text below is verbatim from db/schema.sql:
--  * New table ReleaseVersions (empty), new unique index UX_Trains_VersionSlot (no release is in a version yet, so it starts empty).
--  * ReleaseTrains gains ReleaseVersionId, added in place (ALTER TABLE ADD COLUMN puts it where db/schema.sql has it; nullable).
--  * Existing releases are not put into versions: versions are created by a person (Teams & settings, or the release page), never guessed from titles.
--  * Three triggers change (trg_Train_TypeRules_Insert/Update ask for a type when a release is in a version; trg_Ws_ReleaseTrains_Insert/Update check the version's
--    workspace) and one is new (trg_Ws_ReleaseVersions_Update). Same-event triggers fire newest-first, so every trigger is dropped and all of
--    db/schema.sql's triggers are created again, verbatim and in file order.
-- Foreign keys are off while the column is added; the foreign_key_check at the end proves the result.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 009's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261024211850_Schema,20261024211901_Triggers';
DROP TABLE _upgrade_guard;

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
DROP TRIGGER trg_Waiver_DeclineRole;
DROP TRIGGER trg_Waiver_DeclineDistinct;
DROP TRIGGER trg_Waiver_ApprovedOrDeclined;
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
DROP TRIGGER trg_Train_KeyImmutable;
DROP TRIGGER trg_Train_AssignKey;
DROP TRIGGER trg_Train_TypeRules_Insert;
DROP TRIGGER trg_Train_TypeRules_Update;
DROP TRIGGER trg_ReleaseType_BetaFlagLocked;
DROP TRIGGER trg_Client_SegmentRules_Insert;
DROP TRIGGER trg_Client_SegmentRules_Update;
DROP TRIGGER trg_Deployment_Transition;
DROP TRIGGER trg_DeploymentEvent_NoUpdate;
DROP TRIGGER trg_Step_RunPaused;
DROP TRIGGER trg_Approval_ApprovedNeedsAll;
DROP TRIGGER trg_Approval_Final;
DROP TRIGGER trg_ApprovalDecision_ApprovalOpen;
DROP TRIGGER trg_ApprovalDecision_Final;
DROP TRIGGER trg_ApprovalDecision_Eligible;
DROP TRIGGER trg_ApprovalDecision_Separation;
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
DROP TRIGGER trg_Ws_ReleaseTypes_Insert;
DROP TRIGGER trg_Ws_ReleaseTypes_Update;
DROP TRIGGER trg_Ws_Environments_Insert;
DROP TRIGGER trg_Ws_Environments_Update;
DROP TRIGGER trg_Ws_Clients_Insert;
DROP TRIGGER trg_Ws_Clients_Update;
DROP TRIGGER trg_Ws_CatalogServices_Insert;
DROP TRIGGER trg_Ws_CatalogServices_Update;
DROP TRIGGER trg_Ws_IngestKeys_Update;
DROP TRIGGER trg_Ws_TrainEnvironments_Insert;
DROP TRIGGER trg_Ws_TrainEnvironments_Update;
DROP TRIGGER trg_Ws_BundledProducts_Insert;
DROP TRIGGER trg_Ws_Deployments_Insert;
DROP TRIGGER trg_Ws_Deployments_Update;
DROP TRIGGER trg_Ws_MetricSamples_Insert;
DROP TRIGGER trg_Ws_MetricRollups_Insert;
DROP TRIGGER trg_Ws_ThresholdBreaches_Insert;
DROP TRIGGER trg_Ws_ControlActions_Insert;
DROP TRIGGER trg_Ws_MetricSamples_Update;
DROP TRIGGER trg_Ws_MetricRollups_Update;
DROP TRIGGER trg_Ws_ThresholdBreaches_Update;
DROP TRIGGER trg_Ws_DeploymentControlActions_Update;
DROP TRIGGER trg_Ws_ApprovalPolicies_Update;
DROP TRIGGER trg_Ws_PolicyRequirements_Insert;
DROP TRIGGER trg_Ws_PolicyRequirements_Update;
DROP TRIGGER trg_Ws_Approvals_Insert;
DROP TRIGGER trg_Ws_Approvals_Update;

CREATE TABLE ReleaseVersions (                            -- RT2 (D48): one product version; its releases (Beta 1 to 3, CERT, PenTest Cert, Production) are linked through ReleaseTrains.ReleaseVersionId
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(Name)) BETWEEN 1 AND 100),
    ArchivedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, Name)
);
ALTER TABLE ReleaseTrains ADD COLUMN ReleaseVersionId TEXT REFERENCES ReleaseVersions(Id);
-- RT2 (D48): one release per type (and Beta number) in a version; an aborted release frees its place
CREATE UNIQUE INDEX UX_Trains_VersionSlot ON ReleaseTrains(ReleaseVersionId, ReleaseTypeId, COALESCE(BetaNumber, 0))
    WHERE ReleaseVersionId IS NOT NULL AND CurrentStatus <> 'Aborted';

CREATE TRIGGER trg_Audit_NoUpdate BEFORE UPDATE ON AuditEvents
WHEN NOT (OLD.Hash IS NULL AND NEW.Hash IS NOT NULL AND NEW.PrevHash IS NOT NULL
      AND NEW.Id IS OLD.Id AND NEW.OccurredAt IS OLD.OccurredAt AND NEW.ActorUserId IS OLD.ActorUserId AND NEW.ReleaseTrainId IS OLD.ReleaseTrainId
      AND NEW.EntityType IS OLD.EntityType AND NEW.EntityId IS OLD.EntityId AND NEW.Action IS OLD.Action AND NEW.BeforeJson IS OLD.BeforeJson
      AND NEW.AfterJson IS OLD.AfterJson AND NEW.WorkspaceId IS OLD.WorkspaceId)
BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
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
CREATE TRIGGER trg_Waiver_DeclineRole BEFORE UPDATE OF DeclinedByUserId ON GateWaivers
WHEN NEW.DeclinedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.DeclinedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are declined by a Governance Officer'); END;
CREATE TRIGGER trg_Waiver_DeclineDistinct BEFORE UPDATE OF DeclinedByUserId ON GateWaivers
WHEN NEW.DeclinedByUserId IS NOT NULL AND NEW.DeclinedByUserId = NEW.RequestedByUserId
BEGIN SELECT RAISE(ABORT, 'A waiver needs two distinct people: the requester cannot decline it'); END;
CREATE TRIGGER trg_Waiver_ApprovedOrDeclined BEFORE UPDATE OF ApprovedByUserId, DeclinedByUserId ON GateWaivers
WHEN (NEW.ApprovedByUserId IS NOT NULL AND NEW.DeclinedByUserId IS NOT NULL) OR (OLD.DeclinedAt IS NOT NULL AND NEW.ApprovedByUserId IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'A waiver is approved or declined, not both'); END;
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
CREATE TRIGGER trg_Train_KeyImmutable BEFORE UPDATE OF ReleaseKey ON ReleaseTrains WHEN OLD.ReleaseKey IS NOT NULL AND NEW.ReleaseKey IS NOT OLD.ReleaseKey
BEGIN SELECT RAISE(ABORT, 'A release key never changes'); END;
CREATE TRIGGER trg_Train_AssignKey AFTER INSERT ON ReleaseTrains WHEN NEW.ReleaseKey IS NULL
BEGIN
    UPDATE Workspaces SET NextKeyNumber = NextKeyNumber + 1 WHERE Id = NEW.WorkspaceId;
    UPDATE ReleaseTrains SET ReleaseKey = (SELECT KeyPrefix || '-' || (NextKeyNumber - 1) FROM Workspaces WHERE Id = NEW.WorkspaceId) WHERE Id = NEW.Id;
END;
CREATE TRIGGER trg_Train_TypeRules_Insert BEFORE INSERT ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A release needs a release type')
     WHERE NEW.ReleaseTypeId IS NULL AND EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'A release in a version needs a release type') WHERE NEW.ReleaseVersionId IS NOT NULL AND NEW.ReleaseTypeId IS NULL;
    SELECT RAISE(ABORT, 'Only a Beta release carries a Beta number')
     WHERE NEW.BetaNumber IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.IsBeta = 1);
    SELECT RAISE(ABORT, 'A Beta release needs its number, 1 to 3')
     WHERE NEW.BetaNumber IS NULL AND EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.IsBeta = 1);
END;
CREATE TRIGGER trg_Train_TypeRules_Update BEFORE UPDATE OF ReleaseTypeId, BetaNumber, ReleaseVersionId ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A release cannot lose its release type') WHERE NEW.ReleaseTypeId IS NULL AND OLD.ReleaseTypeId IS NOT NULL;
    SELECT RAISE(ABORT, 'A release in a version needs a release type') WHERE NEW.ReleaseVersionId IS NOT NULL AND NEW.ReleaseTypeId IS NULL;
    SELECT RAISE(ABORT, 'Only a Beta release carries a Beta number')
     WHERE NEW.BetaNumber IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.IsBeta = 1);
    SELECT RAISE(ABORT, 'A Beta release needs its number, 1 to 3')
     WHERE NEW.BetaNumber IS NULL AND EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.IsBeta = 1);
END;
CREATE TRIGGER trg_ReleaseType_BetaFlagLocked BEFORE UPDATE OF IsBeta ON ReleaseTypes WHEN NEW.IsBeta <> OLD.IsBeta
BEGIN
    SELECT RAISE(ABORT, 'A release type that releases use cannot become or stop being a Beta type')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains t WHERE t.ReleaseTypeId = OLD.Id);
END;
CREATE TRIGGER trg_Client_SegmentRules_Insert BEFORE INSERT ON Clients
BEGIN
    SELECT RAISE(ABORT, 'Only an On-Demand client has a CERT environment') WHERE NEW.Segment = 'Standard' AND NEW.CertEnvironmentId IS NOT NULL;
    SELECT RAISE(ABORT, 'An On-Demand client needs a CERT environment') WHERE NEW.Segment = 'OnDemand' AND NEW.CertEnvironmentId IS NULL;
END;
CREATE TRIGGER trg_Client_SegmentRules_Update BEFORE UPDATE OF Segment, CertEnvironmentId ON Clients
BEGIN
    SELECT RAISE(ABORT, 'Only an On-Demand client has a CERT environment') WHERE NEW.Segment = 'Standard' AND NEW.CertEnvironmentId IS NOT NULL;
    SELECT RAISE(ABORT, 'An On-Demand client needs a CERT environment') WHERE NEW.Segment = 'OnDemand' AND NEW.CertEnvironmentId IS NULL;
END;
CREATE TRIGGER trg_Deployment_Transition BEFORE UPDATE OF State ON Deployments WHEN NEW.State <> OLD.State
BEGIN
    SELECT RAISE(ABORT, 'Illegal deployment state transition')
     WHERE NOT ((OLD.State = 'Queued' AND NEW.State IN ('Running','Failed'))
             OR (OLD.State = 'Running' AND NEW.State IN ('Paused','Successful','Failed','RolledBack'))
             OR (OLD.State = 'Paused' AND NEW.State IN ('Running','Failed','RolledBack'))
             OR (OLD.State IN ('Successful','Failed') AND NEW.State = 'RolledBack'));
END;
CREATE TRIGGER trg_DeploymentEvent_NoUpdate BEFORE UPDATE ON DeploymentEvents BEGIN SELECT RAISE(ABORT, 'DeploymentEvents is append-only'); END;
CREATE TRIGGER trg_Step_RunPaused BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running' AND (SELECT PausedAt FROM RunbookRuns WHERE Id = NEW.RunId) IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'The run is paused: no step can start until it is resumed'); END;
CREATE TRIGGER trg_Approval_ApprovedNeedsAll BEFORE UPDATE OF Status ON Approvals
WHEN NEW.Status = 'Approved' AND (NOT EXISTS (SELECT 1 FROM ApprovalDecisions d WHERE d.ApprovalId = NEW.Id)
                               OR EXISTS (SELECT 1 FROM ApprovalDecisions d WHERE d.ApprovalId = NEW.Id AND d.Decision IS NOT 'Approved'))
BEGIN SELECT RAISE(ABORT, 'Every required approver must approve first'); END;
CREATE TRIGGER trg_Approval_Final BEFORE UPDATE OF Status ON Approvals
WHEN OLD.Status IN ('Approved','Cancelled') AND NEW.Status <> OLD.Status
BEGIN SELECT RAISE(ABORT, 'An approved or cancelled approval is final'); END;
CREATE TRIGGER trg_ApprovalDecision_ApprovalOpen BEFORE UPDATE ON ApprovalDecisions
WHEN (SELECT Status FROM Approvals WHERE Id = NEW.ApprovalId) IN ('Approved','Cancelled')
BEGIN SELECT RAISE(ABORT, 'The approval is closed'); END;
CREATE TRIGGER trg_ApprovalDecision_Final BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN OLD.Decision IS NOT NULL AND NEW.Decision IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'A decision is final; the requester resubmits the approval for a new round'); END;
CREATE TRIGGER trg_ApprovalDecision_Eligible BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN NEW.Decision IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'Not an eligible approver for this seat')
     WHERE NOT EXISTS (
        SELECT 1 FROM Approvals a JOIN ReleaseTrains t ON t.Id = a.ReleaseTrainId
         WHERE a.Id = NEW.ApprovalId
           AND EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.WorkspaceId = a.WorkspaceId AND m.UserId = NEW.DecidedByUserId)
           AND ((NEW.EligibleKind = 'ReleaseOwner' AND t.OwnerUserId = NEW.DecidedByUserId)
             OR (NEW.EligibleKind = 'WorkspaceRole' AND EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.WorkspaceId = a.WorkspaceId AND m.UserId = NEW.DecidedByUserId AND m.Role = NEW.EligibleValue))
             OR (NEW.EligibleKind = 'Team' AND EXISTS (SELECT 1 FROM TeamMembers tm WHERE tm.TeamId = NEW.EligibleValue AND tm.UserId = NEW.DecidedByUserId))));
END;
CREATE TRIGGER trg_ApprovalDecision_Separation BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN NEW.Decision = 'Approved'
BEGIN
    SELECT RAISE(ABORT, 'Separation of duties: you cannot approve a change you requested')
     WHERE EXISTS (SELECT 1 FROM Approvals a WHERE a.Id = NEW.ApprovalId AND a.RequestedByUserId = NEW.DecidedByUserId);
    SELECT RAISE(ABORT, 'Separation of duties: on a Production change the release owner approves only the release owner seat')
     WHERE NEW.EligibleKind <> 'ReleaseOwner'
       AND EXISTS (SELECT 1 FROM Approvals a JOIN ReleaseTrains t ON t.Id = a.ReleaseTrainId JOIN Environments e ON e.Id = a.EnvironmentId
                    WHERE a.Id = NEW.ApprovalId AND e.Kind = 'Production' AND t.OwnerUserId = NEW.DecidedByUserId);
    SELECT RAISE(ABORT, 'Separation of duties: one person cannot hold two seats on a Production change')
     WHERE EXISTS (SELECT 1 FROM ApprovalDecisions d JOIN Approvals a ON a.Id = d.ApprovalId JOIN Environments e ON e.Id = a.EnvironmentId
                    WHERE d.ApprovalId = NEW.ApprovalId AND d.Id <> NEW.Id AND d.Decision = 'Approved' AND d.DecidedByUserId = NEW.DecidedByUserId AND e.Kind = 'Production');
END;
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
    SELECT RAISE(ABORT, 'The owner is not a member of this workspace')
     WHERE NEW.OwnerUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call person is not a member of this workspace')
     WHERE NEW.OnCallUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OnCallUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owning team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OnCallTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The release type belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The version belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseVersions x WHERE x.Id = NEW.ReleaseVersionId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseTrains_Update BEFORE UPDATE OF WorkspaceId, TemplateId, ClonedFromTrainId, OwnerUserId, OwnerTeamId, OnCallUserId, OnCallTeamId, ReleaseTypeId, ReleaseVersionId ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owner is not a member of this workspace')
     WHERE NEW.OwnerUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call person is not a member of this workspace')
     WHERE NEW.OnCallUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OnCallUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owning team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OnCallTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The release type belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The version belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseVersions x WHERE x.Id = NEW.ReleaseVersionId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
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
CREATE TRIGGER trg_Ws_BundledProducts_Update BEFORE UPDATE OF ReleaseTrainId, CatalogServiceId ON BundledProducts
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The catalog service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
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
CREATE TRIGGER trg_Ws_ReleaseTypes_Insert BEFORE INSERT ON ReleaseTypes
BEGIN
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.DefaultTemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseTypes_Update BEFORE UPDATE OF WorkspaceId, DefaultTemplateId ON ReleaseTypes
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.DefaultTemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseVersions_Update BEFORE UPDATE OF WorkspaceId ON ReleaseVersions
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_Environments_Insert BEFORE INSERT ON Environments
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.ControlWebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Environments_Update BEFORE UPDATE OF WorkspaceId, ControlWebhookDestinationId ON Environments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.ControlWebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Clients_Insert BEFORE INSERT ON Clients
BEGIN
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.CertEnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Clients_Update BEFORE UPDATE OF WorkspaceId, CertEnvironmentId ON Clients
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.CertEnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CatalogServices_Insert BEFORE INSERT ON CatalogServices
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CatalogServices_Update BEFORE UPDATE OF WorkspaceId, OwnerTeamId ON CatalogServices
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_IngestKeys_Update BEFORE UPDATE OF WorkspaceId ON IngestKeys WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_TrainEnvironments_Insert BEFORE INSERT ON TrainEnvironments
BEGIN
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TrainEnvironments_Update BEFORE UPDATE OF ReleaseTrainId, EnvironmentId ON TrainEnvironments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_BundledProducts_Insert BEFORE INSERT ON BundledProducts
BEGIN
    SELECT RAISE(ABORT, 'The catalog service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Deployments_Insert BEFORE INSERT ON Deployments
BEGIN
    SELECT RAISE(ABORT, 'The service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The ingest key belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM IngestKeys x WHERE x.Id = NEW.IngestKeyId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The run belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookRuns x WHERE x.Id = NEW.RunId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_Deployments_Update BEFORE UPDATE OF WorkspaceId, ExternalId, CatalogServiceId, EnvironmentId, ReleaseTrainId, RunId ON Deployments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'A deployment keeps its external id, service and environment')
     WHERE NEW.ExternalId IS NOT OLD.ExternalId OR NEW.CatalogServiceId IS NOT OLD.CatalogServiceId OR NEW.EnvironmentId IS NOT OLD.EnvironmentId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The run belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookRuns x WHERE x.Id = NEW.RunId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_MetricSamples_Insert BEFORE INSERT ON MetricSamples
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricRollups_Insert BEFORE INSERT ON MetricRollups
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ThresholdBreaches_Insert BEFORE INSERT ON ThresholdBreaches
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ControlActions_Insert BEFORE INSERT ON DeploymentControlActions
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The breach belongs to another deployment')
     WHERE EXISTS (SELECT 1 FROM ThresholdBreaches x WHERE x.Id = NEW.BreachId AND x.DeploymentId IS NOT NEW.DeploymentId);
END;
CREATE TRIGGER trg_Ws_MetricSamples_Update BEFORE UPDATE OF WorkspaceId ON MetricSamples WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_MetricRollups_Update BEFORE UPDATE OF WorkspaceId ON MetricRollups WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ThresholdBreaches_Update BEFORE UPDATE OF WorkspaceId ON ThresholdBreaches WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_DeploymentControlActions_Update BEFORE UPDATE OF WorkspaceId ON DeploymentControlActions WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ApprovalPolicies_Update BEFORE UPDATE OF WorkspaceId ON ApprovalPolicies WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_PolicyRequirements_Insert BEFORE INSERT ON ApprovalPolicyRequirements
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE NEW.EligibleKind = 'Team'
       AND NOT EXISTS (SELECT 1 FROM Teams x JOIN ApprovalPolicies p ON p.Id = NEW.PolicyId WHERE x.Id = NEW.EligibleValue AND x.WorkspaceId = p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_PolicyRequirements_Update BEFORE UPDATE OF PolicyId, EligibleKind, EligibleValue ON ApprovalPolicyRequirements
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE NEW.EligibleKind = 'Team'
       AND NOT EXISTS (SELECT 1 FROM Teams x JOIN ApprovalPolicies p ON p.Id = NEW.PolicyId WHERE x.Id = NEW.EligibleValue AND x.WorkspaceId = p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Approvals_Insert BEFORE INSERT ON Approvals
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Approvals_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId, EnvironmentId ON Approvals
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'An approval keeps its release and environment')
     WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId OR NEW.EnvironmentId IS NOT OLD.EnvironmentId;
END;

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261024211850_Schema', '20261024211901_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261025001928_Schema', '10.0.12'),
    ('20261025001939_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
