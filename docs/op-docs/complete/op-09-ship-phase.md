# Operation: build-ship-phase

Add the ship phase to the `build` binary. After op-09 lands, a ticket sitting in InReview (post-implement, optionally post-review) can be shipped: the binary fetches origin, rebases the feature branch onto current origin/main, scans for unresolved conflict markers, runs regression checks, fast-forwards local main, posts the shipped-at marker, transitions InReview -> Done, then decrufts the worktree. Last phase on the critical path to cutover; after op-09 the new system handles a full Backlog-to-Done ticket lifecycle on local main.

## Why this exists

After op-08, the new system can advance Backlog -> Ready -> InProgress -> InReview and verify the work, but cannot land the change on main. The transition InReview -> Done lives only in the old `/ticket-ship` slash command. Without ship, every dogfooding ticket the new system handles still ends with a manual git operation on the user's side. That breaks the apples-to-apples comparison the May 23 run established and prevents the cutover demonstration (one TLB ticket through plan -> implement -> review -> ship on the new pipeline, compared to a parallel ticket through `/ti + /ta + /tr + /tsh` on the old).

Ship is also the phase where the cost story has the least to prove. Most of ship is deterministic git operations: fetch, rebase, scan, run checks, merge, prune. The only worker contact is none in v1; the conflict-triviality judgment slot stays out (see Notes). The architecture's commitment - state machines in code, agentic work in workers, judgment as discrete calls - is most cleanly visible here.

**Push to origin is out of scope for v1.** ShipPhase performs a local fast-forward merge of the feature branch into main and stops. Origin remains where it was; the user pushes when ready. This matches existing `/ticket-chain --ship` convention and the operational reality observed at op-08 close (local main 12 commits ahead of origin/main without issue). A `--push` flag arrives in v1.1 if dogfooding surfaces friction. The CLAUDE.md rule **never force-push to main** is encoded structurally: no `PushAsync` method is added to `IGitClient` in op-09 at all.

**Conflict triviality judgment slot:** v1 does NOT include a model-driven conflict-triviality judgment. On any rebase conflict, ship aborts the rebase via `git rebase --abort`, posts a `[ship_blocked: rebase_conflicts]` comment naming the conflicting paths, and surfaces to the user. The judgment slot (model decides "trivial enough to auto-resolve" versus "kick back") stays on the table for v1.1.

**Two op-08 follow-up items folded into Plan A:** the deferred LlmCall emission in ReviewPhase (op-08 handoff Section 3) and the FlattenLlmUsage helper extraction (three copies after op-08, fourth coming with chain in op-11) are bundled into op-09 because both are small, mechanical, and unblock cost-comparison measurement that has been pending since op-07.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Ship foundations and observability backfill | - | M |
| B    | Ship phase composition | A | M |

Plan A extends git capabilities with fetch / rebase / merge / branch-delete (no push), adds a conflict-marker scan helper, extracts FlattenLlmUsage to a shared helper, and activates the deferred ReviewPhase LlmCall emission. Plan B composes the ship foundations with the existing checks runner and decrufter into ShipPhase, wires the CLI, and closes outstanding architecture-doc drift. Within Plan A, B01-B04 are independent (different files, different abstractions); run in parallel. Plan B briefs are sequential.

## Plan A: Ship foundations and observability backfill

### Goal

Four pieces of scaffolding: extend `IGitClient` with fetch / rebase / rebase-abort / fast-forward-merge / branch-delete (no push); add a `ConflictMarkerScanner` helper for the post-rebase safety net; extract the duplicated `FlattenLlmUsage` private helper from Plan/Implement/Review into a shared helper; expose `LastWorkerResult` on `ClaudeCodeReviewer` so ReviewPhase Step 12's LlmCall emission goes live.

Brief sequence: B01, B02, B03, B04 are all independent. Run in parallel after this op-doc scaffolds.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | git-publish-ops | Add fetch / rebase / rebase-abort / fast-forward-merge / branch-delete to `IGitClient` (no push) | - | src/ThroughlineBuild.Contracts/IGitClient.cs, src/ThroughlineBuild.Git/ProcessGitClient.cs, tests/ThroughlineBuild.Git.Tests/PublishOpsTests.cs |
| 02 | conflict-marker-scanner | Helper that detects unresolved git conflict markers in files | - | src/ThroughlineBuild.Helpers/ConflictMarkerScanner.cs, tests/ThroughlineBuild.Helpers.Tests/ConflictMarkerScannerTests.cs |
| 03 | extract-llm-usage-flattener | Extract `FlattenLlmUsage` + `UnwrapJsonElement` from three phases into shared helper | - | src/ThroughlineBuild.Helpers/LlmUsageFlattener.cs, src/ThroughlineBuild.Phases/PlanPhase.cs, src/ThroughlineBuild.Phases/ImplementPhase.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs, tests/ThroughlineBuild.Helpers.Tests/LlmUsageFlattenerTests.cs |
| 04 | activate-review-llm-call | Expose `LastWorkerResult` on `ClaudeCodeReviewer` and activate ReviewPhase Step 12 emission | - | src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs, src/ThroughlineBuild.Phases/ReviewPhase.cs, tests/ThroughlineBuild.Verification.Tests/ClaudeCodeReviewerTests.cs, tests/ThroughlineBuild.Phases.Tests/ReviewPhaseTests.cs |

### Briefs - detail

#### Brief 01: git-publish-ops

Goal: Extend `IGitClient` with `FetchAsync`, `RebaseAsync`, `RebaseAbortAsync`, `FastForwardMergeAsync`, `DeleteBranchAsync`. No `PushAsync` in v1 (local-merge-only convention; see "Why this exists").

Inputs:
- The existing `IGitClient` interface (post-op-08; lives in `ThroughlineBuild.Contracts`)
- The existing `ProcessGitClient` implementation (post-op-08; lives in `ThroughlineBuild.Git`)
- Git's `fetch`, `rebase`, `merge --ff-only`, `branch -d` documentation

Outputs:
- New interface methods on `IGitClient`:

```csharp
Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct);
Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct);
Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct);
Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct);
Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct);
```

- New records (alongside the existing worktree records in `IGitClient.cs`):

```csharp
public record GitOpResult(bool Success, string? FailureReason);

public record RebaseResult(
    bool Success,
    bool HadConflicts,
    IReadOnlyList<string> ConflictingPaths,
    string? FailureReason);
```

- `ProcessGitClient` implementations:
  - `FetchAsync` wraps `git fetch <remote>` invoked with `WorkingDirectory = mainWorktreePath`
  - `RebaseAsync` wraps `git rebase <ontoRef>` invoked in the feature worktree:
    - Exit code 0 OR stderr containing "Current branch * is up to date" -> `RebaseResult(true, false, [], null)` (handles the rebase-already-applied case; see Notes)
    - Non-zero exit with unmerged paths -> parse `git diff --name-only --diff-filter=U` (invoked BEFORE abort) to populate `ConflictingPaths`, return `RebaseResult(false, HadConflicts: true, ConflictingPaths, FailureReason: stderr)`. Caller is responsible for invoking `RebaseAbortAsync` to leave the worktree usable.
    - Non-zero exit with no unmerged paths -> `RebaseResult(false, HadConflicts: false, [], FailureReason: stderr)` (some other rebase failure; caller decides recovery)
  - `RebaseAbortAsync` wraps `git rebase --abort`; treats "no rebase in progress" exit code (typically 128 with that message) as success - idempotent.
  - `FastForwardMergeAsync` wraps `git merge --ff-only <mergeRef>` in the main worktree. On failure it returns `GitOpResult(false, FailureReason: stderr)` - git's actual stderr is surfaced verbatim (with a generic exit-code fallback when stderr is empty). No implicit non-FF merge under any flag combination.
  - `DeleteBranchAsync` wraps `git branch -d <branch>` (or `-D` when `force: true`). Refuses unmerged branch without force (relies on git's built-in safety check).
- xUnit tests using a temp git repo with origin and a local clone simulating remote behavior:
  - Fetch retrieves new refs from the simulated remote
  - Rebase succeeds cleanly when feature is behind main
  - Rebase detects conflicts; populates `ConflictingPaths`; caller's `RebaseAbortAsync` leaves the worktree on the feature branch clean (`git status` reports clean)
  - Rebase-already-applied returns success (set up by cherry-picking the feature commits onto main, then rebasing the feature branch onto that main)
  - Fast-forward merge succeeds when main is exactly an ancestor of the feature ref; fails fast otherwise (no implicit non-FF)
  - Delete-branch succeeds on a merged branch; fails on unmerged (non-force); succeeds with force

Acceptance:
- [ ] All five methods added to the interface and implemented in `ProcessGitClient`
- [ ] No `PushAsync` method added (v1 is local-merge-only; OOS at the interface level)
- [ ] `RebaseAsync` returns `HadConflicts: true` with populated `ConflictingPaths` on real conflict, never throws
- [ ] `RebaseAsync` treats "already up to date" (exit 0 OR "Current branch is up to date" message) as `RebaseResult(true, false, [], null)`
- [ ] `RebaseAbortAsync` is idempotent (no error when no rebase is in progress)
- [ ] `FastForwardMergeAsync` refuses non-fast-forward merges under any conditions
- [ ] `DeleteBranchAsync` honors the safety check (refuses unmerged branch without force)
- [ ] None of the methods throw on git-level failure; all return structured results
- [ ] All existing `FakeGitClient` test stubs (across tests/ThroughlineBuild.Phases.Tests/, tests/ThroughlineBuild.Commands.Tests/) updated with no-op stub implementations of the five new methods so the test suite compiles
- [ ] xUnit tests pass against a temp git repo with simulated remote

Notes: `--force-with-lease` is NOT used anywhere in this brief because no push exists in v1. If push lands in v1.1, that's where `--force-with-lease` arrives - never plain `--force`, ever.

The rebase-already-applied case is git's normal behavior for stacked branches: when a feature branch's commits are already on main (because a predecessor ship rebased onto pre-merged content), `git rebase main` emits `warning: skipped previously applied commit <sha>` lines and exits 0. ShipPhase needs this to be a success, not a failure - the regression tests will run on the resulting state and validate semantic correctness regardless.

The `--diff-filter=U` invocation to populate `ConflictingPaths` must happen BEFORE `RebaseAbortAsync`; the unmerged set is empty after abort.

OOS:
- Do not add `PushAsync` under any flag combination (v1 is local-merge-only; never push)
- Do not implement plain `git push --force` or `--force-with-lease` (no push at all)
- Do not implement non-fast-forward merge under any flag combination
- Do not implement interactive rebase
- Do not implement `git stash` operations
- Do not implement automatic conflict resolution
- Do not read claude-config source

#### Brief 02: conflict-marker-scanner

Goal: A pure helper that scans a list of files for unresolved git conflict markers (`<<<<<<<`, `=======`, `>>>>>>>`). Used by ShipPhase as a safety net after rebase to catch markers a worker may have accidentally committed.

Inputs:
- A list of file paths (absolute or relative to a known root)
- File contents read via `System.IO.File`

Outputs:
- `src/ThroughlineBuild.Helpers/ConflictMarkerScanner.cs` with:

```csharp
public static class ConflictMarkerScanner
{
    public static Task<IReadOnlyList<ConflictMarkerHit>> ScanAsync(
        IEnumerable<string> filePaths,
        CancellationToken ct);
}

public record ConflictMarkerHit(string Path, int LineNumber, string MarkerKind);
```

- Marker detection: a line starting with `<<<<<<<` (followed by space or end-of-line) reports `MarkerKind = "start"`; a line that IS exactly `=======` reports `"separator"`; a line starting with `>>>>>>>` (followed by space or end-of-line) reports `"end"`
- Binary file detection: read first 8 KB; if it contains a NUL byte, skip the file (no hits reported)
- Large file ceiling: files over 5 MB are skipped (no hits reported); avoids accidentally scanning artifacts
- xUnit tests covering: file with no conflicts (empty result), file with one conflict block (three hits at expected line numbers), file with multiple conflicts, binary file (skipped), oversized file (skipped), empty list

Acceptance:
- [ ] `ScanAsync` returns the line numbers and kinds for each marker hit
- [ ] Files containing no markers produce empty hits
- [ ] Binary files are skipped (NUL byte detection in first 8 KB)
- [ ] Files exceeding the 5 MB ceiling are skipped
- [ ] No I/O outside reading the listed files
- [ ] xUnit tests pass

Notes: This scanner is a safety net, not a primary conflict detection mechanism. Primary detection is `RebaseAsync.HadConflicts`. The scanner catches the case where a worker committed a file containing conflict markers despite git reporting the rebase as clean (rare but observed in practice). ShipPhase runs the scanner after rebase as a paranoia check.

OOS:
- Do not implement automatic conflict resolution
- Do not implement marker rewrite or strip operations
- Do not parse the conflict contents (only detect the markers)
- Do not read claude-config source

#### Brief 03: extract-llm-usage-flattener

Goal: Extract the duplicated `FlattenLlmUsage` and `UnwrapJsonElement` private static helpers from `PlanPhase`, `ImplementPhase`, and `ReviewPhase` into a shared `LlmUsageFlattener` static class in `ThroughlineBuild.Helpers`. Pure mechanical refactor.

Inputs:
- The three existing copies of the helpers (one each in `PlanPhase.cs`, `ImplementPhase.cs`, `ReviewPhase.cs`) - byte-identical per the op-08 handoff
- The `ThroughlineBuild.Helpers` project (already referenced by `ThroughlineBuild.Phases`)

Outputs:
- `src/ThroughlineBuild.Helpers/LlmUsageFlattener.cs` with:

```csharp
public static class LlmUsageFlattener
{
    public static IReadOnlyDictionary<string, object>? Flatten(object usageObj);
}
```

- The internal `UnwrapJsonElement` helper either becomes a private static method on `LlmUsageFlattener` or stays as a separate internal helper in the same file - implementer's call
- `PlanPhase.cs` removes its private `FlattenLlmUsage` and `UnwrapJsonElement`; call sites use `LlmUsageFlattener.Flatten(...)`
- `ImplementPhase.cs`: same
- `ReviewPhase.cs`: same (its copy is dead code in op-08 - B04 in this op-doc activates it; this brief just extracts cleanly)
- xUnit tests in `LlmUsageFlattenerTests.cs` covering: `IDictionary<string, object?>` input, `JsonElement` input with object kind, various value types (string, int, long, bool, null), unknown shape returns null

Acceptance:
- [ ] `LlmUsageFlattener.Flatten` exists in `ThroughlineBuild.Helpers` namespace
- [ ] All three phase files no longer declare the helpers privately
- [ ] All three call sites use `LlmUsageFlattener.Flatten`
- [ ] All existing phase tests (Plan, Implement, Review) continue to pass without modification (the helper extraction is behavior-preserving)
- [ ] xUnit `LlmUsageFlattenerTests` cover the cases above

Notes: This is the smallest non-trivial brief in op-09 - mechanical, low risk, behavior-preserving. Do not refactor anything else in the phases beyond removing the local helpers and updating the call sites. Resist any urge to "while I'm here, also extract...".

The op-08 handoff (Section 3) flags this explicitly: "Best done as a standalone brief because it touches three phases." This brief is that.

OOS:
- Do not extract any other duplicated logic from the phases (e.g. `TryGetString` is also duplicated but is not in scope for this brief)
- Do not change `Flatten`'s signature or behavior; it must be a drop-in replacement
- Do not introduce a `JsonSerializerContext` source-generator entry for this helper
- Do not read claude-config source

#### Brief 04: activate-review-llm-call

Goal: Expose `LastWorkerResult` on `ClaudeCodeReviewer` and activate the deferred LlmCall emission at `ReviewPhase` Step 12. This is the ~10-line change op-08 handoff Section 3 identifies as the blocker for review-phase token-cost measurement.

Inputs:
- `src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs` (post-op-08; the `VerifyAsync` method already calls `worker.ExecuteAsync` and gets a `WorkerResult` it converts to a `Verdict`)
- `src/ThroughlineBuild.Phases/ReviewPhase.cs` (post-op-08; Step 12's conditional never fires because the underlying `WorkerResult` isn't visible)
- The `LlmUsageFlattener` helper from B03 - this brief assumes B03 has landed (or runs after it); use the shared helper for the flattening

Outputs:
- `ClaudeCodeReviewer` adds:

```csharp
public WorkerResult? LastWorkerResult { get; private set; }
```

  Assigned inside `VerifyAsync` immediately after `worker.ExecuteAsync` returns and before the verdict mapping. Thread-safety is not a concern; one `ClaudeCodeReviewer` instance is constructed per ReviewPhase run.

- `ReviewPhase` Step 12 reads:

```csharp
// Step 12: LlmCall event if verifier worker reported usage
if (_verifier is ClaudeCodeReviewer ccr
    && ccr.LastWorkerResult is { } verifierResult
    && verifierResult.Metadata.TryGetValue("llm_usage", out var usageObj))
{
    var llmData = LlmUsageFlattener.Flatten(usageObj);
    if (llmData is not null)
    {
        await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
    }
}
```

  The `is ClaudeCodeReviewer` pattern check is the v1 surface; if a cross-vendor verifier lands in v1.1 with the same `LastWorkerResult` convention, lift the check to an interface (e.g. `IVerifierWithLastResult`). v1 lives with the type check.

- xUnit tests:
  - `ClaudeCodeReviewerTests` add a case: after `VerifyAsync` returns successfully, `LastWorkerResult` is non-null and matches the underlying mock worker's returned result
  - `ReviewPhaseTests` add a case: verifier worker returns metadata with `llm_usage`; assert exactly one `LlmCall` event was emitted with the expected `Data` keys (`model`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`, `wall_clock_ms`)
  - `ReviewPhaseTests` add a case: verifier worker returns metadata WITHOUT `llm_usage`; assert no `LlmCall` event is emitted

Acceptance:
- [ ] `ClaudeCodeReviewer.LastWorkerResult` is a public read-only property assigned during `VerifyAsync`
- [ ] `ReviewPhase` Step 12 emits exactly one `LlmCall` event per run when the verifier worker reports `llm_usage`
- [ ] `LlmCall` event Data keys match the existing snake_case convention used by Plan and Implement
- [ ] xUnit tests cover the present-usage, absent-usage, and LastWorkerResult-population cases
- [ ] `docs/event-log-format.md` updated: add a Review-phase LlmCall row to the "Happy-path Review example" (or equivalent) section so Pass/Rework/Fail JSONL fixtures show the LlmCall line between VerifierVerdict and the first TicketWrite

Notes: This brief depends on B03 (the shared `LlmUsageFlattener`) only for the flattening call; if B03 lands first, use the shared helper. If for some reason B03 doesn't land before B04, copy the local `FlattenLlmUsage` from ReviewPhase verbatim (it's already there) and let B03 sweep the call site when it ships.

The pattern check `_verifier is ClaudeCodeReviewer` is intentional in v1. The op-08 handoff Section 7 question #5 noted this would activate cost-comparison measurement; the type check is the simplest possible activation. A cleaner interface seam (`IVerifierWithUsage` or similar) lands when cross-vendor verifier work happens.

OOS:
- Do not introduce a new interface like `IVerifierWithUsage` in v1
- Do not change `IVerifier.VerifyAsync`'s signature
- Do not add LlmCall emission to other phases (Plan and Implement already emit; this brief is about Review only)
- Do not surface `LastWorkerResult` on a base type beyond `ClaudeCodeReviewer`
- Do not read claude-config source

## Plan B: Ship phase composition

### Goal

A `ShipPhase` class that composes Plan A's git ops, the conflict-marker scanner, the existing `AutomatedChecksRunner` from op-08, the `WorktreeDecrufter` from op-06, and the `PhaseWorktreeLayout` helper into the end-to-end ship flow. CLI wiring. Arch-doc updates closing op-08 drift and adding ShipPhase coverage.

Brief sequence: B05 (phase) first. B06 (CLI) depends on B05. B07 (arch doc) depends on B06.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | ship-phase | `ShipPhase` orchestrating fetch / rebase / conflict-scan / regression-checks / fast-forward-merge / shipped-at / Done transition / decruft | - | src/ThroughlineBuild.Phases/ShipPhase.cs, tests/ThroughlineBuild.Phases.Tests/ShipPhaseTests.cs |
| 06 | ship-cli | `build ship <id>` subcommand with `[ship]` TOML section | 05 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, src/ThroughlineBuild.Cli/Config.cs, tests/ThroughlineBuild.Cli.Tests/ShipCliTests.cs |
| 07 | arch-doc-revisions | Close op-08 doc drift and add ShipPhase / IGitClient growth coverage | 06 | docs/throughline-build-architecture.md |

### Briefs - detail

#### Brief 05: ship-phase

Goal: `ShipPhase` class that runs the ship flow end-to-end: validate state, fetch remote, rebase feature branch onto current origin/main, scan for conflict markers, run regression checks, fast-forward merge into LOCAL main (no push), post the shipped-at marker, transition InReview -> Done, then decruft.

Inputs:
- `ITicketing`, `IEventSink`, `IGitClient` (constructor injection, mirroring `ReviewPhase` shape)
- `BuildOptions` (existing record in PlanPhase.cs)
- `AutomatedChecksRunner` from op-08 (`ThroughlineBuild.Verification`)
- `WorktreeDecrufter` from op-06 Plan A (`ThroughlineBuild.Helpers`); signature is `DecruftAsync(string worktreePath, string mainWorktreePath, CancellationToken ct)`
- `PhaseWorktreeLayout` from op-07 (`ThroughlineBuild.Helpers`)
- `ConflictMarkerScanner` from B02
- `CheckSpec` from `ThroughlineBuild.Contracts` (post-op-08 rework; NOT `ThroughlineBuild.Verification`)
- An options record `ShipOptions` carrying the regression `CheckSpec` list, the remote name, and the base branch

Outputs:
- `src/ThroughlineBuild.Phases/ShipPhase.cs` with:

```csharp
public record ShipOptions(
    IReadOnlyList<CheckSpec> RegressionChecks,
    string Remote,            // "origin"; used only for git fetch in v1
    string BaseBranch,        // "main"
    bool DeleteFeatureBranch);// default true

public record ShipResult(
    bool Success,
    string TicketId,
    string? MergedSha,
    string? FailureReason,
    ShipFailureStage? FailedAt);

public enum ShipFailureStage
{
    StateCheck,
    Fetch,
    Rebase,
    ConflictMarkerScan,
    RegressionChecks,
    FastForwardMerge,
    Decruft  // post-success, non-fatal
}

public class ShipPhase : IWorkflowPhase
{
    public Phase Phase => Phase.Ship;
    public ShipPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        ShipOptions shipOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null,
        ConflictMarkerScannerFn? markerScanner = null,
        WorktreeDecrufter? decrufter = null);

    public Task<ShipResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
    Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
}

public delegate Task<IReadOnlyList<ConflictMarkerHit>> ConflictMarkerScannerFn(
    IEnumerable<string> filePaths, CancellationToken ct);
```

  The optional override seams (`checksRunner`, `markerScanner`, `decrufter`) mirror ReviewPhase's pattern from op-08 so tests can run hermetically without subprocesses. The `ConflictMarkerScannerFn` delegate exists so tests can inject without holding a class-reference; `ConflictMarkerScanner.ScanAsync` from B02 satisfies the delegate signature.

- Phase logic in order:

  1. Fetch ticket via `_ticketing.GetAsync`
  2. Validate `ticket.State == TicketState.InReview`; if not, return `ShipResult(false, ..., FailedAt: StateCheck)`. No events, no state change.
  3. Compute `PhaseWorktreeNames`; verify the feature worktree exists via `_git.ListWorktreesAsync` matching by `Branch` or `Path` (mirror the lookup pattern from `ReviewPhase` Step 3); if not present, return `ShipResult(false, ..., FailedAt: StateCheck)` with a clear failure reason
  4. Fetch the remote via `_git.FetchAsync(shipOptions.Remote, workingDirectory, ct)`; on failure, return `ShipResult(false, ..., FailedAt: Fetch)` with the git error
  5. Rebase feature branch onto `<remote>/<baseBranch>` via `_git.RebaseAsync($"{shipOptions.Remote}/{shipOptions.BaseBranch}", worktreeNames.WorktreePath, ct)`:
     - If `HadConflicts` is true: call `_git.RebaseAbortAsync` to leave the worktree clean; post `<p><strong>ship_blocked:</strong> rebase conflicts in: {comma-joined paths}</p>` via `_ticketing.CreateCommentAsync`; emit `GateFailure` event with `Data = { kind: "rebase_conflicts", conflicting_paths: [...] }`; return `ShipResult(false, ..., FailedAt: Rebase)`. Ticket stays in InReview.
     - If rebase failed for non-conflict reason: post `<p><strong>ship_blocked:</strong> rebase failed: {reason}</p>`; emit `GateFailure` with `Data = { kind: "rebase_other", reason: ... }`; return failure
     - If rebase succeeded (including the rebase-already-applied case): proceed
  6. Run `markerScanner` over the files changed in the post-rebase diff. Get the changed-file list via `_git.DiffAsync($"{shipOptions.Remote}/{shipOptions.BaseBranch}", worktreeNames.BranchName, workingDirectory, includePatchContent: false, ct)` and pass `diff.Entries.Select(e => Path.Combine(worktreeNames.WorktreePath, e.Path))` to the scanner. If any hits, post `<p><strong>ship_blocked:</strong> conflict markers detected in: {comma-joined paths}</p>`; emit `GateFailure` with `Data = { kind: "conflict_markers", marker_files: [...] }`; return `ShipResult(false, ..., FailedAt: ConflictMarkerScan)`
  7. Run regression checks via `_checksRunner.RunAsync(shipOptions.RegressionChecks, worktreeNames.WorktreePath, ct)`; if any check fails, post `<p><strong>ship_blocked:</strong> regression checks failed: {comma-joined check names}</p>`; emit `GateFailure` with `Data = { kind: "regression_checks", checks_failed: [check names] }`; return `ShipResult(false, ..., FailedAt: RegressionChecks)`
  8. Fast-forward merge: the main worktree must currently be on `baseBranch` for this to work; if it is not, return failure with `FailedAt: FastForwardMerge` and a clear reason naming the current branch (do not auto-checkout to baseBranch). Call `_git.FastForwardMergeAsync(worktreeNames.BranchName, workingDirectory, ct)`; non-FF returns failure. (The clean rebase from Step 5 normally ensures FF is possible; this guard catches operator mistakes like running `build ship` from a non-main main-worktree state.)
  9. Read the post-merge local main HEAD via `_git.HeadShaAsync(workingDirectory, ct)`; this is `MergedSha`
  10. Post `<p>[shipped_at: {MergedSha}]</p>` via `_ticketing.CreateCommentAsync`; emit `TicketWrite` event with `Data = { action: "create_comment" }`
  11. Transition InReview -> Done via `_ticketing.TransitionAsync`; emit `StateTransition` event with `Data = { from: "InReview", to: "Done" }`
  12. Run worktree decruft via `_decrufter.DecruftAsync(worktreeNames.WorktreePath, workingDirectory, ct)`. Emit a `TicketWrite`-style event with `Data = { action: "decruft", halted_at: decruftResult.HaltedAt?.ToString() ?? "complete" }`. Decruft failure does NOT unwind the Done transition.
  13. If `shipOptions.DeleteFeatureBranch` is true: call `_git.DeleteBranchAsync(worktreeNames.BranchName, force: false, workingDirectory, ct)`. Delete failure does NOT unwind Done; log it as part of step-12-equivalent event Data
  14. Return `ShipResult(Success: true, ticketId, MergedSha, FailureReason: null, FailedAt: null)`

- The interface-explicit `PhaseResult` overload converts `ShipResult` into `PhaseResult` with `Outputs` carrying `merged_sha`, `branch`, `worktree_path`
- xUnit tests with mocked dependencies covering:
  - Happy path (InReview ticket shipped successfully)
  - Ticket not in InReview (clean failure, no operations)
  - Worktree missing (clean failure)
  - Fetch fails (clean failure, no state change)
  - Rebase has conflicts (rebase aborted, ship_blocked comment posted, GateFailure event with kind=rebase_conflicts, no state change)
  - Rebase succeeds in the "already up to date" case (proceeds normally; do not treat as failure)
  - Conflict markers detected post-rebase (GateFailure with kind=conflict_markers, ship_blocked comment, no state change)
  - Regression checks fail (GateFailure with kind=regression_checks, checks_failed in Data, no state change)
  - Fast-forward merge fails because main worktree is on a non-main branch (clean failure with clear reason)
  - Successful happy path with `DeleteFeatureBranch: false` leaves the local branch in place
  - Decruft fails after successful Done transition (Done preserved, decruft failure logged via the action=decruft event)

Acceptance:
- [ ] All 14 steps implemented in order
- [ ] Any pre-Done failure (Steps 2-9) leaves the ticket in InReview
- [ ] Decruft and branch-delete failures do NOT unwind the Done transition (Steps 12-13)
- [ ] `ship_blocked:` marker text is literally that string for all gate failures (consistent with `wontfix:` / `deferred:` / `reopened:` / `reviewed:` / `implemented_at:` conventions)
- [ ] `shipped_at:` marker text is literally that string
- [ ] All git operations go through `IGitClient`; no inline `Process.Start("git", ...)` in `ShipPhase`
- [ ] Conflict-marker scan runs AFTER rebase and BEFORE regression checks
- [ ] No push to origin/main anywhere (v1 is local-merge-only)
- [ ] `GateFailure` event Data uses `kind` key with values from the set `rebase_conflicts | rebase_other | conflict_markers | regression_checks` (consistent with the `drift_warning` precedent from ImplementPhase)
- [ ] The `IWorkflowPhase.RunAsync` interface call returns a `PhaseResult` whose `Outputs` carries `merged_sha`, `branch`, `worktree_path` on success
- [ ] `WorktreeDecrufter.DecruftAsync` is called with both paths: `(worktreeNames.WorktreePath, workingDirectory, ct)`
- [ ] xUnit tests cover the listed scenarios using the override seams (no real subprocesses)
- [ ] `docs/event-log-format.md` updated in the same brief: add Ship-phase events to the happy-path examples; document `GateFailure.kind` values added by ship (`rebase_conflicts | rebase_other | conflict_markers | regression_checks`); document the `action: "decruft"` TicketWrite Data

Notes: The order matters. Fetch before rebase (otherwise rebasing onto stale main). Conflict-marker scan before regression checks (markers can cause build failures with misleading messages; surface them with the cleaner failure mode first). Merge before "the ship is complete" - the merge IS the ship completion in v1, not a push. Comment + transition after merge succeeds (the local merge IS the shipped state in this v1).

Step 8's "main worktree must be on baseBranch" guard: this catches the operator mistake where the user has checked out a feature branch in the main worktree (uncommon but possible during exploration). The guard is read-only - it does not auto-checkout. The user fixes the state and reruns.

The state machine has a clean failure boundary: every failure mode prior to Step 11 leaves the ticket in InReview. The user runs `build ship TLB-X` again after fixing the cause. There is no half-shipped state visible to Plane: the ticket is either Done (post-merge + transition) or InReview (everything else).

The `force: false` on `DeleteBranchAsync` is deliberate. A feature branch that has been merged into local main is mergeable by git's check (`git branch -d` succeeds). If the merge happens to NOT have landed (defensive case), the delete fails safely without losing commits. A future `--keep-branch` flag is a v1.1 addition if needed.

OOS:
- Do not implement a conflict-triviality judgment slot (deferred to v1.1; see "Why this exists")
- Do not push to origin/main under any condition
- Do not implement plain `git push --force` or `--force-with-lease`
- Do not implement non-fast-forward merge under any flag combination
- Do not auto-resolve conflicts (manual resolution belongs in `git rebase --continue` from the user's terminal)
- Do not implement a `--dry-run` flag (defer)
- Do not implement automatic worktree-state recovery for the "wrong branch in main worktree" case
- Do not preserve any base64 round-trip pattern from prior systems
- Do not read claude-config source

#### Brief 06: ship-cli

Goal: Wire `build ship <id>` into the CLI dispatch. Extend the TOML config schema with a `[ship]` section that carries the regression-checks list and remote/base-branch settings.

Inputs:
- The existing `src/ThroughlineBuild.Cli/Program.cs` (already routes `plan`, `implement`, `review`, and the four ticket-state commands)
- `src/ThroughlineBuild.Cli/CliUsage.cs` (extracted usage text; add `ship` to the verb list)
- `src/ThroughlineBuild.Cli/Config.cs` (existing TOML config loader; add `[ship]` section parsing)
- `ShipPhase` from B05

Outputs:
- Updated `Program.cs` adding the `ship` verb:
  - Add `ship` to the verb-validation block (the `if (verb == "plan" || verb == "implement" || verb == "review")` check becomes `... || verb == "ship"`)
  - Add `ship` to the verb-dispatch tail block (the `if (verb != "plan" && verb != "implement" && verb != "review")` check becomes `... && verb != "ship"`)
  - Add a `ship` dispatch branch that instantiates `ShipPhase` with dependencies and calls `RunAsync`; map `ShipResult` to exit codes
  - Terminal output on success: `Ship complete: {id} merged={sha} branch={branch}`
  - Terminal output on failure: stage name + failure reason, e.g. `Ship blocked at rebase: conflicts in src/X.cs, src/Y.cs`
- Updated `CliUsage.cs` adding `ship` to the verb list with one-line description; document the no-push convention in the help text
- Updated `Config.cs` adding the `[ship]` config section. New record fields and helper:

```toml
[ship]
remote = "origin"               # used only for git fetch in v1; no automatic push
base_branch = "main"
delete_feature_branch = true

# Regression checks reuse the CheckSpec shape from ThroughlineBuild.Contracts.
# Duplicate or distinct from [[review.checks]] per user preference; for v1
# the lists are independent so each phase can evolve separately.
[[ship.regression_checks]]
name = "build"
executable = "dotnet"
arguments = ["build", "--nologo", "--configuration", "Release"]
timeout_minutes = 5

[[ship.regression_checks]]
name = "test"
executable = "dotnet"
arguments = ["test", "--nologo", "--configuration", "Release", "--no-build"]
timeout_minutes = 10
```

- The `[ship]` TOML section is OPTIONAL with a sensible default (remote="origin", base_branch="main", delete_feature_branch=true, regression_checks=empty list) so configs that predate ship still load - mirrors the optional pattern op-08 B08 established for `[review]`. Same `OptionalStringList` helper or equivalent.
- Exit codes:
  - 0 on success (ticket Done)
  - 1 on any gate failure (rebase, conflict markers, checks; ticket stays in InReview)
  - 2 on config error, unknown verb, missing ticket id
  - 3 on missing secret
  - 4 on phase infrastructure failure (worktree missing, fast-forward merge failed because main is on wrong branch, git unavailable, etc.)
  - Match the `ReviewResult.Success` vs verdict distinction from op-08 - gate failures (verdict-like outcomes) get exit 1; infrastructure failures get exit 4
- `.build/config.toml.example` gains a `[ship]` section with the example values above, plus a comment noting "v1 does not push to origin/main; the local merge is the ship"
- xUnit test `ShipCliTests` confirms:
  - `build ship TLB-X` parses and dispatches to ShipPhase
  - `build ship` (no ticket id) prints usage and exits 2
  - Exit codes match the failure stage on each path
  - `build --help` output contains `ship` as a verb
  - `.build/config.toml.example` contains the `[ship]` section

Acceptance:
- [ ] `build ship <id>` runs from a terminal
- [ ] Verb appears in `build --help` via `CliUsage.UsageText`
- [ ] Help text notes the no-push convention
- [ ] Success exits 0; gate failure exits 1; config/secret/usage errors exit 2/3; infrastructure failure exits 4
- [ ] `[ship]` TOML section is optional; configs without it load with defaults
- [ ] Example config file documents the `[ship]` section with the no-push comment
- [ ] xUnit tests pass

Notes: The regression-checks list is duplicated between `[review.checks]` and `[ship.regression_checks]` by design for v1. Conceptually different gates - review runs them as part of forming the verdict; ship runs them as the last guardrail before main. Users will likely configure the same commands in both for v1; if dogfooding shows the duplication is friction, a shared `[checks]` section with a `phases = ["review", "ship"]` attribute is a v1.1 follow-up.

If new ship CLI tests touch `Console.SetError`, opt them into the `[Collection("CommandConsoleTests")]` xUnit collection per the op-07 fix carried forward.

OOS:
- Do not implement a `--push` flag (v1 is local-merge-only; push is v1.1)
- Do not implement a `--skip-checks` or `--no-decruft` flag
- Do not implement interactive confirmations
- Do not change the existing verb dispatch patterns for plan/implement/review
- Do not read claude-config source

#### Brief 07: arch-doc-revisions

Goal: Close the documentation drift left after op-08's B06 rework (op-08 handoff Section 5), plus document op-09's new surfaces (ShipPhase, IGitClient growth, ConflictMarkerScanner, LlmUsageFlattener). Single brief; runs LAST in Plan B so it documents ship reality including any in-op refinements.

Inputs:
- `docs/throughline-build-architecture.md` (current state, last updated by op-08 B04)
- `docs/op-docs/op-08-handoff.md` Section 5 (drift inventory)
- The op-09 source files as they actually shipped from B01-B06

Outputs:
- Updated `docs/throughline-build-architecture.md` reflecting:
  - **Section 5.6 (Helpers)** - update the `CheckSpec` / `CheckResult` location from `ThroughlineBuild.Verification` to `ThroughlineBuild.Contracts`; add `ConflictMarkerScanner` to the helpers list (one line); add `LlmUsageFlattener` to the helpers list (one line, with note "extracted from per-phase copies in op-09 to support the third concrete IWorkflowPhase landing")
  - **Section 5.7 (Brief Constructor)** - no changes needed; op-09 does not add a new brief builder (ShipPhase has no LLM contact)
  - **Section 5.8 (Verifier)** - add one sentence on `ClaudeCodeReviewer.LastWorkerResult` and the v1 type-check pattern used by ReviewPhase to emit LlmCall; note cross-vendor verifier deferred to v1.1
  - **New Section 5.9 (Ship)** - add a paragraph describing ShipPhase: deterministic git orchestration; rebase / conflict-scan / regression-checks / fast-forward-merge / shipped-at / Done; explicitly note v1 is local-merge-only with no push to origin; reference WorktreeDecrufter for post-ship cleanup
  - **Section 5.2 (State Machine)** - update the IWorkflowPhase implementations list to include ShipPhase as the third concrete phase
  - **Section 6 (Interfaces & Contracts)** - refresh the IGitClient block with all methods present after op-09 (12 total); add the new records (GitOpResult, RebaseResult); note `ConflictMarkerScannerFn` delegate from the ShipPhase signature
  - Optionally a brief project-graph note: `Contracts` (leaf) -> `Briefs` -> `Verification` -> `Phases` -> `Cli`; `Helpers` parallel branch off `Contracts`; `Git` parallel branch off `Contracts` (per op-08 B01). This is informational; not load-bearing.
- The doc reads consistently end-to-end; no contradictions between sections; no orphaned references to "the brief constructor" as singular; no descriptions of features that don't exist in code

Acceptance:
- [ ] All five section updates listed above land
- [ ] `CheckSpec` / `CheckResult` location is corrected to `ThroughlineBuild.Contracts`
- [ ] New Section 5.9 (Ship) describes ShipPhase including the no-push convention
- [ ] Section 5.2 lists the four concrete IWorkflowPhase implementations (Plan, Implement, Review, Ship)
- [ ] Section 6 IGitClient block lists every method present in the interface today (12 after op-09)
- [ ] Doc passes a read-through: no contradictions, no stale references
- [ ] Word count growth bounded: arch doc grows by no more than ~30% from its pre-op-09 size; this is gap-closing plus one new subsection, not a rewrite

Notes: The handoff Section 5 explicitly notes none of these are architecture revisions (design changes) - all are documentation drift. Resist the urge to redesign anything in the arch doc during this brief; what shipped is what gets documented. If any substantive claim in the arch doc contradicts the shipped code (none flagged but possible), call it out in the commit message and ask Dan rather than silently rewriting.

This brief runs LAST in Plan B specifically because op-08 B04's pattern - documenting from the spec before Plan B implementation - led to drift when TLB-89 reworked CheckResult location. Running after ship-phase ships avoids the same pitfall.

OOS:
- Do not propose new architectural patterns
- Do not propose new components beyond what op-09 actually adds
- Do not delete or rewrite Section 7 (Bootstrap Discipline), Section 8 (Migration Plan), Section 10 (Risk Register), or the Appendix
- Do not update any other `.md` files in the repo
- Do not read claude-config source

## What done looks like

After op-09 lands, a real ticket completes its lifecycle on the new pipeline. From a fresh terminal with TLB-50 in InReview (post-implement, optionally post-review-Pass):

```
$ build ship TLB-50
Ship starting: TLB-50
  worktree: /repo/.worktrees/ticket-tlb-50-extract-driftcomparator
  branch: ticket/tlb-50-extract-driftcomparator
  fetch: origin (ok)
  rebase: feature branch onto origin/main (ok, 0 conflicts)
  conflict markers: none
  regression checks: build (pass, 11.8s), test (pass, 23.4s)
  merge: main fast-forward to feature (ok)
  shipped_at: 7a3f9b2c...
  state: InReview -> Done
  decruft: complete
  branch deleted: ticket/tlb-50-extract-driftcomparator
Ship complete: TLB-50 merged=7a3f9b2c branch=ticket/tlb-50-extract-driftcomparator
```

Note the absence of a push step. The user runs `git push origin main` from a terminal when ready. Origin remains stale until then (matching the operational reality observed at op-08 close: local main 12 commits ahead of origin/main, no issues).

For a rebase-conflict path:

```
$ build ship TLB-50
  fetch: origin (ok)
  rebase: feature branch onto origin/main (CONFLICTS in src/X.cs, src/Y.cs)
Ship blocked at rebase: conflicts in src/X.cs, src/Y.cs
  rebase aborted; worktree preserved
  state: InReview (unchanged)
  Next: resolve conflicts in worktree, commit, rerun build ship
```

For a regression-check failure:

```
$ build ship TLB-50
  rebase: ok
  regression checks: build (pass), test (FAIL: 3 failing tests)
Ship blocked at regression_checks: test
  state: InReview (unchanged)
  Next: fix tests in worktree, rerun build ship
```

The event log captures every gate crossed: `WorkerSpawn` events are absent from ship-phase entirely (no agentic work in v1 ship), `GateFailure` events with `kind` from the set `{rebase_conflicts, rebase_other, conflict_markers, regression_checks}`, `TicketWrite` for the shipped-at comment, `StateTransition` `InReview -> Done`, and a final `TicketWrite` with `action: "decruft"` and `halted_at` capturing the decruft outcome. No `LlmCall` events from ship-phase (deterministic git, no model contact).

**The cutover demonstration is now callable.** Pick a TLB ticket in Backlog. Run `build plan`, `build implement`, `build review`, `build ship` on the new pipeline. In parallel, run `/ti`, `/ta`, `/tr`, `/tsh` on an equivalent TLB ticket on the old pipeline. Sum the LlmCall event tokens on the new side (Plan + Implement + Review; Ship contributes zero); sum the audit JSONL tokens on the old side. The ratio is the spine-level cost reduction the architecture has been building toward. Per the May 23 plan-only run (~9.4x at opus tier), the spine-level number should be in the same range or better - Implement and Review carry more of the persistent-context tax than Plan does.

`IWorkflowPhase` now has four concrete implementations (Plan, Implement, Review, Ship); `ChainPhase` (op-11) consumes the interface directly. The remaining critical-path work is op-11. After chain ships, the new system handles multi-ticket dependency waves; the cutover decision becomes a question of confidence rather than capability.

Three follow-up items surfaced by this op-doc's design that need attention after op-09 lands but are out of scope here:

- **The pending cost-comparison run.** Outstanding since op-07's "What done looks like". B04 in this op-doc unblocks the review-phase token-cost measurement; the comparison itself is operator work (run both pipelines, collect event logs, diff). Worth completing before op-11 design starts so chain is being built atop calibrated cost numbers.

- **Push to origin in v1.1.** Local-merge-only is the v1 decision. If dogfooding shows operators want one-step ship, add a `--push` flag and a `[ship.push]` config section. Implement with `--force-with-lease` for the feature-branch push (if push is ever extended to feature branches; main push is always non-force). The `never force-push to main` rule remains structural - no plain `--force` anywhere in the codebase.

- **Conflict-triviality judgment slot in v1.1.** Once dogfooding produces a sample of real rebase conflicts, the judgment-slot version (model classifies "trivial" vs "non-trivial", auto-retries on trivial) becomes a small extension to Step 5 of ShipPhase. Skip for v1.

After op-09 the new system handles a full Backlog-to-Done ticket lifecycle locally. The cutover demo is one terminal session away.