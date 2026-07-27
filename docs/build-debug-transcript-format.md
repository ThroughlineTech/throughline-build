# Worker Debug Transcript Format

Status: implemented diagnostic reference. These artifacts are optional,
best-effort side channels written under `.build/sessions/` when `--debug` is
enabled. They are not workflow inputs and a capture failure does not change the
phase result.

This document covers two independent artifacts:

1. `transcript.jsonl`, currently produced only for Claude Code transports when
   their captured stream can be normalized.
2. `rework-round.json`, produced by recursive chain orchestration for a
   feedback-driven implement rework round.

Provider-specific raw capture files are intentionally outside this contract.
Claude Code, Codex, Gemini, and Copilot write different file sets, and some
files exist only after particular failures.

Related contracts:

- [Event log](build-event-log-format.md) defines the durable workflow event stream
  and its session correlation.
- [WORKER_RESULT envelope](build-worker-result-envelope.md) defines the worker result
  summarized by `worker_status`.

## Capture directories

The CLI derives a human-readable run stem and creates scoped directories below
`.build/sessions/<stem>/`. Ticket, phase, and optional `round-N` components keep
captures separate. Non-round phases may use a generated session component
instead.

The exact directory construction is an implementation detail. Consumers should
discover files recursively rather than synthesize paths:

```sh
find .build/sessions -name transcript.jsonl -o -name rework-round.json
```

The run stem follows
[`SessionFileNameBuilder`](../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs),
but it is not a unique session ID. See the event-log contract for its
same-second collision semantics.

## Claude Code `transcript.jsonl`

[`WorkerTranscriptWriter`](../src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs)
normalizes the captured Claude Code stream into UTF-8 JSON Lines. It emits one
JSON object per line, each with a `rec` discriminator. The writer operates
after the worker finishes, skips malformed input lines, and swallows its own
failures.

Interactive Claude capture may already be redacted and normalized before this
writer receives it. Consequently, the transcript is an analysis representation,
not a byte-for-byte copy of provider output. Provider-specific capture files
remain the place to inspect available raw material.

### `rec: "meta"`

The first record keys the transcript:

| Field | Type | Presence |
|---|---|---|
| `rec` | `"meta"` | always |
| `schema` | integer | always; currently `1` |
| `session_id` | string or null | always |
| `stream_session_id` | string or null | always |
| `ticket` | string | always |
| `phase` | string | always |
| `rework_round` | integer | when supplied by the orchestrator |
| `build_version` | string or null | always |
| `model` | string or null | always |
| `claude_code_version` | string or null | always |
| `cwd` | string or null | always |
| `prompt_file` | string | always; `worker-stdin.txt` |
| `prompt_chars` | integer | always |
| `prompt_sha256` | string | always; lower-case SHA-256 |
| `brief_named_files` | string array | always |
| `brief_named_writes` | string array | always |
| `invocation_args` | string array | always |
| `session_tools` | array | when present in the provider stream |
| `started_at` | ISO 8601 string | always |

`session_id` prefers
[`DebugTranscriptContext`](../src/ThroughlineBuild.Contracts/Models/DebugTranscriptContext.cs)
and falls back to the provider stream session. `stream_session_id` retains the
provider value separately.

### `rec: "turn"`

One record represents one grouped assistant message:

| Field | Type | Meaning |
|---|---|---|
| `i` | integer | Zero-based turn index |
| `message_id` | string or null | Provider message ID |
| `at` | ISO 8601 string | First-line arrival time |
| `dt_ms` | integer | Time since the previous turn, or process start |
| `class` | string | Derived activity class |
| `usage` | object | `input`, `output`, `cache_read`, `cache_creation` |
| `tool_count` | integer | Number of tool-use blocks |
| `tools` | array | Tool `name` and normalized `input` |
| `text_chars` | integer | Text-block character count |
| `thinking_chars` | integer | Thinking-block character count |

The class is derived from tool names with this precedence: write tools become
`production`; `Bash` becomes `verification`; read/search tools become
`discovery`; other tools become `tool`; a tool-free turn becomes `reason` when
it contains thinking only, otherwise `respond`.

### `rec: "tool_result"`

| Field | Type | Meaning |
|---|---|---|
| `for` | string or null | Matching tool-use ID |
| `at` | ISO 8601 string | Arrival time |
| `bytes` | integer | UTF-8 content size |
| `lines` | integer | Content line count |
| `is_error` | boolean | Provider error flag |

### `rec: "result"`

A provider terminal result contains:

| Field | Type | Presence |
|---|---|---|
| `status` | `"ok"` or `"err"` | always |
| `worker_status` | string | always |
| `at` | ISO 8601 string | always |
| `num_turns` | integer | when supplied |
| `duration_ms` | integer | when supplied |
| `duration_api_ms` | integer | when supplied |
| `wall_clock_ms` | integer | always |
| `cost_usd` | number | when supplied |
| `usage` | object | when supplied |
| `files_read` | string array | always |
| `files_written` | string array | always |
| `files_changed` | string array | always |
| `skipped_lines` | integer | always |

If the captured stream has no terminal result, the writer emits a synthetic
record with `status: "incomplete"`. That record has `worker_status`,
`wall_clock_ms`, the three file arrays, and `skipped_lines`; it has no required
`at` or `usage`.

Representative records:

```jsonl
{"rec":"meta","schema":1,"session_id":"ad0389c860b149f8aa8926ff5e540648","stream_session_id":null,"ticket":"TLB-169","phase":"Implement","build_version":"1.0.0","model":"sonnet","claude_code_version":null,"cwd":"C:/repo","prompt_file":"worker-stdin.txt","prompt_chars":1200,"prompt_sha256":"9a65a43f1894924756c013546d580ed1a4a5cec127f16e8d3f6c8a1c8c6415d0","brief_named_files":["src/A.cs"],"brief_named_writes":["src/A.cs"],"invocation_args":["--print"],"started_at":"2026-07-26T21:30:00+00:00"}
{"rec":"result","status":"incomplete","worker_status":"Failed","wall_clock_ms":30000,"files_read":[],"files_written":[],"files_changed":[],"skipped_lines":1}
```

## Chain `rework-round.json`

[`ReworkRoundManifest`](../src/ThroughlineBuild.Phases/ReworkRoundManifest.cs)
writes one pretty-printed JSON object when `ChainPhase` has feedback for an
implement re-dispatch and debug capture is enabled. Standalone `build rework`
and initial implement rounds are not promised to create this file.

| Field | Type | Meaning |
|---|---|---|
| `round` | integer | Rework round number |
| `trigger` | string | `gate` when failed gate checks exist; otherwise `review` |
| `rationale` | string | Feedback passed into rework |
| `checks_failed` | string array | Failed check or criterion names |
| `sha_before` | string or null | Commit before the round |
| `sha_after` | string or null | Commit after the round |
| `gate_failed_checks` | array | Gate records with `name`, `exit_code`, `stdout_tail`, `stderr_tail` |

Example:

```json
{
  "round": 1,
  "trigger": "gate",
  "rationale": "Tests failed",
  "checks_failed": ["test"],
  "sha_before": "a1b2c3d",
  "sha_after": "d4e5f6a",
  "gate_failed_checks": [
    {
      "name": "test",
      "exit_code": 1,
      "stdout_tail": "",
      "stderr_tail": "one failing test"
    }
  ]
}
```

Both writers are observational and best-effort. Tool inputs, rationale, command
output tails, and filesystem paths may contain sensitive repository data; keep
`.build/sessions/` private.
