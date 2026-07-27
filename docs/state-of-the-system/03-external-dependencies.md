# 03 - External Dependencies

Last refreshed: 2026-07-26 (HEAD 00dc074)

Every service, API, CLI, and runtime library this repo depends on; what specific endpoints or tools it touches; what happens when the dependency is missing or unauthenticated.

For configuration of the credentials and base URLs, see [04-configuration.md](04-configuration.md). For failure-mode detail per phase, see [09-failure-modes.md](09-failure-modes.md).

---

## Plane (ticketing backend)

**Primary contract.** Every ticket-bearing verb in `build` reads from and writes to Plane.

### How `build` talks to Plane

Status: Functional.

Single implementation: `PlaneTicketingClient`, which now implements four interfaces - `ITicketing`, `ITicketingProvisioner`, `ITicketingConnectivity`, and `IProjectDiscovery` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:18](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L18)). It wraps `System.Net.Http.HttpClient` with two retry layers plus a throttle:

1. **HTTP-status retry (Polly).** A `ResiliencePipelineBuilder` pipeline retries on `PlaneApiException` where status is 429 or >= 500: up to `PlaneClientOptions.MaxRetryAttempts` (default 5) attempts with exponential-with-jitter backoff (base `RetryBaseDelay` 2s), and when a 429 carries `Retry-After` the pipeline waits exactly that long, clamped to `MaxRetryDelay` (default 60s) ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:84-116](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L84-L116); option defaults in [src/ThroughlineBuild.Plane/PlaneClientOptions.cs:26-38](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L26-L38)).
2. **Transport retry + environmental classification (TLB-545).** Every HTTP request is built, sent, and body-read in one funnel, `SendWithTransportRetryAsync`, so transport-class failures (DNS, connect, TLS, reset, HttpClient timeout) are retried in one place: up to `PlaneClientOptions.TransportRetryAttempts` (default 3) with exponential backoff (base `TransportRetryBaseDelay` 2s, cap `TransportMaxRetryDelay` 10s, +/-25% jitter) ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:256-285](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L256-L285), [src/ThroughlineBuild.Plane/PlaneClientOptions.cs:40-54](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L40-L54)). Retry eligibility is verb-aware in `IsRetryableTransportError`: pre-send failures (DNS/connect/TLS/proxy) retry for any verb; failures after the request may have been processed (mid-response reset, protocol error, timeout) retry only idempotent GET/PATCH - a re-POST could double-create an issue or comment ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:306-330](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L306-L330)). When retries are exhausted (or the shape is non-retryable) the failure surfaces as `TicketingUnavailableException` ([src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs](../../src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs)) - see "Handshake" below.
3. **Rate throttle.** See next section.

- **Base URL:** configurable via `ticketing.plane_base_url`. `PlaneClientOptions.BaseUrl` defaults to `https://api.plane.so` ([src/ThroughlineBuild.Plane/PlaneClientOptions.cs:5](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L5)).
- **Auth:** `X-API-Key` header, value from `ticketing.plane_api_token` (or env, default name `PLANE_API_TOKEN`). Set in the `PlaneTicketingClient` constructor ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:79](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L79)).
- **Endpoint base paths:** the private properties `IssuesBase`, `StatesBase`, `LabelsBase`, and `IssueTypesBase` build `api/v1/workspaces/{slug}/projects/{id}/...` roots ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:197-207](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L197-L207)); `ProjectsBase` builds the workspace-level `api/v1/workspaces/{slug}/projects/` root for discovery ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:480](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L480)).

### Rate throttle (TLB-327, f3953f7)

Status: Functional.

Every HTTP send blocks on a shared `RequestThrottle` before it goes out, capped at `PlaneClientOptions.RequestsPerMinute` (default 40) on a rolling one-minute window ([src/ThroughlineBuild.Plane/RequestThrottle.cs](../../src/ThroughlineBuild.Plane/RequestThrottle.cs), constructed at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:81](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L81)). When the budget is spent the call awaits until the oldest in-window send ages out. This gate is **per-process**: Plane enforces its real 60/min limit server-side and globally per API token, so the throttle cannot coordinate across concurrent `build` instances. The 40/min default leaves headroom for a second instance sharing the token; if Plane still returns a 429, the Polly pipeline backs off on `Retry-After` and recovers. Transport retries re-acquire the throttle per attempt so retries still respect the budget ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:261](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L261)). This is a hard pre-send gate distinct from both retry layers; all three are active.

### Endpoints touched (ITicketing surface)

The `ITicketing` members and the route each one hits are listed below. Ticket reads begin at `PlaneTicketingClient.GetAsync` ([PlaneTicketingClient.cs:831](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L831)); relation operations use the native Plane issue-relation endpoint through `ListRelationsAsync`, `CreateRelationAsync`, and `RemoveRelationAsync` in the same client.

| Method | HTTP | Path | Used by |
|---|---|---|---|
| `GetAsync(id)` | (snapshot; GET pagination on first call) | `issues/?per_page=100` then in-memory | every phase fetches the ticket |
| `GetBatchAsync(ids)` | (in-memory after snapshot) | parallel `GetAsync` | multi-ticket chain dependency graph |
| `TransitionAsync(id, state)` | PATCH | `issues/{uuid}/` | all phase transitions |
| `AppendDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` | `PlanPhase` (read-modify-write append) |
| `UpdateDescriptionAsync(id, html)` | PATCH | `issues/{uuid}/` | description replace (TLB-251) |
| `CreateCommentAsync(id, html)` | POST | `issues/{uuid}/comments/` | every phase posts at least one comment |
| `ApplyLabelsAsync(id, labels)` | PATCH | `issues/{uuid}/` | `PlanPhase` (risk/size), `AmendCommand`, `ScaffoldPhase` |
| `GetRelationsAsync(id)` / `ListRelationsAsync(id)` | GET | `issues/{uuid}/issue-relation/` | chain dependency reads / explicit relation listing |
| `GetCommentsAsync(id)` | GET | `issues/{uuid}/comments/` (404 -> empty) | marker parsing, reopen-marker detection |
| `RollupParentAsync(id)` | GET (`?expand=state`) + PATCH + POST | mixed | `CloseCommand`, `DeferCommand`, auto parent completion |
| `CreateTicketAsync(title, type, html, labels)` | POST | `issues/` | `NewPhase`, `ScaffoldPhase` |
| `SetParentAsync(child, parent)` | PATCH | `issues/{uuid}/` (`parent` field) | `ScaffoldPhase` |
| `QueryAsync(query)` | (in-memory after snapshot) | filters `_issueByUuid.Values` client-side | tree walk, child detection, chain (TLB-251) |
| `TransitionLifecycleAsync(id, transition, reason)` | POST + PATCH | `issues/{uuid}/comments/` then `issues/{uuid}/` | `close` / `defer` / `reopen` (TLB-251) |
| `CreateChildTicketsAsync(parent, children)` | POST (per child) | `issues/` with `parent` set | `ScaffoldPhase` / `DecomposePhase` (TLB-262) |
| `CreateRelationAsync(source, kind, target)` | POST | `issues/{uuid}/issue-relation/` | `build relate`, scaffold dependency edges |
| `RemoveRelationAsync(source, relationId)` | DELETE | `issues/{uuid}/issue-relation/{relation-id}/` | `build relate --remove` |

Notes on specific members:

- **`CreateChildTicketsAsync` (TLB-262):** batched sub-issue creation. Never throws - per-child failures are collected into `CreateChildTicketsResult.Failures`, unknown label names are silently skipped, and each created child is seeded into the snapshot via `IndexIssue` so a later parent-probe in the same run sees it ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1223-1304](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1223-L1304)).
- **Issue-type NAME -> UUID resolution (TLB-213/214):** `CreateTicketAsync` resolves a `type` string against the `IssueTypesBase` cache and PATCHes the resolved UUID; an unknown type throws `InvalidOperationException` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:943](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L943)).
- **Per-run snapshot cache + correct pagination (TLB-366):** Plane silently ignores the list endpoint's query filters (notably `parent=`), so the whole project is paginated once into an in-memory snapshot and all lookups are answered client-side - see next section.

### Provisioning, connectivity, and project discovery (build init / build setup)

Status: Functional. New surface since the last refresh; consumed by `build setup`, connected `build init`, and scaffold preflight.

- **`ITicketingProvisioner`** - `ListStatesAsync` (GET `states/`), `ListLabelNamesAsync` (GET `labels/`), `CreateStateAsync` (POST `states/`), `CreateLabelAsync` (POST `labels/`) ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:449-474](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L449-L474)). `SetupCommand` diffs these against `WorkspaceSchema` (7 states, 9 labels) and creates what is missing (see [02-install-build-run.md](02-install-build-run.md)).
- **`ITicketingConnectivity.TestConnectivityAsync`** - GETs `labels/` and `states/`, then runs `ProbeIssueCreatePermissionAsync`: a deliberately invalid POST to `issues/` (name field has the wrong type) that distinguishes "token can reach create validation" (400/422 = OK) from "token cannot create" (401/403) without ever creating an issue ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:124-195](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L124-L195)). Scaffold runs this before any ticket writes so a bad token fails before partial creation.
- **`IProjectDiscovery`** - `ListProjectsAsync` (GET `projects/?per_page=100`, cursor-paginated with a loud warning at the page cap), `FindProjectByNameAsync`, and `CreateProjectAsync` (POST `projects/`) at the workspace level ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:477-545](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L477-L545)). `ProjectResolver.ResolveAsync` composes find-or-create on top and reports which path was taken; `ProjectResolver.DeriveIdentifier` derives the 2-10 char uppercase Plane identifier offered as the interactive default ([src/ThroughlineBuild.Plane/ProjectResolver.cs:46-81](../../src/ThroughlineBuild.Plane/ProjectResolver.cs#L46-L81)).
- **Actionable 404:** `PlaneTicketingClient.BuildProjectNotFoundMessage` renders a 404 on any project-scoped route as "plane_project_id '<id>' does not resolve to a project in workspace '<slug>' ... re-run 'build init' connected mode" instead of Plane's raw "Page not found." ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:166-169](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L166-L169)); `build setup` and the connectivity probe share it.

### Deep-link URL (TLB-292, 9450889)

Status: Functional.

The Plane work-item URL printed to the operator uses the `?next_path=` redirect pattern: `{base}/?next_path=/{workspaceSlug}/browse/{ticketId}`, built by the local function `BuildPlaneUrl` ([src/ThroughlineBuild.Cli/Program.cs:2166-2173](../../src/ThroughlineBuild.Cli/Program.cs#L2166-L2173)) and duplicated in `NewCommand` ([src/ThroughlineBuild.Commands/NewCommand.cs](../../src/ThroughlineBuild.Commands/NewCommand.cs)). Empty if any of base URL / slug / ticket id is unset.

### Per-run issue snapshot cache (TLB-366)

Status: Functional.

The dominant cache is the issue snapshot. On the first lookup that needs it, `EnsureSnapshotAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:575](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L575)) paginates the entire project once and indexes it into two `ConcurrentDictionary` fields ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:48-59](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L48-L59)):

- `_seqToUuid` (`int -> string`): sequence-id -> issue UUID, write-once identity index.
- `_issueByUuid` (`string -> PlaneIssue`): UUID -> full issue, the mutable source of truth.

Load is single-flight (double-checked `SemaphoreSlim` so concurrent callers share one load). Thereafter `FindIssueAsync` ([:560](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L560)) answers seq lookups from the two indexes with no network call, throwing `KeyNotFoundException` for an unknown seq, and `QueryAsync` filters `_issueByUuid.Values` in memory ([:1023](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1023)).

Every mutating call (`TransitionAsync`, `AppendDescriptionAsync`, `ApplyLabelsAsync`, `RollupParentAsync`, `SetParentAsync`, `TransitionLifecycleAsync`, `UpdateDescriptionAsync`) performs a **write-through** update so the snapshot stays current for the rest of the run: `UpdateCachedIssue` runs the mutation inside `ConcurrentDictionary.AddOrUpdate` with a pure `Func<PlaneIssue,PlaneIssue>` closure ([:634-641](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L634-L641)), so two concurrent field updates compose rather than clobber. Newly created tickets are seeded into the snapshot by `IndexIssue` ([:608-611](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L608-L611)). The pagination loop in `FetchAllIssuesAsync` stops on the authoritative `next_page_results == false` flag rather than the cursor alone - Plane echoes an advancing cursor past the last page, so a cursor-only loop walked to the page cap on every load.

### Other caches held in memory per invocation

- State name -> UUID map: lazy-loaded on first transition, semaphore-guarded.
- Label name -> UUID map: lazy-loaded on first label application, case-insensitive matching.
- Issue-type name -> UUID map: lazy-loaded on first ticket create with a type.

None of the caches (snapshot included) persists across invocations - the binary exits between calls, so each `build` run reloads.

### Capabilities advertised

The `Capabilities` property returns `BackendCapabilities(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:118](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L118)). No caller in the repo actually reads this today - capability-driven dispatch is plumbed at the type level but unused at runtime.

### Handshake when missing or unauthenticated

- **No token in config or env:** `BuildConfigLoader.ResolveSecrets` throws `ConfigException` with message `plane_api_token not set in config and required environment variable '<env_name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:186-189](../../src/ThroughlineBuild.Cli/Config.cs#L186-L189)). The CLI catches it eagerly and exits 3, prefixing the message with `Secret error:` ([src/ThroughlineBuild.Cli/Program.cs:454-464](../../src/ThroughlineBuild.Cli/Program.cs#L454-L464)).
- **Unauthorized (401/403) response from Plane:** raised as `PlaneApiException(status, body)` and surfaces as a phase failure with exit 1. During `build setup`/connectivity probing, 401/403 is rendered as a "token is not authorized to create issues" message instead ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:133-138](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L133-L138)).
- **Rate limit (429) or transient 5xx:** retried up to `MaxRetryAttempts` (default 5) by the Polly pipeline before raising.
- **Workspace or project UUID wrong:** Plane returns 404. `build setup`, connected `build init`, and the connectivity probe map it to the actionable `BuildProjectNotFoundMessage` remedy; phase verbs still surface it as a `PlaneApiException` failure.
- **Network unreachable / DNS / TLS / timeout (TLB-545):** retried by the transport funnel, then wrapped in `TicketingUnavailableException`. `ChainPhase` catches it at the per-ticket boundary and classifies it as environmental: the chain stops cleanly with `ChainOutcome.TicketingUnavailable` (the ticket's work is already committed to its branch, so the run is resumable) and remaining siblings/roots are marked `Skipped` instead of the process crashing ([src/ThroughlineBuild.Phases/ChainPhase.cs:166-181](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L166-L181), outcome defined in [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs)). Verbs without that boundary still surface the exception as a failure.
- **State not installed in the project:** `TransitionAsync` / `TransitionLifecycleAsync` warn to stderr (`Warning: Plane project has no '<state>' state; leaving <id> in its current state.`) and leave the ticket where it is rather than throwing ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:727-735](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L727-L735)). `build setup` exists to create the missing states up front.

### Loose ends - Plane

- **State names are hardcoded** - the reverse map `_stateNameMap` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:440](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L440)) and the forward `switch` in each transition method pin the seven names (`Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`), now also canonized in `WorkspaceSchema.States`. A workspace with different names reads everything as `Backlog` and skips transitions with a stderr warning; `build setup` creates the standard names but does not rename non-standard ones.
- **Rollup ranking** (`StateRank`, [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1336](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1336)) and `ApplyRollupRules` ([:1348](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1348)) hardcode priority ordering; no extensibility for custom state hierarchies.
- **`[rollup]` comment marker** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:924-925](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L924-L925)) is load-bearing for the rollup comment format - no versioning if the format changes.
- **Ticket classification is label-derived.** `PlaneTicketingClient.ToTicketAsync` resolves `risk:low` and `risk:high`, defaults to Medium when neither is present, and resolves `size:s|m|l` ([PlaneTicketingClient.cs:780](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L780)). Plane issue type now reaches `Ticket.Type`; priority remains a Plane write surface rather than a field on the domain `Ticket`.
- **Page cap of 50** (`MaxListPages`, 5000 issues, [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:1082](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1082)) bounds the snapshot load; truncation is loud (stderr warning that lookups beyond the cap will throw "not found", [:1117-1121](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1117-L1121)) but very large projects must raise the cap or narrow the project. The project-list pagination shares the cap with its own warning.
- **Snapshot staleness across processes:** the write-through snapshot only reflects mutations made by *this* client instance. A concurrent second `build` process mutating the same project will not be seen until the next run reloads.
- **A non-retryable POST transport failure may leave Plane state unknown** - the funnel deliberately refuses to re-POST after a mid-flight failure, so a create that actually landed surfaces as `TicketingUnavailableException` and the operator must check Plane before retrying.

---

## Anthropic API (LLM judgment slot)

Status: Functional but fully optional (single production caller, degrades gracefully when absent).

Direct REST calls. Still exactly one production caller: `ReasonTranslator` for `close` / `defer` / `reopen` - this is the **only** LLM consumer left in the deterministic CLI, and it is non-essential. The client is built lazily by `LlmClientFactory.Create` only when one of those three verbs runs, from `WireUpConditionalCommands` ([src/ThroughlineBuild.Cli/Program.cs:2235-2298](../../src/ThroughlineBuild.Cli/Program.cs#L2235-L2298), [src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-30](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L30)). All other verbs never touch the Anthropic REST API - workers reach Anthropic through the `claude` CLI's own OAuth.

TLB-371 degradation: when the factory throws because no key/model is configured, `WireUpConditionalCommands` does not abort. It catches the `ConfigException`, logs `WARNING: LLM unavailable (...); recording reason verbatim without translation.`, and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the last user message unchanged ([src/ThroughlineBuild.Cli/Program.cs:2252-2262](../../src/ThroughlineBuild.Cli/Program.cs#L2252-L2262)). The reason is recorded verbatim and the ticket transition still runs. So `close` / `defer` / `reopen` work with no Anthropic key at all - only non-English reason text would go untranslated.

The production path goes through `AnthropicClient` (implements `ILlmClient`):

- **Implementation:** [src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs).
- **Base URL:** `AnthropicOptions.BaseUrl`, default `https://api.anthropic.com` ([src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:7](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L7)).
- **Endpoint:** `POST /v1/messages` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:52](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52)).
- **Auth headers:** `x-api-key` from `AnthropicOptions.ApiKey`; `anthropic-version` from `AnthropicOptions.ApiVersion`, default `2023-06-01` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:56-57](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L56-L57), default at [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:6](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L6)). The version is a settable option, but `LlmClientFactory` exposes no config knob for it.
- **Vendor gating:** `LlmClientFactory.Create` only accepts `[llm] default_model` values starting `anthropic:`; an empty model or any other prefix throws `ConfigException` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L29)) - caught and downgraded to the `EchoLlmClient` fallback for `close` / `defer` / `reopen`.
- **Model:** `ReasonTranslator.ModelId` pins `claude-haiku-4-5-20251001`; the system prompt is now loaded from the embedded resource `translate-reason-prompt.md` via `TranslateReasonPromptLoader` rather than a string literal ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:13-16](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L13-L16), [src/ThroughlineBuild.JudgmentSlots/TranslateReasonPromptLoader.cs](../../src/ThroughlineBuild.JudgmentSlots/TranslateReasonPromptLoader.cs)).
- **Retry:** Polly 3 retries on 429 / 5xx with exponential backoff ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:102-114](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L102-L114)).
- **Streaming:** `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### Newer non-streaming + streaming path: `IModelClient` (TLB-244/245) - not yet wired

Status: Partial (built and tested, no production caller). Unchanged since the last refresh.

A parallel client abstraction `IModelClient` ([src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs)) exists with an Anthropic implementation `AnthropicModelClient` whose `StreamAsync` is fully implemented (SSE via `HttpCompletionOption.ResponseHeadersRead`, mapping `content_block_delta` / `message_delta` / `message_start` / `message_stop` / `error` into `ModelStreamEvent`) ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs:82-180](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs#L82-L180)). A `ModelClientLlmAdapter` bridges back to `ILlmClient` but stubs its own `InvokeStreamAsync`. None of these types is constructed by `Program.cs` or the factory - the only `ILlmClient` the CLI builds is `AnthropicClient`.

### Handshake when missing or unauthenticated

- **No API key:** only `close` / `defer` / `reopen` ever ask for it, and they do not fail without it. `LlmClientFactory.Create` throws `ConfigException` (`anthropic_api_key not set and env var '<env>' is not set; ...`, or `LLM client required but [llm] default_model is not set in config.toml`), but `WireUpConditionalCommands` catches it, prints the `WARNING: LLM unavailable` line, and swaps in `EchoLlmClient`. The verb runs to completion and exits 0; the reason is stored untranslated. There is no `Secret error:` exit-3 path for a missing Anthropic key.
- **401/403:** `AnthropicApiException(status, body)` propagates; verb exits with phase failure.
- **Rate limit:** Polly retries.

### Loose ends - Anthropic

- **`anthropic-version` is settable but not config-wired** - effectively pinned to the default until a future wiring change.
- **`AnthropicClient.InvokeStreamAsync` still unimplemented** even though `AnthropicModelClient.StreamAsync` proves the streaming path; the streaming client remains dead code at runtime.
- **No request-id capture** - neither the worker stream envelope nor the REST `AnthropicClient` surfaces `anthropic-request-id` into `LlmResponse`.

---

## Worker CLIs (`claude`, `codex`, `gemini`, `copilot`)

Status: Functional (all four agents). Which CLI must be installed depends on `[workers] default_agent` in the live config, not on a hardcoded vendor default.

**Which external CLI the repo requires depends on config.** `default_agent` is a required string read by `ReadWorkersSection` ([src/ThroughlineBuild.Cli/Config.cs:578](../../src/ThroughlineBuild.Cli/Config.cs#L578)) and `WorkerAgentBuilder.Create` dispatches off whatever name is configured ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)). The shipped `build init` template sets `default_agent = "claude-code"` ([src/ThroughlineBuild.Commands/Templates/config.toml.template:28](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L28)), with active `[workers.codex]` blocks as the configured alternate. The live `.build/config.toml` is gitignored and may select a different agent. Config load fail-fasts when the named default (or a phase agent) has no `[workers.<name>]` sub-table (TLB-512; [src/ThroughlineBuild.Cli/Config.cs:679-686](../../src/ThroughlineBuild.Cli/Config.cs#L679-L686)).

There are four `IWorkerAgent` implementations, one per vendor CLI. Each shells out to a subprocess, delivers the brief, and reads a `WORKER_RESULT` envelope back. The envelope parser is shared in `ThroughlineBuild.Workers.Common` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)); since the last refresh it grew substantially:

- Walks marker lines in reverse so the **last** valid envelope wins, tolerating a template echo earlier in the output ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:124](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L124)).
- A pre-pass extracts **named fenced blocks** (`<<<NAME_START` / `<<<NAME_END`) before the envelope - used for `PROJECT_PROFILE` (scaffold derivation) and `COMPLETION_CLAIM` (gate integration; parsed by `CompletionClaimParser` into provides/consumes/AC-bindings, [src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs)) ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:133](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L133)).
- Parses an optional top-level `tickets` array for batch-implement responses (TLB-447), mapping to `WorkerResult.Tickets` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:211-237](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L211-L237)).
- The Claude Code agent now scans the **full assistant transcript**, not just the terminal result text, for the envelope and fenced blocks (commit 945f4b4).

### Shared subprocess contract

All four agents:

- Spawn with `UseShellExecute=false`, redirected stdin/stdout/stderr, `CreateNoWindow=true`, and stdout/stderr decoding pinned to UTF-8 via `ProcessStreamEncoding.ApplyUtf8` so Windows OEM code pages cannot garble worker output (TLB-439; [src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs:19-24](../../src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs#L19-L24)).
- Strip provider API-key env vars to force subscription/OAuth auth (claude-code, codex, gemini); Copilot is the exception - its auth is additive (`GH_TOKEN` or inherited `gh` keyring credential), not subtractive ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:192-200](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L200)).
- Resolve a per-`WorkerSize` `ModelTier` (model id + optional Codex effort) from `Sizes` and pass it via `--model` after stripping the vendor prefix.
- On `Process.Start` failure (CLI not found), catch `Win32Exception` and return `WorkerResult { Status = Failed, Summary = "Worker executable not found: '<exe>'" }` rather than crashing (commit 0f9d114).
- On timeout (`WorkerOptions.Timeout` -> `CancellationTokenSource.CancelAfter`), kill the process tree (`entireProcessTree: true`, swallowed on failure) and write partial output to the debug-capture directory when present.

### Per-CLI subprocess contract

Claude has two transport shapes. `ClaudeCodeInteractiveTransport.ExecuteAsync` ([ClaudeCodeInteractiveTransport.cs:97](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeInteractiveTransport.cs#L97)) is the product-config default: it launches a fresh interactive terminal session without `--print`, watches Claude's persisted transcript for turn completion, and contains the process tree with ConPTY plus a job object on Windows or a PTY plus process group on Unix. `ClaudeCodePrintTransport.ExecuteAsync` ([ClaudeCodeTransport.cs:31](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTransport.cs#L31)) remains the explicit `transport = "print"` rollback and uses `claude --print --verbose --output-format stream-json`.

The argument builders are `BuildArguments`/equivalent in each agent; the table cites the env-sanitize and arg-build sites in the notes below it.

| Agent (`Name`) | Default exe | Brief delivery | Spawn flags | Stdout shape parsed | Auth env stripped |
|---|---|---|---|---|---|
| `claude-code` | `claude` | stdin | `--print --verbose --output-format stream-json` `[--dangerously-skip-permissions]` `[--allowedTools a,b]` `[--disallowedTools TodoWrite,Task]` `[--model M]` `ExtraArgs` | NDJSON stream; full assistant transcript scanned for `WORKER_RESULT` + fenced blocks; terminal `type=result` envelope carries error state | `ANTHROPIC_API_KEY`; sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` when configured |
| `codex` | `codex` | stdin | `exec --json [--dangerously-bypass-approvals-and-sandbox] ExtraArgs [--model M] [-c model_reasoning_effort=E] -` | JSONL event stream (`--json`); in-band error events surfaced (TLB-490); raw text scanned for `WORKER_RESULT` | `CODEX_API_KEY`, `OPENAI_API_KEY` |
| `gemini` | `gemini` | `-p` prompt arg | `-p "<brief>" --output-format json [--yolo] [--model M] ExtraArgs` | JSON envelope `{response, stats}`; `.response` text run through `WorkerResultParser`, raw-stdout fallback | `GEMINI_API_KEY`, `GOOGLE_API_KEY` |
| `copilot` | `copilot` | `-p` prompt arg | `-p "<brief>" -s --no-ask-user ExtraArgs [--model M] [--allow-tool T ...]` | plain text; raw stdout scanned for `WORKER_RESULT` | none stripped; additive `GH_TOKEN` |

Notes on the flag variants:

- Claude Code print mode requires `--verbose` alongside `--print --output-format stream-json`; `ClaudeCodeAgent.BuildArgs` owns permission, tool, and model arguments ([ClaudeCodeAgent.cs:392](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L392)). Agent/Task sub-agent tools are always disallowed, and lean-planning briefs also disallow TodoWrite.
- Codex runs `exec --json` (JSONL event stream) and appends `-c model_reasoning_effort=<effort>` when the resolved `ModelTier.Effort` is non-empty (op-33); args builder at [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:364-376](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L364-L376), env sanitize at [:338-339](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L338-L339).
- The bypass flag is per-vendor: codex `--dangerously-bypass-approvals-and-sandbox`, gemini `--yolo` ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:245-251](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L245-L251)), copilot `-s --no-ask-user` (always emitted, [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:22-35](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L35)). Each agent's model prefix differs: `anthropic:` (claude-code), `openai:` (codex), `google:` (gemini), `github:` (copilot).
- Copilot maps `AllowedTools` to repeated `--allow-tool <tool>` flags, not a comma list; it has no progress digester (`Digester => null`).
- **Tool allowlist enforcement is honest now (TLB-478):** only claude-code and copilot actually forward an allowlist to their CLI. When `review.verifier_allowed_tools` is configured and the review agent is codex or gemini, `VerifierToolEnforcement.UnenforcedWarning` prints a one-line startup warning that the verifier runs unsandboxed ([src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs:20-30](../../src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs#L20-L30)).

### Model resolution fail-fast (TLB-544)

Two layers prevent an unresolvable Claude Code model from failing opaquely mid-chain:

1. **Config load:** `ClaudeCodeModelValidator.Validate` rejects any `[workers.claude-code.sizes]` model that is neither a tier alias (`haiku`/`sonnet`/`opus`) nor a `claude-*` slug - the canonical trap is `model = "fable"`, which must be the full slug `claude-fable-5`. The failure is a `Config error:` at startup ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs:22-48](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs#L22-L48), wired in `ReadWorkersSection` at [src/ThroughlineBuild.Cli/Config.cs:643-648](../../src/ThroughlineBuild.Cli/Config.cs#L643-L648)).
2. **Runtime:** if the CLI still rejects the model (envelope `is_error` with the "issue with the selected model" phrasing), `ClaudeCodeAgent` classifies it via `TryDescribeInvalidModelError` into an operator-actionable reason naming the model and the config key, instead of a generic escalation ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:420-429](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L420-L429)).

### Provider quota / rate-limit classification (TLB-527)

`ProviderErrorClassifier.Classify` in `Workers.Common` pattern-matches a failed `WorkerResult`'s summary and failure reason against rate-limit/quota, auth, and HTTP-code signatures (vendors disagree on status tagging: codex returns `Failed` via in-band JSONL errors, claude-code returns `Escalate` via the `is_error` envelope), extracts a retry-at timestamp from both the claude (`...|<unix>`) and codex (`try again at <date>`) formats, and deliberately excludes timeouts/cancellations so verifier crashes are unaffected ([src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs)). During review, a classified provider error means the verifier never produced a judgment: `ReviewPhase` emits a `review_provider_unavailable` event and returns a typed result so the chain surfaces `ChainOutcome.ReviewUnavailable` - NOT a Fail verdict; the ticket stays cleanly InReview and is resumable via `build review <id>` once quota returns ([src/ThroughlineBuild.Phases/ReviewPhase.cs:237-255](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L237-L255), [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs:22](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L22)).

### Worker subprocesses outside the phases

- **Scaffold profile derivation.** `build scaffold` (after a successful creation run, unless `--no-profile`) spawns the **default worker** to derive review/ship checks, convention files, and setup steps from the op-doc: `ScaffoldProfileRunner.RunAsync` builds the agent via `WorkerAgentBuilder`, and `ScaffoldProfileDeriver.DeriveAsync` runs it read-only (`AllowedTools: Read/Grep/Glob`, `WorkerSize.Small`, timeout clamped to 1-30 min) expecting a `PROJECT_PROFILE` fenced block ([src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs:17-120](../../src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs#L17-L120), [src/ThroughlineBuild.Scaffold/ScaffoldProfileDeriver.cs:36-76](../../src/ThroughlineBuild.Scaffold/ScaffoldProfileDeriver.cs#L36-L76)). The derivation prompt instructs the worker to emit non-vacuous checks with per-check canaries, hermetic test commands, `role` on every check (including `setup` prerequisite steps), and linter invocations with user-global caches disabled ([src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md](../../src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md)). Best-effort by design: every failure is reported loudly but swallowed - derivation never changes the scaffold exit code. Skipped when the config already looks customized (TLB-491) unless `--force-profile`.
- **Codex model probe.** `build init` and `build models refresh` spawn `codex debug models` via `CodexModelProbe.ProbeAsync` (60s timeout, read-only, no bypass flags, never throws - typed `CodexProbeResult` failure instead) to discover the operator-selectable model slugs and reasoning-effort levels ([src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs:41-60](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs#L41-L60)).

### Handshake when CLI missing or unauthenticated

- **CLI not on PATH:** every agent catches `Win32Exception` from `Process.Start` and returns `Status.Failed` with a reason pointing at `workers.<agent>.executable` in config ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:106](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L106), [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:108-113](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L108-L113), [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:90](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L90), [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:90](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L90)). The phase handles the failed result gracefully.
- **Worker subprocess fails to authenticate or hits quota:** surfaced in-band (codex JSONL error events, claude-code `is_error` envelope with the message included - TLB-490) and classified by `ProviderErrorClassifier` where a caller opts in (review); otherwise `Status.Failed`/`Status.Escalate` with stderr/message in the reason.
- **Worker emits no `WORKER_RESULT` marker:** `Status.Failed` with "No WORKER_RESULT found in output" ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:494](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L494)); such failures are tagged `envelope_status=missing`, and implement-phase callers salvage committed work when git shows real commits despite the missing envelope (TLB-471/476).
- **Worker closes stdin early:** tolerated; the orchestrator no longer crashes (TLB-472).
- **Timeout:** `Status.Failed` with "Process cancelled or timed out"; partial stdout/stderr captured to the debug-capture directory when set.

### Loose ends - worker CLIs

- **Vendor CLI drift** is identified in architecture Section 10 as a top risk. No agent pins a CLI version; each parses whatever shape the current CLI produces (the codex `exec --json` JSONL shape and `debug models` output are two new drift surfaces).
- **Tool-input summarization** in `ClaudeCodeProgressDigester.SummarizeToolInput` hardcodes recognized fields - new Claude Code tools render as bare names. The digester now filters system stream events by subtype and throttles the `thinking_tokens` ticker ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs:96-114](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs#L96-L114)). Codex/Gemini digesters have their own field maps; Copilot has no digester.
- **Token usage parity is improving but uneven:** claude-code emits real input/output/cache counts (and per-turn usage for the context-attribution ledger, exp-4); codex now reports real `input_tokens`, `cached_input_tokens`, and `reasoning_output_tokens` but never USD cost ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:423-449](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L423-L449)); gemini reports only a combined total; copilot reports zeros. The `analyze-event-log` tool prefers its own pricing table over worker-reported `cost_usd` (TLB-547).
- **Per-platform process-tree kill** `entireProcessTree: true` may fail on some platforms and the exception is swallowed.
- **`verifier_allowed_tools` is advisory for codex/gemini** - the warning exists, but the only hard backstop is the post-review git-state guard.

---

## NuGet packages

Direct dependencies only (verify by grepping `PackageReference` across the `.csproj` files). All 20 production projects target `net10.0` (`LangVersion 14`, `Nullable enable`); only `Cli` sets `PublishAot=true`.

### Cli project ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj))

- **`Tomlyn 0.16.0`** - TOML parser for `.build/config.toml`. Selected because it is AOT-friendly (architecture Appendix item 2). It is one of two direct third-party packages linked into the AOT binary; Polly is pulled in through the Plane and Anthropic projects below ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:46](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L46)). Its reflection-based trim warning (`IL2104`) is suppressed via `NoWarn` because only the dynamic-model API is reachable ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:14-23](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L14-L23)).

### Plane and Anthropic clients

- **`Polly 8.*`** - retry resilience, referenced directly by `ThroughlineBuild.Plane` ([src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj:10](../../src/ThroughlineBuild.Plane/ThroughlineBuild.Plane.csproj#L10)) and `ThroughlineBuild.Anthropic` ([src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj:10](../../src/ThroughlineBuild.Anthropic/ThroughlineBuild.Anthropic.csproj#L10)). No other production project references it; `ThroughlineBuild.ModelClient` and `ThroughlineBuild.JudgmentSlots` have no package references.

### Reusable Claude Code package

`ThroughlineBuild.ClaudeCode.csproj` also declares package identity and a custom pack target that includes the facade's referenced implementation binaries while suppressing dependency metadata. This is packaging configuration, not a released dependency: the repository has no `dotnet pack`, NuGet push, signing, or release job. Status: Partial distribution.

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
| `git` | Every phase shells out to it. | Process-start failure at first invocation; surfaces as `InvalidOperationException`. `build setup` can `git init` a fresh directory but still needs the git binary. |
| `git worktree` (>= git 2.5) | All implement/review/ship phases, plus `build sweep`. | "unknown command" from older git; same failure path. |
| ICU data | `<InvariantGlobalization>true</InvariantGlobalization>` is set ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:11](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L11)) so the binary does **not** require ICU at runtime. | n/a |
| OpenSSL / Schannel | TLS for HTTPS to Plane and Anthropic. | Network failure if absent. |

---

## Architecture-named services that are not yet wired

| Named | Status |
|---|---|
| GitHub Issues backend | Plumbed via `BackendCapabilities` but no `GitHubTicketingClient`. |
| Linear backend | An empty `src/ThroughlineBuild.Linear/` directory exists (untracked build leftovers only - no csproj, not in the solution); a linear-integration doc was recovered in the op-doc archive. Aspirational. |
| OpenAI / Google LLM `ILlmClient`s | `ILlmClient` has only `AnthropicClient`; `LlmClientFactory` rejects any non-`anthropic:` prefix. (`AnthropicModelClient` adds an `IModelClient` shape designed for OpenAI/Ollama configs but is unwired.) |
| MCP server packaging | Architecture Appendix item 3 calls for stubbing it; no stub today. |
| `bin/notify` shim | Referenced in user-global `CLAUDE.md` for agent notifications; this repo has no `bin/notify` script (the shim lives in the operator's home, not the project). |

---

## Loose ends

- **No central dependency manifest** - the dependency graph is per-`.csproj`, not pinned at solution level.
- **No SBOM** generated by build or CI.
- **No retry budget telemetry** - the Polly pipelines log per-retry stderr lines but nothing aggregates how often retries fire; the transport funnel likewise only writes per-attempt stderr.
- **`bin/notify`** shim referenced by global agent conventions is not provided by this repo; the binary itself never notifies.
