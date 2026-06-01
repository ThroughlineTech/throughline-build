# Operation: investigation-provenance

Make the sequential ticket chain cheap and correct. Skip the redundant plan worker when a ticket already carries its op-doc plan (A); drop in-chain parallelism and reuse one worktree per chain (B); point each ticket at the commits already in its checkout instead of a worker-authored summary (C); forbid the worker `git stash` surgery that has wedged real chains and add a fail-fast entry gate (D); and make the op-doc's declared sequence reach Plane as `blocked_by` relations the chain reads back (E). Together they stop the chain paying for the same investigation, or the same merge race, twice.

## Why this exists

A scaffolded brief-ticket lands in Backlog, so `build chain` routes it Backlog -> Plan and spawns a plan worker that re-investigates and rewrites a plan the op-doc already contains. That is wasted tokens and a fidelity leak: the worker can re-plan differently than the deliberately authored op-doc intended.

Parallel dispatch buys wall-clock at the cost of duplicated investigations on sibling tickets that share a code region, plus the merge-contention machinery (`MainWorktreeLock`, the divergence probe) that exists only because concurrent chains race on the shared worktree. For a solo operator paying per token, wall-clock is not the binding constraint. Once dispatch is sequential and each ticket ships before the next implements, the prior commits already sit in the next agent's checkout, so the handoff is a deterministic pointer to those commits, not a worker-authored prose digest.

Chain 319 wedged: a worker had stashed WIP during ticket 349, the repo-global stash stack carried it across worktree boundaries, a later apply conflicted onto main as `both modified: ShipPhase.cs`, and nothing resolved or aborted it, so every subsequent ship preflight failed. Worktree isolation did not help because the stash stack ignores it.

In the same run two sibling tickets, a loader and the verb that consumes it, executed in the wrong order, the dependent ahead of its dependency, and each independently created the same file. That is a sequence break, not a concurrency break: width-1 dispatch preserves the existing order but does not invent it, and the order is only as good as the `blocked_by` edges, which depend on the op-doc declaring them, scaffold encoding them into Plane, and the chain reading them back. Each link must hold and be visible.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | investigation-bypass | - | S |
| B | sequential-chain | - | M |
| C | handoff-addendum | B | M |
| D | worker-git-hygiene | - | M |
| E | sequence-contract | - | S |

A, B, D, and E are independent; C depends on B because the commit pointer only works once dispatch is sequential and each ticket ships before the next implements.

## Plan A: investigation-bypass

### Goal

After this plan, a Backlog ticket whose description already carries its op-doc plan can be promoted to Ready deterministically and enter the chain at Implement with no plan-worker investigation. The worker-investigation path remains the default; promotion is opt-in.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | plan-promote-path | Deterministic promote branch in PlanPhase: no worker, stamp marker, label, go to Ready | - | src/ThroughlineBuild.Phases/PlanPhase.cs, src/ThroughlineBuild.Cli/Config.cs |
| 02 | wire-promote-flag | CLI flag and config default selecting promote; chain honors it on a Backlog entry | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, src/ThroughlineBuild.Phases/ChainPhase.cs |
| 03 | promote-tests | Cover promotion and the preserved default | 02 | tests/ThroughlineBuild.Phases.Tests/ |

### Briefs - detail

#### Brief 01: plan-promote-path

Goal: PlanPhase gains a deterministic promotion path that turns a Backlog ticket already carrying its plan into a Ready ticket without spawning a worker, so an op-doc-authored ticket is not re-planned by an agent that might diverge from the authored intent.

Inputs: src/ThroughlineBuild.Phases/PlanPhase.cs read end-to-end (the existing fetch, parent-guard, Backlog state-guard, label-apply, marker-post, and transition steps); the main-SHA resolution via BaseRefResolver; the [planned_at] marker format.

Outputs:
- A promotion branch in PlanPhase that skips the worker run and PLAN_BODY resolution while reusing the existing guards and the label/marker/transition steps.
- A [planned_at: <currentMainSha>] marker posted at promotion time.
- Transition Backlog -> Planning -> Ready with no WorkerSpawn and no LlmCall emitted.
- The ticket description left unmodified.

Acceptance:
- [ ] A promoted ticket reaches Ready with no worker spawned
- [ ] The ticket description is byte-identical before and after promotion
- [ ] A [planned_at: <sha>] marker equal to the resolved main SHA is posted
- [ ] Promotion runs only when the explicit option is set; otherwise the worker plan runs

Notes: Stamping planned_at at promotion time exists so ImplementPhase's drift check keeps a baseline rather than silently losing one. The promotion is the WHAT/HOW split applied to planning: the op-doc already encodes the investigation, so re-running an investigating worker only adds cost and a chance of divergence from authored intent.

OOS:
- The CLI flag and config key (Brief 02 owns)
- Auto-detecting whether a description is already a plan
- Any change to ImplementPhase

#### Brief 02: wire-promote-flag

Goal: The promotion path is reachable from the CLI and honored by the chain, so a single invocation takes an op-doc Backlog ticket from promotion straight into the implement-review loop.

Inputs: Brief 01's promotion branch; the verb dispatch in src/ThroughlineBuild.Cli/Program.cs; the [plan] config section in Config.cs; ChainPhase's Backlog routing.

Outputs:
- A --from-brief flag on the plan and chain verbs.
- A [plan].mode config key with values investigate (default) and promote.
- ChainPhase routing a Backlog entry through promotion when selected, then continuing into the implement-review loop unchanged.
- Usage text documenting the flag and config key.

Acceptance:
- [ ] build chain <id> --from-brief on a Backlog op-doc ticket reaches Implement with no plan-worker spawn
- [ ] Without the flag and with default config, chain behavior is unchanged
- [ ] The flag and config key appear in usage output

Notes: The default stays investigate so non-op-doc Backlog tickets are unaffected. The flag overrides the config key when both are present, because an explicit per-run choice should win over a repo default.

OOS:
- The promotion behavior itself (Brief 01 owns)
- Changing where scaffold lands tickets
- Tests (Brief 03 owns)

#### Brief 03: promote-tests

Goal: The promotion contract and the preserved default are locked by tests so a later change cannot silently re-enable worker planning on a promoted ticket or break the no-op default.

Inputs: Briefs 01-02; the existing phase-test fakes for ticketing and events.

Outputs:
- A test asserting a promoted ticket emits no WorkerSpawn or LlmCall and ends in Ready.
- A test asserting the description is unchanged across promotion.
- A test asserting the [planned_at] marker equals the resolved main SHA.
- A test asserting default (no flag) still runs the worker plan.

Acceptance:
- [ ] Promotion emits no WorkerSpawn/LlmCall and ends in Ready
- [ ] Description is byte-identical across promotion
- [ ] [planned_at] marker equals the resolved main SHA
- [ ] Default path still runs the worker plan

Notes: The tests follow the existing reflection-disabled discipline used by the parser tests so they exercise AOT-relevant paths under the same switch the rest of the suite uses.

OOS:
- Implement-phase coverage beyond confirming the drift baseline is found
- Integration testing against a live Plane
- The CLI flag wiring (Brief 02 owns)

## Plan B: sequential-chain

### Goal

After this plan, a chain runs its tickets one at a time in dependency order with at most one worker alive, inside a single worktree created once at chain start and removed once at chain end. Per-ticket branches, per-ticket review diffs, and per-ticket shipping are preserved, and the parallelism surface is gone.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | width-1-dispatch | Force chain concurrency to 1 while preserving blocked_by ordering | - | src/ThroughlineBuild.Phases/ParallelDispatcher.cs, src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Cli/Config.cs |
| 05 | retire-parallel-surface | Remove --max-parallel, the parent-concurrency constant, and now-dead parallelism types | 04 | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, src/ThroughlineBuild.Phases/TicketGraph.cs |
| 06 | shared-chain-worktree | One worktree per chain, branch per ticket inside it, created and removed once | 05 | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Phases/ChainPhase.cs |
| 07 | sequential-tests | Sequential dependency-ordered execution and single-worktree reuse | 06 | tests/ThroughlineBuild.Phases.Tests/ |

### Briefs - detail

#### Brief 04: width-1-dispatch

Goal: The multi-ticket dispatch and parent-chain paths run tickets strictly one at a time while keeping the blocked_by topological order, so dependent work still follows its dependency but no two workers run at once.

Inputs: ParallelDispatcher's level-synchronous loop; ChainPhase.RunParentChainAsync; the workers.max_concurrency config and the MaxParentChainConcurrency constant; TopologicalSorter.ComputeLevels.

Outputs:
- Both dispatch paths executing with effective concurrency 1.
- The topological order preserved (levels are still computed and walked, just one ticket at a time).
- Ancestor-skip and the success/exit-code mapping unchanged.

Acceptance:
- [ ] No two worker subprocesses run concurrently within a chain
- [ ] A blocked ticket runs after its blocker; independent tickets run in input order
- [ ] --continue-past-failure and ancestor-skip behavior are preserved

Notes: Width is pinned to 1 rather than deleting the topological sort because the ordering is the load-bearing part and the concurrency was the disposable part. Running width-1 also removes the cross-worker worktree races that the merge-contention machinery existed to handle.

OOS:
- Removing the now-dead types (Brief 05 owns)
- Worktree reuse (Brief 06 owns)
- Tests (Brief 07 owns)

#### Brief 05: retire-parallel-surface

Goal: The parallelism surface area is removed now that width is 1, collapsing the sharpest orchestration seam in the tree (two TicketGraph types and a shadowed sequential dispatcher).

Inputs: Brief 04; the --max-parallel flag and ChainPhaseOptions.ForceParallel; MaxParentChainConcurrency; the two TicketGraph types; the shadowed SequentialChainDispatcher; MainWorktreeLock.

Outputs:
- --max-parallel and ForceParallel removed.
- MaxParentChainConcurrency removed.
- The unreachable second TicketGraph type and the shadowed SequentialChainDispatcher removed where no longer referenced.
- MainWorktreeLock removed if provably unreachable, else retained with a comment explaining the single remaining guard role.

Acceptance:
- [ ] The build has a single TicketGraph type
- [ ] --max-parallel no longer appears in usage or argument parsing
- [ ] The solution builds and existing tests pass after the removals
- [ ] AOT publish succeeds

Notes: Only code that becomes unreachable after width-1 is removed; anything with a live caller is left in place, because a removal that breaks an active path would trade a cleanup for a regression. The ShipPhase divergence and rebase logic is retained, since sequential chains still rebase onto the target.

OOS:
- Width-1 behavior (Brief 04 owns)
- Worktree reuse (Brief 06 owns)
- The divergence and rebase logic in ShipPhase

#### Brief 06: shared-chain-worktree

Goal: A chain uses one worktree for its whole run, reused across tickets, instead of adding and tearing one down per ticket, while each ticket still works on its own branch and ships independently.

Inputs: Brief 05; the per-ticket git worktree add in ImplementPhase; WorktreeDecrufter; PhaseWorktreeLayout; ShipPhase's worktree-locate and merge steps.

Outputs:
- The chain creating one worktree at start and removing it once at end.
- Each ticket checking out its own ticket/<slug> branch off the current target head inside that worktree, implementing and committing there.
- Per-ticket review diff (target..ticket/<slug>) and per-ticket ship (rebase plus ff-merge into target) unchanged.
- The reused worktree verified clean before each ticket starts, deferring to Plan D's entry gate when present.
- Single-ticket build implement (no chain) still creating its own worktree.

Acceptance:
- [ ] A chain of N tickets creates one worktree and removes it once, not N add/remove cycles
- [ ] Each ticket works on its own branch; per-ticket review diff and ship are unchanged
- [ ] A failed ticket leaves its branch isolated; already-shipped siblings are unaffected
- [ ] AOT publish succeeds

Notes: There is deliberately no single-shared-branch parent mode; children keep independent branches and independent ship so a failed child's commits stay isolated rather than intermingled on a shared branch. The clean-before-each-ticket check overlaps Plan D's entry gate, so when Plan D is present the worktree reuse defers to it rather than building a second gate.

OOS:
- A single shared branch for a parent ticket
- Removing worktrees entirely
- Tests (Brief 07 owns)

#### Brief 07: sequential-tests

Goal: Sequential dependency-ordered execution and single-worktree reuse are pinned by tests so a future change cannot reintroduce concurrency or per-ticket worktree churn unnoticed.

Inputs: Briefs 04-06; the dispatcher and git test fakes.

Outputs:
- A test asserting a multi-ticket chain dispatches sequentially in dependency order.
- A test asserting ancestor-skip still fires on an upstream failure and the single-ticket exit-code mapping is unchanged.
- A test asserting a multi-ticket chain creates and removes exactly one worktree.

Acceptance:
- [ ] A multi-ticket chain dispatches sequentially in dependency order
- [ ] Ancestor-skip fires on upstream failure; single-ticket exit codes unchanged
- [ ] A multi-ticket chain creates and removes exactly one worktree

Notes: The git fakes simulate worktree add and remove so the single-worktree assertion does not require a real repository; this keeps the test hermetic and fast, matching the existing phase-test approach.

OOS:
- Performance or timing assertions
- Live Plane integration
- Real git subprocess execution

## Plan C: handoff-addendum

### Goal

After this plan, the second and later tickets in a chain receive a deterministic pointer to the commits the chain has already produced (their cumulative touched-files and the commit range), folded into the implement brief, so an agent re-greps less and does not recreate a sibling's file. Nothing is authored by a worker, and the whole feature can be compiled out.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 08 | derive-chain-commits | Compute the chain's commit range and cumulative diff --stat from data the chain already holds | B | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Helpers/ChainCommitRange.cs, src/ThroughlineBuild.Contracts/IGitClient.cs |
| 09 | inject-pointer | Fold touched-files into RelevantFiles and a pointer line into Context for the next implement brief | 08 | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs |
| 10 | handoff-compile-flag | Compile-time constant gating injection; disabled yields a baseline-identical brief | 09 | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs |
| 11 | handoff-tests | Pointer threading, empty-range no-op, compile-disabled baseline | 10 | tests/ThroughlineBuild.Phases.Tests/ |

### Briefs - detail

#### Brief 08: derive-chain-commits

Goal: A helper deterministically computes what the chain has done so far (the commit range from chain start to the current target head, plus the cumulative touched-files) without any worker or LLM call, giving the pointer its content.

Inputs: The target-head SHA captured at ChainStart; the current target head; the per-ticket [implemented_at] and [shipped_at] markers the chain already parses; IGitClient (it already exposes LogShas).

Outputs:
- A ChainCommitRange helper returning the commit range, the commit count, and the cumulative git diff --stat touched-files for that range.
- A diff --stat method added to IGitClient if one is not already present.
- The chain capturing the target head SHA once at ChainStart.

Acceptance:
- [ ] For a chain that has shipped M tickets, the helper returns the range bounding exactly those commits and their touched-files
- [ ] At the first ticket of a chain, the range and touched-files are empty
- [ ] The computation makes no LLM call and spawns no worker
- [ ] AOT publish succeeds

Notes: In the shared-worktree, ship-per-ticket model each shipped ticket advances the target head, so chainStart..currentHead is exactly the chain's prior work and needs no separate bookkeeping. The range is anchored once at ChainStart because deriving it per-ticket from markers alone would be more fragile than reading the branch.

OOS:
- Authoring any prose summary
- Injecting the pointer into a brief (Brief 09 owns)
- A dedicated handoff event (markers and git history already record this)

#### Brief 09: inject-pointer

Goal: The next ticket's implement brief opens already listing the files the chain's prior tickets touched and a one-line pointer to the commit range, so the agent uses existing work instead of rediscovering or recreating it.

Inputs: Brief 08's range and touched-files; ImplementPhaseOptions; ImplementBriefBuilder; the Brief record's RelevantFiles and Context fields.

Outputs:
- The chain passing the derived touched-files and range on ImplementPhaseOptions.
- ImplementBriefBuilder folding the touched-files into RelevantFiles (deduped) and a single bounded line into Context.
- An empty range producing a brief identical to the no-pointer case.

Acceptance:
- [ ] In a two-ticket chain, the second ticket's brief lists the first ticket's touched-files and the range pointer
- [ ] An empty range leaves the brief unchanged, with no empty headers or stray context
- [ ] A file touched by several prior tickets appears once

Notes: The file list is the cheap, grep-killing half and the range pointer is a lazy reference for anything deeper, so the carried text stays bounded and does not regrow the unbounded-context cost the pointer is meant to avoid. The same file list is content-level coordination: a later ticket that sees a sibling already created a file uses it rather than recreating it, which is the antidote to the duplicate-file collision out-of-order siblings produced. It is effective only when the order is right, which is why Plan E exists.

OOS:
- Carrying full diffs into the brief
- Capturing dead-ends or negative findings not present in commits
- The compile-time gate (Brief 10 owns)

#### Brief 10: handoff-compile-flag

Goal: The pointer feature can be turned off at compile time, so an operator who does not want it pays nothing and gets a brief identical to the pre-Plan-C baseline.

Inputs: Brief 09's injection path.

Outputs:
- A compile-time constant (a const bool or a DefineConstants symbol with #if) gating injection, defaulting on.
- With the constant off, the next ticket's implement brief byte-identical to a pre-Plan-C baseline.

Acceptance:
- [ ] With the constant false, the next ticket's brief is byte-identical to a no-pointer baseline
- [ ] The off-switch is compile-time, not a runtime config key or flag
- [ ] With the constant true (default), Brief 09 behavior holds

Notes: A single well-named constant in one place is chosen over scattered guards because the goal is a clean, auditable off-switch rather than per-call conditionals. Gating the consumption side is the meaningful switch since that is where the brief-token cost lands.

OOS:
- Per-ticket or per-repo runtime toggles
- Removing the derivation when disabled (it is cheap and harmless)
- Tests (Brief 11 owns)

#### Brief 11: handoff-tests

Goal: The pointer, the empty-range no-op, and the compile-time off-switch are pinned by tests so the feature cannot silently change the brief or fail to disable.

Inputs: Briefs 08-10.

Outputs:
- A test asserting a two-ticket chain threads ticket 1's touched-files and range into ticket 2's brief.
- A test asserting an empty range yields an unchanged brief.
- A test or build variant asserting the disabled build produces the baseline brief.

Acceptance:
- [ ] A two-ticket chain threads the first ticket's touched-files and range into the second brief
- [ ] An empty range yields an unchanged brief
- [ ] The disabled build produces the baseline brief

Notes: The disabled-build assertion may require a separate build configuration or direct exercise of the gated path, depending on how the constant is implemented; the test approach follows whichever the implementation chooses.

OOS:
- Token-cost measurement
- Integration testing against live git history
- The derivation helper internals (Brief 08 owns)

## Plan D: worker-git-hygiene

### Goal

After this plan, no worker uses git stash, the verifier does not mutate git, and a chain that starts on a dirty, conflicted, or stash-polluted tree stops at phase entry with a message naming the offending files and any unrelated stash, instead of running a full phase and failing opaquely at ship.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 12 | no-worker-stash | Implement and review templates across all four agents forbid git stash | - | src/ThroughlineBuild.Briefs/Templates/{claude-code,codex,gemini,copilot}/{implement,review}.md |
| 13 | read-only-verifier | Review posture forbids git mutation; verifier leans on the deterministic diff and AutomatedChecksRunner | 12 | src/ThroughlineBuild.Briefs/Templates/{claude-code,codex,gemini,copilot}/review.md, src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs |
| 14 | hygiene-entry-gate | Phase entry and ship preflight detect dirty/unmerged/stash-polluted state and stop with an attributed reason | 13 | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Contracts/IGitClient.cs |
| 15 | hygiene-tests | Gate fires on conflicted/dirty/stash state; clean tree passes | 14 | tests/ThroughlineBuild.Phases.Tests/ |

### Briefs - detail

#### Brief 12: no-worker-stash

Goal: The implement and review templates for every agent instruct the worker never to use git stash or the shared stash stack, removing the practice that stashed WIP and leaked it across worktrees.

Inputs: The four per-agent implement.md and review.md templates under src/ThroughlineBuild.Briefs/Templates/.

Outputs:
- A no-stash instruction in all four implement templates and all four review templates.
- The instruction naming the failure mode (the stash stack is repo-global and leaks across worktrees) so the worker understands the constraint.
- A stated alternative: if a clean build is needed, build in place rather than stashing.

Acceptance:
- [ ] All four implement templates and all four review templates carry the no-stash instruction
- [ ] The instruction names the repo-global stash-leak failure mode

Notes: This is prevention by instruction and cannot hard-block a worker that ignores it, which is why Brief 14 is the runtime backstop. The wording must be kept in sync across all eight files by hand, since nothing enforces cross-template consistency.

OOS:
- Hard sandboxing of git (not feasible without heavier measures)
- Plan, draft, and decompose templates
- The runtime gate (Brief 14 owns)

#### Brief 13: read-only-verifier

Goal: Review cannot mutate git state; the verifier reaches its verdict from the deterministic diff and the orchestrator-run checks rather than freelancing stash and build cycles in the worktree.

Inputs: The review templates; WorkerAgentReviewer; ReviewPhase's deterministic diff synthesis and AutomatedChecksRunner.

Outputs:
- Review templates stating the verifier is read-only with respect to git (no stash, checkout, reset, or rebase).
- The verifier relying on the synthesized diff and AutomatedChecksRunner for its verdict.

Acceptance:
- [ ] The review templates state the verifier is read-only with respect to git
- [ ] Review produces its verdict from the synthesized diff and automated checks without mutating the tree

Notes: Review already receives a deterministically synthesized diff, so it has no legitimate need to touch git; the confused stash-and-build spelunking seen in the wedged run was the verifier doing work it was never supposed to do.

OOS:
- Changing which automated checks run
- The no-stash instruction in implement templates (Brief 12 owns)
- The entry gate (Brief 14 owns)

#### Brief 14: hygiene-entry-gate

Goal: A poisoned working tree is caught at phase entry and at ship preflight with a precise, attributed message, so an orphaned conflict or stash fails fast and obviously instead of after a full plan-implement-review that then dies at ship.

Inputs: git status --porcelain (it surfaces conflict codes such as UU and AA); git stash list; the existing ship clean-check.

Outputs:
- Phase entry refusing to proceed when the tree has unmerged or conflicted paths, naming them.
- Detection and reporting of a dangling stash that belongs to another ticket.
- A stop message distinguishing orphaned state (not from this ticket) from this ticket's expected changes.
- The ship preflight reporting the same precise state (conflict in X, Y; stash from ticket/Z, unrelated) instead of "N modified tracked files".

Acceptance:
- [ ] A phase started on a tree with unmerged paths stops immediately, naming the conflicted files
- [ ] A dangling stash from an unrelated ticket is detected and named
- [ ] The ship preflight identifies conflict state and unrelated stashes precisely
- [ ] A clean tree passes the gate with no behavior change
- [ ] AOT publish succeeds

Notes: The gate detects and stops rather than auto-cleaning, because automatically dropping a stash or resetting a conflict could destroy real WIP; the safe action is to surface the state and let the operator decide. This gate is also the cleanliness check the shared chain worktree relies on.

OOS:
- Automatic recovery or stash cleanup
- The template instructions (Briefs 12 and 13 own)
- Tests (Brief 15 owns)

#### Brief 15: hygiene-tests

Goal: The entry gate and preflight messaging are pinned by tests so a clean tree keeps passing and a conflicted or stash-polluted tree keeps being caught.

Inputs: Briefs 12-14; git test fakes that can simulate conflict and stash states.

Outputs:
- A test asserting a phase started on a conflicted tree stops and names the files.
- A test asserting a dangling unrelated stash is reported.
- A test asserting a clean tree passes unchanged.

Acceptance:
- [ ] A phase on a conflicted tree stops and names the files
- [ ] A dangling unrelated stash is reported
- [ ] A clean tree passes unchanged

Notes: The git fakes simulate conflict and stash states so the gate is exercised without a real repository, keeping the tests hermetic in line with the existing phase-test approach.

OOS:
- Asserting the template prose (it is not code)
- Live git integration
- Automatic recovery behavior (not built)

## Plan E: sequence-contract

### Goal

After this plan, every dependency the op-doc declares becomes a Plane blocked_by relation that scaffold reports as it creates it, and a chain prints the dependency order it read back from Plane before running, so a dependent ticket runs after its dependency and a missing edge is visible before any work starts.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 16 | scaffold-encodes-deps | Scaffold translates every declared dependency into a Plane blocked_by relation and reports the edges | - | src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs, src/ThroughlineBuild.Scaffold/OpDocParser.cs, src/ThroughlineBuild.Scaffold/OpDocValidator.cs |
| 17 | chain-surfaces-order | Chain prints the dispatch/sibling order it read from Plane before executing | 16 | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Phases/ParallelDispatcher.cs, src/ThroughlineBuild.Commands/ChainCommand.cs |
| 18 | sequence-tests | Declared deps round-trip to relations; chain orders by them | 17 | tests/ThroughlineBuild.Scaffold.Tests/, tests/ThroughlineBuild.Phases.Tests/ |

### Briefs - detail

#### Brief 16: scaffold-encodes-deps

Goal: Scaffold faithfully encodes the op-doc's declared sequence into Plane, creating a blocked_by relation for every declared dependency and reporting the edges, so an under-declared op-doc shows as zero edges instead of silently losing ordering.

Inputs: The Dispatch-order Depends-on column (plan level) and the Briefs-table Deps column (sibling level); the existing relation-creation path in ScaffoldPhase; OpDocValidator.

Outputs:
- A Plane blocked_by relation for every declared dependency at both the plan and brief level.
- A printed summary of the created edges (count and blocker -> blocked pairs).
- A validation error when a Deps entry references an unknown plan or brief, rather than a silently dropped edge.

Acceptance:
- [ ] Every Depends-on and Deps entry produces a corresponding blocked_by relation in Plane
- [ ] Scaffold prints the created dependency edges
- [ ] A Deps entry naming a nonexistent brief or plan fails validation with a clear message
- [ ] AOT publish succeeds

Notes: The relation-creation mechanism already exists (a prior run created sixteen relations); this brief makes it total and visible rather than inventing it. Cross-plan brief-to-brief dependencies are rare and the format is not extended for them; when one occurs the dependent brief is kept in its dependency's plan, or the plans are ordered so the dependency's plan completes first.

OOS:
- Extending the op-doc format's expressiveness
- The chain-side order surfacing (Brief 17 owns)
- Tests (Brief 18 owns)

#### Brief 17: chain-surfaces-order

Goal: The chain prints the dependency order it derived from Plane before executing, so a wrong or missing edge (a dependent ahead of its dependency) is visible up front rather than discovered as duplicate work several tickets later.

Inputs: The sibling and dispatch graph the chain builds from Plane blocked_by relations (BuildSiblingGraphAsync, TopologicalSorter.ComputeLevels).

Outputs:
- The chain printing the computed order, and for a parent chain the per-level grouping, before the first phase runs.
- Tickets with no edge between them shown as unordered relative to each other, making a missing edge obvious.

Acceptance:
- [ ] A multi-ticket or parent chain prints the dependency-ordered sequence derived from Plane before the first phase runs
- [ ] Tickets with no blocked_by edge between them are shown as unordered relative to each other

Notes: This is the read-back half of the sequence contract; it closes the loop by showing what the chain actually got from Plane, so a break is attributable regardless of which upstream link failed. It surfaces the order without changing the ordering algorithm.

OOS:
- Changing the ordering algorithm
- The scaffold encoding (Brief 16 owns)
- Tests (Brief 18 owns)

#### Brief 18: sequence-tests

Goal: The sequence contract is pinned at both ends so declared deps keep becoming relations and the chain keeps ordering by them.

Inputs: Briefs 16-17.

Outputs:
- A test asserting a sample op-doc's declared deps become the expected blocked_by relations.
- A test asserting the chain, given those relations, computes and runs the dependency-correct order.
- A test asserting a validation error when a Deps entry references an unknown brief.

Acceptance:
- [ ] A sample op-doc's declared deps become the expected blocked_by relations
- [ ] The chain computes and runs the dependency-correct order given those relations
- [ ] A Deps entry referencing an unknown brief raises a validation error

Notes: Scaffold and chain are tested separately because a full op-doc-to-execution round trip would require a live Plane; the two halves together still pin the contract.

OOS:
- Plane API integration testing
- Round-trip testing through a live backend
- The format-expressiveness question (decided: not extended)

## What done looks like

A `build chain` run on an op-doc-scaffolded backlog ticket goes straight to Implement when invoked with --from-brief, with no plan worker spawned and the authored plan left intact; the chain then runs its tickets one at a time in the dependency order it prints up front, inside a single worktree, with each ticket shipping into the target before the next implements. The second and later tickets open their implement brief already listing the files prior tickets touched, so the agent does not rediscover or recreate them, and turning off one compile-time constant returns those briefs to exactly what they were before. No worker stashes, the verifier never mutates git, and a chain that starts on a dirty, conflicted, or stash-polluted tree stops at the door naming the offending files and any unrelated stash rather than burning a phase and failing at ship.