# Event Log File Format

**Status:** Reference
**Scope:** `.build/events/*.jsonl` produced by the `build` CLI

The `build` CLI writes a structured event log for every invocation. Each run produces one file. Downstream tooling (audits, replay, cost analysis) reads these files.

---

## File location and naming

- Directory: configured by `events.log_directory` in `.build/config.toml` (typically `.build/events/`).
- Filename: `<session-id>.jsonl`, where `<session-id>` is a fresh GUID (no hyphens) generated per CLI invocation.
- Format: [JSON Lines](https://jsonlines.org/) - one UTF-8 JSON object per line, terminated by `\n`. Append-only.
- Empty file: legal. It means the run exited before emitting any event (for example, PlanPhase rejects a ticket that is not in `Backlog` state at [src/ThroughlineBuild.Phases/PlanPhase.cs:79-80](../src/ThroughlineBuild.Phases/PlanPhase.cs#L79-L80) before the first emit).

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

From `EventKind` at [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:11](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11):

| Value | Name              | Meaning |
|-------|-------------------|---------|
| 0     | `StateTransition` | Ticket moved between Plane states (e.g. Backlog -> Ready). |
| 1     | `LlmCall`         | Direct LLM invocation. Emitted by PlanPhase once per successful plan with worker token usage. |
| 2     | `WorkerSpawn`     | A worker agent (e.g. Claude Code) was launched. |
| 3     | `VerifierVerdict` | A worker or verifier returned a status. |
| 4     | `GateFailure`     | A precondition gate fired. Used by ImplementPhase to surface a `drift_warning` (the run still proceeds) and by ShipPhase to surface ship-blocking conditions (the run halts and the ticket stays in `InReview`). |
| 5     | `TicketWrite`     | Side effect on a Plane ticket (description, labels, comment). |

### Phases

From `Phase` at [src/ThroughlineBuild.Contracts/Models/Phase.cs:3](../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3):

| Value | Name      |
|-------|-----------|
| 0     | `Plan`    |
| 1     | `Implement` |
| 2     | `Review`  |
| 3     | `Ship`    |
| 4     | `Chain`   |
| 5     | `New`     |

---

## Data conventions

`Data` is free-form per `Kind`. Current emitters (in [PlanPhase](../src/ThroughlineBuild.Phases/PlanPhase.cs) and [ImplementPhase](../src/ThroughlineBuild.Phases/ImplementPhase.cs)):

| Kind              | Keys              | Example |
|-------------------|-------------------|---------|
| `WorkerSpawn`     | `worker`, optional `role` | `{"worker": "claude-code"}` (Plan/Implement phases omit `role`); `{"worker": "claude-code", "role": "verifier"}` (Review phase sets `role` to distinguish verifier spawns from implementer spawns in chained runs) |
| `VerifierVerdict` | Plan/Implement: `status`. Review: `kind`, `checks_failed_count` | Plan/Implement: `{"status": "Ok"}`, `{"status": "Failed"}`. Review: `{"kind": "Pass", "checks_failed_count": 0}`, `{"kind": "Rework", "checks_failed_count": 2}`, `{"kind": "Fail", "checks_failed_count": 0}` (`kind` is one of `Pass`, `Rework`, `Fail`) |
| `LlmCall`         | `model`, `vendor`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`, `wall_clock_ms`, optional `partial` | `{"model": "claude-sonnet-4-6", "vendor": "anthropic", "input_tokens": 1234, "output_tokens": 567, "cache_read_tokens": 0, "cache_create_tokens": 0, "wall_clock_ms": 2500}`. `model` is extracted from the NDJSON `type=system` event; falls back to the configured `default_model` (vendor prefix stripped) if the system event is absent. `vendor` is always `"anthropic"` for current Claude Code workers. `anthropic_request_id` is NOT included: the Claude Code CLI does not expose the API request ID in its stream output. |
| `TicketWrite`     | `action`, plus action-specific extras | `{"action": "append_description"}`, `{"action": "apply_labels"}`, `{"action": "create_comment"}`, `{"action": "decruft", "halted_at": "complete"}` (ShipPhase; `halted_at` is `complete` on success, a `DecruftStep` name on halt, or `exception` if the decrufter threw - in which case an `error` key carries the exception message), `{"action": "delete_branch", "success": true}` (ShipPhase; on failure a `reason` key carries the git error) |
| `StateTransition` | `from`, `to`      | `{"from": "Backlog", "to": "Ready"}`, `{"from": "Ready", "to": "InProgress"}`, `{"from": "InProgress", "to": "InReview"}`, `{"from": "InReview", "to": "InProgress"}` (Review-phase Rework path), `{"from": "InReview", "to": "Done"}` (Ship-phase success) |
| `GateFailure`     | `kind`, kind-specific extras | `{"kind": "drift_warning", "planned_at_sha": "...", "main_sha": "..."}` (ImplementPhase emits this when the `[planned_at: <sha>]` marker on a ticket disagrees with current `origin/main`; the phase logs the warning and proceeds without gating). ShipPhase emits one of five ship-blocking kinds, all of which halt the run with the ticket left in `InReview`: `{"kind": "rebase_conflicts", "conflicting_paths": [...]}`, `{"kind": "rebase_other", "reason": "..."}`, `{"kind": "conflict_markers", "marker_files": [...]}`, `{"kind": "regression_checks", "checks_failed": [...]}`, `{"kind": "diverged_bases", "local_ref": "...", "remote_ref": "..."}` |

New emit sites should follow this style: short, lowercase, snake_case keys; values are strings or primitives.

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

The CLI owns the sink with `await using` at [src/ThroughlineBuild.Cli/Program.cs:96](../src/ThroughlineBuild.Cli/Program.cs#L96), so every exit path - success, phase failure, exception, `Ctrl+C` (handled cooperatively via `CancellationTokenSource`) - runs `DisposeAsync` and flushes. A `kill -9` or process crash will still leave a 0-byte file or a partial trailing line; readers must tolerate that.

---

## Reading the log

Quick inspection:

```bash
cat .build/events/<session-id>.jsonl | jq .
```

Programmatically: parse line-by-line with any JSON library. Lines are independent; partial files are still partially valid.
