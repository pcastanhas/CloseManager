# Workflow templates editor

The Workflow Templates page is where admins define the workflow shape for each entity type. It is the most structurally complex admin surface because:

- Templates are versioned, with downstream effects: each version snapshots into in-flight workstreams.
- Each version contains an ordered tree of workstreams and checklist items.
- Publishing a new version is a deployment-like action with consequences.

## Version lifecycle: Draft → Published → Superseded

Every template version has one of three states:

- **Draft** — editable freely. Has a reserved version number but no `EffectiveFromPeriod`. No close has been instantiated from it.
- **Published** — has an `EffectiveFromPeriod` set; is the current source of truth for instantiating workstreams in periods >= that date. Immutable.
- **Superseded** — was previously Published; replaced by a newer Published version. Stays in the database (in-flight workstreams may still reference it) but can't be assigned to new entities or used for new instantiations.

Transitions:
- `Draft → Published` happens via the publish dialog.
- `Draft → discarded` is a soft delete (`IsDeleted = 1`).
- `Published → Superseded` happens automatically when a newer version is published, in the same transaction.

At most one Draft and one Published per entity type, enforced via filtered unique indexes:

```sql
CREATE UNIQUE INDEX UX_WorkflowTemplate_Draft
    ON WorkflowTemplate(EntityTypeId)
    WHERE Status = 'Draft' AND IsDeleted = 0;

CREATE UNIQUE INDEX UX_WorkflowTemplate_Published
    ON WorkflowTemplate(EntityTypeId)
    WHERE Status = 'Published' AND IsDeleted = 0;
```

## Schema additions

The base schema's `WorkflowTemplate` table needs the lifecycle columns:

```sql
ALTER TABLE WorkflowTemplate ADD
    Status              varchar(20) NOT NULL DEFAULT 'Draft',
    PublishedAtUtc      datetime2(3) NULL,
    PublishedByUserId   bigint NULL REFERENCES [User](UserId),
    SupersededAtUtc     datetime2(3) NULL;
```

`EffectiveFromPeriod` is left nullable for drafts (will be NOT NULL once published, enforced by the publish stored procedure).

## Templates list page

Index of all entity types and their template versions. Each entity type expands to show its version history with state badges:

- Draft = blue/info badge
- Published = green badge ("currently active")
- Superseded = neutral badge with reduced row opacity

Each row shows scale context: "5 workstreams · 30 checklist items · used by 42 entities · 487 instantiated workstreams." The instantiated count tells admins how much real-world data references this version.

A "New draft" button per entity type creates a new version branched from the current Published version. Disabled (with tooltip) if a draft already exists.

## Template editor

The deep edit surface for a single template version. Editing is only allowed for Draft versions; Published and Superseded open in a read-only view.

### Layout

- **Header**: version number, state badge, branch info ("Branched from v3"), last edited info, action buttons
- **Diff summary** below the header: "+1 workstream, +3 check items, −1 check item, ~2 reordered" — the executive summary of what's changed since v3
- **Workstream list**: each workstream as a card, draggable to reorder, expandable to show its checklist items
- **Add workstream** affordance at the bottom

### Workstream card

Each workstream shows:

- Drag handle on the left for reordering
- Display number ("1.", "2.") computed from order
- Display name and immutable code (CASH, DEBT_SVC, etc.)
- "edited" badge if changed in this version, "new in vN" badge if newly added
- Checklist item count
- Routing: "Preparer → Treasury-RE"
- Expand/collapse toggle and delete button

### Checklist items inside a workstream

When expanded, each checklist item is a row with:

- Drag handle for reordering
- Item text (clickable to edit inline)
- Delete button
- Add button at the bottom

Items added in the current draft are tinted green with an "Added in v4" sub-label. Items being deleted (when previewed) would be tinted red. The diff is part of the editor, not just the publish dialog.

### Workstream codes are immutable

Once a code is used in a published version, it can't be renamed. Renaming requires deleting the old workstream and adding a new one with a different code. Codes appear in SharePoint paths and audit trails, where stability is more valuable than expressiveness.

## Auto-save

The editor auto-saves continuously. Drafts have no audit consequence; admins shouldn't have to remember to save. Every reorder, text change, and add/delete writes to the database.

Auto-save semantics: a debounced save (~500ms after last keystroke) for text edits; immediate save for structural changes (add, delete, reorder). The header shows a small "saved at HH:MM" indicator, replaced briefly with "saving..." during writes.

## Concurrent editing

Drafts have a soft lock similar to workstream locks but with a longer TTL (1 hour). When a second admin opens a draft another admin holds, they see "Sarah K. has been editing for 12 minutes" with options to wait or take over (which writes an audit event). Auto-save means hand-off doesn't lose work.

## Action buttons

Three buttons in the editor header, in increasing destructiveness:

- **Compare to v3** — opens a diff summary (additions, deletions, reorders, modifications). For v1, summary-only; side-by-side tree diff is a future enhancement.
- **Discard draft** — soft-deletes the draft. Confirmation dialog, required reason. Reversible only by restoring from soft-delete (admin action).
- **Publish** — the heavy action. Opens the publish dialog (below).

## Publish dialog

The gate that turns a draft into a Published version. Sections:

1. **Header** — version, entity type, "v3 will be superseded"
2. **Effective from period** — input field accepting yyyyMM. Closes opening on or after this period will use v4. Earlier closes stay on v3.
3. **Diff summary** — same content as the editor header but more verbose, with each change called out individually
4. **Impact section**:
   - "42 entities currently use this template"
   - "Jan 2026 close (when opened) will instantiate using v4"
   - "Existing in-flight workstreams (Oct/Nov/Dec 2025) stay on v3"
   - Pointer to Active Workflows → Refresh from template for selective in-flight upgrades
5. **Publish notes** — required. Stamped into `WorkflowTemplate.Notes`.
6. **Action buttons** — Cancel and Publish. Publish disabled until notes are filled.

No typed-confirm phrase. Publishing is significant but reversible (a v5 can revert) — the cancel button + required notes field is sufficient gate. Restart workflows are different and need the typed phrase; publish is more like a deployment.

## Publish stored procedure

```sql
CREATE PROCEDURE sp_PublishTemplate
    @TemplateId bigint,
    @EffectiveFromPeriod char(6),
    @Notes nvarchar(1000),
    @ActorUserId bigint,
    @ActorEntraObjectId uniqueidentifier
AS
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRAN;

    DECLARE @EntityTypeId int = (
        SELECT EntityTypeId FROM WorkflowTemplate
        WHERE WorkflowTemplateId = @TemplateId
    );

    -- Supersede the current Published version (if any)
    UPDATE WorkflowTemplate
    SET Status = 'Superseded',
        SupersededAtUtc = SYSUTCDATETIME()
    WHERE EntityTypeId = @EntityTypeId
      AND Status = 'Published'
      AND IsDeleted = 0;

    -- Publish the draft
    UPDATE WorkflowTemplate
    SET Status = 'Published',
        EffectiveFromPeriod = @EffectiveFromPeriod,
        PublishedAtUtc = SYSUTCDATETIME(),
        PublishedByUserId = @ActorUserId,
        Notes = @Notes
    WHERE WorkflowTemplateId = @TemplateId
      AND Status = 'Draft';

    IF @@ROWCOUNT = 0 BEGIN
        ROLLBACK;
        THROW 50010, 'Template not in Draft state', 1;
    END;

    INSERT INTO AuditEvent (...) VALUES (..., 'TemplatePublished', ...);

    COMMIT;
END;
```

## Reading published or superseded versions

Published and Superseded versions open in a read-only view that mirrors the editor's layout. The only difference is no edit affordances — drag handles disappear, text is non-editable, action buttons are absent. Useful for reviewing what was active in any historical period.

To make a change to a Published version: create a new draft. The "New draft" button on the templates list page is the one path; there is no "edit Published" button.

## Future enhancements (not in v1)

- Side-by-side or inline tree diff (rather than summary-only)
- Template branching (multiple drafts from different parents)
- Per-template-version notes / changelog beyond the single Notes field
- Bulk "promote draft to staging" workflow if an org wants more rigorous template testing
