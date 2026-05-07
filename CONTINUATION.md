# Continuation notes

This doc captures where the design session left off, what's decided, what's open, and what to read first when resuming. Written for the human picking this up later, but also usable by an AI assistant given access to the repo.

## Where we are

**Implementation in progress — Phase 7 next (reviewer flow).**

Design is complete. Implementation is 6 phases deep. The repository contains:

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

**Implemented so far (Phases 1–6):**

Phase 1 — Scaffold + Entra SSO + MudBlazor + Serilog + Hangfire + app shell (sidebar, nav, identity card)
Phase 2 — Full EF Core entity model + AppDbContext with all relationships; migration README; Phase 2 schema tests
Phase 3 — AuditService, AppSettingService, CurrentUserService; Roles, Users, Audit Search, Settings pages (full implementations); SettingRow component; JS interop for CSV download; Phase 3 tests
Phase 4 — Entities list + detail (Workflow/Roles/History tabs, EntityRoleAssignment management); Workflow Templates list + editor (working copy, approver modal, save dialog, navigate-away protection); Phase 4 tests
Phase 5 — Period SPs (sp_AssertPeriodOpen, sp_OpenPeriod, sp_ClosePeriod, sp_ReopenPeriod, sp_CloseEntityInPeriod, sp_ExpireLocks); PeriodService (Dapper wrappers + summary queries); OpenPeriodJob (Hangfire + SignalR progress); LockExpirySweepJob (recurring every 2min); PeriodProgressHub; PeriodsPage (full UI with live progress, close/reopen dialogs, typed confirm phrase, entity early-close); Phase 5 tests
Phase 6 — Preparer SPs (sp_AcquireLock, sp_SubmitWorkstream, sp_ApproveChecklistItem, sp_FlagChecklistItemWithComment); WorkstreamService (Dapper wrappers + work-items UNION query + dashboard query); SharePointService (Graph client, large-file upload); DashboardPage (portfolio grid, period-not-opened banner); WorkItemsPage (two sections, WorkItemTile component); PreparerItemPage (/work/{id}: file upload, version pills, checklist, submit dialog, locked-by-you banner); Phase 6 tests

## To-do, in rough priority order

### Schema and lifecycle

- [x] Rewrite `docs/schema/lifecycle-walkthrough.md` against the post-refactor schema. Done; the walkthrough now uses a 3-stage example (Preparer → Treasury-RE → Senior) and frames its SQL as the bodies of the named stored procedures (sp_AcquireLock, sp_SubmitWorkstream, sp_AdvanceStage, sp_SendBackToStage, sp_ApproveFinal, sp_RefreshChecklistFromTemplate, sp_RebuildWorkstream, plus the period-open instantiation pattern).
- [x] ~~Document the publish stored procedure and integrity check for "exactly one Final approver per workstream def."~~ Publish procedure removed in template-versioning simplification (no Draft/Published/Superseded states, no publish ceremony). The "exactly one Final approver" rule is now enforced in the editor's validation step on save (see `09-workflow-templates-editor.md`); a periodic Hangfire integrity-check job is still worth adding in implementation as defense in depth, but it's no longer documented in a separate publish procedure.
- [ ] **Add periodic integrity-check Hangfire job** that catches violations of "exactly one Review-kind stage per WorkstreamDef has IsFinalApproval = 1." Editor validation prevents this on save; the job catches drift from any other path (manual SQL, future bulk-import features, etc.). Document in tech stack / implementation kickoff.
- [x] Document the period-open process at the SQL level. Done — covered in `lifecycle-walkthrough.md` Step 1 (instantiation), updated in `07-multi-entity-considerations.md`.
- [x] Document the rebuild stored procedure (`sp_RebuildWorkstream`) and the refresh-from-template stored procedure (`sp_RefreshChecklistFromTemplate`) at SQL level. Done — both are in `lifecycle-walkthrough.md` as branch cases.

### UI screens still unsketched

- [x] **Period Management page** (`docs/design/10-period-management.md`) — list of periods (one row per yyyyMM, collapsing the per-entity ClosePeriod rows), open/close/reopen actions, per-period detail view with per-entity rows, async period-open via Hangfire job with live progress, soft-confirm close (not hard-block) with required reason, typed-confirm phrase on close, individual entity early-close, and the closed-period write-freeze enforced at the SP layer via `sp_AssertPeriodOpen`. New SPs introduced: `sp_OpenPeriod`, `sp_ClosePeriod`, `sp_ReopenPeriod`, `sp_CloseEntityInPeriod`, `sp_AssertPeriodOpen`. Lifecycle walkthrough updated with the closed-period precondition pattern.
- [x] **Roles top-level admin page** — designed in `docs/design/11-roles.md`.
- [x] **Users admin page** — designed in `docs/design/12-users.md`.
- [x] **Audit Search page** — designed in `docs/design/13-audit-search.md`.
- [x] **Work items page** — designed in `docs/design/14-work-items.md`. Combined preparer + reviewer queue; supersedes reviewer queue doc for routing purposes.
- [x] **My history page** — designed in `docs/design/15-my-history.md`.

### UI screens needing rework after multi-stage refactor

- [x] **Reviewer item page** (`docs/design/03-reviewer-item-page.md`) — rewritten for multi-stage chains. Stage indicator in header. Audit trail strip has full mode (≤2 stages, round ≤2) and compact mode (3+ stages or round ≥3) where events group by round and stage with hover/click drill-down. Prior stages accordion below the strip gives read-only access to earlier-stage checklists and comments. Action button label adapts to `IsFinalApproval` of current stage: "Advance to {next}" at non-final, "Finalize" at final. Send-back dialog lets reviewer pick a target stage (default N-1, override to any earlier stage). Reviewer-added items remain scoped to current stage. Action focus banner adapts to who handed the workstream off (preparer / prior reviewer / re-arrival after sendback). Same component for every stage; differences derive from data, not separate views.
- [x] ~~Publish dialog text — should explicitly mention stage role changes as a thing that requires rebuild for in-flight workstreams.~~ Publish dialog no longer exists; rolled into the Save dialog described in `09-workflow-templates-editor.md`, which already mentions stage role changes explicitly.

### Implementation kickoff

- [x] Set up the .NET solution — net8.0 (upgrade csproj to net9.0 when available), Blazor Server, EF Core 8, Dapper, MudBlazor, Microsoft.Identity.Web, Hangfire, Serilog. Done in Phase 1.
- [x] Implement the schema as EF Core entity model. Migration README at `CloseManager.Web/Data/Migrations/README.md`. Run `dotnet ef migrations add` + apply `PeriodSps.sql` and `PreparerSps.sql` locally. Done in Phase 2.
- [x] `sp_AssertPeriodOpen`, `sp_OpenPeriod`, `sp_ClosePeriod`, `sp_ReopenPeriod`, `sp_CloseEntityInPeriod`, `sp_ExpireLocks` — Done in Phase 5 (`Data/StoredProcedures/PeriodSps.sql`)
- [x] `sp_AcquireLock`, `sp_SubmitWorkstream`, `sp_ApproveChecklistItem`, `sp_FlagChecklistItemWithComment` — Done in Phase 6 (`Data/StoredProcedures/PreparerSps.sql`)
- [x] Set up Entra SSO — Done in Phase 1. SharePoint/Graph credentials go in AppSetting table (Settings page). Done in Phase 6.
- [x] App shell (sidebar, top bar, routing, role-based nav) — Done in Phase 1.
- [x] Dashboard (portfolio view, period-not-opened banner) — Done in Phase 6.

**Remaining SPs to build (Phase 7–8):**
- [ ] `sp_AdvanceStage` — reviewer non-final stage advance (Phase 7)
- [ ] `sp_SendBackToStage` — one-step send-back, CurrentStageIndex--, Round++ (Phase 7)
- [ ] `sp_ApproveFinal` — final stage → Status=Approved (Phase 7)
- [ ] `sp_SaveTemplate` — already stubbed in TemplateEditorPage; needs to be extracted to a real SP (Phase 4 used EF directly; extract to SP in Phase 7 or 8)
- [ ] `sp_RefreshChecklistFromTemplate` — additive in-flight refresh (Phase 8)
- [ ] `sp_RebuildWorkstream` — admin restart (Phase 8)
- [ ] `sp_ClearLock` — admin force-clear (Phase 8)

### Future enhancements (deliberately not v1)

These are noted in various docs as "future":

- Diff / compare-to-version-N UI for templates. The data is in the audit log's BeforeJson/AfterJson and in the historical version rows. Add if a real admin asks for it. Removed from v1 in the template-versioning simplification.
- Template branching (multiple drafts from different parents). Removed entirely with the Draft state; would need re-introducing if branching ever became valuable.
- Multi-session draft persistence in the templates editor. Deliberately dropped in favor of all-or-nothing save — large edits must finish in one session.
- Cross-workstream dependencies (e.g., consolidation: a holdco's financials depending on subs' approved financials)
- Standing notes / carryover items between periods
- Variance/flux engine (auto-flux green hint shown in mockups, not implemented)
- Direct accounting software integration (Yardi, NetSuite, Sage Intacct)
- Calendar-aware deadlines for "never started" detection (currently calendar days from period open)
- Multi-admin support (currently single-admin, no soft locks on drafts/working copies)
- Daily digest emails, weekly retrospective metrics

## Read order to resume

If picking this up cold, read in this order:

1. **`README.md`** at repo root — project context and design principles
2. **`docs/schema/design-decisions.md`** — the rationale behind schema choices (why no claim, why N-stage chains, why checklists per stage, why explicit final flag, etc.). This is the doc to read first because it explains the *constraints* the rest of the design works within.
3. **`docs/schema/schema.sql`** — the actual DDL. Reference this when reading any other doc.
4. **`docs/design/05-app-shell.md`** — overall navigation and routing. Quick orientation to "what pages exist."
5. **`docs/design/01-portfolio-view.md`** + `02-reviewer-queue.md` + `03-reviewer-item-page.md` + `04-preparer-flow.md` — the four end-user screens, in order of how a user would encounter them.
6. **`docs/design/07-multi-entity-considerations.md`** — the visibility model and what scale looks like.
7. **`docs/design/06-tech-stack.md`** — what to build it in, and why.
8. **`docs/design/08-active-workflows.md`** + **`docs/design/09-workflow-templates-editor.md`** + **`docs/design/10-period-management.md`** — the consequential admin pages.

The lifecycle walkthrough (`docs/schema/lifecycle-walkthrough.md`) is the SQL skeleton for the stored procedures. Read it after the schema and design-decisions docs but before implementation kickoff — it's the doc most useful when actually building the procedures.

## Decisions log (chronological)

This is the running log of design decisions, captured to avoid re-litigating them. Each was a deliberate choice with reasoning; don't reverse without understanding why.

1. **No floating discussion thread; comments live inside checklist items.** Tried and rejected the document-anchored comment model (too fragile across xlsx/docx/pptx and across versions). Comments anchor to checklist items instead.
2. **No claim, only a lock.** Earlier model had both claim (long-lived ownership) and lock (short-lived edit). Removed claim — role assignment + lock answers all the questions claim was answering. Continuity-of-reviewer is a soft convention, not enforced.
3. **Templates are versioned with prospective effective dates.** In-flight workstreams snapshot to their version; new closes use the latest published version. Three template states: Draft / Published / Superseded.
4. **The portfolio view drops fixed columns.** Tried a heatmap with one column per workstream type, then per-section column sets, then dropped columns entirely in favor of self-labeling tiles per row. Rationale in `01-portfolio-view.md`.
5. **Dashboard is one component, scoped by role assignment.** A staff accountant's Dashboard and a CFO's Dashboard are the same screen, just different row counts based on entity-role assignments. No "manager view" override.
6. **Approve button is locked until all checklist items resolve.** Approval is structural, not a separate trust act. The button shows a count ("Approve · 2 left") so reviewers know what's between them and done.
7. **No "Start" button on workstreams.** First upload implicitly transitions NotStarted → InProgress.
8. **First file uploaded is the only requirement to submit.** Not all checklist items must be marked Ready. Submit confirmation surfaces the count.
9. **Reference materials sidebar is reference-only, not a starting point.** The starting point is the accounting software export, not last period's submission file.
10. **Heatmap is a deprecated term.** Use "portfolio view" for the multi-entity grid; "Dashboard" for the user-facing label and route.
11. **Single admin assumption in v1.** No concurrent edit handling, no soft locks on drafts, no "another admin is editing" indicators.
12. **Workstream codes editable in drafts.** Joins use FK IDs, so renames are safe; snapshots on instantiation mean in-flight workstreams keep the old name.
13. **Deletions in drafts only affect future instantiations.** In-flight workstreams unaffected unless explicitly rebuilt via Active Workflows.
14. **Side-by-side compare promoted from future to v1.** Workstreams align by code, stages align by OrderIndex within workstream, items align by exact text match. Renames treated as delete+add. Reorders detected on identical text at different positions.
15. **N-stage approval chains (Option A).** Replaced two-role workstream model. `WorkstreamDefStage` defines chain in template; `WorkstreamStage` snapshots at instantiation. Status simplified by collapsing Submitted+InReview into InProgress + CurrentStageIndex pointer. NeedsRevision can rewind to any earlier stage with default of N-1.
16. **Checklist items scoped to a stage, not to the workstream.** Each approver has their own checklist. Preparer sees stage 1's checklist as a prep guide.
17. **Refresh-from-template is additive only.** New checklist items can be added to in-flight workstreams; stage role changes do not propagate. To pick up role changes, restart the workflow.
18. **Templates editor uses workstream cards with indented approver cards.** Approver cards (teal-tinted, distinct from workstream cards) show role + summary + Edit button. Clicking Edit opens a modal with role, display name, stuck threshold, final flag, and checklist items. Stage 0 (Preparer) is implicit, not shown as a card.
19. **Explicit IsFinalApproval flag rather than "highest OrderIndex wins."** Exactly one Review stage per workstream is final, enforced at application layer.
20. **Per-stage stuck thresholds.** `WorkstreamDefStage.StuckThresholdHours` (NULL = system default). Treasury reviews are quick; Senior reviews take longer; one workstream-level threshold isn't right for both.
21. **Template versioning simplified to IsCurrent bit + immutable history.** Dropped Draft/Published/Superseded states, the publish stored procedure, prospective `EffectiveFromPeriod`, and the entire diff/compare-to-version UI. Each save in the templates editor is all-or-nothing: it commits as a new `WorkflowTemplate` row with `IsCurrent = 1` and flips the prior current row to `IsCurrent = 0` (in `sp_SaveTemplate`). Filtered unique index `UX_WorkflowTemplate_Current` enforces "exactly one current per entity type" at the DB level. Versioning still exists — the version column and the immutable history of rows is necessary for FK lineage (`Workstream.WorkstreamDefId` always resolves) and for snapshot stability (in-flight workstreams pin to whatever version they were instantiated against). What's been dropped is the lifecycle/UI complexity, not the underlying immutability. Edit history that used to be displayed via a diff UI is now in the audit log's BeforeJson/AfterJson; visual comparison can be added later if anyone asks for it.
22. **Reviewer item page is one component, parameterized by current stage.** Stage indicator in header, button label that adapts to IsFinalApproval ("Advance to {next}" vs "Finalize"), checklist scope filtered to the current stage, prior-stage history exposed via a read-only accordion below the audit trail strip rather than as a separate tab. Audit trail has two display modes: full (≤2 stages, round ≤2) and compact (3+ stages or round ≥3) where events group by round and stage with drill-down. Send-back dialog lets the reviewer pick a target stage with N-1 as the default. The same component renders for every reviewer in the chain; differences are derived from data (CurrentStageIndex, joined WorkstreamStage row, IsFinalApproval) rather than from a separate per-stage view.
23. **Period management: rolled-up list, per-entity detail, soft-confirm close with write freeze.** The Periods list is one row per yyyyMM (collapsing per-entity ClosePeriod rows); detail expands inline to show per-entity status. Open is async via Hangfire with live progress via SignalR push, idempotent at the entity level for safe retries on partial failures. Close is a soft-confirm (not hard-block) on not-yet-approved workstreams, surfaced in the dialog with required reason and a typed-confirm phrase to prevent wrong-period mistakes. Closing a period freezes write paths on its workstreams, enforced at the SP level via `sp_AssertPeriodOpen` called as the first action in every state-transition SP. Reopen is allowed (and audited). Individual entities can be closed early ahead of the period-wide close. The schema needs no changes — open/closed/reopened are all expressible by toggling `ClosedAtUtc` and writing audit events. Adding entities to an already-open period instantiates them against the *current* template version, even if other entities in the period are on an older version (consistent with "new closes always instantiate from IsCurrent = 1"); rare version mismatch within a period is surfaced in the entity row and resolvable via per-workstream rebuild.
24. **Visibility and chain authorization use the same role-assignment table but answer different questions.** A role can have `EntityRoleAssignment` rows without ever appearing in any `WorkstreamDefStage` row. The Dashboard query joins user → `EntityRoleAssignment` → entities, granting visibility regardless of whether the role is in any chain. The lock-acquisition query joins `EntityRoleAssignment.RoleId` to `WorkstreamStage[CurrentStageIndex].RoleId`, so a role with no stage presence can never acquire a lock — making the role-holder a pure observer. Canonical use case: a "CFO" role assigned to all entities for portfolio visibility, never present in any workstream's stage chain. If a specific workstream genuinely needs the CFO as an approver, that's a separate explicit decision (add a CFO stage to that workstream's template or entity override). Two independent decisions, served by two tables already in the schema; no observer-role mechanism, no permission table, no schema change. The convention works because the org is small enough to manage role assignments on entity creation by hand.

25. **Dashboard visibility is through entity assignment only, not through stage presence.** The Dashboard query joins `Workstream` to `EntityRoleAssignment` on `EntityId` alone — not through `WorkstreamStage.RoleId`. A CFO with entity assignments but no presence in any stage chain sees the full portfolio. Write access (lock acquisition) separately requires the role to match the current stage's `RoleId`. The two queries are independent: one for visibility, one for action eligibility.
26. **Send-back is always one step (N-1). No target-stage picker.** Pressing "Needs revision" sets `CurrentStageIndex--` and `Round++`. There is no dialog asking which stage to send to. If a reviewer at stage N wants to escalate a concern further back, they send to N-1 with a comment and let N-1 decide whether to go further. Stage 0 (Preparer) has no "Needs revision" button.
27. **Round increments on every send-back, not just stage-0 submissions.** Round is a count of how many times any reviewer pushed back. This drives the "Round ≥ 4" attention condition in Active Workflows more faithfully than the prior stage-0-only model.
28. **`RewoundToStageIndex` removed from `WorkstreamStage`.** One-step-only send-back makes it redundant. The audit event's `fromStage`/`toStage` fields in the Notes JSON capture direction without a dedicated column.
29. **Ad-hoc checklist items: you can add to your own stage or the immediately previous stage.** A reviewer at stage N can add items to stage N (their own checklist) or stage N-1 (asking the prior stage to verify something before it comes back). No adding to stages further back or further forward. Preparer (stage 0) can add items to stage 1 only (the stage they submit to).
30. **No notifications in v1.** Users open the app to check their Work Items queue. The team is small enough that this is acceptable. Notification infrastructure (email, Teams) is a future enhancement.
31. **Entity-level workflow overrides deferred to post-v1.** All entities use their type's standard template. The override modeling in the schema and portfolio view design remains for future use.
32. **`sp_OpenForReview` removed from the SP inventory.** It was a no-op wrapper around lock acquisition. `sp_AcquireLock` is sufficient.
33. **System settings live in an `AppSetting` key/value table.** Only the DB connection string lives in `appsettings.json`. All other configuration (stuck thresholds, lock duration, period close confirm phrase, SharePoint/Graph credentials) is in `AppSetting`. Keys are fixed at deployment via seed data; values are admin-editable via the Settings page. Secret values (`IsSecret = 1`) are masked in the UI and in audit log JSON.
34. **Settings page designed (`docs/design/16-settings.md`).** Two sections: personal preferences (all users — theme, sidebar, density) and system settings (admin only — grouped key/value editor with SharePoint connection test button).

35. **Reviewer load panel cut from Dashboard.** For a 12-person co-located team, queue depth is a conversation. The Work Items page already shows each person their own load. Add if team grows or goes remote.
36. **Dashboard grouping toggles cut.** "By accountant" and "by reviewer" groupings removed. Entity-type grouping only. Small team handles this conversationally.
37. **"Expected later today" Work Items section cut.** Inference logic will be wrong often enough to erode trust. Two sections only: Needs Attention and Up Next / In Progress.
38. **Dual audit trail strip modes simplified to one.** A single compact chronological mode replaces the full/compact switching logic. Easier to build, easier to test, consistent for users.
39. **Template history UI cut from v1.** Historical versions stay in the DB for FK lineage. Audit log BeforeJson/AfterJson covers "what changed." Add UI if admins ask post-go-live.
40. **"Group by period" toggle cut from My History.** The period dropdown filter already scopes the view. Two ways to do the same thing adds confusion without adding capability.
41. **Keyboard navigation (J/K) on reviewer queue deferred.** For a team reviewing 5-7 items at a time, mouse navigation is fine. Add if reviewers request it.
42. **"Locked by you in another session" warning added.** When a user opens a workstream they already have locked elsewhere, a banner offers to transfer the lock to the current window. Applies to both preparers and reviewers. Prevents "why can't I edit my own workstream?" confusion.
43. **Period-not-opened Dashboard banner added.** When no period exists for the current month, the Dashboard shows a clear explanation rather than an empty grid: "The {month} close hasn't been opened yet." One conditional render.
44. **File version history added to preparer (and reviewer) item page.** A version pill row (v1 → v2 → v3 current) sits above the document viewer at all times. Each pill is clickable. Derived from `WorkstreamFile.ReplacesFileId`; no schema change.
45. **Lock expiry tooltip added to Work Items locked tiles.** "Locked 43 minutes ago · expires in 2 minutes" appears on hover, giving the waiting person context on whether to wait or come back. Low effort, high clarity.
46. **Unsaved-changes browser warning added to template editor.** A `beforeunload` event fires when the working copy has changes and the user tries to navigate away. Prevents accidental loss of large restructures.

## What to build next — Phase 7 (reviewer flow)

Phase 7 adds the reviewer-side state transitions and completes the core close cycle.

### SPs to write (Data/StoredProcedures/ReviewerSps.sql)

**sp_AdvanceStage(@WorkstreamId, @UserId, @ActorEntraOid)**
- Assert period open
- Assert all ChecklistItems for current stage have ReviewerStatus = 'Approved'
- Stamp current stage Outcome = 'Advanced', CompletedAtUtc, CompletedByUserId
- Enter next stage (set EnteredAtUtc)
- CurrentStageIndex++, release lock
- Audit event: 'StageAdvanced'

**sp_SendBackToStage(@WorkstreamId, @UserId, @ActorEntraOid, @Reason)**
- Assert period open
- Assert CurrentStageIndex > 0 (cannot send back from stage 0)
- Stamp current stage Outcome = 'SentBack'
- Clear prior stage Outcome/CompletedAt/CompletedByUserId (re-enter cleanly)
- CurrentStageIndex--, Round++, Status = 'NeedsRevision', release lock
- Audit event: 'SentBack' with Reason in Notes

**sp_ApproveFinal(@WorkstreamId, @UserId, @ActorEntraOid)**
- Assert period open
- Assert all ChecklistItems for current stage have ReviewerStatus = 'Approved'
- Assert current WorkstreamStage.IsFinalApproval = 1
- Stamp stage Outcome = 'Advanced', CompletedAtUtc
- Status = 'Approved', ApprovedAtUtc, ApprovedByUserId, release lock
- Audit event: 'FinalApproved'

### Pages to build

**ReviewerItemPage** — extends PreparerItemPage pattern for the reviewer role
- Route: `/work/{workstreamId}` (same route, different view based on CurrentStageIndex)
  - At CurrentStageIndex = 0 and user is Preparer → show PreparerItemPage
  - At CurrentStageIndex > 0 and user matches stage role → show ReviewerItemPage
  - Detect which view to render in the page's OnInitializedAsync by querying role
- Stage indicator header: "Stage 2 of 3 · Senior review (final)"
- Checklist: stage-scoped items with Approve / Flag buttons (call SPs)
- Flag dialog: reason-only (no stage picker — one-step send-back)
- Advance/"Finalize" button: locked until all items Approved; label adapts to IsFinalApproval
- "Needs revision" button: opens reason dialog → calls sp_SendBackToStage
- Audit trail strip: single compact chronological mode (from AuditEvent)
- Prior stages accordion: read-only view of completed stages' checklists/comments
- File version pill row (same as preparer — reviewer needs to see version chain)
- Locked-by-you banner (same pattern as preparer)

**My History page** (`/history`) — currently placeholder
- Timeline grouped by calendar date, action chip filters, period dropdown
- Expandable rows for BeforeJson/AfterJson
- CSV export
- Deep-links from other pages

### Tests
- sp_AdvanceStage: advances stage, blocks if checklist items unresolved
- sp_ApproveFinal: sets Status=Approved, blocks if not on final stage or items unresolved
- sp_SendBackToStage: CurrentStageIndex--, Round++, prior stage cleared, throws at stage 0
- Full 3-stage happy path: submit → advance stage 1 → advance stage 2 (final) → Status=Approved
- Send-back re-advance: assert round++, stage reverts and re-advances correctly

## Things to be wary of when implementing

- **The audit trail must be non-bypassable.** Implement state transitions as stored procedures that do the state change and the audit insert in one transaction. Application code calls the procedures rather than issuing UPDATEs directly.
- **The lock acquisition rule must check role appropriateness.** Anyone with the right role for the *current stage* can acquire the lock; the SQL pattern is in `07-multi-entity-considerations.md`.
- **Concurrent reviewers and lock auto-expiry.** Hangfire job runs every minute or two to null out expired locks and write `LockExpired` audit events.
- **Don't conflate operational logging with the business audit trail.** Serilog → file/Application Insights for ops; `AuditEvent` table for SOX-relevant business audit. Both exist; they're separate.
- **Status transitions are conditional.** Use `WHERE Status = 'X'` clauses on UPDATEs to prevent re-firing transitions; use `@@ROWCOUNT` to detect "someone got there first" cases.
- **`EntraObjectId` is denormalized into `AuditEvent`** so the audit trail remains intelligible for years after a User row has been soft-deleted.
- **Send-back is one step only — no `RewoundToStageIndex`.** The column was removed (decision #28). The audit event's Notes JSON captures fromStage/toStage. Round increments on every send-back, not just stage-0 submissions (decision #27).

## Open questions for next conversation

These came up during design but were deferred. Worth revisiting before implementation:

- ~~**Dashboard visibility model.**~~ Resolved: visibility through entity assignment only (decision #25).
- **Calendar-aware close schedule.** The "never started" Active Workflows condition uses calendar days from `OpenedAtUtc`. Should there be a `CloseSchedule` table that knows business days, holidays, planned milestones? Probably yes, but punted.
- **Notification model.** How and when do users get notified when work lands on them? Email? Teams? The schema has `AuditEvent` as the source of truth from which notifications can be derived; the projection has not been designed.
- **Reporting and metrics.** Round count distribution, average sitting time, throughput by reviewer. Built on top of audit log queries. Not designed; not in v1; worth thinking about which metrics matter early.
- **Bulk admin operations.** Active Workflows supports bulk select + bulk action, but the UX for "bulk apply this template change to 50 entities" hasn't been designed. May not be needed if templates are the right unit.
- **Backfill / migration.** This is a new build, no data migration. But once live, how do new entities get their initial role assignments populated? Self-service per-admin? Bulk import? Punted.

## Repository operational notes

- **GitHub repo:** https://github.com/pcastanhas/CloseManager
- **Branch:** main
- **Last commit at writeup time:** Phase 6 complete — preparer flow, SharePoint service, Work Items, Dashboard, PreparerItemPage
- **Git author identity:** Set as `pcastanhas <pcastanhas@users.noreply.github.com>` (GitHub noreply email)
- **Authentication:** A GitHub PAT was used during the design session for pushing commits. **That PAT must be rotated** if it hasn't been already; it was leaked in the conversation log. Future commits should use a fresh PAT or SSH from a controlled environment.

## Visual style for implementation

For consistency when building the Blazor UI, the design has been worked out with these conventions (notes in `docs/mockups/README.md`):

- Flat, minimal aesthetic. No gradients, drop shadows, neon effects.
- 0.5px borders in muted colors; generous whitespace.
- Sentence case throughout. No title case, no all-caps.
- Two font weights: 400 regular, 500 medium. Avoid 600/700.
- Color palette: green for done, amber for in-progress/blocking, red for stuck/failing, blue for informational, teal for approver cards (the multi-stage variant).
- Status badges are pills with low-opacity backgrounds and dark text from the same color family.
- Tabler icons throughout; matched in MudBlazor.
- Destructive actions visually distinct (red tint on backgrounds, alert-triangle icons).
- Confirmation dialogs follow a generalized pattern: header + affected items + what-happens + reason field + (optionally) typed-confirm phrase + locked submit button. See `08-active-workflows.md` for the canonical example.
