# op-08 handoff

**Status:** Reconstructed retroactively from diff + source. Not captured in real time.
**Boundary:** OP07_END (`1344e66`) .. OP08_END (`0b3a3c8`)
**Reconstructed by:** Claude Code (Claude Opus 4.7), 2026-05-23

## Reconstruction provenance

Read: op-08-build-review-phase.md (spec), `git diff --stat` and full diff for the 18-commit range,
all four Plan B brief tickets' Plane descriptions and `[implemented_at:]` / `[shipped]` markers, all
six new source files shipped (ReviewBriefBuilder.cs, ClaudeCodeReviewer.cs, ReviewPhase.cs, plus the
Plan A additions AutomatedChecksRunner.cs, CheckResult.cs, the extracted ProcessGitClient.cs and
ThroughlineBuild.Git.csproj), three changed project files in src/, all eight new or modified test
files, the updated docs/throughline-build-architecture.md and docs/event-log-format.md, the new
`.build/config.toml.example` `[review]` block, and the rework-cycle marker in the TLB-89 Plane
comments. Also surveyed `.build/events/` (no Review-phase or Implement-phase runs present).

High-confidence sections (grounded in code + ticket comments): 1 (spec-vs-shipped), 2 (surprise
files), 3 (TBDs surfaced by the rework), 4 (contamination), 6 (comparison runs). Medium-confidence:
5 (arch-doc gaps - some op-08 surfaces are documented in TLB-87 but pre-date TLB-89's mid-op
relocation, so the doc may be stale in spots). Reconstruction happened immediately post-ship by the
same agent that drove the chain, so design decisions are first-hand rather than inferred.

---

## 1. Picked vs rejected design decisions

### Plan A briefs (verification foundations)

### B01: extract-process-git-client

**Implemented as specified.** `ProcessGitClient` moved from sibling-of-`PlanPhase` to its own
project `src/ThroughlineBuild.Git/` with namespace `ThroughlineBuild.Git`. The class is byte-stable
(only namespace and location changed). New project references only `ThroughlineBuild.Contracts`.
`PlanPhase.cs` shrank by ~600 lines. `tests/ThroughlineBuild.Git.Tests/` was created as the new home
for git-related tests. Project graph remains acyclic.

### B02: git-diff-fetcher

**Implemented as specified.** `DiffAsync(fromRef, toRef, mainWorktreePath, includePatchContent, ct)`
added to `IGitClient` and implemented on the extracted `ProcessGitClient` using three-dot range
syntax (`<fromRef>...<toRef>`). Per-file patch content capped at ~100 KB; binary files (numstat
`-/-`) map to `0/0`. xUnit tests in `tests/ThroughlineBuild.Git.Tests/ProcessGitClientDiffTests.cs`
cover the add/modify/delete/rename matrix plus the include-vs-omit-patch toggle.

### B03: automated-checks-runner

**Implemented as specified.** New project `src/ThroughlineBuild.Verification/` with no project
dependencies (stdlib only at this point). `CheckSpec` and `CheckResult` records, plus
`AutomatedChecksRunner` with sequential execution, ~4 KB stdout/stderr tail capture, optional
stop-on-first-failure, and timeout / cancellation handling. xUnit tests in
`tests/ThroughlineBuild.Verification.Tests/AutomatedChecksRunnerTests.cs`.

### B04: arch-doc-revisions

**Implemented as specified, ahead-of-Plan-B sequencing.** `docs/throughline-build-architecture.md`
grew by ~108 lines (Section 5.2 IWorkflowPhase, Section 5.6 PhaseWorktreeLayout + Verification
helpers subsection, Section 5.7 per-phase brief-builder note, Section 5.8 reviewer description,
Section 6 IGitClient block + IWorkflowPhase block + PhaseResult/CheckSpec/CheckResult). The brief
documented op-08 surfaces from the spec BEFORE Plan B implementation; see Section 5 below for the
consequence (CheckResult location was correct as documented at TLB-87 ship time but became stale
when TLB-89 reworked).

### Plan B briefs (review phase composition)

### B05: review-brief-builder

**Implemented as specified.** Pure static `ReviewBriefBuilder.Build(ticket, diff, implementerResult,
checkResults)` returning a `Brief` with `Phase.Review`, empty `AllowedWrites` / `RelevantFiles`, and
`Context = { feature_branch, base_ref, files_changed_count }`. Instruction renders ticket header,
description-HTML verbatim, implementer summary, automated check results, the changed-files list,
per-file patch content (truncated at a ~50 KB total budget with per-file breadth-over-depth slicing),
and the WORKER_RESULT envelope with `verdict` / `rationale` / `checks_failed` keys and verdict values
enumerated as `Pass`, `Rework`, `Fail`. WORKER_RESULT envelope copied from `ImplementBriefBuilder`
(bare marker on its own line, not a fenced block).

**Implementation decision:** B05 added `ProjectReference Include="..\ThroughlineBuild.Verification\..."`
to `src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj` so that `CheckResult` was visible. At
ship time this looked correct because `ThroughlineBuild.Verification.csproj` had no project refs.
B06 then needed to reference `ThroughlineBuild.Briefs` from `ThroughlineBuild.Verification`, which
would have closed a cycle. This was the design defect that triggered B06's mid-op rework (see B06).

### B06: claude-code-reviewer

**Reworked once.** First attempt (commit `0075f1e`, later rebased to the same SHA on main) detected
the cycle and shortcut around it by passing the caller-supplied `implementerBrief` straight to
`worker.ExecuteAsync` instead of building a review-shaped brief via `ReviewBriefBuilder.Build`. The
shortcut compiled and the tests it wrote against itself passed, but it violated the op-doc
acceptance "VerifyAsync builds the review brief via ReviewBriefBuilder" and meant the verifier
worker was being instructed with the implementer's prompt rather than a verifier prompt. Reviewer
caught the deviation.

**Rework commit** (`dfcf0b6`) made the architectural fix: relocate `CheckSpec` and `CheckResult`
from `ThroughlineBuild.Verification` to `ThroughlineBuild.Contracts/Verifier/CheckResult.cs` so that
`ThroughlineBuild.Briefs` no longer needs to reference `ThroughlineBuild.Verification`.
`ThroughlineBuild.Verification` then safely references `ThroughlineBuild.Briefs`, and
`ClaudeCodeReviewer.VerifyAsync` calls `ReviewBriefBuilder.Build(ticket, diff, implementerResult,
checkResults)` then dispatches the worker against the resulting Phase.Review brief. The
`implementerBrief` parameter is intentionally unused in v1 (suppressed with `_ = implementerBrief;`)
and preserved on the public surface for the contract.

**Project-graph after rework:** `Contracts` (leaf) -> `Briefs` -> `Verification` -> `Phases` -> `Cli`.
Acyclic; verified by `dotnet build`. `AutomatedChecksRunner.cs` and tests had `using
ThroughlineBuild.Contracts;` added; `ReviewBriefBuilder.cs` had `using ThroughlineBuild.Verification;`
swapped for `using ThroughlineBuild.Contracts;`.

**`FlattenLlmUsage` / `UnwrapJsonElement` / `TryGetString` helpers** were copied verbatim into
`ClaudeCodeReviewer.cs` from `ImplementPhase.cs` per the op-doc OOS that explicitly defers
extraction of a shared helper to a follow-up.

### B07: review-phase

**Implemented as specified, 14 phase-logic steps in order, with one deferral.** `ReviewPhase` hosts
`ReviewOptions` and `ReviewResult` records in the same file. Constructor mirrors `ImplementPhase`'s
shape plus two optional override seams (`IVerifier? verifierOverride = null` and `AutomatedChecksRunner?
checksRunner = null`) so tests can inject fakes without instantiating a real `ClaudeCodeReviewer` or
running real subprocesses. The seams match the existing `IGitClient? gitClient = null` pattern from
`ImplementPhase`. Comment templates (`reviewed: pass`, `reviewed: rework`, `reviewed: fail`) match
the op-doc Step 13 marker convention.

**Step 12 deferred:** the op-doc says "if the verifier worker reported llm_usage in its metadata,
emit LlmCall event". Implementing this requires `ReviewPhase` to see the verifier's underlying
`WorkerResult`, but `IVerifier.VerifyAsync` returns only `Verdict`. The investigator proposed
extending `ClaudeCodeReviewer` to expose `LastWorkerResult`, but that change crosses ticket
boundaries. The chain orchestrator's decision (recorded in the TLB-90 brief): leave the
`FlattenLlmUsage` / `UnwrapJsonElement` helpers copied verbatim into `ReviewPhase.cs` per the op-doc
literal instruction, but do NOT emit `LlmCall` in v1. The conditional "if reported" is never true.
LlmCall activation is a small follow-up - see Section 3.

`docs/event-log-format.md` updated with WorkerSpawn `role` key, VerifierVerdict `kind` /
`checks_failed_count` keys for the Review phase, and Pass/Rework/Fail JSONL example fixtures (~35
lines added).

`tests/ThroughlineBuild.Phases.Tests/ReviewPhaseTests.cs` has 9 facts: Pass, Rework with empty
checks, Rework with non-empty checks, Fail, ticket-not-in-InReview early exit, worktree-missing
early exit, no-`implemented_at`-marker early exit, verifier-worker-fails-internally (mapped to Fail
Verdict by ClaudeCodeReviewer; recorded normally by ReviewPhase), and the IWorkflowPhase.RunAsync
PhaseResult-conversion roundtrip.

### B08: review-cli

**Implemented as specified, with one csproj decision flipped relative to the investigation.**
`review` verb added to `Program.cs` verb-validation (line ~28) and dispatch-tail (line ~153);
dispatch branch constructs `ReviewPhase` and maps `ReviewResult` to exit codes. Exit-code semantics
match the op-doc:

- 0: Pass verdict
- 1: Rework or Fail verdict (also OperationCanceledException, matching plan/implement)
- 2: config error, unknown verb, missing ticket-id (KeyNotFoundException)
- 3: missing secret (inherited convention)
- 4: `ReviewResult.Success == false` (worktree missing, no `implemented_at` marker, git error)

The op-doc called exit code 4 out specifically because plan and implement collapse all phase
failures to exit 1; review distinguishes verdict-driven non-Pass (exit 1) from phase-mechanics
failure (exit 4). `CliUsage.cs` documents this.

**`Config.cs`** gained a `ReviewConfig` record (verifier_timeout_minutes, verifier_allowed_tools,
checks) plus a new `ReadReviewSection` and a new `OptionalStringList` helper. The `[review]` TOML
section is OPTIONAL with a sensible default (15 min, ["Read","Grep","Glob"], empty checks list) so
configs that pre-date review still load. `BuildConfig` gained a fifth positional `Review` member;
no existing test fixture constructed `BuildConfig` directly so no fanout updates were needed.

**csproj note:** the investigation expected `ThroughlineBuild.Cli.csproj` to add a project
reference to `ThroughlineBuild.Verification` so `Config.cs` could materialize `CheckSpec` records.
Post TLB-89 rework, `CheckSpec` lives in `ThroughlineBuild.Contracts` which Cli already references
transitively via Briefs and Phases. The Verification ref was NOT added. `dotnet build` confirmed.

**`.build/config.toml.example`** gained a commented `[review]` section with the example
verifier_timeout_minutes, verifier_allowed_tools, and two `[[review.checks]]` entries (build, test).

---

## 2. Surprises in the existing code surface

Files modified that were NOT in any op-08 brief's Files column, or that diverged from the brief:

1. **`src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs`** (new) - relocation of `CheckSpec`
   and `CheckResult` from `ThroughlineBuild.Verification` to `ThroughlineBuild.Contracts`. Triggered
   by the cycle that B05's `Briefs -> Verification` reference created against B06's intended
   `Verification -> Briefs` reference. Caught by reviewer on the first B06 attempt; fixed in the
   rework commit `dfcf0b6`. The records live alongside `IVerifier.cs` under the `Verifier/` folder
   but use namespace `ThroughlineBuild.Contracts` (matching `IVerifier.cs`'s declared namespace
   rather than its folder name).

2. **`src/ThroughlineBuild.Verification/CheckSpec.cs`** (deleted) - same rework. After relocation,
   the file would have held only `using` statements, so it was removed outright.

3. **`src/ThroughlineBuild.Briefs/ThroughlineBuild.Briefs.csproj`** - the `ProjectReference Include
   ="..\ThroughlineBuild.Verification\..."` added by B05 was REMOVED by the B06 rework. Briefs now
   references only `ThroughlineBuild.Contracts`. If a future op-doc proposes Briefs depending on
   Verification again, the cycle considerations from this rework should be revisited first.

4. **`src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs`** + its tests - `using
   ThroughlineBuild.Contracts;` added so `CheckSpec` / `CheckResult` resolve from their new home.
   Otherwise unchanged. The B03 brief did not anticipate this; it followed mechanically from B06's
   relocation.

5. **`src/ThroughlineBuild.Briefs/ReviewBriefBuilder.cs`** - same: `using ThroughlineBuild.Verification;`
   was replaced with `using ThroughlineBuild.Contracts;` during the B06 rework.

6. **`src/ThroughlineBuild.Phases/ReviewPhase.cs`** - added two optional override seams
   (`verifierOverride`, `checksRunnerOverride`) beyond what the op-doc specified, to make tests
   hermetic without instantiating a real `ClaudeCodeReviewer` or running subprocesses. Mirrors the
   `IGitClient? gitClient = null` pattern from `ImplementPhase`.

7. **`src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs`** - the `implementerBrief` parameter
   on `VerifyAsync` is intentionally unused in v1 (suppressed with `_ = implementerBrief;`); kept
   on the public surface for interface contract. Not a deviation per se, but the discard pattern is
   worth flagging for op-09 review-aware code.

8. **`throughline-build.sln`** - 60 new lines for the new project entries (`ThroughlineBuild.Git`,
   `ThroughlineBuild.Verification`, and the two new test projects). Not called out in any brief
   Files column.

9. **Five test files outside the op-08 brief scope** had stub `DiffAsync` additions to their local
   `FakeGitClient` implementations (a B02 ripple): `tests/ThroughlineBuild.Phases.Tests/ImplementPhaseTests.cs`,
   `PlanPhaseTests.cs`, `PlanPhaseInterfaceTests.cs`, `PlanPhaseUsageTests.cs`, and
   `tests/ThroughlineBuild.Commands.Tests/CloseCommandTests.cs` + `TestFakes.cs` +
   `tests/ThroughlineBuild.Phases.Tests/WorktreeDecrufterTests.cs`. Each is a three-line addition
   matching the new interface method.

10. **`src/ThroughlineBuild.Cli/Program.cs`** dispatch chain - the existing `if (verb == "plan") {
    ... } else { /* implement */ ... }` shape was extended to `else if (verb == "review")` rather
    than refactored into a switch. Minor; matches the existing if/else style.

---

## 3. New TBDs surfaced

No inline `TODO`/`FIXME`/`HACK` markers were added. The following follow-ups are recorded in
op-doc Notes, in the TLB-89 / TLB-90 `[implemented]` comments, or in this handoff:

- **`ClaudeCodeReviewer.LastWorkerResult` exposure** - the smallest possible change that activates
  `ReviewPhase` Step 12 LlmCall emission. Add `public WorkerResult? LastWorkerResult { get; private
  set; }`, assign it before returning the `Verdict`, and have `ReviewPhase` Step 12 read
  `(verifier as ClaudeCodeReviewer)?.LastWorkerResult?.Metadata.TryGetValue("llm_usage", ...)` and
  emit `LlmCall` via the already-copied `FlattenLlmUsage`. ~10 lines across two files. Bundle into
  op-09 or land standalone before cost-comparison run; without it, review-phase token usage is
  invisible in the event log.

- **Short-circuit on check failure** (op-doc explicit follow-up, ReviewPhase Step 7-8): if
  dogfooding shows the verifier rubber-stamps Rework whenever automated checks fail (no added
  insight beyond the failing checks), introduce a policy that synthesizes a Rework verdict from
  check failures alone and skips the model verifier. Policy gate lives in `ReviewPhase`.

- **Implementer artifacts persistence** (op-doc explicit follow-up, ReviewPhase Step 5-6): the
  reconstructed implementer `Summary` is synthetic ("Reconstructed from implemented_at: ..."). If
  dogfooding shows verdicts that would have benefited from the implementer's actual self-reported
  summary, add `.build/artifacts/<ticket>/implement.json` written by `ImplementPhase` and read by
  `ReviewPhase`.

- **`FlattenLlmUsage` extraction** (op-doc explicit follow-up): three copies of `FlattenLlmUsage`
  and `UnwrapJsonElement` now exist (`PlanPhase`, `ImplementPhase`, `ReviewPhase`). Extract to
  `ThroughlineBuild.Helpers/LlmUsageFlattener.cs`. Small mechanical refactor; the call sites are
  stable. Best done as a standalone brief because it touches three phases.

- **Branch-already-exists recovery** (carried over from op-07 handoff TBDs) - still not addressed.
  Review-phase doesn't create branches; ship-phase will. Resolution may best land with op-09.

- **Drift gate vs warning** (carried from op-07) - `GateFailure` kind `drift_warning` exists in
  ImplementPhase only. ReviewPhase intentionally does NOT perform a drift check (op-doc Notes:
  "ship-phase's rebase will catch the same condition with sharper semantics"). Op-09 ship-phase
  inherits the resolution.

- **Cross-vendor IVerifier wiring** (op-doc explicit OOS) - `IVerifier` is intentionally a separate
  interface from `IWorkerAgent` to make Codex/Gemini variants drop-in. v1 ships `ClaudeCodeReviewer`
  only; no `--verifier-vendor` flag. Deferred to v1.1.

---

## 4. Contamination patterns observed

None found. op-08 source files contain no references to claude-config, no slash-command prose
(`/ti`, `/tr`, `/ta`), no base64 round-trips, no mirror generators, no vendor-parity guards. The
`ticket/<slug>` branch convention and `.worktrees/ticket-<slug>` path convention continue from
op-07 deliberately so review-phase and the existing slash-command path stay cross-comparable during
dogfooding. The WORKER_RESULT bare-marker envelope is the same shape as ImplementBriefBuilder uses;
no second envelope format was introduced.

`docs/throughline-build-architecture.md` Section 7 (Bootstrap), Section 8 (Migration), Section 10
(Risk Register), and the Appendix were not touched in op-08 per the B04 OOS - they remain owned by
the migration plan, not the per-op handoffs.

---

## 5. Architecture-doc revisions needed

`docs/throughline-build-architecture.md` was updated by B04 (TLB-87) to close the op-07 gaps and
pre-document op-08 surfaces. Three drift items surfaced AFTER B04 shipped, all caused by the B06
rework:

- **`CheckSpec` / `CheckResult` location** - B04 documented these records as living in
  `ThroughlineBuild.Verification`. After the B06 rework they live in `ThroughlineBuild.Contracts`
  (namespace `ThroughlineBuild.Contracts`, alongside `IVerifier.cs`). The arch doc's Verification
  helpers subsection and Section 6 entries for these records should be updated to reflect the
  Contracts home and the cycle-breaking rationale. Status: **needed**.

- **Project graph diagram (if any)** - the prose in B04 describes Verification with no project
  refs. Reality is now Verification -> Contracts + Briefs. If a graph diagram exists or the prose
  enumerates project dependencies, it needs the two new edges. Status: **needed**.

- **`ReviewBriefBuilder` namespace import note** - if the arch doc names imports anywhere, the
  ReviewBriefBuilder example should show `using ThroughlineBuild.Contracts;` for CheckResult, not
  `using ThroughlineBuild.Verification;`. Status: **needed (minor)**.

Nothing here is an architecture revision (a design change). All three are documentation drift caused
by the in-op rework. Op-09 can either patch them inline as part of its B04-equivalent or surface
them as a standalone arch-doc brief.

Two op-09-anticipated surfaces that the arch doc does NOT yet describe:

- **ShipPhase** - the third concrete IWorkflowPhase, expected to live in
  `src/ThroughlineBuild.Phases/ShipPhase.cs`. No current entry. Op-09's B04 (or equivalent) should
  add it alongside PlanPhase / ImplementPhase / ReviewPhase.

- **IGitClient growth (fetch/rebase/merge/push/branch-delete)** - IGitClient currently has 7
  methods after op-08. Op-09 will add ~5 more for ship semantics. Section 6's IGitClient block
  needs a refresh after op-09 lands.

---

## 6. Comparison-run results

`.build/events/` contains exactly two JSONL files, both predating op-07:

- `3d5d885eea534ef38f9835a751c35bbe.jsonl` - `build plan 53`, Phase 0, 2026-05-22.
- `7b6e0de1191646b8ab09307c4e20c1c2.jsonl` - `build plan 56`, Phase 0, 2026-05-22 (partial; only
  WorkerSpawn).

**No `build implement` runs exist (Phase 1).** **No `build review` runs exist (Phase 2).** The
review phase shipped (op-08) but has not been executed against a real ticket. The cost-comparison
run against `/ticket-act + /ticket-review` described in the op-07 "What done looks like" section is
still pending - and now grew to include the review phase as well. Op-09 design without real
review-run evidence is workable but means review-phase happy-path and rework-path semantics have
only been validated through xUnit fakes, not against a live model.

**Recommendation:** before op-09 ship, run `build implement <id>` then `build review <id>` against
a real Backlog ticket (one of the deferred Plane stubs would do) and capture both event-log JSONL
files. Compare token usage against the equivalent `/ta` + `/tr` slash-command pair on the same
ticket. This was promised since op-07 and is now overdue.

---

## 7. Open questions for the op-09 writer

1. **ShipPhase rebase failure semantics** - `/ticket-ship`'s rebase is interactive; conflicts halt
   for human resolution. Should `ShipPhase.RunAsync` auto-abort on conflict and return a clean
   failure with the conflict-file list, or always require an outer human step? The latter matches
   `IWorkflowPhase`'s "phase produces a deterministic verdict from inputs" contract better; the
   former is what /ticket-chain --ship would need to keep flowing. Op-09 picks; document the choice.

2. **Drift at ship time** - per ReviewPhase op-doc Notes, ship's rebase IS the drift gate (a real
   conflict is a hard failure; a drift warning was informational at review time). Confirm op-09
   does not add a separate `drift_warning` for ship. Should `GateFailure` with a new kind like
   `rebase_conflict` be emitted, or is the rebase-failure path enough on its own?

3. **Push semantics** - `/ticket-chain --ship` currently merges to LOCAL main and does not push to
   origin. Does `build ship` follow the same convention, or does it `git push origin main` after
   merge? If push, what's the failure mode when origin/main has moved (force-with-lease vs
   refuse-and-defer-to-human)? **Never force-push to main** is a stated CLAUDE.md rule.

4. **Worktree decruft timing** - `/ticket-chain --ship` calls `git worktree remove --force` after
   transitioning to Done. Should `ShipPhase` own the decruft step, or is it a separate concern
   that lives outside the phase (so a failed ship leaves the worktree available for inspection)?
   `WorktreeDecrufter` already exists from op-06.

5. **`ClaudeCodeReviewer.LastWorkerResult`** - is the op-09 op-doc the right place to land the
   ~10-line surface extension that activates `ReviewPhase` Step 12 LlmCall emission, or does it
   belong in a separate follow-up brief? Affects cost-comparison fidelity.

6. **CheckSpec/CheckResult arch-doc drift** - see Section 5. Bundle the doc fix into one of op-09's
   briefs, or call it out as a standalone brief like B04 did in op-08?

7. **Cost-comparison run** - still pending from op-07 "What done looks like". When does it happen,
   and against which slash-command baseline (`/ta` only, `/ta + /tr`, or `/ta + /tr + /tsh` once
   ShipPhase lands)? See Section 6.

8. **`FlattenLlmUsage` extraction** - three copies now exist (Plan, Implement, Review). Op-09 will
   land ShipPhase, which probably also wants this helper. Extract before adding the fourth copy,
   or accept four and extract on a five-copy threshold?

9. **Rebase-already-applied detection** - during this op's shipping, two ticket branches rebased
   onto post-predecessor main saw `warning: skipped previously applied commit` for upstream
   commits already on main. This is git's normal behavior for stacked branches but means
   `git rebase main` is not idempotent for verification purposes. ShipPhase's "post-rebase test
   pass" gate needs to handle the skipped-cherry-picks case gracefully (it already does in /tch
   because tests run on the result, not on the rebase output).

10. **No `IVerifier.VerifyAsync` consumer beyond `ClaudeCodeReviewer`** - the interface seam is
    real but currently single-impl. Op-09 will not change this. Worth confirming the op-doc keeps
    cross-vendor verification as a v1.1 follow-up rather than letting it creep into ship scope.
