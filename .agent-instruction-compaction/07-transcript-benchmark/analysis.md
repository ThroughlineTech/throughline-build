# Step 7 - Transcript Benchmark Audit

Source transcript: `backlog-transcript.txt`

Purpose: connect the instruction compaction to a real backlog-run shape, not
only static word counts.

## Transcript Shape

| Measure | Value |
| --- | ---: |
| Lines | 1903 |
| Words | 11310 |
| Characters | 98759 |
| Wall time reported | 1h 55m 59s |
| Tickets | 4 |
| Worker agents created | 11 |
| Implementers | 4 |
| Reviewers | 1 |
| Strong reviewers | 6 |

The run completed BKFK2-401 through BKFK2-404 serially. That matters for the
benchmark because the same repository rules and run-backlog worker contracts
were loaded or restated repeatedly across many small contexts.

## Static Compaction Result

| Corpus | Before | After | Delta |
| --- | ---: | ---: | ---: |
| Scoped instruction and skill files | 6513 words | 5653 words | -860 words |
| Root `AGENTS.md` | 1368 words | 939 words | -429 words |
| Nested repo `AGENTS.md` files | 1960 words | 1500 words | -460 words |
| `run-backlog/SKILL.md` router | 1016 words | 757 words | -259 words |

The `run-backlog` router reduction is partly offset for serial runs by the new
lazy reference `serial-loop.md` at 327 words. That is intentional progressive
disclosure: the main skill is smaller and mode selection is clearer, while serial
procedure remains available when needed.

## Real-Run Benefit

The transcript tells workers to read repo instructions and repeats worker safety
motifs in every delegated context. Therefore the root `AGENTS.md` savings are
the most reliable per-worker savings from this compaction:

- root savings: 429 words, about 560 tokens using 1 word ~= 1.3 tokens;
- average nested-file savings if one relevant nested file is read: about 38
  words, about 50 tokens;
- conservative per-worker savings: about 560 tokens;
- likely per-worker savings when one nested file is read: about 610 tokens.

Across an 11-agent run shaped like BKFK2-401 through BKFK2-404:

- conservative savings: about 6160 tokens;
- likely savings with one nested file per worker: about 6710 tokens;
- plus one conductor/repo load of the full compacted corpus: about 1120 tokens.

Practical expected range for the same run shape: about 6200 to 7800 tokens saved
from the completed compaction alone.

## What Compaction Did Not Solve

The transcript still contains repeated workflow ceremony that belongs in code or
generated artifacts:

- repeated worker prompts with role bans, gate command, acceptance criteria, and
  ticket body;
- repeated fingerprint shell snippets;
- repeated ticket ledger comments and readbacks;
- repeated review verdict instructions.

Those repetitions are not instruction-tree redundancy. They are run-time
workflow ceremony. Step 8 separates them into Build helper recommendations.

## Intentional Duplicates

The following stayed duplicated intentionally:

- no direct Plane/REST/MCP or ticket slash-command path in root repo rules;
- no nested worker-spawning verbs from an agent session;
- conductor-only ownership of ticket mutations, commits, integration, branches,
  and worktree lifecycle;
- implementer/reviewer mutation bans in worker contracts;
- no concurrent writers in one working tree;
- no deploy/push/merge/ship without explicit authorization;
- AOT/source-generated serialization reminders in local high-risk subtrees;
- gate-vacuity warnings in root and verification-local guidance.

These duplicates are short safety rails. Removing them would save little and
increase the chance of a high-impact mistake.
