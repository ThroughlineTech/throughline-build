# Prompt: Compare build-engine runs across 2+ repos into a unified report

Hand this file to an agent verbatim. It compares the build-engine performance of two or more
repos (optionally specific runs within each) and produces a single unified report. It reuses the
rigid metrics schema from `build-run-analysis-prompt.md` so the columns line up, and adds a
synthesis that controls for confounds.

Use this after each repo has a `docs/build-run-analysis.md` (produced by
`build-run-analysis-prompt.md`), or let this prompt generate the per-repo analysis on the fly.

---

## Task

Compare the named repos (and the specified runs within them) and write one unified comparison
report. Default output: `docs/analysis/cross-repo-comparison-YYYY-MM-DD.md` in THIS repo
(Throughline Build), unless the user specifies another path.

## Inputs

- Two or more target repos (the user names them). For each, optionally a specific run/event-log
  or "latest".
- Per-repo source of metrics, in priority order:
  1. An existing `<repo>/docs/build-run-analysis.md` with the run(s) of interest already logged.
  2. If absent or stale, generate the analysis first by following
     `build-run-analysis-prompt.md` against that repo, then use its output.
- Stack-agnostic throughout: assume nothing about language, framework, OS, or architecture.

## Method

1. **Gather** each repo/run's metrics into the shared schema (see `build-run-analysis-prompt.md`
   for the rigid per-run schema and the embedded event-log parsing rules). Re-verify
   independently where feasible (run each repo's configured checks); never fabricate a cost for a
   vendor that does not emit `cost_usd`.
2. **Normalize for comparability.** Confirm what is actually the same vs different across the
   columns: op-doc/scope, ticket set, worker/agent, model(s), vendor. A comparison is only valid
   on the axes that are held constant. **List every confound explicitly** - a differing model,
   vendor, scope, or op-doc that makes a naive head-to-head misleading. (Examples seen before: a
   config difference that turned out to be a no-op because routing was identical; a vendor switch
   mid-experiment; one repo running half the op-doc.)
3. **Compare** on each metric, and on quality. Where a metric is not comparable (e.g. cost
   logged for one vendor but not another), say so rather than forcing a number.
4. **Synthesize** a verdict per dimension (meeting tickets, output quality, efficiency), naming
   the winner only where the comparison is valid, and saying "not comparable" where it is not.

## Output schema - RIGID tables, transposed so each repo/run is a column.

```
# Cross-repo build comparison - YYYY-MM-DD

## Subjects
| | Repo A (run) | Repo B (run) | ... |
|---|---|---|---|
| Repo / run | | | |
| Worker / agent | | | |
| Models | | | |
| Vendor(s) | | | |
| Op-doc / scope | | | |
| Tickets in scope | | | |
| Held constant? | <what is the same across columns> |
| Confounds | <what differs and why it limits the comparison> |

## Quantitative comparison
| Metric | Repo A | Repo B | ... | Notes |
|---|---|---|---|---|
| Tickets completed | | | | |
| Completion rate | | | | |
| Total rework rounds | | | | |
| Verdicts (Pass/Rework/Fail) | | | | |
| Build gate (passed/total) | | | | |
| Independent re-verify: build | | | | |
| Independent re-verify: tests | | | | |
| Total wall clock | | | | |
| Total output tokens | | | | |
| Total input tokens (cached) | | | | |
| Total cost (USD) | | | | mark "not logged" where applicable |
| Avg cost / ticket | | | | |
| Avg output tokens / ticket | | | | |
| Source LOC | | | | |
| Test count | | | | |

## Per-dimension verdict
| Dimension | Winner | Basis | Comparable? |
|---|---|---|---|
| Meeting tickets | | | yes/no |
| Output quality | | | yes/no |
| Efficiency (time) | | | yes/no |
| Efficiency (cost) | | | yes/no |

## Qualitative synthesis
Freeform prose: where the agents/models/runs genuinely differed in quality (with file:line
evidence), which differences are signal vs run-to-run variance, what the confounds prevent you
from concluding, and concrete recommendations - for the build engine, the prompts, the briefs,
or the next experiment design needed to get a clean comparison. This is the only freeform
section; keep the tables rigid.

## Bottom line
A few sentences: the defensible takeaways, the explicitly-not-comparable parts, and the single
highest-value next action.
```

## Guardrails

- Only declare a winner on axes that are actually held constant; otherwise label "not comparable"
  and explain the confound. An invalid comparison stated confidently is worse than none.
- Never fabricate cost for a vendor that does not emit `cost_usd`; compare on tokens/time there.
- Re-verify independently where feasible rather than trusting each chain's self-report.
- Stack-agnostic: no language/framework/OS/architecture assumptions; run each repo's own checks.
- ASCII only: plain hyphens, straight quotes, no em/en dashes, no curly quotes.
- Keep the metric rows identical to `build-run-analysis-prompt.md` so this report and the
  per-repo reports stay in lockstep.
