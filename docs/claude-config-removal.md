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

## Status: latticeflow (this repo) - DONE 2026-06-16 (TLB-541, Phase 4)

Verified first that no code/scripts/config read these files (`build` reads only
`.build/config.toml`), then removed:

- [x] `.claude/plane-rest` (49 KB old REST layer) - deleted
- [x] `.claude/plane-config.md` (ID maps) - deleted
- [x] `.claude/ticket-config.md` (stack/preview config) - deleted; build/test commands live in
      `.github/workflows/build.yml` and `dotnet build*` is allowlisted in `.claude/settings.json`
- [x] `.claude/tmp_lengths.py` (stray helper) - deleted
- [x] root `CLAUDE.md` - rewritten to the `build` dispatch table; `/ticket-*` + `/tch` block removed
- [x] root `AGENTS.md` - rewritten to the `build` dispatch table; old `.claude/*` + `~/.codex` pointers removed
- [x] `docs/build-command-setup.md` "What NOT to configure" - updated to point here

Kept on purpose: `.claude/settings.json` (Claude Code harness config, not claude-config) and
`.claude/commands/op-plan.md` (current `build scaffold` workflow).

Left untouched on purpose: references to the old files in **historical / snapshot docs** -
`docs/state-of-the-system/*` (explicitly drift-tolerant, "code wins"), `docs/op-docs/*`
(historical operation plans, incl. the TLB-541 plan itself), `docs/ticket-audit-data/*`, and
`docs/heartbeat/*`. Rewriting those would rewrite history; per the agent briefing, old-system
references in docs are "intentional and bounded." Sweep later only if they cause confusion.

## Status: other repos

- [ ] (add a section per repo as it is migrated)
