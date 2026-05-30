# WORKER_RESULT Envelope Specification

This document specifies the WORKER_RESULT envelope contract and the fenced-block protocol extension that workers use to deliver named blocks of content alongside structured results.

## WORKER_RESULT Envelope

The WORKER_RESULT envelope is the primary mechanism for workers to communicate completion status, summary information, and structured metadata to the orchestrator.

### Marker and Payload

- **Marker**: The literal string `WORKER_RESULT` on its own line (leading and trailing whitespace is tolerated)
- **Payload**: A JSON object immediately following the marker, optionally wrapped in triple-backtick code fence (` ``` `)
- **Code-fence stripping**: The parser strips leading and trailing triple-backtick fences before JSON deserialization, allowing the envelope to be wrapped in markdown code blocks

### Required Fields

- `status` (string): One of `Ok`, `NeedsRework`, `Failed`, or `Escalate`
  - `Ok`: Work completed successfully
  - `NeedsRework`: Work completed but requires rework
  - `Failed`: Work failed and cannot continue
  - `Escalate`: Work is escalating to human review or a different worker
- `summary` (string): Non-empty human-readable summary of the result

### Optional Fields

- `files_changed` (array of strings): List of file paths changed by the worker
- `failure_reason` (string or null): Detailed description of failure cause (typically used when status is `Failed` or `NeedsRework`)
- `metadata` (object): Arbitrary structured metadata about the result (see below for schema extensions)

### Multiple Envelopes

If multiple `WORKER_RESULT` markers appear in the output, the last valid envelope wins. Earlier envelopes are ignored. This allows workers to output intermediate status messages or diagnostic data without affecting the final result.

### Metadata: Escalation Sub-Schema

When `status` is `Escalate`, the worker may include an `escalation` object within `metadata`:

```json
{
  "status": "Escalate",
  "summary": "Plan was subsumed by another ticket",
  "metadata": {
    "escalation": {
      "reason": "subsumed",
      "subsumed_by": {
        "commit": "abc123def456",
        "files": ["docs/plan.md", "src/implementation.cs"],
        "rationale": "A parallel ticket (TLB-999) completed the same work"
      }
    }
  }
}
```

The `escalation` object has the following structure:
- `reason` (string): Escalation reason (e.g., "subsumed", "obsolete", "blocked")
- `subsumed_by` (object, optional): Details when reason is "subsumed" or "obsolete"
  - `commit` (string): Git commit SHA that supersedes this work
  - `files` (array of strings): Files affected by the superseding commit
  - `rationale` (string): Human-readable explanation of why this work was superseded

## Fenced-Block Protocol Extension

The fenced-block protocol allows workers to deliver named blocks of structured content (such as detailed plans, implementation summaries, or review critiques) alongside the WORKER_RESULT envelope. These blocks are referenced via the metadata `_ref` convention.

### Marker Syntax

- **Open**: `<<<NAME_START` on its own line opens a block named NAME
- **Close**: `<<<NAME_END` closes the block with the same NAME
- **Name format**: NAME must match the pattern `^[A-Z][A-Z0-9_]*$` (starts with a letter, followed by zero or more uppercase letters, digits, or underscores)

### Collision Avoidance Rationale

The `<<<` prefix is safe and unambiguous:
- **Markdown**: Triple-backtick fences use `` ` `` characters, not `<`; ATX headers use `#`; blockquotes use `>`; list markers use `-`, `*`, or digits
- **HTML**: Tags use `<letter` (open) or `</` (close), never `<<<`
- **No conflicts**: The `<<<` sequence does not appear in standard markdown syntax or HTML, making it impossible to accidentally trigger fenced-block parsing

### Block Placement Rule

Fenced blocks must appear BEFORE the WORKER_RESULT marker line. The structure is:

```
<<<BLOCK_NAME_START
block content here
<<<BLOCK_NAME_END

<<<ANOTHER_BLOCK_START
more content
<<<ANOTHER_BLOCK_END

WORKER_RESULT
{
  "status": "Ok",
  "summary": "...",
  "metadata": {
    "block_name_ref": "BLOCK_NAME",
    "another_block_ref": "ANOTHER_BLOCK"
  }
}
```

**Critical rule**: No fenced blocks may appear after the WORKER_RESULT marker. The envelope must always be last.

### Block Content

Everything between the `<<<NAME_START` and `<<<NAME_END` lines (not including the fence lines themselves) is preserved verbatim, including:
- Whitespace and indentation
- Newlines and blank lines
- Special characters
- Markdown or code formatting

### JSON Reference Convention (_ref)

To reference a fenced block from within the WORKER_RESULT envelope, use a metadata field named `<field>_ref` with a string value equal to the block NAME:

```json
{
  "status": "Ok",
  "summary": "Plan created",
  "metadata": {
    "plan_body_ref": "PLAN_BODY"
  }
}
```

The parser:
1. Reads the `plan_body_ref` field and extracts the value `"PLAN_BODY"`
2. Looks for a block named `PLAN_BODY` in the output
3. Resolves the reference to the block's content
4. Fails with a clear error if the named block is absent (see Failure Modes below)

This convention allows the envelope to stay compact while delivering large, structured content blocks separately.

### Block Name Registry

The following block names are reserved for specific phases:

#### Plan Phase
- `PLAN_BODY`: The detailed project plan (scaffolded from tickets, diagrams, implementation notes)

#### Implement Phase
- `IMPLEMENT_SUMMARY`: Summary of implementation work, code changes, testing, or rework needed

#### Review Phase
- `REVIEW_CRITIQUE`: Human reviewer feedback, acceptance criteria verification, or requested changes

#### Decompose Phase
- **No fenced blocks**: The decompose phase emits structured JSON (see below)

#### Future Phases
Additional block names may be added as the protocol evolves. Block names are namespaced by phase to avoid collisions.

### Decompose Phase: child_specs JSON Format

The decompose phase produces a `child_specs` array in the metadata, where each child ticket is represented as a structured JSON object:

```json
{
  "child_specs": [
    {
      "title": "short ticket headline",
      "description": "2-4 prose sentences describing the work",
      "acceptance_criteria": "prose acceptance statements",
      "size": "S",
      "scope_boundary": "one sentence boundary"
    }
  ]
}
```

**Field constraints (by design):**
- `title`: 3-8 words, under 60 chars, no code snippets
- `description`: 2-4 prose sentences, approximately 350 chars max, no shell commands or backticks
- `acceptance_criteria`: prose statements, approximately 400 chars max, no embedded code blocks
- `size`: single enum value ("S", "M", or "L")
- `scope_boundary`: one sentence, approximately 120 chars max

**Decision: No fenced-block migration (keep structured JSON)**

The decompose phase keeps child_specs as a JSON array rather than migrating to fenced blocks. Rationale:

1. **Low JSON-escape risk**: The fenced-block migration in plan/implement/review/draft was driven by large content with unescaped double-quotes from shell snippets or code blocks. Decompose's per-child fields are bounded by design and contain only prose - the risk of JSON-escape failure is minimal.

2. **Structural argument**: DecomposePhase needs typed per-child field access (iterating child_specs to create individual Plane tickets in a loop). Migrating to fenced blocks would require either N separate blocks per child or a re-encoded format inside one block, adding implementation complexity with no reliability benefit.

**Future phase guidance**: Apply this decision logic when adding new phases - migrate to fenced blocks only when a field carries multi-paragraph narratives, embedded diffs, shell command listings, or other content with unescaped quotes and backticks. Short prose fields (sentences, not paragraphs) and enums stay in JSON.

### Failure Modes

The parser validates fenced blocks and reports clear, actionable errors:

| Failure Mode | Condition | Parser Response |
|---|---|---|
| **Unclosed fence** | `<<<NAME_START` found with no matching `<<<NAME_END` before EOF or before WORKER_RESULT marker | `Error: unclosed fenced block: NAME` |
| **Mismatched fence names** | `<<<FOO_START` opened but `<<<BAR_END` found | `Error: mismatched fence: expected <<<FOO_END, found <<<BAR_END` |
| **Duplicate block names** | Two or more blocks with the same NAME in one output | `Error: duplicate block name: NAME` |
| **Missing referenced block** | Metadata has `plan_body_ref: "PLAN_BODY"` but no PLAN_BODY block exists | `Error: referenced block not found: PLAN_BODY` |
| **Invalid block name** | NAME does not match `^[A-Z][A-Z0-9_]*$` (e.g., lowercase, starting with digit, or special chars) | `Error: invalid block name: NAME` |

All error messages are deterministic and parseable for automated handling by orchestrators.

## Example: Complete Worker Output with Fenced Block

The following example demonstrates a complete worker output with a PLAN_BODY block referenced in metadata:

```
<<<PLAN_BODY_START
# Implementation Plan for TLB-500

## Overview
This ticket requires implementation of feature X, which involves:
- Backend API changes in service Y
- Frontend UI updates in component Z
- Database schema migration

## Phase 1: Prepare
- [ ] Create database migration
- [ ] Write API contract tests

## Phase 2: Implement
- [ ] Implement backend endpoints
- [ ] Build frontend components
- [ ] Integration testing

## Phase 3: Review
- [ ] Code review
- [ ] E2E testing
- [ ] Deploy to staging

## Acceptance Criteria
All criteria from the ticket must pass before ship.
<<<PLAN_BODY_END

WORKER_RESULT
```json
{
  "status": "Ok",
  "summary": "Plan scaffolded from ticket hierarchy with 3 phases and 8 work items",
  "files_changed": ["tickets/TLB-500.yaml"],
  "failure_reason": null,
  "metadata": {
    "phase": "plan",
    "plan_body_ref": "PLAN_BODY"
  }
}
```

In this example:
1. The `PLAN_BODY` block contains the complete project plan as formatted markdown
2. The WORKER_RESULT envelope references the plan via `plan_body_ref: "PLAN_BODY"`
3. The parser resolves the reference, confirming the block exists and extracting its content
4. The envelope itself remains concise while delivering a large, structured plan

## Integration with WORKER_RESULT Parser

The WORKER_RESULT parser (WorkerResultParser.cs) handles both the envelope and fenced-block protocol:

1. **Phase 1**: Parse fenced blocks and build a map of NAME -> content
2. **Phase 2**: Find and parse the last WORKER_RESULT marker and JSON payload
3. **Phase 3**: Validate all `*_ref` fields in metadata point to existing blocks
4. **Phase 4**: Return the parsed envelope with a resolved blocks map available to the orchestrator

This design decouples block delivery from envelope parsing, allowing workers to output diagnostic information, intermediate status, or structured content without complicating the core envelope contract.
