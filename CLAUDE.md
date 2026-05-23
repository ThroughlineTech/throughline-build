## Tickets

This project uses the universal ticket workflow (Plane backend).

- Tickets are Plane work items. Identifiers look like `TLB-NNN`.
- Plane schema lives in `.claude/plane-config.md`; per-project commands + preview profiles in `.claude/ticket-config.md`.
- Common commands: `/ticket-new`, `/ticket-list`, `/ticket-investigate`, `/ticket-approve`, `/ticket-review`, `/ticket-ship`

/tch --sequential override (Copilot)

When `/tch` (or `/ticket-chain`) is called with multiple ticket IDs and `--sequential`:
- Override the single-ticket-only Copilot restriction for this flag combination only
- For each ticket ID in order, run the full chain as if it were a single `/tch` call:
  1. Investigate the ticket (subagent)
  2. Implement + rework loop (subagent)
  3. If `--ship`: ship steps (subagent)
- Complete each ticket fully before starting the next
- No git worktrees, no parallel fan-out - strict sequential loop
- On any ticket failure: stop, report which ticket failed, do not continue to remaining tickets