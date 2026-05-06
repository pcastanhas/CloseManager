# Portfolio view

The portfolio view is the answer to the question "across all entity-closes, where are the bottlenecks?" It's the primary surface for managers and senior accountants. For staff accountants, it's secondary — they spend most of their time in their own work-items view.

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

## Grouping

Entities are grouped by entity type, with each section having its own header and aggregate badge ("3 stuck", "healthy", "2 at risk"). Healthy sections collapse to a single row. With 100+ entities, this grouping is essential — a flat list of 100 rows is unscannable; 4 sections of 20-40 entities each, with healthy ones collapsed, lets a manager scan the whole portfolio in 5 seconds and drill into the problem area.

Three view toggles in the header:

- **By type** (default): Real Estate Assets, Investment Funds, Operating Cos, Holding Cos
- **By accountant**: rows grouped by who's preparing them, useful for capacity questions ("Maya is overloaded")
- **By reviewer**: useful for senior reviewers asking "what's pending in my queue across entities?"

## Entity-level overrides

Some entities have additional workstreams beyond the template's defaults. Oak Industrial, a real estate asset, also has an IC tax workstream because of its complex inter-company structure. These should be visually distinguished — a dashed border or different background — so admins can quickly see which entities deviate from the standard template.

The override mechanism is captured in the schema as additional rows in the entity-specific configuration; the rendering rule is purely UI ("if not from the standard template, render with override styling").

## Reviewer load panel

The portfolio view includes a "Reviewer load right now" panel at the bottom showing per-role queue depth and average aging. This makes the implicit signal in the heatmap (lots of amber Cash cells = Treasury bottleneck) into an explicit one. Critical when a single reviewer covers many entities.

For an org where Treasury-RE is one person and they're reviewing bank recs for 40+ entities, that's the single biggest failure point in the close, and it deserves permanent surface real estate.
