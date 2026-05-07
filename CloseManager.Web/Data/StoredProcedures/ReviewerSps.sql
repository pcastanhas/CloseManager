-- =============================================================================
-- Reviewer-path stored procedures
-- sp_AdvanceStage, sp_SendBackToStage, sp_ApproveFinal
-- All call sp_AssertPeriodOpen first (from PeriodSps.sql).
-- =============================================================================

-- =============================================================================
-- sp_AdvanceStage
-- Reviewer at a non-final stage advances the workstream to the next stage.
-- Requires: all current-stage checklist items have ReviewerStatus = 'Approved'.
-- Stamps current stage Outcome = 'Advanced', enters next stage, releases lock.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_AdvanceStage
    @WorkstreamId   bigint,
    @UserId         bigint,
    @ActorEntraOid  uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    -- Validate: caller holds the lock
    IF NOT EXISTS (
        SELECT 1 FROM Workstream
        WHERE WorkstreamId = @WorkstreamId
          AND LockedByUserId = @UserId
          AND LockExpiresAtUtc > SYSUTCDATETIME()
    )
        THROW 50040, 'Lock not held by calling user.', 1;

    DECLARE @CurrentStage int, @NextStage int, @IsFinal bit,
            @CurrentStageId bigint, @NextStageId bigint, @EntityId bigint, @Period nvarchar(10);

    SELECT
        @CurrentStage   = w.CurrentStageIndex,
        @EntityId       = w.EntityId,
        @Period         = w.Period
    FROM Workstream w WHERE w.WorkstreamId = @WorkstreamId AND w.IsDeleted = 0;

    IF @CurrentStage IS NULL
        THROW 50041, 'Workstream not found.', 1;

    -- Validate: not at stage 0 (preparers use sp_SubmitWorkstream)
    IF @CurrentStage = 0
        THROW 50042, 'Use sp_SubmitWorkstream to advance from the prepare stage.', 1;

    SELECT @CurrentStageId = ws.WorkstreamStageId, @IsFinal = ws.IsFinalApproval
    FROM WorkstreamStage ws
    WHERE ws.WorkstreamId = @WorkstreamId AND ws.OrderIndex = @CurrentStage AND ws.IsDeleted = 0;

    -- Validate: cannot advance from final stage (use sp_ApproveFinal instead)
    IF @IsFinal = 1
        THROW 50043, 'This is the final stage. Use sp_ApproveFinal to complete.', 1;

    -- Validate: all current-stage checklist items must be Approved
    IF EXISTS (
        SELECT 1 FROM ChecklistItem
        WHERE WorkstreamId = @WorkstreamId
          AND WorkstreamStageId = @CurrentStageId
          AND ReviewerStatus != 'Approved'
          AND IsDeleted = 0
    )
        THROW 50044, 'All checklist items must be approved before advancing.', 1;

    SET @NextStage = @CurrentStage + 1;

    SELECT @NextStageId = ws.WorkstreamStageId
    FROM WorkstreamStage ws
    WHERE ws.WorkstreamId = @WorkstreamId AND ws.OrderIndex = @NextStage AND ws.IsDeleted = 0;

    IF @NextStageId IS NULL
        THROW 50045, 'No next stage found. This may be a final stage — use sp_ApproveFinal.', 1;

    -- Stamp current stage complete
    UPDATE WorkstreamStage
    SET CompletedAtUtc    = SYSUTCDATETIME(),
        CompletedByUserId = @UserId,
        Outcome           = 'Advanced'
    WHERE WorkstreamStageId = @CurrentStageId;

    -- Enter next stage
    UPDATE WorkstreamStage
    SET EnteredAtUtc = SYSUTCDATETIME()
    WHERE WorkstreamStageId = @NextStageId;

    -- Advance workstream, release lock
    UPDATE Workstream
    SET CurrentStageIndex  = @NextStage,
        Status             = 'InProgress',
        LockedByUserId     = NULL,
        LockedAtUtc        = NULL,
        LockExpiresAtUtc   = NULL
    WHERE WorkstreamId = @WorkstreamId;

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Period, EntityId, Action, Notes
    )
    VALUES (
        SYSUTCDATETIME(), @UserId, @ActorEntraOid,
        'Workstream', @WorkstreamId, @WorkstreamId,
        @Period, @EntityId, 'StageAdvanced',
        '{"fromStage":' + CAST(@CurrentStage AS nvarchar) + ',"toStage":' + CAST(@NextStage AS nvarchar) + '}'
    );

    COMMIT;
END;
GO

-- =============================================================================
-- sp_SendBackToStage
-- Reviewer sends the workstream back one step (CurrentStageIndex--).
-- Round increments. Prior stage outcome is cleared so it re-enters cleanly.
-- Cannot send back from stage 0.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_SendBackToStage
    @WorkstreamId   bigint,
    @UserId         bigint,
    @ActorEntraOid  uniqueidentifier,
    @Reason         nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    -- Validate: caller holds the lock
    IF NOT EXISTS (
        SELECT 1 FROM Workstream
        WHERE WorkstreamId = @WorkstreamId
          AND LockedByUserId = @UserId
          AND LockExpiresAtUtc > SYSUTCDATETIME()
    )
        THROW 50050, 'Lock not held by calling user.', 1;

    DECLARE @CurrentStage int, @PriorStage int,
            @CurrentStageId bigint, @PriorStageId bigint,
            @EntityId bigint, @Period nvarchar(10);

    SELECT
        @CurrentStage = w.CurrentStageIndex,
        @EntityId     = w.EntityId,
        @Period       = w.Period
    FROM Workstream w WHERE w.WorkstreamId = @WorkstreamId AND w.IsDeleted = 0;

    IF @CurrentStage IS NULL
        THROW 50051, 'Workstream not found.', 1;

    -- Cannot send back from stage 0
    IF @CurrentStage = 0
        THROW 50052, 'Cannot send back from the prepare stage (stage 0).', 1;

    SET @PriorStage = @CurrentStage - 1;

    SELECT @CurrentStageId = ws.WorkstreamStageId
    FROM WorkstreamStage ws
    WHERE ws.WorkstreamId = @WorkstreamId AND ws.OrderIndex = @CurrentStage AND ws.IsDeleted = 0;

    SELECT @PriorStageId = ws.WorkstreamStageId
    FROM WorkstreamStage ws
    WHERE ws.WorkstreamId = @WorkstreamId AND ws.OrderIndex = @PriorStage AND ws.IsDeleted = 0;

    -- Stamp current stage as SentBack
    UPDATE WorkstreamStage
    SET CompletedAtUtc    = SYSUTCDATETIME(),
        CompletedByUserId = @UserId,
        Outcome           = 'SentBack'
    WHERE WorkstreamStageId = @CurrentStageId;

    -- Clear prior stage outcome so it re-enters cleanly
    UPDATE WorkstreamStage
    SET Outcome           = NULL,
        CompletedAtUtc    = NULL,
        CompletedByUserId = NULL
    WHERE WorkstreamStageId = @PriorStageId;

    -- Rewind workstream, increment Round, release lock
    UPDATE Workstream
    SET CurrentStageIndex = @PriorStage,
        Status            = 'NeedsRevision',
        Round             = Round + 1,
        LockedByUserId    = NULL,
        LockedAtUtc       = NULL,
        LockExpiresAtUtc  = NULL
    WHERE WorkstreamId = @WorkstreamId;

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Period, EntityId, Action, Notes
    )
    VALUES (
        SYSUTCDATETIME(), @UserId, @ActorEntraOid,
        'Workstream', @WorkstreamId, @WorkstreamId,
        @Period, @EntityId, 'SentBack',
        '{"fromStage":' + CAST(@CurrentStage AS nvarchar) + ',"toStage":' + CAST(@PriorStage AS nvarchar) + ',"reason":"' + REPLACE(@Reason, '"', '\"') + '"}'
    );

    COMMIT;
END;
GO

-- =============================================================================
-- sp_ApproveFinal
-- Reviewer at the final stage finalizes the workstream → Status = 'Approved'.
-- Requires: all current-stage checklist items Approved, stage IsFinalApproval = 1.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_ApproveFinal
    @WorkstreamId   bigint,
    @UserId         bigint,
    @ActorEntraOid  uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    EXEC sp_AssertPeriodOpen @WorkstreamId;

    -- Validate: caller holds the lock
    IF NOT EXISTS (
        SELECT 1 FROM Workstream
        WHERE WorkstreamId = @WorkstreamId
          AND LockedByUserId = @UserId
          AND LockExpiresAtUtc > SYSUTCDATETIME()
    )
        THROW 50060, 'Lock not held by calling user.', 1;

    DECLARE @CurrentStage int, @IsFinal bit,
            @CurrentStageId bigint, @EntityId bigint, @Period nvarchar(10);

    SELECT
        @CurrentStage = w.CurrentStageIndex,
        @EntityId     = w.EntityId,
        @Period       = w.Period
    FROM Workstream w WHERE w.WorkstreamId = @WorkstreamId AND w.IsDeleted = 0;

    IF @CurrentStage IS NULL
        THROW 50061, 'Workstream not found.', 1;

    SELECT @CurrentStageId = ws.WorkstreamStageId, @IsFinal = ws.IsFinalApproval
    FROM WorkstreamStage ws
    WHERE ws.WorkstreamId = @WorkstreamId AND ws.OrderIndex = @CurrentStage AND ws.IsDeleted = 0;

    -- Validate: must be the final stage
    IF @IsFinal = 0 OR @IsFinal IS NULL
        THROW 50062, 'sp_ApproveFinal can only be called on the final stage.', 1;

    -- Validate: all current-stage checklist items must be Approved
    IF EXISTS (
        SELECT 1 FROM ChecklistItem
        WHERE WorkstreamId = @WorkstreamId
          AND WorkstreamStageId = @CurrentStageId
          AND ReviewerStatus != 'Approved'
          AND IsDeleted = 0
    )
        THROW 50063, 'All checklist items must be approved before finalizing.', 1;

    -- Stamp stage complete
    UPDATE WorkstreamStage
    SET CompletedAtUtc    = SYSUTCDATETIME(),
        CompletedByUserId = @UserId,
        Outcome           = 'Advanced'
    WHERE WorkstreamStageId = @CurrentStageId;

    -- Finalize workstream, release lock
    UPDATE Workstream
    SET Status           = 'Approved',
        ApprovedAtUtc    = SYSUTCDATETIME(),
        ApprovedByUserId = @UserId,
        LockedByUserId   = NULL,
        LockedAtUtc      = NULL,
        LockExpiresAtUtc = NULL
    WHERE WorkstreamId = @WorkstreamId;

    -- Audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Period, EntityId, Action, Notes
    )
    VALUES (
        SYSUTCDATETIME(), @UserId, @ActorEntraOid,
        'Workstream', @WorkstreamId, @WorkstreamId,
        @Period, @EntityId, 'FinalApproved',
        '{"stage":' + CAST(@CurrentStage AS nvarchar) + '}'
    );

    COMMIT;
END;
GO
