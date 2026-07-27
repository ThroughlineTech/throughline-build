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
   (`../../sst9/src/logic/parser.ts#L49`, `../../sst9/src/logic/parser.ts#L175`).
2. Correct precedence - `orExpr -> andExpr -> notExpr -> comparison -> primary`; `a OR b AND c`
   parses as `a OR (b AND c)`; parentheses recurse via `primary`
   (`../../sst9/src/logic/parser.ts#L214-L264`).
3. Safe-fail evaluator - internal `_eval` may throw (missing qN, type mismatch, broken ref);
   the public `evaluate` wraps it in try/catch and returns `false`, never throwing out
   (`../../sst9/src/logic/evaluator.ts#L130-L136`).
4. Short-circuit AND/OR - AND returns `false` before touching the RHS; OR returns `true` before
   the RHS (`../../sst9/src/logic/evaluator.ts#L61-L72`).
5. Delete does not silently re-point - `qN` with `N==deletedIndex` becomes `BROKEN_REF_N`
   (kept + flagged), `N>deletedIndex` becomes `q(N-1)`
   (`../../sst9/src/logic/migrate.ts#L109-L127`).
6. Reorder has no double-shift - a single old->new permutation is built once and applied
   atomically to both the question array and every rule's refs
   (`../../sst9/src/logic/migrate.ts#L79-L92`, `../../sst9/src/logic/migrate.ts#L137-L161`);
   I hand-checked the index math in both directions (from<to and from>to) against a manual
   array-move - it is correct, including a ref sitting strictly between `from` and `to`.
7. Back-navigation re-evaluation - `isVisible` is recomputed on every render from live
   `answers` state; visible-neighbour lookup re-runs each render, so going back and changing a
   controlling answer re-hides/re-shows the dependent question
   (`../../sst9/src/pages/TakeSurvey.tsx#L74-L78`). No initial-render-only bug.
8. Cycle detection with a visited set - DFS with white/gray/black coloring detects back edges
   (q4->q7->q4) and disables Save (`../../sst9/src/components/admin/RuleEditor.tsx#L37-L69`).

Spec completeness: all declared output files exist; all grammar operators are implemented
(`== != < <= > >= CONTAINS NOT_CONTAINS`, plus `AND OR NOT`, parentheses) and all three
functions (`ANSWERED`, `COUNT_SELECTED`, `LENGTH`); the BNF is exported from
`../../sst9/src/logic/grammar.ts#L23`. 114 logic tests pass.

Minor, non-defect divergences (noted for honesty, none break a criterion):
- The brief said "call migrateOnDelete / migrateOnReorder from QuestionEditor's delete and move
  handlers." The build lifts survey state up: `QuestionEditor` raises `onRemove`/`onMove`
  callbacks and the parent page calls the migration
  (`../../sst9/src/pages/admin/AdminSurveyEdit.tsx#L79`). This is arguably
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

## Run 2 - 2026-06-08: near-replicate chain of experiment-2 (one brief's read-map tightened; Sonnet impl + Sonnet verify)

### Setup

- Project: `sst10` (the chain arm of the `survey-experiment-2` op-doc). The sibling
  `survey-smoketest*` directories ran a different, older spec (`survey-site`) and remain invalid
  baselines (see Run 1); the only apples-to-apples prior run is Run 1 (`sst9`, experiment-1).
- Op-doc: `op-docs/01-survey-experiment2-md` - 8 implementation briefs across two plans, the same
  front-loaded structure as experiment-1. This op-doc is NEARLY IDENTICAL to experiment-1: an
  8-line diff, all of it in Brief 07 (results-chart). The Inputs line changed from
  "read these; do not rediscover them" to "read ONLY these; the rest of the tree is out of scope
  for this brief - do not glob it", and three explicit scope/harness-exclusion bullets were added
  (mirror the AdminResults test idiom; touch only AdminResults.tsx + the new ResultsChart; do not
  read setupTests/vite.config/package.json). Every other brief is byte-identical to experiment-1.
  So Run 2 is best read as a near-replicate of Run 1 with exactly one brief's read-map tightened.
- Run analyzed: `.build/events/1-chain-2026-06-08-194653.jsonl` (8 per-ticket chains concatenated;
  tickets 3,4,5,6,8,9,10,11 -> briefs 01-08; ticket "1" appears only on a `chain_landing_push_skipped`
  TicketWrite, `reason=no_remote`). Build CLI `0.1.0+232ca2d`.
- Worker: `claude-code`. Implement phase model `claude-sonnet-4-6`; Review phase verifier model
  `claude-sonnet-4-6` (same model implement and review, same as Run 1). No Opus escalation.
- Notable: Plan and Ship spawned no worker and emitted no `LlmCall` (planning is LLM-free, the brief
  carries the plan), so the cost/token figures are the COMPLETE LLM spend of the run.

### Quantitative

| Metric | sst10 (chain, experiment-2) |
|---|---|
| Worker / model | claude-code; impl=claude-sonnet-4-6, verify=claude-sonnet-4-6 |
| Tickets completed | 8 / 8 (all outcome=Completed, all InReview->Done) |
| Rework rounds | 0 (across all 8 tickets) |
| Verifier verdicts | 8 Pass / 0 Rework / 0 Fail (checks_failed_count=0 each) |
| Build gate (chain) | passed; gate (Kind13): 0 false_fails, 0 cascade_caught, 0 gate-rework, 63.3s total gate wall |
| Independent re-verify | build PASS (tsc -b + vite, 54 modules) + 212 tests PASS (14 files, 0 fail) |
| Wall clock (chain) | 91.1 min (sum chain duration 5467s; timestamp span 5469s - sequential) |
| Output tokens | 280,105 |
| Input tokens (cached) | uncached 3,933; cache-read 23,550,208; cache-create 659,752; reasoning 0 |
| Cost (USD) | $23.13 (vendor=anthropic, cost_usd present on all 16 LlmCalls) |
| Source LOC | 2,341 (src, 24 non-test files); tests 2,249 LOC (13 files) |
| Tests (full suite) | 212 passing (logic engine alone: parser 41 + evaluator 41 + migrate 19 + integration 10 = 111) |

Per-ticket cost / wall / output-tokens (LlmCall = implement + review; wall = chain
total_duration_ms which also includes the LLM-free plan/gate/ship steps):

| Ticket | Brief | Cost | Impl wall | Rev wall | Chain wall | Output tok |
|---|---|---|---|---|---|---|
| 3 | vite-scaffold | $2.36 | 533s | 72s | 628s | 32,723 |
| 4 | survey-data-model | $1.63 | 330s | 49s | 397s | 18,295 |
| 5 | take-survey | $3.07 | 689s | 63s | 771s | 41,866 |
| 6 | my-responses | $1.72 | 316s | 34s | 372s | 15,833 |
| 8 | admin-surveys | $3.08 | 549s | 90s | 664s | 32,378 |
| 9 | admin-results | $2.38 | 452s | 92s | 572s | 30,778 |
| 10 | results-chart | $1.59 | 368s | 53s | 453s | 20,533 |
| 11 | conditional-logic-engine | $7.30 | 1457s | 122s | 1612s | 87,699 |
| TOTAL | | $23.13 | | | 5467s | 280,105 |

The conditional-logic brief (T11) is again the heaviest single brief: 32% of cost, 30% of chain
wall, 31% of output tokens - the intended concentration. But T11 in Run 2 produced 22% FEWER output
tokens than Run 1 (87,699 vs 112,683) and finished faster (impl 1457s vs 1668s), while costing
slightly MORE ($7.30 vs $6.74). Cost did not track output tokens because the run is cache-read
dominated (23.5M cache-read tokens); see the comparison section.

### Qualitative

Code-quality split. Plan A (T3-T6) and the non-DSL Plan B briefs (T8 admin-surveys, T9 admin-results,
T10 results-chart) are clean and idiomatic, within spec: discriminated-union data model, plain-function
localStorage repository, a hand-rolled SVG bar chart (no chart library), React Router v6, no state-management
or CSS framework. Build green, 212/212 tests green on independent re-verify.

Discriminating brief (T11 conditional-logic-engine) - assessed against the brief's own 8-item
failure-mode rubric; every named trap is avoided:

1. Not regex parsing - a real single-pass tokenizer keeping per-token positions
   (`../../sst10/src/logic/parser.ts#L33`, qN handling `../../sst10/src/logic/parser.ts#L130-L140`)
   feeding a recursive-descent parser, one method per grammar rule
   (`../../sst10/src/logic/parser.ts#L230-L369`).
2. Correct precedence - `parseOrExpr` (lowest) -> `parseAndExpr` -> `parseNotExpr` -> `parseComparison`
   -> `parsePrimary`; `a OR b AND c` parses as `a OR (b AND c)`; parentheses recurse via `primary`
   (`../../sst10/src/logic/parser.ts#L230-L259`, `../../sst10/src/logic/parser.ts#L336-L341`).
3. Safe-fail evaluator - internal `evalNode` may throw (missing qN, type mismatch); public `evaluate`
   wraps it in try/catch and returns `false`, and also coerces a non-boolean result to false, never
   throwing out (`../../sst10/src/logic/evaluator.ts#L147-L154`).
4. Short-circuit AND/OR - AND returns `false` before touching the RHS; OR returns `true` before the
   RHS (`../../sst10/src/logic/evaluator.ts#L88`, `../../sst10/src/logic/evaluator.ts#L100`).
5. Delete does not silently re-point - `qN` with `N==deletedIndex` becomes `broken_q{N}` (kept +
   frozen against later migrations), `N>deletedIndex` becomes `q(N-1)`
   (`../../sst10/src/logic/migrate.ts#L54-L61`). The `broken_qN` token is an unknown
   identifier the parser rejects, so the rule safe-fails to false at runtime (question hidden).
6. Reorder has no double-shift - a single old->new permutation is built once
   (`../../sst10/src/logic/migrate.ts#L81-L95`) and applied to every `qN` through one text
   pass (`../../sst10/src/logic/migrate.ts#L63-L70`); I hand-checked the index math for both
   from<to and from>to against a manual array-move, including a ref strictly between `from` and `to` -
   it is correct. The rewriter also splits quoted string literals out first
   (`../../sst10/src/logic/migrate.ts#L29-L33`) so a `qN`-looking substring inside `'...'` is
   never rewritten - a nicer correctness touch than a naive AST round-trip.
7. Back-navigation re-evaluation - `getVisibleIndices` is recomputed on every render from live
   `answers` state (`../../sst10/src/pages/TakeSurvey.tsx#L17-L32`,
   `../../sst10/src/pages/TakeSurvey.tsx#L47`), with explicit snap-forward when the current
   question becomes hidden after an answer change (`../../sst10/src/pages/TakeSurvey.tsx#L54-L57`).
   Both directions (yes->no re-hides, no->yes re-shows) are integration-tested.
8. Cycle detection with a visited set - DFS over the `qN` reference graph from the edited question's
   refs; reaching the edited question again is a cycle (`../../sst10/src/logic/cycle.ts#L54-L101`,
   visited-set DFS `../../sst10/src/logic/cycle.ts#L83-L94`). Detects q4->q7->q4.

Spec completeness: every declared output file exists; all comparison operators
(`== != < <= > >= CONTAINS NOT_CONTAINS`), `AND OR NOT`, parentheses, all four literal kinds, and all
three functions (`ANSWERED`, `COUNT_SELECTED`, `LENGTH`) are implemented; the BNF is exported from
`../../sst10/src/logic/grammar.ts#L32`. `displayRule?: string` was added to every `Question`
variant (`../../sst10/src/data/types.ts#L6`). 111 logic tests pass (parser 41, evaluator 41,
migrate 19, integration 10); the integration suite directly tests both load-bearing criteria
(two-directional back-navigation and direct + transitive cycle rejection).

Structural divergences from experiment-1's solution (none break a criterion):
- Cycle detection is extracted into a separate pure module `src/logic/cycle.ts` and imported by
  `RuleEditor` (`../../sst10/src/components/admin/RuleEditor.tsx#L31`); Run 1 kept it inline
  in RuleEditor.tsx. The extracted form is more testable, though here it is exercised through the
  integration suite rather than a dedicated cycle unit file.
- AST node types live in a dedicated `src/logic/types.ts`; migration is text-level (rewrite the raw
  rule string) with broken refs serialized as `broken_qN`. Run 1 used a similar serialized
  `BROKEN_REF_N` form. Both keep-and-flag rather than re-point.
- Migration calls are lifted to the parent page: `QuestionEditor` raises `onRemove`/`onMove` and
  `AdminSurveyEdit` calls `migrateOnDelete`/`migrateOnReorder` (`../../sst10/src/pages/admin/AdminSurveyEdit.tsx#L62-L74`).
  This is the same defensible lift noted in Run 1, not a literal call from QuestionEditor's handlers.

One literal acceptance divergence worth flagging honestly: the brief's acceptance says "the admin
LIST shows a broken-rule warning for a rule referencing a deleted question." The warning is
implemented and rendered, but in the per-question editor surface
(`../../sst10/src/components/admin/QuestionEditor.tsx#L48-L59`,
"display rule references a deleted question"), not on the `AdminSurveyList` table, which shows only
title/question-count/response-count/actions (`../../sst10/src/pages/admin/AdminSurveyList.tsx`).
The broken rule IS surfaced where an admin edits it, and the broken-ref data path is unit-tested in
migrate.test.ts, but no UI test asserts the warning renders, and its placement differs from the
literal "admin list" wording. Not a correctness defect - a placement/coverage gap.

No correctness defect was found in the logic engine. The independent re-verify (212/212 tests, clean
build, 54 modules) corroborates the chain's self-reported 8/8 Pass rather than relying on it.

### Comparison vs previous runs

This is a near-replicate of Run 1 with the same worker (`claude-code`), same models (Sonnet implement +
Sonnet verify), same orchestration (chain, sequential), same 8-brief product scope - with the op-doc
differing only in Brief 07's
read-map (results-chart, ticket 10). That makes the dominant confound NOT a model/vendor change (there is
none) but single-sample LLM nondeterminism. Aggregate deltas:

| Metric | Run 1 (sst9, exp1) | Run 2 (sst10, exp2) | Delta |
|---|---|---|---|
| Cost (USD) | $20.32 | $23.13 | +$2.81 (+13.8%) |
| Output tokens | 254,459 | 280,105 | +25,646 (+10.1%) |
| Wall (chain sum) | 5,211s (86.9m) | 5,467s (91.1m) | +256s (+4.9%) |
| Cache-read tokens | 18,889,479 | 23,550,208 | +4,660,729 (+24.7%) |
| Cache-create tokens | 643,477 | 659,752 | +16,275 |
| Uncached input | 2,770 | 3,933 | +1,163 |
| Rework rounds | 0 | 0 | = |
| Verifier verdicts | 8 Pass | 8 Pass | = |
| Gate wall | 68.6s | 63.3s | -5.3s |
| Re-verify tests | 221 pass | 212 pass | -9 |
| Source LOC | 2,438 (25 files) | 2,341 (24 files) | -97 (-4.0%) |
| Logic tests | 114 | 111 | -3 |

Per-brief, the signal separates cleanly from the noise:

| Ticket | Brief | Cost R1 -> R2 | Output R1 -> R2 | Impl wall R1 -> R2 |
|---|---|---|---|---|
| 3 | vite-scaffold | $1.99 -> $2.36 | 18,458 -> 32,723 | 331 -> 533 |
| 4 | survey-data-model | $1.23 -> $1.63 | 12,213 -> 18,295 | 216 -> 330 |
| 5 | take-survey | $1.80 -> $3.07 | 20,201 -> 41,866 | 365 -> 689 |
| 6 | my-responses | $1.78 -> $1.72 | 18,826 -> 15,833 | 304 -> 316 |
| 8 | admin-surveys | $2.40 -> $3.08 | 26,177 -> 32,378 | 459 -> 549 |
| 9 | admin-results | $2.01 -> $2.38 | 21,035 -> 30,778 | 357 -> 452 |
| 10 | results-chart (EDITED brief) | $2.36 -> $1.59 | 24,866 -> 20,533 | 463 -> 368 |
| 11 | conditional-logic-engine | $6.74 -> $7.30 | 112,683 -> 87,699 | 1668 -> 1457 |

Reading this:

- The one brief whose op-doc actually changed (T10, read-map tightened to "read ONLY these, do not
  glob it") is the one brief that got materially CHEAPER: -$0.77 (-33%), -4,333 output tokens (-17%),
  -95s implement wall (-21%). That is exactly the intended effect of a tighter read-map - less
  globbing and tree-reading means fewer tokens. It is the only brief besides T6 to drop on all three
  axes, and the only one with a deliberate cause.
- Every byte-identical brief (T3, T4, T5, T8, T9) rose in cost and output tokens. Their inputs did
  not change, so this is pure run-to-run nondeterminism, and it is large: T5 take-survey more than
  doubled its output (20,201 -> 41,866) on an unchanged brief. The aggregate "+13.8% cost / +10.1%
  output" headline is therefore NOT caused by the op-doc edit - the edited brief got cheaper; the
  increase lives entirely in unchanged briefs and is dominated by sampling variance at n=1.
- Cost did not track output tokens. T11 produced 24,984 FEWER output tokens yet cost $0.56 MORE, and
  the run as a whole added +24.7% cache-read tokens. With heavy prompt caching, cache-read volume,
  not output tokens, is the dominant cost driver - so "more output" and "more expensive" are not
  interchangeable here.
- Quality held flat at high: both runs clear all 8 of the conditional-logic brief's failure-mode
  traps; experiment-2's solution is, if anything, slightly better factored (cycle detection extracted
  to a pure module). Test depth dropped marginally (212 vs 221 overall; 111 vs 114 logic), but the
  load-bearing criteria stay directly covered.

Confound summary: the naive cross-run cost comparison is misleading because n=1 brief-to-brief variance
(tens of percent) swamps the single intended op-doc change. The clean, attributable result is the T10
read-map effect; the aggregate is noise.

### Bottom line

- Tickets: 8/8 completed, 0 rework rounds, 8/8 verifier Pass, independently confirmed (build green, 54
  modules, 212 tests green). The chain met experiment-2 in full.
- Quality: high, including the deliberately convoluted brief, which clears all 8 of its own failure-mode
  traps with only structural/cosmetic deviations (cycle extracted to its own module; broken-rule warning
  placed in the per-question editor rather than the survey-list table). The hardest brief is again where
  the spend concentrates (T11 = 32% of cost).
- Efficiency: $23.13 and ~91 min, sequential, Sonnet-only, no Opus escalation, no rework. The single
  intended change (tightening Brief 07's read-map) did reduce that brief's cost/tokens/time; the
  run-level increase over Run 1 is nondeterminism in the unchanged briefs, not a regression caused by
  the op-doc.
- Process gaps worth acting on: (1) n=1 makes per-brief cost/token deltas uninterpretable - to measure
  the read-map tightening (or any prompt change) honestly, repeat each arm 3-5x and compare
  distributions, not single runs. (2) implement and review are still the SAME model (Sonnet 4.6), so the
  verifier has no capability separation - an Opus verifier remains the obvious untested adversarial
  control, especially on T11. (3) the gate (Kind13) again fired on every ticket and caught nothing
  (0 cascades, 0 false-fails) across both runs - still present-but-unmeasured.

### Next step

- Replicate: run experiment-2 (and/or experiment-1) 3-5 more times with everything held constant to get
  a variance band. Only then is the "+14% cost" or the "-33% on T10" delta separable from noise; right
  now T10's drop is suggestive but single-sample.
- Hold the op-doc constant and vary ONE knob per experiment: first the Opus-verifier arm (does a stronger
  reviewer force any rework on T11?), then the chain-vs-batch arm if a batch run of the same op-doc
  becomes available - the orchestration-mode delta Run 1 also flagged as still unmeasured.

## Run 3 - 2026-06-09: experiment-3 = experiment-2 + explicit per-brief Preload blocks (Sonnet impl + Sonnet verify)

### Setup

- Project: `sst11` (the chain arm of the `survey-experiment-3` op-doc). The sibling
  `survey-smoketest*` directories ran a different, older spec (`survey-site`) and remain invalid
  baselines (see Run 1); the apples-to-apples priors are Run 1 (`sst9`, experiment-1) and Run 2
  (`sst10`, experiment-2).
- Op-doc: `op-docs/01-survey-experiment3-md` - 8 implementation briefs across two plans, same
  front-loaded structure as experiment-1/2. This op-doc is experiment-2 plus exactly one change:
  a `Preload:` block was appended to 7 of the 8 briefs (every implement brief except B01
  vite-scaffold), each listing the specific source files to inline into that brief. The
  `diff` against experiment-2 is ONLY those 7 Preload blocks; every other line is byte-identical.
  Against experiment-1 the diff is the 7 Preload blocks PLUS experiment-2's B07 read-map tightening.
  So Run 3 is best read as a near-replicate of Run 2 with one lever toggled: per-brief input
  preloading turned on (`preload_context = true` in `.build/config.toml`, with the brief naming
  the files).
- Run analyzed: `.build/events/1-chain-2026-06-09-003139.jsonl` (8 per-ticket chains concatenated;
  tickets 3,4,5,6,8,9,10,11 -> briefs 01-08; ticket "1" appears only on a
  `chain_landing_push_skipped` TicketWrite, `reason=no_remote`, same as Run 2). Build CLI
  `0.1.0+20fe008`.
- Worker: `claude-code`. Implement phase model `claude-sonnet-4-6`; Review phase verifier model
  `claude-sonnet-4-6` (same model implement and review, same as Run 1 and Run 2). No Opus escalation.
- Notable: Plan and Ship spawned no worker and emitted no `LlmCall` (planning is LLM-free, the brief
  carries the plan), so the cost/token figures are the COMPLETE LLM spend of the run.
- Telemetry schema delta (new this build): the implement phase now emits a second `Kind 13` shape on
  `Phase 1`, `{kind: "preload_summary", files_requested, files_loaded, files_truncated, bytes_loaded,
  not_found, truncated, omitted}` - the preload lever's own accounting, distinct from the
  `Phase 10` gate `Kind 13` (`gate_wall_ms` etc.) seen in Runs 1-2 (which is unchanged here). Across
  the 8 implement phases the lever requested 45 files, loaded 41, truncated 0, and preloaded 60,753
  bytes; the 4 not-loaded were `src/data/__tests__/repository.test.ts` plus the tsconfig/vite
  convention files requested before B01 created them (T3 scaffold loaded 0 of 3; everything from T5 on
  loaded 100% of requested).

### Quantitative

| Metric | sst11 (chain, experiment-3) |
|---|---|
| Worker / model | claude-code; impl=claude-sonnet-4-6, verify=claude-sonnet-4-6 |
| Tickets completed | 8 / 8 (all outcome=Completed, all InReview->Done) |
| Rework rounds | 0 (across all 8 tickets) |
| Verifier verdicts | 8 Pass / 0 Rework / 0 Fail (checks_failed_count=0 each) |
| Build gate (chain) | passed; gate (Kind13/Phase10): 0 false_fails, 0 cascade_caught, 0 gate-rework, 62.8s total gate wall |
| Preload lever (Kind13/Phase1) | 45 files requested, 41 loaded, 0 truncated, 60,753 bytes preloaded across 8 briefs |
| Independent re-verify | build PASS (tsc -b + vite, 51 modules) + 175 tests PASS (14 files, 0 fail) |
| Wall clock (chain) | 75.5 min (sum chain duration 4,528s; timestamp span 4,530s - sequential) |
| Output tokens | 229,115 |
| Input tokens (cached) | uncached 491; cache-read 19,224,401; cache-create 598,622; reasoning 0 |
| Cost (USD) | $19.27 (vendor=anthropic, cost_usd present on all 16 LlmCalls) |
| Source LOC | 2,151 (src, 24 non-test files); tests 2,126 LOC (14 files) |
| Tests (full suite) | 175 passing (logic engine alone: parser 33 + evaluator 29 + migrate 18 + integration 12 = 92) |

Per-ticket cost / wall / output-tokens (LlmCall = implement + review; wall = chain
total_duration_ms which also includes the LLM-free plan/gate/ship steps):

| Ticket | Brief | Cost | Impl wall | Rev wall | Chain wall | Output tok |
|---|---|---|---|---|---|---|
| 3 | vite-scaffold | $2.55 | 431s | 54s | 513s | 21,026 |
| 4 | survey-data-model | $1.42 | 294s | 35s | 346s | 14,512 |
| 5 | take-survey | $1.95 | 408s | 92s | 518s | 25,582 |
| 6 | my-responses | $1.80 | 309s | 49s | 378s | 18,652 |
| 8 | admin-surveys | $2.80 | 496s | 80s | 603s | 33,160 |
| 9 | admin-results | $2.34 | 474s | 87s | 595s | 32,043 |
| 10 | results-chart | $2.07 | 467s | 63s | 556s | 25,539 |
| 11 | conditional-logic-engine | $4.34 | 756s | 236s | 1,019s | 58,601 |
| TOTAL | | $19.27 | | | 4,528s | 229,115 |

The conditional-logic brief (T11) is again the heaviest single brief, but its concentration is the
LOWEST of the three runs: 22.5% of cost, 22.5% of chain wall, 25.6% of output (vs ~33%/39%/44% in
Run 1 and ~32%/30%/31% in Run 2). T11's output tokens have fallen monotonically across all three runs
(112,683 -> 87,699 -> 58,601) and its cost dropped sharply this run ($6.74 -> $7.30 -> $4.34) - see
the comparison section for why that decline is only partly attributable to the preload lever.

### Qualitative

Code-quality split. Plan A (T3-T6) and the non-DSL Plan B briefs (T8 admin-surveys, T9 admin-results,
T10 results-chart) are clean and idiomatic, within spec: discriminated-union data model, plain-function
localStorage repository, a hand-rolled SVG bar chart (no chart library), React Router v6, no
state-management or CSS framework. Build green (51 modules), 175/175 tests green on independent re-verify.

Discriminating brief (T11 conditional-logic-engine) - assessed against the brief's own 8-item
failure-mode rubric; every named trap is avoided:

1. Not regex parsing - a real single-pass tokenizer keeping per-token positions
   (`../src/logic/parser.ts#L35`; string literals tokenized whole at
   `../src/logic/parser.ts#L93-L106` so operator words inside quotes are never
   confused) feeding a recursive-descent `Parser` class, one method per grammar rule
   (`../src/logic/parser.ts#L204-L306`).
2. Correct precedence - `parseOrExpr` (lowest) -> `parseAndExpr` -> `parseNotExpr` -> `parseComparison`
   -> `parsePrimary`; `a OR b AND c` parses as `a OR (b AND c)`; parentheses recurse via `primary`
   (`../src/logic/parser.ts#L204-L223`, `../src/logic/parser.ts#L284-L289`).
   The exported BNF and a highest-to-lowest precedence table are in
   `../src/logic/grammar.ts#L24`.
3. Safe-fail evaluator - internal `evalNode` may throw (missing qN, type mismatch, broken ref); public
   `evaluate` wraps it in try/catch and returns `false`, never throwing out
   (`../src/logic/evaluator.ts#L5-L11`).
4. Short-circuit AND/OR - AND returns `false` before touching the RHS; OR returns `true` before the RHS
   (`../src/logic/evaluator.ts#L16-L24`).
5. Delete does not silently re-point - `qN` with `N==deletedIndex` becomes `BROKEN_q{N}` (kept +
   flagged), `N>deletedIndex` becomes `q(N-1)`, `N<deletedIndex` unchanged
   (`../src/logic/migrate.ts#L33-L44`). The rewriter uses a `\bq(\d+)\b` word-boundary
   match, so the `q{N}` inside an already-produced `BROKEN_qN` is never re-matched on a later migration
   (the `_` before `q` blocks the boundary) - broken refs are frozen (`../src/logic/migrate.ts#L8-L12`).
   At runtime `BROKEN_qN` is an unknown identifier the parser rejects, so the rule safe-fails to false
   and the question is hidden; `hasBrokenRef` drives the admin warning.
6. Reorder has no double-shift - `buildPermutation` constructs the full old->new index map ONCE
   (`../src/logic/migrate.ts#L53-L69`) and `migrateOnReorder` rewrites every `qN`
   through it in a single pass (`../src/logic/migrate.ts#L78-L94`). I hand-checked the
   index math for both from<to and from>to against a manual array-move, including a reference sitting
   strictly between `from` and `to` - it shifts exactly once, correct in both directions.
7. Back-navigation re-evaluation - `visibleIndices` is recomputed every render from live `answers`
   state (`../src/pages/TakeSurvey.tsx#L27-L41`) with an explicit snap-to-nearest
   when the current question becomes hidden after an answer change
   (`../src/pages/TakeSurvey.tsx#L45-L52`). The integration suite tests both
   directions (yes->no re-hides, no->yes re-shows) and a chained dependency
   (`../src/logic/__tests__/integration.test.ts#L99-L150`).
8. Cycle detection with a visited set - extracted to a pure `src/logic/cycle.ts`; BFS over the `qN`
   reference graph from the edited question's refs, reaching the edited question again is a cycle, a
   `visited` Set prevents the infinite loop (`../src/logic/cycle.ts#L20-L49`). Detects
   the direct (q4->q4) and two-hop (q4->q7->q4) cases; `RuleEditor` gates Save on it
   (`../src/components/admin/RuleEditor.tsx#L28-L33`).

Spec completeness: every declared output file exists; all comparison operators
(`== != < <= > >= CONTAINS NOT_CONTAINS`), `AND OR NOT`, parentheses, all four literal kinds, and all
three functions (`ANSWERED`, `COUNT_SELECTED`, `LENGTH`) are implemented; `ANSWERED(qN)` is true for an
existing empty-string answer as the brief requires (`../src/logic/evaluator.ts#L58-L59`).
`displayRule?: string` was added to every `Question` variant (`../src/data/types.ts#L10-L14`).
The 92 logic tests pass (parser 33, evaluator 29, migrate 18, integration 12); the integration suite
directly tests both load-bearing criteria (two-directional back-navigation and direct + transitive cycle
rejection) plus the broken-ref safe-fail.

Structural divergences from prior solutions (none break a criterion - the same two seen in Run 2 recur):
- Cycle detection is a separate pure module `src/logic/cycle.ts` imported by `RuleEditor` (Run 2 also
  extracted it; Run 1 kept it inline). Here it is exercised through the integration suite rather than a
  dedicated cycle unit file.
- Migration calls are lifted to the parent page: `QuestionEditor` raises `onRemove`/`onMove` and
  `AdminSurveyEdit` calls `migrateOnDelete`/`migrateOnReorder` BEFORE mutating the question array
  (`../src/pages/admin/AdminSurveyEdit.tsx#L71-L91`). This is the same
  defensible lift noted in Runs 1-2, not a literal call from QuestionEditor's handlers.

One literal acceptance divergence, identical to Run 2 and worth flagging again: the brief's acceptance
says "the admin LIST shows a broken-rule warning for a rule referencing a deleted question." The warning
is implemented and rendered, but in the per-question editor surface
(`../src/components/admin/QuestionEditor.tsx#L55-L67`, "Warning: display rule
references a deleted question"), not on the `AdminSurveyList` table. The broken rule IS surfaced where an
admin edits it and the broken-ref data path is unit-tested, but no UI test asserts the warning renders and
its placement differs from the literal "admin list" wording. Not a correctness defect - a placement/coverage gap.

Minor, non-defect nits (surfaced for honesty): `Answer` is typed `string | string[] | number` with no
boolean (`../src/data/types.ts#L16`), so the evaluator's `typeof answer === 'boolean'`
branches (`../src/logic/evaluator.ts#L41`, `../src/logic/evaluator.ts#L87`)
are defensive dead code. Spreading a discriminated-union member to add `displayRule` forces `as Question`
casts in `QuestionEditor` (`../src/components/admin/QuestionEditor.tsx#L51`); cosmetic,
since `displayRule?` is on every variant. As in Run 1, a bare `qN` used as a boolean coerces via the
answer's emptiness, an undefined corner the brief does not specify.

No correctness defect was found in the logic engine. The independent re-verify (175/175 tests, clean build,
51 modules) corroborates the chain's self-reported 8/8 Pass rather than relying on it.

### Comparison vs previous runs

Run 3 holds everything from Run 2 constant (worker `claude-code`, Sonnet implement + Sonnet verify, chain
orchestration sequential, the same 8 briefs) and toggles ONE lever: per-brief input preloading
(`preload_context` on, with each brief naming its files in a `Preload:` block). So the intended variable is
"does inlining the named inputs into the brief - instead of having the worker read them live - reduce
spend?" Aggregate deltas:

| Metric | Run 1 (sst9, exp1) | Run 2 (sst10, exp2) | Run 3 (sst11, exp3) | R2 -> R3 delta |
|---|---|---|---|---|
| Cost (USD) | $20.32 | $23.13 | $19.27 | -$3.86 (-16.7%) |
| Output tokens | 254,459 | 280,105 | 229,115 | -50,990 (-18.2%) |
| Wall (chain sum) | 5,211s (86.9m) | 5,467s (91.1m) | 4,528s (75.5m) | -939s (-17.2%) |
| Cache-read tokens | 18,889,479 | 23,550,208 | 19,224,401 | -4,325,807 (-18.4%) |
| Cache-create tokens | 643,477 | 659,752 | 598,622 | -61,130 (-9.3%) |
| Uncached input | 2,770 | 3,933 | 491 | -3,442 (-87.5%) |
| Rework rounds | 0 | 0 | 0 | = |
| Verifier verdicts | 8 Pass | 8 Pass | 8 Pass | = |
| Gate wall | 68.6s | 63.3s | 62.8s | -0.5s |
| Re-verify tests | 221 pass | 212 pass | 175 pass | -37 |
| Source LOC | 2,438 (25 files) | 2,341 (24 files) | 2,151 (24 files) | -190 |
| Logic tests | 114 | 111 | 92 | -19 |

Per-brief, cost / output / implement-wall across all three runs:

| Ticket | Brief | Cost R1->R2->R3 | Output R1->R2->R3 | Impl wall R1->R2->R3 |
|---|---|---|---|---|
| 3 | vite-scaffold (no Preload block) | $1.99 -> $2.36 -> $2.55 | 18,458 -> 32,723 -> 21,026 | 331 -> 533 -> 431 |
| 4 | survey-data-model | $1.23 -> $1.63 -> $1.42 | 12,213 -> 18,295 -> 14,512 | 216 -> 330 -> 294 |
| 5 | take-survey | $1.80 -> $3.07 -> $1.95 | 20,201 -> 41,866 -> 25,582 | 365 -> 689 -> 408 |
| 6 | my-responses | $1.78 -> $1.72 -> $1.80 | 18,826 -> 15,833 -> 18,652 | 304 -> 316 -> 309 |
| 8 | admin-surveys | $2.40 -> $3.08 -> $2.80 | 26,177 -> 32,378 -> 33,160 | 459 -> 549 -> 496 |
| 9 | admin-results | $2.01 -> $2.38 -> $2.34 | 21,035 -> 30,778 -> 32,043 | 357 -> 452 -> 474 |
| 10 | results-chart | $2.36 -> $1.59 -> $2.07 | 24,866 -> 20,533 -> 25,539 | 463 -> 368 -> 467 |
| 11 | conditional-logic-engine | $6.74 -> $7.30 -> $4.34 | 112,683 -> 87,699 -> 58,601 | 1668 -> 1457 -> 756 |

Reading this honestly - the headline is real but the attribution is not clean:

- Run 3 is the cheapest, lowest-output, and fastest of the three, and it is the only run with the
  preload lever on. The aggregate direction is exactly what preloading predicts: hand the worker its
  named inputs and it reads fewer files live, emitting fewer tool-call/round-trip output tokens.
- The single mechanically-attributable preload effect is the uncached-input collapse: 3,933 -> 491
  (-87.5%). Preloaded file contents ride in the cached prompt prefix (cache-read/cache-create), so the
  live uncached input the worker would otherwise pull shrinks to almost nothing. This one is causal, not
  variance - but uncached input is a rounding error in total cost, so it does not by itself explain the
  dollar drop.
- The dollar/output/wall drop is NOT uniform across briefs, which is what a clean lever effect would
  look like. T6, T9, T10 rose or held flat in output R2->R3 despite gaining Preload blocks; the entire
  aggregate decrease is carried by T5 (-39%) and T11 (-33%), the two largest briefs. And T11's output
  has fallen monotonically since BEFORE preload existed (112,683 in R1 -> 87,699 in R2 with no preload
  change on that brief -> 58,601 in R3) - so a large part of T11's decline is run-to-run nondeterminism,
  not the lever. The brief that got NO meaningful preload (T3 scaffold: no Preload block, convention
  files not yet created, 0 bytes loaded) did not get cheaper - it rose R1->R2->R3 - a useful within-run
  control showing the cost movement is not a global discount.
- Net: at n=1 the preload lever's cost effect is confounded with the same brief-to-brief sampling
  variance (tens of percent) that swamped Run 2's read-map edit. The honest claim is "Run 3 was cheaper
  and the preload telemetry confirms the lever fired and collapsed uncached input," NOT "preloading saved
  16.7%." The 16.7% is one sample.
- Quality held flat at high across all three runs: every run clears all 8 of the conditional-logic
  brief's failure-mode traps. Test depth, however, has eroded run over run (221 -> 212 -> 175 overall;
  114 -> 111 -> 92 logic), tracking the falling output-token budget - the worker is writing fewer tests
  as it writes less overall. The load-bearing criteria stay directly covered, but the trend is worth
  watching: if preloading (or nondeterminism) keeps shrinking output, test thoroughness is the first
  thing to thin.

Confound summary: the same n=1 caveat as Run 2 applies with equal force. The preload lever is confirmed
active (45 files / 60,753 bytes preloaded; uncached input down 87.5%), but its effect on cost/time is not
separable from sampling variance in a single run, and the most eye-catching number (T11 down 33%) sits on
top of a pre-existing downward trend that predates the lever.

### Bottom line

- Tickets: 8/8 completed, 0 rework rounds, 8/8 verifier Pass, independently confirmed (build green, 51
  modules, 175 tests green). The chain met experiment-3 in full.
- Quality: high, including the deliberately convoluted brief, which clears all 8 of its own failure-mode
  traps with only the same structural/cosmetic deviations seen in Run 2 (cycle extracted to its own
  module; broken-rule warning in the per-question editor rather than the survey-list table). The hardest
  brief is again where the spend concentrates, though less so than before (T11 = 22.5% of cost, its lowest
  share across the three runs).
- Efficiency: $19.27 and ~75.5 min - the cheapest and fastest run so far. The preload lever is confirmed
  firing (uncached input collapsed 87.5%), and the aggregate cost/output/wall all moved the way preloading
  predicts, but at n=1 the magnitude is inseparable from the brief-to-brief variance that dominated Run 2.
- Process gaps worth acting on, carried forward and unchanged: (1) n=1 still makes per-brief deltas
  uninterpretable - to credit preloading with any specific saving, repeat the exp2 and exp3 arms 3-5x and
  compare distributions; the within-run control (T3 scaffold, which got no real preload and did not get
  cheaper) is suggestive but not a substitute. (2) implement and review are STILL the same model (Sonnet
  4.6) - the Opus-verifier arm remains the untested adversarial control, especially on T11. (3) the gate
  (Kind13/Phase10) fired on every ticket and again caught nothing across all three runs (0 cascades, 0
  false-fails) - present-but-unmeasured for a third time. (4) new this run: test depth is eroding run over
  run (221 -> 212 -> 175); worth a dedicated check that shrinking output is not quietly trading away
  coverage.

### Next step

- Replicate before crediting preload: run experiment-2 (no preload) and experiment-3 (preload) 3-5x each,
  everything else held constant, and compare the cost/output distributions. Only then is the "-16.7%"
  separable from the n=1 variance band that Run 2 already demonstrated is tens of percent per brief.
- Isolate the preload lever cleanly: the config exposes `preload_context = true/false` as an ablation
  switch. Run the SAME experiment-3 op-doc with `preload_context = false` so the only difference is whether
  the named files are inlined vs read live - that removes the brief-text confound entirely and measures the
  lever directly.
- Still pending from Runs 1-2: the Opus-verifier arm (does a stronger reviewer force any rework on T11?)
  and the chain-vs-batch arm once a batch run of a matching op-doc exists.
