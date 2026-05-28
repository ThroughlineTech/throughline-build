# 03 - External Dependencies

Every service, API, CLI, and runtime library this repo depends on; what specific endpoints or tools it touches; what happens when the dependency is missing or unauthenticated.

For configuration of the credentials and base URLs, see [04-configuration.md](04-configuration.md). For failure-mode detail per phase, see [09-failure-modes.md](09-failure-modes.md).

---

## Plane (ticketing backend)

**Primary contract.** Every ticket-bearing verb in `build` reads from and writes to Plane.

### How `build` talks to Plane

Single implementation: [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Wraps `System.Net.Http.HttpClient` with a `Polly` resilience pipeline (3 retries with 1s, 2s, 4s exponential backoff on 429 + 5xx, [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:44-53](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L44-L53)).

- **Base URL:** configurable via `ticketing.plane_base_url`. Default in [.build/config.toml.example](../../.build/config.toml.example): `https://plane.example.com`.
- **Auth:** `X-API-Key` header, value from `ticketing.plane_api_token` (or env, default name `PLANE_API_TOKEN`). Set in `PlaneTicketingClient` constructor at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:42](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L42).
- **Endpoint base path:** `api/v1/workspaces/{slug}/projects/{id}/issues/` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:64-65](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L64-L65)).

### Endpoints touched

| Method | Verb | Path | Used by |
|---|---|---|---|
| `GetAsync(id)` | GET | `issues/?per_page=100` then filter by sequence id | every phase fetches the ticket |
| `GetBatchAsync(ids)` | GET (parallel) | same | (not currently called) |
| `TransitionAsync(id, state)` | PATCH | `issues/{uuid}/` | all phase transitions |
| `AppendDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` | `PlanPhase` |
| `CreateCommentAsync(id, html)` | POST | `issues/{uuid}/comments/` | every phase posts at least one comment |
| `ApplyLabelsAsync(id, labels)` | PATCH | `issues/{uuid}/` | `PlanPhase` (risk/size), `AmendCommand`, `ScaffoldPhase` (plan-ticket label) |
| `GetRelationsAsync(id)` | GET | `issues/{uuid}/relations/` | rollup logic |
| `GetCommentsAsync(id)` | GET | `issues/{uuid}/comments/` | marker parsing, reopen-marker detection |
| `RollupParentAsync(id)` | GET (expand) + PATCH + POST | mixed | `CloseCommand`, `DeferCommand` |
| `CreateTicketAsync(...)` | POST | `issues/` | `NewPhase`, `ScaffoldPhase` |
| `SetParentAsync(child, parent)` | PATCH | `issues/{uuid}/` | `ScaffoldPhase` |

### Caches held in memory per invocation

- State name -> UUID map: lazy-loaded on first transition, semaphore-guarded ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:126-142](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L126-L142)).
- Label name -> UUID map: lazy-loaded on first label application, case-insensitive matching ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:144-160](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L144-L160)).

Neither cache persists across invocations (the binary exits between calls).

### Capabilities advertised

```csharp
BackendCapabilities(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)
```

[src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:56-60](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L56-L60). No caller in the repo actually reads this today - capability-driven dispatch is plumbed at the type level but unused at runtime.

### Handshake when missing or unauthenticated

- **No token in config or env:** `BuildConfigLoader.ResolveSecrets` throws `ConfigException`, CLI exits 3 with `Secret error: plane_api_token not set in config and required environment variable '<env_name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:115-121](../../src/ThroughlineBuild.Cli/Config.cs#L115-L121)).
- **Unauthorized (401/403) response from Plane:** raised as `PlaneApiException(status, body)` and surfaces as a phase failure with exit 1.
- **Rate limit (429) or transient 5xx:** Polly retries 3 times before raising.
- **Workspace or project UUID wrong:** Plane returns 404 - same path as unauthorized.
- **Network unreachable:** `HttpClient` throws `HttpRequestException`; depending on the call site, surfaces as `PhaseInfraFailure` or propagates to CLI as an uncaught exception (exit 1).

### Loose ends - Plane

- **Hardcoded state name map** at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:163-173](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L163-L173): `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`. A Plane workspace with different state names will fail to transition.
- **Rollup ranking** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:507-543](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L507-L543)) hardcodes priority ordering; no extensibility for custom state hierarchies.
- **`[rollup]` comment marker** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:423](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L423)) is load-bearing for de-duplicating rollup comments - no versioning if the format changes.
- **`Ticket.Size` and `Ticket.Risk`** are always returned as `Size.M` / `Risk.Medium` from Plane reads ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:204-205](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L204-L205)). Label-driven extraction is unimplemented; downstream phases only consume risk/size from the worker output, not the ticket.

---

## Anthropic API (LLM judgment slot)

Direct REST calls. Only one production caller today: `ReasonTranslator` for `close` / `defer` / `reopen`.

- **Implementation:** [src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs).
- **Base URL:** `https://api.anthropic.com` ([src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:7](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L7)).
- **Endpoint:** `POST /v1/messages` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:52](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52)).
- **Auth headers:** `x-api-key` from `AnthropicOptions.ApiKey`; `anthropic-version: 2023-06-01` (hardcoded at [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:6](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L6)).
- **Models:** invoked with the raw model id after stripping `anthropic:` prefix ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:29-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L29-L31)). `ReasonTranslator` pins `claude-haiku-4-5-20251001`.
- **Retry:** Polly 3 retries on 429 / 5xx with exponential backoff ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:104-111](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L104-L111)).
- **Streaming:** `InvokeStreamAsync` throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### Handshake when missing or unauthenticated

- **No API key:** only `close` / `defer` / `reopen` need it. CLI rejects with `"anthropic api key required for close/defer/reopen (reason translation)"` and exit 3 ([src/ThroughlineBuild.Cli/Program.cs:1130-1133](../../src/ThroughlineBuild.Cli/Program.cs#L1130-L1133)). Other verbs do not require the key because they reach Anthropic via the `claude` CLI's own OAuth.
- **401/403:** `AnthropicApiException(status, body)` propagates; verb exits with phase failure.
- **Rate limit:** Polly retries.

### Loose ends - Anthropic

- **Hardcoded `anthropic-version`** - no config knob; will silently miss new model features until bumped.
- **`InvokeStreamAsync` not implemented** but is part of `ILlmClient`. No caller uses the streaming path today.
- **No request-id capture** (`anthropic-request-id` header is not surfaced into `LlmResponse`) - debugging a per-call failure against Anthropic logs requires reproducing the request.

---

## Claude Code CLI (`claude`)

The Claude Code agent CLI is the only worker implementation in v1.

- **Implementation:** [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs).
- **Spawn:** `<exe> --print --verbose --output-format stream-json` with optional `--allowedTools <comma-list>`, `--model <model_id>`, and any `ExtraArgs` from `ClaudeCodeOptions`. Brief is delivered on stdin ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:33-40, 96-98](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L33-L40)).
- **Process env:** `ANTHROPIC_API_KEY` is **stripped** so the child uses Claude Code OAuth, not the orchestrator's key ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:374](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L374)). `CLAUDE_CODE_MAX_OUTPUT_TOKENS` is set when `workers.max_output_tokens` is configured.
- **Output protocol:** NDJSON stream of events with a terminal `type=result` envelope. Legacy single-blob `--output-format json` also parsed as a fallback ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:181-228](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L181-L228)). The orchestrator scans stdout in reverse for a `WORKER_RESULT` marker followed by a JSON object ([src/ThroughlineBuild.Workers.ClaudeCode/WorkerResultParser.cs:26-38](../../src/ThroughlineBuild.Workers.ClaudeCode/WorkerResultParser.cs#L26-L38)) so the last printed envelope wins (tolerates template examples in the brief).

### Handshake when missing or unauthenticated

- **`claude` not on PATH:** `Process.Start` throws; surfaces as `InvalidOperationException` in `ClaudeCodeAgent.ExecuteAsync`. Verb exits 1.
- **Worker subprocess fails to authenticate:** the `--print` flow returns is_error in the envelope or exits non-zero; the orchestrator returns `WorkerResult { Status = Escalate | Failed }` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:263-321](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L263-L321)).
- **Worker emits no `WORKER_RESULT` marker:** `Status.Failed` with reason "No WORKER_RESULT found" ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:318-321](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L318-L321)).
- **Timeout:** `WorkerOptions.Timeout` triggers `CancellationTokenSource.CancelAfter`; process tree is killed ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:107](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L107)) and partial output captured to `.build/sessions/<stem>/` when `--debug`.

### Loose ends - Claude Code worker

- **Vendor CLI drift** is identified in architecture Section 10 as a top risk. The worker pins no version; the orchestrator parses whatever shape `--output-format stream-json` produces today.
- **Tool-input summarization** in `WorkerProgressDigest` hardcodes recognized fields (`file_path`, `pattern`, `command`, `path`, `url`) ([src/ThroughlineBuild.Workers.ClaudeCode/WorkerProgressDigest.cs:138-151](../../src/ThroughlineBuild.Workers.ClaudeCode/WorkerProgressDigest.cs#L138-L151)) - new Claude Code tools render as bare names.
- **Process tree kill** `entireProcessTree: true` may fail on some platforms and the exception is swallowed.
- **Codex / Gemini agents** are named in architecture Section 5.5 but no corresponding `IWorkerAgent` implementations exist.

---

## NuGet packages

### Cli project ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj))

- **`Tomlyn 0.16.0`** - TOML parser for `.build/config.toml`. Selected because it is AOT-friendly (architecture Appendix item 2).

### Test projects

- **`xunit 2.6.2`**
- **`Microsoft.NET.Test.Sdk 17.8.0`**

### Plane and Anthropic clients

- **`Polly`** is used by both `PlaneTicketingClient` and `AnthropicClient` for retry resilience. Version pin lives in the per-project `.csproj` files.

There are no other significant third-party NuGets in the dependency tree. The repository deliberately keeps the dependency surface small to remain AOT-trim-friendly (architecture Section 10, "AOT compatibility" risk).

---

## Implicit runtime dependencies

| Tool | Why | What fails without it |
|---|---|---|
| `git` | Every phase shells out to it. | Process-start failure at first invocation; surfaces as `InvalidOperationException`. |
| `git worktree` (>= git 2.5) | All implement/review/ship phases. | "unknown command" from older git; same failure path. |
| ICU data | `<InvariantGlobalization>true</InvariantGlobalization>` is set ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) so the binary does **not** require ICU at runtime. | n/a |
| OpenSSL / Schannel | TLS for HTTPS to Plane and Anthropic. | Network failure if absent. |

---

## Architecture-named services that are not yet wired

| Named | Status |
|---|---|
| GitHub Issues backend | Plumbed via `BackendCapabilities` but no `GitHubTicketingClient`. |
| OpenAI / Google LLM clients | `ILlmClient` exists; no implementations. |
| Codex / Gemini worker agents | `IWorkerAgent` exists; no implementations. |
| MCP server packaging | Architecture Appendix item 3 calls for stubbing it; no stub today. |
| `bin/notify` shim | Referenced in user-global `CLAUDE.md` for agent notifications; this repo has no `bin/notify` script (the shim lives in the operator's home, not the project). |

---

## Loose ends

- **No central dependency manifest** - the dependency graph is per-`.csproj`, not pinned at solution level. Operators upgrading versions touch ~14 files.
- **No SBOM** generated by build or CI.
- **No retry budget telemetry** - the Polly pipelines on Plane and Anthropic clients log nothing about how often retries actually fire.
- **`bin/notify`** shim referenced by global agent conventions is not provided by this repo; the binary itself never notifies.
