# Operation: build-review-phase

Add the review phase to the `build` binary. After op-08 lands, a ticket sitting in InReview (post-implement) can be verified: the binary runs deterministic automated checks against the feature branch, dispatches an independent verifier to inspect the diff, and applies the resulting verdict by either keeping the ticket in InReview (ready for ship), kicking it back to InProgress for rework, or recording a failure verdict for user adjudication. Third phase on the critical path to cutover.

## Why this exists

After op-07, the new system advances tickets Backlog -> Ready -> InProgress -> InReview but cannot verify the implementation. Without review, the user's only signal that the worker produced acceptable code is reading the diff. The cost story is incomplete because the costliest agentic step (model-driven code review against a constructed diff) lives only in the old slash-command path.

Review is the phase where the architecture's independent-verifier commitment earns out. The IVerifier interface was introduced in op-02 specifically so verification could run against a vendor-or-model different from the implementer with no shared context. Op-08 lands the first concrete IVerifier implementation and the AutomatedChecksRunner that runs deterministic gates (build, test, lint) before the model verifier sees the diff. Failed checks do NOT short-circuit the verifier in v1; the verifier sees the failed-check list and forms a holistic verdict (short-circuit becomes a follow-up if dogfooding shows the model rubber-stamps Rework on check failures).

**Cross-vendor verification timing:** v1 uses ClaudeCodeAgent for the verifier (same vendor as the typical implementer). IVerifier is a separate interface from IWorkerAgent specifically so swapping to Codex or Gemini is a config change, not a code change. Cross-vendor verification stays on the table for v1.1; v1 ships with single-vendor IVerifier so the rest of the spine can be measured without confounding the comparison run.

**Implementer artifacts plumbing:** op-07 did not add a persistence layer for the implementer's brief or WorkerResult. ReviewPhase reconstructs both from ticket+git state: the brief is deterministic from inputs (call ImplementBriefBuilder.Build again), the WorkerResult is synthesized from the `[implemented_at: <sha>]` marker comment and the GitDiff. This avoids adding `.build/artifacts/<ticket>/` persistence in op-08; the synthetic-summary trade-off is documented as a follow-up.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Verification foundations | - | M |
| B    | Review phase composition | A | M |

Plan A extracts ProcessGitClient to its own project, extends it with diff fetching, ships AutomatedChecksRunner, and closes outstanding architecture-doc gaps. Plan B composes the foundations into ReviewBriefBuilder, ClaudeCodeReviewer, ReviewPhase, and CLI. Within Plan A, B02 depends on B01 (DiffAsync goes on the extracted class); B03 and B04 are independent of the others. Plan B briefs are sequential.

## Plan A: Verification foundations

### Goal

Four pieces of scaffolding: extract ProcessGitClient from PlanPhase.cs into its own project (its surface is growing past where it fits as a sibling class in a phase file, and op-09 will add ~6 more methods); add `DiffAsync` to IGitClient and the extracted ProcessGitClient; ship AutomatedChecksRunner in a new `ThroughlineBuild.Verification` project; close the architecture-doc gaps op-07 left behind.

Brief sequence: B01 (extraction) first since B02 (DiffAsync) lands new code on the extracted class. B03 (checks runner) and B04 (arch doc) are independent of the others; run in parallel after B01 lands or concurrent with it depending on agent availability.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | extract-process-git-client | Move `ProcessGitClient` from `PlanPhase.cs` into its own project `ThroughlineBuild.Git` | - | src/ThroughlineBuild.Git/ThroughlineBuild.Git.csproj, src/ThroughlineBuild.Git/ProcessGitClient.cs, src/ThroughlineBuild.Phases/PlanPhase.cs, src/ThroughlineBuild.Phases/ThroughlineBuild.Phases.csproj, tests/ThroughlineBuild.Git.Tests/ |
| 02 | git-diff-fetcher | Materialize typed `GitDiff` from feature branch versus base ref | 01 | src/ThroughlineBuild.Contracts/IGitClient.cs, src/ThroughlineBuild.Git/ProcessGitClient.cs, tests/ThroughlineBuild.Git.Tests/DiffFetchTests.cs |
| 03 | automated-checks-runner | Run configurable build/test/lint commands and report typed results | - | src/ThroughlineBuild.Verification/ThroughlineBuild.Verification.csproj, src/ThroughlineBuild.Verification/CheckSpec.cs, src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs, tests/ThroughlineBuild.Verification.Tests/AutomatedChecksRunnerTests.cs |
| 04 | arch-doc-revisions | Close documentation gaps surfaced by op-07 and op-08 surfaces | - | docs/throughline-build-architecture.md |

### Briefs - detail

#### Brief 01: extract-process-git-client

Goal: Move `ProcessGitClient` (currently a sibling class in `src/ThroughlineBuild.Phases/PlanPhase.cs`) into its own project `ThroughlineBuild.Git`. The class is growing - op-07 added `CreateWorktreeAsync` and `HeadShaAsync`; op-08 will add `DiffAsync`; op-09 will add fetch, rebase, merge, push, branch-delete. Now is the cheap time to give it a proper home.

Inputs:
- The existing `ProcessGitClient` class inside `src/ThroughlineBuild.Phases/PlanPhase.cs` (currently ~7 methods)
- The `IGitClient` interface in `ThroughlineBuild.Contracts`
- The current project graph: Helpers references Contracts; Phases references Helpers and Contracts; Workers reference Contracts; the Cli project references everything

Outputs:
- New project `src/ThroughlineBuild.Git/ThroughlineBuild.Git.csproj` (classlib, net8.0, references `ThroughlineBuild.Contracts` only)
- New file `src/ThroughlineBuild.Git/ProcessGitClient.cs` containing the existing `ProcessGitClient` class verbatim, namespace `ThroughlineBuild.Git`
- `src/ThroughlineBuild.Phases/PlanPhase.cs` no longer contains the `ProcessGitClient` class; usings updated to import from `ThroughlineBuild.Git`
- `src/ThroughlineBuild.Phases/ThroughlineBuild.Phases.csproj` adds a project reference to `ThroughlineBuild.Git`
- `src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj` adds a project reference to `ThroughlineBuild.Git` (the CLI instantiates ProcessGitClient directly today via the default in PlanPhase's constructor; once the constructor default lives in a separate assembly the CLI still gets it implicitly, but an explicit reference is cleaner)
- New test project `tests/ThroughlineBuild.Git.Tests/` containing the existing `ProcessGitClientWorktreeTests.cs` and any other git-related tests, moved out of the Phases test project
- xUnit tests pass unchanged after the move (the class is byte-identical, only its location moved)

Acceptance:
- [ ] `ProcessGitClient` lives at `src/ThroughlineBuild.Git/ProcessGitClient.cs` with namespace `ThroughlineBuild.Git`
- [ ] `PlanPhase.cs` no longer declares `ProcessGitClient` (only imports it)
- [ ] `ImplementPhase.cs` continues to work unchanged (it imports the type, location-independent)
- [ ] Project graph is acyclic: `Git` references only `Contracts`; `Phases` references `Git`, `Helpers`, `Contracts`
- [ ] `dotnet build` succeeds across the solution
- [ ] All existing `ProcessGitClient`-related tests pass from their new project location

Notes: The `PlanPhase.cs` "leave that file location alone" instruction from op-07 was specifically about avoiding scope creep during op-07; it does not survive op-08. The right time to move the class is before it grows further. Op-09 will add ~6 more methods; doing this now keeps each git-method-addition brief small.

The CLI today constructs phases that take `IGitClient? gitClient = null` and fall back to `new ProcessGitClient()` as the default. That fall-back continues to work once the class lives in a referenced assembly; no CLI code changes beyond the project reference.

OOS:
- Do not change `ProcessGitClient` method signatures or behavior during the move
- Do not introduce new methods in this brief (B02 adds `DiffAsync`; op-09 adds the rest)
- Do not split `ProcessGitClient` into multiple classes
- Do not introduce a `LibGit2Sharp`-based alternative implementation alongside the process-based one
- Do not read claude-config source

#### Brief 02: git-diff-fetcher

Goal: Add `DiffAsync` to `IGitClient` and the extracted `ProcessGitClient`. Returns a typed `GitDiff` between a feature ref and a base ref. The `GitDiff` and `DiffEntry` records already exist in `ThroughlineBuild.Contracts.Verifier` (op-02); this brief wires the implementation.

Inputs:
- The `GitDiff` and `DiffEntry` records from `ThroughlineBuild.Contracts.Verifier/IVerifier.cs`
- The extracted `ProcessGitClient` from B01
- Git's `diff --name-status`, `diff --numstat`, and `diff` patch documentation

Outputs:
- `IGitClient.DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct)` returning `GitDiff`
- `ProcessGitClient.DiffAsync` implementation that:
  - Calls `git diff --name-status <fromRef>...<toRef>` (three-dot syntax; this returns changes on the feature branch since divergence, NOT changes on main since divergence) to enumerate changed paths and `DiffKind` per file
  - Calls `git diff --numstat <fromRef>...<toRef>` to fill `LinesAdded` and `LinesRemoved`
  - When `includePatchContent` is true, additionally calls `git diff <fromRef>...<toRef> -- <path>` per file and populates `PatchContent`
  - When `includePatchContent` is false, leaves `PatchContent` as null on every entry
- Patch content per file capped at ~100 KB; entries exceeding the cap have `PatchContent` set to null
- Binary files report `-` for line counts in `--numstat`; map to 0/0
- xUnit tests using a temp git repo: create a branch off main with a few changes (add, modify, delete, rename), call `DiffAsync`, assert the returned `GitDiff.Entries` match expected paths and kinds with and without patch content

Acceptance:
- [ ] `DiffAsync` returns a populated `GitDiff` for a feature branch with changes against main
- [ ] `DiffEntry.Kind` correctly maps to `Added`, `Modified`, `Deleted`, `Renamed` from git's name-status codes
- [ ] `LinesAdded` and `LinesRemoved` come from `--numstat`; binary files return 0/0 (not `-`)
- [ ] `PatchContent` is populated when `includePatchContent` is true and null when false
- [ ] Per-file patch content capped at ~100 KB; oversized patches return entries with null `PatchContent`
- [ ] Empty diff returns `GitDiff` with empty `Entries` (no exception)
- [ ] xUnit tests pass against a temp git repo

Notes: The three-dot range syntax (`<fromRef>...<toRef>`) is correct for "changes on the feature branch since it diverged from main." Two-dot syntax (`..`) would include changes on main since the divergence, which would confuse the reviewer. Use three-dot consistently.

OOS:
- Do not implement diff for uncommitted changes (always between refs)
- Do not implement diff filtering (path globs etc.); the caller filters
- Do not implement merge-base detection separately; the three-dot range handles it
- Do not read claude-config source

#### Brief 03: automated-checks-runner

Goal: A runner that executes a list of configurable shell commands (build, test, lint) in a working directory and returns structured per-check results. Deterministic, no LLM contact.

Inputs:
- A list of `CheckSpec` records describing what to run
- A working directory (the feature worktree path)
- `System.Diagnostics.Process` for command execution

Outputs:
- `src/ThroughlineBuild.Verification/ThroughlineBuild.Verification.csproj` (classlib, net8.0, references only stdlib - no project dependencies)
- `src/ThroughlineBuild.Verification/CheckSpec.cs` containing:

```csharp
public record CheckSpec(
    string Name,           // e.g. "build", "test", "lint"
    string Executable,     // e.g. "dotnet"
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

public record CheckResult(
    string Name,
    bool Passed,
    int ExitCode,
    string StdoutTail,     // last ~4 KB of stdout
    string StderrTail,     // last ~4 KB of stderr
    TimeSpan Elapsed);
```

- `src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs` with:

```csharp
public class AutomatedChecksRunner
{
    public Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct);
}
```

- Execution: sequential (do not parallelize for v1; build/test commands contend on the file system)
- Stop-on-first-failure is OPT-IN via a constructor flag on the runner; default is "run all specs even after a failure" so the reviewer sees the full picture
- xUnit tests cover: all-pass, one-failure-stops-early-mode-off (all results returned), one-failure-stops-early-mode-on (subsequent specs not invoked), timeout per spec (process killed, marked `Passed: false`), cancellation in mid-flight

Acceptance:
- [ ] `RunAsync` invokes each spec sequentially in the working directory
- [ ] Process stdout and stderr are captured; last ~4 KB of each preserved in `StdoutTail` and `StderrTail`
- [ ] Non-zero exit code maps to `Passed: false`
- [ ] Timeout kills the process and marks the result as failed with a clear note in `StderrTail` (e.g. "[runner] timeout after 30s")
- [ ] CancellationToken cancels in-flight checks; outstanding specs report not-run with `ExitCode = -1`, `Passed = false`
- [ ] xUnit tests pass

Notes: The check specs come from `.build/config.toml` under a new `[review.checks]` section; the TOML loader change is part of Plan B B08 (CLI wiring), not this brief. This brief assumes the specs arrive pre-constructed. Capturing only the tail of stdout/stderr keeps event log entries bounded; full output goes to wherever the user pipes the run (or is lost; acceptable for v1).

OOS:
- Do not implement check parallelism
- Do not implement check dependencies (e.g. "test depends on build passing")
- Do not interpret command output beyond exit code (the runner does not parse test output for individual failures)
- Do not implement retry on transient failures
- Do not read claude-config source

#### Brief 04: arch-doc-revisions

Goal: Close the documentation gaps the op-07 handoff identified (Section 5 of the handoff) plus the surfaces op-08 adds. Single brief; covers four small content updates in `docs/throughline-build-architecture.md`. No design changes - documentation only.

Inputs:
- `docs/throughline-build-architecture.md` (the current document)
- `docs/op-docs/op-07-handoff.md` Section 5 (the gap inventory)
- `IWorkflowPhase.cs`, `PhaseWorktreeLayout.cs`, the extended `IGitClient.cs`, `PlanBriefBuilder.cs`, `ImplementBriefBuilder.cs` (the actual surfaces being documented)
- This op-doc's Plan A briefs (B02 `DiffAsync`, B03 AutomatedChecksRunner) - document these as well so op-09 design reads an accurate doc

Outputs:
- Updated `docs/throughline-build-architecture.md` reflecting:
  - **Section 5.2 (State Machine)** - add a paragraph naming `IWorkflowPhase` as the shared contract that `PlanPhase`, `ImplementPhase`, and `ReviewPhase` implement; explain the `Phase` property + typed-result-plus-PhaseResult-conversion pattern. Cross-reference Section 6 for the interface definition.
  - **Section 5.6 (Helpers)** - add `PhaseWorktreeLayout` to the list (one line). Add `AutomatedChecksRunner` to a new "Verification helpers" subsection (one paragraph) - note it lives in `ThroughlineBuild.Verification`, distinct from the pure helpers in `ThroughlineBuild.Helpers`.
  - **Section 5.7 (Brief Constructor)** - replace the generic `(Ticket, RepoState, Phase) -> Brief` signature with a note that brief builders are per-phase static classes (`PlanBriefBuilder.Build`, `ImplementBriefBuilder.Build`, `ReviewBriefBuilder.Build`) with phase-specific signatures, all returning `Brief`. Document that ReviewBriefBuilder's signature differs (takes `GitDiff` and `CheckResult` list, not `RepoState`).
  - **Section 5.8 (Verifier)** - replace the speculative description with reality: `ClaudeCodeReviewer` is the first concrete `IVerifier`; the `implementerBrief` and `implementerResult` parameters are reconstructed by ReviewPhase from ticket+git state (no shared in-memory context with the implementer); cross-vendor verification is supported by the interface and deferred to v1.1.
  - **Section 6 (Interfaces & Contracts)** - extend the `IGitClient` block (currently absent from this section) with all methods including `CreateWorktreeAsync`, `HeadShaAsync`, `DiffAsync`. Add `IWorkflowPhase` and `PhaseResult` records. Add `CheckSpec` and `CheckResult` from the Verification project.
- The doc reads consistently end-to-end; no contradictions between sections

Acceptance:
- [ ] All four sections listed above updated
- [ ] `IWorkflowPhase` named and described
- [ ] Per-phase brief builders documented (Plan, Implement, Review)
- [ ] `IGitClient` surface in Section 6 lists every method present in the interface today
- [ ] `IVerifier` description updated to reflect the implementer-artifacts reconstruction approach
- [ ] Doc passes a read-through: no orphaned references to "the brief constructor" as singular; no descriptions of features that don't exist in code
- [ ] Word count of the arch doc grows by no more than ~30%; this is a closing of gaps, not a rewrite

Notes: The handoff is explicit that none of the gaps require architectural revision - they are documentation drift. Resist the urge to redesign anything in the arch doc during this brief; the design that shipped is what gets documented. If the arch doc has substantive claims that the shipped code contradicts (none flagged in the handoff, but possible), flag them in the brief's commit message rather than silently rewriting them.

OOS:
- Do not propose new architectural patterns
- Do not propose new components beyond what op-08 actually adds
- Do not delete or rewrite Section 7 (Bootstrap Discipline), Section 8 (Migration Plan), Section 10 (Risk Register), or the Appendix
- Do not update any other `.md` files in the repo
- Do not read claude-config source

## Plan B: Review phase composition

### Goal

Compose the Plan A foundations into the review flow. `ReviewBriefBuilder` produces the verifier's brief from ticket + diff + reconstructed implementer result + check results. `ClaudeCodeReviewer` implements `IVerifier` by dispatching a `ClaudeCodeAgent` against that brief and parsing a `Verdict` from the WORKER_RESULT. `ReviewPhase` orchestrates everything. CLI wiring.

Brief sequence: B05 (brief builder) first. B06 (reviewer) depends on B05. B07 (phase) depends on B06. B08 (CLI) depends on B07.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | review-brief-builder | Pure function building the verifier's brief from ticket + diff + check results + reconstructed implementer artifacts | - | src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs, tests/ThroughlineBuild.Briefs.Tests/ReviewBriefBuilderTests.cs |
| 06 | claude-code-reviewer | `ClaudeCodeReviewer` implements `IVerifier` via `ClaudeCodeAgent` dispatch | 05 | src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs, tests/ThroughlineBuild.Verification.Tests/ClaudeCodeReviewerTests.cs |
| 07 | review-phase | `ReviewPhase` orchestrating reconstruction, checks, verifier, verdict-driven state transitions | 06 | src/ThroughlineBuild.Phases/ReviewPhase.cs, tests/ThroughlineBuild.Phases.Tests/ReviewPhaseTests.cs |
| 08 | review-cli | `build review <id>` subcommand with checks config in TOML | 07 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, src/ThroughlineBuild.Cli/Config.cs, tests/ThroughlineBuild.Cli.Tests/ReviewCliTests.cs |

### Briefs - detail

#### Brief 05: review-brief-builder

Goal: A pure function that produces the verifier's `Brief` from the ticket, the diff, the (reconstructed) implementer's worker result, and the automated check results.

Inputs:
- `Ticket` (carries the planned description for context)
- `GitDiff` from B02
- `WorkerResult` reconstructed by ReviewPhase (see B07 for the reconstruction approach)
- `IReadOnlyList<CheckResult>` from B03

Outputs:
- `src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs` with:

```csharp
public static class ReviewBriefBuilder
{
    public static Brief Build(
        Ticket ticket,
        GitDiff diff,
        WorkerResult implementerResult,
        IReadOnlyList<CheckResult> checkResults);
}
```

- The returned `Brief.Instruction` is a markdown prompt that:
  - States the verifier's job: independently assess whether the diff implements the plan correctly
  - Includes ticket Id, title, type, size, risk
  - Includes the ticket's `DescriptionHtml` (which contains the plan from PlanPhase)
  - Renders the `GitDiff` as a list of changed files with line counts, followed by patch content under fenced code blocks where `PatchContent` is non-null
  - Renders the `implementerResult.Summary` (synthetic in v1 - see B07 - but still informative)
  - Renders the `checkResults`: per-check pass/fail with the tail of stderr for failed checks
  - Specifies the WORKER_RESULT envelope (bare-marker format consistent with op-05) with `metadata.verdict` (one of `Pass`, `Rework`, `Fail`), `metadata.rationale` (string), `metadata.checks_failed` (list of strings naming specific concerns)
- `Brief.Phase == Phase.Review`
- `Brief.AllowedWrites` is empty (the verifier is read-only)
- `Brief.Context` includes `feature_branch`, `base_ref`, `files_changed_count`
- Patch content rendering is bounded: total instruction size should stay under ~50 KB; if cumulative patch content exceeds the budget, truncate per-file with an indicator and prefer breadth (one chunk per file) over depth (full content of a few files)
- xUnit tests covering: clean diff (Pass-able), diff with failing checks (Rework-able), large diff that triggers truncation, empty diff (still emits a brief asking the verifier to confirm there is nothing to review)

Acceptance:
- [ ] `Build` is a pure function (no I/O)
- [ ] Returned `Brief.Phase == Phase.Review`
- [ ] Instruction includes the diff (file list + patch content where available)
- [ ] Instruction includes the implementer's `Summary` and the automated check results
- [ ] Instruction specifies the WORKER_RESULT envelope with the three metadata keys: `verdict`, `rationale`, `checks_failed`
- [ ] Verdict values explicitly enumerated in the prompt: `Pass`, `Rework`, `Fail`
- [ ] Total instruction size stays under ~50 KB on typical diffs; large diffs truncate gracefully with per-file indicators
- [ ] xUnit tests pass

Notes: The reviewer's brief is intentionally not the same shape as the implementer's brief. The implementer is told what to do; the reviewer is shown what was done. The implementer's `Summary` field is synthetic in v1 (ReviewPhase reconstructs it from the marker comment, not from a stored value) - including it in the brief is a deliberate trade-off: even a synthetic "Reconstructed from implemented_at: {sha}; touched N files" is useful framing for the verifier. If dogfooding shows this biases the verdict, drop the summary in a follow-up.

Bare-marker WORKER_RESULT format (per op-05): the brief instructs the worker to emit `WORKER_RESULT` on its own line followed by JSON on the next non-empty line. NOT a fenced JSON block. Match the format ImplementBriefBuilder uses.

OOS:
- Do not include the implementer's `metadata.commit_sha` or token usage in the brief; both are irrelevant to the verdict
- Do not include the planned_at marker (already in the description)
- Do not include rationale guidance like "look for these patterns" (the reviewer should form its own judgment)
- Do not parse the diff for semantic content; render it as-is and let the verifier read
- Do not read claude-config source

#### Brief 06: claude-code-reviewer

Goal: `ClaudeCodeReviewer` implements `IVerifier` by dispatching a `ClaudeCodeAgent` worker against a brief produced by `ReviewBriefBuilder`, then parsing a `Verdict` from the WORKER_RESULT.

Inputs:
- `IVerifier` interface from `ThroughlineBuild.Contracts`
- `IWorkerAgent` (typically a `ClaudeCodeAgent` from op-03)
- `ReviewBriefBuilder` from B05
- `CheckResult` list from B03 (per-run context passed via constructor)
- `Ticket` (per-run context passed via constructor)

Outputs:
- `src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs` with:

```csharp
public class ClaudeCodeReviewer : IVerifier
{
    public ClaudeCodeReviewer(
        IWorkerAgent worker,
        Ticket ticket,
        IReadOnlyList<CheckResult> checkResults,
        WorkerOptions workerOptions,
        string workingDirectory);

    public Task<Verdict> VerifyAsync(
        Brief implementerBrief,
        GitDiff diff,
        WorkerResult implementerResult,
        CancellationToken ct);
}
```

- The constructor takes per-run context (ticket, check results, working directory, worker options). `IVerifier.VerifyAsync` takes the implementer brief, diff, and result per the interface contract. The reviewer composes everything into a `ReviewBriefBuilder.Build` call.
- Implementation:
  - Build the brief via `ReviewBriefBuilder.Build(ticket, diff, implementerResult, checkResults)`
  - Dispatch via `worker.ExecuteAsync(brief, workingDirectory, workerOptions, ct)` - run in the MAIN worktree (not the feature worktree); the diff is already in the brief and the verifier is read-only
  - Parse the worker's WORKER_RESULT
  - Map `metadata.verdict` string to `VerdictKind`: `"Pass"` -> `Pass`, `"Rework"` -> `Rework`, `"Fail"` -> `Fail`; unknown values -> `Fail` with rationale noting the malformed verdict
  - Return `Verdict(Kind, Rationale: metadata.rationale ?? "", ChecksFailed: metadata.checks_failed ?? Array.Empty<string>())`
  - If the worker itself fails (`Status != Ok`), return `Verdict(Fail, "verifier worker failed: {worker reason}", Array.Empty<string>())`
- xUnit tests with mocked `IWorkerAgent`: Pass verdict, Rework verdict, Fail verdict, malformed verdict string (maps to Fail), worker failure (maps to Fail with worker reason)

Acceptance:
- [ ] `ClaudeCodeReviewer` implements `IVerifier`
- [ ] `VerifyAsync` builds the review brief via `ReviewBriefBuilder` and dispatches the worker against the main worktree
- [ ] Worker WORKER_RESULT metadata is parsed into `Verdict`: kind, rationale, checks-failed
- [ ] Unknown verdict strings map to `Fail` with a clear rationale
- [ ] Worker failure (`Status != Ok`) maps to `Verdict(Fail, ...)`, not an exception
- [ ] `WorkerOptions.AllowedTools` is honored - ReviewPhase passes a read-only tool set (e.g. `["Read", "Grep", "Glob"]`); the reviewer does not write to disk
- [ ] xUnit tests cover the listed cases with a mocked `IWorkerAgent`

Notes: The verifier worker runs in the MAIN worktree, not the feature worktree, because the diff is provided in the brief and the verifier should not be tempted to wander into the working tree. The verifier's allowed-tools is enforced at the WorkerOptions layer that ReviewPhase constructs (not hardcoded in this class).

The verdict's `Pass | Rework | Fail` taxonomy aligns with `VerdictKind` in `ThroughlineBuild.Contracts.Models`. There is no `NeedsClarification` verdict in v1; ambiguous cases should map to `Rework` with rationale so the implementer can resolve.

The metadata parsing follows the same JsonElement-unwrap pattern ImplementPhase uses for `llm_usage` (and the same `FlattenLlmUsage` private static helper concept). Copy the pattern; do not extract to a shared helper in this brief (OOS - touches multiple phases).

OOS:
- Do not implement cross-vendor verifier wiring (deferred to v1.1; the IVerifier interface already supports it)
- Do not parse the worker's free-text output for verdict hints; only the typed WORKER_RESULT metadata counts
- Do not retry the verifier on Rework or Fail
- Do not implement a second-opinion mechanism (call verifier twice)
- Do not extract `FlattenLlmUsage` to a shared helper (deferred; touches multiple phases)
- Do not preserve any base64 round-trip pattern from prior systems
- Do not read claude-config source

#### Brief 07: review-phase

Goal: `ReviewPhase` orchestrates the review flow: validate state, reconstruct implementer artifacts, fetch diff, run automated checks, construct and run the verifier, apply the verdict by transitioning state and posting a marker comment.

Inputs:
- `ITicketing`, `IWorkerAgent`, `IEventSink`, `IGitClient` (constructor injection, mirroring `ImplementPhase`'s shape)
- `BuildOptions` (existing record at `src/ThroughlineBuild.Phases/PlanPhase.cs:9-13`)
- `AutomatedChecksRunner` from B03
- `IVerifier` (a `ClaudeCodeReviewer` constructed per-run)
- `PhaseWorktreeLayout` from `ThroughlineBuild.Helpers`
- `MarkerParser` from `ThroughlineBuild.Helpers` (for finding the `implemented_at` marker)
- `ImplementBriefBuilder` from `ThroughlineBuild.Briefs` (called by ReviewPhase to reconstruct the implementer's brief)
- A new `ReviewOptions` record carrying check specs and verifier worker options

Outputs:
- `src/ThroughlineBuild.Phases/ReviewPhase.cs` with:

```csharp
public record ReviewOptions(
    IReadOnlyList<CheckSpec> Checks,
    WorkerOptions VerifierWorkerOptions);

public record ReviewResult(
    bool Success,
    string TicketId,
    VerdictKind? Verdict,
    string? VerdictRationale,
    IReadOnlyList<string> ChecksFailed,
    string? FailureReason);

public class ReviewPhase : IWorkflowPhase
{
    public ReviewPhase(
        ITicketing ticketing,
        IWorkerAgent verifierWorker,
        IEventSink events,
        BuildOptions buildOptions,
        ReviewOptions reviewOptions,
        IGitClient? gitClient = null);

    public Phase Phase => Phase.Review;
    public Task<ReviewResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
    Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
}
```

- Phase logic in order:

  1. Fetch ticket via `_ticketing.GetAsync`
  2. Validate `ticket.State == TicketState.InReview`; if not, return `ReviewResult(false, ticketId, null, null, Array.Empty<string>(), "ticket not in InReview state")`. No events, no state change.
  3. Compute `PhaseWorktreeNames = PhaseWorktreeLayout.Compute(ticketId, ticket.Title, workingDirectory)`; verify the feature worktree exists by calling `_git.ListWorktreesAsync` and matching by `Branch` or `Path`; if not present, return `ReviewResult(false, ..., "feature worktree not found at {path}")`
  4. Get current main SHA via `_git.RevParseAsync("origin/main", workingDirectory, ct)`, wrapped in try/catch matching ImplementPhase step 3
  5. Reconstruct implementer brief: build `RepoState(mainSha, topLevelEntries)` and call `ImplementBriefBuilder.Build(ticket, repoState, worktreeNames.BranchName, worktreeNames.WorktreePath)`. This produces the same brief the implementer received (deterministic from inputs).
  6. Reconstruct implementer WorkerResult:
     - Scan ticket comments via `_ticketing.GetCommentsAsync`; find the most recent `[implemented_at: <sha>]` marker via `MarkerParser`; capture the SHA as `implementerCommitSha`. If absent, return `ReviewResult(false, ..., "no implemented_at marker found - ticket reached InReview without an implement marker, ReviewPhase cannot reconstruct implementer state")`
     - Fetch the diff via `_git.DiffAsync(fromRef: "origin/main", toRef: worktreeNames.BranchName, workingDirectory, includePatchContent: true, ct)`
     - Construct `implementerResult = new WorkerResult(Status.Ok, $"Reconstructed from implemented_at: {implementerCommitSha} ({diff.Entries.Count} files changed)", diff.Entries.Select(e => e.Path).ToList(), null, new Dictionary<string, object> { ["commit_sha"] = implementerCommitSha })`
  7. Run automated checks via `_checksRunner.RunAsync(reviewOptions.Checks, worktreeNames.WorktreePath, ct)` (instantiate `AutomatedChecksRunner` lazily; one runner per phase is fine)
  8. Construct the verifier: `var verifier = new ClaudeCodeReviewer(_verifierWorker, ticket, checkResults, reviewOptions.VerifierWorkerOptions, workingDirectory);`
  9. Emit `WorkerSpawn` event with `Data = { worker: <verifier worker name>, role: "verifier" }`
  10. Call `verdict = await verifier.VerifyAsync(implementerBrief, diff, implementerResult, ct)`
  11. Emit `VerifierVerdict` event with `Data = { kind: verdict.Kind.ToString(), checks_failed_count: verdict.ChecksFailed.Count }`
  12. If the verifier worker reported `llm_usage` in its metadata, emit `LlmCall` event using the same `FlattenLlmUsage` pattern ImplementPhase uses (copy the private static method into ReviewPhase verbatim - no extraction in this brief)
  13. Apply verdict and post marker comment + transition:
      - `Pass`: post `<p><strong>reviewed:</strong> pass - {rationale}</p>` via `_ticketing.CreateCommentAsync`; emit `TicketWrite` event with `action: "create_comment"`; ticket stays in InReview (no transition)
      - `Rework`: post `<p><strong>reviewed:</strong> rework - {rationale}{checksList}</p>` where `checksList` is empty when ChecksFailed is empty or `<br/>checks_failed: {comma-joined names}` otherwise; emit `TicketWrite`; transition InReview -> InProgress via `_ticketing.TransitionAsync`; emit `StateTransition` event with `Data = { from: "InReview", to: "InProgress" }`
      - `Fail`: post `<p><strong>reviewed:</strong> fail - {rationale}{checksList}</p>`; emit `TicketWrite`; ticket stays in InReview (no transition)
  14. Return `ReviewResult(Success: true, ticketId, Verdict: verdict.Kind, VerdictRationale: verdict.Rationale, ChecksFailed: verdict.ChecksFailed, FailureReason: null)`

- The interface-explicit `IWorkflowPhase.RunAsync` overload converts `ReviewResult` to `PhaseResult` with `Outputs` carrying `verdict`, `rationale`, `checks_failed_count` (as strings). On phase failure (Steps 2, 3, 4, 6 early-exits), `Outputs` is an empty dictionary.
- xUnit tests with mocked dependencies covering:
  - Pass verdict (ticket stays in InReview, comment posted, no state transition)
  - Rework verdict with empty ChecksFailed (transitions to InProgress, simple comment)
  - Rework verdict with non-empty ChecksFailed (transitions to InProgress, comment includes checks_failed list)
  - Fail verdict (ticket stays in InReview, comment posted)
  - Ticket not in InReview (clean failure, no transitions, no events)
  - Worktree missing (clean failure, no transitions)
  - No implemented_at marker found (clean failure, no transitions)
  - Verifier worker fails internally (verdict is Fail, ticket stays in InReview, recorded normally)

Acceptance:
- [ ] All steps implemented in order
- [ ] State transitions go through `_ticketing.TransitionAsync` only; no side-channel writes
- [ ] Pass verdict keeps ticket in InReview; Rework transitions to InProgress; Fail keeps in InReview
- [ ] `reviewed: pass`, `reviewed: rework`, `reviewed: fail` markers are literally those strings (consistent with `wontfix:` / `deferred:` / `reopened:` convention from op-06)
- [ ] Implementer brief and result are reconstructed from ticket+git state - no synthetic empty placeholders passed to `IVerifier.VerifyAsync`
- [ ] Automated check failures are included in the verifier's brief; verifier owns the verdict (no short-circuit in v1)
- [ ] LlmCall event emitted when verifier worker reports `llm_usage` metadata
- [ ] WorkflowEvent emitted at each significant step using snake_case Data conventions (matches event-log-format.md)
- [ ] xUnit tests cover the listed scenarios
- [ ] The `IWorkflowPhase.RunAsync` interface call returns a `PhaseResult` whose `Outputs` carries `verdict`, `rationale`, `checks_failed_count` on success
- [ ] `docs/event-log-format.md` updated: add Review-phase events to a new happy-path example section (Pass, Rework, Fail variants), document `VerifierVerdict.kind` taxonomy (`Pass | Rework | Fail`), document the `WorkerSpawn.role` field (`role: "verifier"` distinguishes verifier-spawn from implementer-spawn in chained runs)

Notes: One open policy question deferred to follow-up: short-circuit on check failure. Arguments for short-circuit: saves tokens; broken-build code is unambiguously not Pass. Arguments against: the verifier may catch issues OTHER than the failing check that need attention, and combining gives the implementer one complete list. v1 runs the verifier even when checks fail. If dogfooding shows the verifier rubber-stamps Rework on check failures (no additional insight), add a short-circuit policy.

Drift check: ReviewPhase does NOT perform a drift check. At review time the implementer has already committed; "drift" would mean "main moved between plan and review" - ship-phase's rebase will catch the same condition with sharper semantics (a rebase conflict is a concrete failure; a drift warning is informational and unactionable at review time).

OOS:
- Do not invoke ImplementPhase from ReviewPhase on a Rework verdict (user runs `build implement` again themselves, or chain orchestrates in op-11)
- Do not auto-decruft on Fail (user inspects)
- Do not perform a drift check at review time
- Do not implement a `--verifier-vendor` flag (defer to v1.1)
- Do not stream verifier output
- Do not persist implementer brief or result to disk in this op-doc (reconstruction is the v1 approach; persistence is a follow-up if dogfooding surfaces the need)
- Do not extract `FlattenLlmUsage` to a shared helper (deferred; touches three phases)
- Do not preserve any base64 round-trip pattern from prior systems
- Do not read claude-config source

#### Brief 08: review-cli

Goal: Wire `build review <id>` into the CLI dispatch. Extend the TOML config schema with a `[review]` section.

Inputs:
- The existing `src/ThroughlineBuild.Cli/Program.cs` (already routes `plan`, `implement`, and the four ticket-state commands; see lines 27 and 152 for the verb-validation pattern)
- `src/ThroughlineBuild.Cli/CliUsage.cs` (the extracted usage text from op-07 B06; add the `review` verb to the help banner)
- `ReviewPhase` from B07
- The existing TOML config loader (`BuildConfigLoader` in `Config.cs`)

Outputs:
- Updated `Program.cs` adding the `review` verb:
  - Add `review` to the verb-validation block at line 27 (`if (verb == "plan" || verb == "implement" || verb == "review")`)
  - Add `review` to the verb-dispatch tail block (the `if (verb != "plan" && verb != "implement")` check at line 152 becomes `if (verb != "plan" && verb != "implement" && verb != "review")`)
  - Add a `review` dispatch branch alongside `plan` and `implement` that instantiates `ReviewPhase` with dependencies and calls `RunAsync`
  - Terminal output on success: `Review complete: {id} verdict={Pass|Rework|Fail}` plus a one-line rationale tail
- Updated `CliUsage.cs` adding `review` to the verb list with one-line description
- Updated `Config.cs` adding the `[review]` config section. New record fields:

```toml
[review]
verifier_timeout_minutes = 15
verifier_allowed_tools = ["Read", "Grep", "Glob"]   # read-only

[[review.checks]]
name = "build"
executable = "dotnet"
arguments = ["build", "--nologo", "--configuration", "Release"]
timeout_minutes = 5

[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test", "--nologo", "--configuration", "Release", "--no-build"]
timeout_minutes = 10
```

- The verifier model is configured at the worker layer (`config.Workers.ClaudeCodeExecutable`); v1 does not split model selection per-phase
- Exit codes:
  - 0 on Pass verdict
  - 1 on Rework or Fail verdict (non-Pass exits non-zero so `chain` op-11 can branch on the exit code)
  - 2 on config error (existing convention)
  - 3 on missing secret (existing convention)
  - 4 on phase failure not tied to verdict (worktree missing, no implemented_at marker, git error, etc.)
- xUnit `ReviewCliTests` (in `tests/ThroughlineBuild.Cli.Tests/`):
  - `build review TLB-X` parses and dispatches to ReviewPhase (mock at the DI seam)
  - `build review` (no ticket id) prints usage and exits 2
  - Exit codes match the verdict on each path
  - Help text includes `review`

Acceptance:
- [ ] `build review <id>` runs from a terminal
- [ ] Verb appears in `build --help` (via `CliUsage.UsageText`)
- [ ] Verdict Pass exits 0; Rework or Fail exits 1; phase failure exits 4; config or secret errors exit 2/3
- [ ] `.build/config.toml.example` documents the `[review]` section with comments
- [ ] xUnit tests pass

Notes: The non-zero exit on Rework/Fail is the signal `chain` (op-11) will use to halt or branch. For interactive users the exit code is mostly informational; the verdict appears in stdout and the marker comment lands in Plane.

If any review-related CLI test calls `Console.SetError` (the test pattern surfaced in op-07 handoff Section 2 item 4-6), it must join the `[Collection("CommandConsoleTests")]` xUnit collection to avoid the race condition fixed in op-07. ReviewCliTests likely does not need this since it tests dispatch routing rather than stderr writes, but if any new test writes to `Console.Error`, opt into the collection.

OOS:
- Do not implement `--verifier-vendor` or `--verifier-model` flags (deferred to v1.1 cross-vendor work)
- Do not implement `--skip-checks` or `--checks-only` flags
- Do not change the existing verb dispatch patterns for `plan` or `implement`
- Do not read claude-config source

## What done looks like

After op-08 lands, a real ticket continues further through the new pipeline. From a fresh terminal with TLB-50 in InReview (post-implement):

```
$ build review TLB-50
Review starting: TLB-50
  worktree: /repo/.worktrees/ticket-tlb-50-extract-driftcomparator
  branch: ticket/tlb-50-extract-driftcomparator
  checks: build (pass, 12.4s), test (pass, 24.1s)
  verifier: claude-code (model from worker config)
  ... [verifier reads diff, forms verdict] ...
Review complete: TLB-50 verdict=Pass
  rationale: Diff implements the planned changes correctly. Tests cover the new branch.
  state: InReview (unchanged)
  Next: build ship TLB-50 (op-09, not yet shipped)
```

For a Rework verdict, the rationale and checks_failed list land in the marker comment and the ticket goes back to InProgress:

```
$ build review TLB-50
  ... [checks pass, verifier flags missing test coverage] ...
Review complete: TLB-50 verdict=Rework
  rationale: New branch lacks test coverage for the drift-detection path.
  checks_failed: ["missing test coverage for DriftComparator.Compare null inputs"]
  state: InReview -> InProgress
  Next: rerun build implement
```

The event log captures: WorkerSpawn with `role: "verifier"`, VerifierVerdict with `kind: "Pass" | "Rework" | "Fail"` and `checks_failed_count`, LlmCall with the verifier worker's token usage, TicketWrite for the reviewed-marker comment, optional StateTransition (only on Rework). The `[review]` config section drives both the verifier worker options and the check specs.

`IVerifier` now has one concrete implementation; adding Codex or Gemini variants for v1.1 cross-vendor work is a 50-100 line adapter implementing the same interface. ReviewPhase consumes whichever is configured. Verdict-driven state transitions are deterministic. Model contact is bounded to one call per review run.

`ProcessGitClient` now lives in its own project and is ready for op-09 to grow it with fetch / rebase / merge / push / branch-delete. The arch doc is current as of op-08 close.

**Cost comparison feasibility note.** The op-07 cost-comparison run against `/ticket-act` has not yet been performed (op-07 handoff Section 6). Op-08 ship without it; the spine-level cost ratio measurement should run on a real ticket after op-08 lands so both implement and review LlmCall events are captured in one session for direct comparison against the old `/ta + /tr` pair.

Three follow-ups surfaced by this op-doc's design, worth tracking but not in scope here:

- **Implementer artifacts persistence.** ReviewPhase reconstructs the implementer brief and synthesizes a WorkerResult. The synthetic Summary is the weakest link - the verifier sees "Reconstructed from implemented_at: ..." instead of what the implementer self-reported. If dogfooding shows verdicts that would have benefited from the implementer's actual Summary, add `.build/artifacts/<ticket>/implement.json` written by ImplementPhase and read by ReviewPhase. The reconstruction code paths in ReviewPhase Step 6 are the integration point.

- **Short-circuit on check failure.** If the verifier consistently produces Rework verdicts when automated checks fail without adding insight beyond the failing checks, add a policy that synthesizes a Rework verdict from check failures alone and skips the model verifier. The policy gate lives at ReviewPhase Step 7-8.

- **FlattenLlmUsage extraction.** Three phases (Plan, Implement, Review) now each contain a private static `FlattenLlmUsage` and `UnwrapJsonElement`. Extract to `ThroughlineBuild.Helpers/LlmUsageFlattener.cs` once the third copy is in place (it will be, after op-08). The refactor is small and the call sites are stable.

The spine is now plan -> implement -> review. The remaining critical-path piece is ship (op-09), which converts an InReview ticket with a Pass verdict into a Done ticket on main.