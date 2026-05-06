# Schema design decisions

This document captures the rationale behind specific schema choices so that future contributors (and the original author after a long absence) can understand why things are shaped the way they are.

## Why no claim, only a lock

An earlier design had both `ClaimedByUserId` (long-lived, "I'm responsible for this") and `LockedByUserId` (short-lived, "I'm editing right now"). The claim was removed.

The rationale: the role assignment already answers "who can work this." The lock answers "who's working it now." There's no third question that needs answering. Adding claim introduced a state that could go stale (claimed but idle), required admin intervention to release, and broke the natural load-balancing of "anyone with the role can pick this up."

Continuity-of-reviewer across rounds (a thing claim was meant to enforce) is not enforced by the system. It's a soft convention: the UI shows "Erin reviewed round 1" prominently, and other reviewers naturally defer unless Erin is out. If the most-recent reviewer is unavailable, anyone in the role can pick it up and proceed.

## Why N-stage chains, not two roles

An earlier design had `Workstream.PreparerRoleId` and `Workstream.ReviewerRoleId` — exactly two roles per workstream. This couldn't model real workflows that go Preparer → Product Manager → Senior Accountant, or Preparer → Treasury → Senior, or Preparer → Senior → CFO.

The schema now uses an ordered list of stages per workstream:

- `WorkstreamDefStage` defines the chain in the template (one row per stage, ordered)
- `WorkstreamStage` is the snapshot at instantiation (one row per stage, ordered)
- `Workstream.CurrentStageIndex` points at the active stage

OrderIndex 0 is always the preparer; subsequent indices are reviewers in chain order. This naturally expresses two-stage flows (length-2 chain), three-stage flows (length-3 chain), or arbitrary lengths with no special-case code.

The status enum simplifies as a result: `Submitted` and `InReview` collapse into `InProgress` plus the stage pointer. "Where in the flow is this workstream?" is answered by `CurrentStageIndex`, not by parsing distinct status values per stage.

## Why explicit final-approval flag rather than "highest OrderIndex wins"

`WorkstreamDefStage.IsFinalApproval` and `WorkstreamStage.IsFinalApproval` mark the stage whose approval transitions the workstream to `Approved`. The alternative — "the last stage in the chain is always final" — is simpler but less flexible.

The explicit flag lets some workflows have an approver whose role is informational or pro-forma rather than gating completion. It also makes the system's behavior obvious: instead of the application code computing "max(OrderIndex)" implicitly, the schema says exactly which stage closes the workstream.

Exactly one Review-kind stage per workstream must be marked final, enforced at the application layer (with a periodic integrity job catching drift). A CHECK constraint can't cleanly express "exactly one true per parent."

## Why per-stage stuck thresholds

Treasury reviews are quick (cash recs are routine); Senior reviews take longer (they're holistic and may involve cross-tie work). A single workstream-level stuck threshold would either flag Treasury too late or flag Senior too early.

`WorkstreamDefStage.StuckThresholdHours` (and the snapshot `WorkstreamStage.StuckThresholdHours`) lets each approver have its own threshold. NULL means "use system default" (configured in Settings). The Active Workflows page reads the per-stage value when computing "stuck > Xh" matches.

## Why checklist items are scoped to a stage

Each stage in the chain verifies different things. Treasury verifies cash tie-out and bank reconciliation; Senior verifies overall reasonableness and cross-tie to financials. Forcing both to share one checklist would either make Treasury's checklist contain Senior's items (which Treasury can't know about) or make it incomplete.

`ChecklistItem.WorkstreamStageId` ties each item to a specific stage. When work advances from stage 1 to stage 2, the user at stage 2 sees their own checklist; stage 1's checklist becomes historical. Reviewer-added items at stage N are scoped to stage N — a reviewer can't directly modify another stage's checklist; they can only send work back with a comment requesting a verification.

The preparer (stage 0) is a special case: they see the *first reviewer's* checklist (stage 1) as a prep guide, with `PreparerStatus` ("NotReady" / "Ready") tracking their progress. `PreparerStatus` is meaningful only on items belonging to stage 1; it's ignored on later stages.

## How NeedsRevision rewinds the stage chain

When a reviewer at stage N sends a workstream back, they pick a target stage M < N. The rewind:

- Sets `Workstream.CurrentStageIndex = M`
- Sets `Workstream.Status = 'NeedsRevision'`
- Marks `WorkstreamStage` rows for indices M..N as rolled-back (Outcome cleared, timestamps preserved for audit)
- Increments `Workstream.Round` only if M = 0 (back to preparer)
- Writes an audit event capturing the chosen target

The default target is N-1 (the immediately previous stage), but the reviewer can pick stage 0 for "redo from scratch" cases. The `WorkstreamStage.RewoundToStageIndex` column captures the chosen target on the row that initiated the rewind.

`Round` only counts how many times stage 0 has submitted, so a workstream bouncing between stages 1 and 2 without involving the preparer doesn't increment Round.

## Why templates are versioned

Workflow templates change over time. A new SOX control might add a checklist item; an org change might re-route a workstream from Treasury to Asset Manager. The schema needs to handle template edits without breaking historical workstreams that point at the old structure.

The model is deliberately minimal: each save in the templates editor creates a new immutable `WorkflowTemplate` row (with fresh child `WorkstreamDef`, `WorkstreamDefStage`, and `WorkstreamDefChecklistItem` rows) and flips the prior row's `IsCurrent` to 0. A filtered unique index enforces "exactly one current version per entity type." `Version` is a per-entity-type monotonically incrementing integer.

What versioning gives us:

- **FK lineage that doesn't dangle.** `Workstream.WorkstreamDefId` always resolves to a real row, even years after that template version was retired. An auditor in 2030 can walk from a 2025 workstream up to its template and see the structure that produced it.
- **Snapshot stability for in-flight work.** When a period opens, workstreams instantiate from the *current* version and pin to it via FK. Subsequent template edits create new versions without touching prior ones, so in-flight workstreams are unaffected.
- **Edit history via the audit log.** Each save writes an `AuditEvent` with `BeforeJson` (the prior version's full structure) and `AfterJson` (the new). "What did this template look like last month?" is answerable without a comparison UI.

What versioning explicitly does *not* try to do:

- **No prospective scheduling.** Edits take effect immediately on save. There is no `EffectiveFromPeriod` or "publish at month-end" mechanism. If you need a change to take effect later, save it later.
- **No drafts.** The editor is all-or-nothing: open, edit, save (commits a new version) or cancel (discards). There is no Draft/Published/Superseded state machine — only `IsCurrent = 1` and `IsCurrent = 0`.
- **No diff or compare-to-version-X UI.** The audit log carries change history; if visual comparison ever becomes valuable, it can be added later as a read-only feature against the existing version rows.

In-flight workstreams pin to their original version forever and are unaffected by subsequent template edits. The "Rebuild and restart workflow" admin action exists for the rare case where you genuinely want an in-flight close to migrate to the current template — it marks the old workstream `Rebuilt`, instantiates a fresh one from the current version, and links them via `RebuiltFromWorkstreamId`. Rare, audited, deliberate.

The simpler refresh-from-template action (additive only — adds new checklist items to in-flight workstreams without disrupting structure) reads the current version's checklist for the relevant `WorkstreamDef` and inserts any items that don't already exist. Stage role changes do not propagate; they require a full rebuild.

## Why snapshot fields on Workstream

`Workstream` carries denormalized copies of `Code`, `Name`, and `OrderIndex` from `WorkstreamDef`. The role chain is snapshotted to the `WorkstreamStage` child table (one row per stage), with each stage carrying its own `RoleId`, `StageKind`, `IsFinalApproval`, and `StuckThresholdHours`. Checklist items are cloned into `ChecklistItem` rows scoped to the stage they apply to.

Strictly speaking, this snapshot is redundant — `WorkstreamDefId` and `SourceDefStageId` foreign keys point at versioned (immutable) template rows, so the original definitions are reconstructable. The snapshot exists for two reasons:

1. **Query performance.** The Dashboard, reviewer queue, and portfolio grid all read workstream display fields heavily. Reading them from `Workstream` directly avoids joins through `WorkstreamDef → WorkflowTemplate` on every query.
2. **Resilience to schema evolution.** If the template structure changes shape in a future schema migration (new columns, renamed fields, restructured relationships), historical workstreams continue to render correctly from their snapshot rather than depending on a join that may need migration logic.

The FK to the original definition is kept for lineage and audit; the snapshot is the operational read path.

## Why `Period` and `EntityId` are duplicated

Both are reachable through `ClosePeriod` joins, but they're the most common query filters in the system (the Dashboard portfolio grid, the reviewer queue, the per-entity drill-down). The denormalization keeps queries fast and easy to read. The trade-off is that the application layer must keep them consistent on insert; this is enforced in the workstream-instantiation procedure.

## Why explicit `Status` plus an audit log

Status is an explicit column on `Workstream` (and parallel columns `PreparerStatus` and `ReviewerStatus` on `ChecklistItem`). These are fast to query, easy to render, and the natural way for the UI to filter.

The audit log is the immutable history of how status got to where it is. Every state change writes one `AuditEvent` row in the same transaction as the column update. The pattern is non-bypassable when implemented in stored procedures.

This split is deliberate: the columns are for *current state*; the audit log is for *what happened*. Don't try to compute current state from the audit log; don't try to derive history from the columns.

## Why no `Period` on top-level entity types

`EntityType`, `Role`, `User`, `Entity`, and `WorkstreamDef` have no period associated with them — they are reference data. They are scoped only by `IsActive` and the soft-delete trio. This means renaming a role or deactivating an entity affects all closes from that point on. If you ever need period-scoped reference data (e.g., "this entity was active in Sep but not Oct"), the right model is to leave the entity active and have the period-open process opt entities in/out per period rather than mutating the entity record.

## Why two file roles on a workstream

`WorkstreamFile.FileRole` is one of `Primary`, `Supporting`, `Reference`.

- **Primary** is the deliverable — the schedule, the memo, the financial statement. There should be exactly one current Primary per workstream (older ones soft-deleted with `ReplacesFileId` chain).
- **Supporting** are evidence files attached by the preparer to back up the primary — bank statements, loan docs, GL extracts.
- **Reference** are documents not part of this workstream's deliverable but useful for context — could be auto-populated from prior periods, the entity's permanent file, etc.

The "Ready for review" gate requires at least one Primary file. Other roles are optional.

## Why the lock has an explicit expiration column

`LockExpiresAtUtc` is a separate column rather than a computed `LockedAtUtc + 15 minutes`, because (a) you can index on it, and (b) you can have variable-length locks if some operations need longer windows. The auto-expire job filters on `LockExpiresAtUtc < SYSUTCDATETIME()` and uses `IX_Workstream_LockExpiry` (a filtered index that only includes locked rows).

## Why comments can be attached to either checklist item or workstream

Most comments are checklist-item-scoped — they're discussion about a specific verification. But sometimes a preparer wants to leave a note that's about the workstream as a whole ("Submitted late this round because Yardi was down Tuesday"). Allowing a NULL `ChecklistItemId` covers that case without needing two tables.

## Why no nested comment threading

Comments are flat, ordered by `PostedAtUtc`. A reviewer's question and a preparer's reply are sequential entries on the same checklist item, not parent-child rows. Two-level threading would require recursive queries and complicated UI layout for marginal benefit. The chronological flat order is sufficient.

## Why CommentAttachment is its own table

A comment can have zero, one, or many attachments. Most have none; flagged-item discussions sometimes have one (a screenshot of the v3 schedule that's now superseded by v4). Modeling attachments as a separate table keeps the `Comment` row small and lets attachments stand on their own as durable evidence — the screenshot survives even after the underlying file has been replaced.

## Why no notification table

Notifications (Entra/Teams pings, daily digest emails) can be derived from `AuditEvent` by a worker process — no need for a parallel table. When you build notifications, the worker reads new audit events, filters for ones that should notify someone, and routes them. The audit event is the source of truth; notifications are a projection.

## Why everything has `RowVersion` even though we use locks

The lock prevents two reviewers editing the same workstream at the same time. `RowVersion` is for the *audit pipeline*: when an action begins, the application reads the current state into `BeforeJson`; when it commits the change, the audit insert and the row update happen in one transaction. `RowVersion` ensures the read-then-write pattern doesn't race with anything else in unexpected ways. Belt and suspenders.

## Why `EntraObjectId` is denormalized into `AuditEvent`

`AuditEvent` already has `ActorUserId` referencing `User`. But the `User` row could be soft-deleted later (someone leaves the company). The audit trail must remain intelligible for years after the fact, including for users who no longer exist. Denormalizing `ActorEntraObjectId` (the immutable Entra identifier) into the audit row means an auditor in 2030 can still trace any action back to a specific human, even if the corresponding `User` row has been archived.
