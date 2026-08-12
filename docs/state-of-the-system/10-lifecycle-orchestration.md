# 10 - Lifecycle and Orchestration

Last refreshed: 2026-08-12 (HEAD 758ad56a)

The Agile phase state machine implemented by `build` - what each phase does, how the chain orchestrator transitions between them, the gate, the integration-branch tree dispatch, and the rework loop bounded by `MaxReworkRounds`.

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
                                        +-> implement -+             |              |             |
                                                       v             |              |             |
                                                       +-> implement-+              |             |
                                                                     v              |             |
                                                                     +-> gate ------+             |
                                                                     ^   +-> review-+             |
                                                                     |              |             |
                                       Gate fail / Rework verdict ---+              |             |
                                                                                    v             |
                                                                                    +-> ship ---->+
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
| `plan` | `Backlog` | `Planning` -> `Ready` |
| `plan` (promote) | `Backlog` | `Planning` -> `Ready` with no worker/LLM |
| `implement` (initial) | `Ready` | `InProgress` -> `InReview` |
| `implement` (rework) | `InProgress` | `InReview` (no `InProgress` re-entry) |
| `gate` (gating-check fail / invalid claim) | `InReview` | `InProgress` (bounce into rework); no transition on pass, vacuity, or environment failure |
| `review` (Pass / Fail / provider-unavailable) | `InReview` | no change |
| `review` (Rework) | `InReview` | `InProgress` |
| `ship` | `InReview` | `Done` |
| `decompose` | any | no transition; posts `[decomposed_at: <sha>]` + creates N>=2 child sub-issues |
| `close` | non-terminal | `Cancelled` (+ cascade to non-terminal children) |
| `defer` | non-terminal | `Cancelled` (+ cascade to non-terminal children) |
| `reopen` | `Done` / `Cancelled` | `Backlog` or `Ready` (decided by `DetermineTargetState`; children NOT reopened) |
| `new` | n/a | new ticket in Plane default state (`Backlog`) |
| `scaffold` | n/a | 1 operation-ticket + N plan-tickets + M brief-tickets in `Backlog` |

Declaring sites: the plan transitions live in `PlanPhase.RunAsync` and `PlanPhase.RunPromoteAsync` ([src/ThroughlineBuild.Phases/PlanPhase.cs:124](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L124), [:224](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L224)); implement in `ImplementPhase.RunAsync` ([src/ThroughlineBuild.Phases/ImplementPhase.cs](../../src/ThroughlineBuild.Phases/ImplementPhase.cs)); the gate bounce inside `GatePhase.RunAsync` ([src/ThroughlineBuild.Phases/GatePhase.cs:74](../../src/ThroughlineBuild.Phases/GatePhase.cs#L74)); review in `ReviewPhase.RunAsync` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:70](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L70)); ship, close, defer, reopen, and scaffold in their respective `ShipPhase` / `CloseCommand` / `DeferCommand` / `ReopenCommand` / `ScaffoldPhase` classes.

`TicketState` enum: `Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled` ([src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs)).

Plane mirror state names: `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled` (hardcoded reverse map in [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs)).

### Loose ends

- The diagram does not show the `decompose` fan-out (one ticket -> N children) or the tree-aware parent paths; those are below in "Tree-aware chain".
- The gate runs only inside the chain loop; standalone `build implement` + `build review` skip it, so the manual path and the chain path enforce different bars.
- The retired standalone architecture document no longer defines lifecycle transitions; the code and this section are authoritative.

---

## Phase implementations

Every `*Phase` class in [src/ThroughlineBuild.Phases/](../../src/ThroughlineBuild.Phases/) (and `ScaffoldPhase` in `ThroughlineBuild.Scaffold`):
`PlanPhase`, `ImplementPhase`, `GatePhase`, `ReviewPhase`, `ShipPhase`, `DecomposePhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `ScaffoldPhase`. The `Phase` enum has 11 values - `Plan, Implement, Review, Ship, Chain, New, Command, Draft, Scaffold, Decompose, Gate` ([src/ThroughlineBuild.Contracts/Models/Phase.cs:3](../../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3)).

### `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs](../../src/ThroughlineBuild.Phases/PlanPhase.cs))

Status: **Functional**.

Step sequence (all in `PlanPhase.RunAsync`):
1. Fetch ticket.
2. Parent guard: refuse if the ticket has children - parent containers do not get plans (the refusal message is built at [PlanPhase.cs:84](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L84)).
3. State guard: `Backlog`.
4. Resolve `main` SHA via `BaseRefResolver`.
4b. **Promote branch (the default within chain):** if `BuildOptions.PromotePlan` is set - as it is for `build chain` when `[plan].mode` uses its `"promote"` default, or for either verb with `--from-brief` - dispatch to `RunPromoteAsync` ([PlanPhase.cs:100](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L100)) and return - see "Promote path" below. Standalone `build plan` otherwise runs the worker-driven planning sequence regardless of `[plan].mode`:
5. Build `RepoState` (top-level entries + main SHA).
6. Build brief via `PlanBriefBuilder.Build` - the obsolete-detection instructions are a shared template block, `plan-obsolete-initial.md` under [src/ThroughlineBuild.Briefs/Templates/shared/](../../src/ThroughlineBuild.Briefs/Templates/shared/), extracted from the four per-agent plan templates and sanitized to stack-agnostic angle-bracket placeholders.
7. Emit `WorkerSpawn` event.
8. Run worker.
9. Transition `Backlog -> Planning` *before* checking the worker status ([PlanPhase.cs:124](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L124)). A worker failure thus leaves the ticket parked in `Planning`, not `Backlog` (the chain resume machine resets this - see `ResolveEntryAsync` below).
10. Emit `VerifierVerdict` (status of the worker run).
11. On worker `Status != Ok`: return; if the status is `Escalate`, the `WorkerResult` is carried back as `EscalationWorkerResult` for obsolete-claim ratification.
12. Optionally emit `LlmCall` from worker metadata.
13. Resolve the plan body: `FencedBlockResolver.TryResolveRef(blocks, metadata, "plan_body_ref")` -> `PLAN_BODY` block; render to HTML via `MarkdownRenderer.Render`. Validate the scalar keys (`risk_label`, `size_label`, `planned_at_sha`).
14. Append the rendered plan HTML to description.
15. Apply merged risk + size labels.
16. Post `[planned_at: <sha>]` comment.
17. Transition `Planning -> Ready`.

**Promote path (TLB-374) - the default inside chain.** `RunPromoteAsync` ([PlanPhase.cs:224](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L224)) promotes an already-authored brief straight to `Ready` without spawning a worker or calling an LLM: transition `Backlog -> Planning`, apply merged risk/size labels from the ticket's existing fields, post `[planned_at: <mainSha>]`, transition `Planning -> Ready`. For `build chain`, `BuildOptions.PromotePlan` comes from `[plan].mode`, whose default is `"promote"` (`PlanConfig.Default`, [src/ThroughlineBuild.Cli/Config.cs:52-56](../../src/ThroughlineBuild.Cli/Config.cs#L52-L56)). For standalone `build plan`, only `--from-brief` enables promotion; otherwise it investigates regardless of config.

### `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs](../../src/ThroughlineBuild.Phases/ImplementPhase.cs))

Status: **Functional**.

Step sequence (all in `ImplementPhase.RunAsync`):
1. Fetch ticket. Detect rework round vs. initial by `ImplementPhaseOptions.ReviewFeedback` presence.
2. Parent guard: refuse to implement a ticket that has children; writes `phase-status.json` via `EarlyExitManifest`.
3. State guard: `Ready` (initial) or `InProgress` (rework). On guard failure, write `phase-status.json`.
3b. Hygiene gate: refuse on a conflicted or stash-polluted tree, `GateFailure` kind `hygiene_gate`.
4. Resolve base ref + main SHA via `BaseRefResolver` (advances to the local target tip when it is ahead of origin, TLB-411).
5. Compute deterministic worktree names via `PhaseWorktreeLayout`.
6. Drift check: scan comments for the freshest `[planned_at: <sha>]` marker by creation time (`CommentMarkers.LatestValue`, TLB-412); emit `GateFailure` drift warning if it differs from current main SHA (does not block).
7. Resolve the canonical worktree/branch. Three cases keyed on `SharedWorktreePath` and `isRework`: shared-worktree (build inside the supplied worktree), standalone (fresh per-ticket worktree), or rework (locate the existing worktree by `git worktree list`, falling back to disk, with a last-resort recovery checkout).
8. Hold the chain commit range: `HandoffPointerEnabled` (compile const, [ImplementPhase.cs:44](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L44), default true) gates whether `_phaseOptions.ChainCommitRange` reaches the brief - see "Chain commit-range handoff".
9. Set up the working directory: `CreateBranchAsync` inside a shared worktree, `CreateWorktreeAsync` for standalone, reuse for rework. Then transition `Ready -> InProgress` (initial round only).
9b. **Preload (experiment-3 lineage):** after the worktree is materialized, `BuildAndReportPreloadAsync` ([ImplementPhase.cs:644](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L644)) calls `PreloadedContextBuilder.Build` ([src/ThroughlineBuild.Briefs/PreloadedContextBuilder.cs](../../src/ThroughlineBuild.Briefs/PreloadedContextBuilder.cs)) to inline file contents into the brief: paths declared in the ticket's `<h3>Preload</h3>` block plus the `[project].convention_files` bundle, read from the live worktree via a confined reader, head+tail-truncated per-file (16KB) and bounded in total (64KB). Telemetry is advisory and never blocks: a `CostLedger` event with kind `preload_summary` (files requested/loaded/truncated, bytes, not-found lists), plus `GateFailure` kinds `preload_file_not_found` (per declared path absent) and `preload_empty` (declared paths exist but none loaded). Gated off entirely by `[project].preload_context = false`. The section lands in the brief via the `preloaded_context_section` template placeholder filled by `ImplementBriefBuilder.Build`; an empty section leaves the brief byte-identical to the pre-preload baseline. The brief build itself is deferred to this step so the preload reader sees the materialized worktree.
10. Emit `WorkerSpawn`. Run worker inside the worktree. The `WorkerOptions.LeanPlanning` flag is set when `[project].context_hygiene` is enabled and the ticket size is S (the effort-gated context-hygiene experiment; the claude-code agent maps it to `--disallowedTools TodoWrite,Task` and `ImplementBriefBuilder.BuildContextHygieneSection` adds a planning-hygiene constraint line).
11. Emit `VerifierVerdict` (worker status). On non-Ok, return early; `Escalate` is carried as `EscalationWorkerResult`.
12. Emit `LlmCall` if usage metadata is present; capture input/output token counts onto the phase result.
13b. **Context attribution:** if the worker attached per-turn usage (`Metadata["context_turns"]`, produced by `ClaudeCodeTurnParser`), re-emit it as an advisory `CostLedger` event with kind `context_attribution` ([ImplementPhase.cs:388](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L388)): turn count, cache-read series, slope ratio, and per-tool-class byte splits.
14. Envelope recovery: salvage a committed session whose `WORKER_RESULT` is missing or garbled (two recoverable cases; a re-ask sub-step).
15. Post-worker dirty-tree check with one bounded retry: if the worker left uncommitted files, re-run with a "commit everything" note; still dirty -> `GateFailure` and fail.
15b. Validate `commit_sha` metadata against actual `git rev-parse HEAD` in the worktree; actual wins on discrepancy.
15c. **Completion claim (TLB-500/505):** if the worker opted in via a `completion_claim_ref` metadata key ([ImplementPhase.cs:487](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L487)), resolve the `COMPLETION_CLAIM` fenced block and parse it with `CompletionClaimParser`; on an invalid claim, one bounded re-ask asks the worker to emit only the claim block for its already-committed work. The parsed `CompletionClaim` rides the phase result into the gate.
16. Post `[implemented_at: <actualSha>]` comment naming the branch; render `IMPLEMENT_SUMMARY` if supplied.
17. Transition `InProgress -> InReview`.

### `GatePhase` ([src/ThroughlineBuild.Phases/GatePhase.cs](../../src/ThroughlineBuild.Phases/GatePhase.cs))

Status: **Functional**. `GatePhase` is the chain-composed deterministic check gate that runs on the warm worktree after each successful implement round, before the verifier. The separate standalone `build gate` surface is implemented by `GateCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/GateCommand.cs:10](../../src/ThroughlineBuild.Cli/GateCommand.cs#L10)); it runs configured checks and persisted canary policy without the chain's completion-claim, smoke-signal, rework-loop, or lifecycle-transition context.

`GatePhase.RunAsync` ([GatePhase.cs:74](../../src/ThroughlineBuild.Phases/GatePhase.cs#L74)) sequence:
1. **Claim schema validation:** if the implement round produced a `CompletionClaim` ([src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs:18](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs#L18) - `Provides`, `Consumes`, `AcBindings`, `TestsAdded`, plus deferred unenforced hook fields), validate all four arrays are present; an invalid claim is a hard-fail (`GateFailure` kind `claim_schema_invalid`) that bounces `InReview -> InProgress` into rework.
2. **Run configured checks** via `AutomatedChecksRunner.RunAsync` over `GateOptions.Checks` ([GatePhase.cs:9](../../src/ThroughlineBuild.Phases/GatePhase.cs#L9)) - the `[[review.checks]]` specs from `.build/config.toml`, each carrying a `CheckRole` (`Gating`, `Advisory`, `Setup`; [src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:8](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L8)) and an optional `CanaryFile` list.
3. **Smoke signals (TLB-503):** `SmokeCollector` ([src/ThroughlineBuild.Verification/SmokeCollector.cs](../../src/ThroughlineBuild.Verification/SmokeCollector.cs)) collects advisory diff facts (files touched, test files present, grep present/absent probes); never blocks. The **consumes-provides preflight (TLB-507)** runs here: the claim's `Consumes` set is intersected with the chain's `AccumulatedUpstreamProvides` and reported as an advisory smoke signal.
4. **Setup failures:** a failed `Setup`-role check (a prerequisite command such as code generation) hard-fails before the gating cascade (`GateFailure` kind `setup_failed`).
5. **Failure attribution (TLB-538):** when gating checks fail and a `GateControlProber` ([src/ThroughlineBuild.Verification/GateControlProber.cs](../../src/ThroughlineBuild.Verification/GateControlProber.cs)) is wired (production wiring lives in `CliApplication`), spawn a throwaway worktree on the base ref and re-run the failed checks there. Base also fails -> the failure is environmental: reload fresh config from disk and retry once; if still failing, return `EnvironmentFailure` (hard-fail WITHOUT rework, no state bounce; `GateFailure` kinds `gate_control_run`, `gate_config_reloaded`, `gate_environment_failure`). Base passes -> the failure is the ticket's code.
6. **Gating hard-fail:** ordinary gating failures emit `GateFailure` kind `gating_checks_failed`, transition `InReview -> InProgress`, and return a failed `GateOutcome` whose `HardFailReason` becomes the rework feedback (TLB-509 structured-failure-to-rework).
7. **Vacuity probe:** on green, `GateVacuityProver` ([src/ThroughlineBuild.Verification/GateVacuityProver.cs:31](../../src/ThroughlineBuild.Verification/GateVacuityProver.cs#L31), wired when `[review].verify_gate_vacuity` is set) proves each first-green gating check can actually fail: materialize its declared canary file, re-run only that check, and assert it now fails. A check that passes with the broken canary present is `Vacuous` - a config defect that hard-fails the chain without rework (`GateFailure` kinds `gate_vacuous`, `gate_canary_cleanup_failed`; checks with no canary get advisory `gate_unverified`). Proven checks are remembered per run.

The gate's `CheckResults` are forwarded to `ReviewPhase` via `PreComputedChecksRunner` ([src/ThroughlineBuild.Verification/PreComputedChecksRunner.cs](../../src/ThroughlineBuild.Verification/PreComputedChecksRunner.cs)) so checks execute exactly once per ticket.

### `ReviewPhase` ([src/ThroughlineBuild.Phases/ReviewPhase.cs](../../src/ThroughlineBuild.Phases/ReviewPhase.cs))

Status: **Functional**.

Step sequence (all in `ReviewPhase.RunAsync`, [ReviewPhase.cs:70](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L70)):
1. Fetch ticket. Parent-ticket aggregate-review branch if it has children (see "Tree-aware chain").
2. State guard `InReview`.
3. Locate the worktree by branch or path via `git worktree list`; if missing, reconstruct it from the ticket branch (TLB-407).
4. Resolve base ref + main SHA. Inside a parent chain the diff base is the chain's integration branch, not the configured root (a fix from the batch-implement wiring pass).
5. Reconstruct an implementer brief deterministically via `ImplementBriefBuilder.Build` without `ReviewFeedback` (no fresh worker invocation).
6. Determine the commit under review (TLB-412/414): freshest `[implemented_at: <sha>]` marker, with worktree branch HEAD as ground truth; on mismatch emit `GateFailure` kind `implemented_at_superseded` and review HEAD.
7. Compute the diff with patches; synthesize a `WorkerResult` for the verifier.
8. Run automated checks - when the chain ran the gate, this is a `PreComputedChecksRunner` returning the gate's results without re-execution; standalone reviews run the checks live ([ReviewPhase.cs:206](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L206)).
9. Construct the verifier - default `WorkerAgentReviewer`, spawning the verifier worker against the review brief inside the feature worktree ([ReviewPhase.cs:215](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L215)).
10. Emit `WorkerSpawn` (role=verifier). Run verifier.
10b. **Provider-unavailable classification (TLB-527):** if `WorkerAgentReviewer.LastProviderError` is set (the verifier worker was blocked by a quota/rate-limit/auth error, classified by `ProviderErrorClassifier`), return a `ReviewResult` carrying `ProviderUnavailable` ([ReviewPhase.cs:243](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L243)) - NOT a `Fail` verdict; no comment is posted and the ticket stays cleanly `InReview`, resumable via `build review`.
10c. Post-verifier dirty-tree check: hard-fail, no retry - `GateFailure` kind `dirty_worktree_after_review` ([ReviewPhase.cs:266](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L266), TLB-400).
11. Emit `VerifierVerdict` (kind, rationale, checks_failed, plus `checks_failed_details` - the serialized `CheckResult` evidence persisted so a resumed rework brief carries the failing check's verbatim output, the oracle).
12. Optionally emit `LlmCall` from the verifier's usage.
13. Apply verdict: `Pass` -> `reviewed: pass` comment; `Rework` -> `reviewed: rework` comment + `InReview -> InProgress`; `Fail` -> `reviewed: fail` comment, no transition. Advisory-role check failures never produce a `Rework` verdict on their own - role semantics are a cross-phase contract.

### `ShipPhase` ([src/ThroughlineBuild.Phases/ShipPhase.cs](../../src/ThroughlineBuild.Phases/ShipPhase.cs))

Status: **Functional**.

Deterministic - no LLM, no worker. The merge target is `[work].target_branch` if set, else `[ship].base_branch` (resolved by `BuildConfig.ResolveTargetBranch()` and carried on `ShipOptions.TargetBranch`); inside a parent chain the target is the chain integration branch (see "Tree-aware chain"). Step sequence:
1. Fetch ticket. Parent-ticket ship branch if it has children.
2. State guard `InReview`.
3. Locate worktree (by `ticket/<id>` prefix; falls back to creating one from a matching local branch).
4. Pre-flight: `build` binary not running from inside the worktree (`GateFailure` kind `pre_flight_exe_in_worktree`).
5. Pre-flight hygiene + dirty check: both feature and main worktrees clean of tracked changes; `GateFailure` kinds `pre_flight_hygiene` ([ShipPhase.cs:218](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L218)) / `pre_flight_dirty` ([:242](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L242)).
6. Pre-flight (unconditional, catches detached HEAD): the worktree being merged into is on the target branch, else `wrong_worktree_branch` `GateFailure` ([ShipPhase.cs:272](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L272), TLB-402/410).
7. Conditionally fetch from remote. If no remote, or `--no-push`: skip, rebase onto the local target branch.
8. Determine rebase target via divergence handling (see "Divergence and merge orchestration"), under `MainWorktreeLock`.
9. Emit `base_ref_resolved` (a `TicketWrite` event).
10. Rebase feature branch onto resolved target ref. On conflict: `git rebase --abort`, fail at `Rebase`.
11. Conflict-marker scan of the rebased diff's files.
12. Run `ship.regression_checks` (baseline-aware, TLB-401). Two newer refinements: advisory-role checks never block the regression gate, and when a check regresses, the wired `GateControlProber` (`_baselineProber`, [ShipPhase.cs:58](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L58)) re-probes the baseline in a fresh worktree so a stale cached baseline entry cannot misclassify a pre-existing failure as a regression (or vice versa) - the contradictory baseline is corrected ([ShipPhase.cs:552-564](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L552-L564)).
13. Fast-forward merge into the target branch, under `MainWorktreeLock`, with post-merge HEAD re-assertion.
14. Push when a remote exists and `--no-push` is not set; failure fails the ship at `Push`.
15. Read merged HEAD SHA; post `[shipped_at: <mergedSha>]` comment; transition `InReview -> Done`.
16. `WorktreeDecrufter.DecruftAsync` - skipped when `SkipDecruft` is set ([ShipPhase.cs:16](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L16), set by the chain ship factory so chain worktrees survive until the end-of-chain sweep).
17. Optionally `git branch -d ticket/<slug>` (failure non-fatal).

### `DecomposePhase` ([src/ThroughlineBuild.Phases/DecomposePhase.cs](../../src/ThroughlineBuild.Phases/DecomposePhase.cs))

Status: **Functional**. Fans one ticket out into independently shippable child sub-issues; no state transition on the parent. Sequence: fetch + resolve main SHA, build brief via `DecomposeBriefBuilder`, run worker, extract `child_specs` (require >= 2), run the rule-based `DecomposeVerdict.Check` quality gate ([src/ThroughlineBuild.Phases/DecomposeVerdict.cs](../../src/ThroughlineBuild.Phases/DecomposeVerdict.cs) - coverage, uniqueness, size checks; deterministic, not LLM-driven), create child sub-issues via `CreateChildTicketsAsync`, post `[decomposed_at: <mainSha>]`.

### `ChainPhase` ([src/ThroughlineBuild.Phases/ChainPhase.cs](../../src/ThroughlineBuild.Phases/ChainPhase.cs))

Status: **Functional**. The leaf/root coordinator. `ChainPhaseComposition` ([src/ThroughlineBuild.Cli/ChainPhaseComposition.cs](../../src/ThroughlineBuild.Cli/ChainPhaseComposition.cs)) constructs it from three required dependency records: core collaborators, phase factories, and execution capabilities/settings. The phase delegates parent traversal to `ParentChainRunner`, implement/gate/review/rework to `ImplementReviewLoop`, interrupted-state recovery to `ChainResumeResolver`, and integration-branch operations to `ChainIntegrationBranch`. The composition seam also selects the human-output writer and wires diagnostics so required dependencies cannot be silently dropped.

`ChainPhase.RunAsync` ([ChainPhase.cs:176](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L176)) wraps the core in a `TicketingUnavailableException` handler (TLB-545): a ticketing backend unreachable at the transport level after client-side retries is classified as `ChainOutcome.TicketingUnavailable` at the per-ticket boundary - environmental, resumable, never a crash. `RunChainCoreAsync` ([ChainPhase.cs:209](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L209)) then runs:

1. Fetch the ticket.
2. **Outermost-only preflight** (skipped on recursion, which sets `ChainTargetBranch`):
   - **Wrong-branch guard:** the main worktree must be on the configured target branch (mirrors the ship preflight, but before any planning) - else `GateFailure` kind `chain_preflight_wrong_branch` and `RefusedWrongBranch` (`RunOutermostPreflightAsync`, [ChainPhase.cs:518](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L518)).
   - **Hygiene gate:** dangling stash or conflict -> `GateFailure` kind `hygiene_gate_preflight`, `RefusedDirtyTree`.
   - **Tracked-dirty gate:** modified tracked files (sampled to 25 paths) -> `GateFailure` kind `chain_preflight_dirty`, `RefusedDirtyTree`.
3. **Cycle guard:** a ticket already in `VisitedTicketUuids` stops the recursion.
4. **Dry-run:** `--dry-run` uses `ChainDryRunPlanner` ([src/ThroughlineBuild.Phases/ChainDryRunPlanner.cs](../../src/ThroughlineBuild.Phases/ChainDryRunPlanner.cs)) to build and print the full post-order schedule and branch topology, returning `DryRunPreview` with per-ticket preview results; no phases execute.
5. **Parent path:** if the ticket has children, delegate to `ParentChainRunner.RunAsync` ([ParentChainRunner.cs:64](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L64)), but only below `ChainPhaseOptions.MaxDepth` (default 16); reaching the cap returns `ParentStoppedEarly`.
6. Otherwise call `ChainResumeResolver.ResolveAsync` ([src/ThroughlineBuild.Phases/ChainResumeResolver.cs](../../src/ThroughlineBuild.Phases/ChainResumeResolver.cs)) for the **resume state machine**:
   - `Backlog` -> start at Plan; `Ready` -> Implement; `InReview` -> Review.
   - `Planning` -> a plan that never finished: reset to `Backlog`, emit a `chain_resume` `StateTransition`, start at Plan.
   - `InProgress` -> `ResolveInProgressAsync` in `ChainResumeResolver`: count commits on `ticket/<id>` beyond base. 0 commits -> `PruneOrphanBranchAsync` removes the orphaned branch/worktree, reset to `Ready`, clean Implement. Has commits -> `ResumeImplement` at round 1 reusing the worktree, recovering the last `Rework` feedback (with its persisted `FailedCheckDetails` evidence) from the event log via `IReviewFeedbackRetriever.GetLatestRework`, or synthesizing a neutral resume note.
   - `Done` / `Cancelled` -> `RefusedInitialState`.
7. Emit `ChainStart`. If starting at Plan: run `PlanPhase`; on an `obsolete` escalation (and `!NoAutoResolve`) run ratification (see "Obsolete-claim handling"); otherwise failures return `StoppedAtPlan`.
8. Enter `ImplementReviewLoop.RunImplementReviewLoopAsync` ([src/ThroughlineBuild.Phases/ImplementReviewLoop.cs](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs)). A chain starting at Review uses `RunReviewBranchAsync` first (no gate on the resume-into-review path; `ReviewPhase` runs the checks itself) and hands a `Rework` verdict into the loop.
9. Run `ShipPhase`. Inside a parent chain (`ChainTargetBranch` set) the ship uses `_chainShipFactory` and runs in the parent's integration worktree, so the leaf ships into the integration branch, not the configured root ([ChainPhase.cs:487-506](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L487-L506)). Fail -> `StoppedAtShip`.
10. On success at the outermost level, sweep this chain's worktrees through `ChainIntegrationBranch.SweepChainWorktreesAsync`; failures preserve worktrees for inspection. Emit `ChainEnd`, return `Completed` carrying `ShippedProvides` (the completion claim's `Provides`, accumulated for downstream siblings).

**Per-phase START notice (TLB-415).** `ChainEventEmitter.EmitPhaseStart` ([ChainEventEmitter.cs:45](../../src/ThroughlineBuild.Phases/ChainEventEmitter.cs#L45)) pushes a `ChainStep` with `IsStart: true` through `OnStep` before each phase (plan, implement, gate, review, ship, ratify, batch-implement). It is progress-only: never added to `steps` or `phases_run`. `ChainCommand` renders that callback through its injected human-output writer; `--summary-json` supplies `TextWriter.Null`, so human progress cannot contaminate structured stdout.

**Chain commit-range handoff (op-29 briefs 08-11).** `ParentChainRunner` derives `ChainCommitRange` via `ChainCommitRangeHelper.ComputeAsync` ([ParentChainRunner.cs:439](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L439)), describing commits already produced by shipped siblings. `ImplementReviewLoop` forwards it on the first implement round only ([ImplementReviewLoop.cs:115](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L115)); rework rounds suppress it. `ImplementPhase` applies the `HandoffPointerEnabled` guard ([ImplementPhase.cs:44](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L44)), and the builder folds touched files into `RelevantFiles` plus one bounded `chain_pointer` context line only when the range is non-empty.

The chain invokes `OnStep` after each phase; `ChainCommand` streams one-line, per-ticket-prefixed summaries to its injected writer ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)). Each phase gets its own session id from the injected generator; `ChainStart`/`ChainEnd` carry a single chain session id.

### `ReworkPhase` ([src/ThroughlineBuild.Phases/ReworkPhase.cs](../../src/ThroughlineBuild.Phases/ReworkPhase.cs))

Status: **Functional**. Thin wrapper: state guard `InProgress`; resolve feedback (manual `--feedback` wins, else latest `Rework` verdict via `IReviewFeedbackRetriever` - which now also recovers the persisted failed-check evidence); build `ImplementPhaseOptions`; invoke `ImplementPhase` as a rework round.

### `NewPhase` / `DraftPhase` ([src/ThroughlineBuild.Phases/NewPhase.cs](../../src/ThroughlineBuild.Phases/NewPhase.cs), [DraftPhase.cs](../../src/ThroughlineBuild.Phases/DraftPhase.cs))

Status: **Functional**. Unchanged in shape: `NewPhase` is the deterministic creator (audit-symmetry `WorkerSpawn` with role=creator, `TicketWrite` with `create_ticket`); `DraftPhase` runs a draft worker and resolves `body_markdown_ref` -> `DRAFT_BODY` fenced block.

### `ScaffoldPhase` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs))

Status: **Functional**. Multi-write batch only; profile derivation was removed from this phase and now uses the separate worker-free `profile prompt` / external-agent / `profile apply` protocol.

1. Parse op-doc via `OpDocParser` (which now also parses a positive-only `Preload` brief label, rendered as an `<h3>Preload</h3>` block in ticket HTML by `BriefHtmlRenderer` - the source of the implement-phase preload paths).
2. Validate via `OpDocValidator`.
3. Warning gate: abort if warnings and not `--accept-warnings`.
4. Dry-run gate (counts only, no API calls).
5. Plane connectivity test.
6. Create a single operation ticket, then per plan: plan-ticket + `SetParentAsync`, then per brief: brief-ticket + `SetParentAsync`. Failures collected in `ScaffoldFailure[]`; processing continues.

**Profile derivation is now an outer orchestration protocol.** `build scaffold` stops after validating/creating the ticket hierarchy. `build profile prompt` emits the repository-inspection prompt; an external agent returns `PROJECT_PROFILE` JSON; `build profile apply` parses it, creates a temporary proof worktree, runs the install/setup/gating commands, proves every gating canary is rejected, then writes the profile-managed config blocks ([ProfileCommand.cs:155](../../src/ThroughlineBuild.Cli/ProfileCommand.cs#L155), [ProfileGateVerifier.cs:27](../../src/ThroughlineBuild.Cli/ProfileGateVerifier.cs#L27)). No model worker is spawned inside `build`, avoiding nested-agent sessions and making the artifact inspectable before apply.

### Loose ends

- `ScaffoldPhase` is invoked from the CLI, not exposed as an `IWorkflowPhase`.
- `GatePhase` has no standalone verb; operators cannot run the gate (or the vacuity prover) outside a chain.
- `DecomposePhase` writes children but does not transition or label the parent beyond the `[decomposed_at]` marker.
- The architecture doc still describes a 9-value `Phase` enum and `ClaudeCodeReviewer` as the default verifier; both are stale - the enum has 11 values and the verifier is `WorkerAgentReviewer`.

---

## The chain rework loop

`ImplementReviewLoop.RunImplementReviewLoopAsync` ([src/ThroughlineBuild.Phases/ImplementReviewLoop.cs](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs)):

```
        +-------------------+
        | ImplementPhase    |<--------------------------------------+
        +---------+---------+                                       |
                  | InProgress -> InReview                          |
                  v                                                 |
        [check-recheck: if this round was rework citing named       |
         checks, re-run exactly those checks (no LLM); still        |
         failing -> raw output loops straight back, <= 2            |
         retries per round, no rework round consumed]               |
                  |                                                 |
                  v                                                 |
        +---------+---------+  gating fail, round < 2  (feedback)   |
        | GatePhase         +----------------------------------------+
        | (when wired)      +--> vacuous     -> GateVacuous (stop)
        +---------+---------+--> environment -> GateEnvironmentFailure (stop)
                  | pass (checks forwarded to review)
                  v
        +---------+---------+
        | ReviewPhase       |
        +---------+---------+
            |       |        \
          Pass    Rework      Fail / provider error
            |       |               \
            v       v                v
        ShipPhase  round++,      StoppedAtReview /
            |      loop if       ReviewUnavailable
            |      round < 2
            |      else ReworkCapExceeded
```

`MaxReworkRounds = 2` and `MaxCheckRetriesPerReworkRound = 2` are declared together in `ImplementReviewLoop` ([ImplementReviewLoop.cs:17-18](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L17)). The former means at most 2 rework rounds, i.e. up to 3 implement runs total; both the gate bounce and the review `Rework` verdict consume rounds from the same budget. The latter bounds the separate deterministic check-recheck loop, which does NOT consume rework rounds or verifier calls: a check is an oracle, and a subprocess re-run proves in seconds what a verifier LLM call rediscovers in minutes. Advisory-role checks never trigger the recheck short-circuit and never drive rework.

Each `Rework` verdict emits a `ReworkRound` event (`round`, `verdict_that_triggered` = `Rework` or `GateFailure`, `rationale_preview`). The feedback record (`ReviewFeedback`, [src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs](../../src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs)) now carries `FailedCheckDetails` / `GateFailedChecks` - the verbatim `CheckResult` evidence (exit code, output tails) - persisted in the `VerifierVerdict` event's `checks_failed_details` and recovered on resume, so a resumed rework brief carries the oracle. Under `--debug`, each rework round also writes a `ReworkRoundManifest` side-channel record ([src/ThroughlineBuild.Phases/ReworkRoundManifest.cs](../../src/ThroughlineBuild.Phases/ReworkRoundManifest.cs)): round, trigger (gate vs review), rationale, failing checks, and commit SHAs before/after - the inputs for splitting design misses from hygiene slips.

**Gate cost ledger.** `ImplementReviewLoop` accumulates gate wall time and gate-attributable rework token counts, then calls `ChainEventEmitter.EmitCostLedgerAsync` ([ChainEventEmitter.cs:102](../../src/ThroughlineBuild.Phases/ChainEventEmitter.cs#L102)) at every loop exit: `gate_wall_ms`, `gate_attributable_rework_rounds`, token splits when tracked, and `false_fails` (gate hard-fails proven environmental by the control run). This is the TLB-510 measurement substrate for whether the gate pays for itself.

### Loose ends

- `MaxReworkRounds` and `MaxCheckRetriesPerReworkRound` are hardcoded; not configurable per ticket or repo.
- A review-phase infra failure (worker crash) returns `StoppedAtReview` with the failure reason, distinct from a `Fail` verdict; a classified provider quota/rate-limit error returns `ReviewUnavailable` instead (TLB-527).
- The check-recheck loop requires the chain wiring (`_reworkRecheckSpecs` + `_reworkRecheckRunner`); when absent, rework rounds flow exactly as before.

---

## Obsolete-claim handling (ratification)

Status: **Functional** (TLB-282/283/285).

A worker (plan or implement) may return `Status.Escalate` with an `escalation.reason == "obsolete"` claim plus a `subsumed_by` block. When the chain sees this and `--no-auto-resolve` was NOT supplied, `ImplementReviewLoop.IsObsoleteEscalation` confirms the shape and `RunRatificationAsync` ([ImplementReviewLoop.cs:784](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L784)) invokes the `ObsoleteRatifier`, recording a `ratify` `ChainStep`. `ObsoleteRatifier.RatifyAsync` ([src/ThroughlineBuild.Verification/ObsoleteRatifier.cs](../../src/ThroughlineBuild.Verification/ObsoleteRatifier.cs)) performs three checks: cited commit exists, cited files exist, and a model-driven acceptance-criteria check. On `Pass`: ticket -> `Done`, "Subsumed by ..." comment, `TicketSubsumed` event, outcome `RatifiedObsolete` (success). On reject: fall through to `StoppedAtPlan` / `StoppedAtImplement`. Implement-side ratification passes the ticket worktree as the evidence directory, where the cited commit actually lives.

### Loose ends

- Ratification only triggers from the chain, not from standalone `build plan`/`build implement`.
- `RatifiedObsolete` is treated as success by the dispatchers and the aggregate report.

---

## Tree-aware chain (parent tickets)

Status: **Functional**. The model changed substantially since the last refresh: the old "one shared worktree, each child branches inside it, each child ships to main" layout was replaced by an **integration-branch accumulation model** (TLB-492/494/546), and the one-level-deep restriction was replaced by bounded recursion.

When a chained ticket has children, `ChainPhase` delegates to `ParentChainRunner.RunAsync` ([ParentChainRunner.cs:64](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L64)):

1. **Eligibility + order:** filter to non-terminal children, never the parent itself; order by ascending ticket number (`TicketNumber` also parses bare numeric ids when no project identifier is configured - TLB-511 fixed the sort collapsing to lexicographic there).
2. **Sibling dependency ordering:** `ParentChainRunner.BuildSiblingGraphAsync` over `blocked_by` relations, `TopologicalSorter.ComputeLevels`, then `ChainDryRunPlanner.PrintDispatchOrder` through the injected output writer before any phase runs ([ParentChainRunner.cs:86](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L86)).
3. **Integration branch + worktree:** `ChainIntegrationBranch.EnsureIntegrationWorktreeAsync` ([ChainIntegrationBranch.cs:246](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L246)) cuts one worktree checked out on `chain/<slug>` at the integration base - the configured target for the outermost chain, or the parent's integration branch for a nested parent. If creation fails, `ParentChainRunner` returns `ParentStoppedEarly` and emits `GateFailure` kind `integration_worktree_unavailable`; there is no silent fallback to per-ticket shipping.
4. **Integration-branch refresh (TLB-546):** a retained `chain/<slug>` branch from a prior run stays frozen at the base tip it forked from. `ChainIntegrationBranch.RefreshIntegrationBranchAsync` ([ChainIntegrationBranch.cs:282](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L282)) reconciles it with the CURRENT base before any child dispatches: base advanced -> rebase the chain branch onto the current tip; rebase conflict -> abort and stop with the work safe on the branch.
5. **Optional batch-implement path** (see below) for an eligible group of leaf children.
6. **Serial child dispatch:** recurse through the injected child runner per child, level by level, one at a time. Each child runs with `ChainTargetBranch` = the integration branch, `ChainIntegrationWorktreePath` = the integration worktree, `SharedWorktreePath` = null (children implement in standalone `ticket/<id>` worktrees; the integration worktree is reserved for ships and batch sessions), `Depth+1`, the parent's UUID in `VisitedTicketUuids`, and recomputed `ChainCommitRange` + `AccumulatedUpstreamProvides` ([ParentChainRunner.cs:432](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L432)). A leaf ship fast-forwards the integration branch inside that worktree. A completed nested parent is accumulated by `ChainIntegrationBranch.RebaseThenFastForwardAsync` with action `chain_accumulate` ([ParentChainRunner.cs:501](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L501)).
7. **Environmental blast-radius marking (TLB-538/545):** if a child stops with `GateEnvironmentFailure` or `TicketingUnavailable`, remaining undispatched children are synthesized as `Skipped` with the reason instead of silently dropped.
8. **Provides accumulation:** each successful child's `ShippedProvides` is unioned into the set used by the consumes-provides preflight for later siblings.
9. **Root landing (TLB-492):** the OUTERMOST chain (`ChainTargetBranch` is null) calls `ChainIntegrationBranch.LandRootIntegrationBranchAsync` ([ChainIntegrationBranch.cs:111](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L111)) to rebase-then-fast-forward the accumulated branch onto the configured target, then push when a landing remote and push are enabled.
10. Parent rollup is fail-soft. On success, `ChainIntegrationBranch.SweepChainWorktreesAsync` removes this chain's ticket and chain worktrees while retaining their branches; failures preserve the worktrees for inspection. Outcome is `ParentCompleted` or `ParentStoppedEarly` with `ChildResults`.

**Depth and cycles.** Trees deeper than one level now recurse (each level building its own integration branch that accumulates upward) up to `MaxDepth` (default 16), with UUID-based cycle detection. The old grandchild hard-stop is **Legacy**: `ChainOutcome.ParentHasGrandchildren` survives in the enum, the exit-code map, and `ChainCommand` triage text, but `ChainPhase` no longer produces it.

**`build sweep` (recovery verb).** A failed chain preserves its worktrees; `build sweep [--force]` is dispatched by `CliApplication` ([CliApplication.cs:453](../../src/ThroughlineBuild.Cli/CliApplication.cs#L453)) and invokes `ChainWorktreeSweeper` ([src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs)) to clean up afterwards: branch deletion is merged-gated so committed work is never lost; `--force` removes worktrees regardless.

Refusals enforcing the tree discipline (unchanged): `PlanPhase` and `ImplementPhase` refuse parent tickets; `ReviewPhase.RunParentReviewAsync` classifies children (any `InProgress`/`InReview` -> `Rework`; all `Done` -> `Pass`; else `Fail`); `ShipPhase.RunParentShipAsync` requires every child `Done` and transitions the parent straight to `Done` (no merge); `close`/`defer` cascade to non-terminal children; `reopen` does not reopen children.

### `--batch-implement` (warm batch sessions)

Status: **Functional**. When `ChainPhaseOptions.BatchImplementGroup` is set ([ChainPhase.cs:19-31](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L19-L31) - `AllEligibleChildren` for the bare flag, `ExplicitList` for a comma-separated list) and a batch worker plus the integration worktree are available, `ParentChainRunner` replaces the per-child implement+review+ship loop for the group with:

1. **Candidate gating** ([ParentChainRunner.cs:180](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L180)): `Ready` or `Backlog` leaf children in dependency order; internal nodes (children with live children of their own) are excluded per-candidate - the internal-node mis-batching fix. `Backlog` candidates are planned per-ticket first (planning is never batched).
2. **One worker session** (`RunBatchImplementSessionAsync`, [BatchImplementRunner.cs:119](../../src/ThroughlineBuild.Phases/BatchImplementRunner.cs#L119)): a single brief built by `BatchImplementBriefBuilder` ([src/ThroughlineBuild.Briefs/BatchImplementBriefBuilder.cs](../../src/ThroughlineBuild.Briefs/BatchImplementBriefBuilder.cs)) instructs the worker to stack one commit per ticket, in order, on the integration branch inside the integration worktree.
3. **Commit verification:** `BatchCommitVerifier.VerifyAsync` ([src/ThroughlineBuild.Phases/BatchCommitVerifier.cs:37](../../src/ThroughlineBuild.Phases/BatchCommitVerifier.cs#L37)) confirms the worktree is clean and the self-reported SHAs exist on the branch in declared order; `ReconstructFromGitAsync` rebuilds the attribution from git (one commit per ticket) when the worker omits its self-report - git is the source of truth.
4. **One combined review + rework loop** (`RunBatchReviewAndReworkAsync`, [BatchReviewRunner.cs:58](../../src/ThroughlineBuild.Phases/BatchReviewRunner.cs#L58)) over the full stack diff, briefed by `BatchReviewBriefBuilder`.
5. **Stack ship** (`ShipBatchStackAsync`, [ChainIntegrationBranch.cs:45](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L45)): advance the integration branch to the batch tip and mark every member `Done` with outcome `BatchImplemented`.

If batching was requested but the batch worker or integration worktree is unavailable, the chain emits a diagnostics-writer warning plus a `GateFailure` kind `batch_implement_unavailable` before falling back to the per-ticket loop - the silent-downgrade fix ([ParentChainRunner.cs](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs)).

### Loose ends

- The batch path bypasses the per-ticket gate: batch members get the combined review but no `GatePhase` run, so completion claims and vacuity proving do not apply to batched tickets.
- `ParentHasGrandchildren` is dead code in `ChainPhase` but still reachable-looking from the docs/help text; the enum value should eventually be retired or re-documented as Legacy.
- Child cascade close/defer failures are logged to stderr and do not abort the parent transition.

---

## Multi-ticket dispatch

For an explicit multi-ticket `build chain A B ...`, `ChainDependencyGraph.Build` ([ChainDependencyGraph.cs:14](../../src/ThroughlineBuild.Phases/ChainDependencyGraph.cs#L14)) constructs the ordering graph from each ticket's live typed relations. It normalizes bare IDs and configured-project-prefixed IDs by numeric sequence, and directs each blocker to the ticket it blocks. Relation enumeration comes from `ListRelationsAsync`; a cross-project prefix or unavailable relation endpoint fails before dispatch rather than silently guessing. Parent/sibling orchestration still uses its separate `TicketGraph` path.

Status: **Functional**, serial (the "parallel" name is historical).

`build chain TLB-A TLB-B ...` with multiple ids takes the dispatcher path: fetch all tickets, build a `ThroughlineBuild.Phases.TicketGraph` from `blocked_by` relations within the dispatched set, hand it to `ParallelDispatcher`.

`ParallelDispatcher.RunAsync` ([src/ThroughlineBuild.Phases/ParallelDispatcher.cs](../../src/ThroughlineBuild.Phases/ParallelDispatcher.cs)): `TopologicalSorter.ComputeLevels` (Kahn's BFS, input order preserved within a level, throws on a cycle), `PrintDispatchOrder` to its injected output writer, emit `DispatchStart`, run each level under a per-level semaphore whose width is **hard-pinned to 1** (the constructor keeps the `maxConcurrency` parameter for API stability and discards it - the topological order is load-bearing, concurrency is disposable), stop further levels on any non-success outcome, emit `DispatchEnd`.

### Phase output boundaries

`ThroughlineBuild.Phases` has no direct `Console` dependency. `ChainPhaseExecutionDependencies` requires separate output and diagnostics writers; `ChainPhase` forwards them to `ParentChainRunner`, `ChainIntegrationBranch`, `ChainEventEmitter`, and dry-run planning. `ParallelDispatcher` also requires its output writer. Standalone `PlanPhase`, `GatePhase`, `ReviewPhase`, and `ShipPhase` receive a diagnostics writer from CLI composition for best-effort ticket-write warnings and recovery notices. Normal chain output stays on the output writer, refusals and warnings stay on diagnostics, and phase start/completion progress continues through `ChainPhaseOptions.OnStep`. CLI structured-output modes can therefore suppress incidental phase output without redirecting process-global streams.

**Ancestor-skip** (TLB-313): `AncestorSkipFilter.ShouldSkip` synthesizes `Skipped` results for tickets whose ancestors failed; `--continue-past-failure` disables it. `SequentialChainDispatcher` remains as the legacy linear-edge fallback.

### Loose ends

- The dispatcher name is a misnomer: both the multi-ticket path and the parent-chain level loop are strictly serial.
- `workers.max_concurrency` is read from config but ignored by the dispatcher.

---

## Divergence and merge orchestration

Status: **Functional** (TLB-290/291/293/296/297/298).

`ShipPhase` resolves the rebase target by ancestry; when local target and `<remote>/<target>` have diverged it probes with `IGitClient.ProbeDivergenceAsync` (`git merge-tree --write-tree`, returning a `DivergenceState` of `Clean, LocalAhead, RemoteAhead, DivergedNoConflict, DivergedWithConflict` - [src/ThroughlineBuild.Contracts/IGitClient.cs](../../src/ThroughlineBuild.Contracts/IGitClient.cs)):

- `DivergedNoConflict` and NOT `--no-auto-merge`: auto-rebase the local target onto `<remote>/<target>` under `MainWorktreeLock`; emit `TargetAutoRebased` (`outcome=clean`), or on a race-to-conflict abort, emit `TargetAutoRebased` (`outcome=raced_to_conflict`) + `GateFailure` and fail at `Fetch`.
- Conflict, or `--no-auto-merge`: post `ship_blocked` comment, `GateFailure`, fail at `Fetch`.

`MainWorktreeLock` ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs)) is a per-path in-process `SemaphoreSlim` serializing fetch, target auto-rebase, and FF merge on the shared main worktree. With dispatch width-1 serial it is largely defensive.

### Loose ends

- `MainWorktreeLock` is in-process only; two concurrent `build` processes on the same repo can still race.
- `--no-auto-merge` forces the diverged case to a hard stop even when `merge-tree` says it is conflict-free.

---

## Coordination protocol

Plane coordination now distinguishes durable workflow state from commentary. `TicketingWritePolicy.BestEffortAsync` makes designated informational comments and label writes warning-only during an outage, while state transitions, `[planned_at]`/`[implemented_at]`/`[shipped_at]` resume markers, and parent completion transitions remain hard. This lets an otherwise valid git/verification operation survive a lost diagnostic comment without claiming a state change that Plane never recorded.

How phases communicate without a persistent process:

| Mechanism | What it carries |
|---|---|
| **Plane state field** | The authoritative "what phase comes next?" - each phase checks state on entry. |
| **Plane comment markers** | The SHA stamps (`[planned_at]`, `[implemented_at]`, `[shipped_at]`, `[decomposed_at]`) parsed by `MarkerParser`. |
| **Plane comment marker prefixes** | `wontfix:`, `deferred:`, `reopened:`, `reviewed:` for state context. |
| **Ticket description** | Plan HTML appended once; the scaffold's `<h3>Preload</h3>` block names the files the implement phase inlines. |
| **Ticket labels** | `risk:*`, `size:*` from plan; `plan-ticket` from scaffold; `size:*` on decompose children. |
| **Parent relations** | The parent/child edges that drive the tree-aware chain, scaffold tree, and decompose fan-out. |
| **`blocked_by` relations** | The dependency edges that drive multi-ticket and sibling dispatch ordering (serial, width 1). |
| **`.build/events/<stem>.jsonl`** | Replayable audit log. The rework feedback retriever reads it to recover the most recent `Rework` verdict including `checks_failed_details` evidence. |
| **`COMPLETION_CLAIM` block / `ShippedProvides`** | The implement worker's claim feeds the gate; shipped provides accumulate in-process across siblings for the consumes-provides preflight. |
| **`chain/<slug>` integration branch + worktree** | The parent chain's accumulation point: leaves ship into it, nested sub-chains fast-forward into it, the root lands it on the target. |
| **`.worktrees/ticket-<slug>/`** | The implementer's checkout. Reviewer reads its diff; shipper rebases + merges from it. |
| **Local git branch `ticket/<slug>`** | The carrier of the actual commits. |
| **Debug capture directory** | Per-run side channels: `ReworkRoundManifest` records, worker stdin/stdout/stderr, and the structured worker transcript (see [11-llm-architecture.md](11-llm-architecture.md)). |
| **`MainWorktreeLock`** | In-process serialization of main-worktree git ops. |

There is no message bus and no persistent in-process state between separate `build` invocations. Every restart re-reads from Plane + git + events.

### Loose ends

- Within a single `build chain` run, in-process state (the accumulated-provides set, semaphores, the lock) does persist; the "no shared state" principle holds only across separate process invocations.

---

## Sessions

Every `build <verb>` invocation mints session ids via `_sessionIdGenerator` (default `Guid.NewGuid().ToString("N")`). In a chain, each phase gets its own per-phase session id, recorded on `ChainStep.PhaseSessionId`, while `ChainStart`/`ChainEnd` carry a single chain session id. The dispatcher mints its own dispatch session id for `DispatchStart`/`DispatchEnd`.

Session ids flow into `WorkflowEvent.SessionId`, the JSONL file naming (via `SessionFileNameBuilder`), and the debug capture directory.

### Loose ends

- Per-phase session ids are always distinct within a chain; the chain mints a fresh id per phase.

---

## Event kinds emitted

The `EventKind` enum has 14 values ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14)); the enum declaration is the authoritative registry and carries an integer-value comment for the JSONL wire format. The kinds:

| Kind | Emitted by | Meaning |
|---|---|---|
| `StateTransition` | every phase/command that transitions | from / to state (resume transitions carry reason `chain_resume`) |
| `LlmCall` | phases that surface worker LLM usage | tokens / model / wall time |
| `WorkerSpawn` | phases that spawn a worker (and `NewPhase` for audit symmetry) | worker name + role |
| `VerifierVerdict` | every worker phase post-run; review post-verifier; decompose verdict gate | status / verdict / checks_failed / checks_failed_details |
| `GateFailure` | the workhorse: every refused gate across all phases | `kind` discriminator + reason |
| `TicketWrite` | every Plane write | action + payload summary |
| `ChainStart` / `ChainEnd` | `ChainPhase` | start state / outcome, phases_run, rework_rounds, duration |
| `ReworkRound` | `ChainPhase` | round, verdict_that_triggered (`Rework` or `GateFailure`), rationale_preview |
| `TicketSubsumed` | `ChainPhase` (obsolete ratification Pass) | ticket_id, subsumed_by_commit, files, rationale |
| `TargetAutoRebased` | `ShipPhase` | from_sha, onto_sha, outcome (clean / raced_to_conflict) |
| `DispatchStart` / `DispatchEnd` | `ParallelDispatcher` | ticket_count, level_count / outcome, duration |
| `CostLedger` | `ChainPhase` (gate economics), `ImplementPhase` (preload + context attribution) | `kind`-discriminated telemetry record |

`CostLedger` (new since the last refresh, TLB-510) is `kind`-discriminated like `GateFailure`: `gate` (gate wall ms, gate-attributable rework rounds/tokens, `false_fails`) from `ChainPhase.EmitCostLedgerAsync`; `preload_summary` and `context_attribution` from `ImplementPhase`.

`GateFailure` `kind` discriminators are defined at their emit sites, not centrally. Representative values by area: implement (`hygiene_gate`, `drift_warning`, `dirty_worktree_first_attempt`/`dirty_worktree_retry_failed`, `preload_file_not_found`, `preload_empty`), gate (`claim_schema_invalid`, `setup_failed`, `gating_checks_failed`, `gate_control_run`, `gate_config_reloaded`, `gate_environment_failure`, `gate_vacuous`, `gate_unverified`, `gate_canary_cleanup_failed`), review (`dirty_worktree_after_review`, `implemented_at_superseded`), ship (`pre_flight_*`, `wrong_worktree_branch`), chain (`chain_preflight_wrong_branch`, `chain_preflight_dirty`, `hygiene_gate_preflight`, `rework_recheck_failed`, `integration_worktree_unavailable`, `batch_implement_unavailable`, `chain_refresh_rebase_conflicts`).

Full event-line schema in [docs/build-event-log-format.md](../build-event-log-format.md).

### Loose ends

- `build-event-log-format.md` does not enumerate the per-`Data` shape of every kind; the authoritative `Data` keys are in the emitting code.
- `DispatchStart`/`DispatchEnd` carry an empty `TicketId`; `max_concurrency` is always 1.
- The `GateFailure` discriminator namespace is conventions-only; nothing prevents a collision between emit sites.

---

## Chain outcomes and exit codes

The `ChainOutcome` enum has 20 values ([src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:3](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3)); the exit-code mapping lives in `ChainExitCodeMapper.GetExitCode` ([src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs)), used on the single-ticket path and (via a `ParallelDispatchResult` overload: success -> 0, else the preserved outcome's code, else 1) on the dispatcher path.

| Exit | Outcomes | Meaning |
|---|---|---|
| 0 | `Completed`, `RatifiedObsolete`, `ParentCompleted`, `DryRunPreview` | success / preview |
| 2 | `RefusedInitialState`, `RefusedDirtyTree`, `RefusedWrongBranch`, `ParentHasGrandchildren` | refused before any phase ran |
| 3 | `StoppedAtPlan`, `ParentStoppedEarly`, `Skipped` | stopped at/before planning, or a child/ancestor stopped |
| 4 | `StoppedAtImplement` | implementation failed |
| 5 | `StoppedAtReview` | review returned `Fail` (or review infra failure) |
| 6 | `ReworkCapExceeded` | rework budget (or check-recheck retries) exhausted |
| 7 | `StoppedAtShip` | ship gate failed |
| 8 | `GateVacuous` | a gating check is vacuous or its canary leaked - config defect, no rework |
| 9 | `ReviewUnavailable` | verifier blocked by a transient provider error; ticket resumable `InReview` (TLB-527) |
| 10 | `GateEnvironmentFailure` | gate fails on the untouched base ref too - environment broken; siblings skipped (TLB-538) |
| 11 | `TicketingUnavailable` | ticketing backend unreachable at transport level after retries; resumable (TLB-545) |

`BatchImplemented` is a per-member outcome inside a parent chain, not a process exit. Success set used by dispatchers and the aggregate report: `Completed`, `RatifiedObsolete`, `ParentCompleted`, plus `DryRunPreview`/`BatchImplemented` where applicable (`IsChainSuccess` in `ChainPhase` is the authority).

### Loose ends

- `Skipped` maps to exit 3 even though it is treated as a non-failure for the overall exit-0 decision in the sequential aggregate path.
- `ParentHasGrandchildren` is mapped but no longer produced (Legacy).

---

## Where the chain stops cleanly vs. requires manual triage

- **Clean stop / success:** `Completed`, `RatifiedObsolete`, `ParentCompleted`, `BatchImplemented`, `DryRunPreview`, `Skipped`, `RefusedInitialState`, `ReworkCapExceeded` (operator picks up with `build rework`/`build review`).
- **Resumable after an external fix:** `ReviewUnavailable` (wait out the provider quota, re-run `build review`/chain), `GateEnvironmentFailure` (fix the environment/config once, re-run - skipped siblings are picked up), `TicketingUnavailable` (restore connectivity, re-run), `RefusedWrongBranch`/`RefusedDirtyTree` (fix the main worktree, re-run).
- **Requires triage:** `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview`, `StoppedAtShip`, `ParentStoppedEarly`, `GateVacuous` (fix the check or its canary in config). Each leaves the ticket(s) in whatever state the failing phase left them; failed chains preserve their worktrees (`build sweep` cleans up after recovery).

`ChainCommand` surfaces a one-line final summary per outcome on stdout, with per-outcome triage text ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)).

---

## Repository readiness and external conductor orchestration

The repository now exposes the deterministic pieces needed by an outer conductor without asking `build` to spawn a nested agent:

1. `build install` stage 1 ensures init/setup and returns the profile prompt plus canonical next command.
2. The outer agent inspects the repository and returns `PROJECT_PROFILE`; `install --profile` or `profile apply` validates it and proves gating canaries in a temporary worktree before config changes.
3. Install adds embedded SOP stubs, derives deterministic conductor identity, and returns the invariant-authoring prompt.
4. The outer agent returns invariant TOML; `install --invariants` or `conductor apply` atomically replaces the invariant block.
5. Final install readiness runs doctor, checks, secret, branch/operation, cleanliness, and worktree-lease probes before reporting READY ([InstallCommand.ExecuteAsync:175](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L175), [InstallReadiness.PrepareAndAssertAsync:603](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L603)).
6. During backlog execution the outer conductor can call `sop brief` for the versioned procedure, `waves` for scheduling, `worktree lease` for isolation, `worker brief` for inspectable role input, `candidate status` for fingerprints, `gate` for checks, and `evidence add` for append-only audit records. Each command is a single deterministic boundary; ticket lifecycle transitions remain explicit separate verbs ([HelpRegistryFactory.cs:249](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L249)).
7. Candidate lease identity is prefix-aware rather than suffix-matched: `TicketMatches` normalizes full prefixed ids case-insensitively; a bare number matches only after `[conductor].ticket_prefix` resolves it to `<prefix>-<number>`, and no configured prefix means no bare-number match ([CandidateStatusCommand.cs:429-475](../../src/ThroughlineBuild.Cli/CandidateStatusCommand.cs#L429-L475)). This prevents one project's numeric ticket from validating another project's lease.

The SOP admission mode pins an absolute inspection worktree plus full commit SHA and causes mutating verbs to refuse with `sop_admission_refused`; it is an inspection boundary, not a mutation flag ([HelpRegistryFactory.cs:548](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L548)).

### Loose ends

- A successful install handoff is not readiness: callers must inspect `Data.Ready`/stage and stop for the requested external artifact.
- The conductor command family does not itself choose when to transition or close tickets; that policy belongs to the SOP and outer agent.

---

## Loose ends (cross-cutting)

- **`MaxReworkRounds = 2` is hardcoded; all dispatch is serial since op-29.** Both the parent chain level loop and `ParallelDispatcher` (width pinned to 1) run one ticket at a time; no concurrency knob remains.
- **`GatePhase` remains chain-only, but a standalone deterministic gate now exists.** `build gate` runs configured checks and persisted canary-proof policy; standalone `implement`/`review` still do not automatically insert the full chain GatePhase completion-claim/consumes-provides flow.
- **No cross-phase live channel.** ReviewPhase reconstructs the implementer brief deterministically; the chain holds in-process orchestration state (accumulated provides, gate cost accumulators) only for the duration of a run.
- **Chain `WorkflowEvent.Data`** schema lives in code, not exhaustively in [docs/build-event-log-format.md](../build-event-log-format.md).
- **No replay verb** (`build replay <session-id>`). Architecture Appendix item 4 notes this as a future.
- **`SequentialChainDispatcher`** remains as a legacy fallback alongside the now-serial `ParallelDispatcher`.
- **Two cleanup paths:** end-of-chain `SweepChainWorktreesAsync` (success only) decrufts worktrees while retaining branches. The `build sweep` recovery verb also handles branches, with branch deletion merged-gated in `ChainWorktreeSweeper`.
