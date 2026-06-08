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
                                                          (chain) gate              |              |
                                                                     v              |              |
                                                                     +-> review ----+              |
                                                                     ^              |              |
                                                                     |              |              |
                            Rework verdict / gate hard-fail ---------+              |              |
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

In a chain run, a deterministic `gate` step sits between `implement` and `review` (see "The verification gate"). It runs the configured checks once on the warm worktree; a Gating-role failure transitions `InReview -> InProgress` and re-enters the rework loop exactly like a `Rework` verdict. The standalone `implement`/`review` verbs have no gate. The `Gate` phase value (op-30) brings the `Phase` enum to 11.

Backed by these transitions in code:

| Phase | Source state | Target state(s) |
|---|---|---|
| `plan` | `Backlog` | `Planning` -> `Ready` ([src/ThroughlineBuild.Phases/PlanPhase.cs:98, 143-148](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98)) |
| `plan` (promote) | `Backlog` | `Planning` -> `Ready` with no worker/LLM ([src/ThroughlineBuild.Phases/PlanPhase.cs:203-230](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L203-L230)) |
| `implement` (initial) | `Ready` | `InProgress` -> `InReview` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:251-260](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L251-L260)) |
| `implement` (rework) | `InProgress` | `InReview` (no `InProgress` re-entry) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:252-260](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L252-L260)) |
| `gate` (chain-only, hard-fail) | `InReview` | `InProgress` (re-enters the rework loop) ([src/ThroughlineBuild.Phases/GatePhase.cs:137-149](../../src/ThroughlineBuild.Phases/GatePhase.cs#L137-L149)) |
| `gate` (chain-only, pass) | `InReview` | no change (review runs next) |
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
`PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `DecomposePhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `ScaffoldPhase`, plus the chain-only `GatePhase`. The `Phase` enum has 11 values: `Plan, Implement, Review, Ship, Chain, New, Command, Draft, Scaffold, Decompose, Gate` (`Gate` added by op-30; [src/ThroughlineBuild.Contracts/Models/Phase.cs:3](../../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3)). `GatePhase` is not an `IWorkflowPhase` - it is invoked only inside the chain's implement-review loop and emits its events under `Phase.Gate`.

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
13. Structured-failure salvage (Step 14, TLB-471/476): a clean-exit worker that returned no usable `WORKER_RESULT` is salvaged - not failed - when the worktree is clean and HEAD advanced past base, for `envelope_status=missing` (no marker) and `envelope_status=missing_status` (valid JSON, no `status` key). The commit SHA is reconstructed from git HEAD ([src/ThroughlineBuild.Phases/ImplementPhase.cs:327-346, 479-525](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L327-L346)).
13b. Post-worker dirty-tree check (Step 14b) with one bounded retry: if the worker left uncommitted files, re-run with a "commit everything" note; still dirty -> `GateFailure` and fail ([src/ThroughlineBuild.Phases/ImplementPhase.cs:355-392](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L355-L392)).
14. Validate `commit_sha` metadata. Compare against actual `git rev-parse HEAD` in worktree; actual wins on discrepancy (a discrepancy note is folded into the marker comment).
14b. COMPLETION_CLAIM (Step 15c, TLB-505): if the worker opted in via the `completion_claim_ref` metadata key, resolve and parse the `COMPLETION_CLAIM` block (all four implement templates emit it; parsed by `CompletionClaimParser` in `Workers.Common`). A missing/unparseable block triggers ONE targeted re-ask (`GateFailure` `kind = claim_invalid_first_attempt`) before a hard failure - not a rework round. Workers that omit the key are treated as pre-claim format (null claim) ([src/ThroughlineBuild.Phases/ImplementPhase.cs:410-435](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L410-L435)). The parsed claim is carried on `ImplementResult.CompletionClaim` for the gate.
15. Post `[implemented_at: <actualSha>]` comment naming the branch; if the worker supplied a `summary_ref` -> `IMPLEMENT_SUMMARY` fenced block, render it via `MarkdownRenderer` and append it.
16. Transition `InProgress -> InReview`.

### `GatePhase` ([src/ThroughlineBuild.Phases/GatePhase.cs](../../src/ThroughlineBuild.Phases/GatePhase.cs))

Status: **Functional**. Added by op-30. Chain-only: the chain inserts it between `implement` and `review` (the standalone `implement`/`review` verbs never run it). The production CLI always wires `gatePhaseFactory`, so every chain runs the gate; `_gateFactory == null` (gate skipped) only happens in tests.

`RunAsync(ticketId, worktreePath, branchName, baseRef, workingDirectory, claim, ct, accumulatedUpstreamProvides)` ([src/ThroughlineBuild.Phases/GatePhase.cs:48-135](../../src/ThroughlineBuild.Phases/GatePhase.cs#L48-L135)):
1. **Claim schema validation** (only if the worker emitted a claim; a null claim is a legal pre-claim-format worker and proceeds). `ValidateClaim` requires non-null `provides`/`consumes`/`ac_bindings`/`tests_added`. A schema-invalid claim emits `GateFailure` `kind = claim_schema_invalid`, transitions `InReview -> InProgress`, and returns failed.
2. **Run the `[[review.checks]]` capability map ONCE** on the warm worktree via `AutomatedChecksRunner` (or an injected runner), with check roles `Gating` (build/test/typecheck) and `Advisory` (lint/format).
3. **Collect smoke signals** via `SmokeCollector.CollectDiffFacts` over the `baseRef..branch` diff - advisory; a diff failure degrades to an advisory `diff unavailable` signal.
4. **Consumes/provides preflight** (only when `claim.Consumes` is non-empty): compares the claim's consumes against the `accumulatedUpstreamProvides` set; emits an advisory smoke signal and NEVER hard-fails.
5. **Hard-fail only on failed Gating-role checks:** emits `GateFailure` `kind = gating_checks_failed` (+ `checks_failed`), transitions `InReview -> InProgress`, posts a `[gate: hard-fail]` Plane comment (best-effort), and returns failed. Advisory failures are recorded but never block.
6. On pass, returns a `GateOutcome(Passed: true, CheckResults, SmokeSignals)` that the chain forwards to `ReviewPhase`.

The gate's `CheckResults` are reused by `ReviewPhase` (check-reuse, TLB-502; see "ChainPhase" wiring) so the checks run once per ticket, not twice. On hard-fail the chain routes to a rework round carrying `GateFailedChecks` (see "The chain rework loop"). The comment-post and the `InReview -> InProgress` transition are both best-effort (swallowed on failure) so a Plane hiccup cannot block the rework loop.

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
18. `WorktreeDecrufter.DecruftAsync` - **skipped when `SkipDecruft` is set** (the chain ship factory sets it so the integration worktree survives between children, [src/ThroughlineBuild.Phases/ShipPhase.cs:706-716](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L706-L716)); otherwise failure is non-fatal post-merge.
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

Status: **Functional**. The orchestrator. Constructed in `Program.cs` via the `ChainPhaseComposition.BuildChainPhase` seam (extracted so no required dependency can be silently dropped - the original `--batch-implement` bug was exactly a dropped `batchWorker` ctor arg). The composition root wires per-phase factories closed over the shared `PlaneTicketingClient`, worker agents, and `IEventSink`, plus `ratifierFactory` (obsolete-claim handling), `chainShipFactory` (`SkipDecruft=true`, NoPush) for integration-branch ships, the `batchWorker` (the implement agent), root-landing remote/push, and `gateFactory` ([src/ThroughlineBuild.Cli/ChainPhaseComposition.cs:22-64](../../src/ThroughlineBuild.Cli/ChainPhaseComposition.cs#L22-L64), call site [src/ThroughlineBuild.Cli/Program.cs:1849-1864](../../src/ThroughlineBuild.Cli/Program.cs#L1849-L1864)). `MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:60](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L60)).

`RunAsync` entry ([src/ThroughlineBuild.Phases/ChainPhase.cs:137-486](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L137-L486)):

1. Fetch the ticket.
2. **Outermost-only preflight** (skipped on recursion, where `ChainTargetBranch` is set), in order ([src/ThroughlineBuild.Phases/ChainPhase.cs:151-246](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L151-L246)): a **wrong-branch gate** (main worktree must be on `_baseOptions.TargetBranch` or `GateFailure kind = chain_preflight_wrong_branch` -> `RefusedWrongBranch`); a **hygiene gate** (dangling stash/conflict -> `kind = hygiene_gate_preflight` -> `RefusedDirtyTree`); and a **tracked-dirty gate** (tracked changes in the main checkout -> `kind = chain_preflight_dirty` with `dirty_count`/`dirty_paths` -> `RefusedDirtyTree`). All three return before planning, transitions, or worker spawn.
3. Cycle guard (`VisitedTicketUuids`) and `--dry-run` schedule (returns `DryRunPreview`).
4. Query children; if any exist, delegate to `RunParentChainAsync` (the tree-aware path), unless the depth cap is hit (`ParentStoppedEarly`) ([src/ThroughlineBuild.Phases/ChainPhase.cs:282-296](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L282-L296)).
5. Otherwise route on state via `ResolveEntryAsync` (the **resume state machine**, [src/ThroughlineBuild.Phases/ChainPhase.cs:2855-2877](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2855-L2877)):
   - `Backlog` -> start at Plan
   - `Ready` -> start at Implement
   - `InReview` -> start at Review
   - `Planning` -> a plan that never finished (the `Backlog -> Planning` transition precedes the worker, and no plan artifact is written until it succeeds): reset to `Backlog`, emit a `chain_resume` `StateTransition`, start at Plan
   - `InProgress` -> `ResolveInProgressAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:2888-2921](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2888-L2921)): count commits on `ticket/<id>` beyond base. **0 commits** (an interrupted *initial* implement transitions `Ready -> InProgress` before the worker commits) -> `PruneOrphanBranchAsync` removes the orphaned branch/worktree, reset to `Ready`, start a clean Implement - in a parent chain this lets the branch be recreated inside the shared worktree instead of an orphaned standalone one. **Has commits** (interrupted rework) -> `ResumeImplement` at round 1 reusing the worktree, recovering the last `Rework` feedback from the event log or synthesizing a neutral resume note.
   - `Done` / `Cancelled` -> `Refused`: emit `ChainStart`, return `RefusedInitialState` (the only genuinely un-runnable states)
   `ResolveEntryAsync` performs the reset/prune side effects and emits the `chain_resume` `StateTransition` events; resume transitions carry reason `chain_resume`.
6. Emit `ChainStart`.
7. If starting at Plan: emit a console-only START notice (see below), run `PlanPhase`. On failure, if `!NoAutoResolve` and the worker `Escalate`d with reason `obsolete`, run obsolete-claim ratification (see "Obsolete-claim handling"). Otherwise return `StoppedAtPlan`.
8. If Plan succeeded, starting at Implement, or `ResumeImplement`: enter the **implement -> gate -> review loop** (`RunImplementReviewLoopAsync`). `ResumeImplement` re-enters the loop as a rework round (carries recovered/synthesized feedback at round >= 1) so `ImplementPhase` reuses the in-progress worktree.
9. If starting at Review only (`InReview` resume): run one review with no gate (`RunReviewBranchAsync` - the gate runs only after a fresh implement; `ReviewPhase` runs its own checks here). Pass -> Ship; Rework -> implement-gate-review loop; Fail -> `StoppedAtReview`.
10. Run `ShipPhase` (using `_chainShipFactory` into the integration worktree when inside a parent chain - see "Tree-aware chain"). Fail -> `StoppedAtShip`.
11. Emit `ChainEnd`. Return `Completed` (carrying `ShippedProvides` from the implement claim).

**Per-phase START notice (TLB-415).** Before each phase the chain calls `EmitPhaseStart` ([src/ThroughlineBuild.Phases/ChainPhase.cs:491-503](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L491-L503)), which pushes a `ChainStep` through the `OnStep` stream so the operator sees a phase has begun, not just its completion line. These START steps are **console-only** - never added to the `steps` list, so the returned `ChainResult` and its `phases_run` count are unchanged. Called before plan ([:342](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L342)), ship ([:441](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L441)), implement ([:558](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L558)), gate ([:637](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L637)), review ([:854](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L854)), and ratify ([:1045](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1045)).

**Chain commit-range handoff (op-29 briefs 08-11).** A parent chain derives a git commit range (`ChainCommitRange` / `ChainCommitRangeHelper.ComputeAsync`, [src/ThroughlineBuild.Helpers/ChainCommitRange.cs:13-83](../../src/ThroughlineBuild.Helpers/ChainCommitRange.cs#L13-L83)) describing the commits already produced by shipped siblings, and threads it onto `ChainPhaseOptions.ChainCommitRange`. The implement loop passes it to `ImplementBriefBuilder` **only on the first implement round** (`feedback is null`); rework rounds suppress it because the worktree already holds the agent's prior edits ([src/ThroughlineBuild.Phases/ChainPhase.cs:553-557](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L553-L557)). The builder folds the range's touched files into `RelevantFiles` and adds a single bounded `chain_pointer` line to Context, but **only when the range is non-empty** - an empty/absent range leaves the brief byte-identical to the pre-handoff baseline ([src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs:50-90](../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L50-L90)). It is no-LLM and best-effort: any git failure yields `CommitCount=0`. Gated end-to-end by the `HandoffPointerEnabled` compile const ([src/ThroughlineBuild.Phases/ImplementPhase.cs:34](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L34)).

The chain invokes an `Action<string, ChainStep>` callback (`OnStep`) after each phase (and for START notices); `ChainCommand` uses it to stream one-line, per-ticket-prefixed summaries to stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)). Per-ticket output prefixing (`[<ticketId>] `) is done by `PrefixedTextWriter` in `BuildPhaseOptions` ([src/ThroughlineBuild.Phases/ChainPhase.cs:905-931](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L905-L931), TLB-403).

Each phase runs under its own per-phase session id minted from `_sessionIdGenerator`; the chain itself has a single `chainSessionId` used on `ChainStart`/`ChainEnd` ([src/ThroughlineBuild.Phases/ChainPhase.cs:142, 316-327](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L142)).

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
- The architecture doc still describes a 9-value `Phase` enum and `ClaudeCodeReviewer` as the default verifier; both are stale - the enum has 11 values (op-30 added `Gate`) and the verifier is `WorkerAgentReviewer`.

---

## The chain rework loop

`RunImplementReviewLoopAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:527-761](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L527-L761)):

```
              +-------------------+
              | ImplementPhase    |
              +---------+---------+
                        |
                        | InProgress -> InReview
                        v
              +---------+---------+
              | GatePhase (chain) |  (when _gateFactory != null)
              +---------+---------+
                        |
                  pass  |  gating_checks_failed / claim_schema_invalid
                        |   (InReview -> InProgress, gate-attributable rework)
                        v          \
              +---------+---------+  \-> round++ (verdict_that_triggered=GateFailure)
              | ReviewPhase       |       |
              +---------+---------+       |
                        |                 |
            +-----------+-----------+     |
            |           |           |     |
          Pass       Rework        Fail   |
            |           |           |     |
            v           v           v     |
        ShipPhase   round++     return    |
                        |       StoppedAtReview
                  round < 2 ?  <----------+
                  +-----+-----+
                  |           |
                yes          no
                  |           |
        (back to top)   return ReworkCapExceeded
```

`MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:60](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L60)) means at most 1 rework + 1 reattempt, i.e. up to 3 implement runs total. Both a review `Rework` verdict **and** a gate hard-fail consume a rework round and emit a `ReworkRound` event carrying `round`, `verdict_that_triggered` (`"Rework"` or `"GateFailure"`), `rationale_preview` ([src/ThroughlineBuild.Phases/ChainPhase.cs:684-703, 736-751](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L684-L703)). A gate-triggered rework builds a `ReviewFeedback` with `GateFailedChecks` populated (TLB-509) so the next implement brief renders a dedicated gate-failure section; gate-attributable rounds/tokens are tracked for the `CostLedger` event. Operators wanting more rounds invoke `build rework` manually after `ReworkCapExceeded`.

The gate runs only after a *fresh* implement round inside this loop (it reads `implResult.CompletionClaim` and the warm worktree). When the chain starts at Review (state `InReview`), the first review runs in `RunReviewBranchAsync` with **no gate** - `ReviewPhase` runs its own checks - and a `Rework` there hands off to the loop with `startRound = round + 1` and the recovered feedback ([src/ThroughlineBuild.Phases/ChainPhase.cs:763-801](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L763-L801)).

A single `CostLedger` event is emitted per gate-engaged ticket on loop exit (`EmitCostLedgerAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:805-838](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L805-L838)): `gate_wall_ms`, `gate_attributable_rework_rounds`, and gate-attributable token fields when available. Nothing reads it yet (aspirational).

### Loose ends

- `MaxReworkRounds = 2` is hardcoded; not configurable per ticket or repo. It bounds both review-verdict and gate-hard-fail reworks (they share the same `round` counter).
- A review-phase *infra* failure (worker crash) returns `StoppedAtReview` with the failure reason, distinct from a `Fail` verdict ([src/ThroughlineBuild.Phases/ChainPhase.cs:860-874](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L860-L874)).
- The `CostLedger` event has no consumer; its `cascade_caught` / `false_fails` slots are always 0 (annotatable post-hoc).

---

## Obsolete-claim handling (ratification)

Status: **Functional**. Added by TLB-282/283/285.

A worker (plan or implement) may return `Status.Escalate` with an `escalation.reason == "obsolete"` claim plus a `subsumed_by` block (commit, files, rationale). When the chain sees this and `--no-auto-resolve` was NOT supplied, it runs ratification ([src/ThroughlineBuild.Phases/ChainPhase.cs:359-402, 588-628](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L359-L402)):

1. `IsObsoleteEscalation` confirms the escalation shape ([src/ThroughlineBuild.Phases/ChainPhase.cs:987-995](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L987-L995)).
2. `RunRatificationAsync` invokes the `ObsoleteRatifier`, recording a `ratify` `ChainStep` ([src/ThroughlineBuild.Phases/ChainPhase.cs:1032-1062](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1032-L1062)).
3. `ObsoleteRatifier.RatifyAsync` performs three checks ([src/ThroughlineBuild.Verification/ObsoleteRatifier.cs:32-79](../../src/ThroughlineBuild.Verification/ObsoleteRatifier.cs#L32-L79)): (a) cited commit exists (`git rev-parse <commit>^{commit}`), (b) cited files exist at HEAD, (c) a model-driven check that the prior work meets the ticket's acceptance criteria.
4. On `Pass`: the chain transitions the ticket to `Done`, posts a "Subsumed by ..." comment, emits a `TicketSubsumed` event, and returns `RatifiedObsolete` (a success outcome). On reject, it falls through to `StoppedAtPlan` / `StoppedAtImplement`.

`--no-auto-resolve` (CLI flag, parsed at [src/ThroughlineBuild.Cli/Program.cs:85](../../src/ThroughlineBuild.Cli/Program.cs#L85) and threaded as `ChainPhaseOptions.NoAutoResolve`, [src/ThroughlineBuild.Cli/Program.cs:1944](../../src/ThroughlineBuild.Cli/Program.cs#L1944)) disables this and forces the escalation to be treated as a plain stop.

### Loose ends

- Ratification only triggers from the chain, not from a standalone `build plan`/`build implement` (those just return the failure).
- `RatifiedObsolete` is treated as success by the dispatchers and the aggregate report.

---

## Tree-aware chain (parent tickets)

Status: **Functional**. TLB-304/305/306/307; now **recursive** (TLB-492/494) - grandchildren are handled in the same run, not refused. See [docs/grandparent-chain.md](../grandparent-chain.md) for the worked example.

When a chained ticket has children, `RunParentChainAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:1903-2354](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1903-L2354)) runs instead of the per-phase chain:

1. Filter children to non-terminal (not `Done`/`Cancelled`) and never the parent itself; order by ascending ticket number ([src/ThroughlineBuild.Phases/ChainPhase.cs:1916-1921](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1916-L1921), TLB-397). `TopologicalSorter` preserves input order as its within-level tiebreaker, so feeding it lowest-number-first dispatches unordered siblings lowest-number-first.
2. **Sibling dependency ordering:** build a `blocked_by` dependency graph over the eligible siblings (`BuildSiblingGraphAsync`, [:2819-2853](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2819-L2853)) and `TopologicalSorter.ComputeLevels` it into dependency-ordered levels ([:1925-1926](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1925-L1926)). `PrintDispatchOrder` prints the derived order before any phase runs so a wrong/missing edge is visible up front ([:1931](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1931)). Siblings within a level have no `blocked_by` edge and are ordered lowest-number-first; a blocked sibling waits for its blocker's level.
3. **Cut ONE integration worktree for the parent** (`EnsureIntegrationWorktreeAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:1947-1986](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1947-L1986)): a single worktree on `chain/<parent>` at the resolved base (`integrationBaseRef = ChainTargetBranch ?? TargetBranch`). Each leaf child creates its `ticket/<id>` branch INSIDE this worktree and ships into the integration branch (not the main worktree, which stays parked on the configured root). If the worktree cannot be created, the chain emits `GateFailure kind = integration_worktree_unavailable` and returns `ParentStoppedEarly` (no per-ticket fallback - the integration branch is load-bearing for accumulation).
4. **(Optional) batch-implement** ([src/ThroughlineBuild.Phases/ChainPhase.cs:1991-2196](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1991-L2196)): when `--batch-implement` selected a group, the eligible leaf candidates are built in ONE warm worker session before the per-ticket level loop runs (see "Batch-implement orchestration"). Batched ticket ids are recorded so the level loop skips them.
5. Recurse `RunAsync` on each remaining eligible child, level by level, **one at a time** ([src/ThroughlineBuild.Phases/ChainPhase.cs:2203-2314](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2203-L2314)). A child that is itself a parent recurses into its own `RunParentChainAsync` (`Depth + 1`, with `VisitedTicketUuids` extended for the cycle guard); a leaf runs plan/implement/gate/review/ship into the integration branch. The parent chain runs its own serial level loop and does NOT route through `ParallelDispatcher`. Children stack on the **accumulating integration branch, not a frozen origin** (TLB-411): before each child the chain re-resolves the current `integrationBranch` tip and recomputes the `ChainCommitRange` ([:2236-2247](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2236-L2247)); `chainStartSha` is captured once ([:1940-1945](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1940-L1945)). Each child's gate receives `AccumulatedUpstreamProvides` - the union of all previously shipped siblings' `provides` ([:2198-2201, 2281-2284](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2198-L2201)). A level stops the cascade if any child fails. Leaf ship uses `_chainShipFactory` (`SkipDecruft=true`, NoPush) so the integration worktree survives between children and the remote is touched only at root landing.
6. **Accumulate a finished sub-chain** ([src/ThroughlineBuild.Phases/ChainPhase.cs:2286-2312](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2286-L2312)): when a child returns `ParentCompleted` (it was itself a parent), its integration branch is rebased-then-fast-forwarded onto this parent's integration branch (`RebaseThenFastForwardAsync`, reason `chain_accumulate`, TLB-494). A conflict leaves the work safe on the sub-chain branch and flips the result to `ParentStoppedEarly`.
7. **Root landing** ([src/ThroughlineBuild.Phases/ChainPhase.cs:2318-2334](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2318-L2334)): the OUTERMOST chain (`ChainTargetBranch is null`) lands its accumulated integration branch onto the configured target in the main worktree - which the preflight pinned to that target - and pushes (when a remote and push are configured) via `LandRootIntegrationBranchAsync` (TLB-492). A nested parent has no landing; its `ParentCompleted` merge in step 6 carries the work up to its own parent. A landing failure leaves all work on the integration branch and returns `ParentStoppedEarly`.
8. After all children: attempt `RollupParentAsync` (fail-soft). Outcome is `ParentCompleted` if every child succeeded, else `ParentStoppedEarly`. Child results are carried on `ChainResult.ChildResults`.

op-29 made dispatch serial; the recursive integration-branch model (TLB-492/494) replaced the prior one-level shared-worktree layout. Running one ticket at a time eliminates the cross-worker worktree races the old merge-contention machinery existed to handle (see "Multi-ticket dispatch"). The shared Plane `RequestThrottle` still paces API traffic.

Refusals enforcing the tree discipline:
- **Plan/implement refuse parent tickets:** `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs:62-65](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L62-L65)) and `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:69-76](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L69-L76)) refuse a ticket that has children.
- **Aggregate parent review** (TLB-305): `ReviewPhase.RunParentReviewAsync` classifies children - any `InProgress`/`InReview` child -> `Rework` (parent back to `InProgress`); all `Done` -> `Pass`; otherwise `Fail` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:371-408](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L371-L408)).
- **All-children-Done gate for parent ship** (TLB-305): `ShipPhase.RunParentShipAsync` blocks unless every child is `Done`; if so it transitions the parent straight to `Done` (no merge) ([src/ThroughlineBuild.Phases/ShipPhase.cs:778-810](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L778-L810)).
- **Cascade close/defer, parent-only reopen** (TLB-307): `close`/`defer` cascade the lifecycle transition to non-terminal children (unless `--no-cascade`) ([src/ThroughlineBuild.Commands/CloseCommand.cs:48-64](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L48-L64)); `reopen` notes the parent but does NOT reopen children ([src/ThroughlineBuild.Commands/ReopenCommand.cs:38-43](../../src/ThroughlineBuild.Commands/ReopenCommand.cs#L38-L43)).

### Loose ends

- The parent chain now recurses to arbitrary depth (bounded by `--max-depth`, default 16, and the `VisitedTicketUuids` cycle guard); the old one-level rule and the `ParentHasGrandchildren` refusal are gone (the enum value remains but is unreachable from traversal).
- Child cascade close/defer failures are logged to stderr and do not abort the parent transition.
- A child left `Planning`/`InProgress` by an interrupted run is **resumed** by `ResolveEntryAsync`, not refused - so a single stuck sibling no longer flips the whole parent to `ParentStoppedEarly`. An interrupted-initial `InProgress` child's orphaned branch/worktree are pruned and the branch is recreated inside the integration worktree.
- The integration worktree on `chain/<parent>` is torn down nowhere explicitly in `RunParentChainAsync`; a leftover placeholder branch from a prior interrupted run is self-healed by `EnsureIntegrationWorktreeAsync` on the next run.

---

## Batch-implement orchestration (op-32)

Status: **Functional**. TLB-444..454/473/494. `build chain <id> --batch-implement [list]` runs ONLY inside the parent-chain path (`RunParentChainAsync`). A "batch session" is ONE warm worker session (`_batchWorker` - the same agent as per-ticket implement) that implements N children in one shot inside the shared integration worktree; all batch commits stack on `ticket/<firstId>`. The brief is `BatchImplementBriefBuilder`; the worker size is the max across the batch tickets; tickets are transitioned `Ready -> InProgress` before dispatch ([src/ThroughlineBuild.Phases/ChainPhase.cs:1079-1383](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1079-L1383)).

**Candidate selection** ([src/ThroughlineBuild.Phases/ChainPhase.cs:1991-2090](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1991-L2090)): `AllEligibleChildren` (bare `--batch-implement`) or `ExplicitList`. Only `Ready` or `Backlog` candidates are batched; `Backlog` candidates are PLANNED per-ticket first (planning stays per-ticket via `PlanForBatchAsync` - a plan failure stops the chain with `StoppedAtPlan`). Internal nodes (candidates with their own live children) are EXCLUDED from the batch (`GateFailure kind = batch_skip_internal_node`) and chained as parents in the level loop.

**Size caps** (`CheckBatchSizeCaps`, [src/ThroughlineBuild.Phases/ChainPhase.cs:1646-1672](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1646-L1672)) from `[batch]` config (`BuildOptions` fields): `max_tickets=8`, `max_size_score=16` (S=1/M=2/L=4), `max_description_bytes=200000`. A violation emits `GateFailure kind = batch_size_cap_exceeded` + a console line and the batch **falls back to the per-ticket chain** (the now-Ready planned tickets resume at implement, no re-plan). When the batch path is requested but cannot run at all (no batch worker, no eligible children), a loud `GateFailure kind = batch_implement_unavailable` + console line precedes the per-ticket fallback ([:2169-2196](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2169-L2196)).

**Commit-attribution verification** (`BatchCommitVerifier.VerifyAsync`, [src/ThroughlineBuild.Phases/BatchCommitVerifier.cs:37-116](../../src/ThroughlineBuild.Phases/BatchCommitVerifier.cs#L37-L116)): re-derives the actual commits via `git log baseRef..HEAD`, requires the worktree clean, and maps reported `stack_position` to log index monotonically. **Fails closed** on any mismatch, *before* any marker is posted. Returns the confirmed ticket list. Confirmed tickets get an `[implemented_at]` marker (carrying `(batch: stack_position=N)`) and transition `InProgress -> InReview`, so downstream review/ship read the batch stack through the same markers/states as a single-ticket run.

**Partial failure** (worker `Failed` but `Tickets > 0`, [src/ThroughlineBuild.Phases/ChainPhase.cs:1181-1294](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1181-L1294)): the reported subset is verified; confirmed tickets advance (`BatchImplemented`), the first incomplete ticket gets the failure reason posted and is left `InProgress` as a recoverable boundary, and the rest become `StoppedAtImplement`.

**Combined-stack review** (`RunCombinedBatchReviewAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:1404-1473](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1404-L1473)): one review over the full `baseRef..batchBranch` diff via `BatchReviewBriefBuilder`. A second pass runs when pass 1 returned `Rework` OR `batchTickets.Count > BatchReviewSizeThreshold` (a `BuildOptions` default of 5, NOT wired to config - it stays 5 in the production CLI). Each pass posts a `[batch_review: <verdict>]` comment.

**Rework routing** (`ClassifyBatchRework`, [src/ThroughlineBuild.Phases/ChainPhase.cs:1674-1752](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1674-L1752)): if the rationale names exactly one batch ticket -> `Localized` (standard per-ticket `ImplementPhase` rework on the batch branch); zero or 2+ -> `CrossTicket` (re-run the batch worker with the feedback, then re-verify). Bounded by `MaxReworkRounds = 2`.

**Batch ship** (`ShipBatchStackAsync`, [src/ThroughlineBuild.Phases/ChainPhase.cs:2390-2456](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2390-L2456)): switches the integration worktree onto `chain/<parent>` and fast-forwards it onto the reviewed batch tip, then posts `[shipped_at]` and transitions each batch ticket `InReview -> Done` (reason `batch_ship`). The outermost chain's root landing then carries the integration branch to the configured target exactly like a leaf ship.

### Loose ends

- A `BatchImplemented` outcome has no explicit `ChainExitCodeMapper` case (-> default exit 1); in practice batch results are aggregated under the parent's `ParentCompleted`/`ParentStoppedEarly`, so this does not surface as a chain exit code.
- `BatchReviewSizeThreshold` is a `BuildOptions` field with default 5 but is not read from `[batch]` config (only `max_tickets`/`max_size_score`/`max_description_bytes` are wired), so the second-review trigger is effectively a constant.

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
- **The dispatcher name is now a misnomer.** `ParallelDispatcher` runs strictly serially (width 1). Both the multi-ticket path and the parent-chain level loop are serial; the difference is the parent chain runs its own plain serial level loop (recursing into sub-parents) rather than routing through `ParallelDispatcher`.
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

Every `build <verb>` invocation mints session ids via `_sessionIdGenerator` (default `Guid.NewGuid().ToString("N")`). In a chain, each phase (including the gate) gets its own per-phase session id, recorded on `ChainStep.PhaseSessionId`, while the chain lifecycle events (`ChainStart`/`ChainEnd`/`CostLedger`) carry a single `chainSessionId` ([src/ThroughlineBuild.Phases/ChainPhase.cs:142, 316-327](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L142)). The dispatcher mints its own `dispatchSessionId` for `DispatchStart`/`DispatchEnd` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:45](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L45)).

Session ids flow into `WorkflowEvent.SessionId`, the JSONL file naming (via `SessionFileNameBuilder`), and the debug capture directory.

### Loose ends

- Per-phase session ids are now always distinct within a chain (the doc's earlier claim that they were "populated only when phases use distinct session contexts" is stale - the chain always mints a fresh id per phase).

---

## Event kinds emitted

`EventKind` enum has 14 values ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:11-14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11-L14)):

| Kind | Int | Emitted by | Meaning |
|---|---|---|---|
| `StateTransition` | 0 | every phase/command that transitions | from / to state |
| `LlmCall` | 1 | phases that surface worker LLM usage | tokens / model / wall time |
| `WorkerSpawn` | 2 | phases that spawn a worker (and `NewPhase` for audit symmetry; batch review role `batch_verifier`) | worker name + role |
| `VerifierVerdict` | 3 | every worker phase post-run; review post-verifier; decompose verdict gate; batch review pass | status / verdict |
| `GateFailure` | 4 | the workhorse: claim/gate failures, drift warning, hygiene gates, ship pre-flight/diverged/rebase/conflict-marker/regression failures, post-phase dirty-worktree, batch/integration downgrades | `kind` discriminator + reason |
| `TicketWrite` | 5 | every Plane write (description / labels / comments / create / set-parent / rollup / fetch_skipped / base_ref_resolved / decruft / delete_branch) | action + payload summary |
| `ChainStart` | 6 | `ChainPhase` | starting_at_phase, initial_state, chain_session_id |
| `ChainEnd` | 7 | `ChainPhase` | outcome, phases_run, rework_rounds, total_duration_ms |
| `ReworkRound` | 8 | `ChainPhase` (review `Rework` AND gate hard-fail) | round, verdict_that_triggered (`Rework` / `GateFailure`), rationale_preview |
| `TicketSubsumed` | 9 | `ChainPhase` (obsolete ratification Pass) | ticket_id, subsumed_by_commit, files, rationale |
| `TargetAutoRebased` | 10 | `ShipPhase` (DivergedNoConflict auto-rebase; renamed from `MainAutoRebased`) | from_sha, onto_sha, local_commits_replayed, outcome (clean / raced_to_conflict) |
| `DispatchStart` | 11 | `ParallelDispatcher` | ticket_count, level_count, max_concurrency |
| `DispatchEnd` | 12 | `ParallelDispatcher` | outcome (ok / partial / RefusedDirtyTree), total_duration_ms |
| `CostLedger` | 13 | `ChainPhase` (once per gate-engaged ticket, op-30) | gate_wall_ms, gate_attributable_rework_rounds, cascade_caught, false_fails, gate-attributable token fields |

Full event-line schema in [docs/event-log-format.md](../event-log-format.md).

### Loose ends

- `event-log-format.md` does not enumerate the per-`Data` shape of every kind; the authoritative `Data` keys are in the emitting code cited above.
- `DispatchStart`/`DispatchEnd` carry an empty `TicketId` (they are batch-scoped, not ticket-scoped); `max_concurrency` is always 1.
- `CostLedger` has no consumer yet (aspirational); `cascade_caught` / `false_fails` are always emitted as 0 for later annotation, and the gate-attributable token fields are present only when the worker reported usage during a gate-attributable rework round.
- `GateFailure` carries a `kind` discriminator string identifying which gate fired. The current set spans implement (`hygiene_gate`, `drift_warning`, `dirty_worktree_first_attempt`/`dirty_worktree_retry_failed`, `claim_invalid_first_attempt`), gate (`claim_schema_invalid`, `gating_checks_failed`), review (`implemented_at_superseded`, `dirty_worktree_after_review`), ship (`pre_flight_hygiene`, `pre_flight_dirty`, `wrong_worktree_branch`, `parent_children_not_done`, regression/divergence kinds), chain preflight (`chain_preflight_wrong_branch`, `hygiene_gate_preflight`, `chain_preflight_dirty`), parent (`integration_worktree_unavailable`), and batch (`batch_skip_internal_node`, `batch_size_cap_exceeded`, `batch_implement_unavailable`, `batch_review_diff_failed`, `batch_ship_switch_failed`, `batch_ship_merge_failed`).

---

## Chain outcomes and exit codes

`ChainOutcome` enum ([src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:3-21](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3-L21)) is mapped to single-ticket exit codes by `ChainExitCodeMapper.GetExitCode` (extracted from the former inline `Program.cs` switch, [src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs:13-31](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L13-L31)):

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RatifiedObsolete` | 0 | obsolete claim ratified; ticket -> Done |
| `ParentCompleted` | 0 | all eligible children completed |
| `DryRunPreview` | 0 | `--dry-run` schedule printed; no phases executed |
| `RefusedInitialState` | 2 | terminal state (`Done`/`Cancelled`); `Planning`/`InProgress` are now resumed, not refused |
| `RefusedDirtyTree` | 2 | preflight gate: conflict, unrelated stash, or tracked main-worktree changes at chain start, refused before planning |
| `RefusedWrongBranch` | 2 | main worktree not on the ship target branch at chain start, refused before planning |
| `ParentHasGrandchildren` | 2 | legacy "tree deeper than one level"; no longer produced (recursive parent path) |
| `StoppedAtPlan` | 3 | planning failed |
| `ParentStoppedEarly` | 3 | a child did not complete (or sub-chain accumulate / root landing failed) |
| `Skipped` | 3 | skipped because an ancestor failed |
| `StoppedAtImplement` | 4 | implementation failed |
| `StoppedAtReview` | 5 | review returned `Fail` (or review infra failure) |
| `ReworkCapExceeded` | 6 | more than `MaxReworkRounds` rounds (review `Rework` or gate hard-fail) |
| `StoppedAtShip` | 7 | ship gate failed |
| `BatchImplemented` | 1 (default) | no explicit case -> falls through to `default => 1`; aggregated under the parent in practice |

Success set the parent chain uses (`IsChainSuccess`, [src/ThroughlineBuild.Phases/ChainPhase.cs:2844-2848](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2844-L2848)): `Completed`, `RatifiedObsolete`, `ParentCompleted`, `BatchImplemented`. The multi-ticket dispatcher's success set is `Completed`/`RatifiedObsolete`/`ParentCompleted` (`Skipped` is treated as non-failure for the overall exit-0 decision in the sequential path) ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs:127-136](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs#L127-L136)).

### Loose ends

- The multi-ticket dispatcher path returns a flat `0`/`1` from `dispatchResult.Success`, not the per-outcome exit codes above; the granular mapping is only used on the single-ticket path. `ChainExitCodeMapper.GetExitCode(ParallelDispatchResult)` maps a failed dispatch through its `PreservedOutcome` when present, else 1 ([src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs:7-11](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L7-L11)).
- `BatchImplemented` has no explicit mapper case; a single-ticket `BatchImplemented` would map to 1, but batch results are always aggregated under a parent outcome in practice.

---

## Where the chain stops cleanly vs. requires manual triage

- **Clean stop / success:** `Completed`, `RatifiedObsolete`, `ParentCompleted`, `BatchImplemented`, `DryRunPreview`, `Skipped`, `RefusedInitialState`, `ReworkCapExceeded` (operator picks up with `build rework`/`build review`; a gate-capped `ReworkCapExceeded` leaves the ticket `InProgress`).
- **Requires triage:** `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview`, `StoppedAtShip`, `ParentStoppedEarly` (incl. sub-chain accumulate / root landing conflict), `RefusedDirtyTree` (clean the tree - resolve conflicts, drop the stray stash, or commit/stash/revert tracked main-worktree changes), `RefusedWrongBranch` (`git switch <target>`), then re-chain. Each leaves the ticket(s) in whatever state the failing phase left them.

`ChainCommand` surfaces a one-line final summary per outcome on stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs:143-187](../../src/ThroughlineBuild.Commands/ChainCommand.cs#L143-L187)).

---

## Loose ends (cross-cutting)

- **`MaxReworkRounds = 2` is hardcoded; all dispatch is serial.** Both the parent chain (a plain serial level loop, one child at a time, recursing into sub-parents) and the multi-ticket `ParallelDispatcher` (concurrency hard-pinned to 1) run one ticket at a time. `workers.max_concurrency` is read but ignored by the dispatcher; no concurrency knob remains. The same `MaxReworkRounds` bounds review-verdict reworks, gate hard-fail reworks, and batch reworks.
- **No cross-phase live channel.** ReviewPhase reconstructs the implementer brief deterministically. Architecture's "no shared in-memory context with the implementer" principle (Section 5.8) still holds for the worker hand-off, but the chain itself now holds in-process orchestration state for a run.
- **Chain `WorkflowEvent.Data`** carries per-step/per-dispatch fields whose schema lives in code, not exhaustively in [docs/event-log-format.md](../event-log-format.md).
- **No replay verb** (`build replay <session-id>`). Architecture Appendix item 4 notes this as a future.
- **Phase ordering documented in `ChainPhase`** - operators running phases manually out of order hit each phase's state guard.
- **`SequentialChainDispatcher`** remains as a legacy fallback alongside the now-serial `ParallelDispatcher`; the prior two-`TicketGraph`-types seam was resolved by op-29 removing `TicketDependencyGraph` (detailed in "Multi-ticket dispatch").
