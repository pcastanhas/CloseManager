# Reviewer item page

When a reviewer opens an item from their queue, they land on the reviewer item page. This is the most-used screen in the system and the most carefully designed.

## Three cognitive jobs, three panes

The reviewer's cognitive jobs are:

1. **What is this work?** — answered by the document pane (left)
2. **What's the verification status?** — answered by the approval checklist (right)
3. **What's the history?** — answered by the audit trail (top strip)

These need to coexist on screen because reviewers cross-reference between them constantly. A comment that says "see the Mezz row" only works if the document is right there.

## Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ Header: entity, workstream, parties, round, sitting time, actions│
├──────────────────────────────────────────────────────────────────┤
│ Audit trail strip (timeline events left to right, "Now" on right)│
├──────────────────────────────────────────────────────────────────┤
│ Approval progress bar                                            │
├─────────────────────────────────────┬────────────────────────────┤
│                                     │                            │
│   Document pane                     │   Approval checklist       │
│   - Tabs: Primary / Loan docs /     │   - Items in order         │
│     Bank stmt / linked JE           │   - Checkbox + status icon │
│   - Auto-flux flag at bottom        │   - Inline comment threads │
│                                     │   - Approve / Needs rev    │
│                                     │                            │
└─────────────────────────────────────┴────────────────────────────┘
```

The document pane gets slightly more horizontal space (1.1 : 1 ratio) because schedules have more visual weight to display.

## Approval checklist replaces a separate discussion pane

Earlier sketches had a separate "Discussion" pane on the right. The current design folds discussion *into* the checklist items themselves. This is the structural piece that makes everything click together:

- A comment isn't a free-floating thread — it's the conversation about a specific verification.
- When a reviewer marks an item "needs revision," any comment on that item is the explanation.
- When the preparer responds, their reply lives in the same place.
- When the item is resolved, the conversation is implicitly resolved with it.

This solves a problem that earlier designs were trying to solve with comment-anchoring: "how do we link a comment to the specific row of the document being discussed?" The answer is that we don't anchor to document rows — we anchor to checklist items. The reviewer's question "Mezz interest jumped 18%" goes on the checklist item "Mezz interest reflects current rate." No need to point at row 4 of a particular xlsx version that may not exist next round.

## Three states per checklist item, not two

- **Pending** (not yet looked at)
- **Approved**
- **Needs revision**

A binary forces the reviewer to make a decision on every item up front. Pending lets them work through items in any order without the system silently treating "not yet" as "approved."

When marking "needs revision," the system requires a comment. A bare "needs revision" without explanation is useless to the preparer.

## Approve button is locked

The Approve button at the top of the screen is locked until all checklist items are Approved. The lock isn't a UI nuisance — it's the structural rule that makes the whole design work. Approval can't be a casual click. The button also shows a count: "Approve · 2 left" tells the reviewer exactly what's between them and done.

## The audit trail strip

A horizontal timeline of events: Prepared, Submitted, Changes Requested, Resubmitted, Awaiting Review (or whatever sequence applies). Events are colored by type — prep events are neutral, review actions are amber, the current "now" event is red if it's been sitting too long.

Two important properties:

- The audit trail is **immutable**. Comments can be edited or resolved, but timeline events should never be editable. If a reviewer accidentally clicks Approve, the fix is a new "Reverted" event in the timeline, not an edit to the old one.
- The "Now" event auto-scrolls into view when the page loads. Reviewers always see the current state without panning.

## Reviewer-added items

Reviewers can add their own checklist items mid-review. These get an "added by Erin" badge so the preparer knows where the new check came from. When the preparer resubmits, the reviewer-added items are part of the punch list.

## Why this design holds up across formats

Documents in this system can be xlsx, docx, pptx, pdf. Earlier designs that anchored comments to document coordinates ("row 4," "cell C7") were brittle for two reasons:

1. The application would have to render and stably identify positions in arbitrary file formats.
2. Comments would silently rot when the document changed across rounds.

By anchoring to checklist items instead, the design is format-agnostic. The schedule, the memo, the slide deck, the flux narrative — all use the same comment model. The reviewer references location with their own words ("the Mezz row," "section 4.2") in plain text within the comment body.

For the rare case where a screenshot would help ("here's what I was seeing in v3"), comments support attachments. The screenshot becomes durable evidence — it survives even after the underlying document is replaced.
