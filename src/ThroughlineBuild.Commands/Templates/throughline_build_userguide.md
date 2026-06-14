# Throughline Build - Getting Started

Throughline Build (`build`) is a CLI that drives AI coding agents through a plan-implement-review-ship
cycle, using Plane as the ticketing backend. This guide covers installation, configuration, and
running your first ticket end-to-end.

> **Config reference:** run `build init --print-template` to print a fully-commented config with every
> supported key to stdout. For binary downloads and release notes see
> https://github.com/danrichardson/latticeflow/releases.

## Prerequisites

### Install

**git** - Any recent version. Confirm it is on your PATH with `git --version`. The `build ship`
command performs a local fast-forward merge using git.

**Worker agent CLI** - Install exactly one of the following; it must be on your PATH before running
any ticket phase command:

- `claude` (claude-code) - confirm with `claude --version` (the default interactive-hook transport
  needs Claude Code >= 2.1.177; see "Claude Code transport" below)
- `codex` - confirm with `codex --version`
- `gemini` - confirm with `gemini --version`
- `copilot` - confirm with `copilot --version`

### Gather

Have the following values ready before running `build init`. Each one maps to a field that
`build init` writes into `.build/config.toml`.

**Plane base URL** - The root URL for the Plane API. For Plane Cloud this is `https://api.plane.so`;
for a self-hosted instance use your server root. Written to `plane_base_url` in config.

**Workspace slug** - The short identifier that appears in your Plane workspace URL
(e.g. `my-org` in `app.plane.so/my-org/settings`). Written to `plane_workspace_slug` in config.

**Project ID** - The UUID of the Plane project you want tickets created in. Find it in
Plane under Project Settings > General. Written to `plane_project_id` in config.

**Plane API token** - A personal API token from your Plane profile (Settings > API Tokens).
Written to `plane_api_token` in config; alternatively supply via environment variable using
`plane_api_token_env`.

**Default agent name** - The agent key used for plan, implement, review, and rework phases
(e.g. `claude-code`). Must match a `[workers.<name>]` block in config. Written to `default_agent`
under `[workers]`.

**Agent executable** - The command name on your PATH for the agent CLI you chose above
(e.g. `claude` for claude-code, `codex` for codex). Written to `executable` under the agent block.

## Claude Code transport (interactive-hook)

When the worker agent is `claude-code`, `build` runs Claude through one of two transports, selected by
the `transport` key under `[workers.claude-code]`:

- `interactive-hook` (default) - launches an interactive Claude Code session in a terminal host
  (ConPTY on Windows, a PTY on Unix); Claude's argv never contains `--print`. The phase result is read
  from Claude's own persisted transcript. **Requires Claude Code >= 2.1.177.**
- `print` (rollback) - the legacy headless path: `claude --print --verbose --output-format stream-json`.

The transport launches interactive Claude without `--print`; which invocation mode draws on which usage
allowance is controlled by Anthropic's current policy, not by this tool.

### Verify your setup

Run `build setup`: it provisions Plane and then runs a Claude transport preflight. The preflight reports
each Claude agent's transport and whether this host can support it - it checks that `claude` is runnable,
that `claude --version` meets the supported minimum for interactive-hook, and that the platform is
supported. If the selected transport cannot run, the preflight fails clearly here, and any phase you
start then fails before doing work rather than silently falling back to `print`. A transport-capability
failure is distinct from a provider quota, model, permission, or WORKER_RESULT protocol failure, and the
message says which it is.

### Roll back to the print transport

To return to the legacy headless path, change exactly one value in `.build/config.toml`:

```
[workers.claude-code]
transport = "print"
```

No other change is required. Rolling back restores the `claude --print` invocation; the interactive
transport launches interactive Claude without `--print`. Usage/billing classification is governed by
Anthropic's current policy in either mode.

## First Ticket Walkthrough

This sequence takes a repository from zero configuration to a shipped ticket. Replace `TLB-1`
throughout with the ticket ID that `build new` prints in step 3.

### 1. Initialize configuration

```
build init
```

You should see `.build/config.toml` written to your project root. Do not run any other `build`
command until you complete step 2; every other verb reads config at startup and will error on
unfilled `REQUIRED_` values.

### 2. Fill in config values

Open `.build/config.toml` in your editor and replace every `REQUIRED_` placeholder:

- `plane_base_url` - your Plane base URL
- `plane_workspace_slug` - your workspace slug
- `plane_project_id` - your project UUID
- `plane_api_token` - your API token

Also confirm that `default_agent` under `[workers]` and `executable` under the matching
`[workers.<agent>]` block reflect the agent CLI you installed. Save the file before continuing.

### 3. Create a ticket

```
build new "fix the README typo"
```

You should see a ticket ID printed to stdout (e.g. `TLB-1`). Open Plane and confirm the ticket
appears in your project with state Backlog and the title you supplied.

### 4. Plan the ticket

```
build plan TLB-1
```

You should see the plan phase complete with a success message. Open Plane and confirm the ticket
state has moved to Ready.

For chained runs (`build chain TLB-1`, or multiple ticket IDs), start from a clean main checkout.
Tracked changes in the main worktree are refused before planning; commit, stash, or revert them
first. Untracked files do not block the chain.

### 5. Implement the ticket

```
build implement TLB-1
```

You should see the implement phase complete with a success message. Open Plane and confirm the ticket
state has moved to InReview.

### 6. Review the ticket

```
build review TLB-1
```

You should see a `Pass` verdict. If you see `Rework` instead, run `build rework TLB-1` and then
re-run `build review TLB-1`; repeat until you see `Pass`.

### 7. Ship the ticket

```
build ship TLB-1
```

You should see a fast-forward merge on your local main branch and a success message. Open Plane and
confirm the ticket state has moved to Done.

### 8. Confirm the commit

```
git log --oneline -1
```

You should see the commit for your ticket at HEAD. Setup is complete.
