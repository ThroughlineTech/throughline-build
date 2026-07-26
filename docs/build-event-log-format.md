# Event Log File Format

Status: implemented public contract. This document describes the durable JSONL
event stream written by Throughline Build. It specifies the line envelope and
numeric enum values; the contents of `Data` remain emitter-specific.

Related contracts:

- [Worker debug transcript](debug-transcript-format.md) describes optional,
  high-detail worker diagnostics.
- [WORKER_RESULT envelope](worker-result-envelope.md) describes the structured
  result returned by a worker before phases emit summary events.
- [Architecture](throughline-build-architecture.md) explains where the event
  sink sits in the application.

## Location and filename

The default directory is `.build/events/`, configurable with
`events.log_directory`. Each invocation writes one UTF-8 file:

```text
{project}-{ticket-or-slug}-{verb}-{yyyy-MM-dd}-{HHmmss}.jsonl
```

For example:

```text
latticeflow-TLB-169-implement-2026-07-26-143052.jsonl
```

The project component is lower-case. A ticket ID preserves its canonical case.
When no ticket exists, a command-specific slug may be used. Missing verbs become
`run`. Unsafe filename characters are replaced or removed.

The human-readable filename is not the session identifier. Every record carries
a separate per-invocation `SessionId`, normally a 32-character GUID without
hyphens. The sink opens files in append mode, so two identical stems generated
within the same second can share a file; consumers must group records by
`SessionId`, not filename.

The file is opened lazily on the first event. An invocation that emits no events
does not create a file.

Normative implementation:

- [`SessionFileNameBuilder`](../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs)
- [`JsonlEventSink`](../src/ThroughlineBuild.EventLog/JsonlEventSink.cs)
- [`EventLogOptions`](../src/ThroughlineBuild.EventLog/EventLogOptions.cs)

## Record schema

Each physical line is one complete JSON object followed by LF:

| Field | JSON type | Required | Meaning |
|---|---|---:|---|
| `SessionId` | string | yes | Correlation ID for one CLI invocation |
| `Timestamp` | string | yes | ISO 8601 `DateTimeOffset` |
| `Kind` | integer | yes | Event kind; see below |
| `TicketId` | string | yes | Ticket identifier; may be empty for dispatch-wide events |
| `Phase` | integer | yes | Logical phase; see below |
| `Data` | object | yes | Event-specific payload |
| `project_id` | string | no | Ticketing project UUID |
| `project_name` | string | no | Human-readable project name |
| `workspace_slug` | string | no | Ticketing workspace slug |
| `build_version` | string | no | CLI version that emitted the line |

The six original fields use PascalCase. Optional session-context fields use
snake_case and are omitted when unavailable. `Kind` and `Phase` serialize as
integers. Readers must tolerate unknown object properties, unknown future enum
values, and new keys inside `Data`.

Normative implementation:

- [`WorkflowEvent`](../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs)
- [`EventLineDto`](../src/ThroughlineBuild.EventLog/EventLineDto.cs)
- [`EventLogJsonContext`](../src/ThroughlineBuild.EventLog/EventLogJsonContext.cs)

## Event kinds

Numeric values are append-only compatibility identifiers:

| Value | Name | General purpose |
|---:|---|---|
| 0 | `StateTransition` | Ticket state changed |
| 1 | `LlmCall` | Model usage and timing summary |
| 2 | `WorkerSpawn` | Worker invocation started |
| 3 | `VerifierVerdict` | Worker or verification result |
| 4 | `GateFailure` | Gate, warning, or refusal diagnostic |
| 5 | `TicketWrite` | Ticket or workflow side effect |
| 6 | `ChainStart` | Chain execution started |
| 7 | `ChainEnd` | Chain execution ended |
| 8 | `ReworkRound` | A chain rework attempt started |
| 9 | `TicketSubsumed` | Work was ratified as already satisfied |
| 10 | `TargetAutoRebased` | Target branch auto-rebase attempt |
| 11 | `DispatchStart` | Multi-root dispatch started |
| 12 | `DispatchEnd` | Multi-root dispatch ended |
| 13 | `CostLedger` | Token, context, preload, or gate-cost accounting |

`GateFailure` is a historical name, not a universal severity guarantee. Some
emitters use it for non-blocking warnings or recovered conditions. Consumers
must inspect the payload and surrounding outcome rather than treating every
kind `4` record as terminal.

## Phases

| Value | Name |
|---:|---|
| 0 | `Plan` |
| 1 | `Implement` |
| 2 | `Review` |
| 3 | `Ship` |
| 4 | `Chain` |
| 5 | `New` |
| 6 | `Command` |
| 7 | `Draft` |
| 8 | `Scaffold` |
| 9 | `Decompose` |
| 10 | `Gate` |

The normative enum is
[`Phase`](../src/ThroughlineBuild.Contracts/Models/Phase.cs). A phase value
identifies the context in which an event was emitted; it does not imply that a
standalone command with the same name was used.

## `Data` payload

`Data` is a JSON object whose schema belongs to the emitter. It is intentionally
not a single versioned union in this contract. Common key families include:

| Kind | Common keys |
|---|---|
| `StateTransition` | `from`, `to` |
| `LlmCall` | `model`, `vendor`, token counts, `wall_clock_ms` |
| `WorkerSpawn` | `worker`, optionally `role` |
| `VerifierVerdict` | `status` or phase-specific verdict fields |
| `GateFailure` | often `kind`, plus diagnostic details |
| `TicketWrite` | usually `action`, plus action-specific details |
| `ChainStart` | `starting_at_phase`, `initial_state`, `chain_session_id` |
| `ChainEnd` | `outcome`, `phases_run`, `rework_rounds`, `total_duration_ms` |
| `ReworkRound` | `round`, `verdict_that_triggered`, `rationale_preview` |
| `DispatchStart` | `ticket_count`, `level_count`, `max_concurrency` |
| `DispatchEnd` | `outcome`, `total_duration_ms` |
| `CostLedger` | emitter-specific accounting fields |

`CostLedger` intentionally has several shapes. Current emitters use it for
context attribution, preload summaries, and deterministic gate costs. A reader
must not require a discriminator such as `Data.kind`.

Payloads are constrained to the source-generated JSON types registered in
`EventLogJsonContext`: scalar strings, booleans, integers, longs, and doubles;
string arrays/lists; dictionaries; and lists of dictionaries. Do not infer an
exhaustive payload schema from examples.

## Examples

A state transition with full session context:

```json
{"SessionId":"06e46b9c08d74e13bd1815300c0b7e83","Timestamp":"2026-07-26T21:30:52.1200000+00:00","Kind":0,"TicketId":"TLB-169","Phase":1,"Data":{"from":"Ready","to":"InProgress"},"project_id":"c605c531-39de-4bc1-834e-86ecaece87a4","project_name":"LatticeFlow","workspace_slug":"example","build_version":"1.0.0"}
```

A dispatch-wide record uses an empty ticket ID:

```json
{"SessionId":"23c1bc4a5c05459c9162ddbab8f8fd3d","Timestamp":"2026-07-26T21:31:00+00:00","Kind":11,"TicketId":"","Phase":4,"Data":{"ticket_count":3,"level_count":2,"max_concurrency":1}}
```

A two-line JSONL excerpt:

```jsonl
{"SessionId":"ea6dc94cc5894616af540d3355f24389","Timestamp":"2026-07-26T21:32:00+00:00","Kind":2,"TicketId":"TLB-169","Phase":2,"Data":{"worker":"codex","role":"verifier"}}
{"SessionId":"ea6dc94cc5894616af540d3355f24389","Timestamp":"2026-07-26T21:32:10+00:00","Kind":3,"TicketId":"TLB-169","Phase":2,"Data":{"kind":"Pass","checks_failed_count":0}}
```

## Durability and reader behavior

`JsonlEventSink` serializes concurrent writes with a semaphore and writes the
JSON bytes and trailing LF while holding that lock. `FlushAsync` flushes an open
stream; disposal closes it. The file is shared for reading while the process is
running.

Robust consumers should:

1. Enumerate `*.jsonl` files, but correlate by `SessionId`.
2. Parse each non-empty line independently.
3. Preserve enum integers even when their names are unknown.
4. Ignore unknown top-level and `Data` properties.
5. Treat a malformed or truncated final line as an incomplete write, not as
   corruption of earlier lines.
6. Use explicit outcome fields and ticket state for success decisions rather
   than assuming a fixed event sequence.
