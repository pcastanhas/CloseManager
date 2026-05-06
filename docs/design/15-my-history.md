# My history page

Route: `/history`
Sidebar section: Your work (all users)

## What this page is for

My History is a personal audit trail — a chronological record of everything the current user has done in the system. It is the "look back" complement to Work Items' "look forward." 

Use cases:

- **Accountability and self-reference.** "I remember approving that workstream last week — let me find the date." A staff accountant proving they completed their work.
- **SOX audit prep.** An auditor asks a reviewer to demonstrate that they personally approved a specific workstream in a specific period. The reviewer pulls up My History, filters to the period, and shows the `Approved` event.
- **Onboarding.** A new user can review their recent activity to understand what they've done and orient themselves.

This page is not an admin surface — every user sees their own history, scoped to their own `ActorUserId`. Admins do not see other users' histories here; that's in Audit Search with an actor filter.

## Layout

A single main content area — no split pane, no filter drawer. The filter controls sit in a compact row above the results (inline, not a drawer). The history list takes the full content width.

### Page header

No breadcrumb needed (top-level page). Page title: `My history`. Subtitle: the user's own display name + UPN ("Your activity log — {DisplayName}").

Right-aligned: **Export CSV** button. Exports the filtered result set (same columns as the Audit Search export but pre-scoped to `ActorUserId = current user`). Useful for auditors asking users to provide evidence.

### Inline filter row

A compact horizontal row of controls. Not a drawer — this page is simpler than Audit Search and doesn't need a panel.

**Period** — dropdown of distinct periods where the user has activity. Formatted ("May 2025"). Default: most recent period.

**Action type** — a set of toggleable filter chips, not a free-text input. Pre-defined groupings that cover the user-facing actions (not every action in the system):

- **Submitted** — `Submitted` events (preparer to reviewer)
- **Approved** — `Approved`, `FinalApproved` events
- **Sent back** — `SentBack` events
- **Checked items** — `ChecklistItemApproved`, `ChecklistItemFlagged` events
- **Files** — `FileUploaded`, `FileDeleted` events
- **Comments** — `CommentPosted` events
- **Other** — everything else (lock acquisitions, etc.)

All chips on by default. User can toggle individual chips off to narrow.

**Date range** — "From" and "To" date pickers, optional. Defaults to the current calendar month. Overrides the Period dropdown if set manually.

**Reset** link — returns to defaults.

### Result list

Results ordered by `OccurredAtUtc` descending (most recent first). Grouped by calendar date, with a sticky date header ("Today", "Yesterday", "Monday May 5", etc.) that matches how a person would think about their own history.

Within each date group, events are listed as timeline rows — not a formal table, but a structured vertical list with a connecting line on the left (thin, muted, like a git log view):

```
┃
●  Approved                    Workstream: CASH — Plaza Tower          May 2025
   "All items resolved. Final sign-off."
┃
●  Checklist item marked        "Cash reconciliation to GL" — Approved  May 2025
┃
●  Checklist item flagged       "Bank confirmation received?" — Needs revision  May 2025
   "Bank statement is from March. Needs April statement."
┃
```

Each row shows:
- **Action** — friendly label (not the raw enum value). Map from the action code: "Submitted" → "Submitted for review", "SentBack" → "Sent back to preparer", "Approved" → "Approved", "ChecklistItemApproved" → "Checklist item approved", etc.
- **Context** — Workstream name + code, entity name, period. These three together identify exactly what the action is about.
- **Notes** — If the event has a `Notes` field (reason text on sends-back, rebuild reasons, etc.), show it in a muted secondary line below the action.
- **Timestamp** — Time only within each date group (the date is in the section header). Full timestamp tooltip on hover.
- **Expand** — a small ▶ icon that expands the row to show `BeforeJson` / `AfterJson` in the same format as Audit Search's expanded rows. Most users will never need this; auditors will.

### Grouping

The view is grouped by calendar date only. A "Group by period" toggle was considered and cut — the period dropdown filter at the top already scopes the view to a single period, which covers the auditor use case. Two ways to do the same thing adds confusion without adding capability. This view is better for an auditor asking "show me everything you did in April's close."

### Workstream deep-links

Each event row's workstream context is a link. If the workstream is still navigable (the entity exists, the period isn't purged), clicking the link navigates to `/work/{workstreamId}`. If the workstream no longer exists (rebuilt, soft-deleted), the link is disabled (greyed out with a tooltip "Workstream was restarted — view audit trail instead") and a secondary link goes to `/admin/audit?workstreamId={id}` for admins (non-admins see no secondary link — the trail is still in the DB but not directly navigable without admin access).

## Empty state

If the user has no history in the selected period:

> No activity in {period}.
>
> [← Previous period] [Dashboard]

If the user has no history at all (brand new user):

> No activity yet.
>
> Head to Work items to get started.

## Export

Same structure as the Audit Search export but pre-filtered to the current user and not separately audited (personal export of one's own records is not a sensitive operation). File name: `my-history-{yyyyMMdd}.csv`.

## Audit events

No audit events written by this page. Read-only.

## Visual details

- The timeline line is a 1.5px muted border on the left margin, with a small filled circle (●, 8px diameter) at each event's left edge — consistent with the audit trail strip style used in the reviewer item page (`03-reviewer-item-page.md`), just vertical rather than horizontal
- Action labels are styled at font-weight 500 (medium), context text at 400 (regular) in a slightly smaller size — the action should catch the eye first, context identifies what it's about
- Notes text is in a distinct muted color block below the action, italicized — visually separate from the label so it doesn't read as part of the action name
- Date group headers are a thin `<hr>` equivalent with the date text inline — not a heavy section header, just enough separation to chunk the timeline visually
- No grouping toggle — date grouping is the only view
- Expand behavior on each row uses a smooth height animation (`max-height` transition), not an abrupt content pop — the page is a personal log and should feel a little more lightweight than the formal admin surfaces
