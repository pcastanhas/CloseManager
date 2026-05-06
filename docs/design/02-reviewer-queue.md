# Reviewer queue

The reviewer queue answers a different question than the portfolio view. The portfolio view is for *managers* asking "where are the bottlenecks?" The reviewer queue is for *reviewers* asking "what should I work on next?"

A reviewer like Treasury-RE is fielding work across 40+ entities. They need:

- A way to triage which item to grab next (not just "by date submitted")
- Enough entity context to recall what they're looking at
- The ability to see what's coming so they can plan their afternoon

## Three sections, not one flat list

The queue has three sections, in priority order:

1. **Stuck and blocking** — items past the aging threshold, especially ones whose stuck-ness is blocking downstream work. Red tile with left-border accent. The most urgent class of work.
2. **Today** — recently submitted items still within normal SLA. Amber if blocking, neutral otherwise.
3. **Expected later today** — workstreams the system expects to land in the queue based on preparer activity (not yet submitted, but in late-stage prep). Helps reviewers plan whether to batch.

A flat list sorted by urgency loses the mental model. Reviewers want to see "what's on fire," "what's normal," and "what's coming," not just "everything sorted by aging."

## Action focus banner

The single most blocking item is surfaced separately as a banner above the queue. When someone has 11 items waiting, scanning for the truly urgent one wastes attention — the system already knows which item is most blocking, so it should say so explicitly. One banner, not five.

## Tile information density

Each item in the queue shows:

- Entity name and entity type (left rail)
- Preparer name (so the reviewer can mentally pre-load context)
- Workstream name + tile color encoding urgency
- Round count ("1st round" vs "2nd round" — different cognitive task)
- Sitting time
- Auto-flux flags ("auto-flux green" = likely quick approve)
- Blocking pill if downstream work is waiting

The "blocking" pill differentiates two cases of stuck items:

- A stuck item that's blocking downstream is a 5-alarm fire
- A stuck item with no downstream pressure is just inefficient

These warrant different priorities even at the same age.

## What we deliberately don't include

**No bulk approve.** Every approval should be a deliberate act with the reviewer's eyes on the actual document. Bulk approval breaks the audit story — the SOX-relevant question "did the reviewer actually look at this?" becomes ambiguous. The Open button is the only way forward to approval.

**No claim-and-hold.** Anyone with the role can pick up any item. The lock prevents collisions; otherwise reviewers load-balance naturally. If two reviewers open the same item simultaneously, the second one sees a "locked by Sam (12 min remaining)" banner and picks a different item.

## Keyboard navigation

Reviewers blast through queues. J/K to move between items, Enter to open, is the difference between 3 minutes per item and 30 seconds. Worth implementing in v1, even if just for the queue.

## Sorting and filtering toggles

Three view toggles:

- **By urgency** (default): the three-section layout above
- **By accountant**: groups items by preparer, useful when reviewers want to give targeted feedback to one person ("Maya, here's what I'm seeing across your entities this round")
- **By workstream**: rare, but useful for monthly retrospective ("show me all the debt service reviews I did this month")

Filter persistence: the toggle state should survive across sessions per user. Treasury-RE folks who think entity-first vs workstream-first have different mental models.
