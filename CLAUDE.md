## Tickets

This project tracks work in Plane (identifiers `TLB-NNN`). All ticket operations go through
the vendored `build` CLI ([bin/build](bin/build)); backend config lives solely in
`.build/config.toml`. Add `--json` to any verb for a machine-readable envelope
(`{schemaVersion, ok, data}` on success; `{ok:false, error:{code, message}}` on failure).

**The claude-config `/ticket-*` workflow is obsolete - do not use or offer it.** No
`/ticket-new` / `/ticket-list` / etc. slash commands, no `ticket-*` skills, no Plane MCP
(`mcp__plane__*`), no `.claude/plane-rest`. If you catch yourself about to ask "shall I make
a ticket with /ticket-new?", run `build new` instead.

Natural language -> command:

| Intent | Command |
|---|---|
| show the backlog / list tickets | `build list [--state Backlog] [--json]` |
| read / show ticket N | `build get TLB-N [--json]` |
| make a ticket "X" | assemble a JSON draft, then `build new - --json` (fields: `title`, `type`, `description`, `acceptanceCriteria`, `labels`, `parent`, `relations` as `[{"kind":"blocked_by","targetId":"TLB-N"}]`; markdown is rendered to HTML) |
| read a ticket's comments | `build comments TLB-N [--json]` |
| comment / write findings back | `build comment TLB-N "<markdown>" [--json]` (or `-` to read the body from stdin) |
| move state | `build transition TLB-N InProgress [--json]` |
| create / list / remove relations | `build relate TLB-N blocked_by TLB-M [--json]`; `build relate TLB-N --list`; `build relate TLB-N --remove RELATION-ID` |
| close / defer / reopen | `build close\|defer\|reopen TLB-N "reason" [--json]` |
| amend (title / priority / type / labels / parent / size / content) | `build amend TLB-N --title "..." --priority high [--json]` |

Composite intents:
- **investigate ticket N** = `build get TLB-N`, investigate with your own tools, then
  `build comment TLB-N - --json` with the findings.
- **work on N** = read it, implement with your normal process (branch, edit, test), then
  `build comment` + `build transition`. For the full autonomous pipeline use
  `build chain TLB-N` (plan -> implement -> review -> ship gate).

Bare numbers refer to TLB tickets: `35` -> `TLB-35`.
