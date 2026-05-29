# Operation: build-worker-usage-capture

Capture LLM token usage from the Claude Code worker subprocess and emit it as a structured `LlmCall` event in the run's event log. This is the missing data point that blocks apples-to-apples cost comparison against the old `/ti` slash command.

## Why this exists

After op-04 unblocked the auth path, a real `build plan TLB-33` run produced an event log with six events (WorkerSpawn, VerifierVerdict, three TicketWrites, one StateTransition) and zero token usage data. The old system's `ticket-audit` captures `input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens` per command, on the order of 5.4M `cache_read` for a single /ti. The new system records none of that because:

- The orchestrator binary makes no direct LLM calls (correct per op-04)
- The worker subprocess (Claude Code) consumes tokens, but its telemetry never leaves the subprocess
- `ClaudeCodeAgent` invokes `claude --print` in default text mode, parses stdout for the `WORKER_RESULT` envelope only, and discards everything else

`EventKind.LlmCall = 1` is already defined in the contracts (op-doc 2 Brief 01) and listed in the event log reference doc as "reserved; not yet emitted by PlanPhase." This op-doc emits it.

The fix is small. Claude Code's `--output-format json` flag wraps its response in a JSON envelope that includes a `usage` block with the same token fields the old audit produces. `ClaudeCodeAgent` switches to JSON mode, parses the outer envelope, extracts the response text (for `WORKER_RESULT` parsing as before) and the usage block, and passes usage through `WorkerResult.Metadata`. `PlanPhase` reads it and emits an `LlmCall` event.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Worker usage capture and emission | - | S |

## Plan A: Worker usage capture and emission

### Goal

When `build plan <id>` runs successfully, the resulting event log contains one `LlmCall` event (Kind=1) with structured usage data covering the Claude Code worker's token consumption. The field names match the old audit JSONL convention (`input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`) so cost comparison is a direct subtraction.

Brief sequence: B01 switches ClaudeCodeAgent to JSON output mode and parses the envelope. B02 puts the usage payload into `WorkerResult.Metadata`. B03 makes PlanPhase emit the LlmCall event from that metadata. B04 adds tests confirming the event lands.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | claude-code-json-output | Switch ClaudeCodeAgent to `--output-format json`; parse outer envelope | - | src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs, src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeJsonEnvelope.cs |
| 02 | usage-in-metadata | Populate WorkerResult.Metadata with usage under key `llm_usage` | 01 | src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs |
| 03 | plan-emits-llmcall | PlanPhase reads usage from metadata and emits one LlmCall event per run | 02 | src/ThroughlineBuild.Phases/PlanPhase.cs, docs/event-log-file-format.md |
| 04 | usage-test-coverage | Integration test confirms LlmCall event lands with the expected fields | 03 | tests/ThroughlineBuild.Phases.Tests/PlanPhaseUsageTests.cs |

### Briefs - detail

#### Brief 01: claude-code-json-output

Goal: `ClaudeCodeAgent` invokes `claude --print --output-format json` (or current equivalent for the deployed Claude Code version), parses the outer JSON envelope, and routes the inner response text through the existing `WorkerResultParser` logic unchanged.

Inputs:
- Current `ClaudeCodeAgent` implementation from the state report
- Claude Code CLI documentation. Verify the exact flag name on the deployed version with `claude --help`. Common shape: `--output-format` with values `text`, `json`, `stream-json`. Use `json` (single result object) for v1.
- The actual Claude Code JSON envelope shape on the deployed version. Confirm by running `claude --print --output-format json -p "say hi"` and capturing the output. Expected fields typically include `type`, `result` (the assistant text), `usage` (`input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`), and possibly `model`. Field names may diverge slightly across versions; use what is actually emitted.

Outputs:
- Updated `ClaudeCodeAgent.cs` that adds `--output-format json` to the `ProcessStartInfo` args before any other extra args
- A new file `ClaudeCodeJsonEnvelope.cs` with the typed envelope record, registered in a `JsonSerializerContext` for AOT
- `ClaudeCodeAgent` parses stdout as the typed envelope via the source generator; on parse failure, returns `WorkerResult(Status.Escalate, ...)` with a FailureReason naming the parse problem (NOT a silent fallback to text mode)
- The inner `result` text is passed to the existing `WorkerResultParser` for `WORKER_RESULT` extraction (no change to that parser's logic)
- Existing fixture-based tests still pass; fixtures may need updating to wrap their content in the new envelope shape

Acceptance:
- [ ] `--output-format json` (or current equivalent) is present in the args constructed in `ExecuteAsync`
- [ ] Stdout is parsed as the typed envelope via `JsonSerializerContext` source generator (no runtime reflection)
- [ ] Inner result text is extracted and passed to `WorkerResultParser` unchanged
- [ ] Envelope parse failure produces `WorkerResult(Status.Escalate, ...)` with FailureReason identifying the parse issue
- [ ] If the deployed Claude Code version does not support JSON output, the failure surfaces loudly (clear error message), no silent fallback
- [ ] Existing `ClaudeCodeAgent` xUnit tests pass after fixture updates

Notes: The exact flag and envelope shape depend on Claude Code's version. Verify before coding the parser. Capture a real sample output and use it as the fixture seed. AOT compatibility requires the `JsonSerializerContext` approach for the envelope type.

OOS:
- Do not add `stream-json` mode (single-object `json` is sufficient for v1)
- Do not change `WorkerResultParser`'s parsing logic
- Do not add a config flag for output format (always JSON for now)
- Do not implement envelope handling for non-Claude-Code workers (no such workers exist yet)

#### Brief 02: usage-in-metadata

Goal: After successful envelope parsing, `ClaudeCodeAgent` puts the usage data into `WorkerResult.Metadata` under the key `llm_usage` so `PlanPhase` can pick it up without touching the worker abstraction.

Inputs:
- The parsed envelope from B01
- The existing `WorkerResult` record and its `Metadata` dictionary

Outputs:
- `ClaudeCodeAgent` populates `WorkerResult.Metadata["llm_usage"]` with a payload containing:
  - `model` (string, from envelope if available; otherwise null)
  - `input_tokens` (int)
  - `output_tokens` (int)
  - `cache_read_tokens` (int?, null when envelope omits)
  - `cache_create_tokens` (int?, null when envelope omits)
  - `wall_clock_ms` (long, total elapsed from `Process.Start` to `Process.WaitForExitAsync` completion)
- Keys are snake_case to match the event-log Data conventions and the old audit JSONL field naming
- If envelope parsing succeeded but the `usage` block was absent, the payload contains zeroed token counts and a `partial: true` flag

Acceptance:
- [ ] `WorkerResult.Metadata["llm_usage"]` is present on every successful run
- [ ] The value type is either a typed record or a `Dictionary<string, object?>` carrying the listed fields
- [ ] When the envelope provides cache token fields, they appear in the payload; when absent, those fields are null
- [ ] `wall_clock_ms` is measured around the subprocess invocation
- [ ] xUnit test verifies the metadata is populated with the expected shape on a fixture envelope

Notes: `WorkerResult.Metadata` is `IReadOnlyDictionary<string, object>`. A typed record value is cleaner but a dictionary is simpler; either works since `PlanPhase` will unpack it in B03. Whichever you choose, ensure the value is serializable through the event log's `JsonSerializerContext` so it round-trips into the JSONL output without runtime reflection.

OOS:
- Do not change the `WorkerResult` record's shape (add via metadata)
- Do not aggregate or transform usage data beyond what the envelope provides
- Do not store usage data anywhere other than metadata (events come from `PlanPhase` in B03)

#### Brief 03: plan-emits-llmcall

Goal: After `PlanPhase` receives a successful `WorkerResult`, it reads `Metadata["llm_usage"]` and emits one `WorkflowEvent` with `Kind = EventKind.LlmCall` carrying the usage payload.

Inputs:
- The `WorkerResult` returned by `ClaudeCodeAgent` with metadata populated per B02
- The existing `IEventSink` injected into `PlanPhase`
- The `EventKind.LlmCall = 1` value from `WorkflowCore.Contracts`

Outputs:
- Updated `PlanPhase.cs` that emits one event immediately after the `VerifierVerdict` event and before the first `TicketWrite`:
  - `Kind = EventKind.LlmCall`
  - `TicketId` = current ticket
  - `Phase = Phase.Plan`
  - `Data` = the contents of `llm_usage`, flattened into the `Data` dictionary with snake_case keys
- If `llm_usage` is missing from metadata (e.g. a worker-failure path that never populated it), no LlmCall event is emitted (do not emit zeros)
- Existing event emissions (`WorkerSpawn`, `VerifierVerdict`, `TicketWrite`, `StateTransition`) are unchanged in shape or order
- The reference doc `docs/event-log-file-format.md` is updated in the same PR: remove "reserved; not yet emitted" from the LlmCall row, add LlmCall to the Data conventions table with the field list, update the Happy-path Plan example to include the LlmCall event in position 3

Acceptance:
- [ ] One LlmCall event lands in the log per successful plan run, between VerifierVerdict and the first TicketWrite
- [ ] Event `Data` contains the snake_case usage fields (model, input_tokens, output_tokens, cache_read_tokens, cache_create_tokens, wall_clock_ms)
- [ ] No LlmCall event emitted on worker failure (Status != Ok)
- [ ] Existing PlanPhase tests continue to pass
- [ ] `docs/event-log-file-format.md` updated to reflect the new event in the happy-path example and conventions table

Notes: The reference doc's current happy-path example shows six events. After this brief, the canonical happy path has seven, with LlmCall as the third. Update the example block verbatim.

OOS:
- Do not emit LlmCall for any other code path (no other LLM calls exist in the plan phase)
- Do not aggregate usage across multiple worker invocations
- Do not back-fill historical event logs

#### Brief 04: usage-test-coverage

Goal: An integration test that runs `PlanPhase` end-to-end with a mocked worker and asserts the LlmCall event lands with the expected shape.

Inputs:
- The completed B01-B03 changes
- xUnit test infrastructure
- A mock `IWorkerAgent` that returns a `WorkerResult` with a known `llm_usage` payload

Outputs:
- `tests/ThroughlineBuild.Phases.Tests/PlanPhaseUsageTests.cs` with at least two tests:
  - `PlanPhase_emits_llm_call_event_on_success`: runs the phase with a mock worker that returns a populated usage payload; asserts exactly one LlmCall event is emitted with matching values
  - `PlanPhase_omits_llm_call_event_on_worker_failure`: runs the phase with a mock worker returning `Status.Failed`; asserts no LlmCall event is emitted

Acceptance:
- [ ] Both tests pass
- [ ] Mocks at the `IWorkerAgent` and `ITicketing` seams; no real Claude Code or Plane required
- [ ] Tests run in CI on all three OS matrix jobs
- [ ] Failure of either test fails the CI job

Notes: A stub `IEventSink` that captures emitted events into a `List<WorkflowEvent>` is the simplest assertion vehicle. Construct one in the test, inject it into `PlanPhase`, run, then inspect the captured list.

OOS:
- Do not test against the real Anthropic API or real Claude Code subprocess
- Do not test envelope parsing here (that belongs in ClaudeCodeAgent's tests from B01)
- Do not test the cost-comparison itself (that is the dogfooding run on a real ticket, not a unit test)

## What done looks like

After op-05 lands, `build plan <id>` produces an event log file that includes one LlmCall event with structured usage data. The happy-path event sequence becomes (shape example, real numbers from a /ti run on a comparable ticket for illustration):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-X","Phase":0,"Data":{"worker":"claude-code"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-X","Phase":0,"Data":{"status":"Ok"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-X","Phase":0,"Data":{"model":"claude-sonnet-4-6","input_tokens":92,"output_tokens":80439,"cache_read_tokens":5399990,"cache_create_tokens":349062,"wall_clock_ms":172000}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-X","Phase":0,"Data":{"action":"append_description"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-X","Phase":0,"Data":{"action":"apply_labels"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-X","Phase":0,"Data":{"action":"create_comment"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-X","Phase":0,"Data":{"from":"Backlog","to":"Ready"}}
```

The LlmCall event's `Data` is in the same units as the old `ticket-audit` JSONL. A short script can `jq` over a new event log and an old audit file, sum the token fields, and produce the cost-reduction number directly. Wall-clock time also lands in the same event so cost-per-wall-second comparisons are possible.

Once this ships, re-run `build plan` on a fresh TLB ticket and `/ti` on a parallel TT ticket. Compare LlmCall event Data on the new side to the audit JSONL line on the old side. That is the apples-to-apples comparison the architecture has been working toward.