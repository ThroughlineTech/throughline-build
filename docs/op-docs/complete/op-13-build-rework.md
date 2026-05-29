# Operation: build-rework

Add a `build rework` command that takes a ticket whose most recent review verdict was Rework and re-invokes the implementer with that feedback, reusing the existing worktree and feature branch. The command is standalone (not chain-dependent) so operators can drive rework cycles by hand. Five briefs across two plans. This op-doc extracts and supersedes the "rework loop enablers" plan from the build-chain op-doc; once build-rework ships, chain becomes a thin orchestrator that calls ReworkPhase in its loop.

## Why this exists

Today's spine has a dead-end at Rework. Review transitions the ticket InReview → InProgress on a Rework verdict and emits the rationale + checks_failed to the event log, but no command picks up from there. Implement only accepts Ready as a starting state; ship requires InReview; chain doesn't exist yet. The operator's only recovery path is manual: fix the issues by hand, commit, manually move the ticket back to InReview, re-run review.

`build rework <id>` closes the gap. It reads the most recent review event for the ticket, extracts the structured feedback (rationale + checks_failed), constructs an implement brief that includes the feedback, and invokes a fresh implementer subprocess against the existing worktree. The implementer makes additional commits to address the issues; the ticket transitions back to InReview; the operator runs `build review <id>` to re-verdict.

No cap on rework invocations when used standalone - the operator is the cap. If they want to invoke `build rework 147` five times, that's their call. The cap=2 design lives in build-chain, where automatic looping needs a stop condition.

This op-doc also benefits build-chain: once ReworkPhase exists as a standalone class, chain's rework loop logic becomes "call ReworkPhase, increment counter, branch on result" instead of inlining the entire rework flow. The build-chain op-doc will be revised to consume ReworkPhase from this op-doc as a dependency.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Foundation: phase plumbing, template updates, feedback retrieval | - | M |
| B    | ReworkPhase + CLI command | A | S |

## Plan A: Foundation

### Goal

ImplementPhase accepts InProgress as a valid starting state when ReviewFeedback is supplied. ImplementBriefBuilder accepts an optional ReviewFeedback record and surfaces it to the brief template via a `{{review_feedback_section}}` variable. Templates/implement.md renders the rework feedback section when populated. Templates/review.md documents Pass / Rework / Fail criteria so verdicts are consistent. ReviewFeedbackRetriever helper class reads the most recent Review event for a ticket from the event log and reconstructs a ReviewFeedback record.

Briefs are partially sequential: B01 produces the contracts B02 references, B03 is independent and can land in parallel.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | rework-phase-plumbing | ImplementPhase accepts InProgress state when ReviewFeedback is supplied; ImplementBriefBuilder accepts ReviewFeedback and surfaces as substitution variable; ReviewFeedback record + ImplementResult.ReworkRoundNumber added to Contracts | - | src/ThroughlineBuild.Contracts/ReviewFeedback.cs, src/ThroughlineBuild.Contracts/Models/ImplementResult.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ImplementPhaseOptions.cs, src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs, tests/ThroughlineBuild.Phases.Tests/ImplementPhaseReworkTests.cs, tests/ThroughlineBuild.Briefs.Tests/ImplementBriefBuilderReworkTests.cs |
| 02 | template-updates | Templates/implement.md adds rework feedback section; Templates/review.md documents Pass/Rework/Fail verdict criteria | 01 | src/ThroughlineBuild.Briefs/Templates/implement.md, src/ThroughlineBuild.Briefs/Templates/review.md, tests/ThroughlineBuild.Briefs.Tests/Snapshots/implement-rework.txt, tests/ThroughlineBuild.Briefs.Tests/Snapshots/review-verdict-criteria.txt |
| 03 | review-feedback-retrieval | ReviewFeedbackRetriever helper that scans event log for a ticket's most recent Review event and reconstructs a ReviewFeedback record | - | src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs, tests/ThroughlineBuild.EventLog.Tests/ReviewFeedbackRetrieverTests.cs |

### Briefs - detail

#### Brief 01: rework-phase-plumbing

Goal: ImplementPhase accepts ReviewFeedback in its options. When ReviewFeedback is present, the phase accepts InProgress as a valid starting state and skips the Ready → InProgress transition (the ticket is already InProgress; review put it there). The end transition (InProgress → InReview) still fires. ImplementBriefBuilder accepts an optional ReviewFeedback parameter and constructs a `{{review_feedback_section}}` substitution variable.

Inputs:
- Existing ImplementPhase with Ready-only state precondition
- Existing ImplementBriefBuilder.Build signature
- The current review behavior of transitioning InReview → InProgress on Rework verdict (observed in dogfood run on TLB-147)

Outputs:
- `ReviewFeedback` record in Contracts:
  ```csharp
  public sealed record ReviewFeedback(
      string Rationale,
      IReadOnlyList<string> ChecksFailed,
      int ReworkRoundNumber  // 1 for first rework, increments thereafter
  );
  ```
- `ImplementPhaseOptions` gains `ReviewFeedback? ReviewFeedback` (nullable; null = initial round)
- `ImplementPhase.RunAsync` state and transition logic:
  - **Initial round** (`options.ReviewFeedback == null`): require starting state = Ready; transition Ready → InProgress at start; transition InProgress → InReview at end (existing behavior)
  - **Rework round** (`options.ReviewFeedback != null`): require starting state = InProgress (review put it there); no start-state transition; transition InProgress → InReview at end
  - Invalid combinations produce clear errors: "rework round invoked but ticket is in Ready - no review has run yet"; "initial round invoked but ticket is in InProgress - did you mean to invoke rework?"
- `ImplementBriefBuilder.Build` signature gains optional `ReviewFeedback? reviewFeedback = null` parameter
- The builder constructs `review_feedback_section` substitution variable:
  - If `reviewFeedback == null`: empty string
  - If `reviewFeedback != null`: formatted markdown block with the rationale (verbatim), checks_failed list, and round number, prefixed with heading like `## Rework round {n} - reviewer feedback`
- `ImplementResult` gains `int ReworkRoundNumber` field (0 for initial round, 1+ for rework rounds) - lets downstream code report rework attempts cleanly
- Tests for ImplementPhase rework:
  - Initial implement from Ready state with no ReviewFeedback: works as before
  - Rework from InProgress state with ReviewFeedback: accepts state, skips start transition, ends at InReview
  - Initial implement from InProgress (no ReviewFeedback): clear error
  - Rework from Ready (with ReviewFeedback): clear error
  - ReworkRoundNumber propagates from options to result
- Tests for ImplementBriefBuilder rework:
  - With `reviewFeedback == null`, substituted brief has empty `{{review_feedback_section}}` (byte-equivalent to prior baseline)
  - With `reviewFeedback != null`, substituted brief includes rationale and checks_failed list verbatim
  - Round number appears in section heading

Acceptance:
- [ ] ReviewFeedback record exists in Contracts
- [ ] ImplementPhase accepts Ready (initial) or InProgress (rework) as valid starting states with the precondition matching feedback presence
- [ ] Invalid state/feedback combinations fail with clear error messages
- [ ] ImplementBriefBuilder accepts optional ReviewFeedback and constructs the substitution variable
- [ ] Variable is empty string when null, formatted block when present
- [ ] ImplementResult carries ReworkRoundNumber
- [ ] Existing initial-implement behavior unchanged (existing snapshot test passes)
- [ ] Tests pass for both rework and initial cases

Notes: The state-precondition split (Ready=initial, InProgress=rework) is sharper than the chain op-doc's earlier "Ready OR InReview" framing. The dogfood run on TLB-147 confirmed review transitions to InProgress on Rework, so InProgress is the canonical rework-start state. InReview is what implement transitions TO at the end, not FROM at the start.

The error messages on invalid combinations matter for operator UX. If someone runs `build implement 147` against an InProgress ticket without supplying feedback, the error tells them what's wrong: "did you mean to invoke rework?"

The `ReworkRoundNumber` field on ImplementResult is supplied by the caller (chain or operator), not derived. ImplementPhase just passes it through. Chain tracks the round counter; standalone rework can supply 1 by default or read from prior ImplementResults if the operator cares.

OOS:
- Do not change ReviewPhase behavior (it already transitions InReview → InProgress on Rework, which is correct)
- Do not add a ReworkPhase class here (Plan B B04)
- Do not implement event log retrieval here (Plan A B03)
- Do not modify the WORKER_RESULT envelope schema
- Do not change PlanPhase or ShipPhase
- Do not add automatic detection of rework round number from prior implement events (operator/chain supplies it)

#### Brief 02: template-updates

Goal: Templates/implement.md includes a rework feedback section that renders when populated. Templates/review.md documents explicit criteria for Pass / Rework / Fail verdicts so reviewer behavior is consistent and the rework loop converges.

Inputs:
- Current Templates/implement.md
- Current Templates/review.md
- The `{{review_feedback_section}}` variable from B01

Outputs:
- Templates/implement.md updated to reference `{{review_feedback_section}}` near the top of the brief, after the ticket context but before the "Your job" framing
  - When populated: worker reads "you are working on a rework round; here is the reviewer's feedback" before forming initial framing
  - When empty: the variable substitutes to nothing; brief reads as it does today
- Templates/review.md updated with a "Verdict criteria" section documenting:
  - **Pass:** all acceptance criteria met, automated checks pass, implementation matches the plan, no significant quality issues
  - **Rework:** implementation on the right track but execution incomplete; specific named issues the implementer can address (missing edge case, incomplete tests, partial coverage, minor quality issue). The reviewer can articulate exactly what to fix.
  - **Fail:** implementation fundamentally diverges from the plan, OR the plan itself is wrong, OR there are compounding architectural problems that can't be fixed in-place. Needs replanning or operator intervention.
  - Discriminating question between Rework and Fail: "Can the implementer fix this with the current plan, or does the plan itself need revision?" Yes → Rework. No → Fail.
- Templates/review.md also notes: when invoked through chain, rework rounds are capped at 2 (chain's responsibility). The reviewer should not soften a verdict because they think the implementer "won't get another chance" - verdicts are based on the work, not the loop state. When invoked through standalone build rework, there is no automatic cap; verdict the work as it is.
- New snapshot fixtures capturing the updated template content
- Existing snapshot tests updated for the new variable in implement.md (empty case is byte-equivalent to prior baseline)

Acceptance:
- [ ] Templates/implement.md references `{{review_feedback_section}}` at the documented location
- [ ] Templates/review.md includes a Verdict criteria section with the three verdicts and the discriminating question
- [ ] Initial-implement snapshot (empty feedback section) is byte-equivalent to prior baseline plus the new variable's empty expansion
- [ ] Rework-implement snapshot (populated feedback section) shows the feedback block at the documented location
- [ ] Review template's Verdict criteria section captured in a snapshot test
- [ ] All snapshot tests pass

Notes: The rework feedback section sits ABOVE the "Your job" framing deliberately. The worker should know this is a rework round before reading the standard job text; putting it below would force the worker to form an initial framing then encounter the feedback as a correction.

For Templates/review.md, the discriminating question phrasing is the load-bearing test for verdict consistency. Different reviewer runs on similar work should converge on similar verdicts because they apply the same question. If verdict consistency suffers in practice once we have data, the criteria tighten in a follow-up.

The note about chain cap vs standalone rework being uncapped is informational only. The reviewer's job is to verdict the work; loop semantics are caller concerns.

OOS:
- Do not enrich Templates/implement.md beyond adding the rework section variable
- Do not enrich Templates/review.md beyond the Verdict criteria section
- Do not add other new variables to either template
- Do not change the WORKER_RESULT envelope spec in either template
- Do not add verdict-specific subsection structure (the rationale prose carries the detail; rigid templating is over-engineering)

#### Brief 03: review-feedback-retrieval

Goal: ReviewFeedbackRetriever helper class scans the event log for a ticket's most recent Review event and reconstructs a ReviewFeedback record from it. Used by build rework CLI (Plan B) to look up feedback automatically; used by future chain phase to do the same inside its loop.

Inputs:
- Existing event log files at `.build/events/*.jsonl` (one file per session)
- Existing EventKind.Review event schema (carries verdict, rationale, checks_failed in Data dict)
- ReviewFeedback record from B01

Outputs:
- `ReviewFeedbackRetriever` class in `ThroughlineBuild.EventLog` (or wherever the JsonlEventSink lives):
  ```csharp
  public sealed class ReviewFeedbackRetriever
  {
      public ReviewFeedbackRetriever(string eventsDirectory);
      public ReviewFeedback? GetLatestRework(string ticketId);
  }
  ```
- `GetLatestRework` behavior:
  - Enumerates `.jsonl` files in the events directory (sorted by file modification time descending, then by event timestamp descending within each file)
  - Streams events looking for EventKind.Review where TicketId matches the requested ticket
  - Returns the FIRST match in reverse-chronological order whose Data contains `verdict = "Rework"`
  - If most recent review is not Rework (e.g., Pass or Fail), returns null
  - If no review event found for ticket, returns null
  - Constructs ReviewFeedback from Data fields: `Rationale = Data["rationale"]`, `ChecksFailed = Data["checks_failed"] as list`, `ReworkRoundNumber = 1` (the retriever doesn't know about round numbers from prior reworks; supplies 1 as a default that caller can override)
- Handles malformed events gracefully: skip events with missing required fields, log warning, continue scanning
- Handles missing events directory: returns null with debug log
- Thread-safety: scans are read-only; safe for concurrent calls
- Tests:
  - Returns ReviewFeedback when most recent Review event for ticket has Verdict=Rework
  - Returns null when most recent Review event has Verdict=Pass
  - Returns null when most recent Review event has Verdict=Fail
  - Returns null when no Review event exists for the ticket
  - When multiple Review events exist (multiple sessions), returns feedback from the chronologically most recent
  - Handles malformed JSONL lines without crashing
  - Handles missing events directory without crashing

Acceptance:
- [ ] ReviewFeedbackRetriever class exists with GetLatestRework method
- [ ] Returns ReviewFeedback only when most recent review for the ticket was Rework verdict
- [ ] Returns null on Pass, Fail, no-event, or missing-directory cases
- [ ] Handles malformed events without crashing
- [ ] Tests pass for all enumerated cases

Notes: The retriever returns the LATEST review verdict's feedback. If a ticket has had multiple review rounds (rework → review → rework → review), this returns the most recent. That matches the intent: "what does the operator need to address right now?"

The ReworkRoundNumber=1 default is conservative. Operator's `--feedback` override doesn't set it. Chain's automatic loop sets it explicitly based on its own counter. The retriever can't know the true round number without scanning prior implements too; deferring that complexity is fine for v1.

Reading the event log directly (vs. consulting a ticketing-system comment) keeps the retrieval local-first: no network dependency, no Plane API hit. The tradeoff: if events directory is lost or different machine, retrieval fails. The `--feedback` override flag in B05 handles that case.

The events-directory path is supplied to the retriever's constructor by the CLI layer (which knows the orchestrator's main directory via config). This avoids the retriever needing to know about config or path resolution itself.

OOS:
- Do not implement ticket-comment-based feedback retrieval (event log is the source of truth)
- Do not implement cross-machine event log sync
- Do not parse historical reviews to compute true round numbers (operator/chain supplies)
- Do not write a separate "feedback cache" file (events are the cache)
- Do not implement feedback retrieval for non-Rework verdicts (Pass and Fail aren't actionable as rework input)

## Plan B: ReworkPhase + CLI

### Goal

ReworkPhase orchestrator wraps ReviewFeedbackRetriever + ImplementPhase invocation behind one entry point. `build rework <ticket-id>` CLI command exercises it end-to-end with optional `--feedback` override and standard `--debug` support.

Briefs are sequential.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | rework-phase | ReworkPhase orchestrator class: retrieve feedback (or use manual), validate, invoke ImplementPhase, wrap result | A | src/ThroughlineBuild.Phases/ReworkPhase.cs, src/ThroughlineBuild.Phases/ReworkPhaseOptions.cs, src/ThroughlineBuild.Contracts/Models/ReworkResult.cs, tests/ThroughlineBuild.Phases.Tests/ReworkPhaseTests.cs |
| 05 | rework-cli | `build rework <ticket-id> [--feedback "text"] [--debug]` command | 04 | src/ThroughlineBuild.Cli/Commands/ReworkCommand.cs, src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ReworkCommandTests.cs |

### Briefs - detail

#### Brief 04: rework-phase

Goal: ReworkPhase orchestrator class encapsulates the "retrieve feedback, invoke implementer in rework mode" workflow. Standalone callable (from CLI) and reusable (from future ChainPhase).

Inputs:
- ReviewFeedbackRetriever from B03
- ImplementPhase with rework support from B01
- Existing IPlaneTicketing for state inspection
- Existing IWorkerAgent (shared instance from CLI for now; will become factory-resolved per phase once multi-agent foundation lands)

Outputs:
- `ReworkResult` record:
  ```csharp
  public sealed record ReworkResult(
      string TicketId,
      ReworkOutcome Outcome,
      ImplementResult? ImplementResult,  // null when Outcome != Implemented
      string? FailureReason,
      string FeedbackSource  // "event-log" or "manual"
  );
  ```
- `ReworkOutcome` enum: `Implemented | NoFeedbackAvailable | TicketNotInProgress | ImplementFailed`
- `ReworkPhaseOptions` record:
  ```csharp
  public sealed record ReworkPhaseOptions(
      string TicketId,
      string? ManualFeedback,  // null = retrieve from event log
      int ReworkRoundNumber,   // caller supplies; CLI defaults to 1
      bool Debug
  );
  ```
- `ReworkPhase.RunAsync` flow:
  1. Read ticket state from Plane
  2. If state is not InProgress: return ReworkResult with Outcome=TicketNotInProgress and FailureReason explaining (e.g., "ticket is in InReview; if Rework was the verdict, this is unexpected - state should have transitioned to InProgress")
  3. If `options.ManualFeedback` is non-null: construct ReviewFeedback with `Rationale = options.ManualFeedback`, `ChecksFailed = empty list`, `ReworkRoundNumber = options.ReworkRoundNumber`. Set FeedbackSource = "manual".
  4. Otherwise: invoke ReviewFeedbackRetriever to get the latest Rework feedback for the ticket. If null: return ReworkResult with Outcome=NoFeedbackAvailable and FailureReason ("no Rework verdict found in event log for ticket X; if the review was on a different machine, supply --feedback")
  5. Override `ReviewFeedback.ReworkRoundNumber = options.ReworkRoundNumber` (caller-provided wins over retriever's default of 1)
  6. Construct ImplementPhaseOptions with ReviewFeedback set
  7. Invoke ImplementPhase via injected IWorkerAgent
  8. Return ReworkResult with Outcome=Implemented and the ImplementResult, or Outcome=ImplementFailed with FailureReason from the implement result
- Tests:
  - Happy path: ticket InProgress, retriever returns Rework feedback, implement succeeds, Outcome=Implemented
  - Manual feedback supplied: skips retriever, uses manual text, Outcome=Implemented, FeedbackSource="manual"
  - Ticket not in InProgress (e.g., Done): Outcome=TicketNotInProgress, no implement invocation
  - No feedback in event log AND no manual: Outcome=NoFeedbackAvailable
  - Implement fails: Outcome=ImplementFailed, FailureReason populated
  - ReworkRoundNumber from options propagates through to ImplementPhase

Acceptance:
- [ ] ReworkPhase class exists with RunAsync returning ReworkResult
- [ ] State precondition: refuses non-InProgress tickets with clear outcome
- [ ] Manual feedback takes precedence over retriever
- [ ] Retriever-returned-null surfaces as NoFeedbackAvailable outcome (with helpful message about --feedback)
- [ ] ImplementResult bubbles through ReworkResult when Outcome=Implemented
- [ ] Tests pass for all enumerated cases

Notes: The phase is intentionally thin - it's an orchestrator over existing components. The complexity lives in ImplementPhase (rework-mode handling) and ReviewFeedbackRetriever (event log scanning). ReworkPhase just sequences them.

Future ChainPhase calls ReworkPhase with `ReworkRoundNumber` set from its own counter (1 for first rework, 2 for second) and supplies ManualFeedback=null so the retriever pulls from the just-emitted review event. No cap enforcement in ReworkPhase - chain handles its own cap.

The "TicketNotInProgress" outcome with helpful message about state mismatch helps operators understand what's wrong. If someone runs `build rework 147` against a Done ticket, the error tells them why and what to do (likely "this ticket is already shipped; did you mean to file a new ticket?").

The decision to capture FeedbackSource in ReworkResult is for observability: downstream tooling (chain summaries, comparison harness) can distinguish operator-supplied rework from event-log-driven rework. Useful for analyzing where operators override the reviewer's framing.

OOS:
- Do not implement automatic rework cap (chain owns its cap; standalone is uncapped)
- Do not transition ticket state inside ReworkPhase (ImplementPhase handles the InProgress → InReview transition at its end)
- Do not implement feedback parsing from sources other than event log + manual (no comments, no files, no external APIs)
- Do not implement retry on transient errors (operator re-runs)
- Do not implement ReworkPhase-level event emissions beyond what ImplementPhase already emits (caller wraps with chain events when used in chain context)

#### Brief 05: rework-cli

Goal: `build rework <ticket-id>` CLI command exercises ReworkPhase end-to-end. Optional `--feedback "text"` override for when event log retrieval isn't applicable. Optional `--debug` for session capture.

Inputs:
- ReworkPhase from B04
- Existing CLI command-dispatch pattern (matches build implement, build review, etc.)

Outputs:
- `ReworkCommand` class implementing the existing CLI command interface
- CLI usage:
  ```
  build rework <ticket-id> [--feedback "text"] [--debug]
  ```
  - `<ticket-id>` required (single ticket only)
  - `--feedback "text"` optional; overrides event-log retrieval; passes text as Rationale, empty ChecksFailed
  - `--debug` forwarded to ImplementPhase for session capture
- Output (streamed):
  - On success:
    ```
    [TLB-147] rework starting (feedback source: event-log)
    [TLB-147] reviewer rationale: <first 200 chars of rationale>...
    [TLB-147] checks failed: 3
    [TLB-147] implement (rework round 1): Ok (4m 13s)
    [TLB-147] state: InProgress -> InReview
    [TLB-147] rework complete; run `build review 147` to re-verdict
    ```
  - On NoFeedbackAvailable (no review event in log):
    ```
    [TLB-147] rework failed: no Rework verdict found in event log
    
    The event log at .build/events/ contains no Review event for TLB-147 with Verdict=Rework.
    This can happen if:
      - The review was run on a different machine
      - The events directory was cleared
      - The most recent review verdict was Pass or Fail (not Rework)
    
    To proceed: supply feedback manually with --feedback "..."
    To check: review the .build/events/*.jsonl files or re-run `build review 147`
    ```
  - On TicketNotInProgress:
    ```
    [TLB-147] rework failed: ticket is in <state>, not InProgress
    
    Rework requires a ticket in InProgress state (where a Rework verdict transitions it).
    Current state: <state>
    
    If the ticket is in InReview, run `build review 147` to verdict it first.
    If the ticket is in Done, the work is complete - no rework needed.
    ```
  - On ImplementFailed: surface the implement phase's FailureReason
- Exit codes:
  - 0: Outcome.Implemented
  - 2: Outcome.TicketNotInProgress
  - 3: Outcome.NoFeedbackAvailable
  - 4: Outcome.ImplementFailed
- `--help` documents the command, flags, and exit codes
- Tests:
  - Happy path: command runs, output matches expected, exit code 0
  - --feedback path: skips event-log lookup, feedback used
  - --debug forwarded to ImplementPhase
  - Each non-success outcome produces expected output and exit code
  - Unknown ticket ID: clear stderr error and non-zero exit

Acceptance:
- [ ] `build rework --help` documents the command shape, --feedback flag, --debug flag, exit codes
- [ ] Single-ticket invocation works end-to-end against a ticket in InProgress with Rework feedback in event log
- [ ] --feedback override bypasses event-log lookup
- [ ] Output streams as phase runs
- [ ] Non-success outcomes produce helpful error messages with operator guidance
- [ ] Tests pass

Notes: The "checks failed: 3" line in the output shows the count, not the contents. Operators can read the full list in the implement brief or session capture if needed. Keeping the CLI output tight avoids cluttering the terminal.

The error messages on non-success outcomes follow the pattern of being specific about WHY the command failed and WHAT the operator can do. "No feedback in event log" tells them how to recover (use --feedback, check the files, re-run review). "Ticket not in InProgress" tells them what state would work and what to do given the actual state.

Multi-ticket dispatch (`build rework 147 148 149`) is out of scope; v1 is single-ticket. If batch rework becomes useful, that's a separate ticket (and probably uses the same "stop on first failure" pattern as the chain multi-ticket eventual feature).

OOS:
- Do not implement multi-ticket dispatch
- Do not implement --dry-run (rework has side effects; if needed, run plan/review components separately)
- Do not implement --round-number override (CLI defaults to 1; if operator needs higher, that's a chain concern)
- Do not implement automatic retry on transient failures (operator re-runs)
- Do not implement interactive confirmation before running implement (no destructive action that warrants a prompt)
- Do not auto-trigger `build review` after a successful rework (operator runs explicitly to maintain the standalone-verb model)

## What done looks like

`build rework <ticket-id>` works end-to-end for a single ticket. Operator workflow when review returns Rework:

1. `build review <id>` returns Verdict=Rework, transitions ticket InReview → InProgress, writes feedback to event log
2. Operator decides whether to manually fix or let the implementer try: if the latter, `build rework <id>` runs
3. ReworkPhase reads the event log for the latest Rework feedback, invokes the implementer in rework mode against the existing worktree
4. Implementer makes additional commits addressing the feedback, transitions InProgress → InReview
5. Operator runs `build review <id>` to re-verdict
6. If Pass: `build ship <id>`. If Rework again: another `build rework <id>`. If Fail: operator decides whether to replan or close.

The rework loop is now first-class. The dead-end on Rework verdict that the dogfood run on TLB-147 exposed is closed. Build-chain (when it lands) wraps this same workflow in an automatic loop with cap=2, but the underlying capability is a standalone command.

Two design choices that matter long-term:
- **No automatic cap on standalone rework.** The operator is the loop control. Cap=2 is a chain concern (where the loop is automatic), not a per-command concern.
- **Event log is the feedback source of truth.** No ticket comments, no separate feedback files, no Plane API integration for retrieval. Local-first, single source. `--feedback` override handles the rare case where the local event log doesn't have what's needed.

Once this ships, the build-chain op-doc gets simpler: Plan A (rework-loop enablers) migrates here (B01 and B02 are the same work); chain's Plan B becomes "call ReworkPhase in a counted loop." Chain op-doc will be revised separately to reflect the dependency.