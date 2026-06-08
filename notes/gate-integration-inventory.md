# Gate Integration Inventory

Verified at HEAD (branch ticket/tlb-500, base d0ee732). Every file:line was
read directly from the source tree. Docs in docs/state-of-the-system/ are
known-stale; this inventory cites code, not docs.

---

## 1. Existing check machinery

### AutomatedChecksRunner
- **Definition:** `src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs:7`
  `public class AutomatedChecksRunner` - constructor takes optional `bool stopOnFirstFailure`.
  `RunAsync(IReadOnlyList<CheckSpec>, string workingDirectory, CancellationToken)` at line 16.
- **Decision:** REUSE. Brief 06 wires the existing runner; Brief 01 does not touch it.

### CheckSpec and CheckResult models
- **CheckSpec:** `src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:3`
  `public record CheckSpec(string Name, string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout)`
- **CheckResult:** `src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:9`
  `public record CheckResult(string Name, bool Passed, int ExitCode, string StdoutTail, string StderrTail, TimeSpan Elapsed)`
- **Decision:** REUSE. Both records are already the gate's data currency; no new models needed.

### Config capability map
- **review.checks parsing:** `src/ThroughlineBuild.Cli/Config.cs` - `ReadReviewSection` method,
  `[[review.checks]]` TOML array parsed into `IReadOnlyList<CheckSpec>` surfaced as
  `ReviewSection.Checks`. Consumed as `config2.Review.Checks` at construction time.
- **ship.regression_checks parsing:** `src/ThroughlineBuild.Cli/Config.cs` - `ReadShipSection` method,
  `[[ship.regression_checks]]` parsed into `IReadOnlyList<CheckSpec>` as `ShipSection.RegressionChecks`.
- **Decision (review.checks):** REUSE. The gate reads the same `config2.Review.Checks` list the
  review phase already uses; no new config key or new section needed for Brief 02.
- **Decision (ship.regression_checks):** OUT OF SCOPE. A different configured set, a different phase,
  untouched by this operation.

---

## 2. Required reuse decision: one build per ticket

**Decision: relocate and reuse.**

The gate runs at the implement->review seam (inside `ChainPhase.RunImplementReviewLoopAsync`).
It executes the check list once on the implemented branch before review begins.
`ReviewPhase.RunAsync` (line 195-196) already falls back to `new AutomatedChecksRunner()`
when no result is supplied. Brief 06 must thread the gate's pre-run results into
`ReviewPhase` so review consumes them instead of re-running.

Two possible mechanisms for Brief 06:
- Pass a pre-populated `IReadOnlyList<CheckResult>` into `ReviewOptions` (new field), so
  `ReviewPhase` skips its own runner call when results are already present.
- Or pass a stub `AutomatedChecksRunner` subclass that returns the cached results.

Either way: one run of `AutomatedChecksRunner.RunAsync` per ticket per chain loop iteration.
A parallel run that produces a second `RunAsync` call is forbidden by wall discipline.

---

## 3. Every other caller of AutomatedChecksRunner

### ReviewPhase - standalone build review path
- **File:** `src/ThroughlineBuild.Cli/Program.cs:1518-1519`
  Constructs `ReviewOptions(config2.Review.Checks, verifierWorkerOptions)` then
  `new ReviewPhase(...)`. When invoked as `build review TLB-X` (standalone, outside chain),
  `ReviewPhase.RunAsync` line 195 creates its own `AutomatedChecksRunner` internally.
  This path has no gate output available; it must keep the internal runner as its fallback.
  **Consequence:** Brief 06 must not remove the `ReviewPhase` fallback runner; the standalone
  path is stranded if it does.

### ReviewPhase - chain factory
- **File:** `src/ThroughlineBuild.Cli/Program.cs:1750-1761`
  Factory lambda constructs `ReviewOptions(config2.Review.Checks, verifierWorkerOptions)` and
  returns `new ReviewPhase(...)`. This is the chain path; the gate injects pre-run results here.
  Brief 06 edits this factory to pass pre-run results through.

### ShipPhase - regression checks
- **File:** `src/ThroughlineBuild.Phases/ShipPhase.cs:508,595`
  `_checksRunner.RunAsync(_shipOptions.RegressionChecks, ...)` at lines 509 and 598.
  Runs `ship.regression_checks` (a different configured set) in a different phase.
  **Decision:** OUT OF SCOPE. Do not touch ShipPhase. It is unaffected by gate relocation.

**Scope guard for Brief 06:** relocating the review-time check run to the implement->review
seam only affects `ReviewPhase` (chain factory path). The standalone review path and
`ShipPhase` are unaffected.

---

## 4. Claim emission surface

### WorkerResultParser
- **Definition:** `src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:109`
  `internal static class WorkerResultParser` - `TryParse(string stdout)` at line 111.
  Reverse-scans stdout for `WORKER_RESULT` marker, deserializes JSON envelope.
  Pre-pass at line 128 captures `<<<NAME_START`/`<<<NAME_END` named fenced blocks.

### FencedBlockResolver
- **Definition:** `src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs:622`
  `internal static class FencedBlockResolver`
  `TryResolveRef(blocks, metadata, refFieldName, out content, out error)` at line 627.
  Used in `ImplementPhase.cs:400` to resolve `summary_ref` from metadata to block content.

### Implement worker template files
  - `src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md`
  - `src/ThroughlineBuild.Briefs/Templates/codex/implement.md`
  - `src/ThroughlineBuild.Briefs/Templates/copilot/implement.md`
  - `src/ThroughlineBuild.Briefs/Templates/gemini/implement.md`

**Brief 05 surface:** Returning a `CompletionClaim` requires:
1. Editing all four implement templates to instruct the worker to emit a
   `<<<COMPLETION_CLAIM_START` / `<<<COMPLETION_CLAIM_END` fenced block containing
   the `CompletionClaim` JSON, and to add a `completion_claim_ref` key to `metadata`.
2. Extending `WorkerResultParser` (or a new helper) to resolve `completion_claim_ref`
   and deserialize the block content into `CompletionClaim`.
Both changes are Brief 05's true surface; neither is in scope for Brief 01.

---

## 5. State-transition ownership for a gate hard-fail

### Verified call sites
- `ImplementPhase.cs:432` - transitions `InProgress -> InReview` at Step 18:
  ```csharp
  await _ticketing.TransitionAsync(ticketId, TicketState.InReview, ct);
  ```
- `ImplementPhase.cs:86-98` - rework guard: requires `ticket.State == InProgress`
  when `isRework == true` (i.e. `_phaseOptions.ReviewFeedback is not null`).

**Decision: option (a) - gate owns the InReview -> InProgress flip on hard-fail.**

Implement continues to transition InProgress -> InReview at line 432. The gate runs
on the InReview ticket at the implement->review seam. On a gate hard-fail:
- The gate calls `_ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct)`
  to flip InReview -> InProgress.
- The gate constructs a `ReviewFeedback` and feeds it back to the rework loop as if
  review had returned a Rework verdict.
- `ImplementPhase.cs:93-98` then sees `InProgress` on the rework round, satisfying
  the existing guard.

**Why not option (b):** Option (b) requires changing `ImplementPhase` to stop
transitioning and instead leaves the ticket InProgress until the gate passes.
That breaks the existing standalone `build review TLB-X` path, which checks
`ticket.State == InReview` at `ReviewPhase.cs:73-75`. Option (a) is a smaller
and safer edit.

**Exact call sites Brief 06 edits:**
- Adds the InReview -> InProgress transition call inside the gate's hard-fail branch
  (new code in the gate phase, not in ImplementPhase or ReviewPhase).
- Does NOT modify `ImplementPhase.cs:432`.
- Does NOT modify `ReviewPhase.cs:73-75`.

---

## 6. Chain loop and rework feed

### RunImplementReviewLoopAsync
- **Definition:** `src/ThroughlineBuild.Phases/ChainPhase.cs:507`
  `private async Task<ChainResult?> RunImplementReviewLoopAsync(...)`
- **Implement call:** `ChainPhase.cs:530-531`
  `var implResult = await _implementFactory(implBuildOpts, implPhaseOpts).RunAsync(...)`
- **Gate insertion point:** between line 531 (implement completes) and line 585
  (review call via `RunOneReviewAsync`). The gate runs here: after implement succeeds,
  before review begins.
- **Review call:** `ChainPhase.cs:585`
  `var reviewResult = await RunOneReviewAsync(options, steps, round, ct)`
- **MaxReworkRounds:** `ChainPhase.cs:55` - `private const int MaxReworkRounds = 2`
- **Rework-round cap logic:** `ChainPhase.cs:599-620` - `if (round < MaxReworkRounds)`
  constructs `ReviewFeedback` and increments `round`; else returns `ChainOutcome.ReworkCapExceeded`.
- **ReviewFeedback construction:** `ChainPhase.cs:601`
  `feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1)`

**Brief 08 surface:** Gate-failure feedback at the insert point must:
- Construct a `ReviewFeedback` with the gate's failure rationale and failed check names.
- Feed it back into the loop as a rework trigger (same shape as a review-originated one).
- Be distinguishable from a review-originated feedback by a prefix on the rationale
  string, e.g. `"[gate] ..."`, so the worker brief can identify its origin.
- The gate failure does NOT call `RunOneReviewAsync`; it increments `round` and
  loops back to implement, just like a normal rework round.

---

## 7. Event log

### IEventSink interface
- **Definition:** `src/ThroughlineBuild.Contracts/IEventSink.cs:5`
  `public interface IEventSink`
  Methods: `EmitAsync(WorkflowEvent ev, CancellationToken ct)` and `FlushAsync(CancellationToken ct)`.

### JsonlEventSink (JSONL implementation)
- **Definition:** `src/ThroughlineBuild.EventLog/JsonlEventSink.cs:8`
  `public sealed class JsonlEventSink : IEventSink, IAsyncDisposable`
  Appends JSON lines to `{BaseDirectory}/{stem}.jsonl`; newline separator at line 15.

### WorkflowEvent and EventKind
- **WorkflowEvent:** `src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:3`
  `public record WorkflowEvent(string SessionId, DateTimeOffset Timestamp, EventKind Kind, string TicketId, Phase Phase, IReadOnlyDictionary<string, object> Data)`
- **EventKind enum:** `src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14`
  Values: `StateTransition, LlmCall, WorkerSpawn, VerifierVerdict, GateFailure, TicketWrite, ChainStart, ChainEnd, ReworkRound, TicketSubsumed, TargetAutoRebased, DispatchStart, DispatchEnd`

### LLM-call events with token/cost data
- **Emission pattern** (e.g. `ImplementPhase.cs:311-316`):
  Worker metadata `llm_usage` key -> `LlmUsageFlattener.Flatten(usageObj)` ->
  `EmitAsync(EventKind.LlmCall, ticketId, llmData, ct)`.
- **LlmUsageFlattener:** `src/ThroughlineBuild.Helpers/LlmUsageFlattener.cs:7`
  Flattens `llm_usage` dict/JsonElement into a flat `IReadOnlyDictionary<string, object>`.
  Keys carry per-call token counts and cost data (input, output, cache usage).
- **LlmCall events already carry per-call token/cost data** - the ledger's measurable
  terms are already in the event log. Brief 09 reads these events; it does not re-derive them.

### Rework-round count
- **Computed at:** `src/ThroughlineBuild.Phases/ChainPhase.cs:486`
  `var reworkRounds = result.Steps.Count(s => s.PhaseName == "implement" && s.ReworkRoundNumber >= 1)`
  This runs in `EmitChainEndAsync` and is emitted in the `ChainEnd` event payload as `"rework_rounds"`.
- **Brief 09 reuse:** use `ChainStep.ReworkRoundNumber` from the steps list directly;
  do not re-derive the count.

---

## 8. Downstream brief scope flags

| Brief | Impact from this inventory |
|-------|---------------------------|
| Brief 02 | Config schema: `[[review.checks]]` is already the right source. No new key needed. |
| Brief 05 | Emit surface: edit all four implement templates + extend WorkerResultParser/FencedBlockResolver to parse CompletionClaim. |
| Brief 06 | Check relocation: (a) insert gate at ChainPhase.cs:531-585 seam; (b) thread pre-run results into ReviewPhase so review reuses them; (c) add InReview->InProgress flip on hard-fail; (d) preserve ReviewPhase fallback runner for standalone path. Do NOT touch ShipPhase. |
| Brief 08 | Rework feed: construct ReviewFeedback at gate failure point in RunImplementReviewLoopAsync; distinguish origin with rationale prefix. |
| Brief 09 | Ledger: LlmCall events already carry token/cost data. Rework counts are in ChainStep.ReworkRoundNumber. No re-derivation needed. |
