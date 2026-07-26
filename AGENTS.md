# AGENTS.md - latticeflow (Throughline Build)

Read this before you touch anything. It is the tool-agnostic contract for working in
this repo: any coding agent (Claude Code, Codex, Gemini, Copilot, Cursor, a `build`
worker, a human) is expected to follow it. Agent-specific files (`CLAUDE.md`,
`.claude/`, `.github/copilot-instructions.md`, ...) exist only to point back here.

Nested `AGENTS.md` files add local detail and win inside their subtree:
[src/AGENTS.md](src/AGENTS.md) (project layout, AOT discipline),
[tests/AGENTS.md](tests/AGENTS.md) (suite layout, test doubles, snapshots).

## What this repository is

**Throughline Build** - a .NET 10 native-AOT CLI named `build` that drives an Agile
ticket workflow end to end. Deterministic C# owns the state machine, gates, git
operations, and ticket I/O; LLM-bearing phases (plan / implement / review / decompose)
are delegated to an external coding-agent CLI spawned as a worker subprocess. Four
worker agents are implemented and interchangeable (`claude-code`, `codex`, `gemini`,
`copilot`), selected by config or `--agent`.

Design goal that constrains almost every change: **generated output is stack-agnostic.**
The engine targets TypeScript, .NET, Python, and plain-document repos alike. Stack
knowledge belongs in derived data (config, profiles, briefs), never hardcoded in engine
code. That this engine's own source happens to be C# is incidental - do not let it leak
into the process.

Orientation, in the order worth reading:

| Where | What it gives you |
| --- | --- |
| [docs/state-of-the-system/00-index.md](docs/state-of-the-system/00-index.md) | Code-true map of the whole system - architecture diagram, per-doc index, status tags. Start here. |
| [docs/state-of-the-system/01-inventory.md](docs/state-of-the-system/01-inventory.md) | Every CLI verb, library project, tool, and workflow. |
| [docs/throughline_build_userguide.md](docs/throughline_build_userguide.md) | Operator-facing guide to the verbs. |
| [docs/throughline-build-architecture.md](docs/throughline-build-architecture.md) | Statement of architectural intent. Forward-looking - where it disagrees with the source, the source wins. |
| `build --help`, `build help <topic>` | Authoritative verb list and reference docs, generated from the binary you built. |

Docs are point-in-time; each state-of-the-system doc stamps a `Last refreshed` header
with the HEAD it was written against. Trust the code over the docs. If you land a change
that alters a documented surface (a verb, a config key, a contract, a status tag),
updating the affected section and bumping that header is part of your change.

## Build and test

Requires the **.NET 10 SDK**, `git` on `PATH`, and - for native AOT publish - a native
toolchain for the target RID (MSVC on Windows, Xcode CLT on macOS, clang/gcc on Linux).

```
dotnet build throughline-build.sln --nologo -v q          # compile-check, fastest loop
dotnet test --nologo -v q --logger "console;verbosity=minimal"   # full xUnit suite
./build.sh                                                # AOT publish -> bin/, RID auto-detected
RID=osx-arm64 ./build.sh                                  # cross-target
dotnet publish src/ThroughlineBuild.Cli -r linux-x64 -c Release --nologo -v q
```

**Gates for any code change: `dotnet build` and `dotnet test`, both green, output pasted
verbatim.** Do not report a change as done on a partial or filtered test run. CI
([.github/workflows/build.yml](.github/workflows/build.yml)) runs restore + test + publish
on `osx-arm64`, `win-x64`, and `linux-x64`; a change that only builds on your platform is
not finished.

A fresh clone has **no binary and no config**: `bin/` and `.build/` are gitignored. Build
the binary before you use it. The root `Directory.Build.props` / `Directory.Build.targets`
are likewise machine-local (native-AOT linker overrides) - absent by design, and the
tracked `tests/Directory.Build.props` chains to them only if they exist.

## Working conventions

- **Read before writing.** Do not propose or make changes to code you have not read.
- **Branches.** Feature work goes on `ticket/<id>` (what the tool itself cuts) or a short
  descriptive branch. Never commit directly to `main`.
- **Commits.** `{TICKET-ID}: short description` when working a ticket, `topic: short
  description` otherwise. No agent-vendor branding, sign-offs, or "generated with" lines.
- **Implementing and shipping are separate steps.** Do not merge, push to `main`, tag, or
  release without an explicit instruction.
- **ASCII only in anything you write** - code, comments, docs, commit messages, ticket
  bodies. No em/en dashes, no curly quotes. Reason: the Windows + Git Bash + curl path
  mangles non-ASCII bytes on REST round-trips, which has broken ticket updates before.
  When read-modify-writing content that already contains non-ASCII, route the body through
  a file rather than a shell variable.
- **Parallel work needs separate worktrees.** Two agents editing the same working tree
  clobber each other's HEAD. Cut a `git worktree add` per unit of work first.

## Code discipline

- **AOT.** `ThroughlineBuild.Cli` publishes with `PublishAot=true`. Use source-generated
  `JsonSerializerContext` for everything serialized; never rely on reflection-based
  serialization. Keep `ThroughlineBuild.Contracts` I/O-free. Test projects do *not*
  inherit `PublishAot`, so AOT-sensitive tests must flip reflection off explicitly - see
  [tests/AGENTS.md](tests/AGENTS.md).
- **The solution file is the source of truth** for what counts as a project. Directories
  under `src/` that are not in `throughline-build.sln` are local build debris.
- **Tests are per-project.** Each `src/` library has a mirrored suite under `tests/`, with
  project-local fakes rather than a shared doubles library. Reuse the local ones.
- **Gates must be able to fail.** Verification config with zero gating checks ships every
  ticket green - an empty check list is a silent pass, not a pass. When you touch gate,
  scaffold, or verification code, confirm the failure path still fails.

## Ticket workflow

This project tracks its own work in Plane (identifiers `TLB-NNN`) through the `build` CLI
it builds. **This path requires a configured backend.** `.build/config.toml` is gitignored,
so a fresh clone has none - run `build init` (interactive bootstrap) and `build setup`
(provisions states and labels) to create one, or skip this section entirely and use the
repo's GitHub issues and pull requests.

When a backend *is* configured, all ticket operations go through `build`. There is no
direct-REST path, no MCP ticket server, and no `/ticket-*` slash-command flow - those are
obsolete; do not use or offer them. Add `--json` to any verb for a versioned envelope
(`{schemaVersion, ok, data}` on success, `{ok:false, error:{code, message}}` on failure;
codes `usage`, `config_error`, `missing_secret`, `not_found`, `failure`). The envelope is
the only thing on stdout, diagnostics go to stderr. Exit codes: 0 ok, 1 failure, 2
usage/config, 3 missing secret.

| Intent | Command |
| --- | --- |
| list tickets / show the backlog | `build list [--state Backlog] [--json]` |
| read ticket N | `build get TLB-N [--json]` |
| create a ticket | `build new --print-template > draft.md`, fill it in, `build new draft.md` (or pipe a JSON draft to `build new - --json`) |
| read comments | `build comments TLB-N [--json]` |
| write findings back | `build comment TLB-N "<markdown>" [--json]` (`-` reads stdin) |
| move state | `build transition TLB-N InProgress [--json]` |
| relations | `build relate TLB-N blocked_by TLB-M [--json]`; `--list`; `--remove <RELATION-ID>` |
| close / defer / reopen | `build close\|defer\|reopen TLB-N "reason" [--json]` |
| amend | `build amend TLB-N <option> ... [--json]` (`--title`, `--priority`, `--type`, repeatable `--label-add`/`--label-remove`, `--parent`, `--size`, `--note`, `--description`, `--ac`) |

Bare numbers refer to tickets in the configured project: `35` -> `TLB-35`.

Composite intents:

- **investigate ticket N**: `build get TLB-N`, investigate with your own tools, then write
  the findings back with `build comment TLB-N - --json`.
- **work on N**: read it, implement with your normal process (branch, edit, gate), then
  `build comment` + `build transition`. The full autonomous pipeline is
  `build chain TLB-N` (plan -> implement -> review -> ship gate).

Use `build new --print-template` rather than hand-rolling a ticket body: the template uses
the headings the validator recognises (`#` title, `## Acceptance criteria`, `## Out of
scope`), which avoids the missing-acceptance-criteria warning.

## Two hazards specific to this repo

**You are editing the tool you are using.** `build` is both the ticketing interface and the
system under test. Before running it against a live backend while working a ticket, check
whether that ticket touches the verb, client, transport, retry, state-transition, or
serialization path the command depends on - the bug you are fixing may invalidate the
result or its side effects. Prefer read-only verification, inspect the JSON envelope and
exit code, do not chain mutations blindly, and save final ticket updates for a freshly
built binary once the affected path is known good.

**Worker-spawning verbs do not nest.** `build chain` / `implement` / `review` / `plan`
launch a coding-agent CLI as a subprocess, and those CLIs refuse to start inside an
existing agent session. If you are yourself running as an agent, run the deterministic
verbs directly and leave the worker-spawning ones to a plain terminal.
