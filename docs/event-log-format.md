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

Each line is a `WorkflowEvent` ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs)):

| Field       | Type                          | Notes |
|-------------|-------------------------------|-------|
| `SessionId` | string                        | Same GUID as the filename. Identifies one CLI run. |
| `Timestamp` | ISO-8601 with offset          | UTC, e.g. `2026-05-22T22:37:18.9689774+00:00`. |
| `Kind`      | integer (enum)                | See [Event kinds](#event-kinds). |
| `TicketId`  | string                        | Plane work-item identifier, e.g. `TLB-34`. |
| `Phase`     | integer (enum)                | See [Phases](#phases). |
| `Data`      | object (string -> any)        | Kind-specific payload. See [Data conventions](#data-conventions). |

Enums serialize as integers (default `System.Text.Json` behavior; no string converter is registered in [EventLogJsonContext](../src/ThroughlineBuild.EventLog/EventLogJsonContext.cs)). Property names are PascalCase as written by the source generator.

### Event kinds

From `EventKind` at [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:11](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11):

| Value | Name              | Meaning |
|-------|-------------------|---------|
| 0     | `StateTransition` | Ticket moved between Plane states (e.g. Backlog -> Ready). |
| 1     | `LlmCall`         | Direct LLM invocation. Emitted by PlanPhase once per successful plan with worker token usage. |
| 2     | `WorkerSpawn`     | A worker agent (e.g. Claude Code) was launched. |
| 3     | `VerifierVerdict` | A worker or verifier returned a status. |
| 4     | `GateFailure`     | A precondition gate fired. Used by ImplementPhase to surface a `drift_warning` (the run still proceeds). |
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
| `WorkerSpawn`     | `worker`          | `{"worker": "claude-code"}` |
| `VerifierVerdict` | `status`          | `{"status": "Ok"}`, `{"status": "Failed"}` |
| `LlmCall`         | `model`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`, `wall_clock_ms`, optional `partial` | `{"model": "claude-opus", "input_tokens": 1234, "output_tokens": 567, "cache_read_tokens": 0, "cache_create_tokens": 0, "wall_clock_ms": 2500}` |
| `TicketWrite`     | `action`          | `{"action": "append_description"}`, `{"action": "apply_labels"}`, `{"action": "create_comment"}` |
| `StateTransition` | `from`, `to`      | `{"from": "Backlog", "to": "Ready"}`, `{"from": "Ready", "to": "InProgress"}`, `{"from": "InProgress", "to": "InReview"}` |
| `GateFailure`     | `kind`, kind-specific extras | `{"kind": "drift_warning", "planned_at_sha": "...", "main_sha": "..."}` (ImplementPhase emits this when the `[planned_at: <sha>]` marker on a ticket disagrees with current `origin/main`; the phase logs the warning and proceeds without gating) |

New emit sites should follow this style: short, lowercase, snake_case keys; values are strings or primitives.

---

## Happy-path Plan example

A successful `build plan <ticket>` against a ticket in `Backlog` produces seven events in this order:

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":0,"Data":{"worker":"claude-code"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":0,"Data":{"status":"Ok"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":0,"Data":{"model":"claude-opus","input_tokens":1234,"output_tokens":567,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":2500}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"append_description"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"apply_labels"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":0,"Data":{"action":"create_comment"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":0,"Data":{"from":"Backlog","to":"Ready"}}
```

## Happy-path Implement example

A successful `build implement <ticket>` against a ticket in `Ready` produces events in this order (the `LlmCall` line is only present when the worker reports `llm_usage` metadata):

```jsonl
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"Ready","to":"InProgress"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":2,"TicketId":"TLB-34","Phase":1,"Data":{"worker":"claude-code"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":3,"TicketId":"TLB-34","Phase":1,"Data":{"status":"Ok"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":1,"TicketId":"TLB-34","Phase":1,"Data":{"model":"claude-sonnet","input_tokens":12000,"output_tokens":3200,"cache_read_tokens":0,"cache_create_tokens":0,"wall_clock_ms":48000}}
{"SessionId":"<sid>","Timestamp":"...","Kind":5,"TicketId":"TLB-34","Phase":1,"Data":{"action":"create_comment"}}
{"SessionId":"<sid>","Timestamp":"...","Kind":0,"TicketId":"TLB-34","Phase":1,"Data":{"from":"InProgress","to":"InReview"}}
```

If the ticket's `[planned_at: <sha>]` marker disagrees with the current `origin/main`, a `GateFailure` line with `kind = "drift_warning"` is emitted before the state transition to `InProgress`; ImplementPhase logs the warning and proceeds without gating.

## Worker-failure example

Worker returns non-`Ok` (e.g. the nested-session guard fires). PlanPhase returns after the verdict event, so the file ends after two lines:

```jsonl
{"SessionId":"06e46b9c08d74e13bd1815300c0b7e83","Timestamp":"2026-05-22T22:37:18.9689774+00:00","Kind":2,"TicketId":"TLB-34","Phase":0,"Data":{"worker":"claude-code"}}
{"SessionId":"06e46b9c08d74e13bd1815300c0b7e83","Timestamp":"2026-05-22T22:37:19.3100749+00:00","Kind":3,"TicketId":"TLB-34","Phase":0,"Data":{"status":"Failed"}}
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
