# Workstream lifecycle walkthrough

This doc walks through the full lifecycle of a single workstream as a sequence of SQL operations, plus the major branch cases. It serves as both documentation and the SQL skeleton for the stored procedures listed in the implementation kickoff: `sp_OpenPeriod`, `sp_AcquireLock`, `sp_SubmitWorkstream`, `sp_OpenForReview`, `sp_ApproveChecklistItem`, `sp_AdvanceStage`, `sp_SendBackToStage`, `sp_ApproveFinal`, `sp_ClearLock`, `sp_RebuildWorkstream`, and `sp_RefreshChecklistFromTemplate`.

The running example is **Plaza Tower's debt-service review for the October 2025 close**, with a three-stage chain:

- **Stage 0:** Preparer — Maya (UserId=12, role: Preparer, RoleId=1)
- **Stage 1:** Treasury review — Erin (UserId=7, role: Treasury-RE, RoleId=4) — non-final
- **Stage 2:** Senior review — David (UserId=4, role: Senior, RoleId=8) — final

A three-stage example is deliberately chosen over two stages: it exercises `CurrentStageIndex` advancement, the per-stage checklist transition, and `IsFinalApproval` on a non-terminal-position stage configuration (although here Senior is both terminal and final).

## Setup assumptions

- `User` rows for Maya, Erin, David, an admin user, and a `System` user (UserId=1) for automated actions
- `Entity` row for Plaza Tower (EntityId=42, EntityTypeId for RealEstateAsset)
- `Role` rows for Preparer (1), Treasury-RE (4), Senior (8)
- `EntityRoleAssignment`: Maya as Preparer for Plaza Tower; Erin as Treasury-RE for Plaza Tower; David as Senior for Plaza Tower
- A current `WorkflowTemplate` for RealEstateAsset (`IsCurrent = 1`, Version = 7) containing a `WorkstreamDef` for debt service (WorkstreamDefId=27) with three `WorkstreamDefStage` rows:
  - `WorkstreamDefStage` for stage 0: RoleId=1, StageKind='Prepare', IsFinalApproval=0
  - `WorkstreamDefStage` for stage 1: RoleId=4, StageKind='Review', IsFinalApproval=0, StuckThresholdHours=24
  - `WorkstreamDefStage` for stage 2: RoleId=8, StageKind='Review', IsFinalApproval=1, StuckThresholdHours=72
- Each stage has its own `WorkstreamDefChecklistItem` rows (4 items for Treasury, 6 for Senior; the preparer "checklist" is the stage-1 list shown as a prep guide)

## Step 1: Period opens — workstreams instantiate

When the October close opens (admin action or scheduled job), the system creates one `ClosePeriod` per active entity, then materializes workstreams from the **current** template for each entity's type. There is no version resolution by date — `IsCurrent = 1` is the answer.

```sql
-- Open the period for Plaza Tower
INSERT INTO ClosePeriod (EntityId, Period, OpenedByUserId)
VALUES (42, '202510', 1);
-- @ClosePeriodId = 1001

-- Resolve the current template for RealEstateAsset
DECLARE @TemplateId bigint = (
    SELECT WorkflowTemplateId FROM WorkflowTemplate
    WHERE EntityTypeId = (SELECT EntityTypeId FROM Entity WHERE EntityId = 42)
      AND IsCurrent = 1
      AND IsDeleted = 0
);

-- Materialize the debt-service workstream from that template's def
INSERT INTO Workstream (
    ClosePeriodId, Period, EntityId, WorkstreamDefId,
    Code, Name, OrderIndex,
    Status, Round, CurrentStageIndex,
    CreatedByUserId
)
VALUES (
    1001, '202510', 42, 27,
    'DEBT_SVC', 'Debt service review', 4,
    'NotStarted', 1, 0,
    1
);
-- @WorkstreamId = 5042

-- Snapshot the stage chain. One WorkstreamStage row per WorkstreamDefStage.
INSERT INTO WorkstreamStage (
    WorkstreamId, SourceDefStageId,
    OrderIndex, RoleId, StageKind, DisplayName,
    IsFinalApproval, StuckThresholdHours,
    EnteredAtUtc
)
SELECT 5042, WorkstreamDefStageId,
       OrderIndex, RoleId, StageKind, DisplayName,
       IsFinalApproval, StuckThresholdHours,
       CASE WHEN OrderIndex = 0 THEN SYSUTCDATETIME() ELSE NULL END
FROM WorkstreamDefStage
WHERE WorkstreamDefId = 27
ORDER BY OrderIndex;

-- Clone checklist items, scoped to their stage.
INSERT INTO ChecklistItem (
    WorkstreamId, WorkstreamStageId, SourceDefItemId,
    AddedByUserId, OrderIndex, Text
)
SELECT 5042, ws.WorkstreamStageId, di.WorkstreamDefChecklistItemId,
       1, di.OrderIndex, di.Text
FROM WorkstreamDefChecklistItem di
INNER JOIN WorkstreamDefStage ds
    ON ds.WorkstreamDefStageId = di.WorkstreamDefStageId
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = 5042
   AND ws.SourceDefStageId = ds.WorkstreamDefStageId
WHERE ds.WorkstreamDefId = 27;

-- Audit
INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
VALUES (
    1, '...', 'Workstream', 5042,
    5042, '202510', 42, 'Instantiated',
    '{"workstreamDef": "DEBT_SVC", "templateId": <TemplateId>, "templateVersion": 7, "stageCount": 3}'
);
```

The workstream now exists in `NotStarted` state with `CurrentStageIndex = 0`. Stage 0's `EnteredAtUtc` is set (the work has reached the preparer); stages 1 and 2 are seeded but their per-stage timestamps remain null until the workstream advances. Maya sees the workstream on her Dashboard as a "not started" tile.

## Step 2: Maya opens the workstream — lock acquisition

Opening doesn't yet change `Status` — that flips only when she actually uploads or modifies something. But it does acquire a lock. The lock acquisition rule resolves the role for the *current stage* via `WorkstreamStage[CurrentStageIndex]`, replacing the old two-role CASE expression.

```sql
-- Body of sp_AcquireLock(@WorkstreamId, @UserId, @LockMinutes)
UPDATE w
SET LockedByUserId = @UserId,
    LockedAtUtc = SYSUTCDATETIME(),
    LockExpiresAtUtc = DATEADD(MINUTE, @LockMinutes, SYSUTCDATETIME())
FROM Workstream w
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = w.WorkstreamId
   AND ws.OrderIndex = w.CurrentStageIndex
   AND ws.IsDeleted = 0
WHERE w.WorkstreamId = @WorkstreamId
  AND w.IsDeleted = 0
  AND (w.LockedByUserId IS NULL
       OR w.LockedByUserId = @UserId
       OR w.LockExpiresAtUtc < SYSUTCDATETIME())
  AND EXISTS (
    SELECT 1 FROM EntityRoleAssignment era
    WHERE era.EntityId = w.EntityId
      AND era.UserId = @UserId
      AND era.RoleId = ws.RoleId
      AND era.IsDeleted = 0
  );
-- @@ROWCOUNT = 1: lock acquired. 0: someone else has it, or wrong role for current stage.
```

The `EXISTS` clause is the key rule: a user can only acquire the lock if they have the role assignment for the *current stage's* role on this workstream's entity. When Maya opens the workstream while it sits at `CurrentStageIndex = 0` (Preparer stage), her Preparer role on Plaza Tower satisfies the check. After submission, `CurrentStageIndex` advances to 1 (Treasury) and Maya can no longer acquire the lock — but Erin can.

## Step 3: Maya uploads the primary file

She drops the debt schedule export. The app uploads to SharePoint and writes a row.

```sql
BEGIN TRAN;

INSERT INTO WorkstreamFile (
    WorkstreamId, FileRole,
    SpDriveId, SpItemId, SpWebUrl, SpRelativePath,
    FileName, FileExtension, SizeBytes,
    UploadedByUserId
)
VALUES (
    5042, 'Primary',
    'b!abc...', '01XYZ...', 'https://contoso.sharepoint.com/...',
    'PlazaTower/202510/DEBT_SVC/Plaza_Tower_DebtService_Oct.xlsx',
    'Plaza_Tower_DebtService_Oct.xlsx', 'xlsx', 184320,
    12
);
-- @WorkstreamFileId = 9001

-- First file means NotStarted → InProgress. Set workstream-level start
-- and stage-0 start in the same transaction.
UPDATE Workstream
SET Status = 'InProgress',
    StartedAtUtc = SYSUTCDATETIME(),
    LockExpiresAtUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME())
WHERE WorkstreamId = 5042 AND Status = 'NotStarted';

UPDATE WorkstreamStage
SET StartedAtUtc = SYSUTCDATETIME(),
    StartedByUserId = 12
WHERE WorkstreamId = 5042
  AND OrderIndex = 0
  AND StartedAtUtc IS NULL;

INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
VALUES
    (12, '...', 'WorkstreamFile', 9001, 5042, '202510', 42, 'FileUploaded',
     '{"fileRole": "Primary", "fileName": "Plaza_Tower_DebtService_Oct.xlsx", "size": 184320}'),
    (12, '...', 'Workstream', 5042, 5042, '202510', 42, 'StatusChanged',
     '{"from": "NotStarted", "to": "InProgress", "stageIndex": 0}');

COMMIT;
```

The status transition is conditional (`AND Status = 'NotStarted'`) so subsequent file uploads don't re-fire the InProgress transition. The stage-0 `StartedAtUtc` uses the same `IS NULL` guard so re-opening doesn't reset the stage timer.

## Step 4: Maya works the prep checklist (stage-1 items as a guide)

The preparer doesn't have her own checklist — she sees stage 1's checklist (Treasury's verifications) as a prep guide, with `PreparerStatus` tracking whether she considers each item ready. `PreparerStatus` is meaningful only on stage-1 items.

```sql
-- Mark a stage-1 item as ready from the preparer's perspective
UPDATE ChecklistItem
SET PreparerStatus = 'Ready',
    PreparerMarkedAtUtc = SYSUTCDATETIME(),
    PreparerMarkedByUserId = 12
WHERE ChecklistItemId = 7102
  AND WorkstreamId = 5042
  AND WorkstreamStageId = (SELECT WorkstreamStageId FROM WorkstreamStage
                           WHERE WorkstreamId = 5042 AND OrderIndex = 1);

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'ChecklistItem', 7102,
        5042, '202510', 42, 'PreparerMarkedReady',
        '{"itemText": "Senior loan principal & interest match amort"}');

-- Add a note on that item
INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7102, 12, 'Confirmed against amort table. Final payment is March 2027.');

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'Comment', @@IDENTITY,
        5042, '202510', 42, 'CommentPosted',
        '{"checklistItemId": 7102, "kind": "PreparerNote"}');

-- Add an ad-hoc item to the stage-1 checklist (preparer's note for Treasury)
INSERT INTO ChecklistItem (
    WorkstreamId, WorkstreamStageId, SourceDefItemId,
    AddedByUserId, OrderIndex, Text
)
VALUES (5042,
        (SELECT WorkstreamStageId FROM WorkstreamStage
         WHERE WorkstreamId = 5042 AND OrderIndex = 1),
        NULL, 12, 7,
        'Verify hurricane insurance reserve adjustment');

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'ChecklistItem', @@IDENTITY,
        5042, '202510', 42, 'ChecklistItemAdded',
        '{"text": "Verify hurricane insurance reserve adjustment", "addedBy": "Preparer", "stageIndex": 1}');
```

`SourceDefItemId IS NULL` marks the item as ad-hoc. The reviewer at stage 1 sees it with an "added by preparer" badge.

Note that the preparer can only add items to **stage 1** — the stage she's about to submit to. She can't pre-stage items for Senior. If she wants Senior to verify something specific, she leaves a comment on the workstream itself or an item that will surface at stage 2.

## Step 5: Maya submits — advances to stage 1

```sql
-- Body of sp_SubmitWorkstream(@WorkstreamId, @UserId)
BEGIN TRAN;

-- Validate primary file exists
DECLARE @PrimaryCount int = (
    SELECT COUNT(*) FROM WorkstreamFile
    WHERE WorkstreamId = @WorkstreamId
      AND FileRole = 'Primary'
      AND IsDeleted = 0
);
IF @PrimaryCount < 1 BEGIN ROLLBACK; THROW 50001, 'Primary file required', 1; END;

-- Validate workstream is at stage 0 in InProgress (the only valid submit state)
DECLARE @CurStage int;
SELECT @CurStage = CurrentStageIndex FROM Workstream
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress';
IF @CurStage IS NULL OR @CurStage <> 0 BEGIN
    ROLLBACK; THROW 50002, 'Workstream not in submittable state', 1;
END;

-- Stamp stage 0 as completed, Outcome=Advanced
UPDATE WorkstreamStage
SET CompletedAtUtc = SYSUTCDATETIME(),
    CompletedByUserId = @UserId,
    Outcome = 'Advanced'
WHERE WorkstreamId = @WorkstreamId AND OrderIndex = 0;

-- Advance current stage pointer; stamp stage 1's EnteredAtUtc; release lock
UPDATE Workstream
SET CurrentStageIndex = 1,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress';

UPDATE WorkstreamStage
SET EnteredAtUtc = SYSUTCDATETIME()
WHERE WorkstreamId = @WorkstreamId
  AND OrderIndex = 1
  AND EnteredAtUtc IS NULL;

INSERT INTO AuditEvent (...)
VALUES (@UserId, '...', 'Workstream', @WorkstreamId,
        @WorkstreamId, '202510', 42, 'Submitted',
        '{"fromStage": 0, "toStage": 1, "round": 1}',
        '{"checklistReady": "5 of 7"}');

COMMIT;
```

`Status` stays `InProgress` — the multi-stage refactor collapsed `Submitted` and `InReview` into the single `InProgress` state, with the stage pointer answering "where in the flow." The workstream is now visible to anyone with the Treasury-RE role for Plaza Tower; its aging-at-current-stage timer starts from `WorkstreamStage[OrderIndex=1].EnteredAtUtc`.

`Round` does **not** increment on this initial submission — Round = 1 from instantiation. Round only increments on subsequent stage-0 submissions (after a rewind to stage 0).

## Step 6: Erin opens the workstream

Erin acquires the lock via the same `sp_AcquireLock` from Step 2; the `EXISTS` check now resolves against `WorkstreamStage[CurrentStageIndex=1].RoleId = 4` (Treasury-RE), which Erin satisfies.

`Status` does not change on lock acquisition — it's already `InProgress`. There's also no separate `OpenedForReview` event in the multi-stage model; the audit row is just `LockAcquired`. (`sp_OpenForReview` exists in the implementation kickoff list as an alias / convenience wrapper for "lock + observe entry into review-kind stage" but does no state change beyond the lock itself.)

```sql
INSERT INTO AuditEvent (...)
VALUES (7, '...', 'Workstream', 5042,
        5042, '202510', 42, 'LockAcquired',
        '{"stageIndex": 1, "stageRole": "Treasury-RE"}');
```

## Step 7: Erin works the stage-1 checklist

Erin sees only the stage-1 checklist items at the workstream view. Stage 0's "checklist" (which was just stage 1 with `PreparerStatus`) is now historical from her perspective — she sees Maya's `PreparerStatus = 'Ready'` markings on the same items as a hint, but her own `ReviewerStatus` decisions are independent.

```sql
-- Body of sp_ApproveChecklistItem(@ChecklistItemId, @UserId)
UPDATE ChecklistItem
SET ReviewerStatus = 'Approved',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = @UserId
WHERE ChecklistItemId = @ChecklistItemId
  AND IsDeleted = 0;

INSERT INTO AuditEvent (...) VALUES (@UserId, ..., 'ReviewerApproved', ...);
```

Flagging an item plus comment in one transaction (the design rule that prevents flagged items without explanations):

```sql
BEGIN TRAN;

UPDATE ChecklistItem
SET ReviewerStatus = 'NeedsRevision',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = 7
WHERE ChecklistItemId = 7103;

INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7103, 7,
        'Mezz interest jumped from 44k to 52k. Rate change in the loan, or accrual error?');

INSERT INTO AuditEvent (...) VALUES
    (7, ..., 'ReviewerFlagged', ...),
    (7, ..., 'CommentPosted', ...);

COMMIT;
```

## Step 8: Erin sends back to Maya — rewind to stage 0

The schema supports rewinding to any earlier stage. Erin chooses stage 0 (back to preparer) since the issue is a calculation question Maya needs to address. The stored procedure parameterizes the target stage; the UI defaults to `CurrentStageIndex - 1` but lets the reviewer pick.

```sql
-- Body of sp_SendBackToStage(@WorkstreamId, @UserId, @Reason)
-- Send-back always goes exactly one step back (CurrentStageIndex--).
-- No target-stage parameter: the destination is always N-1.
-- Stage 0 (Preparer) cannot call this procedure -- the UI has no send-back button at stage 0.
BEGIN TRAN;

DECLARE @CurStage int, @Round int;
SELECT @CurStage = CurrentStageIndex, @Round = Round
FROM Workstream
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress' AND IsDeleted = 0;

IF @CurStage IS NULL BEGIN ROLLBACK; THROW 50003, 'Workstream not InProgress', 1; END;
IF @CurStage = 0 BEGIN ROLLBACK; THROW 50004, 'Cannot send back from stage 0', 1; END;

DECLARE @TargetStage int = @CurStage - 1;

-- Stamp the sending stage with its outcome; preserve timestamps for audit.
UPDATE WorkstreamStage
SET CompletedAtUtc = SYSUTCDATETIME(),
    CompletedByUserId = @UserId,
    Outcome = 'SentBack'
WHERE WorkstreamId = @WorkstreamId AND OrderIndex = @CurStage;

-- Clear the target stage's prior Outcome/CompletedAt so it re-enters cleanly.
-- EnteredAtUtc and StartedAtUtc are preserved for audit history of the prior visit.
UPDATE WorkstreamStage
SET Outcome = NULL,
    CompletedAtUtc = NULL,
    CompletedByUserId = NULL
WHERE WorkstreamId = @WorkstreamId AND OrderIndex = @TargetStage;

-- Decrement stage pointer; increment Round; flip Status; release lock.
UPDATE Workstream
SET Status = 'NeedsRevision',
    CurrentStageIndex = @TargetStage,
    Round = Round + 1,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress';

INSERT INTO AuditEvent (...)
VALUES (@UserId, '...', 'Workstream', @WorkstreamId,
        @WorkstreamId, '202510', 42, 'SentBack',
        '{"fromStage": ' + CAST(@CurStage AS varchar) + ', "toStage": ' + CAST(@TargetStage AS varchar) + ', "round": ' + CAST(@Round + 1 AS varchar) + ', "reason": "' + @Reason + '"}');

COMMIT;
```

A few things worth noting about the send-back contract:

- **Send-back is always one step (N-1).** There is no target-stage parameter. The destination is always `CurrentStageIndex - 1`. This keeps the procedure simple and the UI unambiguous.
- **Round increments on every send-back**, not just when work reaches stage 0. `Round` is now a true count of how many times any reviewer pushed back — more useful for the "Round ≥ 4" attention condition in Active Workflows.
- **Per-stage timestamps are preserved** even though `Outcome` is cleared on the target stage. The audit log + preserved timestamps reconstruct what happened. `WorkstreamStage` reflects current state; `AuditEvent` carries full history.
- **`RewoundToStageIndex` has been removed from the schema.** One-step-only send-back makes it redundant.

The flagged checklist item stays in `ReviewerStatus = 'NeedsRevision'` — it doesn't reset to `Pending`. When Maya looks at the workstream, that item carries Erin's flag and comment.

## Step 9: Maya addresses the feedback and resubmits — Round 2

Maya re-acquires the lock (now `CurrentStageIndex = 0` again, so her Preparer role qualifies). She replies on the flagged item, replaces the file, and resubmits.

```sql
-- Reply on the flagged item
INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7103, 12,
        'Rate stepped up per loan agreement section 4.2. Updated v4 reflects new rate from Oct 1.');

-- Replace the file (soft-delete + insert with ReplacesFileId)
BEGIN TRAN;

UPDATE WorkstreamFile
SET IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), DeletedByUserId = 12
WHERE WorkstreamFileId = 9001;

INSERT INTO WorkstreamFile (
    WorkstreamId, FileRole, SpDriveId, SpItemId, SpWebUrl, SpRelativePath,
    FileName, FileExtension, SizeBytes, UploadedByUserId, ReplacesFileId
)
VALUES (
    5042, 'Primary', 'b!abc...', '01ABC...', 'https://...',
    'PlazaTower/202510/DEBT_SVC/Plaza_Tower_DebtService_Oct_v4.xlsx',
    'Plaza_Tower_DebtService_Oct_v4.xlsx', 'xlsx', 187200,
    12, 9001
);
-- @NewWorkstreamFileId = 9015

INSERT INTO AuditEvent (...) VALUES
    (12, ..., 'FileReplaced', '{"oldFileId": 9001, "newFileId": 9015}');

COMMIT;
```

Resubmit. Stage 0 has been visited twice now; we increment `Round` and re-stamp stage 0 as Advanced with the new completion timestamp. Stage 1 re-enters from `NeedsRevision`.

```sql
-- Body of sp_SubmitWorkstream(...) called again — same proc, NeedsRevision branch
BEGIN TRAN;

-- Stamp stage 0 with this round's completion
UPDATE WorkstreamStage
SET CompletedAtUtc = SYSUTCDATETIME(),
    CompletedByUserId = 12,
    Outcome = 'Advanced'
WHERE WorkstreamId = 5042 AND OrderIndex = 0;

-- Advance pointer; flip Status back to InProgress.
-- Round was already incremented when Erin pressed 'Needs revision' in Step 8.
UPDATE Workstream
SET Status = 'InProgress',
    CurrentStageIndex = 1,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042 AND Status = 'NeedsRevision';

-- Re-stamp stage 1's EnteredAtUtc for the new visit. (StartedAtUtc cleared
-- on rewind so the next reviewer's start timer is fresh.)
UPDATE WorkstreamStage
SET EnteredAtUtc = SYSUTCDATETIME()
WHERE WorkstreamId = 5042 AND OrderIndex = 1;

INSERT INTO AuditEvent (...)
VALUES (12, ..., 'Resubmitted', '{"round": ' + CAST(Round AS varchar) + ', "fromStage": 0, "toStage": 1}');

COMMIT;
```

The replace pattern (soft-delete + insert with `ReplacesFileId`) gives a clean version chain. An auditor can ask "what did Maya submit at each round?" and get a definitive answer by walking `ReplacesFileId` and joining `AuditEvent` for round labels.

## Step 10: Erin reviews round 2 and advances to stage 2

Erin re-acquires the lock, resolves the flagged item, and approves. With all stage-1 items now `Approved`, she advances the workstream to stage 2 (Senior).

Note: at non-final stages, "approving" means **advancing**, not finalizing. The stored procedure name is `sp_AdvanceStage`; the act is "I'm done at my stage, send it forward."

```sql
-- Resolve the previously flagged item
UPDATE ChecklistItem
SET ReviewerStatus = 'Approved',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = 7
WHERE ChecklistItemId = 7103;

-- Body of sp_AdvanceStage(@WorkstreamId, @UserId)
BEGIN TRAN;

DECLARE @CurStage int, @StageRowId bigint, @IsFinal bit;
SELECT @CurStage = w.CurrentStageIndex,
       @StageRowId = ws.WorkstreamStageId,
       @IsFinal = ws.IsFinalApproval
FROM Workstream w
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = w.WorkstreamId
   AND ws.OrderIndex = w.CurrentStageIndex
   AND ws.IsDeleted = 0
WHERE w.WorkstreamId = @WorkstreamId
  AND w.Status = 'InProgress';

IF @CurStage IS NULL BEGIN ROLLBACK; THROW 50004, 'Not in InProgress', 1; END;
IF @IsFinal = 1 BEGIN
    -- Wrong procedure: final stages call sp_ApproveFinal instead.
    ROLLBACK; THROW 50005, 'Use sp_ApproveFinal for final stage', 1;
END;

-- All checklist items at THIS STAGE must be Approved before advancing
DECLARE @Pending int = (
    SELECT COUNT(*) FROM ChecklistItem
    WHERE WorkstreamId = @WorkstreamId
      AND WorkstreamStageId = @StageRowId
      AND IsDeleted = 0
      AND ReviewerStatus <> 'Approved'
);
IF @Pending > 0 BEGIN ROLLBACK; THROW 50006, 'Items remain at current stage', 1; END;

-- Stamp current stage as completed, Outcome=Advanced
UPDATE WorkstreamStage
SET CompletedAtUtc = SYSUTCDATETIME(),
    CompletedByUserId = @UserId,
    Outcome = 'Advanced'
WHERE WorkstreamStageId = @StageRowId;

-- Move pointer forward; release lock
UPDATE Workstream
SET CurrentStageIndex = @CurStage + 1,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress';

-- Stamp the next stage's EnteredAtUtc
UPDATE WorkstreamStage
SET EnteredAtUtc = SYSUTCDATETIME()
WHERE WorkstreamId = @WorkstreamId
  AND OrderIndex = @CurStage + 1
  AND EnteredAtUtc IS NULL;

INSERT INTO AuditEvent (...)
VALUES (@UserId, '...', 'Workstream', @WorkstreamId,
        @WorkstreamId, '202510', 42, 'StageAdvanced',
        '{"fromStage": <CurStage>, "toStage": <CurStage+1>, "round": <Round>}');

COMMIT;
```

Stage-1 items at status `Approved` stay that way; they don't reset for stage 2's review. Stage 2 has its own checklist (the Senior items cloned at instantiation), and its own `ReviewerStatus` decisions to make. Stage 1's checklist is now historical from stage 2's perspective — the Senior reviewer can see it as "already reviewed by Treasury" but doesn't act on it.

## Step 11: David approves at stage 2 — workstream Approved

Stage 2 is the final-approval stage (`IsFinalApproval = 1`). When David approves all Senior checklist items and triggers final approval, the workstream transitions to `Approved` rather than advancing.

```sql
-- David approves each stage-2 item via sp_ApproveChecklistItem (same as Erin)
-- (omitted)

-- Body of sp_ApproveFinal(@WorkstreamId, @UserId)
BEGIN TRAN;

DECLARE @CurStage int, @StageRowId bigint, @IsFinal bit;
SELECT @CurStage = w.CurrentStageIndex,
       @StageRowId = ws.WorkstreamStageId,
       @IsFinal = ws.IsFinalApproval
FROM Workstream w
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = w.WorkstreamId
   AND ws.OrderIndex = w.CurrentStageIndex
   AND ws.IsDeleted = 0
WHERE w.WorkstreamId = @WorkstreamId
  AND w.Status = 'InProgress';

IF @IsFinal <> 1 BEGIN
    ROLLBACK; THROW 50007, 'Current stage is not the final-approval stage', 1;
END;

DECLARE @Pending int = (
    SELECT COUNT(*) FROM ChecklistItem
    WHERE WorkstreamId = @WorkstreamId
      AND WorkstreamStageId = @StageRowId
      AND IsDeleted = 0
      AND ReviewerStatus <> 'Approved'
);
IF @Pending > 0 BEGIN ROLLBACK; THROW 50006, 'Items remain at current stage', 1; END;

-- Stamp the final stage as completed, Outcome=Advanced
UPDATE WorkstreamStage
SET CompletedAtUtc = SYSUTCDATETIME(),
    CompletedByUserId = @UserId,
    Outcome = 'Advanced'
WHERE WorkstreamStageId = @StageRowId;

-- Transition workstream to Approved; release lock.
UPDATE Workstream
SET Status = 'Approved',
    ApprovedAtUtc = SYSUTCDATETIME(),
    ApprovedByUserId = @UserId,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = @WorkstreamId AND Status = 'InProgress';

INSERT INTO AuditEvent (...)
VALUES (@UserId, '...', 'Workstream', @WorkstreamId,
        @WorkstreamId, '202510', 42, 'Approved',
        '{"stageIndex": <CurStage>, "round": <Round>}',
        '{"totalDurationHours": 47, "rounds": <Round>}');

COMMIT;
```

The "all items at the current stage must be Approved" rule is enforced in SQL itself — the stored procedure checks `@Pending` before transitioning. This rule is non-bypassable when the application calls only the procedure.

There is no "all items across all stages must be Approved" check. Earlier stages' items are already at `Approved` (otherwise the workstream couldn't have advanced past them); the final stage's items are the only ones that gate final approval. This separation is what allows reviewers at later stages to see prior stages as "already verified" rather than re-litigating them.

## Branch case: lock contention

When a second user tries to acquire the lock on a workstream someone else holds:

```sql
-- sp_AcquireLock returns @@ROWCOUNT = 0
```

The application then runs a SELECT to determine why and shows the appropriate UI:

```sql
SELECT w.LockedByUserId,
       w.LockExpiresAtUtc,
       w.Status,
       w.CurrentStageIndex,
       ws.RoleId AS CurrentStageRoleId,
       CASE WHEN EXISTS (
           SELECT 1 FROM EntityRoleAssignment era
           WHERE era.EntityId = w.EntityId
             AND era.UserId = @UserId
             AND era.RoleId = ws.RoleId
             AND era.IsDeleted = 0
       ) THEN 1 ELSE 0 END AS HasRoleForCurrentStage
FROM Workstream w
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = w.WorkstreamId
   AND ws.OrderIndex = w.CurrentStageIndex
   AND ws.IsDeleted = 0
WHERE w.WorkstreamId = @WorkstreamId;
```

If `HasRoleForCurrentStage = 1` and `LockedByUserId` is set, the UI shows "Locked by {user}, expires in N min." If `HasRoleForCurrentStage = 0`, the UI shows "This workstream is currently with the {role} — you can't edit until it comes back" (where `{role}` is resolved via `Role.Name` from the joined stage row).

## Branch case: lock auto-expiration

A background job runs every minute or two:

```sql
DECLARE @ExpiredLocks TABLE (WorkstreamId bigint, PrevLockedBy bigint);

UPDATE Workstream
SET LockedByUserId = NULL,
    LockedAtUtc = NULL,
    LockExpiresAtUtc = NULL
OUTPUT inserted.WorkstreamId, deleted.LockedByUserId
INTO @ExpiredLocks(WorkstreamId, PrevLockedBy)
WHERE LockExpiresAtUtc < SYSUTCDATETIME()
  AND LockedByUserId IS NOT NULL;

INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
SELECT 1, '00000000-0000-0000-0000-000000000000',
       'Workstream', e.WorkstreamId,
       e.WorkstreamId, w.Period, w.EntityId,
       'LockExpired',
       CONCAT('{"prevLockedBy":', e.PrevLockedBy, ',"stageIndex":', w.CurrentStageIndex, '}')
FROM @ExpiredLocks e
INNER JOIN Workstream w ON w.WorkstreamId = e.WorkstreamId;
```

The `IX_Workstream_LockExpiry` filtered index keeps this query cheap.

## Branch case: refresh from template (additive)

When a SOX control gets added mid-period — admin edits the template and saves, creating a new version — admins can pull the additions into in-flight workstreams without restarting them. The procedure compares the workstream's existing per-stage items against the *current* template's defaults (resolved via `IsCurrent = 1`) and inserts only the missing ones.

```sql
-- Body of sp_RefreshChecklistFromTemplate(@WorkstreamId, @UserId)
BEGIN TRAN;

-- Resolve the current template's WorkstreamDef matching this workstream's def code
DECLARE @CurrentDefId bigint = (
    SELECT cdef.WorkstreamDefId
    FROM Workstream w
    INNER JOIN Entity e ON e.EntityId = w.EntityId
    INNER JOIN WorkflowTemplate ct
        ON ct.EntityTypeId = e.EntityTypeId
       AND ct.IsCurrent = 1
       AND ct.IsDeleted = 0
    INNER JOIN WorkstreamDef cdef
        ON cdef.WorkflowTemplateId = ct.WorkflowTemplateId
       AND cdef.Code = w.Code
    WHERE w.WorkstreamId = @WorkstreamId
);

IF @CurrentDefId IS NULL BEGIN
    -- Workstream's def doesn't exist in current template (e.g. removed in a save).
    -- Refresh has nothing to do; not an error.
    ROLLBACK; RETURN;
END;

-- For each WorkstreamStage on the workstream, find the corresponding stage in
-- the current def by OrderIndex, and insert any def checklist items whose Text
-- is not already present on that stage of the workstream.
INSERT INTO ChecklistItem (
    WorkstreamId, WorkstreamStageId, SourceDefItemId,
    AddedByUserId, OrderIndex, Text
)
SELECT @WorkstreamId, ws.WorkstreamStageId, cdci.WorkstreamDefChecklistItemId,
       @UserId, cdci.OrderIndex, cdci.Text
FROM WorkstreamStage ws
INNER JOIN WorkstreamDefStage cds
    ON cds.WorkstreamDefId = @CurrentDefId
   AND cds.OrderIndex = ws.OrderIndex
INNER JOIN WorkstreamDefChecklistItem cdci
    ON cdci.WorkstreamDefStageId = cds.WorkstreamDefStageId
WHERE ws.WorkstreamId = @WorkstreamId
  AND ws.IsDeleted = 0
  AND NOT EXISTS (
    SELECT 1 FROM ChecklistItem ci
    WHERE ci.WorkstreamStageId = ws.WorkstreamStageId
      AND ci.IsDeleted = 0
      AND ci.Text = cdci.Text  -- exact text match; renames treated as add
  );

DECLARE @AddedCount int = @@ROWCOUNT;

INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
SELECT @UserId, '...', 'Workstream', @WorkstreamId,
       @WorkstreamId, w.Period, w.EntityId,
       'ChecklistRefreshedFromTemplate',
       CONCAT('{"addedCount":', @AddedCount, '}')
FROM Workstream w
WHERE w.WorkstreamId = @WorkstreamId;

COMMIT;
```

Stage role changes do not propagate via this procedure — only checklist additions. Role changes require a full rebuild.

## Branch case: rebuild and restart

When a workstream is in such a bad state that admins want to start over, `sp_RebuildWorkstream` marks the old workstream `Rebuilt`, instantiates a fresh one from the *current* template (potentially a newer version than the original), and links them via `RebuiltFromWorkstreamId`.

```sql
-- Body of sp_RebuildWorkstream(@OldWorkstreamId, @UserId, @Reason)
BEGIN TRAN;

DECLARE @ClosePeriodId bigint, @Period char(6), @EntityId bigint, @Code varchar(40), @OrderIndex int;
SELECT @ClosePeriodId = ClosePeriodId,
       @Period = Period,
       @EntityId = EntityId,
       @Code = Code,
       @OrderIndex = OrderIndex
FROM Workstream
WHERE WorkstreamId = @OldWorkstreamId AND Status NOT IN ('Approved', 'Rebuilt');

-- Mark old as Rebuilt (Status, not IsDeleted — keep visible in audit views)
UPDATE Workstream
SET Status = 'Rebuilt',
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = @OldWorkstreamId;

-- Resolve the current def matching by code
DECLARE @CurrentDefId bigint = (
    SELECT cdef.WorkstreamDefId
    FROM Entity e
    INNER JOIN WorkflowTemplate ct
        ON ct.EntityTypeId = e.EntityTypeId
       AND ct.IsCurrent = 1
       AND ct.IsDeleted = 0
    INNER JOIN WorkstreamDef cdef
        ON cdef.WorkflowTemplateId = ct.WorkflowTemplateId
       AND cdef.Code = @Code
    WHERE e.EntityId = @EntityId
);

IF @CurrentDefId IS NULL BEGIN
    -- Code no longer exists in current template — admin should recreate manually.
    ROLLBACK; THROW 50008, 'Workstream code not in current template; cannot rebuild', 1;
END;

-- Create fresh workstream pointing back via RebuiltFromWorkstreamId
INSERT INTO Workstream (
    ClosePeriodId, Period, EntityId, WorkstreamDefId,
    Code, Name, OrderIndex,
    Status, Round, CurrentStageIndex,
    RebuiltFromWorkstreamId, CreatedByUserId
)
SELECT @ClosePeriodId, @Period, @EntityId, @CurrentDefId,
       cdef.Code, cdef.Name, @OrderIndex,
       'NotStarted', 1, 0,
       @OldWorkstreamId, @UserId
FROM WorkstreamDef cdef
WHERE cdef.WorkstreamDefId = @CurrentDefId;
DECLARE @NewWorkstreamId bigint = SCOPE_IDENTITY();

-- Snapshot stages from the current def (same pattern as instantiation in Step 1)
INSERT INTO WorkstreamStage (...)
SELECT @NewWorkstreamId, WorkstreamDefStageId, OrderIndex, RoleId, StageKind,
       DisplayName, IsFinalApproval, StuckThresholdHours,
       CASE WHEN OrderIndex = 0 THEN SYSUTCDATETIME() ELSE NULL END
FROM WorkstreamDefStage
WHERE WorkstreamDefId = @CurrentDefId
ORDER BY OrderIndex;

-- Clone checklist items from current def, scoped to new stage rows
INSERT INTO ChecklistItem (...)
SELECT @NewWorkstreamId, ws.WorkstreamStageId, di.WorkstreamDefChecklistItemId,
       @UserId, di.OrderIndex, di.Text
FROM WorkstreamDefChecklistItem di
INNER JOIN WorkstreamDefStage ds ON ds.WorkstreamDefStageId = di.WorkstreamDefStageId
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = @NewWorkstreamId
   AND ws.SourceDefStageId = ds.WorkstreamDefStageId
WHERE ds.WorkstreamDefId = @CurrentDefId;

-- Audit on both sides
INSERT INTO AuditEvent (...) VALUES
    (@UserId, ..., 'Workstream', @OldWorkstreamId, ..., 'Rebuilt',
     CONCAT('{"newWorkstreamId":', @NewWorkstreamId, ',"reason":"', @Reason, '"}')),
    (@UserId, ..., 'Workstream', @NewWorkstreamId, ..., 'CreatedFromRebuild',
     CONCAT('{"rebuiltFromWorkstreamId":', @OldWorkstreamId, '}'));

COMMIT;
```

The old workstream stays in the database with all files, comments, history, and stage rows intact. An auditor can chain back through `RebuiltFromWorkstreamId` to see exactly what happened. The new workstream may pick up template changes (new checklist items, different stage chain) since it instantiates from `IsCurrent = 1` regardless of what the old workstream was on.

## Implementation guidance

Every state transition in this lifecycle follows the same shape:

1. **Atomic UPDATE with the precondition in the WHERE clause.** Status guards and stage guards live on the row, not in the application layer.
2. **Audit INSERT in the same transaction.** The AuditEvent is part of the same atomic operation as the state change. No race where the change committed but the audit didn't.
3. **Status check via `@@ROWCOUNT`** to detect concurrent attempts. If `@@ROWCOUNT = 0` after an UPDATE that should have matched a row, someone got there first or the precondition failed.

Wrap each transition in a stored procedure (`sp_SubmitWorkstream`, `sp_AdvanceStage`, `sp_SendBackToStage`, `sp_ApproveFinal`, etc.). This gives:

- **Consistent enforcement of the precondition.** The application can't issue a half-formed UPDATE that bypasses the rule.
- **Non-bypassable audit trail.** The audit insert is part of the procedure body; calling code can't forget to write it.
- **A clean unit-test boundary.** Test each procedure end-to-end with a real SQL Server via Testcontainers — the procedure is the unit of behavior.

The application layer (Blazor Server) calls these procedures rather than issuing UPDATEs directly. EF Core can do this via `FromSqlRaw` / `ExecuteSqlRaw` or via Dapper.

A few patterns specific to the multi-stage refactor:

- **Resolve the current stage row by joining `Workstream` to `WorkstreamStage ON OrderIndex = CurrentStageIndex`.** This pattern appears in lock acquisition, advance, send-back, final-approval — every place that asks "what is the active stage's role / threshold / final flag." Worth extracting as a SQL helper view if it gets repetitive.
- **`Round` increments on every send-back** (Step 8), not on advance/resubmit. This gives a true count of how many times any reviewer pushed back, which drives the "Round ≥ 4" attention condition in Active Workflows.
- **`RewoundToStageIndex` has been removed.** Send-back is always one step (N-1); the audit event's `fromStage`/`toStage` fields capture direction without a dedicated column.
- **`IsFinalApproval` distinguishes `sp_AdvanceStage` from `sp_ApproveFinal`.** They are separate procedures because the post-condition differs (advance pointer vs. transition to Approved). The application layer chooses which to call by reading the current stage's `IsFinalApproval` flag.
- **Per-stage timestamps are reset on rewind** for stages from target through current (`StartedAtUtc`, `CompletedAtUtc`, `Outcome` cleared); `EnteredAtUtc` is preserved for audit. The schema column comments describe this as "Reset to NULL on rewind"; the design decision is that `WorkstreamStage` reflects current state, with `AuditEvent` carrying full history.

### Closed-period write freeze

Every state-transition stored procedure must check that the workstream's `ClosePeriod` is open (`ClosedAtUtc IS NULL`) as its first action. This includes `sp_AcquireLock`, `sp_SubmitWorkstream`, `sp_AdvanceStage`, `sp_SendBackToStage`, `sp_ApproveFinal`, `sp_ApproveChecklistItem`, and `sp_FlagChecklistItemWithComment`. Closing a period freezes write paths on its workstreams; reopening unfreezes them. This rule is established in `docs/design/10-period-management.md` and enforced at the SP level.

The cleanest pattern is a small helper:

```sql
CREATE PROCEDURE sp_AssertPeriodOpen
    @WorkstreamId bigint
AS
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM Workstream w
        INNER JOIN ClosePeriod cp ON cp.ClosePeriodId = w.ClosePeriodId
        WHERE w.WorkstreamId = @WorkstreamId
          AND cp.ClosedAtUtc IS NULL
          AND cp.IsDeleted = 0
    ) BEGIN
        THROW 50050, 'Period is closed; reopen the period to make changes', 1;
    END;
END;
```

Each state-transition SP calls `EXEC sp_AssertPeriodOpen @WorkstreamId` as its first action, before any UPDATE. The THROW propagates as an exception the application layer translates into a user-readable message (the close-period dialog text from `10-period-management.md` is the canonical wording).

Read paths (queue queries, dashboard, audit search) are unaffected — closed periods remain fully visible for reporting and audit. Only state-changing actions are blocked.
