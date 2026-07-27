# Partial test selection: investigation + design brainstorm

## Context

Today Throughline Build runs the **whole test suite** on every ticket and every rework round.
Tests are not special-cased anywhere in the code - they are ordinary `CheckSpec` entries
(a `name`, an `executable`, a static `arguments[]` array, a timeout) defined in
the local `.build/config.toml` and executed by
[AutomatedChecksRunner.RunSingleAsync](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L59),
which shells the command out as a subprocess and grades it by exit code (0 = pass).

There are two check arrays:

- `[[review.checks]]` - run during the review phase; failures are *surfaced* to the verifier
  but are **non-blocking** (the local `.build/config.toml` review section).
- `[[ship.regression_checks]]` - run post-rebase, pre-merge; a **hard** gate with main-branch
  baseline comparison (the local `.build/config.toml` ship section).

Both currently say the same thing - `dotnet test --no-build -c Release` - i.e. the full suite,
every time, for every ticket and every rework round.

**The idea:** for an individual ticket, run only the tests that matter - the tests the agent
created plus the tests that exercise the changed code - and reserve the full suite for chains
and for ship. The hard constraints from the requester:

1. **Language- and machine-agnostic.** Throughline Build feeds work to agents across any language and
   toolchain. We cannot bake `dotnet`/`pytest`/`jest`/`go` test-discovery logic into the C# core.
2. **Deterministic, OR cheap agent judgment.** Either the selection is computed deterministically,
   or the agent - which already holds the full context - decides it without churning extra tokens.

No code has been written. This doc is investigation plus a design recommendation.

## What already exists (the ground truth)

Two deterministic change-signals are computed today:

1. **Agent-reported** - `WorkerResult.FilesChanged`, parsed from the `WORKER_RESULT` envelope by
   [WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L27).
2. **Git-truth** - `DiffAsync` -> `DiffEntry { Path, DiffKind Added/Modified/Deleted/Renamed }`,
   computed in [ReviewPhase.cs:183](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L183) (with full
   patch content) and again in ship. `PhaseSummaryBuilder` already walks this diff, **classifies
   test files by path** (`tests/` prefix) and counts `TestsAddedCount`.

So we already know - deterministically, with no extra agent work - the exact set of changed files
and which of them are added test files. That is half the problem solved for free.

The `WORKER_RESULT` envelope is also cheaply extensible: `metadata` is an AOT-safe
`Dictionary<string, JsonElement>`, and named fenced blocks (`<<<NAME_START` / `<<<NAME_END`) are
already supported. Adding a typed field follows the existing `files_changed` pattern.

Briefs already hand the agent `project_test_command` as informational context
([ImplementBriefBuilder.cs:81](../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L81)).

## The genuinely hard part

The request decomposes into two very different sub-problems.

### (A) "Tests the agent created" - EASY and deterministic

These are added/modified files in the diff that live under a test path. We already detect them.
This is language-agnostic **if** the test-path glob is configurable per project
(`tests/**`, `**/*_test.go`, `**/*.test.ts`, `**/test_*.py`). Zero agent tokens required.

### (B) "Tests that the code change touched" - HARD

Mapping a changed *source* file to the tests that exercise it is inherently toolchain-specific.
There is no language-neutral, deterministic primitive for it. Every option is compromised:

- **Coverage map** (file -> covering-tests index): most accurate, but needs a prior instrumented
  run, is language-specific, goes stale, and is heavy. Rejected - violates "no token/time churn"
  and is not agnostic.
- **Static import/dependency graph**: requires a language-specific parser per ecosystem.
  Rejected for an agnostic tool.
- **Naming convention** (`PaymentService.cs` <-> `PaymentServiceTests.cs`): deterministic and
  cheap, but fragile - misses integration tests and cross-cutting tests - and forces every project
  to author a regex.
- **Coarse "run the module/project containing the change"**: simple and safe-ish, but in a
  monorepo or a single big test project (like *this* repo, where one `dotnet test` covers
  everything) it collapses back to the full suite.

There is a **second wall** even once you know *which* tests to run: translating a file set into a
runner invocation is itself toolchain-specific. pytest/jest/go accept file *paths* as positional
args; `dotnet test` does **not** - it is project/solution-oriented and needs
`--filter FullyQualifiedName~X`. No single substitution mechanism baked into Throughline Build works
everywhere. That fact alone is the argument that selection logic cannot live in the C# core.

## Mechanisms (ranked)

### 1. Agent-emitted test command (recommended primary)

The implementing agent already has the whole repo context loaded, knows the language and
framework, and just wrote the tests. Asking it to emit *how to run the relevant subset* costs
about one line of output - no extra churn. Extend the `WORKER_RESULT` envelope:

```json
"test_selection": {
  "command": ["dotnet","test","--no-build","--filter","FullyQualifiedName~PaymentServiceTests|FullyQualifiedName~CheckoutFlow"],
  "rationale": "new PaymentServiceTests + existing CheckoutFlow integration tests touch the changed code"
}
```

Throughline Build runs it verbatim as the review-phase `test` check.

- **Agnostic by construction** - the agent forms the invocation, not C# code.
- **Cheap** - context is already paid for.
- **Risk: trust + under-selection.** Mitigate by validating the command begins with the configured
  `project_test_command` / an executable allowlist (reject arbitrary commands), reject absolute
  paths and `..` escapes, and keep ship's full suite as the backstop (mechanism 3).

### 2. Config-driven substitution template (deterministic fallback)

Add an optional selective variant to the check spec that Throughline Build fills from the git diff using
tokens it can compute deterministically:

```toml
[[review.checks]]
name = "test"
executable = "dotnet"
arguments = ["test","--no-build","-c","Release"]
[review.checks.selective]
test_globs = ["tests/**/*.cs"]
mode = "filter"                       # or "paths" for pytest/jest/go
arg_template = ["--filter", "{changed_test_classes}"]
on_empty = "skip"                     # no relevant tests changed -> skip, or "full"
```

Throughline Build exposes tokens like `{changed_files}`, `{added_test_files}`, `{changed_test_classes}`
(basename-derived). The *mapping policy* lives in config, authored by whoever owns the project, so
the C# core stays agnostic. This is the deterministic path when you do not trust the agent or want
reproducibility. It nails sub-problem (A) cleanly; for (B) it is only as good as the configured
naming convention.

### 3. Full suite (unchanged) - the correctness backstop

Keep `[[ship.regression_checks]]` exactly as-is: always full, always a hard gate. Partial selection
then becomes purely a *review / rework-loop latency optimization*, never a correctness claim.
Under-selection during review cannot ship a regression, because ship re-runs everything.

## Recommended design: layered, with the chain insight built in

```
single ticket, review/rework loop  -> SCOPED tests  (agent-emitted #1, else config #2, else full)
chain, per-ticket review loop      -> SCOPED tests per ticket (fast feedback)
chain ship / single ship           -> FULL suite (existing hard gate, unchanged)
```

This matches the requester's instinct directly: individual ticket runs just what changed; the chain
has the full suite available. The refinement: even in a chain you want scoped tests *during* each
ticket's rework loop (fast iteration), and the full suite lands once at ship - which the code
already does. The shared-worktree chain accumulates every ticket's changes, so one full run at
chain-ship covers cross-ticket interactions that no per-ticket subset would catch.

**Resolution order per check:** agent selector (if valid) -> config template -> full suite. Every
fallback is safe (full), so a missing or garbled selector degrades to today's behavior, never to
"ran nothing."

## Determinism and safety notes

- **Determinism.** Mechanism 2 is fully deterministic (same diff -> same command). Mechanism 1 is
  deterministic in *execution* (Throughline Build runs the exact string) but the *selection* is model
  judgment - acceptable because ship is the deterministic gate. If bit-reproducible review is
  required, make mechanism 2 the default and mechanism 1 opt-in.
- **The empty-set trap.** If no test files changed and no source maps to tests, "run nothing" must
  mean **skip + report "0 scoped tests"**, never silently green. Log what was dropped (no silent
  caps).
- **Path-relativity.** Diff paths are worktree-relative and the runner's `WorkingDirectory` is the
  worktree, so paths line up - but selector validation should reject absolute paths and `..`
  escapes.
- **`--no-build` coupling.** Scoped test runs still need the build check to have compiled
  everything first; keep `build` full even when `test` is scoped - a scoped test in one project can
  break from a change in another.

## Where it plugs in

- Envelope field: [WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)
  plus the `WorkerResult` contract.
- Check spec + template parsing: `CheckSpec` and `ReadReviewSection` in
  [Config.cs](../../src/ThroughlineBuild.Cli/Config.cs).
- Substitution + selection: a new resolver feeding
  [AutomatedChecksRunner.RunAsync](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L16);
  the diff it needs is already computed at
  [ReviewPhase.cs:183](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L183).
- Brief ask: add the selector instruction to the implement template that already carries
  `project_test_command` ([ImplementBriefBuilder.cs:81](../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L81)).

## Open questions

1. **Trust posture.** Execute an agent-emitted test command (validated against an executable
   allowlist), or make the deterministic config template the default and treat the agent selector
   as advisory only?
2. **Scope of the win.** Is the goal mainly to speed up the *rework loop* (where the same suite runs
   2-3x per ticket), or also the first review pass? The rework loop is where partial testing pays
   off most.
3. **This repo's reality.** Throughline Build itself is one `dotnet test` over a single solution, so
   file-path selection does not help it - only `--filter` does. Should the first implementation
   target path-oriented runners (pytest/jest/go, where `{changed_test_files}` is trivial) and treat
   filter-based runners as a second step?
