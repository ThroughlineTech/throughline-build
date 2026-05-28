# Operation: add-codex-agent

Add OpenAI's Codex CLI as a worker agent (`Workers.Codex`) alongside `Workers.ClaudeCode`, using the multi-agent foundation from op-14. First additional agent and the validation that the foundation's acid test holds: a new agent is a new assembly plus config plus templates plus fixtures, with no phase or factory-mechanism changes.

Refer to op-14a-per-agent-notes.md for some notes.

## Why this exists

op-14 made adding an agent mechanical: `IWorkerAgentFactory` constructs by name, `[workers.phases]` and `--agent` select per phase, the `WORKER_RESULT` parser lives in `Workers.Common`, `WorkerSize` drives per-agent model selection, the contract test base asserts the invariants, and `Templates/<agent>/` holds per-agent prompt variants. Codex is the first exercise of that recipe. The Brief 14 research (`agent-tool-name-mapping.md`) found Codex a strong fit: `codex exec` is a real non-interactive mode, the final agent message reaches stdout (or arrives as agent-message items under `--json`), brief delivery works over stdin, `--model` selects the model, and subscription auth follows the same strip-the-API-key pattern as Claude Code.

This op-doc proves the foundation as much as it adds Codex. If anything in the `IWorkerAgent` contract is wrong, it surfaces here, cheaply, before Gemini and Copilot stack on top.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Agent: assembly, IWorkerAgent implementation, model/auth/usage, digester | - | M |
| B | Integration: factory + config, template variants, fixtures + contract tests | A | M |

Plan A is sequential (scaffold, then implement, then enrich). Plan B depends on A; within B the three briefs are independent.

## Plan A: Agent

### Goal

`Workers.Codex` exists with a `CodexAgent : IWorkerAgent` that runs `codex exec` non-interactively, delivers the brief, recovers the terminal result and the `WORKER_RESULT` block, and emits a `WorkerResult` plus `llm_usage` (vendor `openai`) indistinguishable in shape from the claude-code path. Model comes from the sizes map; auth uses the subscription; a progress digester is supplied or null.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | scaffold-workers-codex | New Workers.Codex project + CodexOptions + Codex output DTOs + JsonSerializerContext | - | src/ThroughlineBuild.Workers.Codex/ (new), throughline-build.sln |
| 02 | implement-codex-agent | CodexAgent.ExecuteAsync: argv, brief delivery, subprocess run, output capture, WORKER_RESULT scan, WorkerResult assembly, timeout/cancel, debug capture | 01 | src/ThroughlineBuild.Workers.Codex/CodexAgent.cs |
| 03 | codex-model-auth-usage | Resolve model from sizes map; strip API-key env for subscription auth; output-token cap; build llm_usage (vendor openai, model, tokens) | 02 | src/ThroughlineBuild.Workers.Codex/CodexAgent.cs |
| 04 | codex-progress-digester | CodexProgressDigester from the --json event stream, or return null | 02 | src/ThroughlineBuild.Workers.Codex/CodexProgressDigester.cs |

### Briefs - detail

#### Brief 01: scaffold-workers-codex

Goal: Stand up the project mirroring `Workers.ClaudeCode`'s structure so the rest of the work has a home.

Inputs: `Workers.ClaudeCode` project layout (options record, `ClaudeCodeJsonEnvelope`, `ClaudeCodeJsonContext`); `Workers.Common` (relocated `WorkerResultParser`); `Contracts` (`IWorkerAgent`, `WorkerOptions`, `WorkerResult`, `WorkerSize`, `IWorkerProgressDigester`).

Outputs:
- `ThroughlineBuild.Workers.Codex` classlib, added to the `.sln`, referencing `Workers.Common` and `Contracts` only.
- `CodexOptions` record (executable path, model resolution inputs, sizes map, token cap, timeout) mirroring `ClaudeCodeOptions`.
- Codex output DTOs for whatever terminal form the implementation parses (the `--json` event line(s) carrying the final message and usage, or the plain-text final message). All DTOs registered in a `CodexJsonContext : JsonSerializerContext`.
- `PublishAot` clean (no reflection-based serialization).

Acceptance:
- [ ] `Workers.Codex` builds, is in the solution, depends only on Workers.Common + Contracts
- [ ] `CodexOptions` exists with the fields the agent needs
- [ ] Codex output DTOs exist and are registered in a source-gen JSON context
- [ ] AOT publish of the solution succeeds

Notes: Keep `WorkerResultDto` reuse from `Workers.Common`; only Codex-specific wire DTOs live here. The exact terminal form (text final message vs `--json` agent-message items) is chosen in Brief 02; scaffold DTOs for whichever the implementer picks, register both contexts if both are used.

OOS: Do not implement ExecuteAsync (B02). Do not add config wiring (B05). Do not create templates (B06).

#### Brief 02: implement-codex-agent

Goal: `CodexAgent.ExecuteAsync` runs Codex non-interactively against the working directory, hands it the brief, waits within the timeout, recovers the terminal result, scans for the `WORKER_RESULT` block via the shared parser, and returns a typed `WorkerResult`.

Inputs: `IWorkerAgent` contract; `ClaudeCodeAgent.ExecuteAsync` as the reference for subprocess handling, live sink wiring, debug capture, timeout/cancellation; `Workers.Common.WorkerResultParser`; Brief 14 Codex findings.

Outputs:
- Builds the `codex exec` invocation (non-interactive, runs the full tool loop, exits) in the target working directory.
- Delivers the brief to Codex (stdin or prompt arg per the research; confirm stdin-only delivery during implementation).
- Captures stdout/stderr, honors `WorkerOptions.Timeout` with subprocess cancellation, writes debug capture when `DebugCaptureDirectory` is set, forwards live sinks.
- Recovers the final agent message and scans it for `WORKER_RESULT` via the shared parser; missing/invalid block yields `Status.Failed` with a `FailureReason`, not a crash.
- Returns a `WorkerResult` with status, summary, files-changed, failure reason, metadata.
- `Name` returns `"codex"` (matches the registry key).

Acceptance:
- [ ] A real `codex exec` run against a scratch repo produces a valid `WorkerResult`
- [ ] The `WORKER_RESULT` block is recovered via the shared parser
- [ ] Timeout cancels the subprocess and returns a failure result
- [ ] Missing/invalid `WORKER_RESULT` produces `Status.Failed` with a reason, no crash
- [ ] Debug capture writes when requested
- [ ] `Name` is `"codex"`

Notes: Research hints (HOW, implementer's call): `codex exec [--json] "<prompt>"`; final message on stdout (text) or as agent-message items (`--json`); `--full-auto` / sandbox flags for unattended tool execution; `--ephemeral` to skip session rollout files; `--output-schema` or `-o <file>` available if they harden envelope capture. VERIFY the `WORKER_RESULT` block is not reformatted under `--json`.

OOS: Do not resolve the model or build usage metadata (B03). Do not implement the digester (B04). Do not change the WORKER_RESULT contract.

#### Brief 03: codex-model-auth-usage

Goal: The agent runs on the size-selected model under subscription auth, respects an output-token cap, and emits `llm_usage` matching the event-log shape.

Inputs: `WorkerOptions.Size`; the `[workers.codex.sizes]` map (wired in B05); `ClaudeCodeAgent`'s model normalization and `ANTHROPIC_API_KEY`-strip pattern; `BuildLlmUsageMetadata` as the reference; Brief 14 Codex auth/usage findings.

Outputs:
- Resolves `WorkerOptions.Size` to a Codex model id from the sizes map and passes it via `--model` (or `--config model=`); strips any `vendor:` prefix as claude-code does.
- Forces subscription auth by not propagating `CODEX_API_KEY` / `OPENAI_API_KEY` to the child process env (mirror the strip pattern), unless a config opt-in selects API-key auth.
- Applies an output-token cap if Codex exposes one (VERIFY; may be `--config`-based or absent - if absent, document that and move on).
- Builds `llm_usage` metadata with `vendor = "openai"`, the resolved/extracted model, and token counts from the `--json` usage events; `cost_usd` null (Codex emits tokens, not USD).

Acceptance:
- [ ] Size selects the model end-to-end (small -> the configured small model, large -> the large model)
- [ ] Subscription auth path works headless with the API-key env stripped
- [ ] `llm_usage` carries vendor `openai`, model, and token counts; `cost_usd` null
- [ ] Token cap applied if supported, documented if not

Notes: `analyze-event-log` will need token-based pricing entries for the `openai` models in the sizes map. Note that in the agent's PR description so the pricing table is updated.

OOS: Do not add USD cost synthesis. Do not modify the shared event-log schema (op-14 Brief 08 already added the fields). Do not implement per-phase size overrides.

#### Brief 04: codex-progress-digester

Goal: Live progress for Codex runs, or graceful absence.

Inputs: `IWorkerProgressDigester`; `ClaudeCodeProgressDigester` as the reference; the `--json` event stream (`item.*`, command-execution events).

Outputs:
- `CodexProgressDigester : IWorkerProgressDigester` that turns digest-worthy `--json` events into short progress lines, returning null for the rest; OR, if deferred, `CodexAgent.Digester` returns null and live progress is simply absent.
- `CodexAgent.Digester` wired accordingly.

Acceptance:
- [ ] `CodexAgent.Digester` is non-null with a working digester, or null with progress gracefully absent
- [ ] If implemented, known Codex event shapes produce sensible progress lines and non-events return null

Notes: Optional for v1. The `--json` stream makes a real digester feasible; null is acceptable and the foundation handles it.

OOS: Do not change the sink mechanism. Do not require the digester for correctness.

## Plan B: Integration

### Goal

Codex is registered, configurable, templated, and tested: factory constructs it from `[workers.codex]`, its sizes map drives model selection, `Templates/codex/` holds its prompt variants, real fixtures back its tests, and it passes the shared contract test base.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | codex-factory-and-config | Register codex in the factory; [workers.codex] config + sizes map + validation | A | src/ThroughlineBuild.Cli/WorkerAgentFactory.cs, src/ThroughlineBuild.Cli/Config.cs, .build/config.toml, .build/config.toml.example |
| 06 | codex-template-variants | Templates/codex/ plan/implement/review/draft variants from the research doc | A | src/ThroughlineBuild.Briefs/Templates/codex/ (new) |
| 07 | codex-fixtures-and-contract-tests | Capture real Codex output fixtures; CodexAgentContractTests extends the contract base | A | tests/ThroughlineBuild.Workers.Codex.Tests/ (new) |

### Briefs - detail

#### Brief 05: codex-factory-and-config

Goal: Selecting codex by config or `--agent codex` constructs a working `CodexAgent`.

Inputs: op-14 `WorkerAgentFactory` and `WorkersConfig` (per-agent sub-tables, sizes maps, all-sizes validation).

Outputs:
- Factory registers `"codex"` -> a constructor reading `[workers.codex]`.
- `CodexAgentConfig` (executable, token cap, sizes map) parsed under `[workers.codex]` with `[workers.codex.sizes]`.
- All-three-sizes validation at load (same rule as claude-code).
- `.build/config.toml` and `.example` document a `[workers.codex]` block (commented or active) with a sizes map of real `openai` model ids.

Acceptance:
- [ ] `--agent codex` and `default_agent = "codex"` construct a working CodexAgent
- [ ] `[workers.codex]` + `[workers.codex.sizes]` parse; missing sizes throw at load
- [ ] Config files document the codex block

Notes: This is the wiring that makes `build implement 42 --agent codex` real, and `review = "codex"` in `[workers.phases]` possible.

OOS: Do not change the factory mechanism (op-14 owns it). Do not add Copilot/Gemini config.

#### Brief 06: codex-template-variants

Goal: Codex gets prompt variants phrased for its taxonomy.

Inputs: `Templates/claude-code/` as the baseline; op-14 `TemplateLoader` agent-name resolution; Brief 14 tool-vocabulary findings (Codex exposes file/search/edit/shell as a built-in tool loop rather than fixed named tools, so describe actions rather than naming Claude tools).

Outputs:
- `Templates/codex/{plan,implement,review,draft}.md` adapted from the claude-code variants, with WORKER_RESULT-emission instructions intact and tool references reworded to Codex's model.
- Embedded-resource globs / LF pins updated for the new directory.

Acceptance:
- [ ] All four codex templates exist and load via the agent-aware loader
- [ ] Templates instruct the model to emit the `WORKER_RESULT` block
- [ ] A codex run using these templates produces a parseable result

Notes: Keep deltas from claude-code minimal - only where Codex's tool model or output behavior requires different phrasing.

OOS: Do not alter claude-code templates. Do not add template inheritance.

#### Brief 07: codex-fixtures-and-contract-tests

Goal: Codex passes the shared invariant suite against real captured output.

Inputs: op-14 `IWorkerAgentContractTests`; real `codex exec` runs to capture; `Workers.ClaudeCode.Tests` as the pattern.

Outputs:
- Captured real Codex output fixtures (known-good and known-error), checked in.
- `CodexAgentContractTests : IWorkerAgentContractTests` overriding `CreateAgent` and the fixture-path members.
- AOT-disabled-reflection discipline where parser paths are exercised.

Acceptance:
- [ ] `CodexAgentContractTests` passes against real captured fixtures
- [ ] Known-error fixture yields `Status.Failed` + `FailureReason`
- [ ] Fixtures are real Codex output, not synthesized

Notes: This is the leverage op-14 built - two overridden methods plus fixtures inherit the whole suite.

OOS: Do not synthesize envelopes. Do not add cross-agent comparison tests (comparison-harness thread).

## What done looks like

`build implement 42 --agent codex` runs Codex against the ticket, Codex does the work, emits `WORKER_RESULT`, and TLB records a `WorkerResult` and an `llm_usage` event with vendor `openai` and the size-selected model - the same flow as claude-code, different binary. `[workers.phases]` can route any phase to codex, `[workers.codex.sizes]` maps S/M/L to openai models, `Templates/codex/` holds its prompts, and `CodexAgentContractTests` proves it against real output. The foundation's acid test is confirmed: nothing in the phases or the factory mechanism changed.