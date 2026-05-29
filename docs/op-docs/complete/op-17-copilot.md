# Operation: add-copilot-agent

Add GitHub Copilot CLI as a worker agent (`Workers.Copilot`) on the op-14 foundation. Third and hardest agent, built last on purpose. Same two-plan shape as the Codex and Gemini op-docs, with a gating spike up front because the Brief 14 research flagged Copilot as the one most likely to need per-agent handling.

Refer to op-14a-per-agent-notes.md for some notes.

## Why this exists

Copilot is the awkward fit of the three. The Brief 14 research found: a real programmatic mode (`copilot -p`, with `-s` for clean text and `--no-ask-user` to stop it pausing for questions), but no structured JSON output like Codex/Gemini; the richest per-tool permission vocabulary of the three (`--allow-tool` / `--deny-tool` / `--allow-all-tools`, the one surface that maps cleanly to `WorkerOptions.AllowedTools`); inverted auth (you SET a GitHub token rather than strip an API key, and headless auth is finicky with known open issues); weak usage reporting (GitHub's own agentic-workflows team needed an API proxy because the CLI output was insufficient); and unverified `WORKER_RESULT` survival under `-s`. By building it last, the contract test base and two working agents are in place to diff against, and any per-agent extraction it needs lands against a proven pattern.

Two findings drive the structure of this op-doc: the gating spike (does the `WORKER_RESULT` block survive cleanly under `-s --no-ask-user`), and best-effort usage (the Copilot agent's `llm_usage` may be partial - vendor and model where recoverable, tokens possibly unavailable, cost in premium-request quota rather than USD).

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Spike + Agent: validate WORKER_RESULT survival, then assembly, IWorkerAgent implementation, model/auth/usage | - | M |
| B | Integration: factory + config, template variants, fixtures + contract tests | A | M |

Plan A is sequential and starts with the spike; if the spike fails, the spike's outcome reshapes the remaining Plan A briefs before they run. Plan B depends on A.

## Plan A: Spike + Agent

### Goal

Confirm Copilot can be driven headless to emit a recoverable `WORKER_RESULT`, then `Workers.Copilot` with a `CopilotAgent : IWorkerAgent` that runs `copilot -p -s --no-ask-user`, delivers the brief, recovers the result, scans for `WORKER_RESULT`, and emits a `WorkerResult` plus best-effort `llm_usage` (vendor `github`). Model from the sizes map; GitHub-token auth; digester null.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | copilot-worker-result-spike | Validate WORKER_RESULT survival and headless auth against the installed Copilot CLI; decide extraction approach | - | docs/copilot-spike-findings.md (new) |
| 02 | scaffold-workers-copilot | New Workers.Copilot project + CopilotOptions + output DTOs (per spike) + JsonSerializerContext | 01 | src/ThroughlineBuild.Workers.Copilot/ (new), throughline-build.sln |
| 03 | implement-copilot-agent | CopilotAgent.ExecuteAsync: argv (-p -s --no-ask-user), brief delivery, subprocess run, output capture, WORKER_RESULT recovery per spike, WorkerResult assembly, timeout/cancel, debug capture | 02 | src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs |
| 04 | copilot-model-auth-usage | Resolve model from sizes map; GitHub-token auth; AllowedTools -> --allow-tool; best-effort llm_usage (vendor github); digester null | 03 | src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs |

### Briefs - detail

#### Brief 01: copilot-worker-result-spike

Goal: Before building the agent, confirm the two things the research left open: that a `WORKER_RESULT` fenced block emitted by the model survives to recoverable stdout under `-s --no-ask-user`, and that headless auth works with a token. Decide the extraction approach the agent will use.

Inputs: an installed, authenticated Copilot CLI; a scratch repo; a minimal brief instructing the model to emit a `WORKER_RESULT` block; Brief 14 Copilot findings.

Outputs:
- `docs/copilot-spike-findings.md` recording: whether the `WORKER_RESULT` block emerges intact under `-s --no-ask-user`; what surrounds it (residual metadata to strip); whether any usage/model line is recoverable in that mode; whether stdin-without-`-p` triggers programmatic mode (vs `-p` arg, which ignores piped stdin); and the working headless auth recipe (which of `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN`, PAT scopes).
- A decision: shared-parser-as-is, or a per-agent pre-extraction step (e.g. fence-isolation) feeding the shared parser. If the block cannot be made to survive, STOP and surface options rather than working around it.

Acceptance:
- [ ] `copilot-spike-findings.md` answers the survival, auth, stdin, and usage questions from real runs
- [ ] A recovery approach is chosen and justified
- [ ] If survival is not achievable, the blocker is surfaced with options, not silently worked around

Notes: This is the de-risking gate. The cost of a half-day spike is far below the cost of building B02-B04 on a false assumption.

OOS: Do not build the assembly (B02) or agent (B03) before the spike resolves. Do not change the WORKER_RESULT contract to accommodate Copilot without surfacing it first.

#### Brief 02: scaffold-workers-copilot

Goal: Project mirroring the other worker assemblies, shaped by the spike's findings.

Outputs:
- `ThroughlineBuild.Workers.Copilot` classlib in the `.sln`, referencing `Workers.Common` + `Contracts` only.
- `CopilotOptions` record (executable, model resolution inputs, sizes map, allowed-tools mapping, timeout).
- Output DTOs as the spike requires (likely thin - text mode, no structured envelope), registered in `CopilotJsonContext : JsonSerializerContext` if any JSON is parsed; if pure text, no envelope DTO is needed beyond the shared `WorkerResultDto`.
- AOT clean.

Acceptance:
- [ ] Builds, in solution, depends only on Workers.Common + Contracts
- [ ] `CopilotOptions` exists
- [ ] Any JSON DTOs registered in a source-gen context
- [ ] AOT publish succeeds

OOS: ExecuteAsync (B03), config (B05), templates (B06).

#### Brief 03: implement-copilot-agent

Goal: `CopilotAgent.ExecuteAsync` runs Copilot programmatically, delivers the brief, recovers the result per the spike's approach, scans for `WORKER_RESULT`, returns a typed `WorkerResult`.

Outputs:
- Builds the `copilot -p -s --no-ask-user` invocation (or stdin-without-`-p` per the spike) in the working directory.
- Delivers the brief via the spike-chosen path (note: piped stdin is ignored when `-p` is present, so it is one or the other).
- Captures stdout/stderr; applies the spike's extraction (as-is parser, or pre-extraction then parser) to recover the `WORKER_RESULT`; missing/invalid -> `Status.Failed` + `FailureReason`.
- Honors timeout/cancellation, debug capture, live sinks.
- Returns a `WorkerResult`; `Name` returns `"copilot"`.

Acceptance:
- [ ] A real Copilot run produces a valid `WorkerResult`
- [ ] `WORKER_RESULT` recovered per the spike's approach
- [ ] Timeout cancels and returns failure
- [ ] Missing/invalid block -> `Status.Failed`, no crash
- [ ] Debug capture writes when requested
- [ ] `Name` is `"copilot"`

Notes: `--no-ask-user` is required so the agent does not block waiting for clarification in headless mode. Large briefs favor the stdin path over a `-p` arg (arg-length limits).

OOS: Model/auth/usage (B04); WORKER_RESULT contract changes beyond the spike's agreed extraction.

#### Brief 04: copilot-model-auth-usage

Goal: Size-selected model, working GitHub-token auth, tool permissions mapped, best-effort usage.

Outputs:
- Resolves `WorkerOptions.Size` to a Copilot model id from the sizes map (GitHub-hosted ids, or BYOK provider ids); passes via `--model`.
- Auth: provides the GitHub token to the child env via the spike-confirmed variable (`COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN`). This is set-a-token, the inverse of the strip pattern; document the required PAT scope (Copilot Requests).
- Maps `WorkerOptions.AllowedTools` to `--allow-tool` / `--deny-tool` (the one agent where this maps cleanly); falls back to a sane default (e.g. `--allow-all-tools` only in a sandboxed/trusted run) when AllowedTools is unset.
- Builds best-effort `llm_usage`: `vendor = "github"`, model where recoverable (from non-silent output or config), token counts if the spike found any (else omitted), `cost_usd` null. Document that Copilot bills in premium-request quota, not USD.
- `CopilotAgent.Digester` returns null (no structured stream to digest).

Acceptance:
- [ ] Size selects the model end-to-end
- [ ] Headless GitHub-token auth works per the spike recipe
- [ ] `AllowedTools` maps to `--allow-tool`/`--deny-tool`; unset has a safe default
- [ ] `llm_usage` carries vendor `github` and model where available; cost_usd null; partial usage degrades gracefully
- [ ] `Digester` is null and progress is gracefully absent

Notes: `analyze-event-log` should treat `github` as a quota-based vendor (premium-request count), not a USD-priced one - note this for the pricing table. BYOK (`COPILOT_PROVIDER_BASE_URL` / `COPILOT_PROVIDER_API_KEY`) is an optional config path, not the default.

OOS: USD cost synthesis; implementing a digester; event-log schema changes.

## Plan B: Integration

### Goal

Copilot registered, configurable, templated, tested. Mirrors the Codex/Gemini Plan B.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | copilot-factory-and-config | Register copilot in the factory; [workers.copilot] config + sizes map + validation | A | src/ThroughlineBuild.Cli/WorkerAgentFactory.cs, src/ThroughlineBuild.Cli/Config.cs, .build/config.toml, .build/config.toml.example |
| 06 | copilot-template-variants | Templates/copilot/ plan/implement/review/draft variants | A | src/ThroughlineBuild.Briefs/Templates/copilot/ (new) |
| 07 | copilot-fixtures-and-contract-tests | Capture real Copilot output fixtures; CopilotAgentContractTests extends the contract base | A | tests/ThroughlineBuild.Workers.Copilot.Tests/ (new) |

### Briefs - detail

#### Brief 05: copilot-factory-and-config

Goal: Selecting copilot by config or `--agent copilot` constructs a working `CopilotAgent`.

Outputs:
- Factory registers `"copilot"` -> a constructor reading `[workers.copilot]`.
- `CopilotAgentConfig` (executable, sizes map, optional BYOK provider settings, default tool policy) under `[workers.copilot]` + `[workers.copilot.sizes]`; all-three-sizes validation at load.
- `.build/config.toml` / `.example` document a `[workers.copilot]` block with real GitHub model ids and a note on token auth.

Acceptance:
- [ ] `--agent copilot` and `default_agent = "copilot"` construct a working CopilotAgent
- [ ] Config parses; missing sizes throw at load
- [ ] Config files document the copilot block and the token-auth requirement

OOS: Factory-mechanism changes; other agents' config.

#### Brief 06: copilot-template-variants

Goal: Copilot prompt variants.

Outputs: `Templates/copilot/{plan,implement,review,draft}.md` adapted from claude-code, WORKER_RESULT-emission intact (and hardened per the spike if survival needs specific phrasing). Copilot is the one agent with a Claude-like named-tool permission model, so its templates may name tools where that helps. Resource globs / LF pins updated.

Acceptance:
- [ ] Four copilot templates exist and load via the agent-aware loader
- [ ] Templates instruct emitting the `WORKER_RESULT` block in a form the spike confirmed survives
- [ ] A copilot run using them produces a parseable result

OOS: Altering claude-code templates; template inheritance.

#### Brief 07: copilot-fixtures-and-contract-tests

Goal: Copilot passes the shared suite against real captured output.

Outputs: real known-good/known-error Copilot fixtures checked in; `CopilotAgentContractTests : IWorkerAgentContractTests` overriding `CreateAgent` + fixture paths; AOT-disabled-reflection discipline on parser paths. If usage is partial, the contract base's usage assertions must tolerate absent token counts (confirm the base allows this; if not, surface it - it may mean the base needs a "usage optional" hook rather than a Copilot workaround).

Acceptance:
- [ ] `CopilotAgentContractTests` passes against real fixtures
- [ ] Known-error fixture -> `Status.Failed` + `FailureReason`
- [ ] Partial-usage case handled by the contract base (or the base gap surfaced)
- [ ] Fixtures are real Copilot output

Notes: This is where partial usage may reveal that the op-14 contract base assumes complete `llm_usage`. If so, that is a foundation adjustment to raise, not a Copilot-local hack.

OOS: Synthesized envelopes; cross-agent comparison tests.

## What done looks like

`build implement 42 --agent copilot` runs Copilot programmatically with `-s --no-ask-user`, recovers a `WORKER_RESULT` by the spike-proven path, and records a `WorkerResult` plus a best-effort `llm_usage` event with vendor `github` and the size-selected model (tokens where available, cost null). `AllowedTools` maps to Copilot's `--allow-tool`. `[workers.phases]` can route any phase to copilot, `[workers.copilot.sizes]` maps S/M/L to GitHub models, `Templates/copilot/` holds its prompts, and `CopilotAgentContractTests` proves it against real output. The awkward agent is in - and where it strained the foundation (partial usage, extraction), the strain was surfaced and addressed in the contract, not hidden in Copilot-specific hacks.