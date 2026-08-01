# Proposed Ticket Drafts

No tickets were created or mutated. These drafts are ready to paste into the
repository's normal ticket creation flow.

## Ticket: Add Build Candidate Status Command

### Summary

Add `build candidate status --json` to report candidate tree fingerprints and
lease metadata with one deterministic command.

### Problem

Backlog conductors currently repeat manual shell snippets for base SHA, HEAD,
tracked diff hash, cached diff hash, untracked hash, touched paths, and lease
metadata. The ceremony is noisy and easy to get subtly wrong.

### Acceptance Criteria

- Command reports base SHA, HEAD SHA, tracked diff hash, cached diff hash,
  untracked file-list hash, touched paths, and lease metadata.
- Command accepts `--base <ref>`, optional `--ticket <ID>`, and `--json`.
- Standard JSON envelope is used.
- Nonzero exit reports unsafe or ambiguous state.
- Tests cover clean tree, dirty tracked files, staged files, untracked files,
  missing lease metadata, and invalid base ref.
- Documentation/help is updated.

### Out Of Scope

- Ticket mutation.
- Commit creation.
- Worktree teardown.

## Ticket: Add Structured Evidence Comment Command

### Summary

Add `build evidence add` to format claim, review, commit, integrate, gate, and
final ledger comments consistently.

### Problem

The transcript repeats six freehand evidence comment shapes per ticket. They are
useful, but formatting is manual and verbose.

### Acceptance Criteria

- Supports `--kind claim|review|commit|integrate|gate|final`.
- Supports ticket ID, transaction ID, SHAs, fingerprint reference, gate summary,
  review verdict, rework count, and cleanup state.
- Emits standard JSON envelope and reads back the created evidence or ticket
  comment identifier.
- Does not transition lifecycle state.
- Does not cascade close or mutate related tickets.
- Help/docs and tests are updated.

### Out Of Scope

- Replacing `build comment`.
- Combining evidence with state transitions.

## Ticket: Generate Compact Worker Briefs

### Summary

Add `build worker brief` to generate role-specific markdown briefs for
implementers and reviewers.

### Problem

Worker prompts repeat ticket body, acceptance criteria, surface fence, exact
gate command, repository invariants, mutation bans, and response format.

### Acceptance Criteria

- Command accepts `--ticket <ID>`, `--role <implementer|reviewer|strong-reviewer>`,
  `--worktree <path>`, `--out <path>`, and `--json`.
- Brief includes ticket body/comments, acceptance criteria, declared surface,
  exact gate, role contract, mutation bans, and expected response format.
- Brief includes contract version/hash.
- Generated brief path is returned in the JSON envelope.
- Generated briefs are ignored/uncommitted by default.
- Tests cover each role and missing ticket/comment/gate data.

### Out Of Scope

- Spawning worker agents.
- Mutating ticket state.
- Committing generated briefs.

## Ticket: Summarize Backlog Run

### Summary

Add optional `build run summarize` to produce a final per-ticket run summary.

### Problem

Final handoff currently requires manual assembly of ticket states, commits,
gates, leases, and cleanup status.

### Acceptance Criteria

- Command accepts ticket IDs and optional integration branch.
- Reports per-ticket state, commit SHA, review verdict/rework count, gate
  summary, evidence status, lease teardown status, remaining leases, and working
  tree cleanliness.
- Supports text and JSON output.
- Does not mutate tickets, branches, or worktrees.
- Tests cover completed, blocked, missing-lease, dirty-tree, and mixed-state
  runs.

### Out Of Scope

- Creating tickets.
- Closing tickets.
- Pushing, merging, deploying, or tearing down worktrees.
