# Users admin page

Route: `/admin/users`
Sidebar section: Configuration (admin only)

## What this page is for

The Users page is a read-mostly administrative view of the `User` table. It exists because:

1. Admins need to see who has logged in, when they were last active, and which entity-role assignments they hold — without having to query the DB directly.
2. Admins need a way to soft-delete users who have left the organization, so their names stop appearing in assignment dropdowns.
3. When access issues arise ("why can't Maya see Plaza Tower?") this page is the first place to look.

Identity is sourced exclusively from Entra ID. There are no local credentials, no password fields, no invite flow. A user row is created automatically on first sign-in via the Microsoft.Identity.Web sign-in pipeline (upsert on `EntraObjectId`). Admins cannot create users here — Entra group membership governs access.

## Layout

Full-width list view with a detail panel that slides in on row selection (or opens as a separate route at `/admin/users/{userId}` for deep-link support).

### Page header

Breadcrumb: `Configuration › Users`

No "+ Add" button (users are created on first login, not by admins).

A search field (inline, not a separate filter drawer) — searches across `DisplayName` and `Upn`. Responds on each keystroke with 200ms debounce.

### Users table

Columns:

| Column | Notes |
|---|---|
| Name | `DisplayName` from Entra, synced on each login. |
| Email / UPN | `Upn`. Shown in a smaller, lighter font below the name (two-line cell). |
| Entities assigned | Count of active `EntityRoleAssignment` rows. Clicking expands the detail panel. |
| Last seen | Relative time (e.g., "3 days ago") from `LastSeenUtc`. Users who have never signed in show "Never." |
| Admin | A small shield badge if the user is in the admin Entra group. Not a DB column — derived from the Entra group claim cached at sign-in. |
| Status | Active / Inactive pill. Active = `IsActive = 1 AND IsDeleted = 0`. |
| Actions | "View assignments" icon button (opens detail panel), "Deactivate" icon button (active users), "Reactivate" icon button (inactive users). |

Default sort: Name ascending. Sort by Name, Last seen.

Filter chips above the table:
- **Active** (default on)
- **Inactive** (default off)
- **Admins only** (default off)
- **Never signed in** (useful on cleanup passes)

### User detail panel

Slides in from the right at ~420px wide. Header shows name + UPN + avatar initial. Body has two sections:

**Entity assignments**
A list of all active `EntityRoleAssignment` rows for this user, grouped by entity. Each entity group shows:
- Entity name + code (linked to `/admin/entities/{entityId}`)
- The role(s) held at that entity (comma-separated if multiple)
- "Assigned {date}" for each assignment

If no assignments: "No entity assignments. Assign roles on the Entities page."

No add/remove here — assignments are managed on the entity's Roles sub-tab. This panel is read-only.

**Recent activity**
A short list (last 10) of `AuditEvent` rows where `ActorUserId = this user`, ordered by `OccurredAtUtc` descending. Columns: timestamp, action, target description. A "See full history" link navigates to `/admin/audit?actorUserId={id}` (pre-filtered audit search).

## Deactivate / reactivate

Deactivating a user (`IsActive = 0`) prevents them from taking any action in the app on next request — the auth middleware checks `IsActive` on every page load and returns a "Your account has been deactivated" screen if false. It does not revoke their Entra session immediately (they'll see the page on next navigation, not immediately).

Deactivation does **not** remove entity-role assignments. The assignments remain so the admin can see what the user had access to and so their name appears in historical audit events correctly. The user's name appears in assignment dropdowns only when they are active.

**Deactivate dialog:**

> **Deactivate user — {DisplayName}?**
>
> {DisplayName} holds {N} entity-role assignment(s). They will lose access to the system on next page load. Their historical audit trail is preserved.
>
> Their entity assignments are not removed. To remove assignments, use the Entities page.
>
> [Cancel] [Deactivate]

No reason field (low-stakes, audit event is sufficient).

**Reactivate:** No confirmation dialog — just a snackbar ("User reactivated — {name} can now sign in"). Their existing assignments are still intact.

**Soft-delete (permanent deactivation):** Not exposed in the UI in v1. Soft-delete is reserved for programmatic use (e.g., a data cleanup script) because deleting a user who has audit events would orphan forensic records. The `IsDeleted` flag is in the schema for future use (e.g., a GDPR-driven right-to-erasure process). The UI exposes deactivate only.

## Sync behavior (Entra → User table)

A user row is created or updated on each successful sign-in:
- `EntraObjectId` is the stable identifier — never changes even if UPN changes
- `DisplayName` and `Upn` are overwritten on each login (keeps them current if the user's name changes in Entra)
- `LastSeenUtc` is updated on each login
- `IsActive` is NOT reset by sign-in (if an admin deactivated the user, a sign-in does not reactivate them)

Admins on this page see the most recently synced data. If a user's display name changed in Entra and they haven't signed in since, the old name shows. The "Last seen" column contextualizes this.

## Admin flag display

The `IsAdmin` status is not stored in the `User` table — it's a live claim derived from Entra group membership. On the Users page, the admin badge is computed from a cached set of admin Entra Object IDs (refreshed daily and on each sign-in). This means:

- If you add a user to the Entra admin group, they gain admin access on next sign-in.
- The badge on this page is cached and may lag by up to 24h for users who haven't signed in since the change.
- A "Refresh admin status" button (small, secondary, top right of the table) lets an admin force a re-pull from Graph.

## Audit events

| Action | TargetTable | Notes |
|---|---|---|
| `UserDeactivated` | `User` | AfterJson: { IsActive: false } |
| `UserReactivated` | `User` | AfterJson: { IsActive: true } |
| `UserSynced` | `User` | Written on each login if DisplayName or Upn changed; BeforeJson/AfterJson capture the diff |

User creation on first sign-in also writes a `UserCreated` audit event.

## Edge cases and constraints

**User with active lock holds.** Deactivating a user who currently holds a workstream lock does not auto-clear the lock. The lock will expire via the Hangfire sweep. If it's urgent, the admin should also go to Active Workflows and clear the lock there. A warning in the deactivation dialog is worth surfacing: "This user currently holds {N} active lock(s). Locks will expire automatically or can be cleared on the Active Workflows page."

**User who is the only holder of a role on an entity.** The system does not hard-block deactivation in this case, but a warning is useful: "This user is the only person assigned to {role} on {N} entities. Those workstreams may become unstaffed." (Query: count `EntityRoleAssignment` rows where UserId = this user AND no other active user holds the same (EntityId, RoleId) pair.)

**Name collisions.** Entra allows display name changes. If two users have the same `DisplayName`, the table disambiguates using the UPN shown in the two-line cell. No further action needed — the system uses `UserId` (bigint PK) and `EntraObjectId` as the real identifiers.

## Visual details

- Two-line cells for Name + UPN: name at normal weight, UPN at 0.85em muted color
- "Last seen" as relative time with tooltip showing the absolute UTC timestamp on hover
- The detail panel is a `MudDrawer` variant (Anchor=Right, Persistent=false) that doesn't push content — it overlays with a subtle backdrop
- Admin badge: a small `MudChip` with shield icon, info color, no background fill — should read as informational, not alarming
- The "Never signed in" filter is the highest-value cleanup helper; consider a distinct chip color (neutral/grey) to signal it's informational rather than a status filter
