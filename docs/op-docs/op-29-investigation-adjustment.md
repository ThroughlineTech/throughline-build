# Operation: investigation-provenance

Pay for investigation once. The op-doc is the human's investigation handed to the first agent; the chain's own commits are each agent's investigation handed to the next. This operation makes the chain stop re-paying that cost: skip the redundant plan worker when the ticket already carries an op-doc plan (Plan A), remove the parallelism that forces every worker to cold-start and re-grep in isolation and reuse one worktree per chain (Plan B), and point each agent at the commits already sitting in its own history instead of having a worker re-author a summary (Plan C). It also folds in a git-hygiene fix surfaced during planning: workers doing freelance `git stash` surgery against the repo-global stash stack have wedged real chains, so Plan D forbids it and adds a fail-fast entry gate. Finally, a chain only runs in the right order if the op-doc's declared sequence reaches Plane as `blocked_by` relations and the chain reads them back; a run where a dependent ticket executed ahead of its dependency showed that link is not guaranteed, so Plan E makes the op-doc -> scaffold -> chain sequence contract complete and verifiable at both ends.

## Why this exists

A scaffolded brief-ticket lands in `Backlog`, so `build chain` routes it `Backlog -> Plan` and spawns a plan worker that re-investigates and writes a fresh `PLAN_BODY` - even though the ticket description already is the plan written deliberatively in the op-doc. That is wasted tokens and a fidelity leak: the worker can re-plan differently than the op-doc intended.

Parallel chain dispatch buys wall-clock at the cost of N duplicated investigations on sibling tickets that almost always share a code region, plus the entire merge-contention apparatus. For a solo operator paying per token, wall-clock is not the binding constraint. Once dispatch is sequential, there is no reason to add and tear down a worktree per ticket - one worktree per chain, with a branch per ticket inside it, cuts the churn while keeping independent shipping, clean per-ticket review diffs, and failure isolation.

With the chain sequential and shipping each ticket into the target before the next implements, the prior tickets' commits are already present in the next agent's checkout. So the handoff is not an authored prose digest (eager, lossy, token-costly) - it is a deterministic pointer: the cumulative touched-files and the commit range, derived from data the chain already holds. The next agent dereferences only what it needs.

Separately: a worker stashed WIP during an unrelated ticket, the repo-global stash stack carried it across worktree boundaries, a later apply conflicted onto the main checkout, and nothing resolved or aborted it - wedging every subsequent ship preflight. Worktree isolation did not help because the stash stack ignores it. The fix is to stop workers using stash at all and to detect a dirty or conflicted tree at phase entry instead of 26 minutes later at ship.

Also separately: in one run two sibling tickets - a loader and the verb that consumes it - ran in the wrong order, the dependent ahead of its dependency, and each independently created the same file. That is a sequence break, not a concurrency break: width-1 dispatch preserves the existing topological order but does not invent it, and the order is only as good as the `blocked_by` edges among the siblings. Those edges have a chain of custody: the op-doc declares the dependency in its `Deps` / `Depends on` columns, scaffold must translate every declared dependency into a Plane `blocked_by` relation, and the chain must read those relations back when it orders. The op-doc is the right source because it has the context and is defining the work. Each link must hold and be visible, or a missing edge surfaces only as duplicated, conflicting work several tickets downstream.

## Dispatch order

| Plan | Name | Depends on | Effort |
|---|---|---|---|
| A | investigation-bypass | - | 1 day |
| B | sequential-chain | - | 2 days |
| C | handoff-addendum | B | 1 day |
| D | worker-git-hygiene | - | 1 day |
| E | sequence-contract | - | 1 day |

## Plan A: investigation-bypass

### Goal

A ticket whose description already carries an op-doc plan can enter the chain at Implement without a plan-worker investigation, by deterministically promoting it to a planned state. Default behavior (worker investigation) is preserved; promotion is opt-in.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|---|---|---|---|
| 01 | plan-promote-path | Deterministic promote branch in PlanPhase: no worker, stamp marker, apply labels, transition to Ready | - | PlanPhase.cs, Config.cs |
| 02 | wire-promote-flag | CLI flag + config default selecting promote over investigate; chain honors it on a Backlog entry | 01 | Program.cs, CliUsage.cs, ChainPhase.cs |
| 03 | promote-tests | Cover the promote path end to end | 02 | ThroughlineBuild.Phases.Tests |

### Briefs - detail

#### Brief 01: plan-promote-path

**Goal.** Add a deterministic promotion branch to `PlanPhase` that reuses the existing fetch / parent-guard / `Backlog` state-guard and the existing label-apply, marker-post, and transition steps, but skips the worker run and `PLAN_BODY` resolution.

**Inputs.** A `Backlog` ticket whose description is already the plan; the resolved current main SHA via `BaseRefResolver`.

**Outputs.** The ticket transitioned `Backlog -> Planning -> Ready`, with a `[planned_at: <currentMainSha>]` marker and any `risk:` / `size:` labels applied. No `WorkerSpawn`, no `LlmCall`, no description mutation.

**Acceptance criteria.**
- [ ] A promoted ticket reaches `Ready` without spawning a worker.
- [ ] The ticket description is unchanged by promotion.
- [ ] A `[planned_at: <sha>]` marker is posted with the current main SHA, so `ImplementPhase`'s drift check has a baseline.
- [ ] The promote branch is selected by an explicit option, not by default; absent it, `PlanPhase` runs the worker as before.

**Notes / gotchas.** Stamping `planned_at` at promote time keeps the drift gate live rather than silently disabling it. Same WHAT/HOW split as the two-system division: the human already did the investigation, so the phase only stamps provenance.

**Out of scope.** Auto-detecting whether a description is already a plan - selection is explicit.

#### Brief 02: wire-promote-flag

**Goal.** Make the promote path reachable from the CLI and from `chain`.

**Inputs.** Brief 01's promote branch; the `[plan]` config section and verb dispatch.

**Outputs.** A `--from-brief` flag on `plan` and `chain`, plus a `[plan].mode` config key (`investigate` default, `promote` opt-in). When set, `ChainPhase` routes a `Backlog` entry through promotion instead of the worker plan, then continues into the implement-review loop unchanged.

**Acceptance criteria.**
- [ ] `build chain <id> --from-brief` on a `Backlog` op-doc ticket reaches Implement with no plan-worker spawn.
- [ ] Without the flag and with default config, `chain` behavior is unchanged.
- [ ] The flag and config key appear in usage output.

**Notes / gotchas.** Default stays `investigate` so non-op-doc Backlog tickets are unaffected. Flag wins over config.

**Out of scope.** Changing where `scaffold` lands tickets.

#### Brief 03: promote-tests

**Goal.** Lock the promote contract.

**Inputs.** Briefs 01-02.

**Outputs.** Tests covering promotion and the preserved default.

**Acceptance criteria.**
- [ ] A test asserts a promoted ticket emits no `WorkerSpawn`/`LlmCall` and ends in `Ready`.
- [ ] A test asserts the description is byte-identical before and after promotion.
- [ ] A test asserts the `[planned_at]` marker equals the resolved main SHA.
- [ ] A test asserts default (no flag) still runs the worker plan.

**Notes / gotchas.** AOT-sensitive paths follow the existing reflection-disabled test discipline.

**Out of scope.** Implement-phase tests beyond confirming the drift baseline is found.

## Plan B: sequential-chain

### Goal

Remove concurrency from chain execution while keeping dependency-ordered sequencing, and reuse a single worktree per chain. The dependency graph becomes a pure sequencer; the per-ticket worktree churn goes away. Per-ticket branches, per-ticket review diffs, and per-ticket shipping are preserved.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|---|---|---|---|
| 01 | width-1-dispatch | Force chain concurrency to 1 while preserving blocked_by topological order | - | ParallelDispatcher.cs, ChainPhase.cs, Config.cs |
| 02 | retire-parallel-surface | Remove the --max-parallel flag, the parent-concurrency constant, and the now-unreachable parallelism types | 01 | ChainPhase.cs, Program.cs, CliUsage.cs, TicketGraph.cs, SequentialChainDispatcher.cs, MainWorktreeLock.cs |
| 03 | shared-chain-worktree | One worktree per chain, branch per ticket inside it; create once, remove once | 01 | ImplementPhase.cs, ShipPhase.cs, ChainPhase.cs, PhaseWorktreeLayout.cs, WorktreeDecrufter.cs |
| 04 | sequential-tests | Sequential dependency-ordered execution and single-worktree reuse | 02, 03 | ThroughlineBuild.Phases.Tests |

### Briefs - detail

#### Brief 01: width-1-dispatch

**Goal.** Run chain tickets strictly one at a time, keeping `TopologicalSorter.ComputeLevels` ordering intact.

**Inputs.** The multi-ticket `ParallelDispatcher` path and the parent-chain `RunParentChainAsync` path; `workers.max_concurrency`; `MaxParentChainConcurrency`.

**Outputs.** Both paths execute with effective concurrency 1. `blocked_by` ordering still drives sequence; no two tickets in a level run at once. Ancestor-skip and the success/exit-code mapping are unchanged.

**Acceptance criteria.**
- [ ] At no point do two worker subprocesses run concurrently within a chain.
- [ ] A ticket blocked by another runs after its blocker; independent tickets run in input order.
- [ ] `--continue-past-failure` and ancestor-skip behavior are preserved.

**Notes / gotchas.** Pin width to 1; keep the topological sort - the ordering is the part worth keeping.

**Out of scope.** Removing types (Brief 02) and worktree reuse (Brief 03).

#### Brief 02: retire-parallel-surface

**Goal.** Delete the parallelism surface area now that width is 1.

**Inputs.** Brief 01.

**Outputs.** `--max-parallel` / `ForceParallel` removed; `MaxParentChainConcurrency` removed; the unreachable second `TicketGraph` type and the shadowed `SequentialChainDispatcher` removed if no longer referenced. `MainWorktreeLock` removed if provably unreachable, else kept as a documented cheap guard.

**Acceptance criteria.**
- [ ] The build has a single `TicketGraph` type.
- [ ] `--max-parallel` no longer appears in usage or argument parsing.
- [ ] The solution builds and existing tests pass after the removals.

**Notes / gotchas.** This is the sharpest seam the state-of-system set flags. Remove only what becomes unreachable; if a type still has a live caller, leave it and note why.

**Out of scope.** The `ShipPhase` divergence/rebase logic stays - sequential chains still rebase onto the target.

#### Brief 03: shared-chain-worktree

**Goal.** Use one worktree for the whole chain run, reused across tickets, instead of one per ticket.

**Inputs.** Brief 01 (sequential is the precondition); existing per-ticket `git worktree add` in `ImplementPhase`, `WorktreeDecrufter`, `PhaseWorktreeLayout`.

**Outputs.** The chain creates one worktree at start and removes it once at end. Each ticket checks out its own `ticket/<slug>` branch off the current target head inside that worktree, implements, and commits; review diffs `target..ticket/<slug>`; ship rebases and FF-merges per ticket into the target as today. The reused worktree is verified clean before each ticket starts. Single-ticket `build implement` (no chain) still creates its own worktree.

**Acceptance criteria.**
- [ ] A chain of N tickets creates one worktree and removes it once, not N add/remove cycles.
- [ ] Each ticket works on its own branch; per-ticket review diff and per-ticket ship are unchanged.
- [ ] The reused worktree is asserted clean before each ticket; a dirty or conflicted reused worktree stops with a clear reason rather than proceeding.
- [ ] A failed ticket leaves its branch isolated; already-shipped siblings are unaffected.

**Notes / gotchas.** No single-shared-branch parent mode - children keep independent branches and independent ship, for failure isolation. The clean-before-each-ticket assert overlaps Plan D's entry gate; if Plan D is present, defer to it rather than building a second gate.

**Out of scope.** A single shared branch for a parent; removing worktrees entirely.

#### Brief 04: sequential-tests

**Goal.** Prove sequential dependency-ordered execution and single-worktree reuse.

**Inputs.** Briefs 01-03.

**Outputs.** Tests asserting one-at-a-time execution, preserved ordering/skip/exit-code semantics, and one-worktree-per-chain.

**Acceptance criteria.**
- [ ] A test asserts a multi-ticket chain dispatches sequentially in dependency order.
- [ ] A test asserts ancestor-skip still fires on an upstream failure and the single-ticket exit-code mapping is unchanged.
- [ ] A test asserts a multi-ticket chain creates and removes exactly one worktree.

**Notes / gotchas.** Use the existing dispatcher and git test fakes; no real worker subprocess needed.

**Out of scope.** Performance/timing assertions.

## Plan C: handoff-addendum

### Goal

In a sequential chain, point each ticket's implement brief at the commits the chain has already produced - the cumulative touched-files and the commit range - so the next agent re-greps less. Fully deterministic: no worker authors anything. Compile-time disable-able; an empty range is a no-op.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|---|---|---|---|
| 01 | derive-chain-commits | Compute the chain's commit range and cumulative diff --stat from data the chain already holds | - | ChainPhase.cs, ChainCommitRange.cs, IGitClient.cs |
| 02 | inject-pointer | Fold the touched-files into the next implement brief's RelevantFiles and a pointer line into Context | 01 | ImplementPhaseOptions.cs, ImplementPhase.cs, ImplementBriefBuilder.cs |
| 03 | handoff-compile-flag | Compile-time constant gating injection; disabled => baseline-identical brief | 02 | ChainPhase.cs, ImplementPhase.cs |
| 04 | handoff-tests | Pointer threading, empty-range no-op, compile-disabled baseline | 03 | ThroughlineBuild.Phases.Tests |

### Briefs - detail

#### Brief 01: derive-chain-commits

**Goal.** Deterministically compute what the chain has done so far, without a worker.

**Inputs.** The target-branch head SHA captured at chain start; the current target head; the per-ticket `[implemented_at]` / `[shipped_at]` markers the chain already parses; `IGitClient` (it already exposes `LogShas`).

**Outputs.** A helper that returns the commit range `chainStart..currentHead`, the count of commits in it, and the cumulative `git diff --stat` touched-files for that range. A stat method is added to `IGitClient` if one is not present.

**Acceptance criteria.**
- [ ] Given a chain that has shipped M tickets, the helper returns the range bounding exactly those M tickets' commits and their touched-files.
- [ ] At the first ticket of a chain (nothing shipped yet) the range is empty and the touched-files list is empty.
- [ ] The computation makes no LLM call and spawns no worker.

**Notes / gotchas.** In the shared-worktree + per-ticket-ship model, each shipped ticket advances the target head, so `chainStart..currentHead` is exactly the chain's prior work. Capture `chainStart` once at `ChainStart`.

**Out of scope.** Authoring any prose; emitting a dedicated handoff event (the markers and git history already record this - add an event later only if audit needs it).

#### Brief 02: inject-pointer

**Goal.** Put the pointer in front of the next agent.

**Inputs.** Brief 01's range + stat; `ImplementPhaseOptions`; `ImplementBriefBuilder`; `Brief(..., RelevantFiles, ..., Context)`.

**Outputs.** The chain passes the derived touched-files and range on `ImplementPhaseOptions`; `ImplementBriefBuilder` folds the touched-files into `RelevantFiles` and a single bounded line into `Context` ("N commits this chain, range X..Y; reference for detail"). An empty range produces a brief identical to the no-handoff case.

**Acceptance criteria.**
- [ ] In a 2-ticket sequential chain, the second ticket's implement brief lists the first ticket's touched-files and the range pointer.
- [ ] An empty range leaves the implement brief unchanged - no empty headers, no stray context.
- [ ] The touched-files list is deduped (a file touched by several prior tickets appears once).

**Notes / gotchas.** This is the cheap, grep-killing part (the file list) plus a lazy pointer for the rest. Keep the carried text bounded so it does not regrow the unbounded-context cost it is meant to avoid. Beyond saving greps, the file list is content-level coordination: a later ticket that sees an earlier sibling already created a file uses it instead of recreating it - the antidote to the duplicate-file collision that out-of-order siblings produced. Effective only when the order is right (Plan E); the pointer cannot help a dependent that runs before its dependency.

**Out of scope.** Carrying full diffs into the brief; capturing dead-ends or negative findings (those are not in commits - revisit only if chains re-walk the same ground).

#### Brief 03: handoff-compile-flag

**Goal.** Let the pointer be turned off at compile time.

**Inputs.** Brief 02's injection path.

**Outputs.** A compile-time constant (a `const bool`, or a `DefineConstants` symbol with `#if`) that gates injection. Defaults on. When off, the next ticket's implement brief is byte-identical to a pre-Plan-C baseline.

**Acceptance criteria.**
- [ ] With the constant false, the next ticket's implement brief is byte-identical to a no-pointer baseline.
- [ ] The off-switch is compile-time, not a runtime config key or flag.
- [ ] With the constant true (default), Brief 02 behavior holds.

**Notes / gotchas.** A single well-named constant in one place, not scattered guards.

**Out of scope.** Per-ticket or per-repo runtime toggles.

#### Brief 04: handoff-tests

**Goal.** Lock the pointer, the no-op, and the off-switch.

**Inputs.** Briefs 01-03.

**Outputs.** Tests covering threading, the empty-range no-op, and the compile-disabled baseline.

**Acceptance criteria.**
- [ ] A test asserts a 2-ticket chain threads ticket 1's touched-files and range into ticket 2's brief.
- [ ] A test asserts an empty range yields an unchanged brief.
- [ ] A test (or compile-time variant) asserts the disabled build produces the baseline brief.

**Notes / gotchas.** The disabled assertion may need a separate build configuration or direct exercise of the gated path.

**Out of scope.** Token-cost measurement.

## Plan D: worker-git-hygiene

### Goal

Stop workers from corrupting git state. Forbid `git stash` and other freelance git surgery in workers, make the verifier read-only, and add a fail-fast entry gate that detects a dirty, conflicted, or stash-polluted working tree and stops with a precise reason instead of failing opaquely at ship.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|---|---|---|---|
| 01 | no-worker-stash | Implement + review templates across all four agents forbid git stash and operating on the shared stash stack | - | Briefs/Templates/{claude-code,codex,gemini,copilot}/{implement,review}.md |
| 02 | read-only-verifier | Review posture forbids git mutation; verifier leans on the deterministic diff + AutomatedChecksRunner | - | Briefs/Templates/*/review.md, WorkerAgentReviewer.cs, ReviewPhase.cs |
| 03 | hygiene-entry-gate | Phase entry and ship preflight detect dirty/unmerged/stash-polluted state and stop with a precise, attributed reason | - | ImplementPhase.cs, ShipPhase.cs, IGitClient.cs |
| 04 | hygiene-tests | Gate fires on conflicted/dirty/stash state; clean tree passes | 01, 02, 03 | ThroughlineBuild.Phases.Tests |

### Briefs - detail

#### Brief 01: no-worker-stash

**Goal.** Take `git stash` out of the workers' hands.

**Inputs.** The four per-agent implement and review templates.

**Outputs.** Each implement and review template explicitly instructs the worker never to run `git stash` or otherwise operate on the shared stash stack, and states that if a clean build is needed it must be done in place, never by stashing.

**Acceptance criteria.**
- [ ] All four implement templates and all four review templates carry the no-stash instruction.
- [ ] The instruction names the failure mode (the stash stack is repo-global and leaks across worktrees).

**Notes / gotchas.** This is prevention by instruction; it cannot hard-block a worker from running stash, which is why Brief 03 is the backstop. Keep the wording in sync across all eight files by hand - nothing enforces it.

**Out of scope.** Plan/draft/decompose templates.

#### Brief 02: read-only-verifier

**Goal.** Make review incapable of mutating git state.

**Inputs.** The review templates; `WorkerAgentReviewer`; `ReviewPhase`'s deterministic diff synthesis and `AutomatedChecksRunner`.

**Outputs.** The review posture forbids any git mutation (no stash, no checkout, no reset, no rebase); the verifier relies on the already-synthesized diff and the orchestrator-run checks rather than freelancing stash/build cycles.

**Acceptance criteria.**
- [ ] The review templates state the verifier is read-only with respect to git.
- [ ] Review continues to surface its verdict from the synthesized diff + automated checks without the worker mutating the tree.

**Notes / gotchas.** Review already gets a deterministic diff - it has no legitimate need to touch git.

**Out of scope.** Changing what the automated checks are.

#### Brief 03: hygiene-entry-gate

**Goal.** Catch a poisoned working tree at the door, not at ship.

**Inputs.** `git status --porcelain` (it already surfaces conflict codes like `UU`/`AA`); `git stash list`; the existing ship clean-check.

**Outputs.** Phase entry refuses to proceed when the working tree has unmerged/conflicted paths, naming them. It also detects a dangling stash from another ticket and reports it. The stop message distinguishes orphaned state (not from this ticket) from this ticket's expected changes. The ship preflight reports the same precise state ("unresolved conflict in X, Y; stash from ticket/Z, unrelated to this ticket") instead of "N modified tracked files".

**Acceptance criteria.**
- [ ] A phase started on a tree with unmerged paths stops immediately, naming the conflicted files.
- [ ] A dangling stash from an unrelated ticket is detected and named.
- [ ] The ship preflight message identifies conflict state and unrelated stashes precisely rather than as generic "modified tracked files".
- [ ] A clean tree passes the gate with no change in behavior.

**Notes / gotchas.** Detect and stop - do not auto-clean. Auto-dropping a stash or resetting a conflict could destroy real WIP. This gate would have stopped the second child at entry instead of after a full plan/implement/review.

**Out of scope.** Automatic recovery or stash cleanup.

#### Brief 04: hygiene-tests

**Goal.** Lock the gate behavior.

**Inputs.** Briefs 01-03.

**Outputs.** Tests over the entry gate and preflight messaging.

**Acceptance criteria.**
- [ ] A test asserts a phase started on a conflicted tree stops and names the files.
- [ ] A test asserts a dangling unrelated stash is reported.
- [ ] A test asserts a clean tree passes unchanged.

**Notes / gotchas.** Use git test fakes to simulate conflict and stash states.

**Out of scope.** Testing the template instruction text (it is prose, not code).

## Plan E: sequence-contract

### Goal

Make the op-doc -> scaffold -> chain sequence contract complete and verifiable at both ends, so declared dependencies become Plane `blocked_by` relations and the chain orders by them - and so a missing or wrong edge is visible immediately, not as duplicated work downstream.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|---|---|---|---|
| 01 | scaffold-encodes-deps | Scaffold translates every declared dependency into a Plane blocked_by relation and reports the edges it created | - | ScaffoldPhase.cs, OpDocParser.cs, OpDocValidator.cs, PlaneTicketingClient.cs |
| 02 | chain-surfaces-order | Chain prints the dispatch/sibling order it read from Plane before executing | - | ChainPhase.cs, ParallelDispatcher.cs, ChainCommand.cs |
| 03 | sequence-tests | Declared deps round-trip to relations; chain orders by them | 01, 02 | ThroughlineBuild.Scaffold.Tests, ThroughlineBuild.Phases.Tests |

### Briefs - detail

#### Brief 01: scaffold-encodes-deps

**Goal.** Guarantee scaffold faithfully encodes the op-doc's declared sequence into Plane.

**Inputs.** The Dispatch-order `Depends on` column (plan-group level) and the Briefs-table `Deps` column (sibling level); the existing relation-creation path in scaffold; `OpDocValidator`.

**Outputs.** Scaffold creates a Plane `blocked_by` relation for every declared dependency at both levels, and emits a summary of the edges it created (count and the actual blocker -> blocked pairs). A declared dependency that references an unknown plan or brief is a validation error, not a silently dropped edge.

**Acceptance criteria.**
- [ ] Every `Depends on` and `Deps` entry in a parsed op-doc produces a corresponding `blocked_by` relation in Plane.
- [ ] Scaffold prints the created dependency edges so the operator can confirm the graph matches the op-doc.
- [ ] A `Deps` entry naming a brief or plan that does not exist fails validation with a clear message.

**Notes / gotchas.** The mechanism already exists (a prior run created 16 relations); this brief makes it total and visible. Cross-plan brief-to-brief dependencies are rare and the format is not being extended for them: when one occurs, keep the dependent brief in its dependency's plan, or order the plans so the dependency's plan completes first via the Dispatch `Depends on` edge. This brief encodes whatever the format declares; it does not invent edges the op-doc did not state.

**Out of scope.** Changing the op-doc format's expressiveness (a separate decision).

#### Brief 02: chain-surfaces-order

**Goal.** Make the order the chain will run visible before it runs.

**Inputs.** The sibling/dispatch graph the chain builds from Plane `blocked_by` relations (`BuildSiblingGraphAsync`, `TopologicalSorter.ComputeLevels`).

**Outputs.** Before executing, the chain prints the computed order (and, for a parent chain, the per-level grouping) read from Plane, so "about to run 322 before 321" is visible up front.

**Acceptance criteria.**
- [ ] A multi-ticket or parent chain prints the dependency-ordered sequence it derived from Plane before the first phase runs.
- [ ] Tickets with no `blocked_by` edge between them are shown as unordered relative to each other, making a missing edge obvious.

**Notes / gotchas.** This is the read-back half of the contract: it closes the loop by showing what the chain actually got from Plane, regardless of which upstream link failed.

**Out of scope.** Changing the ordering algorithm; this only surfaces it.

#### Brief 03: sequence-tests

**Goal.** Pin the contract at both ends.

**Inputs.** Briefs 01-02.

**Outputs.** Tests over scaffold encoding and chain ordering.

**Acceptance criteria.**
- [ ] A test asserts a sample op-doc's declared deps become the expected `blocked_by` relations.
- [ ] A test asserts the chain, given those relations, computes and runs the dependency-correct order.
- [ ] A test asserts a validation error when a `Deps` entry references an unknown brief.

**Notes / gotchas.** Scaffold and chain are tested separately; a full op-doc-to-execution round trip is not required in one test.

**Out of scope.** Plane API integration testing.

## What done looks like

Running `build chain <id> --from-brief` on an op-doc-scaffolded `Backlog` ticket takes it straight to Implement with no plan worker spawning and no plan `LlmCall` in the log; the description still holds exactly the op-doc plan, now stamped with `[planned_at]`. Without the flag, planning behaves as it always did.

A multi-ticket chain runs its tickets one at a time, in `blocked_by` dependency order, with never more than one worker alive at once, inside a single worktree created once at the start and removed once at the end. The `--max-parallel` flag is gone and there is one `TicketGraph` type. Each ticket still works on its own branch and ships independently into the target; ship still rebases as before.

The second and later tickets in a chain open their implement brief already listing the files the chain's prior tickets touched, with a one-line pointer to the commit range for anything deeper - no worker authored it, it was derived from the commits already in the checkout. A chain with nothing shipped yet adds nothing. Flipping the compile-time constant off returns the brief to byte-for-byte what it was before this operation.

No worker runs `git stash`, the verifier does not mutate git, and a chain that starts on a dirty, conflicted, or stash-polluted tree stops at the door with a message that names the offending files and any unrelated stash - rather than burning a full phase and failing opaquely at ship.

Every dependency the op-doc declares becomes a Plane `blocked_by` relation that scaffold reports as it creates it, and when a chain runs it first prints the dependency order it read back from Plane - so a dependent ticket runs after its dependency, and a missing edge is visible before any work starts rather than as a duplicate file three tickets later.