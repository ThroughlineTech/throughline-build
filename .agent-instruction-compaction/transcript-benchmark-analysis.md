# Transcript Benchmark Analysis

Source artifacts:

- `backlog-transcript.txt`
- `backlog-run-analysis-report.md`

## Baseline Run

The transcript captures a four-ticket serial backlog run over BKFK2-401 through
BKFK2-404. The existing analysis report records:

- wall time: 1h 55m 59s
- transcript size: 98759 characters, 1903 lines, 11310 words
- tickets completed: 4
- commits produced: 4
- integration branch: `bkfk2-401-404-run`
- worker agents: 11 created and 11 closed
- visible primary conductor gates: 8
- estimated total gates including workers/rework: about 22
- visible shell command time: 994 seconds, about 16.6 minutes

The run quality was high: every ticket had a baseline gate before claim, each
ticket used a Build lease, review was independent, rework stayed in the same
lease, fresh reviewers checked rework, commits happened only after passing
review, merged-tree gates passed, ticket readbacks occurred, and leases were
removed safely.

## Compaction Impact

The instruction compaction reduced the scoped instruction/skill corpus from
6513 to 5653 words, saving 860 words.

Important detail: the `run-backlog` skill router itself dropped from 1016 to 757
words, but serial procedure detail moved to `references/serial-loop.md` at 327
words. A serial conductor that reads both sees a small net increase for that one
topic, while non-serial/adaptation/fan-out flows benefit from loading less up
front. The bigger global savings came from root and nested repo `AGENTS.md`
compaction.

This means instruction compaction helps the standing context, but it does not by
itself solve the largest transcript overhead: worker prompts repeat ticket body,
surface fence, gate command, role contract, safety bans, and fingerprint data.

## Benchmark Findings

| Area | Observation | Benchmark Value |
| --- | --- | ---: |
| Scoped instruction corpus | Before vs after compaction | 6513 -> 5653 words |
| Net savings | Behavior-preserving compaction | 860 words |
| Skill router | `SKILL.md` only | 1016 -> 757 words |
| Serial procedure | New lazy reference | 327 words |
| Transcript size | Existing backlog run | 11310 words |
| Worker contexts | Existing report | 11 workers |
| Visible conductor gates | Existing report | 8 |
| Estimated total gates | Existing report inference | about 22 |
| Visible command time | Existing report | 16.6 minutes |

## Practical Interpretation

The compacted files should reduce routine repository instruction load and make
future agents less likely to reread repeated root-level law in nested files.

For `run-backlog`, the compaction mostly improves routing and progressive
disclosure. The serial path is now easier to reason about because serial,
fan-out, adaptation, and worker contracts each have a clear owner. Runtime and
transcript-size gains will be modest until repeated worker brief text and manual
fingerprint/status blocks are replaced with deterministic artifacts or commands.

## Benchmark Targets For Next Run

Use the next comparable four-ticket serial run to measure:

- transcript words per completed ticket;
- worker prompt words per implementer/reviewer;
- number of repeated safety-contract words per worker;
- visible command time by category;
- number of manual fingerprint/status shell blocks;
- number of ticket evidence comments;
- review findings caught before commit;
- rework rounds and whether fresh reviewers were used;
- final worktree and ticket cleanup status.

Suggested success targets:

- reduce repeated worker prompt text by at least 30 percent;
- replace manual fingerprint shell blocks with one deterministic command;
- preserve baseline gates, independent review, merged-tree gates, readbacks, and
  safe lease teardown;
- do not reduce strong review coverage for contract, ingest, migration, or
  publication tickets.
