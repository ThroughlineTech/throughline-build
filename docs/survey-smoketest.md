# Survey smoketest: build-chain comparison

Running log comparing build-chain runs against the shared `survey-app-build` op-doc
across the `survey-smoketest*` projects. Append new runs as they happen.

## Run 1 - 2026-06-05: survey-smoketest2 vs survey-smoketest3

### Setup

Both projects were handed the **same identical 8-brief op-doc** (`op-docs/01-survey-site.md`,
byte-for-byte identical), spanning two plans:

- **Plan A** (briefs 01-04): scaffold, data model, take-survey, my-responses
- **Plan B** (briefs 05-08): admin CRUD, aggregate results, SVG chart, and the
  **conditional-logic engine** (the deliberately convoluted DSL/parser/evaluator brief)

**Both chains ran only Plan A** (tickets `-3` through `-6`). Neither attempted a single
Plan B brief. The event logs show each chain was invoked with exactly 4 tickets, completed
all 4, and stopped - no failure, no Plan B dispatch.

So the "how did they meet their tickets" answer is: **4/4 tickets each, 100%** - but that is
half the op-doc, and crucially **the hard brief (08, the one designed to separate good from
bad implementations) was never built by either.** Any quality comparison here is on
straightforward CRUD/UI work, not on the part meant to discriminate.

### Quantitative

| Metric | smoketest2 | smoketest3 |
|---|---|---|
| `default_model` config | Sonnet 4.6 | **Opus 4.7** |
| Tickets completed | 4 / 4 | 4 / 4 |
| Rework rounds | 0 | 0 |
| Verifier verdicts | 4x Pass | 4x Pass |
| Build + test gate (chain) | green x4 | green x4 |
| Independent re-verify (this analysis) | build ok, **21 tests pass** | build ok, **27 tests pass** |
| Wall clock (chain) | ~33m 16s | ~32m 19s |
| Sum of output tokens | ~89.1k | ~95.7k |
| **Total cost** | **~$9.30** | **~$9.64** |
| Source LOC | 979 | 1,015 |
| Bundle size (gzip) | 55.96 kB | 55.53 kB |

Cost breakdown by ticket (plan+impl+verify):

- ST2: `-3` $4.81 / `-4` $1.42 / `-5` $1.51 / `-6` $1.56
- ST3: `-3` $4.72 / `-4` $1.28 / `-5` $1.97 / `-6` $1.66

### Big finding: the config difference was nearly a no-op

Despite ST2 being set to Sonnet and ST3 to Opus by `default_model`, **the two runs used
identical per-phase model routing**:

- Plan phase: Sonnet on every brief, both projects
- Implement: **Opus** on brief 01 (sized L), **Sonnet** on briefs 02-04 (sized M) - identical in both
- Verify: Opus on brief 01, Sonnet on 02-04 - identical in both

Both configs share the same `[workers.claude-code.sizes]` map (small=haiku, medium=sonnet,
large=opus), and the inferred size labels came out the same. `default_model` only acts as a
fallback that was never hit. **So this was not a clean Sonnet-vs-Opus A/B - it was closer to a
repeatability test of the same pipeline under the same routing.** The qualitative differences
below are run-to-run worker variance, not a model-tier effect.

If you want to benchmark the two tiers, change the `[workers.claude-code.sizes]` map (or force
a size), not `default_model`.

### Qualitative output quality

Both are clean, idiomatic React+TS, strict mode, no banned deps (no CSS framework, no state
lib, no chart/parser lib). Both correctly handle the one Plan A design wrinkle (the
`(question removed)` orphan-answer case). Divergences - all minor, all favoring ST3:

- **Repository upsert (real difference).** ST2 upserts by `filter(id !== x).push()`, which
  **reorders the edited item to the end of the array**. ST3 upserts in-place via `findIndex`,
  **preserving order**. ST2's approach is a latent bug for anything order-sensitive - and
  Plan B's conditional-logic engine references questions positionally (`q1`, `q2`...), so
  ST2's reordering would have bitten brief 08 had it run.
- **Test depth.** Identical counts on every suite *except* the data layer: ST2 wrote 7
  repository tests, ST3 wrote 13. The extra coverage sits exactly where edge cases live
  (corruption path, empty-store, filtering).
- **Type modeling.** ST3 gave each `Answer` variant a named interface (more
  reusable/extensible); ST2 used an inline union. Cosmetic.
- **Exhaustiveness.** ST3's `formatAnswer` is an exhaustive `switch` (compile error if a new
  kind is added); ST2 uses a `String()` fallback.
- **One small edge regression in ST3:** if the *entire survey* is deleted, ST3's
  ResponseDetail bails to "Response not found," whereas ST2 still renders orphaned answers
  under "Unknown survey." The spec only required the deleted-*question* case (both pass that),
  so neither is wrong, but ST2 degrades slightly more gracefully here.

### Efficiency

Essentially a tie. ST3 cost ~3.6% more (+$0.34) and produced ~7% more output tokens for ~1
minute less wall-clock - the extra spend bought the deeper repository test suite. Both
pipelines ran with **zero rework rounds, zero failed gates, no wasted retries.** Cost is
dominated in both by the single Opus implementation of brief 01 (~$3.4-3.6, ~36-49% of each
project's total); the scaffold being sized L is the main cost lever.

### Bottom line

- **Meeting tickets:** Both 100% on the tickets given (4/4), but both delivered only **half the
  op-doc** - Plan B, including the marquee convoluted brief, was never dispatched.
- **Quality:** Both ship-quality for Plan A. **ST3 is modestly better** (order-preserving
  upsert, ~85% more data-layer tests, more exhaustive typing) - but these are worker-variance
  wins, **not** attributable to Opus, because routing was identical.
- **Efficiency:** Dead heat (~$9.30 vs ~$9.64, ~33 vs ~32 min), both with flawless
  gate-passage and no rework.
- **Process caveats:** (1) the `default_model` Sonnet-vs-Opus difference had no observable
  effect; (2) to exercise the part of the op-doc that actually stresses an agent, chain Plan B -
  brief 08 is where a real Opus-vs-Sonnet gap would show up.

### Next step

Chain Plan B (briefs 05-08) on one or both projects to get a comparison on the brief actually
designed to discriminate.

## Run 2 - 2026-06-05: Plan B (briefs 05-08), Claude vs Codex

### Setup

Both projects ran **Plan B** (admin CRUD, aggregate results, SVG chart, conditional-logic
engine) as tickets `-8` through `-11`. The big change from Run 1: the worker assignment.

- **survey-smoketest2: Claude Code** (`default_agent = "claude-code"`). Same size-routed
  models as before - Sonnet for plan, Opus for L-sized briefs (`-8`, `-11`), Sonnet for M/S
  (`-9`, `-10`); verifier mirrors impl size.
- **survey-smoketest3: Codex / gpt-5.5** (`default_agent = "codex"`). All phases (plan, impl,
  verify) ran on `gpt-5.5` via OpenAI.

So Run 2 is a genuine **cross-agent comparison (Claude vs Codex)** on the brief that was
*designed* to discriminate - unlike Run 1, where the routing turned out identical. Note ST3 is
now a **mixed-vendor project**: Plan A on Claude, Plan B on Codex.

Both chains completed **4/4 tickets, 0 rework rounds, all verifier verdicts Pass**, and both
pass build + full test suite on independent re-verification.

### Quantitative (Plan B only)

| Metric | smoketest2 (Claude) | smoketest3 (Codex/gpt-5.5) |
|---|---|---|
| Worker / model | claude-code (Sonnet+Opus) | codex (gpt-5.5) |
| Tickets completed | 4 / 4 | 4 / 4 |
| Rework rounds | 0 | 0 |
| Verifier verdicts | 4x Pass | 4x Pass |
| Wall clock (chain) | ~50m 35s | **~40m 22s** |
| Output tokens (Plan B) | ~153k | ~83.6k (+ ~13k reasoning) |
| Input tokens (Plan B) | ~0.9M (uncached portion small) | ~6.2M (mostly cached) |
| **Cost (Plan B)** | **~$20.92** | **not logged** (OpenAI vendor emits no `cost_usd`) |
| Tests (full suite, incl. Plan A) | **111 passed** | 78 passed |
| Source LOC (total) | 3,712 | 3,865 |
| Bundle size (gzip) | 61.86 kB | 61.90 kB |

ST2 (Claude) cost by ticket (plan+impl+verify):

- `-8` admin-surveys (L): $0.73 + $4.12 + $0.60 = **$5.45**
- `-9` admin-results (M): $0.82 + $1.84 + $0.24 = **$2.90**
- `-10` results-chart (S): $0.61 + $0.98 + $0.19 = **$1.77**
- `-11` conditional-logic (L): $0.98 + **$8.39** + $1.42 = **$10.79**

The conditional-logic engine (`-11`) alone was **52% of Plan B's Claude cost** - the single
Opus implementation pass was $8.39 (35k output tokens, 2.6M cache-read). That is the
"deliberately convoluted" brief doing exactly what it was designed to do: consume real effort.

**Cost caveat:** the event log does not emit `cost_usd` for the OpenAI vendor, so ST3's Plan B
dollar cost cannot be stated. On token volume alone ST3 produced ~45% fewer output tokens and
ran ~10 min faster, so it was very likely cheaper as well as faster - but that is inference, not
a logged figure. **Action item: make the build emit cost for non-Anthropic vendors** or this
comparison stays one-sided.

### Qualitative: the conditional-logic engine (the discriminating brief)

Both produced a genuinely good hand-rolled recursive-descent parser + tree-walking evaluator -
**no parser library, correct operator precedence** (OR < AND < NOT < comparison), short-circuit
AND/OR, type-checked comparisons that safe-fail to `false`, positional `qN` resolution (1-based,
both agree q1 = first question), and graph-reachability cycle detection. Neither fell into the
traps the brief warned about (regex parsing, left-to-right precedence, unguarded recursion).
This is a real result: **both agents cleared the bar on the hard brief.**

Where they diverge:

| Dimension | ST2 (Claude) | ST3 (Codex) |
|---|---|---|
| `grammar.ts` (required output file) | **MISSING** | present |
| `NOT_CONTAINS` operator (in grammar) | **MISSING** (would fail to parse) | implemented + tested |
| Engine unit tests | **54** (parser 27, evaluator 27) | 11 (parser 6, evaluator 5) |
| Integration tests (back-nav re-eval) | 4 | 2 |
| Module separation | cycle/broken-rule folded into `evaluator.ts` | dedicated `validation.ts` + `grammar.ts` |
| Error model | logs every eval failure | `EvaluationFailure.shouldLog` flag - suppresses noise for expected missing-answer, logs real errors |
| Cycle detection output | boolean | returns actual cycle path(s) - richer for UI warning |
| Admin-page tests | thinner (Edit 3, List 3) | broader (Edit 7, List 5) |

**Net read:** a real quality split, and it cuts both ways.

- **ST2 (Claude) is the better *tested* engine but less *spec-complete*.** It omitted a required
  file (`grammar.ts`) and an entire grammar operator (`NOT_CONTAINS`), yet wrote ~5x the
  engine-level unit tests and more back-navigation integration coverage. Its verifier (Opus,
  detailed walkthroughs) **passed it anyway despite the two missing deliverables** - a verifier
  miss worth flagging.
- **ST3 (Codex) is more *spec-complete and better-factored* but more thinly *tested*.** It
  delivered every required file, the full operator set, cleaner module boundaries, and a more
  refined error model (`shouldLog`) and richer cycle output - but its engine has only 11 unit
  tests, and its verifier rationales were terse ("close enough to pass review" on `-11`), a
  looser review bar than Claude's.

### Efficiency

Codex (ST3) won wall-clock decisively for Plan B: **~40 min vs ~50 min** (-20%), with ~45%
fewer output tokens. It leans heavily on prompt caching (~5.6M of its ~6.2M input tokens were
cached). Claude's (ST2) time and cost are concentrated in the two Opus passes on the L-sized
briefs; absent those it would have been faster and far cheaper, but Opus is also where its
engine depth came from. Both pipelines were waste-free (0 rework, 0 failed gates).

### Cumulative (Plan A + Plan B)

| | smoketest2 | smoketest3 |
|---|---|---|
| Tickets | 8/8 | 8/8 |
| Vendor(s) | Claude throughout | Claude (A) + Codex (B) - **mixed** |
| Build time (both chains) | ~84 min | ~73 min |
| Total tests | 111 | 78 |
| Cost | **~$30.22** (Plan A $9.30 + Plan B $20.92) | not computable (Codex leg uncosted) |

### Bottom line

- **Meeting tickets:** Both 8/8 across the full op-doc now. On the hard brief specifically,
  **both cleared the design bar** (correct parser/precedence/cycle detection) - the more
  important result than any cost delta.
- **Quality is a genuine split, not a winner:** Claude tested deeper but shipped two spec gaps
  (missing `grammar.ts` and `NOT_CONTAINS`); Codex was spec-complete and better-factored but
  thinly tested. If you weight "did it deliver every artifact the brief named," Codex wins
  brief 08; if you weight "is the engine's behavior pinned down by tests," Claude wins.
- **Efficiency:** Codex was ~20% faster with far fewer output tokens; likely cheaper too, but
  unproven because cost isn't logged for OpenAI.
- **Two process gaps surfaced, both actionable:**
  1. **Verifiers passed incomplete work.** ST2's Opus verifier did not flag the missing
     required file or the missing operator. Consider having the verify phase check the brief's
     declared output-file list and grammar surface explicitly.
  2. **No cost telemetry for non-Anthropic vendors.** Add `cost_usd` (or token-times-rate)
     emission for OpenAI/codex so cross-agent runs are comparable on spend.

### Next step

Re-run brief 08 with the verifier instructed to check the brief's output-file manifest, and add
OpenAI cost emission, so the next Claude-vs-Codex round is comparable on dollars and catches
spec-completeness gaps automatically.
