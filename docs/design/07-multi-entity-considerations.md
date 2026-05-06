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

A template has a `Version` and an `EffectiveFromPeriod` (yyyyMM). When a close period opens, the system resolves the latest version with `EffectiveFromPeriod <= period` and clones its definitions into live workstreams.

In-flight workstreams pin to the version they started with. Future template edits don't retroactively change historical work.

## Entity-level overrides

Most entities use their type's standard template. Some entities have additional workstreams (Oak Industrial, a real estate asset, also has an IC tax workstream) or, rarely, omit a standard workstream.

Modeling: the entity's resolved workflow = template + entity-specific additions + entity-specific exclusions. The rendering rule in the UI flags overrides ("entity override" badge with dashed border) so admins can spot deviations from the standard.

## Roles are per-(entity, role)

Even though "Treasury-RE" is a system-wide role, who fills that role differs per entity. The `EntityRoleAssignment` table holds (entity, role, user) tuples. Multiple users per (entity, role) are supported — anyone can claim.

Two things this enables:

- **Backup coverage**: Plaza Tower has both Erin and Sam as Treasury-RE. If Erin's out, Sam picks up.
- **Specialization by entity type**: Treasury-RE for real estate is a different role assignment than Treasury-Inv for investment funds, even if it's the same person — modeling them as distinct roles lets you split the role later without restructuring.

## Reviewer queue queries

The reviewer queue is just:

```sql
SELECT w.* FROM Workstream w
INNER JOIN EntityRoleAssignment era
  ON era.EntityId = w.EntityId
 AND era.RoleId = w.ReviewerRoleId
 AND era.UserId = @UserId
 AND era.IsDeleted = 0
WHERE w.Status IN ('Submitted', 'InReview') AND w.IsDeleted = 0;
```

The (EntityId, RoleId, UserId) index on EntityRoleAssignment plus the (ReviewerRoleId, Status) index on Workstream cover this efficiently.

## Period-open process

When opening a period (e.g. October 2025):

1. For each active entity, insert a `ClosePeriod` row.
2. Resolve the workflow template version (latest `EffectiveFromPeriod <= '202510'` for the entity's type).
3. For each `WorkstreamDef` in that template, insert a `Workstream` row, copying the snapshot fields (Code, Name, OrderIndex, role IDs).
4. For each entity-specific override, insert an additional `Workstream` row.
5. For each `WorkstreamDefChecklistItem`, insert corresponding `ChecklistItem` rows.

This is ~100 entities × 5-7 workstreams × 6-10 checklist items = ~3000-7000 rows in one transaction. Should complete in seconds. Run as a Hangfire job with a clear status indicator on the Period Management page.

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
