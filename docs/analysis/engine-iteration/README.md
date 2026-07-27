# engine-iteration

The feedback -> plan -> implementation loop that changed the engine between experiments.
**Provenance, not evidence** - nothing in here is cited as a result. The results live in
[../findings/](../findings/) and the report.

Files are `NN-<stage>.md`, where `NN` is the experiment number. The three stages are:

| Stage | File | What it is |
|---|---|---|
| Feedback | `NN-feedback-from-<source>.md` | The defect(s) to fix, with acceptance criteria |
| Plan | `NN-plan.md` | File-cited implementation spec written against the C# source |
| Summary | `NN-implementation-summary.md` | What actually shipped: branch, commits, tests |

The protocol that defines this loop is [../method/experiment-harness-prompt.md](../method/experiment-harness-prompt.md).

## Contents

| Experiment | Feedback | Plan | Summary | Result |
|---|---|---|---|---|
| 01 - gate-output vacuity + worktree cleanup | [01](01-feedback-from-smoketest-8.md) | [01](01-plan.md) | [01](01-implementation-summary.md) | Implemented |
| 02 - context pre-loading | [02](02-feedback-from-experiment-1.md) | [02](02-plan.md) | [02](02-implementation-summary.md) | Implemented, silently no-opped in the live run |
| 03 - make pre-loading actually fire | [03](03-feedback-from-experiment-2.md) | *never written* | *never written* | Implemented + ran; lever fired |
| 04 - context-attribution telemetry | [04](04-feedback-from-experiment-3.md) | [04](04-plan.md) | [04](04-implementation-summary.md) | Planned |

## Why experiment 03 has no plan or summary

It is a real gap, not a lost file. Experiment 3 was investigated (at `40738d2`) and implemented
and run, but the plan and implementation-summary files were left as empty placeholders and were
never written; they have since been removed. What experiment 3 did and what it produced is
recorded in [../findings/experiment-program-ledger.md](../findings/experiment-program-ledger.md)
and [../findings/experiment-3-analysis.md](../findings/experiment-3-analysis.md).

Experiment 5 was planned but never ran. Its op-doc was byte-identical to experiment 4's, so a
single copy is kept at [../workloads/survey-experiment-3-and-4.md](../workloads/survey-experiment-3-and-4.md).
