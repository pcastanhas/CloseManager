# Workflow templates editor

The Workflow Templates page is where admins define the workflow shape for each entity type. It is structurally significant because each version snapshots into in-flight workstreams, but the lifecycle around it is deliberately minimal: edits go in, save commits a new version, the prior version becomes historical. There is no draft state, no publish ceremony, no diff UI.

## Versioning model in one screen

Every `WorkflowTemplate` row is one of two states:

- **Current** (`IsCurrent = 1`) — the source of truth for instantiating new workstreams. Exactly one current row per entity type, enforced by a filtered unique index on the schema.
- **Historical** (`IsCurrent = 0`) — was previously current; no longer used for new instantiations. In-flight workstreams may still FK to it. Stays in the database forever for lineage.

Transitions:

- **Save in editor** → creates a new `WorkflowTemplate` row (with fresh child rows) marked `IsCurrent = 1`, flips the prior current row to `IsCurrent = 0`. Atomic, in one transaction, in `sp_SaveTemplate`.
- **Cancel in editor** → discards. Nothing persists.

There is no separate "publish" step. Save *is* the commit. The editor is all-or-nothing: open, edit, save (commits a new version) or cancel (discards everything).

## Templates list page

Index of entity types and their current template. Each entity type shows:

- Current version number, last edited info ("v7, edited 3 days ago by Alex")
- Scale context: "5 workstreams · 30 checklist items · used by 42 entities · 487 instantiated workstreams"
- An "Edit" button that opens the editor seeded with the current version's structure

Historical versions (`IsCurrent = 0`) are retained in the database permanently for FK lineage — in-flight workstreams always resolve their `WorkstreamDefId`. There is no UI for browsing historical versions in v1. The audit log's `BeforeJson`/`AfterJson` on each `TemplateCreated` event captures the full before/after structure for anyone who genuinely needs to see what changed. Add a history UI if admins ask for it after go-live.

## Template editor

The deep edit surface for a template. Opens seeded with the current version's structure as a working copy. Edits live entirely in the editor's local state until Save commits them as a new version.

### Layout

- **Header**: entity type name, "Editing v7 → will save as v8" indicator, last-saved-at-... timestamp from the version being edited (the prior current), action buttons
- **Workstream list**: each workstream as a card, draggable to reorder, expandable to show its stage chain and per-stage checklists
- **Add workstream** affordance at the bottom

### Workstream card

Each workstream's collapsed view shows:

- Drag handle on the left for reordering
- Display number ("1.", "2.") computed from order
- Display name and code (CASH, DEBT_SVC, etc.)
- Summary line: "N approvers · M checklist items"
- Edit and delete buttons on the right

### Approver cards (indented under each workstream)

Each approver is its own card, indented inside the workstream. Approvers render in chain order with `A1`, `A2`, `A3` indices (the preparer is implicit and not shown as a card — every workstream has a Preparer; there's nothing to configure there).

Each approver card shows in-line, without expansion:

- Drag handle for reordering within the workstream
- Approver index (A1, A2, ...)
- Role name (or display-name override)
- Summary line: "N checklist items · stuck after Xh"
- "advances" or "final" badge: "advances" for non-final approvers (work moves on after they approve), "final" for the approver whose approval marks the workstream complete
- Edit button (opens the modal described below)

Approver cards are visually distinct from workstream cards — a teal/green tint and border so the parent-child relationship is obvious without indentation alone.

### Edit Approver modal

Clicking Edit on an approver card opens a focused modal containing all of that approver's configuration. The main editor canvas stays scannable; depth lives behind clicks.

The modal contains:

- **Approver role** (dropdown of system roles)
- **Display name** (optional override; e.g., "Treasury sign-off" instead of "Treasury-RE")
- **Stuck threshold** (number + "hours" unit; default value pre-filled from system setting; helper text explains how it's used by Active Workflows)
- **Final approval** (checkbox; helper text explains "Exactly one approver per workstream must be marked final.")
- **Default checklist items** (inline-editable list with drag handles for reorder, Add button at bottom)

Footer: Delete approver button on the left (with confirmation), Cancel and Save on the right. Note: this Save commits the modal's changes back to the editor's working copy, not to the database. The actual database commit happens only when the editor's top-level Save is clicked.

### Final approval semantics

Exactly one Review-kind stage per workstream must be marked Final. The approver whose `IsFinalApproval` is true is the one whose approval transitions the workstream from `InProgress` to `Approved`. Earlier approvers in the chain are gates that advance the workstream; their approval doesn't end the chain.

In practice the final approver is usually the last in the chain (highest OrderIndex), but the model permits other configurations (e.g., a Senior approval marks complete while a CFO approver later in the chain is informational).

If an admin attempts to save the modal with Final checked while another approver in the same workstream is already final, the system asks "this will unset Final on {other approver}, OK?" rather than silently swapping. If the editor is asked to save a workstream with no approver marked Final, validation blocks the editor's top-level Save until exactly one approver is Final on every workstream.

### Workstream codes are editable in the editor

A code (CASH, DEBT_SVC, etc.) can be renamed before saving. Renames are safe because:

- Each save creates a new `WorkstreamDef` row under the new `WorkflowTemplate`. Joins use `WorkstreamDefId` (FK), so existing in-flight relationships always resolve.
- `Workstream.Code` is a snapshot at instantiation; in-flight workstreams keep the original name regardless of what the new version renames.
- SharePoint paths use the snapshot code, so v7 and v8 instantiations live in different folders even if the code was renamed.

What changes is only what *new* instantiations look like. There is no "edit a saved version" — once a version is saved, that version's codes are immutable. To rename, edit the current template, save as a new version.

### Working copy semantics

The editor opens with the current version's structure loaded into a working copy. All edits — text changes, reorders, adds, deletes — modify the working copy only. The database is untouched until Save commits.

Implications:

- **Cancel discards.** Closing the editor or hitting Cancel throws away the working copy. The current version is unchanged.
- **No partial saves.** There is no way to commit half a change set; either the whole working copy becomes the new version, or nothing does.
- **No multi-session drafts.** Walking away from the editor and coming back tomorrow loses the working copy. If an admin is in the middle of a large restructure, they should finish in one session or accept the redo cost.

This is a deliberate tradeoff against the prior Draft mechanism: simpler to build, simpler to reason about, no orphaned-draft cleanup, no "what was I editing last week" archaeology. The cost is that big edits must happen in one sitting. For the expected admin pool (one person, infrequent edits), this is acceptable.

### Deletions in the editor affect only future instantiations

Removing a workstream or checklist item before save and committing as a new version does not affect any in-flight workstream. In-flight workstreams remain pinned to their original version (v7) until explicitly rebuilt. Examples:

- An admin deletes the OpEx workstream and saves as v8. Open closes (Oct, Nov, Dec 2025) still have OpEx workstreams running normally — they're on v7. Jan 2026 closes (when opened on v8) will not have an OpEx workstream.
- An admin deletes a checklist item from Property Income and saves as v8. v7-instantiated Property Income workstreams still show all 5 original items. v8-instantiated workstreams will have 4 items.

The only way to make an in-flight workstream pick up a deletion is to rebuild it via Active Workflows. Rebuild loses the in-progress work but creates a fresh workstream from the current template. There is no additive-only way to remove items from an in-flight workstream.

### Stage role changes don't propagate via refresh-from-template

The Active Workflows "refresh from template" operation is **additive only**: it adds new checklist items to existing stages but doesn't change the stages themselves. If a new version renames stage 1's role from "Asset Mgr" to "Senior," in-flight workstreams keep the v7 chain (Asset Mgr at stage 1) until rebuilt.

This is intentional. Mid-flight role retargeting would silently change who can act on a workstream that's currently in someone's queue — bad for the people involved and bad for the audit trail. To pick up a stage role change, the workstream must be restarted (via Active Workflows → Restart workflow).

The Save dialog (below) explicitly mentions stage role changes as one of the kinds of edits that won't propagate to in-flight workstreams via refresh.

## Single admin assumption

This system is single-admin in v1—only one person ever edits templates. No soft locks on the working copy, no presence indicators, no concurrent-edit handling. If a future iteration adds multiple admins, the editor would need a soft lock similar to workstream locks but with a longer TTL (~1 hour); not built for v1.

## Action buttons

Two buttons in the editor header:

- **Cancel** — discards the working copy without confirmation if no changes have been made; with a confirmation dialog ("Discard your changes?") if changes exist. No reason field; cancel is cheap.
- **Save** — opens the Save dialog (below).

### Browser navigate-away protection

When the working copy has unsaved changes, a standard `beforeunload` browser warning fires if the user tries to close the tab, navigate away, or refresh: "You have unsaved template changes. Leave anyway?" This is a single event listener on the editor component; it prevents the obvious accidental loss of a large restructure. The warning is suppressed if no changes have been made (clean working copy = no warning).

## Save dialog

The dialog that confirms creating a new version. Sections:

1. **Header** — "Save as version 8" (next number computed from the current version), entity type
2. **Validation summary** — confirms validation passed: "All workstreams have exactly one Final approver. All required fields are populated." Save is locked until validation passes; this section is the receipt that it has.
3. **Impact section**:
   - "42 entities currently use this template"
   - "Jan 2026 closes (when opened) will instantiate using v8"
   - "Existing in-flight workstreams (Oct/Nov/Dec 2025) stay on v7"
   - "Stage role changes do not propagate to in-flight workstreams via refresh from template — to apply role changes, restart those workflows from Active Workflows"
   - Pointer to Active Workflows → Refresh from template for selective in-flight upgrades of additive checklist changes
4. **Save notes** — required. Stamped into `WorkflowTemplate.Notes`. This is the change-summary admins write at save time; combined with the audit log's BeforeJson/AfterJson, it gives both human-written context and machine-readable diff.
5. **Action buttons** — Cancel and Save. Save disabled until notes are filled.

No typed-confirm phrase. Saving is significant but the action is reversible — if a save was a mistake, the admin can edit again and save as v9 to restore the prior shape. The Cancel button + required notes field is sufficient gate.

The Save dialog is also where the audit-log change summary gets generated for the new `AuditEvent` row's `BeforeJson` (the prior current version's full structure) and `AfterJson` (the new). No diff UI — just the raw JSON in the audit log, queryable later if anyone needs to know what changed.

## Save stored procedure

```sql
CREATE PROCEDURE sp_SaveTemplate
    @EntityTypeId       int,
    @WorkingCopyJson    nvarchar(max),  -- full template structure from the editor
    @Notes              nvarchar(1000),
    @ActorUserId        bigint,
    @ActorEntraObjectId uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRAN;

    -- Capture the prior current version for audit BeforeJson
    DECLARE @PriorTemplateId bigint, @PriorVersion int, @PriorJson nvarchar(max);
    SELECT @PriorTemplateId = WorkflowTemplateId,
           @PriorVersion = Version
    FROM WorkflowTemplate
    WHERE EntityTypeId = @EntityTypeId
      AND IsCurrent = 1
      AND IsDeleted = 0;

    -- (Application layer serializes the prior version's full structure
    -- into @PriorJson before calling this procedure, or a separate fn does it.)

    -- Mark prior as historical
    UPDATE WorkflowTemplate
    SET IsCurrent = 0
    WHERE WorkflowTemplateId = @PriorTemplateId;

    -- Insert new version
    INSERT INTO WorkflowTemplate (
        EntityTypeId, Version, IsCurrent, Notes, CreatedByUserId
    )
    VALUES (
        @EntityTypeId,
        ISNULL(@PriorVersion, 0) + 1,
        1,
        @Notes,
        @ActorUserId
    );
    DECLARE @NewTemplateId bigint = SCOPE_IDENTITY();

    -- Insert child rows (WorkstreamDef, WorkstreamDefStage,
    -- WorkstreamDefChecklistItem) parsed from @WorkingCopyJson.
    -- Implementation detail: use OPENJSON or shred in application layer
    -- and pass as TVPs. Both options work; choice is a separate decision.

    -- Audit
    INSERT INTO AuditEvent (
        ActorUserId, ActorEntraObjectId,
        TargetTable, TargetId,
        Action, BeforeJson, AfterJson, Notes
    )
    VALUES (
        @ActorUserId, @ActorEntraObjectId,
        'WorkflowTemplate', @NewTemplateId,
        'TemplateSaved', @PriorJson, @WorkingCopyJson, @Notes
    );

    COMMIT;
END;
```

Two implementation notes:

- The filtered unique index `UX_WorkflowTemplate_Current` enforces "exactly one current per entity type" at the DB level. If a bug ever caused two rows to be marked current concurrently, the index would prevent the second insert and the transaction would roll back — defense in depth against the application-level invariant.
- The first-time case (no prior current row exists) is handled by the `ISNULL(@PriorVersion, 0) + 1` — version starts at 1, the prior-as-historical UPDATE simply affects 0 rows, and `@PriorJson` is null in the audit row.

## Reading historical versions

Historical versions (`IsCurrent = 0`) open in a read-only view that mirrors the editor's layout. The only difference is no edit affordances — drag handles are absent, text is non-editable, action buttons are absent. Useful for reviewing what was active in any historical period or what an in-flight workstream is referencing.

To make a change, edit the current template. There is no "edit historical" path — historical versions are immutable by design.

## Future enhancements (not in v1)

- Diff / compare-to-version-N UI built against the existing version rows. The data is all there in the audit log's BeforeJson / AfterJson and in the version rows themselves; only the rendering is missing. Add if a real admin asks for it.
- Multi-session draft persistence (using a transient `TemplateDraft` table keyed to admin user) for very large edits that span multiple sessions. Probably unneeded for the small-org case but a clean addition if needed.
- Multi-admin editing with a soft lock on the editor's working copy.
- Per-version notes / changelog beyond the single Notes field at save.
