# App shell

The app is a single-page application with a persistent left sidebar. The sidebar's content is role-aware — regular users see a small set of items, admins see additional configuration and operations sections.

## Sidebar structure

Three logical groups, separated by dividers:

### Your work (all users)

- **Dashboard** — the user's landing page. Renders the portfolio view: a multi-entity grid showing the state of every entity-close where the user holds any role assignment. Visibility is automatic and uniform — the same component for everyone, scoped by assignments. A staff accountant sees ~10 entities; a CFO assigned across the whole org sees everything. See `01-portfolio-view.md`.
- **Work items** — the user's queue of individual workstreams needing their attention (preparing or reviewing) right now. Shows a red badge with count when work is waiting. Distinct from Dashboard: Dashboard is entity-close status (read-only summary); Work items is workstream-level tasks the user must act on.
- **My history** — a personal audit trail; what the user has worked on, including past closes

### Configuration (admin only)

- **Entities** — the entity setup hub. Each entity has its own page with sub-tabs for Workflow, Roles, Thresholds, History.
- **Roles** — define what roles exist system-wide
- **Workflow templates** — define what workstreams exist for each entity type, with versioning
- **Users** — user management (read-only mostly, since identity is sourced from Entra)

### Operations (admin only)

- **Period management** — add, open, and close periods
- **Active workflows** — operational dashboard for workflows currently in flight; supports restart, refresh from template, clear locks
- **Audit search** — full-text search across the AuditEvent table; auditor surface

### App-wide footer

- **Settings** — user preferences, theme
- **User identity card** — shows current user, role(s), Admin badge if applicable; clicks open sign-out menu

## Why this shape

The order of groups matters: Your Work first because it's what most users open the app for. Configuration next because it's where you set things up. Operations last because it's day-to-day intervention, less common. App-wide concerns sit at the bottom near the user identity.

The "Active workflows" item gets an amber badge when there are workstreams needing admin attention (stuck locks, round 4+ workstreams, restart-eligible items). This is a different signal from the red Work Items badge: red means "you have work to do," amber means "system needs attention."

## Active state and routing

Each nav item maps to a URL pattern. The active state in the sidebar is computed from the current route — never managed independently. Blazor's `NavLink` component handles this with its `Match` parameter.

URLs follow this convention:

- `/` — Dashboard (renders portfolio view)
- `/work` — work items
- `/work/{workstreamId}` — single workstream (preparer or reviewer view, depending on user role and workstream state)
- `/history` — my history
- `/admin/entities` — entity list
- `/admin/entities/{entityId}` — entity setup hub
- `/admin/roles` — roles list
- `/admin/templates` — template list
- `/admin/templates/{templateId}` — template editor
- `/admin/users` — user list
- `/admin/periods` — period management
- `/admin/active-workflows` — active workflows
- `/admin/audit` — audit search
- `/settings` — preferences

URLs change as the user navigates, even though it's an SPA. This is non-negotiable for two reasons:

1. Users will bookmark specific entities, workstreams, periods.
2. Audit trail entries can capture URL ("Maya was on `/work/5042` when she clicked Submit"), which makes forensic queries possible.

## Top bar

A thin top bar above the main content area shows:

- Breadcrumb trail (e.g. `Configuration › Entities › Plaza Tower`)
- Global search (icon button)
- Notifications bell (with badge dot when unread)
- Optionally: a "current period" indicator so the user always knows which period's data they're viewing

## Sidebar collapse state

The sidebar should be collapsible to icon-only mode. Collapsed state persists per user (saved to user preferences). When collapsed, hovering an icon shows the label as a tooltip.

Section dividers stay visible even when collapsed — they provide grouping cues that are more important without labels.

## Permission-driven visibility

Admin items in the sidebar should be hidden from non-admin users entirely, not greyed out. This keeps the sidebar small for regular users (who see 3 items + Settings) and avoids confusing "what's that for?" questions.

The check should be per-item, not a single isAdmin flag, so future permission splits (e.g. "configuration admin" vs "system admin") are a config change rather than a refactor.

## What lives in entity setup (sub-tabs)

The Entities page has list view and detail view. Detail view (per-entity) has sub-tabs:

- **Workflow** — assigned template, list of workstreams that will be created each period, entity-specific overrides
- **Roles** — grid of roles × users for *this entity*. Distinct from the top-level Roles page (which defines what roles exist).
- **Thresholds** — system-default thresholds with per-entity override toggles. Includes variance materiality, aging windows, round-count warnings.
- **History** — per-entity audit view ("what's been changed about this entity over time?")

## Active workflows page

The most operationally sensitive page in the system. Lists workstreams currently in flight, filterable by entity, period, status, locked/stuck. Bulk action affordances along the top:

- Refresh checklist from template (additive only — never removes existing items)
- Clear locks (force-release a stale lock)
- Restart workflow (rebuild fresh; logs `Rebuilt` audit event)

Every action requires a typed reason that gets stamped into the audit log. The page should feel slightly more "serious" than other admin surfaces — confirmations should be deliberate, the visual style slightly more austere.
