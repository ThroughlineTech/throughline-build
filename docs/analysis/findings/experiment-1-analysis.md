# Survey smoketest - build-run analysis log

> Source note: code paths in this historical finding refer to target repositories inspected
> during the original analysis. Those repositories are retained separately and are not published
> in this evidence package; the paths are provenance references, not public links.

A running log of autonomous `build chain` / `build batch` runs against the survey-app
op-doc, analyzed per `op-docs/review-prompt.md`. Each "Run N" section records the telemetry
parsed from `.build/events/*.jsonl`, an independent re-verification of the working tree
(build + tests run by the analyst, not trusted from the chain's self-report), a code-quality
read weighted toward the deliberately convoluted brief (the conditional-logic engine), and a
comparison against prior runs.

Telemetry note: this repo's `build` CLI (version `0.1.0+5b74e29`) emits a newer event schema
than `Throughline Build/docs/event-log-format.md` documents. Two deltas matter for parsing: (1) each
ticket's chain mints its own `chain_session_id` / `SessionId` rather than sharing one across
the run, and (2) there is a new gate event `Kind 13` on `Phase 10` carrying
`{gate_wall_ms, gate_attributable_rework_rounds, cascade_caught, false_fails}` that the doc
predates. Cost (`cost_usd`) IS emitted here (vendor=anthropic) on every `LlmCall`.

---

## Run 1 - 2026-06-08: chain build of the front-loaded experiment-1 op-doc (Sonnet impl + Sonnet verify)

### Setup

- Project: `sst9` (the chain arm of the `survey-experiment-1` op-doc; the sibling
  `survey-smoketest-9-batch` / `survey-smoketest-9-chain` directories are scaffolded but
  empty, so this is the only experiment-1 run with telemetry).
- Op-doc: `op-docs/01-survey-experiment-1.md` - 8 implementation briefs across two plans
  (Plan A: scaffold, data-model, take-survey, my-responses; Plan B: admin-surveys,
  admin-results, results-chart, conditional-logic-engine). The op-doc is the "front-loaded"
  revision: each brief inlines its read-map, design, failure-modes, and exact Verify commands.
- Run analyzed: `.build/events/1-chain-2026-06-08-135812.jsonl` (8 per-ticket chains
  concatenated; tickets 3,4,5,6,8,9,10,11 -> briefs 01-08; ticket/brief offset is because
  scaffold also created plan/epic tickets 1,2,7).
- Worker: `claude-code`. Implement phase model `claude-sonnet-4-6`; Review phase verifier
  model `claude-sonnet-4-6` (same model for implement and review). No Opus escalation.
- Notable: Plan and Ship phases spawned no worker and emitted no `LlmCall` - planning is
  LLM-free because the brief carries the plan. So the cost/token figures below are the
  COMPLETE LLM spend of the run, not an implement-only subset.

### Quantitative

| Metric | sst9 (chain, experiment-1) |
|---|---|
| Worker / model | claude-code; impl=claude-sonnet-4-6, verify=claude-sonnet-4-6 |
| Tickets completed | 8 / 8 (all outcome=Completed, all InReview->Done) |
| Rework rounds | 0 (across all 8 tickets) |
| Verifier verdicts | 8 Pass / 0 Rework / 0 Fail (checks_failed_count=0 each) |
| Build gate (chain) | passed; gate (Kind13): 0 false_fails, 0 cascade_caught, 0 gate-rework, 68.6s total gate wall |
| Independent re-verify | build PASS (tsc -b + vite, 53 modules) + 221 tests PASS (14 files, 0 fail) |
| Wall clock (chain) | 86.9 min (sum chain duration 5211s; timestamp span 5214s - sequential) |
| Output tokens | 254,459 |
| Input tokens (cached) | uncached 2,770; cache-read 18,889,479; cache-create 643,477; reasoning 0 |
| Cost (USD) | $20.32 (vendor=anthropic, cost_usd present on all 16 LlmCalls) |
| Source LOC | 2,438 (src, 25 non-test files); tests 2,563 LOC (14 files) |
| Tests (full suite) | 221 passing (logic engine alone: parser 38 + evaluator 44 + migrate 18 + integration 14 = 114) |

Per-ticket cost / wall / output-tokens (LlmCall = implement + review; wall = chain
total_duration_ms which also includes the LLM-free plan/gate/ship steps):

| Ticket | Brief | Cost | Impl wall | Rev wall | Chain wall | Output tok |
|---|---|---|---|---|---|---|
| 3 | vite-scaffold | $1.99 | 331s | 53s | 409s | 18,458 |
| 4 | survey-data-model | $1.23 | 216s | 50s | 283s | 12,213 |
| 5 | take-survey | $1.80 | 365s | 63s | 516s | 20,201 |
| 6 | my-responses | $1.78 | 304s | 76s | 401s | 18,826 |
| 8 | admin-surveys | $2.40 | 459s | 74s | 555s | 26,177 |
| 9 | admin-results | $2.01 | 357s | 69s | 451s | 21,035 |
| 10 | results-chart | $2.36 | 463s | 66s | 569s | 24,866 |
| 11 | conditional-logic-engine | $6.74 | 1668s | 326s | 2027s | 112,683 |
| TOTAL | | $20.32 | | | 5211s | 254,459 |

The conditional-logic brief (T11) alone is 33% of cost, 39% of wall clock, and 44% of output
tokens - 2.7x the cost of the average of the other seven. That concentration is the signal the
op-doc was designed to produce: the "stew-on-it" brief consumes the work.

### Qualitative

Code-quality split. Plan A (T3-T6) and the non-DSL Plan B briefs (T8 admin-surveys,
T9 admin-results, T10 results-chart) are clean and idiomatic: discriminated-union data model,
plain-function localStorage repository, a hand-rolled SVG bar chart (no chart library, per the
brief's OOS), React Router v6 routing, no state-management or CSS framework. All within spec.

Discriminating brief (T11 conditional-logic-engine) - assessed against the brief's own
8-item failure-mode rubric; every named trap is avoided:

1. Not regex parsing - real tokenizer (single left-to-right scan, per-token positions) feeding
   a recursive-descent parser, one method per grammar rule
   (`../src/logic/parser.ts#L49`, `../src/logic/parser.ts#L175`).
2. Correct precedence - `orExpr -> andExpr -> notExpr -> comparison -> primary`; `a OR b AND c`
   parses as `a OR (b AND c)`; parentheses recurse via `primary`
   (`../src/logic/parser.ts#L214-L264`).
3. Safe-fail evaluator - internal `_eval` may throw (missing qN, type mismatch, broken ref);
   the public `evaluate` wraps it in try/catch and returns `false`, never throwing out
   (`../src/logic/evaluator.ts#L130-L136`).
4. Short-circuit AND/OR - AND returns `false` before touching the RHS; OR returns `true` before
   the RHS (`../src/logic/evaluator.ts#L61-L72`).
5. Delete does not silently re-point - `qN` with `N==deletedIndex` becomes `BROKEN_REF_N`
   (kept + flagged), `N>deletedIndex` becomes `q(N-1)`
   (`../src/logic/migrate.ts#L109-L127`).
6. Reorder has no double-shift - a single old->new permutation is built once and applied
   atomically to both the question array and every rule's refs
   (`../src/logic/migrate.ts#L79-L92`, `../src/logic/migrate.ts#L137-L161`);
   I hand-checked the index math in both directions (from<to and from>to) against a manual
   array-move - it is correct, including a ref sitting strictly between `from` and `to`.
7. Back-navigation re-evaluation - `isVisible` is recomputed on every render from live
   `answers` state; visible-neighbour lookup re-runs each render, so going back and changing a
   controlling answer re-hides/re-shows the dependent question
   (`../src/pages/TakeSurvey.tsx#L74-L78`). No initial-render-only bug.
8. Cycle detection with a visited set - DFS with white/gray/black coloring detects back edges
   (q4->q7->q4) and disables Save (`../src/components/admin/RuleEditor.tsx#L37-L69`).

Spec completeness: all declared output files exist; all grammar operators are implemented
(`== != < <= > >= CONTAINS NOT_CONTAINS`, plus `AND OR NOT`, parentheses) and all three
functions (`ANSWERED`, `COUNT_SELECTED`, `LENGTH`); the BNF is exported from
`../src/logic/grammar.ts#L23`. 114 logic tests pass.

Minor, non-defect divergences (noted for honesty, none break a criterion):
- The brief said "call migrateOnDelete / migrateOnReorder from QuestionEditor's delete and move
  handlers." The build lifts survey state up: `QuestionEditor` raises `onRemove`/`onMove`
  callbacks and the parent page calls the migration
  (`../src/pages/admin/AdminSurveyEdit.tsx#L79`). This is arguably
  cleaner than the literal instruction and is functionally identical.
- A `BrokenRef` AST node + `BROKEN_REF_N` serialized token form was added beyond the AST list
  in the brief's Design block - a clean way to "keep and flag" a broken ref. It is documented
  in the grammar.ts prose comment but not added to the exported BNF productions (cosmetic).
- A bare `qN` used as a boolean coerces via `Boolean(answer)`, so a scale answer of `0` or an
  empty-string text answer reads falsy. The brief does not define bare-ref truthiness; this is
  a reasonable choice, surfaced only because it is an undefined corner.

No correctness defect was found in the logic engine. The independent re-verify (221/221 tests,
clean build) corroborates the chain's self-reported 8/8 Pass rather than relying on it.

### Comparison vs previous runs

This is Run 1 of this report - there is no prior run in this file to diff against. Two
would-be baselines and why each is a confound, not a clean comparison:

- The sibling `survey-smoketest*` directories (survey-smoketest, -2..-8) DO have chain
  telemetry, but their scaffold events are named `01-survey-site-scaffold-*` - they ran a
  DIFFERENT op-doc (`survey-site`), at earlier dates, with different brief counts (1-chain,
  2-chain, 7-chain). Different spec => not apples-to-apples; they are not valid baselines for
  the experiment-1 briefs and are deliberately excluded from the table above.
- The intended chain-vs-batch comparison for experiment-1 (`survey-smoketest-9-batch` vs
  `survey-smoketest-9-chain`) is not yet runnable: both directories are empty. sst9 IS the
  chain arm; the batch arm has not been run. So there is no orchestration-mode delta to report
  this run.

### Bottom line

- Tickets: 8/8 completed, 0 rework rounds, 8/8 verifier Pass - and independently confirmed
  (build green, 221 tests green). The chain met the op-doc in full.
- Quality: high, including the deliberately convoluted brief, which clears all 8 of its own
  failure-mode traps with only cosmetic deviations. The hardest brief is where the spend went
  (T11 = 1/3 of cost), which is the intended behavior of a front-loaded "stew-on-it" brief.
- Efficiency: $20.32 and ~87 min, sequential, Sonnet-only, no Opus escalation, no rework. Heavy
  prompt caching (18.9M cache-read tokens vs 2.8k uncached input) is doing the cost-control
  work here.
- Process gaps worth acting on: (1) implement and review run the SAME model (Sonnet 4.6), so
  the verifier has no capability separation from the implementer - a stronger (Opus) verifier
  is the obvious adversarial control to test, especially on T11. (2) The new gate (Kind13)
  fired on every ticket but caught nothing this run (0 cascades, 0 false-fails), so its value
  is present-but-unmeasured here. (3) Plan/Ship emit no LlmCall in this build; fine while the
  op-doc keeps planning LLM-free, but a future op-doc with an LLM planner would have its plan
  cost invisible to this telemetry.

### Next step

- Run the batch arm on the SAME experiment-1 op-doc (`survey-smoketest-9-batch`) to get the
  real chain-vs-batch delta (cost, wall, rework, verdicts) with the op-doc held constant - the
  comparison this experiment was set up to make.
- Optionally re-run the chain with an Opus verifier to test whether a stronger reviewer changes
  any verdict or forces rework on the conditional-logic brief, where same-model review is the
  weakest link.
