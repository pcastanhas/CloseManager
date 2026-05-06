# Multi-entity considerations

The most consequential design dimension in this system is that work is *not* uniform across entities. An asset entity has different workstreams than an investment fund. Even when two entity types share a workstream by name, the routing differs. This doc captures how the system handles that variability.

## Entity types are first-class

Each entity has an `EntityType` (RealEstateAsset, InvestmentFund, HoldingCo, OperatingCo, etc.). Entity types are reference data — small, stable, named codes.

## Workflow templates are per-entity-type

Each `EntityType` has one or more `WorkflowTemplate` versions. A template defines:

- The ordered list of `WorkstreamDef` rows (Cash, Property Income, Debt Service, Financials, ...)
- The role routing for each (Preparer, then Treasury-RE; or Preparer, then Asset Manager; etc.)
- The default checklist items per workstream

## Templates are versioned

Each `WorkflowTemplate` row has a `Version` (per-entity-type, monotonically incrementing) and an `IsCurrent` bit. Exactly one row per entity type is current at any time. When a close period opens, the system reads the current row for the entity's type and clones its definitions into live workstreams.

In-flight workstreams pin to the version they started with via FK to `WorkstreamDefId` and snapshot fields on `Workstream` and `WorkstreamStage`. Future template edits create new versions and flip the prior row to `IsCurrent = 0`, but in-flight workstreams continue to FK to the now-historical row, which stays in the database forever for lineage.

## Entity-level overrides

Most entities use their type's standard template. Some entities have additional workstreams (Oak Industrial, a real estate asset, also has an IC tax workstream) or, rarely, omit a standard workstream.

Modeling: the entity's resolved workflow = template + entity-specific additions + entity-specific exclusions. The rendering rule in the UI flags overrides ("entity override" badge with dashed border) so admins can spot deviations from the standard.

## Roles are per-(entity, role)

Even though "Treasury-RE" is a system-wide role, who fills that role differs per entity. The `EntityRoleAssignment` table holds (entity, role, user) tuples. Multiple users per (entity, role) are supported — anyone can claim.

Two things this enables:

- **Backup coverage**: Plaza Tower has both Erin and Sam as Treasury-RE. If Erin's out, Sam picks up.
- **Specialization by entity type**: Treasury-RE for real estate is a different role assignment than Treasury-Inv for investment funds, even if it's the same person — modeling them as distinct roles lets you split the role later without restructuring.

## Visibility on the Dashboard

The Dashboard shows every workstream where the current user holds *any* role assignment on the entity that maps to *any stage* of that workstream. The size of a user's Dashboard is purely a function of how many entity-role rows they have intersected with which stages those roles appear in:

```sql
SELECT DISTINCT w.*
FROM Workstream w
INNER JOIN WorkstreamStage ws ON ws.WorkstreamId = w.WorkstreamId AND ws.IsDeleted = 0
INNER JOIN EntityRoleAssignment era
    ON era.EntityId = w.EntityId
   AND era.RoleId = ws.RoleId
   AND era.UserId = @UserId
   AND era.IsDeleted = 0
WHERE w.IsDeleted = 0;
```

Read access flows broadly from entity-role assignment: if you're the Preparer for Plaza Tower, you also see how the downstream review stages are progressing. Write access is gated separately by the lock-acquisition rule, which only lets you act on workstreams whose *current stage* maps to your role.

To give a user CFO-level visibility across the org, define a CFO role and assign it to that user on every entity. Visibility flows from `EntityRoleAssignment` alone — the Dashboard query asks "what entities is this user assigned to?" without caring whether their role appears in any workstream's stage chain. So a CFO with role assignments but no presence in any `WorkstreamDefStage` row sees the full portfolio and never acquires a lock on anything (the lock-acquisition query joins `EntityRoleAssignment.RoleId` to `WorkstreamStage[CurrentStageIndex].RoleId`; with no stage referencing the CFO role, no match is possible). They are a pure observer by virtue of role-exists-in-assignments-but-not-in-stages, with no special "observer" mechanism needed in the schema.

If a specific workstream genuinely needs the CFO as an approver, that's a separate decision: add a CFO stage to that workstream's template (or that entity's override). Two independent choices, served by two tables already in the schema — `EntityRoleAssignment` for visibility, `WorkstreamDefStage` for chain membership.

To restrict a user to ten entities, give them assignments only on those ten. The role assignment table is the access control list; there is no parallel permission system.

## Work items queue queries

The work items queue is similar to the Dashboard query but narrower: only workstreams where the current stage maps to the user's role and the workstream is awaiting that stage's action:

```sql
SELECT w.*
FROM Workstream w
INNER JOIN WorkstreamStage ws
    ON ws.WorkstreamId = w.WorkstreamId
   AND ws.OrderIndex = w.CurrentStageIndex
   AND ws.IsDeleted = 0
INNER JOIN EntityRoleAssignment era
    ON era.EntityId = w.EntityId
   AND era.RoleId = ws.RoleId
   AND era.UserId = @UserId
   AND era.IsDeleted = 0
WHERE w.IsDeleted = 0
  AND w.Status IN ('NotStarted', 'InProgress', 'NeedsRevision');
```

The (EntityId, RoleId, UserId) index on EntityRoleAssignment plus the (WorkstreamId, OrderIndex) index on WorkstreamStage cover this efficiently. The `Status IN (...)` filter ensures we don't show workstreams that are `Approved` or `Rebuilt` — those are terminal.

## Period-open process

When opening a period (e.g. October 2025):

1. For each active entity, insert a `ClosePeriod` row.
2. Resolve the current workflow template (`WorkflowTemplate WHERE EntityTypeId = @t AND IsCurrent = 1`).
3. For each `WorkstreamDef` in that template, insert a `Workstream` row, copying the snapshot fields (Code, Name, OrderIndex).
4. For each `WorkstreamDefStage` of that def, insert a corresponding `WorkstreamStage` row, snapshotting RoleId, StageKind, IsFinalApproval, and StuckThresholdHours.
5. For each entity-specific override, insert an additional `Workstream` row (with its own stages).
6. For each `WorkstreamDefChecklistItem`, insert a corresponding `ChecklistItem` row scoped to its `WorkstreamStageId`.

This is ~100 entities × 5-7 workstreams × ~3 stages × 6-10 checklist items per stage = on the order of ~10k-20k rows in one transaction. Should complete in seconds. Run as a Hangfire job with a clear status indicator on the Period Management page.

## Heterogeneous portfolio view

The portfolio view renders rows grouped by entity type. Each row's workstreams are rendered as tiles in their natural order, with status color, name, and current reviewer baked into the tile. There are no fixed columns: each entity row owns its own workstream sequence. See `01-portfolio-view.md` for the detail.

## Things deliberately not in v1

- **Cross-entity workstream dependencies.** Consolidation, where a holdco's financials depend on its subsidiaries' approved financials, is not modeled. Each preparer is responsible for starting their own workstream with the right files. Consolidation as a feature can come later.
- **Standing notes / carryover items.** Workstreams are self-contained per period. Reference materials provide some institutional memory; a true carryover-note feature is deferred.
- **Variance/flux engine.** The "auto-flux green" UI hint is shown in mockups but not implemented in v1. When implemented, it would be a separate worker comparing current period schedule to prior period and flagging line items past a materiality threshold.
- **Direct accounting software integration.** v1 uses manual file upload from accounting software exports. Direct Yardi/NetSuite integration is a meaningful future improvement.

## What this scale looks like in practice

- ~12 staff accountants × ~10 entities each = ~120 entity-closes per period
- ~5-7 workstreams per entity = ~600-840 active workstreams per period
- ~6-10 checklist items per workstream = ~3,600-8,400 checklist items per period
- Maybe 1-3 comments per workstream on average = ~600-2,500 comments per period
- File uploads: each workstream gets 1-3 files = ~600-2,500 files per period

Over a year: ~10,000 workstreams, ~50,000 checklist items, ~30,000 files. Well within SQL Server's comfortable range. The audit log will be the largest table by row count — likely 300,000-1M rows per year — and benefits from period-based partitioning if it grows uncomfortably.
