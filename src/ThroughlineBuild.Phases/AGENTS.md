# ThroughlineBuild.Phases - workflow phases + orchestration

Nine phase classes implementing the lifecycle: `PlanPhase`, `ImplementPhase`,
`ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `DecomposePhase`,
`NewPhase`, `DraftPhase`.

Multi-ticket orchestration also lives here: `ParallelDispatcher`, `TicketGraph`,
`AncestorSkipFilter`, `EarlyExitManifest`.

Things that bite:
- `ChainPhase` runs the implement->review loop with `MaxReworkRounds = 2`, then
  ships. Parent-ticket recursion is now SERIAL (op-29 removed concurrent parent
  dispatch and the `--max-parallel` flag); children run in dependency-ordered
  levels via `SemaphoreSlim(1,1)`, one at a time.
- `ShipPhase` mutates and PUSHES the target branch in the main worktree, with a
  preflight guard against shipping onto the wrong branch.

State transitions and full lifecycle:
[../../docs/state-of-the-system/10-lifecycle-orchestration.md](../../docs/state-of-the-system/10-lifecycle-orchestration.md).
