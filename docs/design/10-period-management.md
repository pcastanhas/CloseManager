# Period management

The Period Management page is where admins open new close periods, monitor in-flight ones, and close completed periods. It lives under Operations in the sidebar (route: `/admin/periods`), alongside Active Workflows.

Period management is operationally significant — opening a period materializes thousands of rows; closing a period freezes write paths on workstreams in that period. Both actions are deliberate, audited, and slow enough that the UI has to handle progress and errors gracefully.

## What "a period" means on this page

A period like "October 2025" exists in the schema as **one `ClosePeriod` row per included entity** — opening October 2025 across 100 entities creates 100 rows. The Period Management page collapses that detail: each list row represents a logical period (a yyyyMM value), and the per-entity breakdown is exposed on click.

This collapse matters because admins think in terms of "the October close," not "Plaza Tower's October ClosePeriod row." The detail view exists for the legitimate cases (one entity needs to be closed early, one needs to be added late), but the default presentation is the rolled-up view.

## Periods list

The default page is a vertical list of periods, ordered most-recent-first. Each row shows:

- **Period label** in long form: "October 2025"
- **Status badge**: "Open" (green-tinted), "Closing soon" (amber, derived — see below), or "Closed" (neutral, with closed-on date)
- **Entity coverage**: "47 of 52 active entities included" — the count of `ClosePeriod` rows for this period vs the count of currently-active entities. Discrepancies are normal (some entities don't run on this cadence) but worth surfacing.
- **Progress**: "318 of 412 workstreams approved (77%)" with a thin segmented progress bar
- **Aging**: "Opened 23 days ago" for open periods; "Closed 2 days ago" for closed
- **Action menu**: Open / Close / Reopen depending on state, plus a "View detail" link

Color coding follows the system convention: green for healthy/done, amber for attention-needed (a period open past its expected close date, see below), red for stuck (workstreams blocking close).

The "Closing soon" badge appears on periods whose progress is high (>90% approved) and aging is past a configurable threshold (default 21 calendar days from open). It's a soft nudge to admins that this period is ready to be closed.

### Empty / first-run state

When no periods exist, the list shows a "Get started" affordance: a single button "Open your first period" that opens the standard period-open dialog with no defaults selected. Subsequent periods always default to the prior period's entity selection.

## Opening a period

The "Open period" button at the top of the list opens the period-open dialog.

### Period-open dialog

The dialog has four sections:

1. **Period to open** — a yyyyMM picker showing month and year. Defaults to the next month after the most-recent-opened period. If October 2025 is the most recent, the default is November 2025. Going backward is allowed (the schema allows any period value), but a confirmation appears: "You're opening a period earlier than your most recent. This is unusual; continue?"

2. **Entities to include** — a two-column list:
   - **Selected** (default-checked): all entities that were included in the most recent prior period and are still active. This is the common case — month-over-month closes share entity sets.
   - **Available** (default-unchecked): currently-active entities *not* in the prior period's selection. New entities since the last close show a "new since {prior period}" badge.
   - **Inactive entities** are not shown. If an admin needs to include a soft-deleted entity, they have to reactivate it first.

   Both columns are searchable. Bulk select-all / deselect-all available per column. The selection state is preserved if the dialog is closed and reopened (until the page is refreshed).

3. **Template version preview** — a read-only summary listing each selected entity type and the current template version (`IsCurrent = 1`) it'll instantiate from. "RealEstateAsset → v7 (3 stages, 5 workstreams, 30 checklist items)". This is the receipt that admins see what will materialize before clicking Open.

4. **Confirm and open** — a button that kicks off the period-open job. The dialog closes; the periods list shows the new period with a "Opening..." badge while the Hangfire job runs.

The dialog does *not* require a reason field. Opening a period is a constructive action with low audit weight — the audit log records who opened it and what entity set was included; the action itself is reversible (an empty period can be soft-deleted by admin if it was opened in error before any work was done).

### Async open with progress

Opening a period across 100 entities produces ~10-20k rows. The schema-level work runs as a Hangfire job (per `06-tech-stack.md`), not synchronously in the request.

The periods list shows the in-flight period with:

- A spinning indicator instead of the entity-count number
- "Opening period... 47 of 100 entities materialized" updating live (Blazor Server SignalR pushes job progress)
- "Estimated completion: ~30s" based on row throughput

If the job fails partway:

- The list row turns amber with "Open partially completed — 73 of 100 entities" 
- A "Retry remaining" action becomes available, which re-runs the job for unmaterialized entities only
- Already-materialized entities are not duplicated (the per-(entity, period) UNIQUE constraint on `ClosePeriod` prevents that)

### Idempotency and partial-state recovery

The open job is idempotent at the entity level: running it twice for the same period just skips entities that already have a `ClosePeriod` row. This means "Retry remaining" is safe to click, and partial failures don't corrupt the period — they just leave it incomplete until retry succeeds.

The instantiation transaction is per-entity, not per-period: each entity's `ClosePeriod` + `Workstream` + `WorkstreamStage` + `ChecklistItem` rows commit together in one transaction. If a single entity fails (template validation error, FK issue, anything), only that entity's transaction rolls back; other entities continue. The job records per-entity success/failure for the retry path.

## Period detail view

Clicking a period row expands an inline detail view (or routes to `/admin/periods/{period}` for a deeper view; both work, the inline expansion is preferred for the common case). The detail shows per-entity rows:

- Entity name and entity type
- Workstream count: "5 workstreams · 3 approved · 2 in progress"
- Aging: "opened 23 days ago"
- Status indicator: ● green if all approved, ● amber if some in progress, ● red if any stuck
- Per-entity actions: "Close this entity early" (if all workstreams Approved), "View workstreams" (links to the portfolio view filtered to this entity-period)

This per-entity view answers the question "which entities are holding up this close?" at a glance. The amber/red dots draw the eye; the action lets admins close individual entities ahead of the period-wide close when they're ready.

### Adding entities to an already-open period

The detail view has an "Add entity" button that adds a single entity to the current period (creating its `ClosePeriod` row + workstreams). This handles the case "we forgot to include Widget Industries in the October close" — happens, needs to work without forcing a full period reopen.

The added entity instantiates against the current template version at the moment the addition is made. There's no retroactive "use the version that was current when the period opened" logic because templates are immutable per version: if v7 was current at period open and v8 is current now, v8 is what the new entity gets. This may or may not match what other entities in the same period are on; the design call is to use the current template (consistent with "new closes always instantiate from `IsCurrent = 1`") and surface the version mismatch in the entity row's display ("v8 — others in this period are on v7").

If the version mismatch matters (different checklist requirements within the same close), admins can `sp_RebuildWorkstream` the affected entity's workstreams to bring them onto v8 too. This is rare and deliberate.

## Closing a period

The "Close period" action on a period's row opens the close dialog. This is a destructive action — closing freezes write paths on workstreams in this period — so it follows the canonical destructive-confirm pattern from `08-active-workflows.md`.

### Close-period dialog

1. **Header** — "Close October 2025" + counts: "47 entities · 412 workstreams · 318 approved (77%)"
2. **What you're affecting** — the count of not-yet-approved workstreams broken down by status:
   - "94 workstreams are not yet approved:"
     - 12 InProgress
     - 8 NeedsRevision
     - 74 NotStarted (likely abandoned)
   - "These will be frozen in their current state when closed. They cannot be submitted, advanced, or approved after close. They can be revisited if the period is reopened."
3. **What happens** — plain language: "Closing the period stamps `ClosedAtUtc` on each entity's `ClosePeriod` row. Workstreams in `Approved` state are unaffected. Workstreams not yet approved will block on any state-transition action until the period is reopened."
4. **Reason field** — required. Placeholder: "Closing reason — what's the audit story for not-yet-approved workstreams? (e.g., 'Carried 8 items to November per audit committee approval')". This field is stamped into the audit event for the close.
5. **Typed confirmation phrase** — required for irreversible-feeling actions. Type "close 202510" to enable submit. The phrase matches the period being closed; this prevents accidental closing of the wrong period when admins have multiple browser tabs open.
6. **Submit button** — disabled until reason and phrase are valid. Enabled state: "Close period."

Soft-confirm rather than hard-block on not-yet-approved workstreams. Real closes have edge cases — abandoned workstreams that won't ever finish, last-minute "we'll address that in November" situations. Forcing 100% completion turns the close button into a bureaucratic obstacle. The dialog surfacing the count and requiring an audit-readable reason gives the SOX trail what it needs.

### What happens on close

`sp_ClosePeriod(@Period, @ActorUserId, @Reason)`:

1. UPDATE all `ClosePeriod` rows for @Period: set `ClosedAtUtc = SYSUTCDATETIME()`, `ClosedByUserId = @ActorUserId`.
2. INSERT one `AuditEvent` per `ClosePeriod` row with `Action = 'PeriodClosed'`, `Notes = @Reason`, `AfterJson = '{"approvedCount": N, "notApprovedCount": M}'`.
3. The reason is the same on every row (it's a period-level decision); the per-entity audit rows let auditors filter by entity later.

Closing is one transaction across all entities for the period — it's a single logical action and should commit atomically.

### Write-path enforcement

Closing freezes writes on workstreams in the closed period. This is enforced in the state-transition stored procedures, not at the page level:

- `sp_SubmitWorkstream`, `sp_AdvanceStage`, `sp_SendBackToStage`, `sp_ApproveFinal`, `sp_ApproveChecklistItem`, `sp_FlagChecklistItemWithComment`, `sp_AcquireLock` all need to add a precondition: the workstream's `ClosePeriodId` must reference a `ClosePeriod` row with `ClosedAtUtc IS NULL`.
- If the precondition fails, the procedure THROWs with a specific error code (50050, "Period closed") that the UI translates to "This period has been closed. Reopen it to make changes."

This is a schema-level invariant that the lifecycle walkthrough doesn't currently spell out. It needs to be added there as part of implementing close-as-freeze.

> **Implementation note for the SP rollout:** every state-transition SP needs the closed-period precondition. The cleanest pattern is a small helper proc `sp_AssertPeriodOpen(@WorkstreamId)` that THROWs if the period is closed; each state-transition SP calls it as the first action.

## Reopening a period

Closed periods can be reopened. The "Reopen" action is on closed-period rows in the list. It opens a small confirmation:

1. **Header** — "Reopen October 2025"
2. **What you're affecting** — "47 entities · 94 workstreams that were frozen at close will become editable again."
3. **Reason field** — required. Placeholder: "Why are you reopening this closed period?"
4. **Submit button** — "Reopen period" — confirmation-only, no typed phrase. Reopening is reversible (you can close again) and less destructive than closing.

`sp_ReopenPeriod(@Period, @ActorUserId, @Reason)`:

1. UPDATE all `ClosePeriod` rows for @Period: set `ClosedAtUtc = NULL`, `ClosedByUserId = NULL`.
2. INSERT one `AuditEvent` per row with `Action = 'PeriodReopened'`, `Notes = @Reason`.

The audit trail captures both the original close (with its reason) and the reopen (with its reason), so the full history is reconstructable.

## Closing individual entities ahead of period close

The detail view's "Close this entity early" action lets admins close one entity's `ClosePeriod` without affecting others in the same period. Useful when one entity finishes early and admins want to lock it down before the rest of the close is done.

The dialog is the same shape as the period-wide close, but scoped to one entity:

- Counts reflect the single entity's workstreams
- Action stamps `ClosedAtUtc` only on that entity's `ClosePeriod` row
- The same write-path freeze applies to that entity's workstreams

The period-wide row in the list shows "47 of 47 entities · 12 closed early" so admins can see how many were closed individually before the period itself was closed.

## What this page is not

- **Not a place to edit workstreams.** Workstream-level admin actions live on Active Workflows. Period Management only handles period lifecycle and per-entity inclusion.
- **Not a calendar or schedule.** The "expected close date" is a derived value (default 21 days from open, configurable), not a stored date. If a real close-schedule mechanism is ever built, it would be a separate feature with its own table.
- **Not the place to add or remove entities at the org level.** Entity active/inactive state lives in the entity admin pages. This page only chooses which currently-active entities participate in a given period.

## Schema sketch — does anything need to change?

The current `ClosePeriod` table is sufficient:

```sql
CREATE TABLE ClosePeriod (
    ClosePeriodId       bigint IDENTITY PRIMARY KEY,
    EntityId            bigint NOT NULL REFERENCES Entity(EntityId),
    Period              char(6) NOT NULL,
    OpenedAtUtc         datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    OpenedByUserId      bigint NOT NULL REFERENCES [User](UserId),
    ClosedAtUtc         datetime2(3) NULL,
    ClosedByUserId      bigint NULL REFERENCES [User](UserId),
    -- soft-delete trio + RowVersion
);
```

Open / closed / reopened are all expressible by toggling `ClosedAtUtc` and writing audit events. No new columns needed.

One small potential addition that's worth deferring: a `ClosePeriodNote` table (one row per close/reopen action, with reason text and actor) would centralize the reason history without relying solely on `AuditEvent.Notes`. But `AuditEvent` is already the source of truth for this kind of history, so adding a parallel table just for periods would be redundant. Stick with audit events.

## Implementation notes for kickoff

- **Stored procedures**: `sp_OpenPeriod` (per-entity, called in a loop by the Hangfire job), `sp_ClosePeriod`, `sp_ReopenPeriod`, `sp_CloseEntityInPeriod` (single-entity early close), `sp_AssertPeriodOpen` (helper called by state-transition SPs).
- **The state-transition SPs need a `sp_AssertPeriodOpen` call as their first action** to enforce the closed-period write freeze. Add this when building those SPs; document the dependency in the lifecycle walkthrough at SP-build time.
- **Hangfire job: `OpenPeriodJob`** — takes a period and a list of entity IDs, calls `sp_OpenPeriod` for each, reports progress via Blazor Server SignalR. Idempotent at the entity level so retries are safe.
- **Live progress requires Blazor Server's SignalR push.** The page subscribes to job-progress events and updates the row in place rather than polling.
- **The periods list query** should be a single SELECT joining `ClosePeriod` to `Workstream` (with COUNT and CASE aggregates for the status breakdown). For ~100 entities × 12 months × 5 workstreams = ~6000 rows, this aggregates fast; a materialized view isn't needed.

## Future enhancements (deliberately not v1)

- Close schedule / calendar awareness. Currently "expected close date" is a flat 21-day default. A real `CloseSchedule` table per entity (or per entity type) would let the page show "expected to close on day N" / "X days late" honestly. The CONTINUATION.md open-questions list flags this; not in v1.
- Per-period notifications. When an admin closes a period or a long-frozen period gets reopened, the affected reviewers/preparers might want a Teams/email ping. Derives from `AuditEvent`; build the projection when notifications come online generally.
- Bulk close (close November and December together). Easy to add; not needed in v1.
- Period templates ("our standard close opens these 47 entities"). The default-from-prior-period behavior covers the common case; explicit period templates are a v2 thing if real admins ask.
