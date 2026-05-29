# Operation: lifecycle-commands

Add the ticket lifecycle-management command surface to TLB: `build list`, `build close`, `build defer`, `build reopen`, `build amend`. They share the `IPlaneTicketing` surface and ticket-state semantics, so they ship as one op-doc. Independent of the multi-agent work - this touches ticketing, not workers.

## Why this exists

TLB can create, plan, implement, review, ship, chain, and rework tickets, but cannot manage their lifecycle from the CLI: there is no way to list tickets by state, close one with a reason, defer it, reopen a Done/Cancelled one, or amend its content post-creation. These five close the operator gap and all sit on the same Plane ticketing surface, so grouping them avoids five near-identical op-docs.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Lifecycle commands: ticketing-surface extensions, list, state transitions, amend | - | M |

Single plan. Brief 01 (surface extensions) first; 02-04 depend on it and are independent of each other.

## Plan A: Lifecycle commands

### Goal

The five verbs exist, each backed by an `IPlaneTicketing` method, with consistent output and error handling. Read (`list`), state transitions (`close`/`defer`/`reopen`), and content mutation (`amend`) all go through the ticketing client.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | ticketing-surface-extensions | Add IPlaneTicketing methods for query, state transition, content update | - | src/ThroughlineBuild.Contracts/IPlaneTicketing.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs |
| 02 | build-list | `build list` with state/parent/type filters and a readable display | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |
| 03 | build-close-defer-reopen | Three state-transition verbs sharing a transition path; close takes a reason | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |
| 04 | build-amend | `build amend` to modify description / acceptance criteria post-creation | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |

### Briefs - detail

#### Brief 01: ticketing-surface-extensions

Goal: Extend `IPlaneTicketing` (and `PlaneTicketingClient`) with the query, state-transition, and content-update operations the verbs need.

Inputs: current `IPlaneTicketing` / `PlaneTicketingClient`; the Plane API endpoints for listing, state changes, and issue updates; the existing label/state cache.

Outputs:
- Query method (filter by state, parent, type) returning ticket summaries.
- State-transition method mapping a target lifecycle state (closed/cancelled, deferred, reopened/backlog) to the Plane state; close accepts a reason recorded on the ticket.
- Content-update method for description and acceptance criteria.
- All new request/response DTOs registered for source-gen (AOT-clean).

Acceptance:
- [ ] `IPlaneTicketing` exposes query, state-transition, and content-update operations
- [ ] `PlaneTicketingClient` implements them against the Plane API
- [ ] New DTOs registered in a source-gen JSON context; AOT publish succeeds
- [ ] State names map correctly to the project's Plane states

Notes: This brief edits `PlaneTicketingClient.cs`, which op-14 Brief 11 (ticket size extraction in `GetAsync`) also edits - coordinate ordering to avoid a conflict (land after op-14 B11, or merge carefully).

OOS: The CLI verbs (02-04). Tree/parent behavior (tree-aware op-doc). Decompose's parent-child creation (decompose op-doc).

#### Brief 02: build-list

Goal: `build list` queries and displays tickets.

Inputs: the query method from B01; CLI dispatch and usage text.

Outputs:
- `build list` with `--state`, `--parent`, `--type` filters (combinable).
- Readable tabular output (id, title, state, type, parent).
- Usage text documents the verb and flags.

Acceptance:
- [ ] `build list` returns filtered tickets in a readable form
- [ ] Filters combine correctly
- [ ] Usage documents the verb

OOS: Interactive selection; tree rendering (tree-aware op-doc).

#### Brief 03: build-close-defer-reopen

Goal: Three state-transition verbs on a shared path.

Inputs: the state-transition method from B01.

Outputs:
- `build close <id> --reason "<text>"` (reason required), `build defer <id>`, `build reopen <id>`.
- Shared transition helper; clear error if the transition is invalid for the current state.
- Usage text documents all three.

Acceptance:
- [ ] Each verb transitions the ticket to the correct state
- [ ] `close` records the reason
- [ ] Invalid transitions produce a clear error
- [ ] Usage documents the verbs

OOS: Parent/tree cascade (tree-aware op-doc); bulk transitions (multi-ticket op-doc).

#### Brief 04: build-amend

Goal: `build amend` edits an existing ticket's content.

Inputs: the content-update method from B01.

Outputs:
- `build amend <id>` updating description and/or acceptance criteria (from args, a file, or stdin - implementer's call).
- Usage text documents the verb.

Acceptance:
- [ ] `build amend` updates description and acceptance criteria on an existing ticket
- [ ] Partial updates (one field) work
- [ ] Usage documents the verb

OOS: Worker-driven content rewriting; amending child trees (tree-aware op-doc).

## What done looks like

An operator can `build list --state backlog`, `build close 42 --reason "duplicate"`, `build defer 43`, `build reopen 44`, and `build amend 45` - the full lifecycle surface from the CLI, all on the shared Plane ticketing client, with no change to the worker or agent surfaces.