# 04 - Configuration and Environment

Every config file the binary reads, every environment variable it consults, every secret it requires, and whether each is required or optional.

For installation-time concerns (including the `build init` bootstrap) see [02-install-build-run.md](02-install-build-run.md). For state files written at runtime see [05-state-and-persistence.md](05-state-and-persistence.md).

---

## `.build/config.toml`

The single source of operator-controlled configuration. Discovered by walking up from cwd looking for `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:64-75](../../src/ThroughlineBuild.Cli/Config.cs#L64-L75)). Missing file: exit 2 with `Config error: config file not found: searched from <cwd> upwards for .build/config.toml` ([src/ThroughlineBuild.Cli/Program.cs:151-162](../../src/ThroughlineBuild.Cli/Program.cs#L151-L162)).

Parsed by `Tomlyn` into the typed records `TicketingConfig`, `LlmConfig`, `WorkersConfig` (containing `AgentConfig` per agent), `EventsConfig`, `ReviewConfig`, `ShipConfig`, `WorkConfig`, and `ProjectContext` ([src/ThroughlineBuild.Cli/Config.cs:9-58](../../src/ThroughlineBuild.Cli/Config.cs#L9-L58)). The section readers run in `Load` ([src/ThroughlineBuild.Cli/Config.cs:99-116](../../src/ThroughlineBuild.Cli/Config.cs#L99-L116)).

Two templates exist and the project comment requires them to be kept in lockstep:
- [.build/config.toml.example](../../.build/config.toml.example) - the hand-edit reference, with codex/gemini/copilot agent blocks present but commented out.
- [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template) - the embedded resource emitted by `build init`, using `REQUIRED_*` placeholders for the four Plane fields.

### `[ticketing]` (required) - Functional

`ReadTicketingSection` ([src/ThroughlineBuild.Cli/Config.cs:179-191](../../src/ThroughlineBuild.Cli/Config.cs#L179-L191)).

| Key | Required | Default | Source |
|---|---|---|---|
| `backend` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:183](../../src/ThroughlineBuild.Cli/Config.cs#L183) - value is read but never compared; only `"plane"` is wired (no other adapter exists). |
| `plane_base_url` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:184](../../src/ThroughlineBuild.Cli/Config.cs#L184) |
| `plane_workspace_slug` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:185](../../src/ThroughlineBuild.Cli/Config.cs#L185) |
| `plane_project_id` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:186](../../src/ThroughlineBuild.Cli/Config.cs#L186) - UUID of the Plane project. |
| `plane_api_token_env` | no | `PLANE_API_TOKEN` | [src/ThroughlineBuild.Cli/Config.cs:187](../../src/ThroughlineBuild.Cli/Config.cs#L187) - name of the env var holding the token when not inline. |
| `plane_project_identifier` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:188](../../src/ThroughlineBuild.Cli/Config.cs#L188) - e.g. `"TLB"`. Used as a filename component and in Plane client options. |
| `plane_project_name` | no | `""` | [src/ThroughlineBuild.Cli/Config.cs:189](../../src/ThroughlineBuild.Cli/Config.cs#L189) - e.g. `"throughline-build"`. Filename component / `SessionContext.ProjectName`. |
| `plane_api_token` | no | `null` | [src/ThroughlineBuild.Cli/Config.cs:190](../../src/ThroughlineBuild.Cli/Config.cs#L190) - inline token; takes precedence over env. |

A missing or empty required key throws `ConfigException` (`missing required key '<k>' in [ticketing]` or `must be a non-empty string`); CLI exits 2 with `Config error: ...` ([src/ThroughlineBuild.Cli/Config.cs:141-148](../../src/ThroughlineBuild.Cli/Config.cs#L141-L148)).

### `[llm]` (optional section, optional keys) - Functional

If the section is absent, all values default to empty strings ([src/ThroughlineBuild.Cli/Config.cs:193-201](../../src/ThroughlineBuild.Cli/Config.cs#L193-L201)).

| Key | Default | Use |
|---|---|---|
| `default_model` | `""` | Vendor-prefixed model id for the direct Anthropic client used by reason translation. Consumed ONLY by `LlmClientFactory.Create` for `close`/`defer`/`reopen`; not passed to worker agents. ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)) |
| `anthropic_api_key_env` | `""` | Name of the env var holding the Anthropic key. |
| `anthropic_api_key` | `null` | Inline key; takes precedence over env ([src/ThroughlineBuild.Cli/Config.cs:200](../../src/ThroughlineBuild.Cli/Config.cs#L200)). |

`LlmClientFactory` requires `default_model` to be non-empty and to start with `anthropic:`; any other prefix throws `unsupported LLM vendor prefix '<p>' in [llm] default_model; only 'anthropic:' is supported`, and an empty value throws `LLM client required but [llm] default_model is not set in config.toml` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)). These errors only surface on `close`/`defer`/`reopen`; other verbs never construct the direct LLM client.

### `[workers]` (required section) - Functional

`ReadWorkersSection` ([src/ThroughlineBuild.Cli/Config.cs:203-289](../../src/ThroughlineBuild.Cli/Config.cs#L203-L289)).

| Key | Required | Default |
|---|---|---|
| `default_agent` | yes | - ([src/ThroughlineBuild.Cli/Config.cs:217](../../src/ThroughlineBuild.Cli/Config.cs#L217)) |
| `timeout_minutes` | no | `30` ([src/ThroughlineBuild.Cli/Config.cs:218](../../src/ThroughlineBuild.Cli/Config.cs#L218)) |
| `max_concurrency` | no | `min(ProcessorCount, 4)` ([src/ThroughlineBuild.Cli/Config.cs:281](../../src/ThroughlineBuild.Cli/Config.cs#L281)) - parallel ticket dispatch fan-out for `chain`. |

`default_agent` is the agent name used for any phase not overridden in `[workers.phases]` or by a CLI flag. The named sub-table must exist or the CLI throws `missing [workers.<name>] sub-table in config` at dispatch ([src/ThroughlineBuild.Cli/Program.cs:722-723](../../src/ThroughlineBuild.Cli/Program.cs#L722-L723)).

Migration guard: the old flat keys `claude_code_executable` and `max_output_tokens` directly under `[workers]` now throw a hard `ConfigException` directing the operator to move them into a `[workers.<name>]` sub-table ([src/ThroughlineBuild.Cli/Config.cs:207-215](../../src/ThroughlineBuild.Cli/Config.cs#L207-L215)).

Every sub-table under `[workers]` other than `phases` is parsed as an agent config ([src/ThroughlineBuild.Cli/Config.cs:223-279](../../src/ThroughlineBuild.Cli/Config.cs#L223-L279)).

### `[workers.<agent-name>]` (one block per agent) - Functional

Parsed into `AgentConfig(Executable, MaxOutputTokens, Sizes, BypassPermissions)` ([src/ThroughlineBuild.Cli/Config.cs:24](../../src/ThroughlineBuild.Cli/Config.cs#L24), populated at [src/ThroughlineBuild.Cli/Config.cs:245-278](../../src/ThroughlineBuild.Cli/Config.cs#L245-L278)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `executable` | yes | - | Path or bare command for the worker CLI ([src/ThroughlineBuild.Cli/Config.cs:245](../../src/ThroughlineBuild.Cli/Config.cs#L245)). |
| `max_output_tokens` | no | `null` | Only `ClaudeCodeAgent` uses it (sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS`); Codex/Gemini/Copilot accept the key but do not apply it ([src/ThroughlineBuild.Cli/Config.cs:246-251](../../src/ThroughlineBuild.Cli/Config.cs#L246-L251)). |
| `bypass_permissions` | no | `true` | Per-agent unattended-mode toggle (TLB-229). `true` emits the agent's skip-permissions flag; `false` opts back into the interactive gate ([src/ThroughlineBuild.Cli/Config.cs:257-259](../../src/ThroughlineBuild.Cli/Config.cs#L257-L259)). |
| `[workers.<name>.sizes]` | yes | - | Required sub-table mapping `small`/`medium`/`large` to model ids (TLB-196/197/198). |

`bypass_permissions` is wired into each agent's options ([src/ThroughlineBuild.Cli/Program.cs:752,760,774](../../src/ThroughlineBuild.Cli/Program.cs#L752)). It translates to a different flag per agent: `--dangerously-skip-permissions` for Claude Code ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:376-377](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L376-L377)), `--full-auto` for Codex ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:183-184](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L183-L184)), `--yolo` for Gemini ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:227-228](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L227-L228)). `CopilotOptions` has no `BypassPermissions` field; Copilot always runs `-s --no-ask-user` ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:22-24](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L22-L24)).

#### `[workers.<name>.sizes]` (required per agent) - Functional

| Key | Required |
|---|---|
| `small` | yes |
| `medium` | yes |
| `large` | yes |

All three keys must be present and non-empty or the loader throws `[workers.<name>.sizes] is missing required size keys: ...`; a missing sub-table throws `missing required [workers.<name>.sizes] sub-table in config` ([src/ThroughlineBuild.Cli/Config.cs:261-277](../../src/ThroughlineBuild.Cli/Config.cs#L261-L277)). Each value is the model id the agent passes to its `--model` flag for that size tier. The tier is selected from the ticket's size: `S -> Small`, `L -> Large`, anything else `-> Medium` ([src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs:7-12](../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)), via the `WorkerSize` enum ([src/ThroughlineBuild.Contracts/Models/WorkerSize.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerSize.cs)). Each agent strips its own vendor prefix before passing the id to the CLI: `anthropic:` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:398-400](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L398-L400)), `openai:` ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:203-205](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L203-L205)), `google:` ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:247-249](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L247-L249)), `github:` ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:172-174](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L172-L174)). There is no per-phase `--model` flag; per-phase model selection is achieved indirectly by pointing a phase at a different agent (see `[workers.phases]`).

#### `[workers.phases]` (optional sub-table) - Functional

Maps phase names to agent names (TLB-189/190/191). Allowed keys: `plan`, `implement`, `review`, `decompose`; any other key throws `unknown phase key '<k>' in [workers.phases]` and an empty value throws `value for '<k>' in [workers.phases] must be a non-empty string` ([src/ThroughlineBuild.Cli/Config.cs:228-242](../../src/ThroughlineBuild.Cli/Config.cs#L228-L242)).

Resolution per phase: `AgentFor(phase)` returns the `[workers.phases]` mapping if present, else `default_agent` ([src/ThroughlineBuild.Cli/Program.cs:781-782](../../src/ThroughlineBuild.Cli/Program.cs#L781-L782)). `EffectiveAgentFor(phase)` then layers CLI flags on top: a per-phase flag (`--agent-plan` / `--agent-implement` / `--agent-review`) wins over `--agent` (all phases), which wins over config (TLB-191 cli-flag-override) ([src/ThroughlineBuild.Cli/Program.cs:786-790](../../src/ThroughlineBuild.Cli/Program.cs#L786-L790)). The agent flags are extracted before dispatch ([src/ThroughlineBuild.Cli/Program.cs:63-66](../../src/ThroughlineBuild.Cli/Program.cs#L63-L66), [src/ThroughlineBuild.Cli/CliArgParser.cs:25-52](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L25-L52)).

The orchestrator constructs one agent per name referenced by `default_agent` or any phase mapping (plus any agent named on a CLI flag), selecting the implementation by name: `gemini`, `codex`, `copilot`, else `ClaudeCodeAgent` as fallback ([src/ThroughlineBuild.Cli/Program.cs:725-778](../../src/ThroughlineBuild.Cli/Program.cs#L725-L778)). See [02-install-build-run.md](02-install-build-run.md) "Worker CLIs" for the per-agent table.

### `[work]` (optional section) - Functional

`ReadWorkSection` ([src/ThroughlineBuild.Cli/Config.cs:385-393](../../src/ThroughlineBuild.Cli/Config.cs#L385-L393)). Parsed into `WorkConfig(string? TargetBranch)` ([src/ThroughlineBuild.Cli/Config.cs:46](../../src/ThroughlineBuild.Cli/Config.cs#L46)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `target_branch` | no | `null` | The branch `ship` merges into and pushes, overriding `[ship].base_branch`. An empty string is rejected (treated as unset) at read time ([src/ThroughlineBuild.Cli/Config.cs:390](../../src/ThroughlineBuild.Cli/Config.cs#L390)). |

`BuildConfig.ResolveTargetBranch()` returns `Work.TargetBranch ?? Ship.BaseBranch` ([src/ThroughlineBuild.Cli/Config.cs:58](../../src/ThroughlineBuild.Cli/Config.cs#L58)); that resolved value flows into `ShipOptions.TargetBranch` and `BuildOptions.TargetBranch` and is consumed by `ShipPhase` and the target-aware `BaseRefResolver` (see [01-inventory.md](01-inventory.md) ship verb, [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). The intended editing path is the `build settarget` verb, which validates the branch exists locally before writing the key and preserves config comments via line-edit; hand-editing the TOML works too. When `target_branch != base_branch`, ship enforces that the main worktree is checked out on the target branch before merging.

### `[events]` (required section) - Functional

| Key | Required |
|---|---|
| `log_directory` | yes ([src/ThroughlineBuild.Cli/Config.cs:291-296](../../src/ThroughlineBuild.Cli/Config.cs#L291-L296)) |

Resolved by `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:110-116](../../src/ThroughlineBuild.Cli/Config.cs#L110-L116)). A relative value is resolved against the project root (parent of `.build/`), not the config file's directory. Typical value: `.build/events`.

### `[review]` (optional section, sensible defaults) - Functional

`ReadReviewSection` ([src/ThroughlineBuild.Cli/Config.cs:301-335](../../src/ThroughlineBuild.Cli/Config.cs#L301-L335)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:303-309](../../src/ThroughlineBuild.Cli/Config.cs#L303-L309)).

| Key | Default |
|---|---|
| `verifier_timeout_minutes` | `15` ([src/ThroughlineBuild.Cli/Config.cs:311](../../src/ThroughlineBuild.Cli/Config.cs#L311)) |
| `verifier_allowed_tools` | `["Read", "Grep", "Glob"]` ([src/ThroughlineBuild.Cli/Config.cs:298-299](../../src/ThroughlineBuild.Cli/Config.cs#L298-L299)) |
| `[[review.checks]]` (array-of-tables) | empty list |

Each `[[review.checks]]` entry maps to a `CheckSpec(name, executable, arguments, timeout)` consumed during the review phase. `name` and `executable` are required; `arguments` defaults to empty and `timeout_minutes` defaults to `5` ([src/ThroughlineBuild.Cli/Config.cs:314-329](../../src/ThroughlineBuild.Cli/Config.cs#L314-L329)).

```
[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test"]
timeout_minutes = 5
```

### `[ship]` (optional section) - Functional

`ReadShipSection` ([src/ThroughlineBuild.Cli/Config.cs:337-376](../../src/ThroughlineBuild.Cli/Config.cs#L337-L376)). Missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:339-346](../../src/ThroughlineBuild.Cli/Config.cs#L339-L346)).

| Key | Default |
|---|---|
| `remote` | `"origin"` ([src/ThroughlineBuild.Cli/Config.cs:348](../../src/ThroughlineBuild.Cli/Config.cs#L348)) |
| `base_branch` | `"main"` ([src/ThroughlineBuild.Cli/Config.cs:349](../../src/ThroughlineBuild.Cli/Config.cs#L349)) |
| `delete_feature_branch` | `true` ([src/ThroughlineBuild.Cli/Config.cs:350-352](../../src/ThroughlineBuild.Cli/Config.cs#L350-L352)) |
| `[[ship.regression_checks]]` | empty list |

Same `CheckSpec` shape and default rules as `review.checks` ([src/ThroughlineBuild.Cli/Config.cs:354-369](../../src/ThroughlineBuild.Cli/Config.cs#L354-L369)). The two lists are independent so each phase evolves separately.

### `[project]` (optional section, all keys optional) - Functional

`ReadProjectSection` ([src/ThroughlineBuild.Cli/Config.cs:378-433](../../src/ThroughlineBuild.Cli/Config.cs#L378-L433)) - context handed to brief builders so the worker knows the stack it operates in. Missing section returns `ProjectContext.Empty`.

| Key | Default | Notes |
|---|---|---|
| `language`, `framework`, `package_manager`, `build_command`, `test_command`, `install_command`, `dev_command`, `plane_project_url` | `""` | Flowed into brief context dictionaries ([src/ThroughlineBuild.Cli/Config.cs:383-390](../../src/ThroughlineBuild.Cli/Config.cs#L383-L390)). |
| `notes_file` | `""` | Path to a file (relative to the config file dir, or absolute) whose contents are injected into the plan brief. Missing or unreadable emits a stderr warning and proceeds with empty notes ([src/ThroughlineBuild.Cli/Config.cs:397-420](../../src/ThroughlineBuild.Cli/Config.cs#L397-L420)). |
| `workflow_tool` | `"build"` | Must be `"build"` or `"claude-config"` ([src/ThroughlineBuild.Cli/Config.cs:391-395](../../src/ThroughlineBuild.Cli/Config.cs#L391-L395)). Any other value: `ConfigException`, exit 2. |

`plane_project_url` is consumed only as brief context - it is injected into the plan/implement/review/decompose brief dictionaries as `project_plane_project_url` ([src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs:51](../../src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs#L51) and the three peer builders). It is NOT used to build the per-ticket browse URL in CLI summaries; that URL is built from `plane_base_url` + `plane_workspace_slug` via `BuildPlaneUrl` ([src/ThroughlineBuild.Cli/Program.cs:1579-1584](../../src/ThroughlineBuild.Cli/Program.cs#L1579-L1584)).

### Loose ends

- **`backend` value is unchecked.** [src/ThroughlineBuild.Cli/Config.cs:183](../../src/ThroughlineBuild.Cli/Config.cs#L183) reads it but never compares; any value loads as a Plane backend. Only `"plane"` is meaningful.
- **`default_agent` value is unchecked at parse time.** It is validated lazily: dispatch throws `missing [workers.<name>] sub-table` if no matching block exists ([src/ThroughlineBuild.Cli/Program.cs:722-723](../../src/ThroughlineBuild.Cli/Program.cs#L722-L723)), and unknown agent names fall through to `ClaudeCodeAgent` ([src/ThroughlineBuild.Cli/Program.cs:769-775](../../src/ThroughlineBuild.Cli/Program.cs#L769-L775)).
- **`max_output_tokens` is honored only by Claude Code.** The example/template comments flag it as accepted-but-unused for the other three agents.
- **`workflow_tool` enum** is validated but unused at runtime - the value is stored on `ProjectContext` and flowed into brief context for the worker to read; nothing in code branches on it.
- **`notes_file`** path resolution is anchored at the config file's directory, not the project root - inconsistent with `events.log_directory` which anchors at project root ([src/ThroughlineBuild.Cli/Config.cs:401-403 vs :110-116](../../src/ThroughlineBuild.Cli/Config.cs#L401-L403)).
- **Disagreement with the architecture doc / older state doc:** prior docs claimed `[llm] default_model` flows to `ClaudeCodeAgent` as `--model`. It does not; workers select their model from `[workers.<name>.sizes]`, and `default_model` is consumed only by the reason-translation path ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L28)).

---

## Environment variables

### Read by the binary

| Variable | Required for | What happens if unset |
|---|---|---|
| `PLANE_API_TOKEN` (or whatever `ticketing.plane_api_token_env` names) | every Plane operation | exit 3 `Secret error: plane_api_token not set in config and required environment variable '<name>' is not set` ([src/ThroughlineBuild.Cli/Config.cs:120-125](../../src/ThroughlineBuild.Cli/Config.cs#L120-L125)) |
| `ANTHROPIC_API_KEY` (or whatever `llm.anthropic_api_key_env` names) | `close` / `defer` / `reopen` (reason translation) | resolved as an optional secret at load ([src/ThroughlineBuild.Cli/Config.cs:127-131](../../src/ThroughlineBuild.Cli/Config.cs#L127-L131)); only those three verbs exit 3 if the key is absent, via `LlmClientFactory` ([src/ThroughlineBuild.Cli/Program.cs:270-274](../../src/ThroughlineBuild.Cli/Program.cs#L270-L274)) |
| `BUILD_PROGRESS` | optional - set to `1` to keep the progress digest on even when stderr is redirected | digest auto-suppresses when stderr is redirected and `BUILD_PROGRESS != 1`, to keep CI/script logs clean ([src/ThroughlineBuild.Cli/Program.cs:392-394, 841, 1429](../../src/ThroughlineBuild.Cli/Program.cs#L392-L394)) |
| `EDITOR` (via `ReviewLoop.DefaultEditorResolver`) | the interactive `e` (edit) action in `build new ... --review` | falls back to a platform candidate chain (`vim`, `nano`, `code --wait`; on Windows also `notepad.exe`) ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-274](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L274)) |

The hard gate that previously aborted every run when `ANTHROPIC_API_KEY` was missing has been removed (TLB-227). The Anthropic key is now resolved as optional and only required at the point a verb actually constructs the direct LLM client.

### Set / removed by the binary in worker subprocesses

Each agent sanitizes its child environment to force subscription/OAuth auth rather than orchestrator-key auth, then applies any caller-supplied `EnvironmentVariables` last (so an explicit override wins):

| Agent | Action | Source |
|---|---|---|
| Claude Code | removes `ANTHROPIC_API_KEY`; sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from `max_output_tokens` when set | [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:404-415](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L404-L415) |
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

Two secrets, both required-by-context ([src/ThroughlineBuild.Cli/Config.cs:118-132](../../src/ThroughlineBuild.Cli/Config.cs#L118-L132)):

1. **Plane API token.** Always required (every verb hits Plane). Resolution: inline `plane_api_token`, else the env var named by `plane_api_token_env` (default `PLANE_API_TOKEN`). Missing: exit 3 at load.
2. **Anthropic API key.** Required only for `close` / `defer` / `reopen` reason translation. Resolution: inline `anthropic_api_key`, else the env var named by `anthropic_api_key_env`. Resolved as optional (`null` allowed) at load; only the three reason-translation verbs hard-fail if it is absent. Worker phases reach their provider via the worker CLI's own auth, independent of `ANTHROPIC_API_KEY`.

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

([src/ThroughlineBuild.Cli/Program.cs:786-790](../../src/ThroughlineBuild.Cli/Program.cs#L786-L790)). The model id within an agent is then chosen by ticket size from that agent's `[workers.<name>.sizes]` map - there is no model-level CLI override.

For optional sections (`[llm]`, `[review]`, `[ship]`, `[project]`): a missing section is equivalent to an all-defaults section ([src/ThroughlineBuild.Cli/Config.cs:195-201, 303-309, 339-346, 380-381](../../src/ThroughlineBuild.Cli/Config.cs#L195-L201)).

---

## Loose ends

- **`backend`** and **`default_agent`** are read but not strictly validated at parse time; misconfiguration surfaces lazily (or, for unknown agents, silently falls back to Claude Code).
- **`max_output_tokens`** is honored only by the Claude Code agent.
- **`workflow_tool`** is validated but never branched on.
- **`[llm] default_model`** is reason-translation-only; it does not configure worker models. This corrects the older claim that it feeds `ClaudeCodeAgent --model`.
- **Plaintext secrets** in the config file are the documented default in both templates.
- **No `build config check`** verb exists; the only validation is TOML parse plus required-section/required-key presence.
- **No per-environment overlay** (no `config.local.toml`). Operators with multiple Plane workspaces hand-edit the file or use `build init --force` to regenerate.
