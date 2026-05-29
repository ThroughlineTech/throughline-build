# Operation: tree-aware-commands

Make TLB commands aware of parent-child ticket trees. A configurable-depth tree-walk utility, a behavior contract for what each command does when invoked on a parent, and a shared parent-detection path - then per-command parent behaviors. Foundational structural change that cuts across the whole command surface. Independent of the multi-agent work.

## Why this exists

Today most commands assume a leaf ticket. As decompose starts producing parent-child trees, "what does `build implement` do on a parent" becomes a real question for each verb. This op-doc builds the shared tree-walk and parent-detection foundation once, defines the behavior contract, and applies it per command - rather than each command inventing its own parent handling.

The walker is depth-configurable but defaults to stopping at grandchildren in v1 as a deliberate conservative choice: deeper traversal is mechanically possible and the walker should support it, but raising the default waits until we have more confidence in command behavior at depth. The most likely driver to raise it is auto-decompose of L tickets, which can produce deeper trees worth walking; revisit then.

It depends on nothing in the multi-agent stack (trees are a ticketing concept, above the worker contract). It is most useful after decompose exists to produce trees, and it is the prerequisite for multi-ticket-per-command.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Tree foundation: walk utility + parent-detection + behavior contract | - | M |
| B | Per-command parent behaviors | A | M |

A first. B depends on A; within B the per-command briefs are independent.

## Plan A: Tree foundation

### Goal

A reusable tree-walk over Plane parent-child relationships with a configurable depth limit (defaulting to grandchildren in v1), a single "is this a parent, and how do we handle it" path, and a documented behavior contract that the per-command briefs implement.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | tree-walk-utility | Configurable-depth walk over parent -> children -> ..., defaulting to grandchildren | - | src/ThroughlineBuild.Plane/TicketTreeWalker.cs (new), src/ThroughlineBuild.Contracts/ |
| 02 | parent-detection-contract | Shared is-parent path + the per-command behavior contract (documented) | 01 | src/ThroughlineBuild.Cli/ (dispatch), docs/tree-aware-behavior.md (new) |

### Briefs - detail

#### Brief 01: tree-walk-utility

Goal: One tree-walk used everywhere, with a configurable depth limit defaulting to grandchildren in v1.

Inputs: the Plane parent-child API; `IPlaneTicketing`.

Outputs:
- `TicketTreeWalker` that, given a ticket id, returns the tree (parent, children, and further descendants up to the configured depth) with a depth parameter; default depth = grandchildren.
- BFS or DFS (implementer's choice) with the depth cap enforced.
- Single implementation used by every consumer that needs a tree walk; no per-caller re-implementations.

Acceptance:
- [ ] `TicketTreeWalker` returns the correct tree at the configured depth
- [ ] Default depth is grandchildren
- [ ] Depth parameter is configurable and enforced (deeper traversal works mechanically, just not enabled by default)
- [ ] All tree-walking consumers use this single utility

Notes: The depth default is policy, not a structural limit. Auto-decompose of L tickets is a likely future driver to raise it.

OOS:
- Per-command behaviors (Plan B)
- Multi-ticket dispatch (multi-ticket op-doc)
- Raising the default depth in this op-doc (revisit when there is a concrete reason)

#### Brief 02: parent-detection-contract

Goal: A shared "is this a parent?" path and a written contract for each command's parent behavior.

Inputs: the tree walker; the command surface (plan, implement, review, ship, chain, decompose, and the lifecycle verbs if shipped).

Outputs:
- A shared dispatch-time check: is the target ticket a parent (has children)? If so, route to the command's parent behavior.
- `docs/tree-aware-behavior.md` documenting, per command, the behavior on a parent (the contract the Plan B briefs implement). Proposed defaults: implement on a parent refuses (children worked individually); review aggregates child verdicts or refuses; ship ships when all children are Done; chain recurses children; plan on a parent is defined explicitly; lifecycle verbs (close/defer/reopen) optionally cascade to children.

Acceptance:
- [ ] A shared parent-detection path exists at dispatch
- [ ] `docs/tree-aware-behavior.md` specifies each command's parent behavior
- [ ] The contract covers every command that can target a parent, including decompose and lifecycle verbs if present

Notes: The behavior choices are the design content here - flag the contract to Dan for sign-off before Plan B implements it. The set of commands covered depends on what has shipped (decompose, lifecycle): see the roadmap cascade analysis.

OOS:
- Implementing the per-command behaviors (Plan B)
- Recursion over an operator-supplied ticket list (multi-ticket op-doc)
- Plane-side parent/child type classification - the walker reads the existing parent-child relations; how Plane categorizes ticket types is outside scope

## Plan B: Per-command parent behaviors

### Goal

Each command does the right thing on a parent per the Brief 02 contract.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | plan-implement-on-parent | plan and implement parent behavior per the contract | A | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Phases/ |
| 04 | review-ship-on-parent | review (aggregate/refuse) and ship (all-children-Done) parent behavior | A | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Phases/ |
| 05 | chain-on-parent | chain recurses children per the contract | A | src/ThroughlineBuild.Cli/Program.cs |
| 06 | lifecycle-on-parent | close/defer/reopen parent cascade per the contract (only if lifecycle commands shipped) | A | src/ThroughlineBuild.Cli/Program.cs |

### Briefs - detail

#### Brief 03: plan-implement-on-parent

Goal: Define and implement plan/implement on a parent.

Outputs: implement on a parent refuses with guidance to work children (per contract); plan on a parent does the contract-specified thing (e.g. plan the parent's framing, or refuse). Clear messaging either way.

Acceptance:
- [ ] Invoking implement on a parent ticket produces no changes to the repository or to ticket state; the operator receives a message that identifies the parent and points at the children
- [ ] Invoking plan on a parent produces the contract-specified outcome (no child-state effects beyond what the contract permits)
- [ ] Output messaging for both cases includes the parent ticket id and the count of children detected

Notes: Default behavior on implement-on-parent is refuse-with-guidance per the contract; this brief implements the default, not an override path. If a future use case wants implement-on-parent to mean something else (e.g. recurse to children), the contract is amended first and a separate brief implements it - this brief does not anticipate that.

OOS:
- review and ship on a parent (B04)
- chain on a parent (B05)
- Implementing-on-children-from-parent recursion (multi-ticket op-doc territory)
- Auto-decompose suggestion when implement-on-parent is refused (decompose op-doc territory)

#### Brief 04: review-ship-on-parent

Goal: review and ship on a parent.

Outputs: review aggregates child verdicts or refuses (per contract); ship ships the parent when all children are Done (and refuses otherwise with a clear status of which children block).

Acceptance:
- [ ] review on a parent follows the contract
- [ ] ship on a parent ships only when all children are Done; otherwise reports blockers
- [ ] Messaging is clear

Notes: ship-on-parent is the most operator-impactful behavior in this op-doc - the all-children-Done gate is conservative by design and is meant to be bypassable only by completing children, not by overriding here. If an override flag is later wanted, file it separately so the gate stays the obvious default.

OOS:
- plan and implement on a parent (B03)
- chain on a parent (B05)
- Blocking-condition detection beyond child state (failing CI, open PRs, label-based blockers) - the gate is on child state alone
- A ship-anyway override flag - explicitly not surfaced here

#### Brief 05: chain-on-parent

Goal: chain on a parent recurses children.

Outputs: `build chain <parent>` walks children to the configured depth and chains each, in dependency/order, reporting per-child outcomes.

Acceptance:
- [ ] chain on a parent processes children per the contract
- [ ] Per-child outcomes are reported
- [ ] The configured walker depth is respected

Notes: This is the most behavior-rich brief and the bridge toward multi-ticket; keep the recursion logic reusable by the multi-ticket op-doc.

OOS:
- Explicit operator-supplied multi-ticket lists (multi-ticket op-doc)
- Parallel execution of independent children (multi-ticket op-doc; chain-on-parent here is sequential)
- A chain-level depth override flag - depth is set on the walker, not per chain invocation

#### Brief 06: lifecycle-on-parent

Goal: lifecycle verbs on a parent (only if the lifecycle-commands op-doc has shipped).

Outputs: close/defer/reopen on a parent cascade to children per the contract (e.g. close-parent optionally closes open children with the reason), with an opt-out flag.

Acceptance:
- [ ] lifecycle verbs on a parent follow the contract
- [ ] Cascade is explicit and opt-out-able
- [ ] If lifecycle commands are not shipped, this brief is dropped

Notes: Conditional brief - exists only if lifecycle commands are present. See the cascade analysis.

OOS:
- Implementing the lifecycle verbs themselves (lifecycle-commands op-doc)
- Cascade behavior on grandchildren and deeper - the walker's configured depth governs
- Audit logging beyond the existing event-log entries that the lifecycle verbs already emit

## What done looks like

Every command that can target a parent behaves deliberately on one: a single `TicketTreeWalker` (configurable depth, grandchildren default in v1) backs every parent-aware command, a shared dispatch check routes parent targets to their defined behavior, and `docs/tree-aware-behavior.md` is the contract. implement refuses on a parent, ship gates on all-children-Done, chain recurses, review aggregates, and (if present) lifecycle verbs cascade. The reusable recursion is ready for multi-ticket-per-command to build on, and the depth cap can be raised later (likely with auto-decompose) without rebuilding the walker.