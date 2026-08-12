# Throughline Build - Operator Guide

Throughline Build (`build`) is a CLI for Plane-backed, agent-assisted software
delivery. This is the canonical guide for installing Build into a repository,
proving that repository is ready for work, and operating tickets.

The `docs/state-of-the-system/` set in the Throughline Build source repository
is the current architecture authority. The binary's generated help is the
command-line authority. Run `build help <topic>` when this guide and the
installed binary disagree.

## What installation means

There are two separate readiness layers:

1. `build install` prepares Plane configuration, real review checks, conductor
   data, and the binary-hosted SOP stubs. Its final `READY` means the Build
   control plane is structurally ready.
2. The target repository still needs its own dependencies installed on a host
   it supports, followed by a green `build gate --require-checks --json`.

`build install` deliberately does not install dependencies in the primary
checkout, run the target build, install browser binaries, install system
packages, or prove that the repository supports the current operating system.
Do not tell a developer the repository is ready for work until both layers pass.

## Before you start

Run every command from the target repository root. Have these prerequisites and
values ready:

- `build` and Git on `PATH`;
- at least one supported agent CLI on `PATH`: `claude`, `codex`, `gemini`, or
  `copilot`;
- a host operating system and target toolchain supported by the repository;
- the Plane API base URL, workspace slug, personal API token, and either the
  existing project UUID or the exact project name to find or create; and
- a clean Git checkout you may place on a non-protected run branch.

Confirm the tools in the same shell that will run the installation:

```sh
build --version
git --version
claude --version   # or codex, gemini, or copilot
```

Read the target repository's README, agent instructions, CI workflow, and
current architecture documentation before deriving its profile. If those docs
say Linux or macOS, moving the checkout to a supported host is part of
installation; Build's structural `READY` does not waive that requirement.

`.build/config.toml` is a tracked repository-fact file. Check for a legacy
ignore rule before installation:

```sh
git check-ignore -v .build/config.toml
```

The command must print nothing. If it prints a rule, remove that specific
legacy rule from `.gitignore` before continuing. Otherwise Build can report
local readiness while every new clone still lacks the Plane facts and gate.

## Install Build into a repository

### 1. Make the Plane token available

In a POSIX shell, export the token without putting its value in shell history:

```sh
read -rsp "Plane API token: " PLANE_API_TOKEN
export PLANE_API_TOKEN
printf '\n'
```

The environment contains the value. The tracked config contains only the
variable name:

```text
shell:      PLANE_API_TOKEN=<secret value>
config:     plane_api_token_env = "PLANE_API_TOKEN"
```

Never pass `--token-env "$PLANE_API_TOKEN"`; that expands the secret and uses
the token itself as an environment-variable name.

### 2. Choose one initialization path

Use the interactive path when a person is at the terminal. It prompts for the
URL and workspace, then lets the operator choose an existing Plane project or
create one. You do not need to collect a project UUID before using this path:

```sh
build install --token-env PLANE_API_TOKEN
```

If the token prompt appears after the variable is already set, leave it blank;
`--token-env` keeps the secret out of tracked config.

Use the non-interactive path for automation or when all values are already
known:

```sh
build install --no-interactive \
  --plane-url "PLANE_API_URL" \
  --workspace "WORKSPACE_SLUG" \
  --project-id "PROJECT_UUID" \
  --token-env PLANE_API_TOKEN
```

To resolve or create by name, omit `--project-id` and pass
`--project-name "EXACT PROJECT NAME"`. Do not gather the same values and then
send the operator through the interactive questions too; choose one path.

The command initializes configuration, runs Setup, and stops with a
`profile_handoff`. A bare non-interactive or redirected invocation without the
required flags refuses to write placeholder configuration and prints the
complete command it needs.

### 3. Verify Plane and persist the token

Do this before applying the profile so the non-secret token-file path is part
of the final readiness commit:

```sh
rg -n "REQUIRED_" .build/config.toml  # must print nothing
build setup --check
build list --json
build setup --write-token-file secrets/plane-api-token
```

`build list` is the ticketing check that matters. `build sop doctor` never
loads Plane credentials and cannot prove connectivity.

The token file is machine-local and ignored. The tracked config keeps
`plane_api_token_env` for CI and adds only
`plane_api_token_file = "secrets/plane-api-token"`. Resolution order is an
inline token, the named environment variable, then the token file. Do not put a
literal token in `.build/config.toml`.

### 4. Apply the project profile

The first install invocation emitted a repository-profile prompt. Give it to an
agent that can read the repository. Save only the returned JSON:

```sh
# Save the response as .build/profile.json.
build install --profile .build/profile.json
```

The profile records the target stack, frozen worktree install command, actual
build/test entry points, non-vacuous checks and their failing canaries,
convention files, and any shared-contract authority. Build installs the SOP
stubs and stops again with an `invariants_handoff`.

The profile's `install_command` hydrates future leased worktrees. It is not run
in the primary checkout. Multi-stack repositories still have one
`language`/`framework`/`package_manager` value; use the primary language and a
real named framework, then make the exact install and checks cover every stack.

Stage 2 refuses to replace different checks already written by a human. Use
`--force` only when intentionally replacing them. Matching profile data is an
idempotent no-op.

### 5. Apply review invariants and finish structural readiness

Give the second prompt to an agent that can inspect the repository and save
only its TOML blocks:

```sh
# Save the response as .build/invariants.toml.
build install --invariants .build/invariants.toml
```

The final stage validates the profile, conductor data, token resolution, SOP
doctor, Git hygiene, and worktree lease surface. It creates a non-protected run
branch when necessary and commits the tracked readiness files. A successful
result says `READY: run-backlog preflight passed`.

If doctor reports `constellation.contract_authority.placeholder` and the
repository genuinely has no shared-contract authority, set this explicit
machine-local value in `.build/conductor.toml`, then rerun the same invariants
command:

```toml
[constellation]
contract_authority = "none"
```

Do not invent a directory merely to clear the finding.

### 6. Install and prove the target checkout

Now run the profile's `[project].install_command` yourself in the primary
checkout. Also perform any repository-documented one-time host setup, such as
installing Playwright browsers, Xcode tools, ffmpeg, or a native compiler.

Examples only - use the command derived from the repository:

```sh
npm ci
go mod download && npm --prefix frontend ci
dotnet restore --locked-mode
```

Then run the actual configured gate:

```sh
build gate --require-checks --json
```

If a gating check fails, the repository is not ready for work even though
`build install` reported structural `READY`. Fix the repository or prerequisite;
do not weaken the gate or substitute an unsupported host.

### 7. Final verification

```sh
build setup --check
build list --json
build sop doctor --json
build sop brief run-backlog --json
git ls-files --error-unmatch .build/config.toml
git status --short --branch
```

The expected result is Plane connectivity, doctor success, a brief containing
`sopText`, a tracked config file, and an empty porcelain status on a
non-protected branch.

## Installing a second clone

A completed first installation commits `.build/config.toml` and the Claude and
Codex host stubs. A clone therefore inherits Plane project facts and the gate,
but not the token file, dependencies, or machine-local conductor.

On each new machine:

1. verify the target repository supports that host;
2. export `PLANE_API_TOKEN` and run
   `build setup --write-token-file secrets/plane-api-token`;
3. run `build sop install --json` to recreate `.build/conductor.toml`;
4. reapply the verified conductor invariants;
5. run the target `[project].install_command` and any one-time host setup; and
6. run `build setup --check`, `build list --json`, `build sop doctor --json`,
   and `build gate --require-checks --json`.

If the clone lacks `.build/config.toml` or the host stubs, the first machine did
not complete and commit installation. Treat it as a first install; do not use
`build sop upgrade` to create missing files.

## Installation troubleshooting

### Build says READY, but the target build or tests fail

Structural `READY` does not run the target repository. Confirm the operating
system is supported, run `[project].install_command` in the primary checkout,
install documented system dependencies, and run
`build gate --require-checks --json`. A red target gate is a repository or host
prerequisite blocker, not a successful installation.

### `.build/config.toml` disappears on every clone

Run `git check-ignore -v .build/config.toml`. Remove the reported legacy ignore
rule, verify the config contains only secret indirection, and track the file.
Build can otherwise report local readiness while leaving the repository
uninstallable for the next user.

### The token works in a terminal but not from an agent

The agent did not inherit the interactive shell environment. From a shell where
the token resolves, run:

```sh
build setup --write-token-file secrets/plane-api-token
```

### `sop doctor` passes but `build list` fails

Doctor validates local conductor and gate structure, not Plane. Use
`build setup --check` and `build list --json` for ticketing.

### Only one host stub exists

Run `build sop install --sop run-backlog --json` without `--host`, then verify
with `build sop status --sop run-backlog --json`.

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
| `chain` | Recurses through non-terminal children, skipping Done and Cancelled children. Sibling dependencies determine ordering; `--max-depth` limits traversal and `--dry-run` previews the post-order schedule. The Throughline Build source repository includes `docs/build-grandparent-chain.md` for implementation details. |
| `decompose` | Has no parent-specific guard and creates additional direct children. Check existing children before running it again. |
| `close` / `defer` | Attempts the lifecycle transition on each non-terminal direct child, then on the parent. Use `--no-cascade` to affect only the named ticket. |
| `reopen` | Reopens only the parent and prints a note; children keep their current states. |
| `rework` | Requires InProgress, then uses the implement path; a parent is therefore rejected by implement's child check. |
| `amend` | Changes only the named ticket. `--parent` reparents that ticket explicitly. |

Ticket reads expose the same stable IDs that operators type. `build get --json` includes a
`children` array with each direct child's ID, title, and state, and `parentId` values are stable
ticket IDs such as `TLB-42` rather than Plane UUIDs. `build list --parent` accepts stable ticket IDs
and legacy Plane UUIDs.

## Ticket attachments

List a ticket's normal files and supported inline description images with:

```
build attachments TLB-620
build attachments TLB-620 --json
```

Normal work-item attachments appear first in Plane response order, followed by inline images in
description order. Duplicate asset UUIDs appear once. The JSON form reports `id`, `source`, `name`,
`contentType`, and `sizeBytes`; an empty attachment list is a successful empty `data` array.

Download one discovered attachment to an explicit path:

```
build attachment TLB-620 11111111-2222-3333-4444-555555555555 --output evidence.png
build attachment TLB-620 11111111-2222-3333-4444-555555555555 --output evidence.png --json
```

`--output` is required. The command never writes binary data to stdout, never overwrites an
existing path, and does not leave the requested output partially written when a download fails.
The JSON form reports the attachment metadata, the output path exactly as requested, and the byte
count.

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

Complete every installation and verification step above first. In particular,
do not start ticket work from a structural `READY` result while the target gate
is still red. Use the ticket ID that `build new` prints in the later commands.

### 1. Create a ticket

```
build new "Rate-limit /search to 60 requests per minute per API key, 429 on overage"
```

Free-form text is drafted by the configured worker into a titled body with acceptance criteria, so
state one shippable behavior change specifically enough that a reviewer can fail it: a specific
surface, a specific rule, an observable result. Pass `--title "..."` to fix the title yourself, or
`--review` to inspect, edit, or regenerate the draft before it is filed.

You should see a ticket ID printed to stdout (e.g. `TLB-1`). Open Plane and confirm the ticket
appears in your project with state Backlog and the drafted title.

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

### 2. Plan the ticket

```
build plan TLB-1
```

You should see the plan phase complete with a success message. Open Plane and confirm the ticket
state has moved to Ready.

For chained runs (`build chain TLB-1`, or multiple ticket IDs), start from a clean main checkout.
Tracked changes in the main worktree are refused before planning; commit, stash, or revert them
first. Untracked files do not block the chain.

### 3. Implement the ticket

```
build implement TLB-1
```

You should see the implement phase complete with a success message. Open Plane and confirm the ticket
state has moved to InReview.

### 4. Review the ticket

```
build review TLB-1
```

You should see a `Pass` verdict. If you see `Rework` instead, run `build rework TLB-1` and then
re-run `build review TLB-1`; repeat until you see `Pass`.

### 5. Ship the ticket

```
build ship TLB-1
```

You should see a fast-forward merge on the configured target branch and a success message. When the
configured remote exists, ship pushes by default; use `--no-push` (or `[ship] push = false`) for a
local-only ship. Open Plane and confirm the ticket state has moved to Done.

### 6. Confirm the commit

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
and exit codes are documented in the Throughline Build source repository's
`docs/bring-your-own-conductor.md`.

See that source document for the manifest safety model, exit codes, and a
complete caller-owned conductor sequence.
