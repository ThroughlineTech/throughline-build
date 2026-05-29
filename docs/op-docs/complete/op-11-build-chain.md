# Operation: build-chain

Add a `build chain` command that runs the full ticket spine (plan → implement → review → ship) in a single invocation, with a rework loop between implement and review that retries up to 2 times before escalating to Fail. Each phase still spawns its own isolated worker subprocess - no chat-context warmth, no cache_read compounding. Five briefs across three plans.

## Why this exists

Today, completing a ticket's spine requires four sequential commands. Each is functional and the per-phase subprocess isolation is structurally what delivers the ~9x cost reduction over the old chained-session approach. But operator workflow benefits from a single invocation, and the right place to centralize the implement-then-review handoff is in chain: when review says "rework," the chain spawns a fresh implementer with the prior round's commits and the reviewer's structured feedback, runs implement again, re-reviews. Up to a cap, then escalates.

This op-doc is bigger than the simple "run four phases sequentially" framing of an earlier draft because the rework loop requires changes to existing components. ImplementPhase needs to accept InReview as a valid starting state for rework rounds. ImplementBriefBuilder needs to accept review feedback and surface it to the worker. Templates/implement.md needs a rework-feedback section. Templates/review.md needs explicit criteria distinguishing Pass / Rework / Fail. Plan A bundles those dependency changes so chain ships as a coherent feature, not a half-baked loop blocked on downstream template edits.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Rework loop enablers | - | M |
| B    | ChainPhase with rework loop | A | M |
| C    | CLI | B | S |

Plan A updates ImplementPhase, ImplementBriefBuilder, and the two affected templates so the rework loop has the surfaces it needs. Plan B implements ChainPhase with the rework loop, cap, and single-ticket dispatch. Plan C wires the CLI command.

## Plan A: Rework loop enablers

### Goal

ImplementPhase accepts InReview as a valid starting state. ImplementBriefBuilder accepts an optional ReviewFeedback record and surfaces it to the brief template via a `{{review_feedback_section}}` variable. Templates/implement.md renders a Rework round section when that variable is non-empty. Templates/review.md documents the criteria for choosing among Pass / Rework / Fail verdicts.

Briefs are sequential within this plan because the template change in B02 depends on the variable plumbing from B01.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | rework-phase-plumbing | ImplementPhase accepts InReview state for rework rounds; ImplementBriefBuilder accepts ReviewFeedback record and surfaces as substitution variable | - | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Contracts/ReviewFeedback.cs, src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs, tests/ThroughlineBuild.Phases.Tests/ImplementPhaseReworkTests.cs, tests/ThroughlineBuild.Briefs.Tests/ImplementBriefBuilderReworkTests.cs |
| 02 | template-updates | Templates/implement.md adds rework feedback section; Templates/review.md documents Pass/Rework/Fail criteria | 01 | src/ThroughlineBuild.Briefs/Templates/implement.md, src/ThroughlineBuild.Briefs/Templates/review.md, tests/ThroughlineBuild.Briefs.Tests/Snapshots/implement-rework.txt, tests/ThroughlineBuild.Briefs.Tests/Snapshots/review-verdict-criteria.txt |

### Briefs - detail

#### Brief 01: rework-phase-plumbing

Goal: ImplementPhase accepts a state precondition of `Ready OR InReview` (instead of strictly `Ready`). ImplementBriefBuilder accepts an optional ReviewFeedback parameter and constructs a `{{review_feedback_section}}` substitution variable that is either empty string (initial implement round) or a formatted block including the reviewer's rationale and failed checks (rework round).

Inputs:
- Existing ImplementPhase with its current Ready-only state check
- Existing ImplementBriefBuilder with its current Build signature
- The `{{review_feedback_section}}` variable will be referenced by template changes in B02

Outputs:
- `ReviewFeedback` record (in Contracts): `string Rationale, IReadOnlyList<string> ChecksFailed, int ReworkRoundNumber` (1 for first rework, 2 for second)
- `ImplementPhase.RunAsync` updated:
  - Initial state check now accepts `Ready` OR `InReview` (was: Ready only)
  - If starting state is InReview, the phase treats this as a rework round and expects options to include ReviewFeedback (throw clear error if missing)
  - If starting state is Ready, the phase treats as initial implement and ignores any ReviewFeedback in options
  - Transitions on rework: InReview → InProgress at start, InProgress → InReview at end (same shape as initial implement; the InReview start state is the only relaxation)
- `ImplementPhaseOptions` record gains: `ReviewFeedback? ReviewFeedback` (nullable; null for initial round)
- `ImplementBriefBuilder.Build` signature gains optional `ReviewFeedback? reviewFeedback = null` parameter
- The builder constructs `review_feedback_section` variable:
  - If reviewFeedback is null: empty string
  - If reviewFeedback is non-null: formatted markdown block with the rationale and checks_failed list, plus the round number, prefixed with a heading like `## Rework round {n} - reviewer feedback`
- Variable is added to the substitution dictionary alongside existing variables
- `ImplementResult` record gains: `int ReworkRoundNumber` (0 for initial implement; 1 or 2 for rework rounds) - so chain can track which round produced which result
- Tests for ImplementPhase rework:
  - Initial implement from Ready state works as before (no review feedback expected, none provided)
  - Rework from InReview state requires ReviewFeedback in options; missing it throws clear error
  - Rework from InReview state transitions to InProgress and back to InReview correctly
  - ReworkRoundNumber propagates from options to result
- Tests for ImplementBriefBuilder rework:
  - With reviewFeedback=null, the substituted brief has an empty `{{review_feedback_section}}` (matches existing byte-equivalent baseline from the externalization op-doc)
  - With reviewFeedback set, the substituted brief includes the rationale and checks_failed list verbatim
  - Round number appears in the section heading

Acceptance:
- [ ] ReviewFeedback record exists in Contracts
- [ ] ImplementPhase accepts Ready OR InReview as valid starting states
- [ ] InReview start requires ReviewFeedback to be supplied; missing it fails fast
- [ ] ImplementBriefBuilder accepts optional ReviewFeedback and constructs the substitution variable
- [ ] Variable is empty string when null, formatted block when present
- [ ] ImplementResult carries ReworkRoundNumber
- [ ] Existing initial-implement behavior unchanged (snapshot test still passes for the no-feedback case)
- [ ] Tests pass for both rework and initial cases

Notes: The builder is responsible for the empty-vs-formatted decision, not the template. This matches the existing pattern (see project_notes_section in PlanBriefBuilder): the template just has `{{review_feedback_section}}` at the appropriate spot, builder supplies either "" or a complete block. No conditional logic in template substitution.

The state-precondition relaxation in ImplementPhase is a behavioral change with downstream implications: standalone `build implement <id>` invocations could now succeed against an InReview ticket if the operator supplies feedback. That's probably fine but worth documenting in the implement command's help text. If you want to forbid standalone InReview entry and only allow it from chain, ImplementPhaseOptions could carry an `IsChainInvocation` flag that gates the InReview acceptance. Cleaner architecturally but more surface area. v1 stays open; v1.1 can add the gate if it matters.

The round number in the result lets chain build clear "rework round 2 of 2" log lines without tracking that state itself. Small detail but useful for operator-facing output.

OOS:
- Do not change the ReviewPhase signature or behavior (review reads InReview state regardless of round; round semantics are an implement-side concern)
- Do not propagate ReviewFeedback into PlanPhase or ShipPhase (only implement uses it)
- Do not implement automatic detection of rework round from ticket history (chain supplies the round number explicitly)
- Do not add a separate ReworkPhase class (the architectural choice is "rework is implement-with-feedback," not a distinct phase)
- Do not modify the WORKER_RESULT envelope schema (existing fields suffice; ReworkRoundNumber lives in the orchestrator's ImplementResult, not in the worker's output)

#### Brief 02: template-updates

Goal: Templates/implement.md renders a rework feedback section when the corresponding variable is non-empty. Templates/review.md documents explicit criteria for Pass / Rework / Fail verdicts so the reviewer's verdict is consistent and the chain's rework loop converges.

Inputs:
- Current Templates/implement.md (byte-equivalent baseline from externalization op-doc)
- Current Templates/review.md (same)
- The `{{review_feedback_section}}` variable from Brief 01 (B02 references it; B01 supplies it)

Outputs:
- Templates/implement.md updated to include `{{review_feedback_section}}` near the top, after the ticket context but before the "Your job" section. When the variable is non-empty, the worker sees the rework feedback before reading the job description, which primes the right framing. When empty, the variable substitutes to nothing and the brief reads as it does today.
- Templates/review.md updated with an explicit "Verdict criteria" section documenting:
  - **Pass:** all acceptance criteria met, automated checks pass, implementation matches the plan, no significant quality issues
  - **Rework:** implementation is on the right track but execution incomplete; specific named issues the implementer can address (missing edge case, incomplete tests, partial coverage, minor quality issue). The reviewer can articulate exactly what to fix.
  - **Fail:** implementation fundamentally diverges from the plan, OR the plan itself is wrong, OR there are compounding architectural problems that can't be fixed in-place. Needs replanning or operator intervention - not rework.
  - The discriminating question between Rework and Fail: "Can the implementer fix this with the current plan, or does the plan itself need revision?" Yes → Rework. No → Fail.
- Templates/review.md also notes: rework rounds are capped at 2 (chain's responsibility, not the reviewer's). The reviewer should not soften a verdict because they think the implementer "won't get another chance"; verdicts are based on the work, not on the loop's state.
- New snapshot fixtures capturing the updated template content
- Updated existing snapshot tests if any depend on the prior content

Acceptance:
- [ ] Templates/implement.md references `{{review_feedback_section}}` at the documented location
- [ ] Templates/review.md includes a Verdict criteria section with Pass / Rework / Fail criteria
- [ ] Initial-implement snapshot (with empty review_feedback_section) is byte-equivalent to prior baseline plus the new variable's empty expansion
- [ ] Rework-implement snapshot (with populated review_feedback_section) shows the feedback block at the documented location
- [ ] Review template's new section is captured in a snapshot test
- [ ] All snapshot tests pass

Notes: The rework feedback section sits ABOVE the standard "Your job" text deliberately. The worker should read "you are working on a rework round; here's the prior reviewer's feedback" first, then the standard job framing. Putting it below would make the worker form an initial framing then encounter the feedback as a correction - less direct.

For Templates/review.md, the discriminating question phrasing ("Can the implementer fix this with the current plan?") is the load-bearing test for verdict consistency. Different reviewer workers running on similar tickets should reach similar verdicts because they apply the same discriminating question. If verdict consistency suffers in practice, the criteria may need tightening; that's a future tuning concern.

The "rework is capped at 2" note in the review template is informational only - it tells the reviewer not to game the loop ("they only have one chance left, I'll soften the verdict"). The reviewer's job is to verdict the work; chain's job is to handle the loop semantics.

OOS:
- Do not enrich Templates/implement.md beyond adding the rework section variable (other enrichment is the implement-brief enrichment op-doc's scope, deferred)
- Do not enrich Templates/review.md beyond the verdict criteria section (other enrichment deferred)
- Do not add any other new variables to either template
- Do not change the WORKER_RESULT envelope spec in either template
- Do not add a verdict-specific subsection structure (the rationale prose carries the detail; rigid templating is over-engineering)

## Plan B: ChainPhase with rework loop

### Goal

ChainPhase runs plan → implement → review → ship sequentially on a single ticket, with a rework loop between implement and review that retries up to 2 times before escalating to Fail. Each phase invocation is its own subprocess - no shared state, no warm chat context. State determination from current ticket state at chain start: refuses InProgress, Done, Cancelled.

Briefs are sequential within this plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | chain-phase | ChainPhase orchestrator with rework loop, cap, single-ticket dispatch, structured result | A | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Contracts/ChainResult.cs, src/ThroughlineBuild.Contracts/ChainStep.cs, src/ThroughlineBuild.Contracts/ChainOutcome.cs, tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs |
| 04 | chain-events | Event log additions: ChainStart and ChainEnd events that wrap per-phase events; rework round events for visibility | 03 | src/ThroughlineBuild.EventLog/EventKinds.cs (extended), src/ThroughlineBuild.Phases/ChainPhase.cs (extended), tests/ThroughlineBuild.Phases.Tests/ChainPhaseEventTests.cs |

### Briefs - detail

#### Brief 03: chain-phase

Goal: ChainPhase orchestrates the full spine on a single ticket with a rework loop. Each phase runs in its own subprocess via the existing phase classes. The chain itself is deterministic - no LLM call, no agent. It sequences calls, inspects structured results, manages the rework loop counter.

Inputs:
- Existing PlanPhase, ImplementPhase (now with rework support from Plan A), ReviewPhase, ShipPhase
- The IPlaneTicketing for state inspection at chain start
- The IEventLog for chain-level events

Outputs:
- `ChainStep` record: `string PhaseName, int ReworkRoundNumber, Status Status, string? FailureReason, ReviewVerdict? Verdict, TimeSpan Duration, string? PhaseSessionId`
  - ReworkRoundNumber=0 for initial implement; 1 or 2 for rework rounds; -1 or unset for non-implement phases
  - Verdict is populated only for review phase steps
- `ChainOutcome` enum: `Completed | StoppedAtPlan | StoppedAtImplement | StoppedAtReview | StoppedAtShip | ReworkCapExceeded | RefusedInitialState`
- `ChainResult` record: `string TicketId, IReadOnlyList<ChainStep> Steps, ChainOutcome Outcome, TimeSpan TotalDuration, string? FinalRationale` (FinalRationale populated when outcome is ReworkCapExceeded or StoppedAtReview - holds the final reviewer's rationale so operator knows what couldn't be fixed)
- `ChainPhase` class with `RunAsync(ChainPhaseOptions options, CancellationToken ct)`:
  1. Read ticket state from Plane
  2. Determine starting phase from state:
     - Backlog → start at plan
     - Ready → start at implement (initial round)
     - InReview → start at review
     - InProgress → return ChainResult with Outcome=RefusedInitialState; do nothing else
     - Done → return ChainResult with Outcome=RefusedInitialState
     - Cancelled → return ChainResult with Outcome=RefusedInitialState
  3. From the determined start, run phases in sequence:
     - **Plan:** if part of the chain, invoke PlanPhase. If Status=Ok, append step, continue. Otherwise append step with the non-Ok status, return ChainResult with Outcome=StoppedAtPlan.
     - **Implement loop (initial round + up to 2 rework rounds):**
       a. Invoke ImplementPhase with options.ReworkRoundNumber=current. Initial round (n=0): no ReviewFeedback. Rework rounds (n=1 or 2): pass the prior review's Rationale and ChecksFailed via ReviewFeedback.
       b. If Status != Ok: append step, return ChainResult with Outcome=StoppedAtImplement.
       c. Invoke ReviewPhase. Append review step.
       d. Verdict=Pass: exit loop, proceed to ship.
       e. Verdict=Fail: return ChainResult with Outcome=StoppedAtReview. Populate FinalRationale.
       f. Verdict=Rework AND current round < 2: increment counter, loop back to (a) with feedback.
       g. Verdict=Rework AND current round == 2: return ChainResult with Outcome=ReworkCapExceeded. Populate FinalRationale. The third rework attempt does NOT happen - cap is 2 reworks (so 3 implement attempts total: initial + 2 reworks).
     - **Ship:** invoke ShipPhase. If Outcome=success, append step with Status=Ok, return ChainResult with Outcome=Completed. Otherwise append step, return ChainResult with Outcome=StoppedAtShip.
  4. ChainResult is returned to the caller; chain does not throw on phase failures (the caller decides exit code based on Outcome)
- `ChainPhaseOptions` record: `string TicketId, bool Debug`
- Tests:
  - Happy path: Backlog ticket, all phases pass, ReworkRoundNumber=0 throughout, Outcome=Completed, 4 steps
  - Plan fails: Outcome=StoppedAtPlan, only Steps[0] populated
  - Implement initial fails: Outcome=StoppedAtImplement, 2 steps (plan + implement-round-0)
  - Review returns Rework once then Pass: 6 steps (plan + impl-0 + review-Rework + impl-1 + review-Pass + ship), Outcome=Completed
  - Review returns Rework twice then Pass: 8 steps, Outcome=Completed
  - Review returns Rework three times: chain hits cap, Outcome=ReworkCapExceeded, FinalRationale populated with the third review's rationale, 7 steps captured (plan + 3 cycles of impl+review). The fourth implement attempt does NOT run.
  - Review returns Fail at any cycle: chain stops, Outcome=StoppedAtReview, FinalRationale populated with that review's rationale
  - Ship gate failure: 8+ steps depending on rework rounds, Outcome=StoppedAtShip
  - Initial state InProgress: 0 steps, Outcome=RefusedInitialState
  - Initial state Done: 0 steps, Outcome=RefusedInitialState

Acceptance:
- [ ] ChainPhase exists with RunAsync method that returns ChainResult
- [ ] Starting phase determined from Plane state per the documented table
- [ ] InProgress / Done / Cancelled produce RefusedInitialState outcome with empty Steps
- [ ] Rework loop runs up to cap=2 reworks (3 implement attempts total) before escalating to ReworkCapExceeded
- [ ] Each rework round passes the prior review's Rationale + ChecksFailed to ImplementPhase via ReviewFeedback
- [ ] ChainStep.ReworkRoundNumber populated correctly per round
- [ ] ChainResult.FinalRationale populated when outcome involves a review verdict (StoppedAtReview, ReworkCapExceeded)
- [ ] Each phase runs in its own subprocess (no shared state, no warm context - assert by checking that subprocess SessionIds are distinct across phases in test captures)
- [ ] Tests pass

Notes: The rework cap is hard-coded to 2 in v1. If a future need arises to tune it per project or per ticket, it can become a config option in `.build/config.toml` `[chain]` section. For now, the value is a constant inside ChainPhase with a clear named identifier (e.g., `private const int MaxReworkRounds = 2`).

Each rework round spawns a fresh ImplementPhase invocation, which spawns a fresh worker subprocess. The prior implementer's chat-context is gone. What the new implementer has: the plan_html (from Plane ticket description), the current branch state (from the worktree, including the prior round's commits), and the structured ReviewFeedback (rationale + checks_failed). That's intentional: the cost advantage of subprocess isolation depends on this. Warm-implementer designs save cache_read but compound the per-cycle cost - over many tickets, fresh subprocesses win.

The reviewer also runs fresh each round. It reads the current branch state and the plan, makes a verdict. The reviewer doesn't get the prior reviewer's feedback - if reviewer-1 says "missing tests for case X" and reviewer-2 looks at the rework and the test for case X is there, reviewer-2 doesn't need to know reviewer-1's framing. They just verdict the work.

Mid-implement state (InProgress) refusal at chain start is intentional and conservative. Worktrees that exist in InProgress state may have uncommitted changes, half-applied diffs, or other partial state from a prior crashed or killed run. Resuming from there with a fresh agent is non-deterministic and risky. Operator manually cleans up (delete worktree, transition ticket back to Ready) and re-runs.

No new ticket states for the rework loop. The existing state machine (Backlog / Ready / InProgress / InReview / Done / Cancelled) handles rework cycles by repeatedly traversing InProgress → InReview as the loop iterates. If future requirements call for an explicit "Rework" or "QA" state, that's a separate op-doc; v1 keeps the state model simple.

OOS:
- Do not implement a "smart resume" that detects clean InProgress state and proceeds (worktree might still hide problems; the simplification is worth the rare false-refuse case)
- Do not implement parallel rework attempts (cap=2 means at most 3 sequential implement runs; never concurrent)
- Do not let the cap be configurable per ticket (one global constant in v1)
- Do not implement chain-level retry on transient errors (Plane API hiccups bubble up; operator re-runs)
- Do not implement multi-ticket dispatch (single-ticket in v1; multi-ticket is a separate op-doc)
- Do not modify any phase class beyond what Plan A specified (PlanPhase, ReviewPhase, ShipPhase unchanged)
- Do not add new ticket states for rework cycles (use existing state machine)

#### Brief 04: chain-events

Goal: Emit ChainStart and ChainEnd events around the per-phase events, plus a ReworkRound event for each rework cycle. Downstream analysis (comparison harness, ad-hoc queries) can group all phases and reworks under a single chain invocation by joining on TicketId and time window.

Inputs:
- Existing EventKinds enum and event log infrastructure
- ChainPhase from Brief 03

Outputs:
- EventKinds enum gains: `ChainStart`, `ChainEnd`, `ReworkRound` (use the next available IDs; document the chosen values in the EventKinds source comment)
- ChainPhase emits:
  - At start: `Kind=ChainStart`, TicketId, Phase=-1, Data: `{starting_at_phase: "<plan|implement|review>", initial_state: "<state>", chain_session_id: "<sid>"}`
  - At each rework round trigger: `Kind=ReworkRound`, TicketId, Phase=1 (implement), Data: `{round: 1|2, verdict_that_triggered: "Rework", rationale_preview: "<first 200 chars>"}`
  - At end: `Kind=ChainEnd`, TicketId, Phase=-1, Data: `{outcome: "<ChainOutcome value>", phases_run: <int>, rework_rounds: <int>, total_duration_ms: <int>, final_rationale_preview: "<first 200 chars or null>"}`
- The chain has its own SessionId (distinct from each phase's SessionId). Per-phase events keep their own SessionIds and emit normally.
- The rationale_preview fields are truncated to 200 chars to keep events compact; full rationale is in the chain result and the review event itself.
- Tests:
  - Successful chain emits ChainStart, then per-phase events, then ChainEnd (in that order)
  - Chain with 1 rework emits 1 ReworkRound event between the first review and second implement
  - Chain with cap exceeded emits ReworkRound events for round 1 and 2, but NOT a third (no third attempt happens)
  - ChainEnd Data includes the correct outcome value and phase/rework counts
  - Initial-state refusal emits ChainStart and ChainEnd with phases_run=0 (no per-phase events between)

Acceptance:
- [ ] EventKinds.ChainStart, ChainEnd, ReworkRound exist with documented values
- [ ] ChainPhase emits all three event kinds in the correct sequence
- [ ] Per-phase events are unmodified (no schema change to existing event kinds)
- [ ] ReworkRound events fire only when a rework actually triggers (not preemptively, not after cap)
- [ ] Tests pass

Notes: The ChainSessionId in ChainStart's Data lets downstream tooling group all events from one chain invocation. Without it, joining requires a time-window heuristic. With it, the join is exact.

The rationale_preview is a pragmatic concession: the full reviewer rationale can be long (hundreds of words for a complex Rework), and putting it in every ReworkRound event bloats the event log. 200 chars is enough for the operator to skim what triggered the rework while keeping events tight. Operators wanting the full rationale read the chain result or the review's WORKER_RESULT envelope.

The Phase field in ReworkRound is set to 1 (implement) because that's the next phase that will run as a result of the rework signal. Some downstream tooling may filter by phase; this keeps rework events grouped with implement-phase queries naturally.

OOS:
- Do not modify per-phase events
- Do not propagate the chain SessionId into per-phase events (each phase emits its own SessionId; downstream joins on TicketId + time)
- Do not implement event-log query tools (downstream tooling concern)
- Do not emit per-second progress events during long phases (subprocess wall clock is the heartbeat; events are at phase boundaries)

## Plan C: CLI

### Goal

`build chain` command works end-to-end for a single ticket. Operator-facing output streams per-phase status; exit code reflects ChainOutcome.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | chain-cli | `build chain <ticket-id>` command with debug flag, per-phase streamed output, exit codes mapped to outcomes | B | src/ThroughlineBuild.Cli/Commands/ChainCommand.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ChainCommandTests.cs |

### Briefs - detail

#### Brief 05: chain-cli

Goal: `build chain <id>` runs ChainPhase, streams per-phase status to stdout as phases complete, prints the chain result summary at end, exits with code mapped to ChainOutcome.

Inputs:
- ChainPhase from Plan B
- Existing CLI command-dispatch pattern

Outputs:
- `ChainCommand` class implementing the existing command interface
- CLI usage:
  ```
  build chain <ticket-id> [--debug]
  ```
  - `<ticket-id>` required (single ticket only in v1)
  - `--debug` forwarded to ChainPhase (each phase honors --debug per its existing behavior; chain doesn't add a separate debug capture beyond the per-phase ones)
- Per-phase output (streamed):
  ```
  [SURLF-42] chain starting (initial state: Backlog)
  [SURLF-42] plan: Ok (5m 18s)
  [SURLF-42] implement (round 0): Ok (2m 27s)
  [SURLF-42] review: Rework
  [SURLF-42] implement (round 1): Ok (1m 53s)
  [SURLF-42] review: Pass (4m 6s)
  [SURLF-42] ship: Ok (1s)
  [SURLF-42] chain complete (14m 5s)
  ```
- On rework-cap-exceeded:
  ```
  [SURLF-42] chain starting (initial state: Backlog)
  [SURLF-42] plan: Ok (5m 18s)
  [SURLF-42] implement (round 0): Ok (2m 27s)
  [SURLF-42] review: Rework
  [SURLF-42] implement (round 1): Ok (1m 53s)
  [SURLF-42] review: Rework
  [SURLF-42] implement (round 2): Ok (1m 41s)
  [SURLF-42] review: Rework
  [SURLF-42] chain stopped: rework cap exceeded after 3 implement attempts

  Final reviewer rationale:
  <full rationale text from FinalRationale field>

  Checks failed:
  - <check 1>
  - <check 2>

  Operator triage: ticket left in InReview state. Options:
  - Inspect the worktree at .worktrees/ticket-42-... and resolve manually, then build ship 42
  - Transition ticket to Cancelled if abandoning
  - Replan via build close 42 <reason> followed by a new ticket with refined acceptance criteria
  ```
- On other stop outcomes: similar format, with outcome-specific summary and operator-triage suggestions
- Exit codes:
  - 0: ChainOutcome.Completed
  - 2: RefusedInitialState (no work done; operator error or stale state)
  - 3: StoppedAtPlan (planning failed; ticket needs replanning)
  - 4: StoppedAtImplement (implementation failed before reaching review; worktree may need cleanup)
  - 5: StoppedAtReview (review returned Fail)
  - 6: ReworkCapExceeded (review kept returning Rework)
  - 7: StoppedAtShip (ship gate failed; ticket in InReview)
- Tests:
  - Happy path: chain command runs, output matches expected format, exit code 0
  - Each non-success outcome produces the expected output format and exit code
  - --debug flag is forwarded (verify by checking that each phase's debug behavior is exercised; e.g. session dirs are created)
  - Initial-state-refusal produces clear stderr error and exit code 2

Acceptance:
- [ ] `build chain --help` documents the command shape
- [ ] Single-ticket invocation works end-to-end
- [ ] Output streams per phase as it completes (not buffered until end)
- [ ] Rework rounds visible in output with round numbers
- [ ] Cap-exceeded case surfaces final rationale and operator-triage suggestions clearly
- [ ] Exit codes documented in --help and matched to ChainOutcome values
- [ ] Tests pass

Notes: The "Operator triage" suggestions on stop outcomes matter for operator UX. Chain telling the operator what to do next ("inspect the worktree, build ship, or replan") is friendlier than just "failed, figure it out." Suggestions are specific to outcome.

Multi-ticket dispatch (`build chain 42 43 44`) is explicitly v1-out-of-scope. The command rejects multiple positional ticket arguments with a clear error: "build chain accepts exactly one ticket ID in v1; multi-ticket dispatch is planned for a future release." That keeps the door open and signals intent.

OOS:
- Do not implement multi-ticket dispatch in this brief
- Do not implement --dry-run (chain has too much side-effect surface to meaningfully dry-run; if needed, run plan/implement/review/ship separately to inspect intermediate state)
- Do not implement --from <phase> to override the starting phase (ticket state determines starting phase; override creates state-consistency footguns)
- Do not implement progress callbacks within phases (each phase's internal progress is its own concern; chain reports at phase boundaries only)
- Do not implement interactive confirmation prompts before destructive actions like ship (ship's own gate checks are the safety; chain trusts them)

## What done looks like

`build chain <id>` runs the full ticket spine in one invocation. Each phase is its own isolated subprocess - no shared chat context, no cache_read compounding across phases. The architectural cost advantage (~9x cheaper than old chained-session) is preserved.

The rework loop handles the common review-then-iterate pattern: when review says Rework, chain spawns a fresh implementer with the prior round's commits and structured review feedback (rationale + checks_failed), runs implement again, re-reviews. Cap of 2 reworks means up to 3 implement attempts total per ticket. Beyond that, chain escalates to ReworkCapExceeded and surfaces the final rationale plus operator-triage suggestions.

Verdict criteria (Pass / Rework / Fail) are explicit in the review template, with a discriminating question between Rework and Fail: "Can the implementer fix this with the current plan, or does the plan itself need revision?" Yes → Rework. No → Fail. Different reviewer runs on similar work should converge on similar verdicts because they apply the same criteria.

State machine stays simple: Backlog → Ready → InProgress → InReview → Done. No new states for rework cycles (rework just re-traverses InProgress → InReview). Future complexification (QA stage, explicit Rework state) can layer on if needed, but v1 keeps the existing state set.

InProgress / Done / Cancelled starting states are refused - chain doesn't attempt to resume partial work. Operator handles cleanup outside the chain command if a prior run crashed or was killed.

Single-ticket only in v1. Multi-ticket sequencing is a separate concern (and brings worktree-per-ticket questions, sequential-vs-parallel decisions, scope that doesn't fit the single-ticket scaffolding cleanly). Defer to a future op-doc.