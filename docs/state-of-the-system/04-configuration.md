# 04 - Configuration and Environment

Every config file the binary reads, every environment variable it consults, every secret it requires, and whether each is required or optional.

For installation-time concerns see [02-install-build-run.md](02-install-build-run.md). For state files written at runtime see [05-state-and-persistence.md](05-state-and-persistence.md).

---

## `.build/config.toml`

The single source of operator-controlled configuration. Discovered by walking up from cwd looking for `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:60-71](../../src/ThroughlineBuild.Cli/Config.cs#L60-L71)). Missing file: exit 2 with `Config error: config file not found: searched from <cwd> upwards for .build/config.toml`.

Parsed by `Tomlyn` (NuGet `Tomlyn 0.16.0`) into the typed records `TicketingConfig`, `LlmConfig`, `WorkersConfig`, `EventsConfig`, `ReviewConfig`, `ShipConfig`, and `ProjectContext` at [src/ThroughlineBuild.Cli/Config.cs:73-104](../../src/ThroughlineBuild.Cli/Config.cs#L73-L104).

A template lives at [.build/config.toml.example](../../.build/config.toml.example).

### `[ticketing]` (required)

| Key | Required | Default | Source |
|---|---|---|---|
| `backend` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:179](../../src/ThroughlineBuild.Cli/Config.cs#L179) - value is read but only `"plane"` is supported (no other adapter exists). |
| `plane_base_url` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:180](../../src/ThroughlineBuild.Cli/Config.cs#L180) |
| `plane_workspace_slug` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:181](../../src/ThroughlineBuild.Cli/Config.cs#L181) |
| `plane_project_id` | yes | - | [src/ThroughlineBuild.Cli/Config.cs:182](../../src/ThroughlineBuild.Cli/Config.cs#L182) - UUID of the Plane project. |
| `plane_api_token_env` | no | `PLANE_API_TOKEN` | Name of the env var holding the token (when the token is not inline). |
| `plane_api_token` | no | - | Inline token. Takes precedence over env. |
| `plane_project_identifier` | no | `""` | E.g., `"TLB"`. Used as a filename component. |
| `plane_project_name` | no | `""` | E.g., `"throughline-build"`. Used as a filename component. |

A missing or empty required key throws `ConfigException` at load time; CLI exits 2 with `Config error: ...`.

### `[llm]` (optional section, optional keys)

If section absent, all defaults are empty strings ([src/ThroughlineBuild.Cli/Config.cs:189-197](../../src/ThroughlineBuild.Cli/Config.cs#L189-L197)).

| Key | Default | Use |
|---|---|---|
| `default_model` | `""` | Passed to `ClaudeCodeAgent` as the model id (`--model` flag) and used for direct Anthropic calls. Values like `"anthropic:claude-sonnet-4-6"`. |
| `anthropic_api_key_env` | `""` | Name of env var holding the Anthropic key. |
| `anthropic_api_key` | - | Inline key; takes precedence over env. |

### `[workers]` (required section)

| Key | Required | Default |
|---|---|---|
| `default_agent` | yes | - |
| `claude_code_executable` | yes | - (typically `"claude"`) |
| `timeout_minutes` | no | `30` |
| `max_output_tokens` | no | `32000` |

`default_agent` is read into `BuildOptions.WorkerName` and only one value (`"claude-code"`) is meaningful today; no agent registry exists ([src/ThroughlineBuild.Cli/Config.cs:199-207](../../src/ThroughlineBuild.Cli/Config.cs#L199-L207)). `max_output_tokens` becomes the `CLAUDE_CODE_MAX_OUTPUT_TOKENS` env var passed to the worker subprocess ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:378](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L378)).

### `[events]` (required section)

| Key | Required |
|---|---|
| `log_directory` | yes |

Resolved by `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:106-112](../../src/ThroughlineBuild.Cli/Config.cs#L106-L112)). If the value is relative, it is resolved against the project root (parent of `.build/`), **not** the config file's directory. Typical value: `.build/events`.

### `[review]` (optional section, sensible defaults)

| Key | Default |
|---|---|
| `verifier_timeout_minutes` | `15` |
| `verifier_allowed_tools` | `["Read", "Grep", "Glob"]` ([src/ThroughlineBuild.Cli/Config.cs:216-217](../../src/ThroughlineBuild.Cli/Config.cs#L216-L217)) |
| `[[review.checks]]` (array-of-tables) | empty list |

Each `[[review.checks]]` entry maps to a `CheckSpec(name, executable, arguments, timeout)` consumed by `AutomatedChecksRunner` during the review phase ([src/ThroughlineBuild.Cli/Config.cs:232-247](../../src/ThroughlineBuild.Cli/Config.cs#L232-L247)).

```
[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test"]
timeout_minutes = 5
```

### `[ship]` (optional section)

| Key | Default |
|---|---|
| `remote` | `"origin"` |
| `base_branch` | `"main"` |
| `delete_feature_branch` | `true` |
| `[[ship.regression_checks]]` | empty list |

Same `CheckSpec` shape as `review.checks` ([src/ThroughlineBuild.Cli/Config.cs:255-294](../../src/ThroughlineBuild.Cli/Config.cs#L255-L294)). Used by `ShipPhase` to gate the merge.

### `[project]` (optional section, all keys optional)

`ProjectContext` ([src/ThroughlineBuild.Cli/Config.cs:296-351](../../src/ThroughlineBuild.Cli/Config.cs#L296-L351)) - context handed to brief builders so the worker knows what stack it is operating in.

| Key | Default | Notes |
|---|---|---|
| `language`, `framework`, `package_manager`, `build_command`, `test_command`, `install_command`, `dev_command`, `plane_project_url` | `""` | Flowed into brief context dictionaries. |
| `notes_file` | `""` | Path to a file (relative to config file dir or absolute) whose contents are injected as `{{project_notes_section}}` in the plan brief. Missing or unreadable: warning to stderr, empty notes ([src/ThroughlineBuild.Cli/Config.cs:323-337](../../src/ThroughlineBuild.Cli/Config.cs#L323-L337)). |
| `workflow_tool` | `"build"` | Must be `"build"` or `"claude-config"` ([src/ThroughlineBuild.Cli/Config.cs:312-313](../../src/ThroughlineBuild.Cli/Config.cs#L312-L313)). Any other value: `ConfigException`, exit 2. |

`plane_project_url` is also consumed by `Program.cs` for building the per-ticket `browse/<id>/` URL in CLI summaries ([src/ThroughlineBuild.Cli/Program.cs:1110-1116](../../src/ThroughlineBuild.Cli/Program.cs#L1110-L1116)).

---

## Environment variables

### Read by the binary

| Variable | Required for | What happens if unset |
|---|---|---|
| `PLANE_API_TOKEN` (or whatever `ticketing.plane_api_token_env` names) | every Plane operation | exit 3 `Secret error: plane_api_token not set...` |
| `ANTHROPIC_API_KEY` (or `llm.anthropic_api_key_env`) | `close` / `defer` / `reopen` (via `ReasonTranslator`) | those verbs exit 3 `Secret error: anthropic api key required...` |
| `BUILD_PROGRESS` | optional - forces progress digest on even when stderr is redirected ([src/ThroughlineBuild.Cli/Program.cs:300, 659](../../src/ThroughlineBuild.Cli/Program.cs#L659)) | digest auto-suppresses to keep CI/script logs clean |
| `EDITOR` (via `ReviewLoop.DefaultEditorResolver`) | `build new ... --review` interactive `e` (edit) action | falls back to platform default; on Windows, eventually no editor is available |

### Set by the binary in worker subprocesses

| Variable | Value | Purpose |
|---|---|---|
| `ANTHROPIC_API_KEY` | **unset** | Force Claude Code OAuth instead of orchestrator key ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:374](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L374)) |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | `workers.max_output_tokens` from config | cap claude output ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:378](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L378)) |

Any other env vars from the caller pass through unchanged.

### Used by harness CLAUDE.md (not by `build` itself)

The user's global `CLAUDE.md` configures conventions like `bin/notify` for agent push notifications. Those are conventions for Claude Code sessions working in this repo; the `build` binary neither reads nor writes them.

---

## Secrets

Only two secrets, both required-by-context:

1. **Plane API token.** Always required (every verb hits Plane).
2. **Anthropic API key.** Required only for `close` / `defer` / `reopen`. Other verbs reach Anthropic via the `claude` CLI's own OAuth, which is independent of `ANTHROPIC_API_KEY`.

Both can be supplied inline in `.build/config.toml` (`plane_api_token`, `anthropic_api_key`) or via env. The example config at [.build/config.toml.example](../../.build/config.toml.example) shows inline by default; the comment marks the env-var alternative for CI.

`.build/config.toml` is gitignored ([.gitignore:11](../../.gitignore#L11)) along with `secrets/` ([.gitignore:2](../../.gitignore#L2)). The `secrets/` directory is empty in the working tree today; it is not read by any code path, just reserved.

---

## Configuration sources outside `.build/`

### `.claude/plane-config.md` and `.claude/ticket-config.md`

Read by the Claude Code `/ticket-*` slash commands, **not** by `build`. They duplicate some of the same data (workspace slug, project UUID, state UUIDs, label UUIDs, test/build commands) in a markdown format the harness can parse. These files exist because the older claude-config workflow runs in the same repo and operators still invoke `/ticket-*` from chat.

### `.gitattributes`

LF-pinning of brief templates and snapshot test data. Influences how diffs look but not runtime behavior.

### `throughline-build.sln`

Solution membership only. Adding a new `ThroughlineBuild.X` project requires editing this file and the corresponding `.csproj` references.

---

## Configuration precedence

1. Inline value in `.build/config.toml` (e.g., `plane_api_token = "..."`).
2. Environment variable named by `*_env` key.
3. Default constant in `Config.cs`.

For `[review]` / `[ship]` / `[llm]` / `[project]` sections, missing section is equivalent to an all-defaults section ([src/ThroughlineBuild.Cli/Config.cs:191-197, 220-227, 257-264, 298-299](../../src/ThroughlineBuild.Cli/Config.cs#L298-L299)).

---

## Loose ends

- **`backend` value is unchecked.** [src/ThroughlineBuild.Cli/Config.cs:179](../../src/ThroughlineBuild.Cli/Config.cs#L179) reads it but never compares - any value loads as a Plane backend.
- **`default_agent` value is unchecked.** No registry lookup; the field flows into `BuildOptions.WorkerName` but the only `IWorkerAgent` actually constructed is `ClaudeCodeAgent` regardless.
- **`workflow_tool` enum** is validated but unused at runtime - the value is stored on `ProjectContext` and flowed into brief context for the worker to read; nothing in code branches on it.
- **Secrets in `.build/config.toml`** are stored plaintext on disk. The example config encourages this. Env-var indirection is supported but not the default.
- **No config validation step** beyond TOML parse + section presence. There is no `build config check` verb to confirm the token is valid against Plane.
- **`notes_file`** path resolution is anchored at the config file's directory, not the project root - inconsistent with `events.log_directory` which anchors at project root ([src/ThroughlineBuild.Cli/Config.cs:319-321 vs :106-112](../../src/ThroughlineBuild.Cli/Config.cs#L319-L321)).
- **No per-environment config** (no `config.local.toml` overlay). Operators with multiple Plane workspaces edit the file by hand.
