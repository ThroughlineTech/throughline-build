# 11 - LLM Architecture

How `build` talks to LLMs today, the two distinct interfaces it uses, where vendor-specific code lives, and what it takes to add a new provider (OpenAI, Google, Codex, Gemini, Ollama, etc.) - either against the existing interfaces or by refactoring them.

For dependency detail on the current providers see [03-external-dependencies.md](03-external-dependencies.md). For the inter-project type contracts see [07-contracts.md](07-contracts.md).

---

## Two LLM contact surfaces

The architecture (Section 3) defines three tiers of LLM contact: deterministic (no LLM), judgment slots (small scoped API calls), agentic work (full agent CLI in a worktree). In code, only the **last two** touch an LLM, and they use **two completely different interfaces**:

| Tier | Interface | Lives in | Implementations today |
|---|---|---|---|
| Judgment slot | `ILlmClient` | [src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs) | `AnthropicClient` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs)) |
| Agentic work | `IWorkerAgent` | [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `ClaudeCodeAgent` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs)) |

This distinction is **deliberate and load-bearing**. The two interfaces serve different shapes of work:

- `ILlmClient` is a request-response API call against a vendor REST endpoint. It carries a list of messages, a system prompt, and a max-tokens cap, and returns a single text response plus token usage. Each call is short-lived (single HTTP request), stateless, in-process.
- `IWorkerAgent` is a subprocess spawn against a vendor CLI. It hands the agent a `Brief` (a markdown instruction) and a `workingDirectory`, then watches the subprocess run an entire tool loop (file reads, edits, shell commands, etc.) inside that directory until the agent emits a terminal `WORKER_RESULT` JSON envelope. Each call is long-lived (minutes), process-bearing, and produces side effects on the filesystem.

A new provider may need one, the other, or both. The two interfaces do **not** share a dispatcher today.

---

## `ILlmClient` (judgment slots)

### The contract

[src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs):

- `Task<LlmResponse> InvokeAsync(string modelId, IReadOnlyList<LlmMessage> messages, InvocationOptions options, CancellationToken ct)` - the production path.
- `IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(...)` - declared but stubbed today.

Supporting records:

- `LlmMessage(Role, Content)` - role is a free-form string; the architecture mentions `"user"`, `"assistant"`, `"system"` but the type does not enforce.
- `InvocationOptions(MaxTokens?, Temperature?, System?)` - all optional; `System` is the system prompt forwarded to the vendor.
- `LlmResponse(Content, Usage)` - one text response.
- `LlmUsage(InputTokens, OutputTokens, CacheReadTokens?, CacheWriteTokens?)` - cache fields are Anthropic-specific in spirit (named after their headers) but documented as generic.
- `LlmStreamEvent` hierarchy: `LlmStreamTextDelta`, `LlmStreamUsage`, `LlmStreamDone`.

### The only implementation

`AnthropicClient` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs)) does:

- POST `{BaseUrl}/v1/messages` with `x-api-key` and `anthropic-version` headers ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:52-57](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52-L57)).
- Strips `anthropic:` prefix from `modelId` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:28-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L28-L31)).
- Polly retry: 3 retries on 429 + 5xx with exponential backoff ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:102-114](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L102-L114)).
- Picks the first `text`-typed block out of `content[]` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:77-80](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L77-L80)).
- Source-gen JSON via `AnthropicJsonContext` ([src/ThroughlineBuild.Anthropic/AnthropicApiModels.cs:37-44](../../src/ThroughlineBuild.Anthropic/AnthropicApiModels.cs#L37-L44)) for AOT.
- `InvokeStreamAsync` throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### The only production consumer

`ReasonTranslator` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs)) - translates operator reason text to English when running `close` / `defer` / `reopen`. It hardcodes the model:

```csharp
public const string ModelId = "claude-haiku-4-5-20251001";  // ReasonTranslator.cs:14
```

Wiring lives in `Program.cs.WireUpConditionalCommands` ([src/ThroughlineBuild.Cli/Program.cs:1118-1171](../../src/ThroughlineBuild.Cli/Program.cs#L1118-L1171)):

```csharp
var anthropicClient = new AnthropicClient(http, new AnthropicOptions { ApiKey = secrets.AnthropicApiKey });
var translator = new ReasonTranslator(anthropicClient);
```

There is **no dispatcher**. The consumer takes a concrete `AnthropicClient` instance and calls it directly, passing the bare model id. The interface exists - vendor neutrality is at the interface boundary - but no code today picks an `ILlmClient` implementation from a model id at runtime.

---

## `IWorkerAgent` (agentic work)

### The contract

[src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs):

- `string Name { get; }` - identifier like `"claude-code"`, `"codex"`, `"gemini"` (documented in the XML doc on line 12).
- `Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)`.

Supporting types:

- `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` - the unit of work. Built by `*BriefBuilder` classes from per-phase templates.
- `WorkerOptions(Timeout, AllowedTools?, EnvironmentVariables?, DebugCaptureDirectory?, LiveStdoutSink?, LiveStderrSink?, ProgressDigestSink?)` - process-level controls; some fields are Claude-Code-aware (the `AllowedTools` flag is a Claude Code CLI concept).
- `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata)` - parsed from a `WORKER_RESULT` envelope that the agent must emit at the end of its session.

### The only implementation

`ClaudeCodeAgent` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs)). What it does, with Claude-Code-specific bits called out:

1. Writes the brief to `.build/brief.md` for post-mortem diagnostics ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:22-25](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L22-L25)).
2. Builds the argv:
   ```
   <claude> --print --verbose --output-format stream-json
            [--allowedTools <comma-list>]
            [--model <bare-model-id>]
            [<ExtraArgs>]
   ```
   `--output-format stream-json` + `--verbose` is a Claude-Code-specific contract (the CLI rejects the combo otherwise). The terminal `type=result` NDJSON event is bit-for-bit identical to the legacy `--output-format json` blob, so envelope parsing is shared ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:28-33](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L28-L33)).
3. Strips `anthropic:` from the model id before passing to `--model` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:355-368](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L355-L368)).
4. **Removes `ANTHROPIC_API_KEY`** from the child env so Claude Code uses its OAuth subscription, not per-token billing ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:374](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L374)).
5. Sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` if configured ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:377-378](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L377-L378)).
6. Delivers the brief on stdin, closes stdin to signal EOF ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:97-98](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L97-L98)).
7. Reads stdout as NDJSON: each line is parsed via `ClaudeCodeStreamEvent` for the live digest, and the final `type=result` envelope is extracted via `ClaudeCodeJsonEnvelope` for `WorkerResult` synthesis.
8. **Scans stdout in reverse** for the `WORKER_RESULT` marker (so the last envelope wins) and parses the JSON payload after it via `WorkerResultParser` ([src/ThroughlineBuild.Workers.ClaudeCode/WorkerResultParser.cs:26-38](../../src/ThroughlineBuild.Workers.ClaudeCode/WorkerResultParser.cs#L26-L38)).
9. Builds an `llm_usage` metadata dictionary with `vendor: "anthropic"` hardcoded ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:327-353](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L327-L353)).

### Wiring

Every phase that dispatches a worker takes an `IWorkerAgent` constructor parameter. In `Program.cs`, the same `ClaudeCodeAgent` instance is constructed once per invocation ([src/ThroughlineBuild.Cli/Program.cs:640-646](../../src/ThroughlineBuild.Cli/Program.cs#L640-L646)) and shared across phases:

```csharp
var worker = new ClaudeCodeAgent(new ClaudeCodeOptions
{
    ExecutablePath = config2.Workers.ClaudeCodeExecutable,
    MaxOutputTokens = config2.Workers.MaxOutputTokens,
    Model = config2.Llm.DefaultModel,
    DefaultModel = config2.Llm.DefaultModel
});
```

There is no agent selection logic. `config.workers.default_agent` is read into `BuildOptions.WorkerName` but never used to **pick** an agent - the only constructed agent is `ClaudeCodeAgent`.

---

## Model id convention

Across both interfaces, model identifiers follow the architecture's `vendor:model` convention:

```
anthropic:claude-opus-4-7
anthropic:claude-sonnet-4-6
anthropic:claude-haiku-4-5-20251001
```

The prefix is intended as a routing key but **no router exists today**. The two consumers that resolve model ids handle the prefix independently:

- `AnthropicClient.InvokeAsync` strips `anthropic:` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:28-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L28-L31)).
- `ClaudeCodeAgent.NormalizeModel` strips `anthropic:` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:355-368](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L355-L368)).

A new prefix (e.g., `openai:`) would be silently passed through to whichever client received it, then rejected by the vendor API. No code today inspects the prefix to choose a client.

---

## Where vendor-specific code lives

A map of every Claude / Anthropic-specific touchpoint a port would need to mirror or replace:

### `ILlmClient` path

| File | What is Anthropic-specific |
|---|---|
| `src/ThroughlineBuild.Anthropic/AnthropicClient.cs` | All of it. Endpoint path `/v1/messages`, headers `x-api-key` + `anthropic-version`, request/response shapes, content-block extraction. |
| `src/ThroughlineBuild.Anthropic/AnthropicApiModels.cs` | `AnthropicRequest`/`Response`/`Message`/`ContentBlock`/`Usage` records mirror the Anthropic REST shapes; `AnthropicJsonContext` is the source-gen JSON context. |
| `src/ThroughlineBuild.Anthropic/AnthropicOptions.cs` | `ApiVersion = "2023-06-01"` hardcoded; `BaseUrl` defaults to Anthropic. |
| `src/ThroughlineBuild.Anthropic/AnthropicApiException.cs` | Vendor-named exception. |
| `src/ThroughlineBuild.Contracts/ILlmClient.cs` | `LlmUsage.CacheReadTokens` / `CacheWriteTokens` are named after Anthropic prompt-caching headers - the field names leak vendor vocabulary into the contract. |

### `IWorkerAgent` path

| File | What is Claude-Code-specific |
|---|---|
| `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs` | CLI argv (`--print --verbose --output-format stream-json --allowedTools --model`), env var stripping (`ANTHROPIC_API_KEY` removal + `CLAUDE_CODE_MAX_OUTPUT_TOKENS`), stdin-delivery convention, stream NDJSON parsing, model-prefix normalization, `vendor: "anthropic"` hardcoded in usage metadata. |
| `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs` | `type=result` envelope schema. |
| `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeStreamEvent.cs` | NDJSON stream-event schema (`system`, `assistant`, `user`, `rate_limit_event`). |
| `src/ThroughlineBuild.Workers.ClaudeCode/WorkerProgressDigest.cs` | Tool name shortcuts (`file_path`, `pattern`, `command`, `path`, `url`) match Claude Code tools. |
| `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs` | `ExecutablePath = "claude"`. |
| `src/ThroughlineBuild.Contracts/IWorkerAgent.cs` | `WorkerOptions.AllowedTools` is a Claude-Code CLI concept (`--allowedTools` flag); other agents may not have an equivalent. |

### Vendor-neutral contracts that **don't** need to change

These contracts are deliberately tool-shape-neutral. A new provider works against them as-is:

- The `WORKER_RESULT` JSON envelope ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs:43-63](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs#L43-L63)) - any worker that emits this shape can be parsed by the same `WorkerResultParser`. Re-using `WorkerResultParser` from a new agent requires the new agent to instruct its model to emit `WORKER_RESULT` + JSON, which the existing brief templates already do.
- The brief templates ([src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/) - `plan.md`, `implement.md`, `review.md`, `draft.md`) - vendor-neutral markdown with `{{variable}}` substitution. They instruct the model to emit a fenced WORKER_RESULT block. A different vendor's model might need its own template if its tool-loop conventions differ, but the substitution mechanism is shared.
- The phase classes (`PlanPhase`, `ImplementPhase`, `ReviewPhase`, `DraftPhase`) take `IWorkerAgent` as a constructor param and do not depend on its concrete type.
- `Brief`, `WorkerResult`, `Status`, `Verdict` records in `ThroughlineBuild.Contracts`.

---

## Current consumers and how they pick a model

A complete map of every site that invokes an LLM:

| Site | Interface | Model source | Vendor-specific? |
|---|---|---|---|
| [src/ThroughlineBuild.Cli/Program.cs:1135](../../src/ThroughlineBuild.Cli/Program.cs#L1135) (`WireUpConditionalCommands`) | `ILlmClient` via `AnthropicClient` | `AnthropicOptions.ApiKey` from `secrets.AnthropicApiKey`; model hardcoded in `ReasonTranslator.ModelId` | yes - direct `new AnthropicClient(...)` |
| [src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:14, 22](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L14) | `ILlmClient` | hardcoded `claude-haiku-4-5-20251001` | model id is Claude-specific |
| [src/ThroughlineBuild.Cli/Program.cs:640-646](../../src/ThroughlineBuild.Cli/Program.cs#L640-L646) (every phase) | `IWorkerAgent` via `ClaudeCodeAgent` | `config.workers["claude-code"].executable`, `config.llm.default_model` | yes - direct `new ClaudeCodeAgent(...)` |
| [src/ThroughlineBuild.Cli/Program.cs:424-430](../../src/ThroughlineBuild.Cli/Program.cs#L424-L430) (`build new` draft mode) | `IWorkerAgent` via separate `ClaudeCodeAgent` for `DraftPhase` | same | yes |
| [src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs](../../src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs) (the `IVerifier`) | `IWorkerAgent` (wraps it) | passed-in worker - typically same `ClaudeCodeAgent` | named for Claude but the agent is injected |

`ClaudeCodeReviewer` is a particular case: it implements `IVerifier`, but internally it wraps an `IWorkerAgent` (any agent) plus a `ReviewBriefBuilder`. The class **name** is Claude-Code-specific because that is the only agent it has ever been used with; the **dependency** is just `IWorkerAgent`. Architecture Section 5.8 calls out that cross-vendor verifiers (a Gemini verifier reviewing a Claude implementation) are deferred to v1.1 - the interface allows it but no second implementation exists.

---

## What is **missing** to add a new provider

The current state is "one vendor per surface, wired by direct construction." The named gap is the **dispatcher / router** that takes a `vendor:model` string and returns the right `ILlmClient` or `IWorkerAgent`. Specifically:

### Gap 1: no `ILlmClient` router

There is no `LlmClientFactory(modelId) -> ILlmClient`. `ReasonTranslator` is handed a concrete `AnthropicClient` instance. If you add `OpenAIClient` and want `ReasonTranslator` to pick by model id, you need either:

- a router (`Func<string, ILlmClient>` or a typed factory) injected into `ReasonTranslator` and any future judgment-slot consumer, **or**
- a separate `ReasonTranslator` per vendor (more wiring, fewer abstractions, fine for 1-2 judgment slots).

Either way, `Program.cs.WireUpConditionalCommands` ([src/ThroughlineBuild.Cli/Program.cs:1118-1171](../../src/ThroughlineBuild.Cli/Program.cs#L1118-L1171)) is the single site that needs to change to do the picking.

### Gap 2: no `IWorkerAgent` registry

`config.workers.default_agent` is read but never consulted to select an agent ([src/ThroughlineBuild.Cli/Config.cs:202-207](../../src/ThroughlineBuild.Cli/Config.cs#L202-L207), then unused at dispatch time). `Program.cs` always constructs `ClaudeCodeAgent`. To add a Codex / Gemini / Ollama worker you need:

- An `AgentName -> IWorkerAgent` registry, or a switch statement in `Program.cs` keyed off `default_agent`.
- Per-agent options in `[workers]` (executable path, model, env, extra args) - either a sub-table per agent (`[workers.claude-code]`, `[workers.codex]`) or a single active block.
- A way to set per-phase agent overrides (the architecture mentions "Claude Code for planning, Codex for implementation, Gemini for review" - that requires per-phase agent selection, not just a default).

### Gap 3: `ILlmClient` and `IWorkerAgent` are independent

A vendor that ships both a REST API and an agent CLI (Anthropic does, OpenAI does via Codex CLI, Google does via Gemini CLI) needs **two** classes today - one per interface. There is no shared "AnthropicProvider" type that exposes both surfaces. This is a deliberate split (the two surfaces have very different shapes) but means adding a vendor requires N implementations.

### Gap 4: vendor names leak into shared contracts

A few field names in `Contracts` carry Anthropic vocabulary:

- `LlmUsage.CacheReadTokens` / `CacheWriteTokens` are named after `cache_read_input_tokens` / `cache_creation_input_tokens` Anthropic headers. Other vendors expose prompt caching differently (or not at all). A future contract may want `PromptCacheReadTokens` etc. or a generic `IReadOnlyDictionary<string,int>` for vendor-specific counters.
- `WorkerOptions.AllowedTools` is a Claude-Code CLI flag concept. Codex / Gemini CLIs have different (or no) equivalent.
- `BuildLlmUsageMetadata` hardcodes `"vendor": "anthropic"` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:332](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L332)) - new agents need to emit their own vendor string into the event log so `analyze-event-log` can price correctly.

### Gap 5: `[llm]` config is single-vendor

[src/ThroughlineBuild.Cli/Config.cs:189-197](../../src/ThroughlineBuild.Cli/Config.cs#L189-L197) reads `[llm]` as a single block with one `anthropic_api_key`. Multi-vendor needs either per-vendor sub-tables (`[llm.anthropic]`, `[llm.openai]`) or a different shape.

---

## Decision matrix: how to add a provider X

Use this to pick a path before opening the IDE.

### Question 1: which surface does X expose?

- **REST API only** (judgment slot): you only need an `ILlmClient` impl. Add `XClient : ILlmClient` next to `AnthropicClient`.
- **Agent CLI only** (agentic work): you only need an `IWorkerAgent` impl. Add `XAgent : IWorkerAgent` next to `ClaudeCodeAgent`.
- **Both** (typical for major vendors): add both. They will be wired independently.

### Question 2: do existing interfaces fit, or do they need to change?

For **`ILlmClient`**:

| Provider type | Existing interface fits? | What to do |
|---|---|---|
| OpenAI / xAI / DeepSeek (chat completions, similar shape to Anthropic Messages) | yes | Implement `XClient : ILlmClient`. Map `LlmMessage` -> vendor request, vendor response -> `LlmResponse`. Decide what to do with `LlmUsage.CacheReadTokens` / `CacheWriteTokens` (set to null if no equivalent). |
| Google Gemini (different message structure, parts-based content) | mostly fits | Same pattern. Concatenate parts into a single string for `LlmResponse.Content`, or extend the contract to expose a parts list if you need it. |
| Ollama / local llama.cpp (no auth, different endpoint, simpler usage data) | fits | Same pattern. `LlmUsage` cache fields would be null. |
| Multi-modal (images, audio) | does NOT fit | `LlmMessage.Content` is `string`. Extend to `IReadOnlyList<ContentPart>` or add a sibling interface. Affects every existing implementation - this is a breaking change. |
| Tool-use / function-calling | does NOT fit | The current contract has no notion of tool definitions or tool-call responses. Add a sibling interface (`ILlmToolClient`?) or refactor `ILlmClient` to carry tool args. Either way, breaking. |

For **`IWorkerAgent`**:

| Provider type | Existing interface fits? | What to do |
|---|---|---|
| Codex CLI (subprocess, deliver brief, get back transcript) | fits with adaptations | Implement `CodexAgent : IWorkerAgent`. Convert the brief to whatever shape `codex exec --print` wants. Instruct the model in the brief to emit `WORKER_RESULT` JSON envelope so `WorkerResultParser` can be reused. Decide what to do with `WorkerOptions.AllowedTools` (drop, or map to Codex's equivalent). Strip whatever env var would override Codex's auth (e.g., `OPENAI_API_KEY` if Codex has OAuth). |
| Gemini CLI | same pattern | Same approach. |
| Aider, Continue, etc. (different stdio conventions) | may not fit | If the CLI does not produce a stream of structured events and a parseable terminal envelope, you need different parsing - extend the contract or add a per-agent parser. |
| MCP server (long-lived, JSON-RPC) | does NOT fit | `IWorkerAgent` is one-shot subprocess. An MCP server is a persistent process with bidirectional RPC. Either wrap it (spawn-per-call) or add a sibling interface for persistent agents. |

### Question 3: do you need per-phase agent picking?

The architecture goal is "Claude Code for planning, Codex for implementation, Gemini for review." If yes:

- Phase constructors already take `IWorkerAgent` as a param, so the wiring is feasible.
- You need to refactor `Program.cs` to construct multiple agents and pass the right one per phase.
- Configuration needs per-phase overrides: today the closest thing is `[review.verifier_allowed_tools]` ([src/ThroughlineBuild.Cli/Config.cs:230](../../src/ThroughlineBuild.Cli/Config.cs#L230)), but no per-phase `agent` key exists.

If no (one agent for everything), the registry from Gap 2 is sufficient.

---

## A concrete add-an-OpenAI-judgment-slot checklist

This is the minimal change to support `openai:gpt-5` in `ReasonTranslator` alongside the current Anthropic path, with no refactor of the shared interfaces:

1. Create `src/ThroughlineBuild.OpenAI/` (new project, mirror `ThroughlineBuild.Anthropic.csproj` shape).
2. Implement `OpenAIClient : ILlmClient`:
   - Strip `openai:` prefix from `modelId`.
   - POST `https://api.openai.com/v1/chat/completions` with `Authorization: Bearer <key>` header.
   - Map `LlmMessage` -> OpenAI's `messages` array (system prompt becomes a first `system` role message if `InvocationOptions.System` is set).
   - Map response: pick `choices[0].message.content` for `LlmResponse.Content`. Set `LlmUsage` from `usage.prompt_tokens` / `usage.completion_tokens`; `CacheReadTokens` / `CacheWriteTokens` null.
   - Source-gen JSON context for AOT.
   - Polly retry on 429 + 5xx, same as `AnthropicClient`.
3. Add `OpenAIApiException`.
4. Add the project reference to `ThroughlineBuild.Cli.csproj`.
5. Extend `[llm]` config in `Config.cs` and `.build/config.toml.example` to carry `openai_api_key` (or `openai_api_key_env`).
6. Add a small `ILlmClient ResolveLlmClient(string modelId, BuildSecrets secrets, HttpClient http)` helper in `Program.cs` that picks based on the prefix in `ReasonTranslator.ModelId`.
7. Update `WireUpConditionalCommands` ([src/ThroughlineBuild.Cli/Program.cs:1118-1171](../../src/ThroughlineBuild.Cli/Program.cs#L1118-L1171)) to call the resolver instead of constructing `AnthropicClient` directly.
8. Test: stub `ILlmClient` in `tests/ThroughlineBuild.JudgmentSlots.Tests/` is already abstract over vendor.

If `ReasonTranslator.ModelId` is changed from a constant to a config-driven value, then `[judgment_slots]` config support is the broader change; until then, the model is pinned to Haiku.

---

## A concrete add-a-Codex-worker checklist

This is the minimal change to support a Codex CLI worker alongside Claude Code, without per-phase picking:

1. Create `src/ThroughlineBuild.Workers.Codex/`.
2. Implement `CodexAgent : IWorkerAgent`:
   - `Name => "codex"`.
   - Spawn `codex exec --print` (or whatever Codex's non-interactive flag is).
   - Convert the brief: most likely deliver on stdin, same as Claude Code.
   - Set env: remove `OPENAI_API_KEY` if Codex uses OAuth (matching the Claude Code pattern), or leave it if Codex needs the API key.
   - Capture stdout/stderr the same way.
   - Re-use `WorkerResultParser` to scan for `WORKER_RESULT` (the brief templates already instruct the worker to emit this envelope).
   - Build `llm_usage` metadata with `vendor: "openai"`.
   - Wire AOT JSON contexts for any Codex-specific envelope types.
3. Extend `[workers]` config:
   - Either replace the flat block with `[workers.claude-code]` / `[workers.codex]` sub-tables, or
   - Add `[codex_executable]`, `[codex_model]`, etc. alongside the existing keys.
   - Either way, `default_agent` becomes load-bearing.
4. In `Program.cs`, replace the direct `new ClaudeCodeAgent(...)` with:
   ```
   IWorkerAgent worker = config2.Workers.DefaultAgent switch
   {
       "claude-code" => new ClaudeCodeAgent(...),
       "codex"       => new CodexAgent(...),
       _             => throw new ConfigException($"unknown agent '{...}'")
   };
   ```
5. If per-phase agent picking is wanted later: add `[chain.plan_agent]` / `[chain.implement_agent]` etc., and pass the resolved agent into each phase factory in `ChainPhase` wiring ([src/ThroughlineBuild.Cli/Program.cs:899-940](../../src/ThroughlineBuild.Cli/Program.cs#L899-L940)).
6. The brief templates **may** need a Codex variant if Codex's tool conventions differ enough that the existing instructions confuse it. Start by reusing them; fork only if needed.

---

## Recommended refactor sequence (architectural perspective)

If the goal is "support OpenAI for judgment slots and Codex for at least one phase," the cleanest order:

1. **Build the registry** before the second implementation. Add `ILlmClientFactory` and `IWorkerAgentFactory` (or simple `Func<>` delegates) so the second implementation has somewhere to plug in. Keep the existing `AnthropicClient` / `ClaudeCodeAgent` wiring working through the registry.
2. **Refactor `[llm]` and `[workers]` config shape** to admit multiple providers. Per-vendor sub-tables are the lowest-friction choice. Default to the current single-vendor shape so existing operators are not broken.
3. **Move the model-prefix parsing** out of `AnthropicClient` and `ClaudeCodeAgent.NormalizeModel` into a shared helper (or into the registry itself). Today the same logic exists in two places.
4. **Add the new implementations** (`OpenAIClient`, `CodexAgent` or whichever you start with). Vendor-specific code stays inside the new project; no edits to `Contracts` if the existing interfaces fit.
5. **Decide on the `LlmUsage` cache fields.** Either leave them Anthropic-shaped and let other vendors set null, or extend the contract to a generic counter dictionary. The first is faster; the second is the architecturally clean answer.
6. **Wire the verifier as cross-vendor.** Once a second worker exists, `ClaudeCodeReviewer` becomes mis-named - rename to `WorkerAgentReviewer` or similar, since it depends only on `IWorkerAgent`. This is the architecture's "cross-vendor verification" v1.1 item (Section 5.8).
7. **Add per-phase agent selection** last, only when needed. The phase constructors already accept `IWorkerAgent`, so the type plumbing is in place; this is purely a config + wiring change.

Skipping the registry (step 1) and adding implementations directly produces a Program.cs that grows a `switch` for every vendor, in every wiring site. The registry is the cheapest abstraction that prevents that.

---

## Loose ends

- **`ILlmClient.InvokeStreamAsync` is unused and stubbed** ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)). New providers can implement or also stub; consider removing from the interface if no caller materializes.
- **`config.workers.default_agent` is read but never consulted to pick an agent** ([src/ThroughlineBuild.Cli/Program.cs:640-646](../../src/ThroughlineBuild.Cli/Program.cs#L640-L646)). First-class agent registry would close this gap.
- **`vendor: "anthropic"` is hardcoded** in `BuildLlmUsageMetadata` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:332](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L332)). `analyze-event-log` keys its pricing table off this string; per-agent vendor strings are needed for accurate cost rollups across mixed-vendor sessions.
- **`ReasonTranslator.ModelId` is a `const`** ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:14](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L14)). Switching judgment-slot vendors per call requires either constructor-injected model id or per-call override.
- **`LlmUsage` cache fields** are vendor-shaped. Decide whether to keep them or replace with a generic map before adding the second `ILlmClient` impl - retrofitting later means migrating the event log.
- **`WorkerOptions.AllowedTools`** is Claude-Code-specific. Other agents may ignore it; a future contract could replace it with an opaque `IReadOnlyDictionary<string, object>` of agent-specific options.
- **No tool-use / function-calling support** in `ILlmClient`. Judgment-slot use cases are simple today; if a future slot needs structured output, the contract gets revisited.
- **No MCP server adapter**. Architecture Appendix item 3 contemplates `build` itself as an MCP server; an MCP-server-as-worker adapter would also be a separate animal from `IWorkerAgent`.
- **`docs/op-docs/op-14-new-agent-foundation.md`** is an empty stub - the multi-agent foundation work is tracked there but unfilled.
