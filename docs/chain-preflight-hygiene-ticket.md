# Ticket: Add deterministic main-worktree hygiene gate before `build chain`

## Problem

`build chain 455 456 457 458` can spend substantial implementation and review time, get every ticket to a passing review verdict, and still end with a partial chain because `ship` refuses to merge when the main checkout has uncommitted tracked changes.

This happened in the run logged at:

```text
.build/events/tlb-455-chain-2026-06-04-121906.jsonl
```

The run ended with:

```json
{"Kind":12,"Phase":4,"Data":{"outcome":"partial","total_duration_ms":2526140}}
```

Each child ticket reached review pass, then stopped at ship:

```text
[455] StoppedAtShip
[456] StoppedAtShip
[457] StoppedAtShip
[458] StoppedAtShip
```

The blocking event for each ticket was the same:

```json
{"Kind":4,"Phase":3,"Data":{"kind":"pre_flight_dirty","dirty_paths":["C:/Users/plympton/src/projects/latticeflow"]}}
```

That path is the main checkout, not any ticket worktree. The ticket worktrees were clean. The main checkout had tracked modifications/deletions, so `ShipPhase` correctly refused to merge. The problem is timing: this deterministic blocker was only discovered after plan, implement, review, and rework work had already been performed.

## Goal

Add a deterministic preflight gate at the start of `build chain` that refuses to begin the chain when the main working directory has uncommitted tracked changes that would later block `ship`.

The chain should fail fast before planning or implementing any ticket, with a clear operator-facing message and structured event-log evidence.

## Background

`ShipPhase` already has the right safety behavior. In `src/ThroughlineBuild.Phases/ShipPhase.cs`, before fetch/rebase/regression checks/merge, ship checks both:

- the feature worktree
- the main working directory passed as `workingDirectory`

If either has tracked changes, ship emits:

```json
{
  "kind": "pre_flight_dirty",
  "dirty_paths": [...]
}
```

and returns a preflight failure.

`ChainPhase` also has an outer hygiene gate, but the observed run shows it does not currently reject ordinary tracked modifications in the main checkout before beginning work. That leaves a deterministic late failure path:

1. `build chain` starts.
2. Tickets are planned/implemented/reviewed.
3. Review passes.
4. `ship` runs.
5. `ship` refuses because the main checkout was dirty from the beginning.
6. Chain reports `partial`.

This is wasteful and predictable. The same tracked-change condition that blocks `ship` should block `chain` before the first ticket starts.

## Proposed Behavior

Before `build chain` starts any ticket work, it should inspect the main checkout and refuse to run if tracked files are modified, deleted, renamed, or staged.

The refusal should happen before:

- ticket state transitions
- plan comments
- implementation worktree creation
- worker agent spawn
- review
- ship

The operator should see a direct message similar to:

```text
chain blocked: main worktree has uncommitted tracked changes.

Commit, stash, or revert these files before running build chain:
  D docs/op-docs/op-29-tame-the-cli.md
  M docs/op-docs/op-31-batch-implement.md
  M docs/op-docs/op-doc-example.md
  M docs/op-docs/op-doc-spec.md
  M src/ThroughlineBuild.Scaffold/OpDocParser.cs
```

The event log should contain a structured gate failure with enough detail to diagnose the refusal without rerunning commands.

Suggested event:

```json
{
  "Kind": 4,
  "Phase": 4,
  "TicketId": "<root-or-first-ticket>",
  "Data": {
    "kind": "chain_preflight_dirty",
    "dirty_paths": ["docs/op-docs/op-doc-spec.md", "..."],
    "dirty_count": 5,
    "worktree": "C:/Users/plympton/src/projects/latticeflow"
  }
}
```

The resulting chain outcome should be a refusal-style terminal outcome, not `partial`.

Preferred outcome:

```text
RefusedDirtyTree
```

If the existing `ChainOutcome.RefusedDirtyTree` is already intended for this class of failure, reuse it. Do not add a new enum value unless the existing outcome cannot cleanly represent ordinary tracked-file dirtiness.

## Scope

In scope:

- Add or extend chain preflight logic so ordinary tracked changes in the main checkout block `build chain`.
- Reuse existing git abstraction methods where possible, especially the same tracked-change mechanism used by `ShipPhase`.
- Emit a structured `GateFailure` event with a distinct `kind`.
- Return a chain-level refusal outcome before any ticket phase runs.
- Print a concise operator-facing explanation with the dirty file list or a bounded sample.
- Add tests for clean and dirty main-worktree behavior.
- Document the gate in the event-log docs and any relevant state-of-system/user-guide docs.

Out of scope:

- Changing `ShipPhase` safety behavior.
- Auto-stashing, auto-committing, or auto-reverting user changes.
- Blocking on untracked files by default.
- Changing review/implement dirty-worktree behavior.
- Changing parent/child dependency ordering.
- Retrying after the user cleans the tree inside the same process.

## Policy

Block on tracked changes in the main checkout:

- modified tracked files
- deleted tracked files
- staged tracked files
- renamed tracked files
- copied tracked files if represented by git status as tracked changes

Do not block on untracked files by default.

Rationale: untracked scratch files, local logs, and generated artifacts may exist without affecting `ship`. `ShipPhase` currently blocks on tracked changes, and the chain gate should mirror the actual downstream merge blocker rather than invent a stricter policy.

If the existing git helper cannot distinguish tracked from untracked changes, add or extend a helper so the policy is explicit and testable.

## Implementation Notes

Likely implementation location:

```text
src/ThroughlineBuild.Phases/ChainPhase.cs
```

There is already a preflight block near the start of `RunAsync`:

```csharp
if (options.SharedWorktreePath is null)
{
    var preflightBranch = PhaseWorktreeLayout.BranchName(ticket.Id);
    var preflightFailure = await WorkingTreeHygieneGate
        .CheckAsync(_git, _workingDirectory, preflightBranch, ct)
        .ConfigureAwait(false);
    ...
}
```

Extend this outermost-chain preflight so it also checks ordinary tracked changes in `_workingDirectory`.

The simplest shape is probably:

```csharp
var dirtyFiles = await _git.GetTrackedChangesAsync(_workingDirectory, ct).ConfigureAwait(false);
if (dirtyFiles.Count > 0)
{
    emit GateFailure kind=chain_preflight_dirty;
    return ChainOutcome.RefusedDirtyTree;
}
```

However, prefer routing through `WorkingTreeHygieneGate` if that keeps all worktree hygiene policy in one place. The important requirement is that the exact condition which later causes `ShipPhase` to emit `pre_flight_dirty` is caught before the chain begins.

Be careful with parent-chain recursion:

- Run this gate only for the outermost chain invocation.
- Do not rerun it for each child when `SharedWorktreePath` is set.
- Preserve existing behavior for child chain execution inside a shared worktree.

Be careful with ticket side effects:

- Fetching/reading the ticket is acceptable if needed to compute branch names.
- Do not transition ticket state before this gate passes.
- Do not create plan comments before this gate passes.
- Do not create or modify ticket worktrees before this gate passes.

## Acceptance Criteria

- Running `build chain <tickets...>` from a clean main checkout behaves as it does today.
- Running `build chain <tickets...>` with uncommitted tracked changes in the main checkout exits before plan/implement/review/ship.
- The dirty-chain run does not transition any ticket state.
- The dirty-chain run does not spawn a worker agent.
- The dirty-chain run does not create a ticket worktree.
- The dirty-chain run emits a `GateFailure` event with `kind = "chain_preflight_dirty"`.
- The event includes the main worktree path and the dirty tracked file paths or a bounded sample plus total count.
- The chain result is a refusal outcome, preferably `RefusedDirtyTree`, not `StoppedAtShip` and not top-level `partial`.
- Untracked files alone do not block `build chain`.
- Existing `ShipPhase` `pre_flight_dirty` behavior remains unchanged.
- Parent-chain execution runs the gate once at the outermost level, not once per child.

## Test Plan

Add focused unit tests around `ChainPhase`.

Suggested tests:

### Clean main checkout proceeds

Arrange:

- fake git returns no tracked changes for `_workingDirectory`
- fake hygiene gate has no conflict/stash failure
- ticket is in `Backlog` or `Ready`

Assert:

- chain proceeds into the expected first phase
- no `chain_preflight_dirty` event is emitted

### Dirty tracked file refuses before phases

Arrange:

- fake git returns tracked changes for `_workingDirectory`, for example:

```text
docs/op-docs/op-doc-spec.md
src/ThroughlineBuild.Scaffold/OpDocParser.cs
```

Assert:

- result outcome is `RefusedDirtyTree`
- no plan phase is invoked
- no implement phase is invoked
- no review phase is invoked
- no ship phase is invoked
- no ticket state transition occurs
- event log contains `GateFailure` with `kind = "chain_preflight_dirty"`
- event includes dirty file details

### Untracked-only files do not refuse

Arrange:

- fake git reports no tracked changes
- untracked files exist only if the fake/helper models them separately

Assert:

- chain is not blocked by untracked-only state

### Parent chain gates only once

Arrange:

- parent chain has multiple eligible children
- outer invocation has clean main checkout
- child invocations run with `SharedWorktreePath`

Assert:

- main-worktree preflight executes once for the outer invocation
- child recursion does not redundantly run the main-checkout dirty gate

### Event format is stable

Assert:

- emitted event uses `EventKind.GateFailure`
- phase is `Phase.Chain`
- data includes:

```text
kind = chain_preflight_dirty
dirty_count
dirty_paths or dirty_path_sample
worktree
```

## Documentation Updates

Update event-log documentation to include the new gate failure kind.

Likely file:

```text
docs/event-log-format.md
```

Document that `ChainPhase` can emit:

```json
{
  "kind": "chain_preflight_dirty",
  "dirty_paths": ["..."],
  "dirty_count": 5,
  "worktree": "..."
}
```

and that this halts the chain before ticket work begins.

Also update any lifecycle/user-guide docs that describe `build chain` preconditions. The operator-facing rule should be short:

```text
Run build chain from a clean main checkout. Uncommitted tracked changes in the main checkout block chain startup because they would later block ship.
```

## Non-Goals and Rationale

Do not auto-stash.

Auto-stashing hides operator intent and can create confusing repo state if the process is interrupted. The chain should be explicit: tell the operator what is dirty and stop.

Do not auto-commit.

The tool cannot know whether dirty main-checkout edits are real work, scratch changes, generated docs, or mistakes.

Do not wait until ship.

Waiting until ship already proved costly: the observed chain spent about 42 minutes and reached passing reviews for four tickets before failing at a deterministic preflight condition.

Do not block on untracked files unless a separate policy ticket decides to do that.

The immediate production issue was tracked modifications in the main checkout, and `ShipPhase` blocks on tracked changes. The chain gate should mirror that downstream blocker.

## Regression Risk

Main risk: making `build chain` stricter than existing workflows expect.

Mitigation:

- block only tracked changes
- use the same underlying tracked-change detection as `ShipPhase`
- keep untracked files allowed
- provide clear event and console diagnostics

Secondary risk: parent chain recursion could run the gate after child work has intentionally modified the shared worktree.

Mitigation:

- run only when `SharedWorktreePath is null`
- add a parent-chain test for single execution

## Expected Operator Experience

Before this change:

```text
build chain 455 456 457 458
... 42 minutes of work ...
[455] StoppedAtShip
[456] StoppedAtShip
[457] StoppedAtShip
[458] StoppedAtShip
session outcome: partial
```

After this change:

```text
build chain 455 456 457 458
chain blocked: main worktree has uncommitted tracked changes.

Commit, stash, or revert these files before running build chain:
  D docs/op-docs/op-29-tame-the-cli.md
  M docs/op-docs/op-31-batch-implement.md
  M docs/op-docs/op-doc-example.md
  M docs/op-docs/op-doc-spec.md
  M src/ThroughlineBuild.Scaffold/OpDocParser.cs

outcome: RefusedDirtyTree
```

That is the desired behavior: fail before spending agent time.
