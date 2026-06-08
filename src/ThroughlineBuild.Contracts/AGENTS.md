# ThroughlineBuild.Contracts - interfaces, records, enums

Pure abstractions only. NO I/O, no process spawning, no HTTP, no file access.
Everything here is referenced by nearly every other project, so keep it leaf.

Key interfaces: `IWorkerAgent` / `IWorkerAgentFactory`, `ITicketing`,
`ILlmClient`, `IVerifier`, `IObsoleteRatifier`, `IGitClient`, `IEventSink`,
`IWorkflowPhase`, `IWorkerProgressDigester`, `IReviewFeedbackRetriever`.

Models live in `Models/` (e.g. `ChainOutcome`, `Verdict`, `WorkerResult`,
`Ticket`, `Brief`, `Phase`, `WorkerSize`, plus the op-30/32/33 additions
`CompletionClaim`, `SmokeSignal`, `ModelTier`, `BatchTicketResult`). Verifier
types in `Verifier/` (`IVerifier`, `IObsoleteRatifier`, and the gate's
`CheckSpec`/`CheckResult`/`CheckRole`). Onboarding interfaces `IProjectResolver`,
`IProjectDiscovery`, `ITicketingProvisioner` (op-34) are here too.

Contract details and which implementations satisfy each:
[../../docs/state-of-the-system/07-contracts.md](../../docs/state-of-the-system/07-contracts.md).
