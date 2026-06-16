# claude-config removal - running checklist

`claude-config` (the `/ticket-*` slash commands backed by `.claude/plane-rest`
+ `.claude/plane-config.md` ID maps + the Plane MCP server) is **obsolete**. In
any repo wired for the `build` CLI, ticket work goes through `build` verbs, and
the agent never reaches for `/ticket-new` / `/ticket-list` / etc.

This doc is the running list: what to grep for, and per-repo status as we rip it
out. It exists because the obsolete path keeps resurfacing ("shall I make a
ticket using /ticket-new?"). Related work: TLB-541 (the `build --json` envelope
that replaces the slash commands' integration layer).

## The rule (state this in each repo's CLAUDE.md + AGENTS.md)

- This repo uses the `build` CLI for all ticket operations. Backend config lives
  solely in `.build/config.toml`.
- Do NOT use or offer the `/ticket-*` slash commands, the `ticket-*` skills, the
  Plane MCP (`mcp__plane__*`), or `.claude/plane-rest`. They are obsolete.
- "make a ticket" -> `build new`; "show tickets" -> `build list`; "read 123" ->
  `build get TLB-123`; close/defer/reopen/amend -> the matching `build` verb;
  investigate/approve/chain/review/ship -> the `build` phase verbs.

## Per-repo grep checklist

Run these from a repo root; every hit is a removal candidate:

- `.claude/plane-rest` (the old REST integration script)
- `.claude/plane-config.md` (state/label/project ID maps)
- `.claude/ticket-config.md` (claude-config workflow config; superseded by `.build/config.toml`)
- root `CLAUDE.md` / `AGENTS.md` text pointing at the `/ticket-*` workflow
- `grep -rln 'ticket-new\|ticket-list\|plane-rest\|mcp__plane\|claude-config'`
- any `.codex/` or `~/.codex/AGENTS.md` references to the slash-command workflow

Removal is per-command and atomic with the matching `build` cutover - no
dual-truth window (per TLB-541).

## Status: latticeflow (this repo)

Evidence found (2026-06-16). Not yet removed - tracked for the "retire old path"
phase so each deletion lands with its `build`-backed replacement:

- [ ] `.claude/plane-rest` (49 KB old REST layer)
- [ ] `.claude/plane-config.md` (ID maps)
- [ ] `.claude/ticket-config.md` (stack/preview config; migrate useful bits to `.build`/docs first)
- [ ] `.claude/tmp_lengths.py` (stray claude-config helper)
- [ ] root `CLAUDE.md` - "universal ticket workflow (Plane backend)" section + the `/tch` override block
- [ ] root `AGENTS.md` - points at `.claude/plane-config.md` + `.claude/ticket-config.md` and `~/.codex/AGENTS.md`
- [ ] add the rule above to `CLAUDE.md` + `AGENTS.md` so agents stop offering `/ticket-new`

## Status: other repos

- [ ] (add a section per repo as it is migrated)
