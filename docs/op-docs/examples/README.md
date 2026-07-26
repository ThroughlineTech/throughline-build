# Op-doc examples

Real op-docs from this repository's own development, kept as format specimens.
The authoring rules and a synthetic canonical example live in
[op-doc-spec.md](../op-doc-spec.md) (also emittable from the binary via
`build op-doc spec`); these are what the format looks like applied to actual
work.

**These are point-in-time plans, not documentation of current behavior.** They
were written before the work they describe, and the code has moved since. Where
one of them disagrees with the source, the source wins. For current behavior see
[docs/state-of-the-system/00-index.md](../../state-of-the-system/00-index.md)
and the [user guide](../../throughline_build_userguide.md).

Picked to cover the range of shapes rather than for historical significance:

| File | Shape |
| --- | --- |
| [op-26-build-init.md](op-26-build-init.md) | Smallest complete op-doc (79 lines). One plan, one small deliverable, S effort. |
| [op-24-auto-resolve-ship.md](op-24-auto-resolve-ship.md) | Single plan, sequential briefs, tightly scoped behavior change to an existing phase. |
| [op-31-batch-implement.md](op-31-batch-implement.md) | Three plans with a real A -> B -> C dependency chain and an L-effort core. |
| [op-30-deterministic-chain-gate.md](op-30-deterministic-chain-gate.md) | Design-heavy. Shows a long "Why this exists" carrying the argument, plus a "Deliberately not in this operation" section that fences off the seam the work does *not* close. |
| [op-27-worker-result-fenced-payloads.md](op-27-worker-result-fenced-payloads.md) | A contract migration staged as a proving vertical (one phase first, then the rest). Also the live spec for the `<<<NAME_START` / `<<<NAME_END` payload protocol, referenced from [07-contracts.md](../../state-of-the-system/07-contracts.md). |

The other thirty-odd completed op-docs were removed rather than archived here.
The decisions in them that are not visible in the code were extracted into
[docs/history.md](../../history.md) instead.
