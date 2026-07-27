# Plan - experiment 1: gate-output convention (typecheck vacuity + worktree cleanup)

Spec for the implementer. Input: `01-feedback-from-smoketest-8.md` in this folder, itself distilled
from `docs/analysis/findings/cross-repo-comparison-2026-06-08.md` (engine recommendations 1 and 3). Read the
stack-agnostic constraint (section A) and the architecture-reality section (2) before touching any
code - the feedback's wording is TypeScript-flavored and assumes a fixed config the engine does not
have, and the fix changes shape because of both.

All file:line citations were read from the source tree on branch `minbatch` at HEAD `d00a0cf`. Lines
marked "(verify)" were reported by a survey pass and not re-opened line-by-line; confirm before
editing. Where a doc and the code disagree, the code wins.

---

## A. Stack-agnostic constraint (the #1 goal - non-negotiable)

ThroughlineBuild generates and builds target projects of ANY stack: TypeScript, dotnet, Python, Go,
or even a series of plain text documents. Its OUTPUT is stack-agnostic. Every change in this
experiment MUST be stack-agnostic too. A fix that only works for a TypeScript target is a defect.

The rule that makes this concrete: stack-specific knowledge lives in DATA (the LLM-derived project
profile in the target's `config.toml`, and the op-doc / derive prompt that produces it), NEVER in the
engine MECHANISM (C# code). The engine provides general mechanisms; the LLM that already derives the
per-stack check commands also derives the per-stack specifics each mechanism consumes.

The feedback is written in TypeScript terms (`tsc -b --noEmit`, `vitest test.exclude`) because the
smoke test was a TS project. Do not transcribe those into engine code. Translate each into a general
mechanism + derived data, with TS as the first concrete instance. The two defects below are reframed
accordingly. (The engine's OWN repo is dotnet/C# and we optimize it hard - "agnostic" constrains what
the engine generates, not what it is written in.)

---

## 0. What experiment 1 changes (one sentence each)

- Defect 1: make any gating check impossible to be silently vacuous - a GENERIC "prove this gate can
  fail" self-test driven by a per-check canary the deriver emits, fired at a gating check's first green
  on real code, so a gate that can never fail hard-fails the chain there instead of shipping a
  meaningless green. Typecheck-under-TS-references is the motivating instance; the mechanism is check-
  and stack-agnostic.
- Defect 2: prune per-ticket worktrees on chain SUCCESS by reusing the existing git-level decruft
  ladder (already fully stack-agnostic), sweeping `.worktrees/` clean at the end of a successful chain
  while preserving them on failure; plus derive-prompt guidance that the target's test command be
  hermetic to the engine's working dirs (expressed in whatever runner idiom the stack uses).

Both are instances of the umbrella principle in the feedback: a gate (or the post-run tree) must
produce only actionable signal - quiet on pass, loud and specific on fail; never false-green, never
false-red. That principle is itself stack-agnostic.

---

## 1. Source and framing

The feedback names two defects from the Run 1 survey-app analysis and asks to "fold the fixes into
the gate-output convention in progress." The convention: a check emits nothing on success beyond a
terse one-line status, and on failure emits only the failing items with file:line and message. The
reason is cache economics, established in `docs/analysis/findings/reduce-token-churn.md` and
`chain-efficiency-evidence.md`: gate output becomes worker/reviewer LLM context and is re-read from
cache every later turn, so "test 1 passed / test 2 passed / ..." is pure cache weight with zero
actionable content. None of this is stack-specific - it is true of any toolchain's output.

This experiment does NOT build a general gate-output summarizer. The convention's pass-side ("one
status line, no enumeration") is already satisfied by existing code (see 2.4); experiment 1's job is
the two concrete defects plus locking the already-correct behavior with tests. The general fail-side
error-extraction layer is explicitly out of scope (section 6).

---

## 2. Architecture reality (read this first)

### 2.1 Checks are LLM-derived into the target's config, not hardcoded in the engine

There is no `tsc --noEmit` string in this repo to find-and-replace. The scaffold derives the target
project's checks from the op-doc prose with an LLM worker:

- The derive prompt: `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md`. It instructs
  the worker to read the op-doc and emit a `PROJECT_PROFILE` JSON with `review_checks` /
  `regression_checks`, each `{name, executable, arguments, timeout_minutes}`. The example shows only
  `build` and `test`; rule at line 26-27: "Do not invent a check the op-doc does not support."
- The derived profile is written to the target's `config.toml` by `ConfigProfileWriter`
  (`src/ThroughlineBuild.Cli/ConfigProfileWriter.cs`, `RenderChecks` ~line 224 (verify)), invoked from
  `ScaffoldProfileRunner.RunAsync` (`src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs` ~line 98
  (verify)).
- `src/ThroughlineBuild.Commands/Templates/config.toml.template` intentionally leaves the checks
  section empty (it is filled by the derive step).

Consequence and why this HELPS agnosticism: because the per-stack check commands already flow through
LLM-derived data, the per-stack vacuity canary can flow through the SAME channel. "Change the command
wherever it is configured" maps to derive-prompt guidance (Fix A); "make vacuity un-shippable" maps to
a generic prover fed by a derived canary (Fix B). Neither puts a stack assumption in engine code.

### 2.2 Abstract check names and gating classification (op-30)

The gate already classifies checks by abstract name. From op-30
(`docs/op-docs/op-30-deterministic-chain-gate.md` and the GatePhase it produced): the gate consumes
abstract names `build`, `test`, `typecheck`, `lint`, `format`; `build/test/typecheck` are GATING
(non-zero exit hard-fails), `lint/format` are advisory (recorded as smoke signals, never hard-fail).
`docs/state-of-the-system/04-configuration.md:145` documents the `typecheck` abstract check. These
abstract names are stack-agnostic labels (a Python `mypy` and a TS `tsc` both map to `typecheck`), so
the vacuity prover keys on the abstract name and the GATING role, not on any tool.

- Gate phase: `src/ThroughlineBuild.Phases/GatePhase.cs`. Emits `EventKind.GateFailure` with
  `kind="gating_checks_failed"` and `checks_failed=[names]` (~line 117-121, verify); hard-fail reason
  shaped as `"gate: {names} failed"` (~line 130-131, verify).
- Check model: `record CheckSpec(string Name, string Executable, IReadOnlyList<string> Arguments,
  TimeSpan Timeout, CheckRole Role = CheckRole.Gating)` in
  `src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs`.
- Runner: `src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs`, `RunAsync(IReadOnlyList<CheckSpec>,
  workingDirectory, ct)`; captures the last 4096 chars of each stream via `Tail()` (~line 220). This
  runner is already stack-agnostic (it runs whatever executable+args the check declares) - the prover
  reuses it verbatim.

### 2.3 Chain worktree lifecycle and the EXISTING decruft (already agnostic)

A robust, stack-agnostic removal primitive already exists - reuse it, do not write new teardown:

- `WorktreeDecrufter.DecruftAsync(worktreePath, mainWorktreePath, ct)` -
  `src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:55`. A 7-step ladder: kill preview PIDs, remove
  preview state, pre-clean Windows node_modules reparse points (junctions), `git worktree remove`,
  `... --force`, `Directory.Delete`, `git worktree prune`. It guards on the worktree being present in
  `git worktree list` (returns `WorktreeNotFound` if absent). It operates on git worktrees and the
  filesystem - no language assumptions (the node_modules pre-clean is a Windows-junction defense that
  no-ops when absent, so it is harmless for non-node targets).
- Per-ticket worktree path: `PhaseWorktreeLayout.Compute(ticketId, title, mainWorktreePath)` ->
  `.worktrees/ticket-{slug}`, branch `ticket/{id}` (`src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs`).
- Parent-chain shared worktree: branch `chain/{slug}`, path `.worktrees/chain-{slug}`
  (`ChainIntegrationBranchFromId`, `ChainPhase.cs:2688`; `EnsureIntegrationWorktreeAsync`,
  `ChainPhase.cs:2690`).
- Today decruft runs only after a STANDALONE successful ship:
  `src/ThroughlineBuild.Phases/ShipPhase.cs:706-716` - `if (!_shipOptions.SkipDecruft) { ... DecruftAsync ... }`.
- But the chain ship factory sets `SkipDecruft: true` (`src/ThroughlineBuild.Cli/Program.cs:1830`,
  commented at `ChainPhase.cs:70`) so the shared `chain/{slug}` worktree survives between children.
  Net effect: inside a chain, leaf per-ticket worktrees are never decrufted and there is no
  end-of-chain sweep. Hence `.worktrees/` accumulates after a successful chain - the observed defect.
- Failure already preserves: every early-return failure path in `ChainPhase` skips decruft. Good -
  match this; the new cleanup must be SUCCESS-gated so the preserve-on-failure behavior is unchanged.
- Success predicate to gate on: `IsChainSuccess(ChainOutcome)` - `ChainPhase.cs:2883` (Completed,
  RatifiedObsolete, ParentCompleted, BatchImplemented).
- Git abstraction: `IGitClient.ListWorktreesAsync` and `RemoveWorktreeAsync(path, force, ct)`
  (`src/ThroughlineBuild.Contracts/IGitClient.cs`, `ProcessGitClient`). `ListWorktreesAsync` returns
  `WorktreeInfo(Path, Branch, HeadSha, IsLocked, IsPrunable)` - use Branch to identify this chain's
  worktrees.

CONTRADICTION TO RESOLVE: `src/ThroughlineBuild.Phases/AGENTS.md` claims the parent chain's shared
worktree is "torn down once at chain end." The observed behavior (stale `.worktrees/` after a
successful run, plus the smoke-test false red) and the absence of any success-path sweep say otherwise
for the per-ticket worktrees, and likely for the shared one too. Before coding, locate any existing
end-of-chain teardown and determine precisely what it does and does not remove; the fix either adds
the missing sweep or repairs the existing one. Do not assume the doc is right.

### 2.4 Quiet-on-pass is ALREADY implemented at the brief layer (and is agnostic)

The review brief already renders a passing check as a single status line and omits its output entirely
- it only emits stdout/stderr for FAILED checks, for any check regardless of tool:

- `src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs:196-223` `BuildAutomatedChecksSection`: per check
  emits `- {Name}: PASS|FAIL (exit, elapsed)`, and only when `!check.Passed` appends the (budgeted)
  stdout/stderr tails. Same pattern in `BatchReviewBriefBuilder` (~line 225-248).
- Gate failures into rework: `ImplementBriefBuilder.BuildGateFailureFeedbackSection` (~line 118-132)
  renders the failed checks' tails in fenced blocks. On a gate PASS the chain proceeds to review
  without rework, so pass-side gate output never reaches a worker at all.

So "a passing check emits one status line, no enumeration" is already true through this path for any
stack. Experiment 1 VERIFIES this and LOCKS it with a regression test; it does not rebuild it. If a
separate path leaks passing-check enumeration into context, that is a finding - cite and fix only that.

---

## 3. Defect 1 - gates that cannot fail (vacuity), agnostic

### 3.1 Root cause (TS instance, general class)

In the smoke test the scaffolded TS project used a project-references tsconfig (`"files": []` +
`references`); a typecheck check of `tsc --noEmit` does not follow references, compiles zero files, and
always exits 0. Real compile breaks (tkt 10 null-narrowing, tkt 16 unused imports) passed this gate and
were caught only by the heavier `build` check.

The general class: a configured GATING check can be structurally incapable of failing - it inspects
zero files, points at the wrong project/module, or globs nothing. This is not a TS problem. A Python
`mypy` aimed at an empty package, a dotnet check pointed at the wrong solution, a docs linter globbing
no files - all produce the same meaningless green. The fix must address the class.

### 3.2 Design - generic non-vacuity prover (Fix B) + derive guidance (Fix A)

Fix A (guidance, best-effort, in derived data): teach the deriver to choose commands that actually
traverse the real sources, and to emit a canary per gating check.
Fix B (guarantee, deterministic, generic engine mechanism): a prover that, at a gating check's first
green on real code, runs that check's declared canary and asserts the check now fails; if it still
passes, the gate is vacuous -> hard-fail the chain at the gate.

#### Fix B - the generic gate non-vacuity prover (the guarantee)

A single stack-agnostic mechanism. For each GATING check that, in the gate path, returns GREEN against
materialized source and carries a declared `canary` and has not yet been proven this run:

1. Materialize the canary into the gate's worktree (write the declared file(s) with the declared
   content). The canary is a deliberately broken input the check MUST reject.
2. Re-run that one check via the existing `AutomatedChecksRunner` (reuse - it already runs arbitrary
   executable+args, so it is stack-free).
3. Assert `CheckResult.Passed == false`. If the check PASSES with the canary present, the gate is
   vacuous: hard-fail the chain with an actionable message naming the check and the canary that failed
   to trip it, and emit a structured event (see "unverified/vacuous events" below). Vacuity is a
   config defect, not a code defect - reworking the brief cannot fix it, so do not feed it to the
   rework loop; stop. The hard-fail is a chain FAILURE, so Defect 2's preserve-on-failure leaves the
   worktrees in place for inspection.
4. Remove the canary in a `finally`. Then - and this is load-bearing because the gate runs in a warm
   worktree the chain later ships from - assert the worktree is clean of the canary before proceeding
   (`git status`/the existing hygiene check shows no trace of the probe file). If cleanup did not fully
   land, hard-fail rather than risk the canary being staged into a real commit. (A gitignored probe
   path is NOT a substitute here: `tsc` and most type/build tools ignore `.gitignore` and only see
   files inside their include/references set, so the canary must live in a tracked source dir - the
   clean-assert is the real guard, per the orphaned-state class of bug this engine has already hit.)
5. Mark this check proven for the run (per-check-once state) so it is probed at its first green only,
   not on every subsequent green - one extra check-run per gating check per chain, bounded.

Why the gate path and not scaffold/preflight (corrects an earlier draft): the target source tree does
NOT exist at scaffold time. `ScaffoldPhase.RunAsync` only creates tickets
(`src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:34-114`); the application source (`tsconfig.json`,
`src/...`) is materialized by the implement phase of brief 01 (`npm create vite ...` per the op-doc),
which runs much later in the chain. So a post-scaffold or chain-preflight prover would probe an empty
directory and flag every gate vacuous on every greenfield run. The only sound trigger is event-driven:
prove the first time a gating check passes green on real code, which is exactly the dangerous condition
(a green that might be meaningless). This binds the proof to materialized source and works for
greenfield and brownfield alike. The gate already runs the configured checks once on the warm worktree
at the implement->review seam (`GatePhase`, inserted in `ChainPhase.RunImplementReviewLoopAsync`) - the
prover hooks the per-check GREEN result there.

The canary is DATA, not code. The engine never contains `tsc`/`mypy`/`csc` knowledge; it writes files
and runs a command. Each stack's canary is declared by the deriver (which already knows the stack):

- TS (project references): canary `{ "path": "src/<a-referenced-source-dir>/__tlb_probe.ts",
  "content": "export const __tlb_probe: number = \"not a number\";" }` and the typecheck command
  `tsc -b --noEmit`.
- dotnet: canary a `.cs` file in a compiled project with a type error; check `dotnet build` of the
  solution.
- Python: canary a `.py` with a `mypy`-rejected error; check `mypy <package>`.
- Text-docs: canary a file the configured doc linter rejects (e.g. a broken-link or schema violation);
  check the linter command.

THE SELF-CORRECTING PROPERTY (why this is robust without the engine knowing the stack): if the deriver
declares a canary path the vacuous check cannot see (e.g. a `.ts` outside the referenced set under a
`files: []` root), the check still passes with the canary present -> the prover flags the gate vacuous
-> the chain hard-fails. So a mis-declared canary surfaces the same defect; the engine mechanism remains generic and
the proof is what carries the guarantee. The "inject into a referenced project, not the empty root"
subtlety from the TS case becomes the deriver's concern, and a wrong choice is caught, not silently
accepted.

Profile schema extension (DATA, agnostic): add an optional `canary` to each check in the derived
profile and the parsed `CheckSpec` (or a parallel per-check map kept beside the checks). Shape:
`canary: { path: <relative path under target root>, content: <string> }` (allow a small array of files
if a stack needs more than one). AOT note: a new serialized field needs its `JsonSerializerContext`
updated.

Unverified/vacuous events (do not let a skipped proof vanish): a gating check without a canary is not
silently skipped - emit a structured `gate_unverified` smoke-signal/event (reuse the existing
`SmokeSignal` / `EventKind` machinery) so unverified gates are COUNTABLE across runs, same actionable-
signal principle as the rest of the experiment. A proven-vacuous gate emits a distinct hard-fail event.
Never block scaffold on a missing canary; surface it as a countable signal instead.

Test-gate canary is in scope, not optional: a green test gate that collects ZERO tests
(vitest `passWithNoTests`, a wrong glob, pytest matching nothing) is the scariest false-green, because
"tests pass" is the headline acceptance signal for every brief. The test canary is a deliberately
failing test the runner must report red (data, agnostic - same mechanism). If a full failing-canary is
too heavy for some runner, the fallback is to prove the runner collected > 0 tests; note that a
collected-count assertion needs the runner's own count output (stack-specific parse) so prefer the
failing-test canary to stay in the data-only model. Declare canaries for all gating checks
(`typecheck`, `build`, `test`); `lint` advisory checks may carry one too.

Build the prover as a small testable unit (input: a gating `CheckResult` that is green + its canary +
the gate worktree path + the per-run proven-set; output: OK / vacuous-with-reason / unverified) and
hook it into the gate path on each gating GREEN. Gate it behind a config flag defaulting ON, named
agnostically (`verify_gate_vacuity`) and placed wherever the gate/review config is read.

#### Fix A - derive-prompt guidance (general principle + per-stack examples)

In `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md`, under "Rules for checks", add
(ASCII, LF) a GENERAL non-vacuity rule, then stack examples - not a TS-only edit:

- Every gating check must be capable of FAILING on broken input. Choose a command that traverses the
  project's real sources, not an empty aggregate root. For each gating check, also emit a `canary`: the
  smallest deliberately-broken file the check must reject (path relative to the project root + content).
- Non-vacuous is necessary but not sufficient - the gate should also be as STRICT as the build it is
  meant to replace cheaply. A trivial type-assignment canary proves typecheck RUNS, but the smoke run's
  real failures were unused-import (tkt 16) and null-narrowing (tkt 10) - caught only because `build`
  carried the strictness. A non-vacuous-but-lenient typecheck passes the canary and still misses them.
  So Fix A pushes the deriver to give the typecheck gate the build's strictness flags (the project's
  own strict tsconfig settings: `noUnusedLocals`, `strictNullChecks`, etc.), and to make the canary
  REPRESENTATIVE of the error class that slips (an unused import / a null-narrowing error), not just any
  type error. The value of "typecheck gates" is "typecheck catches the build's error class, cheaper" -
  not merely "typecheck is distinct from build."
- Stack notes (examples, not an exhaustive list): TypeScript with a project-references tsconfig
  (`files: []` + `references`) must use build mode (`tsc -b --noEmit`) so references are followed -
  bare `tsc --noEmit` checks nothing; dotnet should target the solution/correct project; Python `mypy`
  should target the package, not the repo root. Keep typecheck and build non-redundant: do not emit two
  gating checks that are byte-identical commands.

This is guidance to a non-deterministic worker - necessary but not sufficient; Fix B is the guarantee.
Confirm whether Scaffold templates are snapshot-tested (distinct from Briefs snapshots) and update any
snapshot deliberately.

### 3.3 Tests (Defect 1) - prove the mechanism is agnostic, not just TS

- Unit (agnostic core, no real toolchain): the prover against a FAKE check runner.
  - a check whose fake runner returns Passed regardless of files present -> prover reports VACUOUS.
  - a check whose fake runner returns Failed when the canary file exists -> prover reports OK.
  - assert the canary file is materialized before the run and deleted after, including when the runner
    throws; and that the worktree is asserted clean of the canary before proceeding (cleanup-failure ->
    hard-fail). This test has zero stack dependency and is the primary proof the mechanism is general.
- Unit (gate-path trigger): drive the gate with a gating check that returns GREEN -> prover fires once;
  a SECOND green of the same check in the run -> prover does NOT re-fire (per-check-once state); a RED
  gating check -> prover never fires (only green-on-real-code is the dangerous condition).
- Unit (test-gate canary): a test check whose runner reports green with zero tests collected -> the
  failing-test canary flips it red -> OK; a runner that ignores the canary and stays green -> VACUOUS.
- Unit (unverified signal): a gating check with no canary -> prover emits the `gate_unverified`
  event/smoke-signal and does not block.
- Unit (TS instance, optional live tool): a hermetic fixture with a project-references tsconfig:
  `tsc --noEmit` + referenced source -> VACUOUS; `tsc -b --noEmit` -> OK; and the representative
  strictness canary (unused import) trips only when the strict flag is on. Gate behind a trait if `tsc`
  resolution is unreliable in CI; the agnostic core test above is what must always run.
- Unit (second-stack smoke, recommended): the same prover against a trivial non-TS canary (e.g. a
  dotnet or shell check via a fake or a cheap real command) to demonstrate no TS assumption leaked.
- Regression (quiet-on-pass lock): assert `ReviewBriefBuilder.BuildAutomatedChecksSection` emits one
  status line and NO stdout/stderr for a passing check, and DOES emit the tails for a failing one.

### 3.4 Acceptance mapping (Defect 1)

- "an intentional type error trips the typecheck gate (not just build)" -> the generic prover fires on
  the typecheck check's first green at brief 01's gate; with a vacuous command it proves vacuous and
  hard-fails the chain, with `tsc -b --noEmit` + strict flags it confirms non-vacuous. Shown by the
  gate-path trigger test and the TS-instance fixture; the agnostic core test proves the mechanism is
  not TS-specific.
- "a passing typecheck emits one status line, no per-file enumeration" -> already true via 2.4; locked
  by the regression test.
- "typecheck and build are no longer redundant" -> Fix A: typecheck gets the build's strictness flags
  and catches the build's error class cheaper, plus an advisory when two gating checks are byte-
  identical commands. Do not hard-fail on redundancy - flag it.

---

## 4. Defect 2 - uncleaned worktrees poison post-run tests

### 4.1 Root cause

The chain leaves every per-ticket worktree under `.worktrees/` after success (chain ship sets
`SkipDecruft: true` and there is no end-of-chain sweep - see 2.3). A later root test run collects the
stale worktree copies and reports a false red (185 failed / 29 files in the smoke run). Per-ticket
verification ran inside each isolated worktree and was fine; the damage is only to anyone testing from
the main tree afterward. This false-red mechanism is stack-agnostic in principle (any test runner that
globs the tree can collect stale copies), though it bites glob-based runners hardest.

### 4.2 Design - success-gated sweep reusing WorktreeDecrufter (fully agnostic)

Add an end-of-chain cleanup that runs ONLY on chain success and removes this chain's worktrees with the
existing `WorktreeDecrufter.DecruftAsync`. This is git/filesystem only - no stack assumptions.

- Trigger point: the OUTERMOST chain success seam. The parent-chain path lands the root only when
  `ChainTargetBranch is null` (outermost) and no child stopped early (`LandRootIntegrationBranchAsync`,
  gated on `!anyStoppedEarly`, ~`ChainPhase.cs:2364-2392`, verify). The single-ticket path ends via
  `EmitChainEndAsync` (~`ChainPhase.cs:484`, verify). Add a `SweepChainWorktreesAsync` invoked at the
  outermost success seam, after a successful land/ship, before/at `EmitChainEndAsync`. Run it once per
  top-level invocation, not once per recursive child.
- What to remove: this chain's `ticket/{id}` per-ticket worktrees AND the `chain/{slug}` integration
  worktree(s). Identify them via `ListWorktreesAsync` filtered by Branch prefix (`ticket/`, `chain/`)
  intersected with the run's ticket tree, rather than a blind `rm -rf .worktrees/`. Call `DecruftAsync`
  per matching worktree. Branch-filtering is safer than directory nuking (avoids unrelated/concurrent
  worktrees); dispatch is serial today (`src/ThroughlineBuild.Phases/AGENTS.md`: concurrency pinned to
  1) but the filter is self-documenting regardless.
- Preserve on failure: gate the entire sweep on `IsChainSuccess(outcome)` for the outermost outcome. On
  any failure (ParentStoppedEarly / ReworkCapExceeded / ...), do nothing - worktrees survive for
  debugging, exactly as today. Add no decruft to any failure branch.
- Robustness: `DecruftAsync` no-ops on a worktree not in `git worktree list`, so a partially-cleaned
  tree is safe to sweep again. Aggregate the `DecruftResult`s; if any halted, emit one advisory - a
  cleanup miss must not fail an otherwise successful chain. After a clean sweep `.worktrees/` is empty.

### 4.3 Complementary guard - hermetic test command (agnostic, via derived data)

Defense in depth so a surviving worktree cannot produce a false red. The smoke-test fix was a vitest
`test.exclude`; do NOT hardcode vitest in engine code. Express the principle as derive-prompt guidance
so each stack emits it in its own idiom:

- In `derive-profile-prompt.md`: the test command must be HERMETIC - it must not collect the engine's
  working directories (`.worktrees/`, `.build/`) or nested dependency installs. Express the exclusion in
  the target runner's idiom: vitest `test.exclude: ['**/node_modules/**','**/.worktrees/**','**/.build/**']`,
  pytest `--ignore`/`norecursedirs`, jest `testPathIgnorePatterns`. dotnet `dotnet test` is
  project-scoped and already hermetic - no exclusion needed.

This keeps engine code stack-free; the sweep (4.2) is the primary, fully-agnostic fix and this is the
backstop for the residual case where a worktree survives by design (the failure path). If a future
experiment wants an engine-enforced backstop, the agnostic form is a generated `.gitignore`/ignore
marker or a documented contract - not a runner-specific emission in C#.

### 4.4 Tests (Defect 2)

- Unit: `SweepChainWorktreesAsync` with a fake `IGitClient` whose `ListWorktreesAsync` returns a mix of
  `ticket/*`, `chain/*`, and an unrelated worktree.
  - success outcome -> `DecruftAsync` called for each `ticket/*` and `chain/*` of this run, NOT the
    unrelated one; this run's entries are emptied.
  - failure outcome -> `DecruftAsync` never called (preserve-on-failure).
- Unit: sweep invoked exactly once for a multi-child parent chain (outermost only), not per child.
- Regression: a failing chain leaves worktrees in place (assert no decruft on the early-return paths).
- Reuse existing fakes/patterns from `tests/ThroughlineBuild.Helpers.Tests` (WorktreeDecrufter) and
  `tests/ThroughlineBuild.Phases.Tests` (ShipPhase SkipDecruft).

### 4.5 Acceptance mapping (Defect 2)

- "after a successful chain .worktrees/ is empty" -> `SweepChainWorktreesAsync` on the outermost
  success seam (agnostic, git-level).
- "npm test from main post-run matches a fresh clone" -> the sweep (primary) + hermetic-test guidance
  (defense in depth, expressed per stack).
- "worktrees survive on chain failure" -> the `IsChainSuccess` gate; no decruft on failure paths.

---

## 5. Gate-output convention (umbrella) - scope in this experiment

- Pass-side ("one status line, no enumeration"): already satisfied for any stack (2.4). Deliverable =
  verify + lock with the regression test in 3.3. No new machinery.
- Fail-side ("only failing items with file:line and message"): the current code emits the budgeted raw
  stdout/stderr tail of failed checks. A general extraction/summarization layer across toolchains is a
  real but separate effort - OUT of scope here (section 6). Building it now would also tempt stack-
  specific parsers, which the #1 goal disallows without a generic design.

---

## 6. Out of scope / non-goals (do not do these in experiment 1)

- A general gate-output error-extraction/summarizer across stacks. Separate experiment (and must itself
  be stack-agnostic).
- Running the type/build check inside the implement phase (cross-repo recommendation 2). Separate
  lever, separate experiment - it changes loop cost and would confound this one.
- The model-tier / size->model map question (recommendation 4). Separate.
- The declared-output existence verifier and the dead-test downgrade (recommendations 6, 7). Separate.
- Touching `ShipPhase` regression checks or the standalone `build review` path. The gate-integration
  inventory (`notes/gate-integration-inventory.md`) marks `ship.regression_checks` and the standalone
  review fallback runner OUT of scope; leave them alone.
- Weakening review or the rework loop. `chain-efficiency-evidence.md` is explicit: the loop is not
  broken and rework is not the cost driver. Do not touch `MaxReworkRounds`.
- Changing the prompt class (the survey op-doc) to make a fix pass. The op-doc is the control.
- Putting ANY stack-specific branch in engine C# (no `if (language == "typescript")`). Stack specifics
  live in derived data only.

EXPERIMENT-CONTROL WARNING (read before running): the vacuity must live in the INPUT, or Fix B has
nothing to catch. Fix A changes the derive prompt to bias the deriver toward a non-vacuous command, so
a "free deriver" against a clean op-doc is a WEAK control - once Fix A lands, a competent derive emits
the correct command and the defect never appears. Two consequences: (a) test Fix B in isolation with a
DETERMINISTIC vacuous fixture (a config whose typecheck is `tsc --noEmit` over a `files: []` +
references tsconfig with materialized source), not by hoping the deriver picks the bad command; (b) for
the full-chain run, use an input that actually yields a vacuous gating check. Note for this repo: the
canonical sample op-doc (`docs/analysis/workloads/survey-app-build.md`, brief 01) declares only
`build` + `test` with a strict tsconfig and NO explicit `typecheck` check - so a faithful run may emit
no typecheck gate at all, and the smoke run's vacuous typecheck came from the deriver inventing one or a
variant op-doc. The prover is general over GATING checks (not typecheck-specifically), so the control
just needs some vacuous gating check present; the deterministic fixture is the reliable way to get one.

---

## 7. Risks, gotchas, build discipline

- Stack-agnostic check (apply to every change): would this work for a dotnet, Python, or text-doc
  target? If a change needs to know the stack, push that knowledge into the derived profile / derive
  prompt, not into engine code.
- Briefs snapshots: if you touch `src/ThroughlineBuild.Briefs/Templates/*`, the Briefs snapshot tests
  fail until updated; edit as LF, run the Briefs tests, update snapshots deliberately. (Fix A edits a
  Scaffold template - confirm whether Scaffold templates are snapshot-tested.)
- AOT: `Cli` is `PublishAot=true`. Anything serialized (including a new `canary` field on the profile)
  needs a source-generated `JsonSerializerContext`. Keep `Contracts` I/O-free. The prover reads/writes
  files - put it in `Verification`/`Scaffold`/`Helpers`, not `Contracts`.
- Reuse, do not reinvent: `AutomatedChecksRunner` to run the canary check, `WorktreeDecrufter` for
  removal, `ListWorktreesAsync` for enumeration, `IsChainSuccess` for the success gate, the existing
  worktree-hygiene check for the post-canary clean-assert.
- Windows: paths and junctions matter; `WorktreeDecrufter`'s reparse-point pre-clean is the reason to
  reuse it. The canary file must be deleted in a `finally` AND the worktree asserted clean of it before
  the chain proceeds - the prover runs in a warm worktree the chain later ships from, so a leftover
  canary is a commit-poisoning risk, not just clutter.
- ASCII only; `topic: ...` commits; no AI branding; do not merge or push.
- Verify line numbers marked "(verify)" before editing - written against `d00a0cf`; `ChainPhase.cs` is
  large and churning.

---

## 8. Implementation order and commit plan (suggested)

Work on sub-branch `exp-1-gate-output` cut from `minbatch`. Suggested commits:

1. `contracts: add optional per-check canary to the profile/CheckSpec` (schema + JsonSerializerContext;
   parsing in `Config.cs` / `ConfigProfileWriter`).
2. `verification: add generic gate non-vacuity prover (canary-driven)` (Fix B core unit: materialize ->
   re-run check -> assert fail -> delete + clean-assert -> per-check-once; agnostic fake-runner tests).
3. `scaffold: guide deriver to non-vacuous + strict commands and per-check canaries` (Fix A derive-prompt
   edit + any snapshot update).
4. `gate: prove gating-check non-vacuity on first green; hard-fail + event on vacuous` (wire the prover
   into the gate path behind `verify_gate_vacuity`; emit `gate_unverified` / vacuous events).
5. `briefs: lock quiet-on-pass for automated checks with a regression test`.
6. `chain: sweep this chain's worktrees on success, preserve on failure` (Defect 2 sweep + tests).
7. `scaffold: guide deriver to emit a hermetic test command` (4.3 derive-prompt edit).

Each commit leaves `dotnet test` green. Keep 1-5 (Defect 1) and 6-7 (Defect 2) separable for clean
independent back-out.

---

## 9. How we measure this experiment

Against the fixed prompt class (the survey-app op-doc), using `build-run-analysis-prompt.md` for a
comparable report, contrasted with the baselines in `chain-efficiency-evidence.md`:

1. Prover works AND is agnostic (deterministic, no full run needed): the agnostic fake-runner test
   passes; against the deliberately-vacuous fixture (vacuous `tsc --noEmit` over `files: []` + refs with
   materialized source) the prover proves VACUOUS; with `tsc -b --noEmit` it proves OK. The prover also
   accepts a non-TS canary (second-stack test). Per-check-once and the canary clean-assert hold.
2. Vacuity is caught in a real chain on a vacuous INPUT (not the clean op-doc - see the control warning
   in section 6): run the chain against the deterministic vacuous fixture / a vacuity-injecting op-doc;
   the typecheck (or whichever gating check is vacuous) is proven vacuous at brief 01's gate and the
   chain hard-fails with an actionable event, instead of shipping a meaningless green. With the corrected
   input the same run proceeds and a deliberate type error in a referenced file trips the typecheck gate,
   not only the heavier build check.
3. Worktrees clean on success: after a successful chain `.worktrees/` is empty and a root test run
   matches a fresh clone (no false red); after an induced failure (including a vacuity hard-fail) the
   per-ticket worktrees survive for inspection.
4. No regression in the loop: rework count and completion rate stay within prior run-to-run variance;
   review unchanged. Cache/cost is a weak proxy - report but do not over-read it. Primary signals are
   1-3 (behavioral, deterministic).

State confounds honestly. The clean wins are 1-3.

---

## 10. File-by-file change checklist

| # | File | Change | Defect | Agnostic? |
|---|------|--------|--------|-----------|
| 1 | `src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs` (+ JsonSerializerContext) | Add optional `canary {path, content}` to the profile/CheckSpec model | 1B | data, yes |
| 2 | `src/ThroughlineBuild.Cli/Config.cs` + `ConfigProfileWriter.cs` | Parse/render the `canary` field; parse `verify_gate_vacuity` (gate/review config) | 1B | yes |
| 3 | new unit in `ThroughlineBuild.Verification` | Generic non-vacuity prover: materialize canary, re-run check via `AutomatedChecksRunner`, assert fail, `finally` delete + assert worktree clean, per-check-once | 1B | yes (no tool knowledge) |
| 4 | `src/ThroughlineBuild.Phases/GatePhase.cs` (+ `ChainPhase` rework-feed / event sink) | On each gating-check GREEN: invoke the prover; on vacuous hard-fail the chain + emit vacuous event; emit `gate_unverified` when no canary; behind the flag (default on) | 1B | yes |
| 5 | `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` | General non-vacuity rule + strictness rule + per-stack examples + emit per-check canary (incl. test) | 1A | data, yes |
| 6 | test: Verification + Phases tests | Agnostic fake-runner prover tests (vacuous/OK/cleanup-clean) + gate-path trigger (once, green-only) + test-gate canary + unverified-event + optional TS-instance + second-stack | 1B | yes |
| 7 | test: Briefs tests | Quiet-on-pass regression for `BuildAutomatedChecksSection` | conv | yes |
| 8 | `src/ThroughlineBuild.Phases/ChainPhase.cs` | `SweepChainWorktreesAsync` at outermost success seam; gate on `IsChainSuccess`; reuse `WorktreeDecrufter`; filter by `ticket/`+`chain/` | 2 | yes (git-level) |
| 9 | test: Phases tests | Sweep on success / preserve on failure / once-per-outermost | 2 | yes |
| 10 | `src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` | Hermetic-test rule (exclude engine dirs in the runner's idiom) | 2 | data, yes |

Note: the prover hooks the GATE path (row 4), not scaffold - the target source tree does not exist at
scaffold time (see 2.1 / 3.2). Items 2 and the exact seams in 4 and 8 are marked verify in sections
2-4; confirm by reading before editing, and report the exact gate hook point and config section chosen.
Every row keeps stack knowledge in data (rows 1,2,5,10) or in stack-free mechanism (rows 3,4,8); none
branches on language in C#.
