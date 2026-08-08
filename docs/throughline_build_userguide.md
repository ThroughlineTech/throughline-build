# Throughline Build - Getting Started

Throughline Build (`build`) is a CLI that drives AI coding agents through a plan-implement-review-ship
cycle, using Plane as the ticketing backend. This guide covers installation, configuration, and
running your first ticket end-to-end.

> **Config reference:** run `build init --print-template` to print a fully-commented config with every
> supported key to stdout. This repository does not currently publish prebuilt binaries; see
> [Building from source](build-command-setup.md).

## Prerequisites

### Install run-backlog into a repository

After putting `build` and Git on PATH and making the Plane token available,
run the same three-stage sequence in every repository:

```sh
build install
build install --profile .build/profile.json
build install --invariants .build/invariants.toml
```

On a fresh repository, the first command forwards init configuration for a
fully non-interactive run. For example:

```sh
build install --no-interactive --plane-url URL --workspace SLUG --project-id UUID --token-env PLANE_API_TOKEN
```

With no existing config, a bare redirected `build install` stops before writing
placeholders and prints this required command instead.

Each of the first two commands emits a canonical prompt and stops. Give that
prompt to an agent that can read the repository, save only the requested JSON
or TOML, and pass the file to the next invocation. The command never starts a
worker itself. The final invocation prepares a non-protected run branch,
commits only exact installer-owned readiness paths (a deterministic Setup-owned
`.gitignore` when created or changed, plus catalog-emitted host stubs), and
prints READY only after the run-backlog structural preflight passes. It
intentionally does not install target-stack dependencies or require a green
target build during installation.

The sequence is re-runnable on an already-installed repository whatever its
toolchain: a profile that already matches config is a no-op success, and a
second pass adds no commit or branch change. Stage 2 refuses only when the
profile would overwrite `[[review.checks]]` or `[[ship.regression_checks]]`
that are already in config and say something else - checks a human wrote. Pass
`build install --profile .build/profile.json --force` to replace them
deliberately.

If stage 2 cannot install a binary-hosted SOP because an emitted stub differs
from the catalog, it rolls back the profile-config and generated-conductor
changes from that stage. Build preserves the local stub. Resolve that stub
according to your intent, then rerun the command named in the failure message,
normally `build install --profile .build/profile.json`; no hand-editing of
`.build/config.toml` or `.build/conductor.toml` is required.

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

For a human developer, the default path is a file: point `plane_api_token_file` at it (the
conventional path is `secrets/plane-api-token`, already reserved in the generated `.gitignore`).
Unlike an environment variable, a file's contents do not depend on which shell launched `build` -
bash only sources `~/.bashrc` for interactive shells, `zsh -c` does not read `~/.zshrc`, and an
editor or agent harness launched without a login shell inherits neither - so a token exported only
from an rc file is invisible to a non-interactive process even though it works in your own
terminal. Once any other source already resolves the token, write it out with
`build setup --write-token-file secrets/plane-api-token`; that command never prints the token to
stdout, stderr, or any log.

For CI, prefer an environment variable and write only its name to config with
`build init --token-env PLANE_API_TOKEN`; CI sets it directly as part of the pipeline, not via an
interactive shell, so this path is unaffected by the above. A literal `plane_api_token` is
supported but must never be committed. Resolution order: `plane_api_token`, then
`plane_api_token_env`, then `plane_api_token_file`, then failure - and the failure message names
every source it tried.

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

Ticket reads expose the same stable IDs that operators type. `build get --json` includes a
`children` array with each direct child's ID, title, and state, and `parentId` values are stable
ticket IDs such as `TLB-42` rather than Plane UUIDs. `build list --parent` accepts stable ticket IDs
and legacy Plane UUIDs.

## Structured evidence comments

Use `build evidence add` to record one structured audit entry without changing
ticket lifecycle state:

```
build evidence add --ticket TLB-541 --kind claim --claim "implemented the fix" --candidate-sha SHA --fingerprint HASH --json
build evidence add --ticket TLB-541 --kind review --run-head-sha SHA --verdict Pass --fingerprint HASH --json
```

Supported kinds are `claim`, `review`, `commit`, `integrate`, `gate`, and
`final`. Kind-specific fields are validated before any backend write. A
successful invocation posts exactly one comment, reads it back by id, and
reports the read-back evidence. `readBackVerified: true` means only that the
returned id is present in the read-back list; it does not compare stored comment
content with the submitted body. If the post succeeds but read-back fails, the
command reports the comment id and does not retry; inspect `build comments`
before trying again.

This command is an audit entry only. It never closes, defers, reopens, or
transitions a ticket. Use explicit lifecycle commands separately, and never use
evidence as a cascading close or transition shortcut.

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
safe to run again. `.build/config.toml` is tracked, not ignored - it carries repository facts like
`[[review.checks]]` that must travel with a clone - but it should never hold a literal token even
though it is safe to commit when it only names an environment variable. `sop doctor` and `setup
--check` both flag a literal `plane_api_token` before you commit it.

If Plane returns 401 or 403, `build` reports the repository-local config path, repository root,
workspace, and project that were used for the request. The message also reminds you that sibling
repositories may select different `.build/config.toml` files and recommends rerunning connected
`build init` for this repository. Token values and Plane response bodies are not echoed.

If tickets will also run from a non-interactive shell later - an agent harness, a scheduled job, an
editor launched without a login shell - persist the token now, in this same terminal where it
already resolves:

```
build setup --write-token-file secrets/plane-api-token
```

This writes the token this run already resolved (from step 1's environment variable, here) to that
file and sets `plane_api_token_file` in config, without ever printing the token itself. A
non-interactive process reads the file the same way regardless of its own shell, so it no longer
matters whether that later process's shell ever sourced the rc file where you originally exported
`PLANE_API_TOKEN`.

### 4. Create a ticket

```
build new "fix the README typo"
```

You should see a ticket ID printed to stdout (e.g. `TLB-1`). Open Plane and confirm the ticket
appears in your project with state Backlog and the title you supplied.

For machine-created tickets, `build new - --json` accepts a strict JSON draft. The required field
is `title`. Optional fields are `description`, `acceptanceCriteria`, `labels`, `parent`,
`relations`, and `type`; unknown fields are rejected. `acceptanceCriteria` is one Markdown string,
not an array, so use checklist Markdown such as `- [ ] first criterion`. Markdown fields are
rendered to Plane HTML before create.

`type` is optional and backend-dependent. Omitting it sends no explicit type assignment and performs
no work-item-type lookup; there is no implicit `task` type. When supplied, it is resolved through
Plane work-item types and the create request sends the type UUID expected by Plane. Projects that do
not expose work-item types can still create tickets by omitting `type`.

Relations use `{"kind":"blocked_by","targetId":"TLB-1"}` objects. Every relation type and target is
validated before the ticket is created, so an unknown target leaves no new ticket. Plane cannot
atomically create a ticket, assign a parent, and create relation edges: if a parent or later relation
write fails, the error names the created ticket and earlier writes may exist. Inspect them with
`build relate TLB-N --list`.

Successful JSON creates return the legacy top-level `id`, `uuid`, `labels`, `parent`, and
`relations` fields, plus `requested` for the normalized request and `ticket` for the persisted Plane
read-back after parent and relation writes. Use `ticket` when a caller needs the canonical state,
body, labels, type display name, parent, and relations that Plane stored.

Manage existing relations explicitly:

```
build relate TLB-2 blocked_by TLB-1 --json
build relate TLB-2 --list --json
build relate TLB-2 --remove RELATION-ID --json
```

The list includes the stable relation ID required for exact removal. Allowed types are `relates_to`,
`duplicate`, `blocked_by`, `blocking`, `start_before`, `start_after`, `finish_before`, `finish_after`,
`implemented_by`, and `implements`; spaces and hyphens are accepted in place of underscores.
For a new ticket A and target B, use `blocked_by` for "A depends on B", `blocking` for "A blocks B",
`duplicate` for "A duplicates B", and `relates_to` for a loose relationship. Plane may display
inverse edges from the target side; the create request is written from A toward B.

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
build worker brief --ticket TLB-1 --role review --worktree <path> --output .build/review.md
build candidate status --ticket TLB-1 --base main --json
build worktree list
build worktree teardown --ticket TLB-1
```

`worktree`, `gate`, `waves`, and `candidate status` load only the configuration
sections they use without requiring `[ticketing]`, `[workers]`, or `[events]`,
resolving ticketing secrets, or constructing a Plane client. Other commands
still require the full ticketing, worker, and event configuration.

Repositories that use binary-hosted SOPs keep `.build/conductor.toml` ignored
and machine-local. Recreate it in each clone through `build install` or
`build sop install`.
Run `build sop list [--json]` to report embedded SOPs and their binary versions.
Run `build sop install [--sop <name>] [--host claude|codex] [--json]` to emit
host stubs, scaffold a missing `.build/conductor.toml`, and write
`.build/sop-manifest.json`. By default install emits every known host stub;
`--host` narrows emitted stubs to Claude or Codex while still including shared
scaffolded paths. Run `build sop status [--json]` to report catalog drift,
including missing installed paths. Run
`build sop upgrade [--sop <name>] [--host claude|codex] [--json]` after
replacing the binary; it rewrites only emitted files that still match trusted
previous catalog hashes embedded in the current binary. Run
`build sop uninstall [--sop <name>] [--host claude|codex] [--json]` to remove
only emitted regular files that still match the current catalog.

The embedded catalog is the authority. `.build/sop-manifest.json` is a cache of
prior writes, not permission to touch arbitrary paths. Emitted files are stubs
and are validated byte-for-byte against the catalog. Scaffolded files are owned
as paths, not content: install never overwrites an existing scaffolded file, and
status validates `.build/conductor.toml` as structured conductor data instead of
comparing it with a template. A locally modified emitted stub is intentionally
preserved by install, upgrade, and uninstall; delete the local stub and rerun
`build sop install` to restore catalog content. Before any write or delete,
every target path and the manifest path must resolve strictly below the
repository root and must not cross a symlink or reparse point.

Run `build sop doctor [--json]` to validate that conductor data,
manifest-recorded or present emitted host stubs, and the local review-check
contract are present. Run
`build sop brief <name>` to emit the
embedded SOP text plus resolved conductor data, the SOP schema version, SOP
version, binary version, doctor result, owned catalog paths, and run mode. The
brief always emits a JSON envelope; `--json` is accepted for consistency.
Standard briefs run doctor first; if doctor fails, including when
`min_build_version` is newer than the running binary, the command exits 1 and
omits SOP text. Admission briefs validate inspection inputs before doctor reads
conductor data, then run doctor. Unknown SOP names exit 9.

Admission-only inspection enters through the brief mode syntax:
`build sop brief <name> admission <absolute-inspection-root> <inspection-sha>`.
The root must be the absolute git worktree root for the invoking repository;
subdirectories and unrelated repositories are refused. The SHA must be a full
40-character commit SHA that resolves in that worktree; relative roots, short
SHAs, and unresolvable SHAs are refused before conductor data is read. The
emitted `runMode` carries the resolved inspection root, normalized inspection
SHA, inherited `BUILD_SOP_*` environment values, and an explicit verb policy.
With `BUILD_SOP_RUN_MODE=admission` active, mutating verbs refuse with JSON error
code `sop_admission_refused`; read-only inspection verbs remain available.
Admission forbids worktree lease and teardown, ticket comments and transitions,
commits, branches, pushes, and parent or epic expansion.

The sop commands read `.build/conductor.toml` without loading ticketing, worker,
or event configuration; if `.build/config.toml` is absent, doctor reports the
missing `[[review.checks]]` as a validation finding instead of a bootstrap
error. Doctor also validates manifest-recorded or present emitted stubs
byte-for-byte against the catalog and reports missing, modified, non-regular, or
unsafe stub paths as drift. Review invariants in conductor.toml are structured prose: doctor checks
ids, non-empty statements, optional paths, and optional `blocks_done` shape only.
It does not judge whether the statements are true. Unknown conductor keys are
findings, so misspelled fields cannot silently drop contract data. The local
`[[review.checks]]` list must include at least one setup or gating check with a
non-empty executable; advisory-only checks do not make the gate capable of
blocking Done. No `sop` verb starts a worker agent. Exit 0 means the requested
SOP operation passed and status found no drift, exit 1 means validation findings,
brief refusal, admission mutation refusal, drift, or a safety finding, exit 2
means bad arguments, and exit 9 means an unknown SOP name.

Lease prints the absolute worktree path for use as an agent working directory,
runs `[project].install_command` exactly once in the new worktree, and writes a
safety manifest. It never runs `build install`. Dependencies in the primary
working tree remain human-managed; creating or switching an ordinary branch in
that tree runs no install command. Configure the root and the only untracked
local files Build may copy with `[worktree] root` and `[worktree] seed_files`.
Seed files are copied before the project install command runs. Use
`--require-seed <path>` when a listed file must exist before lease creation.
Concurrent attempts for one ticket are serialized,
and rollback removes only branch and worktree artifacts owned by the failing
attempt. Teardown is safe by default: it refuses tracked work and unexpected
untracked files before removing the worktree. Add
`--require-merged-into <ref>` to prove the helper branch is an ancestor of a
named target before any removal. Branch deletion uses Git's non-force delete
unless `--force` is present. `--force` skips the worktree cleanliness proof and
may permanently discard work. All three forms support `--json`.

Run `build gate` from the leased worktree to execute its configured
`[[review.checks]]`. Setup checks run first on every gate invocation. They are
for repeatable prerequisites such as code generation, not dependency
installation. A setup check matching `[project].install_command` is refused,
and any check that changes tracked files fails the gate. Gating and setup failures exit 1;
advisory failures remain visible but do not change the exit code. Use
`--role gating|advisory|all` to select a role, and `--json` for typed per-check
exit codes, durations, captured output, inconclusive missing-path results, and
the persisted canary-proof status (`true`, `false`, or unknown).
By default an empty selected check list exits 0 for compatibility; add
`--require-checks` to make that condition exit 1.

Use `build worker brief --ticket <id> --role implement|review|rework
--worktree <path> --output <path>` to write a compact, inspectable Markdown
brief for a caller-owned worker. The artifact includes ticket context, role
boundaries, the exact gate command, and worktree evidence. Review uses actual
diff/status inputs and an independent-verdict instruction; rework includes
prior blocking findings and keeps the supplied worktree and branch. The
command does not spawn a worker or mutate tickets, git history, branches,
worktrees, deployments, or other files. Add `--json` for source ticket and
output metadata. Add `--agent <name>`, or the per-phase `--agent-implement` /
`--agent-review`, to render the brief from another agent's templates; the
per-phase flag wins over `--agent`, which wins over `[workers.phases]`, which
wins over `default_agent`. The value must name a shipped template set, since
this command renders templates without starting a worker.

For a semantic-risk ticket, record a `Ticket execution contract` in the ticket
body before dispatching a worker. It identifies parent intent, authority,
forbidden shortcuts, required shared surfaces, focused negative tests,
out-of-scope behavior, and the rework fence. Every worker brief carries that
ticket body and treats the recorded contract as binding. Missing or conflicting
contract information stops implementation before code edits; reviewers return it
as a plan or contract defect for the conductor rather than inventing a
replacement contract.

Run `build candidate status --ticket <id> --base <ref> --json` from the
candidate worktree after implementation and review checkpoints. The JSON
envelope reports the resolved base SHA, HEAD SHA, tracked diff hash,
cached/index diff hash, untracked-file hash, touched paths, dirty state, and
lease manifest metadata when present. The untracked hash includes sorted
repository-relative paths, Git-style regular-file modes, and file content
hashes. Missing base refs, non-git directories, conflicted worktrees, invalid
lease manifests, unreadable paths, untracked directories, and untracked
symlink/reparse-point paths fail with a nonzero JSON error envelope. The command
reads git state only; it does not mutate tickets, branches, commits, pushes,
workers, or worktree lifecycle state.

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
