# 04 - Configuration and Environment

Last refreshed: 2026-07-26 (HEAD 00dc074)

Every config file the binary reads, every environment variable it consults, every secret it requires, and whether each is required or optional.

For installation-time concerns (including the `build init` / `build setup` bootstrap) see [02-install-build-run.md](02-install-build-run.md). For state files written at runtime see [05-state-and-persistence.md](05-state-and-persistence.md).

---

## `.build/config.toml`

The single source of operator-controlled configuration. Discovered by `BuildConfigLoader.FindConfigFile` walking up from cwd looking for `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:106-117](../../src/ThroughlineBuild.Cli/Config.cs#L106-L117)). Missing file: exit 2 with `Config error: config file not found: searched from <cwd> upwards for .build/config.toml` ([src/ThroughlineBuild.Cli/Program.cs:427-438](../../src/ThroughlineBuild.Cli/Program.cs#L427-L438)).

Parsed by `Tomlyn` into the typed records `TicketingConfig`, `LlmConfig`, `WorkersConfig` (containing `AgentConfig` per agent), `EventsConfig`, `ReviewConfig`, `ShipConfig`, `WorkConfig`, `ProjectContext`, `PlanConfig`, and the new `BatchConfig`, aggregated into `BuildConfig` ([src/ThroughlineBuild.Cli/Config.cs:10-95](../../src/ThroughlineBuild.Cli/Config.cs#L10-L95)). The ten section readers run in `BuildConfigLoader.Load` ([src/ThroughlineBuild.Cli/Config.cs:144-153](../../src/ThroughlineBuild.Cli/Config.cs#L144-L153)).

**There is now exactly one tracked template.** The old hand-edit reference `.build/config.toml.example` has been deleted; the embedded `build init` template [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template) is the single documented shape, using `REQUIRED_*` placeholders for the four Plane fields. It sets `default_agent = "claude-code"` with an active `[workers.codex]` block as the alternate. The generated live `.build/config.toml` is gitignored and may differ. The template now ships **empty review/ship checks and a blank `[project]` toolchain on purpose** (commit 187d1ca "make build toolchain no-op by default"): checks and toolchain fields are derived from the op-doc at `build scaffold` time, so a project with no build/test step passes the gate instead of hard-failing on a phantom `dotnet build`; commented per-stack examples remain for hand configuration.

Three things edit the config file programmatically, all comment-preserving line edits rather than re-serialization: `build settarget` (the `[work].target_branch` key, `SetTargetCommand`), `build models refresh` (only the `[workers.codex.sizes]` block, `CodexSizesBlockEditor`), and scaffold profile derivation (`ConfigProfileWriter` writes derived `[[review.checks]]` / `[[ship.regression_checks]]` and the `[project]` toolchain + `convention_files`; it refuses to overwrite checks that look hand-customized unless `--force-profile`, `ConfigProfileWriter.AlreadyConfiguredSkipReason` at [src/ThroughlineBuild.Cli/ConfigProfileWriter.cs:73](../../src/ThroughlineBuild.Cli/ConfigProfileWriter.cs#L73)).

### Unknown-key warnings (TLB-405)

After the typed sections load, `BuildConfigLoader.Load` runs a non-fatal validation pass that emits one `warning: unknown config key <path> - ignored` per unrecognized key to `stderr` (or a supplied `warnSink`) - the run still proceeds. Driver: [src/ThroughlineBuild.Cli/Config.cs:155-169](../../src/ThroughlineBuild.Cli/Config.cs#L155-L169); implementation `CollectUnknownKeyWarnings` ([src/ThroughlineBuild.Cli/Config.cs:279-450](../../src/ThroughlineBuild.Cli/Config.cs#L279-L450)). The allowlists are static `HashSet<string>` fields per scope - `KnownTopLevelSections` (now ten sections including `batch`) through `KnownBatchKeys` ([src/ThroughlineBuild.Cli/Config.cs:198-277](../../src/ThroughlineBuild.Cli/Config.cs#L198-L277)). The pass now also descends into the per-size tier tables (`KnownTierKeys`: `model`, `effort`) and validates `[[review.checks]]` / `[[ship.regression_checks]]` entries against `KnownCheckEntryKeys` (`name`, `executable`, `arguments`, `timeout_minutes`, `role`, `canary`). `[workers.phases]` is skipped by this pass because it already hard-errors on unknown keys (see below).

### Required-field handling (TLB-369)

Required scalars go through `RequireString`, which throws `ConfigException` (`missing required key '<k>' in [<section>]` or `key '<k>' in [<section>] must be a non-empty string`); required sections go through `RequireSection` (`missing required TOML section [<section>]`) ([src/ThroughlineBuild.Cli/Config.cs:452-457](../../src/ThroughlineBuild.Cli/Config.cs#L452-L457), [src/ThroughlineBuild.Cli/Config.cs:495-502](../../src/ThroughlineBuild.Cli/Config.cs#L495-L502)). Both surface as `Config error: ...`, exit 2. The `build init` template annotates every required field with an inline `REQUIRED` comment.

### `[ticketing]` (required) - Functional

Read by `ReadTicketingSection` into `TicketingConfig` ([src/ThroughlineBuild.Cli/Config.cs:540-552](../../src/ThroughlineBuild.Cli/Config.cs#L540-L552)); the first four keys are `RequireString`-gated.

| Key | Required | Default | Meaning |
|---|---|---|---|
| `backend` | yes | - | Read but never compared; only `"plane"` is wired (no other adapter exists). |
| `plane_base_url` | yes | - | Plane API base URL. |
| `plane_workspace_slug` | yes | - | Workspace slug. |
| `plane_project_id` | yes | - | UUID of the Plane project. Connected `build init` fills this by find-or-create so the operator never pastes it. |
| `plane_api_token_env` | no | `PLANE_API_TOKEN` | Name of the env var holding the token when not inline. |
| `plane_project_identifier` | no | `""` | e.g. `"TLB"`. Filename component and Plane client option. |
| `plane_project_name` | no | `""` | e.g. `"throughline-build"`. Filename component / `SessionContext.ProjectName`. |
| `plane_api_token` | no | `null` | Inline token; takes precedence over env. |
| `plane_requests_per_minute` | no | `40` | Per-process cap on Plane API calls/min, enforced client-side by `RequestThrottle`. The default is sized for Plane Cloud's 60/min, leaving headroom for a second concurrent `build`. Raise it for a self-hosted Plane with a higher limit (or none). Non-positive values are rejected at load (TLB-565). |

A missing or empty required key throws `ConfigException`; CLI exits 2 with `Config error: ...`.

### `[llm]` (optional section, optional keys) - Functional

Read by `ReadLlmSection` into `LlmConfig`; if the section is absent, all values default to empty ([src/ThroughlineBuild.Cli/Config.cs:554-562](../../src/ThroughlineBuild.Cli/Config.cs#L554-L562)).

| Key | Default | Use |
|---|---|---|
| `default_model` | `""` | **DEPRECATED for worker-model selection.** Vendor-prefixed model id for the direct Anthropic client used by reason translation. Consumed ONLY by `LlmClientFactory.Create` for `close`/`defer`/`reopen`; workers get their model from `[workers.<agent>.sizes]`. Commented out in both the template and the live config, whose comments state it "is no longer used for worker-model selection". |
| `anthropic_api_key_env` | `""` | Name of the env var holding the Anthropic key. |
| `anthropic_api_key` | `null` | Inline key; takes precedence over env. |

`LlmClientFactory.Create` requires `default_model` to be non-empty and to start with `anthropic:`; any other prefix throws `unsupported LLM vendor prefix '<p>' ...`, and an empty value throws `LLM client required but [llm] default_model is not set in config.toml` ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:10-29](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L10-L29)). These errors only surface on `close`/`defer`/`reopen`, and even then are non-fatal: the verb falls back to an `EchoLlmClient` that records the reason verbatim (see [Reason translation](#reason-translation-is-the-only-llm-consumer)). Other verbs never construct the direct LLM client at all.

### `[workers]` (required section) - Functional

Read by `ReadWorkersSection` into `WorkersConfig` ([src/ThroughlineBuild.Cli/Config.cs:564-696](../../src/ThroughlineBuild.Cli/Config.cs#L564-L696)).

| Key | Required | Default |
|---|---|---|
| `default_agent` | yes | - |
| `timeout_minutes` | no | `30` |
| `max_concurrency` | no | `min(ProcessorCount, 4)` - retained config key; the dispatcher is pinned to serial execution regardless (see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md)). |

`default_agent` is the agent name used for any phase not overridden in `[workers.phases]` or by a CLI flag. **Fail-fast validation (TLB-512):** after parsing the agent sub-tables, `ReadWorkersSection` verifies that `default_agent` and every `[workers.phases]` value resolve to a defined `[workers.<name>]` sub-table; a miss (the classic trigger: `default_agent = "codex"` with the codex blocks commented out) throws a `ConfigException` built by `BuildUndefinedAgentMessage` that names the offending setting, the fix, and the agents that ARE defined - `Config error:`, exit 2, instead of a late unhandled exception at agent-resolution time ([src/ThroughlineBuild.Cli/Config.cs:679-686](../../src/ThroughlineBuild.Cli/Config.cs#L679-L686), message builder at [:702-713](../../src/ThroughlineBuild.Cli/Config.cs#L702-L713)).

Migration guard (hard-break): the old flat keys `claude_code_executable` and `max_output_tokens` directly under `[workers]` throw a hard `ConfigException` directing the operator to move them into a `[workers.<name>]` sub-table ([src/ThroughlineBuild.Cli/Config.cs:568-576](../../src/ThroughlineBuild.Cli/Config.cs#L568-L576)).

Every sub-table under `[workers]` other than `phases` is parsed as an agent config.

### `[workers.<agent-name>]` (one block per agent) - Functional

Parsed into `AgentConfig(Executable, MaxOutputTokens, Sizes, BypassPermissions, Transport)` where `Sizes` is `IReadOnlyDictionary<WorkerSize, ModelTier>` and `Transport` carries the Claude-specific transport selection ([src/ThroughlineBuild.Cli/Config.cs](../../src/ThroughlineBuild.Cli/Config.cs)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `executable` | yes | - | Path or bare command for the worker CLI. |
| `max_output_tokens` | no | `null` | Only `ClaudeCodeAgent` uses it (sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS`); Codex/Gemini/Copilot accept the key but do not apply it. |
| `bypass_permissions` | no | `true` | Per-agent unattended-mode toggle (TLB-229). `true` emits the agent's skip-permissions flag; `false` opts back into the interactive gate. |
| `transport` | no | `interactive-hook` | Honored on any Claude-family agent block (anything mapping to `ClaudeCodeAgent`, i.e. not gemini/codex/copilot), not just the literal `claude-code`. Accepted values are `print` and `interactive-hook`; unknown values hard-fail config loading and name both supported values. On gemini/codex/copilot, `transport` emits the standard unknown-key warning and is ignored. The omitted-value default is `interactive-hook` after the Stage 07 cutover; `print` is the documented rollback. `interactive-hook` launches a fresh interactive Claude session under a terminal host (ConPTY on Windows, PTY on Unix; no `--print`), synthesizes the completion from claude's persisted transcript, parses the full final assistant response, and never falls back to `--print`. `ClaudeCodePreflight` gates it (claude runnable, version >= 2.1.177, platform supported) in `build setup`, before the worker-spawning phase verbs, and at the transport entry itself ([`ClaudeCodeInteractiveTransport.ExecuteAsync`](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeInteractiveTransport.cs), [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodePreflight.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodePreflight.cs)). |
| `[workers.<name>.sizes]` | yes | - | Required sub-table mapping `small`/`medium`/`large` to model tiers. A missing sub-table throws. |

`bypass_permissions` is wired into each agent's options by `WorkerAgentBuilder.Create` ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)). It translates to a different flag per agent: `--dangerously-skip-permissions` for Claude Code, `--dangerously-bypass-approvals-and-sandbox` for Codex, `--yolo` for Gemini. `CopilotOptions` has no `BypassPermissions` field; Copilot always runs `-s --no-ask-user` - an asymmetry vs the other three agents (per-agent flag sites are cited in [03-external-dependencies.md](03-external-dependencies.md)).

`transport` is parsed for any Claude-family agent name (not gemini/codex/copilot) and mapped by `WorkerAgentBuilder` into `ClaudeCodeOptions.Transport`. Omission resolves to `interactive-hook` (the config loader's omitted-value default at [src/ThroughlineBuild.Cli/Config.cs](../../src/ThroughlineBuild.Cli/Config.cs)) after the Stage 07 cutover, and the generated `build init` template also sets `transport = "interactive-hook"` explicitly; `print` is the rollback. The `ClaudeCodeOptions`/`AgentConfig` type-level defaults stay `Print` because they only govern directly-constructed options (tests, the print transport itself), not config loading. The interactive host is implemented on Windows (ConPTY) and Unix (PTY); non-debug runs remove their private per-invocation run directory, while debug runs retain settings and completion evidence under the configured capture directory ([`ClaudeCodeInteractiveTransport.ExecuteAsync`](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeInteractiveTransport.cs)).

#### `[workers.<name>.sizes]` (required per agent) - Functional, schema hard-break (op-33)

Each of `small` / `medium` / `large` is **required** and must be an **inline table** `{ model = "...", effort = "..." }` parsed into `ModelTier(Model, Effort)`. The bare-string form (`small = "haiku"`) that the previous refresh documented is **rejected** with `[workers.<n>.sizes.<k>] must be an inline table like { model = "...", effort = "..." }, not a bare string`; a missing `model` or missing size key also throws ([src/ThroughlineBuild.Cli/Config.cs:627-666](../../src/ThroughlineBuild.Cli/Config.cs#L627-L666)).

- `model` (required, non-empty): the id passed to the agent's `--model` flag after vendor-prefix stripping (`anthropic:` / `openai:` / `google:` / `github:` - optional in config).
- `effort` (optional): Codex-only reasoning level (`minimal` | `low` | `medium` | `high` | `xhigh`), passed as `-c model_reasoning_effort=<effort>`; the other agents ignore it.
- **Claude Code model validation (TLB-544):** for the `claude-code` block only, each `model` value is checked by `ClaudeCodeModelValidator.Validate` at config load - only the tier aliases `haiku`/`sonnet`/`opus` or a full `claude-*` slug pass; the alias trap `model = "fable"` is named explicitly and must be `claude-fable-5` ([src/ThroughlineBuild.Cli/Config.cs:643-648](../../src/ThroughlineBuild.Cli/Config.cs#L643-L648), [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs:22-48](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs#L22-L48)).

The tier is selected from the ticket's size: `S -> Small`, `L -> Large`, anything else `-> Medium` via `WorkerSizeMapper` ([src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs:7-12](../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)). There is no per-phase `--model` flag; per-phase model selection is achieved indirectly by pointing a phase at a different agent. This is what replaced the deprecated `[llm] default_model` worker-model path.

**The `[workers.codex.sizes]` block is machine-managed.** `build init` populates it from a live `codex debug models` probe (`CodexModelProbe` + `CodexTierMapper`), and `build models refresh` re-probes and rewrites exactly that block in place with a current-to-proposed diff, leaving every other byte of the config untouched ([src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs:24-82](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L24-L82); block machinery `CodexSizesBlockEditor`/`Reader`/`Renderer` in [src/ThroughlineBuild.Cli/CodexSizesBlockEditor.cs](../../src/ThroughlineBuild.Cli/CodexSizesBlockEditor.cs)).

#### `[workers.phases]` (optional sub-table) - Functional

Maps phase names to agent names (TLB-189/190/191). Allowed keys: `plan`, `implement`, `review`, `decompose`; any other key throws `unknown phase key '<k>' in [workers.phases]` and an empty value throws `value for '<k>' in [workers.phases] must be a non-empty string` ([src/ThroughlineBuild.Cli/Config.cs:589-604](../../src/ThroughlineBuild.Cli/Config.cs#L589-L604)). Every mapped agent name is validated against the defined sub-tables at load (TLB-512, see `[workers]` above). This sub-table is skipped by the unknown-key warning pass because it already hard-errors on its own.

Resolution per phase: the local function `AgentFor(phase)` returns the `[workers.phases]` mapping if present, else `default_agent`; `EffectiveAgentFor(phase)` layers CLI flags on top - a per-phase flag (`--agent-plan` / `--agent-implement` / `--agent-review`) wins over `--agent` (all phases), which wins over config ([src/ThroughlineBuild.Cli/Program.cs:1143-1153](../../src/ThroughlineBuild.Cli/Program.cs#L1143-L1153)). The agent flags are extracted before dispatch by `CliArgParser.ExtractAgentFlags` ([src/ThroughlineBuild.Cli/Program.cs:102-105](../../src/ThroughlineBuild.Cli/Program.cs#L102-L105)).

The orchestrator constructs one agent per name referenced by `default_agent`, any phase mapping, or a CLI flag, building each through `WorkerAgentBuilder.Create` (name switch: `gemini`, `codex`, `copilot`, else `ClaudeCodeAgent` fallback) ([src/ThroughlineBuild.Cli/Program.cs:1117-1141](../../src/ThroughlineBuild.Cli/Program.cs#L1117-L1141)). See [02-install-build-run.md](02-install-build-run.md) "Worker CLIs" for the per-agent table.

### `[work]` (optional section) - Functional

Read by `ReadWorkSection` into `WorkConfig(string? TargetBranch)` ([src/ThroughlineBuild.Cli/Config.cs:818-826](../../src/ThroughlineBuild.Cli/Config.cs#L818-L826)).

| Key | Required | Default | Notes |
|---|---|---|---|
| `target_branch` | no | `null` | The branch `ship` merges into and pushes, overriding `[ship].base_branch`. An empty string is treated as unset at read time. |

A hand-edited `target_branch` bypasses the `build settarget` branch check, so `Load` runs a non-fatal existence validation when a `branchExists` validator is supplied: if the configured branch does not resolve to a local ref, it emits `warning: [work].target_branch '<b>' does not resolve to a local branch - ship will block until it exists or you run 'build settarget'` through the same warning channel as the unknown-key pass ([src/ThroughlineBuild.Cli/Config.cs:159-165](../../src/ThroughlineBuild.Cli/Config.cs#L159-L165)). The check is skipped when no validator is passed (unit tests, or commands that do not touch git).

`BuildConfig.ResolveTargetBranch()` returns `Work.TargetBranch ?? Ship.BaseBranch` ([src/ThroughlineBuild.Cli/Config.cs:89](../../src/ThroughlineBuild.Cli/Config.cs#L89)); `TargetBranchOverridden` is true when `Work.TargetBranch is not null` ([src/ThroughlineBuild.Cli/Config.cs:94](../../src/ThroughlineBuild.Cli/Config.cs#L94)). Both flow into `ShipOptions` ([src/ThroughlineBuild.Cli/Program.cs:1622-1632](../../src/ThroughlineBuild.Cli/Program.cs#L1622-L1632)) and `BuildOptions.TargetBranch`, consumed by `ShipPhase` and the target-aware `BaseRefResolver`. The intended editing path is the `build settarget` verb, which validates the branch exists locally before writing the key and preserves config comments via line-edit; hand-editing the TOML works too. When `target_branch != base_branch`, ship enforces that the main worktree is checked out on the target branch before merging - and chain refuses a wrong main-worktree branch at preflight, not at ship (b9366cd).

### `[events]` (required section) - Functional

Read by `ReadEventsSection`; the section itself is `RequireSection`-gated and `log_directory` is its single required key ([src/ThroughlineBuild.Cli/Config.cs:715-720](../../src/ThroughlineBuild.Cli/Config.cs#L715-L720)). Resolved by `ResolveLogDirectory`: a relative value is resolved against the project root (parent of `.build/`), not the config file's directory ([src/ThroughlineBuild.Cli/Config.cs:174-180](../../src/ThroughlineBuild.Cli/Config.cs#L174-L180)). Typical value: `.build/events`.

### `[review]` (optional section, sensible defaults) - Functional

Read by `ReadReviewSection` into `ReviewConfig`; missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:725-766](../../src/ThroughlineBuild.Cli/Config.cs#L725-L766)).

| Key | Default | Meaning |
|---|---|---|
| `verifier_timeout_minutes` | `15` | Verifier worker timeout. |
| `verifier_allowed_tools` | `["Read", "Grep", "Glob"]` | Tool allowlist for the verifier worker. Only enforced by claude-code/copilot; a startup warning fires when the review agent ignores it (TLB-478, `VerifierToolEnforcement.UnenforcedWarning`, [src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs:20-30](../../src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs#L20-L30)). |
| `verify_gate_vacuity` | `true` | NEW. Enables the gate non-vacuity prover: on a gating check's first green, a per-check canary proves the check can actually fail; a vacuous check hard-fails (da544ff/b736f14). Read at [src/ThroughlineBuild.Cli/Config.cs:738](../../src/ThroughlineBuild.Cli/Config.cs#L738); wired as `GateVacuityProver` only when true ([src/ThroughlineBuild.Cli/Program.cs:1813](../../src/ThroughlineBuild.Cli/Program.cs#L1813)). |
| `[[review.checks]]` (array-of-tables) | empty list | Automated checks; see below. |

Each `[[review.checks]]` entry maps to a `CheckSpec(Name, Executable, Arguments, Timeout, Role, Canary)`. `name` and `executable` are required; `arguments` defaults to empty, `timeout_minutes` defaults to `5`. Entry keys are validated against `KnownCheckEntryKeys` (now including `canary`) by the unknown-key warning pass.

**`role` field** - parsed by `ParseCheckRole`, which now accepts three values; an unrecognized string throws `key 'role' in [<context>] must be "gating", "advisory", or "setup", got "<v>"` ([src/ThroughlineBuild.Cli/Config.cs:459-470](../../src/ThroughlineBuild.Cli/Config.cs#L459-L470)):

| Value | Behaviour |
|---|---|
| `"gating"` (default) | Non-zero exit hard-fails the gate; the ticket cannot advance to review. |
| `"advisory"` | Result is recorded and shown to the verifier; never hard-fails the gate, never drives the rework loop (d30dbac), and never blocks the ship regression gate (22a79ab). |
| `"setup"` | NEW. Not a check - a prerequisite command the engine runs once in the worktree BEFORE the gating/advisory checks (e.g. `xcodegen generate`); a failed setup step hard-fails the gate (371de26). |

**`canary` field** - optional inline-table array `canary = [{ path = "...", content = "..." }]` parsed by `ParseCanary` ([src/ThroughlineBuild.Cli/Config.cs:475-493](../../src/ThroughlineBuild.Cli/Config.cs#L475-L493)): the smallest deliberately-broken file the check must reject, used by the vacuity prover. Entries lacking a path are skipped (best-effort).

**Capability map - abstract check names and their roles** (TLB-501; the template documents the same mapping):

| Abstract name | Role | Rationale |
|---|---|---|
| `build` | gating | Non-zero exit is a hard block; implementer cannot proceed to review. |
| `test` | gating | Test failures hard-fail the gate. Pass `--no-build` when a `build` check precedes it. |
| `typecheck` | gating | Static type-check (distinct from build in languages where build does not type-check). |
| `lint` | advisory | Style/lint failures are recorded and surfaced to the verifier but never hard-fail the gate. |
| `format` | advisory | Formatting violations are recorded as advisory; the verifier sees them as a smoke signal. |

A check absent from config is not-configured and treated as not-run, never as a failure. The gate skips checks not present in the configured list; it never synthesizes a failure for a missing check name. **The template ships this list empty on purpose** - checks are derived from the op-doc at `build scaffold` time (the deriver emits roles, canaries, hermetic test commands, and cache-disabled linter invocations; see [03-external-dependencies.md](03-external-dependencies.md) "Worker subprocesses outside the phases").

```toml
[[review.checks]]
name = "build"
executable = "dotnet"
arguments = ["build", "--no-restore", "-c", "Release", "--nologo", "-v", "q"]
timeout_minutes = 5
role = "gating"

[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test", "--no-build", "-c", "Release", "--nologo", "-v", "q"]
timeout_minutes = 10
role = "gating"
```

### `[ship]` (optional section) - Functional

Read by `ReadShipSection` into `ShipConfig`; missing section = all defaults ([src/ThroughlineBuild.Cli/Config.cs:768-816](../../src/ThroughlineBuild.Cli/Config.cs#L768-L816)).

| Key | Default |
|---|---|
| `remote` | `"origin"` |
| `base_branch` | `"main"` |
| `delete_feature_branch` | `true` |
| `push` | `true` |
| `[[ship.regression_checks]]` | empty list |

`push` (TLB-410) gates whether ship touches the remote after the local fast-forward merge. The effective no-push decision is `NoPush = noPush || !config.Ship.Push`, so either the `--no-push` CLI flag or `push = false` in config disables the remote push ([src/ThroughlineBuild.Cli/Program.cs:1629](../../src/ThroughlineBuild.Cli/Program.cs#L1629)); ship then rebases onto the local target and emits a `fetch_skipped` reason. When `push = true` and the target branch does not yet exist on the remote, ship rebases onto the local target and lets the push create it (TLB-409). Within a recursive chain, integration-branch ships are always `NoPush: true` (the accumulated work is pushed once when the root chain lands, [src/ThroughlineBuild.Cli/Program.cs:1907-1929](../../src/ThroughlineBuild.Cli/Program.cs#L1907-L1929)).

`[[ship.regression_checks]]` entries share the `CheckSpec` shape, role values, and canary support with `review.checks`. The two lists are independent so each phase evolves separately. Regression checks are baseline-aware (TLB-401): only newly-failing checks block the ship; pre-existing failures are noted non-blocking. Advisory regression checks never block, and contradictory baselines are re-checked via the shared `GateControlProber` (22a79ab, 3760266).

### `[plan]` (optional section) - Functional

Read by `ReadPlanSection` into `PlanConfig(string Mode)`; missing section returns `PlanConfig.Default` (`promote`) ([src/ThroughlineBuild.Cli/Config.cs:828-840](../../src/ThroughlineBuild.Cli/Config.cs#L828-L840), record at [:51-57](../../src/ThroughlineBuild.Cli/Config.cs#L51-L57)).

| Key | Default | Notes |
|---|---|---|
| `mode` | `"promote"` | Must be `"investigate"` or `"promote"` (case-insensitive); any other value throws `key 'mode' in [plan] must be either "investigate" or "promote", got "<v>"`, exit 2. |

Within `build chain`, `investigate` spawns a worker to investigate the ticket and write the plan, while `promote` bypasses the worker and promotes the ticket plan in place (no LLM/worker). Standalone `build plan` deliberately ignores this config setting and investigates; `build plan --from-brief` remains the explicit deterministic-promotion path. The shipped template sets `mode = "promote"` ([config.toml.template:247](../../src/ThroughlineBuild.Commands/Templates/config.toml.template#L247)); a local `.build/config.toml` may override it.

### `[batch]` (optional section) - Functional, NEW

Caps that gate the chain's batch-implement path (`build chain <id> --batch-implement [ids]`). Read by `ReadBatchSection` into `BatchConfig`; missing section returns `BatchConfig.Default`, and each cap must be a positive integer or the loader throws ([src/ThroughlineBuild.Cli/Config.cs:842-859](../../src/ThroughlineBuild.Cli/Config.cs#L842-L859); record with default rationale at [:59-75](../../src/ThroughlineBuild.Cli/Config.cs#L59-L75)).

| Key | Default | Meaning |
|---|---|---|
| `max_tickets` | `8` | Maximum tickets in a single batch session. |
| `max_size_score` | `16` | Maximum aggregate size score (S=1, M=2, L=4). |
| `max_description_bytes` | `200000` | Maximum total description HTML bytes across batch tickets (worker-context proxy). |

All caps are checked before the batch session starts, using only declared ticket metadata; exceeding any cap falls back to the proven per-ticket chain with a logged reason. The values flow into `BuildOptions.BatchMaxTickets` / `BatchMaxSizeScore` / `BatchMaxDescriptionBytes` ([src/ThroughlineBuild.Cli/Program.cs:1431-1433](../../src/ThroughlineBuild.Cli/Program.cs#L1431-L1433)).

### `[project]` (optional section, all keys optional) - Functional

Read by `ReadProjectSection` into `ProjectContext` - context handed to brief builders so the worker knows the stack it operates in; missing section returns `ProjectContext.Empty` ([src/ThroughlineBuild.Cli/Config.cs:861-936](../../src/ThroughlineBuild.Cli/Config.cs#L861-L936)).

| Key | Default | Notes |
|---|---|---|
| `language`, `framework`, `package_manager`, `build_command`, `test_command`, `install_command`, `dev_command`, `plane_project_url` | `""` | Flowed into brief context dictionaries. The template ships these blank on purpose - scaffold profile derivation fills them from the op-doc. |
| `notes_file` | `""` | Path to a file (relative to the config file dir, or absolute) whose contents are injected into the plan brief. Missing or unreadable emits a stderr warning and proceeds with empty notes. |
| `workflow_tool` | `"build"` | Must be `"build"` or `"claude-config"`; any other value: `ConfigException`, exit 2. |
| `convention_files` | `[]` | NEW (exp-2, ecca03c). Array of project-root-relative paths to stable convention files (the test harness/setup file the runner auto-loads, build/test config, one canonical test example) inlined into every implement brief so the worker does not re-read them. Contents are read lazily at brief-build from the live worktree, not at config load; blank entries are dropped ([src/ThroughlineBuild.Cli/Config.cs:905-912](../../src/ThroughlineBuild.Cli/Config.cs#L905-L912)). Normally derived at `build scaffold` and rendered into the config by `ConfigProfileWriter` ([src/ThroughlineBuild.Cli/ConfigProfileWriter.cs:140-167](../../src/ThroughlineBuild.Cli/ConfigProfileWriter.cs#L140-L167)). |
| `preload_context` | `true` | NEW (exp-2). Pre-loads named-input + convention file contents into the implement brief; set `false` to ablate the lever and restore the pre-preload brief ([src/ThroughlineBuild.Cli/Config.cs:914-915](../../src/ThroughlineBuild.Cli/Config.cs#L914-L915)). Note the default is ON. |
| `context_hygiene` | `false` | NEW (exp-4, e7ab4da). Opt-in effort-gated planning hygiene for S-effort implement briefs only: a lightweight-planning prompt line plus a restricted tool set (`--disallowedTools TodoWrite,Task` on claude-code); M and L briefs are untouched ([src/ThroughlineBuild.Cli/Config.cs:917-918](../../src/ThroughlineBuild.Cli/Config.cs#L917-L918)). |

`plane_project_url` is consumed only as brief context - it is injected into the plan/implement/review/decompose brief dictionaries as `project_plane_project_url` ([src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs](../../src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs) and the peer builders). It is NOT used to build the per-ticket browse URL in CLI summaries; that URL is built from `plane_base_url` + `plane_workspace_slug` via the `BuildPlaneUrl` helper ([src/ThroughlineBuild.Cli/Program.cs:2166-2173](../../src/ThroughlineBuild.Cli/Program.cs#L2166-L2173)).

### Loose ends

- **`backend` value is unchecked.** `ReadTicketingSection` reads it but never compares; any value loads as a Plane backend. Only `"plane"` is meaningful.
- **Unknown agent names that DO have a block silently fall back to Claude Code** - `WorkerAgentBuilder.Create`'s `_ =>` arm means `[workers.my-agent]` with `default_agent = "my-agent"` runs `ClaudeCodeAgent` against `executable = ...` without warning. (The undefined-agent case is now a hard error - TLB-512.)
- **`max_output_tokens` is honored only by Claude Code.** The template comments flag it as accepted-but-unused for the other three agents.
- **`workflow_tool` enum** is validated but unused at runtime - the value is stored on `ProjectContext` and flowed into brief context for the worker to read; nothing in code branches on it.
- **`notes_file`** path resolution is anchored at the config file's directory, not the project root - inconsistent with `events.log_directory` which anchors at project root, and with `convention_files` which anchor at the worktree root.
- **`effort` is accepted on every agent's tiers but consumed only by Codex** - a `[workers.claude-code.sizes] small = { model = "haiku", effort = "low" }` loads silently and the effort is ignored.
- **Scaffold-derived ownership is heuristic** - `ConfigProfileWriter.AlreadyConfiguredSkipReason` decides "looks customized" from the existing check shape; an operator's hand-written checks that resemble the pristine template can still be overwritten on re-scaffold without `--force-profile`.

---

## Environment variables

### Read by the binary

| Variable | Required for | What happens if unset |
|---|---|---|
| `PLANE_API_TOKEN` (or whatever `ticketing.plane_api_token_env` names) | every Plane operation | exit 3 `Secret error: plane_api_token not set in config and required environment variable '<name>' is not set` (`BuildConfigLoader.ResolveSecrets`, [src/ThroughlineBuild.Cli/Config.cs:182-196](../../src/ThroughlineBuild.Cli/Config.cs#L182-L196)) |
| `ANTHROPIC_API_KEY` (or whatever `llm.anthropic_api_key_env` names) | `close` / `defer` / `reopen` (reason translation) | resolved as an optional secret at load; those three verbs do not hard-fail when it is absent - they fall back to `EchoLlmClient` and record the reason verbatim ([src/ThroughlineBuild.Cli/Program.cs:2252-2262](../../src/ThroughlineBuild.Cli/Program.cs#L2252-L2262)) |
| `BUILD_PROGRESS` | optional - set to `1` to keep the progress digest on even when stderr is redirected | digest auto-suppresses when stderr is redirected and `BUILD_PROGRESS != 1`, to keep CI/script logs clean (checked wherever `enableDigest` is computed, e.g. [src/ThroughlineBuild.Cli/Program.cs:754-757](../../src/ThroughlineBuild.Cli/Program.cs#L754-L757), [:1213-1215](../../src/ThroughlineBuild.Cli/Program.cs#L1213-L1215), [:1414-1416](../../src/ThroughlineBuild.Cli/Program.cs#L1414-L1416)) |
| `EDITOR` (via `ReviewLoop.DefaultEditorResolver`) | the interactive `e` (edit) action in `build new ... --review` | falls back to a platform candidate chain (`vim`, `nano`, `code --wait`; on Windows also `notepad.exe`) ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-268](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L268)) |
| `CLAUDE_CONFIG_DIR` | interactive Claude transport state and transcript lookup | when unset, the transport uses the normal Claude home/profile locations |
| `HOME` / `USERPROFILE` | default Claude trust file and transcript root; `build.sh` install default | interactive Claude and local install paths cannot be resolved when the applicable home variable is absent |

One more indirect read: `build init --token-env NAME` reads `NAME` from the environment to obtain an effective token for connected-mode API calls ([src/ThroughlineBuild.Cli/InitCommand.cs:140-142](../../src/ThroughlineBuild.Cli/InitCommand.cs#L140-L142)).

### Set / removed by the binary in worker subprocesses

Each agent sanitizes its child environment to force subscription/OAuth auth rather than orchestrator-key auth, then applies any caller-supplied `EnvironmentVariables` last (so an explicit override wins). All four also pin child stdout/stderr decoding to UTF-8 (`ProcessStreamEncoding.ApplyUtf8`, TLB-439 - an encoding setting, not an env var).

| Agent | Action | Source |
|---|---|---|
| Claude Code | removes `ANTHROPIC_API_KEY`; sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from `max_output_tokens` when set | `ClaudeCodeAgent.ConfigureEnvironment` ([ClaudeCodeAgent.cs:463](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L463)) |
| Codex | removes `CODEX_API_KEY`, `OPENAI_API_KEY` | `CodexAgent` env sanitize ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:338-339](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L338-L339)) |
| Gemini | removes `GEMINI_API_KEY`, `GOOGLE_API_KEY` (falls back to ADC / gcloud); `max_output_tokens` reserved, no env equivalent applied | `GeminiAgent` env sanitize ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:285-286](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L285-L286)) |
| Copilot | additive only - inherits the `gh` keyring credential; caller may pass `GH_TOKEN` via `EnvironmentVariables` | `CopilotAgent` auth comment ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:192-200](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L200)) |

Any other env vars from the caller pass through unchanged. The `CodexModelProbe` subprocess (`codex debug models`) is read-only and inherits the parent environment unmodified ([src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs:50-53](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs#L50-L53)).

### Used by harness CLAUDE.md (not by `build` itself)

The user's global `CLAUDE.md` configures conventions like `bin/notify` for agent push notifications. Those are conventions for Claude Code sessions working in this repo; the `build` binary neither reads nor writes them.

### Loose ends

- **`GH_TOKEN`** is documented in the Copilot config comments but is never set by `build`; the operator (or a higher-level harness) must place it in the environment before invoking `build` if the `gh` keyring credential is absent.
- **Per-provider API keys are stripped, not read.** `build` never reads `OPENAI_API_KEY` / `GEMINI_API_KEY` / `GOOGLE_API_KEY` / `CODEX_API_KEY`; it only removes them from worker child processes. The only provider key `build` itself reads is the Anthropic key, and only for reason translation.

---

## Secrets

Two secrets, both resolved by `BuildConfigLoader.ResolveSecrets` ([src/ThroughlineBuild.Cli/Config.cs:182-196](../../src/ThroughlineBuild.Cli/Config.cs#L182-L196)):

1. **Plane API token.** Always required (every post-config verb hits Plane). Resolution: inline `plane_api_token`, else the env var named by `plane_api_token_env` (default `PLANE_API_TOKEN`). Missing: exit 3 at load.
2. **Anthropic API key.** Used only for `close` / `defer` / `reopen` reason translation. Resolution: inline `anthropic_api_key`, else the env var named by `anthropic_api_key_env`. Resolved as optional (`null` allowed) at load; even the three reason-translation verbs do not hard-fail if it is absent (see below). Worker phases reach their provider via the worker CLI's own auth, independent of `ANTHROPIC_API_KEY`.

#### Reason translation is the only LLM consumer

Reason translation is the only path in the deterministic CLI that constructs the direct Anthropic client, and it is fully optional. `WireUpConditionalCommands` only runs for `close`/`defer`/`reopen` ([src/ThroughlineBuild.Cli/Program.cs:2235-2298](../../src/ThroughlineBuild.Cli/Program.cs#L2235-L2298)); it tries `LlmClientFactory.Create`, and on `ConfigException` (no key, deprecated `default_model` unset, etc.) it logs `WARNING: LLM unavailable (...); recording reason verbatim without translation.` and substitutes an `EchoLlmClient` ([src/ThroughlineBuild.Cli/EchoLlmClient.cs](../../src/ThroughlineBuild.Cli/EchoLlmClient.cs)) that returns the last user message verbatim. The ticket state transition still runs. `ReasonTranslator.ModelId` pins `claude-haiku-4-5-20251001` ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs:16](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L16)). The old module-level `ANTHROPIC_API_KEY` hard gate is gone (TLB-227/TLB-371).

`.build/config.toml` is gitignored ([.gitignore:14](../../.gitignore#L14)) along with `secrets/` ([.gitignore:2](../../.gitignore#L2)). The `secrets/` directory is reserved and not read by any code path. The `build init` template writes the token inline by default (`REQUIRED_PLANE_API_TOKEN` placeholder) with the env-var line commented; `--token-env` writes the env-var indirection line instead, and `--from <creds-file>` / redirected stdin let automation supply the token without a shell history entry (see [02-install-build-run.md](02-install-build-run.md)).

### Loose ends

- **Secrets in `.build/config.toml`** are stored plaintext on disk. The template encourages inline by default; env-var indirection is supported but optional.
- **Credentials files for `build init --from`** are plaintext too, and nothing prompts the operator to delete them afterwards.

---

## Configuration sources outside `.build/`

### Global claude-config state

No project-local `.claude/plane-config.md`, `.claude/ticket-config.md`, or `.claude/commands/` files are tracked at HEAD. Any older slash-command configuration is installed outside this repository and is not a Throughline Build configuration source.

### `AGENTS.md`

A Codex-agent instruction file written to the workspace by the slash-command installer ([AGENTS.md](../../AGENTS.md)); read by the Codex agent harness, not by `build`.

### `.gitattributes`

LF-pinning of brief templates and snapshot test data ([.gitattributes:1-3](../../.gitattributes#L1-L3)). Influences how diffs and substitutions look but not runtime config behavior.

### `tests/Directory.Build.props` and `test.runsettings`

Tracked test-build configuration: defaults `dotnet test` to the repo's `test.runsettings` (quiet-on-green console logger) and conditionally imports the machine-local, gitignored root `Directory.Build.props` carrying AOT/MSVC linker paths ([tests/Directory.Build.props](../../tests/Directory.Build.props); see [02-install-build-run.md](02-install-build-run.md)).

### `throughline-build.sln`

Solution membership only. Adding a new `ThroughlineBuild.X` project requires editing this file and the corresponding `.csproj` references.

### Loose ends

- An external legacy slash-command installation can drift from `.build/config.toml`; this repository has no synchronization path.

---

## Configuration precedence

For secrets:

1. Inline value in `.build/config.toml` (e.g. `plane_api_token = "..."`, `anthropic_api_key = "..."`).
2. Environment variable named by the matching `*_env` key.
3. (Plane only) absent -> exit 3; (Anthropic) absent -> `null`, degraded to `EchoLlmClient` if a reason-translation verb runs.

For `build init` inputs: explicit flags > credentials file (`--from` or redirected stdin) > interactive prompts > template placeholders ([src/ThroughlineBuild.Cli/InitCommand.cs:96-122](../../src/ThroughlineBuild.Cli/InitCommand.cs#L96-L122)).

For agent / model selection per phase:

1. Per-phase CLI flag (`--agent-plan` / `--agent-implement` / `--agent-review`).
2. `--agent` (applies to all phases).
3. `[workers.phases]` mapping for that phase.
4. `[workers] default_agent`.

(`EffectiveAgentFor`, [src/ThroughlineBuild.Cli/Program.cs:1149-1153](../../src/ThroughlineBuild.Cli/Program.cs#L1149-L1153)). The model tier within an agent is then chosen by ticket size from that agent's `[workers.<name>.sizes]` map - there is no model-level CLI override.

For the `ship` push toggle, `--no-push || !Ship.Push` disables the push. Plan promotion is verb-aware: `--from-brief` explicitly promotes for `plan` or `chain`, while `Plan.IsPromote` applies only to `chain`.

For scaffold-derived config: `build scaffold` derivation defers to existing customized checks unless `--force-profile`; `--no-profile` skips derivation entirely ([src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs:47-56](../../src/ThroughlineBuild.Cli/ScaffoldProfileRunner.cs#L47-L56)).

For optional sections (`[llm]`, `[review]`, `[ship]`, `[work]`, `[plan]`, `[project]`, `[batch]`): a missing section is equivalent to an all-defaults section (each `Read*Section` returns its defaults when the table is absent).

---

## Loose ends

- **`backend`** is read but not validated; misconfiguration surfaces lazily. (`default_agent` is now validated at parse time - TLB-512 - but an unknown name with a block still silently falls back to Claude Code.)
- **`max_output_tokens`** is honored only by the Claude Code agent.
- **`workflow_tool`** is validated but never branched on.
- **`[llm] default_model`** is reason-translation-only and deprecated; it does not configure worker models (those come from `[workers.<name>.sizes]` tier tables).
- **Sizes schema hard-break is silent for old configs only at the error message level** - a pre-op-33 config with bare-string sizes fails loudly at load with the inline-table message; there is no automatic migration.
- **Plaintext secrets** in the config file are the documented default.
- **Non-fatal validation only, with growing exceptions.** Beyond TOML parse, required-section/key presence, the hard-break migration errors, the TLB-512 agent validation, and the TLB-544 claude-code model validation, the loader's extra validation is advisory: unknown keys (TLB-405) and a dangling `[work].target_branch` (TLB-410) emit `warning:` lines but do not fail the run. There is still no `build config check` verb; `build setup` covers the Plane-side half (`--check` keeps the Plane provisioning read-only) and now also runs a Claude transport capability preflight (executable, version, platform) that returns non-zero when a configured `interactive-hook` agent cannot run on this host.
- **No per-environment overlay** (no `config.local.toml`). Operators with multiple Plane workspaces hand-edit the file or use `build init --force` to regenerate.
