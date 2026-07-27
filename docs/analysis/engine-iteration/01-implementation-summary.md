# Implementation summary - experiment 1: gate-output convention (typecheck vacuity + worktree cleanup)

Branch: `exp-1-gate-output` (cut from `main` at `8b53486`, clean tree). Not merged, not pushed.
Implemented per `01-plan.md`; acceptance criteria from
`01-feedback-from-smoketest-8.md`. Standing protocol: `docs/analysis/method/experiment-harness-prompt.md`.

## Commits (oldest -> newest)

| Hash | Message |
|------|---------|
| 369b9d4 | contracts: add optional per-check canary to the profile and CheckSpec |
| b736f14 | verification: add generic gate non-vacuity prover (canary-driven) |
| 2f34de7 | scaffold: guide deriver to non-vacuous, strict checks with per-check canaries |
| da544ff | gate: prove gating-check non-vacuity on first green; hard-fail on vacuous |
| 9d7a3ef | briefs: lock quiet-on-pass for automated checks with a regression test |
| 1972324 | chain: sweep this chain's worktrees on success, preserve on failure |
| 8a90e5f | scaffold: guide deriver to emit a hermetic test command |

Defect 1 = commits 1-5; Defect 2 = commits 6-7. Kept separable for clean independent back-out,
per plan section 8.

## Files changed

### Production (src/) - 487 insertions, 18 deletions
- `ThroughlineBuild.Contracts/Verifier/CheckResult.cs` - new `CanaryFile(Path, Content)` record; optional `CheckSpec.Canary` (last param, default null). Contracts stays I/O-free (no JSON here).
- `ThroughlineBuild.Scaffold/ProjectProfile.cs` - `ProfileCheck.Canary`; `CanaryFileDto`; `ProfileCheckDto.Canary`; source-gen registrations; canary mapping in `ProjectProfileParser` (best-effort: skips blank-path entries, never throws).
- `ThroughlineBuild.Cli/ConfigProfileWriter.cs` - renders `canary = [{ path, content }, ...]` as a TOML inline-table array; new newline-safe `TomlBasicString` escaper (CR/LF/TAB) for canary content; `TomlString` left untouched so existing output is byte-identical.
- `ThroughlineBuild.Cli/Config.cs` - parses `canary` into `CheckSpec.Canary` for `[review].checks` and `[ship.regression_checks]`; `verify_gate_vacuity` (default true) added to `ReviewConfig`/`ReadReviewSection`; `canary` and `verify_gate_vacuity` added to the known-key sets.
- `ThroughlineBuild.Verification/GateVacuityProver.cs` (new, 224 lines) - the generic, stack-free prover.
- `ThroughlineBuild.Phases/GatePhase.cs` - `GateOutcome.Vacuous` flag; optional `GateVacuityProver`; on each green gating check, probe and map the verdict to events / hard-fail.
- `ThroughlineBuild.Phases/ChainPhase.cs` - `SweepChainWorktreesAsync` (internal); two outermost-success call sites; vacuity hard-fail branch (no rework) in the gate block.
- `ThroughlineBuild.Contracts/Models/ChainOutcome.cs` - appended `GateVacuous` (positions of existing values unchanged).
- `ThroughlineBuild.Cli/ChainExitCodeMapper.cs` - `GateVacuous => 8`.
- `ThroughlineBuild.Commands/ChainCommand.cs` - `FormatFinalLine` arm for `GateVacuous`.
- `ThroughlineBuild.Cli/Program.cs` - one shared `GateVacuityProver` per chain run (when the flag is on), captured by the gate-factory closure so per-check-once state persists across gate invocations.
- `ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md` - non-vacuity + strictness + per-check canary rules and a strict canary-bearing example (commit 3); hermetic-test-command rule (commit 7). ASCII, LF, placeholder and WORKER_RESULT envelope intact; example JSON validated.

### Tests (tests/) - 1122 insertions, 14 deletions
- `Verification.Tests/GateVacuityProverTests.cs` (new) - 8 stack-free facts: vacuous, ok (proves materialization), throw-still-cleans-up, cleanup-clean, cleanup-failed (git-hygiene override), unverified->already-proven, per-check-once (runner not re-run), second-stack (python-shaped canary).
- `Phases.Tests/GatePhaseTests.cs` - 7 facts: vacuous hard-fail + `gate_vacuous` event + no rework transition; ok pass; unverified advisory + `gate_unverified`; cleanup-failed; red check -> prover never runs; null prover regression; advisory check not probed.
- `Phases.Tests/ChainPhaseTests.cs` - vacuity routing (-> `GateVacuous`, no `ReworkRound`, no ship) + 7 sweep tests (filter ticket/chain not main/unrelated; advisory on halt; never-throws; success sweeps; failure preserves; leaf-child not swept). `FakeGitClientChain` extended additively; `FakeDecrufterChain` switched `new`->`override` (true no-op so the sweep is the only remover in tests).
- `Phases.Tests/SequentialChainTests.cs` - rewrote the one pre-existing test that asserted `RemoveWorktreeCallCount == 0` after a successful parent chain (that assertion encoded the exact defect-2 behavior being reversed); now asserts the sweep removes the 3 `.worktrees` entries and preserves main. Fake `ListWorktreesAsync` switched to a snapshot to match production semantics.
- `Cli.Tests/ConfigProfileWriterTests.cs` - canary survives render -> real Config load with content carrying a quote AND a newline (load-bearing escaping test).
- `Cli.Tests/ConfigLoaderTests.cs` - `verify_gate_vacuity` defaults true (absent / present), parses false, not warned as unknown.
- `Cli.Tests/ChainExitCodeMapperTests.cs` - `GateVacuous => 8`.
- `Scaffold.Tests/ProjectProfileParserTests.cs` - canary parses (path/content); absent -> null. AOT reflection switch honored.
- `Briefs.Tests/ReviewBriefBuilderTests.cs` - quiet-on-pass / loud-on-fail lock for `ReviewBriefBuilder` and `BatchReviewBriefBuilder` (sentinel-based; per-check, not all-or-nothing).
- `Contracts.Tests/EnumExhaustivenessTests.cs` - added `GateVacuous` to the pinned `ChainOutcome` set (required to keep the exhaustiveness guard green).

## Test result

`dotnet test --nologo -v q` from repo root: **2124 passed, 0 failed, 0 skipped** across all 19 projects.
Baseline (`main`) was fully green before any change; ~36 net-new tests added.

No Briefs template snapshots were touched (no `src/ThroughlineBuild.Briefs/Templates/*` change), so no
snapshot updates were needed. The derive prompt is an embedded resource, not snapshot-pinned; the
Scaffold deriver test (which asserts op-doc substitution survives) stays green.

## Acceptance mapping

### Defect 1 - typecheck vacuity / gate-output convention

- "an intentional type error trips the typecheck gate (not just build)" -> SATISFIED (deterministic mechanism). The generic `GateVacuityProver` fires at a gating check's first GREEN in the gate path (`GatePhase`): it materializes the check's declared canary (a deliberate error), re-runs only that check via the existing `AutomatedChecksRunner`, and asserts it now fails. A vacuous configured typecheck (e.g. `tsc --noEmit` over a `files: []` references tsconfig) does not reject the canary -> verdict `Vacuous` -> the chain hard-fails (`ChainOutcome.GateVacuous`) with a `gate_vacuous` event instead of shipping a meaningless green. Fix A (derive prompt) biases the deriver to `tsc -b --noEmit` + the project's strict flags + a REPRESENTATIVE canary (unused import / null-narrowing) so the typecheck gate catches the build's error class. Proven by `GateVacuityProverTests` (vacuous/ok/materialize), `GatePhaseTests` (gate-path trigger, green-only, once), and `ChainPhaseTests` (vacuity -> `GateVacuous`, no rework, no ship). The agnostic fake-runner core test and the python-shaped second-stack test prove the mechanism is not TS-specific. The end-to-end live-chain proof against a deliberately-vacuous fixture is the experiment RUN's job (plan section 9.2), not a unit test.
- "a passing typecheck emits one status line, no per-file enumeration" -> SATISFIED + LOCKED. Already true via `ReviewBriefBuilder.BuildAutomatedChecksSection` (emits `- name: PASS (exit, elapsed)` and appends stdout/stderr only when `!Passed`); commit 5 pins it with regression tests for both the single and batch review brief builders.
- "typecheck and build are no longer redundant" -> SATISFIED BY GUIDANCE (best-effort, not a deterministic guarantee, exactly as the plan scopes it). The derive prompt now tells the deriver to give the typecheck check the build's strictness flags (so it catches the build's error class cheaper) and to never emit two byte-identical gating commands. This is guidance to a non-deterministic worker; the engine does not hard-fail on redundancy. Honestly flagged as guidance, per plan 3.4.

### Defect 2 - uncleaned worktrees poison post-run tests

- "after a successful chain .worktrees/ is empty" -> SATISFIED. `SweepChainWorktreesAsync` runs at the outermost success seam (single-ticket `Completed` and parent `ParentCompleted`, both gated on `ChainTargetBranch is null`), reusing the stack-agnostic `WorktreeDecrufter` over every worktree whose branch starts `ticket/` or `chain/`. Proven by `SequentialChainTests` (parent chain sweeps its 3 worktrees) and `ChainPhaseTests` (filter correctness; success sweeps).
- "npm test from main post-run matches a fresh clone" -> SATISFIED (primary + defense in depth). The sweep removes the stale worktrees so a glob-based runner cannot collect them; commit 7 adds hermetic-test-command guidance (deriver emits the runner's own exclusion idiom: vitest `test.exclude`, pytest `--ignore`, jest `testPathIgnorePatterns`; `dotnet test` already hermetic) as the backstop for the residual case where a worktree survives by design (the failure path).
- "worktrees survive on chain failure" -> SATISFIED. The sweep is gated on `IsChainSuccess` / outermost; no failure return path sweeps. Proven by `RunAsync_OutermostFailure_PreservesWorktrees_NoSweep`. A vacuity hard-fail (`GateVacuous`) is a chain FAILURE, so it likewise leaves the worktrees in place for inspection (consistent with Defect 1's intent).

## Mechanical decisions and deviations (plan left these to the implementer)

- New `ChainOutcome.GateVacuous` (exit code 8) gives the vacuity hard-fail a distinct, countable outcome. Appended to the enum (existing positions unchanged); `ChainExitCodeMapper`, `ChainCommand.FormatFinalLine`, and the `EnumExhaustivenessTests` guard updated accordingly. The success/skip predicates are explicit allow-lists, so the new value is treated as a failure automatically.
- Events reuse `EventKind.GateFailure` with `kind` discriminators - the established pattern in this codebase (e.g. `chain_landing_wrong_branch`). New kinds: `gate_vacuous` and `gate_canary_cleanup_failed` (hard-fail), `gate_unverified` (advisory, non-blocking), `worktree_sweep_incomplete` (advisory). All countable from `.build/events/*.jsonl`. No new `EventKind` enum value (it is int-serialized by position).
- A prover `CleanupFailed` verdict (canary file leaked) is routed through the SAME hard-fail path as `Vacuous`: a leftover canary in the warm worktree the chain ships from is a commit-poisoning risk, so stopping is correct.
- The prover is a single shared instance per chain run (constructed in `Program.cs`, captured by the gate-factory closure) so its per-check-once set persists across every gate invocation - each gating check is probed at most once per chain.
- `verify_gate_vacuity` lives in `[review]` (default ON). Disabling it passes a null prover, restoring exact pre-change gate behavior (covered by a regression test).
- Two pre-existing tests were adapted (not weakened): the `SequentialChainTests` parent-chain test that asserted zero removals after success (that WAS the defect), and minor test-fake `ListWorktreesAsync` snapshots to match production (`ProcessGitClient.ListWorktreesAsync` re-parses a fresh list each call - verified - so the production sweep has no concurrent-modification risk).

## Out of scope (respected, per plan section 6)

No general gate-output error-extraction/summarizer; no check inside the implement phase; no model-tier
map; no declared-output verifier; `ShipPhase` regression checks and the standalone review path
untouched; `MaxReworkRounds` untouched; the survey op-doc (the control) unchanged; and - the #1 goal -
no stack-specific branch in engine C# (audited: `git diff main..HEAD -- 'src/*.cs'` has no
`language ==`/`if (language ...)`/tool-name comparison; all stack specifics live in derived data and
the derive prompt).

## Recommendation

FOLD candidate - but the decision belongs to the experiment RUN, not to this implementation. The
deterministic mechanism and the engine behavior are fully proven by the unit suite and the whole
suite is green; what remains is the live measurement against the fixed prompt class.

To decide FOLD vs ABANDON, run plan section 9:
1. Mechanism + agnostic (done here, deterministic): prover unit tests pass; vacuous fixture proves
   VACUOUS, `tsc -b --noEmit` + strict flags prove OK; second-stack canary accepted; per-check-once
   and the canary clean-assert hold. (Section 9.1.)
2. Vacuity caught in a real chain on a vacuity-injecting INPUT (NOT the clean op-doc - see the
   control warning in plan section 6): the gating check is proven vacuous at brief 01's gate and the
   chain hard-fails with a `gate_vacuous` event instead of shipping a green; with the corrected input
   a deliberate type error in a referenced file trips the typecheck gate, not only the heavier build.
   (Section 9.2.)
3. Worktrees: after a successful chain `.worktrees/` is empty and a root test run matches a fresh
   clone (no false red); after an induced failure (including a vacuity hard-fail) the worktrees
   survive. (Section 9.3.)
4. No loop regression: rework count / completion rate within prior run-to-run variance; review
   unchanged; cache/cost reported as a weak proxy only. (Section 9.4.)

Branch left intact at `8a90e5f`. No merge, no push, no branch deletion - humans decide.
