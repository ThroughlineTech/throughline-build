# 07 - Contracts

The inter-project type contracts inside this repo, and the artifacts shared with sibling systems (Plane, Claude Code, the older claude-config slash commands).

This document points at the types - it does not reproduce the bodies. For type definitions, follow the cited paths.

For phase orchestration that uses these types, see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md). For external service contracts, see [03-external-dependencies.md](03-external-dependencies.md).

---

## Inter-project contracts (within this repo)

### Dependency graph

```
Contracts (leaf, no project refs)
    +-- Briefs --------+
    +-- Helpers       |
    +-- Git           |
    +-- EventLog      |
    +-- Plane         |
    +-- Anthropic     |
    +-- JudgmentSlots-+
    +-- Workers.ClaudeCode-+
    +-- Verification ------+
    +-- Scaffold ----------+
                           |
                           v
                       Phases
                           |
                           v
                       Commands
                           |
                           v
                          Cli
```

Verify by reading `<ProjectReference Include="..." />` lines in each `.csproj`.

### Core types live in `ThroughlineBuild.Contracts`

| File | Type(s) | Notes |
|---|---|---|
| [src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs) | `Ticket`, `TicketState`, `Size`, `Risk` | `TicketState` has 7 values, `Size` and `Risk` have 3 each. `Size.S/L` and `Risk.Low/High` never constructed in production. |
| [src/ThroughlineBuild.Contracts/Models/Brief.cs](../../src/ThroughlineBuild.Contracts/Models/Brief.cs) | `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` | The unit handed to a worker. |
| [src/ThroughlineBuild.Contracts/Models/WorkerResult.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs) | `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata)`, `Status` enum | Parsed from the `WORKER_RESULT` envelope. |
| [src/ThroughlineBuild.Contracts/Models/Verdict.cs](../../src/ThroughlineBuild.Contracts/Models/Verdict.cs) | `Verdict(Kind, Rationale, ChecksFailed)`, `VerdictKind` (Pass/Rework/Fail) | Returned by `IVerifier.VerifyAsync`. |
| [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs) | `WorkflowEvent(SessionId, Timestamp, Kind, TicketId, Phase, Data)`, `EventKind` (9 values) | Audit / telemetry record. |
| [src/ThroughlineBuild.Contracts/Models/Phase.cs](../../src/ThroughlineBuild.Contracts/Models/Phase.cs) | `Phase` enum (9 values: Plan/Implement/Review/Ship/Chain/New/Command/Draft/Scaffold) | |
| [src/ThroughlineBuild.Contracts/Models/ChainResult.cs](../../src/ThroughlineBuild.Contracts/Models/ChainResult.cs), [ChainStep.cs](../../src/ThroughlineBuild.Contracts/Models/ChainStep.cs), [ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs) | `ChainResult`, `ChainStep`, `ChainOutcome` enum | |
| [src/ThroughlineBuild.Contracts/Models/Relation.cs](../../src/ThroughlineBuild.Contracts/Models/Relation.cs) | `Relation(Kind, TargetId)` | |
| [src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs](../../src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs) | `ReviewFeedback(Rationale, ChecksFailed, ReworkRoundNumber)` | Passed back into `ImplementBriefBuilder` for rework. |
| [src/ThroughlineBuild.Contracts/Models/DraftResult.cs](../../src/ThroughlineBuild.Contracts/Models/DraftResult.cs) | `DraftResult`, `DraftOutcome` enum | |
| [src/ThroughlineBuild.Contracts/Models/NewResult.cs](../../src/ThroughlineBuild.Contracts/Models/NewResult.cs) | `NewResult(Id, Uuid, ValidationWarnings)` | |
| [src/ThroughlineBuild.Contracts/IWorkflowPhase.cs](../../src/ThroughlineBuild.Contracts/IWorkflowPhase.cs) | `IWorkflowPhase`, `PhaseResult(Success, TicketId, Phase, FailureReason?, Outputs)` | `Outputs` is an untyped `IReadOnlyDictionary<string,string>`. |
| [src/ThroughlineBuild.Contracts/Verifier/IVerifier.cs](../../src/ThroughlineBuild.Contracts/Verifier/IVerifier.cs) | `IVerifier`, `GitDiff`, `DiffEntry`, `DiffKind` (4 values: Added/Modified/Deleted/Renamed) | |
| [src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs) | `CheckSpec(Name, Executable, Arguments, Timeout)`, `CheckResult(Name, Passed, ExitCode, StdoutTail, StderrTail, Elapsed)` | StdoutTail/StderrTail capped at ~4 KiB. |
| [src/ThroughlineBuild.Contracts/ITicketing.cs](../../src/ThroughlineBuild.Contracts/ITicketing.cs) | `ITicketing` + `TicketComment`, `BackendCapabilities`, `RollupResult`, `NewTicketResult` | `BackendCapabilities` advertised but unused by any caller. |
| [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `IWorkerAgent`, `WorkerOptions(Timeout, AllowedTools?, EnvironmentVariables?, DebugCaptureDirectory?, LiveStdoutSink?, LiveStderrSink?, ProgressDigestSink?)` | |
| [src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs) | `ILlmClient`, `LlmMessage`, `InvocationOptions`, `LlmResponse`, `LlmUsage`, `LlmStreamEvent` hierarchy | `InvokeStreamAsync` is a stub in the only implementation. |
| [src/ThroughlineBuild.Contracts/IGitClient.cs](../../src/ThroughlineBuild.Contracts/IGitClient.cs) | `IGitClient` + 5 result records (`WorktreeInfo`, `WorktreeCreateResult`, `WorktreeRemoveResult`, `GitOpResult`, `RebaseResult`) | ~15 async methods. Some defaults (RemoteExists/GetTrackedChanges/IsAncestor) keep older test fakes working. |
| [src/ThroughlineBuild.Contracts/IEventSink.cs](../../src/ThroughlineBuild.Contracts/IEventSink.cs) | `IEventSink` | |
| [src/ThroughlineBuild.Contracts/IReviewFeedbackRetriever.cs](../../src/ThroughlineBuild.Contracts/IReviewFeedbackRetriever.cs) | `IReviewFeedbackRetriever` | One method: `GetLatestRework(ticketId)`. |
| [src/ThroughlineBuild.Contracts/ITicketCommand.cs](../../src/ThroughlineBuild.Contracts/ITicketCommand.cs) | `ITicketCommand`, `CommandResult(Success, Message?)`, `TicketCommandContext(TicketId, Args)` | Args is `Dictionary<string,string>` - untyped keys, parser-by-convention per command. |

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
- **The Claude Code CLI** (a vendor binary) - this is the worker sibling.
- **The `/ticket-*` slash commands** in `.claude/commands/` (running under the Claude Code harness, defined elsewhere) - these still operate on the same Plane data.

### Plane (`PlaneTicketingClient` ↔ Plane REST API)

What `build` reads that Plane wrote:

- Ticket records (states, labels, comments, relations, parent links). Field names referenced verbatim in [src/ThroughlineBuild.Plane/PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs) - `name`, `description_html`, `state`, `labels`, `parent`, `created_at`, `comment_html`.
- State names hardcoded in the orchestrator ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:163-173](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L163-L173)): `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`. A workspace with different state names would break transitions.

What `build` writes that other systems read:

- HTML descriptions and comments. Plane web UI renders them.
- Markers embedded in comment HTML (`[planned_at: <sha>]`, etc.) - parsed back by `build` itself.
- The `<strong>rollup:</strong>` and `<strong>wontfix:</strong>` / `<strong>deferred:</strong>` / `<strong>reopened:</strong>` prefixes - used by both `build` (`ReopenCommand` scans for them) and operators reading the Plane UI.

The `/ticket-*` slash commands (a separate consumer) also POST comments and PATCH states against the same Plane tickets. They use the same state-name vocabulary but their comment conventions differ (`Investigation`, `Implementation Plan` headings instead of markers in many places). The two flows coexist by being explicit about which phase wrote what - the most recent marker wins for `MarkerParser`-driven lookups.

### Claude Code (`ClaudeCodeAgent` ↔ `claude` CLI)

This is the most evolving contract.

- `build` invokes the CLI with `--print --verbose --output-format stream-json` and optional `--allowedTools` and `--model` flags. Vendor CLI changes to these flags are a known risk (architecture Section 10).
- `build` parses the NDJSON stream and the terminal `type=result` envelope. The envelope shape (`subtype`, `is_error`, `result`, `usage`) is the contract.
- `build` parses the `WORKER_RESULT` marker block produced by the agent. **The agent itself must follow that contract** - the per-phase brief template includes a stub envelope that the worker is instructed to emit ([src/ThroughlineBuild.Briefs/Templates/implement.md](../../src/ThroughlineBuild.Briefs/Templates/implement.md), [review.md](../../src/ThroughlineBuild.Briefs/Templates/review.md), [plan.md](../../src/ThroughlineBuild.Briefs/Templates/plan.md), [draft.md](../../src/ThroughlineBuild.Briefs/Templates/draft.md)).

The brief templates ARE part of the contract. Changing the required `metadata` keys in a template without updating the consuming phase's validation will silently break. Today, validation lives at:

- Plan: [src/ThroughlineBuild.Phases/PlanPhase.cs:111-114](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L111-L114) requires `plan_html`, `risk_label`, `size_label`, `planned_at_sha`.
- Implement: [src/ThroughlineBuild.Phases/ImplementPhase.cs:186-188](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L186-L188) requires `commit_sha`.
- Review: [src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs:73-100](../../src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs#L73-L100) requires `verdict`, `rationale`, `checks_failed`.
- Draft: [src/ThroughlineBuild.Phases/DraftPhase.cs:70-79](../../src/ThroughlineBuild.Phases/DraftPhase.cs#L70-L79) requires `body_markdown`.

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

---

## Conflicts and overlaps

- **State-name vocabulary** is duplicated in three places: `.claude/plane-config.md`, [.build/config.toml.example](../../.build/config.toml.example) comments, and the hardcoded map in [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:163-173](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L163-L173). They agree today but are not enforced to.
- **Workspace / project IDs** appear in `.claude/plane-config.md` AND `.build/config.toml`. Both must be kept in sync by the operator (no auto-sync).
- **Ticket comment formats** differ between flows: claude-config posts headed sections, `build` posts markers. Old comments visible to both flows but only `build` parses markers.
- **`/ticket-ship` vs `build ship`** - both can transition a ticket to `Done`. Only one should be run per ticket; `build ship` is the current direction.

---

## Loose ends

- **`BackendCapabilities`** is never read - capability-driven dispatch is a typed promise without a runtime consumer. Adding a `GitHubTicketingClient` that returns `TypedRelations: false` would not actually disable any code path today.
- **`Phase.Command`** value is in the enum but no phase implementation uses it - it appears to flow through `ITicketCommand` implementations for `WorkflowEvent.Phase` when no specific phase applies.
- **`IReviewFeedbackRetriever`** has one implementation, one production caller, and a thin contract; could be inlined if the rework flow stays single-source.
- **`InvokeStreamAsync` on `ILlmClient`** is part of the contract but stubbed. Any consumer that takes the interface and expects streaming gets `NotImplementedException` at runtime.
- **Cross-`Phase` metadata key conventions** for `PhaseResult.Outputs` and `WorkerResult.Metadata` live only in code comments at the producing site - no shared constants, no type-safe accessor.
- **Old claude-config workflow** is still operative; the deletion contemplated in architecture Section 8 has not happened. Both flows touch the same Plane records.
