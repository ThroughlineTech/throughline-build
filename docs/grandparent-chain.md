# The Grandparent Chain (Recursive Chain Traversal)

How `build chain <ticket-id>` walks a multi-level ticket tree, builds every
leaf, accumulates the work onto stacked integration branches, and lands the
whole subtree onto the target branch in one run.

Written against `main` (commits `55047fd` recursive traversal, `8324d9b` TLB-475
accumulate recursive branches, `d39120c` TLB-492 ship leaves into the
integration worktree and land the root). Where this doc and the code disagree,
the code wins.

Anchor files:
- [src/ThroughlineBuild.Phases/ChainPhase.cs](src/ThroughlineBuild.Phases/ChainPhase.cs) - the whole engine
- [src/ThroughlineBuild.Commands/ChainCommand.cs](src/ThroughlineBuild.Commands/ChainCommand.cs) - CLI command wrapper + output formatting
- [src/ThroughlineBuild.Commands/DefaultChainRunner.cs](src/ThroughlineBuild.Commands/DefaultChainRunner.cs) - thin runner shim
- [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs](src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs) - the outcome vocabulary
- [src/ThroughlineBuild.Cli/Program.cs](src/ThroughlineBuild.Cli/Program.cs) - arg parsing + verb dispatch

---

## Part 1 - The user types this, and this is what happens

### The command

```
build chain TLB-123
```

That is the entire user-facing surface for the common case. One verb, one
ticket id. The operator does not say "this is a tree" or "go three levels
deep" - the chain discovers the shape of the tree on its own by walking
parent/child links in Plane.

Optional flags that shape the traversal (full list in
[src/ThroughlineBuild.Cli/CliUsage.cs:13](src/ThroughlineBuild.Cli/CliUsage.cs#L13)):

| flag | effect |
| --- | --- |
| `--dry-run` | print the full post-order schedule and branch topology, run no phases |
| `--max-depth <n>` | root-based depth cap: `0` = root only, `1` = root + direct children, default `16` |
| `--batch-implement [ids]` | implement direct children in one warm worker session instead of one-at-a-time |
| `--continue-past-failure` | keep running siblings/descendants even after one fails (default: stop) |
| `--no-auto-resolve` | do not let an "obsolete" escalation auto-ratify a ticket to Done |

### The example tree

The operator runs `build chain TLB-123`. In Plane the tickets are linked like
this (parent -> child edges):

```
TLB-123                      (root; the "grandparent")
|-- TLB-124                  (child of 123; itself a parent)
|   |-- TLB-127              (leaf)
|   |-- TLB-128              (leaf)
|   '-- TLB-129              (leaf)
|-- TLB-125                  (leaf)
'-- TLB-126                  (leaf)
```

123 is the grandparent of 127/128/129 (its children's children). The operator
only ever names 123. Everything below is found by traversal.

### What happens, step by step

This is the literal control flow. Every "examine" below is one call to
`ChainPhase.RunAsync` ([ChainPhase.cs:122](src/ThroughlineBuild.Phases/ChainPhase.cs#L122)),
which the chain calls recursively on itself.

1. **TLB-123 is looked at.** The chain fetches the ticket, runs its one-time
   preflight (clean working tree, main worktree parked on the target branch),
   then asks Plane: "does 123 have any children?" via
   `QueryAsync(new TicketQuery(ParentId: ticket.Uuid))`
   ([ChainPhase.cs:267](src/ThroughlineBuild.Phases/ChainPhase.cs#L267)).

2. **123 is discovered to be a parent.** The query returns 124, 125, 126
   (count > 0), so 123 is NOT a leaf. Instead of planning/implementing 123
   itself, the chain hands it to `RunParentChainAsync`
   ([ChainPhase.cs:281](src/ThroughlineBuild.Phases/ChainPhase.cs#L281)). 123's
   own description is never built - a parent ticket is a container, not a unit
   of work.

3. **123's children are scheduled.** `RunParentChainAsync`
   ([ChainPhase.cs:1720](src/ThroughlineBuild.Phases/ChainPhase.cs#L1720))
   filters out Done/Cancelled children, orders the rest lowest-number-first,
   and builds dependency "levels" from any `blocked_by` edges between siblings
   ([ChainPhase.cs:1733-1748](src/ThroughlineBuild.Phases/ChainPhase.cs#L1733)).
   It creates a **root integration branch** `chain/tlb-123` (in its own
   worktree) cut from the target branch, then dispatches children one at a
   time. First up: 124.

4. **TLB-124 is looked at - and it is ALSO a parent.** Dispatching 124 is just
   another recursive `RunAsync` call
   ([ChainPhase.cs:1953](src/ThroughlineBuild.Phases/ChainPhase.cs#L1953)), with
   `Depth = 1` and `ChainTargetBranch = chain/tlb-123`. That call repeats step 1:
   it queries 124's children, gets back 127/128/129, sees count > 0, and so 124
   goes down the SAME `RunParentChainAsync` path. This is the recursion - "it
   looks at the child tickets and discovers they are parents too and have
   children." 124 gets its own integration branch `chain/tlb-124`, cut this time
   from `chain/tlb-123` (not from the target).

5. **127, 128, 129 are looked at - these are leaves.** Each recursive `RunAsync`
   (now at `Depth = 2`, `ChainTargetBranch = chain/tlb-124`) queries for
   children, gets back nothing, and so takes the leaf path
   ([ChainPhase.cs:289](src/ThroughlineBuild.Phases/ChainPhase.cs#L289) onward):
   resolve where to enter based on ticket state, then run
   plan -> implement -> review (up to 2 rework rounds) -> ship. Each leaf's
   ship fast-forwards `chain/tlb-124` to include that leaf's commits, so
   127, then 128, then 129 stack up on `chain/tlb-124`.

6. **124 finishes and merges UP.** Once all three leaves complete, 124's
   `RunParentChainAsync` returns `ParentCompleted`. Back in 123's loop, that
   `ParentCompleted` triggers a fast-forward merge of `chain/tlb-124` into
   `chain/tlb-123`
   ([ChainPhase.cs:1974-1991](src/ThroughlineBuild.Phases/ChainPhase.cs#L1974)).
   Now everything 127/128/129 produced lives on `chain/tlb-123`.

7. **125 and 126 are looked at - leaves.** Back at 123's level, the next
   children dispatch. Each is a leaf, so each runs plan/implement/review/ship
   and fast-forwards `chain/tlb-123`. The chain now holds all five leaves'
   work on `chain/tlb-123`.

8. **The root lands.** 123 is the outermost chain (`ChainTargetBranch is null`),
   so after all children complete it calls `LandRootIntegrationBranchAsync`
   ([ChainPhase.cs:2042](src/ThroughlineBuild.Phases/ChainPhase.cs#L2042)): it
   rebases `chain/tlb-123` onto the current target tip, fast-forwards the target
   branch in the main worktree to it, and pushes (if a remote/push is
   configured). The entire subtree's work reaches the target branch as one
   coherent advance. 123 returns `ParentCompleted`, the run exits 0.

### Discovery is recursive, not a fixed two levels

The "is it a parent?" check (step 1/2) and the "schedule its children" engine
(step 3) are the same two pieces of code, and a child is dispatched by calling
the top-level entry again. So the tree can be any depth. 123 -> 124 ->
127 is three levels here; a fourth level under 127 would simply recurse once
more. The recursion is bounded only by `--max-depth` (default 16,
[ChainCommand.cs:46](src/ThroughlineBuild.Commands/ChainCommand.cs#L46)) and a
cycle guard ([ChainPhase.cs:233](src/ThroughlineBuild.Phases/ChainPhase.cs#L233)).

> Historical note: before this feature, a tree deeper than one level was
> refused with `ParentHasGrandchildren` ("chain the intermediate ticket
> directly"). That outcome still exists in the enum
> ([ChainOutcome.cs:17](src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L17))
> but the recursive traversal no longer produces it - grandchildren are now
> handled in the same run.

### The post-order schedule (what `--dry-run` prints)

The tree is executed **post-order**: a parent only finishes after all of its
children finish. For the example, `build chain TLB-123 --dry-run` prints
(logic in `VisitDryRunAsync`/`PrintDryRunPlan`,
[ChainPhase.cs:2221](src/ThroughlineBuild.Phases/ChainPhase.cs#L2221)):

```
[TLB-123] dry-run chain plan (max depth 16):
post-order schedule:
  1.     TLB-127 - run plan/implement/review/ship
  2.     TLB-128 - run plan/implement/review/ship
  3.     TLB-129 - run plan/implement/review/ship
  4.   TLB-124 - roll up internal node on chain/tlb-124
  5.   TLB-125 - run plan/implement/review/ship
  6.   TLB-126 - run plan/implement/review/ship
  7. TLB-123 - roll up internal node on chain/tlb-123
branch topology:
  chain/tlb-123 from main integrates subtree for TLB-123
  chain/tlb-124 from chain/tlb-123 integrates subtree for TLB-124
  ticket/tlb-125 from chain/tlb-123 before TLB-125
  ticket/tlb-126 from chain/tlb-123 before TLB-126
  ticket/tlb-127 from chain/tlb-124 before TLB-127
  ticket/tlb-128 from chain/tlb-124 before TLB-128
  ticket/tlb-129 from chain/tlb-124 before TLB-129
```

Leaves run their full lifecycle; internal nodes (124, 123) only "roll up" -
they merge their children's accumulated branch and do no building of their own.

### The branch topology, drawn out

```
main  (target branch; advanced once at the very end by the root landing)
  \
   chain/tlb-123   <- root integration branch, cut from main
     |\
     | chain/tlb-124   <- sub-integration branch, cut from chain/tlb-123
     |   |  (leaves cut from and fast-forwarded back onto chain/tlb-124)
     |   |-- ticket/tlb-127
     |   |-- ticket/tlb-128
     |   '-- ticket/tlb-129
     |        ...then chain/tlb-124 is merged up into chain/tlb-123
     |-- ticket/tlb-125   (cut from and ff'd onto chain/tlb-123)
     '-- ticket/tlb-126   (cut from and ff'd onto chain/tlb-123)
```

Three rules govern where work flows:
- A **leaf** ships (fast-forwards) into its parent's integration branch.
- A **sub-parent** merges its integration branch up into ITS parent's
  integration branch when it completes (`ParentCompleted` -> merge-up).
- The **root** (outermost) rebases its integration branch onto the target and
  fast-forwards the target onto it, then pushes.

Nothing touches the target branch until the very end, so a failure anywhere in
the tree leaves the target branch untouched and all completed work safe on the
`chain/...` integration branches for a re-run.

### What the operator sees, and the result line

Per-phase lines stream to stdout as each leaf runs (formatted by
`ChainCommand.FormatStepLine`,
[ChainCommand.cs:156](src/ThroughlineBuild.Commands/ChainCommand.cs#L156)),
prefixed with the ticket id, e.g. `[TLB-127] implement: Ok (3s)`. At the end the
command prints a final line plus a per-child summary
([ChainCommand.cs:122-132](src/ThroughlineBuild.Commands/ChainCommand.cs#L122)):

```
[TLB-123] parent chain complete: all eligible children completed (Xm Ys)
  [TLB-124] ParentCompleted (...)
  [TLB-125] Completed (...)
  [TLB-126] Completed (...)
```

If any child stopped, the outcome is `ParentStoppedEarly` and an operator-triage
block is printed telling you which child stopped, that completed children are
already shipped and will be skipped on a re-run, and how to resume
([ChainCommand.cs:398](src/ThroughlineBuild.Commands/ChainCommand.cs#L398)).

---

## Part 2 - The recursion engine, in code

### Entry path

```
build chain TLB-123
  -> Program.cs verb dispatch (verb == "chain")            Program.cs:110, 1630
     -> RunChainVerbAsync(...)                              Program.cs:1630+
        -> ChainCommand.ExecuteAsync(ctx)                   ChainCommand.cs:30
           -> DefaultChainRunner.RunAsync(...)              DefaultChainRunner.cs:20
              -> ChainPhase.RunAsync(options)               ChainPhase.cs:122   <-- recursive
```

`ChainPhaseOptions` ([ChainPhase.cs:33](src/ThroughlineBuild.Phases/ChainPhase.cs#L33))
is the recursion's state-carrier. The fields that matter for traversal:

- `Depth` / `MaxDepth` - current recursion depth vs the cap.
- `VisitedTicketUuids` - the set of ancestor UUIDs on the current path, for
  cycle detection.
- `ChainTargetBranch` - **the single most important flag.** `null` means "I am
  the outermost chain"; non-null means "I am nested, ship into this branch."
  This is how the same `RunAsync` body behaves differently at the root vs deep
  in the tree.
- `ChainIntegrationWorktreePath` - the worktree (checked out on the parent's
  integration branch) where a leaf's ship must run.
- `SharedWorktreePath`, `ChainCommitRange`, `BatchImplementGroup`, `DryRun`.

### The parent-vs-leaf decision (`RunAsync`)

The body of `RunAsync` is, in order:

1. **Outermost-only preflight** ([ChainPhase.cs:136](src/ThroughlineBuild.Phases/ChainPhase.cs#L136)):
   guarded by `if (options.ChainTargetBranch is null)`. Runs the wrong-branch
   guard (main worktree must be on the ship target), the working-tree hygiene
   gate (no dangling stash/conflict - the stash stack is repo-global and leaks
   across worktrees), and a dirty-tracked-files check. Children skip all of this
   because they carry a non-null `ChainTargetBranch`. Runs once per chain.

2. **Cycle guard** ([ChainPhase.cs:233](src/ThroughlineBuild.Phases/ChainPhase.cs#L233)):
   if this ticket's UUID is already in `VisitedTicketUuids`, stop with
   `ParentStoppedEarly` ("Cycle detected").

3. **Dry-run branch** ([ChainPhase.cs:244](src/ThroughlineBuild.Phases/ChainPhase.cs#L244)):
   build and print the schedule, return `DryRunPreview`, run no phases.

4. **Parent check** ([ChainPhase.cs:267](src/ThroughlineBuild.Phases/ChainPhase.cs#L267)):
   `QueryAsync(ParentId: ticket.Uuid)`. If it returns any children:
   - enforce the depth cap ([ChainPhase.cs:270](src/ThroughlineBuild.Phases/ChainPhase.cs#L270)),
     then
   - `return RunParentChainAsync(...)` - this ticket is an internal node.

5. **Leaf path** (everything after [ChainPhase.cs:289](src/ThroughlineBuild.Phases/ChainPhase.cs#L289)):
   no children, so resolve the entry phase from ticket state
   (`ResolveEntryAsync`, [ChainPhase.cs:2363](src/ThroughlineBuild.Phases/ChainPhase.cs#L2363)),
   run the implement/review loop (`RunImplementReviewLoopAsync`,
   [ChainPhase.cs:507](src/ThroughlineBuild.Phases/ChainPhase.cs#L507)) or the
   review-only branch, then ship.

### State-driven entry for leaves (`ResolveEntryAsync`)

A leaf does not always start at plan. The entry phase is derived from the
ticket's Plane state ([ChainPhase.cs:2363](src/ThroughlineBuild.Phases/ChainPhase.cs#L2363)):

| state | entry | note |
| --- | --- | --- |
| Backlog | Plan | fresh |
| Ready | Implement | already planned |
| InReview | Review | already implemented |
| Planning | Plan | reset to Backlog first (interrupted plan, nothing to keep) |
| InProgress | Resume | resume in place if branch has commits, else prune + restart |
| Done / Cancelled | Refused | terminal; `RefusedInitialState` |

The InProgress resume logic (`ResolveInProgressAsync`,
[ChainPhase.cs:2396](src/ThroughlineBuild.Phases/ChainPhase.cs#L2396)) is what
makes a re-run safe: a branch with real commits is resumed via the rework path
carrying recovered review feedback; an orphaned branch with no commits is pruned
and the ticket reset to Ready so it rebuilds cleanly inside the shared worktree.

### `RunParentChainAsync` - the internal-node engine

([ChainPhase.cs:1720](src/ThroughlineBuild.Phases/ChainPhase.cs#L1720))

1. **Eligibility + ordering** ([ChainPhase.cs:1733](src/ThroughlineBuild.Phases/ChainPhase.cs#L1733)):
   drop Done/Cancelled children and the parent itself (a self-referential edge
   would recurse forever), order by ascending ticket number then id.

2. **Sibling dependency levels** ([ChainPhase.cs:1742](src/ThroughlineBuild.Phases/ChainPhase.cs#L1742)):
   `BuildSiblingGraphAsync` reads each child's `blocked_by` relations
   ([ChainPhase.cs:2327](src/ThroughlineBuild.Phases/ChainPhase.cs#L2327)) and
   `TopologicalSorter.ComputeLevels` groups them. Same level = no dependency
   between them (dispatched in numeric order); later level = blocked by an
   earlier one. `PrintDispatchOrder` shows this up front so a missing edge is
   visible before any work runs.

3. **Integration worktree** ([ChainPhase.cs:1750-1803](src/ThroughlineBuild.Phases/ChainPhase.cs#L1750)):
   - `integrationBaseRef = options.ChainTargetBranch ?? _baseOptions.TargetBranch`
     - so a nested parent's branch forks from its parent's integration branch;
       the root's forks from the target.
   - `integrationBranch = chain/{slug}` (`ChainIntegrationBranch`,
     [ChainPhase.cs:2161](src/ThroughlineBuild.Phases/ChainPhase.cs#L2161)).
   - `EnsureIntegrationWorktreeAsync`
     ([ChainPhase.cs:2165](src/ThroughlineBuild.Phases/ChainPhase.cs#L2165))
     reuses an existing worktree/branch if present (resumable), else creates one.
   - `chainStartSha` is captured here so the per-child implement brief can be
     told which files prior siblings already touched (`ChainCommitRange`).

4. **(Optional) batch implement** ([ChainPhase.cs:1808-1895](src/ThroughlineBuild.Phases/ChainPhase.cs#L1808)):
   when `--batch-implement` is set, eligible Ready direct children are
   implemented in ONE warm worker session stacked on the first child's branch,
   then reviewed combined (`RunBatchImplementSessionAsync` /
   `RunBatchReviewAndReworkAsync`). Size caps
   ([ChainPhase.cs:1463](src/ThroughlineBuild.Phases/ChainPhase.cs#L1463)) fall
   back to per-ticket dispatch if exceeded. Batched ids are then skipped by the
   normal loop.

5. **The dispatch loop** ([ChainPhase.cs:1897-1993](src/ThroughlineBuild.Phases/ChainPhase.cs#L1897)):
   for each level, for each child (one at a time):
   - compute `childCommitRange` from `chainStartSha..integrationBranch` so the
     brief reflects accumulated sibling commits;
   - build `childOptions = options with { TicketId = child.Id, Depth = Depth+1,
     ChainTargetBranch = integrationBranch, ChainIntegrationWorktreePath =
     sharedWorktreePath, VisitedTicketUuids = +parent.Uuid, SharedWorktreePath =
     null }` ([ChainPhase.cs:1943](src/ThroughlineBuild.Phases/ChainPhase.cs#L1943));
   - **`childResult = await RunAsync(childOptions, ct)`** - the recursion
     ([ChainPhase.cs:1953](src/ThroughlineBuild.Phases/ChainPhase.cs#L1953));
   - if the child failed (`!IsChainSuccess`), set `anyStoppedEarly` and break
     (unless continue-past-failure semantics apply at the dispatch layer);
   - if the child returned `ParentCompleted` (it was itself an internal node),
     **fast-forward merge its `chain/{child}` up into this `chain/{parent}`**
     ([ChainPhase.cs:1974](src/ThroughlineBuild.Phases/ChainPhase.cs#L1974)).

   Children are dispatched strictly one at a time even within an unordered
   level, on purpose ([ChainPhase.cs:1907](src/ThroughlineBuild.Phases/ChainPhase.cs#L1907)):
   a completed child ships into the integration branch before the next child
   resolves its base, so siblings stack cleanly instead of racing.

6. **Root landing** ([ChainPhase.cs:2005-2013](src/ThroughlineBuild.Phases/ChainPhase.cs#L2005)):
   only when `options.ChainTargetBranch is null` (outermost) and nothing stopped
   early, call `LandRootIntegrationBranchAsync`. A nested parent never lands - it
   was already merged up by its parent's loop.

7. **Result** ([ChainPhase.cs:2020-2032](src/ThroughlineBuild.Phases/ChainPhase.cs#L2020)):
   `ParentCompleted` if everything succeeded (and, at root, landed), else
   `ParentStoppedEarly`, with the per-child `ChainResult` list attached.

### `LandRootIntegrationBranchAsync` - the only thing that moves the target

([ChainPhase.cs:2042](src/ThroughlineBuild.Phases/ChainPhase.cs#L2042))

1. Re-check the main worktree is still on the target branch (mirrors ShipPhase's
   pre-merge guard).
2. **Rebase** `chain/{root}` onto the current target tip. The integration branch
   forked from the target at chain start; if the target advanced since (a long
   chain racing a concurrent push, or a reused branch from a prior run), a plain
   fast-forward would be impossible, so replaying the accumulated commits makes
   the target an ancestor again. Conflicts -> abort + stop with a rationale that
   says the work is safe on the integration branch.
3. **Fast-forward** the target branch in the main worktree onto the integration
   branch.
4. **Push** the target (only if a landing remote is configured and push is
   enabled); a push failure stops with "landed locally but push failed -
   reconcile manually."

On any failure it returns a human-readable rationale (becomes
`ParentStoppedEarly` + `FinalRationale`); the accumulated work is never
discarded. The `chain/...` and `ticket/...` branches/worktrees are intentionally
retained at the end so a failed or retried chain can resume from the accumulated
topology.

---

## Part 3 - Outcomes, exit codes, and the supporting flags

### Outcome vocabulary

`ChainOutcome` ([ChainOutcome.cs](src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs)),
with the ones the recursive chain produces highlighted:

| outcome | meaning |
| --- | --- |
| `Completed` | a leaf finished plan..ship |
| `ParentCompleted` | an internal node: all eligible children completed (and, at root, landed) |
| `ParentStoppedEarly` | an internal node: a child stopped, OR the root landing failed |
| `RatifiedObsolete` | ticket auto-marked Done because prior work subsumed it |
| `BatchImplemented` | child implemented in a batch session; review/ship handled in batch flow |
| `DryRunPreview` | `--dry-run` only; no phases ran |
| `StoppedAtPlan/Implement/Review/Ship` | a leaf phase failed |
| `ReworkCapExceeded` | a leaf failed review after `MaxReworkRounds` (2) |
| `RefusedInitialState` | ticket was Done/Cancelled |
| `RefusedDirtyTree` | preflight: working tree not clean |
| `RefusedWrongBranch` | preflight: main worktree not on the target branch |
| `ParentHasGrandchildren` | legacy "too deep" refusal; no longer produced by traversal |
| `Skipped` | descendant skipped because an ancestor failed (without continue-past-failure) |

`IsChainSuccess` ([ChainPhase.cs:2352](src/ThroughlineBuild.Phases/ChainPhase.cs#L2352))
counts `Completed`, `RatifiedObsolete`, `ParentCompleted`, and `BatchImplemented`
as success for the parent loop's stop/continue decision.

### Exit codes

`ChainExitCodeMapper` ([ChainExitCodeMapper.cs:13](src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L13)):
`0` Completed/RatifiedObsolete/ParentCompleted/DryRunPreview; `2`
Refused*/ParentHasGrandchildren; `3` StoppedAtPlan/ParentStoppedEarly/Skipped;
`4` Implement; `5` Review; `6` ReworkCapExceeded; `7` Ship; `1` anything else.

### `--max-depth`

Root-based ([ChainCommand.cs:46](src/ThroughlineBuild.Commands/ChainCommand.cs#L46),
checked at [ChainPhase.cs:270](src/ThroughlineBuild.Phases/ChainPhase.cs#L270)):
`0` = root only, `1` = root + direct children, default `16`. Hitting the cap at
any internal node (including a parent root at `--max-depth 0`) stops that subtree
with `ParentStoppedEarly` ("Depth cap N reached").

### `--batch-implement`

Implements direct children together in one warm worker session rather than one
implement subprocess per child. Two forms: a comma-separated explicit list
(exact group, listed order) or the bare flag (all eligible Ready direct
children, dependency/numeric order). Governed by size caps; oversized groups log
the cap and fall back to per-ticket dispatch. See
[ChainPhase.cs:896](src/ThroughlineBuild.Phases/ChainPhase.cs#L896) onward.

### Multiple root tickets vs one tree

`build chain TLB-A TLB-B` (two positional ids) is different from a tree: the
extra ids are treated as **separate roots** and dispatched through
`ParallelDispatcher` using `blocked_by` edges between them
([Program.cs:1838](src/ThroughlineBuild.Cli/Program.cs#L1838)). Each root then
independently runs its own recursive subtree traversal. (Dispatch concurrency is
pinned to serial; see the Phases AGENTS.md note.) A single root id is the
common, fully-recursive path described in Parts 1-2.

---

## Part 4 - Safety properties worth remembering

- **The target branch moves exactly once**, at the very end, via the root
  landing. Every leaf ships only into an in-memory `chain/...` branch until then.
- **Failure is recoverable.** Completed leaves stay shipped on the integration
  branches; a re-run skips them (their tickets are Done/InReview) and resumes the
  stopped one. Integration/ticket branches and worktrees are retained on purpose.
- **Preflight runs once, at the root**, gated on `ChainTargetBranch is null`, so
  the wrong-branch / dirty-tree / hygiene checks are not re-run per child.
- **Cycles cannot hang the traversal** - `VisitedTicketUuids` accumulates
  ancestors down each path, and a repeat UUID stops with `ParentStoppedEarly`.
- **Parents are containers, never built.** An internal node's own ticket body is
  never planned or implemented; it only schedules children and rolls up their
  branch.

---

## Part 5 - The `--batch-implement` variant: `build chain TLB-123 --batch-implement`

> What this section is really about: whether `--batch-implement` is wired into
> the accumulate-and-land model from Parts 1-4, or whether it implements work
> that then never reaches the target branch. Short answer: **batch covers
> implement + a combined review only - it does NOT ship**, and that has sharp
> consequences in a multi-level tree. Details below.

### The command

```
build chain TLB-123 --batch-implement
```

Bare `--batch-implement` (no id list) becomes `ChainBatchImplementGroup.AllEligibleChildren`
([Program.cs:1819](src/ThroughlineBuild.Cli/Program.cs#L1819),
[ChainCommand.cs:61](src/ThroughlineBuild.Commands/ChainCommand.cs#L61)). The
intent: instead of running a separate implement subprocess for each child, run
ONE warm worker session that implements a group of siblings together, stacking
their commits, then review them combined.

### Three filters decide who actually gets batched

At each parent node, the batch group is computed in
`RunParentChainAsync` ([ChainPhase.cs:1815-1821](src/ThroughlineBuild.Phases/ChainPhase.cs#L1815)).
For the bare flag it is "all eligible children" - but "eligible" then passes
through filters that matter a lot:

1. **`State == TicketState.Ready` only** ([ChainPhase.cs:1820](src/ThroughlineBuild.Phases/ChainPhase.cs#L1820)).
   Batch only replaces the *implement* phase, so a child must already be planned
   (Ready). A **Backlog** child is NOT batched - it falls through to the normal
   per-ticket path, which plans it (and then ships it) on its own.
2. **No explicit "is this a parent?" exclusion** - but an internal node (124) is
   almost always Backlog/operation-state, not Ready, so in practice it is
   excluded by filter 1 and dispatched normally (where it recurses).
3. **Size caps** ([ChainPhase.cs:1838](src/ThroughlineBuild.Phases/ChainPhase.cs#L1838),
   `CheckBatchSizeCaps` [ChainPhase.cs:1463](src/ThroughlineBuild.Phases/ChainPhase.cs#L1463)):
   ticket count, aggregate size score, and total description bytes. An oversized
   group logs the exceeded cap and falls back to the per-ticket chain
   ([ChainPhase.cs:1841](src/ThroughlineBuild.Phases/ChainPhase.cs#L1841)) - no
   batching at all for that node.

### The flag propagates to every level

Crucially, the dispatch loop builds child options with `options with { ... }`
([ChainPhase.cs:1943](src/ThroughlineBuild.Phases/ChainPhase.cs#L1943)) and
**does not reset `BatchImplementGroup`**. So `AllEligibleChildren` rides the
recursion all the way down: every internal node independently batches its own
Ready leaf-children. Batching is therefore **per-parent**, never across levels -
124's leaves are one batch, 123's leaves are a different batch, and the two are
never combined.

### What happens, step by step (same tree as Part 1)

```
TLB-123                      (root)
|-- TLB-124  (parent)        --> not Ready, excluded from batch, recurses
|   |-- TLB-127 (Ready leaf) --\
|   |-- TLB-128 (Ready leaf)    >-- batched together under 124
|   '-- TLB-129 (Ready leaf) --/
|-- TLB-125  (Ready leaf)    --\
'-- TLB-126  (Ready leaf)    --/-- batched together under 123
```

1. **123 is a parent** -> `RunParentChainAsync`. Batch group at 123 = its Ready
   leaf children = {125, 126}. 124 is excluded (not Ready) and stays in the
   normal dispatch loop.
2. **125 + 126 are batch-implemented in one session.**
   `RunBatchImplementSessionAsync` ([ChainPhase.cs:896](src/ThroughlineBuild.Phases/ChainPhase.cs#L896))
   creates a single batch branch `ticket/tlb-125` (the first ticket's branch,
   [ChainPhase.cs:908](src/ThroughlineBuild.Phases/ChainPhase.cs#L908)) cut from
   `chain/tlb-123` inside the integration worktree, and the worker stacks both
   commits on it. Per-ticket `[implemented_at:]` markers are posted and both
   tickets transition to **InReview**
   ([ChainPhase.cs:1168-1173](src/ThroughlineBuild.Phases/ChainPhase.cs#L1168)).
3. **One combined review** runs over the whole stacked diff
   (`RunBatchReviewAndReworkAsync` [ChainPhase.cs:1511](src/ThroughlineBuild.Phases/ChainPhase.cs#L1511);
   up to 2 rework rounds, localized rework routes to one ticket, cross-ticket
   rework re-enters the batch). Each batched ticket gets `BatchImplemented`
   ([ChainPhase.cs:1185](src/ThroughlineBuild.Phases/ChainPhase.cs#L1185)).
4. **No ship runs for 125/126.** The batched ids are added to `batchedTicketIds`
   and then explicitly excluded from the dispatch loop
   ([ChainPhase.cs:1904](src/ThroughlineBuild.Phases/ChainPhase.cs#L1904)) - and
   the dispatch loop is the ONLY place a leaf's ShipPhase runs to fast-forward
   the integration branch ([ChainPhase.cs:439](src/ThroughlineBuild.Phases/ChainPhase.cs#L439)).
   The batch block ([ChainPhase.cs:1876-1895](src/ThroughlineBuild.Phases/ChainPhase.cs#L1876))
   ends after the combined review with no ship and no fast-forward. This is by
   design and is asserted by the tests:
   [ChainPhaseTests.cs:1993](tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs#L1993)
   ("No verifiers enqueued: batch tickets skip review and ship") and the InReview
   assertions at [ChainPhaseTests.cs:2037-2038](tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs#L2037).
5. **124 recurses, carrying the flag.** It batches {127, 128, 129} the same way:
   they land on `ticket/tlb-127` in 124's own integration worktree, transition to
   InReview, and are likewise never shipped onto `chain/tlb-124`. 124 returns
   `ParentCompleted`.
6. **Roll-up and landing run as in Part 1** - but on branches that do not contain
   the batched commits. The merge-up of `chain/tlb-124` into `chain/tlb-123`
   ([ChainPhase.cs:1974](src/ThroughlineBuild.Phases/ChainPhase.cs#L1974)) and the
   root landing of `chain/tlb-123` ([ChainPhase.cs:2042](src/ThroughlineBuild.Phases/ChainPhase.cs#L2042))
   only move whatever is actually on those `chain/...` branches.

### The consequence: batched work is implemented and reviewed, but not landed

Trace where the batched commits live versus what the chain lands:

```
chain/tlb-123  --- root landing fast-forwards the TARGET onto THIS branch
   |   (batch never advanced it: 125/126's commits are over here -->)
   |
   '-- ticket/tlb-125   <- 125 + 126 commits, tickets left InReview, never shipped

chain/tlb-124  --- merged up into chain/tlb-123 (empty merge)
   |
   '-- ticket/tlb-127   <- 127 + 128 + 129 commits, tickets InReview, never shipped
```

Because a leaf only reaches the integration branch through the ShipPhase in the
dispatch loop, and batched leaves are excluded from that loop, **their commits
never fast-forward onto the `chain/...` integration branch.** The root landing
fast-forwards the target onto `chain/tlb-123`, which does not contain them. Net
result for `chain TLB-123 --batch-implement` on this tree:

- 125, 126, 127, 128, 129 are implemented and combined-reviewed, left in
  **InReview** with commits on per-batch `ticket/...` branches.
- The chain reports `ParentCompleted` (batch success counts as success via
  `IsChainSuccess` [ChainPhase.cs:2352](src/ThroughlineBuild.Phases/ChainPhase.cs#L2352))
  and exits 0.
- The **target branch does not receive the batched work** in this run.

A multi-level tree makes it sharper still: 123's batch leaves the shared
integration worktree checked out on `ticket/tlb-125` (the batch branch), not on
`chain/tlb-123`. The later merge-up of `chain/tlb-124`
([ChainPhase.cs:1977-1980](src/ThroughlineBuild.Phases/ChainPhase.cs#L1977))
fast-forwards whatever branch is checked out in that worktree, so with real git
the branch state the merge-up acts on is not the one the landing later reads.
(The fakes in `ChainPhaseTests` do not model branch HEADs, so the suite returns
`ParentCompleted` without exercising this.)

### Intended design vs. what ships today (verified)

The author's stated intent for `--batch-implement`: pool a group of sibling
tickets into ONE shared worktree; implement + deterministically verify + commit
each ticket in turn (same worktree, stacking); run ONE traditional LLM verifier
over the merged result at the end; then **ship the end result**.

Mapping that intent onto the code as it stands on `main`:

| intended step | implemented? | where |
| --- | --- | --- |
| Pool the group into one shared worktree | Yes | batch branch created once in the integration worktree, [ChainPhase.cs:906-910](src/ThroughlineBuild.Phases/ChainPhase.cs#L906) |
| Implement + commit each ticket in declared order, stacking in that worktree | Yes (delegated to the worker) | one warm worker session; brief says "exactly one local commit per ticket, in declared order" [batch-implement.md:24](src/ThroughlineBuild.Briefs/Templates/claude-code/batch-implement.md#L24) |
| Deterministic check per ticket | Partial / not per-ticket | the only deterministic gate is `BatchCommitVerifier` run ONCE after the session - it checks the worktree is clean and each reported commit SHA exists in declared stack order ([BatchCommitVerifier.cs:37](src/ThroughlineBuild.Phases/BatchCommitVerifier.cs#L37)). There is no orchestrator-run build/test gate between tickets; per-ticket correctness is left to the worker. |
| One LLM verifier over the merged result | Yes | `RunCombinedBatchReviewAsync` runs 1-2 combined review passes over the full stacked diff ([ChainPhase.cs:1221](src/ThroughlineBuild.Phases/ChainPhase.cs#L1221)) |
| **Ship the end result** | **No** | nothing ships/merges/lands the batch stack; see below |

So: the first four rows are essentially there (with the caveat that the
"deterministic check" is a single post-session commit-attribution check, not a
per-ticket build/test gate). **The fifth - shipping the end result - is not
implemented.** That matches your hunch ("it should ship the end result, but I
haven't tested it"): as written, it does not.

### Why the ship does not happen

A leaf's commits only reach the integration branch through the ShipPhase call in
the dispatch loop ([ChainPhase.cs:439](src/ThroughlineBuild.Phases/ChainPhase.cs#L439)).
Batched tickets are added to `batchedTicketIds` and explicitly excluded from that
loop ([ChainPhase.cs:1904](src/ThroughlineBuild.Phases/ChainPhase.cs#L1904)), and
the batch block ends after the combined review with no ship and no fast-forward
([ChainPhase.cs:1876-1895](src/ThroughlineBuild.Phases/ChainPhase.cs#L1876)). The
batch commits live on the batch branch `ticket/<first-id>`; the integration
branch `chain/<parent>` is never advanced to them; and the root landing only
fast-forwards the target onto `chain/<parent>` - which does not contain the batch
work. The tests confirm the intent-as-built: "batch tickets skip review and ship"
([ChainPhaseTests.cs:1993](tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs#L1993)),
tickets end InReview ([ChainPhaseTests.cs:2037](tests/ThroughlineBuild.Phases.Tests/ChainPhaseTests.cs#L2037)),
and no test asserts the work reaches the target.

Confirmed by git history, this is a sequencing gap, not a deliberate decision to
never ship: the whole batch series (TLB-444 `bc4cdab` .. TLB-473 `f970a1f`)
merged BEFORE recursive traversal (`55047fd`), accumulate (`8324d9b`/TLB-475),
and root-landing (`d39120c`/TLB-492). Batch was built as an implement-phase
accelerator and the "ship the end result" step was never wired into the
later accumulate-and-land model.

### Invocation caveat: batch pools CHILDREN of a chained parent, not bare roots

`build chain 1 2 3` (three positional ids) is NOT the batch trigger. Three
positional ids go down the multi-root dispatcher path
([Program.cs:1838](src/ThroughlineBuild.Cli/Program.cs#L1838)); each id is run as
its own root chain, and the batch group is only ever consulted inside
`RunParentChainAsync` - which only runs when a ticket HAS children. If 1/2/3 are
independent leaf roots, `--batch-implement` is effectively ignored and they run
as three separate single-ticket chains.

To actually pool 1/2/3 they must be children of a parent P, invoked as:
- `build chain P --batch-implement` - batch all eligible (Ready) direct children of P, or
- `build chain P --batch-implement TLB-1,TLB-2,TLB-3` - batch exactly that subset.

Also note the **Ready-only filter** ([ChainPhase.cs:1820](src/ThroughlineBuild.Phases/ChainPhase.cs#L1820)):
only children already in Ready state are batched. Backlog children (not yet
planned) fall through to the normal per-ticket path, so on a freshly scaffolded
tree the batch may pick up nothing on the first run.

### The fix shape, if you want it to ship

What a normal chain leaf does (the behavior the batch stack must join): its
`ShipPhase` runs in the integration worktree and (Step 8,
[ShipPhase.cs:651-670](src/ThroughlineBuild.Phases/ShipPhase.cs#L651))
fast-forwards `chain/<parent>` onto the leaf's `ticket/<id>` branch, then
transitions the ticket InReview -> Done (Step 11,
[ShipPhase.cs:698-704](src/ThroughlineBuild.Phases/ShipPhase.cs#L698)) and posts
a `[shipped_at: <sha>]` marker. That FF is what advances the integration branch;
the root landing later carries `chain/<root>` to the target.

The batch path produces an N-commit stack on the batch branch `ticket/<first-id>`
but joins neither step. The wrinkle: after the warm session the integration
worktree is left checked out ON `ticket/<first-id>`, not on `chain/<parent>`. So
the fix, after the combined review passes, is to do the leaf-ship equivalent for
the whole stack, once:

1. (Optionally) run the regression checks once over the stacked result, matching
   ShipPhase Step 7.
2. Put the integration worktree back on `chain/<parent>` and
   `FastForwardMergeAsync(batchBranch, integrationWorktreePath)` so the integration
   branch advances to the full stack tip. (`ticket/<first-id>` was cut from
   `chain/<parent>` and only added commits, so the FF is always valid.)
   Alternative: build the batch directly on `chain/<parent>` instead of cutting a
   separate `ticket/<first-id>`, so no post-hoc FF is needed.
3. Transition each batched ticket InReview -> Done and post a `[shipped_at:]`
   marker, mirroring ShipPhase Steps 10-11 per ticket.

Then the existing root landing carries the stack to the target unchanged - the
batch becomes "ship the squashed mega-ticket" exactly as intended. Until this
exists, batched tickets sit in InReview and must be shipped with a follow-up, and
the chain misreports `ParentCompleted` even though the batched work never reached
the target.

I have not run this end to end (the batch worker needs a real agent, and the
unit tests use git fakes that do not model branch HEADs, so they pass without
exercising the landing). The decisive check is a real-`ProcessGitClient`
integration test with a fake batch worker that makes actual commits, asserting
that the target branch advances after `build chain P --batch-implement`.
