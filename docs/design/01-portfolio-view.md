# Portfolio view

The portfolio view is the answer to the question "across all entity-closes I can see, where are the bottlenecks?" It is rendered as the **Dashboard** for every user. The visual is the same regardless of who's looking; what differs is the *filter*: the workstreams a user can see are exactly those for entities they are assigned to in any role.

## Terminology

A few terms have been used interchangeably during design and are worth pinning down:

- **Dashboard** — the sidebar item and the route (`/`). Always the user's landing page. Always renders the portfolio view component.
- **Portfolio view** — the multi-entity grid described here. The same component for everyone, scoped automatically by the user's role assignments.
- **Heatmap** — earlier informal term for this view. Avoid it going forward; the design isn't a fixed-cell heatmap anymore (see below).
- **Work items** — a separate sidebar item. The queue of items waiting on the current user specifically. Different from Dashboard: the Dashboard shows entity-close *status*; Work items shows individual workstream-level *tasks* the user must act on now.

## Visibility model

The Dashboard shows every workstream where the current user holds *any* role assignment on the entity — Preparer, Reviewer, or otherwise. Read access flows broadly from entity-role assignment; the user can see how all workstreams on "their" entities are progressing, even ones they can't currently act on. Write access (the lock-acquisition rule) is separately gated to the role appropriate for the current status.

A staff accountant assigned to 10 entities sees ~50-70 workstreams. A reviewer assigned to many entities for one role sees a wider but possibly shallower slice. A CFO assigned to a CFO role on every entity sees the whole org. There is no "manager view" or "full-portfolio override" — the assignment table itself is the visibility model. To give someone broader visibility, give them more role assignments.

## The job to be done

The portfolio view answers "where are the bottlenecks?" within the slice of work the user can see. For a staff accountant this is "are any of my entities stuck?" For a senior reviewer it's "where is my queue backed up?" For a CFO it's "is the whole close on track?" Same question, different scopes — same component renders all three.

## The wrong design we considered first

An early sketch had the portfolio view as a fixed-column heatmap: rows for entities, columns for workstreams (Cash, AR, AP, Accruals, IC, Financials), each cell colored by status.

This works for one organization with a single set of books. It breaks for multi-entity work in two ways:

1. **Different entity types have different workstreams.** A real estate asset has Property Income and Debt Service; an investment fund has Valuations and Capital Activity. A fixed-column grid forces an N/A in most cells, and the schema is rigid.
2. **Workstream names and order are heterogeneous.** Even when two entity types share a workstream (Cash, Financials), the order may differ, and the routing definitely does (Cash for an asset goes to Treasury-RE; Cash for a fund goes to Treasury-Inv).

## The right design

Each row is `(entity, accountant)` in the left rail. The cells to the right are the workstreams *for that entity, in that entity's natural order*. Each workstream is a tile showing:

- The workstream name (e.g. "OpEx", "Debt service")
- Current status, encoded by tile color (green / amber / red / neutral)
- Sub-text showing the current reviewer or state ("w/ Asset Mgr · 8h", "w/ Treasury-RE · 28h", "approved", "prep · David R.")

This is much more flexible:

- **No fixed columns.** Each row owns its own workstream sequence. Variability between entities is natural, not exceptional.
- **Tile self-labels.** No column headers needed because each tile carries its own name. Reading a row is "what is it, who has it, how long."
- **Tiles flex with row width.** With `flex: 1` and a `min-width`, rows with fewer workstreams have wider tiles; rows with more workstreams compress before scrolling. Variability handled gracefully.
- **Dependency state is visible.** A tile that's blocked by an upstream workstream renders muted with "blocked by val." A reviewer sees green→red→green→blocked across a row and immediately understands the bottleneck.

## Period-not-opened banner

When the current month has no open period yet, a banner renders at the top of the Dashboard above the entity grid:

> **The {month} close hasn't been opened yet.** Contact your admin to open it — your workstreams will appear here once it's open.

This replaces the confusing empty-grid state with a clear explanation. One conditional render; saves a lot of "why is my queue empty?" questions at the start of each month.

The banner appears when: the current calendar month has no `ClosePeriod` rows for any entity the user is assigned to. It disappears once the period is opened.

## Grouping

Entities are grouped by entity type — Real Estate Assets, Investment Funds, Operating Cos, Holding Cos — with each section having its own header and aggregate badge ("3 stuck", "healthy", "2 at risk"). Healthy sections collapse to a single row. With 100+ entities this grouping is essential; a flat list of 100 rows is unscannable.

No grouping toggles ("by accountant", "by reviewer"). They add UI complexity that a small co-located team handles conversationally. Add them if the team grows or goes remote.

## Entity-level overrides

Some entities have additional workstreams beyond the template's defaults. Oak Industrial, a real estate asset, also has an IC tax workstream because of its complex inter-company structure. These should be visually distinguished — a dashed border or different background — so admins can quickly see which entities deviate from the standard template.

The override mechanism is captured in the schema as additional rows in the entity-specific configuration; the rendering rule is purely UI ("if not from the standard template, render with override styling").

## Reviewer load panel

~~Removed in v1 simplification.~~ For a 12-person team in the same office, queue depth is a conversation, not a dashboard panel. The Work Items page already shows each person their own load. If the team grows or remote work becomes the norm, add it then.
