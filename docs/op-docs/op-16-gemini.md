# Operation: add-gemini-agent

Add Google's Gemini CLI as a worker agent (`Workers.Gemini`) on the op-14 foundation. Second additional agent, built after Codex has validated the recipe. Same two-plan shape as `op-add-codex-agent`; this doc carries the Gemini-specific deltas.

Refer to op-14a-per-agent-notes.md for some notes.

## Why this exists

With the foundation (op-14) in place and Codex proving the recipe, Gemini is the second strong fit from the Brief 14 research and the cleanest structured output of the three: headless mode via `-p` / non-TTY, `--output-format json` returning a single object with `.response` (model text) and `.stats` (per-model token totals, tool calls), `--model` selection, brief delivery over stdin or `-p`, and subscription/OAuth auth via the same strip-the-API-key pattern as Claude Code and Codex. Doing Gemini before Copilot means two clean fits validate the foundation and the contract test base before the awkward agent.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Agent: assembly, IWorkerAgent implementation, model/auth/usage, digester | - | M |
| B | Integration: factory + config, template variants, fixtures + contract tests | A | M |

Plan A sequential; Plan B depends on A, its briefs independent.

## Plan A: Agent

### Goal

`Workers.Gemini` with a `GeminiAgent : IWorkerAgent` that runs Gemini headless, delivers the brief, recovers the result from `.response` (json) or stdout (text), scans for `WORKER_RESULT`, and emits a `WorkerResult` plus `llm_usage` (vendor `google`) shaped like the claude-code path. Model from the sizes map; OAuth/subscription auth; digester from `stream-json` or null.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | scaffold-workers-gemini | New Workers.Gemini project + GeminiOptions + Gemini output DTOs (.response/.stats) + JsonSerializerContext | - | src/ThroughlineBuild.Workers.Gemini/ (new), throughline-build.sln |
| 02 | implement-gemini-agent | GeminiAgent.ExecuteAsync: argv, brief delivery, subprocess run, output capture, recover .response, WORKER_RESULT scan, WorkerResult assembly, timeout/cancel, debug capture | 01 | src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs |
| 03 | gemini-model-auth-usage | Resolve model from sizes map; strip API-key env for OAuth; output-token cap; build llm_usage (vendor google, model, tokens from .stats) | 02 | src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs |
| 04 | gemini-progress-digester | GeminiProgressDigester from the stream-json event stream, or return null | 02 | src/ThroughlineBuild.Workers.Gemini/GeminiProgressDigester.cs |

### Briefs - detail

#### Brief 01: scaffold-workers-gemini

Goal: Project mirroring `Workers.ClaudeCode` / `Workers.Codex`.

Outputs:
- `ThroughlineBuild.Workers.Gemini` classlib in the `.sln`, referencing `Workers.Common` + `Contracts` only.
- `GeminiOptions` record (executable, model resolution inputs, sizes map, token cap, timeout).
- DTOs for the `--output-format json` result: `.response` (string) and `.stats` (per-model `tokens.total` etc., `tools`), registered in `GeminiJsonContext : JsonSerializerContext`. If `stream-json` is used for progress, add its event DTOs too.
- AOT clean.

Acceptance:
- [ ] Builds, in solution, depends only on Workers.Common + Contracts
- [ ] `GeminiOptions` exists
- [ ] `.response`/`.stats` DTOs registered in a source-gen context
- [ ] AOT publish succeeds

OOS: Do not implement ExecuteAsync (B02), config (B05), or templates (B06).

#### Brief 02: implement-gemini-agent

Goal: `GeminiAgent.ExecuteAsync` runs Gemini headless, delivers the brief, recovers the model text, scans for `WORKER_RESULT`, returns a typed `WorkerResult`.

Outputs:
- Builds the headless invocation (`-p` and/or non-TTY) in the working directory.
- Delivers the brief over stdin (prepended to `-p`) or as the `-p` argument.
- Captures output; with `--output-format json`, parses `.response` as the model text; with text mode, uses stdout.
- Scans the recovered text for `WORKER_RESULT` via the shared parser; missing/invalid -> `Status.Failed` + `FailureReason`.
- Honors timeout/cancellation, debug capture, live sinks.
- Returns a `WorkerResult`; `Name` returns `"gemini"`.

Acceptance:
- [ ] A real Gemini headless run produces a valid `WorkerResult`
- [ ] `WORKER_RESULT` recovered from `.response` (or stdout) via the shared parser
- [ ] Timeout cancels and returns failure
- [ ] Missing/invalid block -> `Status.Failed`, no crash
- [ ] Debug capture writes when requested
- [ ] `Name` is `"gemini"`

Notes: Research hints: `gemini -p "<prompt>" --output-format json`; the model text is in `.response`, so scan that field for the fenced block; `--yolo` or an approval mode for unattended tool execution. VERIFY the `WORKER_RESULT` block survives intact inside `.response` (not escaped oddly).

OOS: Model/usage (B03), digester (B04), WORKER_RESULT contract changes.

#### Brief 03: gemini-model-auth-usage

Goal: Size-selected model, OAuth/subscription auth, token cap, `llm_usage`.

Outputs:
- Resolves `WorkerOptions.Size` to a Gemini model id from the sizes map; passes via `--model`.
- Forces OAuth by not propagating `GEMINI_API_KEY` / `GOOGLE_API_KEY` to the child env, unless config opts into API-key/Vertex auth.
- Applies an output-token cap if Gemini exposes one (VERIFY; settings/config-based or absent).
- Builds `llm_usage`: `vendor = "google"`, resolved model, token counts from `.stats.models`; `cost_usd` null.

Acceptance:
- [ ] Size selects the model end-to-end
- [ ] OAuth path works headless with API-key env stripped
- [ ] `llm_usage` carries vendor `google`, model, tokens; `cost_usd` null
- [ ] Token cap applied if supported, documented if not

Notes: Add token-based pricing entries for the `google` models to `analyze-event-log`. Vertex auth (`GOOGLE_APPLICATION_CREDENTIALS` + project + `GOOGLE_GENAI_USE_VERTEXAI`) is an optional config path, not the default.

OOS: USD cost synthesis; event-log schema changes; per-phase size overrides.

#### Brief 04: gemini-progress-digester

Goal: Live progress or graceful absence.

Outputs: `GeminiProgressDigester : IWorkerProgressDigester` from `stream-json` events, or `GeminiAgent.Digester` returns null.

Acceptance:
- [ ] Digester present and working, or null with progress gracefully absent
- [ ] If implemented, known event shapes produce sensible lines; non-events return null

Notes: Optional for v1; `stream-json` makes it feasible.

OOS: Sink mechanism changes; requiring the digester for correctness.

## Plan B: Integration

### Goal

Gemini registered, configurable, templated, tested. Mirrors `op-add-codex-agent` Plan B.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | gemini-factory-and-config | Register gemini in the factory; [workers.gemini] config + sizes map + validation | A | src/ThroughlineBuild.Cli/WorkerAgentFactory.cs, src/ThroughlineBuild.Cli/Config.cs, .build/config.toml, .build/config.toml.example |
| 06 | gemini-template-variants | Templates/gemini/ plan/implement/review/draft variants | A | src/ThroughlineBuild.Briefs/Templates/gemini/ (new) |
| 07 | gemini-fixtures-and-contract-tests | Capture real Gemini output fixtures; GeminiAgentContractTests extends the contract base | A | tests/ThroughlineBuild.Workers.Gemini.Tests/ (new) |

### Briefs - detail

#### Brief 05: gemini-factory-and-config

Goal: Selecting gemini by config or `--agent gemini` constructs a working `GeminiAgent`.

Outputs:
- Factory registers `"gemini"` -> a constructor reading `[workers.gemini]`.
- `GeminiAgentConfig` (executable, token cap, sizes map) under `[workers.gemini]` + `[workers.gemini.sizes]`; all-three-sizes validation at load.
- `.build/config.toml` / `.example` document a `[workers.gemini]` block with real `google` model ids.

Acceptance:
- [ ] `--agent gemini` and `default_agent = "gemini"` construct a working GeminiAgent
- [ ] Config parses; missing sizes throw at load
- [ ] Config files document the gemini block

OOS: Factory-mechanism changes; other agents' config.

#### Brief 06: gemini-template-variants

Goal: Gemini prompt variants.

Outputs: `Templates/gemini/{plan,implement,review,draft}.md` adapted from claude-code, WORKER_RESULT-emission intact, tool references reworded to Gemini's model (describe actions; Gemini exposes a built-in tool loop, not fixed named tools). Resource globs / LF pins updated.

Acceptance:
- [ ] Four gemini templates exist and load via the agent-aware loader
- [ ] Templates instruct emitting the `WORKER_RESULT` block
- [ ] A gemini run using them produces a parseable result

OOS: Altering claude-code templates; template inheritance.

#### Brief 07: gemini-fixtures-and-contract-tests

Goal: Gemini passes the shared suite against real captured output.

Outputs: real known-good/known-error Gemini fixtures checked in; `GeminiAgentContractTests : IWorkerAgentContractTests` overriding `CreateAgent` + fixture paths; AOT-disabled-reflection discipline on parser paths.

Acceptance:
- [ ] `GeminiAgentContractTests` passes against real fixtures
- [ ] Known-error fixture -> `Status.Failed` + `FailureReason`
- [ ] Fixtures are real Gemini output

OOS: Synthesized envelopes; cross-agent comparison tests.

## What done looks like

`build implement 42 --agent gemini` runs Gemini headless, recovers the result from `.response`, and records a `WorkerResult` plus an `llm_usage` event with vendor `google` and the size-selected model. `[workers.phases]` can route any phase to gemini, `[workers.gemini.sizes]` maps S/M/L to google models, `Templates/gemini/` holds its prompts, and `GeminiAgentContractTests` proves it against real output. Two clean agents now ride the foundation unchanged.