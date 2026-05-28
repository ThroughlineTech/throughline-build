# Operation: build-multi-agent-foundation

Foundation for supporting Codex, Copilot, and Gemini as worker agents alongside the existing Claude Code agent, all via their CLI subprocess interfaces. Establishes the factory/registry/DI plumbing, per-phase agent selection (config + CLI flag), a per-agent S/M/L sizing abstraction that maps to agent-specific models, a shared progress-digest abstraction, cost-capture and per-agent-vendor fixes, the cleanup the recon surfaced (parser relocation, verifier rename), a shared agent-contract test base, and per-agent brief template variants. Four plans, fourteen briefs.

Non-CLI agents (HTTP/REST workers such as Ollama, or anything that talks to an LLM endpoint directly rather than spawning a vendor CLI) are out of scope here but explicitly not precluded: `IWorkerProgressDigester` is nullable for exactly this reason, and a future HTTP worker plugs into the same factory/config/sizing surface without changing it.

## Why this exists

Per the state-of-the-system doc set, `IWorkerAgent` already exists cleanly and all phases (`PlanPhase`, `ImplementPhase`, `ReviewPhase`, `DraftPhase`) consume it by interface, never by concrete type. But the wiring stops at the composition root: `Program.cs:640-646` hard-constructs a single `ClaudeCodeAgent` and shares that one instance across every phase (a second hard-construction for draft mode lives at `424-430`). The `[workers].default_agent` config field is read into `BuildOptions.WorkerName` and then never consulted to pick an agent. There is no factory, no registry, no per-phase selection, no per-agent sizing. `WorkerResultParser` lives in `Workers.ClaudeCode` where a second agent cannot reuse it without depending on the Claude project. `ClaudeCodeReviewer` is named for Claude but depends only on `IWorkerAgent`, and carries a dead `FlattenLlmUsage`/`UnwrapJsonElement` copy (lines 141-183) duplicating `LlmUsageFlattener`. The brief templates are flat under one directory and the loader is agent-unaware.

None of this is hard individually; it is a lot of small wiring changes that compound into "the foundation that lets us add Codex/Copilot/Gemini next without disturbing the existing flow."

This op-doc folds in three pieces of related work that would otherwise be separate tickets:
- The `tn-max-tokens-config` ticket (max_output_tokens in config). SUPERSEDED: the key already exists as a flat `workers.max_output_tokens` (default 32000); this op-doc only relocates it under `[workers.claude-code]` as part of the schema reshape. No separate ticket.
- The cost-capture gap (`cost_usd` is never captured from the wire; `vendor` is hardcoded `"anthropic"`). Same code path; fix while we are there. Model capture is already done (extracted from the stream `type=system` event), so it is not in scope.
- An S/M/L sizing abstraction so phases pass an abstract size to workers and each agent maps it to its own model tiers, with the ticket as the source of truth for the size.

Hard-break on config schema. No backward-compat fallbacks. Dan is the only operator; migration is one config-file rewrite.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Foundation: factory, DI, config schema, per-phase selection, CLI override | - | L |
| B    | Cleanup: parser relocation, verifier rename, progress digester abstraction, cost/vendor fixes | - | M |
| C    | Sizing abstraction: WorkerSize, per-agent size maps, ticket-size source of truth, phase plumbing | A | M |
| D    | Test contract base + per-agent brief template variants | A, B | M |

A and B can run in parallel (no overlapping files). C depends on A's factory and config schema; within C, the Plane size-extraction and phase plumbing land in Brief 11 after the contract (09) and the per-agent maps (10). D depends on A's factory shape and B's interface cleanups.

## Plan A: Foundation

### Goal

`IWorkerAgentFactory` interface exists, the CLI uses it to construct the right agent per phase based on config. Config schema reshaped to per-agent sub-tables. `[workers.phases]` table drives per-phase selection. `--agent <name>` / `--agent-<phase> <name>` CLI flags override config per invocation. Hard-break on the old flat config shape.

Briefs are mostly sequential within this plan: factory first (01), config reshape (02), per-phase selection layers on (03), CLI override layers on that (04).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | worker-agent-factory | IWorkerAgentFactory in Contracts; WorkerAgentFactory with string->constructor registry; replace shared hard-coded ClaudeCodeAgent construction | - | src/ThroughlineBuild.Contracts/IWorkerAgentFactory.cs, src/ThroughlineBuild.Cli/WorkerAgentFactory.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/WorkerAgentFactoryTests.cs |
| 02 | config-schema-reshape | WorkersConfig restructured to per-agent sub-tables; flat keys removed (hard break); max_output_tokens relocated under [workers.claude-code] | 01 | src/ThroughlineBuild.Cli/Config.cs, .build/config.toml, .build/config.toml.example, tests/ThroughlineBuild.Cli.Tests/ConfigTests.cs |
| 03 | per-phase-selection | [workers.phases] config table; one agent constructed per phase via factory; phases no longer share a single instance | 02 | src/ThroughlineBuild.Cli/Config.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/PerPhaseAgentSelectionTests.cs |
| 04 | cli-flag-override | --agent and --agent-<phase> flags on phase verbs and chain; override config; value validated through factory | 03 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, tests/ThroughlineBuild.Cli.Tests/AgentFlagOverrideTests.cs |

### Briefs - detail

#### Brief 01: worker-agent-factory

Goal: Introduce `IWorkerAgentFactory` as the indirection between "I need an agent named X" and "construct ClaudeCodeAgent (or CodexAgent, etc.) with the right options." Replace the single shared `new ClaudeCodeAgent(...)` at the composition root (`Program.cs:640-646`) with a factory call. The registry pattern mirrors the existing `TicketCommandRegistry` approach.

Inputs:
- Existing `IWorkerAgent` interface in `ThroughlineBuild.Contracts` (`Name`, `ExecuteAsync(Brief, workingDirectory, WorkerOptions, ct)`)
- Existing `ClaudeCodeAgent` constructor taking `ClaudeCodeOptions`
- The shared-instance construction at `Program.cs:640-646` and the draft-mode construction at `424-430`

Outputs:
- `IWorkerAgentFactory` in Contracts: `IWorkerAgent Create(string agentName)`
- `WorkerAgentFactory` implementation in the Cli project (composition-root work)
- Registry shape: `Dictionary<string, Func<IWorkerAgent>>` populated at startup. Each entry knows how to construct its agent from parsed config.
- Initial registry contents: just `"claude-code"` mapped to a constructor that reads the claude-code config and builds `new ClaudeCodeAgent(new ClaudeCodeOptions {...})`
- Unknown agent name throws `ConfigException` with a clear message ("agent 'X' not registered; known agents: claude-code")
- Both worker construction sites (main phases and draft mode) go through the factory
- Tests: factory returns ClaudeCodeAgent for "claude-code"; throws on unknown name with descriptive message; constructed agent has executable path and max_output_tokens wired from config

Acceptance:
- [ ] `IWorkerAgentFactory.Create(string)` exists in Contracts
- [ ] `WorkerAgentFactory` exists with a string->constructor registry
- [ ] Program.cs no longer contains a bare `new ClaudeCodeAgent(...)`; both sites construct via factory
- [ ] Unknown agent name produces ConfigException listing known agents
- [ ] All existing tests still pass (factory wiring is invisible to phases)

Notes: This is the minimum brain transplant. After this lands, `default_agent` finally drives behavior - setting it to anything but `"claude-code"` produces the unknown-agent error instead of being silently ignored. Explicit registration (not reflection/assembly scanning) keeps composition AOT-clean and dependencies traceable; new agents add themselves in a `RegisterAgents()` called from `Program.Main`.

OOS:
- Do not add per-phase selection (B03)
- Do not add CLI flag override (B04)
- Do not add config schema reshape (B02)
- Do not scan for IWorkerAgent implementations (explicit registration only)

#### Brief 02: config-schema-reshape

Goal: Replace the flat `[workers]` block with per-agent sub-tables. Vendor-specific options live under `[workers.<agent-name>]`. `[workers]` keeps cross-agent settings (default_agent, timeout). Relocate the existing `max_output_tokens` from the flat block to `[workers.claude-code]`. Hard-break: old flat keys throw, no fallback.

Inputs:
- Current `WorkersConfig` record and its parse site in `Config.cs` (flat keys: `default_agent`, `claude_code_executable`, `timeout_minutes`, `max_output_tokens`)
- Current `.build/config.toml` and `.build/config.toml.example` `[workers]` sections
- `max_output_tokens` already exists (default 32000) and is set as `CLAUDE_CODE_MAX_OUTPUT_TOKENS` on the subprocess (`ClaudeCodeAgent.cs:377-378`)

Outputs:
- New `WorkersConfig`: `(string DefaultAgent, int TimeoutMinutes, IReadOnlyDictionary<string, AgentConfig> Agents)`. For now `AgentConfig` for the known agent is a typed `ClaudeCodeAgentConfig (string Executable, int MaxOutputTokens)`; the dictionary is the seam where future agents register their config types.
- New config shape:
  ```toml
  [workers]
  default_agent = "claude-code"
  timeout_minutes = 30

  [workers.claude-code]
  executable = "claude"
  max_output_tokens = 32000
  ```
- Old flat keys (`claude_code_executable`, top-level `max_output_tokens`) are no longer parsed; their presence throws a migration-helpful ConfigException, e.g. `"Unknown key 'claude_code_executable' at [workers] - move to [workers.claude-code].executable"`
- The factory entry from B01 reads from `config.Workers.Agents["claude-code"]`
- Update `.build/config.toml` and `.build/config.toml.example` to the new shape
- Find and update every other `config.toml` under the project tree (the repo's own `.build/config.toml` plus any comparison/test-repo configs); a missed one throws ConfigException on the next run
- Tests: parse the new shape; old flat keys produce the migration ConfigException; missing `[workers.claude-code]` when `default_agent = "claude-code"` produces a clear error; ClaudeCodeAgent gets executable + max_output_tokens from the right path; env var is set on subprocess invocation

Acceptance:
- [ ] WorkersConfig restructured to per-agent sub-tables with an Agents map
- [ ] `.build/config.toml` and `.example` updated to the new shape; all other config.toml files under the tree updated
- [ ] Old flat keys produce ConfigException with a migration message (no silent acceptance)
- [ ] ClaudeCodeAgent reads its options from `[workers.claude-code]`
- [ ] CLAUDE_CODE_MAX_OUTPUT_TOKENS still set on subprocess invocation
- [ ] Existing tests updated or pass

Notes: Keeping the top-level name `[workers]` (vs `[agents]`) minimizes churn; code and architecture use both terms. CodexAgentConfig/CopilotAgentConfig/GeminiAgentConfig arrive in their own op-docs. The recon's "max_output_tokens is 8192 for haiku subagents" observation is unconfirmed by the state docs and is not load-bearing here; the brief's work is the relocation, not a subagent-cap fix. If that cap turns out real during dogfood, file it separately.

OOS:
- Do not preserve backward compat for old flat keys (hard break)
- Do not add Codex/Copilot/Gemini configs (their op-docs add their own)
- Do not change the `[llm]` section here (sizing deprecates its worker-model role in Plan C, not here)
- Do not move env-var handling out of agents (each agent owns its env concerns)

#### Brief 03: per-phase-selection

Goal: `[workers.phases]` lets each phase use a different agent. The composition root constructs one agent per phase via the factory and passes the right one to each phase. Default: a missing `[workers.phases]` or a missing key falls back to `default_agent`.

Inputs:
- Factory from B01, config schema from B02
- Phase dispatch sites: plan (`Program.cs:687-737`), implement (`738-802`), review (`1051-1104`), draft mode (`424-430`); chain wiring builds per-phase factories closed over shared dependencies (`897-981`)

Outputs:
- `[workers.phases]` parsed into `WorkersConfig.Phases: IReadOnlyDictionary<string, string>` (phase name -> agent name)
- Recognized keys: `plan`, `implement`, `review` (ship is deterministic/reserved). Draft uses the implement-phase agent (it is implement-shaped work) unless a `draft` key is added later.
- Default: missing/empty/absent key -> `DefaultAgent`
- `.build/config.toml` documents the default explicitly:
  ```toml
  [workers.phases]
  plan = "claude-code"
  implement = "claude-code"
  review = "claude-code"
  ```
- The composition root constructs one agent per phase, e.g. `factory.Create(config.Workers.Phases.GetValueOrDefault("plan", config.Workers.DefaultAgent))`; no shared instance across phases
- The review phase's verifier shim uses the review phase's agent (one slot per phase; verifier separation is a v1.1 concern)
- Chain wiring resolves the per-phase agent for each phase factory
- Tests: populated phases route correctly; missing phases fall back to default; partial phases use default for the rest; unknown agent name surfaces the factory's ConfigException

Acceptance:
- [ ] `[workers.phases]` parsed into a per-phase map
- [ ] One agent constructed per phase via factory; no shared instance
- [ ] Missing/partial phases fall back to default_agent cleanly
- [ ] Unknown agent name in phases surfaces the same ConfigException as default_agent
- [ ] All phases (incl. chain and draft mode) work end-to-end with all-claude-code config
- [ ] Tests pass

Notes: This opens per-phase experimentation: `review = "codex"` (once Codex lands) runs review on Codex while plan/implement stay on Claude. One agent per phase, not separate reviewer-vs-verifier slots; splitting is a v1.1 question. Watch for accidental shared-instance reuse that would defeat per-phase selection.

OOS:
- Do not add a per-phase agent for ship (no worker)
- Do not split review reviewer and verifier into separate slots (v1.1)
- Do not allow phase keys other than plan/implement/review (unknown keys throw)
- Do not implement fallback indirection beyond one level of mapping

#### Brief 04: cli-flag-override

Goal: `--agent <name>` overrides the configured agent for an invocation; on chain, `--agent <name>` applies to all phases and `--agent-<phase> <name>` overrides a single phase. CLI flag always wins over config.

Inputs:
- Per-phase selection from B03
- Verb dispatch and flag parsing in `Program.cs`; usage text in `CliUsage.cs`

Outputs:
- `--agent <name>` on `build plan`, `build implement`, `build review`
- `--agent <name>` and `--agent-plan` / `--agent-implement` / `--agent-review` on `build chain`
- Flag value validated through the factory (unknown name fails fast with the factory's error)
- Absent flag uses configured selection (B03)
- Flags documented in usage text
- Tests: --agent overrides config on each verb; unknown value fails through factory; --agent-<phase> work independently on chain; bare --agent on chain applies to all phases; flag beats config

Acceptance:
- [ ] `--agent` works on plan/implement/review
- [ ] `--agent` and `--agent-<phase>` work on chain
- [ ] Unknown agent name produces the factory's clear error
- [ ] Usage text documents the flags
- [ ] Tests pass

Notes: Enables dispatch like `build chain 42 --agent-plan claude-code --agent-implement codex --agent-review claude-code` for a single chained run - the dynamic mid-orchestration swap Dan wants. CLI flag always wins; no config can override an explicit flag value.

OOS:
- Do not implement env-var override (config + flag suffice)
- Do not implement persistent per-ticket overrides
- Do not implement --agent on ship (no worker)
- Do not add interactive "which agent?" prompts (silent config falls back to default per B03)

## Plan B: Cleanup

### Goal

Move `WorkerResultParser` out of `Workers.ClaudeCode` to a shared `Workers.Common` assembly. Rename `ClaudeCodeReviewer` to `WorkerAgentReviewer` and delete its dead `FlattenLlmUsage` copy. Introduce `IWorkerProgressDigester` so each agent supplies its own digester (or null). Capture `cost_usd` and make `vendor` per-agent. Hygiene that should land before any second agent so it does not inherit misnamed types, a mislocated parser, a hardcoded vendor, or missing cost.

Briefs are independent and can land in any order.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | parser-relocation | Move WorkerResultParser to a new Workers.Common assembly (depends only on Contracts) | - | src/ThroughlineBuild.Workers.Common/ (new project + WorkerResultParser.cs), src/ThroughlineBuild.Workers.ClaudeCode/ (delete parser, add reference), throughline-build.sln, tests/ThroughlineBuild.Workers.Common.Tests/ (new) |
| 06 | verifier-rename | Rename ClaudeCodeReviewer -> WorkerAgentReviewer; delete dead FlattenLlmUsage/UnwrapJsonElement; no verdict-logic change | - | src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs (renamed), src/ThroughlineBuild.Phases/ReviewPhase.cs (construction site), tests/ThroughlineBuild.Verification.Tests/ (rename) |
| 07 | progress-digester-abstraction | IWorkerProgressDigester in Contracts; ClaudeCodeProgressDigester implements it; IWorkerAgent exposes nullable Digester | - | src/ThroughlineBuild.Contracts/IWorkerProgressDigester.cs (new), src/ThroughlineBuild.Contracts/IWorkerAgent.cs (add Digester), src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeProgressDigester.cs (renamed from WorkerProgressDigest.cs), tests/ |
| 08 | cost-and-vendor-capture | Capture cost_usd from the wire envelope; de-hardcode vendor to a per-agent value; flow both through the flattener and LlmCall payload | - | src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs, src/ThroughlineBuild.Helpers/LlmUsageFlattener.cs, docs/event-log-format.md, tests/ |

### Briefs - detail

#### Brief 05: parser-relocation

Goal: Move `WorkerResultParser` from `Workers.ClaudeCode` to a new `Workers.Common` assembly so future agents reuse the WORKER_RESULT marker scan without depending on the ClaudeCode project.

Inputs:
- Current `WorkerResultParser.cs` in `Workers.ClaudeCode`
- Existing parser tests in `tests/ThroughlineBuild.Workers.ClaudeCode.Tests/`, including the AOT regression coverage (`WorkerResultParserAotRegressionTests`)

Outputs:
- New project `ThroughlineBuild.Workers.Common` (classlib, depends only on Contracts), added to `throughline-build.sln`
- `WorkerResultParser.cs` moved into it; `Workers.ClaudeCode` references `Workers.Common`
- Parser tests moved to `ThroughlineBuild.Workers.Common.Tests/`, AOT regression coverage preserved (the test project must still exercise the parser with `System.Text.Json` reflection disabled, since test projects do not inherit PublishAot)
- `ClaudeCodeAgent.cs` updated to the new namespace
- WORKER_RESULT envelope schema documented in XML doc on the class

Acceptance:
- [ ] `Workers.Common` exists with `WorkerResultParser` and is in the solution
- [ ] `Workers.ClaudeCode` depends on `Workers.Common` and uses the relocated parser
- [ ] Parser tests (incl. AOT-disabled-reflection regression) pass in their new location
- [ ] No other behavior change

Notes: The "find the WORKER_RESULT marker, parse the following JSON" logic is the agent-agnostic contract. Moving it here makes the contract explicit and stops each new agent re-implementing the scan. If `Workers.Common` grows, it becomes the home for other generic worker helpers; for now it is one class.

OOS:
- Do not move `ClaudeCodeJsonEnvelope` (vendor-specific; stays in Workers.ClaudeCode)
- Do not refactor the parser interface or add features
- Do not change the WORKER_RESULT envelope contract

#### Brief 06: verifier-rename

Goal: Rename `ClaudeCodeReviewer` to `WorkerAgentReviewer`. It depends only on `IWorkerAgent`; the name is misleading. Delete the dead `FlattenLlmUsage`/`UnwrapJsonElement` copy (lines 141-183) that duplicates `LlmUsageFlattener`. No verdict-logic change.

Inputs:
- Current `ClaudeCodeReviewer.cs` in `Verification` (implements `IVerifier`, wraps an injected `IWorkerAgent`, validates `verdict`/`rationale`/`checks_failed` from worker metadata at lines 73-100, dead flatten copy at 141-183)
- Construction site in `ReviewPhase.cs`
- Tests in `tests/ThroughlineBuild.Verification.Tests/`

Outputs:
- File and class renamed to `WorkerAgentReviewer`
- Construction site in `ReviewPhase.cs` updated
- Dead `FlattenLlmUsage`/`UnwrapJsonElement` removed
- Verification tests renamed and updated for the type name

Acceptance:
- [ ] Class is `WorkerAgentReviewer` in `WorkerAgentReviewer.cs`
- [ ] `ReviewPhase` constructs `new WorkerAgentReviewer(...)`
- [ ] Dead flatten copy removed
- [ ] The review metadata validation (verdict/rationale/checks_failed) is unchanged and still enforced
- [ ] Existing tests pass (renamed)

Notes: Tiny brief, truth-in-naming. The class is already vendor-neutral.

OOS:
- Do not refactor the class API
- Do not change verdict logic or the metadata validation
- Do not rename phase tests that do not reference the type by name

#### Brief 07: progress-digester-abstraction

Goal: `IWorkerProgressDigester` in Contracts. Each agent supplies its own digester or null. Phases that consume progress digests do so through the interface; null means no live progress, workflow unaffected.

Inputs:
- Existing `WorkerProgressDigest` (static class in `Workers.ClaudeCode`, Claude stream-json-shape specific)
- Phases consume the digest via `WorkerOptions.ProgressDigestSink` (the sink stays; only the producer side changes)

Outputs:
- `IWorkerProgressDigester` in Contracts:
  ```csharp
  public interface IWorkerProgressDigester
  {
      string? FormatLine(string rawLine);
  }
  ```
  Returns null when the line is not digest-worthy; a formatted string when it is.
- `ClaudeCodeProgressDigester` (instance class, renamed from the static `WorkerProgressDigest`) implementing it
- `IWorkerAgent` gains `IWorkerProgressDigester? Digester { get; }` (nullable; agents without progress digests return null)
- `ClaudeCodeAgent.Digester` returns a singleton `ClaudeCodeProgressDigester`
- Phases read `_worker.Digester?.FormatLine(line)`; if null, skip the digest line
- Tests: ClaudeCodeProgressDigester handles known Claude stream shapes (ported); a null-digester agent causes phases to skip digest output without error; phases consume via interface, not concrete type

Acceptance:
- [ ] `IWorkerProgressDigester` in Contracts
- [ ] `ClaudeCodeProgressDigester` implements it
- [ ] `IWorkerAgent.Digester` nullable property exists and ClaudeCodeAgent exposes its digester
- [ ] Phases consume the digester through the interface
- [ ] Null digester degrades gracefully (no live progress, no error)
- [ ] Tests pass

Notes: The digester now travels with the agent and is selected by the same factory mechanism. HTTP agents (the deferred Ollama case) return null - no stream to digest. The static-to-instance change is small but is what makes the nullable contract honest.

OOS:
- Do not implement digesters for other agents (their op-docs add their own)
- Do not change the `WorkerOptions.ProgressDigestSink` consumer mechanism
- Do not add per-agent digest configuration (it is per-agent code, not config)

#### Brief 08: cost-and-vendor-capture

Goal: Capture `cost_usd` from the wire envelope (currently dropped) and make `vendor` a per-agent value (currently hardcoded `"anthropic"` at `ClaudeCodeAgent.cs:332`). Model capture is already done (`TryExtractModelFromStream` reads `model` from the `type=system` event with a configured-default fallback) and is NOT in scope.

Inputs:
- `BuildLlmUsageMetadata` at `ClaudeCodeAgent.cs:327-353` (emits model, vendor, wall_clock_ms, token + cache counts; no cost; vendor hardcoded)
- `ClaudeCodeJsonEnvelope` (has `type`, `subtype`, `is_error`, `result`, `usage{input/output/cache}`; no cost field). The CLI's terminal `type=result` line carries `total_cost_usd` on the wire but the DTO does not deserialize it.
- `LlmUsageFlattener` in `Helpers` (flattens `llm_usage` metadata into the `LlmCall` event Data)
- `docs/event-log-format.md` LlmCall schema (currently: model, vendor, input/output tokens, cache, wall_clock_ms, optional partial)

Outputs:
- `ClaudeCodeJsonEnvelope` gains `total_cost_usd` (nullable; some output formats may omit it). The record is already registered in `ClaudeCodeJsonContext`, so adding a property needs no new context registration.
- `BuildLlmUsageMetadata` emits `cost_usd` from the envelope's `total_cost_usd` (null-safe: missing cost -> null, no error)
- `vendor` no longer a literal: each agent supplies its vendor string. ClaudeCodeAgent passes `"anthropic"`; the value threads through `BuildLlmUsageMetadata` (parameter or agent field) so a future Codex agent emits `"openai"`, Gemini `"google"`, Copilot `"github"`. This is what lets `analyze-event-log` price mixed-vendor sessions correctly.
- `LlmUsageFlattener` passes `cost_usd` through to the flattened payload
- `LlmCall` event payload gains `cost_usd` (nullable); update `docs/event-log-format.md`
- AOT note: keep the metadata dict (`Dictionary<string,object>`) and event Data serializable under source-gen. If a boxed `decimal` does not round-trip cleanly through the EventLog JSON context, emit cost as a `double` (or a string) that the context already handles; pick the representation the existing flatten path serializes safely.
- Tests: a fixture with `total_cost_usd` populates `cost_usd`; a fixture without it leaves `cost_usd` null without error; vendor reflects the agent's supplied value, not a literal; flattener emits `cost_usd`; downstream consumers handle null cost gracefully

Acceptance:
- [ ] `cost_usd` captured from the envelope's `total_cost_usd`, null when absent
- [ ] `vendor` is per-agent (ClaudeCodeAgent emits "anthropic"; no hardcoded literal in BuildLlmUsageMetadata)
- [ ] `LlmUsageFlattener` and the `LlmCall` payload carry `cost_usd`
- [ ] `docs/event-log-format.md` updated for `cost_usd`
- [ ] AOT publish unaffected (representation chosen to serialize under source-gen)
- [ ] Tests pass for both populated and missing-cost cases
- [ ] Model capture left untouched and still works

Notes: Cost matters more in a multi-agent world; per-agent comparison needs reliable per-invocation cost, and today it is inferred from tokens. Per-agent vendor is the partner to cost: `analyze-event-log` keys its pricing off vendor+model, so a hardcoded vendor would mis-price the first Codex run. Fixing both here means every future agent inherits a working cost-tracking surface. The dead `FlattenLlmUsage` copy is removed by Brief 06; this brief uses only the canonical `LlmUsageFlattener`.

OOS:
- Do not touch model capture (already done)
- Do not refactor `LlmUsageFlattener`'s interface
- Do not change the flatten format beyond adding `cost_usd`
- Do not implement cross-phase cost aggregation, alerting, or limits (consumer concerns)

## Plan C: Sizing abstraction

### Goal

`WorkerSize` enum (Small/Medium/Large) in Contracts; `WorkerOptions` gains a `Size` field. Each agent's config carries a `[workers.<agent>.sizes]` map (size -> model id). Each agent resolves `WorkerOptions.Size` to a concrete model at invocation. The ticket is the source of truth for size: `PlaneTicketingClient.GetAsync` populates `Ticket.Size` from the Plane `size:` label (today it always returns `Size.M`), and phases map `Ticket.Size` to `WorkerSize`, falling back to Medium when absent.

Briefs are sequential: contract (09), per-agent map (10), then ticket-as-source plus phase plumbing (11).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 09 | worker-size-contract | WorkerSize enum in Contracts; add non-nullable Size to WorkerOptions (default Medium) | A | src/ThroughlineBuild.Contracts/WorkerSize.cs (new), src/ThroughlineBuild.Contracts/IWorkerAgent.cs (WorkerOptions.Size), tests/ |
| 10 | per-agent-size-map | [workers.<agent>.sizes] config; AgentConfig gains Sizes; ClaudeCodeAgent resolves model from the map; deprecate [llm].default_model as the worker-model source | 09 | src/ThroughlineBuild.Cli/Config.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs, .build/config.toml, tests/ |
| 11 | ticket-size-source-and-plumbing | GetAsync extracts Size from the Plane size label; phases map Ticket.Size -> WorkerSize and pass via WorkerOptions.Size; fallback Medium | 10 | src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs (new), src/ThroughlineBuild.Phases/PlanPhase.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs, tests/ |

### Briefs - detail

#### Brief 09: worker-size-contract

Goal: Introduce `WorkerSize` as the abstract sizing contract callers use to communicate intent to agents.

Inputs:
- Current `WorkerOptions` record in `IWorkerAgent.cs`

Outputs:
- `WorkerSize` enum in Contracts: `Small`, `Medium`, `Large`
- `WorkerOptions` gains `WorkerSize Size` (non-nullable; defaults to `Medium`)
- XML doc: an abstract size signal agents map to their own model tiers; sourced from the ticket size (Plan C)
- Existing `WorkerOptions` call sites get the default `Medium`; behavior unchanged this brief
- Tests: default is Medium; explicit value is carried

Acceptance:
- [ ] `WorkerSize` enum exists with three variants
- [ ] `WorkerOptions.Size` is non-nullable, defaults to Medium
- [ ] Existing call sites compile unchanged
- [ ] Tests pass

Notes: An enum (not a string) keeps the taxonomy bounded at the contract layer; growth (XS/XL) happens centrally and is then a config-map entry per agent, not agent code. `WorkerSize` is worker-domain and deliberately separate from the ticket-domain `Size` enum; Brief 11 maps one to the other. They are not the same axis and are not unified: a large ticket is a unit of work that the planned `decompose` command can break into sub-tasks, so ticket size and the model tier a worker runs at are independent. Keeping them separate leaves that relationship free rather than hardwired 1:1.

OOS:
- Do not add agent-side resolution (B10) or phase plumbing (B11)
- Do not allow free-form string sizes

#### Brief 10: per-agent-size-map

Goal: Each agent reads `[workers.<agent>.sizes]` mapping `WorkerSize` to concrete model ids. ClaudeCodeAgent resolves the model from `WorkerOptions.Size` and passes it to the claude CLI. This deprecates `[llm].default_model` as the worker-model source.

Inputs:
- `WorkerSize` from B09; config schema from B02
- ClaudeCodeAgent's current `Model`/`DefaultModel` options (sourced today from `config.llm.default_model`)
- The `[llm]` section (still consumed by `ReasonTranslator` via `AnthropicClient`; confirm before removing anything)

Outputs:
- `ClaudeCodeAgentConfig` (and the generic AgentConfig shape) gains `Sizes: IReadOnlyDictionary<WorkerSize, string>`
- Config shape:
  ```toml
  [workers.claude-code]
  executable = "claude"
  max_output_tokens = 32000

  [workers.claude-code.sizes]
  small  = "anthropic:claude-haiku-4-5"
  medium = "anthropic:claude-sonnet-4-6"
  large  = "anthropic:claude-opus-4-7"
  ```
- ClaudeCodeAgent uses `options.Size` to look up the model id and pass it via the existing `--model` mechanism (NormalizeModel still strips the `anthropic:` prefix)
- The single `ClaudeCodeOptions.Model` default is removed; the agent resolves per-invocation from the sizes map
- Load-time validation: every registered agent must map all three sizes, else ConfigException
- Deprecate `[llm].default_model`'s worker-model role only; leave `[llm]` intact for `ReasonTranslator`/judgment-slot use
- Update `.build/config.toml` with the sizes sub-section
- Tests: sizes parsed; Size=Small resolves to haiku, Size=Large to opus; missing size at load throws; `[llm].default_model` no longer drives worker model selection

Acceptance:
- [ ] `[workers.<agent>.sizes]` parsed per agent
- [ ] ClaudeCodeAgent resolves model from the sizes map by `WorkerOptions.Size`
- [ ] Missing sizes at load produce a loud error
- [ ] `[llm].default_model` deprecated for worker-model selection; other `[llm]` consumers untouched (confirmed)
- [ ] Tests pass

Notes: This is where "Copilot uses Opus for Large, Codex for Medium" becomes config, not code - each agent maps abstract sizes to whatever it runs. Requiring all three sizes (vs partial maps with fallbacks) keeps it explicit; a load-time ConfigException beats a runtime surprise.

OOS:
- Do not validate model strings against live availability
- Do not add per-phase size overrides at config level (v1.1)
- Do not add a "default size" config field (Medium is the WorkerOptions default)
- Do not allow inline size definitions in `[workers.phases]`

#### Brief 11: ticket-size-source-and-plumbing

Goal: Make the ticket the source of truth for size, then thread it to the worker. `PlaneTicketingClient.GetAsync` populates `Ticket.Size` from the Plane `size:` label (today it hardcodes `Size.M`). Phases map `Ticket.Size` to `WorkerSize` via a small mapper and pass it through `WorkerOptions.Size`. Missing/unrecognized -> Medium.

Inputs:
- `PlaneTicketingClient.GetAsync` returning `Size.M`/`Risk.Medium` unconditionally (label-driven extraction unimplemented; `Size.S`/`Size.L` never constructed today)
- The `size:` label the plan phase already writes to the ticket
- `WorkerSize` from B09; phases consume `Ticket` and construct `WorkerOptions`
- Plane label-name cache already loaded by the client

Outputs:
- `GetAsync` reads the ticket's `size:` label and maps it to `Size.S/M/L` (retiring the always-M behavior); unknown/absent -> `Size.M`. Risk extraction is out of scope - leave as-is.
- `WorkerSizeMapper.FromTicketSize(Size) -> WorkerSize` in Helpers (S->Small, M->Medium, L->Large)
- ImplementPhase and ReviewPhase set `WorkerOptions.Size` from `WorkerSizeMapper.FromTicketSize(ticket.Size)`
- PlanPhase uses a sensible default size for plan reasoning (Small is reasonable; the plan run does not yet have a ticket size since it is producing it). Operator override is available via the B04 `--agent` swap if a plan needs a heavier model.
- Tests: a ticket labelled `size:L` yields `Ticket.Size == Large`-mapped worker size; missing label -> Medium; end-to-end, a `size:L` ticket drives implement to invoke the worker with Large, resolving to opus per the claude-code sizes map; ReviewPhase mirrors implement

Acceptance:
- [ ] `GetAsync` populates `Ticket.Size` from the Plane size label (S/M/L), not a hardcoded M
- [ ] `WorkerSizeMapper.FromTicketSize` exists and is used by implement and review
- [ ] Plan phase uses a sensible default size
- [ ] Missing/unrecognized size falls back to Medium
- [ ] End-to-end: `size:L` ticket -> implement worker runs Large -> opus per the sizes map
- [ ] Tests pass

Notes: This closes the chain - plan writes the size label to the ticket; the ticket is the canonical record; later one-shot `implement`/`review` invocations read it back via `GetAsync` and map to the worker model. It also retires the long-standing "Size.S/L never constructed" loose end. The brief bundles the Plane-side extraction with the phase plumbing because they are one logical thread (make ticket size real, then consume it); split into two briefs if you would rather sequence them separately.

OOS:
- Do not implement Risk-label extraction (separate concern)
- Do not add a CLI per-invocation size flag (the ticket is the contract; swap agents via --agent for a different sizes map)
- Do not implement size escalation ("retry larger on failure")
- Do not validate size against agent capabilities

## Plan D: Test contract base + per-agent brief variants

### Goal

A shared `IWorkerAgent` contract test base any new agent's suite inherits, and a per-agent brief template directory structure so each agent gets prompt scaffolding tuned to its CLI. Plus the tool-name research doc that feeds future variant creation.

Briefs are independent within this plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 12 | agent-contract-test-base | Abstract test base asserting IWorkerAgent invariants; ClaudeCode suite inherits it using real captured fixtures | A, B | tests/ThroughlineBuild.Workers.Common.Tests/IWorkerAgentContractTests.cs (new), tests/ThroughlineBuild.Workers.ClaudeCode.Tests/ClaudeCodeAgentContractTests.cs (new/extend) |
| 13 | brief-template-per-agent-variants | Per-agent template dirs; loader resolves by agent name; move all four existing templates into claude-code/ | A | src/ThroughlineBuild.Briefs/Templates/claude-code/ (move plan/implement/review/draft), src/ThroughlineBuild.Briefs/TemplateLoader.cs, src/ThroughlineBuild.Briefs/{Plan,Implement,Review,Draft}BriefBuilder.cs, src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj (embedded-resource globs), .gitattributes, tests/ |
| 14 | tool-name-research-doc | Document the Claude tool vocabulary -> Codex/Copilot/Gemini CLI mapping; input for future variant + agent op-docs | - | docs/agent-tool-name-mapping.md (new) |

### Briefs - detail

#### Brief 12: agent-contract-test-base

Goal: An abstract test base any new agent's suite extends; the base asserts the invariants every `IWorkerAgent` must obey. Adding agent N's tests becomes "override CreateAgent + supply fixtures, inherit the suite."

Inputs:
- Existing test doubles for reference (`StubWorker` and friends in the shared test support)
- Existing `ClaudeCodeAgentTests` for the patterns to extract
- Real captured `*.ndjson` stream fixtures already in `Workers.ClaudeCode.Tests` (use these, not synthesized envelopes)

Outputs:
- `IWorkerAgentContractTests` abstract class in `Workers.Common.Tests`
- Abstract members the subclass implements: `IWorkerAgent CreateAgent()`; `string KnownGoodFixturePath()`; `string KnownErrorFixturePath()`
- Base test methods: emits a valid WorkerResult envelope for a known-good fixture; returns a typed WorkerResult with required fields populated; respects `WorkerOptions.Timeout` and cancels the subprocess; handles a known-error fixture with `Status.Failed` + `FailureReason`; writes debug capture when `DebugCaptureDirectory` is set; `Name` is non-empty and matches the registry key
- `ClaudeCodeAgentContractTests` extends the base, supplies `CreateAgent` + real fixture paths
- AOT discipline: where the base exercises parser-bearing paths, disable `System.Text.Json` reflection so the test reflects AOT behavior (test projects do not inherit PublishAot)
- Tests: the suite passes against ClaudeCodeAgent; a deliberately-broken stub subclass fails the suite (sanity)

Acceptance:
- [ ] `IWorkerAgentContractTests` exists with the documented abstract + concrete methods
- [ ] `ClaudeCodeAgentContractTests` extends it and passes against real captured fixtures
- [ ] XML doc states the invariants tested
- [ ] Tests pass

Notes: This is the leverage move. The second agent's author writes a couple of fixture files and overrides two methods and gets the invariant suite for free. Fixtures must be real captured output - tests against fictional shapes are worse than none.

OOS:
- Do not extract every ClaudeCode-specific test into the base (only agent-agnostic invariants)
- Do not write factory contract tests here (Cli FactoryTests cover that)
- Do not test cross-agent comparisons (comparison-harness op-doc)

#### Brief 13: brief-template-per-agent-variants

Goal: Template structure supports per-agent variants. `TemplateLoader.Load(agentName, templateName)` resolves `Templates/<agentName>/<templateName>.md`. Move the existing four claude-code templates into `Templates/claude-code/`. Brief builders take the agent name and pass it through.

Inputs:
- Existing templates: `Templates/plan.md`, `implement.md`, `review.md`, `draft.md` (four, embedded resources, LF-pinned in `.gitattributes`)
- Existing `TemplateLoader` (caches embedded-resource lookups) and the `{{key}}` substitution
- The four brief builders: `PlanBriefBuilder`, `ImplementBriefBuilder`, `ReviewBriefBuilder`, `DraftBriefBuilder`
- Per-phase agent selection from B03 (so builders know which agent the brief is for)

Outputs:
- Templates restructured:
  ```
  Templates/
    claude-code/
      plan.md
      implement.md
      review.md
      draft.md
  ```
- Embedded-resource names change with the move; update the `.csproj` resource globs, the `.gitattributes` LF pins, and `TemplateLoader`'s resource-name resolution accordingly
- `TemplateLoader.Load(string agentName, string templateName)` resolves to the per-agent path
- All four brief builders take an `agentName` parameter and pass it to the loader; the composition root passes the per-phase agent name in
- For v1, claude-code is the only variant; a missing variant for a registered agent is a clear error (no silent default-template fallback yet)
- Snapshot tests run against the `Templates/claude-code/` baseline and must be byte-equivalent to the pre-move templates

Acceptance:
- [ ] All four templates moved into `Templates/claude-code/`
- [ ] `TemplateLoader` resolves by agent name; resource globs/LF pins updated
- [ ] All four brief builders (including Draft) take and use the agent name
- [ ] Existing brief snapshots pass against the claude-code variant (byte-equivalent)
- [ ] Missing variant for a registered agent produces a clear error
- [ ] Tests pass

Notes: Establishes the per-agent template surface; it does not create non-Claude variants - those ship with each agent's own op-doc (e.g. `Templates/codex/*`). The B14 research doc tells those op-docs what to substitute. No `rework.md`: rework reuses `implement.md` with feedback woven in.

OOS:
- Do not create Codex/Copilot/Gemini variants (agent-specific op-docs)
- Do not implement template inheritance/layering
- Do not implement operator-level template overrides
- Do not implement default/fallback template behavior

#### Brief 14: tool-name-research-doc

Goal: A reference doc mapping the Claude Code tool vocabulary (Grep, Glob, Read, Write/Edit, Bash) to the equivalents in the Codex, Copilot, and Gemini CLIs, plus each CLI's headless-invocation and result-envelope facts. Input for B13's future variant creation and for each agent's op-doc.

Inputs:
- The existing claude-config port as prior research: `copilot-prompts/` and the codex `plane-ticket-workflow/` reference set, generated by `bin/sync-*` from `CLAUDE.md`. These preserve Claude's `Glob/Grep/Read/Bash` vocabulary and adapt at the shell/behavior layer; they are workflow prompts, not tool specs, so treat them as context, not the answer.
- Live inspection of each CLI (`codex --help` / `gemini --help` / `copilot --help` and their exec/non-interactive modes) as the primary source
- Public docs for each CLI's tool taxonomy

Outputs:
- `docs/agent-tool-name-mapping.md`
- Per tool category, the mapping Claude -> Codex / Copilot / Gemini: search (Grep), file pattern (Glob), file read (Read), file edit/write (Edit/Write), shell (Bash)
- Per CLI, the foundation-relevant facts each agent implementation will need: non-interactive invocation flag and output format (does it stream structured events or emit one blob); brief-delivery mechanism (stdin/arg/file); whether the model can be instructed to emit our WORKER_RESULT block and have it survive the output format; the auth env var to strip for subscription auth (Codex `OPENAI_API_KEY`, Gemini `GEMINI_API_KEY`/`GOOGLE_API_KEY`, Copilot TBD); the `--model` equivalent and accepted model id strings (for the sizes map); the output-token-cap equivalent of `CLAUDE_CODE_MAX_OUTPUT_TOKENS`; whether per-run cost/usage is reported and in what field; the vendor string for the event log
- Notes where an agent has no equivalent for a tool, or a tool the others lack
- A comparison table mapping each CLI to the `ClaudeCodeAgent`/`ClaudeCodeOptions` surface, flagging any field that does not map cleanly (those become contract questions for the agent's op-doc)

Acceptance:
- [ ] `docs/agent-tool-name-mapping.md` exists
- [ ] Covers Codex, Copilot, Gemini for the core tool categories and the foundation-relevant CLI facts above
- [ ] Notes gaps where an agent has no equivalent
- [ ] Cites the live CLI inspection and the prior claude-config port
- [ ] Committed

Notes: Research, not code. Time-bounded; where a public source is silent, mark "TBD - confirm during the agent's op-doc" rather than blocking. The deliverable feeds B13's future variant work and each agent op-doc; it is in Plan D because that is the "make multi-agent practical" plan.

OOS:
- Do not write the actual template variants (each agent's op-doc does, using this doc)
- Do not benchmark/evaluate the agents (comparison-harness op-doc)
- Do not document install/auth/pricing as an operational guide (separate doc)
- Do not cover Ollama or other HTTP/non-CLI agents (deferred; different shape)

## What done looks like

The CLI supports per-phase agent selection. Config has per-agent sub-tables with `[workers.<agent>.sizes]` maps. The factory dispatches by name; `--agent` and `--agent-<phase>` override config per invocation. The `WorkerSize` abstraction lets the ticket's size label drive worker model selection automatically: `GetAsync` reads `Ticket.Size` from the label, the phase maps it to a `WorkerSize`, and the agent resolves that to its own model - a small ticket implemented by claude-code runs on haiku, a large one on opus, a medium one on sonnet. Each agent owns its sizes map, so switching an agent for a phase reshapes the model-selection landscape transparently.

The cleanup landed: `WorkerResultParser` lives in `Workers.Common` and is reused; `ClaudeCodeReviewer` is now `WorkerAgentReviewer` with the dead flatten copy gone; `IWorkerProgressDigester` is per-agent and nullable; `cost_usd` is captured and `vendor` is per-agent, so the event log prices mixed-vendor sessions correctly (model capture was already in place).

A shared agent-contract test base means adding any agent N+1 inherits the invariant suite for free against real captured fixtures. Per-agent brief template variants are wired with the claude-code variants (all four, including draft) in place; each future agent op-doc creates its own under `Templates/<agent>/`. The tool-name research doc captures what those variants - and the agent implementations - need.

After this op-doc lands, the Codex (or Copilot, or Gemini) agent op-doc is mechanical: add `ThroughlineBuild.Workers.<Agent>` as a peer to `Workers.ClaudeCode`, implement `IWorkerAgent` (+ optional `IWorkerProgressDigester`) with the agent's options, register in the factory, add `[workers.<agent>]` config + sizes map, create `Templates/<agent>/` variants from the research doc, write fixtures, extend the contract test base. No phase changes, no factory-mechanism changes. When that recipe is all that stands between you and a second agent, the foundation is done.