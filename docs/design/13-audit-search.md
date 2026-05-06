# Audit search page

Route: `/admin/audit`
Sidebar section: Operations (admin only)

## What this page is for

The Audit Search page gives admins and external auditors direct, filterable access to the `AuditEvent` table. It is the primary forensic surface of the system — the place you go when an auditor asks "who approved workstream X, on what date, and what did the checklist look like at that moment?" or when an admin needs to trace a sequence of events that led to an unexpected outcome.

This page is read-only. Nothing written here. The AuditEvent table is append-only at the DB level; no UI action can modify it.

The design bias is toward **precise filtering and clean export**, not toward visualization or aggregation. External auditors will screen-share this page or receive an export from it; it needs to be legible to non-accountants.

## Layout

Two regions: a collapsible filter panel on the left (~280px), and the result list taking the remaining width.

### Page header

Breadcrumb: `Operations › Audit search`

Right-aligned: **Export CSV** button (exports current filtered result set, up to 10,000 rows — shows a warning if the result set is larger, prompting to narrow filters).

### Filter panel

Always visible by default (not collapsed). Collapsible for screen space. Filter panel has no "Apply" button — filters apply with 300ms debounce as the user changes them, just like a live-filter table. Exception: date range pickers apply on picker close.

**Search** (text input, first field)
Full-text search across `Action`, `Notes`, `BeforeJson`, `AfterJson`. Scoped to the fields indexed for search (see implementation notes). Placeholder: "Search actions, notes, or change details…"

**Actor** (user autocomplete)
Filters to `ActorUserId`. Autocomplete against `User` table. Shows display name + UPN in the dropdown. Can clear to "any."

**Period** (text input or dropdown)
`char(6)` yyyyMM. Either a typed input or a dropdown populated with distinct periods in the `AuditEvent` table. Shows formatted (e.g. "May 2025" for "202505"). Can clear to "any."

**Entity** (entity autocomplete)
Filters to `EntityId`. Shows entity name + code. Can clear to "any."

**Target table** (multi-select chips)
Values from the set of distinct `TargetTable` values in the DB. In v1: `Workstream`, `ChecklistItem`, `Comment`, `WorkstreamFile`, `ClosePeriod`, `WorkflowTemplate`, `Role`, `User`, `EntityRoleAssignment`. Shown as toggleable chips; all on by default.

**Action** (multi-select or text filter)
Filters on `Action` column. Either a dropdown of known action values or a free-text filter (typed action name). Free-text is more robust as the action vocabulary grows. Placeholder: "e.g. Submitted, Approved, Rebuilt"

**Date range** (two date pickers)
"From" and "To." Filters on `OccurredAtUtc`. Both optional. Default: no lower bound, upper bound = now (i.e., all history by default). The date picker shows dates in the user's local timezone but stores/queries in UTC.

**Workstream ID** (numeric input, for deep-link / direct lookup)
Admins sometimes arrive with a specific `WorkstreamId` from an error log or audit report. Direct numeric filter on `WorkstreamId`. Usually left empty.

**Reset filters** link at the bottom of the panel — returns all filters to defaults.

### URL persistence

All filters are reflected in the URL query string so the filtered view can be shared (e.g., `?actorUserId=42&period=202504&action=Submitted`). Deep-links from other pages (e.g., "See full history" on the Users page) pre-populate the URL and therefore pre-populate the filter panel.

### Result list

Results are displayed as a flat, dense table rather than a card list — auditors scroll fast and want to see many rows at once.

Columns:

| Column | Notes |
|---|---|
| Timestamp | `OccurredAtUtc` in user's local timezone. Full datetime (not relative). |
| Actor | `DisplayName` linked to `/admin/users?userId={id}`. If the user row has been soft-deleted, show the `EntraObjectId` as fallback (it's denormalized on the event row for exactly this reason). |
| Action | The `Action` string, styled as a monospace badge. Color-coded by category: state transitions (Submitted, Approved, SentBack, Rebuilt) in blue; admin actions (LockForceCleared, ChecklistRefreshedFromTemplate) in amber; destructive (PeriodClosed, UserDeactivated) in red; reference changes (RoleCreated, TemplateCreated) in neutral. |
| Target | "{TargetTable} #{TargetId}" — e.g. "Workstream #4821". Linked to the relevant detail page if the target still exists and is navigable (e.g., a workstream in a non-deleted period). |
| Context | Entity name + period, where present (`EntityId` and `Period` are denormalized on the event). Helps auditors scan without opening each event. |
| Notes | The `Notes` field truncated to ~80 chars, with full text on hover or expand. |
| Expand | ▶ chevron that expands the row to show `BeforeJson` and `AfterJson`. |

Default sort: `OccurredAtUtc` descending (newest first). Reversible (ascending for reading a sequence of events in order).

Pagination: 50 rows per page. Page controls at the bottom. Total count displayed: "1,247 events matching your filters."

### Expanded row (detail)

Clicking the ▶ chevron expands the row inline to show a two-column layout:

**Before** | **After**

Each column shows the JSON pretty-printed with syntax highlighting (key: value pairs, indented). If `BeforeJson` or `AfterJson` is null, the column shows a greyed-out "—". This is the primary evidence surface for an auditor proving a specific state at a specific time.

The expanded row also shows the full `Notes` text (no truncation).

A "Copy link" button in the expanded row copies a deep-link URL that pre-filters to this specific `AuditEventId` (using the Workstream ID + timestamp as a proxy, since direct AuditEventId links require a separate filter not worth building for v1 — or add an `AuditEventId` URL param in a later iteration).

## Performance considerations

The `AuditEvent` table will grow to millions of rows over years of use. The indexed columns for this page's filters are:

- `IX_AuditEvent_Workstream` (WorkstreamId, OccurredAtUtc) — for workstream-scoped queries
- `IX_AuditEvent_Target` (TargetTable, TargetId, OccurredAtUtc) — for target-type drilldowns
- `IX_AuditEvent_Period` (Period, OccurredAtUtc) — for period-scoped queries

Additional indexes to add at implementation time for the filter combinations this page drives:
- `IX_AuditEvent_Actor` (ActorUserId, OccurredAtUtc) — for the actor filter
- `IX_AuditEvent_Entity` (EntityId, OccurredAtUtc) — for the entity filter
- `IX_AuditEvent_Action` (Action, OccurredAtUtc) — for action-type filter

The text search (`Notes`, `BeforeJson`, `AfterJson`) should use SQL Server full-text search if the volume warrants it. In v1, a `LIKE '%{term}%'` filter is acceptable given the small team. Add a visible warning if the result set would exceed 5,000 rows before applying text search ("Narrow the filters before using text search to avoid timeouts").

## Export

The **Export CSV** button exports the current filtered result set:

Columns in export: Timestamp (UTC), Actor DisplayName, Actor UPN, Actor EntraObjectId, Action, TargetTable, TargetId, WorkstreamId, EntityId, Period, Notes, BeforeJson, AfterJson.

The `EntraObjectId` column is critical for forensic durability — it's the stable identifier even if the `DisplayName` or `Upn` has changed or the user has been deleted.

File name: `audit-export-{yyyyMMdd-HHmm}.csv`

Exports are themselves audited:
```
Action: AuditExported
TargetTable: AuditEvent
Notes: "{N} events exported. Filters: {serialized filter state}"
```

## Direct-link entry points

Other pages link to this page with pre-populated filters:
- **Users page** → "See full history" → `/admin/audit?actorUserId={id}`
- **Active Workflows** → "See audit trail" on a specific workstream → `/admin/audit?workstreamId={id}`
- **Period Management** → "Audit events" for a period → `/admin/audit?period={yyyyMM}`
- **Entity setup hub** → History sub-tab → `/admin/audit?entityId={id}`

## Audit events for this page

None. The page is read-only. The export action writes an audit event (see above) but the page itself writes nothing.

## Visual details

- Dense table with `MudDataGrid` at compact density — similar to the Roles page. Auditors are data-heavy users; they want rows, not whitespace.
- Action badge uses a monospace `<code>`-style span, not a full pill — slightly different from the status pills used on workstream pages, to signal "this is an identifier" rather than "this is a status."
- JSON pretty-print in expanded rows uses a `<pre>` tag with `white-space: pre-wrap` and a monospace font at 0.8em. No external syntax-highlighting library needed — the data is JSON and the key-value structure is sufficient at this size.
- The filter panel uses `MudTextField`, `MudAutocomplete`, `MudDateRangePicker`, and a custom chip group for the multi-select filters.
- The result total ("1,247 events") updates live as filters change. If the query is running, show a spinner in place of the count.
- Color coding for action badges: use a lookup map in the component that maps known action strings to color families. Unknown action strings fall back to neutral. This map is maintained in code alongside the stored procedure that generates the actions.
