# Operation: deterministic-chain-gate

Add a deterministic, language-agnostic quality gate that runs between the implement and review phases of a chain, so a defect in one coupled ticket is caught mechanically before later tickets build on it. The gate is a claim/check split: the implementer returns a structured `CompletionClaim`, and the orchestrator independently falsifies it against ground truth (exit codes, git diff, source grep) - it never trusts the claim. This operation builds only the cheap T1 floor (the project's configured checks run once on the warm worktree, plus labeled smoke signals), wires its structured failures into the rework brief, and instruments a cost ledger so the data, not the argument, decides whether a second build (red/green, per-AC isolation) ever earns its place. Crucially, the floor reuses the check config and runner that already ship in `review.checks` / `AutomatedChecksRunner` rather than building a parallel one - relocating the existing review-time check run to the gate, not adding a second. The schema and phase hooks are left forward-compatible so the deferred tiers are additive, not a rewrite.

## Why this exists

Today the only quality gate in a chain is the per-ticket LLM review, which is a prose judgment that runs after the fact. For a chain of tightly-coupled tickets that is a real exposure: a defect in ticket N - a broken inter-ticket contract, a check the implementer skipped, an unsatisfied acceptance criterion - is not caught until review, by which point tickets N+1..N+k have already built on it, and the fix is an implement-cascade across the whole chain. The cascade lands on the implement phase, which a measured chain showed is ~91% of token cost and the dominant slice of serial wall time. So a cheap mechanical check that fails fast at the seam can prevent an expensive cascade on the most expensive axis.

What this floor actually catches, and what it does not. The implementer already runs build and tests in its worktree to finish its ticket, so the gate re-running the same checks does not catch a hidden behavioral defect - that is the compiles-but-wrong seam, explicitly deferred. The floor's real marginal value is twofold: it runs the checks the implementer did not run (typecheck, and any check the worker skipped) so an unrun-check break cannot propagate down the stack; and it provides an independent oracle that catches a dishonest "claimed done" worker that did not actually pass on the final committed tree. The `cascade-caught` ledger term therefore measures skipped-check and dishonesty cascades, not hidden-defect cascades; that expectation is set deliberately so the first measured figure is not read as a disappointment.

The design principle is falsify, don't trust: a verifier is worth exactly the independence of its oracle from the implementer. A deterministic gate's value is not that it is deterministic - it is that it did not write the code under test. Determinism is just the cheapest way to buy an independent, repeatable oracle. The check configuration keeps it language-agnostic (the orchestrator only knows exit codes, git-diff facts, and source grep; the project declares what "build" or "test" means), so the same gate serves C#, TypeScript, Python, Rust, and Go without per-language tooling baked in. That configuration already exists as the `review.checks` table plus the `CheckSpec` model and `AutomatedChecksRunner`; this operation reuses it rather than inventing a parallel one.

The cost of getting this wrong is symmetric and both sides land on the expensive implement axis: a missed defect causes a cascade, but a false-fail dispatches a cold-boot rework agent to "fix" correct code, which can damage it. That dictates which checks hard-gate. Only build, test, and typecheck hard-fail the gate - high-precision, high-cascade-risk checks. Lint and format are treated as smoke signals or auto-fixes, never hard gates: a cosmetic, auto-fixable violation must not burn a cold rework loop. By the same rule, the low-precision signals (diff facts, grep, the consumes/provides preflight) are labeled smoke signals that never fail the gate, and a schema-invalid claim gets a cheap targeted re-ask before any hard-fail, since LLM JSON-shape flakiness over correct code is itself a likely false-fail. It is also why the operation refuses to price the gate at zero: it instruments a ledger (cascade-caught, false-fails, gate wall, gate-attributable rework tokens) per ticket so the unsignable terms get measured on a real chain in both attended and unattended mode before any second build is added.

This floor runs the configured checks once on the warm per-ticket worktree and treats that as the integrated tree. That equivalence holds only because chains stack sequentially - each child branches off the prior, so ticket N's worktree is base plus all prior chain commits. It is true today under serial dispatch with enforced parent-chain stacking. If chains ever fan out from a fixed base in parallel, the per-ticket worktree is no longer integrated and the gate's between-ticket guarantee weakens; revisit then.

After this lands: every chain ticket runs a deterministic gate once between implement and review, on the worktree the implementer left warm, with one check invocation per ticket achieved by relocating the existing review-time check run rather than adding a second. The gate hard-fails only on build/test/typecheck, surfaces everything else as labeled smoke signals that are also handed to the LLM review as a prior, validates the claim's shape, and emits a structured outcome. Gate failures flow into the rework brief as file/check pointers - a cheaper, sharper rework signal than prose. A ledger event per ticket records the cost terms. The LLM review still runs and still owns the semantic judgments the gate cannot make.

## Deliberately not in this operation

The compiles-but-wrong-behind-a-valid-surface seam - a stub that typechecks, returns a plausible constant, and passes a test written against itself - is NOT closed by this floor. It is an oracle-independence failure (the answer key was authored by the thing under test) and the same hole as a buggy golden file. Closing it requires either behavioral red/green where the surface pre-existed, or LLM review with execution, both deferred. The floor narrows the gap and makes the eventual review sharper; it does not claim to close the seam. Also deferred: red/green-at-two-commits, per-AC isolated test runs, the plan-time intent-level bindings plus independent resolver role, and full T2/T3 tier enforcement. The schema and the run-mode tier selector ship as unenforced hooks only.

## Dispatch order

| Plan | Name | Depends on | Effort |
| --- | --- | --- | --- |
| A | Gate foundation: claim, reconciliation inventory, reused runner | - | M |
| B | Wire the gate into the chain | A | M |
| C | Pay for the gate: rework signal and ledger | B | M |

## Plan A: Gate foundation - claim, reconciliation inventory, reused runner

### Goal

Build the deterministic substrate every later plan consumes, with no chain wiring yet. Brief 01 lands the `CompletionClaim` contract (carrying the forward-compatible hooks for the deferred tiers) and a reconciliation inventory that names the real files where the gate slots and the existing check machinery, config, worker-output, state-transition, and event-log surfaces it must reuse - that map is load-bearing for Plans B and C and stops them from building a second check-runner alongside the one already in review. Brief 02 documents the abstract check names in the capability map that already exists (`review.checks`) and generalizes them only where the inventory shows a gap. Brief 03 reuses or extends `AutomatedChecksRunner` to run a configured check and read its exit code, and depends on 02. Brief 04 builds the smoke-signal collectors (git-diff facts and source grep), which are pure over a worktree and depend on nothing. The runner and signals are standalone here - locked and testable before any chain consumes them.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 01 | completion-claim-and-inventory | Define the CompletionClaim contract with deferred-tier hooks; inventory the gate integration points and the reuse-vs-rebuild decisions | - | new: ThroughlineBuild.Contracts/Models/CompletionClaim.cs; docs: notes/gate-integration-inventory.md |
| 02 | capability-map-config | Document the existing review.checks capability map; generalize abstract check names only if the inventory shows a gap | - | modified (if needed): .build/config.toml + Cli/Config.cs |
| 03 | verifier-runner-reuse | Reuse/extend AutomatedChecksRunner to run a configured check and report a deterministic result | 02 | modified: ThroughlineBuild.Verification/AutomatedChecksRunner.cs + tests |
| 04 | smoke-signals | Collect git-diff facts and grep-present/absent as labeled non-gating signals | - | new: ThroughlineBuild.Gate/SmokeSignals.cs + tests |

### Briefs - detail

#### Brief 01: completion-claim-and-inventory

Goal: Define the structured artifact the implementer returns and the orchestrator falsifies (with deferred-tier hook fields present but unenforced), and produce a reconciliation inventory that names the real files and symbols the gate integrates with - including the check-running machinery, config, worker-output, state-transition, and event-log surfaces that already exist. The inventory is the load-bearing output: it must surface where this operation reuses existing code versus builds new, because a parallel build collides with shipping machinery and silently doubles the per-ticket build, the one thing this whole operation is disciplined against.

Read the code at HEAD, not the docs. Per `src/AGENTS.md` the state-of-the-system docs are known-stale ("trust the code over the docs"); the inventory must cite verified file:symbol locations from the current tree, not the docs and not guesses.

Inputs: The current `ImplementResult` and `ReviewFeedback` shapes; the `ChainPhase` implement-review loop; the existing check config and runner; the worker-output parser and template loader; the ticket state-transition call sites; the run event-log path.

Outputs:
- `CompletionClaim` contract (`ThroughlineBuild.Contracts/Models/CompletionClaim.cs`) carrying provides, consumes, ac_bindings (each an AC reference plus a verifier kind: test, grep-present, grep-absent, file, exit, golden), and tests_added; plus forward-compatible hook fields for deferred work (a red/green verifier-kind slot, a tier slot, a per-class routing slot) documented as unenforced and ignored by every consumer this operation builds.
- `notes/gate-integration-inventory.md`, naming the actual file:symbol for each surface below, each with a one-line reuse-or-build-new decision:
  - Existing check machinery: the runner invoked in review (`AutomatedChecksRunner` in `ThroughlineBuild.Verification`, called from `ReviewPhase`), the `CheckSpec` and `CheckResult` models in `Contracts/Verifier/`, and the capability map that already exists - the `[[review.checks]]` table parsed in `Cli/Config.cs` into `IReadOnlyList<CheckSpec>` and surfaced via review options, plus the second instance of the same pattern, `[[ship.regression_checks]]`.
  - The required reuse decision: does the gate relocate the existing review-time check run to the implement->review seam and have review consume the results (one build per ticket), or run a second time (two builds, forbidden by the wall-discipline target)? Default position, to justify or overturn: relocate and reuse.
  - Every other caller of the check runner. If `AutomatedChecksRunner` is invoked outside the chain, relocating the run into a chain-only gate would strand that caller's checks. The scope is already known and the inventory must confirm it: `ReviewPhase` is constructed in two places - the standalone `build review` path (`Program.cs:1254`) and the chain factory (`Program.cs:1454`) - and both pass `ReviewOptions(config.Review.Checks, ...)`, so standalone `build review TLB-X` genuinely runs the checks itself and needs the `ReviewPhase` fallback when no gate output is present. The only other caller, `ShipPhase` (`ShipPhase.cs:508,595,833`), runs `ship.regression_checks`, a different configured set in a different phase, and is unaffected - it is out of scope and must not be touched. Record this so Brief 06 does not over-reach into ShipPhase.
  - Claim emission surface: the WORKER_RESULT envelope parser (`WorkerResultParser` in `Workers.Common`) and the fenced-block resolver used for the implement summary payload, plus the implement worker template loaded via the template loader. Returning a `CompletionClaim` requires changing the template to instruct emission and extending the parser to resolve it; record both as Brief 05's true surface.
  - State-transition boundary: `ImplementPhase` transitions InProgress -> InReview at its end (verified at `ImplementPhase.cs:377`); the rework path requires `State == InProgress` (verified at `ImplementPhase.cs:86`); `ReviewPhase` transitions InReview -> InProgress on a rework verdict. Record the required decision: since implement leaves the ticket InReview, a gate hard-fail must flip InReview -> InProgress to enter rework. Specify which option and name the exact call sites it edits: (a) the gate runs on the InReview ticket and owns the InReview -> InProgress flip on hard-fail, or (b) implement stops transitioning and the gate owns InProgress -> InReview on pass.
  - Chain loop and rework feed: the implement->review seam inside `ChainPhase.RunImplementReviewLoopAsync` (between the implement call and the review call), the rework-round cap, where the chain builds `ReviewFeedback` inline from the review verdict, and how the rework brief is assembled. Brief 08's gate-failure feed must construct feedback at this seam, distinguishable from a review-originated one.
  - Event log: the event sink interface and its JSONL implementation, the event-emission call shape and event kinds, the fact that LLM-call events already carry per-call token/cost data (the ledger's measurable terms), and where the chain already computes rework-round counts (so Brief 09 reuses that rather than re-deriving it).

Acceptance:
- [ ] A completion claim can be expressed as data with no check-execution logic in the contract; hard-gating verifier kinds are distinguished from smoke-signal kinds; deferred-tier hook fields exist and are marked unenforced
- [ ] The inventory names the actual file:symbol, verified at HEAD, for every surface above - not the state-of-system docs, not guesses
- [ ] The inventory records an explicit reuse-or-build-new decision for the existing check machinery, with a one-build-per-ticket justification
- [ ] The inventory records every other caller of the check runner and whether relocation strands a standalone path
- [ ] The inventory records the chosen state-transition ownership for a gate hard-fail, naming the exact call sites edited
- [ ] The inventory flags every downstream brief whose scope or files change as a result
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: This brief's value is the reconciliation, not the contract. The contract is a data shape; the inventory is what stops Plans B and C from building a second check-runner alongside the one already in review and double-building every ticket. Hook fields must be inert: present in the shape, ignored by every consumer this operation builds.

OOS:
- Any check execution, gate phase, or relocation of the review-time check run (that is Brief 06; this brief only decides and documents it)
- Populating or resolving ac_bindings (deferred resolver work)
- Enforcing any hook field
- Editing the config schema (Brief 02 acts on the inventory's finding)

#### Brief 02: capability-map-config

Goal: Confirm the existing `review.checks` capability map covers the abstract checks the gate needs, and generalize the abstract check names only where the Brief 01 inventory shows a gap. The orchestrator already stays language-agnostic via this config; do not build a parallel one.

Inputs: The existing `.build/config.toml` `[[review.checks]]` schema and its parser in `Cli/Config.cs`; the Brief 01 inventory's reuse decision.

Outputs:
- A documented mapping of the abstract check names the gate consumes (build, test, typecheck, lint, format) to the existing `review.checks` entries, plus any minimal config additions the inventory proved necessary.
- A documented gating-vs-advisory classification per abstract check: build, test, and typecheck are gating (their exit code can hard-fail the gate); lint and format are advisory (run by the same runner, exit code recorded and surfaced to review as a smoke signal, never hard-failing). This resolves where lint and format execute - they are runner checks, not diff/grep collector signals, because they are commands - so a TypeScript or Python adopter is not left guessing.
- The parsed model continues to treat an absent check as not-configured, never a failure.

Acceptance:
- [ ] The abstract checks the gate runs are expressible in the existing review.checks config without a parallel config block
- [ ] Each abstract check carries a gating-or-advisory classification, with build/test/typecheck gating and lint/format advisory
- [ ] A check absent from config is reported as not-configured and never as a failure
- [ ] Any new abstract check name added is parsed by the existing config path, not a new one
- [ ] Malformed check configuration is rejected with a clear message at parse time, preserving existing behavior
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Semantics live in the command, not the orchestrator - already true of `review.checks`. The map is the entire language-agnostic contract and it already ships; this brief mostly documents it and closes any abstract-check gaps the inventory found. The dogfood config currently declares only build and test, both `dotnet`. For C# that is correct and the design working as intended: `dotnet build` already is the typecheck, so a separate typecheck check is redundant here and simply stays not-configured. One concrete cost to fix while here: `dotnet test` recompiles unless passed `--no-build`, so the configured pair can compile twice per ticket - the configured test command should be ordered after build and pass `--no-build`, which is the wall-discipline target in action and which Brief 06 enforces at execution time.

OOS:
- Running any command (brief 03)
- Per-language defaults or autodetection
- A second or parallel capability-map config block

#### Brief 03: verifier-runner-reuse

Goal: Reuse or extend `AutomatedChecksRunner` so a single configured check can be run against a given worktree and reported as a deterministic result (which check, pass/fail by exit code, captured output, duration), callable from the gate. Do not build a new engine if the existing runner can serve.

Inputs: `AutomatedChecksRunner` and the `CheckSpec`/`CheckResult` models; the capability-map confirmation from brief 02.

Outputs:
- The runner exposes, or already exposes, running a named configured check in a given worktree and returning a structured result including duration.
- Tests over pass, fail, and not-configured cases at the gate's call granularity.

Acceptance:
- [ ] Running a configured check returns pass when the command exits zero and fail with the captured output when it exits non-zero
- [ ] A not-configured check is skipped and reported as skipped, never as a failure
- [ ] Each result records which check ran and how long it took, and the duration feeds the ledger in Plan C
- [ ] The runner runs a check against the worktree as-is without forcing a fresh checkout
- [ ] No parallel check-runner is introduced; review and the gate call the same runner
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Wall discipline starts here - the runner runs against the worktree it is handed, so the chain can hand it the warm tree the implementer left and avoid a cold rebuild. One invocation is not the same as cheap: actual wall depends on the declared command being incremental and on build artifacts surviving implement; the ledger in Plan C measures the truth.

OOS:
- Deciding which checks to run or in what order (Plan B)
- Any second build, checkout, or historical-SHA execution
- Smoke signals (brief 04)

#### Brief 04: smoke-signals

Goal: Collect cheap, non-gating signals over a worktree - git-diff facts and source grep - that surface suspicious-but-not-conclusive conditions without ever failing the gate.

Inputs: A worktree and its diff against the chain base.

Outputs:
- Diff-fact collection: files touched, add/delete counts, whether a test file was in the diff.
- Grep-present and grep-absent collection over the diff or tree for caller-supplied patterns (for example stub markers, TODO markers, a required wiring pattern).
- All results emitted as labeled smoke signals distinct from hard-gate results, in a shape consumable by the LLM review as a prior (see Brief 06), not only by the ledger.

Acceptance:
- [ ] Diff facts report files touched, line deltas, and test-file presence over the worktree diff
- [ ] Grep-present and grep-absent return matches for supplied patterns over the diff or tree
- [ ] Every signal is labeled as a smoke signal and is structurally distinct from a hard-gate result
- [ ] Collecting signals never returns a gate-failing outcome on its own
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: These are smoke alarms, not gates - the symmetric-cost reasoning in "Why this exists" requires that low-precision checks never burn an expensive rework loop. They inform; they do not block. Smoke signals have two producers that share one shape: this collector (diff facts and grep) and the advisory check results from the runner (lint, format). This brief owns only the diff/grep producer; lint and format run through the runner as advisory checks (Brief 02), not here, because they are commands rather than diff/grep queries.

OOS:
- Failing the gate on any signal
- The consumes/provides preflight (Plan B)
- Pattern authorship or AC binding (deferred)

## Plan B: Wire the gate into the chain

### Goal

Integrate the foundation into the chain so the gate runs once per ticket between implement and review. Brief 05 has the implementer return a `CompletionClaim` - which means changing the implement template and the WORKER_RESULT parser, not just `ImplementResult` - and treats a schema-invalid claim with a cheap re-ask before any hard failure. Brief 06 adds the gate execution point at the seam the brief 01 inventory named: it validates the claim, relocates the existing review-time check run here so it executes once on the warm worktree, collects smoke signals, hands the smoke signals to the review, and emits a structured outcome that hard-fails only on a build/test/typecheck failure - and on hard-fail owns the InReview -> InProgress transition. Brief 07 adds the chain-level consumes/provides preflight as a smoke signal. Brief 06 depends on 03, 04, and 05; brief 07 depends on 06.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 05 | claim-emission | Implementer returns a CompletionClaim via template + parser; invalid shape re-asks, then hard-fails | 01 | modified: implement template, Workers.Common/WorkerResultParser.cs, ImplementPhase.cs, ImplementResult (per 01 inventory) |
| 06 | gate-execution | Run the gate once on the warm tree (relocated review checks) between implement and review; own the hard-fail state flip; feed smoke to review | 03, 04, 05 | modified: ChainPhase.cs, ReviewPhase.cs; new: ThroughlineBuild.Gate gate-phase glue |
| 07 | consumes-provides-preflight | Chain-level set-inclusion smoke check, dormant when fields absent | 06 | modified: gate-phase glue |

### Briefs - detail

#### Brief 05: claim-emission

Goal: Make the implementer return a structured `CompletionClaim` alongside its existing result, and handle a malformed claim cheaply before any hard failure.

Inputs: The `CompletionClaim` contract (brief 01); the implement worker template, the WORKER_RESULT parser, the implement phase, and `ImplementResult`, all located in the 01 inventory.

Outputs:
- The implement worker template instructs the worker to emit a `CompletionClaim` block, and the WORKER_RESULT parser resolves it into a typed claim on `ImplementResult`.
- A missing or schema-invalid claim triggers a cheap, targeted re-ask ("re-emit just the claim for this diff") before any hard failure; only a re-ask that still fails is a hard implement failure.

Acceptance:
- [ ] A successful implement returns a well-formed completion claim resolved from the worker envelope
- [ ] A missing or schema-invalid claim first triggers a bounded re-ask, not an immediate full rework
- [ ] Only after the re-ask also fails does the ticket fail deterministically with a clear reason
- [ ] The claim travels with the implement result to the gate without lossy conversion
- [ ] Existing implement behavior is unchanged when a valid claim is present
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: Schema validity is the cheapest deterministic check in the system, but hard-failing it outright would be exactly the asymmetric false-fail this operation forbids - JSON-shape flakiness over correct code. The re-ask enforces a complete claim without burning an implement-axis loop. Populating provides/consumes and ac_bindings is allowed but not required here; their resolution is deferred.

OOS:
- Running any verifier or gate (brief 06)
- Resolving ac_bindings to concrete checks
- Enforcing the deferred-tier hook fields

#### Brief 06: gate-execution

Goal: Add the gate phase between implement and review: validate the claim, run the relocated configured checks once on the warm worktree (reusing the runner from brief 03), collect smoke signals, hand the smoke signals to the LLM review, and emit a structured outcome that hard-fails only on a build/test/typecheck failure - and on hard-fail transition the ticket InReview -> InProgress to enter rework.

Inputs: The reused runner (brief 03), the smoke-signal collectors (brief 04), the claim from brief 05, and the integration point, standalone-path finding, and state-transition decision named in the 01 inventory.

Outputs:
- A gate phase invoked at the implement-to-review seam inside the chain loop that produces a structured gate outcome (pass or hard-fail, the per-check results, and the collected smoke signals).
- The review-time check run is relocated to the gate; review consumes the gate's check results instead of re-running them (one build per ticket). Per the 01 inventory's standalone-path finding, any non-chain caller of the checks either runs through the gate or retains a fallback so it is not stranded.
- On hard-fail, the gate owns the InReview -> InProgress transition and routes into the existing rework loop; on pass, review proceeds and receives the smoke signals as a prior.

Acceptance:
- [ ] The gate runs after implement and before review for each chain ticket
- [ ] The gate hard-fails only when build, test, or typecheck fails; lint, format, and smoke signals never hard-fail it
- [ ] The configured checks execute exactly once per ticket, relocated from review, against the warm worktree the implementer left, with no second build triggered in review
- [ ] A standalone review path is not stranded of its checks by the relocation
- [ ] A schema-invalid claim, after the brief 05 re-ask, hard-fails the gate before any check runs
- [ ] A gate hard-fail transitions the ticket InReview -> InProgress and enters the rework loop; a pass leaves it InReview for review
- [ ] The LLM review receives the smoke signals as input
- [ ] The gate emits a structured outcome carrying per-check results and smoke signals
- [ ] A single-ticket chain behaves as before apart from the added gate and the relocated checks
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: One build per ticket is the wall-discipline target from "Why this exists" - achieved by relocating the existing review-time check run, not adding a second. Watch the within-ticket double-compile: the dogfood pair `dotnet build` then `dotnet test` recompiles in the test step unless test is ordered after build and passed `--no-build`. The gate runs the gating checks build-first so the test command can reuse build's artifacts; the ledger's gate-wall term will confirm whether that holds on the first real run. The structured outcome is consumed by Plan C.

OOS:
- Red/green or any historical-SHA / second-build execution (deferred)
- Run-mode tier selection beyond running T1 (hook only)
- Feeding the outcome into the rework brief or the ledger (Plan C)
- `ShipPhase` and its `ship.regression_checks` run, which are a different configured set in a different phase and are unaffected by the relocation - leave them untouched

#### Brief 07: consumes-provides-preflight

Goal: Add a chain-level smoke check that the accumulated provides across upstream tickets cover the consumes declared by the current ticket, active only when claims carry those fields.

Inputs: The gate outcome and the accumulated claims across the chain (brief 06).

Outputs:
- A preflight that computes whether declared consumes are a subset of accumulated provides and emits the result as a labeled smoke signal.
- A no-op result when claims omit provides/consumes.

Acceptance:
- [ ] When claims declare consumes and provides, the preflight reports whether consumes is a subset of accumulated upstream provides
- [ ] The result is emitted as a smoke signal and never hard-fails the gate
- [ ] The preflight is a no-op, not a failure, when the fields are absent
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: This is a free string set-inclusion check and a dormant hook until plan-time bindings (deferred) populate provides/consumes. It surfaces a broken contract seam early without risking a false-fail cascade.

OOS:
- Hard-gating on the subset check
- Resolving provides/consumes from intent (deferred resolver work)
- Tests-as-contracts execution

## Plan C: Pay for the gate - rework signal and ledger

### Goal

Make the gate partly self-funding and make its cost measurable. Brief 08 turns a structured gate failure into file/check pointers in the rework brief, so the cold-boot rework agent gets a sharper, cheaper signal than the LLM's prose rationale. Brief 09 emits a per-ticket cost ledger - cascade-caught, false-fails, gate wall, and gate-attributable rework tokens - with the directly-measurable terms populated and the judgment terms recorded as annotatable fields, so a real chain in attended and unattended mode produces the data that decides whether a second build is ever worth adding. Both depend on the structured outcome from brief 06.

### Briefs

| # | Slug | Intent | Depends on | Files touched |
| --- | --- | --- | --- | --- |
| 08 | structured-failure-to-rework | Gate failures become file/check pointers in the rework brief | 06 | modified: ReviewFeedback, ChainPhase feedback seam, rework-brief builder (per 01 inventory) |
| 09 | cost-ledger | Per-ticket gate-cost ledger event (gate-attributable rework tokens, not an unmeasurable delta) | 06 | modified: event-log emission (per 01 inventory) |

### Briefs - detail

#### Brief 08: structured-failure-to-rework

Goal: Feed a gate hard-failure into the rework brief as concrete pointers - which check failed, which files, the captured output - rather than relying on prose.

Inputs: The structured gate outcome (brief 06); the chain feedback seam, the rework brief builder, and the `ReviewFeedback` shape, all located in the 01 inventory.

Outputs:
- A rework brief that, on a gate failure, carries the failed check name, the implicated files, and the captured check output as structured pointers.
- The existing LLM-review rework path is preserved for review-originated rework.

Acceptance:
- [ ] A gate hard-failure produces a rework brief containing the failed check and its captured output
- [ ] The rework brief points at the implicated files rather than only describing the failure in prose
- [ ] Review-originated rework still carries the reviewer's rationale as before
- [ ] Gate-originated and review-originated rework are distinguishable in the brief
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: This is the synergy that makes the gate pay for itself - a structured pointer is a cheaper rework signal than prose, so the gate's wall cost is partly offset by cheaper rework turns. The tokens these cheaper rework turns cost are exactly the gate-attributable rework tokens Brief 09 measures.

OOS:
- The cheap re-check-before-escalation defense for non-claim checks (a deferred false-fail mitigation)
- Changing the rework round cap or loop structure
- The ledger (brief 09)

#### Brief 09: cost-ledger

Goal: Emit a per-ticket ledger event capturing the cost terms so the unsignable terms get measured instead of argued.

Inputs: The structured gate outcome and its per-check durations (brief 06); the chain's existing rework-round computation; the run event-log emission point, located in the 01 inventory.

Outputs:
- A ledger event per chain ticket recording: gate wall (measured from check durations); gate-attributable rework tokens (measured: tokens spent on rework rounds a gate hard-fail triggered, distinguishable via Brief 08, not an unmeasurable with-vs-without delta); cascade-caught (annotatable); and false-fails (annotatable).
- The event is emitted into the existing run event log alongside the gate outcome.

Acceptance:
- [ ] Each gated ticket emits one ledger event into the run event log
- [ ] Gate wall is populated from measured check durations
- [ ] Gate-attributable rework tokens are populated from rework rounds whose trigger was a gate hard-fail, reusing the existing rework-round identification rather than re-deriving it, and marked unavailable where token accounting is absent
- [ ] Cascade-caught and false-fails exist as fields recordable after the fact
- [ ] The ledger event is present for both attended and unattended runs
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: An "implement-token delta" would need a counterfactual baseline that does not exist in a single run. Gate-attributable rework tokens is the real, single-run-measurable quantity and is the number that matters - what the gate cost on the expensive implement axis. Two of the four terms still cannot be signed without instrumentation - that is the point. The ledger is how a real chain in both run modes decides whether the deferred second build ever earns its place, rather than re-litigating the cost model in the abstract.

OOS:
- Acting on the ledger (tier selection, enabling a second build)
- Run-mode-driven tier enforcement (hook only)
- Any analysis or reporting layer over the ledger events

## What done looks like

Every chain ticket runs a deterministic gate once between implement and review, on the warm worktree the implementer left, by relocating the configured-check run that review used to do - one check invocation per ticket, no second run, and no standalone caller stranded. The implementer returns a `CompletionClaim`; a malformed claim is re-asked once and only then hard-fails. The gate hard-fails only on build, test, and typecheck - the checks a project declared in its existing `review.checks` capability map, so the same gate works for C#, TypeScript, Python, Rust, and Go with no per-language code - while lint and format stay smoke signals or auto-fixes, and diff facts, grep results, and the consumes/provides preflight are labeled smoke signals that never block and are handed to the LLM review as a prior. On a hard-fail the gate flips the ticket InReview -> InProgress and the failure flows into the rework brief as file and check pointers, giving the rework agent a sharper signal than prose. A ledger event per ticket lands in the run event log, with gate wall and gate-attributable rework tokens measured and the two judgment terms recordable after the fact, for both attended and unattended runs. The LLM review still runs and still owns the semantic calls the gate cannot make. The schema, the run-mode tier selector, and the per-class routing ship as inert hooks, so adding red/green, per-AC isolation, and the resolver later is additive rather than a rewrite. The compiles-but-wrong seam remains open and is documented as the known frontier - the floor narrows it and sharpens the review, and the ledger is what will tell us whether closing it with a second build is worth the wall. The floor's between-ticket guarantee rests on sequential stacking; if chains ever fan out in parallel, the warm-worktree-equals-integrated-tree assumption must be revisited.
