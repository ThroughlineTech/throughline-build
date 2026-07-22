# Workspace Agent Instructions

Project identifier: **TLB** (Plane). Bare numbers refer to TLB tickets - expand before use:
`35` -> `TLB-35`, `101` -> `TLB-101`.

## Tickets go through the `build` CLI

All ticket operations use the vendored `build` binary ([bin/build](bin/build)). Backend
config (Plane workspace/project/token) lives solely in `.build/config.toml`. Add `--json`
to any verb for a versioned envelope: `{schemaVersion, ok, data}` on success,
`{ok:false, error:{code, message}}` on failure (codes: `usage`, `config_error`,
`missing_secret`, `not_found`, `failure`). The envelope is the only thing on stdout;
diagnostics go to stderr. Exit codes: 0 ok, 1 failure, 2 usage/config, 3 missing secret.

**The claude-config `/ticket-*` workflow is obsolete.** Do NOT use or offer the
`/ticket-*` slash commands, the `ticket-*` skills, the Plane MCP (`mcp__plane__*`), or
`.claude/plane-rest`. Use the `build` verbs below.

| Intent | Command |
|---|---|
| show the backlog / list tickets | `build list [--state Backlog] [--json]` |
| read / show ticket N | `build get TLB-N [--json]` |
| make a ticket | assemble a JSON draft, then `build new - --json` (fields: `title`, `type`, `description`, `acceptanceCriteria`, `labels`, `parent`; markdown is rendered to HTML) |
| read a ticket's comments | `build comments TLB-N [--json]` |
| comment / write findings back | `build comment TLB-N "<markdown>" [--json]` (or `-` for stdin) |
| move state | `build transition TLB-N InProgress [--json]` |
| close / defer / reopen | `build close\|defer\|reopen TLB-N "reason" [--json]` |
| amend | `build amend TLB-N (--size S\|M\|L \| --note "..." \| --description <path\|-> \| --ac <path\|->) [--json]` |

Composite intents:
- **investigate ticket N**: `build get TLB-N`, investigate with your own tools, then write
  the findings back with `build comment TLB-N - --json`.
- **work on N**: read it, implement with your normal process (branch, edit, test), then
  `build comment` + `build transition`. Full autonomous pipeline: `build chain TLB-N`.

The `build` CLI is no-install - it is vendored in the repo. Run `build --help` for the full
verb list and `build help <topic>` (config, exit-codes, summary) for reference docs.

## This repository builds the `build` CLI

Treat `bin/build` as both the ticketing tool and part of the system under test. Before using it
for Plane reads or mutations while working a ticket, check whether that ticket changes the verb,
client, transport, retry, state-transition, or serialization path the command depends on. The bug
being fixed may invalidate the binary's result or side effects. Prefer read-only verification,
inspect the JSON envelope and exit code, avoid chaining multiple Plane mutations blindly, and use
the freshly verified binary for final ticket updates only after the affected path is known-good.
