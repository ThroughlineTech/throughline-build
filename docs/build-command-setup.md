# Setting Up and Running the `build` Command

This guide walks a new contributor through configuring the `build` CLI from scratch and running their first ticket plan.

---

## How the CLI finds its config

The `build` command walks **upward from your current working directory** looking for `.build/config.toml`. This means you can run it from anywhere inside the repo tree - you do not need to `cd` into a specific directory first.

There are two distinct config concerns:

| Concern | File | Contains |
|---|---|---|
| Non-secret settings | `.build/config.toml` | URLs, project IDs, env var names |
| Secrets | Your shell environment | API tokens (never committed) |

---

## Step 1 - Create `.build/config.toml`

Copy the example file and fill in your values:

```bash
cp .build/config.toml.example .build/config.toml
```

For this project the values are in `.build/config.toml.example` - copy and use them as-is. The placeholders you will see there (`your-workspace`, `uuid-of-your-project`) must be replaced with the real values from the project's Plane workspace. Ask a teammate for the correct UUIDs if you do not have them.

The config shape looks like this:

```toml
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "your-workspace"
plane_project_id = "uuid-of-your-project"
plane_api_token_env = "PLANE_API_TOKEN"

[llm]
default_model = "anthropic:claude-opus-4-7"
anthropic_api_key_env = "ANTHROPIC_API_KEY"

[workers]
default_agent = "claude-code"
timeout_minutes = 30

[workers.claude-code]
executable = "claude"

[events]
log_directory = ".build/events"
```

The `*_env` fields name environment variables - they do not hold secret values directly. The actual tokens go in your shell (Step 2).

The `[llm]` section is parsed but not consumed by the current plan-only CLI. It is kept in the config so future phases that wire in `AnthropicClient` (judgment slots, etc.) can read it without a config schema change. You do not need to set `ANTHROPIC_API_KEY` for `build plan` to work.

`.build/config.toml` is gitignored. Do not commit it.

---

## Step 2 - Set your API tokens

The CLI reads `PLANE_API_TOKEN` at startup. If it is missing the CLI exits immediately with exit code 3 and a clear error message naming the variable.

`ANTHROPIC_API_KEY` is **not** required for `build plan`. The plan phase dispatches a Claude Code subprocess as its worker; that subprocess uses Claude Code's own OAuth credentials, not a raw API key. The CLI explicitly strips `ANTHROPIC_API_KEY` from the subprocess environment so Claude Code always uses the OAuth path regardless of what is set in the parent shell.

**On macOS / Linux / Git Bash:**

```bash
export PLANE_API_TOKEN=your-plane-token-here
```

Add this to your shell profile (`~/.zshrc`, `~/.bashrc`, etc.) so it persists across sessions.

**On Windows (PowerShell):**

```powershell
$env:PLANE_API_TOKEN = "your-plane-token-here"
```

To persist it across sessions, add to your PowerShell profile or use System > Environment Variables in Windows settings.

### Where to get the token

- **PLANE_API_TOKEN** - Log into Plane, go to Profile > API Tokens, create a personal token.

---

## Step 3 - Verify your setup

Run the help command from the repo root. If config is wrong or `PLANE_API_TOKEN` is not set you will see a specific error; if everything is correct you will see the usage text:

```bash
dotnet run --project src/ThroughlineBuild.Cli -- --help
```

Expected output:

```
build - Throughline Build

Usage:
  build plan <ticket-id>    Run the plan phase for a ticket
  build --help              Show this help

Exit codes:
  0  Success
  1  Phase failure
  2  Config error
  3  Missing secret (env var not set)
```

---

## Step 3b - Verify your Claude Code session

The plan phase dispatches a Claude Code subprocess as its worker. That subprocess authenticates via Claude Code's own OAuth session, not via `ANTHROPIC_API_KEY`. If you have never logged in, the worker will fail with a confusing authentication error rather than a clear "token missing" message.

Verify your session is active:

```bash
claude --version
```

If that prints a version number you are good. If it fails or prompts for auth, run:

```bash
claude login
```

and follow the browser flow. You only need to do this once per machine.

---

## Step 3c - Provision the Plane project (`build setup`)

`build init` writes your config but does **not** touch the Plane project. The workflow assumes the project carries a specific set of states and labels; the `build` binary resolves them by name at runtime and **hard-fails** when a required label is missing (`Label 'risk:low' not found in Plane project`) and warns when a required state is missing. A brand-new Plane project has neither, so run `setup` once after `init`:

```bash
dotnet run --project src/ThroughlineBuild.Cli -- setup
```

This creates any missing states (Backlog, Planning, Ready, In Progress, In Review, Done, Cancelled) and labels (`risk:low|medium|high`, `size:s|m|l`, `plan-ticket`, `stub`, `delegated`). It is **idempotent** - a project that already meets criteria is left untouched, and re-running prints `Plane project meets criteria: ...`.

To verify without mutating anything (e.g. in CI), use `--check`:

```bash
dotnet run --project src/ThroughlineBuild.Cli -- setup --check
```

`--check` exits `0` when the project meets criteria and `1` (listing the gaps on stderr) when it does not, creating nothing.

The end-to-end path on a fresh project is: `build init` (enter config) -> `build setup` -> `build new "..."` -> `build chain <id>`.

---

## Step 4 - Run a plan

The plan phase sends a structured brief to a Claude Code worker, which investigates the codebase and writes an investigation + implementation plan back into the ticket description on Plane, applying `risk:*` and `size:*` labels and transitioning the ticket to Ready.

The commands below use `dotnet run --project` which runs from source without a publish step. Once you have a published `build` binary on your `PATH` you can use `build plan <ticket-id>` directly instead.

Pick a ticket in Backlog state (e.g., `TLB-99`) and run:

```bash
dotnet run --project src/ThroughlineBuild.Cli -- plan TLB-99
```

What happens:

1. The CLI fetches the ticket from Plane (title, description, relations).
2. It builds a structured brief and dispatches a `claude-code` worker agent.
3. The worker investigates the codebase and writes an implementation plan back to the ticket.
4. On success the CLI prints: `Plan complete: TLB-99 risk=low size=m`
5. Event logs land in `.build/events/` as JSONL files named `<project>-<ticket>-<verb>-<yyyy-MM-dd>-<HHmmss>.jsonl` (see [docs/event-log-format.md](event-log-format.md#file-location-and-naming)).

The `risk=` and `size=` values in the success message are the raw Plane label names (`risk:low`, `size:m`, etc.), which use lowercase. The investigation workflow uses uppercase `S / M / L` for sizing tier references, but the stored label strings and CLI output are always lowercase.

### Cancelling a run

Press `Ctrl+C` once. The CLI catches the signal, cancels the worker gracefully, and exits with code 1.

---

## Troubleshooting

| Error message | Cause | Fix |
|---|---|---|
| `config file not found: searched from ... upwards` | No `.build/config.toml` exists | Complete Step 1 |
| `missing required TOML section [ticketing]` | Config file is malformed or incomplete | Check all four sections are present |
| `plane_api_token not set in config and required environment variable '...' is not set` | No token in config and env var also missing | Add `plane_api_token = "..."` to `.build/config.toml` (Step 1) |
| `Ticket not found` | Ticket ID does not exist in the configured project | Verify the ID in Plane; confirm `plane_project_id` in config matches |
| `Phase failure` | Worker agent returned a non-success verdict | Check `.build/events/` for the session log; the JSONL file has per-step detail |
| `Phase failure` with auth errors in the event log | Claude Code OAuth session not active or expired | Run `claude --version`; if it fails or prompts for login, run `claude login` (Step 3b) |

---

## What NOT to configure here

- `.claude/plane-config.md` - used by Claude Code slash commands (`/ticket-list`, `/ticket-investigate`, etc.), not by the `build` CLI.
- `.claude/secrets*` - written by `/ticket-install` for the slash command workflow; the `build` CLI does not read these files.

These two systems share the same Plane project but have separate config paths.
