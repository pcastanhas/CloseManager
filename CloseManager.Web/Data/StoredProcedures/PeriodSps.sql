-- =============================================================================
-- Period lifecycle stored procedures
-- Run this script against your database to create the procedures.
-- All procedures use SET XACT_ABORT ON so any error auto-rolls back the tx.
-- =============================================================================

-- =============================================================================
-- sp_AssertPeriodOpen
-- Helper called by every state-transition SP as its first action.
-- Throws 50050 if the workstream's period is closed.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_AssertPeriodOpen
    @WorkstreamId bigint
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @IsClosed bit = 0;

    SELECT @IsClosed = CASE WHEN cp.ClosedAtUtc IS NOT NULL THEN 1 ELSE 0 END
    FROM Workstream w
    INNER JOIN ClosePeriod cp ON cp.ClosePeriodId = w.ClosePeriodId
    WHERE w.WorkstreamId = @WorkstreamId AND w.IsDeleted = 0;

    IF @IsClosed = 1
        THROW 50050, 'Period is closed. Reopen the period to make changes.', 1;
END;
GO

-- =============================================================================
-- sp_OpenPeriod
-- Creates one ClosePeriod row for a single entity and materialises all
-- workstreams, stages, and checklist items from the current template.
-- Idempotent: skips the entity if a ClosePeriod row already exists for it.
-- Called in a loop by the OpenPeriodJob Hangfire job — one call per entity.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_OpenPeriod
    @EntityId       bigint,
    @Period         char(6),
    @ActorUserId    bigint,
    @ActorEntraOid  uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;

    -- Idempotency: skip if already opened for this entity-period
    IF EXISTS (
        SELECT 1 FROM ClosePeriod
        WHERE EntityId = @EntityId AND Period = @Period AND IsDeleted = 0
    )
        RETURN;  -- already done, safe to skip

    BEGIN TRAN;

    -- Resolve current template for this entity's type
    DECLARE @EntityTypeId int, @TemplateId bigint, @TemplateVersion int;
    SELECT @EntityTypeId = EntityTypeId FROM Entity WHERE EntityId = @EntityId AND IsDeleted = 0;

    SELECT @TemplateId = WorkflowTemplateId, @TemplateVersion = Version
    FROM WorkflowTemplate
    WHERE EntityTypeId = @EntityTypeId AND IsCurrent = 1 AND IsDeleted = 0;

    IF @TemplateId IS NULL
        THROW 50001, 'No current workflow template for this entity type.', 1;

    -- Create ClosePeriod row
    INSERT INTO ClosePeriod (EntityId, Period, OpenedAtUtc, OpenedByUserId)
    VALUES (@EntityId, @Period, SYSUTCDATETIME(), @ActorUserId);

    DECLARE @ClosePeriodId bigint = SCOPE_IDENTITY();

    -- Materialise each WorkstreamDef as a Workstream
    DECLARE @WorkstreamDefId bigint, @Code nvarchar(40), @Name nvarchar(100), @OrderIdx int;
    DECLARE ws_cursor CURSOR FAST_FORWARD FOR
        SELECT WorkstreamDefId, Code, Name, OrderIndex
        FROM WorkstreamDef
        WHERE WorkflowTemplateId = @TemplateId
        ORDER BY OrderIndex;

    OPEN ws_cursor;
    FETCH NEXT FROM ws_cursor INTO @WorkstreamDefId, @Code, @Name, @OrderIdx;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        INSERT INTO Workstream (
            ClosePeriodId, Period, EntityId, WorkstreamDefId,
            Code, Name, OrderIndex,
            Status, Round, CurrentStageIndex,
            CreatedAtUtc, CreatedByUserId
        )
        VALUES (
            @ClosePeriodId, @Period, @EntityId, @WorkstreamDefId,
            @Code, @Name, @OrderIdx,
            'NotStarted', 1, 0,
            SYSUTCDATETIME(), @ActorUserId
        );

        DECLARE @WorkstreamId bigint = SCOPE_IDENTITY();

        -- Snapshot stage chain
        INSERT INTO WorkstreamStage (
            WorkstreamId, SourceDefStageId,
            OrderIndex, RoleId, StageKind, DisplayName,
            IsFinalApproval, StuckThresholdHours,
            EnteredAtUtc,
            IsDeleted
        )
        SELECT
            @WorkstreamId, wds.WorkstreamDefStageId,
            wds.OrderIndex, wds.RoleId, wds.StageKind, wds.DisplayName,
            wds.IsFinalApproval, wds.StuckThresholdHours,
            CASE WHEN wds.OrderIndex = 0 THEN SYSUTCDATETIME() ELSE NULL END,
            0
        FROM WorkstreamDefStage wds
        WHERE wds.WorkstreamDefId = @WorkstreamDefId
        ORDER BY wds.OrderIndex;

        -- Clone checklist items scoped to their stage
        INSERT INTO ChecklistItem (
            WorkstreamId, WorkstreamStageId, SourceDefItemId,
            AddedByUserId, AddedAtUtc, OrderIndex, Text,
            PreparerStatus, ReviewerStatus, IsDeleted
        )
        SELECT
            @WorkstreamId, ws.WorkstreamStageId, di.WorkstreamDefChecklistItemId,
            @ActorUserId, SYSUTCDATETIME(), di.OrderIndex, di.Text,
            'NotReady', 'Pending', 0
        FROM WorkstreamDefChecklistItem di
        INNER JOIN WorkstreamDefStage ds ON ds.WorkstreamDefStageId = di.WorkstreamDefStageId
        INNER JOIN WorkstreamStage ws
            ON ws.WorkstreamId = @WorkstreamId
           AND ws.SourceDefStageId = ds.WorkstreamDefStageId
        WHERE ds.WorkstreamDefId = @WorkstreamDefId;

        -- Audit
        INSERT INTO AuditEvent (
            OccurredAtUtc, ActorUserId, ActorEntraObjectId,
            TargetTable, TargetId, WorkstreamId, Period, EntityId,
            Action, AfterJson
        )
        VALUES (
            SYSUTCDATETIME(), @ActorUserId, @ActorEntraOid,
            'Workstream', @WorkstreamId, @WorkstreamId, @Period, @EntityId,
            'WorkstreamInstantiated',
            JSON_OBJECT(
                'code':     @Code,
                'templateVersion': @TemplateVersion,
                'defId':    @WorkstreamDefId
            )
        );

        FETCH NEXT FROM ws_cursor INTO @WorkstreamDefId, @Code, @Name, @OrderIdx;
    END;

    CLOSE ws_cursor;
    DEALLOCATE ws_cursor;

    -- Period-level audit
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, Period, EntityId,
        Action, AfterJson
    )
    VALUES (
        SYSUTCDATETIME(), @ActorUserId, @ActorEntraOid,
        'ClosePeriod', @ClosePeriodId, @Period, @EntityId,
        'PeriodOpened',
        JSON_OBJECT('period': @Period, 'templateVersion': @TemplateVersion)
    );

    COMMIT;
END;
GO

-- =============================================================================
-- sp_ClosePeriod
-- Stamps ClosedAtUtc on ALL ClosePeriod rows for the given period.
-- One AuditEvent per entity row for per-entity audit trail.
-- Atomic: all entities close in one transaction.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_ClosePeriod
    @Period         char(6),
    @ActorUserId    bigint,
    @ActorEntraOid  uniqueidentifier,
    @Reason         nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    -- Must have at least one open row for this period
    IF NOT EXISTS (
        SELECT 1 FROM ClosePeriod
        WHERE Period = @Period AND ClosedAtUtc IS NULL AND IsDeleted = 0
    )
        THROW 50002, 'No open period rows found for this period.', 1;

    -- Stamp ClosedAt on all entity rows for this period
    UPDATE ClosePeriod
    SET ClosedAtUtc = SYSUTCDATETIME(), ClosedByUserId = @ActorUserId
    WHERE Period = @Period AND ClosedAtUtc IS NULL AND IsDeleted = 0;

    -- One audit event per ClosePeriod row
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, Period, EntityId,
        Action, Notes,
        AfterJson
    )
    SELECT
        SYSUTCDATETIME(), @ActorUserId, @ActorEntraOid,
        'ClosePeriod', cp.ClosePeriodId, cp.Period, cp.EntityId,
        'PeriodClosed', @Reason,
        JSON_OBJECT(
            'approvedCount':    COUNT(CASE WHEN w.Status = 'Approved' THEN 1 END),
            'notApprovedCount': COUNT(CASE WHEN w.Status != 'Approved' THEN 1 END)
        )
    FROM ClosePeriod cp
    LEFT JOIN Workstream w
        ON w.ClosePeriodId = cp.ClosePeriodId AND w.IsDeleted = 0
    WHERE cp.Period = @Period AND cp.IsDeleted = 0
    GROUP BY cp.ClosePeriodId, cp.Period, cp.EntityId;

    COMMIT;
END;
GO

-- =============================================================================
-- sp_ReopenPeriod
-- Clears ClosedAtUtc on all ClosePeriod rows for the given period.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_ReopenPeriod
    @Period         char(6),
    @ActorUserId    bigint,
    @ActorEntraOid  uniqueidentifier,
    @Reason         nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    IF NOT EXISTS (
        SELECT 1 FROM ClosePeriod
        WHERE Period = @Period AND ClosedAtUtc IS NOT NULL AND IsDeleted = 0
    )
        THROW 50003, 'No closed period rows found for this period.', 1;

    UPDATE ClosePeriod
    SET ClosedAtUtc = NULL, ClosedByUserId = NULL
    WHERE Period = @Period AND IsDeleted = 0;

    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, Period, EntityId,
        Action, Notes
    )
    SELECT
        SYSUTCDATETIME(), @ActorUserId, @ActorEntraOid,
        'ClosePeriod', cp.ClosePeriodId, cp.Period, cp.EntityId,
        'PeriodReopened', @Reason
    FROM ClosePeriod cp
    WHERE cp.Period = @Period AND cp.IsDeleted = 0;

    COMMIT;
END;
GO

-- =============================================================================
-- sp_CloseEntityInPeriod
-- Closes a single entity's ClosePeriod row (early close / individual entity).
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_CloseEntityInPeriod
    @ClosePeriodId  bigint,
    @ActorUserId    bigint,
    @ActorEntraOid  uniqueidentifier,
    @Reason         nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;
    BEGIN TRAN;

    DECLARE @Period char(6), @EntityId bigint;
    SELECT @Period = Period, @EntityId = EntityId
    FROM ClosePeriod
    WHERE ClosePeriodId = @ClosePeriodId AND ClosedAtUtc IS NULL AND IsDeleted = 0;

    IF @Period IS NULL
        THROW 50004, 'ClosePeriod row not found or already closed.', 1;

    UPDATE ClosePeriod
    SET ClosedAtUtc = SYSUTCDATETIME(), ClosedByUserId = @ActorUserId
    WHERE ClosePeriodId = @ClosePeriodId;

    -- Audit
    DECLARE @ApprCount int, @NotApprCount int;
    SELECT
        @ApprCount    = COUNT(CASE WHEN Status = 'Approved' THEN 1 END),
        @NotApprCount = COUNT(CASE WHEN Status != 'Approved' THEN 1 END)
    FROM Workstream
    WHERE ClosePeriodId = @ClosePeriodId AND IsDeleted = 0;

    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, Period, EntityId,
        Action, Notes, AfterJson
    )
    VALUES (
        SYSUTCDATETIME(), @ActorUserId, @ActorEntraOid,
        'ClosePeriod', @ClosePeriodId, @Period, @EntityId,
        'EntityClosedEarly', @Reason,
        JSON_OBJECT('approvedCount': @ApprCount, 'notApprovedCount': @NotApprCount)
    );

    COMMIT;
END;
GO

-- =============================================================================
-- sp_ExpireLocks
-- Hangfire sweep: clears locks whose LockExpiresAtUtc has passed.
-- Run every 2 minutes. Writes one LockExpired audit event per cleared lock.
-- =============================================================================
CREATE OR ALTER PROCEDURE sp_ExpireLocks
    @SystemUserId   bigint = 1,  -- System user for audit events
    @SystemEntraOid uniqueidentifier = '00000000-0000-0000-0000-000000000001'
AS
BEGIN
    SET XACT_ABORT ON;
    SET NOCOUNT ON;

    -- Collect expired locks
    DECLARE @Expired TABLE (WorkstreamId bigint, PriorUserId bigint);

    UPDATE Workstream
    SET LockedByUserId = NULL, LockedAtUtc = NULL, LockExpiresAtUtc = NULL
    OUTPUT DELETED.WorkstreamId, DELETED.LockedByUserId
    INTO @Expired
    WHERE LockExpiresAtUtc < SYSUTCDATETIME()
      AND LockedByUserId IS NOT NULL
      AND IsDeleted = 0;

    -- Audit one event per expired lock
    INSERT INTO AuditEvent (
        OccurredAtUtc, ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId, WorkstreamId,
        Action, Notes
    )
    SELECT
        SYSUTCDATETIME(), @SystemUserId, @SystemEntraOid,
        'Workstream', e.WorkstreamId, e.WorkstreamId,
        'LockExpired',
        CONCAT('Lock held by UserId=', e.PriorUserId, ' expired automatically')
    FROM @Expired e;
END;
GO
