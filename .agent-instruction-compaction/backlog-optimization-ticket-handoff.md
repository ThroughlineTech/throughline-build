# Backlog Optimization Ticket Handoff

Companion report: `backlog-run-analysis-report.md`

## Status

The follow-up tickets have been filed through `build` in this repository. The
initiative is tracked by parent ticket `TLB-599` with eight child tickets:

| Ticket | Title | Role |
| --- | --- | --- |
| `TLB-599` | Backlog run efficiency without safety regression | Parent initiative |
| `TLB-600` | Add Build candidate status fingerprint command | Build primitive |
| `TLB-601` | Adopt candidate status in run-backlog workflow | Harness/instructions adoption |
| `TLB-602` | Add Build worker brief artifact command | Build primitive |
| `TLB-603` | Adopt worker briefs in run-backlog handoffs | Harness/instructions adoption |
| `TLB-604` | Add structured Build evidence comment command | Build primitive |
| `TLB-605` | Adopt structured evidence in run-backlog workflow | Harness/instructions adoption |
| `TLB-606` | Investigate risk-tiered gate policy for backlog runs | Investigation |
| `TLB-607` | Benchmark backlog run efficiency after helper adoption | Benchmark/final analysis |

Confirmed dependency edges:

- `TLB-601` is blocked by `TLB-600`.
- `TLB-603` is blocked by `TLB-602`.
- `TLB-605` is blocked by `TLB-604`.
- `TLB-607` is blocked by `TLB-601`, `TLB-603`, `TLB-605`, and `TLB-606`.

## Objective

Reduce backlog-run context load, transcript verbosity, and conductor shell ceremony
without weakening the safety properties observed in `backlog-transcript.txt`.

Preserve these behaviors:

- baseline gate before claim;
- ticket read/comment/transition through `build` only;
- leased worktree isolation;
- implementer/reviewer separation;
- fresh reviewer after rework;
- strong review for contract, migration, persistence, auth, publication, and
  irreversible behavior;
- candidate fingerprint before review and after review;
- commit only after review passes and fingerprint holds;
- ancestry-preserving rebase/fast-forward integration;
- merged-tree gate before Done;
- explicit ticket transitions and readback;
- safe Build worktree teardown;
- no push, deploy, remote mutation, or migration application without explicit
  authorization.

## Recommended Execution Order

Use this order unless dependency-safe wave planning proves a different order is safe:

1. `TLB-600` - candidate status/fingerprint command.
2. `TLB-601` - adopt candidate status in run-backlog.
3. `TLB-602` - worker brief artifact command.
4. `TLB-603` - adopt worker briefs in run-backlog handoffs.
5. `TLB-604` - structured evidence comment command.
6. `TLB-605` - adopt structured evidence in run-backlog.
7. `TLB-606` - investigate gate policy.
8. `TLB-607` - benchmark the improved workflow.

The three Build primitive tickets may look independent, but they are likely to touch
shared CLI/help/test surfaces. Treat them as serial unless `build waves` says
otherwise.

## Ticket Intent

### TLB-600 - Candidate Status/Fingerprint

Add a deterministic conductor-side command that replaces repeated shell snippets for
candidate tree status.

Likely command shape:

```text
build candidate status --ticket <ID> --base <ref> --json
```

Equivalent naming such as `build fingerprint` is acceptable if the help text is clear.

Required output:

- base SHA;
- current HEAD;
- tracked diff hash;
- cached/index diff hash;
- untracked file hash;
- touched paths;
- lease manifest presence/metadata;
- dirty state;
- nonzero failures for unsafe or unreadable states.

Important boundary: no ticket mutation, commit, branch operation, push, deploy,
integration, or worktree lifecycle mutation.

### TLB-601 - Adopt Candidate Status

Update `run-backlog` conductor guidance so pre-review and post-review checks use the
new Build command. Manual shell snippets should become fallback guidance only.

Behavior must remain the same:

- fingerprint before review;
- fingerprint after review;
- stop on drift;
- preserve untracked-file visibility;
- commit only reviewed paths.

### TLB-602 - Worker Brief Artifact

Add a deterministic command that writes a compact, role-specific Markdown brief for
workers. This is meant to replace large repeated prompt blocks with an inspectable
file plus a short worker instruction.

Likely command shape:

```text
build worker brief --ticket <ID> --role implement|review|rework --worktree <path> --output <path> --json
```

The command should not spawn a worker. It should only generate an artifact and JSON
metadata.

The brief should include:

- ticket body and acceptance criteria;
- role boundary;
- workspace/worktree path;
- exact gate command;
- declared surface/fence when provided;
- repository invariants;
- actual diff/status instructions for reviewers;
- prior blocking findings for rework.

### TLB-603 - Adopt Worker Briefs

Update run-backlog worker handoff text to use generated briefs. Worker prompts should
be shorter but still include the non-negotiable boundary:

- work in the supplied path;
- read the generated brief;
- obey the role contract;
- do not mutate tickets, git history, branches, pushes, deploys, integration, or
  worktree lifecycle;
- run the exact gate;
- return the expected structured report.

Strong review remains required for high-risk work.

### TLB-604 - Structured Evidence Comments

Add a Build command for consistent ticket discussion ledger entries.

Likely command shape:

```text
build evidence add --ticket <ID> --kind claim|review|commit|integrate|gate|final --json
```

The command should format and post exactly one comment per invocation, then report
the created/read-back evidence. Lifecycle transitions must remain separate commands.

Kind-specific validation should reject missing required data, such as:

- candidate SHA;
- run head SHA;
- reviewer verdict;
- gate result;
- fingerprint;
- cleanup/lease state where relevant.

### TLB-605 - Adopt Structured Evidence

Update run-backlog finalization guidance to use structured evidence comments when
available.

Preserve separate events:

- claim;
- review;
- commit;
- integrate;
- gate;
- final;
- ticket transition/readback.

Do not introduce cascading close, implicit transitions, or direct Plane/API access.

### TLB-606 - Gate Policy Investigation

Investigate repeated gates before changing anything. The transcript shows gate
repetition is expensive, but strong review and repeated gates caught real risk in
contract and migration/publication tickets.

The output should classify:

- ticket types that must keep the strong gate path;
- ticket types, if any, that can use lighter gate profiles;
- failure modes still caught by any proposed profile;
- config/help/instruction changes needed for explicit auditability.

No code in this ticket should reduce gate execution.

### TLB-607 - Benchmark After Adoption

Run the after-action measurement. Compare the improved workflow against
`backlog-transcript.txt` or an equivalent multi-ticket transcript.

Measure:

- repeated worker-instruction motifs;
- manual fingerprint snippets replaced;
- evidence comment shape/line count;
- estimated token savings for an 11-worker run;
- remaining intentional duplication;
- preservation of strong review, gates, lease isolation, readbacks, and no-push/no-deploy
  boundaries.

Produce a Markdown report suitable for sharing.

## Implementation Notes

- Code changes in Build primitive tickets require `dotnet build throughline-build.sln
  --nologo -v q` and `dotnet test --nologo -v q --logger
  "console;verbosity=minimal"` to pass.
- Any new JSON shape must use source-generated `JsonSerializerContext`; do not rely on
  reflection serialization.
- Update current help/docs when adding a command or changing documented CLI behavior.
- Keep `ThroughlineBuild.Contracts` I/O-free.
- Do not use worker-spawning verbs from an agent session.
- Do not use direct Plane access. All ticket operations go through `build`.

## Recommendation For Staffing

Filing the tickets benefited from the current transcript context, so filing them now
was the right call. Implementation should be done ticket-by-ticket, preferably by a
fresh agent per ticket or by the `run-backlog` workflow itself, because each child
needs a clean read of the ticket body, comments, current code, and current docs.

The first implementation ticket to pick up is `TLB-600`.
