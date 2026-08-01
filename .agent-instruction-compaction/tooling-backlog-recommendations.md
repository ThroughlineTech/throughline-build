# Tooling Backlog Recommendations

These are recommendations only. No tickets were created or mutated.

## 1. Add `build candidate status --json`

Problem: the transcript repeats hand-written shell blocks to prove candidate
state before review and commit.

Acceptance criteria:

- Reports base SHA, HEAD SHA, tracked diff hash, cached diff hash, untracked
  file-list/hash, touched paths, and lease manifest status.
- Accepts `--base <ref>` and defaults to the current worktree.
- Emits the standard JSON envelope and nonzero exit on unsafe state.
- Has tests for dirty tracked files, staged files, untracked files, missing lease
  manifest, and clean no-change state.

## 2. Add structured evidence commands

Problem: ticket comments are valuable but verbose and manually shaped.

Acceptance criteria:

- `build evidence add --ticket <ID> --kind <claim|review|commit|integrate|gate|final> --json`
  formats a compact, consistent ledger entry.
- Evidence entries include optional SHA, fingerprint, gate summary, reviewer
  verdict, rework count, and cleanup status.
- Each mutation is still explicit, inspectable, and followed by readback.
- The command does not cascade-close or combine lifecycle transitions.

## 3. Generate compact worker briefs

Problem: every worker prompt repeats ticket body, acceptance criteria, surface
fence, exact gate, and role safety contract.

Acceptance criteria:

- `build worker brief --ticket <ID> --role <implementer|reviewer> --worktree <path> --out <path>`
  writes a local markdown brief.
- Briefs include ticket body/comments, acceptance criteria, declared surface,
  exact gate command, role contract, and repository invariants.
- Conductor prompts can reference the brief path plus a short non-mutation
  reminder.
- Generated briefs are never committed by default and are easy to archive for
  audit.

## 4. Add run transcript metrics

Problem: measuring backlog runs currently requires manual transcript reading.

Acceptance criteria:

- A deterministic parser reports wall time, visible command time, gate counts,
  lease counts, comments/transitions/readbacks, worker create/close counts,
  review verdicts, rework rounds, and cleanup status.
- Supports text and JSON output.
- Handles duplicate UI transcript blocks without double-counting agents.

## 5. Add risk-tiered gate profiles without weakening default safety

Problem: every ticket currently pays for the strongest gate pattern, even when
the ticket is low risk.

Acceptance criteria:

- Config can declare gate profiles such as `standard`, `contract`, `migration`,
  and `visual`.
- Default remains conservative.
- Contract, ingest, migration, publication, and security-sensitive tickets still
  require strong review and merged-tree gates.
- Evidence records which profile ran and why.

## 6. Version the worker contract

Problem: safety rules are repeated in prompts because the conductor needs workers
to receive the contract explicitly.

Acceptance criteria:

- `agent-contracts.md` exposes a short version/hash.
- Worker briefs include the version/hash and the relevant role contract.
- Conductor evidence records which contract version was used.
- Contract updates remain behavior-preserving unless explicitly marked breaking.

## Priority Order

1. `build candidate status --json`
2. Compact worker brief generation
3. Structured evidence commands
4. Transcript metrics parser
5. Worker contract version/hash
6. Risk-tiered gate profiles

The first two should produce the largest immediate transcript and operator-load
reduction while preserving the safety behavior that made the benchmark run
successful.
