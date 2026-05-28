# 10 - Lifecycle and Orchestration

The Agile phase state machine implemented by `build` - what each phase does, how the chain orchestrator transitions between them, and the rework loop bounded by `MaxReworkRounds`.

For per-phase failure modes see [09-failure-modes.md](09-failure-modes.md). For inter-project types see [07-contracts.md](07-contracts.md).

---

## The state machine

```
                                   ChainPhase routes here based on initial state
                                                      |
                                                      v
        Backlog        Planning       Ready        InProgress     InReview        Done       Cancelled
            \             |             |              |             |              |             |
             \            |             |              |             |              |             |
              plan -------+-> plan -----+              |             |              |             |
                                        v              |             |              |             |
                                        +-> implement -+             |              |              |
                                                       v             |              |              |
                                                       +-> implement-+              |              |
                                                                     v              |              |
                                                                     +-> review ----+              |
                                                                     ^              |              |
                                                                     |              |              |
                                              Rework verdict --------+              |              |
                                                                                    v              |
                                                                                    +-> ship ----->+
                                                                                                   ^
                                                                              close / defer ------+
                                                                                                  |
                                                                              reopen <-----+ +----+
                                                                                           |
                                                                                           v
                                                                                  Backlog / Ready
```

Backed by these transitions in code:

| Phase | Source state | Target state(s) |
|---|---|---|
| `plan` | `Backlog` | `Planning` -> `Ready` ([src/ThroughlineBuild.Phases/PlanPhase.cs:91, 135-140](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L91)) |
| `implement` (initial) | `Ready` | `InProgress` -> `InReview` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:137-145, 206-212](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L137-L145)) |
| `implement` (rework) | `InProgress` | `InReview` (no `InProgress` re-entry) |
| `review` (Pass / Fail) | `InReview` | no change |
| `review` (Rework) | `InReview` | `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:201-206](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L201-L206)) |
| `ship` | `InReview` | `Done` ([src/ThroughlineBuild.Phases/ShipPhase.cs:330](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L330)) |
| `close` | non-terminal | `Cancelled` |
| `defer` | non-terminal | `Cancelled` |
| `reopen` | `Done` / `Cancelled` | `Backlog` or `Ready` (decided by `DetermineTargetState` based on prior marker + Implementation Plan section presence) |
| `new` | n/a | new ticket in Plane default state (`Backlog`) |
| `scaffold` | n/a | N plan-tickets + M brief-tickets in `Backlog` |

`TicketState` enum: `Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled` ([src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs)).

Plane mirror state names: `Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled` (hardcoded reverse map at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:163-173](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L163-L173)).

---

## Phase implementations

### `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs](../../src/ThroughlineBuild.Phases/PlanPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/PlanPhase.cs:55-143](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L55-L143)):
1. Fetch ticket.
2. State guard: `Backlog`.
3. Resolve `main` SHA via `BaseRefResolver`.
4. Build `RepoState` (top-level entries + main SHA).
5. Build brief via `PlanBriefBuilder`.
6. Emit `WorkerSpawn` event.
7. Run worker.
8. Emit `VerifierVerdict` event (`Pass` for the orchestrator's view of whether the worker delivered).
9. Optionally emit `LlmCall` event from worker metadata.
10. Validate required metadata keys.
11. Append plan HTML to description.
12. Apply risk + size labels.
13. Post `[planned_at: <sha>]` comment.
14. Transition `Backlog -> Planning -> Ready` (two-step).

### `ImplementPhase` ([src/ThroughlineBuild.Phases/ImplementPhase.cs](../../src/ThroughlineBuild.Phases/ImplementPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ImplementPhase.cs:51-217](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L51-L217)):
1. Fetch ticket. Detect rework round vs. initial by `ImplementPhaseOptions.ReviewFeedback` presence.
2. State guard: `Ready` (initial) or `InProgress` (rework). On guard failure, write `phase-status.json` via `EarlyExitManifest`.
3. Resolve base ref + main SHA.
4. Compute deterministic worktree names via `PhaseWorktreeLayout`.
5. Extract `[planned_at: <sha>]` marker from comments. Emit drift warning if differs from current main SHA.
6. Build `ImplementBriefBuilder` with branch / worktree / optional `ReviewFeedback` for rework.
7. `git worktree add -b ticket/<slug> .worktrees/ticket-<slug> <baseRef>`. Early exit if it fails.
8. Transition `Ready -> InProgress` only on initial round.
9. Emit `WorkerSpawn`. Run worker inside the worktree.
10. Validate `commit_sha` metadata. Compare against actual `git rev-parse HEAD` in worktree; actual wins on discrepancy.
11. Post `[implemented_at: <actualSha>]` comment naming the branch.
12. Transition `InProgress -> InReview`.

### `ReviewPhase` ([src/ThroughlineBuild.Phases/ReviewPhase.cs](../../src/ThroughlineBuild.Phases/ReviewPhase.cs))

Status: **Functional**.

Step sequence ([src/ThroughlineBuild.Phases/ReviewPhase.cs:60-223](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L60-L223)):
1. Fetch ticket; state guard `InReview`.
2. Locate the worktree by branch or path via `git worktree list`.
3. Resolve base ref + main SHA.
4. Reconstruct an implementer brief (so the verifier sees what the implementer was told) via `ImplementBriefBuilder.Build` without `ReviewFeedback`. This is reconstructed deterministically - not a fresh worker invocation.
5. Extract `[implemented_at: <sha>]` marker; required.
6. Compute the diff `baseRef..featureBranch` with patches.
7. Synthesize a `WorkerResult` from the marker + diff to hand to the verifier (no re-execution of the implement worker).
8. Run automated checks via `AutomatedChecksRunner.RunAsync(config.Review.Checks, workingDir, ct)`.
9. Run the verifier - default `ClaudeCodeReviewer`, which spawns a `claude` subprocess against the review brief.
10. Emit `VerifierVerdict`.
11. Apply verdict:
    - `Pass` -> post pass comment, no transition.
    - `Rework` -> post rework comment, transition `InReview -> InProgress`.
    - `Fail` -> post fail comment, no transition (operator decides).

### `ShipPhase` ([src/ThroughlineBuild.Phases/ShipPhase.cs](../../src/ThroughlineBuild.Phases/ShipPhase.cs))

Status: **Functional**.

Deterministic - no LLM, no worker. Step sequence ([src/ThroughlineBuild.Phases/ShipPhase.cs:73-371](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L73-L371)):
1. Fetch ticket; state guard `InReview`.
2. Locate worktree.
3. Pre-flight: `build` binary not running from inside the worktree.
4. Pre-flight: both worktrees clean of tracked changes.
5. Conditionally fetch from remote. If no remote: skip, use local base.
6. Determine rebase target via ancestry checks (`IsAncestorAsync`): use `origin/main` if it is an ancestor of local main; use local main if it is an ancestor of `origin/main`; if diverged - fail.
7. Emit `base_ref_resolved` event.
8. Rebase feature branch onto resolved base ref. On conflict: `git rebase --abort`, fail at `Rebase`.
9. Scan committed files for conflict markers.
10. Run `ship.regression_checks`.
11. Fast-forward merge into local base branch.
12. Read merged HEAD SHA.
13. Post `[shipped_at: <mergedSha>]` comment.
14. Transition `InReview -> Done`.
15. `WorktreeDecrufter.DecruftAsync` (failure non-fatal post-merge).
16. Optionally `git branch -d ticket/<slug>` (failure non-fatal).

### `ChainPhase` ([src/ThroughlineBuild.Phases/ChainPhase.cs](../../src/ThroughlineBuild.Phases/ChainPhase.cs))

Status: **Functional**.

Orchestrator. Constructed in `Program.cs` with per-phase factories closed over the shared `PlaneTicketingClient`, `ClaudeCodeAgent`, and `IEventSink` ([src/ThroughlineBuild.Cli/Program.cs:899-940](../../src/ThroughlineBuild.Cli/Program.cs#L899-L940)). Step sequence ([src/ThroughlineBuild.Phases/ChainPhase.cs:45-177](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L45-L177)):

1. Read ticket state; route:
   - `Backlog` -> start at Plan
   - `Ready` -> start at Implement
   - `InReview` -> start at Review
   - else -> emit `ChainStart` + return `RefusedInitialState`
2. Emit `ChainStart`.
3. If starting at Plan: run `PlanPhase`. Fail -> `StoppedAtPlan`.
4. If Plan succeeded or starting at Implement: enter implement-review loop:
   - Round 0: run `ImplementPhase`. Fail -> `StoppedAtImplement`.
   - Run `ReviewPhase`. Fail -> `StoppedAtReview`.
   - On `Pass`: break, continue to Ship.
   - On `Rework`: increment round counter, emit `ReworkRound`, loop. If `round >= MaxReworkRounds` (2): return `ReworkCapExceeded`.
   - On `Fail`: return `StoppedAtReview` with rationale.
5. If starting at Review only: run `ReviewPhase` once. Pass -> Ship. Rework -> implement-review loop. Fail -> `StoppedAtReview`.
6. Run `ShipPhase`. Fail -> `StoppedAtShip`.
7. Emit `ChainEnd`. Return `Completed`.

The chain calls a `Func<ChainStep, Task>` callback (`onStep`) after each phase, which `ChainCommand` uses to stream one-line summaries to stdout ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)).

### `ReworkPhase` ([src/ThroughlineBuild.Phases/ReworkPhase.cs](../../src/ThroughlineBuild.Phases/ReworkPhase.cs))

Status: **Functional**. Thin wrapper.

1. State guard: `InProgress`.
2. Resolve feedback: manual `--feedback` text wins; otherwise read latest `Rework` verdict from event log via `ReviewFeedbackRetriever` (scans `.build/events/` newest-first, [src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs:12-176](../../src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs#L12-L176)).
3. Build `ImplementPhaseOptions` carrying the feedback.
4. Invoke `ImplementPhase` (rework round).

### `NewPhase` ([src/ThroughlineBuild.Phases/NewPhase.cs](../../src/ThroughlineBuild.Phases/NewPhase.cs))

Status: **Functional**. Deterministic creator.

1. Read body file. Validate title; collect non-fatal warnings.
2. Emit `WorkerSpawn` with role=creator (no actual worker; the event is for audit symmetry).
3. `ITicketing.CreateTicketAsync`.
4. Emit `TicketWrite` with `create_ticket` action.

### `DraftPhase` ([src/ThroughlineBuild.Phases/DraftPhase.cs](../../src/ThroughlineBuild.Phases/DraftPhase.cs))

Status: **Functional**. Used by `build new` in draft mode and stdin draft mode.

1. Validate non-empty operator text.
2. Build `DraftBriefBuilder` brief.
3. Run worker.
4. Extract `body_markdown` from metadata.
5. Validate minimal sections (title + description).
6. Return `DraftResult.Ok` with body markdown.

### `ScaffoldPhase` ([src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs))

Status: **Functional**. Multi-write batch.

1. Parse op-doc.
2. Validate.
3. Warning gate: abort if warnings and not `--accept-warnings`.
4. Dry-run gate.
5. For each plan in dispatch order:
   1. Create plan-ticket with `plan-ticket` label.
   2. Emit `TicketWrite` per create.
   3. For each brief in the plan: create brief-ticket; emit; call `SetParentAsync` to link.
6. Failures collected in `ScaffoldFailure[]`; processing continues.

---

## The chain rework loop

```
              +-------------------+
              | ImplementPhase    |
              +---------+---------+
                        |
                        | InProgress -> InReview
                        v
              +---------+---------+
              | ReviewPhase       |
              +---------+---------+
                        |
            +-----------+-----------+
            |           |           |
          Pass       Rework        Fail
            |           |           |
            v           v           v
        ShipPhase   round++     return
                        |       StoppedAtReview
                  round < 2 ?
                  +-----+-----+
                  |           |
                yes          no
                  |           |
        (back to top)   return ReworkCapExceeded
```

`MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:11](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L11)) means at most 1 rework + 1 reattempt, i.e. up to 3 implement runs total. Operators wanting more rounds invoke `build rework` manually after the chain returns `ReworkCapExceeded`.

---

## Coordination protocol

How phases communicate without a persistent process:

| Mechanism | What it carries |
|---|---|
| **Plane state field** | The authoritative "what phase comes next?" - each phase checks state on entry. |
| **Plane comment markers** | The SHA stamps (`[planned_at]`, `[implemented_at]`, `[shipped_at]`) that subsequent phases parse via `MarkerParser`. |
| **Plane comment marker prefixes** | `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>` for terminal-state context. |
| **Ticket description** | Plan HTML appended once; review writes nothing to description. |
| **Ticket labels** | `risk:*`, `size:*` from plan; `plan-ticket` from scaffold. |
| **`.build/events/<stem>.jsonl`** | Replayable audit log. `ReviewFeedbackRetriever` reads it to recover the most recent `Rework` verdict. |
| **`.worktrees/ticket-<slug>/`** | The implementer's checkout, scoped to the feature branch. Reviewer reads its diff; shipper rebases + merges from it. |
| **Local git branch `ticket/<slug>`** | The carrier of the actual commits. |

There is no message bus, no broker, no shared in-process state between phase invocations. Every restart re-reads from Plane + git + events.

---

## Sessions

Every `build <verb>` invocation creates exactly one `SessionId` (a Guid). The session id flows into:

- Every `WorkflowEvent.SessionId` field.
- The JSONL file name (legacy) or as a column inside (current convention with `SessionFileNameBuilder`-derived filename stem, [src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs](../../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs)).
- The debug capture directory name (`.build/sessions/<stem>/`).

The `ChainPhase` does **not** subdivide into sub-session ids - the whole chain run shares one. Per-phase `PhaseSessionId` fields on `ChainStep` allow correlation if needed but are populated only when phases use distinct session contexts (not the default wiring).

---

## Event kinds emitted

`EventKind` enum, [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs):

| Kind | Emitted by | Meaning |
|---|---|---|
| `StateTransition` | every phase that transitions | from / to state ids |
| `WorkerSpawn` | phases that spawn a worker (and `NewPhase` for audit symmetry) | worker name + brief + role |
| `VerifierVerdict` | every phase post-worker (or post-verifier in `Review`) | the verdict the orchestrator records |
| `LlmCall` | phases that surface worker LLM usage | tokens / model / wall time |
| `GateFailure` | drift warnings, ship pre-flight failures, conflict-marker hits, regression check fails | gate name + reason |
| `TicketWrite` | every Plane write (description / labels / comments / create / set-parent / rollup) | action + payload summary |
| `ChainStart`, `ChainEnd`, `ReworkRound` | `ChainPhase` | chain lifecycle markers |

Full event-line schema in [docs/event-log-format.md](../event-log-format.md).

---

## Where the chain stops cleanly vs. requires manual triage

- **Clean stop:** `Pass` on `ship` (chain returns `Completed`), `RefusedInitialState` (chain refused to start because state was unexpected), `ReworkCapExceeded` (chain ran out of rework rounds - operator picks up with `build rework` or `build review`).
- **Requires triage:** `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview` (`Fail` verdict), `StoppedAtShip` (gate failure). Each leaves the ticket in whatever state the failing phase left it.

The chain command surfaces these as "triage suggestions" on stderr ([src/ThroughlineBuild.Commands/ChainCommand.cs](../../src/ThroughlineBuild.Commands/ChainCommand.cs)).

---

## Loose ends

- **`MaxReworkRounds = 2` is hardcoded.** Not configurable per ticket or per repo.
- **Single-ticket chain only.** `build chain` rejects multiple ticket ids ([src/ThroughlineBuild.Cli/Program.cs:62-69](../../src/ThroughlineBuild.Cli/Program.cs#L62-L69)). Architecture targets multi-ticket dispatch as a future release.
- **No cross-phase live channel.** ReviewPhase reconstructs the implementer brief deterministically instead of receiving it from ImplementPhase. This is the architecture's "no shared in-memory context with the implementer" principle (architecture Section 5.8).
- **Chain `WorkflowEvent.Data`** carries per-step duration but the schema lives in code, not in [docs/event-log-format.md](../event-log-format.md) for every variation.
- **`Status.Escalate`** from a worker is parseable but not differentially handled - treated as failure in phase code today.
- **No replay verb** (`build replay <session-id>`). Architecture Appendix item 4 notes this as v1.1 potential.
- **Phase ordering documented only in `ChainPhase`** - if operators run phases manually out of order, each phase's state guard rejects.
