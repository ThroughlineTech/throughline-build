# Operation: multi-ticket-commands

Let commands take an explicit list of tickets - `build chain T1 T2 T3`, `build implement T1 T2`, etc. - and process them with parallel execution where independent, in a dependency-aware order derived from the parent-child relationships among the listed tickets. Hard-depends on the tree-aware op-doc landing first.

## Why this exists

Once a command can be invoked on a parent and recurse to its children (tree-aware op-doc), processing an explicit list of tickets is the same dispatch-and-iterate machinery generalized to operator-supplied sets rather than tree-derived ones. This adds the list surface so an operator can batch work without a parent relationship - the common case of "run these three tickets through chain."

Parallel execution and dependency-aware ordering are first-class here, not deferred: the prior PowerShell scripting that preceded TLB already did both, and stripping them down to serial-execution-in-list-order would be regression. Where two listed tickets have no parent-child relationship, they can run concurrently; where one is an ancestor of another, the ancestor runs first.

It depends entirely on the tree-aware foundation: the walk, the per-ticket dispatch, and the result aggregation. Without it, this would duplicate that machinery; with it, this is arg parsing plus graph construction plus a concurrent runner plus aggregation.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Multi-ticket: list parsing, dependency-aware ordering, parallel dispatch, aggregation | - | M |

Single plan; briefs are sequential (each builds on the previous). Depends on the tree-aware op-doc at the roadmap level.

## Plan A: Multi-ticket

### Goal

Commands accept a space-separated list of ticket ids, derive a dependency graph from the listed tickets' parent-child relationships, execute the graph in topological order with parallelism where dependencies permit, and report an aggregated result.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | multi-ticket-arg-parsing | Parse `<verb> T1 T2 T3 ...` into a ticket list; preserve single-ticket behavior | - | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |
| 02 | dependency-graph-from-list | Build a per-invocation dependency graph from the listed tickets' parent-child relationships; topological order | 01 | src/ThroughlineBuild.Plane/TicketTreeWalker.cs, src/ThroughlineBuild.Cli/MultiTicketGraph.cs (new) |
| 03 | parallel-dispatch | Concurrent execution of independent tickets via tree-aware dispatch; configurable concurrency | 02 | src/ThroughlineBuild.Cli/Program.cs |
| 04 | aggregate-and-report | Gather per-ticket outcomes (success/failure/timing) into one report; deliberate failure semantics | 03 | src/ThroughlineBuild.Cli/Program.cs |

### Briefs - detail

#### Brief 01: multi-ticket-arg-parsing

Goal: Accept a list of ticket ids on the verbs that support it, without breaking single-ticket invocation.

Inputs: current CLI arg parsing; the set of verbs that should support lists (chain, implement, review, ship, decompose, and the lifecycle verbs if present).

Outputs:
- Verbs parse one-or-more ticket ids.
- Single-ticket behavior is unchanged.
- Usage text documents the list form.

Acceptance:
- [ ] Supported verbs accept `T1 T2 T3`
- [ ] Single-ticket invocation behaves exactly as before
- [ ] Usage documents the list form

OOS:
- Dependency graph (B02)
- concurrency (B03)
- aggregation (B04)
- glob/range selectors
- reading ids from a file

#### Brief 02: dependency-graph-from-list

Goal: Derive an execution graph from the listed tickets' parent-child relationships so the runner knows which tickets must precede which, and which can run in parallel.

Inputs: the parsed list from B01; the tree-aware `TicketTreeWalker`; `IPlaneTicketing`.

Outputs:
- A per-invocation graph node-set restricted to the listed tickets, with edges drawn from ancestor->descendant relationships discovered via the walker (a listed ticket that is an ancestor of another listed ticket has an edge to it).
- Topological sort yielding levels (sets of tickets with no remaining dependencies); same-level tickets are concurrency-eligible.
- Cycle detection (should not occur with parent-child trees, but defend against it): a cycle aborts with a clear error.
- Tickets with no relationship are independent (concurrency-eligible from the start).

Acceptance:
- [ ] A graph is built from the listed tickets using parent-child relationships
- [ ] Topological order is produced as levels of concurrency-eligible tickets
- [ ] Unrelated tickets are correctly identified as independent
- [ ] Cycles abort with a clear error

Notes: Limit dependency discovery to the listed tickets - do not pull in unlisted relatives. The graph is the operator's set, not the full tree.

OOS:
- Inferring dependencies from anything other than parent-child (e.g. label-based blockers)
- discovering dependencies outside the listed set
- Persisting the graph across invocations (per-invocation only)

#### Brief 03: parallel-dispatch

Goal: Run the graph concurrently, respecting the dependency order.

Inputs: the graph from B02; the tree-aware per-ticket dispatch (so each ticket - including a listed parent - still uses tree-aware behavior).

Outputs:
- A concurrent runner that processes one topological level at a time: all tickets in a level execute in parallel, the next level waits for the previous to complete.
- Configurable max concurrency (a sensible default, e.g. number of cores or a small fixed cap; flag/config overridable).
- Each ticket is dispatched through the tree-aware path, so a listed parent still recurses to its children correctly within its slot.
- Cancellation propagates: ctrl-c stops in-flight work cleanly.

Acceptance:
- [ ] Independent tickets in the same level run concurrently up to the configured cap
- [ ] Dependent tickets wait for their ancestors to complete
- [ ] A listed parent ticket dispatches through tree-aware behavior (children still recurse)
- [ ] Concurrency is configurable and bounded
- [ ] Cancellation stops work cleanly

Notes: Concurrency model is a deliberate choice - level-synchronous is simpler and good enough; full graph-walk-with-readiness is overkill for v1. Document the choice.

OOS:
- Inter-ticket dependencies discovered at runtime (e.g. a ticket producing a new dependency mid-flight)
- cross-machine distribution
- Resource-aware throttling beyond the configured concurrency cap (the cap is the only control)

#### Brief 04: aggregate-and-report

Goal: Per-ticket outcomes assembled into one operator-facing report; failure semantics deliberate.

Inputs: the runner's per-ticket results.

Outputs:
- An aggregated report listing each ticket with its outcome (success / failure / skipped due to ancestor failure), timing, and a brief failure reason where applicable.
- Failure handling for descendants of a failed ancestor: skip and report as `skipped (ancestor T failed)` by default; an opt-in `--continue-past-failure` runs descendants anyway. Document the default.
- Order in the report preserves the topological order (with same-level tickets in input order), not interleaved completion order.

Acceptance:
- [ ] Per-ticket outcomes are aggregated and reported
- [ ] Descendants of a failed ancestor are skipped and clearly reported as such (default), or run with `--continue-past-failure` (opt-in)
- [ ] Report order is topological / input-stable, not completion-interleaved
- [ ] Single-ticket invocation's report is unchanged

Notes: This matches the prior PowerShell tooling's behavior - dependency-respecting skip on ancestor failure was the default there. Confirm during implementation that the default still fits operator expectations.

OOS:
- Persisted run history
- cross-invocation report aggregation
- web/JSON output formats (CLI-readable suffices for v1)

## What done looks like

`build chain 41 42 43` runs all three through chain, deriving a dependency graph from any parent-child relationships among 41/42/43 (e.g. if 41 is an ancestor of 43, 41 runs first; 42 is independent and runs in parallel with 41), executing each via tree-aware dispatch so a listed parent still recurses to its children, and producing an aggregated, topologically-ordered report. Concurrency is bounded by a configurable cap, ancestor failures skip their descendants by default, and single-ticket invocation is untouched. The result matches and modernizes the parallel + dependency-aware behavior the prior PowerShell scripting already had.