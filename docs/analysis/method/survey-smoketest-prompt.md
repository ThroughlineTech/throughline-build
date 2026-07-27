# Prompt: Analyze a survey-smoketest build run and append to the report

Hand this file to an agent verbatim. It performs the same build-run analysis already recorded
in `docs/survey-smoketest.md`, appends a new dated "Run N" section to that file, and compares
the new results against every previous run in the file.

This prompt is project-specific (the survey-app-build op-doc). For an arbitrary repo, use
`build-run-analysis-prompt.md` instead. To compare multiple repos, use
`cross-repo-comparison-prompt.md`.

---

## Task

Analyze the latest build-chain run(s) on the survey-smoketest projects, append a new "Run N"
section to `docs/survey-smoketest.md`, and compare against all prior runs in that file.

## Inputs

- Report to append to: `docs/survey-smoketest.md` (a running log; read it first, end to end).
- Projects: by default, every `survey-smoketest*` directory that is a sibling of this repo's
  parent (e.g. `../survey-smoketest2`, `../survey-smoketest3`). If the user named specific
  projects, use those instead.
- Event-log schema reference: `docs/event-log-format.md` (authoritative; use it to parse
  `.build/events/*.jsonl` rather than guessing field meanings).

## Method (do all of it; do not skip verification)

1. **Read the existing report** `docs/survey-smoketest.md`. Note the format of prior "Run N"
   sections, the next run number, and what each prior run measured so your comparison is apples
   to apples.

2. **Establish scope.** For each project: read the op-doc(s) under `op-docs/`, confirm whether
   the projects share an identical op-doc (diff them), and identify which briefs/tickets the run
   covered. Read `git log --oneline --stat` to see what landed.

3. **Identify the run(s).** List `.build/events/*.jsonl` newest-first. The relevant run is the
   most recent `*-chain-*.jsonl` (or whichever the user pointed at). Note prior runs already
   covered by the report so you only analyze new ones.

4. **Parse telemetry** from the chain event log per `docs/event-log-format.md`. Each line is a
   `WorkflowEvent` with `Kind`, `TicketId`, `Phase`, `Data`. Extract per ticket and in total:
   worker/agent (`WorkerSpawn.worker`, `role`), models per phase (`LlmCall.model`, `vendor`),
   verdicts (`VerifierVerdict.kind` / `status`, `checks_failed_count`), rework
   (`ChainEnd.rework_rounds`, `ReworkRound`), wall clock (`ChainEnd.total_duration_ms`), tokens
   (`LlmCall.input_tokens`/`output_tokens`/`cache_read_tokens`/`cache_create_tokens`/
   `reasoning_output_tokens`), cost (`LlmCall.cost_usd`), and gate results
   (`TicketWrite.action` = `baseline_computed`/`fixes_detected`, `GateFailure`). **If `cost_usd`
   is absent (non-Anthropic vendors do not emit it), say so explicitly - do not invent a cost.**

5. **Independently verify** the working tree (do not trust the chain's self-report). Install
   deps if needed and run the project's build and test commands. Record build pass/fail and the
   exact test count. Run in the background and clean up generated artifacts (`node_modules`,
   `dist`, verify logs) when done, or note that you left them.

6. **Assess code quality**, weighting the deliberately convoluted brief (the conditional-logic
   engine: `src/logic/` parser, evaluator, cycle detection, take-survey integration). Check:
   correct operator precedence, short-circuit evaluation, safe-fail-to-false on errors, cycle
   detection, back-navigation re-evaluation. Also check **spec completeness**: did every file
   the brief declared as an output get created, and is every declared grammar operator /
   function implemented? Note divergences between projects with file:line evidence.

7. **Compare across projects** for this run, and **compare this run against all prior runs** in
   the report: call out deltas in cost, time, tokens, test depth, quality, and any
   regressions/improvements. Surface confounds explicitly (e.g. a model or vendor change between
   runs that makes a naive comparison misleading).

## Output - append a new section to `docs/survey-smoketest.md`

Match the existing file's style. Use this skeleton, keeping the rigid tables rigid:

```
## Run N - YYYY-MM-DD: <one-line what-changed>

### Setup
<what ran, on which projects, with which agent/model per project, scope of tickets/briefs>

### Quantitative
| Metric | <projectA> | <projectB> | ... |
|---|---|---|---|
| Worker / model | | | |
| Tickets completed | | | |
| Rework rounds | | | |
| Verifier verdicts | | | |
| Build gate (chain) | | | |
| Independent re-verify | build + N tests | | |
| Wall clock (chain) | | | |
| Output tokens | | | |
| Input tokens (cached) | | | |
| Cost (USD) | | not logged (vendor=...) | |
| Source LOC | | | |
| Tests (full suite) | | | |

Per-ticket cost/time breakdown (plan+impl+verify) for any costed project.

### Qualitative
<code-quality split, spec-completeness gaps with file evidence, discriminating-brief assessment>

### Comparison vs previous runs
<deltas and trends vs Run 1..N-1; call out confounds that break naive comparison>

### Bottom line
<meeting tickets / quality / efficiency, plus any process gaps worth acting on>

### Next step
<what to run next to get a cleaner or deeper signal>
```

## Guardrails

- Verify, do not trust: re-run build/test yourself; report failures plainly with output.
- Never fabricate a cost for a vendor that does not emit `cost_usd`; report tokens instead.
- ASCII only: plain hyphens, straight quotes, no em/en dashes, no curly quotes.
- Keep the tables rigid and consistent with prior runs; prose lives only in the qualitative,
  comparison, and bottom-line sections.
