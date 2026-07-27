# Cross-repo build comparison - 2026-06-08

Subject: one run of the **changed** build engine (sst-claude) against two **replicate runs of the
prior** build engine (survey-smoketest6, survey-smoketest7) on the same survey-app workload.
Produced per `docs/analysis/method/cross-repo-comparison-prompt.md`. Source reports listed in "Sources"
at the bottom; this is a desk comparison of the three already-logged `build-run-analysis.md`
reports plus arithmetic re-checks - it did not re-run the three repos' checks (each per-repo report
already did its own independent re-verification).

## Subjects

| | sst-claude (Run 1) | survey-smoketest6 (Run 1) | survey-smoketest7 (Run 1) |
|---|---|---|---|
| Repo / run | sst-claude, Run 1, 2026-06-08 | survey-smoketest6, Run 1, 2026-06-07 | survey-smoketest7, Run 1, 2026-06-07 |
| Engine version | CHANGED (new) | prior | prior |
| Worker / agent | claude-code | claude-code | claude-code |
| Models | sonnet-4-6 (impl+verify); plan=promote | sonnet-4-6 (impl+verify); plan=promote | sonnet-4-6 (impl+verify); plan=promote |
| Vendor(s) | anthropic | anthropic | anthropic |
| Op-doc / scope | op-docs/01b-survey-site.md (8 briefs, Plan A + Plan B) | op-docs/01-survey-site.md (8 briefs) | op-docs/01-survey-site.md (8 briefs) |
| Tickets in scope | 8,9,10,11,13,14,15,16 (B01-B08) | 3,4,5,6,8,9,10,11 (B01-B08) | 3,4,5,6,8,9,10,11 (B01-B08) |
| Held constant? | Same worker, same models (Sonnet 4.6 impl+verify, promote plan = $0 plan), same vendor, same 8-feature survey-app brief set (B01 vite-scaffold .. B08 conditional-logic-engine). smoketest6 and smoketest7 are replicate runs of one another (same op-doc, same engine, same day). |
| Confounds | (1) The sst-claude engine differs from the smoketests and the diff is undocumented - the one axis the comparison exists to measure is unspecified. (2) sst-claude uses op-doc 01b (Plan A + Plan B); smoketests use 01 - same brief->feature map, possibly different brief text. (3) n=1 run per engine version, and the two prior replicates already span 1.90x on cost, so single-run deltas are dominated by run-to-run variance. (4) The three reports were written by different analysis passes: their qualitative sections audit different defect classes and the "In tok" column is defined differently (see synthesis), so some apparent differences are measurement, not engine behavior. (5) Different base SHAs / starting baselines per repo. |

## Quantitative comparison

| Metric | sst-claude (new) | smoketest6 (prior) | smoketest7 (prior) | Notes |
|---|---|---|---|---|
| Tickets completed | 8 / 8 | 8 / 8 | 8 / 8 | Identical. |
| Completion rate | 100% | 100% | 100% | Identical across all three. |
| Total rework rounds | 2 (tkt 10, 16) | 0 | 2 (tkt 9, 11) | All reworks were build-gate TypeScript compile errors. Prior engine produced both 0 and 2; not an engine signal. |
| Verdicts (Pass/Rework/Fail) | 8 / 0 / 0 | 8 / 0 / 0 | 8 / 2 / 0 | NOT measured the same way: sst-claude and smoketest6 count final per-ticket verdicts; smoketest7 counts per-review-session (10 sessions incl. rework rounds). Harmonize before trusting this row. |
| Build gate (passed/total) | 8/8 final (2 initial fails fixed in 1 rework each) | 8/8 (self-report) | 9/10 sessions (tkt 11 rd 1 failed build) | The separate build/tsc gate caught compile errors the test gate could not in both reworking runs. |
| Independent re-verify: build | pass | pass | pass | All green. |
| Independent re-verify: tests | 172/172 | 185/185 | 158/158 | Each measured with `.worktrees` excluded; see synthesis on why the unscoped gate is red in all three. |
| Total wall clock | 5140.9 s (85.7 m) | 4527.9 s (75.5 m) | 5018.8 s (83.6 m) | New run is the slowest but only 2% over smoketest7; inside prior spread (x1.11). |
| Total output tokens | 264,051 | 244,056 | 263,670 | Effectively tied; new vs smoketest7 differ by 0.1%. |
| Total input tokens (cached) | 21,060,409 (20.45M read / 0.60M create / 11,523 fresh) | 19,161,865 (18.41M read / 0.75M create / 384 fresh) | 10,707,121 (9.87M read / 0.84M create / 260 fresh) | Cost driver. smoketest7 read HALF the cache of the other two (see synthesis). |
| Total cost (USD) | 20.92 | 20.52 | 10.80 | All logged (vendor=anthropic). Prior engine span [10.80, 20.52] = x1.90; new run ($20.92) sits at the top of that envelope, not outside it. |
| Avg cost / ticket | 2.62 | 2.56 | 1.35 | Same pattern as total. |
| Avg output tokens / ticket | 33,006 | 30,507 | 32,959 | Tied. |
| Source LOC | 2,275 | 2,338 | 1,908 | New run inside prior spread (x1.23). |
| Test count | 172 | 185 | 158 | New run inside prior spread (x1.17). |

Derived (not a schema row): cost per million output tokens is $79.2 (new), $84.1 (smoketest6),
$41.0 (smoketest7). Output volume is nearly identical across all three, so this ratio is set almost
entirely by cache-read volume, not by how much the models wrote.

## Per-dimension verdict

| Dimension | Winner | Basis | Comparable? |
|---|---|---|---|
| Meeting tickets | Tie | 8/8 completed in all three; all three re-verify build-green | yes |
| Output quality | Not comparable | The three reports audited different defect classes (declared-output gaps in 6/7; test-hygiene in sst-claude). No shared rubric, so quality deltas are analyst-driven, not established | no (analyst confound) |
| Efficiency (time) | Tie / within variance | 75.5-85.7 min; new run is slowest but inside the prior replicate spread | weak |
| Efficiency (cost) | Not comparable as engine signal | Prior replicates span 1.90x ($10.80-$20.52); new run ($20.92) is inside that envelope. n=1 per engine version | no |

## Qualitative synthesis

**The spine: the engine change is not measurable from these runs.** smoketest6 and smoketest7 are
the same engine, same op-doc, same day, same models - replicates. They differ by 1.90x on cost
($20.52 vs $10.80), driven entirely by cache-read volume (18.4M vs 9.9M cache-read tokens for nearly
identical 244K/264K output). On every other metric they differ by only 1.08x-1.23x. The changed
engine (sst-claude) lands inside that replicate envelope on every single metric: cost $20.92 (top of
[10.80, 20.52]), output 264K (inside [244K, 264K]), wall 85.7m (inside [75.5, 83.6] by 2%), LOC 2,275
(inside [1,908, 2,338]), tests 172 (inside [158, 185]). Conclusion: nothing in these three runs
distinguishes the new engine from the old one. Either the change targeted something these metrics do
not capture, or it had no effect on this workload. We cannot tell which, because (a) the change itself
is undocumented and (b) there is one run per engine version against a backdrop of ~2x cost variance.

**The one universal, reproducible engine defect: leftover worktrees.** All three reports - including
the new engine - flag the same thing: the chain leaves every `.worktrees/ticket-N` behind, and because
the scaffolded vitest config has no `test.exclude` for `.worktrees`, a plain `npm test` from the repo
root globs into the stale worktree copies (React resolves to null, no installed node_modules) and
reports hundreds of false failures: sst-claude 185 failed, smoketest6 151 failed, smoketest7 147
failed. In every case the real suite is green once `.worktrees` is excluded. This is the single
highest-confidence finding in the whole corpus - three independent runs, one cause - and the new
engine did not fix it. It is also exactly the failure the analysis prompt's "verify, do not trust the
chain's self-report" step is designed to surface: each ticket's gate passed inside its isolated
worktree while the assembled repo's configured gate is red on a clean checkout.

**Cost is cache-read-bound and noisy; do not attribute it to the engine.** Output billing is a minor
share here (~264K output ~= $4 at Sonnet output rates); the bulk of each bill is the cached repo+brief
context the engine re-sends on every call. Two same-config same-day runs (smoketest6 vs 7) differ 2x
on exactly that cache-read volume, which means per-run cost is not a stable property of the engine.
Any cost claim about the engine change needs >=3 replicates per version, not one.

**Schema drift is eroding the "rigid contract."** The cross-repo prompt's premise is that identical
tables make runs comparable, but two columns are already filled inconsistently across the three
reports: (1) the per-ticket "In tok" column is cache-inclusive in sst-claude (2.1M for ticket 8) but
fresh-uncached-only in smoketest6/7 (tens of tokens) - a 5-order-of-magnitude difference in what the
same column means; (2) the "Verdicts" row counts final per-ticket in two reports and per-review-session
in smoketest7. These are measurement artifacts, not engine behavior, and they silently corrupt any
naive head-to-head. Pin the definitions in the per-repo prompt (rename to "In tok (fresh)" plus a
separate "Cache-read" column; define verdict counting as final-per-ticket).

**Verifier blind spots persist, but each report caught a different one.** smoketest6 and smoketest7
both found that B08 declared `src/logic/grammar.ts` as a deliverable and no implementation ever created
it (the grammar lives as inline parser comments) - and the verifier passed anyway because it audits
what is present, not the brief's declared file manifest. sst-claude's report does not mention grammar.ts
at all (op-doc 01b may not declare it, or the pass did not check) and instead documents test-hygiene
rot: a `vi.restoreAllMocks()` that nukes a `beforeEach` setup leaving dead code
(AdminSurveyList.test.tsx:45-53), and an AND-short-circuit test whose spy is never wired into the AST so
it asserts nothing (evaluator.test.ts). Same underlying engine behavior in all three - verifier notices
a defect, documents it, passes it - but because the three passes looked at different things, you cannot
rank output quality across them. This is the strongest argument for a shared audit checklist (below).

**Model-tier flattening: same fact, opposite framing.** Every LLM call in all three runs was
claude-sonnet-4-6, even though the op-doc assigns S/M/L efforts and config maps small->haiku,
medium->sonnet, large->opus. smoketest6 calls this out as a likely bug ("the size->model map is
currently inert for this run ... S briefs likely overpaid running Sonnet instead of Haiku, Opus never
exercised"). sst-claude frames the identical observation as a feature ("sizing looks well-calibrated;
none escalated to Opus; even the parser-heavy tkt 16 succeeded on Sonnet"). Both are looking at the
same uniform-Sonnet behavior. The disagreement is unresolved and worth a definitive answer: confirm
whether the brief size label actually reaches dispatch and resolves through the map, or whether
everything is hard-pinned to medium.

**Type errors keep consuming review rounds.** Two of the three runs reworked, and every rework was a
trivial TypeScript compile error caught at the review build-gate (sst-claude tkt 10 TS18048 null-narrowing
and tkt 16 unused imports; smoketest7 tkt 11 unused const + param-type mismatch). smoketest7's report
already recommended running `tsc --noEmit`/build inside the implement phase so these do not cost a full
review+rework cycle (~$0.30-$1 each). The new engine still pays that cost. This is a concrete,
already-identified lever the engine change did not pull.

**What the comparison cannot conclude.** Whether the engine change improved anything. The diff is
undocumented; the metrics that moved (cost, rework count) are inside prior run-to-run variance; and the
qualitative differences are confounded by three different analysis passes. This is a "not comparable"
verdict stated honestly, which the prompt's guardrail demands over a confident-but-invalid winner.

### Recommendations

Engine:
1. Prune worktrees on chain success (`git worktree remove` per ticket, or sweep `.worktrees/` at
   ChainEnd). Highest confidence - three independent runs, same defect, still present in the new engine.
2. Run the type/build check inside the implement phase, not only at review, so trivial TS errors are
   fixed in-loop instead of burning a rework round (would have caught sst-claude tkt 10/16 and
   smoketest7 tkt 11).
3. Fix or drop the vacuous typecheck gate. sst-claude found root tsconfig has `"files": []` + project
   references, so `tsc --noEmit` checks nothing and always passes; real type gating comes only from the
   `build` check. Emit `tsc -b --noEmit` or remove the redundant check.
4. Resolve the model-tier question: confirm the S/M/L size label reaches dispatch and resolves through
   the size->model map, or document that uniform-Sonnet is intentional. Right now the map is inert.

Scaffold / template:
5. Emit a vitest `test.exclude` of `['**/node_modules/**', '**/.worktrees/**', '**/.build/**']` so the
   configured test gate is hermetic even if worktrees survive, and run the post-merge regression from the
   assembled repo root, not only inside each isolated worktree.

Verifier:
6. Add a declared-output existence check: diff each brief's Files/Outputs list against the actual change
   set and flag missing deliverables (would have caught grammar.ts in two of three runs).
7. Downgrade-to-Rework (or auto-fix) when the verifier itself identifies dead or non-asserting test code,
   rather than documenting it and passing - otherwise test-hygiene rot accumulates run over run.

Reporting / prompt (so future comparisons are valid):
8. Pin the per-ticket "In tok" definition (split into "In tok (fresh)" + "Cache-read") and the verdict
   counting rule (final-per-ticket) in `build-run-analysis-prompt.md`. Two columns already drifted.
9. Give the per-repo qualitative section a fixed checklist - declared-output manifest, test hygiene,
   gate efficacy, worktree cleanup, model-tier propagation, cache/cost - so every report covers the same
   axes and cross-report quality comparison becomes possible.

Experiment design (the unblock):
10. To measure the engine change at all: document the diff, then run >=3 replicates of BOTH the old and
    the new engine on ONE pinned op-doc. Single runs cannot beat the ~1.9x cost variance already visible
    between smoketest6 and smoketest7.

## Bottom line

All three runs ship the survey-app cleanly - 24/24 briefs completed, all three re-verify build-green and
test-green once `.worktrees` is excluded - so the engine reliably delivers this workload on Sonnet 4.6.
What is NOT comparable is the effect of the system change: its diff is undocumented, the new run lands
inside the envelope two prior replicate runs already span (1.90x on cost alone), and the three reports
were written by different passes that audited different things and even filled the "In tok" and
"Verdicts" columns differently. The single most reproducible signal across all three is a defect the new
engine did not fix: leftover `.worktrees/` that red-line the repo's own configured `npm test`. Highest-
value next action: tell me what the engine change actually was, then run >=3 replicates per engine
version on one pinned op-doc and pin the report schema - until then "did the change help?" is
unanswerable, not negative.

## Sources (referenced reports)

- New engine (subject): `<workspace>/new-engine/docs/build-run-analysis.md` (Run 1, 2026-06-08)
- Prior engine, replicate A: `<workspace>/prior-engine-a/docs/build-run-analysis.md` (Run 1, 2026-06-07)
- Prior engine, replicate B: `<workspace>/prior-engine-b/docs/build-run-analysis.md` (Run 1, 2026-06-07)
- Comparison schema/prompt: `../method/cross-repo-comparison-prompt.md`
- Per-repo schema/prompt (referenced by the above): `docs/build-run-analysis-prompt.md` in each repo

## Extending this report (the less-convoluted way)

You do not need to edit the prompt file or re-paste the schema. When you have another
`<repo>/docs/build-run-analysis.md`, just say:

> add `<full path to the new build-run-analysis.md>` to the cross-repo comparison (this file)

and I will: read the new per-repo report, add a column to the Subjects and Quantitative tables, refresh
the per-dimension verdicts and the variance envelope, and append a dated "Update N" note to the synthesis
recording what the new column changed. This file stays the single running artifact - the cross-repo
analogue of each repo's own running `build-run-analysis.md`. Add new source paths to the "Sources"
section above as they come in.
