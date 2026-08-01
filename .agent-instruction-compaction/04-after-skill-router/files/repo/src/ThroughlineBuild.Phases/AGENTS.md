# ThroughlineBuild.Phases - workflow phases + orchestration

Ten phases live here: plan, implement, gate, review, ship, chain, rework,
decompose, new, and draft. `GatePhase` sits between implement and review; check
running, vacuity proof, and base-ref control live in `Verification`.

Multi-ticket orchestration owns `ParallelDispatcher` (concurrency pinned to 1),
`TicketGraph`/`TopologicalSorter`, `AncestorSkipFilter`, debug manifests, and
`BatchCommitVerifier`, which re-derives commit attribution from git. Never trust
worker-reported SHAs. `ChainDependencyGraph` normalizes bare/project-prefixed
IDs and orders typed relation edges.

Things that bite:

- Everything is serial. Parent chains run children level-by-level in one shared
  integration worktree on `chain/{slug}`; reused integration branches refresh
  against base first (TLB-546). Children cut `ticket/{id}` in place; root landing
  rebases and fast-forwards the integration branch onto the target.
- `--batch-implement` sends Ready/Backlog leaf children to one worker session;
  internal nodes use per-child parent recursion.
- Implement-review rework caps at `MaxReworkRounds = 2`. Environmental failures
  such as gate control run or ticketing unavailable skip remaining siblings.
- `ShipPhase` mutates and pushes the target branch in the main worktree, with
  `WorkingTreeHygieneGate` preflights also used before implement and chain.
- `ChainPhase` construction belongs in Cli's `ChainPhaseComposition`.
- Informational Plane writes go through `TicketingWritePolicy`; lifecycle
  transitions and resume markers remain hard writes.
- Phase code uses injected `TextWriter`s, not `Console`. Keep normal output on
  the output writer, and refusals/warnings/recovery diagnostics on diagnostics
  so structured stdout stays clean. Phase-step progress uses
  `ChainPhaseOptions.OnStep`.

Lifecycle detail:
[../../docs/state-of-the-system/10-lifecycle-orchestration.md](../../docs/state-of-the-system/10-lifecycle-orchestration.md).
