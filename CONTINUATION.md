# Continuation notes

This doc captures where the design session left off, what's decided, what's open, and what to read first when resuming. Written for the human picking this up later, but also usable by an AI assistant given access to the repo.

## Where we are

Design phase. No code has been written. The repository contains:

- A full schema (`docs/schema/schema.sql`) covering users, entities, workflow templates (versioned with IsCurrent bit), multi-stage workstreams, files, checklists, comments, and audit events
- Schema design decisions (`docs/schema/design-decisions.md`) — the why behind specific schema choices, including the simplified template versioning model
- Lifecycle walkthrough (`docs/schema/lifecycle-walkthrough.md`) — current; written against the post-refactor schema using a 3-stage example, with SQL framed as the bodies of the named stored procedures
- Nine design docs in `docs/design/` covering portfolio view, reviewer queue, reviewer item page, preparer flow, app shell, tech stack, multi-entity considerations, active workflows, and workflow templates editor
- Mockups README in `docs/mockups/` indexing the visual sketches produced in chat

## To-do, in rough priority order

### Schema and lifecycle

- [x] Rewrite `docs/schema/lifecycle-walkthrough.md` against the post-refactor schema. Done; the walkthrough now uses a 3-stage example (Preparer → Treasury-RE → Senior) and frames its SQL as the bodies of the named stored procedures (sp_AcquireLock, sp_SubmitWorkstream, sp_AdvanceStage, sp_SendBackToStage, sp_ApproveFinal, sp_RefreshChecklistFromTemplate, sp_RebuildWorkstream, plus the period-open instantiation pattern).
- [x] ~~Document the publish stored procedure and integrity check for "exactly one Final approver per workstream def."~~ Publish procedure removed in template-versioning simplification (no Draft/Published/Superseded states, no publish ceremony). The "exactly one Final approver" rule is now enforced in the editor's validation step on save (see `09-workflow-templates-editor.md`); a periodic Hangfire integrity-check job is still worth adding in implementation as defense in depth, but it's no longer documented in a separate publish procedure.
- [ ] **Add periodic integrity-check Hangfire job** that catches violations of "exactly one Review-kind stage per WorkstreamDef has IsFinalApproval = 1." Editor validation prevents this on save; the job catches drift from any other path (manual SQL, future bulk-import features, etc.). Document in tech stack / implementation kickoff.
- [x] Document the period-open process at the SQL level. Done — covered in `lifecycle-walkthrough.md` Step 1 (instantiation), updated in `07-multi-entity-considerations.md`.
- [x] Document the rebuild stored procedure (`sp_RebuildWorkstream`) and the refresh-from-template stored procedure (`sp_RefreshChecklistFromTemplate`) at SQL level. Done — both are in `lifecycle-walkthrough.md` as branch cases.

### UI screens still unsketched

- [ ] **Period Management page** — list of periods, ability to open a new period for selected entities, ability to close a period. Probably simple. Confirm-on-close for non-fully-approved periods.
- [ ] **Roles top-level admin page** — define system-wide role types. CRUD for the `Role` table. Likely the simplest admin page.
- [ ] **Users admin page** — read-mostly view of `User` table since identity is sourced from Entra. Showing entity-role assignments per user, ability to soft-delete inactive users.
- [ ] **Audit Search page** — full-text search over `AuditEvent`. Critical for SOX auditor work. Filterable by user, target table, target ID, period, action, date range.
- [ ] **Work items page** — the user's queue of items waiting on them right now. Similar to reviewer queue but role-aware (shows preparer items if they have prep work, reviewer items if they have approvals due, scoped to current stage).
- [ ] **My history page** — personal view of audit events. Scoped to events where `ActorUserId = current user`.

### UI screens needing rework after multi-stage refactor

- [ ] **Reviewer item page** (`docs/design/03-reviewer-item-page.md` + mockups) — was designed for two-stage flow. With multi-stage, the audit trail strip becomes longer (more stages = more events). Needs a more compact representation when chains are 3+ stages. Also the per-stage checklist needs to be clarified at runtime — reviewers see only their own stage's checklist, but should be able to see prior stages' completed checklists in a "history" tab or accordion.
- [x] ~~Publish dialog text — should explicitly mention stage role changes as a thing that requires rebuild for in-flight workstreams.~~ Publish dialog no longer exists; rolled into the Save dialog described in `09-workflow-templates-editor.md`, which already mentions stage role changes explicitly.

### Implementation kickoff

- [ ] Set up the .NET solution. Recommended: ASP.NET Core 9 + Blazor Server, EF Core 9 + Dapper for hot paths, MudBlazor, Microsoft.Identity.Web, Hangfire, Serilog. Rationale in `docs/design/06-tech-stack.md`.
- [ ] Implement the schema as EF Core migrations, applied via deployment pipeline.
- [ ] Build stored procedures for state transitions. The full list, with bodies sketched in `docs/schema/lifecycle-walkthrough.md`: `sp_OpenPeriod` (instantiation), `sp_AcquireLock` (lock + role check), `sp_SubmitWorkstream` (stage 0 → stage 1, also handles round increment on resubmit from NeedsRevision), `sp_AdvanceStage` (non-final stage advance), `sp_SendBackToStage` (rewind to a chosen earlier stage), `sp_ApproveFinal` (final stage → Approved), `sp_ApproveChecklistItem` (item Approved), `sp_FlagChecklistItemWithComment` (item NeedsRevision + Comment in one transaction), `sp_SaveTemplate` (template versioning save), `sp_RefreshChecklistFromTemplate` (additive in-flight refresh), `sp_RebuildWorkstream` (admin restart), `sp_ClearLock` (admin force-clear), `sp_ExpireLocks` (Hangfire auto-expiry).
- [ ] Set up Entra SSO and confirm Microsoft Graph access to SharePoint (`Sites.Selected` permission on the specific site).
- [ ] Build out the app shell first — sidebar, top bar, routing, role-based nav visibility.
- [ ] Build the Dashboard (portfolio view) next — most users land here on first login.

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
8. **`docs/design/08-active-workflows.md`** + **`docs/design/09-workflow-templates-editor.md`** — the two consequential admin pages.

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

## Things to be wary of when implementing

- **The audit trail must be non-bypassable.** Implement state transitions as stored procedures that do the state change and the audit insert in one transaction. Application code calls the procedures rather than issuing UPDATEs directly.
- **The lock acquisition rule must check role appropriateness.** Anyone with the right role for the *current stage* can acquire the lock; the SQL pattern is in `07-multi-entity-considerations.md`.
- **Concurrent reviewers and lock auto-expiry.** Hangfire job runs every minute or two to null out expired locks and write `LockExpired` audit events.
- **Don't conflate operational logging with the business audit trail.** Serilog → file/Application Insights for ops; `AuditEvent` table for SOX-relevant business audit. Both exist; they're separate.
- **Status transitions are conditional.** Use `WHERE Status = 'X'` clauses on UPDATEs to prevent re-firing transitions; use `@@ROWCOUNT` to detect "someone got there first" cases.
- **`EntraObjectId` is denormalized into `AuditEvent`** so the audit trail remains intelligible for years after a User row has been soft-deleted.
- **The multi-stage rewind rule sets `RewoundToStageIndex`** on the `WorkstreamStage` row that initiated the rewind, not on the rewind target. This captures the intent for audit.
- **Round only counts stage-0 submissions.** A workstream that bounces between stages 1 and 2 without involving the preparer doesn't increment Round.

## Open questions for next conversation

These came up during design but were deferred. Worth revisiting before implementation:

- **Calendar-aware close schedule.** The "never started" Active Workflows condition uses calendar days from `OpenedAtUtc`. Should there be a `CloseSchedule` table that knows business days, holidays, planned milestones? Probably yes, but punted.
- **Notification model.** How and when do users get notified when work lands on them? Email? Teams? The schema has `AuditEvent` as the source of truth from which notifications can be derived; the projection has not been designed.
- **Reporting and metrics.** Round count distribution, average sitting time, throughput by reviewer. Built on top of audit log queries. Not designed; not in v1; worth thinking about which metrics matter early.
- **Bulk admin operations.** Active Workflows supports bulk select + bulk action, but the UX for "bulk apply this template change to 50 entities" hasn't been designed. May not be needed if templates are the right unit.
- **Backfill / migration.** This is a new build, no data migration. But once live, how do new entities get their initial role assignments populated? Self-service per-admin? Bulk import? Punted.

## Repository operational notes

- **GitHub repo:** https://github.com/pcastanhas/CloseManager
- **Branch:** main
- **Last commit at writeup time:** Templates editor with workstream cards + indented approver cards + edit modal pattern
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
