# Repeated Blocks And Motifs

Source transcript: `backlog-transcript.txt`

Counts are exact phrase counts unless marked as semantic. Some raw counts are
inflated by transcript echo/readback blocks; notes identify those cases.

## Worker Instruction Motifs

| Motif | Count | Notes |
| --- | ---: | --- |
| Created worker agents | 11 | 4 implementers, 1 reviewer, 6 strong reviewers |
| `Ticket body:` blocks | 9 | Raw transcript count; first worker prompt is visibly duplicated by UI echo |
| `Acceptance criteria:` blocks | 9 | Same caveat as ticket body |
| `Rules:` markers | 12 | Worker prompt and review/rework blocks |
| `Repository review invariants` | 5 | Reviewer prompts only |
| `Declared surface fence` | 5 | Mostly implementation prompts |
| `Candidate fingerprint before review` | 4 | First review prompt per ticket |
| `Do not mutate tickets` exact phrase | 5 | Under-counts semantic variants in reviewer prompts |
| `Do not commit` exact phrase | 8 | Includes implementer and rework prompts |
| `push` | 16 | Safety-ban motif, mixed wording |
| `deploy` | 34 | Safety-ban motif, mixed wording and ticket-specific boundaries |
| `tear down worktrees` | 8 | Safety-ban motif |
| `Preserve unrelated changes` | 5 | Mostly implementer prompts |
| `Work only in the supplied workspace` | 5 | Mostly implementer/rework prompts |
| `Make no edits` | 7 | Reviewer prompts |
| `Return exactly VERDICT: PASS...` | 7 | Reviewer prompts |
| `Exact gate command to run` | 5 | Implementer-oriented gate prompt |
| `Exact gate command to rerun` | 7 | Reviewer-oriented gate prompt |
| `Run the exact gate command` | 2 | Additional exact phrase; semantic motif appears more often |

Interpretation: the worker prompts repeat the same four clusters: scope, mutation
bans, exact gate, and report format. The completed compaction reduces the repo
instruction load workers read, but the repeated prompt clusters remain a better
target for `build worker brief`.

## Repeated Conductor Shell Snippets

| Snippet / Motif | Count | Notes |
| --- | ---: | --- |
| `candidateFingerprint` | 8 | Ledger evidence and readback mentions |
| `git ls-files --others --exclude-standard` | 25 | Raw count across command blocks and echoed prompts |
| `git diff --stat` | 3 | Scope/stat checks |
| `git diff --name-only` | 7 | Scope/fingerprint checks |
| `git diff --cached` | 11 | Cached diff fingerprint checks |
| `rev-parse HEAD` | 27 | Branch, lease, candidate, and run-head checks |

Repeated fingerprint ceremony has a stable shape:

```text
base/head SHA
touched file list
tracked diff hash
cached diff hash
untracked file-list hash
candidate SHA
run-head/integration SHA
```

That shape is deterministic enough to become `build candidate status` or
`build fingerprint`.

## Repeated Ticket Ledger Comment Shapes

Actual `build comment` command shapes:

| Ledger kind | Commands | Readback grep echoes | Notes |
| --- | ---: | ---: | --- |
| claim | 4 | 0 | Once per ticket |
| review | 4 | 4 | Once per ticket, then read back |
| commit | 4 | 0 | Once per ticket |
| integrate | 4 | 4 | Once per ticket, then read back |
| gate | 4 | 4 | Once per ticket, then read back |
| final | 4 | 4 | Once per ticket, then read back |

The ledger is valuable, but the transcript shape is repetitive:

```text
run-backlog <kind> <ticket> [transaction-id]: <sha/fingerprint/gate/evidence>
build comments <ticket> --json | grep -F "run-backlog <kind> <ticket>"
```

That is a good candidate for `build evidence add` because the behavior should
stay explicit and read-backable while the comment format becomes deterministic.
