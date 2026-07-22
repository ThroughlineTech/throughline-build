# 11 - LLM Architecture

Last refreshed: 2026-06-15 (`heartbeat-stage-07-finish-cutover`, Stage 07 cutover landed; documents the `ClaudeCodePreflight` capability gate, on top of the Stage 05-06 process-hardening + completion-redesign code surface. The transport default is now `interactive-hook` - the config-loader omitted-value default and the generated template both resolve to it; `print` is the rollback. Validated by the npt5 operator dogfood; see [docs/heartbeat/evidence/stage-07-dogfood.md](../heartbeat/evidence/stage-07-dogfood.md))

How `build` talks to LLMs today, the interfaces it uses, where vendor-specific code lives, and what it takes to add a new provider. The framing is unchanged since the last refresh: the **worker layer is genuinely multi-vendor and wired** (four agents selected at runtime), while the **model-client layer carries a richer abstraction that is built and tested but still not wired**. What changed inside the worker layer is substantial: tiered model selection (`ModelTier`), fail-fast model validation, full-transcript output parsing (driven by Fable's multi-message output), per-turn usage telemetry, provider-error classification, and a structured transcript side channel.

For dependency detail on the current providers see [03-external-dependencies.md](03-external-dependencies.md). For the inter-project type contracts see [07-contracts.md](07-contracts.md).

---

## Two layers, two maturities

The architecture (Section 3) defines three tiers of LLM contact: deterministic (no LLM), judgment slots (small scoped API calls), agentic work (full agent CLI in a worktree). In code, only the last two touch an LLM. They use different interfaces at different layers:

| Layer | Interface | Lives in | Implementations | Status |
|---|---|---|---|---|
| Worker (agentic CLI subprocess) | `IWorkerAgent` | [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `ClaudeCodeAgent`, `CodexAgent`, `GeminiAgent`, `CopilotAgent` | Functional - all four wired and selected at runtime |
| Model client (judgment-slot REST call) | `ILlmClient` | [src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs) | `AnthropicClient` (production); `EchoLlmClient` (degraded fallback); `ModelClientLlmAdapter` (unwired) | Partial - production path is anthropic-only, non-streaming, and fully optional |
| Model client (newer abstraction) | `IModelClient` | [src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs) | `AnthropicModelClient` (SSE streaming) | Aspirational on the production path - built and tested, never constructed by `build` |

The crucial split, re-verified at HEAD:

- **Worker layer is real multi-vendor.** Four `IWorkerAgent` implementations are constructed by one seam (`WorkerAgentBuilder.Create`) and chosen per phase from config and CLI flags. This is the production code path for `plan` / `implement` / `review` / `chain` / `decompose` / draft / batch sessions / scaffold profile derivation. Standalone `plan` is worker-backed unless `--from-brief` is explicit; `[plan].mode` controls only planning inside `chain` - see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).
- **Model-client layer is single-vendor and optional.** The only judgment-slot consumer (`ReasonTranslator`, used by `close` / `defer` / `reopen`) is handed an `ILlmClient` built by `LlmClientFactory`, which constructs `AnthropicClient` directly and rejects any non-`anthropic:` prefix ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L28)). When no client can be built, the verbs degrade to `EchoLlmClient` and record the reason verbatim. Nothing on the production path constructs `AnthropicModelClient`, `ModelClientLlmAdapter`, or any `IModelClient` - the only constructions remain in `ThroughlineBuild.Anthropic.Tests`. `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)), as does `ModelClientLlmAdapter.InvokeStreamAsync` ([src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs:71](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L71)).

The interfaces serve different shapes of work and do not share a dispatcher: `IWorkerAgent` is a long-lived subprocess spawn against a vendor CLI running an entire tool loop until it emits a terminal `WORKER_RESULT` envelope; `ILlmClient`/`IModelClient` are short-lived request-response API calls.

---

## The worker layer (real, wired)

### The contract

[src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs):

- `string Name { get; }` - identifier like `"claude-code"`, `"codex"`, `"gemini"`, `"copilot"`. The phase passes this to the brief builder so the agent gets its own template.
- `IWorkerProgressDigester? Digester { get; }` ([IWorkerAgent.cs:18](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L18)) - the agent's per-line digest formatter, or null when the agent has no digest (Copilot returns null).
- `Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)`.

Supporting types:

- `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` - the unit of work, built by `*BriefBuilder` classes from per-agent, per-phase templates.
- `WorkerOptions` ([IWorkerAgent.cs:51](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L51)) - process-level controls: `Timeout`, `AllowedTools?`, `EnvironmentVariables?`, `DebugCaptureDirectory?`, live stdout/stderr sinks, `ProgressDigestSink?` (one digest line per parsed stream event when `--debug` is off; mutually exclusive with the raw firehose), `Size` (default `Medium`), and the new `LeanPlanning` flag (a generic, stack-agnostic intent bit set by `ImplementPhase` for S-effort briefs under `[project].context_hygiene` - each agent maps it or ignores it; claude-code maps it to `--disallowedTools TodoWrite,Task`).
- `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata, Blocks?)` - parsed from the `WORKER_RESULT` envelope; `Blocks` carries fenced payload blocks captured in the parser pre-pass (op-27). Metadata now also carries `context_turns` (per-turn usage, claude-code only) and `completion_claim_ref` (the gate's `COMPLETION_CLAIM` opt-in).
- `ModelTier(string Model, string? Effort = null)` ([src/ThroughlineBuild.Contracts/Models/ModelTier.cs:9](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs#L9)) - the new per-size model entry. Every agent's `*Options.Sizes` changed from `IReadOnlyDictionary<WorkerSize, string>` to `IReadOnlyDictionary<WorkerSize, ModelTier>`; `Effort` is Codex-only (reasoning level).
- `IWorkerAgentFactory` - `IWorkerAgent Create(string agentName)`.

### The four implementations

All four live in their own `ThroughlineBuild.Workers.<Vendor>` project and share `ThroughlineBuild.Workers.Common`. Per-vendor differences:

| Agent | `Name` / vendor string | Invocation + brief delivery | Auth env handling | Output parsing | Digester |
|---|---|---|---|---|---|
| `ClaudeCodeAgent` | `claude-code` / `anthropic` | Default `interactive-hook` transport (interactive Claude under a terminal host, no `--print`) after the Stage 07 cutover; `print` (`claude --print --verbose --output-format stream-json [...]`, brief on stdin) is the rollback | removes `ANTHROPIC_API_KEY`, sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | interactive: completion synthesized from the persisted transcript; print: NDJSON stream reassembled, then envelope fallback (see below) | `ClaudeCodeProgressDigester` |
| `CodexAgent` | `codex` / `openai` | `codex exec --json [...] -`, brief on stdin | removes `CODEX_API_KEY`, `OPENAI_API_KEY` | JSON event stream from `--json` | `CodexProgressDigester` |
| `GeminiAgent` | `gemini` / `google` | `gemini -p <brief> --output-format json [--yolo]` | removes `GEMINI_API_KEY`, `GOOGLE_API_KEY` | JSON envelope; `WORKER_RESULT` inside `.response`, raw-stdout fallback | `GeminiProgressDigester` |
| `CopilotAgent` | `copilot` / `github` | `copilot -p <brief> -s --no-ask-user [--allow-tool <t>]*` | additive only - sets `GH_TOKEN` if passed, else inherits the gh keyring credential | plain stdout scanned directly | none (returns null) |

Declaring sites for the argv shapes: the claude-code arg builder ([ClaudeCodeAgent.cs:551-566](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L551-L566)), the codex arg builder ([CodexAgent.cs:364-375](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L364-L375)), the gemini arg builder ([GeminiAgent.cs:245-248](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L245-L248)), the copilot arg builder ([CopilotAgent.cs:24-35](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L24-L35)).

Common across all four:

- A `BypassPermissions` option (default true) decides whether to emit the unattended-mode flag: `--dangerously-skip-permissions` (claude-code), `--dangerously-bypass-approvals-and-sandbox` (codex), `--yolo` (gemini). Copilot has no such option - `CopilotOptions` omits the field and `WorkerAgentBuilder` does not pass one; its `-s --no-ask-user` unattended mode is unconditional.
- Each agent resolves its model from `Sizes[options.Size]` (a `ModelTier`), normalizes it with its own `NormalizeModel` (stripping the optional vendor prefix), and passes `--model` only when a mapping exists. Codex additionally appends `-c model_reasoning_effort=<effort>` when the tier carries an effort ([CodexAgent.cs:373-375](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L373-L375)).
- All four call the shared `WorkerResultParser.TryParse` in `Workers.Common` ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)): a fenced-block pre-pass captures `<<<NAME_START`/`<<<NAME_END` payload blocks, then the marker scan walks `WORKER_RESULT` markers in reverse - last valid envelope wins - tolerating code fences around the JSON. The brief templates now instruct workers to emit the blocks and the envelope in one final message, which this parser shape supports.
- All four pin subprocess stdout/stderr decoding to UTF-8 via `ProcessStreamEncoding.ApplyUtf8` ([src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs](../../src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs)) so Windows OEM code pages cannot mangle vendor CLI output.
- Failure-path diagnostics (parse failure, missing envelope, executable not found) go through the static `WorkerDiagnostics.Write` sink ([src/ThroughlineBuild.Workers.Common/WorkerDiagnostics.cs](../../src/ThroughlineBuild.Workers.Common/WorkerDiagnostics.cs)), defaulting to stderr and redirected to a no-op in test assemblies.
- Each builds an `llm_usage` metadata dictionary (vendor, model, wall clock, tokens, cost where available) merged onto the parsed result.

### Claude Code specifics (the deep path)

Status: **Functional**. The claude-code worker accumulated the most change this cycle:

- **Claude-only transport selection and Stop-hook bridge.** `ClaudeCodeAgent.ExecuteAsync` remains the `IWorkerAgent` entry point and provider identity owner, while the constructor selects either `ClaudeCodePrintTransport` or the functional `ClaudeCodeInteractiveTransport` behind internal `IClaudeCodeTransport` ([`ClaudeCodeAgent` constructor](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs), [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTransport.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTransport.cs)). The transport default is `interactive-hook` after the Stage 07 cutover: the config loader's omitted-value default at [src/ThroughlineBuild.Cli/Config.cs](../../src/ThroughlineBuild.Cli/Config.cs) resolves to it and the generated template sets it explicitly. `print` is the rollback via `transport = "print"` (honored on any Claude-family agent name, not just `claude-code`). The `ClaudeCodeOptions`/`AgentConfig` type-level defaults stay `Print` because they only govern directly-constructed options (tests, the print transport itself), not config loading. A `ClaudeCodePreflight` capability gate runs in `build setup`, before the worker-spawning phase verbs, AND at the transport entry (`ExecuteAsync`, so every path is gated - draft, investigate-plan, scaffold, batch - not just the phase verbs) ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodePreflight.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodePreflight.cs), [src/ThroughlineBuild.Cli/ClaudeTransportPreflight.cs](../../src/ThroughlineBuild.Cli/ClaudeTransportPreflight.cs)): it verifies `claude` is runnable (its `--version` exiting 0), `claude --version` >= 2.1.177 (the minimum derived from the undocumented transcript/trust/settings-skip schemas the transport depends on, not merely the tested version), and the host platform is supported - failing clearly without ever silently falling back to `print`. When the interactive transport runs, the worker writes `.build/brief.md`, creates a private run id and directory, writes ephemeral Stop-hook settings, and launches a fresh interactive Claude session through a platform terminal-host abstraction (`InteractiveClaudeProcessLauncherFactory` selects the host so the transport, run store, and parsing stay platform-neutral - [src/ThroughlineBuild.Workers.ClaudeCode/InteractiveClaudeProcessHost.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/InteractiveClaudeProcessHost.cs)) with model, permissions, tool policy, subscription-auth environment, and a short prompt directing Claude to the brief; its argv never contains `--print`. On Windows the host is `WindowsConPtyClaudeProcess` (ConPTY) which places the whole child tree in a mandatory kill-on-close **job object** (a job create/assign failure fails the launch rather than degrading to a best-effort kill); on Unix it is `UnixPtyClaudeProcess` ([src/ThroughlineBuild.Workers.ClaudeCode/UnixInteractiveClaudeProcessLauncher.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/UnixInteractiveClaudeProcessLauncher.cs)), a `posix_openpt`+`posix_spawnp` pseudo terminal whose `POSIX_SPAWN_SETSID` child group is reaped with a process-group signal (group-based containment, weaker than the job object; validated on real macOS 26.4 arm64 AND Ubuntu 24.04 x86_64 / glibc 2.39 by `UnixProcessTreeCleanupTests` - `libc` symbols resolved on both, both tree-cleanup tests passed with zero leftover processes, osx-arm64 and linux-x64 AOT clean) ([`ClaudeCodeInteractiveTransport.ExecuteAsync` and `BuildInteractiveArgs`](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeInteractiveTransport.cs)). `ClaudeRunDirectory` binds the directory to its run id, `ClaudeCompletionStore` atomically publishes and validates versioned `completion.json` records with duplicate-event idempotency and cancellation-aware waiting, and `ClaudeStopHookBridge` consumes a Stop payload when one is produced ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeStopHookBridge.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeStopHookBridge.cs)). **Completion is synthesized from the persisted transcript, not from the Stop hook.** `TranscriptTurnSignal` tails the transcript located by `ClaudeTranscriptLocator` (the project dir plus the newest `*.jsonl` whose content `cwd` matches the canonical worktree) for an assistant message at `stop_reason == end_turn`; on turn-detect the transport synthesizes a `ClaudeCompletionRecord` directly from that transcript (run id, session id, cwd, transcript path, and the reconstructed `last_assistant_message`), best-effort writes `/exit` to the PTY as a graceful nudge, terminates the interactive process tree (Stage 06 graceful-then-forced - `ProcessShutdownSequence`: signal -> wait -> job/group kill), and parses `WORKER_RESULT` plus telemetry from the transcript through the existing `WorkerResultParser`. The Stop-hook `completion.json` record survives only as a best-effort fast-path (consumed if it happens to be written first); completion no longer depends on it. All claude-facing paths are canonicalized through `ClaudeRealPath.Resolve` (libc `realpath` on Unix, `Path.GetFullPath` on Windows) so the spawn cwd, the trust key, and the transcript `cwd` match share claude's own resolved form (fixes macOS `/var` -> `/private/var`), and the transport pre-seeds claude workspace trust in `~/.claude.json` (`projects[<worktree>].hasTrustDialogAccepted = true` plus an integer `projectOnboardingSeenCount`) so the workspace-trust dialog does not hang a fresh `git init` worktree. Cancellation and timeout escalate the shutdown the same way, and failures name the run directory for diagnosis. Each run holds a `ClaudeRunLease` (an exclusive `run.lock` plus diagnostic `owner.json`), and `ClaudeRunDirectorySweeper` reclaims crash-orphaned run directories by lock-freeness - never by killing whatever process a stale pid now names ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeRunLease.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeRunLease.cs)); a per-worktree `InteractiveClaudeWorktreeLock` prevents two runs racing on the shared `.build/brief.md`. Non-debug runs remove their run evidence; debug runs retain it. The hidden pre-config CLI dispatch `build internal claude-stop-hook --run-dir <absolute-path> --run-id <id>` remains absent from public help ([src/ThroughlineBuild.Cli/ClaudeStopHookCommand.cs](../../src/ThroughlineBuild.Cli/ClaudeStopHookCommand.cs)); `ClaudeHookSettingsBuilder` safely quotes native executable and `dotnet build.dll` command prefixes ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeHookSettingsBuilder.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeHookSettingsBuilder.cs)). Interactive observability is recovered through the isolated, tolerant `ClaudePersistedTranscriptReader` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudePersistedTranscriptReader.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudePersistedTranscriptReader.cs)): full assistant text reconstructed across all messages, model identity, token/cache-read/cache-creation/output usage (missing fields marked unavailable, not invented), per-turn `context_turns` attribution, provider/rate/auth error text, and a redacted debug transcript side channel - and optional telemetry failure never turns a valid worker result into a failed phase. **Completion mechanism (resolved):** `claude` 2.1.170+ does not fire the per-turn `--settings` Stop hook in its interactive session (only in `--print`), and on Linux (the `O_NOCTTY`/sdk-cli launch mode) does not fire it on session exit either, so the original "wait for the Stop hook to write `completion.json`" contract was unreliable. The redesign therefore synthesizes the completion record from the persisted transcript instead (turn-detect via `TranscriptTurnSignal`/`ClaudeTranscriptLocator` -> synthesize `ClaudeCompletionRecord` -> best-effort `/exit` nudge -> terminate the process tree -> parse `WORKER_RESULT` + telemetry from the transcript), and keeps the Stop-hook `completion.json` only as a best-effort fast-path, not a dependency. Validated live on Windows 11, macOS arm64, and Linux x86_64 / glibc 2.39 against `claude` 2.1.177 (all three supported platforms green) - on Linux the synthesized `completion.json` carried `stop_hook_active: false`, proving completion came from the transcript, not the hook. This is a completion-contract change, not the process-hosting layer - see [docs/heartbeat/evidence/interactive-completion-redesign.md](../heartbeat/evidence/interactive-completion-redesign.md). It lands on branch `heartbeat-interactive-completion-redesign` @ `87266fc` (off the Stage 06 tip), not yet on `main`.

- **Full-transcript parsing.** `ParseStdoutEnvelope` ([ClaudeCodeAgent.cs:398-497](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L398-L497)) no longer trusts only the terminal `type=result` envelope's `.Result` string: `TryExtractAssistantTranscript` ([ClaudeCodeAgent.cs:268-311](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L268-L311)) reassembles the complete assistant text from all `type=assistant` NDJSON events and parses `WORKER_RESULT` + fenced blocks from that, falling back to the envelope `result` field. The driver was Fable: claude-fable-5 splits its final output across messages far more often than Opus/Sonnet (split block/envelope, trailing narration after the envelope) - shapes pinned by fixtures in `ClaudeCodeFableStreamTests` ([tests/ThroughlineBuild.Workers.ClaudeCode.Tests/ClaudeCodeFableStreamTests.cs](../../tests/ThroughlineBuild.Workers.ClaudeCode.Tests/ClaudeCodeFableStreamTests.cs)), including a verbatim 2026-06-10 capture. Envelope metadata (cost, usage) is still sourced from the terminal result line only.
- **Fail-fast model validation (TLB-544).** `ClaudeCodeModelValidator.Validate` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs:22](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs#L22)) runs at config load ([src/ThroughlineBuild.Cli/Config.cs:646](../../src/ThroughlineBuild.Cli/Config.cs#L646)): it accepts tier aliases (`haiku`/`sonnet`/`opus`) and `claude-*` ids (optional `anthropic:` prefix), and rejects unresolvable values - canonically `model = "fable"`, which must be `claude-fable-5` - with an actionable `ConfigException` instead of a mid-chain CLI session-init failure. At runtime the agent additionally recognizes the CLI's unresolvable-model phrasing in the stream and classifies it clearly ([ClaudeCodeAgent.cs:422](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L422)).
- **Per-turn usage telemetry.** `ClaudeCodeTurnParser` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTurnParser.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTurnParser.cs)) post-parses the NDJSON for per-turn cache_read/cache_creation/output token series and buckets cache-creation bytes per tool class (write/task/todo/read/bash/other). `AttachContextTurns` ([ClaudeCodeAgent.cs:200](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L200)) stashes the flat dictionary on `Metadata["context_turns"]`; `ImplementPhase` re-emits it as the `context_attribution` `CostLedger` event. Best-effort, AOT-safe, behavior-inert.
- **Structured transcript side channel.** Under `--debug`, `WorkerTranscriptWriter` writes `<captureDir>/transcript.jsonl` ([src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs:37](../../src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs#L37)) - one JSONL record per `meta`/`turn`/`tool_result`/`result`, with per-turn usage, verbatim tool calls, and a discovery/production/verification turn classification. Pure post-exit observation; a write failure never changes phase behavior.
- **Digest changes.** The old free-standing `WorkerProgressDigest.cs` is deleted; `ClaudeCodeProgressDigester` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs)) is the instance-based survivor. It now filters `type=system` stream events by subtype (only `init` and `thinking_tokens` are digest-worthy) and throttles the `thinking_tokens` ticker to 5000-token boundaries so extended thinking does not flood the digest.
- **Lean-planning mapping.** When `WorkerOptions.LeanPlanning` is set, the arg builder appends `--disallowedTools TodoWrite,Task` ([ClaudeCodeAgent.cs:559-562](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L559-L562)) - disallow removes the tools outright, where `--allowedTools` only auto-approves. The matching prompt line comes from `ImplementBriefBuilder.BuildContextHygieneSection`; both are gated behind `[project].context_hygiene` (default false) and S-size briefs.

### Codex specifics

Status: **Functional**. `CodexAgent` now runs `codex exec --json` with the brief on stdin via the trailing `-` ([CodexAgent.cs:24](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L24)), parsing the JSON event stream rather than scanning plain stdout; env strip unchanged ([CodexAgent.cs:338-339](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L338-L339)). Model selection rides `ModelTier.Effort` as `-c model_reasoning_effort=<effort>`.

Two new Codex-only discovery pieces feed config, not runtime dispatch:

- `CodexModelProbe` ([src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs)) spawns `codex debug models`, parses the list-visible model slugs with their supported reasoning-effort levels, and returns a typed, never-throwing result.
- `CodexTierMapper` ([src/ThroughlineBuild.Cli/CodexTierMapper.cs:26](../../src/ThroughlineBuild.Cli/CodexTierMapper.cs#L26)) maps that discovery onto a small/medium/large `ModelTier` set heuristically (minis rank below mains; the large tier escalates to the highest supported effort). Consumed by `build init` ([src/ThroughlineBuild.Cli/InitCommand.cs:235](../../src/ThroughlineBuild.Cli/InitCommand.cs#L235)) and the `build models refresh` verb ([src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs:69](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L69)) to write the `[workers.codex.sizes]` block.

### Gemini / Copilot specifics

Status: **Functional**, low churn. Both remain full `IWorkerAgent` implementations wired through `WorkerAgentBuilder`; their changes this cycle were the `ModelTier` sizes migration and test-suite reshuffles (`GeminiModelAuthUsageTests` pins model normalization, env stripping, and usage capture - the "stub" test names refer to stubbed CLI processes in fixtures, not stub agents). Copilot still maps `AllowedTools` to repeated `--allow-tool` flags and still has no `BypassPermissions`; Gemini still ignores `AllowedTools`. Neither is enabled in the live operator config (their `[workers.*]` blocks are commented out).

### Provider-error classification (TLB-527)

`ProviderErrorClassifier` ([src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs)) matches a failed `WorkerResult` against quota/rate-limit and auth signature sets (with retry-at timestamp extraction for the claude and codex phrasings) and returns a typed `ProviderError` or null. Its production consumer is the verifier path: `WorkerAgentReviewer` classifies after each run onto `LastProviderError` ([src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs:86](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L86)); `ReviewPhase` converts that into a `ProviderUnavailable` result instead of a `Fail` verdict, and the chain maps it to `ChainOutcome.ReviewUnavailable` (exit 9) - the ticket stays cleanly `InReview` and resumable. Implement-side worker failures are not classified this way; only review gets the transient-provider escape hatch.

### Usage and cost capture

`ClaudeCodeAgent.BuildLlmUsageMetadata` ([ClaudeCodeAgent.cs:502](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L502)) remains the richest: full token/cache splits plus `total_cost_usd`, tagged `vendor: "anthropic"`. Gemini reports a combined token total; Codex and Copilot emit thin dictionaries (model + vendor + wall clock, zeroed tokens, null cost).

The cost analyzer changed posture (TLB-547): `analyze-event-log` ([src/tools/analyze-event-log.cs](../../src/tools/analyze-event-log.cs)) now aggregates across all chains in a log and **prefers its own pricing table over the worker-reported `cost_usd`** for recognized models, warning when the two disagree by more than 10%; event-supplied cost is only trusted for unrecognized models. The table prices `claude-fable-5` and the repriced Opus tiers alongside the OpenAI entries (whose cached tokens are subtracted from input before billing). Worker `cost_usd` is therefore telemetry, not the accounting source of truth.

### Brief templates per agent

Brief builders load per-agent templates via `TemplateLoader.Load(agentName, templateName)` from [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/): one subdirectory per agent (`claude-code/`, `codex/`, `gemini/`, `copilot/`), each now holding seven templates - `plan.md`, `implement.md`, `review.md`, `decompose.md`, `draft.md`, plus the new `batch-implement.md` and `batch-review.md`. A `shared/` subdirectory (10 files) holds cross-agent blocks: the obsolete-detection blocks (`plan-obsolete-initial.md`, `implement-obsolete-initial.md`/`-rework.md`, extracted from the per-agent templates and sanitized to angle-bracket placeholders), the batch worker-result envelope shapes, and the patch-fetch directives. The implement templates carry the `preloaded_context_section` placeholder (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). The scaffold profile-derivation prompt is NOT here - it is an embedded resource loaded by `ProfilePromptLoader` in `ThroughlineBuild.Scaffold`.

### Worker selection (the wiring)

Construction is now centralized: `WorkerAgentBuilder.Create(name, AgentConfig)` ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs)) is a single switch mapping agent name to a constructed agent with its `ExecutablePath` / `MaxOutputTokens` / `Sizes` / `BypassPermissions` (except Copilot), plus the Claude-only transport selection. The phase-verb factory wiring and scaffold profile-derivation path therefore build Claude agents with the same transport. `Program.cs` populates a name-keyed registry of closures over it, with a fail-fast `ConfigException` when a referenced agent has no `[workers.<name>]` sub-table, and wraps it in `WorkerAgentFactory`.

Selection precedence is `EffectiveAgentFor` ([Program.cs:1149-1153](../../src/ThroughlineBuild.Cli/Program.cs#L1149-L1153)): per-phase CLI flag (`--agent-plan`/`--agent-implement`/`--agent-review`) beats `--agent`, which beats the `[workers.phases]` config entry, which falls back to `default_agent`. Per-phase agent picking is implemented; the chain's batch worker is created from the implement-phase agent inside `ChainPhaseComposition`.

Two adjacent honesty/safety checks:

- `VerifierToolEnforcement.UnenforcedWarning` ([src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs](../../src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs), TLB-478) emits a one-line startup warning when `verifier_allowed_tools` is configured but the resolved review agent is not in the enforcing set (`claude-code`, `copilot`) - codex/gemini ignore the allowlist and run the verifier unsandboxed.
- The scaffold profile deriver is a second production worker consumer: `ScaffoldProfileRunner` builds its read-only worker through the same `WorkerAgentBuilder` seam, and the derivation run is debug-captured under `--debug`.

The product default and the live operator default re-converged: the embedded `build init` template ships `default_agent = "claude-code"`, and the checked-in `.build/config.toml` is back on `default_agent = "claude-code"` ([.build/config.toml:25](../../.build/config.toml#L25)) with tier-alias sizes (`small`/`medium`/`large` = `haiku`/`sonnet`/`opus`); the `[workers.codex.sizes]` block (gpt-5.4-mini/gpt-5.5 with efforts) remains configured but codex is no longer the default. The previous revision's "live config selects Codex" claim is stale.

### Loose ends (worker layer)

- The name-to-type mapping in `WorkerAgentBuilder.Create` is still a hardcoded switch over four names; adding a fifth agent edits that switch (but no longer `Program.cs`).
- The `build new` draft-mode path still constructs `ClaudeCodeAgent` unconditionally ([Program.cs:890](../../src/ThroughlineBuild.Cli/Program.cs#L890)) after resolving the agent name only for config validation - draft generation is effectively Claude-Code-only regardless of `default_agent`.
- `WorkerOptions.AllowedTools` remains Claude-Code-shaped; Copilot maps it, codex/gemini ignore it - now at least surfaced by `VerifierToolEnforcement` for the review phase.
- Token/cost capture is still asymmetric (full splits from claude-code only); the analyzer compensates by pricing from its own table.
- Per-turn telemetry (`ClaudeCodeTurnParser`, `WorkerTranscriptWriter`) is claude-code-only; the other agents have no equivalent, so context-attribution experiments only measure one vendor.
- Provider-error classification covers only the review path; an implement-phase quota hit still surfaces as a generic worker failure.

---

## The model-client layer (built/tested, still unwired)

### `ILlmClient` (production)

[src/ThroughlineBuild.Contracts/ILlmClient.cs](../../src/ThroughlineBuild.Contracts/ILlmClient.cs): `InvokeAsync(modelId, messages, options, ct)` is the production path; `InvokeStreamAsync` is declared but stubbed in every implementation. Supporting records (`LlmMessage`, `InvocationOptions`, `LlmResponse`, `LlmUsage` with Anthropic-named cache fields, the `LlmStreamEvent` hierarchy) are unchanged.

`AnthropicClient` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs)) is the only production implementation: POST `/v1/messages` with `x-api-key` + `anthropic-version`, strips `anthropic:`, Polly retry on 429/5xx, first text block wins, source-gen JSON for AOT. `InvokeStreamAsync` throws ([AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).

### The factory and the only production consumer

`LlmClientFactory.Create` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs)) inspects the `[llm] default_model` prefix and only accepts `anthropic:` - any other prefix throws `ConfigException` ([LlmClientFactory.cs:28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L28)). `default_model` is deprecated for worker selection (worker models come from `[workers.<agent>.sizes]`) and is commented out in the live config, surviving only as this factory's input.

`ReasonTranslator` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15), model pinned to the `ModelId` const `claude-haiku-4-5-20251001`) remains the only LLM consumer in the deterministic CLI, translating operator reason text for `close` / `defer` / `reopen`. The wiring in `WireUpConditionalCommands` ([Program.cs:2235](../../src/ThroughlineBuild.Cli/Program.cs#L2235)) degrades gracefully (TLB-371): when `LlmClientFactory.Create` throws ([Program.cs:2255](../../src/ThroughlineBuild.Cli/Program.cs#L2255)), it logs a `WARNING: LLM unavailable` line and substitutes `EchoLlmClient` ([Program.cs:2261](../../src/ThroughlineBuild.Cli/Program.cs#L2261), [src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)), which returns the operator's reason verbatim - the state transition always runs.

### `IModelClient` (newer, unwired)

The richer abstraction in `ThroughlineBuild.ModelClient` ([src/ThroughlineBuild.ModelClient/IModelClient.cs](../../src/ThroughlineBuild.ModelClient/IModelClient.cs)) is unchanged: `SendAsync`/`StreamAsync`, vendor-shaped `ProviderConfig`, multi-block `ModelRequest` with tool definitions, vendor-tagged `Usage` with optional cost, and a real `ModelStreamEvent` streaming protocol. `AnthropicModelClient` ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs)) implements it with real SSE streaming (TLB-244/245); `ModelClientLlmAdapter` re-presents an `IModelClient` as an `ILlmClient`.

Re-verified at HEAD: **nothing on the production path constructs `AnthropicModelClient`, `ModelClientLlmAdapter`, or any `IModelClient`** - the only constructions are in `ThroughlineBuild.Anthropic.Tests`, and `ModelClientLlmAdapter.InvokeStreamAsync` still throws ([ModelClientLlmAdapter.cs:71](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L71)).

### Loose ends (model-client layer)

- Real streaming exists only in the unwired `AnthropicModelClient.StreamAsync`; both `InvokeStreamAsync` stubs still throw.
- `LlmClientFactory` cannot select a second vendor; wiring `IModelClient` (via `ModelClientLlmAdapter`) onto it remains the open task - untouched this cycle while all investment went into the worker layer.
- `[llm]` config is single-vendor; multi-vendor judgment slots need per-vendor config.
- `LlmUsage` cache fields and the single-string `LlmMessage.Content` remain Anthropic-shaped; `IModelClient` already generalizes both.

---

## Model id and size conventions

### `vendor:model` prefix

Model identifiers follow the `vendor:model` convention; every model-resolving site strips its own vendor prefix independently - there is still no central router. `AnthropicClient` / `ModelClientLlmAdapter` strip `anthropic:`; `LlmClientFactory` accepts only `anthropic:`; each agent's `NormalizeModel` strips its own prefix (`anthropic:` / `openai:` / `google:` / `github:`).

### `WorkerSize` -> `ModelTier` map

`WorkerSize` (`Small`/`Medium`/`Large`) is derived from the ticket size via `WorkerSizeMapper.FromTicketSize` and passed in `WorkerOptions.Size`. Each agent's `*Options.Sizes` now maps `WorkerSize -> ModelTier` (model id or tier alias, plus Codex-only effort); a size with no mapping leaves `--model` off and lets the vendor CLI pick its default. The claude-code entries are validated at config load by `ClaudeCodeModelValidator`; the codex entries can be generated from live discovery by `CodexModelProbe` + `CodexTierMapper` via `build init` / `build models refresh`.

---

## Where vendor-specific code lives

### Worker layer (per agent)

The common subprocess shape is still duplicated across providers rather than generalized; `Workers.Common` holds the shared parsing and plumbing (`WorkerResultParser`, `CompletionClaimParser`, `ProviderErrorClassifier`, `ProcessStreamEncoding`, `WorkerDiagnostics`, `MarkdownRenderer`). Claude now has its own narrow transport abstraction, deliberately not shared with Codex, Gemini, or Copilot.

| Project | Vendor specifics |
|---|---|
| `Workers.ClaudeCode` | stream-json argv, stdin delivery, `ANTHROPIC_API_KEY` removal + `CLAUDE_CODE_MAX_OUTPUT_TOKENS`, full-transcript NDJSON parse, Fable multi-message tolerance, `ClaudeCodeTurnParser`, `ClaudeCodeModelValidator`, `WorkerTranscriptWriter`, `ClaudeCodeProgressDigester`, `--disallowedTools` lean-planning mapping, cost in `llm_usage`. |
| `Workers.Codex` | `codex exec --json -` stdin delivery, JSON event stream, `CODEX_API_KEY`/`OPENAI_API_KEY` removal, `-c model_reasoning_effort` from `ModelTier.Effort`, `CodexModelProbe`, `CodexProgressDigester`. |
| `Workers.Gemini` | `-p` prompt arg, `--output-format json` envelope (`.response` extraction, raw fallback), `--yolo`, `GEMINI_API_KEY`/`GOOGLE_API_KEY` removal, `GeminiProgressDigester`. |
| `Workers.Copilot` | `-p` prompt arg, unconditional `-s --no-ask-user`, additive `GH_TOKEN` auth, `--allow-tool` mapping, no digester. |

### Model-client layer

| File | Vendor specifics |
|---|---|
| `Anthropic/AnthropicClient.cs` | `/v1/messages`, `x-api-key` + `anthropic-version`, content-block extraction, `anthropic:` strip. Production `ILlmClient`. |
| `Anthropic/AnthropicModelClient.cs` | Same endpoint/headers via `ProviderConfig`, plus SSE event mapping. `IModelClient`. Unwired. |
| `Anthropic/AnthropicApiModels.cs` / `AnthropicOptions.cs` | Request/response/SSE records + source-gen context; `ApiVersion = "2023-06-01"`. |
| `Cli/LlmClientFactory.cs` | Hardcoded "only `anthropic:` is supported" gate. |
| `Cli/CodexTierMapper.cs` | Codex model-discovery heuristics (config generation, not dispatch). |

### Vendor-neutral contracts that do not change per provider

- The `WORKER_RESULT` envelope + fenced-block payload protocol, parsed by the shared `WorkerResultParser`; the `COMPLETION_CLAIM` block, parsed by the shared `CompletionClaimParser` ([src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs)).
- The per-agent brief templates and the `shared/` blocks under `Templates/` - markdown with `{{variable}}` substitution via the shared `TemplateLoader`/`TemplateExtensions`.
- The phase classes take `IWorkerAgent` and never depend on a concrete type; `Brief`, `WorkerResult`, `WorkerOptions`, `WorkerSize`, `ModelTier`, `Status`, `Verdict` live in `ThroughlineBuild.Contracts`.

---

## What it takes to add a new provider

### Adding a worker agent (the wired path)

The four existing agents are the template. To add agent `X`:

1. Create `src/ThroughlineBuild.Workers.X/` mirroring an existing worker project. Implement `XAgent : IWorkerAgent` (subprocess spawn via `ProcessStreamEncoding.ApplyUtf8`, brief delivery, stdout capture, cancellation, debug capture). Reuse `WorkerResultParser`; feed it whatever text carries the envelope - and budget for models that split the envelope across messages (the claude-code full-transcript path is the cautionary tale).
2. Implement env handling: strip the API-key env var if the CLI should use subscription/OAuth auth, or pass auth additively (Copilot's `GH_TOKEN`).
3. Emit `llm_usage` with `X`'s own vendor string, and add `X`'s models to the `analyze-event-log` pricing table - the analyzer now prefers table pricing over worker-reported cost.
4. Add an `IWorkerProgressDigester` (or return null), an AOT JSON context for vendor DTOs, and - if `X` reports provider-side quota errors - signatures in `ProviderErrorClassifier` so review failures classify as `ReviewUnavailable` rather than `Fail`.
5. Add `XOptions` with `ExecutablePath`, `MaxOutputTokens`, `Sizes` (`WorkerSize -> ModelTier`), and `BypassPermissions` if `X` has an unattended-mode flag (follow the Copilot shape if unattended is unconditional). Decide whether `X` enforces a tool allowlist and update `VerifierToolEnforcement.EnforcingAgents` accordingly.
6. Add per-agent brief templates under `Templates/x/` - all seven, including `batch-implement.md`/`batch-review.md`, reusing the `shared/` blocks.
7. Add a `"x"` arm to the `WorkerAgentBuilder.Create` switch and a project reference in `Cli.csproj` - the construction seam is one switch now, not a `Program.cs` block.
8. Add the `[workers.x]` config block; set `default_agent = "x"` or reference it from `[workers.phases]` / a CLI flag.
9. Add fixtures and contract tests in `tests/ThroughlineBuild.Workers.X.Tests/` mirroring the existing per-agent suites.

No `Contracts` change is needed.

### Adding a model-client provider (the unfinished path)

Unchanged, and untouched this cycle. Either implement `XClient : ILlmClient` next to `AnthropicClient` and extend `LlmClientFactory.Create` to branch on the prefix (no streaming, no tool use), or implement `XModelClient : IModelClient` (with `AnthropicModelClient` as the streaming reference) and wrap it in `ModelClientLlmAdapter` - which first requires the wiring step that still does not exist. Either route also wants `ReasonTranslator.ModelId` to become config-driven (it is a `const` with a constructor override).

---

## Loose ends

- **Worker layer is wired; model-client layer is not.** The gap is now starker: the worker layer gained tiering, validation, telemetry, and error classification this cycle while the `IModelClient` wiring task did not move. With reason translation degrading to `EchoLlmClient`, there is no operational pressure forcing the wiring.
- **Streaming stubs persist** in `AnthropicClient.InvokeStreamAsync` and `ModelClientLlmAdapter.InvokeStreamAsync`; real streaming lives only in the unwired `AnthropicModelClient.StreamAsync`.
- **Construction is centralized but still name-switched** (`WorkerAgentBuilder.Create`); a data-driven registry remains hypothetical.
- **Draft generation is Claude-Code-only** regardless of `default_agent` ([Program.cs:890](../../src/ThroughlineBuild.Cli/Program.cs#L890)).
- **Telemetry depth is one-vendor:** per-turn usage, tool-class attribution, and the structured transcript exist only for claude-code, which biases any cross-vendor experiment the cost ledger is meant to support.
- **`ReasonTranslator.ModelId` is a pinned `const`** naming a dated Haiku snapshot; no `[judgment_slots]` config exists.
- **No MCP server adapter.** Architecture Appendix item 3 contemplates `build` as an MCP server; an MCP-server-as-worker would be a separate animal from one-shot `IWorkerAgent`.

## Doc-set evolution note

The previous revision introduced the two-layers framing (worker layer multi-vendor and wired; model-client layer single-vendor with an unwired `IModelClient`); that framing survives this refresh intact. What changed within it: sizes became `ModelTier` (model + Codex effort), agent construction moved from a `Program.cs` if-chain into `WorkerAgentBuilder`, claude-code parsing moved from envelope-only to full-transcript (Fable), and the worker layer grew a telemetry/validation/classification ring (`ClaudeCodeTurnParser`, `WorkerTranscriptWriter`, `ClaudeCodeModelValidator`, `CodexModelProbe`/`CodexTierMapper`, `ProviderErrorClassifier`, `ProcessStreamEncoding`, `WorkerDiagnostics`, `CompletionClaimParser`). The op-docs and tickets that drove this cycle are the gate/claim series (TLB-500..TLB-512, TLB-527, TLB-538, TLB-544..TLB-547) and the exp-1..exp-4 experiment branches.
