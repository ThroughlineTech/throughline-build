# ThroughlineBuild.Phases - workflow phases + orchestration

Ten phase classes implementing the lifecycle: `PlanPhase`, `ImplementPhase`,
`GatePhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`,
`DecomposePhase`, `NewPhase`, `DraftPhase`. `GatePhase` (op-30) runs between
implement and review in the chain loop: it validates the worker's
`CompletionClaim`, runs the `[[review.checks]]` capability map once, and on a
Gating-check hard-fail transitions InReview->InProgress and routes to rework.

Multi-ticket orchestration also lives here: `ParallelDispatcher`, `TicketGraph`
(+ `TopologicalSorter` in the same file), `AncestorSkipFilter`,
`BatchCommitVerifier`, `EarlyExitManifest`. The old `TicketDependencyGraph` was
REMOVED (op-29); only `TicketGraph` remains.

Things that bite:
- Dispatch is SERIAL end to end. `ParallelDispatcher` pins concurrency to 1
  (op-29; `--max-parallel`/ForceParallel gone). `ChainPhase` parent recursion
  bypasses the dispatcher and runs its own `SemaphoreSlim(1,1)` level loop;
  children run in dependency-ordered levels, one at a time.
- Parent chain recurses (to `--max-depth`) building per-parent `chain/{slug}`
  integration worktrees with in-place `ticket/{id}` child branches stacking on
  the accumulating base. Integration + leaf branches are RETAINED at chain end
  for resume, not torn down.
- `ChainPhase` runs the implement->gate->review loop with `MaxReworkRounds = 2`
  (a gate hard-fail consumes a round), then ships. `--batch-implement` runs one
  warm worker session over N children, verified by `BatchCommitVerifier`.
  Hygiene gates (`WorkingTreeHygieneGate`) run pre-implement, at chain preflight,
  and pre-ship, plus post-phase worktree-cleanliness checks.
- `ShipPhase` mutates and PUSHES the target branch in the main worktree, with a
  preflight guard against shipping onto the wrong branch.
- Chain construction goes through `ChainPhaseComposition` (in `Cli`) so no
  dependency can be silently dropped; `ChainExitCodeMapper` owns the exit codes.

State transitions and full lifecycle:
[../../docs/state-of-the-system/10-lifecycle-orchestration.md](../../docs/state-of-the-system/10-lifecycle-orchestration.md).
