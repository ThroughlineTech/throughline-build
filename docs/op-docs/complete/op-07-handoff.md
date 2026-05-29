# op-07 handoff

**Status:** Reconstructed retroactively from diff + source. Not captured in real time.
**Boundary:** OP06_END (`507585b`) .. OP07_END (`1344e66`)
**Reconstructed by:** GitHub Copilot (Claude Sonnet 4.6), 2026-05-23

## Reconstruction provenance

Read: op-07-implement-phase.md (spec), `git diff --stat` and full diff for the 15-commit range,
all 6 new source files shipped (IWorkflowPhase.cs, PhaseWorktreeLayout.cs, ImplementBriefBuilder.cs,
ImplementPhase.cs, CliUsage.cs, ProcessGitClientWorktreeTests.cs), both changed project files, and
the 8 test files modified in the range. Also read the 2 event-log JSONL files in `.build/events/`.

High-confidence sections (grounded in code): 1 (spec-vs-shipped), 2 (surprise files), 4 (contamination),
6 (comparison runs). Medium-confidence: 3 (TBDs - sourced from spec Notes text, no inline code markers
added). Low-confidence: 5 (arch doc gaps - the arch doc was not updated in op-07; the assessment of what
is now stale is inferred from reading both documents side by side).

---

## 1. Picked vs rejected design decisions

### B01: workflow-phase-interface

**Implemented as specified.** `IWorkflowPhase` with `Phase` property and `Task<PhaseResult> RunAsync`
added to `ThroughlineBuild.Contracts`. `PlanPhase` implements it via explicit interface implementation,
keeping the typed `PlanResult`-returning `RunAsync` for CLI callers. `PhaseResult.Outputs` is
`IReadOnlyDictionary<string, string>`. Tests pass type-check and roundtrip assertions.

### B02: git-worktree-create

**Implemented as specified.** `CreateWorktreeAsync` and `HeadShaAsync` added to `IGitClient` and
implemented in `ProcessGitClient` (still in PlanPhase.cs per the "leave that file location alone"
direction). Both return structured results rather than throw. One spec ambiguity resolved: the code
comment on `HeadShaAsync` says "callers check string.Length == 40 to detect failure" but ImplementPhase
actually checks `string.IsNullOrEmpty` - a harmless variant that is slightly more defensive.

### B03: phase-worktree-layout

**Implemented as specified.** `PhaseWorktreeLayout.Compute` returns `PhaseWorktreeNames(Slug, BranchName,
WorktreePath)` using `SlugBuilder.BuildBranchSlug`. Convention `ticket/<slug>` and
`.worktrees/ticket-<slug>` preserved as spec noted. One project-graph fix required (see Section 2).

### B04: implement-brief-builder

**Diverged from spec.** Spec said `Build(Ticket, RepoState, PhaseWorktreeNames)` - takes the record.
Shipped code has `Build(Ticket, RepoState, string branchName, string worktreePath)` - deconstructs the
record at the call site in ImplementPhase. The ImplementPhase caller passes
`worktreeNames.BranchName, worktreeNames.WorktreePath`. The Brief content and Context dictionary match
spec exactly; only the parameter type differs. Rationale not recoverable from artifacts; flag for op-08
writer to query Dan if it matters (the interface is internal to Phases + Briefs so op-08 is unlikely to
call it directly).

### B05: implement-phase

**Implemented as specified, all 19 steps in order.** Explicit implementations of
`IWorkflowPhase.RunAsync` converts `ImplementResult` to `PhaseResult` with `commit_sha`, `branch`,
`worktree_path` in Outputs.

**Spec ambiguity resolved at implementation time:** Step 5 uses `GateFailure` event with
`Data.kind = "drift_warning"` (the spec said "pick whichever feels right"). The choice was
`GateFailure` rather than a new event kind. `docs/event-log-format.md` was updated in the same brief
as spec required.

**Implementation decision:** Step 3 wraps `RevParseAsync` in a try/catch returning a clean failure
rather than allowing the exception to propagate. Spec did not specify this but the pattern is consistent
with how other steps handle git errors.

### B06: implement-cli

**Minor divergence from spec.** Usage text was extracted to a new `CliUsage.cs` file with a static
`UsageText` property. Spec said `Program.cs` only in the Files column; this is an additive split, not
a contradiction. Arg validation for `plan` and `implement` is now done before config load (exit 2),
matching spec exit-code semantics. Terminal output on success: `Implement complete: {id} commit={sha}
branch={branch}`.

---

## 2. Surprises in the existing code surface

Files modified that were NOT in any op-07 brief's Files column:

1. **`src/ThroughlineBuild.Cli/CliUsage.cs`** (new) - Usage text extracted from the inline constant in
   Program.cs. Related to B06. Now includes `implement` alongside `plan` in the help banner.

2. **`src/ThroughlineBuild.Helpers/ThroughlineBuild.Helpers.csproj`** - Project reference changed from
   `ThroughlineBuild.Phases` to `ThroughlineBuild.Contracts`. Required to avoid a circular dependency:
   B03 placed `PhaseWorktreeLayout` in Helpers; Phases added a reference to Helpers (for ImplementPhase
   to use it); Helpers referencing Phases would close a cycle. Related to B03.

3. **`src/ThroughlineBuild.Phases/ThroughlineBuild.Phases.csproj`** - Added reference to
   `ThroughlineBuild.Helpers` so ImplementPhase and PhaseWorktreeLayout can coexist. Related to B05.

4. **`tests/ThroughlineBuild.Commands.Tests/TestFakes.cs`** - Added `CommandConsoleTestsCollection`
   xUnit collection definition + stub implementations of `CreateWorktreeAsync` / `HeadShaAsync` on
   `FakeGitClient`. The collection fix addresses a pre-existing `Console.SetError` race condition
   between CloseCommandTests and DeferCommandTests. Triggered by op-07 expanding `IGitClient`
   (all fakes must implement new methods). Related to B02.

5. **`tests/ThroughlineBuild.Commands.Tests/CloseCommandTests.cs`** - Added
   `[Collection("CommandConsoleTests")]` annotation and stub `IGitClient` methods on the local
   `FakeGitClient`. Same race-condition fix. Related to B02.

6. **`tests/ThroughlineBuild.Commands.Tests/DeferCommandTests.cs`** - Added
   `[Collection("CommandConsoleTests")]`. Same race-condition fix, no new IGitClient stubs needed here.

7. **`tests/ThroughlineBuild.Phases.Tests/PlanPhaseTests.cs`** - Stub implementations of
   `CreateWorktreeAsync` / `HeadShaAsync` on the local `FakeGitClient`. Interface expansion forced
   update. Related to B02.

8. **`tests/ThroughlineBuild.Phases.Tests/PlanPhaseUsageTests.cs`** - Same stub additions. Related to B02.

The `Console.SetError` race fix (items 4-6) was bundled as commit `2a8810b` with message
`fix: serialize CloseCommandTests and DeferCommandTests to prevent Console.SetError race` - authored
before the op-07 brief tickets landed. It predates B02 in the log but is included in the op-07 range
because it landed after OP06_END.

---

## 3. New TBDs surfaced

No inline `TODO`/`FIXME`/`HACK` markers were added in op-07 source. The following follow-ups are
documented only in the spec Notes sections; they have no code-level anchors:

- **Branch-already-exists recovery** (`src/ThroughlineBuild.Phases/PlanPhase.cs` `CreateWorktreeAsync`):
  Spec Brief 02 Notes say rerun on same ticket after worker failure will fail because the branch was
  left behind. Current behavior: `CreateWorktreeAsync` returns `WorktreeCreateResult(false, ...)` and
  ImplementPhase returns a clean failure. Recovery semantics are flagged as a follow-up.

- **Auto-decruft on worker failure** (B05 Notes): Worktree is deliberately preserved on failure.
  Spec says "auto-decruft on failure is a follow-up to consider once the dogfooding period surfaces
  actual failure shapes." No ticket exists yet.

- **Drift as gate vs warning** (B05 Notes): Currently a warning only. Spec deferred the judgment-slot
  version ("model says this drift is significant vs cosmetic") explicitly to a future op-doc.
  `GateFailure` with `kind = "drift_warning"` is the v1 surface; the gate behavior is unimplemented.

---

## 4. Contamination patterns observed

None found. The op-07 source files contain no references to claude-config, slash commands (`/ti`, `/ta`),
or prior-system prose templates. The `ticket/<slug>` branch convention and `.worktrees/ticket-<slug>`
path convention were explicitly preserved per the spec's Notes on B03 ("keeps the new system's branches
recognizable to humans cross-referencing against the old system during dogfooding") - this is intentional
compatibility, not unintentional contamination. No base64 round-trips, no mirror generators, no
vendor-parity guards were introduced.

---

## 5. Architecture-doc revisions needed

`docs/throughline-build-architecture.md` was not modified in op-07. Two gaps are now present:

**Section 5.7 (Brief Constructor)** - stale. Describes a single generic `(Ticket, RepoState, Phase) -> Brief`
signature. Shipped reality: two separate static classes (`PlanBriefBuilder`, `ImplementBriefBuilder`) with
different signatures; B04 deconstructed `PhaseWorktreeNames` into positional strings. This is a **documentation
gap** (the doc never described per-phase builders; the arch was always per-phase, the doc just generalized it).
Status: **needed**.

**Section 5.2 (State Machine) or a new section** - gap. `IWorkflowPhase` now exists as the shared contract
for `PlanPhase` and `ImplementPhase`, but the arch doc has no mention of it. The doc mentions phases
abstractly ("the state machine implements this shape") but never names the interface. This is an **architecture
doc gap** - the design decision to have a common interface was implicit in op-07 spec but never written
into the arch doc. Status: **needed**.

**Section 5.6 (Helpers)** - gap. `PhaseWorktreeLayout` is now a helper but is not listed alongside
slug, drift, and marker-comment parser. Minor. Status: **needed** (single line addition).

**IGitClient surface** (`CreateWorktreeAsync`, `HeadShaAsync`) - not described anywhere in the arch doc.
The arch doc's references to IGitClient are only in the context of worktree listing and removal. Status:
**needed** (one bullet per method in Section 5 or wherever git-client surface is described).

Nothing in the arch doc is an **architecture revision** (a design change). All gaps are documentation
gaps - the design shipped as intended.

---

## 6. Comparison-run results

Two event-log JSONL files exist in `.build/events/`. Both are Phase 0 (plan phase) runs:

- Session `3d5d885e`: `build plan 53` - ran 2026-05-23T05:19. Worker returned Ok.
  LlmCall: `input_tokens=10, output_tokens=13014, cache_read_tokens=241797,
  cache_create_tokens=36783, wall_clock_ms=368412`.
- Session `7b6e0de1`: `build plan 56` - partial log (WorkerSpawn event only; no verdict or LlmCall).

No `build implement` runs exist. Phase field `1` (Implement) does not appear in any event log.

The op-07 implement phase cannot be comparison-run until a Ready ticket is available and the implement
binary is invoked. The token-cost comparison against `/ticket-act` described in the "What done looks like"
section of the spec has not been performed.

---

## 7. Open questions for the op-08 writer

1. **B04 signature divergence** - `ImplementBriefBuilder.Build` takes `(string branchName, string worktreePath)`
   instead of `PhaseWorktreeNames`. If op-08's `ReviewBriefBuilder` follows the same pattern, a convention
   has been established. If it should take `PhaseWorktreeNames`, the B04 signature should be aligned first.
   Verify: `src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs` line 8.

2. **HeadShaAsync failure contract** - The code comment says "callers check string.Length == 40"; ImplementPhase
   checks `string.IsNullOrEmpty`. The contract is slightly ambiguous. Op-08's ReviewPhase will call
   `HeadShaAsync` to verify the commit is accessible before running review. Which check does it use?
   Verify: `src/ThroughlineBuild.Phases/PlanPhase.cs` `HeadShaAsync` comment vs ImplementPhase step 16.

3. **ProcessGitClient location** - `ProcessGitClient` remains inside `PlanPhase.cs` (spec Brief 02 said
   "leave that file location alone"). With B02 adding two more methods and B05 adding ImplementPhase,
   the class is growing. Op-08 review-phase will likely need `HeadShaAsync` and possibly new git methods.
   Does `ProcessGitClient` move to its own file before or during op-08?

4. **CliUsage.cs ownership** - `CliUsage.cs` was created as a split from Program.cs during B06 but is
   not mentioned in any brief's Files column. When op-08 adds `build review`, the usage text needs
   updating. Is the convention now "add to CliUsage.cs"? Verify: `src/ThroughlineBuild.Cli/CliUsage.cs`.

5. **Console.SetError serialization scope** - The `CommandConsoleTests` collection covers CloseCommandTests
   and DeferCommandTests. If op-08 adds commands that also call `Console.SetError`, they need to join
   this collection or the race recurs. The fix is in `TestFakes.cs`. Is this pattern documented anywhere?

6. **Drift gate for op-08** - Op-07 spec explicitly deferred the drift-as-gate behavior. Does op-08
   (review phase) need to surface drift again? Review runs after the worker has already committed; drift
   at that point has different semantics. The open question is whether the op-08 spec needs to define
   a policy, or whether the v1 "log and proceed" from ImplementPhase is the policy for all phases until
   judgment slots land.

7. **No comparison run yet** - The op-07 "What done looks like" section describes a cost-comparison run
   against `/ticket-act` as the validation signal. This run has not happened. Should it happen before
   op-08 is scaffolded, or is it being deferred to after op-08 lands?

8. **IWorkflowPhase in arch doc** - Section 5.2 and 5.7 are now stale (see Section 5). Should op-08
   spec include updating the arch doc as an explicit brief, or is the arch doc update bundled into one
   of the op-08 briefs incidentally?
