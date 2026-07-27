# WORKER_RESULT Envelope Specification

Status: implemented worker-to-orchestrator contract. This document separates
the core envelope parser from phase-specific metadata consumers.

Related references:

- [Event log](build-event-log-format.md) describes summaries emitted after worker
  processing.
- [Debug transcript](build-debug-transcript-format.md) describes optional diagnostic
  capture of worker sessions.
- [Architecture](throughline-build-architecture.md) describes the worker
  boundary and AOT serialization constraints.

Normative parser:
[`WorkerResultParser`](../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs).

## Core envelope

A worker finishes by emitting the literal marker `WORKER_RESULT` on its own
line, followed immediately by a JSON object:

```text
WORKER_RESULT
{"status":"Ok","summary":"Implemented the ticket","files_changed":["src/A.cs"],"failure_reason":null,"metadata":{}}
```

Leading and trailing whitespace on the marker line is tolerated. The JSON may
be pretty-printed and may be wrapped in a standalone triple-backtick fence,
with or without a language tag.

The parser consumes the first complete JSON value following the marker.
Trailing narration is ignored, but workers should emit the final envelope last.
When output contains several markers, markers are examined from last to first
and the last valid envelope wins. This tolerates an echoed template before the
real result.

### Fields

| Field | JSON type | Required | Meaning |
|---|---|---:|---|
| `status` | string | yes | `Ok`, `NeedsRework`, `Failed`, or `Escalate`, matched case-insensitively |
| `summary` | string | yes | Non-empty human-readable summary |
| `files_changed` | string array | no | Paths reported as changed; defaults to empty |
| `failure_reason` | string or null | no | Failure or rework detail |
| `metadata` | object | no | Phase-specific values; defaults to empty |
| `tickets` | array | no | Batch extension described below |

Status interpretation belongs to the calling phase. The core parser validates
the enum and non-empty summary, converts metadata values to `JsonElement`, and
returns any scanned named blocks. It does not validate every phase-specific
metadata key.

Canonical JSON payload:

```json
{
  "status": "Ok",
  "summary": "Implemented TLB-500",
  "files_changed": ["src/A.cs"],
  "failure_reason": null,
  "metadata": {
    "commit_sha": "abc123",
    "summary_ref": "IMPLEMENT_SUMMARY"
  }
}
```

## Batch `tickets` extension

Batch implementation uses the same envelope plus a required non-empty
`tickets` array. The dedicated batch parser requires every entry to contain:

| Field | JSON type | Validation |
|---|---|---|
| `ticket_id` | string | non-empty |
| `commit_sha` | string | non-empty |
| `stack_position` | integer | present |
| `files_changed` | string array | present; entries non-empty |
| `summary_ref` | string | non-empty |

Example:

```json
{
  "status": "Ok",
  "summary": "Implemented two-ticket stack",
  "files_changed": ["src/A.cs", "src/B.cs"],
  "failure_reason": null,
  "metadata": {},
  "tickets": [
    {
      "ticket_id": "TLB-500",
      "commit_sha": "abc123",
      "stack_position": 1,
      "files_changed": ["src/A.cs"],
      "summary_ref": "IMPLEMENT_SUMMARY_1"
    },
    {
      "ticket_id": "TLB-501",
      "commit_sha": "def456",
      "stack_position": 2,
      "files_changed": ["src/B.cs"],
      "summary_ref": "IMPLEMENT_SUMMARY_2"
    }
  ]
}
```

The generic parser also accepts a non-empty `tickets` array and validates its
entries when present. The dedicated batch parser additionally rejects a missing
or empty array.

## Named fenced blocks

Large markdown or JSON bodies can be placed before the final envelope and
referenced from metadata.

### Syntax

```text
<<<PLAN_BODY_START
# Implementation plan

Detailed markdown goes here.
<<<PLAN_BODY_END

WORKER_RESULT
{"status":"Ok","summary":"Plan created","metadata":{"plan_body_ref":"PLAN_BODY"}}
```

A block name must match `^[A-Z][A-Z0-9_]*$`. The opening and closing forms are:

```text
<<<NAME_START
<<<NAME_END
```

Markers are recognized after trimming the whole line. Blocks are scanned only
before the last `WORKER_RESULT` marker. Duplicate names are allowed and the
last block wins.

Workers must reserve lines beginning with `<<<` for block markers. The current
scanner does not preserve such marker-shaped content lines. It also normalizes
content by joining lines with LF and does not preserve leading blank lines.
Other indentation, internal blank lines, markdown, and code are retained.

The scanner rejects:

- an invalid block name;
- an opening marker while another block is open;
- a closing marker without an open block;
- a mismatched closing name; and
- a block left open at the final envelope or end of output.

Diagnostics include the block name and source line where applicable. Treat the
exact prose of diagnostics as implementation detail.

### Reference resolution

By convention, metadata keys ending in `_ref` contain a block name.
`WorkerResultParser` does not resolve or globally validate these references.
It returns:

- parsed envelope fields; and
- a case-sensitive map of block name to content.

Phase consumers call `FencedBlockResolver.TryResolveRef` for the keys they
understand. A missing or malformed reference can therefore be required,
optional, or eligible for a legacy fallback depending on the consumer.

Current registry:

| Consumer | Metadata/reference | Conventional block | Policy |
|---|---|---|---|
| Plan | `plan_body_ref` | `PLAN_BODY` | required |
| Draft | `body_markdown_ref` | `DRAFT_BODY` | preferred; legacy inline `body_markdown` fallback |
| Implement summary | `summary_ref` | `IMPLEMENT_SUMMARY` | best-effort |
| Implement claim | `completion_claim_ref` | `COMPLETION_CLAIM` | validated when present; invalid claims may trigger a re-ask |
| Review | `rationale_ref` | `REVIEW_CRITIQUE` | preferred; legacy rationale fallback |
| Batch implement | each `tickets[].summary_ref` | `IMPLEMENT_SUMMARY_<stack_position>` | checked by batch commit verification |
| Batch review | `rationale_ref` | `REVIEW_CRITIQUE` | resolved by review handling |

The `COMPLETION_CLAIM` block contains JSON interpreted by
[`CompletionClaimParser`](../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs),
not by the envelope parser. Its `provides`, `consumes`, `ac_bindings`, and
`tests_added` arrays are all required but may be empty.

## Escalation metadata

An `Escalate` result may include `metadata.escalation`. The only reason with
special parser validation is `obsolete`, matched case-insensitively:

```json
{
  "status": "Escalate",
  "summary": "The ticket is already satisfied",
  "metadata": {
    "escalation": {
      "reason": "obsolete",
      "subsumed_by": {
        "commit": "abc123def456",
        "files": ["src/A.cs"],
        "rationale": "The same change landed in a preceding ticket"
      }
    }
  }
}
```

For `obsolete`, `subsumed_by.commit` and `subsumed_by.rationale` must be
non-empty strings and `subsumed_by.files` must be a non-empty array. Unknown
reasons pass through the parser; orchestration does not auto-resolve them as
obsolete.

## Decompose metadata

Decompose keeps typed child specifications in
`metadata.child_specs` rather than named blocks:

```json
{
  "child_specs": [
    {
      "title": "Implement the parser",
      "description": "Add the parser and its unit tests.",
      "acceptance_criteria": "Valid input parses and invalid input fails clearly.",
      "size": "S",
      "scope_boundary": "Parser and parser tests only."
    }
  ]
}
```

The phase retains object entries with a title and description. Its verdict then
requires a non-empty scope boundary, case-insensitively unique titles, and size
`S`, `M`, or `L`. Acceptance criteria default to an empty string. Writing
guidance in brief templates may be stricter, but sentence counts, character
limits, and markdown restrictions are not enforced by the envelope parser.

## Compatibility guidance

Worker authors should:

1. Emit named blocks before the final envelope.
2. Emit one final `WORKER_RESULT` marker and valid JSON object.
3. Use canonical snake_case field names.
4. Keep `summary` non-empty for every status.
5. Follow the phase template for required metadata and block references.
6. Avoid lines beginning with `<<<` inside block content.

Parser consumers should:

1. Distinguish marker, fence-scan, JSON, status, and validation failures.
2. Treat metadata as phase-specific `JsonElement` values.
3. Resolve only references their phase owns.
4. Preserve unknown metadata and escalation reasons where possible.
