# 11 - LLM Architecture

How `build` talks to LLMs today, the interfaces it uses, where vendor-specific code lives, and what it takes to add a new provider. The single biggest change since the doc-set baseline is that the **worker layer became genuinely multi-vendor and wired** (four agents selected at runtime), while the **model-client layer gained a second, richer abstraction that is built and tested but not yet wired**. These two multiplicities live at different layers and have very different maturity - keeping them apart is the whole point of this doc.

For dependency detail on the current providers see [03-external-dependencies.md](03-external-dependencies.md). For the inter-project type contracts see [07-contracts.md](07-contracts.md).

---

## Two layers, two maturities

The architecture (Section 3) defines three tiers of LLM contact: deterministic (no LLM), judgment slots (small scoped API calls), agentic work (full agent CLI in a worktree). In code, only the last two touch an LLM. They use different interfaces at different layers:

| Layer | Interface | Lives in | Implementations | Status |
|---|---|---|---|---|
| Worker (agentic CLI subprocess) | `IWorkerAgent` | [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `ClaudeCodeAgent`, `CodexAgent`, `GeminiAgent`, `CopilotAgent` | Functional - all four wired and selected at runtime |
| Model client (judgment-slot REST call) | `ILlmClient` | [src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs) | `AnthropicClient` (production); `ModelClientLlmAdapter` (unwired) | Partial - production path is anthropic-only, non-streaming |
| Model client (newer abstraction) | `IModelClient` | [src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs) | `AnthropicModelClient` (SSE streaming) | Aspirational on the production path - built and tested, never constructed by `build` |

The crucial split:

- **Worker layer is real multi-vendor.** Four `IWorkerAgent` implementations are registered in a factory and chosen per phase from config and CLI flags. This is exercised by tests and is the production code path for `plan` / `implement` / `review` / `chain` / `decompose` / `rework` / draft.
- **Model-client layer is single-vendor on the production path.** The only judgment-slot consumer (`ReasonTranslator`, used by `close` / `defer` / `reopen`) is handed an `ILlmClient` built by `LlmClientFactory`, which constructs `AnthropicClient` directly and rejects any non-`anthropic:` prefix. A newer `IModelClient` abstraction (`AnthropicModelClient` with real SSE streaming, plus a `ModelClientLlmAdapter` that re-presents an `IModelClient` as an `ILlmClient`) exists in `ThroughlineBuild.ModelClient` / `ThroughlineBuild.Anthropic` and is unit-tested, but nothing on the production path constructs it. `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException`.

The interfaces serve different shapes of work and do not share a dispatcher:

- `IWorkerAgent` is a subprocess spawn against a vendor CLI. It hands the agent a `Brief` (a markdown instruction) and a `workingDirectory`, then watches the subprocess run an entire tool loop until it emits a terminal `WORKER_RESULT` JSON envelope. Long-lived (minutes), process-bearing, side-effecting on the filesystem.
- `ILlmClient` / `IModelClient` are request-response (or streaming) API calls against a vendor REST endpoint. Short-lived, stateless, in-process.

---

## The worker layer (real, wired)

### The contract

[src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs):

- `string Name { get; }` - identifier like `"claude-code"`, `"codex"`, `"gemini"`, `"copilot"`. The phase passes this to the brief builder so the agent gets its own template.
- `IWorkerProgressDigester? Digester { get; }` - the agent's per-line digest formatter, or null when the agent has no digest (Copilot returns null). Added so the orchestrator can format a live progress digest without knowing the vendor.
- `Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)`.

Supporting types ([IWorkerAgent.cs:43-51](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L43-L51)):

- `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` - the unit of work. Built by `*BriefBuilder` classes from per-agent, per-phase templates.
- `WorkerOptions(Timeout, AllowedTools?, EnvironmentVariables?, DebugCaptureDirectory?, LiveStdoutSink?, LiveStderrSink?, ProgressDigestSink?, Size = WorkerSize.Medium)` - process-level controls. `Size` (the worker-domain size signal, default `Medium`) lets each agent map an abstract size to its own model tier. `AllowedTools` remains a Claude-Code-shaped concept (other agents map or ignore it).
- `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata, Blocks? = null)` - parsed from a `WORKER_RESULT` envelope the agent emits at the end of its session; `Blocks` carries the fenced payload blocks captured in the parser pre-pass (op-27).
- `IWorkerProgressDigester` ([src/ThroughlineBuild.Contracts/IWorkerProgressDigester.cs](../../src/ThroughlineBuild.Contracts/IWorkerProgressDigester.cs)) - `string? FormatLine(string rawNdjsonLine)`, best-effort (must not throw).
- `IWorkerAgentFactory` ([src/ThroughlineBuild.Contracts/IWorkerAgentFactory.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgentFactory.cs)) - `IWorkerAgent Create(string agentName)`.

### The four implementations

All four live in their own `ThroughlineBuild.Workers.<Vendor>` project, implement `IWorkerAgent`, and share one envelope parser in `ThroughlineBuild.Workers.Common`. The per-vendor differences:

| Agent | `Name` / vendor string | Brief delivery | Auth env handling | Model flag / prefix stripped | Output parsing | Digester |
|---|---|---|---|---|---|---|
| `ClaudeCodeAgent` | `claude-code` / `anthropic` | stdin, then close ([ClaudeCodeAgent.cs:101-102](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L101-L102)) | removes `ANTHROPIC_API_KEY`, sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` ([ClaudeCodeAgent.cs:444-448](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L444-L448)) | `--model`, strips `anthropic:` ([ClaudeCodeAgent.cs:416-419](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L416-L419)) | NDJSON; last `type=result` envelope, then WORKER_RESULT inside its `.Result` | `ClaudeCodeProgressDigester` |
| `CodexAgent` | `codex` / `openai` | stdin via `codex exec -` | removes `CODEX_API_KEY`, `OPENAI_API_KEY` | `--model`, strips `openai:` | plain stdout scanned directly for WORKER_RESULT | `CodexProgressDigester` |
| `GeminiAgent` | `gemini` / `google` | `-p` prompt arg ([GeminiAgent.cs:24](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L24)) | removes `GEMINI_API_KEY`, `GOOGLE_API_KEY` ([GeminiAgent.cs:285-286](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L285-L286)) | `--model`, strips optional `google:` ([GeminiAgent.cs:261-270](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L261-L270)) | `--output-format json` envelope; WORKER_RESULT inside `.response`, with raw-stdout fallback | `GeminiProgressDigester` |
| `CopilotAgent` | `copilot` / `github` | `-p` prompt arg ([CopilotAgent.cs:24](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L24)) | additive only - sets `GH_TOKEN` if passed, otherwise inherits the gh keyring credential ([CopilotAgent.cs:192-201](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L201)) | `--model`, strips optional `github:`; `AllowedTools` mapped to repeated `--allow-tool` ([CopilotAgent.cs:32-35](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L32-L35), [167-176](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L167-L176)) | plain stdout scanned directly ([CopilotAgent.cs:138-162](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L138-L162)) | none (returns null) |

Common across all four:

- A `BypassPermissions` option (default true) decides whether to emit the agent's unattended-mode flag: `--dangerously-skip-permissions` ([ClaudeCodeAgent.cs:412-413](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L412-L413)), `--dangerously-bypass-approvals-and-sandbox` ([CodexAgent.cs:365-366](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L365-L366)), `--yolo` ([GeminiAgent.cs:246-247](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L246-L247)); Copilot always passes `-s --no-ask-user` and has **no** `BypassPermissions` option at all - `CopilotOptions` omits the field and `WorkerAgentBuilder` does not pass it ([WorkerAgentBuilder.cs:32-37](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L32-L37)). That asymmetry is called out again in "adding a worker agent".
- All four call the shared `WorkerResultParser.TryParse` in `Workers.Common` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:73-174](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L73-L174)) - a fenced-block pre-pass captures any `<<<NAME_START`/`<<<NAME_END` payload blocks emitted before the marker, then the parser scans for the `WORKER_RESULT` marker line and deserializes the JSON after it, last-valid-envelope-wins. The captured blocks are returned on `WorkerResult.Blocks` and resolved by `FencedBlockResolver` against `*_ref` metadata fields (op-27; see [06-public-surfaces.md](06-public-surfaces.md) / [07-contracts.md](07-contracts.md)). The vendor wrappers differ only in what they feed the parser (the Claude `.Result` string, the Gemini `.response` string, or raw stdout).
- Each builds an `llm_usage` metadata dictionary and merges it onto the parsed result. The `vendor` string is the per-agent constant above; `model`, `wall_clock_ms`, token counts, and (Claude only) `cost_usd` are filled when available. Claude Code reports full input/output/cache token splits and cost; Codex reports real input/output/`cached_input_tokens`/`reasoning_output_tokens` plus `reasoning_effort` from its `turn.completed` usage event, but null cost; Gemini reports a single combined token total in `input_tokens` (output left 0, cost null); Copilot emits zeroed token counts and null cost (silent mode surfaces no usage).

### Usage and cost capture

`ClaudeCodeAgent.BuildLlmUsageMetadata` ([ClaudeCodeAgent.cs:326-355](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L326-L355)) is the richest: it reads token counts and cache fields from the envelope's `usage` block and `total_cost_usd`, and tags `vendor: "anthropic"`. `CodexAgent.BuildLlmUsageMetadata` now also carries real usage - input/output, `cached_input_tokens`, `reasoning_output_tokens`, and `reasoning_effort` from the `turn.completed` event ([CodexAgent.cs:151-158](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L151-L158), [293-333](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L293-L333)) - but null cost. Gemini fills `input_tokens` from its combined token total ([GeminiAgent.cs:297-310](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L297-L310)); Copilot emits a thin dictionary (model + vendor + wall-clock, zeroed tokens, null cost). The `vendor` string is what `analyze-event-log` keys its pricing table off, so per-agent vendor strings are required for mixed-vendor cost rollups - and now exist.

### Brief templates per agent

Brief builders take an `agentName` and load a per-agent template via `TemplateLoader.Load(agentName, templateName)` ([src/ThroughlineBuild.Briefs/TemplateLoader.cs:14-33](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs#L14-L33)). Templates live under [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/) in one subdirectory per agent (`claude-code/`, `codex/`, `gemini/`, `copilot/`), each with `plan.md`, `implement.md`, `review.md`, `decompose.md`, `draft.md`. The phase passes `_worker.Name` so the template always matches the dispatched agent (e.g. [PlanPhase.cs:83](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L83)). Each template instructs its model to emit the shared `WORKER_RESULT` envelope, so the parser stays vendor-neutral.

### Worker selection (the wiring)

`Program.cs` builds a name-keyed registry of `Func<IWorkerAgent>` factories and wraps it in a `WorkerAgentFactory` ([src/ThroughlineBuild.Cli/WorkerAgentFactory.cs](../../src/ThroughlineBuild.Cli/WorkerAgentFactory.cs)). The registry is populated from the set of agent names actually referenced - `default_agent`, any per-phase `[workers.phases]` entry, and any name supplied via CLI flag - each resolved to its `[workers.<name>]` config sub-table ([Program.cs:1078-1087](../../src/ThroughlineBuild.Cli/Program.cs#L1078-L1087)). The per-name concrete-agent construction has been **extracted out of `Program.cs` into `WorkerAgentBuilder.Create(name, cfg)`** ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)), so the factory loop is now just `factoryEntries[agentName] = () => WorkerAgentBuilder.Create(capturedName, capturedCfg)` ([Program.cs:1085](../../src/ThroughlineBuild.Cli/Program.cs#L1085)). `WorkerAgentBuilder` is the single name->agent switch (shared by the scaffold profile-derivation path too):

```csharp
public static IWorkerAgent Create(string name, AgentConfig cfg) => name switch
{
    "gemini"  => new GeminiAgent(new GeminiOptions { ... }),
    "codex"   => new CodexAgent(new CodexOptions { ... }),
    "copilot" => new CopilotAgent(new CopilotOptions { ... }),
    _         => new ClaudeCodeAgent(new ClaudeCodeOptions { ... }),
};
```

Each phase is then constructed with `workerFactory.Create(EffectiveAgentFor(phase))` ([Program.cs:1202](../../src/ThroughlineBuild.Cli/Program.cs#L1202), [1262](../../src/ThroughlineBuild.Cli/Program.cs#L1262)). Selection precedence is computed by `EffectiveAgentFor` ([Program.cs:1095-1099](../../src/ThroughlineBuild.Cli/Program.cs#L1095-L1099)): a per-phase CLI flag (`--agent-plan`/`--agent-implement`/`--agent-review`) beats `--agent`, which beats the per-phase config entry (`AgentFor`, [Program.cs:1090-1091](../../src/ThroughlineBuild.Cli/Program.cs#L1090-L1091)), which falls back to `default_agent`. Per-phase agent picking ("Claude Code for planning, Codex for implementation, Gemini for review") is therefore implemented today, both via config (`[workers.phases]`) and via flags. An unknown agent name surfaces as a `ConfigException` from `WorkerAgentFactory.Create`; an undefined `default_agent`/phase agent (no `[workers.<name>]` block) is now caught even earlier, at config load (TLB-512; see [04-configuration.md](04-configuration.md)).

The default agent is config-driven with no hardcoded vendor fallback in C# (`default_agent` is a required string). The earlier template-vs-live divergence is RESOLVED: the embedded `build init` template ships `default_agent = "claude-code"` ([config.toml.template:24](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L24)) and the checked-in operator config now also sets `default_agent = "claude-code"` ([.build/config.toml:25](../../.build/config.toml#L25)). Both ship `[workers.claude-code]` and `[workers.codex]` blocks uncommented; the codex block carries `{model, effort}` sizes (`small = { model = "gpt-5.4-mini", effort = "low" }`, `medium`/`large = { model = "gpt-5.5", effort = "medium"|"high" }`).

### The verification gate (op-30) - a worker<->orchestrator contract layer - Functional

Between implement and review the chain loop now runs a deterministic **gate** that validates a structured claim the worker emits, so the worker<->orchestrator contract extends past the `WORKER_RESULT` envelope. The implement worker emits a `COMPLETION_CLAIM` fenced block (referenced by a `completion_claim_ref` metadata key on the envelope), parsed by `CompletionClaimParser` in `Workers.Common` into a typed `CompletionClaim` ([src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs:30-90](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs#L30-L90)) and carried on `ImplementResult.CompletionClaim` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:27](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L27), resolved from metadata at [:410-432](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L410-L432)). The claim carries `provides`, `consumes`, `ac_bindings` (each `ac_ref` + `VerifierKind`), and `tests_added`.

`GatePhase` ([src/ThroughlineBuild.Phases/GatePhase.cs](../../src/ThroughlineBuild.Phases/GatePhase.cs)) then, against the warm worktree the implementer left: (1) validates the claim schema and hard-fails (back to InProgress for rework) if it is malformed ([GatePhase.cs:59-73](../../src/ThroughlineBuild.Phases/GatePhase.cs#L59-L73)); (2) runs the `[[review.checks]]` "capability map" once via `AutomatedChecksRunner`, hard-failing only on `Gating`-role check failures (build/test/typecheck) while `Advisory` failures (lint/format) are recorded ([GatePhase.cs:75-77](../../src/ThroughlineBuild.Phases/GatePhase.cs#L75-L77), [108-132](../../src/ThroughlineBuild.Phases/GatePhase.cs#L108-L132)); (3) collects advisory `SmokeSignal`s via `SmokeCollector` (diff facts) ([GatePhase.cs:79-92](../../src/ThroughlineBuild.Phases/GatePhase.cs#L79-L92)); and (4) runs an advisory-only consumes/provides preflight - whether the ticket's `consumes` are a subset of accumulated upstream `provides` ([GatePhase.cs:94-106](../../src/ThroughlineBuild.Phases/GatePhase.cs#L94-L106)). The outcome is a `GateOutcome(Passed, CheckResults, SmokeSignals, HardFailReason?)` consumed by `ChainPhase` and forwarded to `ReviewPhase`. There is no separate `[gate]` config table - the gate reads the same `[[review.checks]]` array. This is part of the worker contract but its depth belongs to [07-contracts.md](07-contracts.md) and [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

### Loose ends (worker layer)

- The agent construction is a hardcoded `switch` over four known names in `WorkerAgentBuilder.Create` ([WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)) rather than a data-driven registry; adding a fifth agent requires editing this one switch (an improvement on the prior inline `Program.cs` block - see "adding a worker agent" below).
- The `build new` draft-mode path resolves the implement-phase agent name but then constructs `ClaudeCodeAgent` unconditionally ([Program.cs:835-850](../../src/ThroughlineBuild.Cli/Program.cs#L835-L850)) - it does NOT route through `WorkerAgentBuilder`, so draft generation is effectively Claude-Code-only regardless of `default_agent`.
- `WorkerOptions.AllowedTools` is Claude-Code-shaped. Copilot maps it to repeated `--allow-tool` flags; Codex/Gemini ignore it.
- Token/cost capture is asymmetric: only Claude Code reports real token counts and cost. Cross-vendor cost rollups in `analyze-event-log` are only as accurate as each agent's `llm_usage` block.

---

## The model-client layer (built/tested, partly unwired)

### `ILlmClient` (production)

[src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs):

- `Task<LlmResponse> InvokeAsync(string modelId, IReadOnlyList<LlmMessage> messages, InvocationOptions options, CancellationToken ct)` - the production path.
- `IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(...)` - declared but stubbed in every implementation.

Supporting records: `LlmMessage(Role, Content)`; `InvocationOptions(MaxTokens?, Temperature?, System?)`; `LlmResponse(Content, Usage)`; `LlmUsage(InputTokens, OutputTokens, CacheReadTokens?, CacheWriteTokens?)`; the `LlmStreamEvent` hierarchy (`LlmStreamTextDelta`, `LlmStreamUsage`, `LlmStreamDone`). The cache fields are named after Anthropic prompt-caching headers.

`AnthropicClient` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs)) is the only production implementation:

- POST `{BaseUrl}/v1/messages` with `x-api-key` + `anthropic-version` headers ([AnthropicClient.cs:52-57](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L52-L57)).
- Strips `anthropic:` from `modelId` ([AnthropicClient.cs:28-31](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L28-L31)).
- Polly retry: 3 retries on 429 + 5xx with exponential backoff ([AnthropicClient.cs:102-114](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L102-L114)).
- Picks the first `text`-typed content block ([AnthropicClient.cs:77-80](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L77-L80)); source-gen JSON via `AnthropicJsonContext` for AOT.
- `InvokeStreamAsync` throws `NotImplementedException` ([AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### The factory and the only production consumer

`LlmClientFactory.Create(LlmConfig, BuildSecrets, HttpClient)` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29)) inspects the `[llm] default_model` prefix and **only** accepts `anthropic:` - any other prefix throws `ConfigException` with "only 'anthropic:' is supported". On the anthropic path it constructs `AnthropicClient` directly ([LlmClientFactory.cs:20](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L20)). This is the proof that the model-client layer is single-vendor on the production path: the factory exists and reads a prefix, but it can return exactly one implementation. Note that `[llm] default_model` is now **deprecated for worker-model selection** - worker models come from `[workers.<agent>.sizes]` `{model, effort}` tier tables, and the live `.build/config.toml` comments `default_model` out entirely ([.build/config.toml:16-20](../../.build/config.toml#L16-L20)). `default_model` survives only as the input to this factory (i.e. for the judgment-slot path); with it commented out, `LlmClientFactory.Create` throws, which is exactly the degraded-mode trigger described next.

`ReasonTranslator` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:25-31](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L25-L31)) is the only judgment-slot consumer - and the **only LLM consumer of any kind in the deterministic CLI**. It translates operator reason text to English when running `close` / `defer` / `reopen`, using the model `claude-haiku-4-5-20251001`, pinned to a `const` ([ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)). It is wired in `Program.cs.WireUpConditionalCommands` ([src/ThroughlineBuild.Cli/Program.cs:2145-2173](../../src/ThroughlineBuild.Cli/Program.cs#L2145-L2173)), which now **degrades gracefully when no LLM client can be built** (TLB-371): if `LlmClientFactory.Create` throws `ConfigException` (no key, or `default_model` unset/commented), it logs a `WARNING: LLM unavailable (...)` line and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the operator's reason text verbatim, so the state transition still runs:

```csharp
try { llmClient = LlmClientFactory.Create(llmConfig, secrets, http); }   // Program.cs:2165 - anthropic only
catch (ConfigException ex) {
    Console.Error.WriteLine($"WARNING: LLM unavailable ({ex.Message}); recording reason verbatim without translation.");
    llmClient = new EchoLlmClient();                                      // Program.cs:2171 - verbatim fallback
}
var translator = new ReasonTranslator(llmClient);                        // Program.cs:2173
```

So reason translation - the lone deterministic-CLI LLM call - is now fully optional: with no LLM key (or no `default_model`), `close` / `defer` / `reopen` still complete, recording the reason untranslated.

### `IModelClient` (newer, unwired)

A second, richer abstraction lives in `ThroughlineBuild.ModelClient` ([src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs)):

- `Task<ModelResponse> SendAsync(ModelRequest, ct)` and `IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest, ct)`.
- `ProviderConfig(BaseUrl, AuthScheme, ExtraHeaders, Vendor, DefaultTimeout)` - vendor-shaped config (the XML doc gives Anthropic / OpenAI-compatible / Ollama shapes).
- `ModelRequest` carries multi-block content (`TextContent`, `ToolUseContent`, `ToolResultContent`), optional `Tools` (`ToolDefinition`), and a `Stream` flag - it is a superset of what `ILlmClient` exposes ([src/ThroughlineBuild.ModelClient/ModelRequest.cs](../../src/ThroughlineBuild.ModelClient/ModelRequest.cs)).
- `ModelResponse` / `Usage` carry a vendor-tagged usage with model, cache fields, and optional `Cost`; the `ModelStreamEvent` hierarchy (`MessageStart`/`ContentDelta`/`MessageDelta`/`MessageStop`/`Error`) is a real streaming protocol ([src/ThroughlineBuild.ModelClient/ModelResponse.cs](../../src/ThroughlineBuild.ModelClient/ModelResponse.cs)).

`AnthropicModelClient` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs)) implements `IModelClient` with real SSE streaming (TLB-244/245): `SendAsync` does the non-streaming POST; `StreamAsync` posts with `stream: true`, reads `event:`/`data:` SSE lines, and maps them to `ModelStreamEvent`s ([AnthropicModelClient.cs:82-180](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs#L82-L180)). `ModelClientLlmAdapter` ([src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs)) wraps an `IModelClient` and presents it as an `ILlmClient`, so an `IModelClient` provider could in principle be dropped into the existing judgment-slot path.

The catch: **nothing on the production path constructs `AnthropicModelClient`, `ModelClientLlmAdapter`, or any `IModelClient`.** `LlmClientFactory` builds `AnthropicClient` directly. The only constructions of these types are in the `ThroughlineBuild.Anthropic.Tests` suite. `ModelClientLlmAdapter.InvokeStreamAsync` is itself still a `NotImplementedException` ([ModelClientLlmAdapter.cs:65-72](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65-L72)), so wiring it in would not by itself deliver streaming to a judgment slot. The `ModelClient` project is referenced by `Cli.csproj` but the reference is currently unused on any live code path.

### Loose ends (model-client layer)

- `AnthropicClient.InvokeStreamAsync` is unused and throws ([AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)). Real streaming exists only in the unwired `AnthropicModelClient`.
- `LlmClientFactory` rejects any non-`anthropic:` prefix, so even though it reads a prefix, the model-client layer cannot select a second vendor. Wiring `IModelClient` (via `ModelClientLlmAdapter`) onto this factory is the open task.
- `[llm]` config is single-vendor (`default_model` + `anthropic_api_key[_env]`, [src/ThroughlineBuild.Cli/Config.cs:19-22](../../src/ThroughlineBuild.Cli/Config.cs#L19-L22)). Multi-vendor judgment slots need per-vendor config.
- `LlmUsage` cache fields and the `LlmMessage.Content` `string` shape are Anthropic-shaped / single-block; `IModelClient` already generalizes both, which is part of why it was built.

---

## Model id and size conventions

### `vendor:model` prefix

Model identifiers follow the `vendor:model` convention. Every model-resolving site strips its own vendor prefix independently - there is no central router:

- `AnthropicClient.InvokeAsync` and `ModelClientLlmAdapter.InvokeAsync` strip `anthropic:`.
- `LlmClientFactory` inspects the prefix to accept/reject (anthropic-only).
- `ClaudeCodeAgent.NormalizeModel` strips `anthropic:`; `CodexAgent` strips `openai:`; `GeminiAgent` strips optional `google:`; `CopilotAgent` strips optional `github:`.

### `WorkerSize` -> `ModelTier` map (op-33)

`WorkerSize` (`Small`/`Medium`/`Large`, [src/ThroughlineBuild.Contracts/Models/WorkerSize.cs:8-13](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs#L8-L13)) is an abstract size signal the calling phase derives from the ticket size via `WorkerSizeMapper.FromTicketSize` ([src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs:7-12](../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)) and passes in `WorkerOptions.Size` (TLB-196/197/198). 

Since op-33, each agent's `*Options.Sizes` is an `IReadOnlyDictionary<WorkerSize, ModelTier>` - not a bare `WorkerSize -> string` map. `ModelTier(string Model, string? Effort = null)` ([src/ThroughlineBuild.Contracts/Models/ModelTier.cs:9](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs#L9)) carries the model id plus an optional reasoning `effort`. The agent looks up `options.Size`, strips the vendor prefix off `tier.Model`, and chooses the `--model` value; a size with no mapping leaves the `--model` flag off and lets the vendor CLI pick its default ([ClaudeCodeAgent.cs:416-419](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L416-L419)). The config shape is `[workers.<agent>.sizes]` with inline `{ model = "...", effort = "..." }` tier tables (see [04-configuration.md](04-configuration.md)).

### Codex reasoning effort + the model probe

`effort` is carried for every vendor but **acted on only by Codex** (a no-op for claude-code/gemini/copilot, per the `ModelTier` doc comment). When the resolved tier has a non-empty effort, `CodexAgent.BuildArgs` appends `-c model_reasoning_effort=<effort>` as two discrete argv entries ([CodexAgent.cs:373-375](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L373-L375)); when null it emits nothing and Codex applies its own per-model default. Codex also records the effort and richer token splits in `llm_usage` telemetry - `reasoning_effort`, `cached_input_tokens`, and `reasoning_output_tokens` are read from the `turn.completed` usage event ([CodexAgent.cs:151-158](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L151-L158), [293-333](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L293-L333)).

The `[workers.codex.sizes]` block can be (re)generated from a live Codex probe. `build models refresh` runs `CodexModelProbe` (`codex debug models`, returning each model's slug + supported/default reasoning levels, [src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs:9-43](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs#L9-L43)), then `CodexTierMapper.Map` maps the discovery onto small/medium/large `ModelTier`s ([src/ThroughlineBuild.Cli/CodexTierMapper.cs:43-80](../../src/ThroughlineBuild.Cli/CodexTierMapper.cs#L43-L80)): payload-order is most-capable-first, so the strongest main model is `large` (its effort "escalated" to the highest supported level - minimal<low<medium<high<xhigh), the next is `medium`, and the first mini/nano (or the weakest main) is `small`. The command overwrites only the `[workers.codex.sizes]` block in place ([ModelsRefreshCommand.cs:24-104](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L24-L104)); see [04-configuration.md](04-configuration.md) "Config-editing verbs".

---

## Where vendor-specific code lives

A map of the vendor-specific touchpoints in each layer.

### Worker layer (per agent)

Each agent's vendor specifics are isolated in its own project. The common shape (subprocess spawn, cancellation, debug capture, calling `WorkerResultParser`) is duplicated across the four agents rather than abstracted into `Workers.Common`, which holds the parser (plus `FencedBlockResolver`) and the AOT-safe `MarkdownRenderer` today.

| Project / file | Vendor specifics |
|---|---|
| `Workers.ClaudeCode/ClaudeCodeAgent.cs` | argv (`--print --verbose --output-format stream-json --allowedTools --model`), `ANTHROPIC_API_KEY` removal + `CLAUDE_CODE_MAX_OUTPUT_TOKENS`, stdin delivery, NDJSON stream parse, `anthropic:` prefix strip, `vendor: "anthropic"` + cost in `llm_usage`. |
| `Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs`, `ClaudeCodeStreamEvent.cs`, `ClaudeCodeProgressDigester.cs`, `ClaudeCodeOptions.cs` | `type=result` envelope schema, NDJSON event schema, tool-name digest shortcuts, `ExecutablePath = "claude"`. |
| `Workers.Codex/CodexAgent.cs` (+ `CodexJsonDtos.cs`, `CodexOptions.cs`, `CodexProgressDigester.cs`, `CodexModelProbe.cs`) | `codex exec --json [--dangerously-bypass-approvals-and-sandbox] --model <m> [-c model_reasoning_effort=<e>] -`, brief over stdin, `CODEX_API_KEY` / `OPENAI_API_KEY` removal, `openai:` prefix strip, `vendor: "openai"`, JSONL parse (extracts `item.completed` agent_message + `turn.completed` usage incl. cached/reasoning tokens), `reasoning_effort` in `llm_usage`. `CodexModelProbe` (`codex debug models`) feeds `build models refresh` / `CodexTierMapper`. |
| `Workers.Gemini/GeminiAgent.cs` (+ `GeminiJsonDtos.cs`, `GeminiOptions.cs`, `GeminiProgressDigester.cs`) | `gemini -p <prompt> --output-format json [--yolo] --model <m>`, `GEMINI_API_KEY` / `GOOGLE_API_KEY` removal, `google:` prefix strip, `vendor: "google"`, `.response` envelope parse. |
| `Workers.Copilot/CopilotAgent.cs` (+ `CopilotJsonDtos.cs`, `CopilotOptions.cs`) | `copilot -p <prompt> -s --no-ask-user [--model <m>] [--allow-tool <t>]*`, additive `GH_TOKEN` auth (no env strip), `github:` prefix strip, `vendor: "github"`, no digester, plain-stdout parse. |
| `Contracts/IWorkerAgent.cs` | `WorkerOptions.AllowedTools` is Claude-Code-shaped; other agents map or ignore it. |

### Model-client layer

| File | Vendor specifics |
|---|---|
| `Anthropic/AnthropicClient.cs` | `/v1/messages`, `x-api-key` + `anthropic-version`, content-block extraction, `anthropic:` strip. Production `ILlmClient`. |
| `Anthropic/AnthropicModelClient.cs` | Same endpoint/headers via `ProviderConfig`, plus SSE event mapping. `IModelClient`. Unwired. |
| `Anthropic/AnthropicApiModels.cs` | `Anthropic*` request/response/SSE records + `AnthropicJsonContext` source-gen. |
| `Anthropic/AnthropicOptions.cs` | `ApiVersion = "2023-06-01"`, default Anthropic `BaseUrl`. |
| `Cli/LlmClientFactory.cs` | Hardcoded "only `anthropic:` is supported" gate. |
| `Contracts/ILlmClient.cs` | `LlmUsage.CacheReadTokens` / `CacheWriteTokens` named after Anthropic headers; `LlmMessage.Content` is single-string. (`IModelClient` already generalizes both.) |

### Vendor-neutral contracts that **don't** change per provider

- The `WORKER_RESULT` JSON envelope ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:38-70](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L38-L70)) and the op-27 fenced-block payload protocol (`<<<NAME_START`/`*_ref` + `MarkdownRenderer`) - any worker that emits this shape is parsed by the same `WorkerResultParser` in `Workers.Common`. The four agents already do this; the per-agent brief templates instruct each model to emit both the envelope and the fenced bodies.
- The per-agent brief templates under [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/) - markdown with `{{variable}}` substitution. A new agent adds its own subdirectory; the substitution mechanism (`TemplateExtensions.Substitute`) and loader (`TemplateLoader.Load`) are shared.
- The phase classes take `IWorkerAgent` (and the verifier takes any injected agent) and do not depend on a concrete type.
- `Brief`, `WorkerResult`, `WorkerOptions`, `WorkerSize`, `Status`, `Verdict` in `ThroughlineBuild.Contracts`.

---

## What it takes to add a new provider

The two layers now diverge sharply. Adding a worker agent is a well-trodden path (it has been done three times); wiring a second model-client provider is the open, unfinished work.

### Adding a worker agent (the wired path)

The four existing agents are the template. To add agent `X`:

1. Create `src/ThroughlineBuild.Workers.X/` mirroring an existing worker project. Implement `XAgent : IWorkerAgent` (subprocess spawn, brief delivery, stdout capture, cancellation, debug capture). Reuse `WorkerResultParser` from `Workers.Common` by instructing the model (in the template) to emit a `WORKER_RESULT` envelope; feed the parser whatever text carries it (raw stdout, or an inner field of a JSON envelope).
2. Implement the env handling: strip the API-key env var if the CLI should use subscription/OAuth auth (Claude/Codex/Gemini do), or pass auth additively (Copilot's `GH_TOKEN`).
3. Emit `llm_usage` metadata with `X`'s own `vendor` string so `analyze-event-log` prices it.
4. Add an `IWorkerProgressDigester` (or return null) and an AOT JSON context for any vendor envelope DTOs.
5. Add `XOptions` with `ExecutablePath`, `MaxOutputTokens`, `Sizes` (the `IReadOnlyDictionary<WorkerSize, ModelTier>` map - since op-33 the tier value is a `{model, effort}` `ModelTier`, not a bare model string; honor `effort` only if `X`'s CLI has a reasoning-effort knob, like Codex), and - if `X` has an unattended-mode flag - `BypassPermissions`. This last field is optional and asymmetric across the existing agents: Claude Code / Codex / Gemini all carry `BypassPermissions` and `WorkerAgentBuilder` forwards it, but Copilot does not - `CopilotOptions` omits the field and the builder's `copilot` branch never sets it ([WorkerAgentBuilder.cs:32-37](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L32-L37)). Copilot's unattended mode (`-s --no-ask-user`) is unconditional, so there is no permission gate to toggle. If `X`'s CLI behaves like Copilot, follow that shape; otherwise mirror the other three.
6. Add per-agent brief templates under `Templates/x/` (start by copying an existing set; fork only if `X`'s tool conventions need it).
7. Wire the builder: add a `"x" =>` arm in the `WorkerAgentBuilder.Create` switch ([WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)) and a project reference in `Cli.csproj`. (This is the single seam now; the old inline `Program.cs` factory body is gone.)
8. Add the config block `[workers.x]` (executable, `[workers.x.sizes]` with `{model, effort}` tiers, etc.); set `default_agent = "x"` or reference it from `[workers.phases]` / a CLI flag.
9. Add fixtures and contract tests in `tests/ThroughlineBuild.Workers.X.Tests/` mirroring the existing per-agent suites.

No `Contracts` change is needed - the interface already fits subprocess agents that emit the envelope.

### Adding a model-client provider (the unfinished path)

The judgment-slot path is anthropic-only because `LlmClientFactory` can only return `AnthropicClient`. Two routes:

- **Against `ILlmClient` (the production interface):** implement `XClient : ILlmClient` next to `AnthropicClient`, then extend `LlmClientFactory.Create` to branch on the model prefix (`openai:`, `google:`, ...) and the matching secret. Extend `[llm]` config beyond the single `anthropic_api_key[_env]` pair. This fits chat-completions-shaped vendors; it does not add streaming (the interface's `InvokeStreamAsync` is stubbed everywhere) or tool use.
- **Against `IModelClient` (the richer interface), then adapt:** implement `XModelClient : IModelClient` (the `AnthropicModelClient` is the reference, with real SSE streaming), then wrap it in `ModelClientLlmAdapter` and hand that to `LlmClientFactory`/`ReasonTranslator`. This is the path that unlocks streaming and multi-block / tool content - but it requires the wiring step that does not exist yet: nothing constructs an `IModelClient` on the production path, and `ModelClientLlmAdapter.InvokeStreamAsync` is itself still stubbed. Wiring `IModelClient` onto the production path is the concrete open task for this layer.

Either route also wants `ReasonTranslator.ModelId` to become config-driven (it is a `const` today) if judgment slots should pick a vendor per call.

---

## Loose ends

- **Worker layer is wired; model-client layer is not.** The single biggest remaining gap is wiring an `IModelClient` provider (via `ModelClientLlmAdapter`) onto the judgment-slot path so the model-client layer matches the worker layer's multi-vendor maturity.
- **`AnthropicClient.InvokeStreamAsync` and `ModelClientLlmAdapter.InvokeStreamAsync` are stubbed** ([AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99), [ModelClientLlmAdapter.cs:65-72](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65-L72)). Real streaming lives only in the unwired `AnthropicModelClient.StreamAsync`.
- **The worker construction is a hardcoded `switch`** over four names in `WorkerAgentBuilder.Create` ([WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)) rather than a data-driven registry. The construction switch has been lifted out of `Program.cs` into its own seam (an improvement), but it is still a manual switch.
- **Token/cost capture is asymmetric.** `ClaudeCodeAgent` reports full token splits and `cost_usd`; Codex now reports real input/output/cached/reasoning token counts (from `turn.completed`) plus `reasoning_effort`, but null cost; Gemini reports a single combined token total (cost null); Copilot emits zeroed tokens and null cost. Cross-vendor cost rollups are only as good as each `llm_usage` block.
- **`WorkerOptions.AllowedTools`** is Claude-Code-shaped; Copilot maps it, Codex/Gemini ignore it. A future contract could replace it with an opaque per-agent options bag.
- **`LlmUsage` cache fields and `LlmMessage.Content`** are Anthropic-shaped / single-block. `IModelClient` already generalizes both; converging on it would retire the leak.
- **`ReasonTranslator.ModelId` is a default `const`** ([ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)) with a constructor override; per-call vendor switching needs config support (`[judgment_slots]`-shaped, not present). With `[llm] default_model` deprecated for worker selection and commented out in the live config, the only LLM call left in the deterministic CLI (reason translation) is now optional - it degrades to `EchoLlmClient` (verbatim) when no client can be built (TLB-371, see "the only production consumer").
- **No MCP server adapter.** Architecture Appendix item 3 contemplates `build` as an MCP server; an MCP-server-as-worker would be a separate animal from one-shot `IWorkerAgent`.

## Doc-set evolution note

The previous revision of this doc framed "two LLM interfaces (`ILlmClient`, `IWorkerAgent`)" with Codex/Gemini as Aspirational and `ClaudeCodeAgent` as the only worker. That framing is now wrong on two counts: (1) the worker layer is genuinely multi-vendor and wired (four agents, factory selection, per-phase picking), and (2) a third interface, `IModelClient`, now exists for the model-client layer. The current accurate framing is two *layers* with different maturity, documented above. The op-docs that drove this change are `op-14-new-agent-foundation`, `op-15-codex`, `op-16-gemini`, `op-17-copilot` (worker layer) and `op-18-rest-API-LLM` (the `IModelClient` work), all under `docs/op-docs/complete/`.
