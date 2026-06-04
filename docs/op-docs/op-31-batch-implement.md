# Operation: batch-implement cohesive ticket groups

Design a batch-implement mode for chains where several sibling tickets are really one design split into bookkeeping units. The mode should let one implement worker hold the full design context in a single session, commit once per ticket, and then hand the combined stack to review. It is not a replacement for isolated per-ticket implementation; it is an explicit optimization for cohesive groups where repeated cold starts are the dominant cost.

## Problem

The measured help-system chain TLB-419..422 was four tightly coupled tickets: model and registry, two sibling renderers, then factory and dispatch wiring. The current chain ran a fresh implement worker per ticket. Because workers are stateless and cold-boot, each child re-read the same growing implementation context from scratch. TLB-422 alone read 2.4M cache tokens on top of three prior commits; the run logged about 7.9M cache-read tokens total, with roughly 91% of cost in implement. The process also paid for five separate review passes over one conceptual feature.

This is the wrong shape for cohesive chains. The useful isolation is the per-ticket commit boundary, state transition, and reviewable history; the expensive part is repeatedly re-priming a worker that needs the same design in memory.

## Current Flow Inventory

`ChainPhase.RunImplementReviewLoopAsync` currently owns the implement -> review loop. For each round it mints a new implement session id, builds phase-scoped options, constructs an `ImplementPhase`, runs it, then immediately runs one review pass. A rework verdict loops by creating `ReviewFeedback` and spawning another implement phase. This is deliberately deterministic and file/git/event-log mediated, but every ticket and every rework round is a separate worker invocation.

Parent chains already serialize children in `RunParentChainAsync`. They compute dependency levels, print the dispatch order, then still run each child by recursively calling `RunAsync(childOptions)`. The code comment is explicit: parent chains dispatch one child at a time so each successful child ships into the local target before the next child resolves its base. That gives predictable stacking but preserves cold-start-per-child behavior.

`SequentialChainDispatcher` is also concurrency-1. It takes an ordered ticket list, synthesizes linear ancestor edges, and awaits `runTicket` for each ticket before moving to the next. It is a conductor, not an execution engine.

Worktree handling already has the substrate a batch mode needs. `PhaseWorktreeLayout` gives canonical `ticket/{slug}` branches and `.worktrees/ticket-{slug}` paths. Parent chains create one shared worktree on a placeholder `chain/{slug}` branch; each child then creates its own `ticket/{id}` branch inside that shared worktree through `ImplementPhaseOptions.SharedWorktreePath`. Ship skips decruft during the parent chain and cleanup happens once at chain end. The current system therefore has a shared filesystem/git lane, but not a shared implement session.

Commit attribution is per ticket today because `ImplementPhase` expects one worker result with one `commit_sha`, verifies it against the actual worktree HEAD, writes an `[implemented_at: <sha>]` comment with the branch name, and transitions that ticket from InProgress to InReview. Any batch design must preserve these markers and Plane transitions per child.

## Proposal

Add an explicit batch-implement path for cohesive sibling groups:

1. The chain conductor selects a declared group of sibling tickets.
2. It creates or reuses the same shared chain worktree path the parent-chain path already uses.
3. It spawns one implement worker with a batch brief containing all selected ticket descriptions, ordering constraints, current chain commit pointer, and the required output contract.
4. The worker implements the group in one session, but commits after each ticket, preserving one logical commit per ticket.
5. The worker returns a structured list of per-ticket results: ticket id, commit sha, branch or stack position, files changed, and summary reference for each ticket.
6. The conductor verifies the worktree is clean, verifies each reported commit is present in order, writes the existing per-ticket implemented_at marker and summary, and transitions each ticket through the normal Plane states.
7. Review runs after the batch over the combined diff or stack, then any rework feedback is targeted back into the same batch context when the defect crosses ticket boundaries.

The important constraint is that batching changes the worker session boundary, not the history boundary. The output history should still be clean and bisectable: TLB-419 commit, then TLB-420 commit, then TLB-421 commit, then TLB-422 commit. Review and ship can reason about a stack rather than an opaque squashed change.

## When To Batch

Batch only when the group is one design:

- The tickets are siblings under the same parent or explicitly selected together.
- They modify shared files or adjacent call paths.
- Later tickets consume interfaces introduced by earlier tickets.
- Reviewing any ticket in isolation would require rereading most of the sibling context.
- The operator would naturally describe the group as one feature split for tracking.

Do not batch when isolation is the point:

- Tickets touch unrelated areas.
- One ticket can fail or be deferred without invalidating the others.
- Parallel implementation would be valid.
- The risk profile benefits from independent fresh-worker scrutiny.
- A child is a pure cleanup, follow-up, or opportunistic enhancement rather than part of the same design.

Declaration should be explicit at first. Recommended order:

1. Add a `batch-implement` label or equivalent workflow label on a parent ticket to opt its eligible children into one batch.
2. Support an explicit CLI flag such as `build chain TLB-418 --batch-implement TLB-419,TLB-420,TLB-421,TLB-422` for operator-selected groups.
3. Later, consider parent-ticket metadata or description markup for stable batch groups.

Do not infer batching solely from sibling status. A parent with many children is not automatically cohesive.

## Invariants

The dispatcher remains a deterministic conductor. It may choose a batch execution unit, but it should not become an agent memory store or hidden state machine. Handoffs stay mediated by files, git commits, Plane comments, and event log records.

Per-ticket state transitions are preserved. Each child still moves Ready -> InProgress -> InReview, and later review/ship behavior remains observable per ticket. If the batch worker succeeds on ticket 1 and fails on ticket 2, the conductor must have enough structured output to leave ticket 1 with a real commit marker and ticket 2 in a recoverable state.

Per-ticket commit markers are preserved. The existing `[implemented_at: <sha>]` comment shape is still the authoritative link from Plane to git. Batch mode should extend the metadata shape, not replace the marker.

The shared worktree model remains explicit. Batch mode should reuse the parent chain's shared worktree/placeholder branch concept rather than introducing an unrelated workspace layout. That keeps cleanup, branch naming, and resume behavior anchored in existing code.

Review remains independent from implementation. One implement worker holding the design does not mean one all-trusting review. The review worker still gets the actual diff and ticket specs from persisted state, not private implement-session memory.

## Review Strategy

Default to one review pass over the combined batch diff, with the reviewer instructed to check each ticket's acceptance criteria and the seams between commits. Allow a second pass when the first review returns rework or when the batch exceeds a configurable size threshold.

This trades granularity for cost. The existing per-rung review caught useful rework in TLB-419, and a large combined diff can reduce per-line fidelity. That is the main quality risk. The mitigation is to preserve per-ticket commits and require the review brief to enumerate commit ranges per ticket, so the reviewer can scan the stack by ticket while still seeing the integrated design.

Do not drop per-ticket review forever as a policy. Batch review should be a mode with a conservative size limit, not the only chain behavior.

## Failure And Rework

Partial failure is the hardest case. The batch contract should require the worker to commit after each completed ticket and stop before starting the next ticket if the design becomes blocked. The conductor can then mark completed children with implemented_at comments and leave the failing child InProgress with the failure reason.

Rework targeting needs two routes:

- Localized rework: if review feedback maps cleanly to one ticket commit, run a normal per-ticket rework on that ticket's branch/worktree.
- Cross-ticket rework: if feedback concerns an interface spanning the group, re-enter batch context with the same ticket group and require new follow-up commits or amended per-ticket commits according to the project's history policy.

The first implementation should avoid history rewriting after markers are posted. Prefer additive rework commits tied to the affected ticket unless the ship path already has a safe amend workflow for unshipped stacks.

## Cost Estimate

For a four-ticket cohesive group like TLB-419..422, the expected win is roughly three avoided implement cold starts plus fewer review passes. Using the observed run as the anchor, the upper-bound avoidable cache-read cost is most of the 7.9M cache-read tokens spent re-priming the implement stack. The retained cost is one larger batch brief, one warm implementation session, per-ticket commit work, and one or two reviews.

A practical estimate: a 4-ticket cohesive batch should cut implement cache-read cost by about 50-70% and total implement-phase cost by a similar order, assuming the worker can keep the shared design in one session and the combined brief does not approach the context limit. Wall time should also improve because review count drops from per-ticket to one or two passes.

## Risks And Open Questions

- Per-ticket attribution from one session: the worker result needs a structured array, and the conductor must verify each commit against git rather than trusting reported shas.
- Partial failure mid-batch: define exactly which tickets get markers and which remain InProgress when only part of the stack exists.
- Rework targeting: decide when to use per-ticket rework versus re-entering batch mode.
- State sequencing: decide whether to transition all tickets to InProgress up front or transition each ticket immediately before its commit is verified.
- Review fidelity: combined review may miss issues a per-ticket review would catch.
- Resume behavior: interrupted batch runs need a deterministic way to reconstruct completed ticket commits from git and Plane comments.
- Size limits: batching should cap ticket count, diff size, and maybe estimated context before falling back to per-ticket spawning.

## Implementation Plan For A Future Ticket

First implementation should be deliberately narrow: add an opt-in batch path for explicit sibling groups only, reuse the parent chain shared worktree, introduce a batch implement brief/output contract, and teach the conductor to verify and post per-ticket markers from a structured result. Keep shipping and standalone `build implement` unchanged. Run one combined review over the resulting stack, with per-ticket commit ranges in the review brief.

Out of scope for the first build: automatic cohesion detection, parallel batch execution, rewriting posted commits, replacing all parent chains, and changing Plane's core state model.
