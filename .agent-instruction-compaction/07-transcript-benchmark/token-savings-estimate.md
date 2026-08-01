# Token Savings Estimate

These are estimates, not model-provider billing records. The conversion used is
1 word ~= 1.3 tokens, which is a conservative planning heuristic for this mostly
English/Markdown instruction text.

## Completed Compaction

| Source | Words Saved | Estimated Tokens Saved |
| --- | ---: | ---: |
| Full scoped corpus | 860 | about 1120 |
| Root `AGENTS.md` | 429 | about 560 |
| Nested repo `AGENTS.md` aggregate | 460 | about 600 |
| `run-backlog/SKILL.md` only | 259 | about 335 |

The full corpus savings already includes the new `serial-loop.md` reference.
For serial runs, `SKILL.md` plus `serial-loop.md` is slightly larger than the old
single `SKILL.md`, but the router is clearer and other modes avoid serial detail
until needed.

## Per Worker Context

Workers in the transcript are instructed to read repository instructions. The
most defensible per-worker savings is therefore the root `AGENTS.md` reduction.

| Assumption | Words Saved Per Worker | Tokens Saved Per Worker |
| --- | ---: | ---: |
| Worker reads root `AGENTS.md` only | 429 | about 560 |
| Worker reads root plus one average compacted nested file | about 467 | about 610 |

## 11-Agent Run Estimate

The BKFK2-401 through BKFK2-404 run created 11 worker agents.

| Scenario | Estimated Tokens Saved |
| --- | ---: |
| 11 workers read root only | about 6160 |
| 11 workers read root plus one relevant nested file | about 6710 |
| Add one conductor load of full compacted corpus delta | about 7280 to 7830 |

Recommended statement: the completed compaction probably saves about 6200 to
7800 tokens on an 11-agent serial run shaped like BKFK2-401 through BKFK2-404,
depending on how many nested instructions each worker reads.

## Additional Savings Not Yet Realized

The transcript shows larger future savings from tooling, not instruction edits:

- worker brief generation could replace repeated pasted role contracts and
  ticket/surface/gate blocks;
- candidate fingerprint commands could replace repeated shell snippets;
- structured evidence commands could replace repeated ledger formatting and
  grep readback blocks.

Those are Step 8 recommendations. They should be measured separately so the
instruction compaction is not credited for behavior that still needs tooling.
