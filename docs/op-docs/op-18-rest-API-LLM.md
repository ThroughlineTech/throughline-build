# Operation: model-rest-client

A provider-neutral REST client for model APIs (`IModelClient`), modeled on the Anthropic Messages API shape but genericized so OpenAI-compatible, Ollama, and BYOK endpoints can be added later by configuration plus a thin adapter. This is the transport layer that future HTTP-based workers will sit on. It is deliberately NOT an `IWorkerAgent` and NOT an agentic tool loop - a raw model endpoint, unlike the CLI agents, has no agent loop of its own, so turning model calls into a worker (driving tools, looping to completion, emitting `WORKER_RESULT`) is a separate follow-on op-doc.

The Anthropic shape is the reference because it is the richest and best-understood, and because the codebase already has an `AnthropicClient` (consumed by `ReasonTranslator` via `[llm]`) to generalize from rather than invent against. Provider adapters and the HTTP agent loop are the "fill in with more research" work and are out of scope here.

## Why this exists

op-14 deliberately left the door open for HTTP/non-CLI workers: `IWorkerProgressDigester` is nullable, and the factory/config/sizing surface is transport-agnostic. The CLI agents (claude-code, codex, gemini, copilot) each bring their own agentic loop; a model REST endpoint does not. So an HTTP worker needs two layers: a model-API client (this op-doc) and, later, a loop that drives tools around it. Building the client now, genericized and tested, gives the later HTTP-agent work a clean transport and gives the existing direct-model consumers (`ReasonTranslator` and any other `AnthropicClient` callers) a provider-neutral path. "Genericize as much as possible" means the interface and the internal request/response/usage/event types are provider-neutral; the Anthropic wire format is the first concrete implementation, and adding a provider is a new `IModelClient` (or adapter) plus config, not a contract change.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Abstraction + Anthropic implementation: interface, neutral types, non-streaming + streaming, usage mapping, provider config | - | M |
| B | Integration + tests: generalize existing AnthropicClient onto IModelClient; serialization/parse/SSE/usage tests | A | M |

Plan A sequential. Plan B depends on A.

## Plan A: Abstraction + Anthropic implementation

### Goal

`ThroughlineBuild.ModelClient` exists with a provider-neutral `IModelClient`, neutral request/response/usage/stream-event types modeled on the Anthropic Messages API, an `AnthropicModelClient` implementing it (non-streaming and streaming), a normalized usage mapping that matches op-14's `llm_usage` shape, and a provider-config record that captures everything a new provider needs (base URL, auth scheme, headers, vendor string). AOT-clean throughout.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | model-client-contract | IModelClient + neutral ModelRequest/ModelResponse/Usage/ModelStreamEvent types + JsonSerializerContext + ProviderConfig record | - | src/ThroughlineBuild.ModelClient/ (new), throughline-build.sln |
| 02 | anthropic-client-nonstreaming | AnthropicModelClient.SendAsync: HttpClient, headers, auth, base URL, request serialization, response parse | 01 | src/ThroughlineBuild.ModelClient/AnthropicModelClient.cs |
| 03 | anthropic-client-streaming | AnthropicModelClient.StreamAsync: SSE parse into the neutral ModelStreamEvent stream | 02 | src/ThroughlineBuild.ModelClient/AnthropicModelClient.cs |
| 04 | usage-and-provider-config | Normalized Usage mapping to the llm_usage shape; ProviderConfig drives base URL / auth / headers / vendor | 02 | src/ThroughlineBuild.ModelClient/ |

### Briefs - detail

#### Brief 01: model-client-contract

Goal: Define the provider-neutral surface. These types are Anthropic-shaped but carry no Anthropic-specific wire details; the wire mapping lives in each implementation.

Inputs: the Anthropic Messages API request/response/SSE shape; the existing `AnthropicClient` in the codebase; op-14's `llm_usage` metadata shape (vendor, model, input/output/cache tokens, optional cost); Contracts.

Outputs:
- `ThroughlineBuild.ModelClient` classlib in the `.sln`, depending only on Contracts (and `System.Net.Http`).
- `IModelClient`:
  - `Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct)`
  - `IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct)`
- Neutral request type `ModelRequest`: model id, optional system prompt, ordered messages (role + content blocks), max output tokens, optional temperature / stop sequences / tool definitions, stream flag.
- Neutral content-block type covering at least text and tool-use/tool-result, extensible for future block kinds.
- Neutral `ModelResponse`: content blocks, stop reason, echoed model, `Usage`.
- Neutral `Usage`: input / output / cache-read / cache-create tokens, model, vendor; optional cost. Shaped to map 1:1 onto op-14's `llm_usage`.
- Neutral `ModelStreamEvent`: a typed set covering message-start, content delta, message-delta (carrying incremental usage / stop reason), message-stop, and error - the union the Anthropic SSE events normalize into and other providers will too.
- `ProviderConfig` record: base URL, auth scheme (header name + value source, e.g. `x-api-key` vs `Authorization: Bearer` vs none), extra static headers (e.g. an API-version header), the vendor string, default timeout.
- All wire DTOs and neutral types that get serialized registered in a source-gen `JsonSerializerContext`.

Acceptance:
- [ ] `ThroughlineBuild.ModelClient` builds, is in the solution, depends only on Contracts + HTTP
- [ ] `IModelClient` with both methods exists
- [ ] Neutral request/response/usage/stream-event/content-block types exist and carry no provider-specific wire fields
- [ ] `ProviderConfig` captures base URL, auth scheme, headers, vendor, timeout
- [ ] Serializable types registered in a source-gen context; AOT publish succeeds

Notes: Keep the neutral types minimal but honest to the Anthropic shape so the first implementation is a thin map. Resist adding provider-specific fields to the neutral types - those belong in the implementation's wire DTOs.

OOS: Implementing any client (B02/B03). Provider adapters other than Anthropic. The agent loop / tool execution. Migrating existing consumers (B05).

#### Brief 02: anthropic-client-nonstreaming

Goal: `AnthropicModelClient.SendAsync` performs a non-streaming Messages call and returns a neutral `ModelResponse`.

Inputs: the neutral types and `ProviderConfig` from B01; the existing `AnthropicClient` for header/auth/base-URL details; Anthropic Messages request/response wire shape.

Outputs:
- `AnthropicModelClient : IModelClient` constructed from a `ProviderConfig` (and an `HttpClient` / handler for testability).
- `SendAsync` serializes `ModelRequest` to the Anthropic wire request, sets headers/auth/base URL from config, posts, deserializes the wire response into the neutral `ModelResponse`, maps usage.
- Anthropic wire DTOs live here (not in the neutral types), registered in this assembly's JSON context.
- Errors (non-2xx, malformed body) surface as a typed exception, not a silent null.

Acceptance:
- [ ] `SendAsync` performs a real Messages call and returns a populated neutral `ModelResponse`
- [ ] Headers/auth/base URL come from `ProviderConfig`
- [ ] Wire DTOs are local to the implementation and registered for source-gen
- [ ] Non-2xx / malformed responses raise a typed error
- [ ] AOT publish succeeds

Notes: This is the behavior-preserving generalization of the existing `AnthropicClient`'s request path. Keep the HttpClient injectable so B06 can test against captured responses without a network.

OOS: Streaming (B03); usage-mapping polish beyond what SendAsync needs (B04); consumer migration (B05).

#### Brief 03: anthropic-client-streaming

Goal: `AnthropicModelClient.StreamAsync` consumes the Anthropic SSE stream and yields neutral `ModelStreamEvent`s.

Inputs: B02's client; the neutral `ModelStreamEvent` set; Anthropic SSE event sequence (message_start, content_block_start/delta/stop, message_delta, message_stop, ping, error).

Outputs:
- `StreamAsync` issues the streaming request, parses the SSE line protocol, and maps each Anthropic event to the neutral event (text deltas -> content delta; message_delta usage/stop -> message-delta event; terminal -> message-stop; errors -> error event), honoring cancellation.
- Incremental usage from the stream is surfaced on the appropriate neutral events so a caller can assemble a final `Usage`.

Acceptance:
- [ ] `StreamAsync` yields neutral events for a real streaming call
- [ ] Anthropic SSE event types map to the neutral set, including terminal usage and errors
- [ ] Cancellation stops the stream promptly
- [ ] AOT publish succeeds

Notes: The neutral event set is the contract other providers' streaming will normalize into; keep the mapping in the implementation, not the neutral types.

OOS: Provider-specific stream formats other than Anthropic SSE; the digester (HTTP workers wire a digester separately if at all).

#### Brief 04: usage-and-provider-config

Goal: A normalized `Usage` that drops straight into op-14's `llm_usage`, and a `ProviderConfig` proven to carry everything a non-Anthropic provider would need.

Inputs: op-14 `llm_usage` shape; the neutral `Usage`; `ProviderConfig` from B01.

Outputs:
- A mapping from neutral `Usage` to the `llm_usage` metadata dict (vendor, model, input/output/cache tokens, optional cost) so an HTTP worker emits events identical in shape to the CLI agents.
- `ProviderConfig` exercised by constructing (config-only, no live call) clients for at least: Anthropic (x-api-key + version header), an OpenAI-compatible shape (Authorization Bearer), and a no-auth local shape (Ollama-style base URL) - proving the config is sufficient even though only the Anthropic client is implemented.
- Documentation of which `ProviderConfig` fields each shape sets, as the template for the future adapters.

Acceptance:
- [ ] Neutral `Usage` maps to the `llm_usage` dict shape op-14 emits
- [ ] `ProviderConfig` can express Anthropic, OpenAI-compatible, and no-auth-local auth/header/base-URL shapes
- [ ] The three shapes are documented as the adapter template
- [ ] cost is optional and null where the provider does not report USD

Notes: This is the genericization proof - the config models three providers even though only one client exists. It de-risks the later adapters without building them.

OOS: Implementing non-Anthropic clients/adapters; reading provider config from `.build/config.toml` (that lands with the first HTTP worker that needs it).

## Plan B: Integration + tests

### Goal

The existing direct-model consumers run on `IModelClient` without behavior change, and the client is covered by tests against captured fixtures (no network).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | generalize-existing-anthropic-consumers | Reimplement the existing AnthropicClient consumers (ReasonTranslator, etc.) on IModelClient, behavior-preserving | A | src/ThroughlineBuild.* (existing AnthropicClient call sites) |
| 06 | model-client-tests | Request serialization, response parse, SSE parse, usage mapping, error handling - against captured fixtures | A | tests/ThroughlineBuild.ModelClient.Tests/ (new) |

### Briefs - detail

#### Brief 05: generalize-existing-anthropic-consumers

Goal: Move existing direct-Anthropic callers onto `IModelClient` / `AnthropicModelClient`, preserving behavior, so there is one model-transport path.

Inputs: the existing `AnthropicClient` and its consumers (`ReasonTranslator` and any others); the `[llm]` config that feeds them.

Outputs:
- Existing consumers construct an `AnthropicModelClient` from a `ProviderConfig` built from `[llm]` and call `IModelClient` instead of the old `AnthropicClient`.
- The old `AnthropicClient` is removed or reduced to a thin shim over `AnthropicModelClient` (implementer's choice), with no behavior change to its consumers.
- `[llm]` semantics unchanged (this is transport refactor, not a config change).

Acceptance:
- [ ] Existing consumers use `IModelClient`; their behavior is unchanged
- [ ] `[llm]` config still drives them identically
- [ ] Existing tests for those consumers pass
- [ ] No duplicate Anthropic HTTP path remains (old client removed or shimmed)

Notes: If this turns out to touch more surface than expected or risks behavior drift, it is splittable from the abstraction - the abstraction (Plan A) plus tests (B06) stand alone. Surface that rather than forcing a risky migration.

OOS: Changing `[llm]` schema or semantics; adding new model features to consumers; provider adapters.

#### Brief 06: model-client-tests

Goal: Cover the client against captured wire fixtures without a network.

Inputs: captured Anthropic non-streaming responses and SSE streams (real, sanitized); an injectable HttpMessageHandler.

Outputs:
- Tests for: `ModelRequest` -> Anthropic wire request serialization; wire response -> neutral `ModelResponse` parse; SSE stream -> neutral `ModelStreamEvent` sequence; neutral `Usage` -> `llm_usage` mapping; non-2xx / malformed -> typed error.
- AOT-disabled-reflection discipline where serialization is exercised, so tests reflect AOT behavior.
- Fixtures are real captured wire output, sanitized of secrets, not synthesized.

Acceptance:
- [ ] Serialization, response parse, SSE parse, usage mapping, and error cases are covered against captured fixtures
- [ ] No network access in tests (injected handler)
- [ ] Source-gen serialization exercised with reflection disabled
- [ ] Fixtures are real, sanitized wire output

OOS: Live integration tests; tests for unimplemented providers.

## What done looks like

`ThroughlineBuild.ModelClient` provides a provider-neutral `IModelClient` with neutral request/response/usage/stream-event types modeled on the Anthropic Messages API. `AnthropicModelClient` implements it for both non-streaming and streaming, driven entirely by a `ProviderConfig` that has been shown to express Anthropic, OpenAI-compatible, and no-auth-local shapes. Neutral `Usage` maps straight onto op-14's `llm_usage`, so a future HTTP worker emits events shaped like the CLI agents. The existing direct-Anthropic consumers run on this one path with no behavior change, and the client is tested against captured fixtures with no network. Adding a real provider later (Ollama, an OpenAI-compatible endpoint, a BYOK gateway) is a new `IModelClient` implementation or adapter plus a `ProviderConfig` - and the agentic loop that turns it into a worker is the separate HTTP-agent op-doc this transport was built to carry.