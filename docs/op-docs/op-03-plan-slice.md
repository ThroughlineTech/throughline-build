# Operation: build-plan-slice

First end-to-end vertical: `build plan <id>` runs from a terminal against a real Plane workspace, dispatches Claude Code as a worker, posts results back to Plane. Validates the architecture and establishes the cost baseline against the existing slash command.

## Why this exists

The architecture is unproven until one phase runs end-to-end against real services. This op-doc implements the smallest meaningful slice: the plan phase (which produces an investigation plan for a ticket, moving it Backlog -> Ready). It exercises every architectural seam (ITicketing via Plane, ILlmClient via Anthropic, IWorkerAgent via Claude Code, IEventSink via JSONL). When this slice runs successfully and produces output equivalent to the existing `/ti` slash command at lower cost, the architecture is validated. When the slice surprises us, the architecture gets revised before the rest of the phases are built.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | External clients | - | L |
| B    | Plan phase composition | A | M |

Plan A implements the concrete clients against the contracts. Plan B composes them into the actual plan flow with state machine subset and CLI entry. Plan B cannot start without Plan A's clients available; within Plan A, the four briefs are independent and can run in any order or in parallel.

## Plan A: External clients

### Goal

Four concrete implementations of the interfaces from op-doc 2: PlaneTicketingClient, AnthropicClient, ClaudeCodeAgent, and JsonlEventSink. Each is its own class library project. Each is unit-tested where possible (mocked HttpClient, fixture subprocess) and integration-validated against real services during the vertical-slice run.

Brief sequence: B01-B04 are independent. Run all in parallel.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | plane-client | Implement ITicketing against Plane REST API | - | src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, src/ThroughlineBuild.Plane/PlaneClientOptions.cs, src/ThroughlineBuild.Plane/PlaneApiModels.cs |
| 02 | anthropic-client | Implement ILlmClient against Anthropic Messages API | - | src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj, src/ThroughlineBuild.Anthropic/AnthropicClient.cs, src/ThroughlineBuild.Anthropic/AnthropicOptions.cs |
| 03 | claude-code-agent | Implement IWorkerAgent that spawns `claude --print` | - | src/ThroughlineBuild.Workers.ClaudeCode/ThroughlineBuild.Workers.ClaudeCode.csproj, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs |
| 04 | event-log | Implement IEventSink as JSONL writer | - | src/ThroughlineBuild.EventLog/ThroughlineBuild.EventLog.csproj, src/ThroughlineBuild.EventLog/JsonlEventSink.cs, src/ThroughlineBuild.EventLog/EventLogOptions.cs |

### Briefs - detail

#### Brief 01: plane-client

Goal: Implement ITicketing against the Plane REST API for the subset of operations the plan phase needs: GetAsync, TransitionAsync, AppendDescriptionAsync, CreateCommentAsync, ApplyLabelsAsync, GetRelationsAsync.

Inputs:
- ITicketing interface from `ThroughlineBuild.Contracts`
- Plane REST API public documentation: https://docs.plane.so/api-reference/introduction
- Polly NuGet package for retry policies
- HttpClient (stdlib)
- Configuration values: PLANE_API_BASE_URL, PLANE_API_TOKEN, PLANE_WORKSPACE_SLUG, PLANE_PROJECT_ID (token from env, others from config file)

Outputs:
- `src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj` (classlib)
- `src/ThroughlineBuild.Plane/PlaneTicketingClient.cs` implementing ITicketing
- `src/ThroughlineBuild.Plane/PlaneClientOptions.cs` for configuration (BaseUrl, WorkspaceSlug, ProjectId, ApiToken)
- `src/ThroughlineBuild.Plane/PlaneApiModels.cs` for wire-format DTOs (separate from Contracts records)
- xUnit tests using mocked HttpClient covering: get success, get not-found, transition success, append-description success, label apply, retry on 429, retry on 5xx, auth failure

Acceptance:
- [ ] All ITicketing methods listed implemented
- [ ] Configuration loaded from PlaneClientOptions (constructor injection)
- [ ] HTTP errors wrapped in PlaneApiException with Status and Body
- [ ] Polly retry policy on 429 and 5xx (exponential backoff, max 3 retries)
- [ ] Capabilities returns `(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)`
- [ ] Wire DTOs separate from Contracts records; explicit translation at the boundary
- [ ] Authorization via `X-API-Key` header per Plane spec
- [ ] State name to UUID resolution cached after first fetch
- [ ] xUnit tests pass
- [ ] AOT-friendly JSON via `JsonSerializerContext` source generator

Notes: Plane state IDs are workspace-specific. TransitionAsync must resolve the state name (e.g., "Ready") to the workspace's state UUID, cached. The HTML body for AppendDescriptionAsync should be the rendered HTML Plane's editor expects (single-line ULs/OLs).

OOS:
- Do not implement methods beyond what the plan phase needs
- Do not implement other backends (no GitHub)
- Do not add CLI parsing here (Plan B)
- Do not preserve any base64 round-trip pattern from any prior system
- Do not read any `bin/plane-rest` shell script from elsewhere

#### Brief 02: anthropic-client

Goal: Implement ILlmClient against the Anthropic Messages API. Single InvokeAsync first; InvokeStreamAsync may throw NotImplementedException for now.

Inputs:
- ILlmClient interface from `ThroughlineBuild.Contracts`
- Anthropic Messages API public documentation: https://docs.anthropic.com/en/api/messages
- HttpClient + Polly
- Configuration: ANTHROPIC_API_KEY (from env)

Outputs:
- `src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj` (classlib)
- `src/ThroughlineBuild.Anthropic/AnthropicClient.cs` implementing ILlmClient
- `src/ThroughlineBuild.Anthropic/AnthropicOptions.cs` (ApiKey, ApiVersion = "2023-06-01", BaseUrl = "https://api.anthropic.com")
- xUnit tests with mocked HttpClient covering: success, error response, retry on 429, retry on 5xx, missing API key

Acceptance:
- [ ] InvokeAsync implemented; sends a Messages API request and returns LlmResponse
- [ ] Model ID format `anthropic:claude-XXX` parsed; the part after `anthropic:` sent as the API model name
- [ ] LlmUsage populated from Anthropic's usage block (input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens)
- [ ] InvokeStreamAsync stubbed to throw NotImplementedException with a clear message
- [ ] API key from AnthropicOptions; never hardcoded
- [ ] HTTP errors wrapped in AnthropicApiException
- [ ] Polly retry on 429 and 5xx
- [ ] Required headers: `x-api-key`, `anthropic-version`, `content-type: application/json`
- [ ] xUnit tests pass
- [ ] AOT-friendly JSON via source generators

Notes: Use `JsonSerializerContext` source generation; the Messages API request and response shapes are stable and small.

OOS:
- Do not implement streaming
- Do not implement tool use
- Do not implement other vendors
- Do not import any old prompt corpus

#### Brief 03: claude-code-agent

Goal: Implement IWorkerAgent that spawns the Claude Code CLI in non-interactive mode and captures the structured WORKER_RESULT.

Inputs:
- IWorkerAgent interface from `ThroughlineBuild.Contracts`
- Claude Code CLI documentation: run `claude --help`, see https://docs.claude.com/en/docs/claude-code
- System.Diagnostics.Process for spawning
- Knowledge of the WORKER_RESULT envelope: a fenced JSON block in stdout containing Status, Summary, FilesChanged, FailureReason, Metadata

Outputs:
- `src/ThroughlineBuild.Workers.ClaudeCode/ThroughlineBuild.Workers.ClaudeCode.csproj` (classlib)
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs` implementing IWorkerAgent with `Name = "claude-code"`
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs` (ExecutablePath default "claude", extra args, working dir defaults)
- xUnit tests using a stub script (echoes a fixed WORKER_RESULT) covering: success, timeout, malformed envelope, non-zero exit, cancellation

Acceptance:
- [ ] ExecuteAsync spawns `claude --print` (or current equivalent) in the provided workingDirectory
- [ ] Brief written to a file at `<workingDirectory>/.build/brief.md` and the command run with that file as input
- [ ] Stdout captured and parsed for the WORKER_RESULT fenced block
- [ ] Stderr captured for diagnostics (included in FailureReason on failure)
- [ ] Timeout enforced; process killed on cancellation token or WorkerOptions.Timeout expiry
- [ ] AllowedTools from WorkerOptions passed via the appropriate flag if set
- [ ] WORKER_RESULT JSON parsed into WorkerResult; if absent or malformed, returns WorkerResult with Status = Escalate and FailureReason set
- [ ] Non-zero exit + no WORKER_RESULT returns WorkerResult with Status = Failed and stderr in FailureReason
- [ ] xUnit tests pass with fixture process

Notes: The brief instructs the worker to emit a fenced JSON block at the end of its output in this exact form:

````
```json WORKER_RESULT
{
  "status": "ok",
  "summary": "...",
  "files_changed": [...],
  "failure_reason": null,
  "metadata": {...}
}
```
````

The parser looks for the fenced block with the `WORKER_RESULT` marker after `json`. The instruction-to-worker contract belongs in the brief constructor (Plan B); ClaudeCodeAgent just looks for it.

OOS:
- Do not implement other worker agents (Codex, Gemini)
- Do not implement sub-agent dispatch from the worker
- Do not stream worker output
- Do not preserve any base64-encoded payload pattern from prior systems

#### Brief 04: event-log

Goal: Implement IEventSink as an append-only JSONL writer to `.build/events/<session-id>.jsonl`.

Inputs:
- IEventSink interface from `ThroughlineBuild.Contracts`
- System.IO (file I/O)
- System.Text.Json with source generators

Outputs:
- `src/ThroughlineBuild.EventLog/ThroughlineBuild.EventLog.csproj` (classlib)
- `src/ThroughlineBuild.EventLog/JsonlEventSink.cs` implementing IEventSink
- `src/ThroughlineBuild.EventLog/EventLogOptions.cs` (BaseDirectory default `.build/events`, SessionId from constructor or generated)
- xUnit tests covering: single emit, multiple emits, flush behavior, concurrent emits, session-id file naming

Acceptance:
- [ ] EmitAsync appends one JSON object per line to `<BaseDirectory>/<SessionId>.jsonl`
- [ ] WorkflowEvent serialized with stable field order
- [ ] File created if absent; appended if present
- [ ] FlushAsync ensures buffered writes are durable (FileStream.FlushAsync)
- [ ] Thread-safe for concurrent EmitAsync (SemaphoreSlim or async lock)
- [ ] xUnit tests verify file contents match expected JSONL output
- [ ] `JsonSerializerContext` source-generator used (AOT-friendly)

Notes: One JSON object per line. No trailing comma. Newline-delimited. The metadata `IReadOnlyDictionary<string, object>` serializes as a nested JSON object.

OOS:
- Do not implement event reading or replay (future)
- Do not implement remote sinks
- Do not buffer indefinitely (write-through is fine)
- Do not implement log rotation

## Plan B: Plan phase composition

### Goal

Compose the external clients from Plan A into the actual `build plan <id>` flow. Brief constructor for the plan phase, the state machine subset (Backlog -> Planning -> Ready), the CLI entry point with TOML config loading.

Brief sequence: B01 brief-constructor first. B02 plan-phase depends on B01. B03 cli-entry depends on B02.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | brief-constructor | Pure function (Ticket, RepoState) -> Brief for the plan phase | - | src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj, src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs |
| 02 | plan-phase | Orchestrates the plan flow end-to-end | 01 | src/ThroughlineBuild.Phases/ThroughlineBuild.Phases.csproj, src/ThroughlineBuild.Phases/PlanPhase.cs |
| 03 | cli-entry | `build plan <id>` subcommand with TOML config | 02 | src/ThroughlineBuild.Cli/Program.cs (modified), src/ThroughlineBuild.Cli/Config.cs, .build/config.toml.example |

### Briefs - detail

#### Brief 01: brief-constructor

Goal: A pure function that takes a Ticket and minimal repo state and returns a Brief object for the plan phase, including the prompt text the worker will receive.

Inputs:
- Ticket record (from Contracts)
- A small RepoState record providing current main SHA and a list of top-level repo entries (so the brief can hint at codebase layout)

Outputs:
- `src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj` (classlib)
- `src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs` with:

```csharp
public static class PlanBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repoState);
}

public record RepoState(
    string MainSha,
    IReadOnlyList<string> TopLevelEntries);
```

- The Brief.Instruction field contains a markdown prompt that:
  - States the goal (plan the work for this ticket: produce an implementation plan plus risk and size assessment)
  - Includes ticket title, type, description (HTML stripped to text or passed through)
  - Lists the repo's top-level entries as context
  - Specifies the WORKER_RESULT envelope to emit at the end
  - Specifies WORKER_RESULT.metadata must include: plan_html (string, the proposed implementation plan as HTML for Plane), risk_label (one of "low", "medium", "high"), size_label (one of "S", "M", "L"), planned_at_sha (the main SHA at planning time)
  - States this is planning only: no file writes

- xUnit tests covering: minimal ticket, rich-description ticket, ticket with relations, edge cases

Acceptance:
- [ ] Build is a pure function (no I/O)
- [ ] Returned Brief.Phase == Phase.Plan
- [ ] Returned Brief.AllowedWrites is empty
- [ ] Returned Brief.Instruction includes the WORKER_RESULT envelope template
- [ ] Returned Brief.Context includes "main_sha" with the current SHA
- [ ] Instruction text under ~1000 tokens (rough cap; do not exceed without justification)
- [ ] xUnit tests pass

Notes: This brief is the lever for cost reduction. Aim for minimal instruction text. State WHAT the worker should do; HOW is the worker's loop. Avoid prose accretion patterns from older prompt corpora.

OOS:
- Do not implement briefs for other phases
- Do not call any external service
- Do not write files
- Do not import prose from any prior prompt corpus

#### Brief 02: plan-phase

Goal: The orchestrator class that runs the plan phase end-to-end. Loads ticket, runs preflight, constructs brief, spawns worker, validates result, writes back to Plane, emits events.

Inputs:
- ITicketing, IWorkerAgent, IEventSink (constructor injection)
- PlanBriefBuilder from B01
- Helpers from op-doc 2 (MarkerParser, etc. as needed)

Outputs:
- `src/ThroughlineBuild.Phases/ThroughlineBuild.Phases.csproj` (classlib)
- `src/ThroughlineBuild.Phases/PlanPhase.cs` with:

```csharp
public class PlanPhase
{
    public PlanPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options);

    public Task<PlanResult> RunAsync(string ticketId, CancellationToken ct);
}

public record PlanResult(
    bool Success,
    string TicketId,
    string? RiskLabel,
    string? SizeLabel,
    string? PlannedAtSha,
    string? FailureReason);

public record BuildOptions(
    string WorkerName,
    TimeSpan WorkerTimeout,
    IReadOnlyList<string>? WorkerAllowedTools);
```

- xUnit tests with mocked dependencies covering: happy path (Backlog ticket planned successfully), ticket not in Backlog (refuses with clean failure), worker returns Status.Failed (records event, returns Success=false), worker returns Status.Escalate (records event, leaves ticket in Planning)

Phase logic:

1. Fetch ticket via ITicketing.GetAsync
2. Validate state == Backlog; if not, return PlanResult(Success=false, FailureReason="ticket not in Backlog state")
3. Get current main SHA from local git (use `git rev-parse origin/main` via Process)
4. Emit StateTransition event (Backlog -> Planning)
5. TransitionAsync to Planning
6. Get repo top-level entries (Directory.EnumerateFileSystemEntries of cwd, top level only)
7. Build Brief via PlanBriefBuilder
8. Emit WorkerSpawn event
9. Call worker.ExecuteAsync in the current working directory with WorkerOptions from BuildOptions
10. Emit VerifierVerdict event recording WorkerResult.Status (no verifier dispatched for plan)
11. If Status == Ok: extract plan_html, risk_label, size_label, planned_at_sha from WorkerResult.Metadata
12. If extraction fails: leave ticket in Planning, return Success=false
13. AppendDescriptionAsync with plan_html; emit TicketWrite event
14. ApplyLabelsAsync with `["risk:{risk_label}", "size:{size_label}"]` plus existing labels preserved; emit TicketWrite event
15. CreateCommentAsync with `<p>[planned_at: {planned_at_sha}]</p>`; emit TicketWrite event
16. TransitionAsync to Ready; emit StateTransition event
17. Return PlanResult(Success=true, ...)

Acceptance:
- [ ] All steps above implemented in order
- [ ] State transitions go through ITicketing (no side-channel writes)
- [ ] WorkflowEvent emitted at each significant step
- [ ] xUnit tests cover the listed scenarios with mocked dependencies
- [ ] Phase is library-callable from any entry point (not CLI-coupled)
- [ ] No prose templates baked in beyond what the brief constructor produces
- [ ] Git subprocess call wrapped so it can be mocked in tests (a tiny IGitClient interface with a default Process-based impl is acceptable)

Notes: BuildOptions is a small config record passed in by the CLI. The "label preservation + new labels" pattern needs care: read current labels first, then ApplyLabelsAsync with the union.

OOS:
- Do not implement other phases (implement, review, ship, chain)
- Do not implement worker dispatch to non-ClaudeCode workers (just take an IWorkerAgent; the CLI wires the concrete instance)
- Do not add caching or persistence beyond IEventSink
- Do not preserve any old base64 envelope from prior systems

#### Brief 03: cli-entry

Goal: The `build plan <id>` subcommand. Loads TOML config, instantiates dependencies, runs PlanPhase, exits with a meaningful code.

Inputs:
- PlanPhase from B02
- PlaneTicketingClient, AnthropicClient, ClaudeCodeAgent, JsonlEventSink from Plan A
- Tomlyn (NuGet) for TOML parsing
- System.CommandLine or manual arg parsing

Outputs:
- Updated `src/ThroughlineBuild.Cli/Program.cs` with a top-level command parser
- `src/ThroughlineBuild.Cli/Config.cs` with the TOML config record and loader
- `.build/config.toml.example` at repo root documenting the schema

TOML schema:

```toml
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "your-workspace"
plane_project_id = "uuid-of-project"
plane_api_token_env = "PLANE_API_TOKEN"

[llm]
default_model = "anthropic:claude-opus-4-7"
anthropic_api_key_env = "ANTHROPIC_API_KEY"

[workers]
default_agent = "claude-code"
timeout_minutes = 30

[workers.claude-code]
executable = "claude"

[events]
log_directory = ".build/events"
```

Acceptance:
- [ ] `build plan <id>` runs from a terminal
- [ ] Config loaded from `.build/config.toml` in cwd or any parent directory (walk up)
- [ ] Secrets loaded from env vars named by `*_env` fields
- [ ] Exit code 0 on success, non-zero on failure (1 for phase failure, 2 for config error, 3 for missing secret)
- [ ] Helpful error messages for missing config, missing env vars, ticket not found
- [ ] `build --help` works and lists `plan` as a subcommand
- [ ] `.build/config.toml.example` exists at repo root and documents the schema with comments

Notes: Tomlyn is AOT-friendly. For one subcommand, manual arg parsing keeps the dependency surface small; if reaching for System.CommandLine, verify AOT compatibility on the current version.

OOS:
- Do not implement other subcommands (implement, review, ship, chain, new, install)
- Do not implement interactive prompts
- Do not implement TOML schema migration
- Do not implement automatic env-var sourcing from `.env` files

## What done looks like

`build plan TT2-XXX` runs from a terminal in a TradeTrack2 worktree. The binary:

- Loads config from `.build/config.toml`
- Fetches the ticket from real Plane via PlaneTicketingClient
- Transitions ticket Backlog -> Planning
- Builds a plan brief from the typed ticket state
- Spawns Claude Code via `claude --print` in the current directory with the brief
- Claude Code performs the planning work (reads files, plans the implementation)
- Claude Code returns a fenced WORKER_RESULT JSON block with plan_html, risk_label, size_label, planned_at_sha
- Binary parses the result
- Binary writes back to Plane: appends plan to description, applies risk and size labels, posts the `[planned_at: SHA]` marker comment, transitions Planning -> Ready
- Binary writes events throughout to `.build/events/<session-id>.jsonl`
- Binary exits zero

Run the same ticket through the existing `/ti` slash command and compare:
- Token cost (use the audit tool in the prior system to capture before/after)
- Wall-clock time
- Output quality: is the plan equivalently good? Is the risk/size assessment sensible? Are the relevant files identified correctly?

Expected outcome: token cost at least 5x lower for the equivalent single-phase work. If not, investigate why before proceeding to op-doc 4. Surprises (in cost, quality, or behavior) are the input to the revision meeting that decides the shape of op-docs 4 through 9.
