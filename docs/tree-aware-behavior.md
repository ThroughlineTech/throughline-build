# Tree-Aware Command Behavior Contract

> **Sign-off gate:** Plan B (per-command parent-behavior implementations) must not begin
> until Dan has reviewed and approved this document. The contract below records the proposed
> defaults; behaviors may be adjusted at sign-off time before any implementation starts.

## How Detection Works

Parent detection is performed by `ParentDetector.HasChildrenAsync` in
`src/ThroughlineBuild.Helpers/ParentDetector.cs`:

```csharp
bool isParent = await ParentDetector.HasChildrenAsync(ticketing, ticket.Uuid, ct);
```

**Important:** the parameter is `ticket.Uuid` (the internal UUID field), NOT the
human-readable identifier (e.g. "TLB-42"). Passing the human-readable id would silently
return zero results because the Plane API query uses `&parent={uuid}`.

The method calls `QueryAsync(new TicketQuery(ParentId: ticketUuid), ct)` and returns
`children.Count > 0`. A single Plane API call; no caching, no depth argument. Use
`TicketTreeWalker.WalkAsync` for full tree scenarios.

## Per-Command Parent Behavior

| Command     | Behavior on a parent ticket                                                                    |
|-------------|-----------------------------------------------------------------------------------------------|
| `plan`      | **Refuse.** Parent containers do not receive independent implementation plans. Plan each child individually. |
| `implement` | **Refuse.** Work child-by-child. Attempting to implement a parent directly is almost always a mistake. |
| `review`    | **Aggregate child states.** Pass if all children are Done; Rework if any child is in-flight (InProgress/InReview); Fail otherwise (children in Cancelled or blocked states count as Fail). |
| `ship`      | **Validate then transition.** Confirm all children are in terminal-Done state, then transition the parent ticket to Done. Abort with a clear error if any child is not Done. |
| `chain`     | **Recurse.** Run chain on each non-terminal child in sequence (skip Done and Cancelled children). The parent's own state is updated after all children complete. |
| `decompose` | **Refuse.** The ticket already has children; re-decomposing is almost always a mistake. Use `build chain` to work the existing children. |
| `close`     | **Cascade.** Close all non-terminal children (those not already Done or Cancelled), then close the parent. |
| `defer`     | **Cascade.** Defer all non-terminal children, then defer the parent. |
| `reopen`    | **Parent only.** Reopen the parent ticket; leave children in their current states. Children may have individual plans and branches; reopening them automatically could cause confusion. The operator reopens children individually if needed. |
| `rework`    | **Not applicable.** A parent ticket cannot reach InProgress in normal flow; it has no work artifact to rework. If this state is somehow reached, refuse with a diagnostic message. |
| `amend`     | **Direct ticket only.** Amend edits the named ticket's metadata/content; `--parent` explicitly reparents that ticket. |

## Out-of-Scope Commands

The following commands are excluded from parent-behavior gating:

- `new` / `scaffold` - create operations; no existing ticket context.
- `list` - query only; no state change.

## Rationale for Key Choices

**decompose refuses on a parent:** decompose's purpose is to create children. A ticket that
already has children has already been decomposed. Refusing with a clear message prevents
accidental re-decomposition and double-counting of work.

**reopen does not cascade:** children may have individual branches, plans, and in-progress
work. Auto-reopening them on parent reopen would invalidate completed work. The operator
must decide which children to reopen.

**ship validates all children Done:** partial completion is not a valid ship state for a
parent. If any child is not Done the ship is aborted, not partially applied.

**chain skips Done and Cancelled:** Done children need no further work; Cancelled children
are intentionally excluded. Only in-flight or unstarted children are chained.
