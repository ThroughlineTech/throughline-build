# ThroughlineBuild.Phases - workflow phases + orchestration

Nine phase classes implementing the lifecycle: `PlanPhase`, `ImplementPhase`,
`ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `DecomposePhase`,
`NewPhase`, `DraftPhase`.

Multi-ticket orchestration also lives here: `ParallelDispatcher`, `TicketGraph`
(+ `TopologicalSorter` in the same file), `AncestorSkipFilter`,
`EarlyExitManifest`. The old `TicketDependencyGraph` was REMOVED (op-29); only
`TicketGraph` remains.

Things that bite:
- Dispatch is SERIAL end to end. `ParallelDispatcher` pins concurrency to 1
  (op-29; `--max-parallel`/ForceParallel gone). `ChainPhase` parent recursion
  bypasses the dispatcher and runs its own `SemaphoreSlim(1,1)` level loop;
  children run in dependency-ordered levels, one at a time.
- Parent chain creates ONE shared worktree on placeholder branch `chain/{slug}`;
  each child cuts its `ticket/{id}` branch in place, torn down once at chain end.
- `ChainPhase` runs the implement->review loop with `MaxReworkRounds = 2`, then
  ships. Hygiene gates (`WorkingTreeHygieneGate`) run pre-implement, at chain
  preflight, and pre-ship, plus post-phase worktree-cleanliness checks.
- `ShipPhase` mutates and PUSHES the target branch in the main worktree, with a
  preflight guard against shipping onto the wrong branch.

State transitions and full lifecycle:
[../../docs/state-of-the-system/10-lifecycle-orchestration.md](../../docs/state-of-the-system/10-lifecycle-orchestration.md).
