# Operation: batch-implement

Add an opt-in batch-implement path to chains for cohesive sibling groups, where several tickets are really one design split into bookkeeping units. One warm implement worker holds the full design across a single session, commits once per ticket so history stays bisectable, and returns a structured per-ticket result the conductor verifies against git before posting the usual per-ticket markers and Plane transitions. Review runs once over the combined stack. It is an optimization for groups where repeated cold starts dominate cost, not a replacement for isolated per-ticket implementation.

## Why this exists

The help-system chain TLB-419..422 was four tightly coupled tickets - model and registry, two sibling renderers, then factory and dispatch wiring - run as a fresh implement worker per ticket. Because workers are stateless and cold-boot, each child re-read the same growing implementation context from scratch. TLB-422 alone read 2.4M cache tokens on top of three prior commits; the run logged about 7.9M cache-read tokens total, with roughly 91 percent of cost in implement, and paid for five separate review passes over one conceptual feature.

This is the wrong shape for cohesive chains. The useful isolation is the per-ticket commit boundary, state transition, and reviewable history. The expensive part is repeatedly re-priming a worker that needs the same design in memory. `ChainPhase.RunImplementReviewLoopAsync` mints a new implement session per round, and `RunParentChainAsync` dispatches one child at a time so each ships into the local target before the next resolves its base - predictable stacking, but cold-start-per-child by construction. The substrate for sharing already exists: parent chains create one shared worktree on a placeholder `chain/{slug}` branch and clean up once at chain end. What is missing is a shared implement session, not a shared filesystem lane.

The cost is concentrated and recurring: every cohesive multi-ticket feature pays the same re-priming tax, and the help-system chain is a representative case rather than an outlier. Building the opt-in path now, while the shared-worktree machinery and per-ticket marker contract are already in place, keeps the change additive and lets the conductor stay a deterministic, file- and git-mediated orchestrator rather than growing hidden session state.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Batch contract and brief | - | M |
| B    | Batch execution and per-ticket attribution | A | L |
| C    | Combined review and rework routing | B | M |

A defines the selection input, the batch brief, and the per-ticket result contract that everything else consumes. B is the core conductor change and carries the most blast radius in `ChainPhase`. C layers review and rework on top of a working batch run. Build A, then B, then C.

## Plan A: Batch contract and brief

### Goal

After this plan, a cohesive sibling group can be declared explicitly and described to one worker, and that worker has a defined structured result to return. `build chain` accepts an opt-in batch selection, the system can render a single batch brief covering every selected ticket with ordering constraints and the chain commit pointer, and a per-ticket result type exists and round-trips through the source-generated JSON context. No execution behavior changes yet; this plan only establishes the inputs and the contract.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | batch-result-contract | Per-ticket structured result type plus JSON-context registration | - | src/ThroughlineBuild.Contracts/Models/WorkerResult.cs, src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs |
| 02 | batch-selection-flag | Opt-in batch group via a build chain flag, plumbed into ChainPhaseOptions | - | src/ThroughlineBuild.Commands/ChainCommand.cs, src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Phases/ChainPhase.cs |
| 03 | batch-implement-brief | Render one brief covering all selected tickets, order, and commit pointer | 01, 02 | src/ThroughlineBuild.Briefs/, src/ThroughlineBuild.Phases/ImplementPhase.cs |

### Briefs - detail

#### Brief 01: batch-result-contract

**Goal:** One warm worker can report a typed, per-ticket result instead of a single commit sha. After this brief a result type carries an ordered list of per-ticket entries (ticket id, commit sha, stack position, files changed, summary reference), and that type serializes and deserializes through the source-generated JSON context with reflection disabled. This is the contract every later brief verifies against.

**Inputs:**
- src/ThroughlineBuild.Contracts/Models/WorkerResult.cs - the current single-result shape (Status, Summary, FilesChanged, FailureReason, Metadata, Blocks).
- src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs and its WorkersCommonJsonContext partial (line 37) - how worker JSON is parsed under AOT.
- tests/AGENTS.md - the note on disabling reflection in parser tests so the AOT path is actually exercised.

**Outputs:**
- A new record (for example BatchTicketResult) in src/ThroughlineBuild.Contracts/Models with ticket id, commit sha, stack position, files changed, and summary reference fields.
- A batch-shaped result the worker can return: an ordered IReadOnlyList of per-ticket entries, either as a sibling record or an extension of the existing result envelope.
- Registration of the new type(s) in WorkersCommonJsonContext so they round-trip without reflection.
- Parser support in WorkerResultParser for the per-ticket array, with a clear failure when the array is missing or malformed.

**Acceptance:**
- [ ] A per-ticket result type exists carrying ticket id, commit sha, stack position, files changed, and summary reference.
- [ ] The type round-trips through the source-generated JSON context with reflection disabled.
- [ ] Parsing a well-formed batch payload yields entries in declared order.
- [ ] Parsing a payload with a missing or malformed per-ticket array fails with a clear, surfaced reason rather than a silent empty list.
- [ ] AOT publish succeeds.

**Notes:** The per-ticket result is the load-bearing contract for the whole operation, so it lives in Contracts (pure, leaf, no I/O per that project's rule) and is registered in the worker JSON context rather than parsed ad hoc. Carrying the entries as a typed list rather than stuffing them into the existing Metadata dictionary is deliberate: the conductor must verify each entry against git, and an untyped bag would push that validation into stringly-typed code. The existing single-result WorkerResult stays intact so standalone build implement is unaffected.

**OOS:**
- Conductor-side verification of the reported shas (Brief 05).
- Producing the batch payload from a real worker session (Brief 04).
- Any change to the single-result path used by standalone implement.

#### Brief 02: batch-selection-flag

**Goal:** An operator can opt a specific sibling group into one batch from the command line, and that selection reaches the chain conductor as structured data. After this brief, `build chain TLB-418 --batch-implement TLB-419,TLB-420,TLB-421,TLB-422` parses into an ordered ticket group on ChainPhaseOptions, and a chain run with no such flag behaves exactly as today.

**Inputs:**
- src/ThroughlineBuild.Commands/ChainCommand.cs lines 37-41 - the flag-extraction pattern for --debug and --no-auto-resolve.
- src/ThroughlineBuild.Cli/Program.cs - the chain verb branch near line 1418.
- src/ThroughlineBuild.Phases/ChainPhase.cs lines 11-17 - the ChainPhaseOptions record.

**Outputs:**
- A --batch-implement flag on build chain accepting a comma-separated ticket id list.
- Validation that the listed tickets are siblings or explicitly co-selected, with a clear error on an empty or malformed list.
- A new optional field on ChainPhaseOptions carrying the ordered batch group.
- Help text for the flag in the chain command help registry.

**Acceptance:**
- [ ] build chain with the batch flag and a ticket list parses into an ordered group on the chain options.
- [ ] build chain without the flag is byte-for-byte unchanged in behavior.
- [ ] A malformed or empty ticket list is rejected with a clear operator-facing message.
- [ ] The flag appears in build chain help output.

**Notes:** Explicit declaration is the conservative first step the design calls for: an operator names the group, the tool does not infer it. The flag carries the group rather than a parent label so the first version has no dependency on Plane label conventions; a parent-ticket label opt-in can layer on later without changing the conductor contract. The flag lives on chain because the shared worktree and serialized dispatch it needs already exist only on the chain path.

**OOS:**
- Parent-label or description-driven batch declaration.
- Automatic cohesion detection from sibling status.
- Executing the batch (Plan B).

#### Brief 03: batch-implement-brief

**Goal:** The system can describe a whole declared group to one worker in a single brief. After this brief, given a batch group and the chain commit pointer, the system renders one brief that contains every selected ticket's description, the required ordering, the current base commit, and the per-ticket output contract from Brief 01, so a worker reading it knows exactly what to build, in what order, and what to return.

**Inputs:**
- src/ThroughlineBuild.Briefs/ - existing brief templates, the renderer, and the snapshot test layout.
- src/ThroughlineBuild.Phases/ImplementPhase.cs - how the per-ticket brief is constructed today.
- Brief 01's per-ticket result contract - for the output-contract section.
- src/ThroughlineBuild.Phases/ChainPhase.cs line 352 - ChainCommitRange usage for the base commit pointer.

**Outputs:**
- A batch brief renderer that takes the ordered group plus the chain commit pointer and emits one brief.
- A section enumerating each ticket with its description and intra-group ordering constraints.
- An explicit output-contract section telling the worker to commit once per ticket and return the per-ticket result array from Brief 01.
- A snapshot test fixture for the rendered batch brief.

**Acceptance:**
- [ ] Given a multi-ticket group, the renderer emits one brief listing every ticket in declared order.
- [ ] The brief states the commit-once-per-ticket requirement and the structured return shape.
- [ ] The brief includes the current base commit pointer for the group.
- [ ] A snapshot test pins the rendered batch brief.

**Notes:** Rendering lives alongside the existing brief templates so the batch brief shares formatting and snapshot discipline (a template change requires a snapshot update, per the Briefs test rules). The brief is the only place the worker learns the output contract, so it must name the per-ticket fields explicitly rather than assume the worker infers them; the conductor will reject a result that does not match. Keeping ordering constraints in the brief, not in conductor heuristics, preserves the deterministic-conductor invariant.

**OOS:**
- Spawning the worker with this brief (Brief 04).
- Combined review brief formatting (Plan C).
- Cohesion detection or group auto-assembly.

## Plan B: Batch execution and per-ticket attribution

### Goal

After this plan, a declared batch group runs as one warm implement session in the shared chain worktree and lands as a clean, bisectable stack with full per-ticket attribution. The conductor spawns a single worker with the batch brief, confirms the worktree is clean and that each reported commit exists in declared order against git, writes the existing per-ticket implemented_at marker and summary for each child, and transitions each ticket Ready -> InProgress -> InReview. A partial failure leaves completed children correctly marked and the failing child recoverable.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | warm-batch-session | Spawn one implement worker with the batch brief in the shared worktree | - | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs |
| 05 | commit-attribution-verify | Confirm clean worktree and each reported commit against git in order | 04 | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Git/ |
| 06 | per-ticket-markers | Post implemented_at marker and summary, then transition each child | 05 | src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/CommentMarkers.cs |
| 07 | partial-failure-recovery | Commit-after-each contract; mark done children, leave failing child recoverable | 05 | src/ThroughlineBuild.Phases/ChainPhase.cs |

### Briefs - detail

#### Brief 04: warm-batch-session

**Goal:** A declared batch group is built by one worker in one session instead of one worker per ticket. After this brief the chain conductor, when a batch group is present, creates or reuses the shared chain worktree and spawns a single implement worker with the Plan A batch brief, and that session produces commits and a per-ticket result array. Per-ticket cold starts are gone for the group.

**Inputs:**
- src/ThroughlineBuild.Phases/ChainPhase.cs - RunImplementReviewLoopAsync (per-round session minting) and RunParentChainAsync (shared worktree on chain/{slug}, around line 779).
- src/ThroughlineBuild.Phases/ImplementPhase.cs line 15 - ImplementPhaseOptions and SharedWorktreePath.
- Brief 03's batch brief and Brief 01's result contract.

**Outputs:**
- A batch branch in the conductor that, when a batch group is set, runs one implement session over the group inside the shared worktree.
- Reuse of the existing chain/{slug} shared worktree and placeholder branch rather than a new workspace layout.
- A single worker invocation carrying the batch brief and returning the per-ticket result array.
- The non-batch chain path left on its existing per-ticket loop.

**Acceptance:**
- [ ] A chain run with a batch group spawns exactly one worker session for the whole group.
- [ ] The session runs inside the existing shared chain worktree, not a new workspace.
- [ ] A chain run without a batch group still spawns one worker per ticket as today.
- [ ] The session returns the per-ticket result array defined in Plan A.

**Notes:** The conductor gains a batch branch but stays a conductor: it chooses one execution unit and still mediates everything through the shared worktree, git, and the returned result rather than through session memory. Reusing chain/{slug} keeps cleanup, branch naming, and resume anchored in existing code, per the shared-worktree invariant. Standalone build implement and the non-batch chain loop are untouched so the change is purely additive and the fallback path is the proven one.

**OOS:**
- Verifying the returned commits (Brief 05).
- Posting markers and transitions (Brief 06).
- Parallel execution of multiple batches.

#### Brief 05: commit-attribution-verify

**Goal:** The conductor trusts git, not the worker's self-report. After this brief, once a batch session returns, the conductor confirms the shared worktree is clean and that each reported commit sha actually exists and appears in the declared per-ticket order in the branch history, mapping each commit to its ticket. A worker that misreports shas, skips a commit, or leaves the tree dirty is caught before any marker is posted.

**Inputs:**
- src/ThroughlineBuild.Phases/ImplementPhase.cs lines 390-394 - single-commit checking against worktree HEAD and the implemented_at write.
- src/ThroughlineBuild.Git/ - the git client surface for commit existence and ancestry checks.
- src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs - the existing worktree-cleanliness gate.

**Outputs:**
- A step that confirms the worktree is clean after the batch session.
- A check that each reported commit sha exists in the branch and that the set appears in declared order.
- A ticket-to-commit mapping built from confirmed git state, not from reported values alone.
- A clear, structured failure when the check does not hold, naming the first ticket that fails.

**Acceptance:**
- [ ] A batch result whose reported commits all exist in declared order is accepted and produces a ticket-to-commit mapping.
- [ ] A result naming a commit absent from the branch is rejected before any marker is posted.
- [ ] A result whose commits are out of declared order is rejected.
- [ ] A dirty worktree after the session is rejected with a clear reason.

**Notes:** Confirming against git is the core integrity property the design insists on: per-ticket attribution from one session is only safe if the conductor reconstructs it from commits rather than trusting the session. Order is checked, not just existence, because the bisectable-stack promise depends on TLB-419 preceding TLB-420 and so on. Failing closed before markers are posted keeps Plane state honest and makes partial failure (Brief 07) tractable.

**OOS:**
- Posting the markers once the commits are confirmed (Brief 06).
- Deciding recovery for a failed check mid-stack (Brief 07).
- Rewriting or reordering commits to satisfy the contract.

#### Brief 06: per-ticket-markers

**Goal:** Every child of a confirmed batch ends in the same observable state a per-ticket run would have produced. After this brief, for each confirmed commit the conductor writes the existing [implemented_at: <sha>] comment with the branch name and rendered summary, and transitions that ticket Ready -> InProgress -> InReview, so downstream review and ship read the batch stack through exactly the same markers and states as today.

**Inputs:**
- src/ThroughlineBuild.Phases/ImplementPhase.cs lines 390-394 - the implemented_at marker HTML shape and its branch and summary content.
- src/ThroughlineBuild.Phases/CommentMarkers.cs - marker parsing and the newest-first staleness note.
- Brief 05's confirmed ticket-to-commit mapping.

**Outputs:**
- Per-child posting of the existing implemented_at marker shape, one per confirmed commit, with branch and summary.
- Optional extra batch metadata on the marker (for example group id, stack position) that extends rather than replaces the existing shape.
- Per-child state transitions Ready -> InProgress -> InReview.
- Reuse of the existing marker writer so single and batch paths emit identical marker syntax.

**Acceptance:**
- [ ] Each child of a confirmed batch carries one commit-attribution marker linking it to its own commit sha and branch.
- [ ] Each child ends in InReview after a successful batch.
- [ ] The marker syntax is identical to a single-ticket run, with any batch fields added rather than substituted.
- [ ] ReviewPhase can reconstruct each child's commit from its marker exactly as for a single-ticket run.

**Notes:** The implemented_at comment is the authoritative Plane-to-git link, so batch mode extends its metadata instead of inventing a parallel marker; anything else would split the source of truth that review and ship already depend on. Transitions stay per child so per-ticket observability and later per-ticket ship behavior are preserved. Reusing the existing writer avoids drift between the single and batch marker formats.

**OOS:**
- Combined review over the stack (Plan C).
- Cross-ticket rework routing (Plan C).
- Changing Plane's underlying state model.

#### Brief 07: partial-failure-recovery

**Goal:** A batch that fails halfway leaves a clean, recoverable boundary instead of an ambiguous half-state. After this brief the batch contract requires the worker to commit after each completed ticket and stop before starting the next if the design becomes blocked; the conductor then marks every completed-and-confirmed child normally and leaves the failing child InProgress with the failure reason, so a resumed run can rebuild the completed boundary from git and Plane comments.

**Inputs:**
- Brief 05's per-commit checking and Brief 06's marker and transition logic.
- src/ThroughlineBuild.Phases/ChainPhase.cs - how a failure reason is recorded and a ticket left InProgress today.
- src/ThroughlineBuild.Phases/AGENTS.md - the resume note on reconstructing in-progress tickets from the event log.

**Outputs:**
- A contract, stated in the batch brief and enforced by the conductor, that completed tickets are committed before a later ticket is attempted.
- Conductor handling that marks completed-and-confirmed children and stops at the first incomplete ticket.
- The failing child left InProgress with a recorded, operator-readable failure reason.
- A resume path that reconstructs the completed-ticket boundary from git commits and posted markers.

**Acceptance:**
- [ ] When a batch stops partway, every completed-and-confirmed child carries its marker and reaches InReview.
- [ ] The first incomplete ticket is left InProgress with a recorded failure reason.
- [ ] No child past the failure point is marked or transitioned.
- [ ] A resumed run reconstructs the completed boundary from git and Plane comments without double-posting markers.

**Notes:** Partial failure is the hardest case, so the boundary is defined by committed-and-confirmed state, not by worker claims: the conductor only advances children it can confirm. Leaving the failing child InProgress rather than Failed keeps it recoverable and consistent with how interrupted single-ticket runs already behave. Idempotent resume (no double-posting) matters because chain re-runs accumulate markers, and CommentMarkers already has to defend against stale newest-first reads.

**OOS:**
- Automatic retry of the failed ticket.
- Cross-ticket rework after review (Plan C).
- History rewriting to splice in the missing commit.

## Plan C: Combined review and rework routing

### Goal

After this plan, a completed batch stack is reviewed once over its combined diff with per-ticket commit ranges, and review feedback routes to the right place. The review brief enumerates each ticket's commit range so the reviewer scans the stack by ticket while seeing the integrated design; a second pass is allowed when the first returns rework or the batch exceeds a size limit. Localized feedback runs a normal per-ticket rework, cross-ticket feedback re-enters batch context, and a batch that exceeds the configured caps falls back to per-ticket spawning.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 08 | combined-stack-review | One review over the combined diff with per-ticket commit ranges | - | src/ThroughlineBuild.Phases/ReviewPhase.cs, src/ThroughlineBuild.Phases/ChainPhase.cs |
| 09 | rework-routing | Route localized versus cross-ticket feedback; additive rework commits | 08 | src/ThroughlineBuild.Phases/ReworkPhase.cs, src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs |
| 10 | batch-size-fallback | Cap ticket count, diff size, and context; fall back to per-ticket | - | src/ThroughlineBuild.Phases/ChainPhase.cs, src/ThroughlineBuild.Cli/Config.cs |

### Briefs - detail

#### Brief 08: combined-stack-review

**Goal:** A cohesive batch is reviewed as one integrated change without losing per-ticket fidelity. After this brief, when a batch completes, the conductor runs one review pass over the combined stack diff, and the review brief enumerates the commit range for each ticket so the reviewer checks each ticket's acceptance criteria and the seams between commits. A second review pass is triggered when the first returns rework or the batch exceeds a configured size threshold.

**Inputs:**
- src/ThroughlineBuild.Phases/ReviewPhase.cs lines 152-186 - reconstructing implementer state from the implemented_at marker.
- src/ThroughlineBuild.Phases/ChainPhase.cs - the single review pass per rung in RunImplementReviewLoopAsync.
- Brief 06's per-ticket markers - for the commit ranges.

**Outputs:**
- A combined review pass over the batch stack diff, run once after a successful batch.
- A review brief section listing each ticket and its commit range within the stack.
- A configurable size threshold that forces a second review pass.
- The per-rung review path for non-batch chains left unchanged.

**Acceptance:**
- [ ] A completed batch is reviewed in one pass over the combined diff by default.
- [ ] The review brief lists each ticket with its commit range.
- [ ] A batch over the size threshold gets a second review pass.
- [ ] Non-batch chains still review once per ticket.

**Notes:** One combined pass trades granularity for cost, and the main quality concern is that a large diff lowers per-line fidelity - the per-rung review caught real rework in TLB-419. The mitigation is structural: per-ticket commits plus commit ranges in the brief let the reviewer scan by ticket while seeing the whole design. The size threshold exists so the mode degrades to more scrutiny on large batches rather than silently reviewing an unreviewably large diff. Combined review is a mode with a conservative limit, not a permanent replacement for per-ticket review.

**OOS:**
- Routing the resulting feedback (Brief 09).
- The size-cap fallback before execution (Brief 10).
- Dropping per-ticket review as a global policy.

#### Brief 09: rework-routing

**Goal:** Review feedback on a batch goes to the smallest correct unit. After this brief, feedback that maps cleanly to one ticket's commit triggers a normal per-ticket rework on that ticket's branch, while feedback about an interface spanning the group re-enters batch context over the same group. Rework adds follow-up commits tied to the affected ticket rather than rewriting commits that already carry posted markers.

**Inputs:**
- src/ThroughlineBuild.Phases/ReworkPhase.cs - the rework contract and how a verdict loops back into a new implement phase via ReviewFeedback.
- src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs - feedback retrieval.
- Briefs 05 and 06 - the ticket-to-commit mapping and posted markers.

**Outputs:**
- A routing decision that classifies review feedback as localized (single ticket) or cross-ticket.
- Localized feedback handled by the existing per-ticket rework on that ticket's branch and worktree.
- Cross-ticket feedback handled by re-entering batch context over the same group.
- Additive rework commits attributed to the affected ticket, with no rewrite of marker-bearing commits.

**Acceptance:**
- [ ] Feedback isolated to one ticket runs a per-ticket rework against that ticket's branch.
- [ ] Feedback spanning the group re-enters batch context over the same group.
- [ ] Rework lands as additive commits on the affected ticket, leaving marker-bearing commits intact.
- [ ] The MaxReworkRounds bound still applies to a batch.

**Notes:** Two routes exist because forcing every defect back through the whole batch would re-pay the cost the operation exists to avoid, while forcing a cross-cutting fix into one ticket would fracture an interface change. The first version avoids history rewriting after markers are posted because there is no safe amend workflow for an already-attributed unshipped stack yet; additive commits keep attribution and resume simple. Reusing ReworkPhase keeps the rework contract identical to single-ticket chains.

**OOS:**
- A safe amend or squash workflow for attributed stacks.
- Classification heuristics beyond the localized and cross-ticket split.
- Changing the rework round bound.

#### Brief 10: batch-size-fallback

**Goal:** Batching never silently takes on a group too large to hold or review well. After this brief, before a batch session starts, the conductor checks the group against configured caps on ticket count, diff or change size, and estimated context, and when any cap is exceeded it falls back to the existing per-ticket chain path and logs which cap forced the fallback. Cohesive but oversized groups degrade safely instead of producing an unreviewable mega-diff or overflowing the worker context.

**Inputs:**
- Brief 04's batch branch - where the conductor picks the execution path.
- src/ThroughlineBuild.Cli/Config.cs - the plan and work config tables where batch caps would live.
- This op-doc's Why section - the cost and size rationale behind the caps.

**Outputs:**
- Configurable caps for batch ticket count, change size, and estimated context.
- A pre-execution gate that compares the declared group against the caps.
- A deterministic fallback to the per-ticket chain path when any cap is exceeded.
- A log or event record naming which cap triggered the fallback.

**Acceptance:**
- [ ] A group within all caps runs as a batch.
- [ ] A group exceeding any cap runs as the existing per-ticket chain instead.
- [ ] The triggering cap is named in the run output or event log.
- [ ] Caps are configurable rather than hard-coded.

**Notes:** The caps make batch mode conservative by default, matching the design's insistence that batching is an optimization for cohesive groups, not the only chain behavior. Falling back to the proven per-ticket path rather than failing keeps a too-large group shippable. Logging the triggering cap avoids a silent downgrade that would read as "batch ran" when it did not, which is exactly the failure mode to avoid.

**OOS:**
- Automatic cohesion detection (the group stays operator-declared).
- Splitting an oversized group into multiple batches.
- Tuning default cap values beyond sane initial constants.

## What done looks like

An operator running `build chain TLB-418 --batch-implement TLB-419,TLB-420,TLB-421,TLB-422` sees one warm worker session build the whole group in the shared chain worktree, then a clean stack of four commits - one per ticket, in order - each with its own implemented_at marker and each ticket moved to InReview. One combined review pass reports against per-ticket commit ranges; a defect isolated to one ticket reworks just that ticket, while a cross-cutting defect re-enters the group. A group that blows past the configured caps quietly runs as today's per-ticket chain, with the triggering cap named in the output. Standalone build implement and non-batch chains behave exactly as before, and the cache-read cost of the cohesive run drops sharply because the design was primed once, not four times.
