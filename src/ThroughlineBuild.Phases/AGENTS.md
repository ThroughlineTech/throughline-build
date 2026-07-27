# ThroughlineBuild.Phases - workflow phases + orchestration

Ten phase classes: `PlanPhase`, `ImplementPhase`, `GatePhase`, `ReviewPhase`,
`ShipPhase`, `ChainPhase`, `ReworkPhase`, `DecomposePhase`, `NewPhase`,
`DraftPhase`. `GatePhase` runs between implement and review in the chain loop;
its check running, vacuity proof, and base-ref control run live in
`Verification`. Multi-ticket orchestration: `ParallelDispatcher` (concurrency
pinned to 1), `TicketGraph` (+ `TopologicalSorter` in the same file),
`AncestorSkipFilter`, `EarlyExitManifest`, `ReworkRoundManifest` (--debug
side-channel), `BatchCommitVerifier` (re-derives batch commit attribution
from git state - never trust worker-reported SHAs).
Explicit multi-ticket chains use `ChainDependencyGraph` to normalize bare and
project-prefixed IDs and order typed relation edges.

Things that bite:
- Everything is SERIAL. Parent chains run children level-by-level in
  dependency order inside ONE shared integration worktree on `chain/{slug}`;
  a reused integration branch is refreshed against its base ref first
  (TLB-546). Children cut `ticket/{id}` branches in place; root landing
  rebases + fast-forwards the integration branch onto the target.
- `--batch-implement` sends all Ready/Backlog LEAF children to one worker
  session; internal nodes fall through to per-child parent recursion.
- Implement->review rework loop caps at `MaxReworkRounds = 2`. Environmental
  failures (gate control run, ticketing unavailable) skip remaining siblings.
- `ShipPhase` mutates and PUSHES the target branch in the main worktree, with
  `WorkingTreeHygieneGate` preflights (also pre-implement and chain preflight).
- `ChainPhase` construction lives in Cli's `ChainPhaseComposition`.
- Informational Plane writes go through `TicketingWritePolicy`; lifecycle
  transitions and resume markers remain hard writes.

Lifecycle detail: [../../docs/state-of-the-system/10-lifecycle-orchestration.md](../../docs/state-of-the-system/10-lifecycle-orchestration.md).
