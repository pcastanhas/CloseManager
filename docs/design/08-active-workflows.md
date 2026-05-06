# Active workflows

The Active Workflows page is the admin operational surface for in-flight workstreams that need attention. It lives under Operations in the sidebar and is the most consequential admin page in the system — every action on it is destructive, audited, or both.

## What this page is for

Three categories of admin intervention:

1. **Stuck or stale state.** Locks held by absent users, workstreams stuck across many rounds, items instantiated wrong that need to be torn down.
2. **Template drift.** Pulling additions from the current template into in-flight workstreams that pinned to an older version (additive only — new checklist items appear, existing ones never disappear).
3. **Forensic investigation.** Triage when someone reports "X is stuck" — find it, see who has the lock, see how many rounds, decide what to do.

This page is **not** a generic "all workstreams" list. The Dashboard already shows status. Active Workflows surfaces only workstreams matching one of the explicit attention conditions.

## Attention conditions

Default filter chips at the top of the page:

- **Stuck > 24h** — workstream sitting in same status > 24h (configurable)
- **Lock held > 4h** — someone's lock has gone stale (auto-expiry runs every 15 min, but a 4h-old lock means no human activity in 4h on the same workstream)
- **Round ≥ 4** — process problem; preparer/reviewer aren't converging
- **Template behind** — workstream pinned to a `WorkflowTemplate` row that's no longer the current version (`IsCurrent = 0`)
- **Never started** — workstream still in `NotStarted` past day 2 of close
- **Blocked downstream** — workstream is blocking other workstreams' completion

Each chip shows a count and is colored by severity (red for stuck, amber for round/lock issues, info for template drift). Default state has all chips active. Admins toggle to drill in.

A search field lets admins find specific workstreams by entity, workstream name, or user.

## The action set

Three primary admin actions, listed in increasing destructiveness:

### Refresh from template (additive)

Pulls additions from the current template into existing in-flight workstreams that pinned to an older version. Never removes existing items. Used when a SOX control is added mid-period (template was edited and saved as a new version) and admins want existing workstreams to pick it up without restarting them.

The confirmation dialog previews additions only:
- "3 new check items will be added to each of 5 selected workstreams"
- Lists each new item by text
- Reason field (required)
- Standard submit button (no typed confirm phrase)

Audit log entry per affected workstream:
```
Action: ChecklistRefreshedFromTemplate
Notes: <reason>
AfterJson: { "addedItems": [...] }
```

### Clear locks (recoverable)

Releases stale locks. Anyone with the right role can immediately reacquire — the lock release isn't a state change, just an availability change.

Confirmation dialog:
- Lists locked workstreams and current holder
- Reason field (required)
- Standard submit button (no typed confirm phrase)

Audit log entry per cleared lock:
```
Action: LockForceCleared
Notes: <reason>
BeforeJson: { "lockedBy": <userId>, "lockedAt": <utc>, "expiresAt": <utc> }
```

### Restart workflow (destructive)

Soft-deletes the existing workstream (status flips to `Rebuilt`), creates a fresh one from the current template, links them via `RebuiltFromWorkstreamId`. Files, comments, and audit trail are preserved on the old workstream for forensics but not visible on the new one.

The confirmation dialog is heavier:
- Red-tinted header with explicit warning
- Lists each workstream with round count, file count, comment count
- Section explaining what happens (archive, recreate, notify preparers)
- Reason field (required, with placeholder cueing auditor-readable text)
- **Typed confirmation phrase**: admin must type `restart N` (where N = count) to confirm
- Submit button locked until both reason and confirmation are valid

Audit log entries on both sides:
```
On old workstream:
  Action: Rebuilt
  Notes: <reason>
  AfterJson: { "newWorkstreamId": <id> }

On new workstream:
  Action: CreatedFromRebuild
  Notes: <reason>
  AfterJson: { "rebuiltFromWorkstreamId": <id> }
```

## Visual principles

The page should feel slightly more "serious" than the rest of the admin UI:

- **Destructive actions look different.** The Restart button has a red background and an alert-triangle icon. Other action buttons are neutral.
- **Conditions are color-coded.** Red for stuck, amber for lock/round issues, info for template drift, neutral for never-started.
- **Selected rows tint subtly.** Bulk operations are common; admins should see at a glance what's about to be acted on.
- **Counts are explicit.** Action buttons in the bulk header show "Restart" not "Restart selected" — but the modal confirms with a count ("Restart 3 workstreams").

## Bulk operation rules

- Bulk actions appear only when at least one row is selected.
- The bulk actions are the only way to act on multiple workstreams at once. Per-row actions (the "Open" button) navigate to the workstream's individual page.
- Bulk actions produce one audit entry per affected workstream, not one entry for the bulk operation. Auditors should see the granular action chain.

## Backend implementation notes

Each action is a stored procedure that does the state change(s) and the audit insert(s) in one transaction:

- `sp_RefreshChecklistFromTemplate` — takes a workstream ID; resolves the current template for that workstream's entity type, computes missing checklist items per stage by comparing the workstream's existing items to the current template's defaults, inserts the missing ones, writes audit
- `sp_ClearLock` — takes a workstream ID, nulls lock fields, writes audit
- `sp_RebuildWorkstream` — takes a workstream ID, marks old as Rebuilt, instantiates new from current template, writes audit on both

The Active Workflows page calls these procedures via Dapper or `ExecuteSqlRaw`. Bulk actions iterate the selected items, calling the procedure once per item, accumulating success/failure into a result summary shown in a banner after the dialog closes.

## What this page is not

- Not a Dashboard for admins — that's the regular Dashboard, scoped to admin assignments.
- Not a system status / health page — that's a future Settings → System Health view.
- Not a way to bulk-approve or bulk-reject workstreams. Approval is always an individual reviewer action on the reviewer item page.
- Not a place to edit checklist items, comments, or files. Those edits happen in the workstream itself; the only thing this page edits is workflow lifecycle state.

## Confirmation dialog pattern, generalized

The pattern used here should apply to all destructive admin actions across the system:

1. **Header** — clear name of the action, count of items affected, "this cannot be undone" if applicable
2. **What you're affecting** — enumeration of the items, with identifying detail (round count, file count, etc.)
3. **What happens** — plain-language description of the post-state
4. **Reason field** — required, with placeholder that cues auditor-readable text
5. **Typed confirmation phrase** — only for irreversible actions; matches the count or names the action explicitly
6. **Submit button** — disabled until reason and confirmation are valid

This pattern shows up again on Period Management (closing a period). Worth implementing as a shared Blazor component (`<DangerousActionDialog />` or similar).
