-- Upgrade 016 (REOS-175, REOS-177; D56, D63): the CAB module's data model and its separation-of-duties triggers.
-- From: migrations 20261126134858_Schema + 20261126134908_Triggers (after 015).
-- To:   migrations 20261126225422_Schema + 20261126225440_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/016_cab.sql
-- One transaction. Text below is verbatim from db/schema.sql (the two blocks delimited ">>> REOS-175 + REOS-177"):
--  * New tables: ChangeRequests, CabMeetings, CabAgendaItems, CabAttendees, CabDecisions, with 5 indexes. They start EMPTY and are only reachable while the 'cab' module is enabled in a workspace.
--  * New triggers: 21 (workspace boundary, append-only decisions, status moves, separation of duties). No existing table changes, so nothing is rebuilt and no existing trigger is dropped.
--
-- Chained after 015 (outbound webhooks) when both merged (CHAIN 1-3 below carry 015's pair as the guard and the regenerated pair as the result).

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly the previous upgrade's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261126134858_Schema,20261126134908_Triggers';   -- CHAIN 1
DROP TABLE _upgrade_guard;

-- >>> REOS-175 + REOS-177 (D56, D63): CAB module data model. Own block. Reachable only while the 'cab' module is enabled in the workspace (the API guard answers 404 otherwise); the tables exist and are empty with the module off.
-- Version-exempt (append-only): CabDecisions. Roots with WorkspaceId: ChangeRequests, CabMeetings. Children take their workspace from the parent: CabAgendaItems, CabAttendees, CabDecisions.
CREATE TABLE ChangeRequests (                             -- a change request: Standard, Normal or Emergency; linked to a release and a Go/No-Go decision. Never deleted: cancel it
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    ChangeKey TEXT NOT NULL CHECK (length(ChangeKey) BETWEEN 1 AND 40),   -- CHG-0001, numbered per workspace by the service
    Title TEXT NOT NULL CHECK (length(trim(Title)) BETWEEN 1 AND 200),
    Description TEXT CHECK (Description IS NULL OR length(Description) <= 4000),
    ChangeType TEXT NOT NULL CHECK (ChangeType IN ('Standard','Normal','Emergency')),
    Status TEXT NOT NULL DEFAULT 'Draft' CHECK (Status IN ('Draft','Submitted','Approved','Rejected','Implemented','Closed','Cancelled')),
    RiskLevel TEXT NOT NULL DEFAULT 'Medium' CHECK (RiskLevel IN ('Low','Medium','High','Critical')),
    RiskFactors TEXT CHECK (RiskFactors IS NULL OR length(RiskFactors) <= 4000),   -- free text for now; REOS-176 shows the visible risk rules
    RequestedByUserId TEXT NOT NULL REFERENCES Users(Id), -- the requester: cannot approve or reject this change (REOS-177)
    CreatedByUserId TEXT NOT NULL REFERENCES Users(Id),   -- who keyed it in; differs from the requester when raised on someone's behalf. Also counts as "requested" for the chair rule (REOS-177)
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE SET NULL,
    GoNoGoDecisionId TEXT REFERENCES GoNoGoDecisions(Id) ON DELETE SET NULL,
    PlannedStartAt TEXT,                                  -- UTC; both or neither
    PlannedEndAt TEXT,
    RollbackNote TEXT CHECK (RollbackNote IS NULL OR length(RollbackNote) <= 4000),
    SubmittedAt TEXT,
    DecidedByUserId TEXT REFERENCES Users(Id),            -- the person whose decision moved it to Approved or Rejected (same pattern as TrainTemplates.ApprovedByUserId)
    DecidedAt TEXT,
    ImplementedAt TEXT,
    ClosedAt TEXT,
    CreatedAt TEXT NOT NULL,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (WorkspaceId, ChangeKey),
    CHECK ((PlannedStartAt IS NULL) = (PlannedEndAt IS NULL)),
    CHECK (PlannedEndAt IS NULL OR PlannedEndAt > PlannedStartAt),
    CHECK ((DecidedByUserId IS NULL) = (DecidedAt IS NULL)),
    CHECK (Status NOT IN ('Approved','Rejected') OR DecidedByUserId IS NOT NULL)
);

CREATE TABLE CabMeetings (                                -- a CAB meeting: a chair, a time window, an agenda of changes, attendees and recorded decisions
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    Title TEXT NOT NULL CHECK (length(trim(Title)) BETWEEN 1 AND 200),
    StartsAt TEXT NOT NULL,                               -- UTC
    EndsAt TEXT NOT NULL,
    ChairUserId TEXT NOT NULL REFERENCES Users(Id),
    Status TEXT NOT NULL DEFAULT 'Planned' CHECK (Status IN ('Planned','InProgress','Completed','Cancelled')),
    Location TEXT CHECK (Location IS NULL OR length(Location) <= 200),
    Minutes TEXT CHECK (Minutes IS NULL OR length(Minutes) <= 8000),
    CreatedByUserId TEXT NOT NULL REFERENCES Users(Id),
    CreatedAt TEXT NOT NULL,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (EndsAt > StartsAt)
);

CREATE TABLE CabAgendaItems (                             -- a change put in front of a meeting
    Id TEXT PRIMARY KEY,
    MeetingId TEXT NOT NULL REFERENCES CabMeetings(Id) ON DELETE CASCADE,
    ChangeRequestId TEXT NOT NULL REFERENCES ChangeRequests(Id),
    SequenceOrder INTEGER NOT NULL CHECK (SequenceOrder >= 1),
    Notes TEXT CHECK (Notes IS NULL OR length(Notes) <= 2000),
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (MeetingId, ChangeRequestId)
);

CREATE TABLE CabAttendees (                               -- who is invited and who came; the chair is the meeting's ChairUserId and also has a row
    MeetingId TEXT NOT NULL REFERENCES CabMeetings(Id) ON DELETE CASCADE,
    UserId TEXT NOT NULL REFERENCES Users(Id),
    AttendeeRole TEXT NOT NULL DEFAULT 'Member' CHECK (AttendeeRole IN ('Member','Guest')),
    Attendance TEXT NOT NULL DEFAULT 'Invited' CHECK (Attendance IN ('Invited','Attended','Absent')),
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (MeetingId, UserId)
);

CREATE TABLE CabDecisions (                               -- one person's recorded decision on a change; immutable. Approved or Rejected by someone other than the requester (REOS-177), or Deferred
    Id TEXT PRIMARY KEY,
    ChangeRequestId TEXT NOT NULL REFERENCES ChangeRequests(Id),
    MeetingId TEXT REFERENCES CabMeetings(Id) ON DELETE SET NULL,   -- null when decided outside a meeting (Emergency review, Standard change)
    Decision TEXT NOT NULL CHECK (Decision IN ('Approved','Rejected','Deferred')),
    Retroactive INTEGER NOT NULL DEFAULT 0 CHECK (Retroactive IN (0, 1)),   -- 1 = the review of an Emergency change that was implemented first
    Rationale TEXT CHECK (Rationale IS NULL OR length(Rationale) <= 2000),
    DecidedByUserId TEXT NOT NULL REFERENCES Users(Id),
    DecidedAt TEXT NOT NULL,
    CHECK (Retroactive = 0 OR Decision <> 'Deferred')
);

CREATE INDEX IX_ChangeRequests_Workspace ON ChangeRequests(WorkspaceId, Status, PlannedStartAt);
CREATE INDEX IX_ChangeRequests_Train ON ChangeRequests(ReleaseTrainId) WHERE ReleaseTrainId IS NOT NULL;
CREATE INDEX IX_CabMeetings_Workspace ON CabMeetings(WorkspaceId, StartsAt);
CREATE INDEX IX_CabAgenda_Change ON CabAgendaItems(ChangeRequestId);
CREATE INDEX IX_CabDecisions_Change ON CabDecisions(ChangeRequestId, DecidedAt);
-- <<< REOS-175 + REOS-177
-- >>> REOS-175 + REOS-177 (D56, D63): CAB module triggers. Own block, in file order (SQLite fires same-event triggers newest-first, so the rule written last is the one reported first).
-- Workspace boundary (the trg_Ws_* pattern): a root never moves workspace; a person named on a row is a member of the row's workspace; rows that point at each other are in one workspace.
-- Separation of duties for change approval (REOS-177). The service checks first and answers 422 with the guard and text below; these triggers are the backstop for any other writer (DbRule 422).
--   Rule                                                                  Service guard                 Trigger message (the text a direct write gets)
--   1  A requester cannot approve or reject their own change              ChangeSelfApproval            Separation of duties: you cannot approve or reject a change you requested
--   2  The CAB chair cannot be the only approver of a change they raised  ChangeChairSoleApprover       Separation of duties: the CAB chair cannot be the only approver of a change they raised; a second approver is needed
--      (raised = requested it, or keyed it in: CreatedByUserId)
--   3  An Emergency change still needs a second person: it may be          ChangeEmergencyNeedsReview    Separation of duties: an Emergency change needs a review by a second person before it can be closed
--      implemented before approval, the review is then recorded as a
--      retroactive decision (CabDecisions.Retroactive = 1) and the change
--      cannot be closed without one by someone other than the requester
-- Rule 1 is enforced twice: on the decision being recorded (CabDecisions insert) and on the change moving to Approved or Rejected (its decider and last editor). Rules 2 and 3 are enforced on the change's status move.
-- Status moves: Draft > Submitted > Approved | Rejected | Cancelled; Approved > Implemented | Cancelled; Implemented > Closed; Draft > Cancelled; Submitted > Implemented only for Emergency. Rejected, Closed and Cancelled are final.
CREATE TRIGGER trg_Ws_ChangeRequests_Insert BEFORE INSERT ON ChangeRequests
BEGIN
    SELECT RAISE(ABORT, 'The release belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The Go/No-Go decision belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM GoNoGoDecisions g JOIN ReleaseTrains x ON x.Id = g.ReleaseTrainId WHERE g.Id = NEW.GoNoGoDecisionId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The Go/No-Go decision belongs to another release')
     WHERE NEW.ReleaseTrainId IS NOT NULL AND EXISTS (SELECT 1 FROM GoNoGoDecisions g WHERE g.Id = NEW.GoNoGoDecisionId AND g.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The requester is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The person who created the change is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.CreatedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ChangeRequests_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId, GoNoGoDecisionId, RequestedByUserId, CreatedByUserId ON ChangeRequests
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The release belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The Go/No-Go decision belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM GoNoGoDecisions g JOIN ReleaseTrains x ON x.Id = g.ReleaseTrainId WHERE g.Id = NEW.GoNoGoDecisionId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The Go/No-Go decision belongs to another release')
     WHERE NEW.ReleaseTrainId IS NOT NULL AND EXISTS (SELECT 1 FROM GoNoGoDecisions g WHERE g.Id = NEW.GoNoGoDecisionId AND g.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The requester is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The person who created the change is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.CreatedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CabMeetings_Insert BEFORE INSERT ON CabMeetings
BEGIN
    SELECT RAISE(ABORT, 'The chair is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.ChairUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CabMeetings_Update BEFORE UPDATE OF WorkspaceId, ChairUserId ON CabMeetings
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The chair is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.ChairUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CabAgendaItems_Insert BEFORE INSERT ON CabAgendaItems
BEGIN
    SELECT RAISE(ABORT, 'The change belongs to another workspace than the meeting')
     WHERE EXISTS (SELECT 1 FROM CabMeetings m JOIN ChangeRequests c ON c.Id = NEW.ChangeRequestId WHERE m.Id = NEW.MeetingId AND c.WorkspaceId IS NOT m.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CabAgendaItems_Update BEFORE UPDATE OF MeetingId, ChangeRequestId ON CabAgendaItems
BEGIN
    SELECT RAISE(ABORT, 'An agenda item keeps its meeting and its change')
     WHERE NEW.MeetingId IS NOT OLD.MeetingId OR NEW.ChangeRequestId IS NOT OLD.ChangeRequestId;
END;
CREATE TRIGGER trg_Ws_CabAttendees_Insert BEFORE INSERT ON CabAttendees
BEGIN
    SELECT RAISE(ABORT, 'The attendee is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM CabMeetings m JOIN WorkspaceMembers wm ON wm.WorkspaceId = m.WorkspaceId WHERE m.Id = NEW.MeetingId AND wm.UserId = NEW.UserId);
END;
CREATE TRIGGER trg_Ws_CabAttendees_Update BEFORE UPDATE OF MeetingId, UserId ON CabAttendees
BEGIN
    SELECT RAISE(ABORT, 'An attendee row keeps its meeting and its person')
     WHERE NEW.MeetingId IS NOT OLD.MeetingId OR NEW.UserId IS NOT OLD.UserId;
END;
CREATE TRIGGER trg_Ws_CabDecisions_Insert BEFORE INSERT ON CabDecisions
BEGIN
    SELECT RAISE(ABORT, 'The meeting belongs to another workspace than the change')
     WHERE EXISTS (SELECT 1 FROM CabMeetings m JOIN ChangeRequests c ON c.Id = NEW.ChangeRequestId WHERE m.Id = NEW.MeetingId AND m.WorkspaceId IS NOT c.WorkspaceId);
    SELECT RAISE(ABORT, 'The decider is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM ChangeRequests c JOIN WorkspaceMembers wm ON wm.WorkspaceId = c.WorkspaceId WHERE c.Id = NEW.ChangeRequestId AND wm.UserId = NEW.DecidedByUserId);
END;

-- Append-only decisions, and a change request is never deleted
CREATE TRIGGER trg_CabDecision_NoUpdate BEFORE UPDATE ON CabDecisions BEGIN SELECT RAISE(ABORT, 'CAB decisions are immutable'); END;
CREATE TRIGGER trg_CabDecision_NoDelete BEFORE DELETE ON CabDecisions BEGIN SELECT RAISE(ABORT, 'CAB decisions are immutable'); END;
CREATE TRIGGER trg_Change_NoDelete BEFORE DELETE ON ChangeRequests BEGIN SELECT RAISE(ABORT, 'A change request is kept for the record; cancel it instead'); END;

-- Meeting: legal status moves only
CREATE TRIGGER trg_CabMeeting_Transition BEFORE UPDATE OF Status ON CabMeetings
WHEN NEW.Status IS NOT OLD.Status
 AND NOT ((OLD.Status = 'Planned' AND NEW.Status IN ('InProgress','Cancelled')) OR (OLD.Status = 'InProgress' AND NEW.Status IN ('Completed','Cancelled')))
BEGIN SELECT RAISE(ABORT, 'Illegal CAB meeting status move'); END;

-- Change: legal status moves only (generic, so it is written before the specific rules below)
CREATE TRIGGER trg_Change_Transition BEFORE UPDATE OF Status ON ChangeRequests
WHEN NEW.Status IS NOT OLD.Status
 AND NOT ((OLD.Status = 'Draft' AND NEW.Status IN ('Submitted','Cancelled'))
       OR (OLD.Status = 'Submitted' AND NEW.Status IN ('Approved','Rejected','Cancelled','Implemented'))
       OR (OLD.Status = 'Approved' AND NEW.Status IN ('Implemented','Cancelled'))
       OR (OLD.Status = 'Implemented' AND NEW.Status = 'Closed'))
BEGIN SELECT RAISE(ABORT, 'Illegal change request status move'); END;

CREATE TRIGGER trg_Change_OnlyEmergencyBeforeApproval BEFORE UPDATE OF Status ON ChangeRequests
WHEN OLD.Status = 'Submitted' AND NEW.Status = 'Implemented' AND NEW.ChangeType <> 'Emergency'
BEGIN SELECT RAISE(ABORT, 'Only an Emergency change can be implemented before it is approved'); END;

-- A change is approved or rejected by a decision that is on record, made by the person who is named as its decider
CREATE TRIGGER trg_Change_DecisionOnRecord BEFORE UPDATE OF Status ON ChangeRequests
WHEN NEW.Status IN ('Approved','Rejected') AND OLD.Status = 'Submitted'
 AND NOT EXISTS (SELECT 1 FROM CabDecisions d WHERE d.ChangeRequestId = NEW.Id AND d.Retroactive = 0 AND d.Decision = NEW.Status AND d.DecidedByUserId IS NEW.DecidedByUserId)
BEGIN SELECT RAISE(ABORT, 'A change is approved or rejected by a recorded CAB decision'); END;

-- REOS-177 rule 3: an Emergency change cannot be closed until someone other than the requester has reviewed it
CREATE TRIGGER trg_Change_EmergencyNeedsReview BEFORE UPDATE OF Status ON ChangeRequests
WHEN NEW.Status = 'Closed' AND OLD.Status IS NOT NEW.Status AND NEW.ChangeType = 'Emergency'
 AND NOT EXISTS (SELECT 1 FROM CabDecisions d WHERE d.ChangeRequestId = NEW.Id AND d.Decision IN ('Approved','Rejected') AND d.DecidedByUserId IS NOT NEW.RequestedByUserId)
BEGIN SELECT RAISE(ABORT, 'Separation of duties: an Emergency change needs a review by a second person before it can be closed'); END;

-- REOS-177 rule 2: the chair (of a meeting that has the change on its agenda, or that recorded the decision) who raised the change is not its only approver
CREATE TRIGGER trg_Change_ChairNotSoleApprover BEFORE UPDATE OF Status ON ChangeRequests
WHEN NEW.Status = 'Approved' AND OLD.Status IS NOT NEW.Status
 AND EXISTS (SELECT 1 FROM CabDecisions d JOIN CabMeetings m ON (m.Id = d.MeetingId OR EXISTS (SELECT 1 FROM CabAgendaItems a WHERE a.MeetingId = m.Id AND a.ChangeRequestId = NEW.Id))
              WHERE d.ChangeRequestId = NEW.Id AND d.Decision = 'Approved' AND d.Retroactive = 0 AND m.ChairUserId = d.DecidedByUserId
                AND (d.DecidedByUserId IS NEW.CreatedByUserId OR d.DecidedByUserId IS NEW.RequestedByUserId))
 AND NOT EXISTS (SELECT 1 FROM CabDecisions d2 WHERE d2.ChangeRequestId = NEW.Id AND d2.Decision = 'Approved' AND d2.Retroactive = 0
                  AND d2.DecidedByUserId IS NOT NEW.CreatedByUserId AND d2.DecidedByUserId IS NOT NEW.RequestedByUserId)
BEGIN SELECT RAISE(ABORT, 'Separation of duties: the CAB chair cannot be the only approver of a change they raised; a second approver is needed'); END;

-- REOS-177 rule 1 on the change row: whoever moves it to Approved or Rejected (the decider, and the last editor) is not the requester
CREATE TRIGGER trg_Change_Separation BEFORE UPDATE OF Status, DecidedByUserId, LastChangedByUserId ON ChangeRequests
WHEN NEW.Status IN ('Approved','Rejected') AND OLD.Status IS NOT NEW.Status
 AND (NEW.DecidedByUserId IS NEW.RequestedByUserId OR NEW.LastChangedByUserId IS NEW.RequestedByUserId)
BEGIN SELECT RAISE(ABORT, 'Separation of duties: you cannot approve or reject a change you requested'); END;

-- A decision is recorded while its change waits for the CAB; a retroactive review only for an Emergency change that has been implemented
CREATE TRIGGER trg_CabDecision_ChangeState BEFORE INSERT ON CabDecisions
BEGIN
    SELECT RAISE(ABORT, 'A decision is recorded while the change is waiting for the CAB (Submitted)')
     WHERE NEW.Retroactive = 0 AND NOT EXISTS (SELECT 1 FROM ChangeRequests c WHERE c.Id = NEW.ChangeRequestId AND c.Status = 'Submitted');
    SELECT RAISE(ABORT, 'A retroactive review is only for an Emergency change that has been implemented')
     WHERE NEW.Retroactive = 1 AND NOT EXISTS (SELECT 1 FROM ChangeRequests c WHERE c.Id = NEW.ChangeRequestId AND c.ChangeType = 'Emergency' AND c.Status = 'Implemented');
END;

-- REOS-177 rule 1 on the decision: the requester cannot approve or reject their own change (written last, so it is the message reported first)
CREATE TRIGGER trg_CabDecision_Separation BEFORE INSERT ON CabDecisions
WHEN NEW.Decision IN ('Approved','Rejected')
BEGIN
    SELECT RAISE(ABORT, 'Separation of duties: you cannot approve or reject a change you requested')
     WHERE EXISTS (SELECT 1 FROM ChangeRequests c WHERE c.Id = NEW.ChangeRequestId AND c.RequestedByUserId IS NEW.DecidedByUserId);
END;
-- <<< REOS-175 + REOS-177

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261126134858_Schema', '20261126134908_Triggers');   -- CHAIN 2
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES   -- CHAIN 3
    ('20261126225422_Schema', '10.0.12'),
    ('20261126225440_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
