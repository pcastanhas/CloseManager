# Mockups

The visual mockups produced during the design phase were rendered as HTML widgets in a chat session. They are not preserved as source files in this repo because the original output was rendered HTML inline in the conversation, not standalone files.

The mockups covered:

1. **Initial close board (single-org)** — a Kanban-style board with workstream rows and review-stage columns. Used as a starting point but later evolved.

2. **Reviewer's personal queue (single-org)** — first version, before multi-entity work was introduced.

3. **Engagement detail (single-org)** — the document/checklist/comments three-pane reviewer view, in its earliest form.

4. **Portfolio close heatmap** — first multi-entity attempt with fixed columns. Showed the limitations of one-size-fits-all column structure.

5. **Portfolio by entity type** — improved heatmap with entity types in their own sections, each with their own column set. Showed the type-specific routing labels in column headers.

6. **Portfolio workstream strip** — final iteration where columns are dropped entirely. Each row's workstreams are tiles in their natural order, with status color, name, and current reviewer baked into the tile. This is the design that was approved.

7. **Reviewer queue (multi-entity)** — the queue redesigned with three sections (Stuck/Today/Expected), action focus banner, and rich tile content per item.

8. **Reviewer item page** — the full reviewer screen integrating header, audit timeline, document pane (with tabs for primary + supporting), and approval checklist with embedded comment threads.

9. **Approval checklist pane** — close-up of the checklist mechanics, including locked Approve button, three-state items (pending/approved/needs revision), comment threads inside items, and reviewer-added items.

10. **Comments pane (no anchoring)** — the design decision to anchor comments to checklist items rather than to document positions, with the "Re:" reference field as a soft pointer.

11. **Preparer revision flow** — the preparer's view of an item that came back with feedback, with action focus banner, locked Resubmit button, punch-list ordering (needs-fix first), and reply-input baked into the flagged item.

12. **Preparer empty state** — the workstream when first opened: primary upload zone, prep checklist as guide, reference materials sidebar (correctly framed as reference, not starting point).

13. **App shell** — the persistent left sidebar with three nav groups (Your Work / Configuration / Operations), top bar with breadcrumb, and main content area. Plus an entity setup hub with sub-tabs for Workflow / Roles / Thresholds / History.

## Visual style notes

For consistency when implementing the actual UI:

- Flat, minimal aesthetic — no gradients, drop shadows, neon effects
- 0.5px borders in muted colors, generous whitespace
- Typography: sentence case throughout, no all-caps, no title case
- Two font weights only: 400 regular, 500 medium (avoid 600/700 — too heavy)
- Sparing use of color: green for done, amber for in-progress/blocking, red for stuck/failing, blue for informational
- Status badges are pills with low-opacity backgrounds and dark text from the same color family
- Progress bars use 4-5px height; segmented for stage progress
- Icons: Tabler outline icons throughout (matched in MudBlazor)

## Implementation translation

When building in Blazor + MudBlazor, the mockups translate as follows:

- **Three-pane layouts** → `MudGrid` with explicit column ratios
- **Tabs in document pane** → `MudTabs`
- **Checklist items** → custom component built on `MudPaper` + `MudCheckBox` + nested comment thread
- **Audit trail strip** → custom horizontal flex layout with colored event tiles
- **Sidebar nav** → `MudNavMenu` with custom group dividers and active-state styling driven by `NavLink Match`
- **Heatmap tiles** → custom flex layout with `repeat(auto-fit, minmax(110px, 1fr))` for responsive sizing
- **Locked button states** → standard `MudButton Disabled="true"` with a `MudTooltip` explaining why
