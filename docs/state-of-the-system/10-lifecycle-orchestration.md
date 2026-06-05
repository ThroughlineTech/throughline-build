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
| `plan` (promote) | `Backlog` | `Planning` -> `Ready` with no worker/LLM ([src/ThroughlineBuild.Phases/PlanPhase.cs:203-230](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L203-L230)) |
| `implement` (initial) | `Ready` | `InProgress` -> `InReview` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:251-260](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L251-L260)) |
| `implement` (rework) | `InProgress` | `InReview` (no `InProgress` re-entry) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:252-260](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L252-L260)) |
| `review` (Pass / Fail) | `InReview` | no change ([src/ThroughlineBuild.Phases/ReviewPhase.cs:257-300](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L257-L300)) |
| `review` (Rework) | `InReview` | `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:279-284](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L279-L284)) |
| `ship` | `InReview` | `Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:696](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L696)) |
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

Step sequence ([src/ThroughlineBuild.Phases/PlanPhase.cs:58-151](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L58-L151)):
1. Fetch ticket.
2. Parent guard: refuse if the ticket has children - parent containers do not get plans ([src/ThroughlineBuild.Phases/PlanPhase.cs:62-65](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L62-L65)).
3. State guard: `Backlog`.
4. Resolve `main` SHA via `BaseRefResolver`.
4b. **Promote branch:** if `BuildOptions.PromotePlan` is set, dispatch to `RunPromoteAsync` and return ([src/ThroughlineBuild.Phases/PlanPhase.cs:80-81](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L80-L81)) - see "Promote path" below. Otherwise the full worker-driven planning sequence runs:
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

**Promote path (TLB-374).** `RunPromoteAsync` ([src/ThroughlineBuild.Phases/PlanPhase.cs:203-230](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L203-L230)) promotes an already-authored brief straight to `Ready` without spawning a worker or calling an LLM: transition `Backlog -> Planning`, apply merged risk/size labels from the ticket's existing fields, post `[planned_at: <mainSha>]`, transition `Planning -> Ready`. It is enabled by `BuildOptions.PromotePlan`, set from the CLI `--from-brief` flag OR `[plan].mode = "promote"` in config. The ticket must already carry a usable description (the human wrote the brief); promote does no planning work, it only flips state and stamps labels.

### `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs](../../src/ThroughlineBuild.Phases/ImplementPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ImplementPhase.cs:64-340](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L64-L340)):
1. Fetch ticket. Detect rework round vs. initial by `ImplementPhaseOptions.ReviewFeedback` presence.
2. Parent guard: refuse to implement a ticket that has children; writes `phase-status.json` via `EarlyExitManifest` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:69-76](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L69-L76)).
3. State guard: `Ready` (initial) or `InProgress` (rework). On guard failure, write `phase-status.json`.
3b. Hygiene gate: refuse on a conflicted or stash-polluted tree, `GateFailure` kind `hygiene_gate` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:93-106](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L93-L106)).
4. Resolve base ref + main SHA via `BaseRefResolver` (advances to the local target tip when it is ahead of origin - the accumulating state a local-shipping chain leaves behind, TLB-411) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:108-123](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L108-L123)).
5. Compute deterministic worktree names via `PhaseWorktreeLayout`.
6. Drift check: scan comments for the **freshest** `[planned_at: <sha>]` marker by creation time (`CommentMarkers.LatestValue`, not list order, TLB-412); emit `GateFailure` drift warning if it differs from current main SHA (does not block).
7. Resolve the canonical worktree/branch. Three cases keyed on `SharedWorktreePath` and `isRework` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:147-204](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L147-L204)).
8. Build the brief via `ImplementBriefBuilder.Build`, passing the chain commit range (`effectiveChainRange`) only when `HandoffPointerEnabled` (compile const, default true) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:206-211](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L206-L211)) - see "Chain commit-range handoff".
9. Set up the working directory ([src/ThroughlineBuild.Phases/ImplementPhase.cs:213-249](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L213-L249)):
   - **Shared worktree (initial):** the chain pre-created one worktree; create *this ticket's* `ticket/<id>` branch INSIDE it via `CreateBranchAsync` (no new worktree).
   - **Standalone (initial):** `CreateWorktreeAsync` cuts a fresh per-ticket worktree + branch.
   - **Rework:** reuse the existing worktree found in step 7; no git operation.
10. Transition `Ready -> InProgress` only on initial round.
11. Emit `WorkerSpawn`. Run worker inside the worktree.
12. Emit `VerifierVerdict` (worker status). On non-Ok, return early; `Escalate` is carried as `EscalationWorkerResult` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:296-299](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L296-L299)).
13. Post-worker dirty-tree check (Step 14b) with one bounded retry: if the worker left uncommitted files, re-run with a "commit everything" note; still dirty -> `GateFailure` and fail ([src/ThroughlineBuild.Phases/ImplementPhase.cs:301-339](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L301-L339)).
14. Validate `commit_sha` metadata. Compare against actual `git rev-parse HEAD` in worktree; actual wins on discrepancy (a discrepancy note is folded into the marker comment).
15. Post `[implemented_at: <actualSha>]` comment naming the branch; if the worker supplied a `summary_ref` -> `IMPLEMENT_SUMMARY` fenced block, render it via `MarkdownRenderer` and append it.
16. Transition `InProgress -> InReview`.

### `ReviewPhase` ([src/ThroughlineBuild.Phases/ReviewPhase.cs](../../src/ThroughlineBuild.Phases/ReviewPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ReviewPhase.cs:60-260](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L60-L260)):
1. Fetch ticket. Parent-ticket aggregate-review branch if it has children (see "Tree-aware chain").
2. State guard `InReview`.
3. Locate the worktree by branch or path via `git worktree list`; if missing, reconstruct it from the ticket branch (TLB-407).
4. Resolve base ref + main SHA.
5. Reconstruct an implementer brief (so the verifier sees what the implementer was told) via `ImplementBriefBuilder.Build` without `ReviewFeedback`. This is reconstructed deterministically - not a fresh worker invocation.
6. Determine the commit under review (TLB-412/414, [src/ThroughlineBuild.Phases/ReviewPhase.cs:152-181](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L152-L181)): extract the **freshest** `[implemented_at: <sha>]` marker by creation time (required); read the worktree branch HEAD as ground truth. If HEAD differs from the marker (an implementer amended/squashed after posting the marker), emit `GateFailure` kind `implemented_at_superseded` and attribute the review to HEAD - the diff and checks run against HEAD, never the orphaned marker commit.
7. Compute the diff `baseRef..featureBranch` with patches; synthesize a `WorkerResult` from the resolved commit + diff to hand to the verifier (no re-execution of the implement worker).
8. Run automated checks via `AutomatedChecksRunner.RunAsync(...)`.
9. Construct the verifier - default `WorkerAgentReviewer`, which spawns the verifier worker against the review brief inside the feature worktree ([src/ThroughlineBuild.Phases/ReviewPhase.cs:204-205](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L204-L205)). An `IVerifier` override may be injected.
10. Emit `WorkerSpawn` (role=verifier). Run verifier.
10b. Post-verifier dirty-tree check: **hard-fail, no retry** - if the verifier left tracked files uncommitted, emit `GateFailure` kind `dirty_worktree_after_review` and fail ([src/ThroughlineBuild.Phases/ReviewPhase.cs:217-235](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L217-L235), TLB-400). The verifier templates ban `git stash`/`checkout`/`reset`/`rebase` because the stash stack leaks across worktrees.
11. Emit `VerifierVerdict` (kind, rationale, checks_failed).
12. Optionally emit `LlmCall` if the `WorkerAgentReviewer`'s last worker result carries usage.
13. Apply verdict:
    - `Pass` -> post `reviewed: pass` comment, no transition.
    - `Rework` -> post `reviewed: rework` comment, transition `InReview -> InProgress`.
    - `Fail` -> post `reviewed: fail` comment, no transition (operator decides).

### `ShipPhase` ([src/ThroughlineBuild.Phases/ShipPhase.cs](../../src/ThroughlineBuild.Phases/ShipPhase.cs))

Status: **Functional**.

Deterministic - no LLM, no worker. The merge **target** is `[work].target_branch` if set, else `[ship].base_branch` (resolved by `BuildConfig.ResolveTargetBranch()` and carried on `ShipOptions.TargetBranch`, [src/ThroughlineBuild.Phases/ShipPhase.cs:248](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L248)). Step sequence:
1. Fetch ticket. Parent-ticket ship branch if it has children (see "Tree-aware chain").
2. State guard `InReview`.
3. Locate worktree (by `ticket/<id>` prefix; falls back to creating one from a matching local branch).
4. Pre-flight: `build` binary not running from inside the worktree.
5. Pre-flight hygiene + dirty check: both feature and main worktrees clean of tracked changes; `GateFailure` kind `pre_flight_hygiene` / `pre_flight_dirty` ([src/ThroughlineBuild.Phases/ShipPhase.cs:204-243](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L204-L243)).
6. Pre-flight (unconditional, catches detached HEAD): the main worktree is checked out on the target branch, else `wrong_worktree_branch` `GateFailure` and fail at `PreFlight` ([src/ThroughlineBuild.Phases/ShipPhase.cs:256-277](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L256-L277), TLB-402/410).
7. Conditionally fetch from remote. If no remote, or `--no-push`: skip, rebase onto the local target branch.
8. Determine rebase target via divergence handling (see "Divergence and merge orchestration"). Fetch and the auto-rebase of the local target branch are wrapped in `MainWorktreeLock`.
9. Emit `base_ref_resolved` (a `TicketWrite` event, carries `target_branch` + `source`).
10. Rebase feature branch onto resolved target ref. On conflict: `git rebase --abort`, fail at `Rebase`.
11. Conflict-marker scan of the rebased diff's files.
12. Run `ship.regression_checks` (baseline-aware, TLB-401: only newly-failing checks block; pre-existing failures noted non-blocking).
13. Fast-forward merge into the target branch, under `MainWorktreeLock`, with a post-merge HEAD re-assertion ([src/ThroughlineBuild.Phases/ShipPhase.cs:654-667](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L654-L667)).
14. Push the target branch to remote when a remote exists and `--no-push` is not set; failure fails the ship at the `Push` stage.
15. Read merged HEAD SHA.
16. Post `[shipped_at: <mergedSha>]` comment ([src/ThroughlineBuild.Phases/ShipPhase.cs:686-689](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L686-L689)).
17. Transition `InReview -> Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:696](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L696)).
18. `WorktreeDecrufter.DecruftAsync` - **skipped when `SkipDecruft` is set** (the chain ship factory sets it so the shared worktree survives between children, [src/ThroughlineBuild.Phases/ShipPhase.cs:703-706](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L703-L706)); otherwise failure is non-fatal post-merge.
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

Status: **Functional**. The orchestrator. Constructed in `Program.cs` with per-phase factories closed over the shared `PlaneTicketingClient`, worker agents, and `IEventSink`, plus a `ratifierFactory` for obsolete-claim handling and a `chainShipFactory` (`SkipDecruft=true`) for shared-worktree ships ([src/ThroughlineBuild.Cli/Program.cs:1493-1503](../../src/ThroughlineBuild.Cli/Program.cs#L1493-L1503)).

`RunAsync` entry ([src/ThroughlineBuild.Phases/ChainPhase.cs:72-288](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L72-L288)):

1. Fetch the ticket.
2. **Outermost-only preflight hygiene + tracked-dirty gate** (skipped on recursion, which sets `SharedWorktreePath`): a dangling stash or conflict on the repo-global tree -> `GateFailure` kind `hygiene_gate_preflight`; ordinary tracked changes in the main checkout -> `GateFailure` kind `chain_preflight_dirty` with `dirty_count`, `dirty_paths`, and `worktree`. Both return `RefusedDirtyTree` before planning, ticket transitions, worker spawn, review, or ship ([src/ThroughlineBuild.Phases/ChainPhase.cs:86-140](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L86-L140)).
3. Query children; if any exist, delegate to `RunParentChainAsync` (the tree-aware path) and return ([src/ThroughlineBuild.Phases/ChainPhase.cs:113-117](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L113-L117)).
4. Otherwise route on state via `ResolveEntryAsync` (the **resume state machine**, [src/ThroughlineBuild.Phases/ChainPhase.cs:960-982](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L960-L982)):
   - `Backlog` -> start at Plan
   - `Ready` -> start at Implement
   - `InReview` -> start at Review
   - `Planning` -> a plan that never finished (the `Backlog -> Planning` transition precedes the worker, and no plan artifact is written until it succeeds): reset to `Backlog`, emit a `chain_resume` `StateTransition`, start at Plan
   - `InProgress` -> `ResolveInProgressAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:993-1026](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L993-L1026)): count commits on `ticket/<id>` beyond base. **0 commits** (an interrupted *initial* implement transitions `Ready -> InProgress` before the worker commits) -> `PruneOrphanBranchAsync` removes the orphaned branch/worktree, reset to `Ready`, start a clean Implement - in a parent chain this lets the branch be recreated inside the shared worktree instead of an orphaned standalone one. **Has commits** (interrupted rework) -> `ResumeImplement` at round 1 reusing the worktree, recovering the last `Rework` feedback from the event log or synthesizing a neutral resume note.
   - `Done` / `Cancelled` -> `Refused`: emit `ChainStart`, return `RefusedInitialState` (the only genuinely un-runnable states)
   `ResolveEntryAsync` performs the reset/prune side effects and emits the `chain_resume` `StateTransition` events; resume transitions carry reason `chain_resume`.
5. Emit `ChainStart`.
6. If starting at Plan: emit a console-only START notice (see below), run `PlanPhase`. On failure, if `!NoAutoResolve` and the worker `Escalate`d with reason `obsolete`, run obsolete-claim ratification (see "Obsolete-claim handling"). Otherwise return `StoppedAtPlan`.
7. If Plan succeeded, starting at Implement, or `ResumeImplement`: enter the implement-review loop (`RunImplementReviewLoopAsync`). `ResumeImplement` re-enters the loop as a rework round (carries recovered/synthesized feedback at round >= 1) so `ImplementPhase` reuses the in-progress worktree.
8. If starting at Review only: run one review (`RunReviewBranchAsync`). Pass -> Ship; Rework -> implement-review loop; Fail -> `StoppedAtReview`.
9. Run `ShipPhase` (using `_chainShipFactory` when inside a shared worktree - see "Tree-aware chain"). Fail -> `StoppedAtShip`.
10. Emit `ChainEnd`. Return `Completed`.

**Per-phase START notice (TLB-415).** Before each phase the chain calls `EmitPhaseStart` ([src/ThroughlineBuild.Phases/ChainPhase.cs:293-304](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L293-L304)), which pushes a `ChainStep` with `IsStart: true` through the `OnStep` stream so the operator sees a phase has begun, not just its completion line. These START steps are **console-only** - never added to the `steps` list, so the returned `ChainResult` and its `phases_run` count are unchanged. Called before plan ([:162](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L162)), ship ([:255](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L255)), implement ([:350](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L350)), review ([:490](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L490)), and ratify ([:609](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L609)).

**Chain commit-range handoff (op-29 briefs 08-11).** A parent chain derives a git commit range (`ChainCommitRange` / `ChainCommitRangeHelper.ComputeAsync`, [src/ThroughlineBuild.Helpers/ChainCommitRange.cs:13-83](../../src/ThroughlineBuild.Helpers/ChainCommitRange.cs#L13-L83)) describing the commits already produced by shipped siblings, and threads it onto `ChainPhaseOptions.ChainCommitRange`. The implement loop passes it to `ImplementBriefBuilder` **only on the first implement round** (`feedback is null`); rework rounds suppress it because the worktree already holds the agent's prior edits ([src/ThroughlineBuild.Phases/ChainPhase.cs:345-349](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L345-L349)). The builder folds the range's touched files into `RelevantFiles` and adds a single bounded `chain_pointer` line to Context, but **only when the range is non-empty** - an empty/absent range leaves the brief byte-identical to the pre-handoff baseline ([src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs:50-90](../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L50-L90)). It is no-LLM and best-effort: any git failure yields `CommitCount=0`. Gated end-to-end by the `HandoffPointerEnabled` compile const ([src/ThroughlineBuild.Phases/ImplementPhase.cs:34](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L34)).

The chain invokes an `Action<string, ChainStep>` callback (`OnStep`) after each phase (and for START notices); `ChainCommand` uses it to stream one-line, per-ticket-prefixed summaries to stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)). Per-ticket output prefixing (`[<ticketId>] `) is done by `PrefixedTextWriter` in `BuildPhaseOptions` ([src/ThroughlineBuild.Phases/ChainPhase.cs:541-550](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L541-L550), TLB-403).

Each phase runs under its own per-phase session id minted from `_sessionIdGenerator`; the chain itself has a single `chainSessionId` used on `ChainStart`/`ChainEnd` ([src/ThroughlineBuild.Phases/ChainPhase.cs:77, 136-147](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L77)).

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

`RunImplementReviewLoopAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:329-442](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L329-L442)):

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

`MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:21](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L21)) means at most 1 rework + 1 reattempt, i.e. up to 3 implement runs total. Each `Rework` verdict emits a `ReworkRound` event carrying `round`, `verdict_that_triggered`, `rationale_preview` ([src/ThroughlineBuild.Phases/ChainPhase.cs:422-433](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L422-L433)). Operators wanting more rounds invoke `build rework` manually after the chain returns `ReworkCapExceeded`.

When the chain starts at Review (state `InReview`), the first review runs in `RunReviewBranchAsync`; a `Rework` there hands off to the loop with `startRound = round + 1` and the recovered feedback ([src/ThroughlineBuild.Phases/ChainPhase.cs:444-481](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L444-L481)).

### Loose ends

- `MaxReworkRounds = 2` is hardcoded; not configurable per ticket or repo.
- A review-phase *infra* failure (worker crash) returns `StoppedAtReview` with the failure reason, distinct from a `Fail` verdict ([src/ThroughlineBuild.Phases/ChainPhase.cs:496-510](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L496-L510)).

---

## Obsolete-claim handling (ratification)

Status: **Functional**. Added by TLB-282/283/285.

A worker (plan or implement) may return `Status.Escalate` with an `escalation.reason == "obsolete"` claim plus a `subsumed_by` block (commit, files, rationale). When the chain sees this and `--no-auto-resolve` was NOT supplied, it runs ratification ([src/ThroughlineBuild.Phases/ChainPhase.cs:181-214, 369-400](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L181-L214)):

1. `IsObsoleteEscalation` confirms the escalation shape ([src/ThroughlineBuild.Phases/ChainPhase.cs:552-561](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L552-L561)).
2. `RunRatificationAsync` invokes the `ObsoleteRatifier`, recording a `ratify` `ChainStep` ([src/ThroughlineBuild.Phases/ChainPhase.cs:597-626](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L597-L626)).
3. `ObsoleteRatifier.RatifyAsync` performs three checks ([src/ThroughlineBuild.Verification/ObsoleteRatifier.cs:32-79](../../src/ThroughlineBuild.Verification/ObsoleteRatifier.cs#L32-L79)): (a) cited commit exists (`git rev-parse <commit>^{commit}`), (b) cited files exist at HEAD, (c) a model-driven check that the prior work meets the ticket's acceptance criteria.
4. On `Pass`: the chain transitions the ticket to `Done`, posts a "Subsumed by ..." comment, emits a `TicketSubsumed` event, and returns `RatifiedObsolete` (a success outcome). On reject, it falls through to `StoppedAtPlan` / `StoppedAtImplement`.

`--no-auto-resolve` (CLI flag, threaded as `ChainPhaseOptions.NoAutoResolve`) disables this and forces the escalation to be treated as a plain stop ([src/ThroughlineBuild.Cli/Program.cs:52-53](../../src/ThroughlineBuild.Cli/Program.cs#L52-L53)).

### Loose ends

- Ratification only triggers from the chain, not from a standalone `build plan`/`build implement` (those just return the failure).
- `RatifiedObsolete` is treated as success by the dispatchers and the aggregate report.

---

## Tree-aware chain (parent tickets)

Status: **Functional**. TLB-304/305/306/307 + grandchild-stop (f3953f7).

When a chained ticket has children, `RunParentChainAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:628-900](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L628-L900)) runs instead of the per-phase chain:

1. Filter children to non-terminal (not `Done`/`Cancelled`) and never the parent itself; order by ascending ticket number ([src/ThroughlineBuild.Phases/ChainPhase.cs:641-646](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L641-L646), TLB-397). `TopologicalSorter` preserves input order as its within-level tiebreaker, so feeding it lowest-number-first dispatches unordered siblings lowest-number-first.
2. **Grandchild stop:** for each eligible child, query *its* children; if any are live, the tree is deeper than one level. Return `ParentHasGrandchildren` and tell the operator to chain the intermediate ticket directly ([src/ThroughlineBuild.Phases/ChainPhase.cs:652-684](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L652-L684)). This guards against runaway recursion that previously hammered Plane's rate limiter.
3. **Sibling dependency ordering:** build a `blocked_by` dependency graph over the eligible siblings (`BuildSiblingGraphAsync`, [:930-948](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L930-L948)) and `TopologicalSorter.ComputeLevels` it into dependency-ordered levels ([:689](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L689)). `PrintDispatchOrder` prints the derived order before any phase runs so a wrong/missing edge is visible up front ([:694, :918-928](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L694)). Siblings within a level have no `blocked_by` edge and are ordered lowest-number-first; a blocked sibling waits for its blocker's level.
4. **Cut ONE shared worktree for the whole chain** ([src/ThroughlineBuild.Phases/ChainPhase.cs:699-783](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L699-L783)): create a single worktree on a placeholder branch `chain/<slug>` at the resolved base. Each child then creates its own `ticket/<id>` branch INSIDE this worktree (in `ImplementPhase` step 9). A leftover placeholder branch from a prior interrupted run is self-healed (deleted and recreated). If the worktree cannot be created, the chain falls back to **per-ticket standalone worktrees** and emits a loud `shared_worktree_unavailable` `GateFailure` + stderr warning instead of degrading silently ([:766-782](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L766-L782)).
5. Recurse `RunAsync` on each eligible (leaf) child, level by level, **serialized by `SemaphoreSlim(1, 1)`** ([src/ThroughlineBuild.Phases/ChainPhase.cs:785, 798-861](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L785-L861)) - children run one at a time, even within a level. The parent chain runs its own level loop and does NOT route through `ParallelDispatcher`. Children stack on the **accumulating base, not a frozen origin** (TLB-411): each ship advances the local target tip, and before each child the chain re-resolves the current target and recomputes the `ChainCommitRange` ([:821-832](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L821-L832)). `chainStartSha` is captured once ([:719-725](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L719-L725)). A level stops the cascade if any child fails. Per-ticket ship uses `_chainShipFactory` (`SkipDecruft=true`, [:31, 258-260](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L257)) so the shared worktree survives between children.
6. **Tear down the shared worktree once at chain end** ([src/ThroughlineBuild.Phases/ChainPhase.cs:869-881](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L869-L881)): decruft the worktree directory and force-delete the `chain/<slug>` placeholder branch so it does not collide with the next run. Failure is non-fatal.
7. After all children: attempt `RollupParentAsync` (fail-soft).
8. Outcome is `ParentCompleted` if every child succeeded, else `ParentStoppedEarly`. Child results are carried on `ChainResult.ChildResults`.

op-29 (brief 06) replaced the prior concurrent parent dispatch and per-ticket worktree-per-child layout with this width-1 serial, shared-worktree model. Running serially eliminates the cross-worker worktree races the old merge-contention machinery existed to handle (see "Multi-ticket dispatch"). The shared Plane `RequestThrottle` still paces API traffic.

Refusals enforcing the tree discipline:
- **Plan/implement refuse parent tickets:** `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs:62-65](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L62-L65)) and `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:69-76](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L69-L76)) refuse a ticket that has children.
- **Aggregate parent review** (TLB-305): `ReviewPhase.RunParentReviewAsync` classifies children - any `InProgress`/`InReview` child -> `Rework` (parent back to `InProgress`); all `Done` -> `Pass`; otherwise `Fail` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:317-370](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L317-L370)).
- **All-children-Done gate for parent ship** (TLB-305): `ShipPhase.RunParentShipAsync` blocks unless every child is `Done`; if so it transitions the parent straight to `Done` (no merge) ([src/ThroughlineBuild.Phases/ShipPhase.cs:769-808](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L769-L808)).
- **Cascade close/defer, parent-only reopen** (TLB-307): `close`/`defer` cascade the lifecycle transition to non-terminal children (unless `--no-cascade`) ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-64](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L64)); `reopen` notes the parent but does NOT reopen children ([src/ThroughlineBuild.Commands/ReopenCommand.cs:38-43](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L38-L43)).

### Loose ends

- The parent chain is exactly one level deep by design; deeper trees require the operator to chain intermediate tickets first.
- Child cascade close/defer failures are logged to stderr and do not abort the parent transition.
- A child left `Planning`/`InProgress` by an interrupted run is now **resumed** by `ResolveEntryAsync`, not refused - so a single stuck sibling no longer flips the whole parent to `ParentStoppedEarly`. An interrupted-initial `InProgress` child's orphaned branch/worktree are pruned and the branch is recreated inside the shared worktree.
- If the shared chain worktree cannot be created (commonly: its path survives a prior interrupted parent chain), the chain falls back to per-ticket standalone worktrees and now emits a loud `shared_worktree_unavailable` `GateFailure` + stderr warning instead of degrading silently.

---

## Multi-ticket dispatch

Status: **Functional**, but **serial** - the "parallel" name is now historical (see below).

`build chain TLB-A TLB-B TLB-C ...` collects positional ids beyond the first. When extra ids are present, the CLI takes the dispatcher path:

1. `GetBatchAsync` fetches all tickets.
2. Build a `ThroughlineBuild.Phases.TicketGraph` (a node+edge graph): add a node per ticket, then for each `blocked_by` relation whose target is in the dispatched set, add an edge `blocker -> blocked`.
3. Hand the graph to `ParallelDispatcher` with `config.workers.max_concurrency` - but the dispatcher **ignores it** (see below).

`ParallelDispatcher.RunAsync` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:39-164](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L39-L164)):
- `TopologicalSorter.ComputeLevels` runs Kahn's BFS to produce dependency-ordered levels, preserving input order within a level; throws `InvalidOperationException` on a cycle ([src/ThroughlineBuild.Phases/TicketGraph.cs:15-94](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L15-L94)).
- `PrintDispatchOrder` prints the derived order before any phase runs ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:61](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L61)).
- Emit `DispatchStart` (ticket_count, level_count, max_concurrency=1).
- Process each level: dispatch its tickets under a per-level `SemaphoreSlim(_maxConcurrency, _maxConcurrency)` where `_maxConcurrency` is **hard-pinned to 1** ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:31-35, 98](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L31-L35)), each running the full `ChainPhase`. Width 1 means tickets within a level run **one at a time**; dependency levels still gate cross-level ordering.
- After each level, any non-success outcome (`Completed`/`RatifiedObsolete`/`ParentCompleted` are the success set) stops further levels.
- Emit `DispatchEnd` (`outcome` = `ok`|`partial`|`RefusedDirtyTree`, total_duration_ms). A uniform dirty-tree preflight refusal is preserved instead of being collapsed to `partial`.
- Returns `ParallelDispatchResult(Success, Results, FailureReason, PreservedOutcome)`.

**Why width 1.** op-29 (brief 04) pinned concurrency to 1 unconditionally. The constructor retains the `maxConcurrency` parameter for API stability but discards it. The reasoning recorded in code: the topological order is the load-bearing part; concurrency is disposable, and running width-1 removes the cross-worker worktree races that the merge-contention machinery (`MainWorktreeLock`, divergence auto-rebase) existed to handle. The former `--max-parallel` flag / `ChainPhaseOptions.ForceParallel` surface was **removed**.

**Ancestor-skip** (TLB-313): `AncestorSkipFilter.ShouldSkip` walks a ticket's ancestors (via blocker edges); if any failed and `continuePastFailure` is false, it synthesizes a `ChainResult` with outcome `Skipped` and a `SkipReason` ([src/ThroughlineBuild.Phases/AncestorSkipFilter.cs:28-88](../../src/ThroughlineBuild.Phases/AncestorSkipFilter.cs#L28-L88)). The `--continue-past-failure` flag disables it.

`SequentialChainDispatcher` (the original fallback) runs ids one at a time, building implicit linear edges (each predecessor is an ancestor of all followers) and applying `AncestorSkipFilter` ([src/ThroughlineBuild.Commands/SequentialChainDispatcher.cs:31-66](../../src/ThroughlineBuild.Commands/SequentialChainDispatcher.cs#L31-L66)), with `ChainCommand.PrintAggregateReport` for the summary.

### Loose ends

- **`TicketDependencyGraph` was removed** (op-29 brief 05). Only one `TicketGraph` survives: `ThroughlineBuild.Phases.TicketGraph` (the mutable node/edge class consumed by `ParallelDispatcher`/`TopologicalSorter`, [src/ThroughlineBuild.Phases/TicketGraph.cs:4](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L4)). The former two-overlapping-`TicketGraph`-types seam is resolved.
- **The dispatcher name is now a misnomer.** `ParallelDispatcher` runs strictly serially (width 1). Both the multi-ticket path and the parent-chain level loop are serial; the difference is the parent chain runs its own `SemaphoreSlim(1,1)` loop rather than routing through `ParallelDispatcher`.
- A dispatcher failure stops *subsequent levels*; within the now width-1 levels there is at most one in-flight ticket anyway.

---

## Divergence and merge orchestration

Status: **Functional**. TLB-290/291/293/296/297/298.

`ShipPhase` resolves the rebase target (the configured target branch) by ancestry, and when the local target and `<remote>/<target>` have diverged it probes for conflicts ([src/ThroughlineBuild.Phases/ShipPhase.cs:330-410](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L330-L410)):

- `localIsAncestorOfRemote && !remoteIsAncestorOfLocal` -> `<remote>/<target>` (reason `origin_target_ahead`).
- `remoteIsAncestorOfLocal && !localIsAncestorOfRemote` -> local `<target>` (reason `local_target_ahead`).
- both -> same commit (reason `same_commit`).
- neither (diverged) -> `IGitClient.ProbeDivergenceAsync` (TLB-296), which uses `git merge-tree --write-tree` to classify without mutating, returning a `DivergenceState`: `Clean, LocalAhead, RemoteAhead, DivergedNoConflict, DivergedWithConflict` ([src/ThroughlineBuild.Contracts/IGitClient.cs:22-29](../../src/ThroughlineBuild.Contracts/IGitClient.cs#L22-L29), [src/ThroughlineBuild.Git/ProcessGitClient.cs:962](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L962)).
  - `DivergedNoConflict` and NOT `--no-auto-merge` (TLB-297/298): auto-rebase the local target onto `<remote>/<target>` under `MainWorktreeLock`. On success emit `TargetAutoRebased` (`outcome=clean`) and rebase the feature onto the local target. On a race-to-conflict, abort the rebase, emit `TargetAutoRebased` (`outcome=raced_to_conflict`) + a `GateFailure`, and fail at the `Fetch` stage ([src/ThroughlineBuild.Phases/ShipPhase.cs:356-411](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L356-L411)).
  - Otherwise (conflict, or `--no-auto-merge`): post `ship_blocked` comment, emit `GateFailure`, fail at `Fetch`.

`MainWorktreeLock` (TLB-290/291) is a per-path in-process `SemaphoreSlim` keyed on the normalized main-worktree path; it serializes the fetch, the target-branch auto-rebase, and the fast-forward merge on the shared main worktree ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:6-29](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L6-L29)). With dispatch now width-1 serial (op-29), it is largely defensive - it remains correct if concurrency were ever reintroduced.

After a successful FF merge, when a remote exists and `--no-push` is not set the phase pushes the target branch to the remote (TLB-293); a push failure fails the ship at the `Push` stage. The step-6 preflight guarantees the main worktree is on the target branch before the FF merge advances it.

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
| **`blocked_by` relations** | The dependency edges that drive multi-ticket and sibling dispatch ordering (serial, width 1; levels gate ordering only). |
| **`.build/events/<stem>.jsonl`** | Replayable audit log. The rework feedback retriever reads it to recover the most recent `Rework` verdict. |
| **`.worktrees/ticket-<slug>/`** | The implementer's checkout. Reviewer reads its diff; shipper rebases + merges from it. |
| **Local git branch `ticket/<slug>`** | The carrier of the actual commits. |
| **`MainWorktreeLock`** | In-process serialization of main-worktree git ops across concurrent chains. |

There is no message bus and no persistent in-process state between separate `build` invocations. Every restart re-reads from Plane + git + events.

### Loose ends

- Within a single `build chain` of multiple tickets / a parent chain, in-process state (semaphores, the lock) *does* persist for the run; the "no shared state" principle holds only across separate process invocations.

---

## Sessions

Every `build <verb>` invocation mints session ids via `_sessionIdGenerator` (default `Guid.NewGuid().ToString("N")`). In a chain, each phase gets its own per-phase session id, recorded on `ChainStep.PhaseSessionId`, while the chain lifecycle events (`ChainStart`/`ChainEnd`) carry a single `chainSessionId` ([src/ThroughlineBuild.Phases/ChainPhase.cs:77, 136-147](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L77)). The dispatcher mints its own `dispatchSessionId` for `DispatchStart`/`DispatchEnd` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:45](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L45)).

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
| `GateFailure` | 4 | the workhorse: drift warning, hygiene gates, ship pre-flight/diverged/rebase/conflict-marker/regression failures, post-phase dirty-worktree, shared-worktree-unavailable | `kind` discriminator + reason |
| `TicketWrite` | 5 | every Plane write (description / labels / comments / create / set-parent / rollup / fetch_skipped / base_ref_resolved / decruft / delete_branch) | action + payload summary |
| `ChainStart` | 6 | `ChainPhase` | starting_at_phase, initial_state, chain_session_id |
| `ChainEnd` | 7 | `ChainPhase` | outcome, phases_run, rework_rounds, total_duration_ms |
| `ReworkRound` | 8 | `ChainPhase` | round, verdict_that_triggered, rationale_preview |
| `TicketSubsumed` | 9 | `ChainPhase` (obsolete ratification Pass) | ticket_id, subsumed_by_commit, files, rationale |
| `TargetAutoRebased` | 10 | `ShipPhase` (DivergedNoConflict auto-rebase; renamed from `MainAutoRebased`) | from_sha, onto_sha, local_commits_replayed, outcome (clean / raced_to_conflict) |
| `DispatchStart` | 11 | `ParallelDispatcher` | ticket_count, level_count, max_concurrency |
| `DispatchEnd` | 12 | `ParallelDispatcher` | outcome (ok / partial / RefusedDirtyTree), total_duration_ms |

Full event-line schema in [docs/event-log-format.md](../event-log-format.md).

### Loose ends

- `event-log-format.md` does not enumerate the per-`Data` shape of every kind; the authoritative `Data` keys are in the emitting code cited above.
- `DispatchStart`/`DispatchEnd` carry an empty `TicketId` (they are batch-scoped, not ticket-scoped); `max_concurrency` is always 1.
- `GateFailure` carries a `kind` discriminator string identifying which gate fired: `hygiene_gate`, `hygiene_gate_preflight`, `chain_preflight_dirty`, `pre_flight_hygiene`, `pre_flight_dirty`, `drift_warning`, `wrong_worktree_branch`, `dirty_worktree_first_attempt`/`dirty_worktree_retry_failed`, `dirty_worktree_after_review`, `implemented_at_superseded`, `shared_worktree_unavailable`, `parent_children_not_done`, etc.

---

## Chain outcomes and exit codes

`ChainOutcome` enum ([src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:3-18](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3-L18)) and the single-ticket exit-code mapping in `Program.cs`:

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RatifiedObsolete` | 0 | obsolete claim ratified; ticket -> Done |
| `ParentCompleted` | 0 | all eligible children completed |
| `RefusedInitialState` | 2 | terminal state (`Done`/`Cancelled`); `Planning`/`InProgress` are now resumed, not refused |
| `RefusedDirtyTree` | 2 | preflight gate: conflict, unrelated stash, or tracked main-worktree changes at chain start, refused before planning |
| `ParentHasGrandchildren` | 2 | tree deeper than one level |
| `StoppedAtPlan` | 3 | planning failed |
| `ParentStoppedEarly` | 3 | a child did not complete |
| `Skipped` | 3 | skipped because an ancestor failed |
| `StoppedAtImplement` | 4 | implementation failed |
| `StoppedAtReview` | 5 | review returned `Fail` (or review infra failure) |
| `ReworkCapExceeded` | 6 | more than `MaxReworkRounds` reworks |
| `StoppedAtShip` | 7 | ship gate failed |

Success set used by dispatchers and the aggregate report: `Completed`, `RatifiedObsolete`, `ParentCompleted` (`Skipped` is treated as non-failure for the overall exit-0 decision in the sequential path) ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:128-133](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L128-L133), [src/ThroughlineBuild.Phases/ChainPhase.cs:950-953](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L950-L953)).

### Loose ends

- The multi-ticket dispatcher path returns a flat `0`/`1` from `dispatchResult.Success`, not the per-outcome exit codes above; the granular mapping is only used on the single-ticket path.

---

## Where the chain stops cleanly vs. requires manual triage

- **Clean stop / success:** `Completed`, `RatifiedObsolete`, `ParentCompleted`, `Skipped`, `RefusedInitialState`, `ReworkCapExceeded` (operator picks up with `build rework`/`build review`).
- **Requires triage:** `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview`, `StoppedAtShip`, `ParentStoppedEarly`, `ParentHasGrandchildren`, `RefusedDirtyTree` (clean the tree - resolve conflicts, drop the stray stash, or commit/stash/revert tracked main-worktree changes - then re-chain). Each leaves the ticket(s) in whatever state the failing phase left them.

`ChainCommand` surfaces a one-line final summary per outcome on stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs:143-187](../../src/ThroughlineBuild.Commands/ChainCommand.cs#L143-L187)).

---

## Loose ends (cross-cutting)

- **`MaxReworkRounds = 2` is hardcoded; all dispatch is serial since op-29.** Both the parent chain (`SemaphoreSlim(1, 1)` level loop) and the multi-ticket `ParallelDispatcher` (concurrency hard-pinned to 1) run one ticket at a time. `workers.max_concurrency` is read but ignored by the dispatcher; no concurrency knob remains.
- **No cross-phase live channel.** ReviewPhase reconstructs the implementer brief deterministically. Architecture's "no shared in-memory context with the implementer" principle (Section 5.8) still holds for the worker hand-off, but the chain itself now holds in-process orchestration state for a run.
- **Chain `WorkflowEvent.Data`** carries per-step/per-dispatch fields whose schema lives in code, not exhaustively in [docs/event-log-format.md](../event-log-format.md).
- **No replay verb** (`build replay <session-id>`). Architecture Appendix item 4 notes this as a future.
- **Phase ordering documented in `ChainPhase`** - operators running phases manually out of order hit each phase's state guard.
- **`SequentialChainDispatcher`** remains as a legacy fallback alongside the now-serial `ParallelDispatcher`; the prior two-`TicketGraph`-types seam was resolved by op-29 removing `TicketDependencyGraph` (detailed in "Multi-ticket dispatch").
