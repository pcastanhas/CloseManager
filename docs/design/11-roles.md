# Roles admin page

Route: `/admin/roles`
Sidebar section: Configuration (admin only)

## What this page is for

The Roles page is the system-wide definition of what role types exist. It is the simplest admin page in the system: a flat list of roles with create, edit, and soft-delete operations. No nesting, no entity-scoping here — this is purely the vocabulary of roles. Assigning users to roles on specific entities happens on the entity's Roles sub-tab (see `05-app-shell.md`).

Roles are referenced by:
- `EntityRoleAssignment` — who holds a role on a given entity
- `WorkstreamDefStage` — which role is responsible at each stage of a workstream

This is a reference table. Changes here cascade forward: if a role is renamed, any in-flight workstreams are unaffected (their stages snapshotted at instantiation). The name change only affects display strings in the UI going forward.

Examples of roles that will exist: Preparer, TreasuryRE, TreasuryInv, AssetMgr, ValCommittee, Senior, CFO.

## Layout

The page is a full-width list view. No split-pane layout needed — roles are simple enough to edit inline or in a small modal.

### Page header

Breadcrumb: `Configuration › Roles`

Action button right-aligned: **+ Add role** (primary button, opens the add role modal).

### Roles table

A simple table with these columns:

| Column | Notes |
|---|---|
| Code | Short unique identifier, monospace. E.g. `TreasuryRE`. |
| Name | Display name. E.g. `Treasury — Real Estate`. |
| Entities assigned | Count of active `EntityRoleAssignment` rows for this role. Links to a filtered entity search on click. |
| Template stages | Count of `WorkstreamDefStage` rows using this role across all *current* templates. Indicates how widely the role is used. |
| Created | Date only (not time). |
| Status | Active / Inactive pill badge. |
| Actions | Edit icon button, deactivate/reactivate icon button. |

Default sort: Code ascending. Sortable by Code and Name.

Rows are never hard-deleted (soft-delete only). Inactive roles are visible by default with a dimmer row style; a toggle chip above the table ("Show inactive") hides them. The toggle defaults to "off" (inactive roles hidden) but admins doing cleanup will want to show them.

### Empty state

If no roles exist yet (fresh install), show a centered illustration placeholder and the text:

> No roles defined yet. Add the first role to start setting up workflows.

With the **+ Add role** button repeated inline.

## Add / edit role modal

A single `MudDialog`. Used for both add and edit (title changes to "Edit role" and fields are pre-populated).

Fields:

**Code** (required)
- `varchar(40)` per schema
- Uppercase letters, digits, underscores only — enforce with a regex validator
- Must be unique across non-deleted roles — validated on save with a DB check (not client-side only)
- Hint text: "Short identifier used in templates and audit logs. Cannot be changed after roles are in use." (can't change: once a role appears in a `WorkstreamDefStage` or `EntityRoleAssignment` row, its Code should be treated as immutable for FK/logging reasons; UI enforces this by making the field read-only on edit if the role is in use)
- On add: editable. On edit: read-only if in use (show a tooltip "Code is locked — this role is in use by templates or assignments"), editable otherwise.

**Name** (required)
- `nvarchar(100)` per schema
- Free text, no constraint beyond length
- Always editable (safe rename — name is not used as a FK target)

**Notes** (optional, future-proofing)
- A small text area, `nvarchar(500)`, not in the current schema but worth adding as a nullable column — lets admins record what this role is for (e.g. "Third-party asset manager review; only applies to Fund entities")
- If the schema is not modified, omit this field in v1 and add it in a later migration

Footer buttons: **Cancel** | **Save**

Save behavior:
- Validates both fields
- On Code conflict: inline field error ("Code already in use")
- On success: closes modal, shows a brief snackbar confirmation, refreshes the table

## Deactivate / reactivate

Deactivating a role (setting `IsActive = 0`) does not remove it from the system — it prevents it from being selected when adding new template stages or entity assignments. Existing assignments and in-flight workstreams are unaffected.

**Deactivate:** An icon button (person-off icon, neutral color) on each active row. Clicking opens a small confirmation dialog:

> **Deactivate role — {Name}?**
>
> This role will no longer appear in template and assignment dropdowns. Existing assignments and in-flight workstreams are not affected.
>
> [Cancel] [Deactivate]

No reason field required (low-stakes operation, the audit event alone is enough).

**Reactivate:** A reactivate icon button on inactive rows. No confirmation needed — no dialog, just immediate action with a snackbar ("Role reactivated").

## Audit events

Every mutating action on a Role row writes an `AuditEvent`:

| Action | TargetTable | Notes |
|---|---|---|
| `RoleCreated` | `Role` | AfterJson includes Code, Name |
| `RoleRenamed` | `Role` | BeforeJson: old Name; AfterJson: new Name |
| `RoleDeactivated` | `Role` | — |
| `RoleReactivated` | `Role` | — |

Code changes (if they happen before the role is in use) also write an audit event with before/after.

## Edge cases and constraints

**Deleting a role that is in use.** Hard delete is not exposed in the UI. If an admin wants to remove a role that appears in templates or assignments, they must first reassign or remove those references. The deactivate path is the normal exit — if the role is truly obsolete, deactivate it and the dropdowns will stop surfacing it.

**Role with zero assignments and zero template stages.** Can be freely deactivated or its code changed (not yet in use).

**The Preparer role.** Implicitly exists as `OrderIndex = 0` in every `WorkstreamDef`. The system does not need a Preparer row in the `Role` table in principle (stage 0 doesn't use a `WorkstreamDefStage.RoleId` for lock authorization the same way). In practice, it's cleaner to have a Preparer role in the `Role` table so entity-role assignment works uniformly. The templates editor's stage-0 card is implicit (not shown as a user-configurable stage), but the entity's Roles sub-tab still lets an admin assign users to the Preparer role for each entity.

## Visual details

- Table uses a `MudDataGrid` with density set to compact (more rows visible without scrolling — role lists can get long once entities × role types multiply)
- Code column rendered in a monospace span, slightly dimmed color
- "Entities assigned" count is a link; zero count renders as a dash (–) rather than a zero, since zero means "not used anywhere" and is more salient than a number
- Inactive rows use `opacity: 0.55` on the entire row — still readable but clearly secondary
- The "Show inactive" toggle is a `MudSwitch` in the secondary row above the table (not a chip, to avoid confusion with filter chips on other pages)
