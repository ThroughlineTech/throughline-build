# 06 - Public Surfaces

The CLI surface, the exported library interfaces, and the inter-project contracts that anything outside this repo (or any unfamiliar reader inside it) might depend on. Status for each.

For inter-project contracts (records and interfaces) in detail, see [07-contracts.md](07-contracts.md). For verb behavior in detail, see [01-inventory.md](01-inventory.md).

---

## CLI surface

The whole user-facing API of this repository.

```
build <verb> [args] [--debug | --quiet] [--summary-json]

  plan <id>
  implement <id>
  review <id>
  ship <id>
  chain <id>
  rework <id> [--feedback "..."]
  new <body-path | text | -> [--title "..."] [--type "..."] [--label "..."]* [--review]
  new --print-template
  scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings]
  amend <id> [--size S|M|L] [--note "..."]
  close <id> <reason>
  defer <id> <reason>
  reopen <id> [reason]
  help | --help
```

Full usage text: [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs). All verbs are dispatched from [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs).

### Stable contracts on the CLI

These are not just convenience flags - downstream tooling (CI, the operator's other agents, the `analyze-event-log` tool) reads them.

| Contract | Where | Status |
|---|---|---|
| **Exit codes** are deterministic. Global: 0 ok, 1 phase failure, 2 config/unknown verb, 3 missing secret, 4 phase infra failure. Per-verb overrides for `chain`, `rework`, `scaffold` are spelled out in [CliUsage.cs:59-86](../../src/ThroughlineBuild.Cli/CliUsage.cs#L59-L86). | Program.cs throughout | Functional |
| **`--summary-json`** emits a structured JSON object on stdout (in addition to stderr noise) - the schema is the `PhaseSummary` records ([src/ThroughlineBuild.Helpers/PhaseSummary.cs](../../src/ThroughlineBuild.Helpers/PhaseSummary.cs)) rendered by `PhaseSummaryRenderer.RenderJson` ([src/ThroughlineBuild.Helpers/PhaseSummaryRenderer.cs](../../src/ThroughlineBuild.Helpers/PhaseSummaryRenderer.cs)). | per phase | Functional |
| **Default summary text block** is stable per-phase (TLB-123 spec note in `CliUsage.cs:52-57`). Operators redirect it (`build plan TLB-N 2>/dev/null > summary.txt`). | per phase | Functional |
| **`--debug`** captures worker stdio to `.build/sessions/<stem>/` with a stable layout: `worker-stdin.txt`, `worker-stdout.txt`, `worker-stderr.txt`, `envelope-result.txt`, `worker-result.json`, `parse-error.txt`. | per phase | Functional |
| **Progress digest** (default behavior, `[m:ss] kind <payload>`) auto-suppresses when stderr is redirected unless `BUILD_PROGRESS=1`. | Program.cs around lines 657-669 | Functional |
| **Plane comment markers** `[planned_at: <sha>]`, `[implemented_at: <sha>]`, `[shipped_at: <sha>]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>` are load-bearing - downstream phases parse them. | per phase | Functional |

### Conventions the CLI follows

- Always reads `.build/config.toml` from the nearest ancestor directory.
- Always resolves the main worktree root before phase dispatch, so `build` invoked from inside a feature worktree still operates on the right paths.
- Never pushes to a remote.
- Never amends or force-resets anything.
- Single-shot, no daemon, no shared state between invocations.

---

## Exported library surfaces

Each `ThroughlineBuild.X.csproj` library is technically public if referenced by another project. In practice only `ThroughlineBuild.Cli` is consumed by anything outside the solution (it produces the binary). Library projects are private to the solution today.

Below: the interfaces and record types that have the most consumer surface area - the ones that would matter most if anyone wrote a second binary or a plugin against this code.

### `ThroughlineBuild.Contracts`

The leaf of the dependency graph. Pure interfaces, records, enums - no I/O, no static state.

| Type | Purpose | Implementations |
|---|---|---|
| `ITicketing` | All ticket reads / writes / transitions. | `PlaneTicketingClient` (only) |
| `IWorkerAgent` | Spawn an agent subprocess against a `Brief`. | `ClaudeCodeAgent` (only) |
| `ILlmClient` | Direct LLM API call (judgment slot). | `AnthropicClient` (only, streaming stubbed) |
| `IGitClient` | All git subprocess operations. | `ProcessGitClient` (only) |
| `IEventSink` | Sink for `WorkflowEvent`. | `JsonlEventSink`, `RecordingEventSink` |
| `IWorkflowPhase` | `Phase` + `RunAsync(ticketId, workingDirectory, ct) -> PhaseResult`. | `PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `ScaffoldPhase` |
| `IVerifier` | Take a brief + diff + worker result, return a `Verdict`. | `ClaudeCodeReviewer` (only) |
| `IReviewFeedbackRetriever` | Most-recent `Rework` verdict for a ticket from the event log. | `ReviewFeedbackRetriever` |
| `ITicketCommand` | Imperative ticket-touching command (`ExecuteAsync(ctx, ct)`). | `AmendCommand`, `ChainCommand`, `CloseCommand`, `DeferCommand`, `NewCommand`, `ReopenCommand`, `ReworkCommand`, `ScaffoldCommand` |

Records that flow across boundaries: `Ticket`, `Brief`, `WorkerResult`, `Verdict`, `WorkflowEvent`, `PhaseResult`, `GitDiff`, `DiffEntry`, `CheckSpec`, `CheckResult`, `Relation`, `ReviewFeedback`, `BackendCapabilities`, `NewTicketResult`, `TicketComment`, `RollupResult`, `ChainResult`, `ChainStep`, `DraftResult`, `NewResult`. Status: **Functional** - all are immutable records, all are consumed.

Enums: `TicketState`, `Size`, `Risk`, `Phase`, `Status`, `VerdictKind`, `EventKind`, `ChainOutcome`, `DiffKind`, `DraftOutcome`. Status: **Functional** except `Size.S`, `Size.L`, `Risk.Low`, `Risk.High` which are declared but never constructed in production paths.

### `ThroughlineBuild.Phases`

The phase classes are the next-most-public surface - any new orchestrator (e.g., an MCP server) would consume them directly rather than reinvent the orchestration.

| Class | Constructor takes | Returns |
|---|---|---|
| `PlanPhase` | `ITicketing, IWorkerAgent, IEventSink, BuildOptions, IGitClient?, ProjectContext?` | `PlanResult(Success, TicketId, RiskLabel, SizeLabel, PlannedAtSha, FailureReason)` |
| `ImplementPhase` | same + optional `ImplementPhaseOptions` (for rework feedback) | `ImplementResult(Success, TicketId, CommitSha, BranchName, WorktreePath, FailureReason, ReworkRoundNumber)` |
| `ReviewPhase` | same + `ReviewOptions` + optional `IVerifier`/`AutomatedChecksRunner` overrides | `ReviewResult(Success, TicketId, VerdictKind?, VerdictRationale, ChecksFailed[], FailureReason)` |
| `ShipPhase` | `ITicketing, IEventSink, BuildOptions, ShipOptions, IGitClient?, AutomatedChecksRunner?, ConflictMarkerScannerFn?, WorktreeDecrufter?, processPathProvider?` | `ShipResult(Success, TicketId, MergedSha?, FailureReason, FailedAt?)` |
| `ChainPhase` | `ITicketing, IEventSink, BuildOptions, planFactory, implementFactory, reviewFactory, shipFactory, sessionIdGenerator?, workingDirectory?` | `ChainResult(TicketId, Steps[], Outcome, TotalDuration, FinalRationale?)` |
| `ReworkPhase` | `ITicketing, IWorkerAgent, IEventSink, BuildOptions, IReviewFeedbackRetriever, ReworkPhaseOptions, IGitClient?, ProjectContext?` | `ReworkResult(TicketId, Outcome, ImplementResult?, FailureReason, FeedbackSource)` |
| `NewPhase` | `ITicketing, IEventSink, BuildOptions` | `NewResult(Id, Uuid, ValidationWarnings[])` |
| `DraftPhase` | `IWorkerAgent, BuildOptions` | `DraftResult(Outcome, BodyMarkdown?, FailureReason)` |
| `ScaffoldPhase` | `ITicketing, IEventSink, sessionId` | `ScaffoldResult(...)` |

Status: **Functional**. Each phase also implements `IWorkflowPhase` via an explicit interface method that adapts the typed result to a generic `PhaseResult`.

### `ThroughlineBuild.Briefs`

Static `*BriefBuilder.Build(...)` factories. Each loads a Markdown template from embedded resources and substitutes named placeholders.

| Builder | Inputs | Output |
|---|---|---|
| `PlanBriefBuilder.Build(ticket, repoState, projectContext?)` | ticket + main SHA + top-level entries | `Brief(Phase.Plan, ...)` |
| `ImplementBriefBuilder.Build(ticket, repoState, branchName, worktreePath, projectContext?, reviewFeedback?)` | full ticket context + worktree coords + optional prior review feedback | `Brief(Phase.Implement, ...)` |
| `ReviewBriefBuilder.Build(ticket, diff, implementerResult, checkResults, projectContext?)` | ticket + diff with patches + implementer summary + check results | `Brief(Phase.Review, ...)` with patch content budgeted to 50 KiB total |
| `DraftBriefBuilder.Build(operatorText)` | free-form operator text | rendered `string` (caller wraps in a `Brief`) |

Templates live at [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/): `draft.md`, `plan.md`, `implement.md`, `review.md`. Substitution uses `{{key}}` syntax via `TemplateExtensions.Substitute` ([src/ThroughlineBuild.Briefs/TemplateExtensions.cs:5-18](../../src/ThroughlineBuild.Briefs/TemplateExtensions.cs#L5-L18)).

### `ThroughlineBuild.Workers.ClaudeCode`

Public types: `ClaudeCodeAgent` (the `IWorkerAgent`), `ClaudeCodeOptions`, `WorkerResultParser`, `ClaudeCodeJsonEnvelope`, `ClaudeCodeStreamEvent`, `WorkerProgressDigest`, `ClaudeCodeJsonContext` (AOT source-gen).

The `WORKER_RESULT` envelope contract (what a worker must emit at end of its session) is enforced at parse time. Schema:

```json
{
  "status": "Ok | NeedsRework | Failed | Escalate",
  "summary": "string",
  "files_changed": ["string", ...],
  "failure_reason": "string?",
  "metadata": { "key": <JsonElement>, ... }
}
```

The `metadata` dictionary is the extension point for phase-specific fields - `plan_html`, `risk_label`, `size_label`, `planned_at_sha` for plan; `commit_sha` for implement; `verdict`, `rationale`, `checks_failed` for review; `body_markdown` for draft. Required key sets are enforced per-phase, not at parse.

### `ThroughlineBuild.Plane`

Public type: `PlaneTicketingClient` + `PlaneClientOptions` + `PlaneApiException`. Internal model types in `PlaneApiModels.cs`. See [03-external-dependencies.md](03-external-dependencies.md) for endpoint detail.

### `ThroughlineBuild.EventLog`

Public types: `JsonlEventSink`, `RecordingEventSink` (mirrors emissions into memory + exposes `Snapshot()`), `EventLogOptions`, `SessionContext`, `SessionFileNameBuilder`, `ReviewFeedbackRetriever`, `EventLineDto` (`internal sealed class` - not actually public outside the assembly), and `EventLogJsonContext` (AOT source-gen).

### `ThroughlineBuild.Helpers`

Pure helpers + a few I/O-bearing helpers: `ConflictMarkerScanner`, `DocOnlyDetector`, `DriftComparator`, `LlmUsageFlattener`, `MainWorktreeResolver`, `MarkerParser`, `PhaseSummary` + `PhaseSummaryBuilder` + `PhaseSummaryRenderer`, `PhaseWorktreeLayout`, `SlugBuilder`, `WorktreeDecrufter`. `DocOnlyDetector` and `DriftComparator` are tested but unused in production.

### `ThroughlineBuild.Git`

Public: `ProcessGitClient` (implements `IGitClient`), `BaseRefResolver`.

### `ThroughlineBuild.Verification`

Public: `AutomatedChecksRunner`, `ClaudeCodeReviewer` (implements `IVerifier`).

### `ThroughlineBuild.Scaffold`

Public: `ScaffoldPhase`, `OpDocParser`, `OpDocValidator`, `BriefHtmlRenderer`, `ScaffoldOptions`, `ScaffoldResult`, `ParseResult`, `ValidationResult`, plus `OpDoc` / `Plan` / `Brief` / `DispatchEntry` / `OpDocParseError` records in `OpDocTypes.cs`.

### `ThroughlineBuild.Commands`

Public: the `ITicketCommand` implementations and runners listed in [01-inventory.md](01-inventory.md). `TicketCommandRegistry` (a `Dictionary<string, ITicketCommand>` wrapper) is the dispatch surface.

### `ThroughlineBuild.JudgmentSlots`

Public: `ReasonTranslator` only.

### `ThroughlineBuild.Anthropic`

Public: `AnthropicClient` (implements `ILlmClient`), `AnthropicOptions`, `AnthropicApiException`, `AnthropicJsonContext` (AOT source-gen). `InvokeStreamAsync` is part of `ILlmClient` but throws `NotImplementedException`.

---

## Surfaces called out for stability

- **`WORKER_RESULT` envelope JSON schema** is the contract between any future worker agent and the orchestrator. Breaking it breaks every phase that dispatches a worker.
- **Plane marker comment formats** (`[planned_at]`, `[implemented_at]`, `[shipped_at]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>`). Changing any of these strings breaks subsequent phases that read them.
- **JSONL event log line schema** (`EventLineDto`) - the `analyze-event-log` and `token-audit` tools depend on it. Backward-compat is preserved via `[JsonIgnore(WhenWritingNull)]` on the four newer fields.
- **CLI exit code mapping** is the contract any CI workflow relies on.
- **`build new --print-template`** output - some operators script against it.

---

## Loose ends

- **No public-vs-internal distinction.** All types in library projects default to `public` and could be referenced by anything that adds a project reference. There is no NuGet packaging, no API analyzer, no `InternalsVisibleTo` audit.
- **`EventLineDto` is `internal`** to `ThroughlineBuild.EventLog` ([src/ThroughlineBuild.EventLog/EventLineDto.cs:12](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12)) so the on-disk format is technically not a typed public contract - it is documented in [docs/event-log-format.md](../event-log-format.md).
- **Brief template files** are public-by-convention but not by any explicit contract; reorganizing the templates directory would break embedded-resource lookups.
- **MCP server packaging** (architecture Appendix item 3) would create a new public surface; not implemented.
- **No semantic versioning** - the only version exposed at runtime is `Assembly.GetExecutingAssembly().GetName().Version` which is whatever `dotnet publish` decides.
