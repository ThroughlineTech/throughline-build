# 03 - External Dependencies

Every service, API, CLI, and runtime library this repo depends on; what specific endpoints or tools it touches; what happens when the dependency is missing or unauthenticated.

For configuration of the credentials and base URLs, see [04-configuration.md](04-configuration.md). For failure-mode detail per phase, see [09-failure-modes.md](09-failure-modes.md).

---

## Plane (ticketing backend)

**Primary contract.** Every ticket-bearing verb in `build` reads from and writes to Plane.

### How `build` talks to Plane

Status: Functional.

Single implementation: [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Wraps `System.Net.Http.HttpClient` with a `Polly` resilience pipeline that retries on `PlaneApiException` where status is 429 or >= 500. Retries default to 5 attempts with exponential-with-jitter backoff (base 2s), but when a 429 carries a `Retry-After` header the pipeline waits exactly that long (clamped to `MaxRetryDelay`, default 60s) instead of guessing. Attempt count and delays are configurable via `PlaneClientOptions`; the pipeline is built in the constructor at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:80-110](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L80-L110).

- **Base URL:** configurable via `ticketing.plane_base_url`. Default in [.build/config.toml.example](../../.build/config.toml.example): `https://api.plane.so`. `PlaneClientOptions.BaseUrl` also defaults to `https://api.plane.so` ([src/ThroughlineBuild.Plane/PlaneClientOptions.cs:5](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L5)).
- **Auth:** `X-API-Key` header, value from `ticketing.plane_api_token` (or env, default name `PLANE_API_TOKEN`). Set in the `PlaneTicketingClient` constructor at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:75](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L75).
- **Endpoint base paths:** issues `api/v1/workspaces/{slug}/projects/{id}/issues/` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:122-123](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L122-L123)); also `states/`, `labels/`, and `issue-types/` under the same project root ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:125-132](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L125-L132)).

### Rate throttle (TLB-327, f3953f7)

Status: Functional.

Every HTTP send blocks on a shared `RequestThrottle` before it goes out, capped at `PlaneClientOptions.RequestsPerMinute` (default 40) on a rolling one-minute window ([src/ThroughlineBuild.Plane/RequestThrottle.cs](../../src/ThroughlineBuild.Plane/RequestThrottle.cs), constructed at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:77](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L77)). When the budget is spent the call awaits until the oldest in-window send ages out. This gate is **per-process**: Plane enforces its real 60/min limit server-side and globally per API token, so the throttle cannot coordinate across concurrent `build` instances. The 40/min default leaves headroom for a second instance sharing the token; if Plane still returns a 429 (e.g. two instances contending), the Polly retry pipeline backs off on `Retry-After` and recovers. The clock and the wait are injectable for testing. This is a hard pre-send gate distinct from the Polly post-failure retry pipeline; both are active.

### Endpoints touched

| Method | Verb | Path | Used by |
|---|---|---|---|
| `GetAsync(id)` | GET (first call only) | resolves the sequence id through `FindIssueAsync` against the per-run snapshot ([:284](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L284)); the snapshot is loaded once via `EnsureSnapshotAsync` ([:299](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L299)) | every phase fetches the ticket |
| `GetBatchAsync(ids)` | (in-memory, after snapshot) | parallel `GetAsync`, all served from the snapshot after the first load | multi-ticket chain dependency graph |
| `TransitionAsync(id, state)` | PATCH | `issues/{uuid}/` | all phase transitions |
| `AppendDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` | `PlanPhase` (read-modify-write append) |
| `UpdateDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` ([:916-932](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L916-L932)) | description replace (TLB-251) |
| `CreateCommentAsync(id, html)` | POST | `issues/{uuid}/comments/` | every phase posts at least one comment |
| `ApplyLabelsAsync(id, labels)` | PATCH | `issues/{uuid}/` | `PlanPhase` (risk/size), `AmendCommand`, `ScaffoldPhase` |
| `GetRelationsAsync(id)` | GET | `issues/{uuid}/relations/` | rollup / relation logic |
| `GetCommentsAsync(id)` | GET | `issues/{uuid}/comments/` (404 -> empty) | marker parsing, reopen-marker detection |
| `RollupParentAsync(id)` | GET (`?expand=state`) + PATCH + POST | mixed ([:584-653](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L584-L653)) | `CloseCommand`, `DeferCommand` |
| `CreateTicketAsync(title, type, html, labels)` | POST | `issues/` ([:655-699](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L655-L699)) | `NewPhase`, `ScaffoldPhase` |
| `SetParentAsync(child, parent)` | PATCH | `issues/{uuid}/` (`parent` field) | `ScaffoldPhase` |
| `QueryAsync(query)` | (in-memory, after snapshot) | loads the snapshot once, then filters `_issueByUuid.Values` by state/parent/type client-side ([:735-790](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L735-L790)) | tree walk, child detection, chain (TLB-251) |
| `TransitionLifecycleAsync(id, transition, reason)` | POST comment + PATCH | `issues/{uuid}/comments/` then `issues/{uuid}/` ([:861-914](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L861-L914)) | `close` / `defer` / `reopen` (TLB-251) |
| `CreateChildTicketsAsync(parent, children)` | POST (per child) | `issues/` with `parent` field set ([:935-1016](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L935-L1016)) | `ScaffoldPhase` / `DecomposePhase` sub-issue creation (TLB-262) |

New since the architecture doc:

- **`CreateChildTicketsAsync` (TLB-262):** batched sub-issue creation. Resolves the label cache once, then POSTs each child as an issue with `parent` set to `parentUuid`. Never throws - per-child failures are collected into `CreateChildTicketsResult.Failures` ([:1011](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1011)); unknown label names are silently skipped ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:935-1016](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L935-L1016)).
- **`QueryAsync` / `TransitionLifecycleAsync` / `UpdateDescriptionAsync` (TLB-251):** filtered listing, lifecycle transitions with marker comments, and full description replace.
- **Issue-type NAME -> UUID resolution (TLB-213/214):** `CreateTicketAsync` resolves a `type` string against an `issue-types/` cache and PATCHes the resolved UUID; an unknown type throws `InvalidOperationException` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:682](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L682)).
- **Per-run snapshot cache + correct pagination (TLB-366):** Plane silently ignores the list endpoint's query filters (notably `parent=`) and returns the whole project, so the client no longer re-paginates per lookup. Instead the entire project is paginated once into an in-memory snapshot and every `FindIssueAsync` / `QueryAsync` answer is computed from it client-side - see "Per-run issue snapshot cache" below. This eliminated the redundant page walks that kept `build chain` hammering Plane's rate limiter as the project grew.

### Deep-link URL (TLB-292, 9450889)

Status: Functional.

The Plane work-item URL printed to the operator uses the `?next_path=` redirect pattern: `{base}/?next_path=/{workspaceSlug}/browse/{ticketId}` ([src/ThroughlineBuild.Cli/Program.cs:1692-1698](../../src/ThroughlineBuild.Cli/Program.cs#L1692-L1698), duplicated in [src/ThroughlineBuild.Commands/NewCommand.cs:100](../../src/ThroughlineBuild.Commands/NewCommand.cs#L100)). Empty if any of base URL / slug / ticket id is unset.

### Per-run issue snapshot cache (TLB-366)

Status: Functional.

The dominant cache is the issue snapshot. On the first lookup that needs it, `EnsureSnapshotAsync` ([:299](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L299)) paginates the entire project once and indexes it into two `ConcurrentDictionary` fields ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:48-61](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L48-L61)):

- `_seqToUuid` (`int -> string`): sequence-id -> issue UUID, write-once identity index.
- `_issueByUuid` (`string -> PlaneIssue`): UUID -> full issue, the mutable source of truth.

Load is single-flight (double-checked `SemaphoreSlim` so concurrent callers share one load). Thereafter `FindIssueAsync` ([:284](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L284)) answers seq lookups from `_seqToUuid` + `_issueByUuid` with no network call, throwing `KeyNotFoundException` for an unknown seq, and `QueryAsync` filters `_issueByUuid.Values` in memory ([:735](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L735)).

Every mutating call (`TransitionAsync`, `AppendDescriptionAsync`, `ApplyLabelsAsync`, `RollupParentAsync`, `SetParentAsync`, `TransitionLifecycleAsync`, `UpdateDescriptionAsync`) performs a **write-through** update so the snapshot stays current for the rest of the run: `UpdateCachedIssue` runs the mutation inside `ConcurrentDictionary.AddOrUpdate` with a pure `Func<PlaneIssue,PlaneIssue>` closure ([:358](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L358)), so two concurrent field updates compose rather than clobber (the lost-update race the atomic-update commit fixed). Newly created tickets are seeded into the snapshot by `IndexIssue` so a later parent-probe in the same run sees them. The pagination loop stops on the authoritative `next_page_results == false` flag rather than the cursor alone ([:806-816](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L806-L816), flag defined at [src/ThroughlineBuild.Plane/PlaneApiModels.cs:71-78](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs#L71-L78)) - Plane echoes an advancing cursor past the last page, so a cursor-only loop walked to the page cap on every load.

### Other caches held in memory per invocation

- State name -> UUID map: lazy-loaded on first transition, semaphore-guarded.
- Label name -> UUID map: lazy-loaded on first label application, case-insensitive matching.
- Issue-type name -> UUID map: lazy-loaded on first ticket create with a type.

None of the caches (snapshot included) persists across invocations - the binary exits between calls, so each `build` run reloads.

### Capabilities advertised

```csharp
BackendCapabilities(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)
```

[src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:114-118](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L114-L118). No caller in the repo actually reads this today - capability-driven dispatch is plumbed at the type level but unused at runtime.

### Handshake when missing or unauthenticated

- **No token in config or env:** `BuildConfigLoader.ResolveSecrets` throws `ConfigException` with message `plane_api_token not set in config and required environment variable '<env_name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:167](../../src/ThroughlineBuild.Cli/Config.cs#L167)). The CLI catches it eagerly and exits 3, prefixing the message with `Secret error:` ([src/ThroughlineBuild.Cli/Program.cs:213](../../src/ThroughlineBuild.Cli/Program.cs#L213)).
- **Unauthorized (401/403) response from Plane:** raised as `PlaneApiException(status, body)` and surfaces as a phase failure with exit 1.
- **Rate limit (429) or transient 5xx:** the throttle makes a client-side 429 nearly impossible; a server-origin 429 or 5xx is retried up to `MaxRetryAttempts` (default 5) times by Polly before raising ([src/ThroughlineBuild.Plane/PlaneClientOptions.cs:26](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L26)).
- **Workspace or project UUID wrong:** Plane returns 404 - same path as unauthorized.
- **State not installed in the project:** `TransitionAsync` / `TransitionLifecycleAsync` warn to stderr and leave the ticket where it is rather than throwing ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:450-457](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L450-L457)).
- **Network unreachable:** `HttpClient` throws `HttpRequestException`; depending on the call site, surfaces as `PhaseInfraFailure` or propagates to CLI as an uncaught exception (exit 1).

### Loose ends - Plane

- **Reverse state name map** at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:271-280](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L271-L280) and the forward `switch` in each transition method hardcode the seven state names (`Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`). A Plane workspace with different state names reads everything as `Backlog` and skips transitions with a stderr warning.
- **Rollup ranking** (`StateRank`, [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1048](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1048)) and `ApplyRollupRules` ([:1060](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1060)) hardcode priority ordering; no extensibility for custom state hierarchies.
- **`[rollup]` comment marker** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:640](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L640)) is load-bearing for the rollup comment format - no versioning if the format changes.
- **`Ticket.Risk`** is always returned as `Risk.Medium` from Plane reads ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:403](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L403)). `Ticket.Size` now IS extracted from a `size:s|m|l` label ([:387-394](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L387-L394)) - the architecture doc's claim that size is always `M` is stale.
- **Page cap of 50** (`MaxListPages`, 5000 issues) still bounds the snapshot load, but truncation is no longer silent: if the cap is hit with a live cursor, `FetchAllIssuesAsync` writes a loud stderr warning that the snapshot is truncated and lookups beyond the cap will throw "not found" ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:828-832](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L828-L832)). Very large projects must raise the cap or narrow the project.
- **Snapshot staleness across processes:** the write-through snapshot only reflects mutations made by *this* client instance. A concurrent second `build` process mutating the same project will not be seen until the next run reloads. Within a single run this is correct; across runs it is reload-on-start.

---

## Anthropic API (LLM judgment slot)

Status: Functional but fully optional (single production caller, degrades gracefully when absent).

Direct REST calls. Still exactly one production caller: `ReasonTranslator` for `close` / `defer` / `reopen` - this is the **only** LLM consumer left in the deterministic CLI, and it is now non-essential. The hard API-key gate was removed in TLB-227 (2c04bf9 / 042f963): instead of a top-level check, the client is now built lazily by `LlmClientFactory.Create` only when one of those three verbs runs ([src/ThroughlineBuild.Cli/LlmClientFactory.cs](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs), invoked from `WireUpConditionalCommands` at [src/ThroughlineBuild.Cli/Program.cs:1710-1738](../../src/ThroughlineBuild.Cli/Program.cs#L1710-L1738)). All other verbs never touch the Anthropic REST API - workers reach Anthropic through the `claude` CLI's own OAuth.

TLB-371 (0b017fb) went one step further: when the factory throws because no key/model is configured, `WireUpConditionalCommands` no longer aborts. It catches the `ConfigException`, logs `WARNING: LLM unavailable (...); recording reason verbatim without translation.`, and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the last user message unchanged ([src/ThroughlineBuild.Cli/Program.cs:1727-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1727-L1737)). The reason is recorded verbatim and the ticket transition still runs. So `close` / `defer` / `reopen` work with no Anthropic key at all - only non-English reason text would go untranslated.

The production path goes through `AnthropicClient` (implements `ILlmClient`):

- **Implementation:** [src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs).
- **Base URL:** `https://api.anthropic.com` ([src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:7](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L7)).
- **Endpoint:** `POST /v1/messages` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:52](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52)).
- **Auth headers:** `x-api-key` from `AnthropicOptions.ApiKey`; `anthropic-version` from `AnthropicOptions.ApiVersion`, default `2023-06-01` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:56-57](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L56-L57), default at [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:6](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L6)). The version is now a settable option rather than a string literal, though `LlmClientFactory` does not expose a config knob for it.
- **Vendor gating:** `LlmClientFactory` only accepts `[llm] default_model` values starting `anthropic:`; an empty model or any other prefix throws `ConfigException` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L29)) - but with TLB-371 that throw is now caught and downgraded to the `EchoLlmClient` fallback (see above) for `close` / `defer` / `reopen`, so it no longer aborts the verb.
- **Models:** invoked with the raw model id after stripping the `anthropic:` prefix ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:29-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L29-L31)). `ReasonTranslator` pins `claude-haiku-4-5-20251001` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)).
- **Retry:** Polly 3 retries on 429 / 5xx with exponential backoff ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:102-114](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L102-L114)).
- **Streaming:** `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### Newer non-streaming + streaming path: `IModelClient` (TLB-244/245) - not yet wired

Status: Partial (built and tested, no production caller).

A parallel client abstraction `IModelClient` ([src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs)) was added with an Anthropic implementation `AnthropicModelClient` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs)). Unlike `AnthropicClient`, its `StreamAsync` is fully implemented: it sets `stream:true`, reads the SSE response with `HttpCompletionOption.ResponseHeadersRead`, and maps `content_block_delta` / `message_delta` / `message_start` / `message_stop` / `error` events into `ModelStreamEvent` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs:82-180](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs#L82-L180)). Auth and headers come from a `ProviderConfig` (`AuthScheme` + `ExtraHeaders`) rather than the fixed `AnthropicOptions` shape. A `ModelClientLlmAdapter` bridges `IModelClient` back to `ILlmClient` but still stubs its own `InvokeStreamAsync` ([src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs:65-72](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65-L72)). None of `AnthropicModelClient` / `ModelClientLlmAdapter` / `IModelClient` is constructed by `Program.cs` or the factory today - the only `ILlmClient` the CLI builds is `AnthropicClient`.

### Handshake when missing or unauthenticated

- **No API key:** only `close` / `defer` / `reopen` ever ask for it, and as of TLB-371 they no longer fail without it. `LlmClientFactory.Create` still throws `ConfigException` (message `anthropic_api_key not set and env var '<env>' is not set; configure [llm] anthropic_api_key or set the env var`, or `LLM client required but [llm] default_model is not set in config.toml` when no model is configured at all - [src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-19](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L19)), but `WireUpConditionalCommands` catches it, prints the `WARNING: LLM unavailable (...); recording reason verbatim` line, and swaps in `EchoLlmClient` ([src/ThroughlineBuild.Cli/Program.cs:1727-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1727-L1737)). The verb runs to completion and exits 0; the reason is stored untranslated. There is no longer any `Secret error:` exit-3 path for a missing Anthropic key, nor the old `"anthropic api key required for close/defer/reopen (reason translation)"` wording.
- **401/403:** `AnthropicApiException(status, body)` propagates; verb exits with phase failure.
- **Rate limit:** Polly retries.

### Loose ends - Anthropic

- **`anthropic-version` is settable but not config-wired** - `AnthropicOptions.ApiVersion` exists, but `LlmClientFactory` never reads a config value for it, so it is effectively pinned to the default until a future wiring change.
- **`AnthropicClient.InvokeStreamAsync` still unimplemented** even though `AnthropicModelClient.StreamAsync` proves the streaming path. The two clients are not yet reconciled and the streaming one is dead code at runtime.
- **No request-id capture** - the Claude Code worker explicitly notes `anthropic_request_id` is unavailable in its stream envelope ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:325](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L325)); the REST `AnthropicClient` likewise does not surface `anthropic-request-id` into `LlmResponse`.

---

## Worker CLIs (`claude`, `codex`, `gemini`, `copilot`)

Status: Functional (all four agents). Which CLI must be installed depends on `[workers] default_agent` in the live config, not on a hardcoded vendor default.

**Which external CLI the repo requires depends on config.** There is no hardcoded vendor default in C# - `default_agent` is a required string ([src/ThroughlineBuild.Cli/Config.cs:478](../../src/ThroughlineBuild.Cli/Config.cs#L478)) and `WorkerAgentFactory` dispatches off whatever name is configured. The two live answers diverge:

- **Checked-in operator config (`.build/config.toml`) now defaults to `codex`** ([.build/config.toml:25](../../.build/config.toml#L25), commit 420d9c4). The `[workers.codex]` block is uncommented (executable `codex`; sizes small=`gpt-5.4-mini`, medium=`gpt-5.4`, large=`gpt-5.5` - [.build/config.toml:47-56](../../.build/config.toml#L47-L56)). So this repo, as configured, expects the **`codex`** CLI on PATH.
- **The shipped template and example still default to `claude-code`** ([src/ThroughlineBuild.Commands/Templates/config.toml.template:23](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L23), [.build/config.toml.example:25](../../.build/config.toml.example#L25)). A fresh `build init` therefore generates a `claude-code` default, expecting the **`claude`** CLI on PATH.

The four agents and their executables/flags are otherwise unchanged (see the per-CLI table below).

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
| `codex` | `codex` | positional prompt arg | `exec [--dangerously-bypass-approvals-and-sandbox] ExtraArgs [--model M] "<brief>"` ([:180-193](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L180-L193)) | plain text; raw stdout scanned for `WORKER_RESULT` ([:137-161](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L137-L161)) | `CODEX_API_KEY`, `OPENAI_API_KEY` ([:163-171](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L163-L171)) |
| `gemini` | `gemini` | `-p` prompt arg | `-p "<brief>" --output-format json [--yolo] [--model M] ExtraArgs` ([:224-236](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L224-L236)) | JSON envelope `{response, stats}`; `.response` text run through `WorkerResultParser`, raw-stdout fallback ([:163-217](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L163-L217)) | `GEMINI_API_KEY`, `GOOGLE_API_KEY` ([:264-271](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L264-L271)) |
| `copilot` | `copilot` | `-p` prompt arg | `-p "<brief>" -s --no-ask-user ExtraArgs [--model M] [--allow-tool T ...]` ([:22-35](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L35)) | plain text; raw stdout scanned for `WORKER_RESULT` | none stripped; additive `GH_TOKEN` |

Notes on the flag variants:

- Claude Code requires `--verbose` alongside `--print --output-format stream-json` (the CLI rejects the combination otherwise); the bypass flag is `--dangerously-skip-permissions`, emitted only when `ClaudeCodeOptions.BypassPermissions` is true (default true) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:357-387](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L357-L387)).
- The bypass flag is per-vendor: codex `--dangerously-bypass-approvals-and-sandbox`, gemini `--yolo`, copilot `-s --no-ask-user` (always emitted). Each agent's model prefix differs: `anthropic:` (claude-code), `openai:` (codex), `google:` (gemini), `github:` (copilot).
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
- **Default agent depends on config**: the checked-in `.build/config.toml` selects `codex` (420d9c4); the template/example `build init` generates still select `claude-code`. All four agents are real, unit-tested, and selectable per phase via `[workers.phases]`; see 04-configuration.md and 07-contracts.md.

---

## NuGet packages

Direct dependencies only (verify by grepping `PackageReference` across the `.csproj` files). All 19 production projects target `net10.0` (`LangVersion 14`, `Nullable enable`); only `Cli` sets `PublishAot=true`.

### Cli project ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj))

- **`Tomlyn 0.16.0`** - TOML parser for `.build/config.toml`. Selected because it is AOT-friendly (architecture Appendix item 2). This is the only third-party package the AOT binary links ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:41](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L41)). Its reflection-based trim warning (`IL2104`) is suppressed via `NoWarn` because only the dynamic-model API is reachable ([:13-20](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L13-L20)).

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
| Codex / Gemini / Copilot worker agents | Now implemented as real `IWorkerAgent`s (`CodexAgent`, `GeminiAgent`, `CopilotAgent`), unit-tested. The checked-in `.build/config.toml` selects `codex` as default; the template/example still default to `claude-code`. Token/cost metadata is partial for codex/gemini/copilot. |
| MCP server packaging | Architecture Appendix item 3 calls for stubbing it; no stub today. |
| `bin/notify` shim | Referenced in user-global `CLAUDE.md` for agent notifications; this repo has no `bin/notify` script (the shim lives in the operator's home, not the project). |

---

## Loose ends

- **No central dependency manifest** - the dependency graph is per-`.csproj`, not pinned at solution level. Operators upgrading versions touch ~14 files.
- **No SBOM** generated by build or CI.
- **No retry budget telemetry** - the Polly pipelines on Plane and Anthropic clients log nothing about how often retries actually fire.
- **`bin/notify`** shim referenced by global agent conventions is not provided by this repo; the binary itself never notifies.
