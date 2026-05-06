-- =============================================================================
-- Close Manager — Initial schema (v1)
-- Target: SQL Server
-- =============================================================================
-- Conventions:
--   - bigint IDENTITY primary keys (int IDENTITY for small reference tables)
--   - All timestamps datetime2(3), stored UTC
--   - Period is char(6) yyyyMM
--   - Soft delete trio (IsDeleted, DeletedAtUtc, DeletedByUserId) on every
--     business table
--   - rowversion column for optimistic-write integrity in the audit pipeline
--   - Append-only AuditEvent table (no soft delete) for the audit trail
-- =============================================================================


-- =============================================================================
-- Reference data
-- =============================================================================

-- Entra (Azure AD) identity projection. Synced on login; no local credentials.
CREATE TABLE [User] (
    UserId              bigint IDENTITY PRIMARY KEY,
    EntraObjectId       uniqueidentifier NOT NULL UNIQUE,
    Upn                 nvarchar(256) NOT NULL,
    DisplayName         nvarchar(200) NOT NULL,
    IsActive            bit NOT NULL DEFAULT 1,
    LastSeenUtc         datetime2(3) NULL,
    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);

-- Entity types: RealEstateAsset, InvestmentFund, HoldingCo, OperatingCo, etc.
CREATE TABLE EntityType (
    EntityTypeId        int IDENTITY PRIMARY KEY,
    Code                varchar(40) NOT NULL UNIQUE,
    Name                nvarchar(100) NOT NULL,
    IsActive            bit NOT NULL DEFAULT 1,
    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);

-- The legal entities being closed.
CREATE TABLE Entity (
    EntityId            bigint IDENTITY PRIMARY KEY,
    EntityTypeId        int NOT NULL REFERENCES EntityType(EntityTypeId),
    Code                varchar(40) NOT NULL UNIQUE,    -- short code used in SharePoint paths
    Name                nvarchar(200) NOT NULL,
    IsActive            bit NOT NULL DEFAULT 1,
    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId     bigint NOT NULL REFERENCES [User](UserId),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);

-- Roles: Preparer, TreasuryRE, TreasuryInv, AssetMgr, ValCommittee, Senior, CFO, etc.
CREATE TABLE [Role] (
    RoleId              int IDENTITY PRIMARY KEY,
    Code                varchar(40) NOT NULL UNIQUE,
    Name                nvarchar(100) NOT NULL,
    IsActive            bit NOT NULL DEFAULT 1,
    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);

-- Per-entity role assignments. Multiple users per (entity, role) supported;
-- "anyone in the role for this entity can claim a matching workstream."
CREATE TABLE EntityRoleAssignment (
    EntityRoleAssignmentId  bigint IDENTITY PRIMARY KEY,
    EntityId                bigint NOT NULL REFERENCES Entity(EntityId),
    RoleId                  int NOT NULL REFERENCES [Role](RoleId),
    UserId                  bigint NOT NULL REFERENCES [User](UserId),
    AssignedAtUtc           datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    AssignedByUserId        bigint NOT NULL REFERENCES [User](UserId),
    IsDeleted               bit NOT NULL DEFAULT 0,
    DeletedAtUtc            datetime2(3) NULL,
    DeletedByUserId         bigint NULL,
    RowVersion              rowversion NOT NULL,
    CONSTRAINT UQ_EntityRoleAssignment UNIQUE (EntityId, RoleId, UserId)
);
CREATE INDEX IX_EntityRoleAssignment_User
    ON EntityRoleAssignment(UserId, IsDeleted);
CREATE INDEX IX_EntityRoleAssignment_Lookup
    ON EntityRoleAssignment(EntityId, RoleId, IsDeleted);


-- =============================================================================
-- Workflow templates (versioned)
-- =============================================================================
-- A template is an ordered list of workstream definitions for a given
-- entity type. Templates are versioned with effective-from periods. In-flight
-- workstreams pin to the version active at instantiation.

CREATE TABLE WorkflowTemplate (
    WorkflowTemplateId  bigint IDENTITY PRIMARY KEY,
    EntityTypeId        int NOT NULL REFERENCES EntityType(EntityTypeId),
    Version             int NOT NULL,
    EffectiveFromPeriod char(6) NOT NULL,               -- yyyyMM, prospective
    Notes               nvarchar(1000) NULL,
    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId     bigint NOT NULL REFERENCES [User](UserId),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL,
    CONSTRAINT UQ_WorkflowTemplate UNIQUE (EntityTypeId, Version)
);

-- Workstream definitions within a template version.
CREATE TABLE WorkstreamDef (
    WorkstreamDefId     bigint IDENTITY PRIMARY KEY,
    WorkflowTemplateId  bigint NOT NULL REFERENCES WorkflowTemplate(WorkflowTemplateId),
    Code                varchar(40) NOT NULL,           -- e.g. CASH, DEBT_SVC
    Name                nvarchar(100) NOT NULL,         -- e.g. "Debt service review"
    OrderIndex          int NOT NULL,                   -- display order within template
    PreparerRoleId      int NOT NULL REFERENCES [Role](RoleId),
    ReviewerRoleId      int NOT NULL REFERENCES [Role](RoleId),
    Description         nvarchar(1000) NULL,
    RowVersion          rowversion NOT NULL,
    CONSTRAINT UQ_WorkstreamDef_Code UNIQUE (WorkflowTemplateId, Code)
);

-- Default checklist items per workstream def.
CREATE TABLE WorkstreamDefChecklistItem (
    WorkstreamDefChecklistItemId bigint IDENTITY PRIMARY KEY,
    WorkstreamDefId     bigint NOT NULL REFERENCES WorkstreamDef(WorkstreamDefId),
    OrderIndex          int NOT NULL,
    Text                nvarchar(500) NOT NULL,
    Guidance            nvarchar(2000) NULL,            -- optional reviewer guidance
    IsRequired          bit NOT NULL DEFAULT 1,
    RowVersion          rowversion NOT NULL
);


-- =============================================================================
-- Close model
-- =============================================================================

-- One ClosePeriod per (period, entity). Materialized when the period opens.
CREATE TABLE ClosePeriod (
    ClosePeriodId       bigint IDENTITY PRIMARY KEY,
    EntityId            bigint NOT NULL REFERENCES Entity(EntityId),
    Period              char(6) NOT NULL,
    OpenedAtUtc         datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    OpenedByUserId      bigint NOT NULL REFERENCES [User](UserId),
    ClosedAtUtc         datetime2(3) NULL,
    ClosedByUserId      bigint NULL REFERENCES [User](UserId),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL,
    CONSTRAINT UQ_ClosePeriod UNIQUE (EntityId, Period)
);
CREATE INDEX IX_ClosePeriod_Period ON ClosePeriod(Period, IsDeleted);

-- Workstream instance: a workstream def materialized for a (period, entity).
-- The central object the UI revolves around.
--
-- Status values (enforced at the application layer):
--   NotStarted  - just instantiated, no preparer activity yet
--   InProgress  - preparer has started work (first file or checklist mark)
--   Submitted   - preparer pressed "Ready for review"
--   InReview    - reviewer has opened it (first reviewer action)
--   NeedsRevision - reviewer requested changes
--   Approved    - all checklist items approved + approve clicked
--   Rebuilt     - admin restarted; replaced by new workstream
CREATE TABLE Workstream (
    WorkstreamId        bigint IDENTITY PRIMARY KEY,
    ClosePeriodId       bigint NOT NULL REFERENCES ClosePeriod(ClosePeriodId),
    Period              char(6) NOT NULL,               -- denormalized for query speed
    EntityId            bigint NOT NULL,                -- denormalized for query speed
    WorkstreamDefId     bigint NOT NULL REFERENCES WorkstreamDef(WorkstreamDefId),

    -- Snapshot of def metadata at instantiation. Frozen so future template
    -- edits do not retroactively change historical workstreams.
    Code                varchar(40) NOT NULL,
    Name                nvarchar(100) NOT NULL,
    OrderIndex          int NOT NULL,
    PreparerRoleId      int NOT NULL,
    ReviewerRoleId      int NOT NULL,

    Status              varchar(30) NOT NULL DEFAULT 'NotStarted',
    Round               int NOT NULL DEFAULT 1,         -- review round counter

    -- Single-edit lock. Anyone with the role appropriate for the current
    -- status can acquire if no one else holds it (or theirs has expired).
    LockedByUserId      bigint NULL REFERENCES [User](UserId),
    LockedAtUtc         datetime2(3) NULL,
    LockExpiresAtUtc    datetime2(3) NULL,

    -- Aging / lifecycle timestamps
    StartedAtUtc        datetime2(3) NULL,              -- first preparer action
    SubmittedAtUtc      datetime2(3) NULL,              -- most recent submission
    SubmittedByUserId   bigint NULL REFERENCES [User](UserId),
    ReviewStartedAtUtc  datetime2(3) NULL,              -- first reviewer action
    ApprovedAtUtc       datetime2(3) NULL,
    ApprovedByUserId    bigint NULL REFERENCES [User](UserId),

    -- If rebuilt by admin, points back to the predecessor for lineage
    RebuiltFromWorkstreamId bigint NULL REFERENCES Workstream(WorkstreamId),

    CreatedAtUtc        datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId     bigint NOT NULL REFERENCES [User](UserId),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL,
    CONSTRAINT UQ_Workstream UNIQUE (ClosePeriodId, Code)
);
CREATE INDEX IX_Workstream_Status
    ON Workstream(Status, Period, IsDeleted);
CREATE INDEX IX_Workstream_ReviewerRole
    ON Workstream(ReviewerRoleId, Status, IsDeleted)
    INCLUDE (EntityId, Period, SubmittedAtUtc);
CREATE INDEX IX_Workstream_PreparerRole
    ON Workstream(PreparerRoleId, Status, IsDeleted)
    INCLUDE (EntityId, Period);
CREATE INDEX IX_Workstream_LockExpiry
    ON Workstream(LockExpiresAtUtc)
    WHERE LockedByUserId IS NOT NULL;


-- =============================================================================
-- Files (SharePoint metadata mirror)
-- =============================================================================
-- Files live in SharePoint under /{entity.Code}/{period}/{workstream.Code}/.
-- Each row represents one distinct SharePoint file. No SharePoint versioning
-- is used: replacements are new files. The replacement chain is captured via
-- ReplacesFileId, with old rows soft-deleted.

CREATE TABLE WorkstreamFile (
    WorkstreamFileId    bigint IDENTITY PRIMARY KEY,
    WorkstreamId        bigint NOT NULL REFERENCES Workstream(WorkstreamId),

    FileRole            varchar(20) NOT NULL,           -- 'Primary' | 'Supporting' | 'Reference'

    -- SharePoint identity (Graph API)
    SpDriveId           nvarchar(200) NOT NULL,
    SpItemId            nvarchar(200) NOT NULL,
    SpWebUrl            nvarchar(1000) NOT NULL,
    SpRelativePath      nvarchar(500) NOT NULL,

    -- Metadata mirror for fast queries without round-tripping to Graph
    FileName            nvarchar(255) NOT NULL,
    FileExtension       varchar(20) NULL,
    SizeBytes           bigint NULL,
    ContentHash         varbinary(32) NULL,             -- SHA-256, populated async

    UploadedAtUtc       datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    UploadedByUserId    bigint NOT NULL REFERENCES [User](UserId),

    -- Replacement chain
    ReplacesFileId      bigint NULL REFERENCES WorkstreamFile(WorkstreamFileId),

    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);
CREATE INDEX IX_WorkstreamFile_Workstream
    ON WorkstreamFile(WorkstreamId, IsDeleted, FileRole);
CREATE UNIQUE INDEX UX_WorkstreamFile_SpItem
    ON WorkstreamFile(SpDriveId, SpItemId)
    WHERE IsDeleted = 0;


-- =============================================================================
-- Checklists and comments
-- =============================================================================
-- Live checklist instance for a workstream. Items are created at
-- instantiation by cloning WorkstreamDefChecklistItem, plus reviewer- and
-- preparer-added items (where SourceDefItemId IS NULL).

CREATE TABLE ChecklistItem (
    ChecklistItemId     bigint IDENTITY PRIMARY KEY,
    WorkstreamId        bigint NOT NULL REFERENCES Workstream(WorkstreamId),
    SourceDefItemId     bigint NULL REFERENCES WorkstreamDefChecklistItem(WorkstreamDefChecklistItemId), -- NULL if added ad hoc
    AddedByUserId       bigint NOT NULL REFERENCES [User](UserId),
    AddedAtUtc          datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    OrderIndex          int NOT NULL,
    Text                nvarchar(500) NOT NULL,

    -- Preparer side
    PreparerStatus      varchar(20) NOT NULL DEFAULT 'NotReady',  -- 'NotReady' | 'Ready'
    PreparerMarkedAtUtc datetime2(3) NULL,
    PreparerMarkedByUserId bigint NULL REFERENCES [User](UserId),

    -- Reviewer side
    ReviewerStatus      varchar(20) NOT NULL DEFAULT 'Pending',   -- 'Pending' | 'Approved' | 'NeedsRevision'
    ReviewerMarkedAtUtc datetime2(3) NULL,
    ReviewerMarkedByUserId bigint NULL REFERENCES [User](UserId),

    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);
CREATE INDEX IX_ChecklistItem_Workstream
    ON ChecklistItem(WorkstreamId, IsDeleted);

-- Flat comments. May belong to a checklist item OR to the workstream as a
-- whole (when ChecklistItemId is NULL). Ordered chronologically.
CREATE TABLE Comment (
    CommentId           bigint IDENTITY PRIMARY KEY,
    WorkstreamId        bigint NOT NULL REFERENCES Workstream(WorkstreamId),
    ChecklistItemId     bigint NULL REFERENCES ChecklistItem(ChecklistItemId),
    AuthorUserId        bigint NOT NULL REFERENCES [User](UserId),
    PostedAtUtc         datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    Body                nvarchar(max) NOT NULL,
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);
CREATE INDEX IX_Comment_ChecklistItem
    ON Comment(ChecklistItemId, PostedAtUtc)
    WHERE ChecklistItemId IS NOT NULL;
CREATE INDEX IX_Comment_Workstream
    ON Comment(WorkstreamId, PostedAtUtc);

-- Optional file attachments on comments (e.g. screenshot evidence).
CREATE TABLE CommentAttachment (
    CommentAttachmentId bigint IDENTITY PRIMARY KEY,
    CommentId           bigint NOT NULL REFERENCES Comment(CommentId),
    SpDriveId           nvarchar(200) NOT NULL,
    SpItemId            nvarchar(200) NOT NULL,
    SpWebUrl            nvarchar(1000) NOT NULL,
    FileName            nvarchar(255) NOT NULL,
    SizeBytes           bigint NULL,
    UploadedAtUtc       datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    UploadedByUserId    bigint NOT NULL REFERENCES [User](UserId),
    IsDeleted           bit NOT NULL DEFAULT 0,
    DeletedAtUtc        datetime2(3) NULL,
    DeletedByUserId     bigint NULL,
    RowVersion          rowversion NOT NULL
);


-- =============================================================================
-- Audit trail
-- =============================================================================
-- Append-only. Every state-changing action writes one row in the same
-- transaction as the state change. Never updated, never deleted.

CREATE TABLE AuditEvent (
    AuditEventId        bigint IDENTITY PRIMARY KEY,
    OccurredAtUtc       datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    ActorUserId         bigint NOT NULL REFERENCES [User](UserId),
    ActorEntraObjectId  uniqueidentifier NOT NULL,      -- denormalized for forensic durability

    -- Polymorphic target
    TargetTable         varchar(40) NOT NULL,           -- e.g. 'Workstream', 'ChecklistItem'
    TargetId            bigint NOT NULL,

    -- Workstream context for fast scoped queries
    WorkstreamId        bigint NULL,
    Period              char(6) NULL,
    EntityId            bigint NULL,

    Action              varchar(40) NOT NULL,           -- e.g. 'Submitted', 'Approved', 'FileUploaded', 'CommentPosted', 'Rebuilt'
    BeforeJson          nvarchar(max) NULL,
    AfterJson           nvarchar(max) NULL,
    Notes               nvarchar(1000) NULL              -- e.g. reason text on rebuild
);
CREATE INDEX IX_AuditEvent_Workstream
    ON AuditEvent(WorkstreamId, OccurredAtUtc);
CREATE INDEX IX_AuditEvent_Target
    ON AuditEvent(TargetTable, TargetId, OccurredAtUtc);
CREATE INDEX IX_AuditEvent_Period
    ON AuditEvent(Period, OccurredAtUtc);
