# Experiment program - handoff / living ledger

Read this first. It is the orientation + running record for the process-experiment program on the
ThroughlineBuild / Throughline Build engine. Keep it THIN: one short entry per experiment, links out to the
detail. Every agent updates exactly three things: (1) the "Current state" line, (2) its experiment's
ledger row, (3) any new standing learning. Do NOT duplicate per-experiment detail here - that lives in
the experiment folders.

Where things live: the handoff ledger, the planning docs (feedback + plan), and the run analysis all
live and are updated on `main` (durable program record). Only the CODE change plus its
implementation-summary live on the experiment branch. Fold brings the code into main; abandon discards
it - but the ledger + analysis on main preserve the record of what was tried and why.

## Current state
- NEXT: Experiment 4 is PLANNED (plan written on `main`: `engine-iteration/04-plan.md`).
  Experiment 3 RAN (sst11, analysis in `findings/experiment-3-analysis.md`): preload confirmed firing
  (uncached input -87.5%); cheapest/fastest run, but at n=1 the magnitude is inseparable from brief-to-brief
  variance. Exp-4 turns from front-loading (which cut TURNS) to the intra-session `cache_read` RAMP (context
  per TURN). Its gating investigation is RESOLVED: a spawning parent CANNOT prune the child's accumulated
  context (no sub-limit compaction, hooks can't retro-drop tool_results, `build` does not own the turn loop),
  so the feedback's L1 (supersession pruning) and L3 (parent output cap) are DEAD. Exp-4 = (a) always-on
  per-turn context-attribution telemetry (`context_attribution` CostLedger: cache_read series + per-tool-class
  bytes + slope) build-regardless, and (b) one behavioral lever behind `[project].context_hygiene` (default
  false): effort-gated planning hygiene for S briefs only - a prompt line + `--disallowedTools TodoWrite,Task`.
  Dan: have the implementer code the exp-4 plan on a fresh `exp-4-context-hygiene` branch off main, land the
  prereqs (s9 of the plan) first, then RUN off vs on arms on the exp-4 op-doc (agents cannot run `build chain`
  - nested-session guard).

## The program in one paragraph
We tweak the engine's process in a survey-app prompt family and record both runner and op-doc
changes. Some iterations intentionally changed execution guidance in the op-doc to activate the
runner mechanism under test; later runs held that op-doc byte-identical while runner instructions
continued to evolve. #1 goal: the engine's generated OUTPUT is stack-agnostic, so every change must
be too (stack knowledge in derived data, never in engine C#). Base branch is `main`; each experiment
is a throwaway branch off main that we later FOLD into main or ABANDON.

## Standing docs (the contracts)
- Protocol + roles + repo traps: `experiment-harness-prompt.md`
- Implementation-lead brief (paste-ready): `experiment-implementer-prompt.md`
- Run metrics report (rigid tables): `build-run-analysis-prompt.md`
- Prompt family + baselines: `docs/analysis/workloads/survey-app-build.md`,
  `docs/analysis/findings/chain-efficiency-evidence.md`

## The loop (where a fresh agent plugs in)
1. PLAN (agent): a feedback note -> a deep-dive plan in `experiment N/`. Works on main.
2. IMPLEMENT (agent, the lead brief): plan -> code on `exp-N-slug` branch + `implementation-summary.md`.
   Runs `dotnet test`; does NOT run the chain.
3. RUN (Dan, manual): run the experiment-branch `build` binary on the op-doc against a fresh target
   repo; capture `.build/events/*.jsonl`. Also run `main`'s binary on the same op-doc as the control.
   Agents CANNOT run `build chain` (the claude worker hits the nested-session guard) - this step is Dan.
4. ANALYZE (fresh agent, on main): `build-run-analysis` report into `experiment N/analysis-from-*.md`,
   compare vs the main control, AND verify the experiment's acceptance criteria from the feedback note.
   Update this ledger.
5. DECIDE (Dan): fold the branch into main or abandon it; drop claude-web feedback into
   `experiment N+1/feedback-*.md`. Back to step 1.

## Experiment ledger
### Experiment 1 - gate-output: typecheck vacuity + worktree cleanup
- Branch: `exp-1-gate-output` (off main @8b53486) | Status: IMPLEMENTED, green (2124 pass / 0 fail,
  ~36 new tests). Run + analysis PENDING.
- Change: a generic gate non-vacuity prover (canary-as-data, fired at a gating check's first green;
  new `ChainOutcome.GateVacuous`, exit 8) + a success-gated worktree sweep reusing the decruft ladder.
  Stack-agnostic audited (no `language ==` in engine C#).
- Feedback: `engine-iteration/01-feedback-from-smoketest-8.md`
- Plan: `engine-iteration/01-plan.md`
- Summary: `engine-iteration/01-implementation-summary.md` (on the branch)
- Analysis: `findings/experiment-1-analysis.md` (after Dan's run)
- Result: mechanism proven deterministically by the unit suite; live run pending | Decision: FOLD
  candidate, pending run

### Experiment 2 - context pre-loading (named-input + convention bundle inlined into the implement brief)
- Branch: `exp-2-context-preload` (now on `main` @27e0dbb) | Status: IMPLEMENTED + RAN (sst10), but the
  mechanism SILENTLY NO-OPPED in the run. 2178 tests green; the deterministic unit suite proved a
  mechanism that does not fire end-to-end.
- Change: pre-load the brief's named inputs + a derived convention bundle into the implement prompt so the
  worker stops re-discovering files. Stack-agnostic audited.
- Plan: `engine-iteration/02-plan.md` | Summary: `engine-iteration/02-implementation-summary.md`
- Analysis: `findings/experiment-2-analysis.md` (Run 2 = sst10; rigid-table analysis missed the
  no-op because it had no `--debug` turn-class data)
- Result: NO-OP in the live run - 3 defects (`engine-iteration/03-feedback-from-experiment-2.md`) | Decision:
  do NOT fold/abandon independently of experiment 3 (exp-2 + exp-3 = one unit; exp-2 alone is a proven no-op)

### Experiment 3 - make context pre-loading actually fire (and fail loudly)
- Branch: `exp-3-preload-fire` | Status: IMPLEMENTED + RAN (sst11). Preload confirmed firing.
- Change: fixes experiment 2's 3 defects - a NEW positive-only `Preload:` brief label (replaces the
  prose-`Inputs:` scrape that no-opped), build the pre-load section AFTER the worktree is checked out at
  baseRef, and emit loud countable events (`preload_summary`/`preload_file_not_found`/`preload_empty`).
- Feedback: `engine-iteration/03-feedback-from-experiment-2.md`
- Plan: never written to disk (investigated @40738d2; the placeholder file was empty and has been
  removed). Experiment 3 is the one loop with no `engine-iteration/03-plan.md` - see the note in
  `engine-iteration/README.md`.
- Analysis: `findings/experiment-3-analysis.md` (Run 3 = sst11)
- Result: lever fired (45 files / 60,753 bytes preloaded; uncached input -87.5%); cheapest/fastest of the 3
  runs ($19.27, 75.5m), but the cost magnitude is inseparable from n=1 brief-to-brief variance. New watch:
  test depth eroding run-over-run (221 -> 212 -> 175) | Decision: pending (replicate 3-5x before crediting)

### Experiment 4 - per-turn context attribution telemetry + effort-gated planning hygiene
- Branch: `exp-4-context-hygiene` (to be cut from `main`) | Status: PLANNED (plan written), not yet implemented
- Change: attacks the intra-session `cache_read` RAMP (context per turn) rather than turn count. (a) Always-on
  per-turn context-attribution telemetry (a `context_attribution` CostLedger advisory: cache_read series +
  per-tool-class byte split + slope), build-regardless - first time intra-session cost is event-legible.
  (b) One behavioral lever behind `[project].context_hygiene` (default false, opt-in): for S-effort briefs ONLY,
  a planning-hygiene prompt line + `--disallowedTools TodoWrite,Task`. L1 (supersession pruning) and L3
  (parent output cap) from the feedback are DROPPED as unreachable from a spawning parent.
- Feedback: `engine-iteration/04-feedback-from-experiment-3.md`
- Plan: `engine-iteration/04-plan.md`
- Result: pending implement + run | Decision: pending

## Standing learnings (do not re-litigate)
- A process mechanism MUST fail loudly (a countable event) when it no-ops. Experiment 2's pre-load
  silently did nothing through a FULL run + a rigid-table analysis without anyone noticing - the
  gate-output convention (experiment 1) is the standard: emit a hard, greppable signal, never a
  clean-looking prompt.
- OpDocParser's brief label regex (`BriefLabelPattern`) only matches `Label:` with the token immediately
  before the colon; `Inputs (parenthetical):` does NOT match and the content is absorbed into the prior
  `Goal` label. Any mechanism needing structured brief inputs uses a dedicated recognized label, never a
  scrape of prose `Inputs:`. The renderer also has NO fenced-block support (inline backticks only).
- Anything that reads the worker's worktree must build AFTER the worktree is checked out at `baseRef`
  (post Step 9 in `ImplementPhase`), not at brief-build time - the chain path is NOT materialized early.
- `EventKind.GateFailure` is a DISCRIMINATED ADVISORY telemetry kind (string `kind` field:
  `drift_warning`, `hygiene_gate`, `gate_unverified`, ...), NOT the hard-fail mechanism. Emitting it is
  pure telemetry; the chain's hard-fail comes from a phase RETURN VALUE (e.g. `ChainOutcome.GateVacuous`),
  never from the event stream (sinks are write-only). So a new loud signal can emit + proceed (advisory),
  and promoting it to fatal is a separate, deliberate control-flow change.
- A spawning parent CANNOT prune the worker's accumulated context (exp-4 gating finding). `build` pipes one
  stdin prompt to `claude -p` and reads the result; it does NOT own the Read/Edit/Bash/TodoWrite turn loop or
  its growing context. claude-code exposes no parent-settable sub-limit compaction, and hooks (PreCompact/
  PostToolUse) only ADD context or BLOCK actions - they never retro-drop/rewrite a tool_result already in the
  window. So "drop superseded TodoWrite/Read" and "cap an oversized tool_result" are dead as engine mechanisms.
  What IS reachable per-spawn: the argv (`BuildArgs` is rebuilt each call; `--model` already varies by size,
  `--allowedTools`/`--disallowedTools` restrict the toolset) and the brief text. Per-turn `message.usage` +
  `tool_use` names ARE in the stream we already capture (parsed only on the `--debug` path today), so per-turn
  cost is observable even though it is not controllable.
- `BaseRefResolver` advances `origin/<target>` to the LOCAL target tip when ahead (TLB-411), so a chain
  child branches from its locally-shipped siblings - the integration tip carries prior briefs' commits.
  The stdin's `origin/main <sha>` base label is the frozen-origin drift anchor, not the per-ticket baseRef.
- Checks are LLM-derived into the target's `config.toml`; stack knowledge stays in derived data, not
  engine C#.
- Cache/cost is a WEAK proxy for run quality (`chain-efficiency-evidence.md`); prefer behavioral /
  deterministic signals.
- The rework loop is not the cost driver; do not weaken review or touch `MaxReworkRounds`.
- Base = `main`; experiments are fold-or-abandon branches.
- An agent cannot run `build chain` (nested-session guard) - the chain run is a manual Dan step;
  agents implement and analyze, they do not run the engine end-to-end.
- Agents share ONE working tree. A parallel agent can switch the branch under you, so a commit can land
  on the wrong branch - run `git branch --show-current` before every commit; keep handoff + analysis on
  main, code + summary on the experiment branch.
