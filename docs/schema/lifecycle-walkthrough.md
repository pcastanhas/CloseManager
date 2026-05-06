# Workstream lifecycle walkthrough

This doc walks through the full happy-path lifecycle of a single workstream as a sequence of SQL operations, plus a few branch cases. It serves as both documentation and a sanity check on the schema — implementing the lifecycle in code should follow these patterns.

The running example is Plaza Tower's debt service review for the October 2025 close, prepared by Maya (UserId=12) and reviewed by Erin (UserId=7, role: Treasury-RE).

## Setup assumptions

- `User` rows for Maya, Erin, an admin, and a "System" user (UserId=1) for automated actions
- `Entity` row for Plaza Tower (EntityId=42, EntityTypeId for RealEstateAsset)
- `Role` rows for Preparer (RoleId=1) and TreasuryRE (RoleId=4)
- `EntityRoleAssignment`: Maya as Preparer for Plaza Tower, Erin as TreasuryRE for Plaza Tower
- A `WorkflowTemplate` for RealEstateAsset, version 3, effective from `202509`, with a `WorkstreamDef` for debt service (WorkstreamDefId=27) and 6 `WorkstreamDefChecklistItem` rows tied to it

## Step 1: Period opens — workstreams instantiate

When the October close opens (admin action or scheduled job), the system creates one `ClosePeriod` per active entity, then materializes workstreams from the resolved template.

```sql
-- Open the period for Plaza Tower
INSERT INTO ClosePeriod (EntityId, Period, OpenedByUserId)
VALUES (42, '202510', 1);
-- @ClosePeriodId = 1001

-- Resolve template version: latest WorkflowTemplate for RealEstateAsset
-- where EffectiveFromPeriod <= '202510'. Result: version 3.

-- Materialize each workstream from that template's defs.
INSERT INTO Workstream (
    ClosePeriodId, Period, EntityId, WorkstreamDefId,
    Code, Name, OrderIndex, PreparerRoleId, ReviewerRoleId,
    Status, Round, CreatedByUserId
)
VALUES (
    1001, '202510', 42, 27,
    'DEBT_SVC', 'Debt service review', 4, 1, 4,
    'NotStarted', 1, 1
);
-- @WorkstreamId = 5042

-- Clone checklist items from the def
INSERT INTO ChecklistItem (
    WorkstreamId, SourceDefItemId, AddedByUserId, OrderIndex, Text
)
SELECT 5042, WorkstreamDefChecklistItemId, 1, OrderIndex, Text
FROM WorkstreamDefChecklistItem
WHERE WorkstreamDefId = 27
ORDER BY OrderIndex;

-- Audit
INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
VALUES (
    1, '...', 'Workstream', 5042,
    5042, '202510', 42, 'Instantiated',
    '{"templateVersion": 3, "workstreamDef": "DEBT_SVC"}'
);
```

The workstream now exists in `NotStarted` state with its 6 checklist items and no files. Maya sees it on her home view as a "not started" tile.

## Step 2: Maya opens the workstream — soft state changes

Opening doesn't yet change `Status` — that flips only when she actually uploads or modifies something. But it does acquire a lock.

```sql
UPDATE Workstream
SET LockedByUserId = 12,
    LockedAtUtc = SYSUTCDATETIME(),
    LockExpiresAtUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME())
WHERE WorkstreamId = 5042
  AND IsDeleted = 0
  AND (LockedByUserId IS NULL OR LockedByUserId = 12 OR LockExpiresAtUtc < SYSUTCDATETIME())
  AND EXISTS (
    SELECT 1 FROM EntityRoleAssignment era
    WHERE era.EntityId = Workstream.EntityId
      AND era.UserId = 12
      AND era.IsDeleted = 0
      AND era.RoleId = CASE
            WHEN Workstream.Status IN ('NotStarted', 'InProgress', 'NeedsRevision')
              THEN Workstream.PreparerRoleId
            WHEN Workstream.Status IN ('Submitted', 'InReview')
              THEN Workstream.ReviewerRoleId
            ELSE NULL
          END
  );
-- @@ROWCOUNT = 1: she got the lock. 0 = either someone else has it or wrong role.
```

The `EXISTS` subclause enforces "the lock can only be taken by a user with the role appropriate for the current status." This is the key rule that replaces the explicit claim system.

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

-- First file means the workstream transitions NotStarted → InProgress.
UPDATE Workstream
SET Status = 'InProgress',
    StartedAtUtc = SYSUTCDATETIME(),
    LockExpiresAtUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME())
WHERE WorkstreamId = 5042 AND Status = 'NotStarted';

INSERT INTO AuditEvent (
    ActorUserId, ActorEntraObjectId, TargetTable, TargetId,
    WorkstreamId, Period, EntityId, Action, AfterJson
)
VALUES
    (12, '...', 'WorkstreamFile', 9001, 5042, '202510', 42, 'FileUploaded',
     '{"fileRole": "Primary", "fileName": "Plaza_Tower_DebtService_Oct.xlsx", "size": 184320}'),
    (12, '...', 'Workstream', 5042, 5042, '202510', 42, 'StatusChanged',
     '{"from": "NotStarted", "to": "InProgress"}');

COMMIT;
```

The status transition is conditional (`AND Status = 'NotStarted'`) so subsequent file uploads don't re-fire the InProgress transition.

## Step 4: Maya works the prep checklist

```sql
-- Mark item ready
UPDATE ChecklistItem
SET PreparerStatus = 'Ready',
    PreparerMarkedAtUtc = SYSUTCDATETIME(),
    PreparerMarkedByUserId = 12
WHERE ChecklistItemId = 7102 AND WorkstreamId = 5042;

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'ChecklistItem', 7102,
        5042, '202510', 42, 'PreparerMarkedReady',
        '{"itemText": "Senior loan principal & interest match amort"}');

-- Add a note
INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7102, 12, 'Confirmed against amort table. Final payment is March 2027.');

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'Comment', @@IDENTITY, 5042, '202510', 42, 'CommentPosted',
        '{"checklistItemId": 7102, "kind": "PreparerNote"}');

-- Add an ad-hoc checklist item
INSERT INTO ChecklistItem (
    WorkstreamId, SourceDefItemId, AddedByUserId, OrderIndex, Text
)
VALUES (5042, NULL, 12, 7, 'Verify hurricane insurance reserve adjustment');

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'ChecklistItem', @@IDENTITY,
        5042, '202510', 42, 'ChecklistItemAdded',
        '{"text": "Verify hurricane insurance reserve adjustment", "addedBy": "Preparer"}');
```

`SourceDefItemId IS NULL` marks the item as ad-hoc. Reviewer UI uses that to show an "added" badge.

## Step 5: Maya submits — Ready for review

```sql
BEGIN TRAN;

-- Validate primary file exists
DECLARE @PrimaryCount int = (
    SELECT COUNT(*) FROM WorkstreamFile
    WHERE WorkstreamId = 5042 AND FileRole = 'Primary' AND IsDeleted = 0
);
IF @PrimaryCount < 1 BEGIN ROLLBACK; THROW 50001, 'Primary file required', 1; END;

-- Transition InProgress → Submitted, release Maya's lock
UPDATE Workstream
SET Status = 'Submitted',
    SubmittedAtUtc = SYSUTCDATETIME(),
    SubmittedByUserId = 12,
    LockedByUserId = NULL,
    LockedAtUtc = NULL,
    LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042 AND Status = 'InProgress';

INSERT INTO AuditEvent (...)
VALUES (12, '...', 'Workstream', 5042,
        5042, '202510', 42, 'Submitted',
        '{"status": "InProgress", "round": 1}',
        '{"status": "Submitted", "round": 1, "checklistReady": "5 of 7"}');

COMMIT;
```

The workstream is now visible to anyone with the TreasuryRE role for Plaza Tower. Aging starts from `SubmittedAtUtc`.

## Step 6: Erin opens it — lock + status flip

```sql
UPDATE Workstream
SET LockedByUserId = 7,
    LockedAtUtc = SYSUTCDATETIME(),
    LockExpiresAtUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME()),
    Status = CASE WHEN Status = 'Submitted' THEN 'InReview' ELSE Status END,
    ReviewStartedAtUtc = COALESCE(ReviewStartedAtUtc, SYSUTCDATETIME())
WHERE WorkstreamId = 5042
  AND IsDeleted = 0
  AND (LockedByUserId IS NULL OR LockedByUserId = 7 OR LockExpiresAtUtc < SYSUTCDATETIME())
  AND EXISTS (
    SELECT 1 FROM EntityRoleAssignment era
    WHERE era.EntityId = Workstream.EntityId
      AND era.UserId = 7
      AND era.IsDeleted = 0
      AND era.RoleId = Workstream.ReviewerRoleId
  );

INSERT INTO AuditEvent (...)
VALUES (7, '...', 'Workstream', 5042,
        5042, '202510', 42, 'OpenedByReviewer',
        '{"newStatus": "InReview"}');
```

The status transition is folded into the same UPDATE, conditionally. `ReviewStartedAtUtc` only sets the first time (the COALESCE pattern), so re-opens don't reset reviewer aging.

## Step 7: Erin works the review checklist

```sql
-- Approve an item
UPDATE ChecklistItem
SET ReviewerStatus = 'Approved',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = 7
WHERE ChecklistItemId = 7101 AND WorkstreamId = 5042;

INSERT INTO AuditEvent (...) VALUES (7, ..., 'ReviewerApproved', ...);

-- Flag an item + comment in one transaction
BEGIN TRAN;

UPDATE ChecklistItem
SET ReviewerStatus = 'NeedsRevision',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = 7
WHERE ChecklistItemId = 7103 AND WorkstreamId = 5042;

INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7103, 7,
        'Mezz interest jumped from 44k to 52k. Rate change in the loan, or accrual error?');

INSERT INTO AuditEvent (...) VALUES
    (7, ..., 'ReviewerFlagged', ...),
    (7, ..., 'CommentPosted', ...);

COMMIT;
```

The "needs revision + comment" pair always happens in one transaction. That's the design rule that prevents flagged items without explanations.

## Step 8: Erin requests changes — back to Maya

```sql
BEGIN TRAN;

UPDATE Workstream
SET Status = 'NeedsRevision',
    LockedByUserId = NULL,
    LockedAtUtc = NULL,
    LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042 AND Status = 'InReview';

INSERT INTO AuditEvent (...)
VALUES (7, '...', 'Workstream', 5042,
        5042, '202510', 42, 'ChangesRequested',
        '{"status": "InReview", "round": 1}',
        '{"status": "NeedsRevision", "flaggedItems": 1, "newItems": 1}');

COMMIT;
```

The workstream goes back into the preparer's pool. Anyone with the Preparer role for Plaza Tower can pick it up — but in practice it'll be Maya, since she's the only one. The flagged items stay in `NeedsRevision` state on the items themselves; they don't reset to `Pending`.

## Step 9: Maya addresses the feedback

```sql
-- Reacquire lock (back to preparer state)
UPDATE Workstream SET LockedByUserId = 12, ... WHERE ...;

-- Reply on the flagged item
INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, Body)
VALUES (5042, 7103, 12,
        'Rate stepped up per loan agreement section 4.2. Updated v4 reflects new rate from Oct 1.');

-- Replace v3 with v4
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

INSERT INTO AuditEvent (...) VALUES
    (12, ..., 'FileReplaced', '{"oldFileId": 9001, "newFileId": ...}');

COMMIT;

-- Resubmit
UPDATE Workstream
SET Status = 'Submitted',
    Round = Round + 1,
    SubmittedAtUtc = SYSUTCDATETIME(),
    SubmittedByUserId = 12,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042 AND Status = 'NeedsRevision';

INSERT INTO AuditEvent (...) VALUES (12, ..., 'Resubmitted', '{"round": 2}');
```

The replace pattern (soft-delete + insert with `ReplacesFileId`) gives a clean version chain. An auditor can ask "what did Maya submit at each round?" and get a definitive answer.

## Step 10: Erin re-reviews and approves

```sql
-- Resolve the flagged item
UPDATE ChecklistItem
SET ReviewerStatus = 'Approved',
    ReviewerMarkedAtUtc = SYSUTCDATETIME(),
    ReviewerMarkedByUserId = 7
WHERE ChecklistItemId = 7103 AND WorkstreamId = 5042;

-- Approve workstream. All checklist items must be Approved.
BEGIN TRAN;

DECLARE @Pending int = (
    SELECT COUNT(*) FROM ChecklistItem
    WHERE WorkstreamId = 5042 AND IsDeleted = 0 AND ReviewerStatus <> 'Approved'
);
IF @Pending > 0 BEGIN ROLLBACK; THROW 50002, 'Items remain', 1; END;

UPDATE Workstream
SET Status = 'Approved',
    ApprovedAtUtc = SYSUTCDATETIME(),
    ApprovedByUserId = 7,
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042 AND Status = 'InReview';

INSERT INTO AuditEvent (...)
VALUES (7, '...', 'Workstream', 5042,
        5042, '202510', 42, 'Approved',
        '{"status": "InReview", "round": 2}',
        '{"status": "Approved", "round": 2, "totalDurationHours": 47}');

COMMIT;
```

The "all items approved before workstream approved" rule is enforced in SQL itself, not just at the application layer. Worth implementing this as a stored procedure so the rule is non-bypassable.

## Branch case: lock contention

When a second user tries to acquire the lock:

```sql
UPDATE Workstream SET ... WHERE WorkstreamId = 5042 AND ...;
-- @@ROWCOUNT = 0
```

The application then runs a SELECT to determine why and shows the appropriate UI:

```sql
SELECT LockedByUserId, LockExpiresAtUtc, Status,
    CASE WHEN EXISTS (
        SELECT 1 FROM EntityRoleAssignment era
        WHERE era.EntityId = w.EntityId AND era.UserId = @UserId AND era.IsDeleted = 0
          AND era.RoleId = CASE
                WHEN w.Status IN ('NotStarted', 'InProgress', 'NeedsRevision')
                  THEN w.PreparerRoleId
                WHEN w.Status IN ('Submitted', 'InReview')
                  THEN w.ReviewerRoleId
                ELSE NULL END
    ) THEN 1 ELSE 0 END AS HasRole
FROM Workstream w
WHERE w.WorkstreamId = @WorkstreamId;
```

If `HasRole = 1` and `LockedByUserId` is set, show "Locked by {user}, expires in N min." If `HasRole = 0`, show "This workstream is currently with the {role} — you can't edit until it comes back."

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

INSERT INTO AuditEvent (...)
SELECT 1, '00000000-0000-0000-0000-000000000000',
       'Workstream', e.WorkstreamId,
       e.WorkstreamId, w.Period, w.EntityId,
       'LockExpired',
       CONCAT('{"prevLockedBy":', e.PrevLockedBy, '}')
FROM @ExpiredLocks e
INNER JOIN Workstream w ON w.WorkstreamId = e.WorkstreamId;
```

The `IX_Workstream_LockExpiry` filtered index keeps this query cheap.

## Branch case: rebuild and restart

When a workstream is in such a bad state that admins want to start over:

```sql
BEGIN TRAN;

-- Mark old as Rebuilt (Status, not IsDeleted — keep visible in audit views)
UPDATE Workstream
SET Status = 'Rebuilt',
    LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
WHERE WorkstreamId = 5042;

-- Create fresh workstream pointing back via RebuiltFromWorkstreamId
INSERT INTO Workstream (
    ClosePeriodId, Period, EntityId, WorkstreamDefId,
    Code, Name, OrderIndex, PreparerRoleId, ReviewerRoleId,
    Status, Round, RebuiltFromWorkstreamId, CreatedByUserId
)
SELECT ClosePeriodId, Period, EntityId, WorkstreamDefId,
       Code, Name, OrderIndex, PreparerRoleId, ReviewerRoleId,
       'NotStarted', 1, WorkstreamId, 1
FROM Workstream WHERE WorkstreamId = 5042;
-- @NewWorkstreamId = 5099

-- Clone fresh checklist from current template version (note: this could
-- pick up a newer template if one has become effective since)
INSERT INTO ChecklistItem (WorkstreamId, SourceDefItemId, AddedByUserId, OrderIndex, Text)
SELECT 5099, WorkstreamDefChecklistItemId, 1, OrderIndex, Text
FROM WorkstreamDefChecklistItem
WHERE WorkstreamDefId = (SELECT WorkstreamDefId FROM Workstream WHERE WorkstreamId = 5099);

-- Audit on both sides
INSERT INTO AuditEvent (...) VALUES
    (1, ..., 'Workstream', 5042, ..., 'Rebuilt',
     '{"newWorkstreamId": 5099, "reason": "wrong template applied"}'),
    (1, ..., 'Workstream', 5099, ..., 'CreatedFromRebuild',
     '{"rebuiltFromWorkstreamId": 5042}');

COMMIT;
```

The old workstream stays in the database with all files, comments, and history intact. An auditor can chain back through `RebuiltFromWorkstreamId` to see exactly what happened.

## Implementation guidance

Every state transition in this lifecycle follows the same shape:

1. Atomic UPDATE with the precondition in the WHERE clause
2. Audit INSERT in the same transaction
3. Status check via `@@ROWCOUNT` to detect concurrent attempts

Wrap each transition in a stored procedure (`sp_SubmitWorkstream`, `sp_OpenForReview`, `sp_ApproveChecklistItem`, etc.). This gives you:

- Consistent enforcement of the precondition
- Non-bypassable audit trail (the audit insert is part of the same SP)
- A clean unit-test boundary (test each procedure end-to-end with a real SQL Server via Testcontainers)

The application layer (Blazor Server) calls these procedures rather than issuing UPDATEs directly. EF Core can do this via `FromSqlRaw` / `ExecuteSqlRaw` or via Dapper.
