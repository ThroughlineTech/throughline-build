# 04 - Configuration and Environment

Every config file the binary reads, every environment variable it consults, every secret it requires, and whether each is required or optional.

For installation-time concerns (including the `build init` bootstrap) see [02-install-build-run.md](02-install-build-run.md). For state files written at runtime see [05-state-and-persistence.md](05-state-and-persistence.md).

---

## `.build/config.toml`

The single source of operator-controlled configuration. Discovered by walking up from cwd looking for `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:85-96](../../src/ThroughlineBuild.Cli/Config.cs#L85-L96)). Missing file: exit 2 with `Config error: config file not found: searched from <cwd> upwards for .build/config.toml` ([src/ThroughlineBuild.Cli/Program.cs:183](../../src/ThroughlineBuild.Cli/Program.cs#L183)).

Parsed by `Tomlyn` into the typed records `TicketingConfig`, `LlmConfig`, `WorkersConfig` (containing `AgentConfig` per agent), `EventsConfig`, `ReviewConfig`, `ShipConfig`, `WorkConfig`, `PlanConfig`, and `ProjectContext` ([src/ThroughlineBuild.Cli/Config.cs:9-66](../../src/ThroughlineBuild.Cli/Config.cs#L9-L66)). The nine section readers run in `Load` ([src/ThroughlineBuild.Cli/Config.cs:123-131](../../src/ThroughlineBuild.Cli/Config.cs#L123-L131)).

Two templates exist and the project comment requires them to be kept in lockstep:
- [.build/config.toml.example](../../.build/config.toml.example) - the hand-edit reference, with codex/gemini/copilot agent blocks present but commented out. `default_agent = "claude-code"`, `default_model` active.
- [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template) - the embedded resource emitted by `build init`, using `REQUIRED_*` placeholders for the four Plane fields. Also `default_agent = "claude-code"`.

The live checked-in operator config [.build/config.toml](../../.build/config.toml) has diverged from both templates: it sets `default_agent = "codex"` ([.build/config.toml:25](../../.build/config.toml#L25)) with an uncommented `[workers.codex]` block ([.build/config.toml:47-56](../../.build/config.toml#L47-L56)), and comments out `default_model` entirely. So the product/`build init` default agent is `claude-code`; the operator's checked-in default is `codex`. There is no hardcoded vendor default in C# - `default_agent` is a required string ([src/ThroughlineBuild.Cli/Config.cs:478](../../src/ThroughlineBuild.Cli/Config.cs#L478)) and the factory keys off whatever name is configured (see [`[workers.phases]`](#workersphases-optional-sub-table---functional)). This drift is tracked in [Loose ends](#loose-ends) below.

### Unknown-key warnings (TLB-405)

After the typed sections load, `BuildConfigLoader.Load` runs a non-fatal validation pass that emits one `warning: unknown config key <path> - ignored` per unrecognized key to `stderr` (or a supplied `warnSink`) - the run still proceeds. Driver: [src/ThroughlineBuild.Cli/Config.cs:133-147](../../src/ThroughlineBuild.Cli/Config.cs#L133-L147); implementation `CollectUnknownKeyWarnings` [src/ThroughlineBuild.Cli/Config.cs:244-393](../../src/ThroughlineBuild.Cli/Config.cs#L244-L393). The allowlists are static `HashSet<string>` per scope: top-level sections [src/ThroughlineBuild.Cli/Config.cs:176-179](../../src/ThroughlineBuild.Cli/Config.cs#L176-L179) and per-section key sets [src/ThroughlineBuild.Cli/Config.cs:181-242](../../src/ThroughlineBuild.Cli/Config.cs#L181-L242). Entries inside `[[review.checks]]` and `[[ship.regression_checks]]` are validated against `KnownCheckEntryKeys` (`name`, `executable`, `arguments`, `timeout_minutes`) ([src/ThroughlineBuild.Cli/Config.cs:218-221](../../src/ThroughlineBuild.Cli/Config.cs#L218-L221)). `[workers.phases]` is skipped by this pass because it already hard-errors on unknown keys (see below). The warning pass is wired into the load path in `Program.cs`.

### Required-field handling (TLB-369)

Required scalars go through `RequireString`, which throws `ConfigException` (`missing required key '<k>' in [<section>]` or `key '<k>' in [<section>] must be a non-empty string`); required sections go through `RequireSection` (`missing required TOML section [<section>]`) ([src/ThroughlineBuild.Cli/Config.cs:395-409](../../src/ThroughlineBuild.Cli/Config.cs#L395-L409)). Both surface as `Config error: ...`, exit 2. The example and `build init` template annotate every required field with an inline `REQUIRED` comment.

### `[ticketing]` (required) - Functional

`ReadTicketingSection` ([src/ThroughlineBuild.Cli/Config.cs:440-452](../../src/ThroughlineBuild.Cli/Config.cs#L440-L452)).

| Key | Required | Default | Source |
|---|---|---|---|
| `backend` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:444](../../src/ThroughlineBuild.Cli/Config.cs#L444) - value is read but never compared; only `"plane"` is wired (no other adapter exists). |
| `plane_base_url` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:445](../../src/ThroughlineBuild.Cli/Config.cs#L445) |
| `plane_workspace_slug` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:446](../../src/ThroughlineBuild.Cli/Config.cs#L446) |
| `plane_project_id` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:447](../../src/ThroughlineBuild.Cli/Config.cs#L447) - UUID of the Plane project. |
| `plane_api_token_env` | no | `PLANE_API_TOKEN` | [src/ThroughlineBuild.Cli/Config.cs:448](../../src/ThroughlineBuild.Cli/Config.cs#L448) - name of the env var holding the token when not inline. |
| `plane_project_identifier` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:449](../../src/ThroughlineBuild.Cli/Config.cs#L449) - e.g. `"TLB"`. Used as a filename component and in Plane client options. |
| `plane_project_name` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:450](../../src/ThroughlineBuild.Cli/Config.cs#L450) - e.g. `"throughline-build"`. Filename component / `SessionContext.ProjectName`. |
| `plane_api_token` | no | `null` | [src/ThroughlineBuild.Cli/Config.cs:451](../../src/ThroughlineBuild.Cli/Config.cs#L451) - inline token; takes precedence over env. |

A missing or empty required key throws `ConfigException` via `RequireString`/`RequireSection` (`missing required key '<k>' in [ticketing]` or `must be a non-empty string`); CLI exits 2 with `Config error: ...` ([src/ThroughlineBuild.Cli/Config.cs:395-409](../../src/ThroughlineBuild.Cli/Config.cs#L395-L409)).

### `[llm]` (optional section, optional keys) - Functional

If the section is absent, all values default to empty strings ([src/ThroughlineBuild.Cli/Config.cs:454-462](../../src/ThroughlineBuild.Cli/Config.cs#L454-L462)). The whole section is optional.

| Key | Default | Use |
|---|---|---|
| `default_model` | `""` | **DEPRECATED for worker-model selection.** Vendor-prefixed model id for the direct Anthropic client used by reason translation. Consumed ONLY by `LlmClientFactory.Create` for `close`/`defer`/`reopen`; not passed to worker agents - workers get their model from `[workers.<agent>.sizes]`. Commented out in the live config ([.build/config.toml:16-22](../../.build/config.toml#L16-L22)). ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)) |
| `anthropic_api_key_env` | `""` | Name of the env var holding the Anthropic key. |
| `anthropic_api_key` | `null` | Inline key; takes precedence over env ([src/ThroughlineBuild.Cli/Config.cs:461](../../src/ThroughlineBuild.Cli/Config.cs#L461)). |

`LlmClientFactory` requires `default_model` to be non-empty and to start with `anthropic:`; any other prefix throws `unsupported LLM vendor prefix '<p>' in [llm] default_model; only 'anthropic:' is supported`, and an empty value throws `LLM client required but [llm] default_model is not set in config.toml` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)). These errors only surface on `close`/`defer`/`reopen`, and even then are non-fatal: if no client can be built the verb falls back to an `EchoLlmClient` that records the reason verbatim (see [Reason translation](#reason-translation-is-the-only-llm-consumer)). Other verbs never construct the direct LLM client at all.

### `[workers]` (required section) - Functional

`ReadWorkersSection` ([src/ThroughlineBuild.Cli/Config.cs:464-550](../../src/ThroughlineBuild.Cli/Config.cs#L464-L550)).

| Key | Required | Default |
|---|---|---|
| `default_agent` | yes | - ([src/ThroughlineBuild.Cli/Config.cs:478](../../src/ThroughlineBuild.Cli/Config.cs#L478)) |
| `timeout_minutes` | no | `30` ([src/ThroughlineBuild.Cli/Config.cs:479](../../src/ThroughlineBuild.Cli/Config.cs#L479)) |
| `max_concurrency` | no | `min(ProcessorCount, 4)` ([src/ThroughlineBuild.Cli/Config.cs:542](../../src/ThroughlineBuild.Cli/Config.cs#L542)) - retained config key; the dispatcher is pinned to serial execution regardless (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). |

`default_agent` is the agent name used for any phase not overridden in `[workers.phases]` or by a CLI flag. There is no hardcoded vendor default - the live config sets `codex`, the templates set `claude-code`. The named sub-table must exist or the CLI throws `missing [workers.<name>] sub-table in config` at dispatch ([src/ThroughlineBuild.Cli/Program.cs:752-753](../../src/ThroughlineBuild.Cli/Program.cs#L752-L753)).

Migration guard (hard-break): the old flat keys `claude_code_executable` and `max_output_tokens` directly under `[workers]` now throw a hard `ConfigException` directing the operator to move them into a `[workers.<name>]` sub-table ([src/ThroughlineBuild.Cli/Config.cs:469-476](../../src/ThroughlineBuild.Cli/Config.cs#L469-L476)).

Every sub-table under `[workers]` other than `phases` is parsed as an agent config ([src/ThroughlineBuild.Cli/Config.cs:484-540](../../src/ThroughlineBuild.Cli/Config.cs#L484-L540)).

### `[workers.<agent-name>]` (one block per agent) - Functional

Parsed into `AgentConfig(Executable, MaxOutputTokens, Sizes, BypassPermissions)` ([src/ThroughlineBuild.Cli/Config.cs:24](../../src/ThroughlineBuild.Cli/Config.cs#L24), populated at [src/ThroughlineBuild.Cli/Config.cs:506-539](../../src/ThroughlineBuild.Cli/Config.cs#L506-L539)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `executable` | yes | - | Path or bare command for the worker CLI ([src/ThroughlineBuild.Cli/Config.cs:506](../../src/ThroughlineBuild.Cli/Config.cs#L506)). |
| `max_output_tokens` | no | `null` | Only `ClaudeCodeAgent` uses it (sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS`); Codex/Gemini/Copilot accept the key but do not apply it ([src/ThroughlineBuild.Cli/Config.cs:507-512](../../src/ThroughlineBuild.Cli/Config.cs#L507-L512)). |
| `bypass_permissions` | no | `true` | Per-agent unattended-mode toggle (TLB-229). `true` emits the agent's skip-permissions flag; `false` opts back into the interactive gate ([src/ThroughlineBuild.Cli/Config.cs:518-520](../../src/ThroughlineBuild.Cli/Config.cs#L518-L520)). |
| `[workers.<name>.sizes]` | yes | - | Required sub-table mapping `small`/`medium`/`large` to model ids (TLB-196/197/198). A missing sub-table throws ([src/ThroughlineBuild.Cli/Config.cs:523-538](../../src/ThroughlineBuild.Cli/Config.cs#L523-L538)). |

`bypass_permissions` is wired into each agent's options at factory construction ([src/ThroughlineBuild.Cli/Program.cs:782,790,804](../../src/ThroughlineBuild.Cli/Program.cs#L782)). It translates to a different flag per agent: `--dangerously-skip-permissions` for Claude Code ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:376-377](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L376-L377)), `--dangerously-bypass-approvals-and-sandbox` for Codex ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:183-184](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L183-L184)), `--yolo` for Gemini ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:227-228](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L227-L228)). `CopilotOptions` has no `BypassPermissions` field; the factory does not pass it ([src/ThroughlineBuild.Cli/Program.cs:792-798](../../src/ThroughlineBuild.Cli/Program.cs#L792-L798)) and Copilot always runs `-s --no-ask-user` ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:22-24](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L24)) - an asymmetry vs the other three agents.

#### `[workers.<name>.sizes]` (required per agent) - Functional

| Key | Required |
|---|---|
| `small` | yes |
| `medium` | yes |
| `large` | yes |

All three keys must be present and non-empty or the loader throws `[workers.<name>.sizes] is missing required size keys: ...`; a missing sub-table throws `missing required [workers.<name>.sizes] sub-table in config` ([src/ThroughlineBuild.Cli/Config.cs:522-538](../../src/ThroughlineBuild.Cli/Config.cs#L522-L538)). Each value is the model id the agent passes to its `--model` flag for that size tier. The tier is selected from the ticket's size: `S -> Small`, `L -> Large`, anything else `-> Medium` ([src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs:7-12](../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)), via the `WorkerSize` enum ([src/ThroughlineBuild.Contracts/Models/WorkerSize.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs)). Each agent strips its own vendor prefix before passing the id to the CLI: `anthropic:` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:398-400](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L398-L400)), `openai:` ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:203-205](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L203-L205)), `google:` ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:247-249](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L247-L249)), `github:` ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:172-174](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L172-L174)). There is no per-phase `--model` flag; per-phase model selection is achieved indirectly by pointing a phase at a different agent (see `[workers.phases]`). This is what replaced the deprecated `[llm] default_model` worker-model path.

#### `[workers.phases]` (optional sub-table) - Functional

Maps phase names to agent names (TLB-189/190/191). Allowed keys: `plan`, `implement`, `review`, `decompose`; any other key throws `unknown phase key '<k>' in [workers.phases]` and an empty value throws `value for '<k>' in [workers.phases] must be a non-empty string` ([src/ThroughlineBuild.Cli/Config.cs:489-503](../../src/ThroughlineBuild.Cli/Config.cs#L489-L503)). This sub-table is skipped by the unknown-key warning pass because it already hard-errors on its own.

Resolution per phase: `AgentFor(phase)` returns the `[workers.phases]` mapping if present, else `default_agent` ([src/ThroughlineBuild.Cli/Program.cs:811-812](../../src/ThroughlineBuild.Cli/Program.cs#L811-L812)). `EffectiveAgentFor(phase)` then layers CLI flags on top: a per-phase flag (`--agent-plan` / `--agent-implement` / `--agent-review`) wins over `--agent` (all phases), which wins over config (TLB-191 cli-flag-override) ([src/ThroughlineBuild.Cli/Program.cs:816-820](../../src/ThroughlineBuild.Cli/Program.cs#L816-L820)). The agent flags are extracted before dispatch ([src/ThroughlineBuild.Cli/Program.cs:73-74](../../src/ThroughlineBuild.Cli/Program.cs#L73-L74), [src/ThroughlineBuild.Cli/CliArgParser.cs:25-52](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L25-L52)).

The orchestrator constructs one agent per name referenced by `default_agent` or any phase mapping (plus any agent named on a CLI flag), selecting the implementation by name: `gemini`, `codex`, `copilot`, else `ClaudeCodeAgent` as fallback ([src/ThroughlineBuild.Cli/Program.cs:767-808](../../src/ThroughlineBuild.Cli/Program.cs#L767-L808)). `WorkerAgentFactory.Create` throws a `ConfigException` listing the known agent names if an unknown name is requested. See [02-install-build-run.md](02-install-build-run.md) "Worker CLIs" for the per-agent table.

### `[work]` (optional section) - Functional

`ReadWorkSection` ([src/ThroughlineBuild.Cli/Config.cs:644-652](../../src/ThroughlineBuild.Cli/Config.cs#L644-L652)). Parsed into `WorkConfig(string? TargetBranch)` ([src/ThroughlineBuild.Cli/Config.cs:47](../../src/ThroughlineBuild.Cli/Config.cs#L47)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `target_branch` | no | `null` | The branch `ship` merges into and pushes, overriding `[ship].base_branch`. An empty string is rejected (treated as unset) at read time ([src/ThroughlineBuild.Cli/Config.cs:649](../../src/ThroughlineBuild.Cli/Config.cs#L649)). |

A hand-edited `target_branch` bypasses the `build settarget` branch check, so `Load` runs a non-fatal existence validation when a `branchExists` validator is supplied: if the configured branch does not resolve to a local ref, it emits `warning: [work].target_branch '<b>' does not resolve to a local branch - ship will block until it exists or you run 'build settarget'` through the same warning channel as the unknown-key pass ([src/ThroughlineBuild.Cli/Config.cs:142-143](../../src/ThroughlineBuild.Cli/Config.cs#L142-L143)). The check is skipped when no validator is passed (unit tests, or commands that do not touch git).

`BuildConfig.ResolveTargetBranch()` returns `Work.TargetBranch ?? Ship.BaseBranch` ([src/ThroughlineBuild.Cli/Config.cs:68](../../src/ThroughlineBuild.Cli/Config.cs#L68)); `TargetBranchOverridden` is true when `Work.TargetBranch is not null` ([src/ThroughlineBuild.Cli/Config.cs:73](../../src/ThroughlineBuild.Cli/Config.cs#L73)). Both flow into `ShipOptions.TargetBranch`/`TargetBranchOverridden` ([src/ThroughlineBuild.Cli/Program.cs:1281-1283](../../src/ThroughlineBuild.Cli/Program.cs#L1281-L1283)) and `BuildOptions.TargetBranch`, consumed by `ShipPhase` and the target-aware `BaseRefResolver` (see [01-inventory.md](01-inventory.md) ship verb, [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). The intended editing path is the `build settarget` verb, which validates the branch exists locally before writing the key and preserves config comments via line-edit; hand-editing the TOML works too. When `target_branch != base_branch`, ship enforces that the main worktree is checked out on the target branch before merging (pre-merge guard `wrong_worktree_branch`).

### `[events]` (required section) - Functional

The section itself is required (`RequireSection`).

| Key | Required |
|---|---|
| `log_directory` | yes ([src/ThroughlineBuild.Cli/Config.cs:552-557](../../src/ThroughlineBuild.Cli/Config.cs#L552-L557)) |

Resolved by `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:152-158](../../src/ThroughlineBuild.Cli/Config.cs#L152-L158)). A relative value is resolved against the project root (parent of `.build/`), not the config file's directory. Typical value: `.build/events`.

### `[review]` (optional section, sensible defaults) - Functional

`ReadReviewSection` ([src/ThroughlineBuild.Cli/Config.cs:562-596](../../src/ThroughlineBuild.Cli/Config.cs#L562-L596)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:564-570](../../src/ThroughlineBuild.Cli/Config.cs#L564-L570)).

| Key | Default |
|---|---|
| `verifier_timeout_minutes` | `15` ([src/ThroughlineBuild.Cli/Config.cs:572](../../src/ThroughlineBuild.Cli/Config.cs#L572)) |
| `verifier_allowed_tools` | `["Read", "Grep", "Glob"]` ([src/ThroughlineBuild.Cli/Config.cs:559-560](../../src/ThroughlineBuild.Cli/Config.cs#L559-L560)) |
| `[[review.checks]]` (array-of-tables) | empty list |

Each `[[review.checks]]` entry maps to a `CheckSpec(name, executable, arguments, timeout)` consumed during the review phase. `name` and `executable` are required; `arguments` defaults to empty and `timeout_minutes` defaults to `5` ([src/ThroughlineBuild.Cli/Config.cs:576-589](../../src/ThroughlineBuild.Cli/Config.cs#L576-L589)). Entry keys are also validated against `KnownCheckEntryKeys` by the unknown-key warning pass.

```
[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test"]
timeout_minutes = 5
```

### `[ship]` (optional section) - Functional

`ReadShipSection` ([src/ThroughlineBuild.Cli/Config.cs:598-642](../../src/ThroughlineBuild.Cli/Config.cs#L598-L642)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:600-608](../../src/ThroughlineBuild.Cli/Config.cs#L600-L608)).

| Key | Default |
|---|---|
| `remote` | `"origin"` ([src/ThroughlineBuild.Cli/Config.cs:610](../../src/ThroughlineBuild.Cli/Config.cs#L610)) |
| `base_branch` | `"main"` ([src/ThroughlineBuild.Cli/Config.cs:611](../../src/ThroughlineBuild.Cli/Config.cs#L611)) |
| `delete_feature_branch` | `true` ([src/ThroughlineBuild.Cli/Config.cs:612-614](../../src/ThroughlineBuild.Cli/Config.cs#L612-L614)) |
| `push` | `true` ([src/ThroughlineBuild.Cli/Config.cs:615-617](../../src/ThroughlineBuild.Cli/Config.cs#L615-L617)) |
| `[[ship.regression_checks]]` | empty list |

`push` (TLB-410) gates whether ship touches the remote after the local fast-forward merge. The effective no-push decision is `NoPush = noPush || !config.Ship.Push`, so either the `--no-push` CLI flag or `push = false` in config disables the remote push; ship then rebases onto the local target and emits a `fetch_skipped` reason ([src/ThroughlineBuild.Cli/Program.cs:1282](../../src/ThroughlineBuild.Cli/Program.cs#L1282)). When `push = true` and the target branch does not yet exist on the remote, ship rebases onto the local target and lets the push create it (TLB-409).

Same `CheckSpec` shape and default rules as `review.checks` ([src/ThroughlineBuild.Cli/Config.cs:619-634](../../src/ThroughlineBuild.Cli/Config.cs#L619-L634)). The two lists are independent so each phase evolves separately. Regression checks are baseline-aware (TLB-401): only newly-failing checks block the ship; pre-existing failures are noted non-blocking (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)).

### `[plan]` (optional section) - Functional

`ReadPlanSection` ([src/ThroughlineBuild.Cli/Config.cs:654-666](../../src/ThroughlineBuild.Cli/Config.cs#L654-L666)). Parsed into `PlanConfig(string Mode)`; missing section returns `PlanConfig.Default` (`promote`) ([src/ThroughlineBuild.Cli/Config.cs:51-55](../../src/ThroughlineBuild.Cli/Config.cs#L51-L55)).

| Key | Default | Notes |
|---|---|---|
| `mode` | `"promote"` | Must be `"investigate"` or `"promote"` (case-insensitive); any other value throws `key 'mode' in [plan] must be either "investigate" or "promote", got "<v>"`, exit 2 ([src/ThroughlineBuild.Cli/Config.cs:659-663](../../src/ThroughlineBuild.Cli/Config.cs#L659-L663)). |

`investigate` spawns a worker to investigate the ticket and write the plan; `promote` bypasses the worker and promotes the ticket plan in place (no LLM/worker). The effective promote decision is `fromBrief || config.Plan.IsPromote`, so either the `--from-brief` CLI flag or `mode = "promote"` enables it ([src/ThroughlineBuild.Cli/Program.cs:1081](../../src/ThroughlineBuild.Cli/Program.cs#L1081)). The live config sets `mode = "promote"` ([.build/config.toml:132-133](../../.build/config.toml#L132-L133)).

### `[project]` (optional section, all keys optional) - Functional

`ReadProjectSection` ([src/ThroughlineBuild.Cli/Config.cs:668-723](../../src/ThroughlineBuild.Cli/Config.cs#L668-L723)) - context handed to brief builders so the worker knows the stack it operates in. Missing section returns `ProjectContext.Empty`.

| Key | Default | Notes |
|---|---|---|
| `language`, `framework`, `package_manager`, `build_command`, `test_command`, `install_command`, `dev_command`, `plane_project_url` | `""` | Flowed into brief context dictionaries ([src/ThroughlineBuild.Cli/Config.cs:673-680](../../src/ThroughlineBuild.Cli/Config.cs#L673-L680)). |
| `notes_file` | `""` | Path to a file (relative to the config file dir, or absolute) whose contents are injected into the plan brief. Missing or unreadable emits a stderr warning and proceeds with empty notes ([src/ThroughlineBuild.Cli/Config.cs:687-710](../../src/ThroughlineBuild.Cli/Config.cs#L687-L710)). |
| `workflow_tool` | `"build"` | Must be `"build"` or `"claude-config"` ([src/ThroughlineBuild.Cli/Config.cs:681-685](../../src/ThroughlineBuild.Cli/Config.cs#L681-L685)). Any other value: `ConfigException`, exit 2. |

`plane_project_url` is consumed only as brief context - it is injected into the plan/implement/review/decompose brief dictionaries as `project_plane_project_url` ([src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs:51](../../src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs#L51) and the three peer builders). It is NOT used to build the per-ticket browse URL in CLI summaries; that URL is built from `plane_base_url` + `plane_workspace_slug` via `BuildPlaneUrl` ([src/ThroughlineBuild.Cli/Program.cs:1694](../../src/ThroughlineBuild.Cli/Program.cs#L1694)).

### Loose ends

- **Template-vs-live `default_agent` drift.** The `build init` template ([src/ThroughlineBuild.Commands/Templates/config.toml.template:23](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L23)) and the hand-edit example ([.build/config.toml.example:25](../../.build/config.toml.example#L25)) both ship `default_agent = "claude-code"`, but the checked-in operator config ([.build/config.toml:25](../../.build/config.toml#L25)) sets `codex`. A fresh `build init` therefore produces a claude-code-default config that does not match what this repo actually runs. There is no enforced lockstep between the live config and the templates (only the example<->template lockstep is commented).
- **`backend` value is unchecked.** [src/ThroughlineBuild.Cli/Config.cs:444](../../src/ThroughlineBuild.Cli/Config.cs#L444) reads it but never compares; any value loads as a Plane backend. Only `"plane"` is meaningful.
- **`default_agent` value is unchecked at parse time.** It is validated lazily: dispatch throws `missing [workers.<name>] sub-table` if no matching block exists ([src/ThroughlineBuild.Cli/Program.cs:752-753](../../src/ThroughlineBuild.Cli/Program.cs#L752-L753)), and unknown agent names that do have a block fall through to `ClaudeCodeAgent` ([src/ThroughlineBuild.Cli/Program.cs:799-805](../../src/ThroughlineBuild.Cli/Program.cs#L799-L805)).
- **`max_output_tokens` is honored only by Claude Code.** The example/template comments flag it as accepted-but-unused for the other three agents.
- **`workflow_tool` enum** is validated but unused at runtime - the value is stored on `ProjectContext` and flowed into brief context for the worker to read; nothing in code branches on it.
- **`notes_file`** path resolution is anchored at the config file's directory, not the project root - inconsistent with `events.log_directory` which anchors at project root ([src/ThroughlineBuild.Cli/Config.cs:691-693 vs :152-158](../../src/ThroughlineBuild.Cli/Config.cs#L691-L693)).
- **Disagreement with the architecture doc / older state doc:** prior docs claimed `[llm] default_model` flows to `ClaudeCodeAgent` as `--model`. It does not; workers select their model from `[workers.<name>.sizes]`, and `default_model` is now deprecated even for the reason-translation path it still feeds ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)).

---

## Environment variables

### Read by the binary

| Variable | Required for | What happens if unset |
|---|---|---|
| `PLANE_API_TOKEN` (or whatever `ticketing.plane_api_token_env` names) | every Plane operation | exit 3 `Secret error: plane_api_token not set in config and required environment variable '<name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:162-167](../../src/ThroughlineBuild.Cli/Config.cs#L162-L167)) |
| `ANTHROPIC_API_KEY` (or whatever `llm.anthropic_api_key_env` names) | `close` / `defer` / `reopen` (reason translation) | resolved as an optional secret at load ([src/ThroughlineBuild.Cli/Config.cs:169-173](../../src/ThroughlineBuild.Cli/Config.cs#L169-L173)); those three verbs no longer hard-fail when it is absent - they fall back to `EchoLlmClient` and record the reason verbatim ([src/ThroughlineBuild.Cli/Program.cs:1730-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1730-L1737)) |
| `BUILD_PROGRESS` | optional - set to `1` to keep the progress digest on even when stderr is redirected | digest auto-suppresses when stderr is redirected and `BUILD_PROGRESS != 1`, to keep CI/script logs clean ([src/ThroughlineBuild.Cli/Program.cs:423, 881, 1077](../../src/ThroughlineBuild.Cli/Program.cs#L423)) |
| `EDITOR` (via `ReviewLoop.DefaultEditorResolver`) | the interactive `e` (edit) action in `build new ... --review` | falls back to a platform candidate chain (`vim`, `nano`, `code --wait`; on Windows also `notepad.exe`) ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-268](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L268)) |

The hard gate that previously aborted every run when `ANTHROPIC_API_KEY` was missing has been removed (TLB-227). The Anthropic key is now resolved as optional and only required at the point a verb actually constructs the direct LLM client.

### Set / removed by the binary in worker subprocesses

Each agent sanitizes its child environment to force subscription/OAuth auth rather than orchestrator-key auth, then applies any caller-supplied `EnvironmentVariables` last (so an explicit override wins):

| Agent | Action | Source |
|---|---|---|
| Claude Code | removes `ANTHROPIC_API_KEY`; sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from `max_output_tokens` when set | [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:404-415](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L404-L415) (env set at :412) |
| Codex | removes `CODEX_API_KEY`, `OPENAI_API_KEY` | [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:163-171](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L163-L171) |
| Gemini | removes `GEMINI_API_KEY`, `GOOGLE_API_KEY` (falls back to ADC / gcloud); `max_output_tokens` reserved, no env equivalent applied | [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:253-271](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L253-L271) |
| Copilot | additive only - inherits the `gh` keyring credential; caller may pass `GH_TOKEN` via `EnvironmentVariables` | [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:178-188](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L178-L188) |

Any other env vars from the caller pass through unchanged.

### Used by harness CLAUDE.md (not by `build` itself)

The user's global `CLAUDE.md` configures conventions like `bin/notify` for agent push notifications. Those are conventions for Claude Code sessions working in this repo; the `build` binary neither reads nor writes them.

### Loose ends

- **`GH_TOKEN`** is documented in the Copilot config comments but is never set by `build`; the operator (or a higher-level harness) must place it in the environment before invoking `build` if the `gh` keyring credential is absent ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:178-182](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L178-L182)).
- **Per-provider API keys are stripped, not read.** `build` never reads `OPENAI_API_KEY` / `GEMINI_API_KEY` / `GOOGLE_API_KEY` / `CODEX_API_KEY`; it only removes them from worker child processes. The only provider key `build` itself reads is the Anthropic key, and only for reason translation.

---

## Secrets

Two secrets, both required-by-context (`ResolveSecrets`, [src/ThroughlineBuild.Cli/Config.cs:160-174](../../src/ThroughlineBuild.Cli/Config.cs#L160-L174)):

1. **Plane API token.** Always required (every verb hits Plane). Resolution: inline `plane_api_token`, else the env var named by `plane_api_token_env` (default `PLANE_API_TOKEN`). Missing: exit 3 at load ([src/ThroughlineBuild.Cli/Config.cs:165-167](../../src/ThroughlineBuild.Cli/Config.cs#L165-L167)).
2. **Anthropic API key.** Required only for `close` / `defer` / `reopen` reason translation. Resolution: inline `anthropic_api_key`, else the env var named by `anthropic_api_key_env`. Resolved as optional (`null` allowed) at load ([src/ThroughlineBuild.Cli/Config.cs:169-173](../../src/ThroughlineBuild.Cli/Config.cs#L169-L173)); even the three reason-translation verbs no longer hard-fail if it is absent (see below). Worker phases reach their provider via the worker CLI's own auth, independent of `ANTHROPIC_API_KEY`.

#### Reason translation is the only LLM consumer

Reason translation is the only path in the deterministic CLI that constructs the direct Anthropic client, and it is now fully optional. `WireUpConditionalCommands` only runs for `close`/`defer`/`reopen` ([src/ThroughlineBuild.Cli/Program.cs:1710-1737](../../src/ThroughlineBuild.Cli/Program.cs#L1710-L1737)); it tries `LlmClientFactory.Create`, and on `ConfigException` (no key, deprecated `default_model` unset, etc.) it logs `WARNING: LLM unavailable (...); recording reason verbatim without translation.` and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the last user message verbatim. The ticket state transition still runs. `ReasonTranslator` uses model `claude-haiku-4-5-20251001` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)). The old module-level `ANTHROPIC_API_KEY` hard gate is gone (TLB-227/TLB-371).

`.build/config.toml` is gitignored ([.gitignore:14](../../.gitignore#L14)) along with `secrets/` ([.gitignore:2](../../.gitignore#L2)). The `secrets/` directory is reserved and not read by any code path. The example config shows inline secrets by default with the env-var alternative commented; the `build init` template uses the `REQUIRED_PLANE_API_TOKEN` placeholder and supports `--token-env` to write the env-var indirection line instead (see [02-install-build-run.md](02-install-build-run.md)).

### Loose ends

- **Secrets in `.build/config.toml`** are stored plaintext on disk. Both templates encourage inline by default; env-var indirection is supported but optional.
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

([src/ThroughlineBuild.Cli/Program.cs:816-820](../../src/ThroughlineBuild.Cli/Program.cs#L816-L820)). The model id within an agent is then chosen by ticket size from that agent's `[workers.<name>.sizes]` map - there is no model-level CLI override.

For the `ship` push / plan-mode toggles, the CLI flag and the config key OR together: `--no-push || !Ship.Push` disables the push, and `--from-brief || Plan.IsPromote` selects promote mode.

For optional sections (`[llm]`, `[review]`, `[ship]`, `[work]`, `[plan]`, `[project]`): a missing section is equivalent to an all-defaults section ([src/ThroughlineBuild.Cli/Config.cs:454-462, 562-570, 598-608, 644-652, 654-657, 668-671](../../src/ThroughlineBuild.Cli/Config.cs#L454-L462)).

---

## Loose ends

- **Template-vs-live `default_agent` drift** (see the `.build/config.toml` section above): `build init` and the example default to `claude-code`; the checked-in operator config defaults to `codex`. No lockstep is enforced between the live config and the templates.
- **`backend`** and **`default_agent`** are read but not strictly validated at parse time; misconfiguration surfaces lazily (or, for unknown agents that do have a block, silently falls back to Claude Code).
- **`max_output_tokens`** is honored only by the Claude Code agent.
- **`workflow_tool`** is validated but never branched on.
- **`[llm] default_model`** is reason-translation-only and now deprecated; it does not configure worker models (those come from `[workers.<name>.sizes]`). This corrects the older claim that it feeds `ClaudeCodeAgent --model`.
- **Plaintext secrets** in the config file are the documented default in both templates.
- **Non-fatal validation only.** Beyond TOML parse plus required-section/required-key presence and the hard-break migration errors, the loader's extra validation is advisory: unknown keys (TLB-405) and a missing `[work].target_branch` (TLB-410) emit `warning:` lines but do not fail the run. There is no `build config check` verb to confirm the token is valid against Plane.
- **No per-environment overlay** (no `config.local.toml`). Operators with multiple Plane workspaces hand-edit the file or use `build init --force` to regenerate.
