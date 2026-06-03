# Operation: deterministic-chain-gate (v2)

> This is a v2 revision of `op-30-deterministic-chain-gate.md`. The architecture and
> dispatch order are unchanged; the edits are integration-reality and framing fixes
> found in review against HEAD. See "Changes from v1" below for the full list and the
> reasoning. Every changed location is tagged `[v2]` inline.

## Changes from v1 (why)

1. [v2] **The capability map and check-runner already exist - reconcile, do not
   rebuild.** `.build/config.toml` already declares `[[review.checks]]` (name +
   executable), parsed in `Cli/Config.cs` (~575-595) into `IReadOnlyList<CheckSpec>`,
   run by `AutomatedChecksRunner` (`ThroughlineBuild.Verification`) inside
   `ReviewPhase` (Step 7). A second instance is `[[ship.regression_checks]]`. Building
   a new capability map (Brief 02) and a new `VerifierEngine` (Brief 03) alongside
   this would run two builds per ticket - violating the one-build wall-discipline that
   is the spine of this operation. Brief 01 now mandates a reuse-vs-rebuild decision;
   Brief 02 shrinks; Brief 03 reuses/extends `AutomatedChecksRunner`; Brief 06
   RELOCATES the existing review-checks run to the gate and has review consume the
   results rather than re-run them.

2. [v2] **Claim emission needs the worker template + envelope parser, not just
   `ImplementResult`.** The implementer is an LLM worker whose output is parsed from
   the WORKER_RESULT envelope (`Workers.Common/WorkerResultParser.cs`,
   `FencedBlockResolver`). Returning a `CompletionClaim` requires changing the
   implement template (`TemplateLoader.Load(agentName, "implement"...)`) to instruct
   emission AND extending the parser to resolve it. Brief 05's surface is widened
   accordingly.

3. [v2] **Gate hard-fail must own a state transition.** `ImplementPhase` ends by
   moving the ticket InProgress -> InReview (Step 18, ~line 377); the rework path
   requires InProgress (Step 2 guard). A gate inserted after implement runs on an
   InReview ticket, so on hard-fail it must flip InReview -> InProgress to enter
   rework (mirroring `ReviewPhase` ~line 279). Brief 01 forces this decision; Brief 06
   wires it.

4. [v2] **Schema-invalid claim does a cheap re-ask before a full rework.** Hard-failing
   a malformed claim into a full implement rework contradicts this operation's own
   symmetric-cost rule (a false-fail must not burn an expensive implement-axis loop).
   LLM JSON-shape flakiness over correct code is a likely false-fail. Brief 05 now
   does a targeted "re-emit just the claim" before any hard-fail.

5. [v2] **Ledger term redefined: "implement-token delta" -> "gate-attributable rework
   tokens."** A delta needs a counterfactual baseline that does not exist in a single
   run. The measurable quantity is the tokens spent on rework rounds a gate hard-fail
   triggered (distinguishable per Brief 08). Brief 09 measures that instead, and
   reuses the chain's existing rework-round count (`ChainPhase.cs` ~line 309).

6. [v2] **Honest scope of what the T1 floor catches.** The implementer already builds
   and tests in its worktree; the gate re-running the same checks catches skipped
   checks (typecheck/lint/format the implementer did not run) and a dishonest "claimed
   done" worker - NOT hidden behavioral defects, which are the deferred
   compiles-but-wrong seam. "Why this exists" now says so, so the first measured
   `cascade-caught` figure is not read as a disappointment against the prose.

7. [v2] **"Integrated tree" == warm per-ticket worktree only under sequential
   stacking.** The equivalence holds because chains stack sequentially (each child
   branches off the prior). Noted as an explicit dependency so a future parallel
   dispatch does not silently break the gate's meaning.

8. [v2] **`format` is a smoke signal / auto-fix, not a hard-gate.** A format violation
   is cosmetic, auto-fixable, near-zero cascade risk; hard-failing it dispatches a
   cold rework agent over whitespace - the asymmetric-cost waste this plan forbids.
   Hard-gating is reserved for build/test/typecheck.

9. [v2] **Smoke signals feed the LLM review, not only the ledger.** Otherwise they are
   write-only telemetry. The gate surfaces (diff facts, grep, consumes/provides
   preflight); the review consumes them as a cheap deterministic prior for its
   semantic judgment.

---

Add a deterministic, language-agnostic quality gate that runs between the implement and review phases of a chain, so a defect in one coupled ticket is caught mechanically before later tickets build on it. The gate is a claim/check split: the implementer returns a structured `CompletionClaim`, and the orchestrator independently falsifies it against ground truth (exit codes, git diff, source grep) - it never trusts the claim. This operation builds only the cheap T1 floor (a single integrated build run once on the warm worktree, plus labeled smoke signals), wires its structured failures into the rework brief, and instruments a cost ledger so the data, not the argument, decides whether a second build (red/green, per-AC isolation) ever earns its place. The schema and phase hooks are left forward-compatible so the deferred tiers are additive, not a rewrite. **[v2]** The floor REUSES the check config and runner that already ship in `review.checks` / `AutomatedChecksRunner` rather than building a parallel one.

## Why this exists

Today the only quality gate in a chain is the per-ticket LLM review, which is a prose judgment that runs after the fact. For a chain of tightly-coupled tickets that is a real exposure: a defect in ticket N - a stub that typechecks, a broken inter-ticket contract, an unsatisfied acceptance criterion - is not caught until review, by which point tickets N+1..N+k have already built on it, and the fix is an implement-cascade across the whole chain. The cascade lands on the implement phase, which a measured chain showed is ~91% of token cost and the dominant slice of serial wall time. So a cheap mechanical check that fails fast at the seam can prevent an expensive cascade on the most expensive axis.

**[v2] What this floor actually catches (and what it does not).** The implementer
already runs build and tests in its worktree to finish its ticket, so the gate
re-running the same checks does not catch a hidden behavioral defect - that is the
compiles-but-wrong seam, explicitly deferred. The floor's real marginal value is
twofold: (a) running checks the implementer did NOT run - typecheck, lint - so an
unrun-check break cannot propagate down the stack; and (b) providing an independent
oracle that catches a dishonest "claimed done" worker that did not actually pass. The
`cascade-caught` ledger term will therefore measure skipped-check and dishonesty
cascades, not hidden-defect cascades; expectations are set there deliberately.

The design principle is falsify, don't trust: a verifier is worth exactly the independence of its oracle from the implementer. A deterministic gate's value is not that it is deterministic - it is that it did not write the code under test. Determinism is just the cheapest way to buy an independent, repeatable oracle. The capability-map keeps it language-agnostic (the orchestrator only knows exit codes, git-diff facts, and source grep; the project declares what "build" or "test" means), so the same gate serves C#, TypeScript, Python, Rust, and Go without per-language tooling baked in. **[v2] That capability map already exists as the `review.checks` config + `CheckSpec` model; this operation reuses it rather than inventing a parallel one.**

The cost of getting this wrong is symmetric and both sides land on the expensive implement axis: a missed defect causes a cascade, but a false-fail dispatches a cold-boot rework agent to "fix" correct code, which can damage it. That is why the low-precision checks (diff facts, grep, the consumes/provides preflight) ship as labeled smoke signals that do not fail the gate, and only the high-precision configured exit-code checks (build, test, typecheck) hard-gate. **[v2] `format` is treated as a smoke signal or auto-fix, not a hard-gate - a cosmetic, auto-fixable violation must not burn a rework loop. And by the same symmetric-cost rule, a schema-invalid claim gets a cheap targeted re-ask before any hard-fail (Brief 05), since LLM JSON-shape flakiness over correct code is itself a likely false-fail.** It is also why the operation refuses to price the gate at zero: it instruments a ledger (cascade-caught, false-fails, gate wall, gate-attributable rework tokens) per ticket so the unsignable terms get measured on a real chain in both attended and unattended mode before any second build is added.

**[v2] Dependency on sequential stacking.** This floor runs the configured checks once
on the warm per-ticket worktree and treats that as the "integrated" tree. That
equivalence holds only because chains stack sequentially - each child branches off the
prior, so ticket N's worktree is base + all prior chain commits. It is true today
(serial dispatch, enforced parent-chain stacking). If chains ever fan out from a fixed
base in parallel, the per-ticket worktree is no longer integrated and the gate's
between-ticket guarantee weakens; revisit then.

After this lands: every chain ticket runs a deterministic gate once between implement and review, on the worktree the implementer left warm (target: one build invocation per ticket, achieved by relocating the existing review-checks run, not adding a second). The gate hard-fails only on configured exit-code checks, surfaces everything else as labeled smoke signals **[v2] that are also handed to the LLM review as a prior**, validates the claim's shape, and emits a structured outcome. Gate failures flow into the rework brief as file/check pointers - a cheaper, sharper rework signal than prose. A ledger event per ticket records the cost terms. The LLM review still runs and still owns the semantic judgments the gate cannot make.

## Deliberately not in this operation

The compiles-but-wrong-behind-a-valid-surface seam - a stub that typechecks, returns a plausible constant, and passes a test written against itself - is NOT closed by this floor. It is an oracle-independence failure (the answer key was authored by the thing under test) and the same hole as a buggy golden file. Closing it requires either behavioral red/green where the surface pre-existed, or LLM review with execution, both deferred. The floor narrows the gap and makes the eventual review sharper; it does not claim to close the seam. Also deferred: red/green-at-two-commits, per-AC isolated test runs, the plan-time intent-level bindings plus independent resolver role, and full T2/T3 tier enforcement. The schema and the run-mode tier selector ship as unenforced hooks only.

## Dispatch order

| Plan | Name | Depends on | Effort |
| --- | --- | --- | --- |
| A | Gate foundation: claim, reconciliation inventory, reused runner | - | M **[v2]** |
| B | Wire the gate into the chain | A | M |
| C | Pay for the gate: rework signal and ledger | B | M |

**[v2] Plan A effort drops from L to M:** Brief 02 collapses to documenting/generalizing
existing config and Brief 03 reuses `AutomatedChecksRunner` instead of building a new
engine, once Brief 01's inventory confirms the reuse decision.

## Plan A: Gate foundation - claim, reconciliation inventory, reused runner

### Goal

Build the deterministic substrate every later plan consumes, with no chain wiring yet. **[v2]** Brief 01 lands the `CompletionClaim` contract (carrying the forward-compatible hooks for the deferred tiers) and a RECONCILIATION inventory that names the real files where the gate slots AND the existing check machinery, config, worker-output, state-transition, and event-log surfaces it must reuse - that map is load-bearing for Plans B and C and stops them from building a second check-runner. Brief 02 **[v2]** documents and, if needed, generalizes the abstract check names in the capability map that already exists (`review.checks`). Brief 03 **[v2]** reuses/extends `AutomatedChecksRunner` to run a configured check and read its exit code. Brief 04 builds the smoke-signal collectors (git-diff facts and source grep) which are pure over a worktree and depend on nothing.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 01 | completion-claim-and-inventory | Define the CompletionClaim contract with deferred-tier hooks; inventory the gate integration points AND the reuse-vs-rebuild decisions | - | new: ThroughlineBuild.Contracts/Models/CompletionClaim.cs; docs: notes/gate-integration-inventory.md |
| 02 | capability-map-config | **[v2]** Document the existing review.checks capability map; generalize abstract check names (build/test/typecheck/lint/format) only if the inventory shows a gap | - | modified (if needed): .build/config.toml + Cli/Config.cs |
| 03 | verifier-runner-reuse | **[v2]** Reuse/extend AutomatedChecksRunner to run a configured check and report a deterministic result | 02 | modified: ThroughlineBuild.Verification/AutomatedChecksRunner.cs + tests |
| 04 | smoke-signals | Collect git-diff facts and grep-present/absent as labeled non-gating signals | - | new: ThroughlineBuild.Gate/SmokeSignals.cs + tests |

### Briefs - detail

#### Brief 01: completion-claim-and-inventory  **[v2 - rewritten]**

Goal: Define the structured artifact the implementer returns and the orchestrator falsifies (with deferred-tier hook fields present but unenforced), AND produce a reconciliation inventory that names the real files/symbols the gate integrates with - including the check-running machinery, config, worker-output, state-transition, and event-log surfaces that ALREADY EXIST. The inventory is the load-bearing output: it must surface where this operation REUSES existing code versus builds new, because a parallel build collides with shipping machinery and silently doubles the per-ticket build (the one thing this whole operation is disciplined against).

Read the CODE at HEAD, not docs. Per src/AGENTS.md the state-of-the-system docs are known-stale ("trust the code over the docs"); the inventory must cite verified file:symbol locations from the current tree, not the docs and not guesses.

Inputs: The current ImplementResult/ReviewFeedback shapes; the ChainPhase implement-review loop; the existing check config + runner; the worker-output parser; the run event-log path.

Outputs:
- CompletionClaim contract (ThroughlineBuild.Contracts/Models/CompletionClaim.cs) carrying: provides, consumes, ac_bindings (each an AC reference plus verifier kind: test | grep-present | grep-absent | file | exit | golden), tests_added; plus forward-compatible hook fields for deferred work (a red/green verifier-kind slot, a tier slot, a per-class routing slot) documented as unenforced and ignored by every consumer this operation builds.
- notes/gate-integration-inventory.md, naming the ACTUAL file:symbol for each of the following, with a one-line REUSE-or-BUILD-NEW decision per item:

  EXISTING CHECK MACHINERY (the collision - reconcile, do not reinvent):
  - The check runner already invoked in review: AutomatedChecksRunner (ThroughlineBuild.Verification/AutomatedChecksRunner.cs) called at ReviewPhase.cs Step 7 (RunAsync(_reviewOptions.Checks, worktree)).
  - The CheckSpec model (Contracts/Verifier/, co-located with CheckResult.cs - confirm at HEAD) and CheckResult.
  - The capability map THAT ALREADY EXISTS: the [[review.checks]] table-array in .build/config.toml, parsed in Cli/Config.cs (~575-595) into IReadOnlyList<CheckSpec>, surfaced via ReviewOptions.Checks. Note the second instance of the same pattern: [[ship.regression_checks]] (Config.cs ~619-640).
  - REQUIRED DECISION the inventory must record: does the gate RELOCATE the existing review-checks run to the implement->review seam and have review CONSUME the results (one build per ticket), or run a second time (two builds - forbidden by the wall-discipline target)? Default position to justify or overturn: relocate + reuse. If the gate uses the existing review.checks config, Brief 02 collapses from "build a capability map" to "the map already exists; document and, if needed, generalize abstract check names (build/test/typecheck/lint/format)"; and Brief 03 becomes "reuse/extend AutomatedChecksRunner," not a new VerifierEngine.

  CLAIM EMISSION SURFACE (so Brief 05 is scoped correctly):
  - The WORKER_RESULT envelope parser: WorkerResultParser (Workers.Common/WorkerResultParser.cs) and the fenced-block resolver FencedBlockResolver (used at ImplementPhase.cs Step 15b for summary_ref).
  - The implement worker TEMPLATE loaded via TemplateLoader.Load(agentName, "implement"...) (Briefs/TemplateLoader.cs) - locate the per-agent implement template file(s). Returning a CompletionClaim requires changing the template to instruct emission AND extending the parser to resolve it; record both as part of Brief 05's true surface.

  STATE-TRANSITION BOUNDARY (so Brief 06 wires correctly):
  - ImplementPhase transitions InProgress -> InReview at its end (ImplementPhase.cs Step 18, ~line 377). ReviewPhase transitions InReview -> InProgress on a Rework verdict (ReviewPhase.cs ~line 279). ImplementPhase's isRework path REQUIRES state == InProgress (Step 2 guard, ~line 86).
  - REQUIRED DECISION the inventory must record: since implement leaves the ticket in InReview, a gate hard-fail must flip InReview -> InProgress to enter the rework loop (mirroring the review's Rework handling). Specify which: (a) gate runs on the InReview ticket and owns the InReview->InProgress flip on hard-fail, or (b) ImplementPhase stops transitioning and the gate owns InProgress->InReview on pass. Name the exact call sites either choice edits.

  CHAIN LOOP + REWORK FEED (so Briefs 06/08 hook the right place):
  - The implement->review seam is inside ChainPhase.RunImplementReviewLoopAsync, between ImplementPhase.RunAsync (~line 354) and RunOneReviewAsync (~line 406); MaxReworkRounds = 2 (~line 21).
  - The chain builds ReviewFeedback inline from the review verdict (ChainPhase.cs ~line 422); the rework brief is assembled by ImplementBriefBuilder.Build(..., ReviewFeedback, ...) (Briefs/ImplementBriefBuilder.cs); ReviewFeedback is Contracts/Models/ReviewFeedback.cs. Brief 08's gate-failure feed must construct feedback at this seam, distinguishable from a review-originated one.

  EVENT LOG (so Brief 09 emits correctly):
  - IEventSink (Contracts/IEventSink.cs); JsonlEventSink (EventLog/JsonlEventSink.cs) writes the .jsonl; events are emitted via EmitAsync(new WorkflowEvent(...)) with an EventKind. Note that LlmCall events already carry per-call token/cost data (the ledger's measurable terms) and that the chain already computes rework rounds at ChainPhase.cs ~line 309 (Count of implement steps with ReworkRoundNumber >= 1) - the ledger should reuse that, not re-derive.

Acceptance:
- [ ] A completion claim can be expressed as data with no check-execution logic in the contract; hard-gating verifier kinds are distinguished from smoke-signal kinds; deferred-tier hook fields exist and are marked unenforced.
- [ ] The inventory names the ACTUAL file:symbol (verified at HEAD) for every item above - not the state-of-system docs, not guesses.
- [ ] The inventory records an explicit REUSE-or-BUILD-NEW decision for the existing check machinery (AutomatedChecksRunner, CheckSpec, review.checks config), with a one-build-per-ticket justification.
- [ ] The inventory records the chosen state-transition ownership for a gate hard-fail, naming the exact call sites edited.
- [ ] The inventory flags every downstream brief whose scope/files change as a result (expected: 02 shrinks, 03 reuses the runner, 05 gains the template+parser surface, 06 gains the state flip, 08 hooks the chain feedback seam, 09 reuses the existing rework-round count).
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: This brief's value is the reconciliation, not the contract. The contract is a data shape; the inventory is what stops Plans B and C from building a second check-runner alongside the one already in ReviewPhase and double-building every ticket. Hook fields must be inert: present in the shape, ignored by every consumer this operation builds.

OOS:
- Any check execution, gate phase, or relocation of the review-checks run (that is Brief 06; this brief only DECIDES and DOCUMENTS it).
- Populating or resolving ac_bindings (deferred resolver work).
- Enforcing any hook field.
- Editing config.toml schema (Brief 02 acts on the inventory's finding).

#### Brief 02: capability-map-config  **[v2 - scope reduced]**

Goal: **[v2]** Confirm the existing `review.checks` capability map covers the abstract checks the gate needs, and generalize the abstract check names (build/test/typecheck/lint/format) only where the Brief 01 inventory shows a gap. The orchestrator already stays language-agnostic via this config; do not build a parallel one.

Inputs: The existing `.build/config.toml` `[[review.checks]]` schema and its parser in `Cli/Config.cs`; the Brief 01 inventory's reuse decision.

Outputs:
- A documented mapping of the abstract check names the gate consumes to the existing `review.checks` entries, plus any minimal config additions the inventory proved necessary (for example a `typecheck` or `lint` entry not currently declared).
- The parsed model continues to treat an absent check as not-configured, never a failure.

Acceptance:
- [ ] The abstract checks the gate runs are expressible in the existing review.checks config without a parallel config block.
- [ ] A check absent from config is reported as not-configured and never as a failure.
- [ ] Any new abstract check name added is parsed by the existing Config.cs path, not a new one.
- [ ] Malformed check configuration is rejected with a clear message at parse time (existing behavior preserved).
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: **[v2]** Semantics live in the command, not the orchestrator - this is already true of review.checks. The map is the entire language-agnostic contract and it already ships; this brief mostly documents it and closes any abstract-check gaps the inventory found.

OOS:
- Running any command (brief 03).
- Per-language defaults or autodetection.
- A second/parallel capability-map config block.

#### Brief 03: verifier-runner-reuse  **[v2 - reuse, not new engine]**

Goal: **[v2]** Reuse or extend `AutomatedChecksRunner` so a single configured check can be run against a given worktree and reported as a deterministic result (which check, pass/fail by exit code, captured output, duration), callable from the gate. Do not build a new `VerifierEngine` if the existing runner can serve.

Inputs: `AutomatedChecksRunner` (ThroughlineBuild.Verification) and `CheckSpec`/`CheckResult`; the capability-map confirmation from brief 02.

Outputs:
- The runner exposes (or already exposes) running a named configured check in a given worktree and returning a structured result including duration.
- Tests over pass, fail, and not-configured cases at the gate's call granularity.

Acceptance:
- [ ] Running a configured check returns pass when the command exits zero and fail with the captured output when it exits non-zero.
- [ ] A not-configured check is skipped and reported as skipped, never as a failure.
- [ ] Each result records which check ran and how long it took (duration feeds the ledger in Plan C).
- [ ] The runner runs a check against the worktree as-is without forcing a fresh checkout.
- [ ] **[v2]** No parallel check-runner is introduced; ReviewPhase and the gate call the same runner.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: Wall discipline starts here - the runner runs against the worktree it is handed, so the chain can hand it the warm tree the implementer left and avoid a cold rebuild. **[v2]** "One invocation" is not the same as "cheap": actual wall depends on the declared command being incremental and on build artifacts surviving implement; the ledger (Plan C) measures the truth.

OOS:
- Deciding which checks to run or in what order (Plan B).
- Any second build, checkout, or historical-SHA execution.
- Smoke signals (brief 04).

#### Brief 04: smoke-signals

Goal: Collect cheap, non-gating signals over a worktree - git-diff facts and source grep - that surface suspicious-but-not-conclusive conditions without ever failing the gate.

Inputs: A worktree and its diff against the chain base.

Outputs:
- Diff-fact collection: files touched, add/delete counts, whether a test file was in the diff.
- Grep-present and grep-absent collection over the diff or tree for caller-supplied patterns (for example stub markers, TODO markers, a required wiring pattern).
- All results emitted as labeled smoke signals distinct from hard-gate results.
- **[v2]** A shape consumable by the LLM review as a prior (see Brief 06), not only by the ledger.

Acceptance:
- [ ] Diff facts report files touched, line deltas, and test-file presence over the worktree diff.
- [ ] Grep-present and grep-absent return matches for supplied patterns over the diff or tree.
- [ ] Every signal is labeled as a smoke signal and is structurally distinct from a hard-gate result.
- [ ] Collecting signals never returns a gate-failing outcome on its own.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: These are smoke alarms, not gates - the symmetric-cost reasoning in "Why this exists" requires that low-precision checks never burn an expensive rework loop. They inform; they do not block. **[v2]** `format` results, if collected, belong here (smoke) or are auto-fixed - not hard-gated.

OOS:
- Failing the gate on any signal.
- The consumes/provides preflight (Plan B).
- Pattern authorship or AC binding (deferred).

## Plan B: Wire the gate into the chain

### Goal

Integrate the foundation into the chain so the gate runs once per ticket between implement and review. Brief 05 has the implementer return a `CompletionClaim` **[v2]** (changing the implement template and the WORKER_RESULT parser, not just ImplementResult) and treats a schema-invalid claim with a cheap re-ask before any hard failure. Brief 06 adds the gate execution point at the seam the brief 01 inventory named: it validates the claim, **[v2]** RELOCATES the existing review-checks run here so it executes once on the warm worktree, collects smoke signals, hands the smoke signals to the review, and emits a structured gate outcome that hard-fails only on configured-check failure - and on hard-fail owns the InReview -> InProgress transition. Brief 07 adds the chain-level consumes/provides preflight as a smoke signal. Brief 06 depends on 03, 04, and 05; brief 07 depends on 06.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 05 | claim-emission | **[v2]** Implementer returns a CompletionClaim (template + parser); invalid shape re-asks, then hard-fails | 01 | modified: implement template, Workers.Common/WorkerResultParser.cs, ImplementPhase.cs, ImplementResult (per 01 inventory) |
| 06 | gate-execution | **[v2]** Run the T1 gate once on the warm tree (relocated review-checks) between implement and review; own the hard-fail state flip; feed smoke to review | 03, 04, 05 | modified: ChainPhase.cs, ReviewPhase.cs; new: ThroughlineBuild.Gate gate-phase glue |
| 07 | consumes-provides-preflight | Chain-level set-inclusion smoke check, dormant when fields absent | 06 | modified: gate-phase glue |

### Briefs - detail

#### Brief 05: claim-emission  **[v2 - widened surface + re-ask]**

Goal: Make the implementer return a structured `CompletionClaim` alongside its existing result, and handle a malformed claim cheaply before any hard failure.

Inputs: The `CompletionClaim` contract (brief 01); the implement template, the WORKER_RESULT parser, the implement phase, and `ImplementResult` (all located in the 01 inventory).

Outputs:
- **[v2]** The implement worker template instructs the worker to emit a CompletionClaim block, and `WorkerResultParser`/`FencedBlockResolver` resolve it into a typed claim on `ImplementResult`.
- **[v2]** A missing or schema-invalid claim triggers a cheap, targeted re-ask ("re-emit just the claim for this diff") before any hard failure; only a re-ask that still fails is a hard implement failure.

Acceptance:
- [ ] A successful implement returns a well-formed completion claim resolved from the worker envelope.
- [ ] A missing or schema-invalid claim first triggers a bounded re-ask, not an immediate full rework.
- [ ] Only after the re-ask also fails does the ticket fail deterministically with a clear reason.
- [ ] The claim travels with the implement result to the gate without lossy conversion.
- [ ] Existing implement behavior is unchanged when a valid claim is present.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: **[v2]** Schema validity is the cheapest deterministic check in the system, but hard-failing it outright would be exactly the asymmetric false-fail this operation forbids - JSON-shape flakiness over correct code. The re-ask keeps the anti-lazy-worker property without burning an implement-axis loop. Populating provides/consumes and ac_bindings is allowed but not required here; their resolution is deferred.

OOS:
- Running any verifier or gate (brief 06).
- Resolving ac_bindings to concrete checks.
- Enforcing the deferred-tier hook fields.

#### Brief 06: gate-execution  **[v2 - relocate checks, own state flip, feed review]**

Goal: Add the gate phase between implement and review: validate the claim, **[v2]** run the relocated configured exit-code checks once on the warm worktree (reusing AutomatedChecksRunner), collect smoke signals, hand the smoke signals to the LLM review, and emit a structured outcome that hard-fails only on a configured-check failure - and on hard-fail transition the ticket InReview -> InProgress to enter rework.

Inputs: The reused runner (brief 03), the smoke-signal collectors (brief 04), the claim from brief 05, and the integration point + state-transition decision named in the 01 inventory.

Outputs:
- A gate phase invoked at the implement-to-review seam (ChainPhase.RunImplementReviewLoopAsync, between implement and RunOneReviewAsync) that produces a structured gate outcome (pass or hard-fail, the per-check results, and the collected smoke signals).
- **[v2]** The review-checks run is relocated to the gate; ReviewPhase consumes the gate's check results instead of re-running them (one build per ticket).
- **[v2]** On hard-fail, the gate owns the InReview -> InProgress transition and routes into the existing rework loop; on pass, review proceeds (and receives the smoke signals as a prior).

Acceptance:
- [ ] The gate runs after implement and before review for each chain ticket.
- [ ] The gate hard-fails only when a configured exit-code check fails; smoke signals never hard-fail it.
- [ ] **[v2]** The configured checks execute exactly once per ticket (relocated from review), against the warm worktree the implementer left - verified no second build is triggered in review.
- [ ] A schema-invalid claim (post re-ask) hard-fails the gate before any check runs.
- [ ] **[v2]** A gate hard-fail transitions the ticket InReview -> InProgress and enters the rework loop; a pass leaves it InReview for review.
- [ ] **[v2]** The LLM review receives the smoke signals as input.
- [ ] The gate emits a structured outcome carrying per-check results and smoke signals.
- [ ] A single-ticket chain behaves as before apart from the added gate and the relocated checks.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: One build per ticket is the wall-discipline target from "Why this exists" - achieved by RELOCATING the existing review-checks run, not adding a second. The structured outcome is consumed by Plan C.

OOS:
- Red/green or any historical-SHA / second-build execution (deferred).
- Run-mode tier selection beyond running T1 (hook only).
- Feeding the outcome into the rework brief or the ledger (Plan C).

#### Brief 07: consumes-provides-preflight

Goal: Add a chain-level smoke check that the accumulated provides across upstream tickets cover the consumes declared by the current ticket, active only when claims carry those fields.

Inputs: The gate outcome and the accumulated claims across the chain (brief 06).

Outputs:
- A preflight that computes whether declared consumes are a subset of accumulated provides and emits the result as a labeled smoke signal.
- A no-op result when claims omit provides/consumes.

Acceptance:
- [ ] When claims declare consumes and provides, the preflight reports whether consumes is a subset of accumulated upstream provides.
- [ ] The result is emitted as a smoke signal and never hard-fails the gate.
- [ ] The preflight is a no-op, not a failure, when the fields are absent.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: This is a free string set-inclusion check and a dormant hook until plan-time bindings (deferred) populate provides/consumes. It surfaces a broken contract seam early without risking a false-fail cascade.

OOS:
- Hard-gating on the subset check.
- Resolving provides/consumes from intent (deferred resolver work).
- Tests-as-contracts execution.

## Plan C: Pay for the gate - rework signal and ledger

### Goal

Make the gate partly self-funding and make its cost measurable. Brief 08 turns a structured gate failure into file/check pointers in the rework brief, so the cold-boot rework agent gets a sharper, cheaper signal than the LLM's prose rationale. Brief 09 **[v2]** emits a per-ticket cost ledger - cascade-caught, false-fails, gate wall, and gate-attributable rework tokens - with the directly-measurable terms populated and the judgment terms recorded as annotatable fields, so a real chain in attended and unattended mode produces the data that decides whether a second build is ever worth adding. Both depend on the structured outcome from brief 06.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 08 | structured-failure-to-rework | Gate failures become file/check pointers in the rework brief | 06 | modified: ReviewFeedback, ChainPhase feedback seam (~line 422), rework-brief builder (per 01 inventory) |
| 09 | cost-ledger | **[v2]** Per-ticket gate-cost ledger event (gate-attributable rework tokens, not an unmeasurable delta) | 06 | modified: event-log emission (per 01 inventory) |

### Briefs - detail

#### Brief 08: structured-failure-to-rework

Goal: Feed a gate hard-failure into the rework brief as concrete pointers - which check failed, which files, the captured output - rather than relying on prose.

Inputs: The structured gate outcome (brief 06); the chain feedback seam (ChainPhase.cs ~line 422), the rework brief builder and `ReviewFeedback` shape (located in the 01 inventory).

Outputs:
- A rework brief that, on a gate failure, carries the failed check name, the implicated files, and the captured check output as structured pointers.
- The existing LLM-review rework path is preserved for review-originated rework.

Acceptance:
- [ ] A gate hard-failure produces a rework brief containing the failed check and its captured output.
- [ ] The rework brief points at the implicated files rather than only describing the failure in prose.
- [ ] Review-originated rework still carries the reviewer's rationale as before.
- [ ] Gate-originated and review-originated rework are distinguishable in the brief.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: This is the synergy that makes the gate pay for itself - a structured pointer is a cheaper rework signal than prose, so the gate's wall cost is partly offset by cheaper rework turns. **[v2]** The tokens these cheaper rework turns cost are exactly the "gate-attributable rework tokens" Brief 09 measures.

OOS:
- The cheap re-check-before-escalation defense (a deferred false-fail mitigation).
- Changing the rework round cap or loop structure.
- The ledger (brief 09).

#### Brief 09: cost-ledger  **[v2 - term redefined, reuse existing count]**

Goal: Emit a per-ticket ledger event capturing the cost terms so the unsignable terms get measured instead of argued.

Inputs: The structured gate outcome and its per-check durations (brief 06); the chain's existing rework-round computation (ChainPhase.cs ~line 309); the run event-log emission point (located in the 01 inventory).

Outputs:
- A ledger event per chain ticket recording: gate wall (measured from check durations); **[v2]** gate-attributable rework tokens (measured: tokens spent on rework rounds a gate hard-fail triggered, distinguishable via Brief 08 - NOT an unmeasurable with-vs-without delta); cascade-caught (annotatable); false-fails (annotatable).
- The event is emitted into the existing run event log alongside the gate outcome.

Acceptance:
- [ ] Each gated ticket emits one ledger event into the run event log.
- [ ] Gate wall is populated from measured check durations.
- [ ] **[v2]** Gate-attributable rework tokens are populated from rework rounds whose trigger was a gate hard-fail (reusing the existing rework-round identification, not re-deriving it), and marked unavailable where token accounting is absent.
- [ ] Cascade-caught and false-fails exist as fields recordable after the fact.
- [ ] The ledger event is present for both attended and unattended runs.
- [ ] AOT publish succeeds with no new trim or AOT warnings.

Notes: **[v2]** The original "implement-token delta" was not measurable from a single run (a delta needs a counterfactual baseline). Gate-attributable rework tokens is the real, single-run-measurable quantity and is the number that matters - what the gate cost on the expensive implement axis. Two of the four terms still cannot be signed without instrumentation - that is the point. The ledger is how a real chain in both run modes decides whether the deferred second build ever earns its place, rather than re-litigating the cost model in the abstract.

OOS:
- Acting on the ledger (tier selection, enabling a second build).
- Run-mode-driven tier enforcement (hook only).
- Any analysis or reporting layer over the ledger events.

## What done looks like

Every chain ticket runs a deterministic gate once between implement and review, on the warm worktree the implementer left, **[v2]** by relocating the configured-check run that review used to do (one build invocation per ticket, no second run). The implementer returns a `CompletionClaim`; a malformed claim is re-asked once and only then hard-fails. The gate hard-fails only on the configured exit-code checks a project declared in its existing `review.checks` capability map (so the same gate works for C#, TypeScript, Python, Rust, and Go with no per-language code), **[v2]** with `format` kept as a smoke signal / auto-fix rather than a hard-gate, and surfaces diff facts, grep results, and the consumes/provides preflight as labeled smoke signals that never block **[v2] and are handed to the LLM review as a prior**. On a hard-fail the gate flips the ticket InReview -> InProgress and a gate failure flows into the rework brief as file and check pointers, giving the rework agent a sharper signal than prose. A ledger event per ticket lands in the run event log, with gate wall and **[v2]** gate-attributable rework tokens measured and the two judgment terms recordable after the fact, for both attended and unattended runs. The LLM review still runs and still owns the semantic calls the gate cannot make. The schema, the run-mode tier selector, and the per-class routing ship as inert hooks, so adding red/green, per-AC isolation, and the resolver later is additive rather than a rewrite. The compiles-but-wrong seam remains open and is documented as the known frontier - the floor narrows it and sharpens the review, and the ledger is what will tell us whether closing it with a second build is worth the wall. **[v2]** The floor's between-ticket guarantee rests on sequential stacking; if chains ever fan out in parallel, revisit the warm-worktree-equals-integrated-tree assumption.
