# Briefing: Throughline Build chain efficiency - findings + decisions for the holistic plan

You are authoring a holistic plan (and the op-doc schema that supports it) to make
human + agent work on Throughline Build efficient. You have: the op-doc spec + addendum +
example, a sample op-doc (survey-app-build), and the state-of-system docs. This packet
adds the empirical findings and decisions from a forensic investigation of a real run,
plus the schema changes you (as op-doc author) have the leverage to make. Trust code
over the state-of-system docs where they disagree; the docs lag HEAD.

## 1. What actually happened (cite these; they are concrete)

- Run: `build chain 1 --batch-implement` on the survey-app-build op-doc (8 leaf briefs
  under 2 plans). Codex worker, build e05d918. Event log 1-chain-...191540.jsonl.
- Brief 08 / conditional-logic-engine (ticket 11) - the op-doc's own "deliberate
  stew-on-it brief" - hit ReworkCapExceeded: 3 review rounds, all returning Rework on
  REAL, in-scope defects (positional qN rule-reference migration on delete and on
  move/reorder), with automated checks GREEN. The review/rework loop worked correctly;
  the implementer could not converge in initial + 2 rework rounds.
- That one capped leaf cascaded: ParentStoppedEarly up to the root, and the root landing
  is gated on no-child-stopped, so NOTHING landed - including 7 fully-completed sibling
  briefs. ~70 minutes, zero tickets shipped to the target.
- Two claude-code runs (build d0ee732) on the identical op-doc completed brief 08 in
  0 rework (run A) and 1 rework (run B). The Codex run used a later engine build, so the
  comparison does not isolate worker choice even though the structure, cap, and brief
  were the same.
- All three runs ran 8 COLD per-ticket leaf chains. None batched: --batch-implement is
  parsed and threaded but ChainPhase is constructed without a batch worker, so the batch
  path is unreachable. (Wiring fix in flight.)
- Observed cost pattern: the run with MORE rework used 9.9M cache_read; the run with ZERO
  rework used 18.4M. cache_create (unique context) was ~equal. The cache ratio is
  consistent with more turns, but the logs do not expose direct turn counts. Treat
  turns/context/cohesion as the optimization hypothesis, not a measured causal result.

## 2. Root causes (so the plan targets the right things)

1. promote is global. [plan] mode = "promote" (the default) skips the plan worker for
   EVERY brief. That was the right call for 7 of 8 briefs (planning cost ~2x implement)
   but it robbed the one convoluted brief of the design pass its own author demanded
   ("Plan the AST shape and precedence climbing before coding"). This is the central
   tension with the addendum, which currently treats "no planning pass" as universal.
2. MaxReworkRounds = 2 is hardcoded. Brief 08 was still converging at round 3 (each
   Rework named a narrower defect). The cap fell where weaker workers hadn't crossed the
   convergence line yet.
3. All-or-nothing landing. One stuck leaf strands all completed siblings.
4. Worker capability variance is real and large. The loop is doing its job by refusing
   to ship buggy work; do NOT weaken review to "fix" this.

## 3. Direction already decided (plan toward these)

- Architecture target: warm-batch per PLAN (one agent holds a plan's cohesive briefs in
  one session), a DETERMINISTIC gate per brief (op-30) BETWEEN briefs inside that
  session, and one agentic review per plan using the brief Acceptance boxes as the
  rubric. Peel high-risk briefs into their own isolated, planned sessions.
- Batch granularity is cohesion-based: warm per plan, not "all 8 at once" (a giant
  session's turns blow up cache super-linearly) and not "all isolated" (cold re-priming
  dominates for cohesive work). Respect op-31's size caps.
- op-30 (deterministic-chain-gate, in flight - modifiable) should run a brief's machine
  commands after each brief's commit, before the next brief starts, INSIDE the warm
  session - not only at top-level ticket boundaries.

## 4. Op-doc schema deltas you should bake in (you author op-docs - this is your lever)

- Per-brief plan signal: add `Plan: promote | investigate` (or `design_risk: low|high`)
  to the brief table + detail. Default promote; high-risk/L-effort design briefs get a
  plan pass. This makes promote selective instead of global.
- Per-brief machine-runnable `Verify:` block: the exact commands the op-30 gate runs
  (e.g. `npm test -- src/logic/__tests__/parser.test.ts`, `npm run build`). Acceptance
  checkboxes stay prose-for-humans; Verify is the deterministic gate input. A gate can't
  run commands a brief doesn't name.
- Required "Failure modes / wrong implementations" section for every high-risk brief.
  Brief 08 already does this (regex-parser trap, precedence trap, cycle trap) and it is
  the single cheapest quality lever - it pre-loads the reviewer's rubric and warns the
  implementer. Generalize it.
- Cohesion grouping signal: make explicit which briefs should batch warm together (today
  inferred from Deps). This drives the warm-session boundary.

## 5. Invariants the plan must not violate

- Chain dispatch is strictly sequential (dispatcher pinned to 1); ordering comes solely
  from declared Deps -> Plane blocked_by. An omitted dep is a wrong-order run, not a lost
  optimization (addendum is right about this; keep it).
- AOT discipline: any new serialized type needs source-gen JSON context; keep the
  release-gate Acceptance checkbox convention.
- Don't weaken review/rework to paper over worker capability.

## 6. Open decisions the plan must resolve (call these out)

- How does a hard brief get its design: (a) reintroduce a per-brief investigate/plan
  pass, or (b) require YOU (op-doc author) to carry the full design in the brief so
  promote still holds, or (c) a cheap "design-only" thinking pass that outputs the
  approach (AST shape, precedence) and nothing else? (b) best fits the addendum and your
  leverage but can't do codebase-specific investigation; (a) is heavier; (c) is a middle
  path. Decide and update the addendum accordingly.
- promote-vs-investigate heuristic: keyed on Effort=L? on an explicit design_risk flag?
  operator override?
- Rework cap: make MaxReworkRounds configurable and pick a default; possibly higher for
  design_risk:high briefs.
- Landing on partial failure: keep all-or-nothing, or land the completed dependency-safe
  prefix so a single stuck leaf doesn't strand finished work?
- Gate-failure behavior mid-warm-session: stop at the failing brief with prior briefs
  committed (graceful), per op-31's partial-failure design.

## 7. Reconcile the addendum

The addendum currently presents "promotion = no planning pass" as absolute. Update it so
promote is the default and high-risk briefs are the documented exception (whichever of
6's options you choose). The rest of the addendum (deps load-bearing, briefs
implementation-ready, lean on carried-forward context) stays - it is correct and the run
confirmed it.
