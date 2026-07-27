# Operation: chain-smoke

Build a trivially small, self-contained demo - a tiny color palette split into data, a derived summary, and an index - across one plan of three dependent briefs, purely to exercise the chain orchestrator end to end. The operation writes three plain files and nothing else, so it runs in seconds with no build toolchain, yet it has enough structure (a recursed parent plan, three sequentially dependent leaf briefs) to make the difference between a per-ticket `build chain 1` and a warm `build chain 1 --batch-implement` visible in the event log.

## Why this exists

The chain orchestrator has two execution shapes for a cohesive group of sibling briefs: a per-ticket run that cold-boots one implement worker per brief, and a batch run (--batch-implement) that hands the whole group to one warm implement session with a single combined review. Validating that both shapes work end to end, and seeing the concrete difference between them, needs a target small enough to run in seconds yet structured enough to exercise parent recursion, sibling dependencies, and the batch grouping.

This op-doc is that target. It is a deliberately trivial, hermetic operation - three tiny files, no build toolchain, no external services - whose only purpose is to drive the chain machinery so an operator can run `build chain 1` and `build chain 1 --batch-implement` against the same tree and compare. It writes real files and real commits so ship and the event log behave exactly as they would for real work, while keeping the per-brief work small enough to avoid excessive review churn.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Palette demo | - | S |

Single plan.

## Plan A: Palette demo

### Goal

After this plan, a `demo/` directory holds a small color palette dataset, a derived summary of it, and a README index linking both - three files produced by three sequentially dependent briefs, enough to drive the chain through implement, review, and ship in either per-ticket or batch mode.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | palette-data | Tiny JSON color dataset plus its schema doc | - | demo/palette.json, demo/palette-schema.md |
| 02 | palette-summary | Derived markdown summary computed from the dataset | 01 | demo/palette-summary.md |
| 03 | palette-readme | Index README linking the dataset and the summary | 02 | demo/README.md |

### Briefs - detail

#### Brief 01: palette-data

Goal: A tiny, typed color dataset exists on disk for the later briefs to summarize and index. This is the root of the chain - it depends on nothing and creates the artifact the other two read.

Inputs: None. Create a new `demo/` directory at the repository root.

Outputs:
- `demo/palette.json`: a JSON array of at least five objects, each `{ "name": string, "hex": string }` where hex is `#RRGGBB`.
- `demo/palette-schema.md`: a short prose doc naming the two fields and the hex format.

Acceptance:
- [ ] `demo/palette.json` exists and parses as valid JSON
- [ ] it has at least five entries, each with a non-empty `name` and a `#RRGGBB` `hex`
- [ ] `demo/palette-schema.md` exists and documents the `name` and `hex` fields

Notes: The dataset is deliberately tiny and self-contained so the chain exercises implement, review, and ship without any build toolchain. JSON is chosen over prose so the summary brief's job is mechanical and unambiguous, which avoids unnecessary review churn.

OOS:
- Any rendering or transformation of the palette (Brief 02 owns the summary)
- A real color system, accessibility metadata, or more than the two fields

#### Brief 02: palette-summary

Goal: A derived, human-readable summary of the palette exists, computed from the data file Brief 01 produced rather than from a fresh definition.

Inputs: `demo/palette.json` and `demo/palette-schema.md` from Brief 01.

Outputs:
- `demo/palette-summary.md`: states the total color count and lists every color as `name - hex`, in the order they appear in `palette.json`.

Acceptance:
- [ ] `demo/palette-summary.md` exists
- [ ] it states a total count that equals the number of entries in `palette.json`
- [ ] every `name`/`hex` pair from `palette.json` appears in the summary

Notes: This brief reads the artifact Brief 01 produced instead of re-defining the data, which is what makes the 01->02 dependency real and exercises the chain's carried-forward context (the prior brief's touched files reach this implementer). The declared dep is what aligns run order with that carried context.

OOS:
- Editing `palette.json` (read-only here)
- Sorting or grouping beyond source order
- The index doc (Brief 03)

#### Brief 03: palette-readme

Goal: A demo index ties the dataset and its summary together so a reader can find both, completing a strict 01->02->03 chain.

Inputs: `demo/palette.json` (Brief 01) and `demo/palette-summary.md` (Brief 02).

Outputs:
- `demo/README.md`: one or two sentences describing the demo, with relative links to `palette.json` and `palette-summary.md`.

Acceptance:
- [ ] `demo/README.md` exists
- [ ] it links to both `demo/palette.json` and `demo/palette-summary.md`
- [ ] it describes the demo in at most two sentences

Notes: The README depends on the summary existing so the three briefs form a strict sequential chain. With a single warm batch session all three land as one stacked commit series; a per-ticket run builds each in its own cold session - the difference this op-doc exists to make visible.

OOS:
- Any content beyond the index
- Styling, CI, or tooling

## What done looks like

A `build chain 1` run takes the operation through its single plan and three briefs, producing `demo/palette.json`, `demo/palette-summary.md`, and `demo/README.md` in dependency order, each on its own ticket with its own implement, review, and ship. The same op-doc run as `build chain 1 --batch-implement` produces the identical three files, but builds all three in one warm implement session with a single combined review before shipping the stack. An operator comparing the two runs' event logs sees three per-ticket implement/review/ship cycles in the first and a single batch implement plus one combined review in the second, with the parent plan ticket recursed (not batched) in both.
