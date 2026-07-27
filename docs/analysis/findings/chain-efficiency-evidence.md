# Evidence appendix: survey-app-build chain runs (efficiency investigation)

A one-page, citable record of three real runs of the `survey-app-build` op-doc, for the
holistic-plan briefing. Counts and event outcomes below are read from the event logs and
source at the commits named. Interpretations and proxy measures are labeled as such.
Trust code over the state-of-system docs where they disagree.

## Ticket tree (op-doc -> scaffold)

The `survey-app-build` op-doc (8 briefs, 2 plans) scaffolds to:

- ticket 1 = the operation (grandparent)
- ticket 2 = Plan A (parent); ticket 7 = Plan B (parent)
- leaves 3,4,5,6 = briefs 01-04 (Plan A); leaves 8,9,10,11 = briefs 05-08 (Plan B)
- **ticket 11 = brief 08 = conditional-logic-engine** = the op-doc's self-described
  "deliberate stew-on-it brief... Plan carefully before writing code."

## The three runs

All three were invoked as `build chain 1 --batch-implement`. All three ran 8 COLD
per-ticket leaf chains. None batched (see "Batch was unwired").

| Run | Build | Worker | Wall | Ticket 11 | All leaves | cache_read | cache_create | output | LLM calls |
|-----|-------|--------|------|-----------|-----------|-----------|-------------|--------|-----------|
| codex | e05d918 | codex (gpt-5.5) | ~70 min | ReworkCapExceeded | chain failed | 10.3M | - | 141,141 | 24 |
| claude A (claud-chain.json) | d0ee732 | claude-code | ~84 min | Completed, 1 rework | all Completed | 9.87M | 0.84M | 263,670 | 20 |
| claude B (claude-chain2.json) | d0ee732 | claude-code | ~75 min | Completed, 0 rework | all Completed | 18.41M | 0.75M | 244,056 | 16 |

- Event logs: codex = `survey-smoketest-8/.build/events/1-chain-2026-06-07-191540.jsonl`;
  claude A/B = `docs/analysis/claud-chain.json`, `docs/analysis/claude-chain2.json`.
- `d0ee732` (both claude runs) is an ancestor of `e05d918` (codex). Both builds lack the
  batch wiring; the build delta is not the batching difference.

## What killed the codex run

- Ticket 11 got 3 review rounds, ALL returning Rework, then hit the cap. The rationales
  were specific, in-scope, and correct - real defects in ticket 11's own files
  (positional `qN` rule-reference migration on delete and on move/reorder; a broken-ref
  textual collision), with **automated checks green** (`build` PASS, `test` PASS by the
  final round). The review/rework loop did its job; the implementer could not converge in
  initial + 2 rework rounds.
- Cascade: ticket 11 `ReworkCapExceeded` -> parent 7 `ParentStoppedEarly` -> root 1
  `ParentStoppedEarly` -> the root landing is gated on no-child-stopping, so it was
  skipped. Nothing landed - including the fully-completed Plan A sub-chain. ~70 minutes,
  zero tickets shipped to the target.
- Same op-doc, same per-ticket cold-start shape, same `MaxReworkRounds=2`, same near-
  identical review brief: claude-code cleared ticket 11 in 0 rework (run B) and 1 rework
  (run A), while the Codex run did not converge. Worker capability is a plausible contributor,
  but runner build and execution conditions also differed, so this does not isolate a vendor
  effect.

## Cache variation tracks inferred session turns more than rework rounds

Observed:

- claude A did MORE work (2 rework rounds on tickets 9 and 11) and used 9.87M cache_read.
- claude B did LESS work (0 rework) and used 18.41M cache_read - ~2x more.
- `cache_create` (unique context written) was ~equal (0.84M vs 0.75M). The 2x gap is in
  `cache_read`: B re-read its cached context far more often. cache_read/cache_create was
  ~12x (A) vs ~25x (B), an approximate proxy for session turns, not a direct turn count.
- Ticket 11 implement, single session: B read 4.87M cache producing 62K output (many
  turns); A's first implement read 2.16M producing 90K output (fewer turns).

Implication to test: turns/context/cohesion (warm batching, tighter briefs, front-loaded
context) may be a better efficiency lever than reducing rework rounds. In these two
runs, cache/cost did not track how much rework happened.

## Batch was unwired (so --batch-implement was a silent no-op)

- The flag is parsed, validated, and threaded into `ChainPhaseOptions.BatchImplementGroup`,
  but `ChainPhase` is constructed in `src/ThroughlineBuild.Cli/Program.cs` (~line 1829)
  without the `batchWorker:` argument, so `_batchWorker` is null and the batch block is
  unreachable. `grep -rn "batchWorker:" src/` is empty, and the string has never existed
  in `Program.cs` git history - never wired, no regression. (Fix in flight.)
- Consequence: all three runs ran cold per-ticket leaf chains. The "all briefs to one
  warm agent" shape that batch is meant to provide never executed.

## Standing mechanisms relevant to the plan

- `promote`: `[plan] mode = "promote"` (default) skips the plan worker for every brief
  (`src/ThroughlineBuild.Cli/Config.cs`); the brief description is promoted to Ready and
  implemented directly. Brief 08 - the one needing design - got no plan pass.
- `MaxReworkRounds = 2` is a hardcoded const (`src/ThroughlineBuild.Phases/ChainPhase.cs`).
- All-or-nothing landing: the outermost chain lands only when no child stopped early
  (`LandRootIntegrationBranchAsync`, gated on `!anyStoppedEarly`).
- Latent (NOT the cause of this failure): leaf review is built without `ChainTargetBranch`
  (`ChainPhase.cs` review call vs plan/implement/ship), so in a stacked chain its diff
  base is the root target, not the integration branch. Real, worth fixing, but ticket 11's
  rationales were in-scope with green checks, so it did not drive this failure.

## One-line takeaways for the plan

- The loop is not broken and rework is not the cost driver - do not weaken review.
- Give the few hard (design_risk) briefs design up front; promote is right for the rest.
- Cut turns-per-session via warm cohesive batching + tighter briefs.
- Stop one stuck leaf from stranding a whole chain's completed work.
