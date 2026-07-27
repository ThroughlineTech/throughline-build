# Experiment harness: process-tweak A/B protocol (hand this to an agent verbatim)

This file sets up a repeatable way to modify the ThroughlineBuild engine's process one change at
a time and measure each change against a fixed prompt class. Read it fully before doing anything.
It defines three roles - planner, implementer, reviewer - and the branch and commit discipline that
keeps every change cleanly reversible. If you were pointed at a specific `experiment N/` folder,
your job is almost certainly the IMPLEMENTER role; read the whole file anyway so you understand why.

---

## 0. What we are doing and why

ThroughlineBuild (this repo, the `build` CLI, a.k.a. Throughline Build) is a deterministic C# engine that
runs a ticket-chain build workflow: plan -> implement -> review -> ship, orchestrated by `ChainPhase`.
We are improving that engine's process through a series of small, isolated experiments. Each
experiment makes ONE coherent change, then we re-run a known prompt class (the survey-app op-doc
smoke test) and compare the run's behavior and metrics against the prior baseline. One change at a
time is the whole point: it keeps cause and effect attributable. Do not bundle unrelated changes.

The loop per experiment:

1. A feedback note (`engine-iteration/NN-feedback-*.md`) states the defect(s) to fix, with acceptance
   criteria. It is usually distilled from a run analysis under `docs/analysis/findings/`.
2. The planner deep-dives the C# source and writes `engine-iteration/NN-plan.md`: a
   detailed, file-cited implementation plan.
3. The implementer codes the plan on a fresh sub-branch, exactly as specified, and reports back.
4. The reviewer checks the diff against the plan and the acceptance criteria.
5. We run the experiment (re-run the prompt class), analyze the result, write new feedback, repeat.

---

## 0.1 The #1 design constraint: stack-agnostic output (all roles - non-negotiable)

ThroughlineBuild generates and builds target projects of ANY stack: TypeScript, dotnet, Python, Go, or
even a series of plain text documents. Its OUTPUT is stack-agnostic. Every experiment change MUST be
stack-agnostic too. A fix that only works for a TypeScript target (or any single stack) is a defect,
not a feature, and gets rejected in review.

The rule that makes this concrete: stack-specific knowledge lives in DATA - the LLM-derived project
profile (the target's `config.toml` checks, canaries, etc.) and the op-doc / derive prompt
(`src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md`) - NEVER in the engine MECHANISM
(C# code). The engine provides general mechanisms; the same LLM that derives the per-stack check
commands also derives the per-stack data each mechanism consumes. No `if (language == "typescript")`
in engine code, ever.

Feedback notes are often written in one stack's terms (because a given smoke test ran on one stack).
Do not transcribe stack-specific wording (`tsc -b --noEmit`, `vitest test.exclude`) into engine code -
translate each into a general mechanism plus derived data, with that stack as the first instance.

The one exception: the engine's OWN repo is dotnet/C# and we optimize it hard (debug the C# source).
"Agnostic" constrains what the engine GENERATES, not what the engine is WRITTEN in.

---

## 1. Branch and commit discipline (all roles - non-negotiable)

- The base for every experiment is `main` (minbatch was merged into main; main is now the shared base).
  Each experiment that changes code gets its OWN branch cut from main, named `exp-<N>-<short-slug>`
  (e.g. `exp-1-gate-output`). Create it with a clean tree: `git switch -c exp-<N>-<slug>` off main.
  Confirm with `git branch --show-current` before each commit.
- That branch is both the back-out unit and the decision unit: after the experiment runs we (humans)
  decide whether to FOLD it into main or ABANDON it (delete the branch, lose nothing). Never commit
  experiment code to main directly; never merge - folding is a separate, explicit human step.
- Planning docs (the feedback note and the plan) live on main as the shared record, committed before
  the experiment branch is cut. The experiment branch holds the code change plus the implementation
  summary (written into the experiment folder when the work is done).
- Keep each experiment's diff atomic and self-contained so `git diff main...HEAD` is exactly the change
  under test. No drive-by refactors, no reformatting unrelated files.
- Commit messages: `topic: short description` (lower-case topic), imperative mood, one logical
  change per commit. No `Co-Authored-By: Claude` trailer, no "Generated with" lines, no AI branding.
- ASCII only in everything you write - code, comments, commits, docs. No em dashes, en dashes, or
  curly quotes; plain `-` hyphens and straight quotes only. (Windows + Git Bash mangles non-ASCII.)
- Do not push unless explicitly told to. Local commits on the sub-branch are the deliverable.

---

## 2. The repo: build, test, and the traps that bite

- Test the engine: `dotnet test --nologo -v q --logger "console;verbosity=minimal"` from repo root.
- Compile-check only: `dotnet build throughline-build.sln --nologo -v q`.
- Native CLI (rarely needed for an experiment): `dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release --nologo -v q` produces `build.exe`.
- Project layout and dependency order: `src/AGENTS.md`. Architecture: `docs/state-of-the-system/`
  (written at an older commit; it flags its own drift - trust the code over the docs where they
  disagree, and prefer citing code).
- Traps:
  - Worker brief templates under `src/ThroughlineBuild.Briefs/Templates/` are snapshot-tested. If you
    edit a template, the Briefs snapshot tests will fail until you update them; edit templates as LF,
    then run the Briefs tests and update the snapshots intentionally (see `Templates/AGENTS.md`).
  - `ThroughlineBuild.Cli` is AOT (`PublishAot=true`). Anything serialized needs a source-generated
    `JsonSerializerContext`; do not add reflection-based serialization. Keep `Contracts` I/O-free.
  - The engine builds a separate TARGET repo (the scaffolded project, e.g. survey-app). The target's
    toolchain config (`config.toml` checks, tsconfig) lives in the target repo, not here. Many "the
    engine should X" fixes are actually engine-emits-or-validates-X fixes - read the plan carefully on
    which side a change lands.

---

## 3. The known prompt class (the fixed measurement target)

Every experiment is measured against the same input so runs stay comparable: the survey-app op-doc
smoke test. Reference material in this repo:

- `docs/analysis/workloads/survey-app-build.md` - the op-doc (8 briefs, 2 plans).
- `docs/analysis/findings/chain-efficiency-evidence.md` - three real baseline runs with metrics.
- `docs/analysis/method/survey-smoketest-prompt.md` and `docs/analysis/method/build-run-analysis-prompt.md` - the
  analysis prompts that turn a run's `.build/events/*.jsonl` telemetry into a comparable report.

Do not change the prompt class between experiments; that is the control. If the op-doc itself must
change to exercise a fix, that is itself a finding - call it out, do not silently edit the control.

---

## 4. IMPLEMENTER role (the usual job)

You were given an experiment number `NN` and the `docs/analysis/engine-iteration/` folder. Do this:

1. Read `engine-iteration/NN-feedback-*.md` (the intent) and `engine-iteration/NN-plan.md` (the
   spec). The plan is authoritative for WHAT to change and WHERE. Read every file:line surface the
   plan cites before editing it - do not propose changes to code you have not read.
2. Confirm `main` is current and the tree is clean, then cut `exp-<N>-<slug>` off main. For the
   orchestrating-lead variant of this role (task sub-agents, verify, rework, summarize), use the
   paste-ready brief in `docs/analysis/method/experiment-implementer-prompt.md`.
3. Implement exactly the plan. Stay inside its stated scope. If the plan is wrong, ambiguous, or
   collides with reality (a cited line moved, an assumption is false), STOP and report the conflict -
   do not improvise a different design. A wrong-but-in-scope plan is the reviewer's problem to fix;
   silent scope drift is yours and it poisons the experiment.
4. Add or update tests as the plan specifies. Run `dotnet test`; if you touched Briefs templates,
   update snapshots deliberately. The change is not done until the suite is green.
5. Commit in logical units with `topic: ...` messages. Do not merge, do not push.
6. Write a short implementation report (in your final message, or append to the plan file under a
   `## Implementation report` heading if asked): the sub-branch name, the commits, files changed,
   test result, and a line-by-line mapping of each acceptance criterion in the feedback to how the
   diff satisfies it. Flag anything you could not do and why. Then hand back for review.

Hard rules for the implementer: one experiment = one sub-branch = one coherent change; never touch
main; ASCII only; no AI branding in commits; every change stack-agnostic (section 0.1) - no
stack-specific branch in engine code, stack specifics go in derived data; verify, do not assume - if a
check or test was skipped, say so plainly.

---

## 5. PLANNER role (for reference)

Given `engine-iteration/NN-feedback-*.md`, deep-dive the C# source (read real code, cite file:line) and
write `engine-iteration/NN-plan.md` containing: the root cause of each defect; the
architecture reality that shapes the fix; a stack-agnostic design (section 0.1 - every mechanism
stack-free, every stack specific pushed into derived data, with the feedback's stack as just the first
instance); the exact surfaces to change with citations; the test strategy (including a stack-agnostic
test that proves no single-stack assumption leaked); an explicit scope boundary (what is deliberately
NOT in this experiment); risks and repo traps; the implementation/commit order; and the measurement
method (how we will tell the experiment worked against the prompt class). The plan must be detailed
enough that the implementer makes no design decisions - only mechanical ones.

---

## 6. REVIEWER role (for reference)

Given the implementer's sub-branch, diff it against the experiment branch, confirm the change matches
the plan and nothing more, run `dotnet test`, and verify each acceptance criterion against observable
behavior (not the implementer's self-report). Then decide: accept (ready to run the experiment),
rework (back to implementer with specific notes), or reject the approach (back to planner). Record the
verdict. The reviewer does not merge to main; that remains a separate human decision.

---

## 7. Running and analyzing an experiment (for reference)

After review accepts: re-run the prompt class on a fresh scaffold using the experiment build, capture
`.build/events/*.jsonl`, and produce a comparable report via `build-run-analysis-prompt.md`. Compare
against the prior baseline (and the three runs in `chain-efficiency-evidence.md`). State confounds
honestly; a "not comparable" verdict beats a confident-but-invalid winner. The delta and any new
defects become the next experiment's feedback note. Then the loop repeats.
