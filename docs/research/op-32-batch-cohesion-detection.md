# Operation: batch-cohesion-detection

Replace op-31's manual batch declaration with an automatic cohesion detector so an operator no longer has to hand-pick which sibling tickets fuse into one batch-implement session. When a parent chain dispatches, a deterministic cohesion gate partitions the eligible children into solo tickets and candidate batch groups using signals already on the ticket and brief - size, risk, and file-set overlap - resolving the obvious cases for free; a cheap small-model classifier judges only the ambiguous middle band the metadata cannot settle. The output is an explicit, logged batch plan that feeds op-31's batch-implement engine. The plan is advisory and reversible: a failed batch falls back to per-ticket implementation, and the detector is biased toward solo.

## Why this exists

op-31 built the batch-implement execution engine - one warm worker session over a cohesive sibling group, one commit per ticket, one combined review - but deliberately left selection manual: an operator opts a group in with a `batch-implement` label or a `--batch-implement TLB-419,420,421` flag, and "automatic cohesion detection" is listed as out of scope for its first build. That manual step is the obvious next increment, and it does not scale: a human has to read four briefs and judge cohesion before every chain, which is exactly the kind of repeated judgment the workflow exists to mechanize. op-31's own warning - "do not infer batching solely from sibling status, a parent with many children is not automatically cohesive" - is the crux: sibling status is necessary but not sufficient, so the detector needs more than a graph edge.

The decision splits into two questions that a single fuzzy "should I batch?" conflates. CAN we batch (is it safe) is a blast-radius question - size, risk, count, file overlap - and is almost entirely answerable from data already on the `Ticket` record and the phase briefs, for zero tokens. SHOULD we batch (is it worth it, and is it one coherent design) has a semantic core that metadata cannot reach: three small tickets in three unrelated subsystems pass every numeric check yet share no design context, so fusing them gains only the cold-start saving while muddying one worker's focus. The deterministic gate disposes of CAN and the extreme SHOULD cases; the cheap classifier is spent only on the semantic middle.

The cost of the detector must stay below what it saves, and the structure enforces that. op-31 measured the prize: the TLB-419..422 chain logged about 7.9M cache-read tokens re-priming a stateless worker once per ticket, roughly 91% of cost in implement. One avoided cold start is tens of thousands of tokens on a capable model. The deterministic gate is free and disposes of the extremes, so the classifier - the only paid component - fires only on the residual ambiguous band, reads trimmed briefs (a few KB), and runs on the cheapest configured model. A single avoided cold start pays for many classifier calls. The math only inverts if the detector batches things that should have stayed solo and they de-batch after a failed review, which is exactly why the classifier is split-biased and the gate hard-vetoes the risky shapes up front.

The error is asymmetric and that dictates the bias. A wrong "solo" merely forgoes a saving op-31 would have captured - the chain runs as it does today. A wrong "batch" produces a tangled stack, coarse failure granularity, and a possible de-batch-and-retry that burns the expensive implement axis twice. So the detector defaults to solo on every uncertainty: a hard-veto signal, a low-confidence classifier verdict, or a missing file hint all resolve to solo. This operation is the selection layer only; it is distinct from op-30's deterministic quality gate (which falsifies an implementer's claim after the fact between implement and review) and it consumes - does not rebuild - op-31's batch-implement execution engine. It must therefore be sequenced after op-31 lands.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Deterministic cohesion gate | - | M |
| B | Cheap classifier for the ambiguous band | A | S |
| C | Wire the batch plan into the chain | B, op-31 | M |

A is standalone and produces the batch-plan artifact from metadata alone, so it is buildable and testable with no LLM and no chain wiring. B layers the paid classifier onto only the band A could not settle. C consumes the resulting plan and is the one plan that depends on op-31's batch-implement engine actually existing; it must not start until op-31 has landed.

## Plan A: Deterministic cohesion gate

### Goal

After this plan, a parent chain can compute a structured batch plan from ticket and brief metadata alone, with no LLM call: each eligible child is partitioned into a solo ticket, a member of an auto-approved batch group, or a member of an ambiguous group awaiting the Plan B classifier. The partition is a pure, tested function over extracted cohesion signals - size, risk, count, dependency shape, and file-set overlap - and it logs which band each group fell in. Nothing yet acts on the plan.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | batch-plan-contract-and-inventory | Define the BatchPlan/BatchGroup/CohesionSignals contracts and inventory where the partition runs and where file hints come from before implement | - | new: src/ThroughlineBuild.Contracts/Models/BatchPlan.cs, src/ThroughlineBuild.Contracts/Models/CohesionSignals.cs; docs: notes/cohesion-detection-inventory.md |
| 02 | cohesion-signal-extraction | Extract size, risk, count, dependency edges, and file-set overlap into CohesionSignals from the ticket graph and available briefs | 01 | new: src/ThroughlineBuild.Cohesion/CohesionSignalExtractor.cs + tests |
| 03 | deterministic-partition-rules | Pure partition: hard-veto to solo, auto-approve to batch, escalate the residue to ambiguous | 02 | new: src/ThroughlineBuild.Cohesion/CohesionGate.cs + tests |

### Briefs - detail

#### Brief 01: batch-plan-contract-and-inventory

Goal: Define the data the detector produces and the orchestrator consumes, and produce a reconciliation inventory that names the real call sites and - the load-bearing question - where file-overlap signal can actually be read before implement runs. The inventory is what stops Plan C from wiring against a signal that does not exist yet at partition time.

Inputs: The `Ticket` record at `src/ThroughlineBuild.Contracts/Models/Ticket.cs` (`Size`, `Risk`, `Relations`, `Labels`, `ParentId`); the `Brief` record at `src/ThroughlineBuild.Contracts/Models/Brief.cs` (`RelevantFiles`, `AllowedWrites`); `ChainPhase.RunParentChainAsync` and `BuildSiblingGraphAsync` in `src/ThroughlineBuild.Phases/ChainPhase.cs`; `TopologicalSorter.ComputeLevels` in `src/ThroughlineBuild.Phases/TicketGraph.cs`; the `[plan] mode` config that decides whether an investigation worker or in-place promotion produces the plan.

Outputs:
- `BatchPlan` and `BatchGroup` contracts: a `BatchPlan` is an ordered list of `BatchGroup`, each group carrying its member ticket ids, a `Disposition` (Solo, Batch, Ambiguous), and the band/reason it was assigned. A solo ticket is a group of one with Disposition Solo, so the chain dispatch loop can iterate groups uniformly.
- `CohesionSignals` contract: the per-group signal bundle (member sizes, max risk, member count, intra-group dependency edge set, file-overlap ratio, and a `FileHintsAvailable` flag).
- `notes/cohesion-detection-inventory.md` naming, with verified file:symbol citations read at HEAD, each of: the exact point in `RunParentChainAsync` after `BuildSiblingGraphAsync` where the partition slots before per-level dispatch; whether `Brief.RelevantFiles`/`AllowedWrites` exist for a child before its implement phase runs, or whether file hints must come from the plan/investigation output or ticket description, and the fallback when no hint exists; how op-31's batch-implement entry point is invoked (the signature Plan C will call); the run event-log emission point the Plan C ledger will reuse.

Acceptance:
- [ ] BatchPlan, BatchGroup, and CohesionSignals are expressible as plain data with no extraction or partition logic in the contracts
- [ ] A solo ticket and a batch group share one BatchGroup shape so the dispatch loop iterates groups uniformly
- [ ] The inventory cites verified file:symbol locations at HEAD for every surface above, not the state-of-system docs
- [ ] The inventory records whether file-overlap signal is available before implement, and the explicit fallback (treat as no-overlap, bias solo) when it is not
- [ ] The inventory records op-31's batch-implement entry-point signature that Plan C will call
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The contract is the easy half; the inventory is the load-bearing output. File-set overlap is the single strongest cohesion signal, but the briefs that carry `RelevantFiles`/`AllowedWrites` are built per phase, so the inventory must settle whether that signal exists at partition time or has to be sourced from the plan output or ticket description. Per `src/AGENTS.md` the design choice is to trust the code over the docs - the citations must come from the current tree. The forward-compatible `FileHintsAvailable` flag exists so a missing hint degrades the signal to "no overlap, bias solo" rather than crashing the partition.

OOS:
- Extracting any signal (Brief 02)
- Any partition rule (Brief 03)
- Any LLM classifier (Plan B)
- Any chain wiring or op-31 invocation (Plan C)

#### Brief 02: cohesion-signal-extraction

Goal: Turn a candidate sibling group and the available ticket/brief data into a populated `CohesionSignals` bundle, so the partition rules in Brief 03 operate on numbers rather than re-reading tickets. The extractor is pure over its inputs and never calls an agent.

Inputs: The `CohesionSignals` contract (Brief 01); the sibling graph and dependency levels from `BuildSiblingGraphAsync` and `TopologicalSorter`; the per-child `Ticket` records; the file-hint source the Brief 01 inventory identified (briefs, plan output, or description) with its documented fallback.

Outputs:
- A `CohesionSignalExtractor` that, given a group of sibling tickets and their available briefs, returns `CohesionSignals`: the multiset of member `Size` values, the maximum `Risk`, the member count, the set of intra-group `blocked_by` edges, and a file-overlap ratio computed as the intersection over union of each member's file hints.
- When file hints are unavailable for any member, the overlap ratio is reported as unknown and `FileHintsAvailable` is false - never silently zero in a way indistinguishable from a real disjoint set.
- Tests over: all-small overlapping, mixed-size disjoint, a dependency-laddered group, and a group with missing file hints.

Acceptance:
- [ ] Member sizes, max risk, and count are extracted directly from the Ticket records
- [ ] Intra-group blocked_by edges are extracted from the same relation data BuildSiblingGraphAsync uses
- [ ] File-overlap ratio is computed by intersection-over-union of member file hints when available
- [ ] Missing file hints yield FileHintsAvailable=false and an unknown overlap, distinct from a computed-zero overlap
- [ ] Extraction performs no agent call and no git or network IO beyond reading the supplied data
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: File overlap is weighted heavily downstream because it is the signal that separates a genuine cohesive design (members editing the same files or adjacent call paths, exactly op-31's batch criteria) from a coincidental size match. Reporting unknown-overlap distinctly from zero-overlap matters: zero overlap is a real "these are disjoint, keep solo" signal, while unknown is "we could not tell, bias solo" - collapsing them would hide the difference from the ledger.

OOS:
- Deciding anything from the signals (Brief 03)
- Sourcing file hints that the inventory found do not exist pre-implement (use the documented fallback)
- The classifier (Plan B)

#### Brief 03: deterministic-partition-rules

Goal: Apply the deterministic gate to the extracted signals and produce a `BatchPlan` in which every group is dispositioned Solo, Batch, or Ambiguous, with no LLM involved. This is the free 80% that disposes of the obvious cases and hands only the genuine middle to Plan B.

Inputs: `CohesionSignals` per candidate group (Brief 02); the `BatchPlan`/`BatchGroup` contracts (Brief 01).

Outputs:
- A `CohesionGate` pure function mapping candidate groups to a `BatchPlan`. Hard-veto to Solo when any member is `Size.L`, any member is `Risk.High`, member count exceeds a configurable cap (default 3), file hints are unavailable, or the group spans a real dependency ladder of mixed sizes. Auto-approve to Batch when all members are `Size.S`, max risk is Low, count is within cap, and the file-overlap ratio exceeds a configurable threshold. Everything else is dispositioned Ambiguous.
- Each disposition records the deciding rule as its reason string, so the plan is self-explaining in logs.
- A logged batch-plan summary at partition time listing each group, its disposition, and its reason.
- Tests asserting the slam-dunk (all-small overlapping low-risk) auto-batches, every veto condition forces solo, and the mixed-but-safe case lands ambiguous.

Acceptance:
- [ ] Any L-sized or High-risk member, an over-cap count, or unavailable file hints forces the group to Solo
- [ ] An all-small, low-risk, within-cap, high-overlap group auto-approves to Batch with no classifier needed
- [ ] Every other group is dispositioned Ambiguous, not silently batched or soloed
- [ ] Each group's disposition carries the deciding rule as a human-readable reason
- [ ] The cap and overlap threshold are configurable, not hardcoded constants
- [ ] The partition is a pure function with no agent call, and the batch plan is logged at partition time
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The gate is deliberately conservative at both extremes - it only auto-batches the case where the tickets are essentially one change split for bookkeeping (all small, low risk, heavily overlapping files), and it vetoes anything whose failure would be expensive to bisect (an L ticket, a High-risk ticket, a long dependency ladder). The asymmetric-cost reasoning from "Why this exists" lives here: when in doubt the rule resolves toward Solo, so the only groups that reach the paid classifier are ones where a real saving is plausible but cohesion is genuinely uncertain.

OOS:
- The classifier that resolves Ambiguous groups (Plan B)
- Acting on any Batch disposition (Plan C)
- Tuning the default cap or threshold against real chains (post-landing measurement)

## Plan B: Cheap classifier for the ambiguous band

### Goal

After this plan, the groups the deterministic gate left Ambiguous are resolved to Batch or Solo by a single cheap small-model call per group, biased toward Solo, and folded back into the BatchPlan. Groups the gate already dispositioned never reach the classifier, so the only tokens spent are on the genuine semantic middle.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | cohesion-classifier-worker | A small-model worker that returns a structured batch/solo verdict over a trimmed group brief | A | new: src/ThroughlineBuild.Cohesion/CohesionClassifier.cs + tests |
| 05 | fold-verdicts-into-plan | Resolve each Ambiguous group via the classifier and rewrite its disposition, skipping already-decided groups | 04 | modified: src/ThroughlineBuild.Cohesion/CohesionGate.cs (or a planner that composes it) + tests |

### Briefs - detail

#### Brief 04: cohesion-classifier-worker

Goal: Build a focused classifier that answers one semantic question metadata cannot - whether a group of sibling tickets is one coherent design an agent could implement in a single session, or distinct work that fusing would degrade - and returns a structured, split-biased verdict on the cheapest configured model.

Inputs: The `IWorkerAgent`/`WorkerOptions`/`WorkerSize` abstraction at `src/ThroughlineBuild.Contracts/IWorkerAgent.cs`; `ObsoleteRatifier` at `src/ThroughlineBuild.Verification/ObsoleteRatifier.cs` as the existing pattern for a focused, verdict-parsing worker call; the per-group ticket titles, `Brief.Instruction` text, and file hints.

Outputs:
- A `CohesionClassifier` that builds a trimmed brief - member titles, instructions, and file hints only, no repo context and no plan - and runs it through a worker pinned to `WorkerSize.Small` (resolving to the configured small model, haiku or gpt-mini, regardless of the members' own sizes).
- A structured verdict: `{ decision: batch | solo, confidence: 0..1, reason: string }`, parsed deterministically from the worker envelope the way `ObsoleteRatifier` extracts its verdict.
- A split bias: any verdict below a configurable confidence threshold resolves to Solo, and a parse failure resolves to Solo, never to Batch.
- Tests over a clearly-cohesive group (votes batch), a clearly-disjoint group (votes solo), and a malformed verdict (resolves solo).

Acceptance:
- [ ] The classifier brief contains only titles, instructions, and file hints - not repo context, not the full plan
- [ ] The worker runs at WorkerSize.Small irrespective of member ticket sizes
- [ ] The verdict is parsed into a typed decision/confidence/reason with no free-text guessing
- [ ] A sub-threshold confidence or a parse failure resolves to Solo, never Batch
- [ ] The confidence threshold is configurable
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Pinning the classifier to the small model regardless of member size is the whole cost argument - the question is a cheap semantic judgment over a few KB, not a reasoning-heavy task, so it must not inherit a member's Medium or Large model. Reusing the `ObsoleteRatifier` shape rather than inventing a new worker path keeps the call mediated by the same envelope-parsing machinery the rest of the system trusts. The split bias is the asymmetric-cost rule applied to the classifier itself: a false solo costs only a forgone saving, a false batch costs a tangled stack, so uncertainty must fall to solo.

OOS:
- Deciding which groups to classify (Brief 05 supplies only Ambiguous ones)
- Any deterministic signal (Plan A owns)
- Acting on the verdict in the chain (Plan C)

#### Brief 05: fold-verdicts-into-plan

Goal: Resolve the BatchPlan to a final state in which no group remains Ambiguous, by running the classifier on exactly the Ambiguous groups and rewriting each to Batch or Solo, while leaving Plan A's already-decided groups untouched so no tokens are wasted re-judging them.

Inputs: The dispositioned `BatchPlan` from Brief 03; the `CohesionClassifier` from Brief 04.

Outputs:
- A planner step that iterates the BatchPlan, calls the classifier only for groups with Disposition Ambiguous, and rewrites each to Batch or Solo carrying the classifier's reason and confidence.
- Solo and Batch groups from Plan A pass through unchanged with no classifier call.
- A logged before/after of any Ambiguous group's resolution, including the verdict and confidence.

Acceptance:
- [ ] Every Ambiguous group is resolved to Batch or Solo after this step; none remain Ambiguous
- [ ] Groups already dispositioned Solo or Batch by Plan A incur no classifier call
- [ ] Each resolved group records the classifier verdict, confidence, and reason
- [ ] The number of classifier calls equals the number of Ambiguous groups, asserted in a test
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The "skip already-decided groups" property is the cost discipline made literal - the test asserting one call per Ambiguous group is what guarantees the classifier never silently re-judges the free cases and erodes the saving. This step is where Plan A's free triage and Plan B's paid judgment compose into a final, fully-dispositioned plan ready for the chain to act on.

OOS:
- Acting on the final plan (Plan C)
- Re-running the deterministic gate (Plan A produced its dispositions already)

## Plan C: Wire the batch plan into the chain

### Goal

After this plan, a parent chain consumes the final BatchPlan: solo groups run the existing per-ticket implement path unchanged, batch groups feed op-31's batch-implement engine with a worker size escalated for the fused workload, an operator can override the plan from the CLI, a failed batch falls back to solo, and a per-decision ledger event records what the detector decided and what it cost, so real chains decide whether the classifier earns its place.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 06 | chain-consumes-batch-plan | RunParentChainAsync dispatches by group: solo to the existing path, batch to op-31's engine, with CLI override and solo fallback | B, op-31 | modified: src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Commands/ChainCommand.cs |
| 07 | fused-group-size-escalation | The batch worker runs one tier above the group's largest member, capped at Large | 06 | modified: src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs (or the batch dispatch site) + tests |
| 08 | cohesion-decision-ledger | A per-group ledger event records band, classifier tokens, estimated cold-starts avoided, and batch-vs-solo outcome | 06 | modified: the run event-log emission point (per Brief 01 inventory) |

### Briefs - detail

#### Brief 06: chain-consumes-batch-plan

Goal: Make `RunParentChainAsync` dispatch by `BatchGroup` instead of by individual child: a Solo group runs today's per-ticket implement-review path, a Batch group is handed to op-31's batch-implement engine, an operator flag overrides the computed plan, and a batch execution failure de-batches and retries the members solo so the detector is never a one-way door.

Inputs: The final BatchPlan (Plan B); the partition slot and op-31 entry-point signature from the Brief 01 inventory; `RunParentChainAsync` and the per-level dispatch loop in `src/ThroughlineBuild.Phases/ChainPhase.cs`; op-31's batch-implement engine; `ChainCommand` in `src/ThroughlineBuild.Commands/ChainCommand.cs` for the override flags.

Outputs:
- `RunParentChainAsync` computes the BatchPlan after `BuildSiblingGraphAsync`, then dispatches per group: Solo groups invoke the existing per-ticket path unchanged; Batch groups invoke op-31's batch-implement engine with the group's members.
- A `--no-batch` flag that forces every group Solo, and op-31's `--batch-implement TLB-...,...` flag interpreted as an operator-declared Batch group that overrides the computed disposition for those ids.
- A batch-execution failure (the batch worker errors, or the combined review rejects past the rework cap) falls back to running that group's members through the existing per-ticket path, logged as a de-batch.
- The dispatch order and stacking semantics for Solo groups are unchanged from today.

Acceptance:
- [ ] A chain with only Solo groups behaves exactly as today
- [ ] A Batch group is implemented through op-31's engine, not the per-ticket path
- [ ] `--no-batch` forces all groups Solo and skips the classifier entirely
- [ ] An explicit `--batch-implement` list overrides the computed plan for those ticket ids
- [ ] A batch failure de-batches the group to per-ticket implementation rather than failing the chain
- [ ] Per-ticket commit markers and Plane state transitions are preserved in both paths (per op-31's invariants)
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: This brief is the join with op-31 and must not start until op-31's engine exists - it calls that engine, it does not reimplement batched execution. The de-batch fallback is what makes the whole detector safe to enable: because a wrong Batch disposition degrades to the status-quo per-ticket path rather than a dead chain, the asymmetric-cost bet is bounded on the downside. `--no-batch` skipping the classifier matters for cost - an operator who wants the old behavior pays nothing for the detector.

OOS:
- The batched execution itself, commit-per-ticket, and combined review (op-31 owns)
- Worker size for the batch (Brief 07)
- The ledger (Brief 08)
- Parallel batch execution (op-31 deferred it; out of scope here too)

#### Brief 07: fused-group-size-escalation

Goal: Size the single batch worker for the combined workload rather than for one member, so a group of three Small tickets is not handed to the small model as though it were one Small ticket.

Inputs: The batch dispatch site (Brief 06); `WorkerSizeMapper.FromTicketSize` at `src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs`; the `WorkerSize` enum.

Outputs:
- A size rule for batch groups: the batch worker's `WorkerSize` is one tier above the largest member's mapped size, capped at `WorkerSize.Large`. A group of all-Small members runs Medium; a group already containing a Medium member runs Large; Large is the ceiling.
- The rule applies only to Batch-group dispatch; Solo dispatch keeps `WorkerSizeMapper.FromTicketSize` as-is.
- Tests over all-small (escalates to Medium), contains-medium (escalates to Large), and the Large cap.

Acceptance:
- [ ] A batch group of all-Small members runs at Medium
- [ ] A batch group containing a Medium member runs at Large
- [ ] Escalation never exceeds Large
- [ ] Solo dispatch is unaffected and still uses FromTicketSize
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The deterministic gate already caps batch groups below any Large member, so escalation never starts from Large and the cap is reached only via the contains-Medium case. The escalation is the conscious half of the cost trade named in op-31's estimate: the win is fewer cold starts, the retained cost is one larger warm session, and pricing that session one tier up is what keeps the fused work from being under-powered relative to its real size.

OOS:
- Choosing which groups are batched (Plans A and B)
- Per-member sizing inside a batch (the batch is one session at one size by construction)

#### Brief 08: cohesion-decision-ledger

Goal: Emit one ledger event per group so the detector's decisions and their cost are measurable on real chains, letting data rather than argument decide whether the classifier and the batch path pay for themselves.

Inputs: The final BatchPlan with per-group dispositions, bands, and classifier verdicts (Plan B); the classifier's token usage from the worker call events (the LLM-call events already carry per-call token data, per the Brief 01 inventory); the run event-log emission point.

Outputs:
- A ledger event per group recording: the disposition (Solo/Batch) and the band that produced it (deterministic-veto, deterministic-auto, or classifier); classifier tokens spent (zero for deterministically-decided groups); estimated cold starts avoided (member count minus one for a Batch group, zero for Solo); and the execution outcome including any de-batch fallback.
- The event lands in the existing run event log alongside the chain's other events, for both attended and unattended runs.

Acceptance:
- [ ] Each group emits one ledger event carrying its disposition and deciding band
- [ ] Classifier tokens are recorded per group and are zero for deterministically-decided groups
- [ ] Estimated cold starts avoided is recorded for Batch groups
- [ ] A de-batch fallback is recorded on the affected group's event
- [ ] The event is present for both attended and unattended runs
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The ledger is the same discipline op-30 applied to its quality gate - instrument the cost terms so the cost model is settled by a real chain rather than re-litigated in the abstract. The two numbers that matter face each other directly in the event: classifier tokens spent versus cold starts avoided, which is exactly the trade "Why this exists" claims nets positive. If a real chain shows the classifier band rarely pays, the band's cap or threshold is the tuning knob, and the ledger is what reveals it.

OOS:
- Acting on the ledger to auto-tune thresholds (a later increment)
- Any reporting or analysis layer over the ledger events
- Token accounting for the implement workers (op-31's engine owns its own cost reporting)

## What done looks like

An operator runs `build chain TLB-418` on a parent whose children include a tightly-coupled cluster and an unrelated cleanup ticket, and the chain decides for itself how to group them. The cluster's all-small, low-risk, file-overlapping members are auto-batched for free; a mixed pair the metadata cannot settle gets a single cheap small-model verdict that lands them as one batch or two solos; the unrelated cleanup stays solo. The operator sees a logged batch plan naming each group, its disposition, and why. Batched groups flow into op-31's one-session engine at an escalated worker size and still produce one commit and one Plane transition per ticket; if a batch goes wrong it de-batches to the per-ticket path rather than wedging the chain. `--no-batch` returns the exact old behavior at zero detector cost, and a per-group ledger event lets the operator read, after the run, how many cold starts the detector avoided and how many classifier tokens that judgment cost - the data that says whether the murky-middle classifier was worth keeping.
