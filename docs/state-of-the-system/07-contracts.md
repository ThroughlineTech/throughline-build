# 07 - Contracts

The inter-project type contracts inside this repo, and the artifacts shared with sibling systems (Plane, Claude Code, the older claude-config slash commands).

This document points at the types - it does not reproduce the bodies. For type definitions, follow the cited paths.

For phase orchestration that uses these types, see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md). For external service contracts, see [03-external-dependencies.md](03-external-dependencies.md).

---

## Inter-project contracts (within this repo)

### Dependency graph

```
Contracts (leaf, no project refs)
    +-- Briefs
    +-- Helpers
    +-- Git
    +-- EventLog
    +-- Plane
    +-- ModelClient (leaf, no project refs)
    +-- Anthropic ........... refs Contracts + ModelClient
    +-- JudgmentSlots ....... refs Contracts
    +-- Workers.Common ...... refs Contracts (holds WorkerResultParser)
    +-- Workers.ClaudeCode .. refs Contracts + Workers.Common
    +-- Workers.Codex ....... refs Contracts + Workers.Common
    +-- Workers.Gemini ...... refs Contracts + Workers.Common
    +-- Workers.Copilot ..... refs Contracts + Workers.Common
    +-- Verification ........ refs Contracts + Briefs
    +-- Scaffold
    +-- Phases .............. refs Contracts + Briefs + Git + Helpers + Verification
                                    |
                                    v
                                Commands
                                    |
                                    v
                                   Cli (refs all four Workers.* + ModelClient + ...)
```

Verify by reading `<ProjectReference Include="..." />` lines in each `.csproj`. Two new leaf projects since the architecture doc: `ThroughlineBuild.ModelClient` (the `IModelClient` abstraction) and `ThroughlineBuild.Workers.Common` (the shared `WorkerResultParser`). `Helpers` now hosts the tree-walk utilities (`TicketTreeWalker`, `ParentDetector`).

### Core types live in `ThroughlineBuild.Contracts`

| File | Type(s) | Notes |
|---|---|---|
| [src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs) | `Ticket(Id, Uuid, Title, Type, State, Size, Risk, DescriptionHtml, Relations, Labels, ParentId)`, `TicketState`, `Size`, `Risk` | `Uuid` added (TLB-263) - the Plane work-item UUID, distinct from the human `Id` like `TLB-42`. `ParentId` carries the parent UUID. `TicketState` has 7 values, `Size` and `Risk` have 3 each. `Risk.Low/High` never constructed in production; `Size` is now read from a `size:` label (so `Size.S/L` can appear). |
| [src/ThroughlineBuild.Contracts/Models/Brief.cs](../../src/ThroughlineBuild.Contracts/Models/Brief.cs) | `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` | The unit handed to a worker. |
| [src/ThroughlineBuild.Contracts/Models/WorkerResult.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs) | `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata, Blocks? = null, Tickets? = null)`, `Status` enum (Ok/NeedsRework/Failed/Escalate) | Parsed from the `WORKER_RESULT` envelope by `Workers.Common.WorkerResultParser`. `Blocks` (`IReadOnlyDictionary<string,string>?`) carries the fenced-block payloads captured in the parser pre-pass (op-27); resolved by `FencedBlockResolver`. `Tickets` (`IReadOnlyList<BatchTicketResult>?`, op-32) carries per-ticket batch attribution on the live batch implement path. |
| [src/ThroughlineBuild.Contracts/Models/Verdict.cs](../../src/ThroughlineBuild.Contracts/Models/Verdict.cs) | `Verdict(Kind, Rationale, ChecksFailed)`, `VerdictKind` (Pass/Rework/Fail) | Returned by `IVerifier.VerifyAsync` and `IObsoleteRatifier.RatifyAsync`. |
| [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs) | `WorkflowEvent(SessionId, Timestamp, Kind, TicketId, Phase, Data)`, `EventKind` (14 values) | `EventKind` grew from 9 to 14: added `TicketSubsumed` (TLB-285), `TargetAutoRebased` (TLB-298; renamed from `MainAutoRebased` when ship gained a configurable target branch, ordinal 10 unchanged), `DispatchStart`, `DispatchEnd` (TLB-312), and `CostLedger` (=13, the gate-attributable rework cost ledger). Integer values are pinned in a comment ([WorkflowEvent.cs:11-14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11-L14)). |
| [src/ThroughlineBuild.Contracts/Models/Phase.cs](../../src/ThroughlineBuild.Contracts/Models/Phase.cs) | `Phase` enum (11 values: Plan/Implement/Review/Ship/Chain/New/Command/Draft/Scaffold/Decompose/Gate) | `Decompose` added (TLB-259/264); `Gate` added (op-30) for the deterministic chain gate's events. |
| [src/ThroughlineBuild.Contracts/Models/ChainResult.cs](../../src/ThroughlineBuild.Contracts/Models/ChainResult.cs), [ChainStep.cs](../../src/ThroughlineBuild.Contracts/Models/ChainStep.cs), [ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs) | `ChainResult`, `ChainStep`, `ChainOutcome` enum | `ChainResult` gained `ShippedProvides` ([ChainResult.cs:13-16](../../src/ThroughlineBuild.Contracts/Models/ChainResult.cs#L13-L16)) - the `Provides` declared by a shipped leaf ticket's completion claim, accumulated upstream by `ChainPhase` for the consumes/provides preflight (populated only for leaf tickets with `Outcome=Completed`). `ChainOutcome` grew to 15 values: added `RefusedDirtyTree`, `RefusedWrongBranch`, `ParentCompleted`, `ParentStoppedEarly`, `ParentHasGrandchildren`, `Skipped`, `BatchImplemented` (op-32), `DryRunPreview` ([ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs)). |
| [src/ThroughlineBuild.Contracts/Models/TicketNode.cs](../../src/ThroughlineBuild.Contracts/Models/TicketNode.cs) | `TicketNode(Root, Children)` | Recursive ticket-tree node (TLB-301). Built by `Helpers.TicketTreeWalker`. |
| (removed) `Contracts.Models.TicketGraph` record + `Helpers.TicketDependencyGraph` | - | REMOVED (op-29 brief 05 / 52e81d3). The Contracts dependency-graph record and its `TicketDependencyGraph.BuildAsync` builder are gone; only the live dispatcher type `ThroughlineBuild.Phases.TicketGraph` + `TopologicalSorter` remain ([src/ThroughlineBuild.Phases/TicketGraph.cs](../../src/ThroughlineBuild.Phases/TicketGraph.cs)). The old "two TicketGraph types" overlap is resolved - see the note below. |
| [src/ThroughlineBuild.Contracts/Models/ParallelDispatchResult.cs](../../src/ThroughlineBuild.Contracts/Models/ParallelDispatchResult.cs) | `ParallelDispatchResult` | Aggregate result of multi-ticket dispatch (TLB-312/313). |
| [src/ThroughlineBuild.Contracts/Models/SubsumedByEvidence.cs](../../src/ThroughlineBuild.Contracts/Models/SubsumedByEvidence.cs) | `SubsumedByEvidence(Commit, Files, Rationale)` | Structured form of the `metadata.escalation.subsumed_by` block (TLB-278). |
| [src/ThroughlineBuild.Contracts/Models/Relation.cs](../../src/ThroughlineBuild.Contracts/Models/Relation.cs) | `Relation(Kind, TargetId)` | |
| [src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs](../../src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs) | `ReviewFeedback(Rationale, ChecksFailed, ReworkRoundNumber, IReadOnlyList<CheckResult>? GateFailedChecks = null)` | Passed back into `ImplementBriefBuilder` for rework. `GateFailedChecks` (TLB-509) distinguishes the rework's origin: `null` = review-originated rework; non-null (the gating `CheckResult`s that failed) = gate-originated rework, set by `ChainPhase` when the deterministic gate fails ([ChainPhase.cs:686-687](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L686-L687)) and rendered into a "gate failure" brief section by `ImplementBriefBuilder` ([ImplementBriefBuilder.cs:108-122](../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L108-L122)). |
| [src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs) | `CompletionClaim(Provides, Consumes, AcBindings, TestsAdded, ...)`, `AcBinding(AcRef, VerifierKind Kind)`, `enum VerifierKind { Test, GrepPresent, GrepAbsent, File, Exit, Golden, Smoke }` ([:6](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs#L6)) | op-30 gate artifact. The implement worker emits it as a `COMPLETION_CLAIM` fenced block + `completion_claim_ref` metadata; parsed by `CompletionClaimParser` (Workers.Common, [CompletionClaimParser.cs:37-90](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs#L37-L90)) into `ImplementResult.CompletionClaim` ([ImplementPhase.cs:27](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L27)). The four arrays are required (may be empty). Three DECLARED-BUT-UNENFORCED hook fields `RedGreenKind`, `Tier`, `RoutingKey` (all default null, read by no consumer; Aspirational, [CompletionClaim.cs:28-34](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs#L28-L34)). |
| [src/ThroughlineBuild.Contracts/Models/SmokeSignal.cs](../../src/ThroughlineBuild.Contracts/Models/SmokeSignal.cs) | `enum SmokeSignalKind { DiffFacts, GrepPresent, GrepAbsent }`, `SmokeSignal(Label, Kind, bool Matched, Details)` | Advisory, never gate-failing (op-30). Produced by `SmokeCollector` (Verification, [SmokeCollector.cs](../../src/ThroughlineBuild.Verification/SmokeCollector.cs)) over a `GitDiff` or worktree tree; consumed by the LLM verifier as a prior, not a verdict. |
| [src/ThroughlineBuild.Contracts/Models/BatchTicketResult.cs](../../src/ThroughlineBuild.Contracts/Models/BatchTicketResult.cs) | `BatchTicketResult(TicketId, CommitSha, StackPosition, FilesChanged, SummaryRef)` (snake_case JSON) | op-32 per-ticket attribution inside one batch implement session. Carried on the LIVE batch path by `WorkerResult.Tickets` (see below). |
| [src/ThroughlineBuild.Contracts/Models/BatchWorkerResult.cs](../../src/ThroughlineBuild.Contracts/Models/BatchWorkerResult.cs) | `BatchWorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata, Tickets, Blocks? = null)` | **Possibly-vestigial parallel DTO.** It exists, has source-gen registration and parser support (`WorkerResultParser.TryParseBatch`), and is unit-tested - but the LIVE batch path does NOT use it: `ChainPhase` calls the standard `IWorkerAgent.ExecuteAsync` ([ChainPhase.cs:1171-1173](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1171-L1173)) and reads attribution off the returned `WorkerResult.Tickets`/`Blocks`. `TryParseBatch` is invoked only from tests (grep-confirmed). |
| [src/ThroughlineBuild.Contracts/Models/ModelTier.cs](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs) | `ModelTier(string Model, string? Effort = null)` | The model id + optional reasoning effort for a `WorkerSize`. Effort is carried for every vendor but acted on only by Codex (no-op for claude-code/gemini/copilot). |
| [src/ThroughlineBuild.Contracts/Models/DraftResult.cs](../../src/ThroughlineBuild.Contracts/Models/DraftResult.cs) | `DraftResult`, `DraftOutcome` enum | |
| [src/ThroughlineBuild.Contracts/Models/NewResult.cs](../../src/ThroughlineBuild.Contracts/Models/NewResult.cs) | `NewResult(Id, Uuid, ValidationWarnings)` | |
| [src/ThroughlineBuild.Contracts/Models/WorkerSize.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs) | `WorkerSize` enum (Small/Medium/Large) | Worker-domain size signal, mapped by each agent to its own model tier (TLB-196). Deliberately separate from the ticket-domain `Size` enum. |
| [src/ThroughlineBuild.Contracts/IWorkflowPhase.cs](../../src/ThroughlineBuild.Contracts/IWorkflowPhase.cs) | `IWorkflowPhase`, `PhaseResult(Success, TicketId, Phase, FailureReason?, Outputs)` | `Outputs` is an untyped `IReadOnlyDictionary<string,string>`. |
| [src/ThroughlineBuild.Contracts/Verifier/IVerifier.cs](../../src/ThroughlineBuild.Contracts/Verifier/IVerifier.cs) | `IVerifier`, `GitDiff`, `DiffEntry`, `DiffKind` (4 values: Added/Modified/Deleted/Renamed) | |
| [src/ThroughlineBuild.Contracts/Verifier/IObsoleteRatifier.cs](../../src/ThroughlineBuild.Contracts/Verifier/IObsoleteRatifier.cs) | `IObsoleteRatifier` | One method `RatifyAsync(ticket, escalateResult, ct) -> Verdict`. Implemented by `Verification.ObsoleteRatifier` (TLB-282); consumed by the chain auto-resolve path. |
| [src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs) | `enum CheckRole { Gating, Advisory }` ([:5](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L5)), `CheckSpec(Name, Executable, Arguments, Timeout, CheckRole Role = Gating)` ([:7-12](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L7-L12)), `CheckResult(Name, Passed, ExitCode, StdoutTail, StderrTail, Elapsed, CheckRole Role = Gating, bool Skipped = false)` ([:14-22](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L14-L22)) | StdoutTail/StderrTail capped at ~4 KiB. `CheckRole` (op-30) splits Gating (non-zero exit hard-fails the gate) from Advisory (recorded, surfaced to the verifier, never hard-fails). The `[[review.checks]]`-parsed `CheckSpec` set is the shared "capability map" the deterministic gate AND review both consume. `Skipped` flags a config-absent check that never counts as a failure. |
| [src/ThroughlineBuild.Contracts/ITicketing.cs](../../src/ThroughlineBuild.Contracts/ITicketing.cs) | `ITicketing` + `TicketComment`, `BackendCapabilities`, `RollupResult`, `NewTicketResult`, `TicketQuery`, `LifecycleTransition`, `ChildTicketSpec`, `CreatedChild`, `CreateChildTicketsResult`; plus `ITicketingConnectivity` + `TicketingConnectivityResult(bool Success, string Message)` ([ITicketing.cs:127-132](../../src/ThroughlineBuild.Contracts/ITicketing.cs#L127-L132)) | Grew by 5 methods: `QueryAsync`, `TransitionLifecycleAsync`, `UpdateDescriptionAsync` (TLB-251), `CreateChildTicketsAsync` (TLB-262), plus `SetParentAsync`. `BackendCapabilities` still advertised but unused. `ITicketingConnectivity.TestConnectivityAsync` is a pre-write readiness probe (op-34) so a config/auth failure is reported once before any partial creation; implemented by `PlaneTicketingClient`. |
| [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `IWorkerAgent` (adds `Digester` property), `WorkerOptions(Timeout, AllowedTools?, EnvironmentVariables?, DebugCaptureDirectory?, LiveStdoutSink?, LiveStderrSink?, ProgressDigestSink?, Size)` | `Size` (WorkerSize, default Medium) added (TLB-196). `IWorkerAgent.Digester` returns the agent's `IWorkerProgressDigester?`. |
| [src/ThroughlineBuild.Contracts/IWorkerAgentFactory.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgentFactory.cs) | `IWorkerAgentFactory` | One method `Create(agentName)`. Maps a configured agent name (claude-code/codex/gemini/copilot) to a concrete `IWorkerAgent`. |
| [src/ThroughlineBuild.Contracts/IWorkerProgressDigester.cs](../../src/ThroughlineBuild.Contracts/IWorkerProgressDigester.cs) | `IWorkerProgressDigester` | One method `FormatLine(rawLine) -> string?`, best-effort, never throws. Implemented per-agent (Copilot returns null). |
| [src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs) | `ILlmClient`, `LlmMessage`, `InvocationOptions`, `LlmResponse`, `LlmUsage`, `LlmStreamEvent` hierarchy | `InvokeStreamAsync` is a stub in both implementations (`AnthropicClient`, `ModelClientLlmAdapter`). |
| [src/ThroughlineBuild.Contracts/IGitClient.cs](../../src/ThroughlineBuild.Contracts/IGitClient.cs) | `IGitClient` + result records (`WorktreeInfo`, `WorktreeCreateResult`, `WorktreeRemoveResult`, `GitOpResult`, `RebaseResult`) + `DivergenceState` enum | `DivergenceState` (Clean/LocalAhead/RemoteAhead/DivergedNoConflict/DivergedWithConflict) added with `ProbeDivergenceAsync` (TLB-296). ~19 async methods; several have interface defaults (RemoteExists/GetTrackedChanges/IsAncestor/Push/ProbeDivergence/LogShas) so older test fakes keep compiling. |
| [src/ThroughlineBuild.Contracts/IEventSink.cs](../../src/ThroughlineBuild.Contracts/IEventSink.cs) | `IEventSink` | |
| [src/ThroughlineBuild.Contracts/IReviewFeedbackRetriever.cs](../../src/ThroughlineBuild.Contracts/IReviewFeedbackRetriever.cs) | `IReviewFeedbackRetriever` | One method: `GetLatestRework(ticketId)`. |
| [src/ThroughlineBuild.Contracts/ITicketCommand.cs](../../src/ThroughlineBuild.Contracts/ITicketCommand.cs) | `ITicketCommand`, `CommandResult(Success, Message?)`, `TicketCommandContext(TicketId, Args)` | Args is `Dictionary<string,string>` - untyped keys, parser-by-convention per command. |

### Tree-walk utilities live in `ThroughlineBuild.Helpers` (TLB-301/302)

Not in Contracts - these consume `ITicketing` rather than define a contract:

- `TicketTreeWalker` ([src/ThroughlineBuild.Helpers/TicketTreeWalker.cs](../../src/ThroughlineBuild.Helpers/TicketTreeWalker.cs)) - BFS walk of a ticket and its descendants to a depth (default 2), producing a `TicketNode`. Each level issues a `QueryAsync(ParentId: node.Uuid)`.
- `ParentDetector` ([src/ThroughlineBuild.Helpers/ParentDetector.cs](../../src/ThroughlineBuild.Helpers/ParentDetector.cs)) - `HasChildrenAsync(ticketing, uuid)` returns whether a ticket has any children. Used by `plan`/`implement` to refuse parent tickets (TLB-304) and by `chain` to recurse parents (TLB-306).

### Onboarding / provisioning contracts (op-33/34)

Bootstrap-time interfaces that route on the workspace slug alone (no configured project id yet), so `build init`/`build setup` can turn a project name into the uuid the rest of the system needs and bring a fresh Plane project up to the workflow's required state/label set. All four are implemented by the Plane backend; `ProjectResolver` is a separate class, the other three are satisfied by `PlaneTicketingClient` ([PlaneTicketingClient.cs:18](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L18), [ProjectResolver.cs:13](../../src/ThroughlineBuild.Plane/ProjectResolver.cs#L13)).

| File | Type(s) | Notes |
|---|---|---|
| [src/ThroughlineBuild.Contracts/IProjectResolver.cs](../../src/ThroughlineBuild.Contracts/IProjectResolver.cs) | `IProjectResolver.ResolveAsync(name) -> ProjectResolveResult(ProjectId, ProjectResolveOutcome)`, `enum ProjectResolveOutcome { Found, Created }` | Name -> project id, find-or-create, reporting which path was taken. Builds its Plane client from raw credentials, since it runs before any `.build/config.toml`. |
| [src/ThroughlineBuild.Contracts/IProjectDiscovery.cs](../../src/ThroughlineBuild.Contracts/IProjectDiscovery.cs) | `IProjectDiscovery` (`ListProjectsAsync`/`FindProjectByNameAsync`/`CreateProjectAsync`), `ProjectInfo(Id, Name, Identifier, DateTimeOffset? UpdatedAt = null)` | Workspace-slug-scoped discovery + creation. `UpdatedAt` is the "most recently used" sort key for the interactive picker; a 401/403 from create surfaces as a typed scope error. |
| [src/ThroughlineBuild.Contracts/ITicketingProvisioner.cs](../../src/ThroughlineBuild.Contracts/ITicketingProvisioner.cs) | `ITicketingProvisioner` (`ListStatesAsync`/`ListLabelNamesAsync`/`CreateStateAsync`/`CreateLabelAsync`), `ExistingState(Name, Group, double Sequence)` | Read the live state/label set and create whatever is missing so the project meets `WorkspaceSchema`. Distinct from `ITicketing` (whole-project, not per-ticket). |
| [src/ThroughlineBuild.Contracts/WorkspaceSchema.cs](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs) | `static WorkspaceSchema` (`States`, `Labels`, `RequiredState(Name, Group, TicketState)`) | **Single source of truth** for the 7 workflow states ([:23-32](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L23-L32)) and 9 labels ([:39-44](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L39-L44)) these provision. The runtime state-name reverse map in the Plane client is now DERIVED from `WorkspaceSchema.States` (not a hardcoded literal - [PlaneTicketingClient.cs:354-357](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L354-L357)), so the provisioning command and the runtime decoder can never drift. Missing labels hard-fail plan/chain; a missing state downgrades a transition to a warning. |

### Second model abstraction: `IModelClient` (separate project)

`IModelClient` is NOT in `Contracts` - it lives in its own leaf project [src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs) alongside `ModelRequest`, `ModelResponse`, `Usage`, `ContentBlock`/`TextContent`, the `ModelStreamEvent` hierarchy, and `ProviderConfig`. It is a richer, provider-agnostic alternative to `ILlmClient` (multi-block content, real `StreamAsync`, `ProviderConfig` auth shape for anthropic/openai/ollama). The Anthropic project implements it (`AnthropicModelClient`) and provides `ModelClientLlmAdapter` to bridge `IModelClient` back to `ILlmClient`. As of HEAD nothing in `Program.cs` constructs an `IModelClient` - the live LLM path is `AnthropicClient : ILlmClient` only (see 03-external-dependencies.md). Two overlapping abstractions for the same job is itself a loose end.

### Construction conventions

- Phases take their dependencies via constructor. Most fields are required interfaces; a few have defaults (`IGitClient = new ProcessGitClient(...)`, `ProjectContext = ProjectContext.Empty`) so tests can construct without DI.
- Records are immutable - all phase results, briefs, verdicts, events use C# record syntax with init-only properties.
- No DI container. All wiring is direct construction in `Program.cs`.

### Untyped / fragile contracts inside the model

- **`PhaseResult.Outputs`** is `IReadOnlyDictionary<string,string>` - per-phase key conventions (e.g., `commit_sha`, `branch`, `worktree_path`) are documented in code comments only.
- **`WorkflowEvent.Data`** is `IReadOnlyDictionary<string,object>` - per-EventKind shape documented in [docs/event-log-format.md](../event-log-format.md).
- **`WorkerResult.Metadata`** is similarly free-form - each phase asserts its required keys at parse time.
- **`TicketCommandContext.Args`** is `Dictionary<string,string>` keyed by flag name (with `--` stripped) - each command's `ExecuteAsync` is responsible for fishing out its keys.

These untyped dictionaries are intentional - they let phases evolve their per-phase metadata without changing the shared records - but they mean the type system does not catch a typo in a metadata key. The narrowing happens at use, not at the boundary.

---

## Contracts with sibling systems

This repo is the successor to `claude-config` (the older slash-command workflow). It also produces / consumes artifacts visible to:

- **Plane** (the ticketing backend) - this is the operational sibling.
- **The worker CLIs** (`claude`, `codex`, `gemini`, `copilot` - vendor binaries) - the worker siblings.
- **The `/ticket-*` slash commands** in `.claude/commands/` (running under the Claude Code harness, defined elsewhere) - these still operate on the same Plane data.

### Plane (`PlaneTicketingClient` <-> Plane REST API)

What `build` reads that Plane wrote:

- Ticket records (states, labels, comments, relations, parent links). Field names referenced verbatim in [src/ThroughlineBuild.Plane/PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs) - `name`, `description_html`, `state`, `label_ids`, `parent`, `type`, `created_at`, `comment_html`, `sequence_id`, `next_cursor`.
- State names `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled` - the reverse map is no longer a hardcoded literal but DERIVED from `WorkspaceSchema.States` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:354-357](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L354-L357)); the forward `switch` (state -> Plane name) is still spelled out per transition method ([:632-638](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L632-L638)). A workspace with different state names reads everything as `Backlog` and skips transitions - though `build setup` now provisions the canonical names from the same schema.
- The `size:s|m|l` label, mapped into `Ticket.Size` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:580-585](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L580-L585)).
- The `parent` field (a UUID) -> `Ticket.ParentId`; sub-issue children are discovered via `QueryAsync(ParentId:)` plus client-side filtering, since Plane ignores the server-side `parent=` query param.

What `build` writes that other systems read:

- HTML descriptions and comments. Plane web UI renders them.
- Markers embedded in comment HTML (`[planned_at: <sha>]`, `[implemented_at: <sha>]`, `[decomposed_at: <sha>]`, etc.) - parsed back by `build` itself. The `decomposed_at` marker is posted by `DecomposePhase` ([src/ThroughlineBuild.Phases/DecomposePhase.cs:145](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L145)) (TLB-263). Marker LOOKUP is now freshest-by-timestamp, not by list position: chain re-runs accumulate multiple markers on one ticket, and `CommentMarkers.LatestValue` ([src/ThroughlineBuild.Phases/CommentMarkers.cs:19-37](../../src/ThroughlineBuild.Phases/CommentMarkers.cs#L19-L37)) picks the comment with the max `CreatedAt`. The prior code kept the last marker in return order, but Plane returns comments newest-first, so a re-run read a STALE `implemented_at` from an earlier run and mis-attributed the implementer diff - a spurious Rework (TLB-412).
- **`implemented_at` marker vs worktree HEAD (TLB-414):** the freshest `implemented_at` marker proves implement ran and self-reports a SHA, but the worktree branch HEAD is ground truth. An implementer that amends or squashes AFTER posting the marker leaves it pointing at a superseded commit. `ReviewPhase` reads the freshest marker then compares it to HEAD ([src/ThroughlineBuild.Phases/ReviewPhase.cs:152-181](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L152-L181)); on divergence it emits a `GateFailure` with `kind = implemented_at_superseded` and attributes the review to HEAD (the diff and automated checks already run against HEAD), so the verifier never reasons about an orphaned commit.
- The `<strong>rollup:</strong>`-style prefixes. `TransitionLifecycleAsync` posts `<strong>wontfix:</strong>` / `<strong>deferred:</strong>` / `<strong>reopened:</strong>` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1071-1073](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1071-L1073)); rollup uses a `[rollup] ...` comment ([:840-841](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L840-L841)). Used by both `build` and operators reading the Plane UI.
- **Sub-issue parent/child linkage:** `CreateChildTicketsAsync` / `SetParentAsync` set the `parent` UUID field directly, so the children appear as native Plane sub-issues (TLB-262).

What rides inside the worker `WORKER_RESULT` envelope (not a Plane field but a shared schema):

- **`metadata.escalation` (TLB-278):** when a worker sets `Status=Escalate` it may attach an `escalation` object with `reason` and, when `reason == "obsolete"`, a required `subsumed_by { commit, files[], rationale }`. Validated in [src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:173-197](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L173-L197) (schema documented in the XML doc-comment at [:94-107](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L94-L107)) and modeled by `SubsumedByEvidence`. The chain auto-resolve path (`IObsoleteRatifier`) consumes it; on ratification the ticket transitions to Done and a `TicketSubsumed` event is emitted (TLB-282/283/285).

The `/ticket-*` slash commands (a separate consumer) also POST comments and PATCH states against the same Plane tickets. They use the same state-name vocabulary but their comment conventions differ (`Investigation`, `Implementation Plan` headings instead of markers in many places). The two flows coexist by being explicit about which phase wrote what - the freshest marker (max comment `CreatedAt`, via `CommentMarkers.LatestValue`) wins for marker-driven lookups.

### Worker CLIs (`ClaudeCodeAgent` / `CodexAgent` / `GeminiAgent` / `CopilotAgent` <-> vendor CLIs)

This is the most evolving contract. Spawn flags and output shapes per agent are tabulated in 03-external-dependencies.md; this section covers the shared envelope contract.

- The `WORKER_RESULT` envelope is the cross-agent contract, parsed by the shared `WorkerResultParser` in `ThroughlineBuild.Workers.Common` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)) - moved out of the per-agent project so all four agents share one parser. Required fields `status` + non-empty `summary`; optional `files_changed`, `failure_reason`, `metadata` (incl. the `escalation` block above). Walked in reverse so the last valid envelope wins (tolerates a template echo).
- Claude Code additionally wraps its output in an NDJSON / `type=result` envelope (`subtype`, `is_error`, `result`, `usage`); Gemini wraps it in a `{response, stats}` JSON object; Codex and Copilot emit plain text. Each agent's parser unwraps to the inner text, then runs the shared `WorkerResultParser`.
- **The agent itself must follow the envelope contract** - the per-phase, per-agent brief template includes a stub envelope the worker is instructed to emit. Templates now live under per-agent subdirectories `src/ThroughlineBuild.Briefs/Templates/{claude-code,codex,gemini,copilot}/{plan,implement,review,draft,decompose}.md`, loaded by [src/ThroughlineBuild.Briefs/TemplateLoader.cs](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs) (`Load(agentName, templateName)`, embedded resources; hyphen in `claude-code` becomes underscore in the resource name). A `decompose.md` variant was added per agent (TLB-261). The plan / implement templates carry an "obsolete detection" section instructing the worker to escalate with the `metadata.escalation` block (TLB-279/280), e.g. [src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md:34-59](../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L34-L59).
- **Fenced-block payload protocol (op-27, TLB-333..342).** The spec is [docs/op-docs/complete/op-27-worker-result-fenced-payloads.md](../op-docs/complete/op-27-worker-result-fenced-payloads.md). This is a worker-output CONTRACT: markdown bodies that used to be JSON-string metadata fields are now emitted as named fenced blocks (`<<<NAME_START` ... `<<<NAME_END`) before the `WORKER_RESULT` marker, referenced by `*_ref` metadata fields. The plan/implement/review/draft templates were migrated to emit them; the parser pre-pass captures them into `WorkerResult.Blocks` ([src/ThroughlineBuild.Contracts/Models/WorkerResult.cs:9](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs#L9), optional `IReadOnlyDictionary<string,string>?`); `FencedBlockResolver.TryResolveRef` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:624-633](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L624-L633)) resolves a ref field to its body; `MarkdownRenderer.Render` turns the body into Plane HTML. Per-phase: plan `PLAN_BODY`/`plan_body_ref`, implement `IMPLEMENT_SUMMARY`/`summary_ref`, review `REVIEW_CRITIQUE`/`rationale_ref`, draft `DRAFT_BODY`/`body_markdown_ref`. See [06-public-surfaces.md](06-public-surfaces.md).

The brief templates ARE part of the contract. Changing the required `metadata` keys (or fenced-block names/refs) in a template without updating the consuming phase's validation will silently break. Today, validation/resolution lives at:

- Plan: [src/ThroughlineBuild.Phases/PlanPhase.cs:140-149](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L140-L149) resolves `plan_body_ref` -> `PLAN_BODY` (rendered to HTML) and requires `risk_label`, `size_label`, `planned_at_sha`.
- Implement: [src/ThroughlineBuild.Phases/ImplementPhase.cs:397-407](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L397-L407) requires `commit_sha`; optionally resolves `summary_ref` -> `IMPLEMENT_SUMMARY` for the rendered comment.
- Review: [src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs:77-122](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L77-L122) reads `verdict`, `checks_failed`, and resolves `rationale_ref` -> `REVIEW_CRITIQUE` (falling back to a direct `rationale` field). The reviewer was renamed from `ClaudeCodeReviewer` to the agent-agnostic `WorkerAgentReviewer`.
- Draft: [src/ThroughlineBuild.Phases/DraftPhase.cs:70-84](../../src/ThroughlineBuild.Phases/DraftPhase.cs#L70-L84) resolves `body_markdown_ref` -> `DRAFT_BODY`, falling back to the legacy direct `body_markdown` field.

### Older claude-config workflow (still active in the same repo)

The `.claude/commands/` directory (defined elsewhere by `/ticket-install`) hosts the older slash-command workflow. It and `build` coexist by:

- Reading the **same** `.claude/plane-config.md` and `.claude/ticket-config.md` for ticket-workflow conventions.
- Using **different** session log locations: claude-config writes session JSONL to `~/.claude/projects/<encoded-path>/`; `build` writes structured event JSONL to `.build/events/`.
- Writing **different** comment formats to the same Plane tickets - markers vs. headed sections. Operators can tell them apart by inspecting the comment.
- `build new --print-template` produces a body file with the headings `# Title`, `**Type:**`, `## Description`, `## Acceptance criteria`, `## Out of scope`, `## Notes` ([src/ThroughlineBuild.Commands/Templates/new-ticket-body.md](../../src/ThroughlineBuild.Commands/Templates/new-ticket-body.md)). This is the same template shape claude-config's `/ticket-new` expects. NewPhase's validator recognizes those headings ([src/ThroughlineBuild.Phases/NewPhase.cs:99-118](../../src/ThroughlineBuild.Phases/NewPhase.cs#L99-L118)).
- `build`'s `Backlog`/`Planning`/`Ready`/`InProgress`/`InReview`/`Done`/`Cancelled` state vocabulary matches the one in `.claude/plane-config.md:15-21`.

Architecture Section 8 describes the cutover plan: when `chain` ships clean and survives a real 5-ticket chain, the markdown corpus (`commands/`) and the mirror infrastructure (`bin/sync-*`, `copilot-prompts/`, `plugins/latticeflow/`) get deleted in one commit. As of HEAD that deletion has not happened.

### Shared artifacts visible across both flows

| Artifact | Written by | Read by |
|---|---|---|
| Plane ticket descriptions and comments | both | both |
| Plane state transitions | both | both |
| `.claude/plane-config.md`, `.claude/ticket-config.md` | `/ticket-install` (external) | both - `build` does **not** consume these directly today, but they document the same configuration the operator put into `.build/config.toml` |
| `~/.claude/projects/<encoded>/...jsonl` | `claude` CLI | `token-audit` (this repo) |
| `.build/events/*.jsonl` | `build` only | `analyze-event-log` (this repo) |
| `.worktrees/ticket-<slug>/` | both | both |
| Plane deep-link URL `?next_path=/{slug}/browse/{id}` | `build` (printed to operator) + Plane UI | operator (TLB-292) |
| `WORKER_RESULT` envelope + `metadata.escalation` schema | worker CLI (per brief template) | all four `IWorkerAgent`s via shared `WorkerResultParser` |
| Fenced-block payload protocol (`<<<NAME_START`/`*_ref`, op-27) | worker CLI (per brief template) | `WorkerResultParser` pre-pass + `FencedBlockResolver` + `MarkdownRenderer` |
| `COMPLETION_CLAIM` fenced block + `completion_claim_ref` metadata (op-30) | implement worker (per brief template) | `CompletionClaimParser` (Workers.Common) -> `ImplementResult.CompletionClaim` -> the deterministic gate |
| Batch `tickets` array inside `WORKER_RESULT` (op-32) | batch implement worker (per brief template) | `WorkerResult.Tickets` consumed by `ChainPhase` for per-ticket commit attribution |
| op-doc authoring spec (`docs/op-docs/op-doc-spec.md`) | operator / `op-plan` | embedded single-source as a Scaffold resource ([Scaffold.csproj:13](../../src/ThroughlineBuild.Scaffold/ThroughlineBuild.Scaffold.csproj#L13), loaded by `OpDocDocsLoader.LoadSpec`) - the on-disk file IS the embedded copy (compiled in verbatim), so the two cannot drift |

---

## Conflicts and overlaps

- **State-name vocabulary** still appears in `.claude/plane-config.md` and [.build/config.toml.example](../../.build/config.toml.example) comments, but inside the binary it is now single-sourced: the reverse map and `build setup` provisioning both read [WorkspaceSchema.States](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L23-L32) (the reverse map is `WorkspaceSchema.States.ToDictionary(...)` at [PlaneTicketingClient.cs:354-357](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L354-L357)). The forward `switch` (state -> Plane name) in each transition method is still spelled out by hand and must stay in sync with the schema, but the old triple-duplication inside C# is gone. The two markdown copies are still unenforced.
- **Two `Size` enums:** the ticket-domain `Size` (`Contracts/Models/Ticket.cs`) and the worker-domain `WorkerSize` (`Contracts/Models/WorkerSize.cs`). A phase maps from one to the other; they are deliberately separate but both express small/medium/large.
- **Two LLM abstractions:** `ILlmClient` (Contracts) and `IModelClient` (ModelClient project) overlap entirely in intent. Only `ILlmClient`/`AnthropicClient` is wired at runtime; `IModelClient`/`AnthropicModelClient` is built and tested but dead at runtime.
- **~~Two `TicketGraph` types~~ - RESOLVED (op-29 brief 05 / 52e81d3):** the duplicate `Contracts.Models.TicketGraph` record and its test-only `Helpers.TicketDependencyGraph.BuildAsync` builder were REMOVED. Only the live dispatcher type survives: the mutable `ThroughlineBuild.Phases.TicketGraph` class ([src/ThroughlineBuild.Phases/TicketGraph.cs:4-13](../../src/ThroughlineBuild.Phases/TicketGraph.cs#L4-L13)), built and consumed via `TopologicalSorter.ComputeLevels` (same file, `:15-94`) on the multi-ticket dispatch path. `TicketDependencyGraph` is absent from `src/` (grep-confirmed); the name lingers only in stale docs.
- **Two reason-translation/escalation consumers of `Verdict`:** `IVerifier` (review) and `IObsoleteRatifier` (chain auto-resolve) both return `Verdict`.
- **Workspace / project IDs** appear in `.claude/plane-config.md` AND `.build/config.toml`. Both must be kept in sync by the operator (no auto-sync).
- **Ticket comment formats** differ between flows: claude-config posts headed sections, `build` posts markers. Old comments visible to both flows but only `build` parses markers.
- **`/ticket-ship` vs `build ship`** - both can transition a ticket to `Done`. Only one should be run per ticket; `build ship` is the current direction.

---

## Loose ends

- **`BackendCapabilities`** is never read - capability-driven dispatch is a typed promise without a runtime consumer. Adding a `GitHubTicketingClient` that returns `TypedRelations: false` would not actually disable any code path today.
- **`Phase.Command`** value is in the enum but no phase implementation uses it - it appears to flow through `ITicketCommand` implementations for `WorkflowEvent.Phase` when no specific phase applies.
- **`IReviewFeedbackRetriever`** has one implementation, one production caller, and a thin contract; could be inlined if the rework flow stays single-source.
- **`ILlmClient` vs `IModelClient`** - two abstractions for the same job, in two projects, with different content models. `InvokeStreamAsync` on `ILlmClient` is stubbed in both implementations, while `AnthropicModelClient.StreamAsync` actually works but has no caller. Reconciliation is unfinished.
- **`metadata.escalation` schema** is validated by `WorkerResultParser` and modeled by `SubsumedByEvidence`, but the brief templates (the producing side) and the parser (the consuming side) hold the schema in two places (a markdown stub plus C# validation) with no shared source of truth - same fragility as the other `metadata` keys.
- **Brief templates fan out 4 agents x 5 phases** under `Templates/<agent>/`. The required `metadata` keys must stay in sync across all per-agent variants and the consuming phase validators by hand; nothing enforces that the codex/gemini/copilot variants demand the same keys as claude-code.
- **`IWorkerAgentFactory` agent selection** maps a config name to one of four agents, but the configured default is still `claude-code`; the non-claude agents are tested but not the live path.
- **Cross-`Phase` metadata key conventions** for `PhaseResult.Outputs` and `WorkerResult.Metadata` live only in code comments at the producing site - no shared constants, no type-safe accessor.
- **`CompletionClaim` hook fields are Aspirational.** `RedGreenKind`, `Tier`, `RoutingKey` are declared on the record (default null) and documented as reserved for a future tier-routing gate, but no consumer reads them today ([CompletionClaim.cs:28-34](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs#L28-L34)). The `CompletionClaimParser` does not even deserialize them.
- **`BatchWorkerResult` is a possibly-vestigial parallel DTO.** It has source-gen registration, a dedicated `WorkerResultParser.TryParseBatch`, and unit tests, but the live batch implement path returns the standard `WorkerResult` (with `Tickets`/`Blocks`) instead; `TryParseBatch` is called only from tests. Either the live path should adopt it or it should be deleted.
- **Old claude-config workflow** is still operative; the deletion contemplated in architecture Section 8 has not happened. Both flows touch the same Plane records.
