# Tree-aware behavior implementation notes

The operator-facing behavior matrix lives in the
[Throughline Build user guide](throughline_build_userguide.md#parent-tickets).
The recursive scheduler, dependency ordering, branch topology, depth limits,
and outcomes are documented in the
[recursive-chain deep dive](build-grandparent-chain.md). This stable page records only
the parent-detection convention shared by those documents.

## Parent detection

Plane parent queries require the ticket's internal `Uuid`, not its
human-readable ID:

```csharp
var children = await ticketing.QueryAsync(
    new TicketQuery(ParentId: ticket.Uuid), ct);
```

Passing a value such as `TLB-42` as `ParentId` would not match Plane's internal
parent field.

[`ParentDetector.HasChildrenAsync`](../src/ThroughlineBuild.Helpers/ParentDetector.cs)
wraps that query and returns whether any direct children exist. It is a tested
helper, not a universal command gateway: the shipped plan, implement, review,
ship, chain, close, defer, and reopen paths currently perform their own direct
child query.

For bounded tree traversal,
[`TicketTreeWalker.WalkAsync`](../src/ThroughlineBuild.Helpers/TicketTreeWalker.cs)
uses breadth-first queries and a depth argument. The
[`ChainPhase`](../src/ThroughlineBuild.Phases/ChainPhase.cs) scheduler has its
own traversal because it also owns dependency order, cycle detection,
integration branches, and child outcomes.

When changing parent behavior, update the user-guide matrix and the deep dive.
Tests must query children by `ticket.Uuid` and cover both leaf and parent cases.
