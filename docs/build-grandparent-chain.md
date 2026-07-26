# The Grandparent Chain (Recursive Chain Traversal)

**Status:** As built
**Last verified:** 2026-07-26

This document explains how `build chain <ticket-id>` discovers a multi-level
ticket tree, schedules its live leaves, accumulates their commits on integration
branches, and lands the completed root. For the command-level parent matrix, see
the [operator user guide](throughline_build_userguide.md#parent-tickets). For
the shared UUID parent-query convention, see
[tree-aware behavior](tree-aware-behavior.md).

Normative implementation:

- [`ChainPhase`](../src/ThroughlineBuild.Phases/ChainPhase.cs)
- [`ChainCommand`](../src/ThroughlineBuild.Commands/ChainCommand.cs)
- [`ChainOutcome`](../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs)
- [`ChainExitCodeMapper`](../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs)
- [`TopologicalSorter`](../src/ThroughlineBuild.Phases/TicketGraph.cs)

## 1. One root can describe a whole tree

Given:

```text
TLB-123
|-- TLB-124
|   |-- TLB-127
|   |-- TLB-128
|   `-- TLB-129
|-- TLB-125
`-- TLB-126
```

the operator runs:

```console
build chain TLB-123
```

Every `ChainPhase.RunAsync` call fetches its ticket and queries direct children
with `TicketQuery(ParentId: ticket.Uuid)`. A ticket with children is an internal
node; a ticket without children is a leaf. Child dispatch calls `RunAsync`
again, so TLB-124 is discovered as a parent in exactly the same way as TLB-123.

Traversal is not fixed at two levels. `VisitedTicketUuids` prevents cycles and
`MaxDepth` bounds recursion. The default maximum depth is 16.

Done and Cancelled children are excluded from the live schedule. A
self-referential child UUID is also excluded. If a parent has no remaining live
children, its parent path completes without running a leaf phase.

## 2. Leaf and internal-node work

### Leaves

A leaf resumes from its Plane state:

| State | Chain entry |
|---|---|
| Backlog | Plan |
| Planning | Reset the interrupted plan and plan again |
| Ready | Implement |
| InProgress | Resume committed work through rework, or prune an empty orphan branch and restart implement |
| InReview | Review |
| Done / Cancelled | Refused when named directly; filtered when it is a child |

A normal leaf runs the remaining portion of plan, implement, gate/review,
rework, and ship. Review can re-enter implementation up to the configured
rework cap. Provider or ticketing outages have distinct outcomes so they are not
misreported as code-review failures.

### Internal nodes

An internal node never plans or implements its own ticket body. It:

1. filters and orders live direct children;
2. creates or resumes `chain/<parent>` in an integration worktree;
3. recursively runs each scheduled child;
4. accumulates a completed child-parent's integration branch;
5. attempts the Plane parent rollup; and
6. if it is the outermost node, lands the completed integration branch.

The retained integration branch is refreshed against its current base before
new child dispatch. A conflict stops safely before more work is started.

## 3. Dependency order

For each parent, chain reads `blocked_by` relations between that parent's
eligible direct children and builds a sibling graph. `TopologicalSorter`
produces dependency levels.

- A blocker runs before the sibling that depends on it.
- Unrelated siblings share a level but are still dispatched serially, ordered
  by ticket number and then ID.
- Serial execution is intentional: each successful child advances the
  integration branch before the next child resolves its base.

The derived order is printed before phases start. Dependencies are evaluated
among siblings at the current parent; recursive child trees build their own
local sibling graph.

`build chain A B` is a different topology: A and B are separate roots managed
by the multi-root dispatcher. Dispatch remains serial.
The production multi-root dispatcher runs every root in a failing level and
then omits later dependent levels. Although the CLI accepts
`--continue-past-failure`, that flag is not currently wired into this dispatcher
path. Inside one parent tree, the first ordinary child failure stops later
siblings.

## 4. Branch topology and landing

For the example tree:

```text
configured target
  `-- chain/tlb-123
      |-- chain/tlb-124
      |   |-- ticket/tlb-127
      |   |-- ticket/tlb-128
      |   `-- ticket/tlb-129
      |-- ticket/tlb-125
      `-- ticket/tlb-126
```

The flow is:

1. A leaf branch is cut from its parent's current integration tip.
2. A successful leaf ship fast-forwards the parent integration branch.
3. A successful nested parent rebases its `chain/<child-parent>` branch onto
   the parent's current integration branch, then fast-forwards the parent.
4. Only the outermost successful parent rebases and fast-forwards the
   configured target, then pushes when remote push is enabled.

Nested leaf ships are local: they never push an integration branch. The root
landing performs the one remote push for the accumulated tree. With
`--no-push`, `[ship] push = false`, or no configured remote, landing remains
local.

If a child or pre-landing merge stops, the configured target has not received
the partial parent tree. Commits remain on ticket and integration branches for
diagnosis and resume. Root landing moves the local target before an optional
push, so a push failure can leave the local target advanced while the remote is
unchanged. Successful root completion removes chain worktrees best-effort but
retains branches; `build sweep` is the explicit merged-safe branch cleanup.

## 5. Dry-run and depth

`--dry-run` still performs root preflight and ticket/relation queries, then
prints:

- a post-order schedule, with leaves before their internal nodes;
- the integration and ticket branch topology;
- dependency levels; and
- cycle or depth warnings.

It runs no ticket phases and returns `DryRunPreview`.

`--max-depth N` is root-based:

- `0` allows only a leaf root; a parent root stops at the cap.
- `1` includes direct children.
- `2` includes grandchildren.

Encountering an internal node at the cap returns `ParentStoppedEarly` with a
depth diagnostic. The legacy `ParentHasGrandchildren` enum value remains for
compatibility but recursive traversal no longer produces it merely because
grandchildren exist.

## 6. Warm batch implementation

`--batch-implement` batches eligible sibling leaves within each parent. It does
not batch unrelated roots or combine leaves across different parents.

Candidate rules:

- The bare flag selects eligible Backlog and Ready direct children in dependency
  order.
- An explicit comma-separated list selects that exact eligible sibling subset
  in the supplied order.
- A candidate with live children is skipped from the batch and recursively
  chained as an internal node.
- Planning, InProgress, and InReview candidates follow the normal per-ticket
  path. Backlog batch candidates are planned individually first; promote mode
  can do that without a plan worker.
- Ticket count, aggregate size, and description-size caps can downgrade the
  group to normal per-ticket execution. The downgrade is surfaced.

For an accepted group:

1. one warm worker creates the declared commit stack;
2. `BatchCommitVerifier` checks cleanliness, reported commits, and stack order;
3. one combined review covers the stacked diff, with localized or cross-ticket
   rework as needed;
4. `ShipBatchStackAsync` switches the integration worktree back to
   `chain/<parent>` and fast-forwards it to the batch tip; and
5. every confirmed ticket receives its shipped marker and Done transition.

The root landing then carries the batch stack to the configured target. Current
tests cover Backlog planning, promote mode, combined review, state-write
failures, size fallback, internal-node exclusion, real-git local landing, and
the no-remote path.

The batch flag is preserved during recursion, so each internal node may form
its own direct-child batch.

## 7. Failure and resume behavior

The parent result is `ParentCompleted` only when every scheduled child and the
root landing succeed. Otherwise it is `ParentStoppedEarly` with child results
and rationale.

Environmental failures receive special handling:

- a gate that also fails on the untouched base is
  `GateEnvironmentFailure`;
- a provider that cannot review is `ReviewUnavailable`; and
- exhausted DNS/connect/TLS/timeout retries against Plane produce
  `TicketingUnavailable`.

After a machine-wide environment or ticketing failure, undispatched siblings
are recorded as `Skipped` with a reason. A later run resumes from Plane state
and retained git branches rather than repeating completed work.

Key exit codes:

| Exit | Outcomes |
|---:|---|
| 0 | Completed, RatifiedObsolete, ParentCompleted, DryRunPreview |
| 2 | Refused initial state, dirty tree, wrong branch, legacy grandchildren refusal |
| 3 | StoppedAtPlan, ParentStoppedEarly, Skipped |
| 4 | StoppedAtImplement |
| 5 | StoppedAtReview |
| 6 | ReworkCapExceeded |
| 7 | StoppedAtShip |
| 8 | GateVacuous |
| 9 | ReviewUnavailable |
| 10 | GateEnvironmentFailure |
| 11 | TicketingUnavailable |

`BatchImplemented` is treated as a successful child outcome inside parent
composition; a successful batch root still reports `ParentCompleted`.

## 8. Safety invariants

- The outermost preflight checks the configured target branch, tracked
  cleanliness, conflicts, and repository hygiene before dispatch.
- Parent-tree children execute serially against an accumulating base.
- Cycles and depth exhaustion stop rather than recurse indefinitely.
- Internal-node integration branches are refreshed before reuse.
- Failed rebase/merge work is retained on explicit branches.
- Nested ships do not push; the root performs at most one push.
- Parent tickets are containers; leaf tickets carry code changes.
