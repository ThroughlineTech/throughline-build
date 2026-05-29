# Operation: auto-resolve-obsolete-escalations

Let `build chain` auto-resolve and continue past tickets where the worker correctly escalates because the work has already been done by a prior commit, instead of stopping for operator triage. Adds a structured `metadata.escalation` convention to the WORKER_RESULT envelope so the orchestrator can distinguish "operator-required escalation" from "obsolete-work escalation," and routes ratified obsolete claims to a transition-and-continue path. No Status enum change.

## Why this exists

When briefs in a multi-brief op-doc are co-designed (e.g. a phase and its template), a sound implementer can absorb downstream-brief scope into an earlier brief in a single commit. The planner for the subsumed brief then correctly detects the work is already done and returns `Status=Escalate` with a prose rationale citing the prior commit. That is the right protocol-level signal: the worker cannot produce new work for this brief. But the orchestrator today treats every Escalate the same way - it stops the chain for operator triage. For automated multi-ticket runs, every legitimate subsumption stops a chain that should have continued.

The worker's signal is already correct (Escalate plus rationale). The gap is downstream: the orchestrator has no structured way to recognize the subspecies of Escalate where prior work has already satisfied this brief. This op-doc adds that structure - a small metadata convention plus a reviewer-ratified auto-resolve branch in `ChainPhase` - so the same chain that just stopped on TLB-260 ("decompose.md was created in A.01 commit 80ccafa") would have transitioned the ticket to Done with the cited evidence and continued.

Additive only: existing Escalate behavior is preserved. Any Escalate without recognized `metadata.escalation` content keeps stopping for operator triage. No status semantics change; no enum addition.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Worker convention: escalation-metadata schema + template instructions | - | M |
| B | Orchestrator + reviewer: ratification + auto-resolve-and-continue | A | M |
| C | Visibility: event-log + analyzer surfacing | B | S |

A first; B depends on A's schema. C is small and depends on B (and transitively A).

## Plan A: Worker convention

### Goal

A documented `metadata.escalation` shape in the `WORKER_RESULT` envelope that workers populate when escalating with a known reason, and template updates that instruct workers when and how to populate it. Plan and implement templates first, since those are the phases where obsolete-detection is most likely. Other phases adopt the convention later as needed.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | escalation-metadata-schema | Define and document the metadata.escalation shape; the parser tolerates it cleanly | - | docs/event-log-format.md, docs/worker-result-envelope.md (or wherever the envelope is documented), src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs (validation only if needed), tests/ |
| 02 | claude-code-template-obsolete-instructions | Update claude-code plan.md and implement.md to instruct emitting the structured escalation block on obsolete detection | 01 | src/ThroughlineBuild.Briefs/Templates/claude-code/plan.md, src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md |
| 03 | per-agent-template-variants | Mirror the claude-code instructions in codex / gemini / copilot plan.md and implement.md | 02 | src/ThroughlineBuild.Briefs/Templates/{codex,gemini,copilot}/plan.md, src/ThroughlineBuild.Briefs/Templates/{codex,gemini,copilot}/implement.md |

### Briefs - detail

#### Brief 01: escalation-metadata-schema

Goal: Define the `metadata.escalation` schema, document it in the envelope spec, and ensure the parser carries it through cleanly (it already passes arbitrary metadata; this brief just locks in the field shape and adds a minimal validation pass).

Inputs: current WORKER_RESULT metadata convention (the parser stores values as `JsonElement`-backed `object`s); the documented envelope; the immediate use case (an obsolete escalation citing a commit, files, and a rationale).

Outputs:
- Schema documented in the envelope spec and `docs/event-log-format.md`:
  ```
  metadata.escalation: {
    reason: string,             // recognized values: "obsolete"; extensible
    subsumed_by?: {             // required when reason = "obsolete"
      commit: string,           // git SHA
      files: string[],          // files the prior commit produced that satisfy this brief
      rationale: string         // one-line human-readable summary
    }
  }
  ```
- Recognized reasons enumerated as a documented list; unknown reasons are accepted (no parser failure) but treated as "unknown escalation reason - no auto-resolve" by the orchestrator (Plan B handles).
- Minimal parser validation: if `metadata.escalation.reason == "obsolete"`, the `subsumed_by` block must be present with all three fields populated; missing/malformed fails the worker result with a clear reason (consistent with how the parser handles other malformed metadata).
- Tests against captured fixtures: valid obsolete escalation parses cleanly; missing `subsumed_by` on an obsolete reason fails with the right message; unknown reasons parse and pass through.

Acceptance:
- [ ] `metadata.escalation` schema is documented with recognized reasons enumerated
- [ ] Parser carries the field through to `WorkerResult.Metadata` unchanged
- [ ] Obsolete reason without a populated `subsumed_by` fails with a clear message
- [ ] Unknown reasons pass through without failure (orchestrator handles)
- [ ] Tests cover valid, malformed, and unknown-reason cases

Notes: Stays under `metadata` so no DTO changes or `JsonSerializerContext` additions are needed - the parser already accepts arbitrary metadata.

OOS:
- Template instructions (B02, B03)
- reviewer ratification (B04)
- orchestrator branching (B05)
- analyzer surfacing (C06)

#### Brief 02: claude-code-template-obsolete-instructions

Goal: Plan and implement templates instruct the worker, on discovering the requested work is already done by a prior commit, to return `Status=Escalate` with a populated `metadata.escalation` block.

Inputs: schema from B01; current `Templates/claude-code/plan.md` and `Templates/claude-code/implement.md`; the planner output from TLB-260 as the worked example.

Outputs:
- Both templates carry a documented "obsolete detection" section: when scanning the repo for the acceptance criteria's artifacts reveals they already exist with the required behavior, the worker emits `Status=Escalate` with `metadata.escalation = { reason: "obsolete", subsumed_by: { commit, files, rationale } }`. Rationale phrasing matches what the existing TLB-260 planner produced (terse, citing commit and files).
- The detection instructions are explicit about what does and does not qualify: "the file exists AND its content meets the acceptance criteria" qualifies; "a file with the same name exists" does not.
- A concrete example block in each template showing a populated obsolete escalation envelope.
- Existing Escalate behavior for other reasons (e.g. atomic parent in `decompose.md`) is unchanged - workers keep using bare Escalate for those, and the orchestrator continues to stop.

Acceptance:
- [ ] A claude-code plan or implement run against a brief whose work is already complete in a prior commit emits a `WORKER_RESULT` with `Status=Escalate` and a populated `metadata.escalation = { reason: "obsolete", subsumed_by: { commit, files, rationale } }`
- [ ] Existing non-obsolete Escalate guidance is unchanged
- [ ] A real claude-code plan run against a subsumed brief emits the structured escalation
- [ ] Detection bar is explicit (acceptance criteria met, not just file existence)

Notes: This is the canon. The agent variants (B03) port from here.

OOS:
- Other agents' variants (B03)
- other phase templates (out of scope this op-doc; adopt later as needed)
- Obsolete-detection heuristics in code (the worker decides; we don't pre-check)

#### Brief 03: per-agent-template-variants

Goal: Mirror the obsolete-detection instructions into the codex / gemini / copilot variants of plan.md and implement.md, semantically equivalent to the claude-code canon.

Inputs: the claude-code canon from B02; the existing per-agent variants; the Brief 14 research doc for any agent-specific tool-vocabulary phrasing.

Outputs:
- `Templates/{codex,gemini,copilot}/plan.md` and `.../implement.md` each updated with the same obsolete-detection guidance and example envelope, reworded for each agent's tool taxonomy where it helps.
- Real runs on each agent against a subsumed brief emit the same structured escalation shape as claude-code.

Acceptance:
- [ ] Six templates updated (three agents x two phases)
- [ ] Each produces a structurally-identical obsolete escalation envelope when run against a subsumed brief
- [ ] Agent-specific tool-vocabulary phrasing is applied where helpful (otherwise verbatim from canon is acceptable)

Notes: Verbatim copies are acceptable for v1 if no agent-specific tuning is obviously needed - the canon is already mostly agent-neutral. File targeted tuning tickets later if a real run on a non-claude agent produces poor obsolete detection.

OOS:
- Other phase templates (review, ship, draft, decompose)
- altering claude-code templates beyond B02's scope
- Per-agent obsolete-detection tuning beyond the canonical instructions

## Plan B: Orchestrator + reviewer

### Goal

Reviewer ratifies obsolete claims (verifies the cited commit and files actually satisfy this brief's acceptance), and `ChainPhase` auto-transitions ratified tickets to Done with the evidence and continues. Unratified obsolete claims and all non-obsolete escalations stop as today.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | reviewer-obsolete-ratification | The reviewer recognizes obsolete escalations and produces a ratify/reject verdict | A | src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs, tests/ |
| 05 | chain-auto-resolve-and-continue | ChainPhase branches on a ratified obsolete to transition the ticket to Done with evidence and proceed | 04 | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs (transition rationale), tests/ |

### Briefs - detail

#### Brief 04: reviewer-obsolete-ratification

Goal: When the upstream phase (plan or implement) returns Escalate with `metadata.escalation.reason == "obsolete"`, the reviewer's job is to verify the cited evidence actually satisfies the brief's acceptance criteria, and emit a ratify/reject verdict.

Inputs: `WorkerAgentReviewer` (post-op-14 rename of `ClaudeCodeReviewer`); the upstream phase's `WorkerResult` including `metadata.escalation.subsumed_by`; the brief's acceptance criteria; the configured agent (review uses the review-phase agent per op-14).

Outputs:
- Reviewer detects an obsolete escalation arriving from the upstream phase and runs a dedicated ratification path: confirm the cited commit exists in the repo, confirm each cited file exists at HEAD (or at the cited commit), and confirm the prior work satisfies this brief's acceptance criteria. This last check is the substantive one - a model-driven verification using the same review tooling, asking "does this prior work meet these acceptance criteria?"
- Verdict shape: ratified (cited evidence verifies the acceptance) or rejected (one or more acceptance criteria are not met by the cited work). Rejected returns the reviewer's normal NeedsRework or Failed flow with a rationale, so the chain handles it as it would any rework signal.
- Ratified verdict carries the original `subsumed_by` evidence forward so Plan B's chain branch can record it.
- For escalations with no recognized reason or no `metadata.escalation` at all, the reviewer skips the ratification path entirely and the existing Escalate flow proceeds (chain stops for operator triage).

Acceptance:
- [ ] Obsolete escalations from plan or implement never bypass the reviewer - every one produces a ratify/reject verdict
- [ ] Ratification checks: cited commit exists, cited files exist, prior work satisfies the current brief's acceptance criteria
- [ ] Verdict is ratified or rejected with a rationale; rejected uses the existing NeedsRework/Failed flow
- [ ] Ratified verdict carries `subsumed_by` evidence forward
- [ ] Non-obsolete or unrecognized-reason escalations bypass ratification and proceed as today
- [ ] Tests cover ratify, reject, and pass-through

Notes: Ratification is the gate that keeps a hallucinated obsolete claim from auto-resolving difficult work. The cost of running it is a model call; the cost of skipping it is a wrongly-closed ticket. Always ratify.

OOS:
- Chain branching and transition (B05)
- event-log entries (C06)
- ratification on phases other than plan/implement

#### Brief 05: chain-auto-resolve-and-continue

Goal: `ChainPhase` recognizes a ratified obsolete and transitions the ticket to Done with the cited evidence in the transition rationale, emits the event (C06 owns the event-kind addition), and continues to the next ticket in the chain. Rejected ratifications and non-obsolete escalations stop as today.

Inputs: the ratified verdict from B04 carrying `subsumed_by`; current `ChainPhase` flow; `IPlaneTicketing` transition with rationale; the existing chain-stop path for unratified or non-obsolete escalations.

Outputs:
- `ChainPhase` inspects the post-review outcome: ratified obsolete -> transition ticket to Done with a rationale of the form "Subsumed by <commit>: <rationale>; files: <list>"; emit the subsumed event (per C06); continue to the next ticket in the chain.
- Rejected obsolete claim: chain treats as the reviewer's NeedsRework or Failed verdict - existing flow.
- Non-obsolete escalations: existing flow (chain stops for operator triage).
- CLI output line for the auto-resolved ticket clearly says "Subsumed by <commit> - continuing" rather than "Failed."
- Configurable: a chain-level flag or config option `--no-auto-resolve` to force the legacy behavior (stop on every escalation) for cases where the operator wants to inspect every claim.

Acceptance:
- [ ] Ratified obsolete claim -> ticket transitions to Done with the cited evidence in the rationale
- [ ] Chain continues to the next ticket after a ratified obsolete
- [ ] Rejected obsolete claim flows through the existing rework/failure path; chain handles as today
- [ ] Non-obsolete escalations still stop the chain (no behavior change)
- [ ] CLI output distinguishes "Subsumed by" from "Failed"
- [ ] `--no-auto-resolve` flag forces legacy stop-on-every-escalate behavior
- [ ] Tests cover ratify-and-continue, reject-and-rework, non-obsolete-and-stop, and the opt-out flag

Notes: This is the brief that converts the user-facing automation experience. Once it lands, multi-ticket chains absorb legitimate subsumption without operator intervention.

OOS:
- Event-kind addition (C06)
- analyzer surfacing (C06)
- ratification logic (B04)

## Plan C: Visibility

### Goal

Subsumed tickets are first-class in the event log and visible in `analyze-event-log` output, so chain runs are auditable and the rate of auto-resolution is observable.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 06 | event-log-and-analyzer-surfacing | New TicketSubsumed event kind + analyze-event-log surfaces subsumed tickets in chain summaries | A, B | docs/event-log-format.md, src/ThroughlineBuild.Events/ (event kind), src/ThroughlineBuild.AnalyzeEventLog/, tests/ |

### Briefs - detail

#### Brief 06: event-log-and-analyzer-surfacing

Goal: A dedicated event kind records each auto-resolve, and `analyze-event-log` surfaces subsumed tickets in chain summaries so the audit trail is complete.

Inputs: current event-kind set (per op-14 the full set is documented in `docs/event-log-format.md`); current `analyze-event-log` output for chains; the ratified-obsolete branch from B05.

Outputs:
- New event kind `TicketSubsumed` (or extend an existing event with a `subsumed_by` payload - implementer's call, likely a dedicated kind is cleaner) with payload `{ ticket_id, subsumed_by_commit, files, rationale }`. Registered in the appropriate `JsonSerializerContext`. Documented in `docs/event-log-format.md`.
- `ChainPhase` emits the event when it auto-resolves a ticket (called from B05's branch).
- `analyze-event-log` surfaces subsumed tickets in chain summaries: a count of auto-resolved tickets per chain run, with the citing commit for each. Distinguishes subsumed (auto-resolved) from completed-normally and from failed.

Acceptance:
- [ ] New event kind registered and documented; AOT publish succeeds
- [ ] ChainPhase emits the event on each auto-resolve
- [ ] `analyze-event-log` distinguishes subsumed / completed / failed in chain summaries
- [ ] Subsumed tickets show the citing commit in the analyzer output
- [ ] Tests cover event emission and analyzer parsing

Notes: This is the audit trail. Without it, an automated chain that subsumes several tickets leaves no clean record of why. Small brief but load-bearing for ops trust in the auto-resolve.

OOS:
- Cross-run subsumption analytics
- web/JSON output formats
- alerting on high subsumption rates

## What done looks like

A `build chain T1 T2 T3` run in which T2 turns out to be subsumed by a commit produced during T1 transitions T2 to Done with a rationale citing the subsuming commit, emits a `TicketSubsumed` event, prints "Subsumed by <commit> - continuing" to the chain output, and proceeds to T3 - all without operator intervention. The reviewer verifies each obsolete claim before auto-resolving, so a hallucinated obsolete escalation gets routed back through normal rework rather than wrongly closing a ticket. Non-obsolete escalations (atomic parent, true blockers) keep stopping for operator triage. `analyze-event-log` surfaces subsumed tickets alongside completed and failed ones, and `--no-auto-resolve` is available for operators who want every escalation to stop the chain regardless.

The legitimate-subsumption-stops-chain pattern that surfaced on TLB-260 no longer breaks automated multi-ticket runs.