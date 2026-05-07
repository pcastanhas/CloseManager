# Continuation notes

This doc captures where the design session left off, what's decided, what's open, and what to read first when resuming. Written for the human picking this up later, but also usable by an AI assistant given access to the repo.

## Where we are

**Implementation in progress — Phase 8 next (admin operations).**

Design is complete. Implementation is 7 phases deep. The solution builds clean with zero errors and zero warnings.

**Design docs (complete):**
- Full schema (`docs/schema/schema.sql`), design decisions (`docs/schema/design-decisions.md`), lifecycle walkthrough (`docs/schema/lifecycle-walkthrough.md`)
- 16 design docs in `docs/design/` covering all screens and admin pages
- `BUILDPLAN.md` — 9-phase implementation plan with per-phase test checklist

**Code — solution structure:**
```
CloseManager.sln
  CloseManager.Web/        — Blazor Server app (net8.0, target net9.0 when available)
  CloseManager.Tests/      — xUnit + bUnit + Testcontainers
```

**Implemented so far (Phases 1–7):**

Phase 1 — Scaffold + Entra SSO + MudBlazor + Serilog + Hangfire + app shell (sidebar, nav, identity card)
Phase 2 — Full EF Core entity model + AppDbContext with all relationships; migration README; Phase 2 schema tests
Phase 3 — AuditService, AppSettingService, CurrentUserService; Roles, Users, Audit Search, Settings pages (full implementations); SettingRow component; JS interop for CSV download; Phase 3 tests
Phase 4 — Entities list + detail (Workflow/Roles/History tabs, EntityRoleAssignment management); Workflow Templates list + editor (working copy, approver modal, save dialog, navigate-away protection); Phase 4 tests
Phase 5 — Period SPs (sp_AssertPeriodOpen, sp_OpenPeriod, sp_ClosePeriod, sp_ReopenPeriod, sp_CloseEntityInPeriod, sp_ExpireLocks); PeriodService (Dapper wrappers + summary queries); OpenPeriodJob (Hangfire + SignalR progress); LockExpirySweepJob (recurring every 2min); PeriodProgressHub; PeriodsPage (full UI with live progress, close/reopen dialogs, typed confirm phrase, entity early-close); Phase 5 tests
Phase 6 — Preparer SPs (sp_AcquireLock, sp_SubmitWorkstream, sp_ApproveChecklistItem, sp_FlagChecklistItemWithComment); WorkstreamService (Dapper wrappers + work-items UNION query + dashboard query); SharePointService (Graph client, large-file upload); DashboardPage (portfolio grid, period-not-opened banner); WorkItemsPage (two sections, WorkItemTile component); PreparerItemPage (/work/{id}: file upload, version pills, checklist, submit dialog, locked-by-you banner); Phase 6 tests
Phase 7 — Reviewer SPs (sp_AdvanceStage, sp_SendBackToStage, sp_ApproveFinal); ReviewerItemPage (/work/{id}/review: stage indicator, audit trail strip, prior-stages accordion, checklist with Approve/Flag/Undo, Advance/Finalize/Send-back dialogs, ad-hoc item add); HistoryPage (/history: date-grouped timeline, action chip filters, period dropdown, CSV export); WorkstreamService reviewer methods + GetMyHistoryAsync; routing wired (PreparerItemPage redirects to review route when stage > 0); Phase 7 tests (11 tests)

**Build status:** Clean — 0 errors, 0 warnings as of last commit.

**Notable fixes applied during build stabilization (for awareness):**
- MudBlazor v7 breaking changes: `@bind-IsVisible` → `@bind-Visible` on all MudDialogs; `Title` attribute removed from MudIconButton/MudButton (replaced with MudTooltip wrappers); `Action`/`OnClick` removed from MudAlert (replaced with inline MudButton); `Dense` removed from MudDatePicker; `ForceLoad` removed from MudButton
- Graph SDK v5: `client.Sites[id].Drives[id]` → `client.Drives[id]`; `Root.ItemWithPath()` → `Items["root:/path:"]`
- `AdminOptions` class was missing — added to `Auth/AdminOptions.cs`
- `UserSyncService.GetEntraObjectId` made `public` (was `private`, needed by tests + CurrentUserService)
- `Microsoft.AspNetCore.SignalR.Client` package added to csproj (was missing, used in PeriodsPage)
- `CloseManager.Web.Graph` and `CloseManager.Web.Jobs` usings added where missing
- CS8669 suppressed project-wide (Razor codegen nullable annotation artifact)
- bunit version bumped 1.34.4 → 1.35.3 to match NuGet resolution

## What to build next — Phase 8 (admin operations)

Phase 8 adds the admin-side workstream management tools that let admins unstick, refresh, and rebuild in-flight workstreams.

### SPs to write (Data/StoredProcedures/AdminSps.sql)

**sp_RefreshChecklistFromTemplate(@WorkstreamId, @UserId, @ActorEntraOid)**
- Additive only — resolves current template by entity type and workstream code
- Inserts missing checklist items by exact text match (no duplicates, no deletions)
- Scopes items to the correct WorkstreamStage by OrderIndex
- Audit event with addedCount in Notes

**sp_ClearLock(@WorkstreamId, @UserId, @ActorEntraOid, @Reason)**
- Nulls LockedByUserId, LockedAtUtc, LockExpiresAtUtc
- Audit event `LockForceCleared` with BeforeJson capturing prior lock holder name + userId

**sp_RebuildWorkstream(@WorkstreamId, @UserId, @ActorEntraOid, @Reason)**
- Marks old workstream Status = 'Rebuilt'
- Instantiates fresh workstream from current template (same pattern as sp_OpenPeriod)
- Links via RebuiltFromWorkstreamId
- Audit events on both old and new workstream rows

**sp_SaveTemplate(@WorkflowTemplateId, @ActorUserId, @ActorEntraOid, @Notes, @WorkstreamsJson)**
- Currently TemplateEditorPage uses EF directly — extract to a proper SP
- New version commit: inserts new WorkflowTemplate row with IsCurrent=1
- Flips prior current row to IsCurrent=0 in same transaction
- Inserts WorkstreamDef, WorkstreamDefStage, WorkstreamDefChecklistItem child rows from JSON
- Audit event with BeforeJson/AfterJson of full template structure
- Filtered unique index UX_WorkflowTemplate_Current enforces one current per entity type

### Pages to build

**ActiveWorkflowsPage** (`/admin/active-workflows`) — currently placeholder
- Attention condition filter chips with counts: Stuck >24h, Lock held >4h, Round ≥4, Template behind, Never started
- Search bar, bulk select checkboxes
- Bulk action buttons: Refresh checklist, Clear locks, Restart (rebuild)
- Confirmation dialogs per action — Restart requires typed confirm phrase
- Per-row actions: individual clear lock, refresh, rebuild
- Design reference: `docs/design/08-active-workflows.md`

### Tests (Phase8/AdminOpsTests.cs)
- sp_RefreshChecklistFromTemplate: add item to template (v2), call refresh on v1-instantiated workstream, assert new item added and existing items unchanged
- sp_ClearLock: lock held by user A, admin calls clear, assert lock fields null and LockForceCleared audit event with lockedBy=userId A
- sp_RebuildWorkstream: assert old Status='Rebuilt', new RebuiltFromWorkstreamId set, new workstream has fresh checklist from current template
- Active Workflows: workstream stuck >24h appears in Stuck chip count

## SP reference — implementation status

**Period lifecycle** ✅ all done (Phase 5)
- sp_AssertPeriodOpen, sp_OpenPeriod, sp_ClosePeriod, sp_ReopenPeriod, sp_CloseEntityInPeriod

**Workstream state transitions** ✅ all done (Phases 6–7)
- sp_AcquireLock, sp_ExpireLocks, sp_SubmitWorkstream, sp_ApproveChecklistItem, sp_FlagChecklistItemWithComment
- sp_AdvanceStage, sp_SendBackToStage, sp_ApproveFinal

**Admin operations** ⬜ Phase 8
- sp_SaveTemplate (Phase 8 — currently EF direct in TemplateEditorPage)
- sp_RefreshChecklistFromTemplate (Phase 8)
- sp_ClearLock (Phase 8)
- sp_RebuildWorkstream (Phase 8)

## Open questions for next session

- **Calendar-aware close schedule** — "never started" Active Workflows condition uses calendar days from OpenedAtUtc. Should there be a CloseSchedule table that knows business days and holidays? Punted from v1.
- **Notification model** — how and when do users get notified when work lands on them? Email? Teams? AuditEvent is the source of truth for derivation. Not designed; not in v1.
- **Reporting and metrics** — round count distribution, average sitting time, throughput by reviewer. Phase 9 hardening may include performance baselines.
- **Bulk admin operations** — Active Workflows supports bulk select + bulk action, but "bulk apply this template change to 50 entities" UX hasn't been designed.

## Repository operational notes

- **GitHub repo:** https://github.com/pcastanhas/CloseManager
- **Branch:** main
- **Last commit at writeup time:** Phase 7 complete + full build stabilization (zero errors/warnings)
- **Git author identity:** Set as `pcastanhas <pcastanhas@users.noreply.github.com>`
- **Authentication:** Provide a fresh short-lived PAT at the start of each session

## Read order to resume

1. `README.md` — project context and design principles
2. `CONTINUATION.md` (this file) — current status and what to build next
3. `BUILDPLAN.md` Phase 8 section — detailed checklist
4. `docs/design/08-active-workflows.md` — Active Workflows page design
5. `docs/schema/lifecycle-walkthrough.md` — SP skeleton for RefreshChecklistFromTemplate and RebuildWorkstream
6. `CloseManager.Web/Data/StoredProcedures/PreparerSps.sql` — pattern to follow for new SPs
7. `CloseManager.Web/Features/Periods/PeriodsPage.razor` — pattern for admin pages with bulk actions

## Visual style conventions

- Flat, minimal. No gradients, drop shadows, neon.
- 0.5px borders in muted colors; generous whitespace.
- Sentence case throughout. No title case, no all-caps.
- Two font weights: 400 regular, 500 medium.
- Color palette: green done, amber in-progress/blocking, red stuck/failing, blue informational, teal approver cards.
- Status badges: pills with low-opacity backgrounds.
- Tabler icons via MudBlazor.
- Destructive actions: red tint backgrounds, alert-triangle icons.
- Confirmation dialogs: header + affected items + what-happens + reason field + optional typed-confirm phrase + locked submit.
- MudBlazor v7 notes: use @bind-Visible (not @bind-IsVisible) on MudDialog; use MudTooltip wrapper instead of Title= on buttons; no Action/OnClick on MudAlert — use inline MudButton instead.
