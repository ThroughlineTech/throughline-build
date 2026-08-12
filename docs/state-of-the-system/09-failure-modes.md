# 09 - Failure Modes and Idempotency

Last refreshed: 2026-08-11 (HEAD 758ad56a)

For each major operation, how it fails and whether re-running is safe. Exit codes summarized in [06-public-surfaces.md](06-public-surfaces.md); chain/dispatch outcomes in [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

The existing phase classification remains, while the new conductor/install layer adds fail-closed containment, manifest, canary, read-back, and staged-handoff boundaries. These commands generally make no remote mutation; the exceptions are worker brief reads and evidence comment creation.

---

## Failure-mode summary table

| Operation | Pre-flight gates | Common failures | Failed-at state | Idempotent on rerun? |
|---|---|---|---|---|
| `plan` (promote mode, default) | ticket exists; not a parent; state == `Backlog`; base ref resolves | ticketing write failure | partial transitions possible | rerun refuses once out of `Backlog` |
| `plan` (`mode = "investigate"`) | same | worker non-Ok; unresolvable `plan_body_ref`; missing scalar metadata keys | `Planning` once the worker has run (transition lands after the worker, before the status check) | partial - rerun fails the `Backlog` guard; operator must reset state |
| `implement` | ticket exists; not a parent; state == `Ready` (initial) or `InProgress` (rework); hygiene gate; worktree/branch create succeeds | hygiene block; worktree creation fails; worker non-Ok; missing `commit_sha`; dirty worktree after exit (one bounded retry) | `Ready` if pre-worker; `InProgress` if worker ran but did not deliver | yes - rerun resumes via the rework path; a missing rework worktree is recreated from the branch |
| `gate` | standalone: configured checks and persisted canary policy; chain-composed: after implement, before review, with completion-claim context | setup-check failure; gating-check failure; invalid completion claim in the chain path; vacuous gating check; environment failure on base ref | chain gating fail -> `InProgress` (rework); vacuity/environment fail -> stays `InReview`; standalone reports an exit result without lifecycle transition | yes - checks re-run; chain vacuity probes are once-per-check-per-run |
| `review` | ticket exists; state == `InReview`; worktree locatable (reconstructed from branch if missing); `[implemented_at]` marker | provider quota/auth block (-> `ReviewUnavailable`, no verdict posted); verifier crash; missing verdict metadata; dirty worktree after verifier (hard fail) | state unchanged (only `Rework` changes it) | yes - rerun re-runs checks and verifier |
| `ship` | state == `InReview`; worktree locatable; exe not inside it; both worktrees clean; main worktree on target; bases reconcilable; rebase, marker scan, regression checks, FF merge, push | per stage via `ShipFailureStage` | enum value identifies stage | partially - rebase + checks idempotent; post-merge transitions not retried |
| `decompose` | ticket exists; base ref resolves | worker non-Ok; malformed / <2 `child_specs`; `DecomposeVerdict` quality gate; all child creates fail | no parent transition | no - rerun duplicates child sub-issues |
| `chain` | non-terminal state (reconciled and resumed); main worktree on target branch; clean tree | inner-phase failures as `StoppedAt*`; rework cap; vacuous gate; environment failure; ticketing unreachable | `ChainOutcome` identifies the stop | yes - resumes from landed state; integration branch refreshed on reuse |
| `chain` (multi-ticket / parent) | dependency graph acyclic; tree at most one level deep | cycle; a child fails; environmental stop skips remaining siblings | `ParallelDispatchResult` / parent outcomes | yes - completed tickets skipped by state |
| `rework` | state == `InProgress`; `--feedback` or a `Rework` verdict in the log | feedback retrieval fails; implement fails | `ImplementFailed` / `NoFeedbackAvailable` / `TicketNotInProgress` | yes |
| `new` / `scaffold` / `amend` / `close` / `defer` / `reopen` | unchanged shapes | unchanged | unchanged | `scaffold` still duplicates on rerun |
| `sweep` (NEW, TLB-531) | config resolves a target branch | a worktree removal halts mid-ladder | nothing transitioned; partial removal possible | yes - merged-gated; safe to re-run |
| `profile apply` / `verify-canaries` | valid profile JSON; at least one gating check; temp worktree/install/checks succeed | missing/ineffective canary, dirty/failed setup, existing differing config | config unchanged until proof; atomic write after proof | yes; identical apply is no-op |
| `conductor apply` / SOP lifecycle | repository root and target containment; no symlink/reparse traversal; valid schema/hashes | malformed invariants, local stub edits, missing trusted history, doctor finding | temp files/cache or hash-matching targets only | yes; install restores missing files, status/doctor read-only |
| `worktree lease/list/teardown` | git roots, contained path/branch, optional allowlisted seed, manifest identity | collision, failed install, dirty tree, unmerged branch, invalid manifest | partial lease is reported/manifested; no ticket state | yes after fixing cause; force teardown explicitly discards dirty worktree content |
| `gate` (standalone) / `waves` / `candidate status` | standalone config slice or valid input/base | zero checks with `--require-checks`, unverified canaries, cycle, invalid path/hash input | no remote state | yes; read/execute only |
| `evidence add` | kind-specific fields and Plane ticket | post succeeds but read-back fails | comment may exist; id is reported | inspect comments before retry; blind retry can duplicate |
| `attachments` | valid ticket id and authenticated Plane read | project/ticket unavailable; comment-style page cap does not apply; malformed inline asset is skipped | no local or remote mutation | yes; read-only |
| `attachment` | valid ticket, asset currently belongs to it, absent explicit output path | unknown asset, storage redirect/HTTP failure, output already exists, write/move failure | same-directory temporary file is removed best-effort; final path is either absent or complete | yes after fixing cause; existing final file is never overwritten |
| `install` | correct stage input and readiness prerequisites | profile/SOP failure rolls local edits back; final readiness finding stops | explicit handoff or rollback; READY only after all probes | yes; stages are designed to be re-run |

### Loose ends

- The investigate-mode `plan` idempotency caveat stands: the `Planning` transition lands after the worker but before the status check ([src/ThroughlineBuild.Phases/PlanPhase.cs:124-134](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L124-L134)), so a failed plan parks the ticket in `Planning`. The chain reconciles this (`Planning -> Backlog` reset); the standalone verb does not.

---

## Per-phase failure detail

### `plan` (`PlanPhase`)

- **Parent ticket:** "is a parent ticket with N children: ... plan each child individually" ([src/ThroughlineBuild.Phases/PlanPhase.cs:81-84](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L81-L84)).
- **Wrong state:** "ticket not in Backlog state" (:86-87).
- **Base-ref resolution failure:** "git rev-parse failed: ..." (:88-97); resolution now goes through `BaseRefResolver` with the configured target, not a literal `main`.
- **Promote mode (the default inside chain, TLB-495):** `_options.PromotePlan` short-circuits to `RunPromoteAsync` (:99-100, :224-250) - no worker at all; labels + `[planned_at: <base-sha>]` + `Backlog -> Planning -> Ready`. Failure modes reduce to ticketing-write failures. `[plan] mode` accepts only `"promote"` or `"investigate"` ([src/ThroughlineBuild.Cli/Config.cs:828-840](../../src/ThroughlineBuild.Cli/Config.cs#L828-L840)). Standalone `build plan` investigates regardless of that setting unless `--from-brief` is passed.
- **Worker failure (investigate mode):** non-Ok returns the envelope reason after the `Planning` transition (:131-134); `Escalate` is carried back as `EscalationWorkerResult` for chain ratification.
- **Unresolvable plan body / missing metadata:** `plan_body_ref` -> `PLAN_BODY` fenced block (:145-148); scalar keys `risk_label` / `size_label` / `planned_at_sha` still required (:152-155).

### `implement` (`ImplementPhase`)

- **Parent ticket / wrong state:** refusals write `phase-status.json` via `EarlyExitManifest` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:113-123](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L113-L123)); state-specific guidance via `InitialRoundStateGuidance`; a rework round against a non-InProgress ticket returns "rework round invoked but ticket is in X" (:128).
- **Hygiene gate:** conflicted paths or unrelated stash entries refuse with `GateFailure` kind `hygiene_gate` (:139-142).
- **Worktree creation fails (initial):** "worktree create failed: ..." or "branch create in shared worktree failed: ..." on the chain integration-worktree path (:283-306).
- **Missing rework worktree (CHANGED):** rework now attempts to *recreate* the worktree from the ticket branch; only when recreation also fails does it hard-exit with "rework expected existing worktree ... could not recreate it from branch ..." (:254).
- **Worker fails after worktree created:** ticket stays `InProgress`; rerun resumes via rework. `Escalate` carried back as `EscalationWorkerResult` (:420, :457).
- **Missing `commit_sha`:** "worker metadata missing commit_sha" (:478). A `commit_sha` mismatch with actual HEAD is informational - actual HEAD wins, discrepancy folded into the `implemented_at` comment.
- **Drift warning:** `GateFailure` kind `drift_warning`, non-blocking (:177); freshest-marker selection per TLB-412.
- **Post-worker dirty-worktree check:** one bounded retry with an injected "commit before returning" note (`dirty_worktree_first_attempt` :437); a second dirty result hard-fails (`dirty_worktree_retry_failed` :464).
- **Preload advisories (NEW):** `preload_file_not_found` / `preload_empty` are advisory `GateFailure`s; preload problems never fail the phase (:670-684).

### `gate` (`GatePhase`, NEW - TLB-503/505/506/507/538)

Runs inside the chain between implement and review ([src/ThroughlineBuild.Phases/GatePhase.cs](../../src/ThroughlineBuild.Phases/GatePhase.cs); wired via the `gateFactory` in `ChainPhase`). It re-runs the configured checks itself (deterministic), validates the implementer's `CompletionClaim` (parsed by `CompletionClaimParser.TryParse`, [src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs:37](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs#L37)), runs the consumes-provides preflight (the ticket's `consumes` must be a subset of accumulated upstream `provides`; result is an advisory `SmokeSignal`, [GatePhase.cs:120-131](../../src/ThroughlineBuild.Phases/GatePhase.cs#L120-L131)), and collects diff-fact smoke signals via `SmokeCollector.CollectDiffFacts` ([src/ThroughlineBuild.Verification/SmokeCollector.cs:17](../../src/ThroughlineBuild.Verification/SmokeCollector.cs#L17)). Smoke signals and the preflight are **advisory - they never fail the gate**.

Check `role` drives gating: `Setup` failures and `Gating` failures hard-fail; `Advisory` failures are recorded only. Failure paths, each a `GateFailure` event with the named kind:

- `claim_schema_invalid` ([GatePhase.cs:92](../../src/ThroughlineBuild.Phases/GatePhase.cs#L92)) - malformed completion claim; hard-fail, feeds rework.
- `setup_failed` (:151) - a Setup-role prerequisite failed; hard-fail, feeds rework (worker-fixable, rework cap bounds it).
- `gating_checks_failed` (:263) - a Gating-role check failed on the ticket worktree; normally hard-fail -> `InReview -> InProgress` rework with structured evidence.
- **Environment classification (TLB-538):** before blaming the ticket, `GateControlProber.ProbeAsync` re-runs the failed checks in a throwaway worktree at the base SHA ([src/ThroughlineBuild.Verification/GateControlProber.cs:34](../../src/ThroughlineBuild.Verification/GateControlProber.cs#L34); `gate_control_run` event at [GatePhase.cs:189](../../src/ThroughlineBuild.Phases/GatePhase.cs#L189)). If the base also fails, the gate tries one recovery arm - reloading the check config from disk (`gate_config_reloaded`, :209) - and otherwise emits `gate_environment_failure` (:235) and hard-fails **without** the rework transition: the ticket stays `InReview`, the chain returns `GateEnvironmentFailure`, and remaining siblings are skipped.
- **Vacuity proving (da544ff):** on a gating check's first green, `GateVacuityProver.ProveAsync` materializes the check's configured canary (broken input), re-runs only that check, and requires it to fail ([src/ThroughlineBuild.Verification/GateVacuityProver.cs:42](../../src/ThroughlineBuild.Verification/GateVacuityProver.cs#L42); once per check per run). A check that passes with the canary present is structurally vacuous: `gate_vacuous` hard-fail, no rework transition, chain outcome `GateVacuous` ([GatePhase.cs:303-311](../../src/ThroughlineBuild.Phases/GatePhase.cs#L303-L311)). A canary that cannot be cleaned up is `gate_canary_cleanup_failed` (same handling). A gating check with **no** canary emits advisory `gate_unverified` (:295) and never blocks.

### `review` (`ReviewPhase`)

- **Parent ticket:** aggregate-review branch (`RunParentReviewAsync`, [src/ThroughlineBuild.Phases/ReviewPhase.cs:381-416](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L381-L416)): in-flight children -> `Rework`; stalled children -> `Fail` ("children not Done: ..."); all Done -> `Pass`.
- **Worktree not found:** reconstructed from the ticket branch via `CheckoutWorktreeAsync` before failing (:129-136, TLB-407).
- **No `[implemented_at]` marker:** hard fail (:174). Review attributes to worktree HEAD; a superseded marker emits `implemented_at_superseded` and proceeds against HEAD (:185, TLB-414).
- **Provider unavailable (NEW, TLB-527):** when `WorkerAgentReviewer.LastProviderError` is set (classified by `ProviderErrorClassifier.Classify` from the worker's failure reason/summary - quota/rate-limit/429/529 -> `RateLimitOrQuota`, 401/auth phrases -> `Auth`; [src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs:60](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs#L60)), review posts **no verdict comment and no transition** - that would record a rejection the reviewer never made. It emits `GateFailure` kind `review_provider_unavailable` (provider, error kind, optional `retry_at`) and returns a typed `ProviderUnavailable` result ([src/ThroughlineBuild.Phases/ReviewPhase.cs:242](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L242)). `ImplementReviewLoop` maps it to `ReviewUnavailable` (exit 9) instead of `StoppedAtReview` ([src/ThroughlineBuild.Phases/ImplementReviewLoop.cs:643](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L643)); the ticket stays cleanly `InReview`, resumable via `build review <id>`.
- **Advisory checks never drive rework (d30dbac):** the verdict event separates `checks_failed` (gating/setup) from `advisory_failed`, and only the former feeds the rework loop; failed-check evidence is persisted as `checks_failed_details` (:444-476) so rework briefs carry the check's own output. After rework, cited checks are re-run.
- **Dirty worktree after verifier:** hard fail, no retry (`dirty_worktree_after_review`, :266).
- **Verdicts:** `Pass` no transition; `Rework` -> `InProgress`; `Fail` no transition.

### `ship` (`ShipPhase`)

By stage (`ShipFailureStage`, [src/ThroughlineBuild.Phases/ShipPhase.cs:29-40](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L29-L40)):

| Stage | Trigger | Recovery |
|---|---|---|
| `StateCheck` | not `InReview`; worktree not found; (parent) children not Done | fix state; recreate worktree; finish children |
| `PreFlight` | exe inside the worktree (:186-203); worktree dirty/conflicted/stash-polluted (`ShipPreflightAsync` :209); main worktree not on target or detached (`wrong_worktree_branch` :260-275, unconditional) | move binary; clean tree; `git switch <target>` |
| `Fetch` | fetch failed; target diverged-with-conflict; `--no-auto-merge`; raced auto-rebase (aborted, never leaves detached HEAD) | reconcile local vs remote target manually |
| `Rebase` | rebase conflicts (aborted via `RebaseAbortAsync`) | resolve on the feature branch; rerun |
| `ConflictMarkerScan` | leftover conflict markers in committed files | clean up; recommit |
| `RegressionChecks` | a *gating regression* - newly failing vs baseline; advisory failures and pre-existing failures never block (see below) | fix on the feature branch |
| `FastForwardMerge` | merge failed, or post-merge HEAD assertion inside the lock failed | refetch; rerun |
| `Push` | `git push` failed after the local FF merge landed | push manually or rerun |
| `Decruft` | post-merge cleanup failed (non-fatal, post-`Done`) | clean up manually / `build sweep` |

**Baseline-aware regression checks, advisory-aware and self-correcting (TLB-401, 22a79ab):** regressions are computed against the baseline failing-set from the detached `.worktrees/baseline-<sha>` run. Two refinements since the last refresh:

- **Advisory regressions never block ship** - they are reported via a `ship` event with action `advisory_regressions_noted` and a console line "advisory regressions (never block ship)" ([src/ThroughlineBuild.Phases/ShipPhase.cs:538-539](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L538-L539), :621-630); the legacy (no-baseline) gate likewise exempts advisory failures (:698-708), mirroring gate semantics.
- **Contradictory-baseline recheck:** "fails on feature, passed on baseline" can also mean the cached baseline was wrong (tool-cache leak). Ship re-runs the contested checks in a fresh control worktree on the same base SHA via the baseline prober; checks confirmed failing on base are reclassified as pre-existing and the `BaselineCache` entry is corrected in place (:544-595, cache fix at :590). A failed Setup step on the pristine base makes the control run inconclusive-conservative.

**Push failure still leaves a local-only merge** with the ticket `InReview`; once the `Done` transition lands (:792), decruft and branch-delete failures are non-fatal. Parent ship blocks at `StateCheck` with `parent_children_not_done` (:888) unless every child is `Done`, then transitions only the parent (:895).

### `decompose` (`DecomposePhase`)

Unchanged shape: worker non-Ok; malformed / <2 `child_specs`; `DecomposeVerdict.Check` quality gate ([src/ThroughlineBuild.Phases/DecomposePhase.cs:113](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L113)); "all child ticket creations failed" (:143); partial create still posts `[decomposed_at]` (:147). Not idempotent - rerun duplicates children.

### `chain` (`ChainPhase`)

Wraps the others. Outcome -> exit-code mapping now lives in `ChainExitCodeMapper.GetExitCode` ([src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs:13](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L13)); `ChainOutcome` ([src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs)) has grown to 20 values. Success (0): `Completed`, `RatifiedObsolete`, `ParentCompleted`, `DryRunPreview`. Refusals (2): `RefusedInitialState`, `RefusedDirtyTree`, **`RefusedWrongBranch` (NEW)**, `ParentHasGrandchildren`. Stops: `StoppedAtPlan`/`ParentStoppedEarly`/`Skipped` (3), `StoppedAtImplement` (4), `StoppedAtReview` (5), `ReworkCapExceeded` (6, cap still `MaxReworkRounds = 2` at [src/ThroughlineBuild.Phases/ImplementReviewLoop.cs:17](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L17)), `StoppedAtShip` (7). New classified stops: `GateVacuous` (8), `ReviewUnavailable` (9), `GateEnvironmentFailure` (10), `TicketingUnavailable` (11). `BatchImplemented` is an internal per-ticket outcome of the warm-batch path, not a process exit.

**Preflight (outermost entry only):**

- **Wrong-branch guard (NEW):** main worktree not on the target branch (or detached) refuses before any planning with `GateFailure` kind `chain_preflight_wrong_branch` -> `RefusedWrongBranch` ([src/ThroughlineBuild.Phases/ChainPreflight.cs:45](../../src/ThroughlineBuild.Phases/ChainPreflight.cs#L45)) - the same invariant ship would enforce at the end, checked up front.
- **Hygiene:** `ChainPreflight` emits `hygiene_gate_preflight` and tracked-dirt `chain_preflight_dirty`, both mapping to `RefusedDirtyTree` ([ChainPreflight.cs:63](../../src/ThroughlineBuild.Phases/ChainPreflight.cs#L63)).
- **Integration worktree:** creation failure stops `ParentChainRunner` with `integration_worktree_unavailable` -> `ParentStoppedEarly` ([src/ThroughlineBuild.Phases/ParentChainRunner.cs:139](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L139)); there is no longer a per-ticket-worktree fallback. A reused integration branch is refreshed by `ChainIntegrationBranch.RefreshIntegrationBranchAsync` before child dispatch; a conflicted refresh stops the chain before work is burned ([src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:282](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L282)).

**Chain resume (`ChainResumeResolver.ResolveAsync`, [src/ThroughlineBuild.Phases/ChainResumeResolver.cs:51](../../src/ThroughlineBuild.Phases/ChainResumeResolver.cs#L51)):** `Backlog`/`Ready`/`InReview` enter directly; `Planning` resets to `Backlog`; `InProgress` is reconciled by `ResolveInProgressAsync` (:86), where an orphan branch is pruned to `Ready` and real commits resume as a rework round using the latest `Rework` verdict, including `checks_failed_details` evidence from the event log. Only `Done`/`Cancelled` refuse.

**Environmental stops skip siblings (TLB-538/545):** when a child stops with `GateEnvironmentFailure` or `TicketingUnavailable`, `ParentChainRunner` uses `ContainsEnvironmentalStop` and marks every undispatched sibling `Skipped` with an explanatory rationale instead of silently omitting them ([src/ThroughlineBuild.Phases/ParentChainRunner.cs:470](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L470); skip synthesis at :519).

**Root landing failures (TLB-492/494):** the outermost chain lands the integration branch through `ChainIntegrationBranch.LandRootIntegrationBranchAsync` ([src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:111](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L111)). Distinct failure kinds, all leaving work safe on the integration branch: `chain_landing_wrong_branch` (:122), rebase/FF failure from `RebaseThenFastForwardAsync` (:133), and `chain_landing_push_failed` (:166). A missing remote is not a failure: the land completes locally and emits `chain_landing_push_skipped` with reason `no_remote` (:139).

**Sweep:** on success `ChainIntegrationBranch.SweepChainWorktreesAsync` sweeps `ticket/`/`chain/` worktrees best-effort; halts surface as `worktree_sweep_incomplete` and never fail the chain ([src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:364](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L364)). Failures preserve everything; `build sweep` is the recovery verb (see 05).

### Batch implement (`--batch` path) failure modes

When batching is configured and a parent chain has eligible leaf siblings, `BatchImplementRunner.RunBatchImplementSessionAsync` uses one warm worker session to implement them as a commit stack on a single branch in the integration worktree ([src/ThroughlineBuild.Phases/BatchImplementRunner.cs:119](../../src/ThroughlineBuild.Phases/BatchImplementRunner.cs#L119)), dispatched by `ParentChainRunner` ([src/ThroughlineBuild.Phases/ParentChainRunner.cs:300](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L300)).

- **Loud downgrade (e76ac5d):** when batch preconditions are not met (no batch worker configured, no shared worktree), `ParentChainRunner` emits `batch_implement_unavailable` and runs the per-ticket path ([src/ThroughlineBuild.Phases/ParentChainRunner.cs:367](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L367)) - the previous *silent* downgrade was the bug.
- **Commit verification:** `BatchCommitVerifier` confirms the worktree is clean and that each self-reported commit exists in declared stack order; when the worker omitted its self-report, `ReconstructFromGitAsync` maps commits 1:1 onto tickets by stack position and fails on a count mismatch ([src/ThroughlineBuild.Phases/BatchCommitVerifier.cs:133](../../src/ThroughlineBuild.Phases/BatchCommitVerifier.cs#L133), used at [BatchImplementRunner.cs:332](../../src/ThroughlineBuild.Phases/BatchImplementRunner.cs#L332)).
- **Partial failure:** a worker that died mid-stack has its confirmed commits verified and those tickets advanced (`BatchImplemented`); unconfirmed tickets return `StoppedAtImplement`.

### Multi-ticket / parallel dispatch failure modes

- **Cycle:** `TopologicalSorter` throws; `ParallelDispatcher` returns `Success=false`, no tickets run.
- **Partial failure:** the dispatcher stops dispatching subsequent levels after the first failure; the current level finishes (`failureReason` set, [src/ThroughlineBuild.Phases/ParallelDispatcher.cs:155-168](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L155-L168)). Exit code comes from `ChainExitCodeMapper.GetExitCode(ParallelDispatchResult)`, which preserves the first failing outcome's code.
- **Ancestor-skip:** unchanged (`AncestorSkipFilter`, `--continue-past-failure`).
- **Cancellation:** dispatcher records `failureReason = "cancelled"` (:93, :149).

### `rework` / `new` / `scaffold` / `amend` / `close` / `defer` / `reopen`

Shapes unchanged: `ReworkOutcome` enum (`Implemented` / `NoFeedbackAvailable` / `TicketNotInProgress` / `ImplementFailed`, [src/ThroughlineBuild.Phases/ReworkPhase.cs:8](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L8)); `new` validation exceptions; scaffold's collected `ScaffoldFailure[]` with duplicate-on-rerun; close/defer cascade soft-failures; reopen never reopens children. A missing Anthropic key degrades `close`/`defer`/`reopen` to verbatim-reason recording via `EchoLlmClient` with a stderr warning ([src/ThroughlineBuild.Cli/CliApplication.cs:2407-2415](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2407-L2415), TLB-371).

### Conductor and readiness commands

- **Profile proof fails before write.** Proposed gating checks must exist and each canary must be rejected in an isolated worktree; setup/check failure, ineffective canary, cleanup leak, or install failure returns nonzero and leaves config unchanged (`ProfileGateVerifier.VerifyAsync`, [ProfileGateVerifier.cs:27](../../src/ThroughlineBuild.Cli/ProfileGateVerifier.cs#L27)).
- **SOP mutations are catalog/hash bounded.** Upgrade preserves locally edited emitted stubs unless their bytes match a trusted previous catalog hash; uninstall removes only current catalog-owned regular files whose bytes still match. All target and manifest paths are containment- and link-checked ([SopInstallCommand.cs:992](../../src/ThroughlineBuild.Cli/SopInstallCommand.cs#L992)).
- **Lease failures retain evidence.** Install failure is represented in `WorktreeInstallRecord`; teardown refuses manifest mismatch, containment violation, dirty state, and unmerged helper branches unless the relevant explicit option is supplied ([WorktreeLease.cs:13](../../src/ThroughlineBuild.Helpers/WorktreeLease.cs#L13), [WorktreeLeaseManager.cs:282](../../src/ThroughlineBuild.Helpers/WorktreeLeaseManager.cs#L282)).
- **Evidence is intentionally at-least-once only when the caller chooses.** A successful POST followed by failed read-back is not retried by the command, preventing automatic duplicate audit comments ([EvidenceCommand.cs:49](../../src/ThroughlineBuild.Cli/EvidenceCommand.cs#L49)).
- **Install handoffs are success-with-stop, not completion.** `profile_handoff` and `invariants_handoff` exit 0 but carry `Ready=false`, a prompt, and canonical next command; only the final readiness stage returns READY ([InstallCommand.cs:545](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L545)).

### Loose ends

- Decompose's "partial create is success" rule still stamps `[decomposed_at]` over a half-created fan-out.
- The `ParallelDispatcher` stop-after-level rule remains coarser than the sequential ancestor skip.
- Gate vacuity proving covers only checks that declare a `canary` in config; un-canaried gating checks are advisory-`gate_unverified` forever.

---

## Cross-cutting failure modes

### Worker CLI missing or misconfigured

- `process.Start()` catches `Win32Exception` in all four agents and returns a soft `Status.Failed` `WorkerResult` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:106](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L106)).
- **Unresolvable claude-code model fails fast (NEW, TLB-544):** `ClaudeCodeModelValidator.Validate` rejects anything that is not a tier alias (`haiku`/`sonnet`/`opus`) or a `claude-*` slug at config load with an operator-actionable `Config error` - the canonical trap being `model = "fable"`, which must be the full slug `claude-fable-5` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs:22-47](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs#L22-L47), wired at [src/ThroughlineBuild.Cli/Config.cs:646](../../src/ThroughlineBuild.Cli/Config.cs#L646)). Previously this surfaced as a generic envelope error deep inside a chain.
- **Undefined agent fails fast (NEW, TLB-512):** `default_agent` (or a `[workers.phases]` entry) naming an agent with no `[workers.<name>]` sub-table throws a `ConfigException` at load - "...there is no [workers.X] sub-table in config. Uncomment or add ... Configured agents: ..." ([src/ThroughlineBuild.Cli/Config.cs:679-686](../../src/ThroughlineBuild.Cli/Config.cs#L679-L686), message at :702-713) - instead of an unhandled exception at agent-resolution time. Exit 2 via the friendly handler.

### Provider quota / rate-limit / auth during a worker session

- `ProviderErrorClassifier.Classify` pattern-matches the worker's failure reason + summary into `RateLimitOrQuota` (usage/rate limit, quota, overloaded, 429/529) or `Auth` (invalid key, login/session expired, 401), with an optional `RetryAt` parsed from the message ([src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs:60-111](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs#L60-L111)). Timeouts and cancellations are deliberately not provider errors.
- Today only the **review** path consumes the classification (`ReviewUnavailable`, exit 9). A quota-blocked *implement* still surfaces as a plain `StoppedAtImplement`.

### Plane unreachable / throttled (layered, TLB-545)

Ticketing failures are no longer uniformly fatal inside phases. `TicketingWritePolicy.BestEffortAsync` ([TicketingWritePolicy.cs:15](../../src/ThroughlineBuild.Phases/TicketingWritePolicy.cs#L15)) catches non-cancellation failures for explicitly informational comments/labels, warns on stderr, emits `ticketing_write_failed` locally when possible, and lets the phase continue. State transitions and workflow-resume markers remain hard writes. `ChainPhase.RunAsync` converts a hard `TicketingUnavailableException` into the resumable `TicketingUnavailable` outcome; the batch wrapper preserves the failing ticket identity and marks remaining siblings skipped.

Three layers, innermost first:

1. **Rate gate:** every call awaits `RequestThrottle.AcquireAsync` - at most `RequestsPerMinute` (default 40) per rolling minute ([src/ThroughlineBuild.Plane/RequestThrottle.cs:49](../../src/ThroughlineBuild.Plane/RequestThrottle.cs#L49), [src/ThroughlineBuild.Plane/PlaneClientOptions.cs:20](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L20)). This is a self-imposed budget, NOT a reading of the server's real limit: at budget the gate hard-waits and prints `[throttle] rate-limit budget full; waiting Ns` even against a self-hosted Plane that would have accepted the call. Tune per deployment via `ticketing.plane_requests_per_minute` (TLB-565).
2. **HTTP-status retry:** Polly retries up to `MaxRetryAttempts` (default 5) on 429/5xx honoring `Retry-After` ([PlaneClientOptions.cs:26](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L26)); 401/403 throw immediately.
3. **Transport retry (NEW):** `SendWithTransportRetryAsync` retries DNS/connect/TLS/timeout failures - errors where no HTTP status exists yet - up to `TransportRetryAttempts` (default 3) with exponential backoff (base 2s, cap 10s, +-25% jitter) ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:284-312](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L284-L312); options at [PlaneClientOptions.cs:47-53](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L47-L53)). Mid-flight failures (response ended, protocol error, client timeout) retry only **idempotent verbs** (GET/PATCH); POST never retries mid-flight (:300-326). Exhausted retries throw `TicketingUnavailableException` ([src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs:11-16](../../src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs#L11-L16)).

`ChainPhase.RunAsync` catches `TicketingUnavailableException` at the per-ticket boundary and classifies it as the environmental outcome `TicketingUnavailable` (exit 11) instead of crashing the process ([src/ThroughlineBuild.Phases/ChainPhase.cs:176](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L176)); the ticket's work is already committed on its branch, the chain is resumable once connectivity returns, and remaining siblings/roots are marked `Skipped`.

Snapshot truncation is unchanged: the per-run snapshot load caps at `MaxListPages = 50` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1725](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1725)) with a loud stderr warning; unknown ticket ids in a multi-ticket chain surface as "Ticket not found", exit 2.

### Anthropic rate-limited / key absent

Soft everywhere: worker-driven phases use the worker CLI's own auth; `close`/`defer`/`reopen` degrade to `EchoLlmClient` verbatim recording (TLB-371).

### git divergence / conflict

Unchanged at the ship layer (`ProbeDivergenceAsync`, auto-rebase, `TargetAutoRebased`); the chain adds the integration-branch refresh (conflicted refresh stops before dispatch) and the landing's rebase-then-fast-forward (conflict stops with work preserved on the integration branch).

### MainWorktreeLock contention / Ctrl-C / worker hangs / disk full / kill failures

Unchanged: in-process lock only (two `build` processes can still race); every verb installs a Ctrl-C handler that cancels the phase and kills worker process trees (`Process.Kill(entireProcessTree: true)`, swallowed on failure); worker timeouts via `WorkerOptions.Timeout` (default 30 min, verifier 15); event-sink write failures surface as phase failures.

### Loose ends

- `RequestThrottle` + retries remain process-scoped; concurrent `build` processes can collectively exceed Plane's server-side budget.
- Provider-error classification is regex/phrase-based over worker output; a vendor wording change silently reverts `ReviewUnavailable` to `Fail`-shaped review failures.
- `TicketingUnavailableException` is only caught at the chain boundary; standalone verbs (`plan`, `implement`, `ship`, ...) let it propagate as an ordinary error exit.

---

## Idempotency posture summary

### JSON command failures and partial writes

`--json` changes representation, not exit semantics: configuration/usage failures exit 2, missing secrets exit 3, not-found and operational failures exit 1, while stdout remains a single schema-versioned envelope. `PlaneCliError.Report` owns backend-error classification ([PlaneCliError.cs:16](../../src/ThroughlineBuild.Cli/PlaneCliError.cs#L16)).

Structured `build new - --json` resolves all referenced tickets before creation, but issue creation and subsequent parent/relation writes are not atomic. A relation failure reports that the ticket already exists and that earlier edges may have landed. `build relate` create/remove operations are individually retryable by the operator; removal requires the exact stable edge ID returned by `--list`. `build amend` validates inputs before mutation but applies its fields sequentially, so a later Plane failure can preserve earlier changes.

`build`'s rerun safety is **state-driven**: each phase enforces a ticket-state precondition, and SHA markers act as forward-progress guards rather than de-dup keys.

- A phase that already transitioned fails its state guard on rerun. Worker-backed `plan` still parks failed runs in `Planning`; promotion (the chain default, or explicit via `--from-brief`) has almost no window to fail mid-flight.
- **Chain / multi-ticket / parent chain are safe to re-run** and now stronger than before: stuck states are reconciled (`Planning` reset, `InProgress` resumed with persisted check evidence), a reused integration branch is refreshed against its moved base (TLB-546) so resumed children never implement against a stale snapshot, and environmental stops (`GateEnvironmentFailure`, `TicketingUnavailable`, `ReviewUnavailable`) leave tickets cleanly resumable with no rework round burned.
- **Cleanup is idempotent:** the chain success sweep decrufts only ticket and chain worktrees, retaining their branches. `build sweep` merged-gates branch deletion; `--force` widens worktree removal but never branch deletion.
- **Conductor install is staged and idempotent:** profile apply no-ops when identical; SOP install restores missing catalog paths without clobbering edits; conductor apply atomically replaces only invariants; readiness rechecks rather than recording a stale success.
- **Marker staleness** remains solved by freshest-marker selection (TLB-412) and HEAD attribution (TLB-414).
- The most expensive non-idempotent verbs are still `scaffold` and `decompose` (duplicate ticket trees, hand cleanup in Plane).

---

## Loose ends

- **No transactional Plane writes; no rollback verb.** Unchanged.
- **`scaffold` / `decompose` duplication** remains the sharpest edge; use `--dry-run`.
- **Push failure leaves a local-only merge** (ship `Push` stage; chain `chain_landing_push_failed`) - operator pushes manually or reruns.
- **`Status.Escalate`** is handled by the chain (obsolete-claim ratification) but standalone `plan`/`implement` still just fail.
- **Ctrl-C between Plane writes** can still leave a half-updated ticket.
- **Exit codes 8-11 are new public surface** (`GateVacuous`, `ReviewUnavailable`, `GateEnvironmentFailure`, `TicketingUnavailable`); scripted callers written against the old 0-7 range will misclassify them as generic failures.
