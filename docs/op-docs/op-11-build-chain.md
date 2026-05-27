# Operation: build-chain

Add a `build chain` command that runs the full ticket spine (plan → implement → review → ship) in a single invocation, stopping cleanly on any non-success phase result. Three briefs across two plans.

## Why this exists

Today, completing a ticket's spine requires four sequential commands: `build plan <id>`, `build implement <id>`, `build review <id>`, `build ship <id>`. Each is functional and the architecture explicitly delivers them as composable independent invocations - which is a feature, not a bug, given the subprocess-isolation cost advantage measured at ~9x cheaper than the old chained-session approach.

But for operators running ticket after ticket, the four-command sequence is friction. The old system's `/ticket-chain` (`/tch`) collapses the spine into one invocation. Operators have asked for the equivalent.

`build chain <id>` runs the spine as four sequential phase invocations within a single orchestrator process. Each phase still spawns its own isolated worker subprocess - no cache_read compounding between phases, no shared chat state. The chain command is operator convenience layered on top of the existing per-phase architecture, not a different architecture.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | ChainPhase orchestrator | - | M |
| B    | CLI command and multi-ticket support | A | S |

Plan A delivers the ChainPhase class that calls each phase's RunAsync in sequence, handles non-success outcomes cleanly, and reports per-phase status to the caller. Plan B wires the CLI command and adds optional multi-ticket support so the operator can run several tickets through the spine in a single invocation.

## Plan A: ChainPhase orchestrator

### Goal

`ChainPhase.RunAsync` accepts a ticket ID, runs plan/implement/review/ship in sequence on it, stops on any phase that returns a non-success Status (or non-Pass verdict for review), and returns a `ChainResult` describing what completed and what didn't.

Briefs are sequential within this plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | chain-phase | ChainPhase orchestrator class that runs the four phases in sequence and aggregates results | - | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Contracts/ChainResult.cs, src/ThroughlineBuild.Contracts/ChainStep.cs, tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs |
| 02 | chain-events | Event log additions: emit a chain-start and chain-end event around the four-phase sequence so post-hoc analysis can group phases by chain invocation | 01 | src/ThroughlineBuild.EventLog/EventKinds.cs (extended), src/ThroughlineBuild.Phases/ChainPhase.cs (extended), tests/ThroughlineBuild.Phases.Tests/ChainPhaseEventTests.cs |

### Briefs - detail

#### Brief 01: chain-phase

Goal: ChainPhase orchestrates the full spine sequentially on a single ticket. Each phase runs in its own subprocess (via the existing phase classes), with chain-level state tracking but no shared worker context.

Inputs:
- Existing PlanPhase, ImplementPhase, ReviewPhase, ShipPhase classes (each with their own RunAsync method)
- The existing IPlaneTicketing for state inspection between phases
- Existing IEventLog

Outputs:
- `ChainStep` record: `string PhaseName, Status Status, string? FailureReason, TimeSpan Duration, string? PhaseSessionId`
- `ChainResult` record: `string TicketId, IReadOnlyList<ChainStep> Steps, ChainOutcome Outcome, TimeSpan TotalDuration`
- `ChainOutcome` enum: `Completed | StoppedAtPlan | StoppedAtImplement | StoppedAtReview | StoppedAtShip | Failed`
- `ChainPhase` class with `RunAsync(ChainPhaseOptions options, CancellationToken ct)`:
  - Inspects ticket's current state via Plane API to determine starting phase
  - If state is Backlog: starts at plan
  - If state is Ready (already planned): starts at implement
  - If state is InProgress (mid-implement): error - chain doesn't recover from a mid-implement state; operator runs implement directly
  - If state is InReview (implemented, awaiting review): starts at review
  - If state is Done or Cancelled: error - nothing to chain
  - For each phase in sequence from the determined start:
    1. Record start time
    2. Invoke the phase's RunAsync
    3. Capture the phase's result, duration, and session ID
    4. Inspect the result:
       - For plan/implement/ship: if Status != Ok, stop chain and report
       - For review: if Verdict != Pass, stop chain and report (Rework and Fail both stop)
    5. If Status == Ok and (for review) Verdict == Pass, continue to next phase
  - Returns ChainResult with all steps that ran (including the failing one if any)
- `ChainPhaseOptions` record: `string TicketId, bool Debug, ChainStopAfter? StopAfter`
- `ChainStopAfter` enum: `Plan | Implement | Review` (optional; if set, chain stops after this phase even if successful - useful for spine-but-don't-ship workflows)
- Tests covering:
  - Happy path: Backlog ticket, all four phases run, all return success, outcome is Completed
  - Plan fails: chain stops, outcome is StoppedAtPlan, only Steps[0] is present
  - Implement fails: chain stops, outcome is StoppedAtImplement, Steps has plan and implement
  - Review returns Rework: chain stops, outcome is StoppedAtReview, three steps captured
  - Review returns Fail: same as Rework outcome-wise (StoppedAtReview)
  - Ticket starts in Ready state: chain starts at implement, three steps (no plan)
  - Ticket starts in Done state: error, no phases run
  - StopAfter=Review: chain runs three phases even if all succeed, outcome is Completed (with a note in result indicating early stop by request)

Acceptance:
- [ ] ChainPhase exists with RunAsync method
- [ ] Starting phase is determined by ticket's current Plane state
- [ ] All four phase classes are invoked correctly with their existing signatures
- [ ] Non-success outcomes stop the chain at the failing phase
- [ ] Review verdict (Pass/Rework/Fail) is treated as a chain-stopping signal when not Pass
- [ ] ChainResult accurately reflects what ran, what's missing, total duration
- [ ] StopAfter option works for early termination
- [ ] Tests pass

Notes: ChainPhase does NOT spawn its own worker subprocess - it just orchestrates the existing phase classes. Each phase still spawns its own isolated worker (Claude Code subprocess), so cache_read does NOT compound across phases (this is the architectural win vs old-system chaining).

The "determine starting phase from current state" logic mirrors what an operator would do manually. If a ticket is already past Ready, plan is skipped; if past InReview, review is skipped; etc. This makes chain resumable: after a failure, fix the issue, re-run `build chain <id>` and it picks up where it left off.

Mid-state errors (InProgress) are a deliberate refusal: chain can't safely resume mid-implement because the worker subprocess that was implementing has terminated and the worktree may be in a partial state. The operator handles those explicitly via `build implement` directly.

Review's verdict taxonomy (Pass | Rework | Fail) requires care: Rework means the reviewer wants the implementer to redo work; Fail means abandon. Both stop the chain. The operator decides what to do next (likely `build implement` again with a clarified plan, or close/defer). Chain doesn't auto-retry.

OOS:
- Do not implement chain-level retry on phase failure (operator decides)
- Do not implement parallel chains across multiple tickets (Plan B handles multi-ticket as sequential)
- Do not introduce a shared context between phases (each phase reads what it needs from Plane and the worktree; no chain-level memory)
- Do not bypass any phase's own state-gate checks (each phase still validates its own preconditions)
- Do not emit per-phase events differently than the standalone phase invocations (events are emitted by the phase classes; chain just observes outcomes)

#### Brief 02: chain-events

Goal: Add chain-start and chain-end events that wrap the per-phase events, so post-hoc analysis (or the comparison harness) can group all phases of a chain invocation together. Useful for cost aggregation across a chain run.

Inputs:
- Existing event log schema and EventKinds enum
- ChainPhase from Brief 01

Outputs:
- `EventKinds` enum gains `ChainStart` (e.g., Kind=6) and `ChainEnd` (e.g., Kind=7) values (or whatever the next available IDs are)
- ChainPhase emits:
  - At start: `Kind=ChainStart`, TicketId, Phase=-1 (or null - the sentinel value for chain-level events), Data: `{starting_at_phase: "plan", initial_state: "Backlog"}`
  - At end: `Kind=ChainEnd`, TicketId, Phase=-1, Data: `{outcome: "Completed", phases_run: 4, total_duration_ms: 12345}`
- All per-phase events still emit as they always did - the chain events are additive bookends
- Tests covering:
  - Chain emits start + end events around the per-phase events
  - Failed chain emits end event with the failure outcome
  - Chain start data includes initial ticket state and determined starting phase

Acceptance:
- [ ] ChainStart and ChainEnd event kinds exist in EventKinds enum
- [ ] ChainPhase emits both events
- [ ] Per-phase events are unchanged
- [ ] Events can be filtered/joined by chain invocation (downstream tooling concern, but the data is structured to support it)
- [ ] Tests pass

Notes: The chain SessionId is distinct from each phase's SessionId. The chain events use their own SessionId; per-phase events use the phase's worker SessionId as today. Downstream analysis joins on TicketId + timestamp window.

If the EventKinds enum has gaps (e.g., 6 is used for something else), pick the next available value. Document the chosen values in the EventKinds source comment.

OOS:
- Do not modify the schema of per-phase events
- Do not propagate ChainSessionId into per-phase events (would require touching every phase class; not worth it for the marginal analysis benefit)
- Do not implement event-log filtering or query tools in this brief (downstream tooling)

## Plan B: CLI command and multi-ticket support

### Goal

`build chain` command works for a single ticket. Optionally supports multiple ticket IDs in sequence (each runs through the spine independently before moving to the next).

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | chain-cli | `build chain` CLI command with single and multi-ticket modes, plus operator-facing output | A | src/ThroughlineBuild.Cli/Commands/ChainCommand.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ChainCommandTests.cs |

### Briefs - detail

#### Brief 03: chain-cli

Goal: `build chain <id>` runs ChainPhase on one ticket. `build chain <id1> <id2> <id3>` runs them in sequence (chain each, stop the multi-ticket loop on the first chain that doesn't complete). Operator-facing output is per-phase status with totals at the end.

Inputs:
- ChainPhase from Plan A
- Existing CLI command dispatch pattern

Outputs:
- `ChainCommand` class implementing the existing command interface
- CLI usage:
  ```
  build chain <ticket-id> [<ticket-id>...] [--stop-after plan|implement|review] [--debug]
  ```
  - `<ticket-id>` required, one or more
  - `--stop-after`: optional; halts each chain after the named phase even if successful
  - `--debug`: forwarded to each phase as the debug flag
- Per-ticket output (streamed as phases complete):
  ```
  [SURLF-42] chain starting (initial state: Backlog)
  [SURLF-42] plan: Ok (5m 18s)
  [SURLF-42] implement: Ok (2m 27s)
  [SURLF-42] review: Pass (4m 6s)
  [SURLF-42] ship: Ok (1s)
  [SURLF-42] chain complete (12m 12s)
  ```
- On failure:
  ```
  [SURLF-42] plan: Ok (5m 18s)
  [SURLF-42] implement: Failed - "<failure reason>"
  [SURLF-42] chain stopped at implement
  ```
- Multi-ticket mode prints a summary at the end:
  ```
  Summary: 3 tickets processed, 2 completed, 1 stopped
  - SURLF-42: completed
  - SURLF-43: completed
  - SURLF-44: stopped at review (verdict: Rework)
  ```
- Exit code: 0 if all chains completed; non-zero if any chain stopped or failed; specific non-zero codes per failure type (operator scripts can branch on them)
- Tests covering:
  - Single-ticket happy path produces expected output
  - Single-ticket failure path produces expected stop-at output
  - Multi-ticket loop processes all tickets even if one stops (continues to next ticket in the list)
  - --stop-after halts after the named phase even on success
  - --debug propagates to each phase
  - Exit codes are correct for each outcome combination

Acceptance:
- [ ] `build chain --help` documents the command shape
- [ ] Single-ticket invocation works
- [ ] Multi-ticket invocation works
- [ ] Per-phase status is streamed as phases complete (not batched at the end)
- [ ] Failure summary is clear and actionable
- [ ] Exit codes are documented and tested
- [ ] Tests pass

Notes: The streamed output matters for operator experience: a chain takes 10-15 minutes per ticket, and silent execution for that long feels broken. Even if the underlying phase calls don't expose progress, the per-phase completion logs give visible heartbeats every few minutes.

For multi-ticket runs, the loop is SEQUENTIAL. Parallel chain execution across tickets would be a separate feature (and would require thinking about Plane API rate limits, worktree contention, and worker subprocess concurrency). Defer.

The "stop on first non-completed chain" decision for multi-ticket mode is a tradeoff: stopping is safer (operator sees the first failure and decides) but continuing-through-failures might be what some workflows want. v1 stops; if operators want continue-through, add a `--continue-on-failure` flag in a v1.1 ticket.

OOS:
- Do not implement parallel chain execution across multiple tickets
- Do not implement --continue-on-failure (could be a v1.1 flag if requested)
- Do not implement --dry-run (which phases WOULD run without actually running them)
- Do not implement --from <phase> to override the starting phase (the ticket state already determines this; manual override would create state-consistency footguns)
- Do not implement progress callbacks within phases (each phase's internal progress is its own concern)

## What done looks like

`build chain <id>` runs the full spine on a ticket in one invocation. Each phase spawns its own isolated worker subprocess - no cache_read compounding, no shared chat state. The chain is resumable: after a failure, fix the issue and re-run `build chain <id>` and it picks up where the prior chain stopped (because phase-determination is based on current Plane state, not chain memory).

Operators who run many tickets through the spine save four commands per ticket and get a unified progress stream. The cost-per-ticket is structurally the same as four separate invocations (since each phase still subprocess-isolated), but the operator UX is significantly better.

Multi-ticket chaining works as a sequential loop: `build chain 42 43 44` runs each ticket through its own spine in order. Stops on first chain that doesn't complete (with a summary of what ran and what didn't). This pattern is useful for working through a backlog after planning a sprint's worth of tickets.

The architecture continues to deliver the ~9x cost advantage over the old system's `/tch` because each phase is an independent subprocess. The chain command is operator-facing convenience layered on top, not a different cost profile.