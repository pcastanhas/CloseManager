# Work items page

Route: `/work`
Sidebar section: Your work (all users)

## What this page is for

Work Items is the user's action queue — a list of workstreams where they have something to do right now. It is distinct from the Dashboard in a fundamental way:

- **Dashboard** answers "what is the overall close status across my entities?" — a status-monitoring surface; mostly read-only.
- **Work items** answers "what do I need to touch today?" — an action surface; every item on this page is waiting on the current user.

A staff accountant arriving for the morning checks Work Items first. It surfaces exactly the workstreams that need them and nothing else. A workstream that is stuck waiting on a different reviewer is not their problem and should not appear here.

## Who sees what

The page is scoped to `ActorUserId = current user`. The query finds workstreams where the user is the expected actor at the current stage:

**Preparer items:** Workstreams where:
- `CurrentStageIndex = 0` (stage 0 = prepare stage)
- `Status IN ('NotStarted', 'InProgress', 'NeedsRevision')`
- User holds the `Preparer` role at the relevant entity (`EntityRoleAssignment`)

**Reviewer items:** Workstreams where:
- `CurrentStageIndex > 0`
- `Status = 'InProgress'`
- `WorkstreamStage[CurrentStageIndex].RoleId` matches a role the user holds at the relevant entity

This means: a CFO who is assigned to 30 entities for portfolio visibility but holds no reviewer stages sees no reviewer items here (the Dashboard is their primary surface). A Senior Reviewer who holds the Senior role on 8 entities sees all the workstreams currently sitting in the Senior stage for those 8 entities.

## Layout

A three-section, prioritized queue — the same structure as the reviewer queue (`02-reviewer-queue.md`) but with explicit preparer items included and a unified view across role types.

### Page header

Breadcrumb: (none, this is a top-level page)

Page title: `Work items`

Subtitle: "What's waiting on you right now." with a live count: "5 items."

A sort/group control on the right: "Group by: Role · Entity · None" (dropdown). Default: None (simple priority order). Most users will want the flat priority list.

### Three sections

**Needs attention (red)**

Items that have been waiting too long or have explicit blocking conditions. These appear at the top with a red left border or red section header.

Conditions for appearing here:
- Preparer items where `Status = 'NeedsRevision'` (reviewer sent back feedback — this is urgent)
- Reviewer items where the workstream's `CurrentStageIndex` entry time exceeds the stage's `StuckThresholdHours`
- Any item where `Round >= 4` (process is not converging — escalation is probably warranted)

**Up next (amber)**

Items that are fresh arrivals — just landed on the user. These are the expected day-to-day items.

Conditions:
- Preparer items `Status = 'NotStarted'` (period just opened; first touch needed)
- Reviewer items where the workstream entered the current stage within the last 24h

**In progress (neutral)**

Items the user has already started working on (lock held or released recently, some activity recorded) but has not yet submitted or approved.

Conditions:
- Preparer items `Status = 'InProgress'` with at least one file uploaded
- Reviewer items where the current stage has a `StartedAtUtc` for this user (they've opened the item) but no `CompletedAtUtc`

### Empty state

If there are no items in any section:

> **You're all caught up.**
>
> No workstreams are waiting on you right now. Check back after the next submission.

With a subtle illustration (a clean checkmark or inbox-zero visual) and a link to the Dashboard.

## Work item tile

Each workstream appears as a tile (consistent with the reviewer queue design in `02-reviewer-queue.md`). The tile shows:

- **Workstream name + code** — top left, medium weight
- **Entity name** — below workstream name, muted color
- **Period** — top right, formatted ("May 2025")
- **Role badge** — your role for this item (e.g., "Preparer", "Senior Reviewer") — important when the user holds multiple roles across entities
- **Status / stage indicator** — for preparer items: the prepare-stage status. For reviewer items: a compact stage chain (e.g., "Prep → Treasury → **Senior** → CFO") with the current stage bolded
- **Waiting since** — relative time ("landed 2h ago", "sent back 3 days ago")
- **Checklist progress** (reviewer items only) — e.g., "3 / 8 items resolved" — gives a sense of how much work remains
- **Lock indicator** — if another user holds the lock, a lock icon + their name ("Locked by Maya"). The user cannot acquire the lock until it releases; item appears in the list but is subtly dimmed with a tooltip explaining the lock.

Tile click navigates to `/work/{workstreamId}` — the preparer or reviewer item page depending on the user's role at the current stage.

## Preparer items vs. reviewer items — visual differentiation

The same tile component is used for both, but with a small visual cue to distinguish role type:
- **Preparer items:** a pencil icon (outline) in the top-left corner next to the workstream name
- **Reviewer items:** a checkmark-circle icon in the top-left corner

This matters because a Senior Reviewer who is also a Preparer on other entities sees both item types in one list. Without the icon, the context switch is invisible.

## Locked items

If a workstream the user could otherwise act on is locked by someone else:
- Item appears in the list (the user may need to be aware of it)
- Tile has slightly reduced opacity and a grey left border overriding the section color
- A lock icon and "Locked by {name}" string appear in place of the normal waiting-since timestamp
- Clicking the tile still navigates to the item page, but the lock acquisition controls are disabled there with an explanation

If the lock is held by the current user in another tab or browser session:
- Tile shows "Locked by you" with an amber warning
- Navigating to the item page will allow the user to re-acquire or continue their session

## Notification badge

The sidebar shows a red badge on "Work items" with the count of items in the "Needs attention" section (not total items — just the urgent ones). This matches the convention described in `05-app-shell.md`: "red badge = you have work to do."

When the Needs attention section is empty, the badge disappears — it is not replaced with a neutral count of total items. Showing 0 attention items as a red badge creates alert fatigue.

## Refresh behavior

Work Items should stay fresh automatically. In Blazor Server, this is straightforward: a Hangfire job (or a simple `PeriodicTimer` in the Blazor component) can push updates via SignalR to refresh the list every 2 minutes, or whenever a relevant AuditEvent fires for a workstream in the user's queue.

The practical win: if a reviewer submits a workstream back to the preparer, the preparer's Work Items list gains a "Needs attention" item in near real-time without a manual page refresh.

## Sorting within sections

Within each section, items sort by:
1. Waiting time (longest waiting first) — surfaces the most overdue item at the top of each section
2. Entity name as tiebreaker

The sort is always descending by urgency — this page is explicitly opinionated about priority. The user can group by Entity or Role if they want a different view, but raw age-based ranking is the primary read.

## Relation to the reviewer queue

The reviewer queue design (`02-reviewer-queue.md`) covers the reviewer-specific interaction patterns (action focus banners, tile content, multi-entity grouping). Work Items is the combined surface. Where they overlap:

- Tile layout and content in Work Items matches the reviewer queue tile structure for reviewer items
- The three-section model (Stuck/Today/Expected in the reviewer queue) maps to Needs attention / Up next / In progress here
- "Stuck" in the reviewer queue = "Needs attention" here; the naming change makes it inclusive of preparer-side urgency (NeedsRevision) without using reviewer-centric language

In v1, the reviewer queue (`/queue` in earlier drafts) and Work Items are the same page at `/work`. There is no separate `/queue` route. The reviewer queue design doc should be treated as the interaction specification for the reviewer-item tiles; this doc governs the overall page structure.

## Audit events

The Work Items page itself writes no audit events. Actions taken from this page navigate to the item page (`/work/{workstreamId}`), where the state-transition procedures write the events.

## Visual details

- Section headers use a slightly bolder divider line with a colored left accent (red / amber / neutral) rather than a full colored background — keeps the palette from getting loud across three sections
- Each section is collapsible (a `MudExpansionPanel` equivalent) with the item count shown in the header: "Needs attention (2)"
- Items within each section are `MudPaper` cards with 1px border, no drop shadow
- The role badge is a small pill, same style as status badges but using a teal or purple color family to distinguish it from workstream status colors (which are green/amber/red/blue)
- On narrow screens, the stage chain collapses to just the current stage label (e.g., "Senior" rather than the full chain) — Blazor handles this via a `@if (IsMobileWidth)` branch
