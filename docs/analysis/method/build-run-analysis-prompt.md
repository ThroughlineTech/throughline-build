# Prompt: Analyze build-engine run(s) on a single repo (generic)

Hand this file to an agent verbatim. It analyzes the build-engine run history of whatever repo
it is pointed at, with no assumptions about language, framework, platform, or architecture. It
emits a rigid metrics report (stable tables) plus a freeform qualitative section, and appends it
to a running log in the target repo so repeated runs accumulate and stay comparable.

This is the stack-agnostic version of `survey-smoketest-prompt.md`. To compare several repos,
use `cross-repo-comparison-prompt.md` (which consumes the report this prompt produces).

---

## Task

Analyze the build-engine run(s) for the target repo. Produce the standard report (schema below)
and append it as a new "Run N" section to `<repo>/docs/build-run-analysis.md` (create the file
and any missing `docs/` directory if absent).

## Inputs

- Target repo: the repo the user names, else the current working directory.
- Telemetry: `<repo>/.build/events/*.jsonl` (one file per CLI invocation, JSON Lines).
- Config: `<repo>/.build/config.toml` (worker/agent, model map, and the check commands).
- Optional schema reference: `<repo>/docs/event-log-format.md` if present; otherwise use the
  embedded schema below.
- Optional run selector: if the user names specific event-log files or a date range, analyze
  only those; else analyze the most recent chain run and note any earlier runs already logged in
  `build-run-analysis.md`.

## Embedded event-log schema (use if no event-log-format.md is present)

Each line is one JSON object. Fields: `SessionId`, `Timestamp`, `Kind` (int enum), `TicketId`,
`Phase` (int enum), `Data` (object). Optional: `project_name`, `workspace_slug`, `build_version`.

`Kind`: 0 StateTransition (`Data.from`,`to`) | 1 LlmCall (`model`,`vendor`,`input_tokens`,
`output_tokens`,`cache_read_tokens`,`cache_create_tokens`,`wall_clock_ms`, optional `cost_usd`,
optional `cached_input_tokens`,`reasoning_output_tokens`) | 2 WorkerSpawn (`worker`, optional
`role`) | 3 VerifierVerdict (`status` for plan/implement; `kind` in {Pass,Rework,Fail} +
`checks_failed_count` for review) | 4 GateFailure (`kind` + extras) | 5 TicketWrite (`action`:
e.g. `baseline_computed` with `failing_count`, `fixes_detected` with `names`, `fetch_skipped`) |
6 ChainStart (`starting_at_phase`,`initial_state`) | 7 ChainEnd (`outcome`,`phases_run`,
`rework_rounds`,`total_duration_ms`) | 8 ReworkRound | 9 TicketSubsumed | 10 TargetAutoRebased.

`Phase`: 0 Plan | 1 Implement | 2 Review | 3 Ship | 4 Chain | 5 New | 8 Scaffold.

Robustness rule: classify each event by the `Data` keys present (e.g. `cost_usd`/`model` => a
usage record; `outcome`+`rework_rounds` => chain end; `from`/`to` => transition; `worker` =>
spawn; `kind`/`status` => verdict; `action` => side effect). Treat the numeric `Kind` as a guide
that may evolve. Tolerate missing optional fields.

## Method (do all of it; do not skip verification)

1. **Read the existing report** if `build-run-analysis.md` exists; find the next run number and
   match prior formatting so runs stay comparable.
2. **Inventory the run.** List event-log files newest-first. Read `config.toml` for the
   `default_agent`, the size->model map, and the configured check commands.
3. **Recover scope generically.** Identify the tickets in the run from event `TicketId`s. If the
   repo has tickets/op-docs/plan docs, read them to learn intended scope and any declared output
   files - but make NO language/framework assumptions; treat declared outputs as opaque paths.
4. **Parse telemetry** into the metrics in the schema below, per ticket and in total. **Never
   invent a cost: if `cost_usd` is absent for a vendor, report it as not-logged and fall back to
   token volume.**
5. **Independently verify** by running the repo's OWN configured checks from `config.toml` (do
   not assume a build tool or test runner - run whatever the config declares). Capture each
   check's pass/fail by exit code. For test count, best-effort parse the test check's stdout; if
   the runner does not report a count, record "n/a" and say so. Run long checks in the
   background; clean up generated artifacts afterward or note what you left.
6. **Measure code footprint** best-effort and stack-agnostically: source LOC (count text lines
   in tracked non-binary files, excluding obvious vendored/lock/generated paths - state your
   method), test count (from step 5), files changed vs the run's base SHA.
7. **Assess quality qualitatively.** What did the run do well; where did it cut corners; did
   declared output files all appear; did verifiers pass incomplete work; are there spec gaps,
   rework loops, or efficiency problems. Be specific with file:line evidence.

## Output schema - RIGID. Keep tables exactly these shapes so reports are cross-comparable.

Append to `<repo>/docs/build-run-analysis.md`:

```
## Run N - YYYY-MM-DD: <one-line label>

### Run metadata
| Field | Value |
|---|---|
| Repo | <path or name> |
| Date analyzed | YYYY-MM-DD |
| Worker / agent | <default_agent> |
| Models observed | <model(s) by phase> |
| Vendor(s) | <anthropic / openai / mixed> |
| Event log file(s) | <filenames> |
| Tickets in scope | <ids> |
| Scope source | <op-doc / tickets / inferred> |

### Per-ticket results
| Ticket | Outcome | Rework | Verdict | Plan model | Impl model | Verify model | Wall (s) | Out tok | In tok | Cost USD |
|---|---|---|---|---|---|---|---|---|---|---|
| ... | | | | | | | | | | |
| TOTAL | | | | - | - | - | | | | |

### Aggregate metrics
| Metric | Value |
|---|---|
| Tickets attempted | |
| Tickets completed | |
| Completion rate | |
| Total rework rounds | |
| Verdicts (Pass/Rework/Fail) | |
| Build gate (passed/total) | |
| Test gate (passed/total) | |
| Independent re-verify: build | pass/fail |
| Independent re-verify: tests | passed/total or n/a |
| Total wall clock | |
| Total output tokens | |
| Total input tokens (cached) | |
| Total cost (USD) | <value> or "not logged (vendor=...)" |
| Avg cost / ticket | |
| Avg output tokens / ticket | |

### Code footprint (best-effort)
| Metric | Value | Method |
|---|---|---|
| Source LOC | | |
| Test count | | |
| Files changed vs base | | |
| Build artifact size | | |

### Qualitative analysis
Freeform prose. Cover: what went well, where the run cut corners (with file:line evidence),
spec-completeness gaps (declared outputs missing, verifier passing incomplete work), efficiency
(rework loops, cost concentration, token usage), and concrete improvement suggestions for the
build engine, the prompts, or the briefs. This is the only freeform section; keep the tables rigid.

### Comparison vs previous runs (if any)
Deltas and trends vs Run 1..N-1, with confounds called out explicitly.
```

## Guardrails

- Stack-agnostic: assume nothing about language, framework, OS, or architecture. Run the repo's
  declared check commands; treat declared output files as opaque paths.
- Verify, do not trust the chain's self-report; report failures plainly with output.
- Never fabricate cost for a vendor that does not emit `cost_usd`.
- ASCII only: plain hyphens, straight quotes, no em/en dashes, no curly quotes.
- The metrics tables are a stable contract for cross-repo comparison - do not rename or reorder
  their rows/columns. Put all interpretation in the qualitative section.
