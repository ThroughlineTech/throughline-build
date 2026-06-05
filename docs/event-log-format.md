# Event Log File Format

**Status:** Reference
**Scope:** `.build/events/*.jsonl` produced by the `build` CLI

The `build` CLI writes a structured event log for every invocation. Each run produces one file. Downstream tooling (audits, replay, cost analysis) reads these files.

---

## File location and naming

- Directory: configured by `events.log_directory` in `.build/config.toml` (typically `.build/events/`).
- Filename stem: `<project>-<ticket-or-slug>-<verb>-<yyyy-MM-dd>-<HHmmss>.jsonl`. Example: `latticeflow-TLB-169-implement-2026-05-28-143052.jsonl`.
  - `<project>` is the slugified, lowercased `ticketing.plane_project_name` from the config; if absent, falls back to slugified `plane_project_identifier`; if both empty, the segment is omitted.
  - `<ticket-or-slug>` is the canonical ticket id (case preserved, e.g. `TLB-169`) for phase verbs (`plan`, `implement`, `review`, `ship`, `chain`, `rework`, `amend`, `close`, `defer`, `reopen`). For `scaffold`, it is the op-doc filename stem. For `new`, it is omitted.
  - `<verb>` is the CLI subcommand, lowercased.
  - The `HHmmss` suffix is local time and exists for collision-safety: the sink opens in `FileMode.Append`, so two runs of the same verb on the same ticket on the same second would otherwise merge into one file. Within the same second, the second run will indeed append to the first.
  - Stem construction lives in [src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs](../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs).
- The same stem is used as the directory name for `--debug` worker captures under `.build/sessions/<stem>/`, so the two artifacts of a single run sort together by name.
- The `SessionId` field inside each event record (see [Record schema](#record-schema) below) is still a per-invocation GUID with no hyphens. The on-disk filename was made human-readable; the in-record correlation key was not changed.
- Format: [JSON Lines](https://jsonlines.org/) - one UTF-8 JSON object per line, terminated by `\n`. Append-only.
- Empty file: legal. It means the run exited before emitting any event (for example, PlanPhase rejects a ticket that is not in `Backlog` state at [src/ThroughlineBuild.Phases/PlanPhase.cs:59-60](../src/ThroughlineBuild.Phases/PlanPhase.cs#L59-L60) before the first emit).

---

## Record schema

Each line is serialized from a `WorkflowEvent` ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs)) via the `EventLineDto` wrapper in the sink:

| Field             | Type                          | Notes |
|-------------------|-------------------------------|-------|
| `SessionId`       | string                        | Same GUID as the filename. Identifies one CLI run. |
| `Timestamp`       | ISO-8601 with offset          | UTC, e.g. `2026-05-22T22:37:18.9689774+00:00`. |
| `Kind`            | integer (enum)                | See [Event kinds](#event-kinds). |
| `TicketId`        | string                        | Plane work-item identifier, e.g. `TLB-34`. |
| `Phase`           | integer (enum)                | See [Phases](#phases). |
| `Data`            | object (string -> any)        | Kind-specific payload. See [Data conventions](#data-conventions). |
| `project_id`      | string, nullable              | Plane project UUID from `[ticketing].plane_project_id` in config. Omitted from logs written before TLB-147. |
| `project_name`    | string, nullable              | Plane project display name from `[ticketing].plane_project_name` in config. Omitted when the config key is absent or empty. |
| `workspace_slug`  | string, nullable              | Plane workspace slug from `[ticketing].plane_workspace_slug` in config. Omitted from logs written before TLB-147. |
| `build_version`   | string, nullable              | CLI assembly version (e.g. `"1.0.0"`). Omitted from logs written before TLB-147. |

Enums serialize as integers (default `System.Text.Json` behavior; no string converter is registered in [EventLogJsonContext](../src/ThroughlineBuild.EventLog/EventLogJsonContext.cs)). The six original fields (`SessionId`, `Timestamp`, `Kind`, `TicketId`, `Phase`, `Data`) are PascalCase. The four session fields added by TLB-147 are snake_case per the ticket schema spec.

### Forward compatibility

Logs produced before TLB-147 lack `project_id`, `project_name`, `workspace_slug`, and `build_version`. Readers must tolerate the absence of these fields and treat them as null/absent. The six original fields are unchanged and remain present in all logs.

### Event kinds

From `EventKind` at [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:13](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L13):

| Value | Name              | Meaning |
|-------|-------------------|---------|
| 0     | `StateTransition` | Ticket moved between Plane states (e.g. Backlog -> Ready). |
| 1     | `LlmCall`         | Direct LLM invocation. Emitted by PlanPhase / ImplementPhase / ReviewPhase once per successful run with worker token usage extracted from the `WORKER_RESULT` envelope's `llm_usage` metadata. |
| 2     | `WorkerSpawn`     | A worker agent (e.g. Claude Code) was launched. |
| 3     | `VerifierVerdict` | A worker or verifier returned a status. |
| 4     | `GateFailure`     | A precondition gate fired. Used by ImplementPhase to surface a `drift_warning` (the run still proceeds) and by ShipPhase to surface ship-blocking conditions (the run halts and the ticket stays in `InReview`). |
| 5     | `TicketWrite`     | Side effect on a Plane ticket (description, labels, comment). |
| 6     | `ChainStart`      | Emitted once at the beginning of a `build chain` run, before any inner phase runs. |
| 7     | `ChainEnd`        | Emitted once at the end of a `build chain` run, after the final inner phase (or after the refused-state early-exit). |
| 8     | `ReworkRound`    | Emitted by ChainPhase between an `InReview -> InProgress` Rework transition and the start of the next ImplementPhase round. Not emitted by `build rework` (manual rework does not loop). |
| 9     | `TicketSubsumed` | Emitted by ChainPhase when a ticket is auto-resolved as obsolete after the ratifier passes. Phase is Chain (4). Appears after the subsumption comment is posted and before `ChainEnd`. |
| 10    | `TargetAutoRebased` | Emitted by ShipPhase on every auto-rebase attempt of local target branch onto origin/target branch (DivergedNoConflict path). Emitted regardless of outcome. Not emitted when `--no-auto-merge` is set (no attempt is made). |

### Phases

From `Phase` at [src/ThroughlineBuild.Contracts/Models/Phase.cs:3](../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3):

| Value | Name      | Used by |
|-------|-----------|---------|
| 0     | `Plan`    | `PlanPhase` |
| 1     | `Implement` | `ImplementPhase`, `ReworkPhase` (delegated), `ChainPhase.ReworkRound` events |
| 2     | `Review`  | `ReviewPhase` |
| 3     | `Ship`    | `ShipPhase` |
| 4     | `Chain`   | `ChainPhase` (ChainStart / ChainEnd) |
| 5     | `New`     | `NewPhase` |
| 6     | `Command` | `AmendCommand`, `CloseCommand`, `DeferCommand`, `ReopenCommand` (generic command-bearing events that do not belong to a workflow phase) |
| 7     | `Draft`   | `DraftPhase` (used by `build new` in draft mode) |
| 8     | `Scaffold` | `ScaffoldPhase` (used by `build scaffold`) |

---

## Data conventions

`Data` is free-form per `Kind`. Current emitters (in [PlanPhase](../src/ThroughlineBuild.Phases/PlanPhase.cs) and [ImplementPhase](../src/ThroughlineBuild.Phases/ImplementPhase.cs)):

| Kind              | Keys              | Example |
|-------------------|-------------------|---------|
| `WorkerSpawn`     | `worker`, optional `role` | `{"worker": "claude-code"}` (Plan/Implement phases omit `role`); `{"worker": "claude-code", "role": "verifier"}` (Review phase sets `role` to distinguish verifier spawns from implementer spawns in chained runs) |
| `VerifierVerdict` | Plan/Implement: `status`. Review: `kind`, `checks_failed_count` | Plan/Implement: `{"status": "Ok"}`, `{"status": "Failed"}`. Review: `{"kind": "Pass", "checks_failed_count": 0}`, `{"kind": "Rework", "checks_failed_count": 2}`, `{"kind": "Fail", "checks_failed_count": 0}` (`kind` is one of `Pass`, `Rework`, `Fail`) |
| `LlmCall`         | `model`, `vendor`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`, `wall_clock_ms`, optional `cost_usd`, optional `partial` | `{"model": "claude-sonnet-4-6", "vendor": "anthropic", "input_tokens": 1234, "output_tokens": 567, "cache_read_tokens": 0, "cache_create_tokens": 0, "wall_clock_ms": 2500, "cost_usd": 0.0123}`. `model` is extracted from the NDJSON `type=system` event; falls back to the configured `default_model` (vendor prefix stripped) if the system event is absent. `vendor` is supplied by the agent; Claude Code workers emit `"anthropic"`. `cost_usd` is the `total_cost_usd` from the terminal CLI envelope; the key is absent when the envelope did not carry a cost value. `anthropic_request_id` is NOT included: the Claude Code CLI does not expose the API request ID in its stream output. |
| `TicketWrite`     | `action`, plus action-specific extras | `{"action": "append_description"}`, `{"action": "apply_labels"}`, `{"action": "create_comment"}`, `{"action": "decruft", "halted_at": "complete"}` (ShipPhase; `halted_at` is `complete` on success, a `DecruftStep` name on halt, or `exception` if the decrufter threw - in which case an `error` key carries the exception message), `{"action": "delete_branch", "success": true}` (ShipPhase; on failure a `reason` key carries the git error) |
| `StateTransition` | `from`, `to`      | `{"from": "Backlog", "to": "Ready"}`, `{"from": "Ready", "to": "InProgress"}`, `{"from": "InProgress", "to": "InReview"}`, `{"from": "InReview", "to": "InProgress"}` (Review-phase Rework path), `{"from": "InReview", "to": "Done"}` (Ship-phase success) |
| `GateFailure`     | `kind`, kind-specific extras | `{"kind": "drift_warning", "planned_at_sha": "...", "main_sha": "..."}` (ImplementPhase emits this when the `[planned_at: <sha>]` marker on a ticket disagrees with current `origin/main`; the phase logs the warning and proceeds without gating). ChainPhase can halt before planning with `{"kind": "hygiene_gate_preflight", "detail": "..."}` for conflicts/unrelated stashes or `{"kind": "chain_preflight_dirty", "dirty_count": 2, "dirty_paths": ["src/A.cs"], "worktree": "C:/repo"}` for tracked main-worktree changes. ShipPhase emits ship-blocking kinds, all of which halt the run with the ticket left in `InReview`, including `pre_flight_hygiene`, `pre_flight_dirty`, `rebase_conflicts`, `rebase_other`, `conflict_markers`, `regression_checks`, and `diverged_bases`. |
| `ChainStart`      | `starting_at_phase`, `initial_state`, `chain_session_id` | `{"starting_at_phase": "plan", "initial_state": "Backlog", "chain_session_id": "<guid>"}`. `starting_at_phase` is one of `plan` (Backlog ticket), `implement` (Ready ticket), `review` (InReview ticket), or `refused` (any other state). `chain_session_id` is the GUID shared by every `WorkflowEvent.SessionId` written from this chain run, including the inner phase events. |
| `ChainEnd`        | `outcome`, `phases_run`, `rework_rounds`, `total_duration_ms`, optional `final_rationale_preview` | `{"outcome": "Completed", "phases_run": 4, "rework_rounds": 0, "total_duration_ms": 312500}`. `outcome` is the `ChainOutcome` enum name (`Completed`, `RefusedInitialState`, `StoppedAtPlan`, `StoppedAtImplement`, `StoppedAtReview`, `ReworkCapExceeded`, `StoppedAtShip`). `phases_run` counts every `ChainStep` produced including rework retries. `rework_rounds` counts only implement steps whose `ReworkRoundNumber >= 1`. `final_rationale_preview` is a truncated form of the last review's rationale, present only when ChainPhase has a Rework / Fail verdict to record. |
| `ReworkRound`     | `round`, `verdict_that_triggered`, `rationale_preview` | `{"round": 1, "verdict_that_triggered": "Rework", "rationale_preview": "first 200 chars of the review rationale..."}`. Emitted before the next ImplementPhase round begins. `round` is the 1-based index of the upcoming implement attempt (round 0 is the initial implement). `verdict_that_triggered` is always `"Rework"` today - `Fail` and `Pass` do not loop. `rationale_preview` is `""` if the review supplied no rationale. The event's `Phase` field is `Implement` (= 1), not `Chain`, because the round number refers to an implement attempt. |
| `TicketSubsumed`  | `ticket_id`, `subsumed_by_commit`, `files`, `rationale` | `{"ticket_id": "TLB-34", "subsumed_by_commit": "abc123def456", "files": ["src/Foo.cs", "src/Bar.cs"], "rationale": "already done in prior commit"}`. `ticket_id` duplicates `WorkflowEvent.TicketId` for readability in raw-log inspection. `subsumed_by_commit` is the SHA of the commit that makes the ticket obsolete. `files` is the string array of paths already handled by that commit (may be empty). `rationale` is the human-readable explanation. Phase is always `Chain` (= 4). |
| `TargetAutoRebased` | `from_sha`, `onto_sha`, `local_commits_replayed`, `outcome` | `{"from_sha": "<sha>", "onto_sha": "<sha>", "local_commits_replayed": ["sha1", "sha2"], "outcome": "clean"}`. `from_sha` is the HEAD of local target branch before the rebase. `onto_sha` is the SHA of origin/target branch at the time of the attempt. `local_commits_replayed` is the list of SHAs that were ahead of origin/target branch on local target branch (newest first, may be empty). `outcome` is `"clean"` on success or `"raced_to_conflict"` when the divergence probe predicted no conflict but the rebase found one. Phase is always `Ship` (= 3). |

New emit sites should follow this style: short, lowercase, snake_case keys; values are strings or primitives.

### WORKER_RESULT metadata.escalation

When a worker emits `status: Escalate`, it may include a structured `metadata.escalation` object to convey the escalation context to the orchestrator:

```json
"metadata": {
  "escalation": {
    "reason": "obsolete",
    "subsumed_by": {
      "commit": "abc123def456",
      "files": ["src/Foo.cs", "src/Bar.cs"],
      "rationale": "These changes were already applied in the referenced commit."
    }
  }
}
```

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `reason` | string | yes | Why the worker escalated. Recognized values listed below. |
| `subsumed_by` | object | when `reason == "obsolete"` | The commit that makes this ticket obsolete. |
| `subsumed_by.commit` | string (non-empty) | yes (if subsumed_by present) | SHA of the commit that subsumes this ticket's work. |
| `subsumed_by.files` | string array (non-empty) | yes (if subsumed_by present) | Paths already handled by that commit. |
| `subsumed_by.rationale` | string (non-empty) | yes (if subsumed_by present) | Human-readable explanation of why the ticket is now obsolete. |

**Recognized reasons:**

| Value | Meaning | subsumed_by required? |
|-------|---------|-----------------------|
| `"obsolete"` | The ticket's work is already done by another commit or ticket. | Yes - parser fails with `ValidationError` if absent or incomplete. |

**Unknown reasons:** Any `reason` value not listed above is accepted by the parser without failure. The orchestrator treats an unknown reason as "unknown escalation reason - no auto-resolve" and handles it via its Plan B path.

**Parser rule:** If `metadata.escalation.reason == "obsolete"` (case-insensitive), the `subsumed_by` object must be present and fully populated (`commit` non-empty string, `files` non-empty array, `rationale` non-empty string). A missing or incomplete `subsumed_by` causes the worker result parse to fail with `ValidationError` and message: `metadata.escalation.reason is 'obsolete' but subsumed_by is missing or incomplete (requires commit string, non-empty files array, and rationale string)`. This is consistent with how the parser handles other malformed required fields (`status`, `summary`).

---

## Happy-path Plan example

A successful `build plan <ticket>` against a ticket in `Backlog` produces seven events in this order:

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":0,"Data":{"worker":"claude-code"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":0,"Data":{"status":"Ok"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":0,"Data":{"model":"claude-sonnet-4-6","vendor":"anthropic","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"append_description"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"apply_labels"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":0,"Data":{"from":"Backlog","to":"Ready"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

## Happy-path Implement example

A successful `build implement <ticket>` against a ticket in `Ready` produces events in this order (the `LlmCall` line is only present when the worker reports `llm_usage` metadata):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"Ready","to":"InProgress"},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":1,"Data":{"worker":"claude-code"},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":1,"Data":{"status":"Ok"},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":1,"Data":{"model":"claude-sonnet-4-6","vendor":"anthropic","input_tokens":12000,"output_tokens":3200,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":48000},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":1,"Data":{"action":"create_comment"},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"InProgress","to":"InReview"},"project_id":"<uuid>","workspace_slug":"acme","build_version":"1.0.0"}
```

If the ticket's `[planned_at: <sha>]` marker disagrees with the current `origin/main`, a `GateFailure` line with `kind = "drift_warning"` is emitted before the state transition to `InProgress`; ImplementPhase logs the warning and proceeds without gating.

## Happy-path Review example

A successful `build review <ticket>` against a ticket in `InReview` emits a `WorkerSpawn` (with `role: "verifier"`), then a `VerifierVerdict` carrying `kind` and `checks_failed_count`. When the verifier worker reports `llm_usage` metadata, an `LlmCall` line is emitted. Then a `TicketWrite` for the `reviewed:` comment. The `Rework` path additionally emits a `StateTransition` from `InReview` back to `InProgress`; `Pass` and `Fail` leave the ticket in `InReview`.

Pass (verdict `Pass`, ticket stays in `InReview`):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":2,"Data":{"worker":"claude-code","role":"verifier"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":2,"Data":{"kind":"Pass","checks_failed_count":0},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":2,"Data":{"model":"claude-opus","vendor":"anthropic","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":2,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

Rework (verdict `Rework`, ticket transitions back to `InProgress`):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":2,"Data":{"worker":"claude-code","role":"verifier"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":2,"Data":{"kind":"Rework","checks_failed_count":2},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":2,"Data":{"model":"claude-opus","vendor":"anthropic","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":2,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":2,"Data":{"from":"InReview","to":"InProgress"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

Fail (verdict `Fail`, ticket stays in `InReview`):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":2,"Data":{"worker":"claude-code","role":"verifier"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":2,"Data":{"kind":"Fail","checks_failed_count":0},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":2,"Data":{"model":"claude-opus","vendor":"anthropic","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":2,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

## Happy-path Ship example

A successful `build ship <ticket>` against a ticket in `InReview` rebases the feature branch onto `origin/main`, scans for conflict markers, runs regression checks, fast-forward merges into the local main worktree, posts a `[shipped_at: <sha>]` comment, transitions `InReview -> Done`, then decrufts the worktree and deletes the feature branch. Ship-phase emits no `LlmCall` or `WorkerSpawn` events in v1.

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":3,"Data":{"from":"InReview","to":"Done"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"decruft","halted_at":"complete"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"delete_branch","success":true},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

Ship-blocking failures emit a single `GateFailure` line and halt. The ticket stays in `InReview` and no `StateTransition` is emitted. For example, a rebase that hits conflicts:

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":4,"TicketId":"TLB-34","Phase":3,"Data":{"kind":"rebase_conflicts","conflicting_paths":["src/A.cs","src/B.cs"]},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

A regression-check failure:

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":4,"TicketId":"TLB-34","Phase":3,"Data":{"kind":"regression_checks","checks_failed":["test","lint"]},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

The full set of ShipPhase `GateFailure.kind` values is `rebase_conflicts | rebase_other | conflict_markers | regression_checks | diverged_bases`. Decruft and branch-delete failures after a successful Done transition (Steps 12-13) are logged via the `decruft` and `delete_branch` `TicketWrite` Data shapes documented above; they do not unwind the Done transition.

## Happy-path Chain example

A successful `build chain <ticket>` against a `Backlog` ticket runs Plan -> Implement -> Review (Pass) -> Ship. The file opens with a `ChainStart` and closes with a `ChainEnd`; everything in between is the same shape as the per-phase runs above, just interleaved into one file. The chain's own `SessionId` (the `chain_session_id` carried in the `ChainStart` data) is the `SessionId` on every line - the inner phases share it rather than minting their own.

```jsonl
{"SessionId":"<csid>","Timestamp":"...","Kind":6,"TicketId":"TLB-34","Phase":4,"Data":{"starting_at_phase":"plan","initial_state":"Backlog","chain_session_id":"<csid>"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":0,"Data":{"worker":"claude-code"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":0,"Data":{"status":"Ok"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":0,"Data":{"model":"claude-sonnet-4-6","vendor":"anthropic","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"append_description"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"apply_labels"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":0,"Data":{"from":"Backlog","to":"Ready"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"Ready","to":"InProgress"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":1,"Data":{"worker":"claude-code"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":1,"Data":{"status":"Ok"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":1,"Data":{"model":"claude-sonnet-4-6","vendor":"anthropic","input_tokens":12000,"output_tokens":3200,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":48000},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":1,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"InProgress","to":"InReview"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":2,"Data":{"worker":"claude-code","role":"verifier"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":2,"Data":{"kind":"Pass","checks_failed_count":0},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":2,"Data":{"model":"claude-opus","vendor":"anthropic","input_tokens":8000,"output_tokens":2000,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":15000},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":2,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":3,"Data":{"from":"InReview","to":"Done"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"decruft","halted_at":"complete"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":3,"Data":{"action":"delete_branch","success":true},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":7,"TicketId":"TLB-34","Phase":4,"Data":{"outcome":"Completed","phases_run":4,"rework_rounds":0,"total_duration_ms":312500},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

If the review returns `Rework`, ChainPhase emits a `ReworkRound` line between the Rework `StateTransition` and the next ImplementPhase:

```jsonl
{"SessionId":"<csid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":2,"Data":{"kind":"Rework","checks_failed_count":1},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":2,"Data":{"action":"create_comment"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":2,"Data":{"from":"InReview","to":"InProgress"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":8,"TicketId":"TLB-34","Phase":1,"Data":{"round":1,"verdict_that_triggered":"Rework","rationale_preview":"missing acceptance criterion for empty-list case"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"<csid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":1,"Data":{"worker":"claude-code"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
...
```

If the chain refuses to start (the ticket is in some state other than `Backlog` / `Ready` / `InReview`), the file contains only `ChainStart` (with `starting_at_phase: "refused"`) and `ChainEnd` (with `outcome: "RefusedInitialState"`, `phases_run: 0`).

## Worker-failure example

Worker returns non-`Ok` (e.g. the nested-session guard fires). PlanPhase returns after the verdict event, so the file ends after two lines:

```jsonl
{"SessionId":"06e46b9c08d74e13bd1815300c0b7e83","Timestamp":"2026-05-22T22:37:18.9689774+00:00","Kind":2,"TicketId":"TLB-34","Phase":0,"Data":{"worker":"claude-code"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
{"SessionId":"06e46b9c08d74e13bd1815300c0b7e83","Timestamp":"2026-05-22T22:37:19.3100749+00:00","Kind":3,"TicketId":"TLB-34","Phase":0,"Data":{"status":"Failed"},"project_id":"<uuid>","project_name":"LatticeFlow","workspace_slug":"acme","build_version":"1.0.0"}
```

---

## Durability

The sink ([src/ThroughlineBuild.EventLog/JsonlEventSink.cs](../src/ThroughlineBuild.EventLog/JsonlEventSink.cs)) writes through a 4 KB `FileStream` buffer for throughput. The buffer is flushed by:

- explicit `FlushAsync(ct)` on the sink, or
- `DisposeAsync()` when the sink leaves scope.

The CLI owns the sink with `await using` in each verb-dispatch branch ([src/ThroughlineBuild.Cli/Program.cs:181, 268, 541, 647](../src/ThroughlineBuild.Cli/Program.cs#L647) - one binding per verb family: state-command, `new`, `scaffold`, and the phase verbs). Every exit path - success, phase failure, exception, `Ctrl+C` (handled cooperatively via `CancellationTokenSource`) - runs `DisposeAsync` and flushes. A `kill -9` or process crash will still leave a 0-byte file or a partial trailing line; readers must tolerate that.

---

## Reading the log

Quick inspection:

```bash
cat .build/events/<stem>.jsonl | jq .
```

(The `SessionId` inside each line is still a GUID; only the filename was made human-readable.)

Programmatically: parse line-by-line with any JSON library. Lines are independent; partial files are still partially valid.
