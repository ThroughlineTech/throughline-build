# Operation: chain-run-defenses-and-output

Fix three bugs surfaced during the first parent-chain-children run (`build chain 348`) and clean up chain output so parallel ticket execution is line-by-line readable. The three bugs: implement and review can leave dirty worktrees that cascade into late ship-gate failures; ship's regression checks fail on pre-existing test failures rather than only on regressions the branch introduces; the main worktree ends detached at the remote ref after parallel ship operations. The output fix tags every chain-output line with its ticket ID so operators can distinguish parallel children at a glance.

## Why this exists

`build chain 348` was the first real parent-chain-children execution and surfaced three orthogonal bugs that all hit ship after a clean implement+review pass. Each bug is small in isolation but together they make chained ship runs flaky in ways that are hard to triage from current output. The dirty-worktree case cascaded into a ship-pre-flight failure that referenced files no agent had touched (review's build artifacts). The regression-check failure happened because four snapshot tests were already failing on main from a template migration that never regenerated snapshots, and ship ran every test unconditionally with no awareness of the baseline state. The detached-HEAD case left the main worktree in a confusing state after every chain run, which is fixable but needs a small audit to find the actual culprit.

The output problem compounded the diagnostic difficulty. The sample chain output mixed parent-tagged lines (`[348]`), wall-clock-only lines (`[0:02]`), and untagged worker session output streaming from two parallel children. With no ticket attribution on the worker stream, an operator looking at the output cannot tell which child ticket produced which session event. Fixing that is a single change at the TLB output-re-emission layer.

The four briefs are independent of each other - they share no code paths beyond all being chain-related - so they can land in any order. The op-doc bundles them because they all came out of the same chain-348 run and form one coherent "make chained execution operator-debuggable" effort.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Chain-run defenses and output legibility | - | M |

Single plan; four independent briefs that can land in any order.

## Plan A: Chain-run defenses and output legibility

### Goal

After this plan, implement and review do not leave dirty worktrees that surprise ship; ship's regression checks only block on new failures the branch introduces; the main worktree always ends on its local target_branch after a chain run; and every line of chain output is attributed to a ticket. The bug profile from chain-348 is fully closed.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | post-phase-worktree-validation | Implement and review validate worktree cleanliness on completion; implement retries once, review hard-fails | - | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs, src/ThroughlineBuild.Git/ (shared status helper), tests |
| 02 | ship-baseline-aware-regression | Baseline regression tests against origin/target_branch SHA; only fail ship on new failures; --skip-baseline opt-out | - | src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Git/ (baseline worktree), tests |
| 03 | main-worktree-detached-head-fix | Audit and fix the code path leaving main worktree detached at origin after parallel chain runs | - | src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Git/ (fetch and merge paths), tests |
| 04 | chain-output-ticket-attribution | Every chain output line carries its ticket ID; worker session output from child agents is tagged at TLB's re-emission layer | - | src/ThroughlineBuild.Cli/ (output layer), src/ThroughlineBuild.Phases/ChainPhase.cs, tests |

### Briefs - detail

#### Brief 01: post-phase-worktree-validation

Goal: Implement and review validate that the worktree is clean before declaring success. Implement gets one retry with the agent (a confused agent can be guided to commit pending changes); review hard-fails if it leaves the worktree dirty because review's job is to verify, not to commit code. After this brief, a dirty worktree surfaces immediately at the phase boundary that caused it rather than cascading into a confusing ship-pre-flight failure.

Inputs: current ImplementPhase and ReviewPhase exit paths; the existing IGitClient or equivalent for running git status checks in a worktree; the existing chain orchestrator's failure-handling and retry-dispatch code; the event log for recording phase-failure events.

Outputs:
- A shared git-status-clean check helper used by both ImplementPhase and ReviewPhase, returning either "clean" or a list of dirty paths.
- ImplementPhase, after the worker agent exits with Ok, runs the cleanliness check. If clean, proceeds as today. If dirty, emits a structured failure carrying the list of dirty paths; the chain orchestrator retries the implement worker once with a system-prompt addition along the lines of "the implementation left the worktree dirty - the following files need to be committed: <list>." After the retry, the cleanliness check runs again; if clean, proceeds; if still dirty, hard-fail with the dirty-paths list in the failure reason.
- ReviewPhase, after the worker agent exits with Ok, runs the same cleanliness check. If clean, proceeds as today. If dirty, hard-fail immediately with the dirty-paths list; no retry.
- Both validations are recorded in the event log so operators can audit retry attempts and hard-fails after the fact.
- Failure messages name the phase, the dirty-file count, and a sample of the dirty paths (truncated if many) so operator triage is fast.

Acceptance:
- [ ] An implement phase whose worker exits Ok with a dirty worktree triggers exactly one retry attempt
- [ ] A successful retry (worktree clean after second attempt) lets implement complete with Ok
- [ ] A failed retry (still dirty after second attempt) produces a phase failure with the dirty-paths list in the failure reason
- [ ] A review phase whose worker exits Ok with a dirty worktree produces a phase failure with the dirty-paths list and does not retry
- [ ] Phase failures from this validation are recorded in the event log
- [ ] Both phases proceed as today when the worktree is clean after the worker exits
- [ ] AOT publish succeeds

Notes: The case observed during chain-348 was review's `dotnet build/publish` leaving tracked-file modifications - a one-time observation, not a confirmed reproducer. This brief is defense-in-depth that catches the case loudly at the right phase boundary; the upstream fix (review either gitignoring its build artifacts or cleaning up after itself) is a follow-up ticket if the dirty-after-review case recurs in practice. The implement retry pattern is intentionally bounded - one retry then hard-fail - because tokens are expensive and a confused agent rarely gets clearer with more attempts. The shared status helper avoids duplicating the check across phases and gives a single place to evolve the cleanliness contract later.

OOS:
- Fixing the underlying cause of review's build dirt (separate follow-up ticket if the case recurs)
- Validating worktree cleanliness in plan, decompose, or ship phases (those phases do not normally write to the worktree; separate decision needed if validation is desired there)
- Restructuring the IWorkerAgent contract to include cleanliness as part of WORKER_RESULT
- Auto-committing dirty files on behalf of the agent

#### Brief 02: ship-baseline-aware-regression-checks

Goal: Ship's regression checks compare current-branch test results against a baseline computed from origin/target_branch's current SHA. Only tests that pass on baseline and fail on the current branch cause ship to fail. Pre-existing failures (failing on baseline and on branch) are reported but do not block ship. The baseline is computed once per chain invocation and cached by origin/target_branch SHA.

Inputs: current ship regression-check code (the `[ship.regression_checks]` config and the test-runner invocation); the chain runner (the right scope for the baseline cache because it spans multiple ship invocations within one operator action); target_branch from the configurable-target-branch op-doc (baseline runs against origin/target_branch SHA, not hardcoded origin/main); the existing worktree management primitives for creating temporary worktrees; the event log for recording baseline events.

Outputs:
- A baseline test-results cache scoped to the chain invocation, keyed by origin/target_branch SHA. First ship in the chain computes the baseline; subsequent ships in the same chain reuse it as long as the SHA matches. New chain invocation = fresh baseline.
- A baseline worktree mechanism: a temporary worktree at `.worktrees/baseline-<short_sha>` is created at origin/target_branch's current SHA, the regression-check commands run there, the failing-test set is captured, then the baseline worktree is decrufted via the existing decrufter.
- When ship's regression checks run on the feature worktree, results are compared to the baseline:
  - Tests passing on baseline and failing on branch = regressions, ship fails
  - Tests failing on baseline and failing on branch = pre-existing failures, ship proceeds with a clear "noted, pre-existing" entry in output
  - Tests failing on baseline and passing on branch = fixes, ship proceeds with an informational note
  - Tests passing on baseline and passing on branch = no output
- A `--skip-baseline` flag on `build ship` and `build chain` that bypasses baseline computation entirely; ship falls back to today's behavior (any failing test fails ship). Useful when an operator is confident origin is clean.
- Event log entries: baseline-computed (with SHA and failing-test count), regressions-detected (with count and sample test names), fixes-detected, pre-existing-failures-noted, baseline-skipped.
- Operator output clearly distinguishes the four categories so triage is fast.

Acceptance:
- [ ] The first ship invocation in a chain computes a baseline against origin/target_branch's current SHA
- [ ] Subsequent ship invocations in the same chain reuse the cached baseline when the SHA matches
- [ ] A change in origin/target_branch SHA mid-chain invalidates the cache and triggers a fresh baseline computation
- [ ] Ship fails when the feature branch introduces a test failure that was passing on baseline
- [ ] Ship proceeds when a failing test on the feature branch was already failing on baseline; the failure is reported as pre-existing
- [ ] A test that fails on baseline and passes on branch is reported as a fix; ship proceeds
- [ ] `--skip-baseline` on `build ship` and `build chain` bypasses baseline computation and applies the legacy behavior (any failing test fails ship)
- [ ] Baseline events appear in the event log with the SHA and the failing-test count
- [ ] AOT publish succeeds; baseline-result DTOs registered in source-gen contexts

Notes: The baseline run is a real time cost. On a substantial test suite this adds minutes per chain invocation, mitigated by the per-chain cache that pays once and reuses across the chain's ship invocations. The cache deliberately does not persist across separate chain invocations because origin/target_branch can move between them and stale baseline data is worse than no baseline data - a fresh baseline at chain start is the cleanest contract. Operators with confidence in origin's cleanliness skip the entire mechanism via `--skip-baseline`. The baseline worktree's naming (`baseline-<short_sha>`) is deliberate to avoid collision with ticket worktrees and to make decruft straightforward. Test-result identity is by test name; flake detection (tests that flip state between runs on the same SHA) is out of scope and reported as state changes if it occurs.

OOS:
- Cross-chain persistent baseline caching (per-chain scope only; persistence opens stale-cache risks)
- Flake detection for tests that flip state between baseline and feature runs
- Skipping or rerunning specific tests as a triage tool (operators use the test runner directly for that)
- Baselining build output or other non-test checks beyond regression-checks scope

#### Brief 03: main-worktree-detached-head-fix

Goal: After a chain invocation involving parallel children, the main worktree's HEAD is on the local target_branch, not detached at a remote ref. The fix audits the fetch and merge paths in ship for any operation that detaches HEAD, replaces those operations with equivalents that keep HEAD on the local branch, and adds a post-merge assertion that fails loudly if HEAD ever ends up detached after a ship operation completes.

Inputs: current ShipPhase.cs, specifically the fetch step (around line 243) and the ff-merge step (around line 312); BaseRefResolver and any other code that touches main-worktree HEAD; the configurable-target-branch op-doc's changes to ship destination; the MainWorktreeLock primitive and how it scopes main-worktree operations; the observed bug from chain-348 (main worktree ends `HEAD detached at origin/main`).

Outputs:
- An audit summary in the brief's implementation documenting which code paths were checked and what was found: the fetch step, the ff-merge step, BaseRefResolver's interaction with the main worktree, and any other code that runs `git checkout` against the main worktree. The audit's findings are recorded in the PR or commit message so future readers can see which paths were considered.
- Any operation in those paths that detaches HEAD (a direct checkout of a remote ref like `origin/<branch>`, or any other operation that leaves HEAD off the local branch) is replaced with an operation that keeps HEAD on the local target_branch: fetch + ff-merge against the local branch, equivalent constructions that do not detach.
- A post-condition assertion in ShipPhase: after the ff-merge step completes, verify HEAD is on the local target_branch. If it is detached or on the wrong branch, emit a ship failure with a clear message identifying the unexpected HEAD state.
- The fix is target-branch-aware: works for `feature/x` and for `main` identically.
- A regression test exercising the chain-348 scenario: a parent ticket with two parallel children, both shipping concurrently against the same main worktree. The test asserts the main worktree's HEAD is on local target_branch at end of chain.

Acceptance:
- [ ] After a chain invocation involving parallel children, the main worktree's HEAD is on local target_branch
- [ ] No code path in ShipPhase leaves the main worktree's HEAD detached at a remote ref
- [ ] After every ff-merge in ship, a post-condition assertion verifies HEAD is on the local branch; a failure of this assertion produces a clear ship failure
- [ ] The fix applies correctly when target_branch is `main` and when target_branch is a feature branch
- [ ] A regression test reproduces the parallel-chain scenario and verifies the main-worktree end state
- [ ] AOT publish succeeds

Notes: The audit step is part of the brief rather than a separate prep effort. The implementer reads the relevant code paths (fetch, ff-merge, BaseRefResolver's main-worktree interactions, anywhere else `git checkout` appears in main-worktree context), documents what was found, and fixes whichever paths were leaving HEAD detached. The fix may be a single line in one place or several lines across multiple paths - the audit determines the scope before the fix lands. The post-condition assertion is the safety net that converts any future regression of this behavior from "operator confused after chain run" into "ship phase fails loudly with a clear message." The chain-348 reproducer test pins the parallel-ship case so this exact scenario does not regress.

OOS:
- Restructuring the MainWorktreeLock or its acquisition pattern (the lock is correct; only operations inside it were buggy)
- Changing the fetch protocol or remote configuration
- Adding a recovery command to repair a detached main worktree post-hoc (separate concern if ever needed)
- Auditing detached-HEAD scenarios in feature worktrees (this brief is main-worktree-specific)

#### Brief 04: chain-output-ticket-attribution

Goal: Every line of chain output is prefixed with its ticket ID. Parent-level lines (chain starting, chain Ok) use the parent ticket's ID; child-level lines (worker session output streaming from a child agent) use the child ticket's ID. After this brief, a parallel chain run is readable line-by-line: an operator scanning the output can immediately tell which ticket each line belongs to.

Inputs: current chain orchestrator output (the `[<ticket>]` prefix on parent-level lines and the worker-session stream that currently has no ticket attribution); the worker session output re-emission code path (where TLB consumes the worker's stdout/JSON stream and re-emits to operator); the chain runner's knowledge of which child ticket is dispatched to which worker session.

Outputs:
- The chain output layer prefixes every line with the relevant ticket ID using the bracket format `[<ticket-id>]`.
- Worker session output streaming from a child ticket's agent is tagged with that child's ticket ID at the TLB re-emission layer. TLB controls the consumption of the worker's stdout/JSON, so it knows which child invocation the stream belongs to.
- Parent-level chain output lines (chain start, chain completion, chain stop) continue to be prefixed with the parent ticket ID, format unchanged.
- When a wall-clock time prefix is also present (e.g. `[0:02]`), it appears consistently relative to the ticket prefix - format: `[<ticket-id>] [<time>] <content>`.
- Single-ticket (non-chain) invocations continue to display with their ticket ID prefix exactly as today.
- The format is applied uniformly across all output paths, not only in chain mode.

Acceptance:
- [ ] Every line of chain output begins with a `[<ticket-id>]` prefix
- [ ] Worker session output from a child agent is tagged with that child's ticket ID, not with the parent's ID and not without a tag
- [ ] Parallel child tickets in a chain are line-by-line distinguishable in the output stream
- [ ] When a wall-clock time prefix is present, it appears consistently positioned relative to the ticket prefix
- [ ] Single-ticket invocations display with their ticket prefix unchanged from today
- [ ] AOT publish succeeds

Notes: Worker session output comes from the agent's stdout (claude-code's stream-json events, codex's `--json` output, etc.). TLB consumes this stream and re-emits to the operator, which is the right place to tag with ticket ID - TLB knows which child invocation the stream belongs to. Tagging inside the agent's own output via a system-prompt addition would be unreliable across the four-agent surface and would not work cleanly for agents whose output formats are tightly fixed. The format choice of bare `[<ticket-id>]` without parent context comes from the design call: chain-start announces the parent context, so subsequent lines stay short and readable. Operators wanting parent context can scroll up. Color-coding per ticket would also help but is a separate UX improvement, not in this brief.

OOS:
- Color-coding output per ticket (separate UX improvement)
- Restructuring the output format beyond the prefix change (JSON output, structured streaming, etc.)
- Adding agent-name attribution to output (`[<ticket-id> <agent>]` or similar)
- Suppressing or filtering output based on ticket or phase

## What done looks like

A chain invocation involving parallel children produces output where every line is attributed to its ticket - parent for parent-level lines, child for child-level lines including the worker session stream. The three chain-348 bugs are closed: implement and review validate worktree cleanliness on completion (implement retries once and then hard-fails; review hard-fails immediately) so a dirty worktree surfaces at the phase that caused it rather than cascading into a confusing ship-pre-flight failure; ship's regression checks compare against a baseline of origin/target_branch and only block on regressions the branch introduces, with pre-existing failures reported but not blocking; the main worktree always ends on its local target_branch after a chain run, with a post-merge assertion that fails loudly if HEAD ever ends up detached. The chain-348 reproducer test pins all three bug fixes against regression, and the line-by-line ticket attribution makes future chain runs operator-debuggable on first read.