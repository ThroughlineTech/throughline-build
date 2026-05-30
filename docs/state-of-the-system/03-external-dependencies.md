# 03 - External Dependencies

Every service, API, CLI, and runtime library this repo depends on; what specific endpoints or tools it touches; what happens when the dependency is missing or unauthenticated.

For configuration of the credentials and base URLs, see [04-configuration.md](04-configuration.md). For failure-mode detail per phase, see [09-failure-modes.md](09-failure-modes.md).

---

## Plane (ticketing backend)

**Primary contract.** Every ticket-bearing verb in `build` reads from and writes to Plane.

### How `build` talks to Plane

Status: Functional.

Single implementation: [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Wraps `System.Net.Http.HttpClient` with a `Polly` resilience pipeline that retries on `PlaneApiException` where status is 429 or >= 500. Retries default to 5 attempts with exponential-with-jitter backoff (base 2s), but when a 429 carries a `Retry-After` header the pipeline waits exactly that long (clamped to `MaxRetryDelay`, default 60s) instead of guessing. Attempt count and delays are configurable via `PlaneClientOptions` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:53-72](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L53-L72)).

- **Base URL:** configurable via `ticketing.plane_base_url`. Default in [.build/config.toml.example](../../.build/config.toml.example): `https://api.plane.so`. `PlaneClientOptions.BaseUrl` also defaults to `https://api.plane.so` ([src/ThroughlineBuild.Plane/PlaneClientOptions.cs:5](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L5)).
- **Auth:** `X-API-Key` header, value from `ticketing.plane_api_token` (or env, default name `PLANE_API_TOKEN`). Set in the `PlaneTicketingClient` constructor at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:49](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L49).
- **Endpoint base paths:** issues `api/v1/workspaces/{slug}/projects/{id}/issues/` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:73-74](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L73-L74)); also `states/`, `labels/`, and `issue-types/` under the same project root ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:76-83](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L76-L83)).

### Rate throttle (TLB-327, f3953f7)

Status: Functional.

Every HTTP send blocks on a shared `RequestThrottle` before it goes out, capped at `PlaneClientOptions.RequestsPerMinute` (default 40) on a rolling one-minute window ([src/ThroughlineBuild.Plane/RequestThrottle.cs](../../src/ThroughlineBuild.Plane/RequestThrottle.cs), constructed at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:51](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L51)). When the budget is spent the call awaits until the oldest in-window send ages out. This gate is **per-process**: Plane enforces its real 60/min limit server-side and globally per API token, so the throttle cannot coordinate across concurrent `build` instances. The 40/min default leaves headroom for a second instance sharing the token; if Plane still returns a 429 (e.g. two instances contending), the Polly retry pipeline backs off on `Retry-After` and recovers. The clock and the wait are injectable for testing. This is a hard pre-send gate distinct from the Polly post-failure retry pipeline; both are active.

### Endpoints touched

| Method | Verb | Path | Used by |
|---|---|---|---|
| `GetAsync(id)` | GET | `issues/?per_page=100` then filter by sequence id ([:210-216](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L210-L216)) | every phase fetches the ticket |
| `GetBatchAsync(ids)` | GET (parallel `GetAsync`) | same | (not currently called) |
| `TransitionAsync(id, state)` | PATCH | `issues/{uuid}/` | all phase transitions |
| `AppendDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` | `PlanPhase` (read-modify-write append) |
| `UpdateDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` ([:701-714](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L701-L714)) | description replace (TLB-251) |
| `CreateCommentAsync(id, html)` | POST | `issues/{uuid}/comments/` | every phase posts at least one comment |
| `ApplyLabelsAsync(id, labels)` | PATCH | `issues/{uuid}/` | `PlanPhase` (risk/size), `AmendCommand`, `ScaffoldPhase` |
| `GetRelationsAsync(id)` | GET | `issues/{uuid}/relations/` | rollup / relation logic |
| `GetCommentsAsync(id)` | GET | `issues/{uuid}/comments/` (404 -> empty) | marker parsing, reopen-marker detection |
| `RollupParentAsync(id)` | GET (`?expand=state`) + PATCH + POST | mixed ([:417-484](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L417-L484)) | `CloseCommand`, `DeferCommand` |
| `CreateTicketAsync(title, type, html, labels)` | POST | `issues/` ([:486-537](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L486-L537)) | `NewPhase`, `ScaffoldPhase` |
| `SetParentAsync(child, parent)` | PATCH | `issues/{uuid}/` (`parent` field) | `ScaffoldPhase` |
| `QueryAsync(query)` | GET (cursor-paginated) | `issues/?per_page=100&state=&parent=&type=` ([:551-598](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L551-L598)) | tree walk, child detection, chain (TLB-251) |
| `TransitionLifecycleAsync(id, transition, reason)` | POST comment + PATCH | `issues/{uuid}/comments/` then `issues/{uuid}/` ([:648-699](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L648-L699)) | `close` / `defer` / `reopen` (TLB-251) |
| `CreateChildTicketsAsync(parent, children)` | POST (per child) | `issues/` with `parent` field set ([:716-785](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L716-L785)) | `ScaffoldPhase` / `DecomposePhase` sub-issue creation (TLB-262) |

New since the architecture doc:

- **`CreateChildTicketsAsync` (TLB-262):** batched sub-issue creation. Resolves the label cache once, then POSTs each child as an issue with `parent` set to `parentUuid`. Never throws - per-child failures are collected into `CreateChildTicketsResult.Failures`; unknown label names are silently skipped ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:738-784](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L738-L784)).
- **`QueryAsync` / `TransitionLifecycleAsync` / `UpdateDescriptionAsync` (TLB-251):** filtered listing, lifecycle transitions with marker comments, and full description replace.
- **Issue-type NAME -> UUID resolution (TLB-213/214):** `CreateTicketAsync` resolves a `type` string against an `issue-types/` cache and PATCHes the resolved UUID; an unknown type throws `InvalidOperationException` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:508-514](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L508-L514)).
- **Client-side parent filtering (TLB-327):** Plane silently ignores the `parent=` list-query param, so `QueryAsync` walks every cursor page and filters by `ParentId` itself ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:582-590](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L582-L590)). `RollupParentAsync` applies the same client-side filter on siblings ([:439-446](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L439-L446)). Page walks are capped at `MaxListPages = 50` ([:602](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L602)).

### Deep-link URL (TLB-292, 9450889)

Status: Functional.

The Plane work-item URL printed to the operator uses the `?next_path=` redirect pattern: `{base}/?next_path=/{workspaceSlug}/browse/{ticketId}` ([src/ThroughlineBuild.Cli/Program.cs:1579-1583](../../src/ThroughlineBuild.Cli/Program.cs#L1579-L1583), duplicated in [src/ThroughlineBuild.Commands/NewCommand.cs:100](../../src/ThroughlineBuild.Commands/NewCommand.cs#L100)). Empty if any of base URL / slug / ticket id is unset.

### Caches held in memory per invocation

- State name -> UUID map: lazy-loaded on first transition, semaphore-guarded ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:141-157](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L141-L157)).
- Label name -> UUID map: lazy-loaded on first label application, case-insensitive matching ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:159-175](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L159-L175)).
- Issue-type name -> UUID map: lazy-loaded on first ticket create with a type ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:177-193](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L177-L193)).

None persists across invocations (the binary exits between calls).

### Capabilities advertised

```csharp
BackendCapabilities(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)
```

[src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:65-69](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L65-L69). No caller in the repo actually reads this today - capability-driven dispatch is plumbed at the type level but unused at runtime.

### Handshake when missing or unauthenticated

- **No token in config or env:** `BuildConfigLoader.ResolveSecrets` throws `ConfigException` with message `plane_api_token not set in config and required environment variable '<env_name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:123-125](../../src/ThroughlineBuild.Cli/Config.cs#L123-L125)). The CLI catches it eagerly and exits 3, prefixing the message with `Secret error:` ([src/ThroughlineBuild.Cli/Program.cs:181-185](../../src/ThroughlineBuild.Cli/Program.cs#L181-L185)).
- **Unauthorized (401/403) response from Plane:** raised as `PlaneApiException(status, body)` and surfaces as a phase failure with exit 1.
- **Rate limit (429) or transient 5xx:** the throttle makes a client-side 429 nearly impossible; a server-origin 429 or 5xx is retried 3 times by Polly before raising.
- **Workspace or project UUID wrong:** Plane returns 404 - same path as unauthorized.
- **State not installed in the project:** `TransitionAsync` / `TransitionLifecycleAsync` warn to stderr and leave the ticket where it is rather than throwing ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:295-303](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L295-L303)).
- **Network unreachable:** `HttpClient` throws `HttpRequestException`; depending on the call site, surfaces as `PhaseInfraFailure` or propagates to CLI as an uncaught exception (exit 1).

### Loose ends - Plane

- **Reverse state name map** at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:196-206](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L196-L206) and the forward `switch` in each transition method hardcode the seven state names (`Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`). A Plane workspace with different state names reads everything as `Backlog` and skips transitions with a stderr warning.
- **Rollup ranking** (`StateRank`, [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:799-809](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L799-L809)) and `ApplyRollupRules` ([:811-836](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L811-L836)) hardcode priority ordering; no extensibility for custom state hierarchies.
- **`[rollup]` comment marker** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:471](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L471)) is load-bearing for the rollup comment format - no versioning if the format changes.
- **`Ticket.Risk`** is always returned as `Risk.Medium` from Plane reads ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:248](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L248)). `Ticket.Size` now IS extracted from a `size:s|m|l` label ([:232-239](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L232-L239)) - the architecture doc's claim that size is always `M` is stale.
- **Page cap of 50** (5000 issues) silently truncates very large projects; a parent query that overflows would under-report children.

---

## Anthropic API (LLM judgment slot)

Status: Functional (single production caller).

Direct REST calls. Still exactly one production caller: `ReasonTranslator` for `close` / `defer` / `reopen`. The hard API-key gate was removed in TLB-227 (2c04bf9 / 042f963): instead of a top-level check, the client is now built lazily by `LlmClientFactory.Create` only when one of those three verbs runs ([src/ThroughlineBuild.Cli/LlmClientFactory.cs](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs), invoked from `WireUpConditionalCommands` at [src/ThroughlineBuild.Cli/Program.cs:1605-1617](../../src/ThroughlineBuild.Cli/Program.cs#L1605-L1617)). All other verbs never touch the Anthropic REST API - workers reach Anthropic through the `claude` CLI's own OAuth.

The production path goes through `AnthropicClient` (implements `ILlmClient`):

- **Implementation:** [src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs).
- **Base URL:** `https://api.anthropic.com` ([src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:7](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L7)).
- **Endpoint:** `POST /v1/messages` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:52](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52)).
- **Auth headers:** `x-api-key` from `AnthropicOptions.ApiKey`; `anthropic-version` from `AnthropicOptions.ApiVersion`, default `2023-06-01` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:56-57](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L56-L57), default at [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:6](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L6)). The version is now a settable option rather than a string literal, though `LlmClientFactory` does not expose a config knob for it.
- **Vendor gating:** `LlmClientFactory` only accepts `[llm] default_model` values starting `anthropic:`; an empty model or any other prefix throws `ConfigException` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)).
- **Models:** invoked with the raw model id after stripping the `anthropic:` prefix ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:29-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L29-L31)). `ReasonTranslator` pins `claude-haiku-4-5-20251001` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)).
- **Retry:** Polly 3 retries on 429 / 5xx with exponential backoff ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:102-114](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L102-L114)).
- **Streaming:** `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### Newer non-streaming + streaming path: `IModelClient` (TLB-244/245) - not yet wired

Status: Partial (built and tested, no production caller).

A parallel client abstraction `IModelClient` ([src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs)) was added with an Anthropic implementation `AnthropicModelClient` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs)). Unlike `AnthropicClient`, its `StreamAsync` is fully implemented: it sets `stream:true`, reads the SSE response with `HttpCompletionOption.ResponseHeadersRead`, and maps `content_block_delta` / `message_delta` / `message_start` / `message_stop` / `error` events into `ModelStreamEvent` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs:82-180](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs#L82-L180)). Auth and headers come from a `ProviderConfig` (`AuthScheme` + `ExtraHeaders`) rather than the fixed `AnthropicOptions` shape. A `ModelClientLlmAdapter` bridges `IModelClient` back to `ILlmClient` but still stubs its own `InvokeStreamAsync` ([src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs:65-72](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65-L72)). None of `AnthropicModelClient` / `ModelClientLlmAdapter` / `IModelClient` is constructed by `Program.cs` or the factory today - the only `ILlmClient` the CLI builds is `AnthropicClient`.

### Handshake when missing or unauthenticated

- **No API key:** only `close` / `defer` / `reopen` need it. `LlmClientFactory.Create` throws `ConfigException` with message `anthropic_api_key not set and env var '<env>' is not set; configure [llm] anthropic_api_key or set the env var` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:16-19](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L16-L19)). `WireUpConditionalCommands` returns the message and the CLI exits 3, prefixed with `Secret error:` ([src/ThroughlineBuild.Cli/Program.cs:271-275](../../src/ThroughlineBuild.Cli/Program.cs#L271-L275)). The old wording `"anthropic api key required for close/defer/reopen (reason translation)"` no longer exists.
- **401/403:** `AnthropicApiException(status, body)` propagates; verb exits with phase failure.
- **Rate limit:** Polly retries.

### Loose ends - Anthropic

- **`anthropic-version` is settable but not config-wired** - `AnthropicOptions.ApiVersion` exists, but `LlmClientFactory` never reads a config value for it, so it is effectively pinned to the default until a future wiring change.
- **`AnthropicClient.InvokeStreamAsync` still unimplemented** even though `AnthropicModelClient.StreamAsync` proves the streaming path. The two clients are not yet reconciled and the streaming one is dead code at runtime.
- **No request-id capture** - the Claude Code worker explicitly notes `anthropic_request_id` is unavailable in its stream envelope ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:325](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L325)); the REST `AnthropicClient` likewise does not surface `anthropic-request-id` into `LlmResponse`.

---

## Worker CLIs (`claude`, `codex`, `gemini`, `copilot`)

Status: Functional (claude-code); Partial (codex / gemini / copilot - built and tested, default config still selects claude-code).

There are now four `IWorkerAgent` implementations, one per vendor CLI. Each shells out to a subprocess, delivers the brief, and reads a `WORKER_RESULT` envelope back. The envelope parser is shared in `ThroughlineBuild.Workers.Common` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)) - it walks the marker lines in reverse so the last valid envelope wins, tolerating a template echo earlier in the output, and validates `metadata.escalation` (see 07-contracts.md).

### Shared subprocess contract

All four agents:

- Spawn with `UseShellExecute=false`, redirected stdin/stdout/stderr, `CreateNoWindow=true`.
- Strip provider API-key env vars to force subscription/OAuth auth (claude-code, codex, gemini); Copilot is the exception - its auth is additive (`GH_TOKEN` or inherited `gh` keyring credential), not subtractive ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:178-188](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L178-L188)).
- Resolve a per-`WorkerSize` model from `Sizes` and pass it via `--model` after stripping the vendor prefix.
- On `Process.Start` failure (CLI not found), catch `Win32Exception` and return `WorkerResult { Status = Failed, Summary = "Worker executable not found: '<exe>'" }` rather than crashing - see "missing CLI" below (TLB; commit 0f9d114 "Don't crash when Claude isn't found").
- On timeout (`WorkerOptions.Timeout` -> `CancellationTokenSource.CancelAfter`), kill the process tree (`entireProcessTree: true`, swallowed on failure) and write partial output to the debug-capture directory when present.

### Per-CLI subprocess contract

| Agent (`Name`) | Default exe | Brief delivery | Spawn flags | Stdout shape parsed | Auth env stripped |
|---|---|---|---|---|---|
| `claude-code` | `claude` | stdin | `--print --verbose --output-format stream-json` `[--dangerously-skip-permissions]` `[--allowedTools a,b]` `[--model M]` `ExtraArgs` ([:373-387](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L373-L387)) | NDJSON, terminal `type=result` envelope; legacy single-blob `--output-format json` also accepted; inner `result` text run through `WorkerResultParser` ([:259-321](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L259-L321)) | `ANTHROPIC_API_KEY` ([:408](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L408)); sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` when configured |
| `codex` | `codex` | positional prompt arg | `exec [--full-auto] ExtraArgs [--model M] "<brief>"` ([:180-193](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L180-L193)) | plain text; raw stdout scanned for `WORKER_RESULT` ([:137-161](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L137-L161)) | `CODEX_API_KEY`, `OPENAI_API_KEY` ([:163-171](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L163-L171)) |
| `gemini` | `gemini` | `-p` prompt arg | `-p "<brief>" --output-format json [--yolo] [--model M] ExtraArgs` ([:224-236](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L224-L236)) | JSON envelope `{response, stats}`; `.response` text run through `WorkerResultParser`, raw-stdout fallback ([:163-217](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L163-L217)) | `GEMINI_API_KEY`, `GOOGLE_API_KEY` ([:264-271](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L264-L271)) |
| `copilot` | `copilot` | `-p` prompt arg | `-p "<brief>" -s --no-ask-user ExtraArgs [--model M] [--allow-tool T ...]` ([:22-35](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L35)) | plain text; raw stdout scanned for `WORKER_RESULT` | none stripped; additive `GH_TOKEN` |

Notes on the flag variants:

- Claude Code requires `--verbose` alongside `--print --output-format stream-json` (the CLI rejects the combination otherwise); the bypass flag is `--dangerously-skip-permissions`, emitted only when `ClaudeCodeOptions.BypassPermissions` is true (default true) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:357-387](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L357-L387)).
- The bypass flag is per-vendor: codex `--full-auto`, gemini `--yolo`, copilot `-s --no-ask-user` (always emitted). Each agent's model prefix differs: `anthropic:` (claude-code), `openai:` (codex), `google:` (gemini), `github:` (copilot).
- Copilot maps `AllowedTools` to repeated `--allow-tool <tool>` flags, not a comma list; it has no progress digester (`Digester => null`).

### Handshake when CLI missing or unauthenticated

- **CLI not on PATH:** every agent catches `Win32Exception` from `Process.Start` and returns `Status.Failed` with a reason pointing at `workers.<agent>.executable` in config (e.g. [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:89-96](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L89-L96), [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:83-90](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L83-L90), [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:84-91](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L84-L91), [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:84-91](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L84-L91)). The phase then handles the failed result gracefully rather than the process aborting with an uncaught exception (TLB; commit 0f9d114).
- **Worker subprocess fails to authenticate:** Claude Code returns `is_error=true` in the envelope (mapped to `Status.Escalate`) or exits non-zero (`Status.Failed`); the other agents surface a non-zero exit as `Status.Failed` with stderr in the reason.
- **Worker emits no `WORKER_RESULT` marker:** `Status.Failed` with "No WORKER_RESULT found in output" (e.g. [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:317-321](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L317-L321)).
- **Timeout:** `Status.Failed` with "Process cancelled or timed out"; partial stdout/stderr captured to the debug-capture directory when set.

### Loose ends - worker CLIs

- **Vendor CLI drift** is identified in architecture Section 10 as a top risk. No agent pins a CLI version; each parses whatever shape the current CLI produces.
- **Tool-input summarization** in `ClaudeCodeProgressDigester.SummarizeToolInput` hardcodes recognized fields (`command`, `file_path`, `pattern`, `path`, `url`) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs:149-176](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs#L149-L176)) - new Claude Code tools render as bare names. Codex/Gemini digesters have their own field maps; Copilot has no digester.
- **Token usage is best-effort or absent** for codex / gemini / copilot: their `BuildLlmUsageMetadata` reports 0 tokens (gemini reports only a combined total) and null cost. Only claude-code emits real input/output/cache counts from the envelope.
- **Per-platform process-tree kill** `entireProcessTree: true` may fail on some platforms and the exception is swallowed.
- **The non-claude agents are built and unit-tested but not the default**: the configured agent selection (per phase) still defaults to `claude-code`; see 04-configuration.md and 07-contracts.md.

---

## NuGet packages

Direct dependencies only (verify by grepping `PackageReference` across the `.csproj` files).

### Cli project ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj))

- **`Tomlyn 0.16.0`** - TOML parser for `.build/config.toml`. Selected because it is AOT-friendly (architecture Appendix item 2). This is the only third-party package the AOT binary links ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:32](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L32)).

### Plane and Anthropic clients

- **`Polly 8.*`** - retry resilience, referenced directly by `ThroughlineBuild.Plane` ([src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj](../../src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj)) and `ThroughlineBuild.Anthropic` ([src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj](../../src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj)). No other production project references it; `ThroughlineBuild.ModelClient` has no package references.

### Test projects

Every `tests/*` project pins the same trio:

- **`xunit 2.6.2`**
- **`xunit.runner.visualstudio 2.5.3`**
- **`Microsoft.NET.Test.Sdk 17.8.0`**

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
| OpenAI / Google LLM `ILlmClient`s | `ILlmClient` has only `AnthropicClient`; `LlmClientFactory` rejects any non-`anthropic:` prefix. (`AnthropicModelClient` adds an `IModelClient` shape designed for OpenAI/Ollama configs but is unwired.) |
| Codex / Gemini / Copilot worker agents | Now implemented as real `IWorkerAgent`s (`CodexAgent`, `GeminiAgent`, `CopilotAgent`), unit-tested, but not the default agent selection. Their token/cost metadata is partial. |
| MCP server packaging | Architecture Appendix item 3 calls for stubbing it; no stub today. |
| `bin/notify` shim | Referenced in user-global `CLAUDE.md` for agent notifications; this repo has no `bin/notify` script (the shim lives in the operator's home, not the project). |

---

## Loose ends

- **No central dependency manifest** - the dependency graph is per-`.csproj`, not pinned at solution level. Operators upgrading versions touch ~14 files.
- **No SBOM** generated by build or CI.
- **No retry budget telemetry** - the Polly pipelines on Plane and Anthropic clients log nothing about how often retries actually fire.
- **`bin/notify`** shim referenced by global agent conventions is not provided by this repo; the binary itself never notifies.
