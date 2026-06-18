# ThroughlineBuild.Contracts - interfaces, records, enums

Pure abstractions only. NO I/O, no process spawning, no HTTP, no file access.
Everything here is referenced by nearly every other project, so keep it leaf.

Key interfaces: `IWorkerAgent` / `IWorkerAgentFactory`, `ITicketing` (+
`ITicketingProvisioner`, `ITicketingConnectivity`, `IProjectDiscovery`,
`IProjectResolver`), `ILlmClient`, `IGitClient`, `IEventSink`,
`IWorkflowPhase`, `IWorkerProgressDigester`, `IReviewFeedbackRetriever`;
`IVerifier` / `IObsoleteRatifier` live in `Verifier/`.

Models live in `Models/` (e.g. `ChainOutcome`, `Verdict`, `WorkerResult`,
`BatchWorkerResult`, `Ticket`, `Brief`, `Phase`, `WorkerSize`,
`CompletionClaim`, `ProviderError`). `TicketingUnavailableException` marks a
failure as environmental so orchestration skips siblings instead of reworking.
One data-carrying exception to "interfaces only": `WorkspaceSchema` is the
static canonical state/label set shared by the Plane client and `build setup`
so the two cannot drift.

Contract details and which implementations satisfy each:
[../../docs/state-of-the-system/07-contracts.md](../../docs/state-of-the-system/07-contracts.md).
