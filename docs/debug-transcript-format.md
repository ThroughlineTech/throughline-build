# Worker Debug Transcript Format

**Status:** Reference
**Scope:** `transcript.jsonl` + `rework-round.json` produced under `.build/sessions/...` when `--debug` is on

The `--debug` flag already tees the raw worker stream to disk (`worker-stdin.txt`,
`worker-stdout.txt`, `worker-stderr.txt`, `envelope-result.txt`, `worker-result.json`).
On top of that raw firehose, `--debug` now also writes a **structured transcript**: the same
claude-code stream re-emitted in a stable, mechanically-comparable JSONL schema, so analysis
of turn counts, per-turn context size, redundant-read rate, and rework classification is a
`jq` away rather than a bespoke parse per run.

This is the inverse of the gate-output work: gates stay quiet in the worker's context; debug
stays verbose on disk. Same principle - context carries only what the next turn needs;
analysis data goes to a side channel.

**Hard guarantee: pure observation.** Everything here is written *after* the worker exits (or
is killed), reads only the captured stream and the brief, and writes only to disk. Nothing
enters the worker's prompt or alters its behavior - the worker runs byte-for-byte identically
with or without `--debug`. Every writer is best-effort: a malformed line is counted and
skipped, and any failure is swallowed so debug capture can never change phase flow.

---

## File location and naming

Both files land in the same per-session scoped directory the raw capture already uses,
keyed by ticket / phase / rework-round:

```
.build/sessions/<stem>/
  session-index.txt                 # appended index of every phase run (existing)
  <TICKET>/
    implement/
      round-0/
        worker-stdin.txt            # the exact prompt the worker received (verbatim)
        worker-stdout.txt           # raw NDJSON firehose
        transcript.jsonl            # <-- structured transcript (this doc)
      round-1/
        ...
        transcript.jsonl
        rework-round.json           # <-- why round-1 happened (this doc)
    review/
      <session>/
        transcript.jsonl
```

- `<stem>` is the same human-readable run stem the event log uses
  ([SessionFileNameBuilder](../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs)).
- The directory path itself encodes `ticket / phase / round`; the transcript repeats those as
  fields so a concatenation of many files is still self-describing.
- Writers: [WorkerTranscriptWriter](../src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs)
  (transcript) and [ReworkRoundManifest](../src/ThroughlineBuild.Phases/ReworkRoundManifest.cs)
  (rework-round).

---

## `transcript.jsonl`

[JSON Lines](https://jsonlines.org/) - one UTF-8 JSON object per line, terminated by `\n`.
Each object carries a `rec` discriminator. The line order is the stream's timeline.

### `rec: "meta"` (always line 1)

The keying record. One per file.

| Field                 | Type        | Notes |
|-----------------------|-------------|-------|
| `rec`                 | `"meta"`    | |
| `schema`              | int         | Schema version (currently `1`). |
| `session_id`          | string?     | Orchestrator session id for the phase (from `DebugTranscriptContext`, falling back to the stream's `session_id`). |
| `stream_session_id`   | string?     | The claude-code CLI session id from the `system` event. |
| `ticket`              | string      | From the brief. |
| `phase`               | string      | From the brief (`Implement`, `Review`, `Plan`, `Decompose`, `Draft`). |
| `rework_round`        | int?        | Present on implement rework rounds; absent otherwise. |
| `build_version`       | string?     | Engine build sha, e.g. `0.1.0+d0ee732` (`build --version`). The key that holds the build constant across A/B runs. |
| `model`               | string?     | Resolved from the stream `system` event, falling back to the configured tier. |
| `claude_code_version` | string?     | From the `system` event. |
| `cwd`                 | string?     | From the `system` event. |
| `prompt_file`         | string      | `"worker-stdin.txt"` - the verbatim prompt lives there (not duplicated here). |
| `prompt_chars`        | int         | Length of the brief instruction. |
| `prompt_sha256`       | string      | Lowercase hex SHA-256 of the verbatim prompt - pins prompt identity across runs. |
| `brief_named_files`   | string[]    | The brief's `RelevantFiles` (the read-map). Diff against `files_read` for redundant-read / under-specification analysis. |
| `brief_named_writes`  | string[]    | The brief's `AllowedWrites`. |
| `invocation_args`     | string[]    | The exact argv passed to the `claude` CLI (model, allowedTools, ...). |
| `session_tools`       | string[]?   | The tool list claude-code exposed (from the `system` event), verbatim. |
| `started_at`          | ISO-8601    | Worker process start (the dt origin for turn 0). |

> **op-doc sha is intentionally absent.** The op-doc only exists at scaffold time; the chain
> runs against a ticket, and stamping the op-doc hash anywhere the worker can see it would
> perturb the very thing being measured. Hold the build constant with `build_version` and run
> the A/B by controlling which op-doc you scaffolded.

### `rec: "turn"` (one per assistant message)

claude-code emits one assistant *message* as several NDJSON lines (one per content block)
sharing a `message.id`; they are grouped back into a single turn here. This is the record
that separates **"more turns"** from **"bigger context per turn"** - the confound that makes
cumulative `cache_read` useless for judging a run.

| Field           | Type    | Notes |
|-----------------|---------|-------|
| `rec`           | `"turn"`| |
| `i`             | int     | 0-based turn index. |
| `message_id`    | string? | The assistant `message.id`. |
| `at`            | ISO-8601| Arrival time of the turn's first stream line. |
| `dt_ms`         | int     | Latency since the previous turn (since `started_at` for turn 0). The CLI stream carries no per-event timestamps; arrival time is the authoritative inter-turn clock. |
| `class`         | string  | `discovery` / `production` / `verification` / `tool` / `reason` / `respond` - derived from the turn's tool names (see below). The raw `tools` are recorded too, so classification is auditable and overridable. |
| `usage`         | object  | Per-turn `{input, output, cache_read, cache_creation}` token counts, verbatim from `message.usage`. `cache_read` is this turn's context size. |
| `tool_count`    | int     | Number of `tool_use` blocks in the turn. |
| `tools`         | array   | `[{name, input}]` - tool name + arguments **verbatim** (file paths and grep/glob patterns preserved). |
| `text_chars`    | int     | Total chars of `text` blocks in the turn. |
| `thinking_chars`| int     | Total chars of `thinking` blocks in the turn. |

Turn class is derived from tool names by precedence: any write tool
(`Write`/`Edit`/`MultiEdit`/`NotebookEdit`) -> `production`; else any `Bash` -> `verification`;
else any read/search tool (`Read`/`NotebookRead`/`Glob`/`Grep`/`LS`/`WebFetch`/`WebSearch`/`Task`)
-> `discovery`; else any other tool -> `tool`; no tools -> `reason` (thinking only) or
`respond` (text).

### `rec: "tool_result"` (one per tool result)

A failed tool call followed by a retry is a wasted turn; this is how to see them.

| Field      | Type           | Notes |
|------------|----------------|-------|
| `rec`      | `"tool_result"`| |
| `for`      | string?        | The `tool_use_id` this result answers. |
| `at`       | ISO-8601       | Arrival time. |
| `bytes`    | int            | UTF-8 byte size of the result content. |
| `lines`    | int            | Newline-delimited line count of the result content. |
| `is_error` | bool           | True when the tool call errored. |

### `rec: "result"` (terminal, one per file)

| Field             | Type       | Notes |
|-------------------|------------|-------|
| `rec`             | `"result"` | |
| `at`              | ISO-8601   | Arrival of the terminal `result` event. |
| `status`          | string     | `ok` / `err` (from the envelope's `is_error`), or `incomplete` when the stream was truncated (killed / timed-out worker). |
| `worker_status`   | string     | The orchestrator's `WorkerResult.Status` (`Ok`/`Failed`/`Escalate`/...). |
| `num_turns`       | int?       | The CLI's own turn count (cross-check against the `turn` records). |
| `duration_ms`     | int?       | CLI-reported wall time. |
| `duration_api_ms` | int?       | CLI-reported API time. |
| `wall_clock_ms`   | int        | Orchestrator-measured wall time for the subprocess. |
| `cost_usd`        | double?    | `total_cost_usd` when present. |
| `usage`           | object     | Cumulative `{input, output, cache_read, cache_creation}` for the session. |
| `files_read`      | string[]   | Concrete files `Read` (not searches). Diff against `brief_named_files`. |
| `files_written`   | string[]   | Files targeted by `Write`/`Edit`/`MultiEdit`/`NotebookEdit`. |
| `files_changed`   | string[]   | The worker's self-reported `WorkerResult.FilesChanged`. |
| `skipped_lines`   | int        | NDJSON lines that failed to parse (diagnostic; normally 0). |

---

## `rework-round.json`

Written into the scoped directory of each implement **rework** round (round >= 1, or any round
driven by prior feedback). A rework is either a *design miss* (front-loadable into the op-doc)
or a *hygiene slip* (the gate's job); this record carries the inputs needed to make that split
mechanically. It is a single pretty-printed JSON object (not JSONL).

| Field                | Type     | Notes |
|----------------------|----------|-------|
| `round`              | int      | The rework round number. |
| `trigger`            | string   | `gate` (a gating check hard-failed) or `review` (the reviewer returned a Rework verdict). The presence of `gate_failed_checks` is the design-miss vs hygiene-slip tell. |
| `rationale`          | string   | The failure rationale, verbatim - the same text fed back into the rework brief. |
| `checks_failed`      | string[] | Names of the failed checks / criteria. |
| `sha_before`         | string?  | HEAD before this round's implement (the prior round's commit; null on the first/resume round). |
| `sha_after`          | string?  | HEAD after this round's implement. |
| `gate_failed_checks` | array    | For gate-triggered reworks: `[{name, exit_code, stdout_tail, stderr_tail}]`, verbatim. Empty for review-triggered reworks. |

---

## Worked example: redundant-read rate

```sh
# Files the brief named vs files the worker actually Read, for one implement session:
jq -r 'select(.rec=="meta")     | .brief_named_files[]' transcript.jsonl | sort -u > named.txt
jq -r 'select(.rec=="result")   | .files_read[]'        transcript.jsonl | sort -u > read.txt
comm -12 named.txt read.txt   # named AND read -> candidate redundant reads (already in the prompt)
comm -13 named.txt read.txt   # read but NOT named  -> add to the next op-doc's read-map
comm -23 named.txt read.txt   # named but NOT read  -> over-specified front-loading to trim
```

```sh
# Per-turn context size vs output - the "more turns" / "bigger context" split:
jq -r 'select(.rec=="turn") | [.i, .class, .usage.cache_read, .usage.output] | @tsv' transcript.jsonl
```
