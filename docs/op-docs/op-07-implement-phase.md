# Operation: build-implement-phase

Add the implement phase to the `build` binary. After op-07 lands, a ticket can be planned (op-03) and then implemented (op-07): the binary creates a feature worktree and branch, dispatches an agentic worker to perform the code changes per the plan, captures the worker's commit SHA, and transitions the ticket Ready -> InProgress -> InReview. This is the second phase on the critical path to cutover.

## Why this exists

After op-06, the new system can plan a ticket (Backlog -> Ready) and run ticket-state commands (amend, close, defer, reopen) but cannot advance the ticket further. The full Agile spine - plan, implement, review, ship - exists only in the old slash-command system, where every step pays the persistent-Opus cost shape that the May 23 comparison run measured at ~9x what the new architecture spends for equivalent work.

Implement is the longest-running phase by wall-clock and the highest-token by output volume (the worker actually writes code). It is also the phase that most exercises the agentic-worker tier: planning is constrained to producing one HTML blob and three labels; implement is open-ended within the worktree. If the architecture's "thin orchestrator + heavy worker" cost shape holds for implement, it holds for the whole spine.

Implement is also where two new shared abstractions earn their place. The `IWorkflowPhase` interface gets extracted (PlanPhase becomes the first implementor; ImplementPhase the second). The `IGitClient` surface grows the worktree CREATION method op-06 deferred. Both extractions are small and pay for themselves immediately when op-08 and op-09 land.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Shared foundations | - | M |
| B    | Implement phase composition | A | L |

Plan A introduces the abstractions and git extensions both implement and downstream phases will consume. Plan B composes them into the actual implement flow with brief, phase class, and CLI wiring. Within Plan A, briefs are independent. Plan B briefs are sequential.

## Plan A: Shared foundations

### Goal

Three pieces of shared scaffolding: an `IWorkflowPhase` interface extracted from PlanPhase's existing shape; an extension to `IGitClient` adding worktree creation and head-resolution; and an `IPhaseWorktreeLayout` helper that names branches and worktree paths consistently across implement, review, and ship.

Brief sequence: B01-B03 are independent. Run all three in parallel.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | workflow-phase-interface | Extract `IWorkflowPhase` from PlanPhase | - | src/ThroughlineBuild.Contracts/IWorkflowPhase.cs, src/ThroughlineBuild.Phases/PlanPhase.cs, tests/ThroughlineBuild.Phases.Tests/PlanPhaseInterfaceTests.cs |
| 02 | git-worktree-create | Add `CreateWorktreeAsync` and `HeadShaAsync` to `IGitClient` | - | src/ThroughlineBuild.Contracts/IGitClient.cs, src/ThroughlineBuild.Phases/PlanPhase.cs (ProcessGitClient class), tests/ThroughlineBuild.Git.Tests/WorktreeCreateTests.cs |
| 03 | phase-worktree-layout | Helper that derives branch name and worktree path from ticket ID + title | - | src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs, tests/ThroughlineBuild.Helpers.Tests/PhaseWorktreeLayoutTests.cs |

### Briefs - detail

#### Brief 01: workflow-phase-interface

Goal: Promote PlanPhase's shape into an `IWorkflowPhase` interface so ImplementPhase, ReviewPhase, and ShipPhase share a contract. The interface is intentionally small: a phase identifier, the run method, and a typed result record common across phases.

Inputs:
- The existing `PlanPhase` class and its `PlanResult` record
- The `Phase` enum from `ThroughlineBuild.Contracts.Models`

Outputs:
- `IWorkflowPhase` interface in `ThroughlineBuild.Contracts`. Shape:

```csharp
public interface IWorkflowPhase
{
    Phase Phase { get; }
    Task<PhaseResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
}

public record PhaseResult(
    bool Success,
    string TicketId,
    Phase Phase,
    string? FailureReason,
    IReadOnlyDictionary<string, string> Outputs);
```

- `PlanPhase` updated to implement `IWorkflowPhase`. The existing `RunAsync` signature already matches; `Phase` property returns `Phase.Plan`. The existing `PlanResult` record stays as the strongly-typed return of `PlanPhase.RunAsync` for callers that want it (the CLI), and PlanPhase ALSO exposes the interface's `RunAsync` that converts `PlanResult` into `PhaseResult` with `Outputs` carrying `risk_label`, `size_label`, `planned_at_sha`
- `PhaseResult.Outputs` is `IReadOnlyDictionary<string, string>` (string-to-string for portability across the event log)
- xUnit test confirms `PlanPhase` is assignable to `IWorkflowPhase` and roundtrips a successful plan through the interface

Acceptance:
- [ ] `IWorkflowPhase` exists and is implemented by `PlanPhase`
- [ ] `PhaseResult` exists with the fields shown
- [ ] The existing `PlanPhase.RunAsync(string, string, CancellationToken)` signature still returns `PlanResult` for typed callers
- [ ] An interface-typed call (`((IWorkflowPhase)planPhase).RunAsync(...)`) returns a `PhaseResult` whose `Outputs` dictionary contains the three plan keys when the run succeeds
- [ ] `Phase` property returns `Phase.Plan` on `PlanPhase`
- [ ] All existing PlanPhase tests pass; the new interface test passes

Notes: Keep both signatures (`PlanResult` returning method and `IWorkflowPhase.RunAsync`) on PlanPhase rather than collapsing one into the other. The typed method serves CLI callers that need structured fields. The interface method serves any future polymorphic dispatcher (notably ChainPhase in op-11). Method overloading by return type isn't legal in C#; use explicit interface implementation for the `IWorkflowPhase.RunAsync` member.

OOS:
- Do not retrofit `ITicketCommand` (op-06) to share this interface; commands and phases are distinct categories
- Do not add a "before/after" hook system to the interface
- Do not introduce a phase registry or DI container at this point
- Do not read claude-config source

#### Brief 02: git-worktree-create

Goal: Add `CreateWorktreeAsync` and `HeadShaAsync` to `IGitClient`. The first creates a new worktree at a given path on a new branch off a base ref; the second reads the current HEAD SHA of a given worktree.

Inputs:
- The existing `IGitClient` interface and `ProcessGitClient` implementation (which lives inside `src/ThroughlineBuild.Phases/PlanPhase.cs` per the current code layout; leave that file location alone)
- Git's `worktree add` documentation

Outputs:
- `IGitClient.CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)` returning `WorktreeCreateResult`
- `IGitClient.HeadShaAsync(string worktreePath, CancellationToken ct)` returning `string` (the SHA at HEAD of that worktree)
- New `WorktreeCreateResult` record: `(bool Success, string? FailureReason, string? AbsolutePath)`
- `ProcessGitClient` implementations:
  - `CreateWorktreeAsync` wraps `git worktree add -b <newBranch> <worktreePath> <fromRef>` invoked with `WorkingDirectory = mainWorktreePath`
  - `HeadShaAsync` wraps `git rev-parse HEAD` invoked with `WorkingDirectory = worktreePath`
- xUnit tests using a temp git repo:
  - Create a worktree, confirm it appears in `ListWorktreesAsync` and its HEAD matches the requested base
  - Attempt to create a worktree at a path that already exists; confirm `WorktreeCreateResult(false, ...)` rather than throw
  - Read the HEAD of an existing worktree; confirm a 40-char SHA returns

Acceptance:
- [ ] Both methods are on the interface and implemented in `ProcessGitClient`
- [ ] `CreateWorktreeAsync` succeeds against a clean repo and returns the worktree's absolute path in `WorktreeCreateResult.AbsolutePath`
- [ ] `CreateWorktreeAsync` fails fast with a populated `FailureReason` when the target path exists, the branch already exists, or the base ref does not resolve
- [ ] `HeadShaAsync` returns the 40-character SHA at HEAD of the named worktree
- [ ] Neither method throws on git-level failure; both return the structured result
- [ ] xUnit tests pass against a temp git repo created in `[Fact]` setup

Notes: The branch-already-exists case is worth handling cleanly: implement-phase reruns on the same ticket after a worker failure should not blow up because the prior run left the branch behind. For v1, `CreateWorktreeAsync` reports the failure verbatim and the caller (ImplementPhase) decides what to do; recovery semantics are a follow-up.

OOS:
- Do not add `git worktree add --detach` support (always create a branch)
- Do not add a "force overwrite existing worktree" flag (use the decrufter from op-06 first)
- Do not implement push, fetch, or rebase here (those land in op-09)
- Do not read claude-config source

#### Brief 03: phase-worktree-layout

Goal: A pure helper that, given a ticket ID and title, returns the canonical branch name and worktree path used across implement, review, and ship phases. Removes ad-hoc slug-and-path construction from the phase classes.

Inputs:
- `SlugBuilder` from op-02 (already produces a lowercase-hyphenated slug)

Outputs:
- `src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs` with:

```csharp
public static class PhaseWorktreeLayout
{
    public static PhaseWorktreeNames Compute(string ticketId, string title, string mainWorktreePath);
}

public record PhaseWorktreeNames(
    string Slug,           // e.g. "tlb-42-add-implement-phase"
    string BranchName,     // e.g. "ticket/tlb-42-add-implement-phase"
    string WorktreePath);  // absolute path, e.g. "/repo/.worktrees/ticket-tlb-42-add-implement-phase"
```

- xUnit tests covering: simple ticket ID + title, title with special characters (round-trips through SlugBuilder), absolute path resolution

Acceptance:
- [ ] `Compute("TLB-42", "Add implement phase", "/repo")` returns:
  - `Slug = "tlb-42-add-implement-phase"`
  - `BranchName = "ticket/tlb-42-add-implement-phase"`
  - `WorktreePath = "/repo/.worktrees/ticket-tlb-42-add-implement-phase"` (using platform path separator)
- [ ] Slug construction delegates to `SlugBuilder.BuildBranchSlug`; no parallel slug logic in this helper
- [ ] Worktree path is always absolute (combine with the provided `mainWorktreePath`)
- [ ] No I/O in this helper

Notes: This is intentionally a thin, pure helper. The convention `ticket/<slug>` for branches and `.worktrees/ticket-<slug>` for paths matches what the existing slash-command system uses; preserving these names keeps the new system's branches recognizable to humans cross-referencing against the old system during the dogfooding period.

OOS:
- Do not consult git or the filesystem
- Do not validate that the path is writable or that the branch is available
- Do not implement a reverse mapping (worktree path -> ticket ID)
- Do not read claude-config source

## Plan B: Implement phase composition

### Goal

Compose the Plan A foundations into the actual implement flow. An `ImplementBriefBuilder` produces the worker's brief from ticket state. An `ImplementPhase` class orchestrates worktree creation, worker dispatch, commit-SHA capture, and state transitions. The CLI gains a `build implement <id>` subcommand.

Brief sequence: B04 brief-constructor first. B05 phase depends on B04. B06 CLI depends on B05.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | implement-brief-builder | Pure function `(Ticket, RepoState, PhaseWorktreeNames) -> Brief` | - | src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs, tests/ThroughlineBuild.Briefs.Tests/ImplementBriefBuilderTests.cs |
| 05 | implement-phase | `ImplementPhase` class orchestrating the implement flow | 04 | src/ThroughlineBuild.Phases/ImplementPhase.cs, tests/ThroughlineBuild.Phases.Tests/ImplementPhaseTests.cs |
| 06 | implement-cli | `build implement <id>` subcommand with config wiring | 05 | src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ImplementCliTests.cs |

### Briefs - detail

#### Brief 04: implement-brief-builder

Goal: A pure function that consumes a Ticket (with its planned description), a RepoState (current main SHA, top-level entries), and the computed `PhaseWorktreeNames`, and produces the Brief the worker will receive.

Inputs:
- `Ticket` record (its `DescriptionHtml` contains the plan_html that PlanPhase appended in op-03)
- `RepoState` (already defined in `ThroughlineBuild.Briefs` for PlanBriefBuilder)
- `PhaseWorktreeNames` from Plan A Brief 03

Outputs:
- `src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs` with:

```csharp
public static class ImplementBriefBuilder
{
    public static Brief Build(Ticket ticket, RepoState repo, PhaseWorktreeNames worktreeNames);
}
```

- The returned `Brief.Instruction` is a markdown prompt that:
  - States the worker's job: implement the plan recorded in the ticket description, committing logical units
  - Includes ticket ID, title, type, size, risk
  - Includes the ticket's full HTML description (which contains the plan from PlanPhase)
  - States that the worker is running inside the feature worktree at `worktreeNames.WorktreePath`, on branch `worktreeNames.BranchName`, off `repo.MainSha`
  - Specifies the WORKER_RESULT envelope to emit at the end, with `metadata.commit_sha` (HEAD of the feature branch after all commits) and `metadata.files_changed` (list of paths relative to the worktree root)
  - States that the worker must commit its changes locally on the feature branch; force-push, rebase, and main-branch mutation are forbidden
- `Brief.Phase == Phase.Implement`
- `Brief.AllowedWrites` is an empty list (the worktree itself acts as the write boundary; the worker is unconstrained inside it)
- `Brief.Context` includes `main_sha`, `branch`, `worktree_path`
- xUnit tests covering: minimal ticket, ticket with rich description, ticket with relations carried through

Acceptance:
- [ ] `Build` is a pure function (no I/O)
- [ ] Returned `Brief.Phase == Phase.Implement`
- [ ] Instruction includes the WORKER_RESULT envelope template with `commit_sha` and `files_changed` keys in metadata
- [ ] Instruction includes the ticket's `DescriptionHtml` so the worker sees the recorded plan
- [ ] Instruction explicitly forbids force-push, rebase, and writes outside the worktree
- [ ] Context dictionary contains `main_sha`, `branch`, `worktree_path`
- [ ] Instruction text stays under ~1500 tokens (rough cap; do not exceed without justification)
- [ ] xUnit tests pass

Notes: The brief follows the same lean shape as `PlanBriefBuilder`. The plan is delivered as the ticket's `DescriptionHtml` rather than parsed out and re-rendered; the worker can read HTML directly and the round-trip avoids brittle section extraction. The bare-marker WORKER_RESULT format from op-05 is the canonical envelope; the brief instructs the worker to emit that, not a fenced block.

OOS:
- Do not parse the plan HTML to extract sub-sections (the worker can read it as-is)
- Do not include cost or model-routing hints
- Do not include any prose from claude-config's `commands/ticket-act.md`
- Do not read claude-config source

#### Brief 05: implement-phase

Goal: `ImplementPhase` class that runs the implement flow end-to-end: validate state, drift-check against the planned-at SHA, create worktree and branch, dispatch worker, capture commit SHA from the worker's metadata, post the implemented-at marker, transition state.

Inputs:
- `ITicketing`, `IWorkerAgent`, `IEventSink`, `IGitClient` (constructor injection, matching `PlanPhase`'s shape)
- `BuildOptions` (already defined in `ThroughlineBuild.Phases`)
- `ImplementBriefBuilder` from B04
- `PhaseWorktreeLayout` from Plan A B03
- `MarkerParser` from op-02 (for finding the `planned_at` marker in ticket comments)
- `DriftComparator` from op-02 (for the SHA comparison; the file-overlap dimension stays unused in v1)

Outputs:
- `src/ThroughlineBuild.Phases/ImplementPhase.cs` with:

```csharp
public class ImplementPhase : IWorkflowPhase
{
    public Phase Phase => Phase.Implement;
    public ImplementPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IGitClient? gitClient = null);

    public Task<ImplementResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
    Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
}

public record ImplementResult(
    bool Success,
    string TicketId,
    string? CommitSha,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason);
```

- Phase logic in order:

  1. Fetch ticket via `ITicketing.GetAsync`
  2. Validate `ticket.State == TicketState.Ready`; if not, return `ImplementResult(false, ..., FailureReason: "ticket not in Ready state")`. No events emitted, no state change.
  3. Get current main SHA via `_git.RevParseAsync("origin/main", workingDirectory, ct)`
  4. Compute `PhaseWorktreeNames` from ticket + main worktree path
  5. Drift check: fetch ticket comments via `ITicketing.GetCommentsAsync`; scan comment bodies for `[planned_at: <sha>]` via `MarkerParser`; if a marker is present and its SHA differs from current main, emit a drift-warning event but proceed
  6. Build `RepoState` (`mainSha`, top-level entries via `Directory.EnumerateFileSystemEntries(workingDirectory)`)
  7. Build the Brief via `ImplementBriefBuilder.Build(ticket, repoState, worktreeNames)`
  8. Create worktree via `_git.CreateWorktreeAsync(worktreeNames.WorktreePath, worktreeNames.BranchName, "origin/main", workingDirectory, ct)`; if it fails, return `ImplementResult(false, ..., FailureReason)` with no state transition (the worktree-create failure is the entire failure surface for setup)
  9. Transition Ready -> InProgress via `ITicketing.TransitionAsync`; emit `StateTransition` event
  10. Emit `WorkerSpawn` event
  11. Call `_worker.ExecuteAsync(brief, worktreeNames.WorktreePath, workerOptions, ct)`
  12. Emit `VerifierVerdict` event with `status` = worker's `Status`
  13. If `Metadata["llm_usage"]` is present, emit `LlmCall` event with the flattened usage (reuse the flattening logic from PlanPhase; the snake_case keys match)
  14. If `WorkerResult.Status != Ok`: leave ticket in InProgress, return `ImplementResult(false, ..., FailureReason)`. Do NOT auto-decruft the worktree on failure; the user inspects it and decides
  15. Extract `commit_sha` from `WorkerResult.Metadata`; if missing, return failure with `FailureReason: "worker metadata missing commit_sha"` and leave in InProgress
  16. Verify the commit SHA exists in the worktree: call `_git.HeadShaAsync(worktreeNames.WorktreePath, ct)`; if it differs from `metadata.commit_sha`, prefer the actual HEAD SHA (the worker may have committed more after self-reporting) and log the discrepancy in the implemented-at comment
  17. Post `<p>[implemented_at: {actualHeadSha}] (branch {branchName})</p>` via `ITicketing.CreateCommentAsync`; emit `TicketWrite` event with `action = "create_comment"`
  18. Transition InProgress -> InReview via `ITicketing.TransitionAsync`; emit `StateTransition` event
  19. Return `ImplementResult(Success: true, ...)`

- The interface-explicit `PhaseResult` overload converts `ImplementResult` into `PhaseResult` with `Outputs` carrying `commit_sha`, `branch`, `worktree_path`
- xUnit tests with mocked dependencies covering:
  - Happy path (Ready ticket implemented successfully)
  - Ticket not in Ready (returns clean failure, no transitions)
  - Worktree create fails (returns clean failure, ticket stays in Ready)
  - Worker returns Status.Failed (records events, returns Success=false, ticket left in InProgress)
  - Worker returns Status.Ok but metadata missing commit_sha (returns failure, ticket left in InProgress)
  - Drift detected (warning event emitted, run continues)

Acceptance:
- [ ] All 19 steps implemented in order
- [ ] State transitions go through `ITicketing.TransitionAsync` only; no side-channel writes
- [ ] Worktree create failure does NOT transition the ticket
- [ ] Worker failure leaves the ticket in InProgress (user inspects worktree, decides next step)
- [ ] WorkflowEvent emitted at each significant step using the existing snake_case Data conventions (event-log-format.md)
- [ ] Drift warning emitted as a new event kind or as a `GateFailure` event with `Data.kind = "drift_warning"`; pick whichever feels right and update event-log-format.md in the same brief
- [ ] xUnit tests cover the listed scenarios with mocked dependencies
- [ ] The `IWorkflowPhase.RunAsync` interface call returns a `PhaseResult` whose `Outputs` carries `commit_sha`, `branch`, `worktree_path` on success
- [ ] No prose templates baked in beyond what the brief constructor produces
- [ ] `docs/event-log-format.md` updated: add Implement-phase events to the happy-path example (a separate section), add drift_warning to the Data conventions table if that representation is chosen

Notes: Failure mode handling here is conservative: any failure leaves the ticket in whatever state we already transitioned to. The user runs `build close` or `build defer` if they want to terminate, or manually fixes the worktree and reruns. No auto-recovery. The worktree is preserved on failure precisely so the user can inspect it. Auto-decruft on failure is a follow-up to consider once the dogfooding period surfaces actual failure shapes.

The drift check intentionally stays as a warning, not a gate, for v1. The old system's `/ticket-act` re-runs planning on detected drift; the new system would need to invoke PlanPhase from ImplementPhase to match, which couples phases in a way the architecture explicitly avoids. The right behavior is: surface the drift, let the user decide whether to `build defer` and re-plan, or to proceed. The judgment-slot version of this (model says "this drift is significant" vs "this drift is cosmetic") is a candidate for a future op-doc, not v1.

OOS:
- Do not invoke PlanPhase from ImplementPhase on detected drift
- Do not auto-decruft the worktree on worker failure
- Do not implement a `--force` or `--rerun` flag (the user can `build defer` + reopen for v1)
- Do not run tests, lint, or any verification inside ImplementPhase; review-phase owns that
- Do not push the feature branch to origin (op-09 ship-phase does that as part of merge)
- Do not implement worker-output streaming
- Do not preserve any base64-encoded payload pattern from prior systems
- Do not read claude-config source

#### Brief 06: implement-cli

Goal: Wire `build implement <id>` into the CLI dispatch, using the same config-loading and dependency-instantiation patterns as `build plan`.

Inputs:
- The existing `Program.cs` CLI dispatch (which already routes `plan` and the four ticket-state commands from op-06)
- `ImplementPhase` from B05

Outputs:
- Updated `src/ThroughlineBuild.Cli/Program.cs` adding the `implement` verb
- Subcommand parses `build implement <id>` with no required flags
- Reuses `BuildOptions` construction from the existing `plan` path
- Help text updated: `build --help` lists `implement` alongside `plan` and the four ticket-state commands
- Exit codes: 0 on success, 1 on phase failure (worker failed, worktree create failed, drift was treated as a gate, etc.), 2 on config error, 3 on missing secret
- xUnit test `ImplementCliTests` confirms:
  - `build implement TLB-X` parses and dispatches to ImplementPhase (mock the phase at the DI seam)
  - `build implement` (no id) prints usage and exits non-zero
  - `build --help` output contains `implement` as a verb

Acceptance:
- [ ] `build implement <id>` runs from a terminal
- [ ] The verb appears in `build --help`
- [ ] Phase failure exits 1 with the failure reason printed to stderr
- [ ] Config or secret errors exit 2 or 3 with clear messages
- [ ] xUnit tests pass

Notes: Argument parsing stays minimal. The implement phase deliberately has no flags in v1; future flags like `--worker codex` or `--rerun-from-failure` are their own op-docs.

OOS:
- Do not add per-phase config flags beyond what the existing CLI exposes
- Do not implement interactive prompts ("did you mean...?")
- Do not implement a `--dry-run` mode
- Do not read claude-config source

## What done looks like

After op-07 lands, a real ticket flows further through the new pipeline. From a fresh terminal in a Throughline Build worktree, with a TLB ticket sitting in Ready (post-plan):

```
$ build implement TLB-50
TLB-50 IMPLEMENT
  state: Ready -> InProgress
  worktree: /repo/.worktrees/ticket-tlb-50-extract-driftcomparator
  branch: ticket/tlb-50-extract-driftcomparator
  worker: claude-code
  ... [worker runs, edits files, commits] ...
  commit: 4f2c8e1a... (HEAD of feature branch)
  state: InProgress -> InReview
  Next: build review TLB-50 (op-08, not yet shipped)
```

The event log for the session records: StateTransition Ready->InProgress, WorkerSpawn, VerifierVerdict status=Ok, LlmCall with the worker's token usage, TicketWrite for the implemented-at comment, StateTransition InProgress->InReview. Cost comparison against the old `/ticket-act` slash command is direct: same ticket would be run through both systems, LlmCall event usage subtracted from old `ticket-audit` JSONL for `/ta` on a parallel ticket.

The feature branch (`ticket/tlb-50-extract-driftcomparator`) exists locally with one or more commits. The worktree at `.worktrees/ticket-tlb-50-extract-driftcomparator` is on the main worktree's git but isolated; the user can `cd` in to inspect, or wait for op-08/09 to land and run `build review` / `build ship` against it.

PlanPhase now implements `IWorkflowPhase` alongside ImplementPhase. ChainPhase (op-11) will dispatch both polymorphically through the same interface. The worktree-creation primitive is reusable; the worktree-layout helper is shared. The shape is ready for review-phase to consume.