# 06 - Public Surfaces

The CLI surface, the exported library interfaces, and the inter-project contracts that anything outside this repo (or any unfamiliar reader inside it) might depend on. Status for each.

For inter-project contracts (records and interfaces) in detail, see [07-contracts.md](07-contracts.md). For verb behavior in detail, see [01-inventory.md](01-inventory.md).

---

## CLI surface

The whole user-facing API of this repository.

```
build <verb> [args] [--debug | --quiet] [--summary-json] [--error-location]

  plan <id> [id ...]      [--agent <name>]
  implement <id> [id ...] [--agent <name>]
  review <id> [id ...]    [--agent <name>]
  ship <id> [id ...]      [--no-auto-merge]
  chain <id> [id ...]     [--agent <name>] [--agent-plan <name>] [--agent-implement <name>] [--agent-review <name>] [--no-auto-resolve] [--no-auto-merge] [--continue-past-failure]
  rework <id> [--feedback "..."]
  decompose <id> [--agent <name>]
  new <body-path | text | -> [--title "..."] [--type "..."] [--label "..."]* [--review]
  new --print-template
  scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings]
  init [--force] [--print-template] [--plane-url URL] [--workspace SLUG] [--project-id UUID] [--token TOKEN | --token-env VAR]
  settarget [<branch> | --unset]
  list [--state <name>] [--parent <id>] [--type <name>]
  amend <id> [--size S|M|L] [--note "..."] [--description <path|->] [--ac <path|->]
  close <id> <reason>
  defer <id> <reason>
  reopen <id> [reason]
  --help
```

Full usage text: [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs). All verbs are dispatched from [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs).

`--agent` (and the per-phase `--agent-plan` / `--agent-implement` / `--agent-review` for `chain`) selects which worker agent runs the phase; the name must be a key in the `[workers.<name>]` config sub-table. See [11-llm-architecture.md](11-llm-architecture.md) for the four wired agents and the selection precedence.

### Stable contracts on the CLI

These are not just convenience flags - downstream tooling (CI, the operator's other agents, the `analyze-event-log` tool) reads them.

| Contract | Where | Status |
|---|---|---|
| **Exit codes** are deterministic. Global: 0 ok, 1 phase/command failure, 2 config/unknown verb, 3 missing secret, 4 phase infra failure (review verifier crash, ship worktree missing, git unavailable). Per-verb overrides for `chain`, `rework`, `scaffold` are spelled out in [CliUsage.cs:74-101](../../src/ThroughlineBuild.Cli/CliUsage.cs#L74-L101). The actual chain `ChainOutcome -> exit code` switch is at [Program.cs:1359-1374](../../src/ThroughlineBuild.Cli/Program.cs#L1359-L1374). | Program.cs throughout | Functional |
| **`--summary-json`** emits a structured JSON object on stdout (in addition to stderr noise) - the schema is the `PhaseSummary` records ([src/ThroughlineBuild.Helpers/PhaseSummary.cs](../../src/ThroughlineBuild.Helpers/PhaseSummary.cs)) rendered by `PhaseSummaryRenderer.RenderJson` ([src/ThroughlineBuild.Helpers/PhaseSummaryRenderer.cs](../../src/ThroughlineBuild.Helpers/PhaseSummaryRenderer.cs)). | per phase | Functional |
| **Default summary text block** is stable per-phase (contract spelled out in [CliUsage.cs:67-72](../../src/ThroughlineBuild.Cli/CliUsage.cs#L67-L72)). Operators redirect it (`build plan TLB-N 2>/dev/null > summary.txt`). | per phase | Functional |
| **`--debug`** captures worker stdio to `.build/sessions/<stem>/` with a stable layout: `worker-stdin.txt`, `worker-stdout.txt`, `worker-stderr.txt`, `envelope-result.txt` (or `parse-error.txt` on failure), `worker-result.json`. Non-Claude agents that deliver the brief via args write a placeholder `worker-stdin.txt` and a `worker-result-summary.txt` instead of `envelope-result.txt`; see the per-agent `WriteDebugCapture` methods. `--debug` is a no-op for `ship` (no worker subprocess). | per phase | Functional |
| **Progress digest** (default behavior, `[m:ss] kind <payload>`) auto-suppresses when stderr is redirected unless `BUILD_PROGRESS=1`. The per-line format is produced by each agent's `IWorkerProgressDigester` (null for Copilot, which has no digest). | [Program.cs:839-851](../../src/ThroughlineBuild.Cli/Program.cs#L839-L851) | Functional |
| **Plane comment markers** `[planned_at: <sha>]`, `[implemented_at: <sha>]`, `[decomposed_at: <sha>]`, `[shipped_at: <sha>]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>` are load-bearing - downstream phases parse them. See the marker subsection below. | per phase | Functional |

### Exit codes (full enumeration)

The global mapping (any verb that does not override it):

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Phase or command failure |
| 2 | Config error or unknown verb |
| 3 | Missing secret (env var not set) |
| 4 | Phase infrastructure failure (review verifier crash, ship worktree missing, git unavailable) |

`chain` overrides these with a `ChainOutcome`-keyed switch ([Program.cs:1359-1374](../../src/ThroughlineBuild.Cli/Program.cs#L1359-L1374), enum at [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:3-17](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3-L17)):

| Code | ChainOutcome |
|---|---|
| 0 | `Completed`, `RatifiedObsolete`, `ParentCompleted` |
| 2 | `RefusedInitialState`, `ParentHasGrandchildren` |
| 3 | `StoppedAtPlan`, `ParentStoppedEarly`, `Skipped` |
| 4 | `StoppedAtImplement` |
| 5 | `StoppedAtReview` |
| 6 | `ReworkCapExceeded` |
| 7 | `StoppedAtShip` |

`Skipped` (TLB-313) is the outcome for a ticket whose ancestor failed when `--continue-past-failure` is not set; in the multi-ticket aggregate path a `Skipped` result still counts as "all good" for the overall exit code ([Program.cs:1338-1343](../../src/ThroughlineBuild.Cli/Program.cs#L1338-L1343)). `rework` overrides codes 2/4 and `scaffold` overrides codes 2/3 - see [CliUsage.cs:91-101](../../src/ThroughlineBuild.Cli/CliUsage.cs#L91-L101).

### Conventions the CLI follows

- Always reads `.build/config.toml` from the nearest ancestor directory.
- Always resolves the main worktree root before phase dispatch, so `build` invoked from inside a feature worktree still operates on the right paths.
- `ship` pushes the merge target to the configured remote after a fast-forward merge (no other verb pushes); see [05-state-and-persistence.md](05-state-and-persistence.md).
- Never amends or force-resets anything (no `git push --force`, `git reset --hard`, or interactive rebase anywhere).
- Single-shot, no daemon, no shared state between invocations.

### Loose ends (CLI surface)

- The `init` verb is implemented (writes `.build/config.toml` from a template) - it is no longer the aspirational `install` verb the older docs reference.
- The `decompose` verb is a first-class dispatched verb ([Program.cs:704](../../src/ThroughlineBuild.Cli/Program.cs#L704)) but is not invoked through an `ITicketCommand`; it runs `DecomposePhase` directly.
- `--agent` selection names are validated against config sub-tables at construction time; an unknown name surfaces as a `ConfigException` from `WorkerAgentFactory.Create`, not a usage error.

---

## Exported library surfaces

Each `ThroughlineBuild.X.csproj` library is technically public if referenced by another project. In practice only `ThroughlineBuild.Cli` is consumed by anything outside the solution (it produces the binary). Library projects are private to the solution today.

Below: the interfaces and record types that have the most consumer surface area - the ones that would matter most if anyone wrote a second binary or a plugin against this code.

### `ThroughlineBuild.Contracts`

The leaf of the dependency graph. Pure interfaces, records, enums - no I/O, no static state.

| Type | Purpose | Implementations |
|---|---|---|
| `ITicketing` | All ticket reads / writes / transitions. | `PlaneTicketingClient` (only) |
| `IWorkerAgent` | Spawn an agent subprocess against a `Brief`; exposes `Name` and an optional `IWorkerProgressDigester`. | `ClaudeCodeAgent`, `CodexAgent`, `GeminiAgent`, `CopilotAgent` - all wired (see [11-llm-architecture.md](11-llm-architecture.md)) |
| `IWorkerAgentFactory` | Resolve a configured agent name to an `IWorkerAgent`. | `WorkerAgentFactory` (Cli; registry-backed) |
| `IWorkerProgressDigester` | Format one raw NDJSON line into a one-line digest (best-effort, never throws). | `ClaudeCodeProgressDigester`, `CodexProgressDigester`, `GeminiProgressDigester` (Copilot returns null) |
| `ILlmClient` | Direct LLM API call (judgment slot). | `AnthropicClient` (production, non-streaming, `InvokeStreamAsync` stubbed); `ModelClientLlmAdapter` exists but is unwired |
| `IGitClient` | All git subprocess operations. | `ProcessGitClient` (only) |
| `IEventSink` | Sink for `WorkflowEvent`. | `JsonlEventSink`, `RecordingEventSink` |
| `IWorkflowPhase` | `Phase` + `RunAsync(ticketId, workingDirectory, ct) -> PhaseResult`. | `PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `DecomposePhase`, `ScaffoldPhase` |
| `IVerifier` | Take a brief + diff + worker result, return a `Verdict`. | `WorkerAgentReviewer` (only) - agent-agnostic; wraps any injected `IWorkerAgent` |
| `IObsoleteRatifier` | Verify an `obsolete` escalation against the prior commit's evidence. | `ObsoleteRatifier` |
| `IReviewFeedbackRetriever` | Most-recent `Rework` verdict for a ticket from the event log. | `ReviewFeedbackRetriever` |
| `ITicketCommand` | Imperative ticket-touching command (`ExecuteAsync(ctx, ct)`). | `AmendCommand`, `ChainCommand`, `CloseCommand`, `DeferCommand`, `ListCommand`, `NewCommand`, `ReopenCommand`, `ReworkCommand`, `ScaffoldCommand` |

The four `IWorkerAgent` implementations live in their own `ThroughlineBuild.Workers.<Vendor>` projects ([ClaudeCodeAgent.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs), [CodexAgent.cs](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs), [GeminiAgent.cs](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs), [CopilotAgent.cs](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs)) and share the `WORKER_RESULT` parser in `ThroughlineBuild.Workers.Common`. `IVerifier`, `IObsoleteRatifier`, and `CheckResult` live under [src/ThroughlineBuild.Contracts/Verifier/](../../src/ThroughlineBuild.Contracts/Verifier/); the model records live under [src/ThroughlineBuild.Contracts/Models/](../../src/ThroughlineBuild.Contracts/Models/).

Records that flow across boundaries: `Ticket`, `Brief`, `WorkerResult`, `WorkerOptions`, `WorkerSize`, `Verdict`, `WorkflowEvent`, `PhaseResult`, `GitDiff`, `DiffEntry`, `CheckSpec`, `CheckResult`, `Relation`, `ReviewFeedback`, `BackendCapabilities`, `NewTicketResult`, `TicketComment`, `RollupResult`, `ChainResult`, `ChainStep`, `DraftResult`, `NewResult`, `SubsumedByEvidence`, `ParallelDispatchResult`, `TicketGraph` / `TicketNode`. Status: **Functional** - all are immutable records, all are consumed.

Enums: `TicketState`, `Size`, `Risk`, `Phase`, `Status`, `VerdictKind`, `EventKind`, `ChainOutcome`, `DiffKind`, `DraftOutcome`, `WorkerSize`. Status: **Functional** except `Size.S`, `Size.L`, `Risk.Low`, `Risk.High` which are declared but never constructed in production paths. `EventKind` now has 13 members (see the JSONL schema section). `Status` has four members: `Ok`, `NeedsRework`, `Failed`, `Escalate` ([src/ThroughlineBuild.Contracts/Models/WorkerResult.cs:10](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs#L10)). `WorkerSize` (`Small`/`Medium`/`Large`, [src/ThroughlineBuild.Contracts/Models/WorkerSize.cs:8-13](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs#L8-L13)) is the worker-domain size signal, distinct from the ticket-domain `Size`.

### `ThroughlineBuild.Phases`

The phase classes are the next-most-public surface - any new orchestrator (e.g., an MCP server) would consume them directly rather than reinvent the orchestration.

| Class | Constructor takes | Returns |
|---|---|---|
| `PlanPhase` | `ITicketing, IWorkerAgent, IEventSink, BuildOptions, IGitClient?, ProjectContext?` | `PlanResult(Success, TicketId, RiskLabel, SizeLabel, PlannedAtSha, FailureReason)` |
| `ImplementPhase` | same + optional `ImplementPhaseOptions` (for rework feedback) | `ImplementResult(Success, TicketId, CommitSha, BranchName, WorktreePath, FailureReason, ReworkRoundNumber)` |
| `ReviewPhase` | same + `ReviewOptions` + optional `IVerifier`/`AutomatedChecksRunner` overrides | `ReviewResult(Success, TicketId, VerdictKind?, VerdictRationale, ChecksFailed[], FailureReason)` |
| `ShipPhase` | `ITicketing, IEventSink, BuildOptions, ShipOptions (now carries `TargetBranch`), IGitClient?, AutomatedChecksRunner?, ConflictMarkerScannerFn?, WorktreeDecrufter?, processPathProvider?, TextWriter? progressWriter, bool verbose` | `ShipResult(Success, TicketId, MergedSha?, FailureReason, FailedAt?)` |
| `ChainPhase` | `ITicketing, IEventSink, BuildOptions, planFactory, implementFactory, reviewFactory, shipFactory, sessionIdGenerator?, workingDirectory?` | `ChainResult(TicketId, Steps[], Outcome, TotalDuration, FinalRationale?)` |
| `ReworkPhase` | `ITicketing, IWorkerAgent, IEventSink, BuildOptions, IReviewFeedbackRetriever, ReworkPhaseOptions, IGitClient?, ProjectContext?` | `ReworkResult(TicketId, Outcome, ImplementResult?, FailureReason, FeedbackSource)` |
| `NewPhase` | `ITicketing, IEventSink, BuildOptions` | `NewResult(Id, Uuid, ValidationWarnings[])` |
| `DraftPhase` | `IWorkerAgent, BuildOptions` | `DraftResult(Outcome, BodyMarkdown?, FailureReason)` |
| `DecomposePhase` | `ITicketing, IWorkerAgent, IEventSink, BuildOptions, IGitClient?, ProjectContext?` | decompose result (writes a `[decomposed_at: <sha>]` marker; see [src/ThroughlineBuild.Phases/DecomposePhase.cs](../../src/ThroughlineBuild.Phases/DecomposePhase.cs)) |

`ScaffoldPhase` lives in `ThroughlineBuild.Scaffold`, not `ThroughlineBuild.Phases` (see that project's surface below). Status: **Functional**. Each phase also implements `IWorkflowPhase` via an explicit interface method that adapts the typed result to a generic `PhaseResult`. `ThroughlineBuild.Phases` also exposes the parallel/dependency-ordered chain machinery (`ParallelDispatcher`, `AncestorSkipFilter`, `EarlyExitManifest`, `TicketGraph`).

### `ThroughlineBuild.Briefs`

Static `*BriefBuilder.Build(...)` factories. Each takes an `agentName` first argument, loads the matching per-agent Markdown template from embedded resources, and substitutes named placeholders. There is now a `DecomposeBriefBuilder` alongside the others.

| Builder | Inputs | Output |
|---|---|---|
| `PlanBriefBuilder.Build(agentName, ticket, repoState, projectContext?)` | agent + ticket + main SHA + top-level entries | `Brief(Phase.Plan, ...)` |
| `ImplementBriefBuilder.Build(agentName, ticket, repoState, branchName, worktreePath, projectContext?, reviewFeedback?)` | agent + full ticket context + worktree coords + optional prior review feedback | `Brief(Phase.Implement, ...)` |
| `ReviewBriefBuilder.Build(agentName, ticket, diff, implementerResult, checkResults, projectContext?)` | agent + ticket + diff with patches + implementer summary + check results | `Brief(Phase.Review, ...)` with patch content budgeted |
| `DecomposeBriefBuilder.Build(agentName, ...)` | agent + ticket context | `Brief(Phase.Decompose-equivalent, ...)` |
| `DraftBriefBuilder.Build(agentName, operatorText)` | agent + free-form operator text | rendered `string` (caller wraps in a `Brief`) |

The caller passes `_worker.Name` as `agentName` so the brief matches the dispatched agent (e.g. [PlanPhase.cs:83](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L83)). Templates live under per-agent subdirectories at [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/): `claude-code/`, `codex/`, `gemini/`, `copilot/`, each holding `plan.md`, `implement.md`, `review.md`, `decompose.md`, `draft.md`. `TemplateLoader.Load(agentName, templateName)` ([src/ThroughlineBuild.Briefs/TemplateLoader.cs:14-33](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs#L14-L33)) resolves the embedded resource (hyphens in the agent name map to underscores in the resource path). Substitution uses `{{key}}` syntax via `TemplateExtensions.Substitute` ([src/ThroughlineBuild.Briefs/TemplateExtensions.cs:5-18](../../src/ThroughlineBuild.Briefs/TemplateExtensions.cs#L5-L18)). The directory layout and resource-name mapping are a public-by-convention surface: renaming a subdirectory breaks the embedded-resource lookup at runtime.

### `ThroughlineBuild.Workers.Common`

This is the shared worker surface. Public type: nothing exported - the `WORKER_RESULT` parser is `internal`. The `WorkerResultParser` class moved here from `Workers.ClaudeCode` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)) and is `internal static` - it is exposed to the four worker assemblies and their test assemblies via `InternalsVisibleTo` ([ThroughlineBuild.Workers.Common.csproj:13-37](../../src/ThroughlineBuild.Workers.Common/ThroughlineBuild.Workers.Common.csproj#L13-L37)), not to the wider solution. All four agents call it to scan their output for the envelope.

The `WORKER_RESULT` envelope contract (what a worker must emit at the end of its session) is documented and enforced at parse time in [WorkerResultParser.cs:38-70](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L38-L70). Shape:

```json
{
  "status": "Ok | NeedsRework | Failed | Escalate",
  "summary": "string (required, non-empty)",
  "files_changed": ["string", ...],
  "failure_reason": "string?",
  "metadata": { "key": <JsonElement>, ... }
}
```

Parser rules ([WorkerResultParser.cs:73-174](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L73-L174)): the literal marker line `WORKER_RESULT` precedes the JSON payload (optionally fenced in triple backticks, with or without a `json` tag); multiple markers are tolerated and the LAST valid envelope wins (the first is often a template echo); `status` and a non-empty `summary` are required (a missing `status` fails loudly rather than defaulting to `Ok`).

**Fenced-block payload protocol (op-27, TLB-333/334).** Large markdown payloads (plan bodies, implement summaries, review critiques, draft bodies) are no longer JSON-string fields - the JSON envelope grew brittle when bodies contained quotes/newlines. Instead the worker emits named fenced blocks *before* the `WORKER_RESULT` marker, delimited by `<<<NAME_START` / `<<<NAME_END` (block names must match `^[A-Z][A-Z0-9_]*$`), and the envelope references them by a `*_ref` metadata field. The parser runs a **fenced-block pre-pass** over stdout up to the marker, captures the blocks into a `Dictionary<string,string>`, and returns them alongside the parsed result; `FencedBlockResolver.TryResolveRef(blocks, metadata, "<field>_ref", out content, out error)` later resolves a ref field to its block body. The per-phase block names and ref fields:

| Phase | Block name | Metadata ref field | Consumer |
|---|---|---|---|
| plan | `PLAN_BODY` | `plan_body_ref` | `PlanPhase` -> `MarkdownRenderer.Render` -> Plane description |
| implement | `IMPLEMENT_SUMMARY` | `summary_ref` | `ImplementPhase` -> rendered HTML comment (optional) |
| review | `REVIEW_CRITIQUE` | `rationale_ref` | `WorkerAgentReviewer` -> `Verdict.Rationale` (falls back to direct `rationale`) |
| draft | `DRAFT_BODY` | `body_markdown_ref` | `DraftPhase` (falls back to legacy `body_markdown`) |

`metadata` is otherwise the extension point for phase-specific scalar fields - `risk_label`, `size_label`, `planned_at_sha` for plan; `commit_sha` for implement; `verdict`, `checks_failed` for review. Required key sets are enforced per-phase, not at parse. The draft and review consumers retain backward-compatible fallbacks to the pre-op-27 direct-string fields.

**`MarkdownRenderer` (TLB-335).** Resolved block bodies are markdown; `MarkdownRenderer.Render` ([src/ThroughlineBuild.Workers.Common/MarkdownRenderer.cs](../../src/ThroughlineBuild.Workers.Common/MarkdownRenderer.cs)) turns them into the HTML Plane stores. It is a hand-rolled CommonMark *subset* (headings, paragraphs, fenced/inline code, ordered+unordered lists, bold/italic, links, with HTML escaping) chosen over Markdig to stay AOT-safe with zero reflection. Constructs outside the subset (tables, blockquotes, strikethrough) pass through as literal text.

Two `metadata` keys are parsed structurally:

- `metadata.escalation` (TLB-278): present when `status == Escalate` and the worker wants to convey structured escalation context. When `escalation.reason == "obsolete"` the parser requires a `subsumed_by` object with non-empty `commit` (string), `files` (non-empty array), and `rationale` (string), or the parse fails with a `ValidationError`. Unknown reasons pass through without a `subsumed_by` check ([WorkerResultParser.cs:123-150](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L123-L150)). Downstream, `ObsoleteRatifier` and `ChainPhase` consume `subsumed_by.commit` / `files` / `rationale` to decide whether to auto-resolve the ticket ([src/ThroughlineBuild.Verification/ObsoleteRatifier.cs:88-103](../../src/ThroughlineBuild.Verification/ObsoleteRatifier.cs#L88-L103)).
- `metadata.llm_usage`: each agent merges this dictionary onto the parsed result. It carries `model`, `vendor`, `wall_clock_ms`, token counts, and (when the vendor reports it) `cost_usd`. The `vendor` string is per-agent: `anthropic` (Claude Code), `openai` (Codex), `google` (Gemini), `github` (Copilot). See [11-llm-architecture.md](11-llm-architecture.md) for the per-agent capture detail.

### `ThroughlineBuild.Workers.ClaudeCode`

Public types: `ClaudeCodeAgent` (the `IWorkerAgent`), `ClaudeCodeOptions`, `ClaudeCodeJsonEnvelope`, `ClaudeCodeStreamEvent`, `ClaudeCodeProgressDigester` (the public `IWorkerProgressDigester`; `WorkerProgressDigest` is now an internal static helper it delegates to), `ClaudeCodeJsonContext` (AOT source-gen). `WorkerResultParser` no longer lives here - it moved to `Workers.Common` (above).

### `ThroughlineBuild.Workers.Codex` / `.Gemini` / `.Copilot`

Each exports its `IWorkerAgent` plus an `*Options` record and AOT JSON DTO/context: `CodexAgent` + `CodexOptions` + `CodexProgressDigester`; `GeminiAgent` + `GeminiOptions` + `GeminiProgressDigester` (+ `GeminiResultEnvelope`); `CopilotAgent` + `CopilotOptions` (no digester - `CopilotAgent.Digester` returns null). All four emit the same `WORKER_RESULT` envelope contract through `Workers.Common`.

### `ThroughlineBuild.Plane`

Public type: `PlaneTicketingClient` + `PlaneClientOptions` + `PlaneApiException`. Internal model types in `PlaneApiModels.cs`. See [03-external-dependencies.md](03-external-dependencies.md) for endpoint detail.

### `ThroughlineBuild.EventLog`

Public types: `JsonlEventSink`, `RecordingEventSink` (mirrors emissions into memory + exposes `Snapshot()`), `EventLogOptions`, `SessionContext`, `SessionFileNameBuilder`, `ReviewFeedbackRetriever`, `EventLineDto` (`internal sealed class` - not actually public outside the assembly), and `EventLogJsonContext` (AOT source-gen).

#### JSONL event-log schema

Each line is a serialized `EventLineDto` ([src/ThroughlineBuild.EventLog/EventLineDto.cs:12-36](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12-L36)) wrapping a `WorkflowEvent` ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:3-9](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L3-L9)). The six original fields keep their PascalCase names (`SessionId`, `Timestamp`, `Kind`, `TicketId`, `Phase`, `Data`); the four newer session-level fields are snake_case and `[JsonIgnore(WhenWritingNull)]`, so a sink without a `SessionContext` emits the pre-TLB-147 shape unchanged: `project_id`, `project_name`, `workspace_slug`, `build_version`.

`Kind` serializes as the integer ordinal of `EventKind`. The enum has grown to 13 members ([WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14)); the ordinals are load-bearing for the `analyze-event-log` and `token-audit` tools:

| Ordinal | EventKind |
|---|---|
| 0 | `StateTransition` |
| 1 | `LlmCall` |
| 2 | `WorkerSpawn` |
| 3 | `VerifierVerdict` |
| 4 | `GateFailure` |
| 5 | `TicketWrite` |
| 6 | `ChainStart` |
| 7 | `ChainEnd` |
| 8 | `ReworkRound` |
| 9 | `TicketSubsumed` |
| 10 | `TargetAutoRebased` (renamed from `MainAutoRebased`; ordinal unchanged) |
| 11 | `DispatchStart` |
| 12 | `DispatchEnd` |

`DispatchStart` / `DispatchEnd` (11/12) frame parallel multi-ticket chain dispatch. `Data` is an arbitrary `IReadOnlyDictionary<string, object>` whose contents are per-`Kind` and not statically typed. The format is documented in [docs/event-log-format.md](../event-log-format.md).

### `ThroughlineBuild.Helpers`

Pure helpers + a few I/O-bearing helpers: `ConflictMarkerScanner`, `DocOnlyDetector`, `DriftComparator`, `LlmUsageFlattener`, `MainWorktreeResolver`, `MarkerParser`, `PhaseSummary` + `PhaseSummaryBuilder` + `PhaseSummaryRenderer`, `PhaseWorktreeLayout`, `SlugBuilder`, `WorktreeDecrufter`. `DocOnlyDetector` and `DriftComparator` are tested but unused in production.

### `ThroughlineBuild.Git`

Public: `ProcessGitClient` (implements `IGitClient`), `BaseRefResolver`.

### `ThroughlineBuild.Verification`

Public: `AutomatedChecksRunner`, `WorkerAgentReviewer` (implements `IVerifier`; renamed from the former `ClaudeCodeReviewer` - it is agent-agnostic and wraps whatever `IWorkerAgent` the caller injects, [src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs:14-39](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L14-L39)), `ObsoleteRatifier` (implements `IObsoleteRatifier`).

### `ThroughlineBuild.Scaffold`

Public: `ScaffoldPhase`, `OpDocParser`, `OpDocValidator`, `BriefHtmlRenderer`, `ScaffoldOptions`, `ScaffoldResult`, `ParseResult`, `ValidationResult`, plus `OpDoc` / `Plan` / `Brief` / `DispatchEntry` / `OpDocParseError` records in `OpDocTypes.cs`.

### `ThroughlineBuild.Commands`

Public: the `ITicketCommand` implementations and runners listed in [01-inventory.md](01-inventory.md). `TicketCommandRegistry` (a `Dictionary<string, ITicketCommand>` wrapper) is the dispatch surface. Also exposes the chain dispatch machinery (`IChainRunner` / `DefaultChainRunner`, `IReworkRunner` / `DefaultReworkRunner`, `SequentialChainDispatcher`) and `ListCommand`.

### `ThroughlineBuild.JudgmentSlots`

Public: `ReasonTranslator` only. It is constructed with an `ILlmClient`; its default model id is a `const` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)), with a second constructor accepting a model-id override.

### `ThroughlineBuild.ModelClient`

Public: `IModelClient` (`SendAsync` + `StreamAsync`), `ProviderConfig`, the `ModelRequest` / `ModelMessage` / `ContentBlock` (`TextContent`, `ToolUseContent`, `ToolResultContent`) / `ToolDefinition` request records, the `ModelResponse` / `Usage` / `ModelStreamEvent` hierarchy, `UsageMapper`, `ModelClientJsonContext`. This is a newer, richer LLM-call abstraction than `ILlmClient` (multi-block content, tool definitions, real streaming events, vendor-tagged usage with optional cost). It is **Partial as a public surface**: built and unit-tested, but no production path constructs an `IModelClient` yet. See [11-llm-architecture.md](11-llm-architecture.md).

### `ThroughlineBuild.Anthropic`

Public: `AnthropicClient` (implements `ILlmClient`, production), `AnthropicOptions`, `AnthropicApiException`, the `Anthropic*` API-model records, `AnthropicJsonContext` (AOT source-gen). `AnthropicClient.InvokeStreamAsync` is part of `ILlmClient` but throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

This project also holds two newer types that target the `IModelClient` abstraction: `AnthropicModelClient` (real SSE streaming via `StreamAsync`, TLB-244/245) and `ModelClientLlmAdapter` (wraps an `IModelClient` and presents it as an `ILlmClient`). Both are tested but **unwired** - nothing on the production path constructs them; the judgment-slot path still builds `AnthropicClient` directly via `LlmClientFactory`. `ModelClientLlmAdapter.InvokeStreamAsync` also throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs:65-72](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65-L72)).

---

## Surfaces called out for stability

- **`WORKER_RESULT` envelope JSON schema** is the contract between every worker agent and the orchestrator (all four agents emit it; the parser lives in `Workers.Common`). Breaking it breaks every phase that dispatches a worker. The `metadata.escalation` / `subsumed_by` sub-schema and the `metadata.llm_usage` shape are part of this contract.
- **Fenced-block payload protocol** (`<<<NAME_START`/`<<<NAME_END` markers + `*_ref` metadata fields, spec in `docs/op-docs/op-27-worker-result-fenced-payloads.md`) is now part of the worker contract for plan/implement/review/draft bodies; the per-agent brief templates emit it and `FencedBlockResolver` consumes it. The `MarkdownRenderer` CommonMark-subset is the rendering contract for those bodies into Plane HTML.
- **Plane marker comment formats** (`[planned_at: <sha>]`, `[implemented_at: <sha>]`, `[decomposed_at: <sha>]`, `[shipped_at: <sha>]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>`). Changing any of these strings breaks subsequent phases / commands that read them (e.g. `ReopenCommand` keys off the prior `deferred:` / `wontfix:` marker). Markers are emitted as HTML `<p>`/`<strong>` and parsed back through `MarkerParser` after HTML-tag stripping ([src/ThroughlineBuild.Helpers/MarkerParser.cs:8](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L8)).
- **JSONL event log line schema** (`EventLineDto`, including the `EventKind` integer ordinals) - the `analyze-event-log` and `token-audit` tools depend on it. Backward-compat is preserved via `[JsonIgnore(WhenWritingNull)]` on the four newer fields.
- **CLI exit code mapping** is the contract any CI workflow relies on - including the `ChainOutcome` overrides for `chain`.
- **`build new --print-template`** output - some operators script against it.
- **Per-agent template directory layout** under `Templates/<agent>/` - renaming an agent subdirectory breaks the embedded-resource lookup in `TemplateLoader`.

---

## Loose ends

- **Mostly no public-vs-internal distinction.** Most types in library projects default to `public` and could be referenced by anything that adds a project reference. There is no NuGet packaging and no API analyzer. The exceptions are `Workers.Common` (uses `InternalsVisibleTo` to share its `internal` `WorkerResultParser` with the four worker assemblies + their tests) and `EventLog` (`EventLineDto` is `internal`).
- **`WorkerResultParser` is `internal`** in `ThroughlineBuild.Workers.Common`, not a public type. Code outside the worker assemblies cannot call it directly; the WORKER_RESULT contract it enforces is the durable surface, not the class.
- **`EventLineDto` is `internal`** to `ThroughlineBuild.EventLog` ([src/ThroughlineBuild.EventLog/EventLineDto.cs:12](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12)) so the on-disk format is technically not a typed public contract - it is documented in [docs/event-log-format.md](../event-log-format.md).
- **The `IModelClient` surface is built but unwired.** `IModelClient` / `AnthropicModelClient` / `ModelClientLlmAdapter` are public and tested, but no production code constructs them. If they are wired onto the judgment-slot path later, that is a new live public surface; until then it is dead-public.
- **Brief template files** are public-by-convention but not by any explicit contract; reorganizing the per-agent templates directory would break embedded-resource lookups.
- **MCP server packaging** (architecture Appendix item 3) would create a new public surface; not implemented.
- **No semantic versioning** - the only version exposed at runtime is `Assembly.GetExecutingAssembly().GetName().Version` which is whatever `dotnet publish` decides.
