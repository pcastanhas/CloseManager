# Build plan — CloseManager

## Principles

- Every phase ends with something runnable and testable end-to-end.
- You are never building blind for more than one phase.
- Phases build on each other: later phases assume earlier ones are complete and passing.
- Stored procedures are the state-transition boundary — application code calls procedures, never issues UPDATEs directly against business tables.
- Every state-changing action writes an `AuditEvent` in the same transaction as the state change. Non-bypassable.

## Solution structure

Two projects:

```
CloseManager.sln
  CloseManager.Web/          -- Blazor Server app; everything lives here
    Features/
      Dashboard/
      WorkItems/
      History/
      Periods/
      Templates/
      ActiveWorkflows/
      Admin/
        Roles/
        Users/
        AuditSearch/
      Settings/
    Data/
      AppDbContext.cs
      Migrations/
      StoredProcedures/      -- Dapper call wrappers, one class per SP group
    Jobs/
      LockExpirySweep.cs
      PeriodOpenJob.cs
    Graph/
      SharePointService.cs
    Auth/
      UserSyncService.cs
  CloseManager.Tests/        -- xUnit + bUnit + Testcontainers
```

---

## Phase 1 — Solution scaffold and auth

**Done when:** sign in with an Entra account, see your display name in the sidebar identity card, get redirected to login if unauthenticated.

- [ ] Create solution and two projects (`CloseManager.Web`, `CloseManager.Tests`)
- [ ] Install core packages:
  - `Microsoft.Identity.Web` + `Microsoft.Identity.Web.UI`
  - `MudBlazor`
  - `Serilog.AspNetCore`
  - `Hangfire.AspNetCore` + `Hangfire.SqlServer`
  - `Dapper`
  - `Microsoft.EntityFrameworkCore.SqlServer`
  - `Microsoft.Graph`
  - `FluentValidation.AspNetCore`
- [ ] Wire Entra SSO in `Program.cs` — unauthenticated requests redirect to login
- [ ] `UserSyncService` — upsert `User` row on sign-in from Entra claims (`EntraObjectId`, `DisplayName`, `Upn`, `LastSeenUtc`)
- [ ] App shell: persistent sidebar, top bar with breadcrumb, footer identity card — static nav, no role-gating yet
- [ ] `appsettings.json` — DB connection string only; all other config comes from `AppSetting` (Phase 2)
- [ ] Serilog wired to console and file sink; request logging middleware in place

**Tests:**
- [ ] App starts, redirects unauthenticated user to Entra login
- [ ] After sign-in, `User` row exists in DB with correct `EntraObjectId` and `DisplayName`
- [ ] Sidebar renders with placeholder nav items

---

## Phase 2 — Schema and migrations

**Done when:** `dotnet ef database update` on a fresh SQL Server produces the full schema with no errors; seed data present in `AppSetting` and reference tables.

- [ ] EF Core `AppDbContext` — all tables mapped as entity classes (match `docs/schema/schema.sql` exactly)
- [ ] Initial migration — full schema from `schema.sql`
- [ ] Seed migration — `AppSetting` rows (all 8 keys; SharePoint values null at deployment)
- [ ] Seed migration — `EntityType` reference rows (RealEstateAsset, InvestmentFund, HoldingCo, OperatingCo)
- [ ] Seed migration — `Role` reference rows (Preparer, TreasuryRE, TreasuryInv, AssetMgr, Senior, CFO, etc.)
- [ ] `System` user row (UserId=1) for automated/Hangfire actions
- [ ] Verify all indexes and unique constraints from schema are present in the migration

**Tests (Testcontainers):**
- [ ] Apply migrations to a fresh SQL Server container — assert zero errors
- [ ] Assert `AppSetting` has exactly 8 rows with correct keys
- [ ] Assert `EntityType` and `Role` seed rows exist
- [ ] Assert filtered unique index `UX_WorkflowTemplate_Current` is present (test by attempting two IsCurrent=1 rows for the same EntityTypeId)

---

## Phase 3 — Admin reference data

**Done when:** create a role, rename it, deactivate it; see your own user appear; verify audit events written correctly; search audit events; configure AppSettings.

- [ ] `AuditService` — shared helper wrapping `AuditEvent` insert; all subsequent features use this
- [ ] Admin auth gate — read Entra admin group membership on sign-in; cache on principal; show/hide admin sidebar items
- [ ] **Roles page** (`/admin/roles`) — table, add modal, edit modal (code locked if in use), deactivate/reactivate
- [ ] **Users page** (`/admin/users`) — table with filter chips, deactivate/reactivate, detail panel (assignments + recent activity)
- [ ] **Settings page** (`/settings`) — personal preferences section (theme, density, sidebar default); system settings section (key/value editor grouped by prefix, secret masking, save per group, SharePoint connection test button)
- [ ] `AppSettingService` — typed accessor for all `AppSetting` keys; used by the rest of the app instead of raw DB reads
- [ ] **Audit Search page** (`/admin/audit`) — filter panel (actor, period, entity, target table, action, date range, workstream ID), results table with expandable rows (BeforeJson/AfterJson), URL-persisted filters, CSV export
- [ ] Role-based nav: non-admins see only "Your work" section in sidebar

**Tests:**
- [ ] Create role → audit event `RoleCreated` written with correct AfterJson
- [ ] Rename role → `RoleRenamed` event written with before/after
- [ ] Deactivate role → `RoleDeactivated` event written; role no longer appears in active dropdowns
- [ ] Deactivate user → `UserDeactivated` written; user's next request gets deactivated screen
- [ ] Audit Search: filter by actor, assert only that actor's events returned
- [ ] Audit Search: CSV export contains correct columns including `ActorEntraObjectId`
- [ ] Settings: save `StuckThreshold.Default` → `AppSettingUpdated` audit event written; value readable via `AppSettingService`
- [ ] Settings: secret field masked in UI; `AppSettingUpdated` audit event shows `[secret]` not actual value

---

## Phase 4 — Entity setup and workflow templates

**Done when:** create an entity, assign users to roles on it, define a template with 2 workstreams and 2 stages each, save as v1; verify all child rows created in the DB.

- [ ] **Entities list** (`/admin/entities`) — table of entities with type badge, active status, edit/deactivate
- [ ] **Entity detail** (`/admin/entities/{id}`) — sub-tabs: Workflow, Roles, Thresholds, History
  - Workflow sub-tab: assigned template version, workstream list (read-only preview)
  - Roles sub-tab: `EntityRoleAssignment` grid — assign/remove users per role for this entity
  - Thresholds sub-tab: per-entity stuck threshold override toggles
  - History sub-tab: deep-link to `/admin/audit?entityId={id}`
- [ ] **Workflow Templates list** (`/admin/templates`) — one row per entity type; current version, last edited, entity count, history link
- [ ] **Workflow Templates editor** (`/admin/templates/{id}`) — workstream cards, approver cards (teal tint), drag-to-reorder, edit-approver modal (role, display name, stuck threshold, final flag, checklist items), save dialog
- [ ] `sp_SaveTemplate` — new version commit, prior version `IsCurrent → 0`, audit event with BeforeJson/AfterJson of full template structure
- [ ] Validation: exactly one `IsFinalApproval = 1` per workstream on save; blocked until valid
- [ ] Template history view — read-only list of historical versions; click opens editor in read-only mode

**Tests:**
- [ ] Save template v1 → `WorkflowTemplate` row with `IsCurrent=1`, child `WorkstreamDef`, `WorkstreamDefStage`, `WorkstreamDefChecklistItem` rows created
- [ ] Save template v2 → v1 flips to `IsCurrent=0`; filtered unique index prevents two current rows
- [ ] Attempt save with no final approver on a workstream → validation blocks; no DB write
- [ ] Assign user to role on entity → `EntityRoleAssignment` row created; audit event written
- [ ] Entity detail History sub-tab → links to Audit Search pre-filtered to that entity

---

## Phase 5 — Period management and workstream instantiation

**Done when:** open a period, verify all rows materialize; close it, verify write-freeze; reopen it.

- [ ] `sp_AssertPeriodOpen(@WorkstreamId)` — helper called by all state-transition SPs; THROWs 50050 if period closed
- [ ] `sp_OpenPeriod(@EntityId, @Period, @ActorUserId)` — per-entity instantiation: `ClosePeriod` + `Workstream` + `WorkstreamStage` + `ChecklistItem` rows; idempotent (skips if `ClosePeriod` row already exists for this entity-period)
- [ ] `sp_ClosePeriod(@Period, @ActorUserId, @Reason)` — stamps `ClosedAtUtc` on all entity rows for the period; one audit event per entity
- [ ] `sp_ReopenPeriod(@Period, @ActorUserId, @Reason)` — clears `ClosedAtUtc`; audit events
- [ ] `sp_CloseEntityInPeriod(@ClosePeriodId, @ActorUserId, @Reason)` — single-entity early close
- [ ] `OpenPeriodJob` — Hangfire job; iterates entity list, calls `sp_OpenPeriod` per entity, reports progress via SignalR; handles per-entity failure without aborting other entities
- [ ] **Period Management page** (`/admin/periods`) — list (rolled-up by yyyyMM), inline detail expansion, open/close/reopen dialogs with correct confirm patterns, live progress during open job, "Add entity" button on detail view
- [ ] Lock expiry Hangfire sweep — `sp_ExpireLocks` running every 2 minutes; register in `Program.cs`

**Tests (Testcontainers):**
- [ ] `sp_OpenPeriod` for 3 entities → assert 3 `ClosePeriod` rows, correct `Workstream` count per entity, checklist items scoped to correct `WorkstreamStage`
- [ ] `sp_OpenPeriod` called twice for same entity-period → idempotent; no duplicate rows
- [ ] `sp_ClosePeriod` → `ClosedAtUtc` set; `sp_AssertPeriodOpen` on any workstream in that period throws 50050
- [ ] `sp_ReopenPeriod` → `ClosedAtUtc` cleared; `sp_AssertPeriodOpen` no longer throws
- [ ] `sp_CloseEntityInPeriod` → only that entity's `ClosePeriod` row closed; other entities in same period unaffected
- [ ] Lock expiry sweep: insert a workstream with `LockExpiresAtUtc` in the past; run sweep; assert lock cleared and `LockExpired` audit event written

---

## Phase 6 — Preparer flow

**Done when:** sign in as a preparer, see a workstream in Work Items, upload a file to SharePoint, mark checklist items ready, submit; workstream advances to stage 1.

- [ ] `SharePointService` — `UploadFile`, `GetDriveUrl`; reads credentials from `AppSettingService`; returns clear error if SharePoint not configured
- [ ] `sp_AcquireLock(@WorkstreamId, @UserId, @LockMinutes)` — role check via `EntityRoleAssignment` join to current `WorkstreamStage`; `@@ROWCOUNT` guard for contention
- [ ] `sp_SubmitWorkstream(@WorkstreamId, @UserId)` — validates primary file exists; advances `CurrentStageIndex`; stamps stage 0 `Outcome = 'Advanced'`; releases lock; audit event
- [ ] `sp_ApproveChecklistItem(@ChecklistItemId, @UserId)` — sets `PreparerStatus = 'Ready'` (stage-0 callers) or `ReviewerStatus = 'Approved'` (stage N callers) based on caller's current stage
- [ ] `sp_FlagChecklistItemWithComment(@ChecklistItemId, @UserId, @CommentBody)` — sets `ReviewerStatus = 'NeedsRevision'`; inserts `Comment` in same transaction
- [ ] **Preparer item page** (`/work/{workstreamId}`) — file upload zone (primary + supporting), file version pill row (v1→v2→v3 derived from ReplacesFileId), locked-by-you session banner, prep checklist (stage 1 items as guide with PreparerStatus), submit button locked until primary file exists, submit confirmation modal
- [ ] **Work Items page** (`/work`) — two sections (Needs Attention / Up Next+In Progress); preparer tiles with pencil icon; locked tile with expiry tooltip; locked-by-you session warning; sidebar badge count
- [ ] **Dashboard** (`/`) — portfolio view; entity rows grouped by type (no grouping toggles); workstream tiles with status color, name, current stage; entity-type section headers with aggregate badges; period-not-opened banner
- [ ] Lock contention handling: `sp_AcquireLock` returns 0 rows → UI queries current lock holder and shows "Locked by {name}, expires in N min"

**Tests:**
- [ ] Full preparer happy path (Testcontainers): open period → acquire lock → upload file (mocked Graph) → mark 3 items ready → submit → assert `CurrentStageIndex = 1`, stage-0 `Outcome = 'Advanced'`, lock released
- [ ] Submit without primary file → SP throws; UI shows validation message
- [ ] Lock contention: two users attempt `sp_AcquireLock` concurrently; assert exactly one succeeds
- [ ] Work Items: preparer with 2 NotStarted + 1 NeedsRevision workstreams → correct section placement
- [ ] Dashboard: assert workstreams visible to user match their `EntityRoleAssignment` rows (entity-only join, not stage join)

---

## Phase 7 — Reviewer flow

**Done when:** sign in as a reviewer, approve all checklist items, advance through the chain to final approval; send back one step, verify `Round++` and `CurrentStageIndex--`; sign in as the prior-stage person, see the item in Needs Attention, address it, re-advance.

- [ ] `sp_AdvanceStage(@WorkstreamId, @UserId)` — all current-stage items must be `ReviewerStatus = 'Approved'`; `CurrentStageIndex++`; stamps stage `Outcome = 'Advanced'`; releases lock; throws if called on final stage
- [ ] `sp_SendBackToStage(@WorkstreamId, @UserId, @Reason)` — `CurrentStageIndex--`; `Round++`; `Status = 'NeedsRevision'`; stamps current stage `Outcome = 'SentBack'`; clears prior stage's `Outcome`/`CompletedAt`; releases lock; throws if `CurrentStageIndex = 0`
- [ ] `sp_ApproveFinal(@WorkstreamId, @UserId)` — same item check as `sp_AdvanceStage`; `Status = 'Approved'`; stamps `ApprovedAtUtc`, `ApprovedByUserId`; releases lock; throws if current stage is not `IsFinalApproval = 1`
- [ ] **Reviewer item page** (`/work/{workstreamId}`) — stage indicator header ("Stage 2 of 3 · Senior review (final)"), audit trail strip (single compact chronological mode), prior stages accordion (read-only), file version pill row, locked-by-you session banner, checklist scoped to current stage, advance/"Finalize" button locked until all items approved, "Needs revision" button opens reason-only dialog
- [ ] Work Items page — reviewer tiles added (checkmark icon); tile shows stage chain with current stage bolded, checklist progress bar
- [ ] **My History page** (`/history`) — timeline grouped by date, action chips filter, expand row for BeforeJson/AfterJson, "group by period" toggle, CSV export

**Tests (Testcontainers):**
- [ ] Full 3-stage happy path: submit → advance stage 1 → advance stage 2 (final) → assert `Status = 'Approved'`, `ApprovedAtUtc` set
- [ ] `sp_AdvanceStage` with unresolved checklist item → throws; `Status` unchanged
- [ ] `sp_ApproveFinal` called on non-final stage → throws
- [ ] `sp_SendBackToStage`: assert `CurrentStageIndex--`, `Round++`, `Status = 'NeedsRevision'`, prior stage `Outcome` cleared
- [ ] `sp_SendBackToStage` at stage 0 → throws
- [ ] Re-advance after send-back: `Status` returns to `InProgress`, `CurrentStageIndex` increments
- [ ] Work Items: NeedsRevision workstream appears in Needs Attention for the stage-N-1 person
- [ ] My History: `SentBack` event appears under correct date group with reason text

---

## Phase 8 — Admin operations

**Done when:** find a stuck workstream in Active Workflows, clear its lock, refresh checklist from template, rebuild it; all actions write correct audit events.

- [ ] `sp_RefreshChecklistFromTemplate(@WorkstreamId, @UserId)` — additive only; resolves current template by entity type and workstream code; inserts missing checklist items by exact text match; audit event with `addedCount`
- [ ] `sp_ClearLock(@WorkstreamId, @UserId, @Reason)` — nulls lock fields; audit event `LockForceCleared` with BeforeJson capturing prior lock holder
- [ ] `sp_RebuildWorkstream(@WorkstreamId, @UserId, @Reason)` — marks old `Status = 'Rebuilt'`; instantiates fresh workstream from current template; links via `RebuiltFromWorkstreamId`; audit events on both old and new
- [ ] **Active Workflows page** (`/admin/active-workflows`) — attention condition filter chips (Stuck >24h, Lock held >4h, Round ≥4, Template behind, Never started) with counts; search; bulk select; bulk action buttons (Refresh, Clear locks, Restart); confirmation dialogs per action pattern; typed confirm phrase for Restart
- [ ] Periodic Hangfire integrity-check job — scans `WorkstreamDef` rows in current templates for any with no `IsFinalApproval = 1` stage; logs warning via Serilog; does not auto-fix
- [ ] Full Audit Search wired to real data (was built in Phase 3 but only had reference-data events; now has workstream events to query)

**Tests:**
- [ ] `sp_RefreshChecklistFromTemplate`: add item to template (save v2); call refresh on v1-instantiated workstream; assert new item added, existing items unchanged
- [ ] `sp_ClearLock`: lock held by user A; admin calls clear; assert lock fields null, `LockForceCleared` audit event with `lockedBy = userId A`
- [ ] `sp_RebuildWorkstream`: assert old workstream `Status = 'Rebuilt'`, new workstream `RebuiltFromWorkstreamId` set, new workstream has fresh checklist from current template
- [ ] Active Workflows: workstream stuck >24h appears in Stuck chip count; clearing the lock removes it from the Lock chip count
- [ ] Integrity-check job: insert a `WorkstreamDef` with no final stage; run job; assert Serilog warning logged

---

## Phase 9 — Hardening and go-live readiness

**Done when:** full test suite passes; performance within targets; error paths handled gracefully.

- [ ] bUnit component tests:
  - Work Items tile renders correct section for each status
  - Send-back dialog blocks submit until reason field filled
  - Advance button disabled with correct tooltip when checklist items remain
  - Locked tile shows lock holder name and expiry tooltip ("expires in N min")
  - Locked-by-you banner appears when `LockedByUserId = currentUser`
  - File version pill row renders correct count and highlights current version
  - Period-not-opened banner renders when no ClosePeriod rows exist for current month
  - Template editor fires `beforeunload` warning when working copy has changes
  - Settings secret field masks value; shows on toggle
- [ ] Testcontainers stress tests:
  - `sp_AssertPeriodOpen` called from every state-transition SP on a closed period — assert all throw 50050
  - Concurrent lock acquisition: 10 simultaneous `sp_AcquireLock` calls for same workstream — assert exactly 1 succeeds
  - Lock expiry under load: 50 expired locks; sweep runs; all cleared in one job execution
- [ ] Performance:
  - Dashboard query with 100 entities × 7 workstreams → assert <200ms
  - Work Items query for user with 20 assignments → assert <100ms
  - Audit Search with 100k rows, filtered by actor + period → assert <500ms
- [ ] Error handling:
  - SharePoint upload fails (Graph returns 503) → UI shows clear message; no partial `WorkstreamFile` row left
  - SP THROW propagates correctly through Dapper to Blazor component → user sees human-readable error, not stack trace
  - Hangfire job fails mid-period-open → partial state survives; "Retry remaining" works
- [ ] Serilog structured logging review — confirm ops logs and business audit trail are cleanly separated
- [ ] Security review:
  - Non-admin cannot reach any `/admin/*` route
  - User cannot acquire lock on workstream where their role doesn't match current stage
  - Closed-period write-freeze tested via direct SP calls (not just UI paths)
- [ ] Deployment checklist: migrations auto-apply on startup; three environments (dev/staging/prod) documented; rollback procedure documented

---

## SP reference — full list

For tracking implementation progress. All called via Dapper or `ExecuteSqlRaw`; none issued as raw UPDATEs from application code.

**Period lifecycle**
- [ ] `sp_AssertPeriodOpen` (Phase 5)
- [ ] `sp_OpenPeriod` (Phase 5)
- [ ] `sp_ClosePeriod` (Phase 5)
- [ ] `sp_ReopenPeriod` (Phase 5)
- [ ] `sp_CloseEntityInPeriod` (Phase 5)

**Workstream state transitions**
- [ ] `sp_AcquireLock` (Phase 6)
- [ ] `sp_ExpireLocks` (Phase 5 — Hangfire sweep)
- [ ] `sp_SubmitWorkstream` (Phase 6)
- [ ] `sp_ApproveChecklistItem` (Phase 6)
- [ ] `sp_FlagChecklistItemWithComment` (Phase 6)
- [ ] `sp_AdvanceStage` (Phase 7)
- [ ] `sp_SendBackToStage` (Phase 7)
- [ ] `sp_ApproveFinal` (Phase 7)

**Admin operations**
- [ ] `sp_SaveTemplate` (Phase 4)
- [ ] `sp_RefreshChecklistFromTemplate` (Phase 8)
- [ ] `sp_ClearLock` (Phase 8)
- [ ] `sp_RebuildWorkstream` (Phase 8)
