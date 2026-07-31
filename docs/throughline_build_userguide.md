# Throughline Build - Getting Started

Throughline Build (`build`) is a CLI that drives AI coding agents through a plan-implement-review-ship
cycle, using Plane as the ticketing backend. This guide covers installation, configuration, and
running your first ticket end-to-end.

> **Config reference:** run `build init --print-template` to print a fully-commented config with every
> supported key to stdout. This repository does not currently publish prebuilt binaries; see
> [Building from source](build-command-setup.md).

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
Prefer keeping the value in an environment variable and writing only its name to config with
`build init --token-env PLANE_API_TOKEN`. A literal `plane_api_token` is supported but must never
be committed.

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
  from Claude's own persisted transcript. **Requires Claude Code >= 2.1.177**, which `build setup`
  preflights. Generated configs set this explicitly; a `[workers.claude-code]` block that omits
  `transport` also resolves to it.
- `print` (rollback) - the legacy headless path: `claude --print --verbose --output-format stream-json`.
  Select it with `transport = "print"` to return to the pre-cutover behavior.

The interactive transport launches interactive Claude without `--print`; which invocation mode draws on
which usage allowance is controlled by Anthropic's current policy, not by this tool.

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

## Parent tickets

Commands detect a parent by querying its direct children. The current behavior is:

| Command | Behavior when the named ticket has children |
|---|---|
| `plan` | Refuses; plan the children instead. |
| `implement` | Refuses; implement the children instead. |
| `review` | Aggregates direct-child states. Any child in InProgress or InReview produces Rework and moves the parent to InProgress; all children Done produces Pass; every other mix produces Fail. |
| `ship` | Requires every direct child to be Done, then moves the parent to Done. It stops without transitioning the parent if any child is not Done. |
| `chain` | Recurses through non-terminal children, skipping Done and Cancelled children. Sibling dependencies determine ordering; `--max-depth` limits traversal and `--dry-run` previews the post-order schedule. The source repository includes `docs/build-grandparent-chain.md` for implementation details. |
| `decompose` | Has no parent-specific guard and creates additional direct children. Check existing children before running it again. |
| `close` / `defer` | Attempts the lifecycle transition on each non-terminal direct child, then on the parent. Use `--no-cascade` to affect only the named ticket. |
| `reopen` | Reopens only the parent and prints a note; children keep their current states. |
| `rework` | Requires InProgress, then uses the implement path; a parent is therefore rejected by implement's child check. |
| `amend` | Changes only the named ticket. `--parent` reparents that ticket explicitly. |

## First Ticket Walkthrough

This sequence takes a repository from zero configuration to a shipped ticket. Replace the uppercase
placeholders and use the ticket ID that `build new` prints.

### 1. Set the Plane token

macOS, Linux, or Git Bash:

```bash
export PLANE_API_TOKEN="your-token"
```

PowerShell:

```powershell
$env:PLANE_API_TOKEN = "your-token"
```

### 2. Initialize configuration

```
build init --no-interactive \
  --plane-url PLANE_URL \
  --workspace WORKSPACE_SLUG \
  --project-id PROJECT_UUID \
  --token-env PLANE_API_TOKEN
```

On PowerShell, enter the command on one line or use PowerShell backticks instead of backslashes.
This writes `.build/config.toml` without putting the token value in the file. Running `build init`
without `--no-interactive` at a terminal instead starts the create-or-pick Plane project flow.

### 3. Verify configuration and provision the project

Check that `.build/config.toml` has no `REQUIRED_` placeholders and that `default_agent` under
`[workers]` and `executable` under the matching `[workers.<agent>]` block match the worker CLI
you installed. Then run:

```
build setup
```

Setup initializes local repository support, adds the managed ignore rules, provisions the required
Plane states and labels, checks connectivity, and preflights configured Claude transports. It is
safe to run again. Keep `.build/config.toml` ignored even when it contains only an environment
variable name.

### 4. Create a ticket

```
build new "fix the README typo"
```

You should see a ticket ID printed to stdout (e.g. `TLB-1`). Open Plane and confirm the ticket
appears in your project with state Backlog and the title you supplied.

For machine-created tickets, `build new - --json` accepts a strict JSON draft. Relations use
`{"kind":"blocked_by","targetId":"TLB-1"}` objects. Every relation type and target is validated
before the ticket is created, so an unknown target leaves no new ticket. Plane cannot atomically
create a ticket and its relation edges: if a later relation POST fails, the error names the created
ticket and earlier edges may exist. Inspect them with `build relate TLB-N --list`.

Manage existing relations explicitly:

```
build relate TLB-2 blocked_by TLB-1 --json
build relate TLB-2 --list --json
build relate TLB-2 --remove RELATION-ID --json
```

The list includes the stable relation ID required for exact removal. Allowed types are `relates_to`,
`duplicate`, `blocked_by`, `blocking`, `start_before`, `start_after`, `finish_before`, `finish_after`,
`implemented_by`, and `implements`; spaces and hyphens are accepted in place of underscores.

### 5. Plan the ticket

```
build plan TLB-1
```

You should see the plan phase complete with a success message. Open Plane and confirm the ticket
state has moved to Ready.

For chained runs (`build chain TLB-1`, or multiple ticket IDs), start from a clean main checkout.
Tracked changes in the main worktree are refused before planning; commit, stash, or revert them
first. Untracked files do not block the chain.

### 6. Implement the ticket

```
build implement TLB-1
```

You should see the implement phase complete with a success message. Open Plane and confirm the ticket
state has moved to InReview.

### 7. Review the ticket

```
build review TLB-1
```

You should see a `Pass` verdict. If you see `Rework` instead, run `build rework TLB-1` and then
re-run `build review TLB-1`; repeat until you see `Pass`.

### 8. Ship the ticket

```
build ship TLB-1
```

You should see a fast-forward merge on the configured target branch and a success message. When the
configured remote exists, ship pushes by default; use `--no-push` (or `[ship] push = false`) for a
local-only ship. Open Plane and confirm the ticket state has moved to Done.

### 9. Confirm the commit

```
git log --oneline -1
```

You should see the commit for your ticket at HEAD. Setup is complete.

## Bring your own conductor

To keep one long-lived agent session in charge of implementation and review, use
the ticket CRUD verbs together with deterministic worktree leases:

```
build worktree lease --ticket TLB-1 --slug readme-fix
build waves --input tickets.json
build gate --ticket TLB-1
build worktree list
build worktree teardown --ticket TLB-1
```

`worktree`, `gate`, and `waves` load repository configuration without resolving
ticketing secrets or constructing a Plane client.

Lease prints the absolute worktree path for use as an agent working directory,
runs `[project].install_command`, and writes a safety manifest. Configure the
root and the only untracked local files Build may copy with `[worktree] root`
and `[worktree] seed_files`. Use `--require-seed <path>` when a listed file must
exist before lease creation. Concurrent attempts for one ticket are serialized,
and rollback removes only branch and worktree artifacts owned by the failing
attempt. All three forms support `--json`.

Run `build gate` from the leased worktree to execute its configured
`[[review.checks]]`. Setup checks run first. Gating and setup failures exit 1;
advisory failures remain visible but do not change the exit code. Use
`--role gating|advisory|all` to select a role, and `--json` for typed per-check
exit codes, durations, captured output, and inconclusive missing-path results.

Use `build waves --input <path|->` before leasing worktrees to level declared
dependencies and pack file-disjoint ready tickets up to `[waves].cap` (default
2). Exact-file overlap and uncertain or empty file predictions serialize
automatically. Repository-specific `global`, `cohesive-module`, and `pairwise`
path rules belong in `[[waves.serialize]]`; no repository paths are built into
the command. Output names the rule and path for each serialization decision
and reports estimated speedup. JSON input and output shapes, glob semantics,
and exit codes are documented in `docs/bring-your-own-conductor.md`.

See `docs/bring-your-own-conductor.md` for the manifest safety model, exit codes,
and a complete caller-owned conductor sequence.
