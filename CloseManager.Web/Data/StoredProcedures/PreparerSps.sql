-- =============================================================================
-- Preparer-path stored procedures
-- sp_AcquireLock, sp_SubmitWorkstream, sp_ApproveChecklistItem,
-- sp_FlagChecklistItemWithComment
-- All call sp_AssertPeriodOpen first (from PeriodSps.sql).
-- =============================================================================

-- =============================================================================
-- sp_AcquireLock
-- Acquires an exclusive lock on a workstream for the calling user.
-- The user must hold the role matching the current stage on this entity.
-- Returns @@ROWCOUNT = 1 on success, 0 if blocked (wrong role or already locked).
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_AcquireLock
    @WorkstreamId   bigint,
    @UserId         bigint,
    @LockMinutes    int = 15,
    @ActorEntraOid  uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT OFF;  -- caller checks @@ROWCOUNT

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    UPDATE w
    SET w.LockedByUserId    = @UserId,
        w.LockedAtUtc       = SYSUTCDATETIME(),
        w.LockExpiresAtUtc  = DATEADD(MINUTE, @LockMinutes, SYSUTCDATETIME())
    FROM Workstream w
    INNER JOIN WorkstreamStage ws
        ON ws.WorkstreamId = w.WorkstreamId
       AND ws.OrderIndex   = w.CurrentStageIndex
       AND ws.IsDeleted    = 0
    WHERE w.WorkstreamId = @WorkstreamId
      AND w.IsDeleted    = 0
      AND (
            w.LockedByUserId IS NULL
         OR w.LockedByUserId = @UserId
         OR w.LockExpiresAtUtc < SYSUTCDATETIME()
          )
      AND EXISTS (
            SELECT 1 FROM EntityRoleAssignment era
            WHERE era.EntityId  = w.EntityId
              AND era.UserId    = @UserId
              AND era.RoleId    = ws.RoleId
              AND era.IsDeleted = 0
          );
    -- @@ROWCOUNT = 0: either wrong role for current stage, or locked by another
END;
GO

-- =============================================================================
-- sp_SubmitWorkstream
-- Preparer submits stage 0 → advances to stage 1.
-- Requires: primary file uploaded, caller holds lock.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_SubmitWorkstream
    @WorkstreamId   bigint,
    @UserId         bigint,
    @ActorEntraOid  uniqueidentifier,
    @Note           nvarchar(1000) = NULL
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    -- Validate: primary file must exist
    IF NOT EXISTS (
        SELECT 1 FROM WorkstreamFile
        WHERE WorkstreamId = @WorkstreamId
          AND FileRole = 'Primary'
          AND IsDeleted = 0
    )
        THROW 50010, 'A primary file must be uploaded before submitting.', 1;

    -- Validate: caller holds the lock
    IF NOT EXISTS (
        SELECT 1 FROM Workstream
        WHERE WorkstreamId = @WorkstreamId
          AND LockedByUserId = @UserId
          AND LockExpiresAtUtc > SYSUTCDATETIME()
    )
        THROW 50011, 'Lock not held by calling user.', 1;

    -- Stamp stage 0 as complete
    UPDATE WorkstreamStage
    SET CompletedAtUtc    = SYSUTCDATETIME(),
        CompletedByUserId = @UserId,
        Outcome           = 'Advanced'
    WHERE WorkstreamId = @WorkstreamId AND OrderIndex = 0;

    -- Stamp stage 1 as entered
    UPDATE WorkstreamStage
    SET EnteredAtUtc = SYSUTCDATETIME()
    WHERE WorkstreamId = @WorkstreamId AND OrderIndex = 1;

    -- Advance workstream
    UPDATE Workstream
    SET Status              = 'InProgress',
        CurrentStageIndex   = 1,
        StartedAtUtc        = ISNULL(StartedAtUtc, SYSUTCDATETIME()),
        LockedByUserId      = NULL,
        LockedAtUtc         = NULL,
        LockExpiresAtUtc    = NULL
    WHERE WorkstreamId = @WorkstreamId;

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Period, EntityId, Action, Notes
    )
    SELECT SYSUTCDATETIME(), @UserId, @ActorEntraOid,
           'Workstream', @WorkstreamId, @WorkstreamId,
           w.Period, w.EntityId, 'Submitted', @Note
    FROM Workstream w WHERE w.WorkstreamId = @WorkstreamId;

    COMMIT;
END;
GO

-- =============================================================================
-- sp_ApproveChecklistItem
-- Marks a checklist item as Ready (preparer) or Approved (reviewer).
-- Role determined by the workstream's CurrentStageIndex.
-- Stage 0 → sets PreparerStatus = 'Ready'
-- Stage N → sets ReviewerStatus = 'Approved'
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_ApproveChecklistItem
    @ChecklistItemId    bigint,
    @UserId             bigint,
    @ActorEntraOid      uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    DECLARE @WorkstreamId bigint, @CurrentStage int, @StageId bigint;

    SELECT
        @WorkstreamId = ci.WorkstreamId,
        @CurrentStage = w.CurrentStageIndex,
        @StageId      = ci.WorkstreamStageId
    FROM ChecklistItem ci
    INNER JOIN Workstream w ON w.WorkstreamId = ci.WorkstreamId
    WHERE ci.ChecklistItemId = @ChecklistItemId AND ci.IsDeleted = 0;

    IF @WorkstreamId IS NULL
        THROW 50020, 'ChecklistItem not found.', 1;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    IF @CurrentStage = 0
    BEGIN
        UPDATE ChecklistItem
        SET PreparerStatus        = 'Ready',
            PreparerMarkedAtUtc   = SYSUTCDATETIME(),
            PreparerMarkedByUserId = @UserId
        WHERE ChecklistItemId = @ChecklistItemId;
    END
    ELSE
    BEGIN
        -- Only items belonging to the current stage can be acted on
        DECLARE @ItemStageIndex int;
        SELECT @ItemStageIndex = ws.OrderIndex
        FROM WorkstreamStage ws
        WHERE ws.WorkstreamStageId = @StageId;

        IF @ItemStageIndex != @CurrentStage
            THROW 50021, 'Cannot approve checklist item from a different stage.', 1;

        UPDATE ChecklistItem
        SET ReviewerStatus         = 'Approved',
            ReviewerMarkedAtUtc    = SYSUTCDATETIME(),
            ReviewerMarkedByUserId = @UserId
        WHERE ChecklistItemId = @ChecklistItemId;
    END;

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId, Action
    )
    SELECT SYSUTCDATETIME(), @UserId, @ActorEntraOid,
           'ChecklistItem', @ChecklistItemId, @WorkstreamId,
           CASE WHEN @CurrentStage = 0 THEN 'ChecklistItemMarkedReady'
                ELSE 'ChecklistItemApproved' END;

    COMMIT;
END;
GO

-- =============================================================================
-- sp_FlagChecklistItemWithComment
-- Reviewer flags a checklist item as NeedsRevision and posts a comment.
-- Comment and status update happen in one transaction.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_FlagChecklistItemWithComment
    @ChecklistItemId    bigint,
    @UserId             bigint,
    @ActorEntraOid      uniqueidentifier,
    @CommentBody        nvarchar(max)
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    DECLARE @WorkstreamId bigint, @CurrentStage int;

    SELECT @WorkstreamId = ci.WorkstreamId, @CurrentStage = w.CurrentStageIndex
    FROM ChecklistItem ci
    INNER JOIN Workstream w ON w.WorkstreamId = ci.WorkstreamId
    WHERE ci.ChecklistItemId = @ChecklistItemId AND ci.IsDeleted = 0;

    IF @WorkstreamId IS NULL
        THROW 50030, 'ChecklistItem not found.', 1;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    IF @CurrentStage = 0
        THROW 50031, 'Cannot flag items at the prepare stage.', 1;

    UPDATE ChecklistItem
    SET ReviewerStatus         = 'NeedsRevision',
        ReviewerMarkedAtUtc    = SYSUTCDATETIME(),
        ReviewerMarkedByUserId = @UserId
    WHERE ChecklistItemId = @ChecklistItemId;

    INSERT INTO Comment (WorkstreamId, ChecklistItemId, AuthorUserId, PostedAtUtc, Body)
    VALUES (@WorkstreamId, @ChecklistItemId, @UserId, SYSUTCDATETIME(), @CommentBody);

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Action, Notes
    )
    SELECT SYSUTCDATETIME(), @UserId, @ActorEntraOid,
           'ChecklistItem', @ChecklistItemId, @WorkstreamId,
           'ChecklistItemFlagged', LEFT(@CommentBody, 200);

    COMMIT;
END;
GO
