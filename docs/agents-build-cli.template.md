# Tickets: use the `build` CLI

This repo tracks work in Plane. **You never talk to Plane directly.** All ticket
operations go through the vendored `build` binary at [bin/build](bin/build) (already in
the repo - no install). The CLI holds the Plane connection and API token in
`.build/config.toml` and loads them for you.

> **You do not need any credentials, URLs, or tokens.** Do not look for them, do not ask
> for them, do not connect to a Plane API or MCP. If a `build` command ever fails with a
> config or missing-secret error, stop and tell the human - the fix is in
> `.build/config.toml`, not something for you to hunt down.

## The verbs you need

| Intent | Command |
|---|---|
| list / show the backlog | `build list [--state Backlog] [--type <type>] [--parent <id>]` |
| read one ticket | `build get <ID>` |
| read a ticket's comments | `build comments <ID>` |
| comment / write findings back | `build comment <ID> "<markdown>"` (or `-` to read body from stdin) |
| create a ticket | `build new - --json` with a JSON draft on stdin (fields: `title`, `type`, `description`, `acceptanceCriteria`, `labels`, `parent`); or `build new --print-template > draft.md`, fill it in, `build new draft.md` |
| move state | `build transition <ID> InProgress` |
| close / defer / reopen | `build close|defer|reopen <ID> "reason"` |
| amend (size / note / desc / AC) | `build amend <ID> (--size S\|M\|L \| --note "..." \| --description <path\|-> \| --ac <path\|->)` |

`<ID>` is the ticket identifier as shown in the first column of `build list` (e.g.
`PROJ-42`). A bare number means that project's ticket: `42` -> `<prefix>-42`. Run
`build list` once to see the prefix this repo uses.

## Scripting it (`--json`)

Add `--json` to any verb for a machine-readable envelope - the only thing on stdout, with
diagnostics on stderr:

- success: `{schemaVersion, ok: true, data: ...}`
- failure: `{ok: false, error: {code, message}}` (codes: `usage`, `config_error`,
  `missing_secret`, `not_found`, `failure`)

Exit codes: `0` ok, `1` failure, `2` usage/config error, `3` missing secret.

## When you need more than the table

`build` is self-documenting - prefer it over guessing:

- `build --help` - full verb list
- `build <verb> --help` - flags and examples for one verb
- `build help <topic>` - reference docs (`config`, `exit-codes`, `summary`, `digest`)

## Composite intents

- **investigate ticket N**: `build get <ID>`, investigate with your own tools, then write
  findings back with `build comment <ID> -`.
- **work on N**: read it, implement with your normal process (branch, edit, test), then
  `build comment` + `build transition`. The full autonomous pipeline (plan -> implement ->
  review -> ship gate) is `build chain <ID>`.

## Do not

- Do not use any `/ticket-*` slash command, `ticket-*` skill, the Plane MCP
  (`mcp__plane__*`), or a hand-rolled `curl` against the Plane REST API. Those paths are
  obsolete or unsupported here. Use the `build` verbs above - they are the only supported
  interface.
