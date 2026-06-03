# 09 - Failure Modes and Idempotency

For each major operation, how it fails and whether re-running is safe. Exit codes summarized in [06-public-surfaces.md](06-public-surfaces.md); chain/dispatch outcomes in [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Failure-mode summary table

| Operation | Pre-flight gates | Common failures | Failed-at state | Idempotent on rerun? |
|---|---|---|---|---|
| `plan` | ticket exists; not a parent; state == `Backlog`; `git rev-parse main` resolves | worker non-Ok status; unresolvable `plan_body_ref` -> `PLAN_BODY` fenced block; missing scalar metadata keys (`risk_label`, `size_label`, `planned_at_sha`) | ticket left in `Planning` once the worker has run (transition precedes the status check) | partial - rerun fails the `Backlog` guard once parked in `Planning`; operator must reset state |
| `implement` | ticket exists; not a parent; state == `Ready` (initial) or `InProgress` (rework); hygiene gate (no conflicts / unrelated stashes); `git worktree add` succeeds | hygiene-gate block (`hygiene_gate`); worktree creation fails; worker non-Ok; missing `commit_sha` metadata; dirty worktree after worker exit (one bounded retry) | `Ready` if pre-worker; `InProgress` if worker ran but didn't deliver | yes if worktree was created (rerun reuses it via the rework path) |
| `review` | ticket exists; state == `InReview`; worktree locatable (reconstructed from branch if missing); `[implemented_at]` marker present | check timeout (non-fatal); verifier subprocess crash; missing verdict metadata; dirty worktree after verifier exit (hard fail, `dirty_worktree_after_review`) | state unchanged (only `Rework` changes it) | yes - rerun re-runs checks and verifier; one extra verdict comment posted |
| `ship` | ticket exists; state == `InReview`; worktree locatable; build.exe not inside it; both worktrees clean (hygiene gate); main worktree on target branch (not detached); bases not diverged-with-conflict; rebase succeeds; no conflict markers; no regressions; FF merge + push succeed | listed at each stage via `ShipFailureStage` | enum value identifies stage (`StateCheck`, `PreFlight`, `Fetch`, `Rebase`, `ConflictMarkerScan`, `RegressionChecks`, `FastForwardMerge`, `Push`, `Decruft`) | partially - rebase + checks idempotent; post-merge transitions not retried by `ship` itself |
| `decompose` | ticket exists; `git rev-parse main` resolves | worker non-Ok; malformed / <2 `child_specs`; `DecomposeVerdict` quality-gate failure; all child creates fail | no parent transition (decompose never moves the parent state) | no - rerun creates duplicate child sub-issues |
| `chain` | any non-terminal state (`Backlog`/`Ready`/`InReview` route directly; `Planning`/`InProgress` are reconciled and resumed), or has children (parent path); only `Done`/`Cancelled` refuse | any inner phase failure propagates as `StoppedAt*`; rework cap; obsolete escalation; parent-tree refusals | `ChainOutcome` value identifies stop point | yes - rerunning starts at whatever state the ticket landed in; an interrupted-implement `InProgress` ticket is resumed rather than refused |
| `chain` (multi-ticket / parent) | per-ticket as above; dependency graph acyclic; parent tree at most one level deep | cycle in `blocked_by` graph; a level/child fails; grandchildren present | `ParallelDispatchResult.FailureReason` / `ParentHasGrandchildren` / `ParentStoppedEarly` | yes - completed tickets are skipped on re-entry by their state |
| `rework` | state == `InProgress`; manual `--feedback` or a `Rework` verdict in event log | feedback retrieval fails; underlying `ImplementPhase` fails | `ImplementFailed`, `NoFeedbackAvailable`, `TicketNotInProgress` | yes |
| `new` (file mode) | body file readable; title present | validation throws on missing title / empty body | nothing to roll back | yes - duplicates the Plane ticket on rerun |
| `new` (draft mode) | worker dispatchable | draft worker fails / wrong shape; user quits review loop | nothing posted | yes |
| `scaffold` | op-doc parses; validation passes (or `--accept-warnings`) | per-ticket create or parent-link failures collected in `ScaffoldFailure[]` | partial creation possible (operation/plan/brief tree) | no - rerun creates duplicates; nothing is matched back by content |
| `amend` | state not terminal; at least one of `--size` / `--note` | invalid size value; terminal state | nothing | yes - replacing labels is idempotent; appending notes accumulates |
| `close` / `defer` | state not terminal; reason supplied | child cascade failure (soft); rollup failure (soft); decruft failure (soft) | nothing pre-transition; `Cancelled` after transition (+ cascaded children) | no - rerunning on a `Cancelled` ticket fails the state check |
| `reopen` | state is `Done` or `Cancelled` | ambiguous target defaults to `Backlog` | nothing pre-transition | yes once back in active state - rerun fails the state check |

### Loose ends

- The `plan` idempotency posture changed: the `Backlog -> Planning` transition now happens *before* the worker-status check ([src/ThroughlineBuild.Phases/PlanPhase.cs:98](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98)), so a failed plan no longer leaves the ticket cleanly re-runnable in `Backlog`.

---

## Per-phase failure detail

### `plan` (`PlanPhase`)

- **Parent ticket:** returns failure "is a parent ticket with N children: ... plan each child individually" ([src/ThroughlineBuild.Phases/PlanPhase.cs:60-63](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L60-L63)).
- **Wrong state:** "ticket not in Backlog state" ([src/ThroughlineBuild.Phases/PlanPhase.cs:65-66](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L65-L66)). CLI exit 1.
- **`git rev-parse` failure:** "git rev-parse failed: ..." ([src/ThroughlineBuild.Phases/PlanPhase.cs:73-76](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L73-L76)).
- **Worker failure:** worker `Status != Ok` returns the envelope reason ([src/ThroughlineBuild.Phases/PlanPhase.cs:105-108](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L105-L108)). The ticket is already in `Planning` at this point. If the status is `Escalate`, the `WorkerResult` is returned as `EscalationWorkerResult` so the chain can run obsolete-claim ratification.
- **Unresolvable plan body / missing metadata keys:** the plan body now arrives as the `PLAN_BODY` fenced block referenced by `plan_body_ref`; an unresolvable ref fails with "worker metadata missing or unresolvable plan_body_ref: ..." and the scalar keys (`risk_label`, `size_label`, `planned_at_sha`) are still required ([src/ThroughlineBuild.Phases/PlanPhase.cs:120-125](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L120-L125)). The resolved markdown is rendered to HTML by `MarkdownRenderer` before the description append.
- **Idempotency caveat:** a prior run that posted the description but died before the marker comment will append the description a second time on rerun - the append is `existing + html` so duplication is visible.

### `implement` (`ImplementPhase`)

- **Parent ticket:** refuses with "is a parent ticket with N children: work child-by-child ..."; writes `phase-status.json` via `EarlyExitManifest` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:69-76](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L69-L76)).
- **Wrong state:** writes `phase-status.json` and returns. For an initial round the message is state-specific (InReview -> "run `build review`/`build ship`"; InProgress -> "run `build rework` or reset to Ready"; Backlog/Planning -> "plan it first"; Done/Cancelled -> "nothing to implement") via `InitialRoundStateGuidance`; a rework round against a non-InProgress ticket returns "rework round invoked but ticket is in X" ([src/ThroughlineBuild.Phases/ImplementPhase.cs:78-91](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L78-L91)).
- **Hygiene gate (Step 2b):** before any worktree work, `WorkingTreeHygieneGate.CheckAsync` refuses on conflicted/unmerged paths or stash entries unrelated to the ticket branch, emitting a `GateFailure` with `kind = hygiene_gate` and returning "working tree is not clean: ..." ([src/ThroughlineBuild.Phases/ImplementPhase.cs:93-106](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L93-L106), [src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs:24-62](../../src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs#L24-L62)). The gate ignores ordinary uncommitted modifications - those are handled separately.
- **Worktree creation fails (initial):** returns "worktree create failed: ..." (or "branch create in shared worktree failed: ..." on the chain shared-worktree path) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:236-247](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L236-L247)). Common cause: branch already exists.
- **Missing rework worktree:** rework requires the existing worktree on disk; absence is a hard early-exit ([src/ThroughlineBuild.Phases/ImplementPhase.cs:198-203](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L198-L203)).
- **Worker fails after worktree created:** ticket already `InProgress`; stays `InProgress`. Rerun goes through the rework path. `Escalate` is carried back as `EscalationWorkerResult` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:296-299](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L296-L299)).
- **Missing `commit_sha`:** "worker metadata missing commit_sha" ([src/ThroughlineBuild.Phases/ImplementPhase.cs:344-348](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L344-L348)).
- **`commit_sha` mismatch with actual HEAD:** actual HEAD wins; a discrepancy note is folded into the `implemented_at` comment ([src/ThroughlineBuild.Phases/ImplementPhase.cs:357-369](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L357-L369)). Informational only.
- **Drift warning:** emitted as `GateFailure` but does not block ([src/ThroughlineBuild.Phases/ImplementPhase.cs:114-122](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L114-L122)). The drift check selects the freshest `[planned_at]` marker by comment creation time, not list order, so a chain re-run does not read a stale prior-run marker (TLB-412, [src/ThroughlineBuild.Phases/CommentMarkers.cs:19-37](../../src/ThroughlineBuild.Phases/CommentMarkers.cs#L19-L37)).
- **Post-worker dirty-worktree check (Step 14b, TLB-400):** after a successful worker exit, any uncommitted tracked files trigger one bounded retry - the brief is re-issued with an instruction to commit, and a second dirty result hard-fails with `dirty_worktree_retry_failed` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:301-333](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L301-L333)).

### `review` (`ReviewPhase`)

- **Parent ticket:** takes the aggregate-review branch instead of failing - see "Parent-tree failure modes".
- **Wrong state:** "ticket not in InReview state" ([src/ThroughlineBuild.Phases/ReviewPhase.cs:73-75](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L73-L75)).
- **Worktree not found:** before failing, review now tries to reconstruct the worktree from the ticket branch if the branch still exists on disk (e.g. a parent chain tore down its shared worktree, or a prior run was interrupted) (TLB-407, [src/ThroughlineBuild.Phases/ReviewPhase.cs:110-129](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L110-L129)). Only if the branch is also gone does it return "feature worktree not found at ...".
- **No `[implemented_at]` marker:** "no implemented_at marker found - ..." ([src/ThroughlineBuild.Phases/ReviewPhase.cs:162-164](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L162-L164)). The marker is selected by freshest creation time (TLB-412), and review attributes to the worktree branch HEAD rather than the marker SHA: if HEAD differs from the marker (an implementer amended/squashed after posting it), a `GateFailure` with `kind = implemented_at_superseded` is emitted and the review, diff, and checks all run against HEAD (TLB-414, [src/ThroughlineBuild.Phases/ReviewPhase.cs:152-181](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L152-L181)).
- **Dirty worktree after verifier (Step 10b, TLB-400):** any uncommitted tracked file left after the verifier exits is a HARD failure with no retry - the verifier verdict is emitted first, then a `GateFailure` with `kind = dirty_worktree_after_review` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:217-235](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L217-L235)).
- **Check timeout:** the check is marked failed; the verifier sees it in the brief. The phase does not abort.
- **Verifier subprocess crash:** propagates as a phase infra failure (CLI maps to exit 4 on the standalone `review` verb).
- **Verdict `Pass`:** no transition; ticket stays `InReview` for `ship`.
- **Verdict `Rework`:** transitions back to `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:268-279](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L268-L279)).
- **Verdict `Fail`:** no transition; operator decides.

The default verifier is `WorkerAgentReviewer` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:204-205](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L204-L205)); it runs the verifier worker inside the feature worktree so it cannot dirty tracked files in main and block the subsequent ship pre-flight. (The former `ClaudeCodeReviewer` class no longer exists.)

### `ship` (`ShipPhase`)

By stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:23-34](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L23-L34)):

| Stage | Trigger | Recovery |
|---|---|---|
| `StateCheck` | ticket not `InReview`; worktree not found; (parent) children not Done | fix state via `review`/`implement`; recreate worktree; finish children |
| `PreFlight` | build.exe running from inside the worktree; either worktree dirty/conflicted/stash-polluted (`ShipPreflightAsync`, `pre_flight_hygiene` / `pre_flight_dirty`); main worktree not checked out on the target branch *or detached* (`wrong_worktree_branch`, now unconditional - applies when targeting main too) | move binary; commit or stash; `git checkout <target>`; rerun |
| `Fetch` | `git fetch` failed; target branch diverged-with-conflict, or diverged and `--no-auto-merge`, or auto-rebase raced to conflict (the raced rebase is aborted unconditionally so the main worktree never lands detached, [ShipPhase.cs:382-386](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L382-L386)) | resolve local `<target>` vs `<remote>/<target>` manually; rerun |
| `Rebase` | rebase conflicts; rebase fails otherwise. Aborted by `RebaseAbortAsync` | resolve conflicts on the feature branch; rerun |
| `ConflictMarkerScan` | leftover `<<<<<<<` / `=======` / `>>>>>>>` in committed files | clean up; recommit; rerun |
| `RegressionChecks` | a *regression* (a check that newly fails relative to the baseline) - pre-existing failures and fixes do not block (see below) | fix on the feature branch; rerun |
| `FastForwardMerge` | merge failed, or HEAD is not on the target branch after the FF merge (post-merge assertion inside the lock, [ShipPhase.cs:655-667](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L655-L667)) | refetch; rerun |
| `Push` | `git push <remote> <target>` failed after the local FF merge already landed | the merge is local-only until push succeeds; push manually or rerun |
| `Decruft` | post-merge worktree cleanup failed | merge already landed; clean up manually |

The fetch, the target-branch auto-rebase, and the FF merge run under `MainWorktreeLock` so concurrent chains do not race on the shared main worktree ([src/ThroughlineBuild.Phases/ShipPhase.cs:301, 364, 652](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L301)). The post-FF-merge HEAD assertion runs inside the same lock so no other ship can move HEAD between the merge and the check.

**Baseline-aware regression checks (TLB-401):** with a `BaselineCache`, ship first runs the same checks against a detached worktree at the onto-ref (`ComputeBaselineAsync`), caches the set of failing check names, then partitions the feature-branch results: *regressions* (newly failing, not in the baseline set) BLOCK at `RegressionChecks` with `kind = regression_checks`; *pre-existing* failures are noted non-blocking (`pre_existing_failures_noted`); checks that the branch *fixes* emit `fixes_detected` ([src/ThroughlineBuild.Phases/ShipPhase.cs:489-646](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L489-L646)). `--skip-baseline` (or a failed baseline computation) falls through to the legacy gate where *any* failing check blocks.

**Failed ship leaves the worktree and the feature branch on disk by design** for inspection.

**Once `Done` transition lands, push has already succeeded; decruft and branch-delete failures are non-fatal** ([src/ThroughlineBuild.Phases/ShipPhase.cs:418-452](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L418-L452)) - the merge+push is the load-bearing operation. Note that a `Push` failure occurs *before* the `Done` transition, so a push-blocked ship leaves the ticket in `InReview` with the merge present locally.

### `decompose` (`DecomposePhase`)

- **`git rev-parse` failure:** "git rev-parse failed: ..." ([src/ThroughlineBuild.Phases/DecomposePhase.cs:61-64](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L61-L64)).
- **Worker non-Ok:** returns the envelope reason ([src/ThroughlineBuild.Phases/DecomposePhase.cs:91-93](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L91-L93)).
- **Missing / malformed `child_specs`:** "worker metadata missing or malformed child_specs array" ([src/ThroughlineBuild.Phases/DecomposePhase.cs:102-105](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L102-L105)).
- **Fewer than 2 specs:** "worker returned N child spec(s); at least 2 are required" ([src/ThroughlineBuild.Phases/DecomposePhase.cs:106-108](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L106-L108)).
- **Quality-gate failure:** `DecomposeVerdict.Check` runs `coverage_check` (every child needs `scope_boundary`), `uniqueness_check` (no duplicate titles), `size_check` (size in S/M/L). On any failure it emits a `VerifierVerdict status=VerdictFailed` and returns the joined failures with `VerdictFailures` populated; **no child tickets are created** ([src/ThroughlineBuild.Phases/DecomposePhase.cs:110-121](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L110-L121), [src/ThroughlineBuild.Phases/DecomposeVerdict.cs:5-29](../../src/ThroughlineBuild.Phases/DecomposeVerdict.cs#L5-L29)).
- **All child creates fail:** "all child ticket creations failed: ..." ([src/ThroughlineBuild.Phases/DecomposePhase.cs:139-141](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L139-L141)). A *partial* create (some children made) is treated as success and a `[decomposed_at]` comment is posted.
- **CLI:** ticket-not-found returns exit 2; phase failure returns exit 1; multiple ticket ids are rejected up front ([src/ThroughlineBuild.Cli/Program.cs:90, 1515-1569](../../src/ThroughlineBuild.Cli/Program.cs#L90)).
- **Not idempotent.** A successful or partially-successful run creates child sub-issues with no content-match guard; rerun duplicates them.

### `chain` (`ChainPhase`)

Wraps the others. Single-ticket exit codes are remapped from `ChainOutcome` ([src/ThroughlineBuild.Cli/Program.cs:1657-1673](../../src/ThroughlineBuild.Cli/Program.cs#L1657-L1673)):

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RatifiedObsolete` | 0 | obsolete claim ratified -> Done |
| `ParentCompleted` | 0 | all eligible children completed |
| `RefusedInitialState` | 2 | state is `Done` or `Cancelled` (the only refused states - see resume below) |
| `RefusedDirtyTree` | 2 | hygiene preflight found a conflicted / stash-polluted tree |
| `ParentHasGrandchildren` | 2 | tree deeper than one level |
| `StoppedAtPlan` | 3 | planning failed |
| `ParentStoppedEarly` | 3 | a child did not complete |
| `Skipped` | 3 | ancestor failed and `--continue-past-failure` absent |
| `StoppedAtImplement` | 4 | implementation failed |
| `StoppedAtReview` | 5 | review returned `Fail` (or review infra failure) |
| `ReworkCapExceeded` | 6 | more than `MaxReworkRounds` (2) reworks |
| `StoppedAtShip` | 7 | ship gate failed |

The chain emits `ChainStart`/`ChainEnd` around every run, `ReworkRound` per rework, and `TicketSubsumed` when an obsolete claim is ratified. See [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) for the full sequence.

**Hygiene preflight (once, at the outermost chain start):** for a non-shared (single-ticket) entry, the chain runs `WorkingTreeHygieneGate.CheckAsync` against the working directory *before any planning*, emitting a `GateFailure` with `kind = hygiene_gate_preflight` and returning `ChainOutcome.RefusedDirtyTree` (exit 2) on a conflicted/stash-polluted tree - so a dirty tree fails fast instead of corrupting a ticket mid-chain ([src/ThroughlineBuild.Phases/ChainPhase.cs:86-110](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L86-L110)). For a parent chain, if the shared worktree cannot be created the chain degrades loudly to per-ticket worktrees (`GateFailure kind = shared_worktree_unavailable`) rather than aborting ([src/ThroughlineBuild.Phases/ChainPhase.cs:762-782](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L762-L782)).

**Chain resume (`ResolveEntryAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:960-1065](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L960-L1065)):** the chain no longer refuses non-`Backlog` states. `Backlog` -> plan, `Ready` -> implement, `InReview` -> review enter directly. A `Planning` ticket (plan started but never appended an artifact) is reset to `Backlog` and replanned. An `InProgress` ticket is reconciled by `ResolveInProgressAsync`: a branch with zero commits beyond base is an orphan - the branch/worktree are pruned and the ticket reset to `Ready` for a clean implement; a branch *with* commits is resumed in place as a rework round (recovering the last `Rework` verdict from the event log, else synthesizing a neutral resume note). Resume transitions emit `StateTransition` with `reason = chain_resume`. Only `Done`/`Cancelled` fall through to `RefusedInitialState`.

### Parent-tree failure modes (chain on a parent)

- **Grandchildren present:** if any eligible child has live children of its own, the chain returns `ParentHasGrandchildren` (exit 2) without dispatching anything; the operator must chain the intermediate ticket directly ([src/ThroughlineBuild.Phases/ChainPhase.cs:665-684](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L665-L684)). This guard (f3953f7) prevents the runaway recursion that previously hammered Plane's rate limiter.
- **A child stops early:** the parent chain runs all leaf children serially (dispatch semaphore `SemaphoreSlim(1, 1)` since op-29 - one child at a time, even within a dependency level); if any child outcome is not in the success set, the parent returns `ParentStoppedEarly`. Children that already completed are not retried.
- **Aggregate review of a parent:** any child still `InProgress`/`InReview` yields `Rework`; some children not Done (and not in-flight) yields `Fail`; all Done yields `Pass` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:317-373](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L317-L373)).
- **Parent ship gate:** blocks at `StateCheck` with "children not Done: ..." (GateFailure `kind = parent_children_not_done`) unless every child is `Done`, then transitions the parent straight to `Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:769-801](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L769-L801)).

### Multi-ticket / parallel dispatch failure modes

- **Cycle in dependency graph:** `TopologicalSorter.ComputeLevels` throws `InvalidOperationException` listing the cycle members; `ParallelDispatcher` catches it and returns `ParallelDispatchResult(Success=false, ...)` with the message - no tickets run ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:44-52](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L44-L52), [src/ThroughlineBuild.Phases/TicketGraph.cs:86-90](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L86-L90)).
- **Partial failure of one ticket in a level:** the dispatcher is **not** continue-past-failure. After each level, if any ticket's outcome is outside the success set (`Completed`/`RatifiedObsolete`/`ParentCompleted`), it records the first failure and stops dispatching *subsequent* levels; the current level's already-running tickets finish ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:118-133](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L118-L133)). The CLI returns 1.
- **Ancestor-skip (sequential fallback):** `AncestorSkipFilter` walks blocker edges; if an ancestor failed and `--continue-past-failure` is absent, the ticket gets a synthesized `Skipped` result and is never dispatched ([src/ThroughlineBuild.Phases/AncestorSkipFilter.cs:28-88](../../src/ThroughlineBuild.Phases/AncestorSkipFilter.cs#L28-L88)). `--continue-past-failure` disables the skip and lets later tickets run anyway.
- **Recovery mid-batch:** completed tickets re-entered on a rerun resume at their landed state (Done -> chain refuses; InReview -> resumes at review/ship), so re-running the same batch is safe and naturally idempotent at the ticket level.

### `rework` (`ReworkPhase`)

- **Wrong state:** `TicketNotInProgress` (exit 2). Ticket must be `InProgress` ([src/ThroughlineBuild.Phases/ReworkPhase.cs:62-70](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L62-L70)).
- **No feedback available:** `NoFeedbackAvailable` (exit 3). Supply `--feedback "..."` or run a `review` first to produce a `Rework` verdict in the event log ([src/ThroughlineBuild.Phases/ReworkPhase.cs:83-92](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L83-L92)).
- **Implement fails:** `ImplementFailed` (exit 4).
- Multiple ticket ids are rejected up front for `rework` ([src/ThroughlineBuild.Cli/Program.cs:90](../../src/ThroughlineBuild.Cli/Program.cs#L90)).

### `new` (`NewPhase`)

- **Body file missing / unreadable:** `NewPhaseValidationException` with the IO error.
- **Title missing:** `NewPhaseValidationException` "No title found: ...".
- **Body empty:** `NewPhaseValidationException` "Body is empty".
- **Non-fatal warnings** (missing Acceptance / Out of scope / Type, short body) are surfaced but do not block.
- Draft-mode failure paths add: draft worker non-Ok, an unresolvable `body_markdown_ref` -> `DRAFT_BODY` fenced block with no legacy `body_markdown` fallback, missing required sections ([src/ThroughlineBuild.Phases/DraftPhase.cs:70-84](../../src/ThroughlineBuild.Phases/DraftPhase.cs#L70-L84)).

### `scaffold` (`ScaffoldPhase`)

- **Parse errors** (hard, e.g. missing H1): abort with `WasAbortedByParseErrors` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:43-55](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L43-L55)).
- **Validation errors:** abort with `WasAbortedByValidationErrors`.
- **Warnings without `--accept-warnings`:** abort with `WasBlockedByWarnings`.
- **Operation-ticket create failure:** collected as a `ScaffoldFailure(op_create, ...)`; processing continues but plans cannot be parented ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:124-152](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L124-L152)).
- **Per-plan / per-brief create or parent-link failures:** collected in `ScaffoldFailure[]`; processing continues. Orphaned briefs are left visible for manual cleanup.
- **`--dry-run`** previews counts without API calls.
- **Not idempotent.** Reruns create duplicate operation/plan/brief tickets - no content-match against existing Plane tickets.

### `amend` / `close` / `defer` / `reopen`

Each rejects terminal state (or non-terminal for `reopen`) up front. `close`/`defer`/`reopen` build a `ReasonTranslator` lazily from `LlmClientFactory`; a missing Anthropic key **no longer fails the verb** (TLB-371). The `ConfigException` is caught, a `WARNING: LLM unavailable (...); recording reason verbatim without translation.` is logged, and an `EchoLlmClient` substitutes - the reason is recorded verbatim and the state transition still runs ([src/ThroughlineBuild.Cli/Program.cs:1727-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1727-L1737)). Reason translation is now the only LLM consumer in the deterministic CLI and is fully optional.

`close`/`defer` **cascade** the lifecycle transition to non-terminal children unless `--no-cascade`; a child cascade failure is logged to stderr and does not abort the parent ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-64](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L64), [src/ThroughlineBuild.Commands/DeferCommand.cs:48-64](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L48-L64)). They also produce **soft failures** for parent rollup and worktree decruft. `reopen` only notes a parent ticket - it does NOT reopen children ([src/ThroughlineBuild.Commands/ReopenCommand.cs:38-43](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L38-L43)).

### Loose ends

- Decompose's "partial create is success" rule means a half-created fan-out still stamps `[decomposed_at]`; a rerun then duplicates whatever did get created.
- The `ParallelDispatcher` "stop after level" behavior is coarser than the sequential `AncestorSkipFilter`; an unrelated ticket in a failed ticket's level may still have run.

---

## Cross-cutting failure modes

### Worker CLI missing

- The worker subprocess launch (`process.Start()`) catches `Win32Exception` and returns a soft `WorkerResult(Status.Failed, "Worker executable not found: '<path>'. Verify it is on PATH or set workers.claude-code.executable in config.toml ...")` rather than crashing the process (0f9d114) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:89-96](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L89-L96)). The owning phase then fails normally.

### Plane unreachable / throttled

- Every Plane HTTP call first awaits `RequestThrottle.AcquireAsync` - a hard rate gate admitting at most `RequestsPerMinute` (default 40) calls per rolling minute, blocking when the budget is spent so the process stays well under Plane's server-side 60/min limit ([src/ThroughlineBuild.Plane/RequestThrottle.cs](../../src/ThroughlineBuild.Plane/RequestThrottle.cs), [src/ThroughlineBuild.Plane/PlaneClientOptions.cs:20](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L20)). The 40/min default leaves headroom for a second `build` instance sharing the same token. The throttle is process-wide, so parallel dispatch and parent chains stay under budget. The TLB-366 per-run snapshot cache further cuts call volume: the project is paginated once, then `FindIssueAsync`/`QueryAsync` answer from memory instead of re-paginating per ticket (the root cause of the throttle pressure that grew with the project).
- On top of the gate, a Polly retry strategy retries up to `MaxRetryAttempts` (default 5) times on `PlaneApiException` with `Status == 429 || Status >= 500`, honoring a `Retry-After` header when present; other statuses throw immediately ([src/ThroughlineBuild.Plane/PlaneClientOptions.cs:26](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L26)). Unauthenticated (401/403) is not retried - it throws `PlaneApiException` and the CLI surfaces exit 1.
- **Snapshot truncation:** the snapshot load is capped at `MaxListPages = 50` (5000 issues); hitting the cap with a live cursor writes a loud stderr warning that the snapshot is truncated and out-of-cap lookups will throw "not found" ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:819-826](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L819-L826)).
- **Unknown ticket id in a multi-ticket chain:** `FindIssueAsync` throws `KeyNotFoundException` for a seq absent from the snapshot; the chain multi-ticket batch path catches it and exits 2 ("Ticket not found") rather than crashing unhandled ([src/ThroughlineBuild.Cli/Program.cs:1226-1231](../../src/ThroughlineBuild.Cli/Program.cs#L1226-L1231)).

### Anthropic rate-limited / key absent

- Polly retries inside `AnthropicClient`; failures propagate as `AnthropicApiException`. The Anthropic key is used only for `close`/`defer`/`reopen`'s `ReasonTranslator`; the `LlmClientFactory` is invoked lazily for just those verbs, so an absent key is **soft** for the worker-driven phases (plan/implement/review/ship/chain/decompose), which use the worker CLI's own auth, not the Anthropic SDK (TLB-227). Since TLB-371 the key is no longer hard-required even for those three verbs - an absent key degrades to an `EchoLlmClient` that records the reason verbatim rather than failing ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29), [src/ThroughlineBuild.Cli/Program.cs:1727-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1727-L1737)).

### git divergence / conflict

- `ship` probes the diverged local/remote target branch via `ProbeDivergenceAsync` (`git merge-tree`). `DivergedNoConflict` auto-rebases the local target onto `<remote>/<target>` (unless `--no-auto-merge`), emitting `TargetAutoRebased`; `DivergedWithConflict`, a raced auto-rebase, or `--no-auto-merge` produce a `GateFailure` and fail at `Fetch` ([src/ThroughlineBuild.Phases/ShipPhase.cs:305-345](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L305-L345)). See [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) "Divergence and merge orchestration".

### MainWorktreeLock contention

- The in-process `SemaphoreSlim` keyed on the normalized main-worktree path serializes the fetch / main-rebase / FF-merge across concurrent chains ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:6-29](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L6-L29)). Contention manifests as one chain waiting (not failing) for another's git op. It does NOT coordinate across separate `build` processes, so two concurrent invocations on the same repo can still race.

### Claude Code worker hangs

- `WorkerOptions.Timeout` (default 30 min via `workers.timeout_minutes`, verifier default 15 via `verifier_timeout_minutes`) triggers `CancellationTokenSource.CancelAfter` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:51](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L51), [src/ThroughlineBuild.Cli/Config.cs:218, 311](../../src/ThroughlineBuild.Cli/Config.cs#L218)). On cancellation the process tree is killed via `Process.Kill(entireProcessTree: true)` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:111](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L111)). Phase returns `Status.Failed`.

### Ctrl-C

- Every verb installs `Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };`. The active phase observes cancellation, kills worker subprocesses, and exits 1. Multi-ticket/parent dispatch also surfaces "Cancelled." and the dispatcher breaks with `failureReason = "cancelled"` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:74-79, 109-114](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L74-L79)).

### Disk full

- Event-log writes fail; the sink throws. The phase observes the exception and surfaces a phase failure.

### Process tree kill fails

- `process.Kill(entireProcessTree: true)` is wrapped in `try { ... } catch { }`; the exception is swallowed ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:111](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L111)). A stale subprocess can leak; the operator cleans up manually.

### Loose ends

- Since TLB-371 a missing/misconfigured Anthropic key is never a hard failure - even `close`/`defer`/`reopen` degrade to verbatim-reason recording - so a bad key surfaces only as a stderr warning, never an error exit.
- `RequestThrottle` (40/min) + Polly are process-scoped; they do not protect against multiple `build` processes collectively exceeding Plane's server-side 60/min. The snapshot cache only sees this process's own writes, so a concurrent second `build` mutating the same project is invisible until the next run reloads.

---

## Idempotency posture summary

`build`'s rerun safety is **state-driven**: each phase enforces a precondition on the ticket state, and SHA markers (`[planned_at]` / `[implemented_at]` / `[shipped_at]` / `[decomposed_at]`) act as forward-progress guards rather than de-dup keys.

- A phase that already transitioned the ticket will fail its state guard on rerun (exit 1/2). The notable change: a failed `plan` now leaves the ticket in `Planning` (not `Backlog`), so its rerun is no longer a clean replay.
- A phase that failed *before* transitioning is safe to rerun and replays the brief; Plane may accumulate duplicate comments / description appends if it died between writes - markers are not de-duplicated.
- **Chain / multi-ticket / parent chain** are safe to re-run: each ticket resumes from its landed state, and completed tickets are skipped by their own guards. The chain now actively *reconciles* stuck states rather than refusing them - an interrupted `Planning` ticket is reset and replanned, and an `InProgress` ticket either resumes its rework or has its orphan branch pruned and reset to `Ready` (only `Done`/`Cancelled` refuse).
- **Marker staleness no longer mis-triggers rework** (resolved). Both the implement drift check and the review reconstruction select the *freshest* marker by comment creation time rather than list order (TLB-412), and review attributes to the worktree HEAD when an implementer amended past its `[implemented_at]` marker (TLB-414) - previously a chain re-run could read a stale prior-run SHA and spuriously rework or review the wrong commit.
- The most expensive non-idempotent verbs are `scaffold` (duplicate operation/plan/brief tree) and `decompose` (duplicate child sub-issues) - both must be cleaned up by hand in Plane.

---

## Loose ends

- **No transactional Plane writes.** A phase interrupted between two writes leaves a partial state visible.
- **No rollback verb.** There is no `build undo`.
- **`scaffold` and `decompose` idempotency** are the biggest sharp edges - both create ticket trees with no content-match guard. Use `--dry-run` for scaffold.
- **Push failure leaves a local-only merge.** A `Push`-stage failure lands the FF merge in the local base branch but leaves the ticket `InReview` and the remote behind; the operator must push manually or rerun.
- **Decruft / branch-delete failures during ship are non-fatal** (post-`Done`) but only visible on stderr - scripted callers might miss them.
- **`Status.Escalate`** is now differentially handled in the chain (obsolete-claim ratification) but not in standalone `plan`/`implement` verbs, which just return the failure.
- **Ctrl-C in the middle of a Plane write** can leave the ticket half-updated because each HTTP call is atomic but a phase makes several.
