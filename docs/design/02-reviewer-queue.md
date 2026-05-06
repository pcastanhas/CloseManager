# Reviewer queue

The reviewer queue answers a different question than the portfolio view. The portfolio view is for *managers* asking "where are the bottlenecks?" The reviewer queue is for *reviewers* asking "what should I work on next?"

A reviewer like Treasury-RE is fielding work across 40+ entities. They need:

- A way to triage which item to grab next (not just "by date submitted")
- Enough entity context to recall what they're looking at
- The ability to see what's coming so they can plan their afternoon

## Three sections, not one flat list

The queue has three sections, in priority order:

1. **Needs attention** — items past the aging threshold or sent back. Red tile with left-border accent. The most urgent class of work.
2. **Up next / In progress** — recently submitted items still within normal SLA, plus items the reviewer has already started. Amber if past threshold, neutral otherwise.

A flat list sorted by urgency loses the mental model. Reviewers want to see "what's on fire" and "what's normal," not everything sorted by aging. A predicted "expected later today" third section was considered and cut — inference logic will be wrong often enough to erode trust, and a small team can see what's coming by looking at the Dashboard.

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

J/K navigation was considered for v1 and deferred. For a team reviewing 5-7 items at a time, mouse navigation is fine. Add if reviewers ask for it.

## Sorting and filtering

No view toggles. The two-section urgency layout is the only view. "By accountant" and "by workstream" groupings were considered and cut — the My History page covers the retrospective use case, and targeted feedback to a preparer is a conversation, not a filtered queue view.
