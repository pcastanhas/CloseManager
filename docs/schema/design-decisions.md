# Schema design decisions

This document captures the rationale behind specific schema choices so that future contributors (and the original author after a long absence) can understand why things are shaped the way they are.

## Why no claim, only a lock

An earlier design had both `ClaimedByUserId` (long-lived, "I'm responsible for this") and `LockedByUserId` (short-lived, "I'm editing right now"). The claim was removed.

The rationale: the role assignment already answers "who can work this." The lock answers "who's working it now." There's no third question that needs answering. Adding claim introduced a state that could go stale (claimed but idle), required admin intervention to release, and broke the natural load-balancing of "anyone with the role can pick this up."

Continuity-of-reviewer across rounds (a thing claim was meant to enforce) is not enforced by the system. It's a soft convention: the UI shows "Erin reviewed round 1" prominently, and other reviewers naturally defer unless Erin is out. If the most-recent reviewer is unavailable, anyone in the role can pick it up and proceed.

## Why templates are versioned

Workflow templates change over time. A new SOX control might add a checklist item; an org change might re-route a workstream from Treasury to Asset Manager. If templates were mutable, in-flight closes would silently change shape, and historical audit trails would mis-represent what was actually required at the time.

Templates are therefore versioned with an `EffectiveFromPeriod` (yyyyMM). When a period opens, the system resolves the latest version with `EffectiveFromPeriod <= period`. In-flight workstreams pin to that version forever.

The "Rebuild and restart workflow" admin action exists for the rare case where you genuinely want an in-flight close to pick up a newer template — it soft-deletes the old workstream, instantiates a fresh one from the current template, and links them via `RebuiltFromWorkstreamId`. Rare, audited, deliberate.

## Why snapshot fields on Workstream

`Workstream` carries denormalized copies of `Code`, `Name`, `OrderIndex`, `PreparerRoleId`, `ReviewerRoleId` from `WorkstreamDef`. This is intentional. Even though the `WorkstreamDefId` foreign key is preserved for lineage, the snapshot freezes the definition at instantiation. Future template edits don't retroactively change historical workstreams.

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
