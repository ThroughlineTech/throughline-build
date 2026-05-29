# Operation: multi-ticket-prerequisites

Make the build pipeline concurrency-safe at the main-checkout level so op-multi-ticket-commands' parallel-dispatch brief is correct as written. Removes the v1 single-ticket CLI guard at `Program.cs:70-78`, adds a main-worktree-path-scoped in-process mutex around the two windows in ship that touch the main checkout (the fetch at Step 4 and the fast-forward merge at Step 8), and verifies serialization with concurrent-run tests. After this op-doc, multi-ticket parallel execution is mechanically safe; the operator-facing CLI surface for it is op-multi-ticket-commands.

## Why this exists

The recon confirmed two things about TLB's working-dir model. First, the worktree-per-ticket design (each ticket runs in its own `.worktrees/ticket-<slug>` directory, per `ImplementPhase.cs:133-146` and the `PhaseWorktreeLayout` helper) already isolates worker execution - parallel workers in separate worktrees do not share working trees, branches, or index state. Second, ship's main-checkout operations are unguarded: the fetch at Step 4 and the fast-forward merge at Step 8 both touch the main worktree, and there is no code-level concurrency protection in `ChainPhase.cs:45-176`, `DefaultChainRunner.cs:20-28`, or anywhere else around git state. Two concurrent ship invocations against the same repo would race on those two windows.

There is also an explicit CLI refusal at `Program.cs:70-78` that rejects multi-ticket chain invocations entirely with the message "build chain accepts exactly one ticket ID in v1; multi-ticket dispatch is planned for a future release." That refusal is the first thing an operator hits when trying multi-ticket; the concurrency gap is what would bite them next if the refusal were removed.

Both gaps are small. Both are prerequisite to op-multi-ticket-commands' parallel-dispatch brief actually working as designed. The fix is precise: remove the CLI guard, add a path-scoped mutex around the two main-checkout windows, verify with tests. Worktree-level work needs no change. The lock primitive is built to be reusable so any future TLB code path that touches the main checkout can call it without re-inventing the gate.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Multi-ticket prerequisites: CLI guard removal, main-worktree mutex, concurrent-run verification | - | S |

Single plan; briefs sequential.

## Plan A: Multi-ticket prerequisites

### Goal

After this plan, the CLI no longer refuses multi-ticket invocations on principle; the two main-checkout windows in ship are serialized by a reusable, path-scoped, in-process mutex; concurrent ship invocations against the same repo serialize cleanly while concurrent invocations against different repos run in parallel; tests pin the behavior.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | remove-cli-v1-guard | Remove the principled multi-ticket refusal at Program.cs:70-78 and update usage text | - | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |
| 02 | main-worktree-mutex | Path-scoped in-process mutex; applied to the fetch and ff-merge windows in ship | - | src/ThroughlineBuild.Git/, src/ThroughlineBuild.Phases/ShipPhase.cs |
| 03 | concurrent-run-verification | Tests proving serialization on same path, parallelism across paths, and cancellation release | 02 | tests/ |

### Briefs - detail

#### Brief 01: remove-cli-v1-guard

Goal: Remove the explicit multi-ticket refusal in the chain verb dispatch and update any usage text that still claims v1 accepts a single ticket only. After this brief, the CLI does not block multi-ticket invocations on principle. The actual parsing and dispatch of multi-ticket arguments are owned by op-multi-ticket-commands; in the interim between this op-doc landing and that op-doc landing, an unparsed multi-arg invocation produces a clear "not yet implemented" diagnostic from the dispatch layer rather than the v1-guard message.

Inputs: the guard at `Program.cs:70-78` and any sibling messages; the usage text in `CliUsage.cs`; the existing single-ticket chain behavior.

Outputs:
- The principled refusal at `Program.cs:70-78` is removed.
- Usage text no longer claims chain accepts exactly one ticket ID; phrasing is updated to reflect "chain takes one ticket today; multi-ticket support is in development."
- Multi-arg invocations of chain (and any other verb that previously routed through the v1 guard) produce a clean "not yet implemented" response from the dispatch layer rather than the removed v1-guard message.
- Single-ticket invocations of every affected verb behave exactly as today.

Acceptance:
- [ ] `build chain 41 42` no longer produces the "v1 accepts exactly one ticket ID" error
- [ ] Single-ticket chain invocations behave exactly as today
- [ ] Usage text accurately reflects current state without the v1-guard messaging
- [ ] No regression in any existing verb's single-ticket behavior

Notes: This brief alone does not make multi-ticket dispatch work - that arrives with op-multi-ticket-commands. It only removes the principled refusal so the concurrency-safety work in B02 has a path to land usefully. A user running `build chain 41 42` between this op-doc and op-multi-ticket-commands will get a less-confident "not yet implemented" response; that is an acceptable interim. The version-numbered v1 wording was the right call when it was written - it is no longer accurate now that multi-ticket is being actively designed.

OOS:
- Implementing multi-ticket arg parsing (op-multi-ticket-commands B01 owns)
- Implementing the dependency graph or parallel dispatch (op-multi-ticket-commands B02 / B03 own)
- Updating usage text for the eventual multi-ticket syntax (the syntax lands with op-multi-ticket-commands)
- Any change to single-ticket verb behavior

#### Brief 02: main-worktree-mutex

Goal: A reusable lock primitive scoped to the main-worktree path, applied to the two windows in ship that touch the main checkout (the fetch at Step 4 and the fast-forward merge at Step 8). After this brief, two concurrent ship invocations against the same main-worktree path serialize their main-checkout operations cleanly with no corruption and predictable ordering; concurrent invocations against different main-worktree paths run in parallel.

Inputs: ShipPhase.cs at the fetch (Step 4) and ff-merge (Step 8) sites; the existing locks in `JsonlEventSink._lock` and `PlaneTicketingClient._stateLock` as patterns for how mutexes live in TLB; the worktree-path resolution already in `PhaseWorktreeLayout`.

Outputs:
- A new helper exposing `WithLockAsync(string mainWorktreePath, Func<CancellationToken, Task> action, CancellationToken ct)` semantics. The lock is keyed by the normalized absolute path of the main worktree. Two callers with the same normalized path serialize; two callers with different paths run in parallel.
- Lock state is held in a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by normalized path. Entries are created on demand and persist for the process lifetime (the dictionary itself is process-scoped state on the helper).
- Normalization is consistent and resilient to common path variations (trailing slashes, case on platforms where it matters, symlink expansion if relevant).
- ShipPhase wraps Step 4 (fetch) inside `WithLockAsync` for the main worktree's path; wraps Step 8 (fast-forward merge) inside the same `WithLockAsync` call. The two windows are taken separately, not held continuously across the rebase step which runs in the feature worktree and does not touch main.
- The lock is acquired with cancellation support; a cancelled invocation releases its hold so a queued caller can proceed.
- The helper is callable from any future TLB code path that operates against the main checkout.

Acceptance:
- [ ] Two concurrent operations against the same main-worktree path execute serially under the lock; one waits while the other holds
- [ ] Two concurrent operations against different main-worktree paths execute in parallel
- [ ] Path normalization treats trivially-equivalent paths (trailing slash, case where applicable) as the same lock
- [ ] A cancelled operation releases its lock hold so a queued operation proceeds
- [ ] Ship's fetch and ff-merge steps are protected by the lock; the feature-worktree rebase step is not held under the lock
- [ ] The helper is callable from any future code path that operates against the main checkout (no ship-specific coupling in its API)

Notes: The recon noted "ProcessGitClient may or may not serialize git ops internally - I did not find evidence it does." Verify during implementation. If ProcessGitClient already serializes, this lock is belt-and-suspenders for the specific ship windows; if it does not, this is the only protection. Either way the lock is correct - it is the load-bearing layer at the right scope (main-worktree operations). The mutex is non-reentrant; ship's flow does not take it recursively today, and a future caller that needs reentrancy should document the case rather than work around the contract.

OOS:
- Locking around git operations in the feature worktree (each worktree is isolated; no lock needed)
- Locking around Plane API calls (the existing PlaneTicketingClient._stateLock covers that)
- Locking around event-log writes (the existing JsonlEventSink._lock covers that)
- A distributed or cross-process lock (in-process is sufficient; concurrent processes against the same repo are operator error, not something to defend against at this layer)

#### Brief 03: concurrent-run-verification

Goal: Tests that exercise concurrent operations and verify the lock produces correct serialization without corruption. Without this brief, the lock is unverified and a future regression could silently remove its protection.

Inputs: the lock helper from B02; existing test fixtures and patterns for ShipPhase and IGitClient; synchronization primitives suitable for deterministic concurrency tests (TaskCompletionSource and manual signaling).

Outputs:
- A test that drives two concurrent operations against the same main-worktree path, holds one inside the locked region at a known synchronization point, and asserts the other is blocked until the first releases.
- A test that drives two concurrent operations against different main-worktree paths and asserts both proceed in parallel (neither blocks the other).
- A test that cancels an operation holding the lock and asserts a queued operation proceeds promptly afterward.
- A test that exercises ship's two locked windows (fetch and ff-merge) and asserts each window takes the lock for the right scope (no leakage into the feature-worktree rebase step).
- All tests use deterministic synchronization (TaskCompletionSource, manual signals) rather than wall-clock timing; no Thread.Sleep-based assertions.
- All tests pass under the AOT-compatible test harness.

Acceptance:
- [ ] The same-path concurrency test confirms serialized execution under the lock
- [ ] The different-paths concurrency test confirms parallel execution across path boundaries
- [ ] The cancellation test confirms the lock is released on cancellation and a queued operation proceeds
- [ ] The ship-windows test confirms the lock is taken for fetch and ff-merge but not for the feature-worktree rebase between them
- [ ] All tests are deterministic - no Thread.Sleep, no wall-clock-timing assertions
- [ ] All tests pass under AOT-compatible execution

Notes: Concurrency tests are inherently flaky if not written carefully. Use deterministic synchronization primitives at every observation point - any "B is waiting on A" assertion needs a signaled barrier, not a sleep. Existing TLB test patterns for async coordination should provide the right primitives; if they do not, introduce them sparingly and keep them local to this test file. Live network or real git remotes are not in scope - use stubs and in-memory fixtures consistent with existing TLB tests.

OOS:
- Performance benchmarking of the lock (a separate concern when there is a reason)
- Tests of multi-process or cross-machine scenarios (in-process locking only is in scope)
- Tests of multi-ticket arg parsing or dispatch behavior (those tests live with op-multi-ticket-commands)
- Stress tests with many concurrent invocations beyond what is needed to verify correctness

## What done looks like

The v1 single-ticket guard at `Program.cs:70-78` is removed; usage text reflects current state. The main-checkout's two windows in ship (fetch and fast-forward merge) are serialized by a reusable, path-scoped, in-process lock that any future TLB code path touching the main checkout can call. Concurrent ship invocations against the same repo serialize cleanly without corruption; concurrent invocations against different repos run in parallel. Tests pin the serialization, the cross-path parallelism, the cancellation behavior, and the ship-window scoping. op-multi-ticket-commands' Brief 03 (parallel-dispatch) now has the concurrency-safe foundation it assumed; that op-doc's design becomes correct without rewriting.