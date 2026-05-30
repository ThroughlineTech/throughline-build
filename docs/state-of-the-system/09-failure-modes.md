# 09 - Failure Modes and Idempotency

For each major operation, how it fails and whether re-running is safe. Exit codes summarized in [06-public-surfaces.md](06-public-surfaces.md); chain/dispatch outcomes in [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Failure-mode summary table

| Operation | Pre-flight gates | Common failures | Failed-at state | Idempotent on rerun? |
|---|---|---|---|---|
| `plan` | ticket exists; not a parent; state == `Backlog`; `git rev-parse main` resolves | worker non-Ok status; missing required metadata keys (`plan_html`, `risk_label`, `size_label`, `planned_at_sha`) | ticket left in `Planning` once the worker has run (transition precedes the status check) | partial - rerun fails the `Backlog` guard once parked in `Planning`; operator must reset state |
| `implement` | ticket exists; not a parent; state == `Ready` (initial) or `InProgress` (rework); `git worktree add` succeeds | worktree creation fails; worker non-Ok; missing `commit_sha` metadata | `Ready` if pre-worker; `InProgress` if worker ran but didn't deliver | yes if worktree was created (rerun reuses it via the rework path) |
| `review` | ticket exists; state == `InReview`; worktree locatable; `[implemented_at]` marker present | check timeout (non-fatal); verifier subprocess crash; missing verdict metadata | state unchanged (only `Rework` changes it) | yes - rerun re-runs checks and verifier; one extra verdict comment posted |
| `ship` | ticket exists; state == `InReview`; worktree locatable; build.exe not inside it; both worktrees clean; bases not diverged-with-conflict; rebase succeeds; no conflict markers; regression checks pass; FF merge + push succeed | listed at each stage via `ShipFailureStage` | enum value identifies stage (`StateCheck`, `PreFlight`, `Fetch`, `Rebase`, `ConflictMarkerScan`, `RegressionChecks`, `FastForwardMerge`, `Push`, `Decruft`) | partially - rebase + checks idempotent; post-merge transitions not retried by `ship` itself |
| `decompose` | ticket exists; `git rev-parse main` resolves | worker non-Ok; malformed / <2 `child_specs`; `DecomposeVerdict` quality-gate failure; all child creates fail | no parent transition (decompose never moves the parent state) | no - rerun creates duplicate child sub-issues |
| `chain` | starting state `Backlog`/`Ready`/`InReview`, or has children (parent path) | any inner phase failure propagates as `StoppedAt*`; rework cap; obsolete escalation; parent-tree refusals | `ChainOutcome` value identifies stop point | yes - rerunning starts at whatever state the ticket landed in |
| `chain` (multi-ticket / parent) | per-ticket as above; dependency graph acyclic; parent tree at most one level deep | cycle in `blocked_by` graph; a level/child fails; grandchildren present | `ParallelDispatchResult.FailureReason` / `ParentHasGrandchildren` / `ParentStoppedEarly` | yes - completed tickets are skipped on re-entry by their state |
| `rework` | state == `InProgress`; manual `--feedback` or a `Rework` verdict in event log | feedback retrieval fails; underlying `ImplementPhase` fails | `ImplementFailed`, `NoFeedbackAvailable`, `TicketNotInProgress` | yes |
| `new` (file mode) | body file readable; title present | validation throws on missing title / empty body | nothing to roll back | yes - duplicates the Plane ticket on rerun |
| `new` (draft mode) | worker dispatchable | draft worker fails / wrong shape; user quits review loop | nothing posted | yes |
| `scaffold` | op-doc parses; validation passes (or `--accept-warnings`) | per-ticket create or parent-link failures collected in `ScaffoldFailure[]` | partial creation possible (operation/plan/brief tree) | no - rerun creates duplicates; nothing is matched back by content |
| `amend` | state not terminal; at least one of `--size` / `--note` | invalid size value; terminal state | nothing | yes - replacing labels is idempotent; appending notes accumulates |
| `close` / `defer` | state not terminal; reason supplied; Anthropic key present | translator failure (fatal); child cascade failure (soft); rollup failure (soft); decruft failure (soft) | nothing pre-translation; `Cancelled` after transition (+ cascaded children) | no - rerunning on a `Cancelled` ticket fails the state check |
| `reopen` | state is `Done` or `Cancelled`; (translator if reason supplied) | translator failure (fatal); ambiguous target defaults to `Backlog` | nothing pre-transition | yes once back in active state - rerun fails the state check |

### Loose ends

- The `plan` idempotency posture changed: the `Backlog -> Planning` transition now happens *before* the worker-status check ([src/ThroughlineBuild.Phases/PlanPhase.cs:98](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98)), so a failed plan no longer leaves the ticket cleanly re-runnable in `Backlog`.

---

## Per-phase failure detail

### `plan` (`PlanPhase`)

- **Parent ticket:** returns failure "is a parent ticket with N children: ... plan each child individually" ([src/ThroughlineBuild.Phases/PlanPhase.cs:60-63](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L60-L63)).
- **Wrong state:** "ticket not in Backlog state" ([src/ThroughlineBuild.Phases/PlanPhase.cs:65-66](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L65-L66)). CLI exit 1.
- **`git rev-parse` failure:** "git rev-parse failed: ..." ([src/ThroughlineBuild.Phases/PlanPhase.cs:73-76](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L73-L76)).
- **Worker failure:** worker `Status != Ok` returns the envelope reason ([src/ThroughlineBuild.Phases/PlanPhase.cs:105-108](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L105-L108)). The ticket is already in `Planning` at this point. If the status is `Escalate`, the `WorkerResult` is returned as `EscalationWorkerResult` so the chain can run obsolete-claim ratification.
- **Missing metadata keys:** "worker metadata missing required keys (...)" ([src/ThroughlineBuild.Phases/PlanPhase.cs:120-122](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L120-L122)).
- **Idempotency caveat:** a prior run that posted the description but died before the marker comment will append the description a second time on rerun - the append is `existing + html` so duplication is visible.

### `implement` (`ImplementPhase`)

- **Parent ticket:** refuses with "is a parent ticket with N children: work child-by-child ..."; writes `phase-status.json` via `EarlyExitManifest` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:57-64](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L57-L64)).
- **Wrong state:** writes `phase-status.json` and returns; the message distinguishes "initial round invoked but ticket is in X - did you mean to invoke rework?" from "rework round invoked but ticket is in X" ([src/ThroughlineBuild.Phases/ImplementPhase.cs:66-79](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L66-L79)).
- **Worktree creation fails (initial):** returns "worktree create failed: ..." ([src/ThroughlineBuild.Phases/ImplementPhase.cs:143-155](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L143-L155)). Common cause: branch already exists.
- **Missing rework worktree:** rework requires the existing worktree on disk; absence is a hard early-exit ([src/ThroughlineBuild.Phases/ImplementPhase.cs:132-140](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L132-L140)).
- **Worker fails after worktree created:** ticket already `InProgress`; stays `InProgress`. Rerun goes through the rework path. `Escalate` is carried back as `EscalationWorkerResult` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:203-206](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L203-L206)).
- **Missing `commit_sha`:** "worker metadata missing commit_sha" ([src/ThroughlineBuild.Phases/ImplementPhase.cs:209-212](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L209-L212)).
- **`commit_sha` mismatch with actual HEAD:** actual HEAD wins; a discrepancy note is folded into the `implemented_at` comment ([src/ThroughlineBuild.Phases/ImplementPhase.cs:214-223](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L214-L223)). Informational only.
- **Drift warning:** emitted as `GateFailure` but does not block ([src/ThroughlineBuild.Phases/ImplementPhase.cs:114-122](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L114-L122)).

### `review` (`ReviewPhase`)

- **Parent ticket:** takes the aggregate-review branch instead of failing - see "Parent-tree failure modes".
- **Wrong state:** "ticket not in InReview state" ([src/ThroughlineBuild.Phases/ReviewPhase.cs:73-75](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L73-L75)).
- **Worktree not found:** "feature worktree not found at ..." ([src/ThroughlineBuild.Phases/ReviewPhase.cs:100-102](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L100-L102)).
- **No `[implemented_at]` marker:** "no implemented_at marker found - ..." ([src/ThroughlineBuild.Phases/ReviewPhase.cs:136-138](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L136-L138)).
- **Check timeout:** the check is marked failed; the verifier sees it in the brief. The phase does not abort.
- **Verifier subprocess crash:** propagates as a phase infra failure (CLI maps to exit 4 on the standalone `review` verb).
- **Verdict `Pass`:** no transition; ticket stays `InReview` for `ship`.
- **Verdict `Rework`:** transitions back to `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:216-221](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L216-L221)).
- **Verdict `Fail`:** no transition; operator decides.

The default verifier is `WorkerAgentReviewer` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:162-163](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L162-L163)); it runs the verifier worker inside the feature worktree so it cannot dirty tracked files in main and block the subsequent ship pre-flight. (The former `ClaudeCodeReviewer` class no longer exists.)

### `ship` (`ShipPhase`)

By stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:23-34](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L23-L34)):

| Stage | Trigger | Recovery |
|---|---|---|
| `StateCheck` | ticket not `InReview`; worktree not found; (parent) children not Done | fix state via `review`/`implement`; recreate worktree; finish children |
| `PreFlight` | build.exe running from inside the worktree; either worktree dirty | move binary; commit or stash; rerun |
| `Fetch` | `git fetch` failed; bases diverged-with-conflict, or diverged and `--no-auto-merge`, or auto-rebase raced to conflict | resolve `main` vs `origin/main` manually; rerun |
| `Rebase` | rebase conflicts; rebase fails otherwise. Aborted by `RebaseAbortAsync` | resolve conflicts on the feature branch; rerun |
| `ConflictMarkerScan` | leftover `<<<<<<<` / `=======` / `>>>>>>>` in committed files | clean up; recommit; rerun |
| `RegressionChecks` | a `CheckSpec` returned non-zero or timed out | fix on the feature branch; rerun |
| `FastForwardMerge` | rare - usually a race | refetch; rerun |
| `Push` | `git push <remote> <baseBranch>` failed after the local FF merge already landed | the merge is local-only until push succeeds; push manually or rerun |
| `Decruft` | post-merge worktree cleanup failed | merge already landed; clean up manually |

The fetch, the main auto-rebase, and the FF merge run under `MainWorktreeLock` so concurrent chains do not race on the shared main worktree ([src/ThroughlineBuild.Phases/ShipPhase.cs:194, 243-246, 380-383](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L194)).

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

Wraps the others. Single-ticket exit codes are remapped from `ChainOutcome` ([src/ThroughlineBuild.Cli/Program.cs:1359-1373](../../src/ThroughlineBuild.Cli/Program.cs#L1359-L1373)):

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RatifiedObsolete` | 0 | obsolete claim ratified -> Done |
| `ParentCompleted` | 0 | all eligible children completed |
| `RefusedInitialState` | 2 | state not `Backlog`/`Ready`/`InReview` |
| `ParentHasGrandchildren` | 2 | tree deeper than one level |
| `StoppedAtPlan` | 3 | planning failed |
| `ParentStoppedEarly` | 3 | a child did not complete |
| `Skipped` | 3 | ancestor failed and `--continue-past-failure` absent |
| `StoppedAtImplement` | 4 | implementation failed |
| `StoppedAtReview` | 5 | review returned `Fail` (or review infra failure) |
| `ReworkCapExceeded` | 6 | more than `MaxReworkRounds` (2) reworks |
| `StoppedAtShip` | 7 | ship gate failed |

The chain emits `ChainStart`/`ChainEnd` around every run, `ReworkRound` per rework, and `TicketSubsumed` when an obsolete claim is ratified. See [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) for the full sequence.

### Parent-tree failure modes (chain on a parent)

- **Grandchildren present:** if any eligible child has live children of its own, the chain returns `ParentHasGrandchildren` (exit 2) without dispatching anything; the operator must chain the intermediate ticket directly ([src/ThroughlineBuild.Phases/ChainPhase.cs:548-581](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L548-L581)). This guard (f3953f7) prevents the runaway recursion that previously hammered Plane's rate limiter.
- **A child stops early:** the parent chain runs all leaf children (bounded at `MaxParentChainConcurrency = 4`); if any child outcome is not in the success set, the parent returns `ParentStoppedEarly` ([src/ThroughlineBuild.Phases/ChainPhase.cs:622-641](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L622-L641)). Children that already completed are not retried.
- **Aggregate review of a parent:** any child still `InProgress`/`InReview` yields `Rework`; some children not Done (and not in-flight) yields `Fail`; all Done yields `Pass` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:254-310](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L254-L310)).
- **Parent ship gate:** blocks at `StateCheck` with "children not Done: ..." unless every child is `Done`, then transitions the parent straight to `Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:478-517](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L478-L517)).

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
- Draft-mode failure paths add: draft worker non-Ok, missing `body_markdown`, missing required sections.

### `scaffold` (`ScaffoldPhase`)

- **Parse errors** (hard, e.g. missing H1): abort with `WasAbortedByParseErrors` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:43-55](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L43-L55)).
- **Validation errors:** abort with `WasAbortedByValidationErrors`.
- **Warnings without `--accept-warnings`:** abort with `WasBlockedByWarnings`.
- **Operation-ticket create failure:** collected as a `ScaffoldFailure(op_create, ...)`; processing continues but plans cannot be parented ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:124-152](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L124-L152)).
- **Per-plan / per-brief create or parent-link failures:** collected in `ScaffoldFailure[]`; processing continues. Orphaned briefs are left visible for manual cleanup.
- **`--dry-run`** previews counts without API calls.
- **Not idempotent.** Reruns create duplicate operation/plan/brief tickets - no content-match against existing Plane tickets.

### `amend` / `close` / `defer` / `reopen`

Each rejects terminal state (or non-terminal for `reopen`) up front. `close`/`defer`/`reopen` build a `ReasonTranslator` lazily from `LlmClientFactory`; a missing Anthropic key surfaces as a `ConfigException` message and an error return *only for these verbs* ([src/ThroughlineBuild.Cli/Program.cs:1605-1617](../../src/ThroughlineBuild.Cli/Program.cs#L1605-L1617)).

`close`/`defer` **cascade** the lifecycle transition to non-terminal children unless `--no-cascade`; a child cascade failure is logged to stderr and does not abort the parent ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-64](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L64), [src/ThroughlineBuild.Commands/DeferCommand.cs:48-64](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L48-L64)). They also produce **soft failures** for parent rollup and worktree decruft. `reopen` only notes a parent ticket - it does NOT reopen children ([src/ThroughlineBuild.Commands/ReopenCommand.cs:38-43](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L38-L43)).

### Loose ends

- Decompose's "partial create is success" rule means a half-created fan-out still stamps `[decomposed_at]`; a rerun then duplicates whatever did get created.
- The `ParallelDispatcher` "stop after level" behavior is coarser than the sequential `AncestorSkipFilter`; an unrelated ticket in a failed ticket's level may still have run.

---

## Cross-cutting failure modes

### Worker CLI missing

- The worker subprocess launch (`process.Start()`) catches `Win32Exception` and returns a soft `WorkerResult(Status.Failed, "Worker executable not found: '<path>'. Verify it is on PATH or set workers.claude-code.executable in config.toml ...")` rather than crashing the process (0f9d114) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:89-96](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L89-L96)). The owning phase then fails normally.

### Plane unreachable / throttled

- Every Plane HTTP call first awaits `RequestThrottle.AcquireAsync` - a hard rate gate admitting at most `RequestsPerMinute` (default 60) calls per rolling minute, blocking when the budget is spent so the process never trips a 429 ([src/ThroughlineBuild.Plane/RequestThrottle.cs:13-75](../../src/ThroughlineBuild.Plane/RequestThrottle.cs#L13-L75), [src/ThroughlineBuild.Plane/PlaneClientOptions.cs:17](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L17)). The throttle is process-wide, so parallel dispatch and parent chains stay under budget.
- On top of the gate, a Polly retry strategy retries up to 3 times on `PlaneApiException` with `Status == 429 || Status >= 500`; other statuses throw immediately ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:54-58](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L54-L58)). Unauthenticated (401/403) is not retried - it throws `PlaneApiException` and the CLI surfaces exit 1.

### Anthropic rate-limited / key absent

- Polly retries inside `AnthropicClient`; failures propagate as `AnthropicApiException`. The Anthropic key is only required for `close`/`defer`/`reopen`'s `ReasonTranslator`; the `LlmClientFactory` is invoked lazily for just those verbs, so an absent key is **soft** for the worker-driven phases (plan/implement/review/ship/chain/decompose), which use the worker CLI's own auth, not the Anthropic SDK (TLB-227) ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29), [src/ThroughlineBuild.Cli/Program.cs:1605-1617](../../src/ThroughlineBuild.Cli/Program.cs#L1605-L1617)).

### git divergence / conflict

- `ship` probes diverged local/remote `main` via `ProbeDivergenceAsync` (`git merge-tree`). `DivergedNoConflict` auto-rebases local main onto origin (unless `--no-auto-merge`), emitting `MainAutoRebased`; `DivergedWithConflict`, a raced auto-rebase, or `--no-auto-merge` produce a `diverged_bases` `GateFailure` and fail at `Fetch` ([src/ThroughlineBuild.Phases/ShipPhase.cs:230-298](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L230-L298)). See [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) "Divergence and merge orchestration".

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

- The Anthropic key being soft for worker phases means a misconfigured key is not caught until a `close`/`defer`/`reopen` runs.
- `RequestThrottle` + Polly are process-scoped; they do not protect against multiple `build` processes collectively exceeding Plane's 60/min.

---

## Idempotency posture summary

`build`'s rerun safety is **state-driven**: each phase enforces a precondition on the ticket state, and SHA markers (`[planned_at]` / `[implemented_at]` / `[shipped_at]` / `[decomposed_at]`) act as forward-progress guards rather than de-dup keys.

- A phase that already transitioned the ticket will fail its state guard on rerun (exit 1/2). The notable change: a failed `plan` now leaves the ticket in `Planning` (not `Backlog`), so its rerun is no longer a clean replay.
- A phase that failed *before* transitioning is safe to rerun and replays the brief; Plane may accumulate duplicate comments / description appends if it died between writes - markers are not de-duplicated.
- **Chain / multi-ticket / parent chain** are safe to re-run: each ticket resumes from its landed state, and completed tickets are skipped by their own guards.
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
