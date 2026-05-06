# Preparer flow

The preparer's view shares structure with the reviewer's view but has a different center of gravity. Where the reviewer is *deciding whether to approve*, the preparer is *responding to feedback* (or, on round 1, *executing the prep*).

## How a workstream starts

There is no "Start" button. When the close period opens, all workstreams are instantiated in `NotStarted` state. Maya doesn't create the Plaza Tower debt service workstream — it's already waiting for her in the close.

Clicking the tile takes her to the preparer page, which looks structurally similar to the reviewer page but with empty state. The first upload is implicitly the start. The status transitions from `NotStarted` to `InProgress` automatically on the first preparer action.

This avoids the dead click of pressing Start before doing anything useful. The user opens the workstream and does the work; state flows from activity, not from explicit transitions.

## The prep checklist

The same checklist the reviewer will use is visible to the preparer from the moment the workstream is opened. This serves two purposes:

1. **Verification criteria for the reviewer** (their job)
2. **Prep guide for the preparer** (her job)

Maya can mark each item "Mark ready" as she completes the prep. When she submits, those "ready" markers carry over as a signal to Erin: items Maya marked ready are her assertion of "I think this is done." Items she didn't mark ready are her implicit ask for Erin's input.

The Mark Ready / Note buttons mirror the reviewer's Approve / Needs Revision buttons but with different verbs. Maya isn't approving anything — she's asserting completion of prep work. Erin still has to verify.

## The "Ready for review" gate

The only structural requirement for submitting is that a Primary file exists. Not all 6 checklist items need to be marked Ready. Maya might submit with covenant compliance still unchecked because she's specifically asking Erin to validate that approach. The submit confirmation should show "5 of 6 marked ready · 1 you want Erin to review fresh" so it's clear what state she's submitting in.

This gate is gentle. The system isn't trying to force-march the preparer through perfect checklist completion. It's preventing the obvious mistake of submitting with no document attached.

## Empty state — what's the starting point?

A point that came up during design and got corrected: the prior month's submission file is **not a starting point**, only a reference. The numbers are wrong for the new period.

The actual starting point is an export from the accounting software (Yardi, NetSuite, Sage Intacct, etc.). The empty-state UI presents:

- A primary upload zone for the export ("Drop your debt schedule export")
- A "Reference materials" section below, containing:
  - Last period's submitted version (for sanity comparison only)
  - Permanent file documents (loan agreements, org docs)
  - Standing notes from prior closes (institutional memory)

The reference materials are populated automatically based on entity and workstream. Maya doesn't curate them.

Future enhancement: direct integration with accounting software. "Import latest from Yardi" replaces "drop file here" when the integration is configured. Reduces stale-export errors and saves real time. v1 is manual upload.

## Revision flow — what changes when an item comes back

When Erin requests changes, Maya gets the workstream back in `NeedsRevision` state. The preparer page now has a different center of gravity:

### Action focus banner at top

"Erin requested 1 fix · sitting 28h" is the first thing Maya reads. It also flags that Erin added 1 new check after Maya's resubmit so she's not surprised. Color is amber, not red — this is work to do, not a crisis.

### Resubmit button is locked

Mirror of the reviewer's Approve button: disabled with a count ("Resubmit · 1 to address"). Same lock pattern, different rationale: she can't resubmit until the conversation on the flagged item shows resolution.

### Punch-list ordering

The checklist on the right is reordered into three sections:

1. **Needs your fix** — items the reviewer flagged. Top of the page.
2. **New checks Erin added** — items she added but hasn't yet decided on. Maya can see them but can't act on them.
3. **Approved by Erin** (collapsed-style) — done items. Single line each. Visible but not engaging Maya's attention.

This priority order means Maya's eye lands on what she has to do.

### Reply input is baked into the flagged item

The flagged checklist item shows the entire conversation thread inline:

- Erin's original concern
- Maya's prior response (round 1)
- Erin's follow-up (the new comment requesting more info)
- A reply input as the natural next step

No separate "open thread" UI, no global comment composer. Maya reads top-to-bottom and types her answer at the bottom. Erin's latest message is highlighted in amber to make clear what Maya needs to respond to.

### Document pane has an upload zone

The dashed-border "Drop a new version to replace v3 · creates v4" is Maya's primary affordance for actual document changes. Replies in comments are for explanation; new document versions are for actual fixes.

A "v1, v2" history button shows what she submitted previously, useful when iterating across rounds.

### "Add" tab on supporting docs

Maya sometimes needs to attach new evidence (a loan amendment doc, an updated bank confirm). Putting "Add" inline with existing tabs makes that one click rather than buried in a menu.

## When uploading a new version, ask if it addresses the flag

When Maya uploads v4 while a "needs revision" item is open, the system should ask:

> Does this version address the Mezz interest issue?

And offer to flip the item from "needs revision" to a state like "addressed, awaiting re-verification." This keeps the checklist honest about what work has been done. Erin's view of that item then says "Maya marked addressed in v4" so she knows what to look for.

## Resubmit confirmation

When Maya clicks Resubmit, the system confirms:

> Resubmit round 3 to Erin? 1 item marked addressed.

This is the parallel to Erin's approval confirmation — a deliberate moment that creates a clean audit event. Reduces accidental resubmits and gives Maya a chance to add a final note ("Updated rate from Oct 1 onwards. Working capital line activity unchanged.").
