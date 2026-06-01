# 10 - Lifecycle and Orchestration

The Agile phase state machine implemented by `build` - what each phase does, how the chain orchestrator transitions between them, the multi-ticket and tree-aware dispatch paths, and the rework loop bounded by `MaxReworkRounds`.

For per-phase failure modes see [09-failure-modes.md](09-failure-modes.md). For inter-project types see [07-contracts.md](07-contracts.md).

---

## The state machine

```
                                   ChainPhase routes here based on initial state
                                                      |
                                                      v
        Backlog        Planning       Ready        InProgress     InReview        Done       Cancelled
            \             |             |              |             |              |             |
             \            |             |              |             |              |             |
              plan -------+-> plan -----+              |             |              |             |
                                        v              |             |              |             |
                                        +-> implement -+             |              |              |
                                                       v             |              |              |
                                                       +-> implement-+              |              |
                                                                     v              |              |
                                                                     +-> review ----+              |
                                                                     ^              |              |
                                                                     |              |              |
                                              Rework verdict --------+              |              |
                                                                                    v              |
                                                                                    +-> ship ----->+
                                                                                                   ^
                                                                              close / defer ------+
                                                                                                  |
                                                                              reopen <-----+ +----+
                                                                                           |
                                                                                           v
                                                                                  Backlog / Ready
```

Backed by these transitions in code:

| Phase | Source state | Target state(s) |
|---|---|---|
| `plan` | `Backlog` | `Planning` -> `Ready` ([src/ThroughlineBuild.Phases/PlanPhase.cs:98, 143-148](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98)) |
| `implement` (initial) | `Ready` | `InProgress` -> `InReview` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:159-167, 231-236](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L159-L167)) |
| `implement` (rework) | `InProgress` | `InReview` (no `InProgress` re-entry) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:159-167](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L159-L167)) |
| `review` (Pass / Fail) | `InReview` | no change ([src/ThroughlineBuild.Phases/ReviewPhase.cs:196-234](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L196-L234)) |
| `review` (Rework) | `InReview` | `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:216-221](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L216-L221)) |
| `ship` | `InReview` | `Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:411](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L411)) |
| `decompose` | any | no transition; posts `[decomposed_at: <sha>]` + creates N>=2 child sub-issues ([src/ThroughlineBuild.Phases/DecomposePhase.cs:136-149](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L136-L149)) |
| `close` | non-terminal | `Cancelled` (+ cascade to non-terminal children) ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-76](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L76)) |
| `defer` | non-terminal | `Cancelled` (+ cascade to non-terminal children) ([src/ThroughlineBuild.Commands/DeferCommand.cs:48-76](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L48-L76)) |
| `reopen` | `Done` / `Cancelled` | `Backlog` or `Ready` (decided by `DetermineTargetState`; children NOT reopened) ([src/ThroughlineBuild.Commands/ReopenCommand.cs:78-128](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L78-L128)) |
| `new` | n/a | new ticket in Plane default state (`Backlog`) |
| `scaffold` | n/a | 1 operation-ticket + N plan-tickets + M brief-tickets in `Backlog` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:116-289](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L116-L289)) |

`TicketState` enum: `Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled` ([src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs)).

Plane mirror state names: `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled` (hardcoded reverse map in [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs)).

### Loose ends

- The diagram does not show the `decompose` fan-out (one ticket -> N children) or the tree-aware parent paths; those are below in "Tree-aware chain".
- Architecture doc ([docs/throughline-build-architecture.md](../throughline-build-architecture.md)) Section 4 predates the `Decompose`, parent-chain, ratify, and divergence transitions; the code above is authoritative.

---

## Phase implementations

Every `*Phase` class in [src/ThroughlineBuild.Phases/](../../src/ThroughlineBuild.Phases/) (and `ScaffoldPhase` in `ThroughlineBuild.Scaffold`):
`PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `DecomposePhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `ScaffoldPhase`. The `Phase` enum has 10 values: `Plan, Implement, Review, Ship, Chain, New, Command, Draft, Scaffold, Decompose` ([src/ThroughlineBuild.Contracts/Models/Phase.cs:3](../../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3)).

### `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs](../../src/ThroughlineBuild.Phases/PlanPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/PlanPhase.cs:56-151](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L56-L151)):
1. Fetch ticket.
2. Parent guard: refuse if the ticket has children - parent containers do not get plans ([src/ThroughlineBuild.Phases/PlanPhase.cs:60-63](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L60-L63)).
3. State guard: `Backlog`.
4. Resolve `main` SHA via `BaseRefResolver`.
5. Build `RepoState` (top-level entries + main SHA).
6. Build brief via `PlanBriefBuilder`.
7. Emit `WorkerSpawn` event.
8. Run worker.
9. Transition `Backlog -> Planning` *before* checking the worker status ([src/ThroughlineBuild.Phases/PlanPhase.cs:98](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98)). A worker failure thus leaves the ticket parked in `Planning`, not `Backlog`.
10. Emit `VerifierVerdict` (status of the worker run).
11. On worker `Status != Ok`: return; if the status is `Escalate`, the `WorkerResult` is carried back as `EscalationWorkerResult` for obsolete-claim ratification ([src/ThroughlineBuild.Phases/PlanPhase.cs:105-108](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L105-L108)).
12. Optionally emit `LlmCall` from worker metadata.
13. Resolve the plan body: `FencedBlockResolver.TryResolveRef(blocks, metadata, "plan_body_ref")` -> `PLAN_BODY` block; render to HTML via `MarkdownRenderer.Render` ([src/ThroughlineBuild.Phases/PlanPhase.cs:120-125](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L120-L125)). Validate the scalar keys (`risk_label`, `size_label`, `planned_at_sha`).
14. Append the rendered plan HTML to description.
15. Apply merged risk + size labels.
16. Post `[planned_at: <sha>]` comment.
17. Transition `Planning -> Ready`.

### `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs](../../src/ThroughlineBuild.Phases/ImplementPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ImplementPhase.cs:52-241](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L52-L241)):
1. Fetch ticket. Detect rework round vs. initial by `ImplementPhaseOptions.ReviewFeedback` presence.
2. Parent guard: refuse to implement a ticket that has children; writes `phase-status.json` via `EarlyExitManifest` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:57-64](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L57-L64)).
3. State guard: `Ready` (initial) or `InProgress` (rework). On guard failure, write `phase-status.json`.
4. Resolve base ref + main SHA.
5. Compute deterministic worktree names via `PhaseWorktreeLayout`.
6. Drift check: scan comments for `[planned_at: <sha>]`; emit `GateFailure` drift warning if it differs from current main SHA (does not block).
7. Build `ImplementBriefBuilder` with branch / worktree / optional `ReviewFeedback` for rework.
8. Initial: `git worktree add`. Rework: require the existing worktree on disk, else early-exit. ([src/ThroughlineBuild.Phases/ImplementPhase.cs:131-156](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L131-L156))
9. Transition `Ready -> InProgress` only on initial round.
10. Emit `WorkerSpawn`. Run worker inside the worktree.
11. Emit `VerifierVerdict` (worker status). On non-Ok, return early; `Escalate` is carried as `EscalationWorkerResult` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:203-206](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L203-L206)).
12. Validate `commit_sha` metadata. Compare against actual `git rev-parse HEAD` in worktree; actual wins on discrepancy (a discrepancy note is folded into the marker comment).
13. Post `[implemented_at: <actualSha>]` comment naming the branch; if the worker supplied a `summary_ref` -> `IMPLEMENT_SUMMARY` fenced block, render it via `MarkdownRenderer` and append it to the comment ([src/ThroughlineBuild.Phases/ImplementPhase.cs:252-271](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L252-L271)).
14. Transition `InProgress -> InReview`.

### `ReviewPhase` ([src/ThroughlineBuild.Phases/ReviewPhase.cs](../../src/ThroughlineBuild.Phases/ReviewPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ReviewPhase.cs:60-237](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L60-L237)):
1. Fetch ticket. Parent-ticket aggregate-review branch if it has children (see "Tree-aware chain").
2. State guard `InReview`.
3. Locate the worktree by branch or path via `git worktree list`.
4. Resolve base ref + main SHA.
5. Reconstruct an implementer brief (so the verifier sees what the implementer was told) via `ImplementBriefBuilder.Build` without `ReviewFeedback`. This is reconstructed deterministically - not a fresh worker invocation.
6. Extract `[implemented_at: <sha>]` marker; required.
7. Compute the diff `baseRef..featureBranch` with patches; synthesize a `WorkerResult` from marker + diff to hand to the verifier (no re-execution of the implement worker).
8. Run automated checks via `AutomatedChecksRunner.RunAsync(...)`.
9. Construct the verifier - default `WorkerAgentReviewer`, which spawns the verifier worker against the review brief inside the feature worktree ([src/ThroughlineBuild.Phases/ReviewPhase.cs:162-163](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L162-L163)). An `IVerifier` override may be injected.
10. Emit `WorkerSpawn` (role=verifier).
11. Run verifier; emit `VerifierVerdict` (kind, rationale, checks_failed).
12. Optionally emit `LlmCall` if the `WorkerAgentReviewer`'s last worker result carries usage ([src/ThroughlineBuild.Phases/ReviewPhase.cs:185-192](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L185-L192)).
13. Apply verdict:
    - `Pass` -> post `reviewed: pass` comment, no transition.
    - `Rework` -> post `reviewed: rework` comment, transition `InReview -> InProgress`.
    - `Fail` -> post `reviewed: fail` comment, no transition (operator decides).

### `ShipPhase` ([src/ThroughlineBuild.Phases/ShipPhase.cs](../../src/ThroughlineBuild.Phases/ShipPhase.cs))

Status: **Functional**.

Deterministic - no LLM, no worker. The merge **target** is `[work].target_branch` if set, else `[ship].base_branch` (resolved by `BuildConfig.ResolveTargetBranch()` and carried on `ShipOptions.TargetBranch`, [src/ThroughlineBuild.Phases/ShipPhase.cs:225](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L225)). Step sequence:
1. Fetch ticket. Parent-ticket ship branch if it has children (see "Tree-aware chain").
2. State guard `InReview`.
3. Locate worktree (by `ticket/<id>-` prefix; falls back to creating one from a matching local branch).
4. Pre-flight: `build` binary not running from inside the worktree.
5. Pre-flight: both feature and main worktrees clean of tracked changes.
6. Pre-flight (non-default target only): the main worktree is checked out on the target branch, else `wrong_worktree_branch` `GateFailure` and fail at `PreFlight` ([src/ThroughlineBuild.Phases/ShipPhase.cs:227-247](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L227-L247)).
7. Conditionally fetch from remote. If no remote: skip, use the local target branch.
8. Determine rebase target via divergence handling (see "Divergence and merge orchestration"). Fetch and the auto-rebase of the local target branch are wrapped in `MainWorktreeLock`.
9. Emit `base_ref_resolved` (a `TicketWrite` event).
10. Rebase feature branch onto resolved target ref. On conflict: `git rebase --abort`, fail at `Rebase`.
11. Conflict-marker scan of the rebased diff's files.
12. Run `ship.regression_checks`. Under `--debug` all check results stream to stderr; otherwise only failed checks do.
13. Fast-forward merge into the target branch, under `MainWorktreeLock` ([src/ThroughlineBuild.Phases/ShipPhase.cs:496-498](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L496-L498)).
14. Push the target branch to remote when a remote exists; failure fails the ship at the `Push` stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:509-510](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L509-L510)).
15. Read merged HEAD SHA.
16. Post `[shipped_at: <mergedSha>]` comment.
17. Transition `InReview -> Done`.
18. `WorktreeDecrufter.DecruftAsync` (failure non-fatal post-merge).
19. Optionally `git branch -d ticket/<slug>` (failure non-fatal).

`ShipPhase` emits phase-level progress lines ("[ship] fetching...", "[ship] merging into <target>...") to its progress writer, and under `--debug` (verbose) also streams raw git output.

### `DecomposePhase` ([src/ThroughlineBuild.Phases/DecomposePhase.cs](../../src/ThroughlineBuild.Phases/DecomposePhase.cs))

Status: **Functional**. Added by TLB-259/264/265. Fans one ticket out into independently shippable child sub-issues. No state transition on the parent.

Step sequence ([src/ThroughlineBuild.Phases/DecomposePhase.cs:52-149](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L52-L149)):
1. Fetch ticket; resolve main SHA via `BaseRefResolver`.
2. Build `RepoState` + brief via `DecomposeBriefBuilder`.
3. Emit `WorkerSpawn`. Run worker.
4. Emit `VerifierVerdict` (worker status). On non-Ok, fail.
5. Optionally emit `LlmCall`.
6. Extract `child_specs` from worker metadata; require at least 2 ([src/ThroughlineBuild.Phases/DecomposePhase.cs:102-108](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L102-L108)).
7. Rule-based `DecomposeVerdict.Check` quality gate over the specs ([src/ThroughlineBuild.Phases/DecomposeVerdict.cs:5-29](../../src/ThroughlineBuild.Phases/DecomposeVerdict.cs#L5-L29)):
   - `coverage_check`: every child has a non-empty `scope_boundary`.
   - `uniqueness_check`: no two children share a title (case-insensitive).
   - `size_check`: every child size is one of S / M / L.
   On any failure, emit a `VerifierVerdict` with `status=VerdictFailed` + `checks_failed`, and return failure (no tickets created).
8. On pass: emit `VerifierVerdict` `status=VerdictPassed`.
9. Map each `ChildSpec` to a `ChildTicketSpec` (description HTML + size label) and call `CreateChildTicketsAsync` to create sub-issues parented to this ticket. If all creations fail, return failure.
10. Post `[decomposed_at: <mainSha>]` comment.

The verdict is **rule-based**, not LLM-driven - the worker produces the specs, `DecomposeVerdict` validates them deterministically.

### `ChainPhase` ([src/ThroughlineBuild.Phases/ChainPhase.cs](../../src/ThroughlineBuild.Phases/ChainPhase.cs))

Status: **Functional**. The orchestrator. Constructed in `Program.cs` with per-phase factories closed over the shared `PlaneTicketingClient`, worker agents, and `IEventSink`, plus a `ratifierFactory` for obsolete-claim handling ([src/ThroughlineBuild.Cli/Program.cs:1201-1210](../../src/ThroughlineBuild.Cli/Program.cs#L1201-L1210)).

`RunAsync` entry ([src/ThroughlineBuild.Phases/ChainPhase.cs:51-224](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L51-L224)):

1. Fetch the ticket. Query its children; if any exist, delegate to `RunParentChainAsync` (the tree-aware path) and return ([src/ThroughlineBuild.Phases/ChainPhase.cs:60-65](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L60-L65)).
2. Otherwise route on state (`ResolveEntryAsync`):
   - `Backlog` -> start at Plan
   - `Ready` -> start at Implement
   - `InReview` -> start at Review
   - `Planning` -> a plan that never finished (the `Backlog -> Planning` transition precedes the worker, and no plan artifact is written until it succeeds): reset to `Backlog`, emit a `chain_resume` `StateTransition`, and start at Plan
   - `InProgress` -> resume an interrupted implement. If the ticket's branch has **no commits beyond base** (an interrupted *initial* implement transitions `Ready -> InProgress` before the worker commits), prune the orphaned branch/worktree, reset to `Ready`, and start a clean Implement - in a parent chain this lets the branch be recreated inside the shared worktree instead of an orphaned standalone one. If the branch **has commits** (interrupted rework), resume in place via the rework path (round 1, reusing the worktree), recovering the last `Rework` feedback from the event log or synthesizing a neutral resume note
   - `Done` / `Cancelled` -> emit `ChainStart`, return `RefusedInitialState` (the only genuinely un-runnable states)
3. Emit `ChainStart`.
4. If starting at Plan: run `PlanPhase`. On failure, if `!NoAutoResolve` and the worker `Escalate`d with reason `obsolete`, run obsolete-claim ratification (see "Obsolete-claim handling"). Otherwise return `StoppedAtPlan`.
5. If Plan succeeded or starting at Implement: enter the implement-review loop (`RunImplementReviewLoopAsync`).
6. If starting at Review only: run one review (`RunReviewBranchAsync`). Pass -> Ship; Rework -> implement-review loop; Fail -> `StoppedAtReview`.
7. Run `ShipPhase`. Fail -> `StoppedAtShip`.
8. Emit `ChainEnd`. Return `Completed`.

The chain invokes an `Action<ChainStep>` callback (`OnStep`) after each phase; `ChainCommand` uses it to stream one-line summaries to stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)).

Each phase runs under its own per-phase session id minted from `_sessionIdGenerator`; the chain itself has a single `chainSessionId` used on `ChainStart`/`ChainEnd` ([src/ThroughlineBuild.Phases/ChainPhase.cs:56, 107-108](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L56)).

### `ReworkPhase` ([src/ThroughlineBuild.Phases/ReworkPhase.cs](../../src/ThroughlineBuild.Phases/ReworkPhase.cs))

Status: **Functional**. Thin wrapper.

1. State guard: `InProgress` ([src/ThroughlineBuild.Phases/ReworkPhase.cs:62-70](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L62-L70)).
2. Resolve feedback: manual `--feedback` text wins; otherwise read latest `Rework` verdict from the event log via an `IReviewFeedbackRetriever` ([src/ThroughlineBuild.Phases/ReworkPhase.cs:76-96](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L76-L96)). `ReworkRoundNumber` from options overrides the retrieved value.
3. Build `ImplementPhaseOptions` carrying the feedback.
4. Construct and invoke `ImplementPhase` (rework round) ([src/ThroughlineBuild.Phases/ReworkPhase.cs:102-103](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L102-L103)).

### `NewPhase` ([src/ThroughlineBuild.Phases/NewPhase.cs](../../src/ThroughlineBuild.Phases/NewPhase.cs))

Status: **Functional**. Deterministic creator.

1. Read body file. Validate title; collect non-fatal warnings.
2. Emit `WorkerSpawn` with role=creator (no actual worker; the event is for audit symmetry).
3. `ITicketing.CreateTicketAsync`.
4. Emit `TicketWrite` with `create_ticket` action. No `LlmCall` is emitted for this deterministic phase.

### `DraftPhase` ([src/ThroughlineBuild.Phases/DraftPhase.cs](../../src/ThroughlineBuild.Phases/DraftPhase.cs))

Status: **Functional**. Used by `build new` in draft mode and stdin draft mode.

1. Validate non-empty operator text.
2. Build `DraftBriefBuilder` brief.
3. Run worker.
4. Resolve `body_markdown_ref` -> `DRAFT_BODY` fenced block (falling back to a legacy direct `body_markdown` field) ([src/ThroughlineBuild.Phases/DraftPhase.cs:70-84](../../src/ThroughlineBuild.Phases/DraftPhase.cs#L70-L84)).
5. Validate minimal sections (title + description).
6. Return `DraftResult.Ok` with body markdown.

### `ScaffoldPhase` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs))

Status: **Functional**. Multi-write batch.

1. Parse op-doc via `OpDocParser`.
2. Validate via `OpDocValidator`.
3. Warning gate: abort if warnings and not `--accept-warnings`.
4. Dry-run gate (counts only, no API calls).
5. Create a single top-level **operation ticket** titled `Operation: <slug>` with the `plan-ticket` label (TLB-228) ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs:124-152](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L124-L152)). Its UUID is the parent for all plan-tickets.
6. For each plan in dispatch order:
   1. Create plan-ticket with `plan-ticket` label; emit `TicketWrite role=plan`.
   2. `SetParentAsync` plan -> operation ticket.
   3. For each brief in the plan: create brief-ticket; emit `TicketWrite role=brief`; `SetParentAsync` brief -> plan.
7. Failures collected in `ScaffoldFailure[]`; processing continues. Result carries `OpTicketId`.

### Loose ends

- `ScaffoldPhase` is invoked from the CLI, not exposed as an `IWorkflowPhase`; it has its own `ScaffoldResult` shape.
- `DecomposePhase` writes children but does not transition or label the parent beyond the `[decomposed_at]` marker; there is no "decomposed" terminal state.
- The architecture doc still describes a 9-value `Phase` enum and `ClaudeCodeReviewer` as the default verifier; both are stale - the enum has 10 values and the verifier is `WorkerAgentReviewer`.

---

## The chain rework loop

`RunImplementReviewLoopAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:249-357](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L249-L357)):

```
              +-------------------+
              | ImplementPhase    |
              +---------+---------+
                        |
                        | InProgress -> InReview
                        v
              +---------+---------+
              | ReviewPhase       |
              +---------+---------+
                        |
            +-----------+-----------+
            |           |           |
          Pass       Rework        Fail
            |           |           |
            v           v           v
        ShipPhase   round++     return
                        |       StoppedAtReview
                  round < 2 ?
                  +-----+-----+
                  |           |
                yes          no
                  |           |
        (back to top)   return ReworkCapExceeded
```

`MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:14](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L14)) means at most 1 rework + 1 reattempt, i.e. up to 3 implement runs total. Each `Rework` verdict emits a `ReworkRound` event carrying `round`, `verdict_that_triggered`, `rationale_preview` ([src/ThroughlineBuild.Phases/ChainPhase.cs:337-348](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L337-L348)). Operators wanting more rounds invoke `build rework` manually after the chain returns `ReworkCapExceeded`.

When the chain starts at Review (state `InReview`), the first review runs in `RunReviewBranchAsync`; a `Rework` there hands off to the loop with `startRound = round + 1` and the recovered feedback ([src/ThroughlineBuild.Phases/ChainPhase.cs:359-396](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L359-L396)).

### Loose ends

- `MaxReworkRounds = 2` is hardcoded; not configurable per ticket or repo.
- A review-phase *infra* failure (worker crash) returns `StoppedAtReview` with the failure reason, distinct from a `Fail` verdict ([src/ThroughlineBuild.Phases/ChainPhase.cs:410-424](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L410-L424)).

---

## Obsolete-claim handling (ratification)

Status: **Functional**. Added by TLB-282/283/285.

A worker (plan or implement) may return `Status.Escalate` with an `escalation.reason == "obsolete"` claim plus a `subsumed_by` block (commit, files, rationale). When the chain sees this and `--no-auto-resolve` was NOT supplied, it runs ratification ([src/ThroughlineBuild.Phases/ChainPhase.cs:127-160, 284-315](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L127-L160)):

1. `IsObsoleteEscalation` confirms the escalation shape ([src/ThroughlineBuild.Phases/ChainPhase.cs:455-464](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L455-L464)).
2. `RunRatificationAsync` invokes the `ObsoleteRatifier`, recording a `ratify` `ChainStep` ([src/ThroughlineBuild.Phases/ChainPhase.cs:500-528](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L500-L528)).
3. `ObsoleteRatifier.RatifyAsync` performs three checks ([src/ThroughlineBuild.Verification/ObsoleteRatifier.cs:32-79](../../src/ThroughlineBuild.Verification/ObsoleteRatifier.cs#L32-L79)): (a) cited commit exists (`git rev-parse <commit>^{commit}`), (b) cited files exist at HEAD, (c) a model-driven check that the prior work meets the ticket's acceptance criteria.
4. On `Pass`: the chain transitions the ticket to `Done`, posts a "Subsumed by ..." comment, emits a `TicketSubsumed` event, and returns `RatifiedObsolete` (a success outcome). On reject, it falls through to `StoppedAtPlan` / `StoppedAtImplement`.

`--no-auto-resolve` (CLI flag, threaded as `ChainPhaseOptions.NoAutoResolve`) disables this and forces the escalation to be treated as a plain stop ([src/ThroughlineBuild.Cli/Program.cs:52-53](../../src/ThroughlineBuild.Cli/Program.cs#L52-L53)).

### Loose ends

- Ratification only triggers from the chain, not from a standalone `build plan`/`build implement` (those just return the failure).
- `RatifiedObsolete` is treated as success by the dispatchers and the aggregate report.

---

## Tree-aware chain (parent tickets)

Status: **Functional**. TLB-304/305/306/307 + grandchild-stop (f3953f7).

When a chained ticket has children, `RunParentChainAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:530-642](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L530-L642)) runs instead of the per-phase chain:

1. Filter children to non-terminal (not `Done`/`Cancelled`) and never the parent itself.
2. **Grandchild stop:** for each eligible child, query *its* children; if any are live, the tree is deeper than one level. Return `ParentHasGrandchildren` and tell the operator to chain the intermediate ticket directly ([src/ThroughlineBuild.Phases/ChainPhase.cs:548-581](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L548-L581)). This guards against the runaway recursion that previously hammered Plane's rate limiter.
3. **Sibling dependency ordering (TLB-329):** build a `blocked_by` dependency graph over the eligible siblings (`BuildSiblingGraphAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:675-693](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L675-L693)) and `TopologicalSorter.ComputeLevels` it into dependency-ordered levels ([src/ThroughlineBuild.Phases/ChainPhase.cs:596-597](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L596-L597)). Independent siblings run concurrently within a level; a sibling blocked by another waits for its blocker's level. The `--max-parallel` flag (`ChainPhaseOptions.ForceParallel`) collapses all siblings into one level and skips the relation fetch, restoring the prior all-concurrent behavior ([src/ThroughlineBuild.Phases/ChainPhase.cs:585-593](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L585-L593)).
4. Recurse `RunAsync` on each eligible (leaf) child, level by level, bounded by `SemaphoreSlim(MaxParentChainConcurrency)` where `MaxParentChainConcurrency = 4` ([src/ThroughlineBuild.Phases/ChainPhase.cs:600,702](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L600)). A level stops the cascade if any child in it fails. The shared Plane `RequestThrottle` paces the API traffic.
5. After all children: attempt `RollupParentAsync` (fail-soft).
6. Outcome is `ParentCompleted` if every child succeeded, else `ParentStoppedEarly`. Child results are carried on `ChainResult.ChildResults`.

Refusals enforcing the tree discipline:
- **Plan/implement refuse parent tickets:** `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs:60-63](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L60-L63)) and `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:57-64](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L57-L64)) refuse a ticket that has children.
- **Aggregate parent review** (TLB-305): `ReviewPhase.RunParentReviewAsync` classifies children - any `InProgress`/`InReview` child -> `Rework` (parent back to `InProgress`); all `Done` -> `Pass`; otherwise `Fail` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:254-310](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L254-L310)).
- **All-children-Done gate for parent ship** (TLB-305): `ShipPhase.RunParentShipAsync` blocks unless every child is `Done`; if so it transitions the parent straight to `Done` (no merge) ([src/ThroughlineBuild.Phases/ShipPhase.cs:478-517](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L478-L517)).
- **Cascade close/defer, parent-only reopen** (TLB-307): `close`/`defer` cascade the lifecycle transition to non-terminal children (unless `--no-cascade`) ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-64](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L64)); `reopen` notes the parent but does NOT reopen children ([src/ThroughlineBuild.Commands/ReopenCommand.cs:38-43](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L38-L43)).

### Loose ends

- The parent chain is exactly one level deep by design; deeper trees require the operator to chain intermediate tickets first.
- Child cascade close/defer failures are logged to stderr and do not abort the parent transition.
- Children run **serially** within a level (the dispatch semaphore is `SemaphoreSlim(1, 1)`; the former `MaxParentChainConcurrency = 4` constant was removed in op-29). Sibling dependency levels still gate ordering, but there is no within-level concurrency.
- A child left `Planning`/`InProgress` by an interrupted run is now **resumed** by `ResolveEntryAsync`, not refused - so a single stuck sibling no longer flips the whole parent to `ParentStoppedEarly`. An interrupted-initial `InProgress` child's orphaned branch/worktree are pruned and the branch is recreated inside the shared worktree.
- If the shared chain worktree cannot be created (commonly: its path survives a prior interrupted parent chain), the chain falls back to per-ticket standalone worktrees and now emits a loud `shared_worktree_unavailable` `GateFailure` + stderr warning instead of degrading silently.

---

## Multi-ticket dispatch

Status: **Functional** (parallel path); **Legacy** (sequential fallback, see loose ends).

`build chain TLB-A TLB-B TLB-C ...` collects positional ids beyond the first ([src/ThroughlineBuild.Cli/Program.cs:1144-1150](../../src/ThroughlineBuild.Cli/Program.cs#L1144-L1150)). When extra ids are present, the CLI takes the parallel path ([src/ThroughlineBuild.Cli/Program.cs:1212-1290](../../src/ThroughlineBuild.Cli/Program.cs#L1212-L1290)):

1. `GetBatchAsync` fetches all tickets.
2. Build a `ThroughlineBuild.Phases.TicketGraph` (a node+edge graph): add a node per ticket, then for each `blocked_by` relation whose target is in the dispatched set, add an edge `blocker -> blocked` ([src/ThroughlineBuild.Cli/Program.cs:1232-1256](../../src/ThroughlineBuild.Cli/Program.cs#L1232-L1256)).
3. Hand the graph to `ParallelDispatcher` (TLB-312) with `config.workers.max_concurrency` (default 4) ([src/ThroughlineBuild.Cli/Program.cs:1258-1261](../../src/ThroughlineBuild.Cli/Program.cs#L1258-L1261), [src/ThroughlineBuild.Cli/Config.cs:31](../../src/ThroughlineBuild.Cli/Config.cs#L31)).

`ParallelDispatcher.RunAsync` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:35-155](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L35-L155)):
- `TopologicalSorter.ComputeLevels` runs Kahn's BFS to produce concurrency-eligible levels, preserving input order within a level; throws `InvalidOperationException` on a cycle ([src/ThroughlineBuild.Phases/TicketGraph.cs:15-94](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L15-L94)).
- Emit `DispatchStart` (ticket_count, level_count, max_concurrency).
- Process each level: dispatch its tickets concurrently under a per-level `SemaphoreSlim(maxConcurrency)`, each running the full `ChainPhase`. This is **level-synchronous** - a level must finish before the next starts.
- After each level, any non-success outcome (`Completed`/`RatifiedObsolete`/`ParentCompleted` are the success set) stops further levels ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:118-133](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L118-L133)).
- Emit `DispatchEnd` (`outcome` = `ok`|`partial`, total_duration_ms).
- Returns `ParallelDispatchResult(Success, Results, FailureReason)`.

The CLI prints a per-ticket `[id] outcome (Nms)` summary and returns 0 only if the dispatch succeeded ([src/ThroughlineBuild.Cli/Program.cs:1282-1289](../../src/ThroughlineBuild.Cli/Program.cs#L1282-L1289)).

**Dependency graph from the parent chain** (TLB-311): `TicketDependencyGraph.BuildAsync` is a separate helper that builds level-ordered dependencies by walking each ticket's *parent* chain (rather than `blocked_by` relations) and returns a `ThroughlineBuild.Contracts.Models.TicketGraph(Levels, CycleDetected, CycleMembers)` ([src/ThroughlineBuild.Helpers/TicketDependencyGraph.cs:8-138](../../src/ThroughlineBuild.Helpers/TicketDependencyGraph.cs#L8-L138)). Note this is a different `TicketGraph` type from the one `ParallelDispatcher` consumes (see loose ends).

**Ancestor-skip** (TLB-313): `AncestorSkipFilter.ShouldSkip` walks a ticket's ancestors (via blocker edges); if any failed and `continuePastFailure` is false, it synthesizes a `ChainResult` with outcome `Skipped` and a `SkipReason` ([src/ThroughlineBuild.Phases/AncestorSkipFilter.cs:28-88](../../src/ThroughlineBuild.Phases/AncestorSkipFilter.cs#L28-L88)). The `--continue-past-failure` flag disables it ([src/ThroughlineBuild.Cli/Program.cs:56-57](../../src/ThroughlineBuild.Cli/Program.cs#L56-L57)).

`SequentialChainDispatcher` (the original fallback) runs ids one at a time, building implicit linear edges (each predecessor is an ancestor of all followers) and applying `AncestorSkipFilter` ([src/ThroughlineBuild.Commands/SequentialChainDispatcher.cs:31-66](../../src/ThroughlineBuild.Commands/SequentialChainDispatcher.cs#L31-L66)). The CLI still wires it inside the single-ticket branch (`chainTicketIds.Count > 1`), with `ChainCommand.PrintAggregateReport` for the summary ([src/ThroughlineBuild.Cli/Program.cs:1310-1343](../../src/ThroughlineBuild.Cli/Program.cs#L1310-L1343)).

### Loose ends

- **Two `TicketGraph` types coexist:** `ThroughlineBuild.Phases.TicketGraph` (a mutable node/edge class consumed by `ParallelDispatcher`/`TopologicalSorter`, [src/ThroughlineBuild.Phases/TicketGraph.cs:4](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L4)) and `ThroughlineBuild.Contracts.Models.TicketGraph` (a `(Levels, CycleDetected, CycleMembers)` record produced by `TicketDependencyGraph.BuildAsync`, [src/ThroughlineBuild.Contracts/Models/TicketGraph.cs:3](../../src/ThroughlineBuild.Contracts/Models/TicketGraph.cs#L3)). The live CLI parallel path builds the *Phases* graph from `blocked_by` relations and does NOT call `TicketDependencyGraph.BuildAsync` - that helper is exercised by tests but not wired into `Program.cs` chain dispatch.
- **`SequentialChainDispatcher` is largely shadowed.** With extra positional ids present the CLI always takes the parallel path first (`extraTicketIds.Count > 0` at [src/ThroughlineBuild.Cli/Program.cs:1213](../../src/ThroughlineBuild.Cli/Program.cs#L1213)); the sequential branch at line 1310 sits behind that early return and is effectively legacy. Its own doc-comment still says "TLB-312 will replace the call site," which has already happened for the parallel path.
- `ParallelDispatcher` failure stops *subsequent levels* but lets the current level's already-running tickets finish (level-synchronous, not fail-fast within a level).

---

## Divergence and merge orchestration

Status: **Functional**. TLB-290/291/293/296/297/298.

`ShipPhase` resolves the rebase target (the configured target branch) by ancestry, and when the local target and `<remote>/<target>` have diverged it probes for conflicts ([src/ThroughlineBuild.Phases/ShipPhase.cs:278-345](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L278-L345)):

- `localIsAncestorOfRemote && !remoteIsAncestorOfLocal` -> `<remote>/<target>` (reason `origin_target_ahead`).
- `remoteIsAncestorOfLocal && !localIsAncestorOfRemote` -> local `<target>` (reason `local_target_ahead`).
- both -> same commit (reason `same_commit`).
- neither (diverged) -> `IGitClient.ProbeDivergenceAsync` (TLB-296), which uses `git merge-tree --write-tree` to classify without mutating, returning a `DivergenceState`: `Clean, LocalAhead, RemoteAhead, DivergedNoConflict, DivergedWithConflict` ([src/ThroughlineBuild.Contracts/IGitClient.cs:22-29](../../src/ThroughlineBuild.Contracts/IGitClient.cs#L22-L29), [src/ThroughlineBuild.Git/ProcessGitClient.cs:962](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L962)).
  - `DivergedNoConflict` and NOT `--no-auto-merge` (TLB-297/298): auto-rebase the local target onto `<remote>/<target>` under `MainWorktreeLock`. On success emit `TargetAutoRebased` (`outcome=clean`) and rebase the feature onto the local target. On a race-to-conflict, abort the rebase, emit `TargetAutoRebased` (`outcome=raced_to_conflict`) + a `GateFailure`, and fail at the `Fetch` stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:310-345](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L310-L345)).
  - Otherwise (conflict, or `--no-auto-merge`): post `ship_blocked` comment, emit `GateFailure`, fail at `Fetch`.

`MainWorktreeLock` (TLB-290/291) is a per-path in-process `SemaphoreSlim` keyed on the normalized main-worktree path; it serializes the fetch, the target-branch auto-rebase, and the fast-forward merge so concurrent chains (parallel dispatch, parent chain) cannot race on the shared main worktree ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:6-29](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L6-L29)).

After a successful FF merge, when a remote exists the phase pushes the target branch to the remote (TLB-293); a push failure fails the ship at the `Push` stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:509-510](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L509-L510)). When the target is non-default, the step-6 preflight guarantees the main worktree is on that branch before the FF merge advances it.

### Loose ends

- `MainWorktreeLock` is in-process only - it does not coordinate across separate `build` processes; two concurrent invocations on the same repo can still race.
- `--no-auto-merge` forces the diverged case to a hard stop even when `merge-tree` says it is conflict-free.

---

## Coordination protocol

How phases communicate without a persistent process:

| Mechanism | What it carries |
|---|---|
| **Plane state field** | The authoritative "what phase comes next?" - each phase checks state on entry. |
| **Plane comment markers** | The SHA stamps (`[planned_at]`, `[implemented_at]`, `[shipped_at]`, `[decomposed_at]`) parsed by `MarkerParser`. |
| **Plane comment marker prefixes** | `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>`, `<strong>reviewed:</strong>` for state context. |
| **Ticket description** | Plan HTML appended once; review writes nothing to description; reopen must not touch description/labels. |
| **Ticket labels** | `risk:*`, `size:*` from plan; `plan-ticket` from scaffold; `size:*` on decompose children. |
| **Parent relations** | The parent/child edges that drive the tree-aware chain, scaffold tree, and decompose fan-out. |
| **`blocked_by` relations** | The dependency edges that drive multi-ticket parallel dispatch ordering. |
| **`.build/events/<stem>.jsonl`** | Replayable audit log. The rework feedback retriever reads it to recover the most recent `Rework` verdict. |
| **`.worktrees/ticket-<slug>/`** | The implementer's checkout. Reviewer reads its diff; shipper rebases + merges from it. |
| **Local git branch `ticket/<slug>`** | The carrier of the actual commits. |
| **`MainWorktreeLock`** | In-process serialization of main-worktree git ops across concurrent chains. |

There is no message bus and no persistent in-process state between separate `build` invocations. Every restart re-reads from Plane + git + events.

### Loose ends

- Within a single `build chain` of multiple tickets / a parent chain, in-process state (semaphores, the lock) *does* persist for the run; the "no shared state" principle holds only across separate process invocations.

---

## Sessions

Every `build <verb>` invocation mints session ids via `_sessionIdGenerator` (default `Guid.NewGuid().ToString("N")`). In a chain, each phase gets its own per-phase session id, recorded on `ChainStep.PhaseSessionId`, while the chain lifecycle events (`ChainStart`/`ChainEnd`) carry a single `chainSessionId` ([src/ThroughlineBuild.Phases/ChainPhase.cs:56, 107-121](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L56)). The dispatcher mints its own `dispatchSessionId` for `DispatchStart`/`DispatchEnd` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:41](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L41)).

Session ids flow into `WorkflowEvent.SessionId`, the JSONL file naming (via `SessionFileNameBuilder`), and the debug capture directory.

### Loose ends

- Per-phase session ids are now always distinct within a chain (the doc's earlier claim that they were "populated only when phases use distinct session contexts" is stale - the chain always mints a fresh id per phase).

---

## Event kinds emitted

`EventKind` enum has 13 values ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14)):

| Kind | Int | Emitted by | Meaning |
|---|---|---|---|
| `StateTransition` | 0 | every phase/command that transitions | from / to state |
| `LlmCall` | 1 | phases that surface worker LLM usage | tokens / model / wall time |
| `WorkerSpawn` | 2 | phases that spawn a worker (and `NewPhase` for audit symmetry) | worker name + role |
| `VerifierVerdict` | 3 | every worker phase post-run; review post-verifier; decompose verdict gate | status / verdict |
| `GateFailure` | 4 | drift warning, ship pre-flight/diverged/rebase/conflict-marker/regression failures | gate kind + reason |
| `TicketWrite` | 5 | every Plane write (description / labels / comments / create / set-parent / rollup / fetch_skipped / base_ref_resolved / decruft / delete_branch) | action + payload summary |
| `ChainStart` | 6 | `ChainPhase` | starting_at_phase, initial_state, chain_session_id |
| `ChainEnd` | 7 | `ChainPhase` | outcome, phases_run, rework_rounds, total_duration_ms |
| `ReworkRound` | 8 | `ChainPhase` | round, verdict_that_triggered, rationale_preview |
| `TicketSubsumed` | 9 | `ChainPhase` (obsolete ratification Pass) | ticket_id, subsumed_by_commit, files, rationale |
| `TargetAutoRebased` | 10 | `ShipPhase` (DivergedNoConflict auto-rebase; renamed from `MainAutoRebased`) | from_sha, onto_sha, local_commits_replayed, outcome (clean / raced_to_conflict) |
| `DispatchStart` | 11 | `ParallelDispatcher` | ticket_count, level_count, max_concurrency |
| `DispatchEnd` | 12 | `ParallelDispatcher` | outcome (ok / partial), total_duration_ms |

Full event-line schema in [docs/event-log-format.md](../event-log-format.md).

### Loose ends

- `event-log-format.md` does not enumerate the per-`Data` shape of every kind; the authoritative `Data` keys are in the emitting code cited above.
- `DispatchStart`/`DispatchEnd` carry an empty `TicketId` (they are batch-scoped, not ticket-scoped).

---

## Chain outcomes and exit codes

`ChainOutcome` enum ([src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:3-17](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3-L17)) and the single-ticket exit-code mapping in `Program.cs` ([src/ThroughlineBuild.Cli/Program.cs:1359-1373](../../src/ThroughlineBuild.Cli/Program.cs#L1359-L1373)):

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RatifiedObsolete` | 0 | obsolete claim ratified; ticket -> Done |
| `ParentCompleted` | 0 | all eligible children completed |
| `RefusedInitialState` | 2 | terminal state (`Done`/`Cancelled`); `Planning`/`InProgress` are now resumed, not refused |
| `ParentHasGrandchildren` | 2 | tree deeper than one level |
| `StoppedAtPlan` | 3 | planning failed |
| `ParentStoppedEarly` | 3 | a child did not complete |
| `Skipped` | 3 | skipped because an ancestor failed |
| `StoppedAtImplement` | 4 | implementation failed |
| `StoppedAtReview` | 5 | review returned `Fail` (or review infra failure) |
| `ReworkCapExceeded` | 6 | more than `MaxReworkRounds` reworks |
| `StoppedAtShip` | 7 | ship gate failed |

Success set used by dispatchers and the aggregate report: `Completed`, `RatifiedObsolete`, `ParentCompleted` (`Skipped` is treated as non-failure for the overall exit-0 decision in the sequential path) ([src/ThroughlineBuild.Cli/Program.cs:1338-1343](../../src/ThroughlineBuild.Cli/Program.cs#L1338-L1343), [src/ThroughlineBuild.Phases/ParallelDispatcher.cs:118-124](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L118-L124)).

### Loose ends

- The multi-ticket parallel path returns a flat `0`/`1` from `dispatchResult.Success`, not the per-outcome exit codes above; the granular mapping is only used on the single-ticket path.

---

## Where the chain stops cleanly vs. requires manual triage

- **Clean stop / success:** `Completed`, `RatifiedObsolete`, `ParentCompleted`, `Skipped`, `RefusedInitialState`, `ReworkCapExceeded` (operator picks up with `build rework`/`build review`).
- **Requires triage:** `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview`, `StoppedAtShip`, `ParentStoppedEarly`, `ParentHasGrandchildren`. Each leaves the ticket(s) in whatever state the failing phase left them.

`ChainCommand` surfaces a one-line final summary per outcome on stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs:143-187](../../src/ThroughlineBuild.Commands/ChainCommand.cs#L143-L187)).

---

## Loose ends (cross-cutting)

- **`MaxReworkRounds = 2` and `MaxParentChainConcurrency = 4` are hardcoded.** Only the multi-ticket `workers.max_concurrency` is config-driven.
- **No cross-phase live channel.** ReviewPhase reconstructs the implementer brief deterministically. Architecture's "no shared in-memory context with the implementer" principle (Section 5.8) still holds for the worker hand-off, but the chain itself now holds in-process orchestration state for a run.
- **Chain `WorkflowEvent.Data`** carries per-step/per-dispatch fields whose schema lives in code, not exhaustively in [docs/event-log-format.md](../event-log-format.md).
- **No replay verb** (`build replay <session-id>`). Architecture Appendix item 4 notes this as a future.
- **Phase ordering documented in `ChainPhase`** - operators running phases manually out of order hit each phase's state guard.
- **Two `TicketGraph` types** and the partly-shadowed `SequentialChainDispatcher` are the sharpest orchestration-code seams (detailed in "Multi-ticket dispatch").
