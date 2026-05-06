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
- **Workstream list**: each workstream as a card, draggable to reorder, expandable to show its stage chain and per-stage checklists
- **Add workstream** affordance at the bottom

The diff summary that appeared in earlier sketches is now folded into the Compare view and the Publish dialog rather than the editor header — too much information for a header that's primarily about navigating the structure.

### Workstream card

Each workstream's collapsed view shows:

- Drag handle on the left for reordering
- Display number ("1.", "2.") computed from order
- Display name and immutable-after-publish code (CASH, DEBT_SVC, etc.)
- "edited" badge if changed in this version, "new in vN" badge if newly added
- **Stage chain pills**: a single line with each stage's role rendered as a pill, separated by arrows (`Preparer → Asset Mgr → Senior`). Reads in one line, scannable across the workstream list.
- Stage count and total checklist item count
- Expand/collapse toggle and delete button

The stage chain pills in the header are read-only — full chain editing happens in the expanded view.

### Expanded view: two sections

When a workstream is expanded, the body splits into two sections:

**1. Approval chain** (top section)

The ordered list of stages that work moves through. Each stage row has:

- Drag handle for reordering stages
- Stage index (0, 1, 2 — read-only)
- Stage kind (`Prepare` for stage 0, `Review` for all others — auto-determined, not editable)
- Role picker (dropdown of system roles)
- Optional display name (e.g., "Treasury sign-off") that overrides the role name in the UI
- Delete button (disabled on stage 0; preparer is required)

An "Add stage" button at the bottom of the chain appends a new Review stage. New stages default to a placeholder role until the admin picks one.

Stage 0 is always present, always Prepare, always required. Admins can't delete it or change its kind.

**2. Default checklists by stage** (bottom section)

One checklist block per reviewer stage (stages 1+). Stage 0 has no checklist of its own — the preparer uses stage 1's checklist as a prep guide at runtime.

Each checklist block has a heading showing the stage index and display name (e.g., "1 · Asset review"), with the items listed indented below. Each item:

- Drag handle for reordering within the stage
- Item text (clickable to edit inline)
- Delete button
- Add button at the bottom of each block

Items added in the current draft are tinted green with an "Added in v4" sub-label.

The checklists stack vertically — all stages visible at once. This works because admins authoring templates think holistically about the chain. (At runtime, reviewers see only their own stage's checklist; that uses tabs, not stacking.)

### Workstream codes are editable in drafts

A code (CASH, DEBT_SVC, etc.) can be renamed inside a draft. Renames are safe because:

- The schema joins by `WorkstreamDefId` (FK), not by code text — existing relationships survive a rename.
- `Workstream.Code` is a snapshot at instantiation; in-flight workstreams keep the original name regardless of what the draft changes.
- SharePoint paths use the snapshot code, so v3 and v4 instantiations live in different folders even if the code was renamed.

What changes is only what *new* instantiations look like. Once a draft is published, future codes are immutable in that published version (codes can only be edited in drafts).

The diff view treats a code rename as `delete + add` rather than a true rename in v1; rename detection can come later if it becomes important.

### Deletions affect only future instantiations

Removing a workstream or checklist item from a draft does not affect any in-flight workstream. In-flight workstreams remain pinned to their original template version (v3) until explicitly rebuilt. Examples:

- An admin deletes the OpEx workstream in v4. Open closes (Oct, Nov, Dec 2025) still have OpEx workstreams running normally — they're on v3. Jan 2026 closes (when opened on v4) will not have an OpEx workstream.
- An admin deletes a checklist item from Property Income in v4. v3-instantiated Property Income workstreams still show all 5 original items. v4-instantiated workstreams will have 4 items.

The only way to make an in-flight workstream pick up a deletion is to rebuild it via Active Workflows. Rebuild loses the in-progress work but creates a fresh workstream from the current Published template. There is no additive-only way to remove items from an in-flight workstream.

### Stage role changes don't propagate via refresh-from-template

The Active Workflows "refresh from template" operation is **additive only**: it adds new checklist items to existing stages but doesn't change the stages themselves. If a draft renames stage 1's role from "Asset Mgr" to "Senior," in-flight workstreams keep the v3 chain (Asset Mgr at stage 1) until rebuilt.

This is intentional. Mid-flight role retargeting would silently change who can act on a workstream that's currently in someone's queue — bad for the people involved and bad for the audit trail. To pick up a stage role change, the workstream must be restarted (via Active Workflows → Restart workflow).

## Auto-save

The editor auto-saves continuously. Drafts have no audit consequence; admins shouldn't have to remember to save. Every reorder, text change, and add/delete writes to the database.

Auto-save semantics: a debounced save (~500ms after last keystroke) for text edits; immediate save for structural changes (add, delete, reorder). The header shows a small "saved at HH:MM" indicator, replaced briefly with "saving..." during writes.

## Single admin assumption

This system is single-admin in v1—only one person ever edits templates. No soft locks on drafts, no presence indicators, no concurrent-edit handling. If a future iteration adds multiple admins, drafts would need a soft lock similar to workstream locks but with a longer TTL (~1 hour); not built for v1.

## Action buttons

Three buttons in the editor header, in increasing destructiveness:

- **Compare to v3** — opens a side-by-side diff view (described below).
- **Discard draft** — soft-deletes the draft. Confirmation dialog, required reason. Reversible only by restoring from soft-delete (admin action).
- **Publish** — the heavy action. Opens the publish dialog (below).

## Side-by-side compare view

Renders v3 (left column) and the current draft (right column) with row-level alignment.

### Alignment

- Workstreams align by `Code`. Match found on both sides → both columns render. Match found only on left → right column shows "— removed in v4 —". Match found only on right → left column shows "— not in v3 —".
- Within a matched pair of workstreams, **stages align by `OrderIndex`**. Stages that exist on only one side (because a stage was added or removed) render with `+` / `−` markers. A stage with the same OrderIndex but a different RoleId on each side renders as a role change (amber).
- Within a matched pair of stages, checklist items align by exact text match; items in only one side render with a `+` (added) or `−` (removed, line-through) marker; items in both sides at different positions render with `↑`/`↓` arrows showing direction of move.

### Visual encoding

- Unchanged workstreams stay collapsed; both sides show just headline metadata.
- Modified workstreams expand to show item-level diffs, both rows tinted amber.
- Added workstreams: green tint on the right side; "— not in v3 —" placeholder on the left.
- Removed workstreams (rare): red tint on the left; "— removed in v4 —" placeholder on the right.
- Diff color legend at the bottom: green = added, red = removed, amber = modified or reordered, neutral = unchanged.

### Diff algorithm (v1 simplification)

The diff in v1 uses straightforward matching rules to keep complexity manageable:

1. **Match workstreams by `Code`.** A code rename is treated as `delete + add` — no rename detection. Admins who rename a code see two changes in the diff; the summary still shows the right counts.
2. **Match stages by `OrderIndex` within a workstream.** Same-position stages on both sides are considered matched even if their roles differ; a role swap shows as a `~` modification, not delete+add. This matches the mental model: "the second stage of this workflow" is a stable concept; the role filling that slot can change.
3. **Match checklist items by exact text within a stage.** Any text edit is shown as `delete + add`. A typo fix on a long item appears as removal + insertion of the corrected version. Imperfect but easy to understand.
4. **Detect reorders only on identical text.** Items present on both sides with the same text but different `OrderIndex` values render as moved (`↑` or `↓`).

Refinements (fuzzy matching, true rename detection, edit-distance-based modification detection) can come later if a real admin says they need them.

### Diff summary at the top

The compare view's header shows the same counts the editor header shows ("+1 ws · +4 items · −1 item · ~2 reordered"). This gives the executive overview at a glance; the side-by-side gives the spatial detail.

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

- True rename detection in the diff (currently treated as delete + add)
- Edit-distance-based modification detection (currently exact text match)
- Template branching (multiple drafts from different parents)
- Per-template-version notes / changelog beyond the single Notes field
- Bulk "promote draft to staging" workflow if an org wants more rigorous template testing
