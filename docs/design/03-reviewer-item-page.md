# Reviewer item page

When a reviewer opens an item from their queue, they land on the reviewer item page. This is the most-used screen in the system and the most carefully designed.

The page is the same component for every reviewer in the chain — Treasury at stage 1, Senior at stage 2, CFO at stage 3 if such a chain existed. What differs across stages is which checklist is active, what action is available (advance vs finalize), where a send-back goes, and what the audit trail has accumulated.

## Three cognitive jobs, three panes

The reviewer's cognitive jobs are:

1. **What is this work?** — answered by the document pane (left)
2. **What's the verification status?** — answered by the approval checklist (right) — *scoped to the reviewer's own stage*
3. **What's the history?** — answered by the audit trail (top strip) — *and, for multi-stage chains, the prior-stage history accordion below it*

These need to coexist on screen because reviewers cross-reference between them constantly. A comment that says "see the Mezz row" only works if the document is right there.

## Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ Header: entity, workstream, parties, round, sitting time, actions│
│ Stage indicator: "Stage 2 of 3 · Senior review (final)"          │
├──────────────────────────────────────────────────────────────────┤
│ Audit trail strip (compact mode for chains > 2 stages)           │
├──────────────────────────────────────────────────────────────────┤
│ Prior stages accordion (collapsed by default)                    │
│   ▸ Stage 1 · Treasury — advanced by Erin · 4 items approved     │
├──────────────────────────────────────────────────────────────────┤
│ Approval progress bar (current stage's items)                    │
├─────────────────────────────────────┬────────────────────────────┤
│                                     │                            │
│   Document pane                     │   Approval checklist       │
│   - Tabs: Primary / Loan docs /     │   - YOUR stage's items     │
│     Bank stmt / linked JE           │   - Items in order         │
│   - Auto-flux flag at bottom        │   - Checkbox + status icon │
│                                     │   - Inline comment threads │
│                                     │   - Advance / Send back    │
│                                     │     (or Finalize at last)  │
│                                     │                            │
└─────────────────────────────────────┴────────────────────────────┘
```

The document pane gets slightly more horizontal space (1.1 : 1 ratio) because schedules have more visual weight to display.

The stage indicator in the header (`"Stage 2 of 3 · Senior review (final)"`) is the small but important addition over the original two-stage design. It tells the reviewer where they are in the chain, what their stage is named, and whether their action will finalize or advance. The "(final)" suffix appears only on the stage with `IsFinalApproval = 1`.

## Approval checklist replaces a separate discussion pane

Earlier sketches had a separate "Discussion" pane on the right. The current design folds discussion *into* the checklist items themselves. This is the structural piece that makes everything click together:

- A comment isn't a free-floating thread — it's the conversation about a specific verification.
- When a reviewer marks an item "needs revision," any comment on that item is the explanation.
- When the preparer responds, their reply lives in the same place.
- When the item is resolved, the conversation is implicitly resolved with it.

This solves a problem that earlier designs were trying to solve with comment-anchoring: "how do we link a comment to the specific row of the document being discussed?" The answer is that we don't anchor to document rows — we anchor to checklist items. The reviewer's question "Mezz interest jumped 18%" goes on the checklist item "Mezz interest reflects current rate." No need to point at row 4 of a particular xlsx version that may not exist next round.

## Each reviewer sees only their own stage's checklist

The right pane shows checklist items scoped to the reviewer's current stage (`ChecklistItem.WorkstreamStageId = WorkstreamStage[OrderIndex = CurrentStageIndex].WorkstreamStageId`). When David at stage 2 (Senior) opens a workstream that has just been advanced by Erin from stage 1 (Treasury), he sees Senior's checklist — not Treasury's.

This matters because each stage verifies different things. Treasury verifies cash tie-out; Senior verifies overall reasonableness. Forcing one reviewer to scroll past another's checklist creates noise and invites accidental approval of the wrong items.

What about the prior stage's checklist? It's available but not active — see the **Prior stages accordion** below.

## Three states per checklist item

- **Pending** (not yet looked at)
- **Approved**
- **Needs revision**

A binary forces the reviewer to make a decision on every item up front. Pending lets them work through items in any order without the system silently treating "not yet" as "approved."

When marking "needs revision," the system requires a comment. A bare "needs revision" without explanation is useless to the preparer (or to the prior reviewer, in the rewind-to-stage-1 case).

## Action button: Advance, Finalize, or Send back

The action button at the top of the screen replaces the original "Approve" button. Its label depends on whether the reviewer's stage has `IsFinalApproval = 1`:

- **At a non-final stage**: button reads "Advance to {next stage display name}" (e.g., "Advance to Senior review"). On click, the workstream advances to the next stage.
- **At the final stage**: button reads "Finalize" or "Mark approved." On click, the workstream transitions to `Approved`.

In both cases, the button is locked until all of the *current stage's* checklist items are Approved. The lock isn't a UI nuisance — it's the structural rule that makes the whole design work. The button shows a count: "Advance · 2 left" or "Finalize · 2 left" tells the reviewer exactly what's between them and done.

A second action — **Needs revision** — sits next to the primary advance/finalize button. This is the one-step send-back path, described below.

The reason for distinct verbs (advance vs finalize) is informational: at a non-final stage, the reviewer's mental model is "I'm signing off so the next person can do their part." At the final stage, the mental model is "this close is done for this workstream." Same lock rule, different post-state, different verb.

The button's role-aware label also clarifies what's happening for the same person across different workstreams. If a Senior is at stage 2 (final) on Plaza Tower's debt service but at stage 2 of 4 (advance to CFO) on a holdco's consolidation, the button label changes accordingly. They don't have to remember which workstream is which structure.

## Send back to a chosen stage

In the original two-stage design, "needs revision" had only one possible destination: back to the preparer. With multi-stage chains, a reviewer at stage N can send back to any earlier stage M < N. Senior at stage 2 might send back to Treasury at stage 1 (asking for re-verification of cash) or to the preparer at stage 0 (asking for a corrected schedule).

### Send-back dialog



### When does Round increment?

Round increments only on the next stage-0 submission (`sp_SubmitWorkstream` from `NeedsRevision` at stage 0). Rewinding to stage 1 and back to stage 2 doesn't change Round. The "round count" in the header reflects how many times the preparer has submitted; it's not a per-stage counter.

This matches what the round count is *for*: it tells the reviewer "this is the third time the preparer has handed me something" — distinct cognitive load from "this is the third time you've handled this internally between approvers." The latter is interesting (visible in the audit trail) but not a primary indicator.

## The audit trail strip — compact mode for long chains

A two-stage workstream's happy path is six events: Instantiated, Started, Submitted, LockAcquired (Treasury), All items approved, Approved. Easy to render as a horizontal strip.

A three-stage workstream's happy path is eight events. Add one round of revision and it's twelve. Add a stage-2-back-to-stage-1 send-back and it's sixteen. The strip can't show all of these inline without becoming illegible.

### Two display modes

**Full mode** (default for chains ≤ 2 stages and round ≤ 2):
Linear horizontal timeline, all events shown, "Now" auto-scrolls into view. Same as the original design.

**Compact mode** (chains ≥ 3 stages or round ≥ 3):
Events grouped by round and stage. The strip shows:

- Each round as a labeled section: "Round 1", "Round 2"
- Within each round, each stage entry is one tile: "Stage 0 prep", "Stage 1 review", "Stage 2 review"
- The tile shows summary info — duration, outcome (advanced / sent back to stage M), actor — rather than every individual event
- Hovering a tile expands a tooltip with the underlying events for that stage entry
- Clicking a tile expands it inline, accordion-style

The current ("Now") tile is always fully expanded regardless of mode.

This solves the legibility problem without losing information. An auditor or the reviewer themselves can drill into any prior stage entry; the default presentation is the structured summary.

### Audit trail events shown

The strip surfaces these event types from `AuditEvent.Action`:

- `Instantiated`, `StatusChanged` (NotStarted → InProgress), `FileUploaded`, `Submitted` (stage 0 → stage 1)
- `LockAcquired` per stage (compact mode rolls these up into "started by {actor}")
- `ReviewerApproved`, `ReviewerFlagged` per checklist item (compact mode shows aggregate count: "4 approved, 1 flagged")
- `StageAdvanced`, `SentBack` (always one step back; reason captured in Notes)
- `Approved` (final stage transition), `Rebuilt`

Comments are *not* events on the timeline — they're attached to checklist items, visible in the right pane. Putting them on the timeline doubled the event count without adding clarity in the original two-stage design; the principle stays.

## Prior stages accordion

Below the audit trail strip, a row of collapsed accordions — one per completed prior stage — gives the current reviewer access to what previous stages did without cluttering the active checklist.

Each accordion header reads: "Stage 1 · Treasury — advanced by Erin · 4 items approved · 1 ad-hoc item added"

When expanded, the accordion shows a read-only view of that stage's checklist:

- Each item with its final `ReviewerStatus` (all should be Approved on a stage that successfully advanced)
- Comments attached to those items, in chronological order
- A small badge on items that were flagged at some point ("flagged in round 1") so the current reviewer can see what almost went wrong

Read-only is the operative word: David at stage 2 cannot un-approve Treasury's items, can't add comments to them, can't change their state. If David has a concern about something Treasury approved, his option is to press **Needs revision** — this sends back to stage 1 (Treasury) with a required reason comment explaining why he wants Treasury to re-verify. He doesn't reach into stage 1's data; he reroutes the workflow.

The accordion is collapsed by default because most stage-2 reviewers don't need to re-read stage 1's verifications every time — they trust them, and the accordion is there for the cases when they want context. On a workstream that's been around the loop a few times, the accordion is also where the audit story is most legible.

For non-existent prior stages (the reviewer is at stage 1 with only stage 0 prepared), the accordion shows stage 0 in the same shape — but stage 0's "checklist" is the preparer's prep guide (which is just stage 1's checklist with `PreparerStatus` markings). Showing that as a stage-0 read-only accordion gives the stage-1 reviewer signal about what the preparer asserted ready vs left for them.

## Reviewer-added items stay scoped to the current stage

Reviewers can add their own checklist items mid-review. These get an "added by {reviewer}" badge so other parties (preparer or future reviewers in the chain) know where the new check came from.

A key constraint: a reviewer-added item is scoped to the current stage. Erin at stage 1 cannot add an item that David at stage 2 will be required to verify. If Erin wants Senior to verify something specific, she has two options: (a) approve and let her stage-1 record stand, leaving the request as a comment on the workstream; (b) hold the workstream at stage 1 with a comment until the preparer addresses something. There is no "add a check for the next reviewer" path.

This matches the design-decisions principle that each stage owns its own verification list. Cross-stage requests happen as comments and rewinds, not as imposed checklist items.

## Action focus banner adapts to stage context

The banner above the page (when applicable) adapts to who handed the workstream to the current reviewer:

- **From the preparer (stage 0 → reviewer stage 1)**: "Maya submitted 4h ago · 5 of 6 items marked ready" — same as the original design.
- **From a prior reviewer (stage 1 → stage 2)**: "Erin advanced 2h ago · 4 items approved at Treasury, 1 ad-hoc added" — context, not crisis. Color: neutral or info.
- **A re-arrival after a sendback (stage 2 → stage 1, then stage 1 → stage 2 again)**: "Erin re-advanced 30m ago after addressing your concern about cash tie-out" — confirms the loop closed.

The banner is informational on a happy path. It becomes an attention banner (amber) when sitting time at the current stage exceeds the per-stage `StuckThresholdHours`. The threshold is per-stage, not per-workstream — Treasury's 24h vs Senior's 72h — so the banner color is honest about whether this item is actually stuck for *this stage*.

## The audit trail is immutable

Comments can be edited or resolved, but timeline events should never be editable. If a reviewer accidentally clicks Advance, the fix is a Send-back from the next stage with a "this was advanced prematurely" comment, plus a manual Rebuild if the chain's been compromised. The audit trail records both events; nothing is overwritten.

## Why this design holds up across formats

Documents in this system can be xlsx, docx, pptx, pdf. Earlier designs that anchored comments to document coordinates ("row 4," "cell C7") were brittle for two reasons:

1. The application would have to render and stably identify positions in arbitrary file formats.
2. Comments would silently rot when the document changed across rounds.

By anchoring to checklist items instead, the design is format-agnostic. The schedule, the memo, the slide deck, the flux narrative — all use the same comment model. The reviewer references location with their own words ("the Mezz row," "section 4.2") in plain text within the comment body.

For the rare case where a screenshot would help ("here's what I was seeing in v3"), comments support attachments. The screenshot becomes durable evidence — it survives even after the underlying document is replaced.

## A note on the same component for every stage

The reviewer item page is one Blazor component, parameterized by `WorkstreamId` and rendered for whichever user has the workstream open. The differences between stages — checklist scope, button labels, accordion contents — derive from the workstream's `CurrentStageIndex` and the joined `WorkstreamStage` row, not from a separate "stage 1 page" / "stage 2 page" / "final page" component. Send-back is always one step (N-1); no branching logic needed.

This is enforced by the data model: there is no "reviewer 1 view" vs "reviewer 2 view" concept in the schema. Every reviewer sees the workstream through the same lens, with their stage-specific data filtering applied at query time. The component is dumb about how many stages exist; the schema tells it.
