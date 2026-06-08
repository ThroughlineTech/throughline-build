# 04 - Configuration and Environment

Every config file the binary reads, every environment variable it consults, every secret it requires, and whether each is required or optional.

For installation-time concerns (including the `build init` bootstrap) see [02-install-build-run.md](02-install-build-run.md). For state files written at runtime see [05-state-and-persistence.md](05-state-and-persistence.md).

---

## `.build/config.toml`

The single source of operator-controlled configuration. Discovered by `FindConfigFile`, walking up from cwd looking for `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:104-115](../../src/ThroughlineBuild.Cli/Config.cs#L104-L115)). Missing file: exit 2 with `Config error: config file not found: searched from <cwd> upwards for .build/config.toml` ([src/ThroughlineBuild.Cli/Program.cs:430-438](../../src/ThroughlineBuild.Cli/Program.cs#L430-L438)).

Parsed by `Tomlyn` into the typed records `TicketingConfig`, `LlmConfig`, `WorkersConfig` (containing `AgentConfig` per agent), `EventsConfig`, `ReviewConfig`, `ShipConfig`, `WorkConfig`, `ProjectContext`, `PlanConfig`, and `BatchConfig` ([src/ThroughlineBuild.Cli/Config.cs:9-93](../../src/ThroughlineBuild.Cli/Config.cs#L9-L93)). The ten section readers run in `Load` ([src/ThroughlineBuild.Cli/Config.cs:142-151](../../src/ThroughlineBuild.Cli/Config.cs#L142-L151)).

There is now a single template, no separate hand-edit `.example` (the old `.build/config.toml.example` is gone):
- [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template) - the embedded resource emitted by `build init`, loaded by `ConfigTemplateLoader.Load` ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:20-36](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L20-L36)) from the assembly manifest (no disk-relative lookup, keeping the AOT single-binary contract). It uses `REQUIRED_*` placeholders for the four Plane fields and ships `default_agent = "claude-code"` ([config.toml.template:24](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L24)) with both `[workers.claude-code]` and `[workers.codex]` blocks uncommented.

The template-vs-checked-in `default_agent` split is RESOLVED: the live checked-in operator config [.build/config.toml](../../.build/config.toml) also sets `default_agent = "claude-code"` ([.build/config.toml:25](../../.build/config.toml#L25)). Both comment out `default_model`. There is no hardcoded vendor default in C# - `default_agent` is a required string ([src/ThroughlineBuild.Cli/Config.cs:544](../../src/ThroughlineBuild.Cli/Config.cs#L544)) and the factory keys off whatever name is configured (see [`[workers.phases]`](#workersphases-optional-sub-table---functional)). An undefined `default_agent` (or phase agent) is now caught at load time (see [`[workers]`](#workers-required-section---functional)).

### Unknown-key warnings (TLB-405)

After the typed sections load, `BuildConfigLoader.Load` runs a non-fatal validation pass that emits one `warning: unknown config key <path> - ignored` per unrecognized key to `stderr` (or a supplied `warnSink`) - the run still proceeds. Driver: [src/ThroughlineBuild.Cli/Config.cs:153-167](../../src/ThroughlineBuild.Cli/Config.cs#L153-L167); implementation `CollectUnknownKeyWarnings` [src/ThroughlineBuild.Cli/Config.cs:276-447](../../src/ThroughlineBuild.Cli/Config.cs#L276-L447). The allowlists are static `HashSet<string>` per scope: top-level sections [src/ThroughlineBuild.Cli/Config.cs:196-199](../../src/ThroughlineBuild.Cli/Config.cs#L196-L199) (now ten, including `batch`) and per-section key sets [src/ThroughlineBuild.Cli/Config.cs:201-274](../../src/ThroughlineBuild.Cli/Config.cs#L201-L274). The pass descends through the nested worker tables: each `[workers.<agent>]` sub-table is checked against `KnownAgentKeys` (`executable`, `max_output_tokens`, `bypass_permissions`, `sizes`), its `sizes` sub-table against `KnownSizesKeys` (`small`/`medium`/`large`), and each inline tier table against `KnownTierKeys` (`model`, `effort`) ([src/ThroughlineBuild.Cli/Config.cs:317-343](../../src/ThroughlineBuild.Cli/Config.cs#L317-L343)). Entries inside `[[review.checks]]` and `[[ship.regression_checks]]` are validated against `KnownCheckEntryKeys` (`name`, `executable`, `arguments`, `timeout_minutes`, `role`) ([src/ThroughlineBuild.Cli/Config.cs:245-248](../../src/ThroughlineBuild.Cli/Config.cs#L245-L248)). `[workers.phases]` is skipped by this pass because it already hard-errors on unknown keys (see below). The warning pass is wired into the load path in `Program.cs`.

### Required-field handling (TLB-369)

Required scalars go through `RequireString`, which throws `ConfigException` (`missing required key '<k>' in [<section>]` or `key '<k>' in [<section>] must be a non-empty string`) ([src/ThroughlineBuild.Cli/Config.cs:468-475](../../src/ThroughlineBuild.Cli/Config.cs#L468-L475)); required sections go through `RequireSection` (`missing required TOML section [<section>]`) ([src/ThroughlineBuild.Cli/Config.cs:449-454](../../src/ThroughlineBuild.Cli/Config.cs#L449-L454)). Both surface as `Config error: ...`, exit 2 ([src/ThroughlineBuild.Cli/Program.cs:447-452](../../src/ThroughlineBuild.Cli/Program.cs#L447-L452)). The `build init` template annotates every required field with an inline `REQUIRED` comment.

### `[ticketing]` (required) - Functional

`ReadTicketingSection` ([src/ThroughlineBuild.Cli/Config.cs:506-518](../../src/ThroughlineBuild.Cli/Config.cs#L506-L518)).

| Key | Required | Default | Source |
|---|---|---|---|
| `backend` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:510](../../src/ThroughlineBuild.Cli/Config.cs#L510) - value is read but never compared; only `"plane"` is wired (no other adapter exists). |
| `plane_base_url` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:511](../../src/ThroughlineBuild.Cli/Config.cs#L511) |
| `plane_workspace_slug` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:512](../../src/ThroughlineBuild.Cli/Config.cs#L512) |
| `plane_project_id` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:513](../../src/ThroughlineBuild.Cli/Config.cs#L513) - UUID of the Plane project. |
| `plane_api_token_env` | no | `PLANE_API_TOKEN` | [src/ThroughlineBuild.Cli/Config.cs:514](../../src/ThroughlineBuild.Cli/Config.cs#L514) - name of the env var holding the token when not inline. |
| `plane_project_identifier` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:515](../../src/ThroughlineBuild.Cli/Config.cs#L515) - e.g. `"TLB"`. Used as a filename component and in Plane client options. |
| `plane_project_name` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:516](../../src/ThroughlineBuild.Cli/Config.cs#L516) - e.g. `"throughline-build"`. Filename component / `SessionContext.ProjectName`. |
| `plane_api_token` | no | `null` | [src/ThroughlineBuild.Cli/Config.cs:517](../../src/ThroughlineBuild.Cli/Config.cs#L517) - inline token; takes precedence over env. |

A missing or empty required key throws `ConfigException` via `RequireString`/`RequireSection` (`missing required key '<k>' in [ticketing]` or `must be a non-empty string`); CLI exits 2 with `Config error: ...` ([src/ThroughlineBuild.Cli/Config.cs:468-475](../../src/ThroughlineBuild.Cli/Config.cs#L468-L475)).

### `[llm]` (optional section, optional keys) - Functional

If the section is absent, all values default to empty strings ([src/ThroughlineBuild.Cli/Config.cs:520-528](../../src/ThroughlineBuild.Cli/Config.cs#L520-L528)). The whole section is optional.

| Key | Default | Use |
|---|---|---|
| `default_model` | `""` | **DEPRECATED for worker-model selection.** Vendor-prefixed model id for the direct Anthropic client used by reason translation. Consumed ONLY by `LlmClientFactory.Create` for `close`/`defer`/`reopen`; not passed to worker agents - workers get their model from `[workers.<agent>.sizes]`. Commented out in the live config ([.build/config.toml:16-20](../../.build/config.toml#L16-L20)). ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29)) |
| `anthropic_api_key_env` | `""` | Name of the env var holding the Anthropic key. |
| `anthropic_api_key` | `null` | Inline key; takes precedence over env ([src/ThroughlineBuild.Cli/Config.cs:527](../../src/ThroughlineBuild.Cli/Config.cs#L527)). |

`LlmClientFactory` requires `default_model` to be non-empty and to start with `anthropic:`; any other prefix throws `unsupported LLM vendor prefix '<p>' in [llm] default_model; only 'anthropic:' is supported`, and an empty value throws `LLM client required but [llm] default_model is not set in config.toml` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29)). These errors only surface on `close`/`defer`/`reopen`, and even then are non-fatal: if no client can be built the verb falls back to an `EchoLlmClient` that records the reason verbatim (see [Reason translation](#reason-translation-is-the-only-llm-consumer)). Other verbs never construct the direct LLM client at all.

### `[workers]` (required section) - Functional

`ReadWorkersSection` ([src/ThroughlineBuild.Cli/Config.cs:530-655](../../src/ThroughlineBuild.Cli/Config.cs#L530-L655)).

| Key | Required | Default |
|---|---|---|
| `default_agent` | yes | - ([src/ThroughlineBuild.Cli/Config.cs:544](../../src/ThroughlineBuild.Cli/Config.cs#L544)) |
| `timeout_minutes` | no | `30` ([src/ThroughlineBuild.Cli/Config.cs:545](../../src/ThroughlineBuild.Cli/Config.cs#L545)) |
| `max_concurrency` | no | `min(ProcessorCount, 4)` ([src/ThroughlineBuild.Cli/Config.cs:647](../../src/ThroughlineBuild.Cli/Config.cs#L647)) - retained config key; the dispatcher is pinned to serial execution regardless (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). |

`default_agent` is the agent name used for any phase not overridden in `[workers.phases]` or by a CLI flag. There is no hardcoded vendor default; both the template and the live config set `claude-code`.

**Undefined-agent enforcement (TLB-512, commit e05d918):** `ReadWorkersSection` now validates at load time that `default_agent` and every `[workers.phases]` agent name resolve to a defined `[workers.<name>]` sub-table; a name with no matching block throws a friendly `ConfigException` via `BuildUndefinedAgentMessage` ([src/ThroughlineBuild.Cli/Config.cs:638-672](../../src/ThroughlineBuild.Cli/Config.cs#L638-L672)) that names the offending setting, the fix (uncomment/add the sub-table or repoint the setting), and which agents ARE defined. This routes through the `Config error:` exit-2 handler instead of blowing up later as an unhandled exception at agent-resolution time. The classic trigger was leaving `[workers.codex]` commented while `default_agent = "codex"`. Program.cs still carries a belt-and-suspenders `missing [workers.<name>] sub-table in config` throw at factory-build time ([src/ThroughlineBuild.Cli/Program.cs:1063-1064](../../src/ThroughlineBuild.Cli/Program.cs#L1063-L1064), [1081-1082](../../src/ThroughlineBuild.Cli/Program.cs#L1081-L1082)), but in practice the load-time check fires first.

Migration guard (hard-break): the old flat keys `claude_code_executable` and `max_output_tokens` directly under `[workers]` now throw a hard `ConfigException` directing the operator to move them into a `[workers.<name>]` sub-table ([src/ThroughlineBuild.Cli/Config.cs:535-542](../../src/ThroughlineBuild.Cli/Config.cs#L535-L542)).

Every sub-table under `[workers]` other than `phases` is parsed as an agent config ([src/ThroughlineBuild.Cli/Config.cs:550-627](../../src/ThroughlineBuild.Cli/Config.cs#L550-L627)).

### `[workers.<agent-name>]` (one block per agent) - Functional

Parsed into `AgentConfig(Executable, MaxOutputTokens, Sizes, BypassPermissions)` ([src/ThroughlineBuild.Cli/Config.cs:24](../../src/ThroughlineBuild.Cli/Config.cs#L24), populated at [src/ThroughlineBuild.Cli/Config.cs:572-626](../../src/ThroughlineBuild.Cli/Config.cs#L572-L626)). `Sizes` is now an `IReadOnlyDictionary<WorkerSize, ModelTier>` (the size->`{model, effort}` map; see [`[workers.<name>.sizes]`](#workersnamesizes-required-per-agent---functional) below), not a bare model-id map.

| Key | Required | Default | Notes |
|---|---|---|---|
| `executable` | yes | - | Path or bare command for the worker CLI ([src/ThroughlineBuild.Cli/Config.cs:572](../../src/ThroughlineBuild.Cli/Config.cs#L572)). |
| `max_output_tokens` | no | `null` | Only `ClaudeCodeAgent` uses it (sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS`); Codex/Gemini/Copilot accept the key but do not apply it ([src/ThroughlineBuild.Cli/Config.cs:573-578](../../src/ThroughlineBuild.Cli/Config.cs#L573-L578)). |
| `bypass_permissions` | no | `true` | Per-agent unattended-mode toggle (TLB-229). `true` emits the agent's skip-permissions flag; `false` opts back into the interactive gate ([src/ThroughlineBuild.Cli/Config.cs:584-586](../../src/ThroughlineBuild.Cli/Config.cs#L584-L586)). |
| `[workers.<name>.sizes]` | yes | - | Required sub-table mapping `small`/`medium`/`large` to inline `{ model, effort }` tier tables (TLB-196/197/198, op-33). A missing sub-table throws ([src/ThroughlineBuild.Cli/Config.cs:588-625](../../src/ThroughlineBuild.Cli/Config.cs#L588-L625)). |

`bypass_permissions` is read into `AgentConfig.BypassPermissions` and forwarded to each agent's options by `WorkerAgentBuilder.Create` ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)). It translates to a different flag per agent: `--dangerously-skip-permissions` for Claude Code ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:412-413](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L412-L413)), `--dangerously-bypass-approvals-and-sandbox` for Codex ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:365-366](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L365-L366)), `--yolo` for Gemini ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:246-247](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L246-L247)). `CopilotOptions` has no `BypassPermissions` field; `WorkerAgentBuilder`'s `copilot` branch does not pass it ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:32-37](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L32-L37)) and Copilot always runs `-s --no-ask-user` ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:22-24](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L24)) - an asymmetry vs the other three agents.

#### `[workers.<name>.sizes]` (required per agent) - Functional

| Key | Required | Shape |
|---|---|---|
| `small` | yes | inline table `{ model = "...", effort = "..." }` |
| `medium` | yes | inline table `{ model = "...", effort = "..." }` |
| `large` | yes | inline table `{ model = "...", effort = "..." }` |

**op-33 schema change:** each size now maps to an inline TOML table, not a bare model string. Every size key is parsed into a `ModelTier(Model, Effort?)` ([src/ThroughlineBuild.Contracts/Models/ModelTier.cs:9](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs#L9)): `model` is a REQUIRED non-empty string, `effort` is an optional string (`null` when absent or empty). All three size keys must be present or the loader throws `[workers.<name>.sizes] is missing required size keys: ...`; a missing sub-table throws `missing required [workers.<name>.sizes] sub-table in config` ([src/ThroughlineBuild.Cli/Config.cs:588-625](../../src/ThroughlineBuild.Cli/Config.cs#L588-L625)). A bare model string (the pre-release form) now HARD-ERRORS with `[workers.<name>.sizes.<size>] must be an inline table like { model = "...", effort = "..." }, not a bare string` ([src/ThroughlineBuild.Cli/Config.cs:611-617](../../src/ThroughlineBuild.Cli/Config.cs#L611-L617)); a table missing `model` throws `must be an inline table with a non-empty 'model' string` ([src/ThroughlineBuild.Cli/Config.cs:600-602](../../src/ThroughlineBuild.Cli/Config.cs#L600-L602)).

`model` is the id the agent passes to its `--model` flag for that size tier. `effort` is acted on ONLY by Codex (`-c model_reasoning_effort=<effort>`; a no-op for claude-code/gemini/copilot, per the `ModelTier` doc comment). The tier is selected from the ticket's size: `S -> Small`, `L -> Large`, anything else `-> Medium` ([src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs:7-12](../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)), via the `WorkerSize` enum ([src/ThroughlineBuild.Contracts/Models/WorkerSize.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs)). Each agent strips its own vendor prefix before passing the id to the CLI: `anthropic:`, `openai:`, `google:`, `github:`. There is no per-phase `--model` flag; per-phase model selection is achieved indirectly by pointing a phase at a different agent (see `[workers.phases]`). This is what replaced the deprecated `[llm] default_model` worker-model path. The `[workers.codex.sizes]` block can be regenerated from a live Codex probe with `build models refresh` (see [Config-editing verbs](#config-editing-verbs)).

#### `[workers.phases]` (optional sub-table) - Functional

Maps phase names to agent names (TLB-189/190/191). Allowed keys: `plan`, `implement`, `review`, `decompose`; any other key throws `unknown phase key '<k>' in [workers.phases]` and an empty value throws `value for '<k>' in [workers.phases] must be a non-empty string` ([src/ThroughlineBuild.Cli/Config.cs:555-569](../../src/ThroughlineBuild.Cli/Config.cs#L555-L569)). This sub-table is skipped by the unknown-key warning pass because it already hard-errors on its own. Each phase agent name is also validated to resolve to a `[workers.<name>]` sub-table at load time (TLB-512, see [`[workers]`](#workers-required-section---functional)).

Resolution per phase: `AgentFor(phase)` returns the `[workers.phases]` mapping if present, else `default_agent` ([src/ThroughlineBuild.Cli/Program.cs:1090-1091](../../src/ThroughlineBuild.Cli/Program.cs#L1090-L1091)). `EffectiveAgentFor(phase)` then layers CLI flags on top: a per-phase flag (`--agent-plan` / `--agent-implement` / `--agent-review`) wins over `--agent` (all phases), which wins over config (TLB-191 cli-flag-override) ([src/ThroughlineBuild.Cli/Program.cs:1095-1099](../../src/ThroughlineBuild.Cli/Program.cs#L1095-L1099)). The agent flags are extracted before dispatch ([src/ThroughlineBuild.Cli/Program.cs:73-74](../../src/ThroughlineBuild.Cli/Program.cs#L73-L74), [src/ThroughlineBuild.Cli/CliArgParser.cs:25-52](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L25-L52)).

The orchestrator builds a factory entry per name referenced by `default_agent` or any phase mapping (plus any agent named on a CLI flag), each resolved to its `[workers.<name>]` config sub-table ([src/ThroughlineBuild.Cli/Program.cs:1078-1087](../../src/ThroughlineBuild.Cli/Program.cs#L1078-L1087)). The name->concrete-agent construction is centralized in `WorkerAgentBuilder.Create(name, cfg)`, which selects the implementation by name: `gemini`, `codex`, `copilot`, else `ClaudeCodeAgent` as fallback ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)). `WorkerAgentFactory.Create` throws a `ConfigException` listing the known agent names if an unknown name is requested. See [02-install-build-run.md](02-install-build-run.md) "Worker CLIs" for the per-agent table.

### `[work]` (optional section) - Functional

`ReadWorkSection` ([src/ThroughlineBuild.Cli/Config.cs:770-778](../../src/ThroughlineBuild.Cli/Config.cs#L770-L778)). Parsed into `WorkConfig(string? TargetBranch)` ([src/ThroughlineBuild.Cli/Config.cs:47](../../src/ThroughlineBuild.Cli/Config.cs#L47)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `target_branch` | no | `null` | The branch `ship` merges into and pushes, overriding `[ship].base_branch`. An empty string is rejected (treated as unset) at read time ([src/ThroughlineBuild.Cli/Config.cs:775](../../src/ThroughlineBuild.Cli/Config.cs#L775)). |

A hand-edited `target_branch` bypasses the `build settarget` branch check, so `Load` runs a non-fatal existence validation when a `branchExists` validator is supplied: if the configured branch does not resolve to a local ref, it emits `warning: [work].target_branch '<b>' does not resolve to a local branch - ship will block until it exists or you run 'build settarget'` through the same warning channel as the unknown-key pass ([src/ThroughlineBuild.Cli/Config.cs:162-163](../../src/ThroughlineBuild.Cli/Config.cs#L162-L163)). The check is skipped when no validator is passed (unit tests, or commands that do not touch git).

`BuildConfig.ResolveTargetBranch()` returns `Work.TargetBranch ?? Ship.BaseBranch` ([src/ThroughlineBuild.Cli/Config.cs:87](../../src/ThroughlineBuild.Cli/Config.cs#L87)); `TargetBranchOverridden` is true when `Work.TargetBranch is not null` ([src/ThroughlineBuild.Cli/Config.cs:92](../../src/ThroughlineBuild.Cli/Config.cs#L92)). Both flow into `ShipOptions.TargetBranch`/`TargetBranchOverridden` and `BuildOptions.TargetBranch`, consumed by `ShipPhase` and the target-aware `BaseRefResolver` (see [01-inventory.md](01-inventory.md) ship verb, [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). The intended editing path is the `build settarget` verb (see [Config-editing verbs](#config-editing-verbs)), which validates the branch exists locally before writing the key and preserves config comments via line-edit; hand-editing the TOML works too. When `target_branch != base_branch`, ship enforces that the main worktree is checked out on the target branch before merging (pre-merge guard `wrong_worktree_branch`).

### `[events]` (required section) - Functional

The section itself is required (`RequireSection`).

| Key | Required |
|---|---|
| `log_directory` | yes ([src/ThroughlineBuild.Cli/Config.cs:674-679](../../src/ThroughlineBuild.Cli/Config.cs#L674-L679)) |

Resolved by `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:172-178](../../src/ThroughlineBuild.Cli/Config.cs#L172-L178)). A relative value is resolved against the project root (parent of `.build/`), not the config file's directory. Typical value: `.build/events`.

### `[review]` (optional section, sensible defaults) - Functional

`ReadReviewSection` ([src/ThroughlineBuild.Cli/Config.cs:684-720](../../src/ThroughlineBuild.Cli/Config.cs#L684-L720)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:686-692](../../src/ThroughlineBuild.Cli/Config.cs#L686-L692)).

| Key | Default |
|---|---|
| `verifier_timeout_minutes` | `15` ([src/ThroughlineBuild.Cli/Config.cs:694](../../src/ThroughlineBuild.Cli/Config.cs#L694)) |
| `verifier_allowed_tools` | `["Read", "Grep", "Glob"]` ([src/ThroughlineBuild.Cli/Config.cs:681-682](../../src/ThroughlineBuild.Cli/Config.cs#L681-L682)) |
| `[[review.checks]]` (array-of-tables) | empty list ([src/ThroughlineBuild.Cli/Config.cs:697-714](../../src/ThroughlineBuild.Cli/Config.cs#L697-L714)) |

Each `[[review.checks]]` entry maps to a `CheckSpec(Name, Executable, Arguments, Timeout, Role)` ([src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:7-12](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L7-L12)) consumed during the review phase. `name` and `executable` are required; `arguments` defaults to empty, `timeout_minutes` defaults to `5`, and `role` defaults to `CheckRole.Gating`. Entry keys are validated against `KnownCheckEntryKeys` by the unknown-key warning pass; `role` is parsed by `ParseCheckRole` ([src/ThroughlineBuild.Cli/Config.cs:456-466](../../src/ThroughlineBuild.Cli/Config.cs#L456-L466)) into the `CheckRole` enum (`Gating`/`Advisory`, [src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:5](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L5)), and an invalid value throws a `ConfigException` at parse time. This same `[[review.checks]]` array is the "capability map" the gate phase consumes between implement and review (roles Gating/Advisory) - there is no separate `[gate]` table (see [11-llm-architecture.md](11-llm-architecture.md) gate section and [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)).

**Capability map - abstract check names and their roles:**

| Abstract name | Role | Rationale |
|---|---|---|
| `build` | gating | Non-zero exit is a hard block; implementer cannot proceed to review. |
| `test` | gating | Test failures hard-fail the gate. Pass `--no-build` when a `build` check precedes it to avoid recompiling. |
| `typecheck` | gating | Static type-check (distinct from build in languages where build does not type-check). In C#/dotnet, `dotnet build` is already the typecheck so a separate `typecheck` entry stays not-configured. |
| `lint` | advisory | Style/lint failures are recorded and surfaced to the verifier but never hard-fail the gate. |
| `format` | advisory | Formatting violations are recorded as advisory; the verifier sees them as a smoke signal. |

A check absent from config is not-configured and treated as not-run, never as a failure. The gate (Brief 06) skips checks not present in the configured list; it never synthesizes a failure for a missing check name.

**role field:**

| Value | Behaviour |
|---|---|
| `"gating"` (default) | Non-zero exit hard-fails the gate; the ticket cannot advance to review. |
| `"advisory"` | Result is recorded and shown to the verifier; gate does not hard-fail regardless of exit code. |

A missing or invalid `role` value at parse time: absent -> defaults to `"gating"`; an unrecognized string -> `ConfigException`: `key 'role' in [review.checks] must be either "gating" or "advisory", got "<v>"`.

```toml
[[review.checks]]
name = "build"
executable = "dotnet"
arguments = ["build"]
timeout_minutes = 5
role = "gating"

[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test", "--no-build"]
timeout_minutes = 10
role = "gating"

# Advisory example:
# [[review.checks]]
# name = "lint"
# executable = "dotnet"
# arguments = ["format", "--verify-no-changes"]
# timeout_minutes = 5
# role = "advisory"
```

### `[ship]` (optional section) - Functional

`ReadShipSection` ([src/ThroughlineBuild.Cli/Config.cs:722-768](../../src/ThroughlineBuild.Cli/Config.cs#L722-L768)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:724-732](../../src/ThroughlineBuild.Cli/Config.cs#L724-L732)).

| Key | Default |
|---|---|
| `remote` | `"origin"` ([src/ThroughlineBuild.Cli/Config.cs:734](../../src/ThroughlineBuild.Cli/Config.cs#L734)) |
| `base_branch` | `"main"` ([src/ThroughlineBuild.Cli/Config.cs:735](../../src/ThroughlineBuild.Cli/Config.cs#L735)) |
| `delete_feature_branch` | `true` ([src/ThroughlineBuild.Cli/Config.cs:736-738](../../src/ThroughlineBuild.Cli/Config.cs#L736-L738)) |
| `push` | `true` ([src/ThroughlineBuild.Cli/Config.cs:739-741](../../src/ThroughlineBuild.Cli/Config.cs#L739-L741)) |
| `[[ship.regression_checks]]` | empty list |

`push` (TLB-410) gates whether ship touches the remote after the local fast-forward merge. The effective no-push decision is `NoPush = noPush || !config2.Ship.Push`, so either the `--no-push` CLI flag or `push = false` in config disables the remote push; ship then rebases onto the local target and emits a `fetch_skipped` reason ([src/ThroughlineBuild.Cli/Program.cs:1571](../../src/ThroughlineBuild.Cli/Program.cs#L1571), [1794](../../src/ThroughlineBuild.Cli/Program.cs#L1794)). When `push = true` and the target branch does not yet exist on the remote, ship rebases onto the local target and lets the push create it (TLB-409).

Same `CheckSpec` shape and default rules as `review.checks` ([src/ThroughlineBuild.Cli/Config.cs:743-760](../../src/ThroughlineBuild.Cli/Config.cs#L743-L760)). The two lists are independent so each phase evolves separately. Regression checks are baseline-aware (TLB-401): only newly-failing checks block the ship; pre-existing failures are noted non-blocking (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)).

### `[plan]` (optional section) - Functional

`ReadPlanSection` ([src/ThroughlineBuild.Cli/Config.cs:780-792](../../src/ThroughlineBuild.Cli/Config.cs#L780-L792)). Parsed into `PlanConfig(string Mode)`; missing section returns `PlanConfig.Default` (`promote`) ([src/ThroughlineBuild.Cli/Config.cs:51-55](../../src/ThroughlineBuild.Cli/Config.cs#L51-L55)).

| Key | Default | Notes |
|---|---|---|
| `mode` | `"promote"` | Must be `"investigate"` or `"promote"` (case-insensitive); any other value throws `key 'mode' in [plan] must be either "investigate" or "promote", got "<v>"`, exit 2 ([src/ThroughlineBuild.Cli/Config.cs:786-789](../../src/ThroughlineBuild.Cli/Config.cs#L786-L789)). |

`promote` is now the default (TLB-495): `investigate` spawns a worker to investigate the ticket and write the plan; `promote` bypasses the worker and promotes the ticket plan in place (no LLM/worker). The effective promote decision is `fromBrief || config2.Plan.IsPromote`, so either the `--from-brief` CLI flag or `mode = "promote"` enables it ([src/ThroughlineBuild.Cli/Program.cs:1365](../../src/ThroughlineBuild.Cli/Program.cs#L1365)). The live config sets `mode = "promote"` ([.build/config.toml:139-140](../../.build/config.toml#L139-L140)).

### `[batch]` (optional section) - Functional

`ReadBatchSection` ([src/ThroughlineBuild.Cli/Config.cs:794-811](../../src/ThroughlineBuild.Cli/Config.cs#L794-L811)). Parsed into `BatchConfig(MaxTickets, MaxSizeScore, MaxDescriptionBytes)` ([src/ThroughlineBuild.Cli/Config.cs:63-73](../../src/ThroughlineBuild.Cli/Config.cs#L63-L73)); missing section returns `BatchConfig.Default`. These caps gate the batch-implement path: when any cap is exceeded the conductor falls back to the proven per-ticket chain and logs which cap triggered the fallback. All caps are checked before the batch session starts, using only declared ticket metadata.

| Key | Default | Notes |
|---|---|---|
| `max_tickets` | `8` | Maximum tickets in one batch session. Must be `>= 1` or throws `key 'max_tickets' in [batch] must be a positive integer` ([src/ThroughlineBuild.Cli/Config.cs:803-804](../../src/ThroughlineBuild.Cli/Config.cs#L803-L804)). |
| `max_size_score` | `16` | Maximum aggregate size score (S=1, M=2, L=4). Must be `>= 1` ([src/ThroughlineBuild.Cli/Config.cs:805-806](../../src/ThroughlineBuild.Cli/Config.cs#L805-L806)). |
| `max_description_bytes` | `200000` | Maximum total bytes of ticket description HTML across the batch (a context-size proxy). Must be `>= 1` ([src/ThroughlineBuild.Cli/Config.cs:807-808](../../src/ThroughlineBuild.Cli/Config.cs#L807-L808)). |

Note: there is NO `batch_review_size_threshold` config key - that threshold is a code constant, not config (see Loose ends below).

### `[project]` (optional section, all keys optional) - Functional

`ReadProjectSection` ([src/ThroughlineBuild.Cli/Config.cs:813-868](../../src/ThroughlineBuild.Cli/Config.cs#L813-L868)) - context handed to brief builders so the worker knows the stack it operates in. Missing section returns `ProjectContext.Empty`.

| Key | Default | Notes |
|---|---|---|
| `language`, `framework`, `package_manager`, `build_command`, `test_command`, `install_command`, `dev_command`, `plane_project_url` | `""` | Flowed into brief context dictionaries ([src/ThroughlineBuild.Cli/Config.cs:818-825](../../src/ThroughlineBuild.Cli/Config.cs#L818-L825)). |
| `notes_file` | `""` | Path to a file (relative to the config file dir, or absolute) whose contents are injected into the plan brief. Missing or unreadable emits a stderr warning and proceeds with empty notes ([src/ThroughlineBuild.Cli/Config.cs:832-855](../../src/ThroughlineBuild.Cli/Config.cs#L832-L855)). |
| `workflow_tool` | `"build"` | Must be `"build"` or `"claude-config"` ([src/ThroughlineBuild.Cli/Config.cs:826-830](../../src/ThroughlineBuild.Cli/Config.cs#L826-L830)). Any other value: `ConfigException`, exit 2. |

`plane_project_url` is consumed only as brief context - it is injected into the plan/implement/review/decompose brief dictionaries as `project_plane_project_url` (by `PlanBriefBuilder` and the three peer builders). It is NOT used to build the per-ticket browse URL in CLI summaries; that URL is built from `plane_base_url` + `plane_workspace_slug` via `BuildPlaneUrl` ([src/ThroughlineBuild.Cli/Program.cs:2078-2083](../../src/ThroughlineBuild.Cli/Program.cs#L2078-L2083)).

### Config-editing verbs

Two verbs edit `.build/config.toml` in place (dispatched in the early pre-config-load band, like `build init`, because they manipulate the config rather than consume it). Both are Functional.

- **`build settarget`** ([src/ThroughlineBuild.Cli/SetTargetCommand.cs](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs)) writes `[work].target_branch`, validating that the branch exists locally before writing and preserving config comments via line-edit. The same `DefaultBranchValidator` is supplied to `Load` as the `branchExists` callback for the non-fatal target-branch warning ([src/ThroughlineBuild.Cli/Program.cs:445](../../src/ThroughlineBuild.Cli/Program.cs#L445)).
- **`build models refresh`** ([src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs:24-104](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L24-L104)) re-probes Codex via `codex debug models` (`CodexModelProbe`), maps the discovery onto small/medium/large tiers (`CodexTierMapper`, large tier effort "escalated" to the highest supported level), prints a current-to-proposed diff, and overwrites ONLY the `[workers.codex.sizes]` block (and its discovered-menu comment) in place - every other section is preserved verbatim. This is the way to regenerate the codex sizes block after Codex's model lineup changes. A probe failure leaves the config byte-unchanged with an actionable message; exit 0 on rewrite/no-change, 1 on probe/IO failure, 2 when no config exists.

### Loose ends

- **Template-vs-live `default_agent` drift is RESOLVED.** The `build init` template ([src/ThroughlineBuild.Commands/Templates/config.toml.template:24](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L24)) and the checked-in operator config ([.build/config.toml:25](../../.build/config.toml#L25)) now both ship `default_agent = "claude-code"`. The old hand-edit `.build/config.toml.example` is gone; there is a single template.
- **`backend` value is unchecked.** [src/ThroughlineBuild.Cli/Config.cs:510](../../src/ThroughlineBuild.Cli/Config.cs#L510) reads it but never compares; any value loads as a Plane backend. Only `"plane"` is meaningful.
- **`default_agent`/phase-agent undefined names now hard-error at load** (TLB-512). A name with no `[workers.<name>]` block throws a friendly `ConfigException` at load ([src/ThroughlineBuild.Cli/Config.cs:638-672](../../src/ThroughlineBuild.Cli/Config.cs#L638-L672)) rather than failing lazily at dispatch. A defined-but-unrecognized name (one that has a block but is not gemini/codex/copilot) still falls through to `ClaudeCodeAgent` in `WorkerAgentBuilder` ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:38-44](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L38-L44)).
- **`max_output_tokens` is honored only by Claude Code.** The template comments flag it as accepted-but-unused for the other three agents.
- **`workflow_tool` enum** is validated but unused at runtime - the value is stored on `ProjectContext` and flowed into brief context for the worker to read; nothing in code branches on it.
- **`notes_file`** path resolution is anchored at the config file's directory, not the project root - inconsistent with `events.log_directory` which anchors at project root ([src/ThroughlineBuild.Cli/Config.cs:836-838 vs :172-178](../../src/ThroughlineBuild.Cli/Config.cs#L836-L838)).
- **`batch_review_size_threshold` is NOT config.** The batch caps live in `[batch]`, but the batch-review size threshold is a code constant, not a config key.
- **`CodexOptions.MaxOutputTokens` is accepted but unused** - `max_output_tokens` flows into `AgentConfig` for every agent, but only Claude Code applies it; the template comment says so for codex/gemini/copilot.
- **Disagreement with the architecture doc / older state doc:** prior docs claimed `[llm] default_model` flows to `ClaudeCodeAgent` as `--model`. It does not; workers select their model from `[workers.<name>.sizes]` (now `{model, effort}` tier tables), and `default_model` is deprecated-but-retained, feeding only the reason-translation path ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:8-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L8-L29)).

---

## Environment variables

### Read by the binary

| Variable | Required for | What happens if unset |
|---|---|---|
| `PLANE_API_TOKEN` (or whatever `ticketing.plane_api_token_env` names) | every Plane operation | exit 3 `Secret error: plane_api_token not set in config and required environment variable '<name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:185-187](../../src/ThroughlineBuild.Cli/Config.cs#L185-L187)) |
| `ANTHROPIC_API_KEY` (or whatever `llm.anthropic_api_key_env` names) | `close` / `defer` / `reopen` (reason translation) | resolved as an optional secret at load ([src/ThroughlineBuild.Cli/Config.cs:189-191](../../src/ThroughlineBuild.Cli/Config.cs#L189-L191)); those three verbs no longer hard-fail when it is absent - they fall back to `EchoLlmClient` and record the reason verbatim ([src/ThroughlineBuild.Cli/Program.cs:2163-2172](../../src/ThroughlineBuild.Cli/Program.cs#L2163-L2172)) |
| `BUILD_PROGRESS` | optional - set to `1` to keep the progress digest on even when stderr is redirected | digest auto-suppresses when stderr is redirected and `BUILD_PROGRESS != 1`, to keep CI/script logs clean ([src/ThroughlineBuild.Cli/Program.cs:711, 1161, 1361](../../src/ThroughlineBuild.Cli/Program.cs#L711)) |
| `EDITOR` (via `ReviewLoop.DefaultEditorResolver`) | the interactive `e` (edit) action in `build new ... --review` | falls back to a platform candidate chain (`vim`, `nano`, `code --wait`; on Windows also `notepad.exe`) ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-268](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L268)) |

The hard gate that previously aborted every run when `ANTHROPIC_API_KEY` was missing has been removed (TLB-227). The Anthropic key is now resolved as optional and only required at the point a verb actually constructs the direct LLM client.

### Set / removed by the binary in worker subprocesses

Each agent sanitizes its child environment to force subscription/OAuth auth rather than orchestrator-key auth, then applies any caller-supplied `EnvironmentVariables` last (so an explicit override wins):

| Agent | Action | Source |
|---|---|---|
| Claude Code | removes `ANTHROPIC_API_KEY`; sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from `max_output_tokens` when set | [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:444-448](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L444-L448) (key removed at :444, env set at :448) |
| Codex | removes `CODEX_API_KEY`, `OPENAI_API_KEY` | [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:335-343](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L335-L343) |
| Gemini | removes `GEMINI_API_KEY`, `GOOGLE_API_KEY` (falls back to ADC / gcloud); `max_output_tokens` reserved, no env equivalent applied | [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:282-290](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L282-L290) |
| Copilot | additive only - inherits the `gh` keyring credential; caller may pass `GH_TOKEN` via `EnvironmentVariables` | [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:192-201](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L201) |

Any other env vars from the caller pass through unchanged.

### Used by harness CLAUDE.md (not by `build` itself)

The user's global `CLAUDE.md` configures conventions like `bin/notify` for agent push notifications. Those are conventions for Claude Code sessions working in this repo; the `build` binary neither reads nor writes them.

### Loose ends

- **`GH_TOKEN`** is documented in the Copilot config comments but is never set by `build`; the operator (or a higher-level harness) must place it in the environment before invoking `build` if the `gh` keyring credential is absent ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:192-201](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L201)).
- **Per-provider API keys are stripped, not read.** `build` never reads `OPENAI_API_KEY` / `GEMINI_API_KEY` / `GOOGLE_API_KEY` / `CODEX_API_KEY`; it only removes them from worker child processes. The only provider key `build` itself reads is the Anthropic key, and only for reason translation.

---

## Secrets

Two secrets, both required-by-context (`ResolveSecrets`, [src/ThroughlineBuild.Cli/Config.cs:180-194](../../src/ThroughlineBuild.Cli/Config.cs#L180-L194)):

1. **Plane API token.** Always required (every verb hits Plane). Resolution: inline `plane_api_token`, else the env var named by `plane_api_token_env` (default `PLANE_API_TOKEN`). Missing: exit 3 at load ([src/ThroughlineBuild.Cli/Config.cs:185-187](../../src/ThroughlineBuild.Cli/Config.cs#L185-L187)).
2. **Anthropic API key.** Required only for `close` / `defer` / `reopen` reason translation. Resolution: inline `anthropic_api_key`, else the env var named by `anthropic_api_key_env`. Resolved as optional (`null` allowed) at load ([src/ThroughlineBuild.Cli/Config.cs:189-193](../../src/ThroughlineBuild.Cli/Config.cs#L189-L193)); even the three reason-translation verbs no longer hard-fail if it is absent (see below). Worker phases reach their provider via the worker CLI's own auth, independent of `ANTHROPIC_API_KEY`.

#### Reason translation is the only LLM consumer

Reason translation is the only path in the deterministic CLI that constructs the direct Anthropic client, and it is now fully optional. `WireUpConditionalCommands` only runs for `close`/`defer`/`reopen` ([src/ThroughlineBuild.Cli/Program.cs:2145-2173](../../src/ThroughlineBuild.Cli/Program.cs#L2145-L2173)); it tries `LlmClientFactory.Create`, and on `ConfigException` (no key, deprecated `default_model` unset, etc.) it logs `WARNING: LLM unavailable (...); recording reason verbatim without translation.` and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the last user message verbatim. The ticket state transition still runs. `ReasonTranslator` uses model `claude-haiku-4-5-20251001` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)). The old module-level `ANTHROPIC_API_KEY` hard gate is gone (TLB-227/TLB-371).

`.build/config.toml` is gitignored ([.gitignore:14](../../.gitignore#L14)) along with `secrets/` ([.gitignore:2](../../.gitignore#L2)). The `secrets/` directory is reserved and not read by any code path. The `build init` template uses the `REQUIRED_PLANE_API_TOKEN` placeholder and supports `--token-env` to write the env-var indirection line instead (see [02-install-build-run.md](02-install-build-run.md)).

### Loose ends

- **Secrets in `.build/config.toml`** are stored plaintext on disk. The template encourages inline by default; env-var indirection is supported but optional.
- **No config validation step** beyond TOML parse + section presence. There is no `build config check` verb to confirm the token is valid against Plane.

---

## Configuration sources outside `.build/`

### `.claude/plane-config.md` and `.claude/ticket-config.md`

Read by the Claude Code `/ticket-*` slash commands, not by `build`. They duplicate some of the same data (workspace slug, project UUID, state UUIDs, label UUIDs, test/build commands) in a markdown format the harness parses. These files exist because the older claude-config workflow runs in the same repo and operators still invoke `/ticket-*` from chat.

### `AGENTS.md`

A Codex-agent instruction file written to the workspace by the slash-command installer ([AGENTS.md](../../AGENTS.md)); read by the Codex agent harness, not by `build`.

### `.gitattributes`

LF-pinning of brief templates and snapshot test data ([.gitattributes:1-3](../../.gitattributes#L1-L3)). Influences how diffs and substitutions look but not runtime config behavior.

### `throughline-build.sln`

Solution membership only. Adding a new `ThroughlineBuild.X` project requires editing this file and the corresponding `.csproj` references.

### Loose ends

- The two `.claude/*.md` files can drift from `.build/config.toml`; there is no enforced single-source-of-truth between the build CLI config and the slash-command config.

---

## Configuration precedence

For secrets:

1. Inline value in `.build/config.toml` (e.g. `plane_api_token = "..."`, `anthropic_api_key = "..."`).
2. Environment variable named by the matching `*_env` key.
3. (Plane only) absent -> exit 3; (Anthropic) absent -> `null`, deferred until a verb needs it.

For agent / model selection per phase:

1. Per-phase CLI flag (`--agent-plan` / `--agent-implement` / `--agent-review`).
2. `--agent` (applies to all phases).
3. `[workers.phases]` mapping for that phase.
4. `[workers] default_agent`.

([src/ThroughlineBuild.Cli/Program.cs:1095-1099](../../src/ThroughlineBuild.Cli/Program.cs#L1095-L1099)). The model id within an agent is then chosen by ticket size from that agent's `[workers.<name>.sizes]` map (now `{model, effort}` tier tables) - there is no model-level CLI override.

For the `ship` push / plan-mode toggles, the CLI flag and the config key OR together: `--no-push || !Ship.Push` disables the push, and `--from-brief || Plan.IsPromote` selects promote mode.

For optional sections (`[llm]`, `[review]`, `[ship]`, `[work]`, `[plan]`, `[project]`, `[batch]`): a missing section is equivalent to an all-defaults section ([src/ThroughlineBuild.Cli/Config.cs:520-528, 686-692, 724-732, 770-778, 780-792, 794-811, 813-868](../../src/ThroughlineBuild.Cli/Config.cs#L520-L528)).

---

## Loose ends

- **Template-vs-live `default_agent` drift is RESOLVED** (see the `.build/config.toml` section above): both the `build init` template and the checked-in operator config default to `claude-code`. The old `.build/config.toml.example` is gone.
- **`backend`** is read but not strictly validated; only `"plane"` is meaningful. **`default_agent`** and phase-agent names are now validated at load time (TLB-512) - an undefined name hard-errors with a friendly `Config error:`; a defined-but-unrecognized name (has a block but is not gemini/codex/copilot) still falls back to Claude Code in `WorkerAgentBuilder`.
- **`max_output_tokens`** is honored only by the Claude Code agent (`CodexOptions.MaxOutputTokens` etc. are accepted but unused).
- **`workflow_tool`** is validated but never branched on.
- **`[llm] default_model`** is reason-translation-only and deprecated-but-retained; it does not configure worker models (those come from `[workers.<name>.sizes]` `{model, effort}` tier tables). This corrects the older claim that it feeds `ClaudeCodeAgent --model`.
- **`batch_review_size_threshold`** is a code constant, not a `[batch]` config key.
- **Plaintext secrets** in the config file are the documented default in the template.
- **Non-fatal validation only.** Beyond TOML parse plus required-section/required-key presence and the hard-break migration errors, the loader's extra validation is advisory: unknown keys (TLB-405) and a missing `[work].target_branch` (TLB-410) emit `warning:` lines but do not fail the run. There is no `build config check` verb to confirm the token is valid against Plane.
- **No per-environment overlay** (no `config.local.toml`). Operators with multiple Plane workspaces hand-edit the file or use `build init --force` to regenerate.
