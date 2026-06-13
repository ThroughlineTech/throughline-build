# PROMPT

The verbatim prompt that produced the `state-of-the-system` doc set in this directory, the interpretation notes for how it was answered, the doc set listing, and the refresh history.

---

## Verbatim prompt

```
# State of the System - Repo Documentation Prompt (generic)

A drop-in prompt for producing a code-true "state of the system" doc set for
any repository. Fill the CONFIG block, then paste CONFIG + PROMPT into a
session opened at the repo root.

The agent produces the doc set **and** a `PROMPT.md` that records exactly
what it was given, so the set stays reproducible and updatable on later runs.

---

## How to use

1. Fill the CONFIG block. Delete any line that does not apply.
2. Paste the CONFIG block followed by the PROMPT body into the session.
3. On a later refresh, paste the same (or amended) CONFIG + PROMPT. The agent
   reads the existing `PROMPT.md`, updates docs in place, and appends to the
   refresh history. If the prompt text itself changed, it updates the verbatim
   copy in `PROMPT.md` and notes what changed.
4. Between refreshes, any agent whose change alters a documented surface
   updates the affected doc sections in the same change set as the code,
   following "Keeping the set current (update-as-you-go)" in the PROMPT body.
   Full refreshes reconcile drift; update-as-you-go keeps the set trustworthy
   in between.

---

## CONFIG

```
Repo:            latticeflow
Main focus:      "even coverage, no single focus"
Related sets:    "none"
Must-answer Qs:  "none, derive them"
Output dir:      docs/state-of-the-system/
Notify on done:  notify me
```

---

## PROMPT

Read through the entire `<Repo>` repository as it exists today and document it
thoroughly: what is in it, what each component does, what each reads and
writes, what each expects from the workspace and from the host machine, and
how it composes with the rest of the stack.

Where a main focus is named in CONFIG, that subsystem gets the deepest
treatment - it is the load-bearing surface and the rest of the set is context
around it. Where CONFIG says even coverage, weight the docs by how much each
area actually matters to someone re-scaffolding the system.

Write it for where the repo is **today**, broken into logical sections.

### Rules

- **Code-true.** Read from source, not from existing docs or specs. Where a
  doc and the code disagree, the code wins, and note the disagreement.
- **Cite `file:line` for every claim.** No assertion about behavior without a
  reference. Every citation must carry the symbol it points at (type, member,
  config key, or section heading) alongside the line number - line numbers
  drift within days of a refresh; symbol names survive. Never emit a bare
  line-number column in an inventory table: cite the registration site once
  in the prose above the table instead.
- **Point to schemas, contracts, and registries - do not reproduce them.**
  Reference schema files, type definitions, and contract files by path. Keep
  the prose at the level of what they mean and how they are used. The same
  rule covers any enumerable surface declared in one source location -
  endpoint maps, metric catalogs, parser registries, options classes: point
  at the declaration site, summarize its shape and count, and transcribe only
  the entries the surrounding prose actually discusses. A transcribed table
  is stale the day the registry changes; a pointer is not.
- **Status-tag every command and major code path** as one of: Functional,
  Partial, Legacy, Aspirational, Broken.
- **End every section with a "loose ends" call-out** - declared but unused
  capabilities, dead references, planned-but-not-shipped behavior, known gaps.
- **Stamp freshness on every doc.** Each doc in the set carries a header line
  `Last refreshed: <date> (HEAD <sha>)`, updated whenever that doc is touched
  for any reason. A reader must always be able to bound the staleness of what
  they are reading.
- If a CONFIG question has no implementation, answer it explicitly as "not
  implemented" and name the boundary where it would live.

### Cross-reference

If CONFIG names related doc sets, mirror their depth and structure - the
reader will have all sets open side by side. Note where this repo's surfaces
connect to theirs.

### Questions the set must answer

Answer every question in the CONFIG must-answer list, each by `file:line`
cite or by explicit "not implemented + boundary named".

In addition, regardless of CONFIG, the set must answer all of the following:

1. **Inventory.** Every command / module / service / endpoint / script: what
   it does at a high level, its inputs (arguments, files read, env vars, MCP
   tools, network calls), its outputs (files written, side effects, exit
   states), and which other components it invokes.
2. **Install / build / run.** How the repo gets onto a machine and runs -
   setup scripts, package managers, build steps. What an update does, what an
   uninstall leaves behind, what the host machine must provide.
3. **External dependencies.** Services, APIs, MCP servers, and databases the
   repo requires, and which specific tools or endpoints from each. What the
   handshake looks like when a dependency is missing or unauthenticated.
4. **Configuration and environment.** Every env var, config file, and secret.
   Which are required vs optional, and which are referenced but unused.
5. **State and persistence.** Everything the repo writes over the lifetime of
   a session - files, directories, logs, scratch state, DB rows, caches -
   where it writes them, and whether they are cleaned up.
6. **Public surfaces.** APIs, CLIs, and exported interfaces other code depends
   on, with the functional state of each.
7. **Contracts with sibling repos or systems.** What this repo reads that
   another wrote, and vice versa. Where two definitions of a shared artifact
   overlap or conflict, and how each side handles the other's version.
8. **Workspace and environment assumptions.** What the code assumes about
   where it runs beyond the obvious - branch conventions, required tooling,
   OS, CI integration, stack-specific code paths in command or build bodies.
9. **Failure modes and idempotency.** For each major operation: how it fails,
   and whether re-running it is safe.
10. **Lifecycle / orchestration.** If the repo drives a multi-step process,
    the phases, the coordination protocol, and how it transitions state.

### Output

Save the docs under the CONFIG output directory (default
`docs/state-of-the-system/`). Use a numbered scheme with a `00`-prefixed
index/README that carries a short architectural map and a one-line summary of
each doc. Standalone-readable documents - one per logical section. Split into
several documents rather than one long file.

The `00` index must also carry two short standing notes:

- **"Trusting this set"** - every claim is point-in-time at the HEAD in each
  doc's header. Before relying on a claim about code that may have moved, run
  `git log <docHEAD>..HEAD --oneline -- <cited paths>` and treat claims about
  changed files as unverified until re-checked. Say plainly that a stale
  status tag (e.g. Aspirational) may have inverted since the stamp.
- **"Keeping this set current"** - a pointer to the update-as-you-go contract
  below, so an agent that lands here during feature work learns the duty to
  update affected sections in the same change set as the code.

### Keeping the set current (update-as-you-go)

The set has two maintenance modes, and both are first-class:

- **Refresh** - a session run from this prompt. Full refreshes re-verify the
  whole set against HEAD; targeted refreshes re-verify only the docs named in
  the request.
- **Update-as-you-go** - any agent whose change alters a documented surface
  updates the affected doc sections in the same change set as the code. Doc
  updates are part of the change, not deferred to the next refresh.

A change alters a documented surface when it adds, removes, or re-shapes
anything the set inventories: an endpoint or its auth requirements, a database
table or migration, a background worker, a page or route, a tool or script, a
config key or secret file, a cross-process contract - or anything that flips
a status tag (e.g. Aspirational work landing as Functional).

The update-as-you-go contract:

1. Edit only the sections the change affects; do not re-verify unrelated
   docs.
2. Update the `Last refreshed` header of every doc touched to the current
   date and HEAD.
3. Append a row to the refresh history in `PROMPT.md` with scope `targeted`,
   naming the docs touched and the change that drove the edit.
4. If landed code is tagged Aspirational, Partial, or Broken in an existing
   doc, fix the tag in the same change. An inverted status tag is the most
   damaging form of staleness, because the set claims verification.

### PROMPT.md (required deliverable)

Write a `PROMPT.md` alongside the doc set containing:

- **Verbatim prompt.** The exact CONFIG block and PROMPT body used for this
  run, copied verbatim, in a fenced block. If this is a refresh and the prompt
  text changed since the last run, update this copy and add a short note of
  what was added, removed, or reworded.
- **Document set.** A list of every document in the set with a one-line
  description, and how the set evolved (docs added or removed over time).
- **How the prompt was interpreted.** Any judgment calls made - what "main
  focus" was taken to mean, what was deliberately covered lightly, how
  ambiguous instructions were resolved.
- **Refresh history.** A table: date, branch / HEAD commit, scope (`full` or
  `targeted`), notes on what changed in that pass. Every edit to the set
  appends a row - full refreshes, targeted refreshes, and update-as-you-go
  edits made during feature work. An edit without a history row makes the
  recorded history unreliable, which defeats its purpose.

On every later run, update `PROMPT.md` in place: refresh the verbatim prompt
if it changed, update the document set list, and append a row to the refresh
history table.

### Nested AGENTS.md breadcrumbs (required deliverable)

Maintain thin, pointer-style `AGENTS.md` files at the load-bearing directories
so agents orient without grepping. These are breadcrumbs, NOT a second copy of
the doc set: keep each at roughly 20 lines or fewer, containing (a) the
directory's one job, (b) the non-obvious gotchas a `grep` would not quickly
reveal, and (c) relative links into this doc set (primarily `01-inventory.md`)
for the full picture. Do not restate per-project prose that the inventory
already owns - a breadcrumb that drifts is worse than none.

Alongside each `AGENTS.md`, write a sibling `CLAUDE.md` whose body is only a
one-line pointer plus an `@AGENTS.md` import line, so Claude Code - which
auto-loads nested `CLAUDE.md`, not nested `AGENTS.md` - picks up the same
breadcrumb. Codex and other agents read `AGENTS.md` directly.

Current breadcrumb locations (add or prune as the tree changes):

```
src/                                      - dependency order, AOT discipline, index pointer
src/ThroughlineBuild.Cli/                 - verb-dispatch maze, arg pre-passes, adding a verb
src/ThroughlineBuild.Contracts/           - interfaces only, no-I/O rule
src/ThroughlineBuild.Workers.Common/      - WORKER_RESULT envelope + fenced-block protocol
src/ThroughlineBuild.Workers.ClaudeCode/  - vendor-worker template, adding a vendor
src/ThroughlineBuild.Briefs/Templates/    - per-agent templates, LF/snapshot trap
src/ThroughlineBuild.Plane/               - sole ITicketing, snapshot cache, throttle
src/ThroughlineBuild.Phases/              - phases + multi-ticket orchestration
src/ThroughlineBuild.Scaffold/            - op-doc parsing + profile derivation
src/ThroughlineBuild.Verification/        - gate provers, stack-agnostic rule
tests/                                    - AOT-switch discipline, shared doubles, snapshots
```

On every refresh, re-verify each breadcrumb against HEAD and correct drift the
same way the numbered docs are refreshed; record the pass in the refresh
history. The root `AGENTS.md` (ticket workflow, written by `/ticket-install`)
and the root `CLAUDE.md` are out of scope - do not overwrite them.

### Voice

Professional. No marketing prose, no "we" voice, no emoji. Plain technical
prose, `file:line` references throughout.

### On completion

<Notify on done - e.g. "notify me when you're done", or omit.>
```

---

## Document set

| File | Description |
|---|---|
| `00-index.md` | Architectural map + one-line summary per doc + status legend. |
| `01-inventory.md` | The 21 CLI verbs (incl. `setup`, `sweep`, `models refresh`, `op-doc`), 19 src projects (1 entry + 18 libraries), 2 AOT tools (`token-audit`, `analyze-event-log`), scripts and CI - what each is, what it reads/writes, status. |
| `02-install-build-run.md` | Toolchain, `build.sh`, `dotnet publish` flow, host requirements, update / uninstall. |
| `03-external-dependencies.md` | Plane REST API, Anthropic REST API, `claude` CLI, NuGet packages, handshake on missing dependency. |
| `04-configuration.md` | `.build/config.toml` sections, env vars, secrets, precedence. |
| `05-state-and-persistence.md` | `.build/`, `.worktrees/`, Plane writes, in-process caches, cleanup. |
| `06-public-surfaces.md` | CLI surface (incl. the tiered help system), library-level public types, stability call-outs (`WORKER_RESULT` envelope + fenced blocks + `COMPLETION_CLAIM`, marker comments, JSONL schema). |
| `07-contracts.md` | Inter-project contracts within this repo, shared artifacts with Plane / Claude Code / the older claude-config flow. |
| `08-workspace-assumptions.md` | Branch conventions, required tooling, OS specifics, CI matrix, worktree-aware behavior. |
| `09-failure-modes.md` | Per-phase failure modes, idempotency posture, cross-cutting failure modes. |
| `10-lifecycle-orchestration.md` | The state machine, per-phase step sequences (incl. `GatePhase`), integration-branch chain traversal, batch implement, chain rework loop (`MaxReworkRounds = 2`), event kinds emitted. |
| `11-llm-architecture.md` | The two LLM layers - the wired four-vendor worker layer (`IWorkerAgent`) and the built-but-unwired model-client layer (`ILlmClient` / `IModelClient`) - vendor-specific code map, what it takes to add a new provider. Added 2026-05-28 by request to support multi-provider planning; rewritten 2026-05-30 once the multi-provider worker set actually landed. |
| `PROMPT.md` | This file. |

### Nested breadcrumb files (outside this directory)

Thin pointer files maintained as a required deliverable (see "Nested AGENTS.md
breadcrumbs" in the prompt). Each directory below carries an `AGENTS.md` plus a
sibling `CLAUDE.md` (`@AGENTS.md` import shim): `src/`,
`src/ThroughlineBuild.Cli/`, `src/ThroughlineBuild.Contracts/`,
`src/ThroughlineBuild.Workers.Common/`, `src/ThroughlineBuild.Workers.ClaudeCode/`,
`src/ThroughlineBuild.Briefs/Templates/`, `src/ThroughlineBuild.Plane/`,
`src/ThroughlineBuild.Phases/`, `src/ThroughlineBuild.Scaffold/`,
`src/ThroughlineBuild.Verification/`, `tests/`. They point back into this set
and are re-verified against HEAD on each refresh.

Set evolution:
- 2026-05-28 - initial publication (12 documents: 00-index, 01-10, PROMPT.md).
- 2026-05-28 - added `11-llm-architecture.md` after operator request for a multi-provider planning document. No existing docs were modified except this `PROMPT.md` and `00-index.md` to link the new entry.
- 2026-05-30 - full code-true refresh against HEAD `68d6fa2` (the baseline `164e733` was ~150 commits behind). No documents added or removed; the same 13-file set was rewritten in place. The multi-provider planning anticipated by `11` shipped at the worker layer in the interim, so `11` flipped from "Codex/Gemini Aspirational" to "four agents Functional and wired", and the new Aspirational item became the unwired `IModelClient` layer.
- 2026-06-02 - added the "Nested AGENTS.md breadcrumbs" deliverable to the verbatim prompt and created the first set of breadcrumb files (9 `AGENTS.md` + 9 `CLAUDE.md` shims listed above). This is the first change to the verbatim PROMPT body since publication: a new required-deliverable subsection was appended; no existing prompt rules were reworded or removed. No numbered docs were added or rewritten.
- 2026-06-01 - code-true refresh against HEAD `e8d9a95` (52 commits past `68d6fa2`). No documents added or removed; the same 13-file set was updated in place. Major deltas absorbed: the new `settarget` verb and `[work].target_branch` config; target-branch-aware ship (configurable merge destination, preflight wrong-branch guard, `MainAutoRebased` -> `TargetAutoRebased`, progress/`--debug` stderr output); the op-27 fenced-block payload protocol (parser pre-pass + `FencedBlockResolver` + new AOT `MarkdownRenderer`, migrating plan/implement/review/draft bodies out of JSON-string metadata); the TLB-366 per-run Plane issue snapshot cache with `next_page_results` pagination and write-through updates (throttle re-confirmed at 40/min); the AOT ILC OOM mitigation; TLB-329 sibling-`blocked_by` dependency-ordered parent-chain levels with the `--max-parallel` override; and state-aware implement guidance.
- 2026-06-02 - code-true refresh against HEAD `420d9c4` (57 commits past `e8d9a95`). No documents added or removed; the same 13-file set was updated in place and all 9 breadcrumb `AGENTS.md` files re-verified against HEAD. Major deltas absorbed: the net10 SDK upgrade across all 19 projects (and CI/build.sh paths); the new `user-guide` verb (TLB-322) and interactive `build init` prompting (TLB-370); the op-29 chain rework (one shared `chain/{slug}` worktree per parent chain with `ticket/{id}`-only child branches, `ParallelDispatcher` concurrency hard-pinned to 1 so dispatch is serial and the `--max-parallel`/`ForceParallel` surface is gone, removal of `TicketDependencyGraph` leaving the single live `TicketGraph`, working-tree hygiene gates before implement/chain/ship plus post-phase cleanliness validation, the repo-global `git stash` ban for workers and verifiers, chain-commit-range handoff into the implement brief, per-phase START notices, sibling lowest-number-first ordering, and chain resume of Planning/InProgress states); ship target/push changes (`--no-push` local-only merge, baseline-aware regression checks, unconditional detached-HEAD guard); freshest-marker-by-timestamp selection and review-attributes-to-HEAD (TLB-412/414); config validation (unknown-key warnings TLB-405, required-field annotations TLB-369); the PlanPhase promote path (TLB-374, `--from-brief`); and the default-agent split (shipped template/`build init` default `claude-code`, checked-in operator `.build/config.toml` default `codex`). The verbatim prompt above was unchanged from the prior run.
- 2026-06-11 - full code-true refresh against HEAD `3a73eb9` (~250 commits past `420d9c4`). No numbered docs added or removed; all 13 files updated in place. The verbatim PROMPT body changed upstream on 2026-06-10 (commit `202ba8d` to `state-of-the-system-prompt.md`): symbol-bearing citations (no bare line-number table columns), registry-pointer rule for enumerable surfaces, per-doc `Last refreshed` freshness headers, two standing notes in the `00` index ("Trusting this set", "Keeping this set current"), a new "Keeping the set current (update-as-you-go)" section, and a `scope` column in the refresh history. All of these were applied across the set this pass, and the scope column was backfilled onto prior rows. Two breadcrumb pairs added (`src/ThroughlineBuild.Scaffold/`, `src/ThroughlineBuild.Verification/`), bringing the breadcrumb set to 11 directories.

---

## How the prompt was interpreted

**CONFIG resolution.**

- **Repo: `latticeflow`** - taken to be the repository at the cwd. The product housed in it is "Throughline Build" (the architecture doc gives both names); both names are used in the prose where context warrants.
- **Main focus: `"even coverage, no single focus"`** - taken to mean each must-answer question gets its own doc, with weighting by load-bearing surface. The Cli + Phases + Plane + Workers axis got the longest treatment because that is what a re-scaffolder would need to reproduce most carefully; the smaller libraries (Helpers, JudgmentSlots, Anthropic) got proportionally shorter coverage.
- **Related sets: `"none"`** - no cross-reference to other doc sets was attempted. The doc set assumes the reader has only this repository open.
- **Must-answer Qs: `"none, derive them"`** - taken to mean: answer only the ten universal questions in the PROMPT body, no additions.
- **Output dir: `docs/state-of-the-system/`** - used verbatim.
- **Notify on done: `notify me`** - executed at the end of the run via `bin/notify` per the user's global `CLAUDE.md` convention.

**Doc structure choice.** A numbered scheme with `00-index` was chosen per the spec. The ten universal questions map cleanly to `01` through `10`, in the order the prompt lists them. The architectural map and status legend live in `00`.

**Code-true judgments.**

- The architecture document [docs/throughline-build-architecture.md](../throughline-build-architecture.md) is a forward-looking proposal dated 2026-05-21 - it names a number of components that do not exist in the source today (OpenAI / Google LLM clients, Codex / Gemini workers, GitHub ticketing adapter, `install` verb, MCP server packaging, replay verb). Where the architecture and the code disagree, the code wins, and the discrepancy is called out as a "loose end" in the relevant doc.
- Status-tagging was applied to verbs, library projects, and individual public-surface types. The bar for "Functional" was: implemented end-to-end, exercised by the test suite, and not marked TODO. The bar for "Partial" required a real stub (`NotImplementedException`) or unwired plumbing.

**Citation style.** Every assertion about behavior carries a `file:line` reference rendered as a markdown link to the source path with `#L<n>` anchor (VSCode-friendly). Where a reference covers a small range, `#L<start>-L<end>` is used. The convention is consistent across the set.

**Deliberately covered lightly.**

- Individual test bodies are not described; the test suite shape is summarized in [01-inventory.md](01-inventory.md). Each phase's per-test behavior would multiply the set's size without giving a re-scaffolder more leverage.
- Per-file private helpers are mentioned only when they are the load-bearing piece (e.g., `MarkerParser`, `SlugBuilder`, `WorktreeDecrufter`). Helpers without production callers (`DocOnlyDetector`, `DriftComparator`) are flagged but not detailed.
- The op-doc narrative chain under `docs/op-docs/` is summarized briefly in [01-inventory.md](01-inventory.md) but not unpacked op-by-op - those are historical execution plans, not current contracts.

**Ambiguities resolved.**

- The architecture mentions "MCP tools" as an invocation surface (the binary as an MCP server). No such code path exists today; [03-external-dependencies.md](03-external-dependencies.md) names this explicitly under "Architecture-named services that are not yet wired".
- "Backend" in `.build/config.toml` is read but only `"plane"` is meaningfully supported. The doc set surfaces this as a loose end in [04-configuration.md](04-configuration.md).
- The `Phase.Command` enum value is documented as "used by `ITicketCommand` implementations for `WorkflowEvent.Phase` when no specific workflow phase applies" - that is the observed usage, not a documented intent.

**2026-05-30 refresh judgment calls.**

- **No new documents.** The interim work was large (multi-provider workers, multi-ticket/tree chain, `decompose`/`init`/`list` verbs, divergence/auto-rebase, obsolete-claim ratification) but it all maps onto the existing ten-question structure. Multi-ticket and tree-aware orchestration was folded into `10-lifecycle-orchestration.md`; the worker fan-out into `11-llm-architecture.md`. Splitting out a new doc would have broken the clean question-to-doc mapping for no reader benefit.
- **Two multiplicities, two maturities.** The single most important code-true correction this pass: the *worker* layer (agent CLIs) is genuinely multi-vendor and wired, but the *model-client* layer (`IModelClient`/`AnthropicModelClient`) is built and tested yet constructed on no production path. The docs now state this distinction explicitly rather than treating "multi-provider" as one undifferentiated effort.
- **Slash-command flags vs CLI flags.** Several flags named in operator-facing slash-command docs (`--n`/`--no-promote` for decompose, `--all`/`--feature` for list, `--sequential`/`--ship`/`--in-given-order` for chain) are not parsed by the `build` binary; only the flags in `CliUsage.cs` are real. The inventory doc records this gap rather than documenting the slash-command surface as if it were the CLI's.
- **Duplicate-type call-outs.** Two `TicketGraph` types (a `Contracts` record built by `TicketDependencyGraph`, test-only; and a `Phases` class consumed by the live dispatcher) and two `Size` enums are documented as overlaps in `07-contracts.md` rather than silently picking one.
- **Architecture-doc drift.** `docs/throughline-build-architecture.md` is now further from the code than at first publication (it still posits an `install` verb, local-merge-only ship with no push, a 9-value `Phase` enum, and `ClaudeCodeReviewer`). Each disagreement is flagged as a loose end in the relevant doc; the code wins.

**2026-06-01 refresh judgment calls.**

- **No new documents.** The interim work (settarget + target-branch ship, the op-27 fenced-block protocol, the Plane snapshot cache, AOT OOM mitigation, sibling-dependency parent chains) all maps onto the existing ten-question structure. `settarget` is a config-editing verb, so it landed in `01`/`04`/`06`/`10` rather than a new doc; the fenced-block protocol is a worker-output contract folded into `06`/`07`/`11`; the snapshot cache into `03`/`05`/`09`. Keeping the clean question-to-doc mapping was worth more than a new file.
- **Shifted `PlaneTicketingClient.cs` cites.** TLB-366 grew the file ~+300 lines, so many cites in `03`/`05` moved. The snapshot/pagination/throttle references were re-verified against HEAD and corrected; a handful of untouched prose cites into unrelated methods (e.g. `RollupParentAsync` internals) were left at their prior neighborhood rather than re-paginated wholesale - flagged here so a future pass knows they were not individually re-checked.
- **`settarget` exit codes fold into the global scheme.** It returns 0 / 2 (config-not-found or branch-not-found), which matches the global "2 = config error" code, so no per-verb override section was added to `06`.
- **Stale CLI usage text.** `CliUsage.cs:12` still describes ship as "local fast-forward merge, no push to remote", which the code contradicts (ship pushes). This is recorded as a discrepancy in `06`/`08` (the never-push convention was removed); the usage string itself is a source bug, not a doc one.
- **Verbatim prompt unchanged** from the prior run (same CONFIG + PROMPT body).

**2026-06-02 refresh judgment calls.**

- **No new documents.** The 57-commit interim was large (the op-29 chain refactor, net10, two new verb behaviors, ship target/push work, config validation) but every delta maps onto the existing ten-question structure. The chain rework folded into `10`; its failure modes into `09`; the worktree/branch-naming and stash constraints into `05`/`08`; config validation into `04`; the new verbs into `01`/`02`/`06`. The clean question-to-doc mapping was kept.
- **Project count was already correct.** The five projects an earlier premise suspected were new (`Commands`, `EventLog`, `Git`, `Scaffold`, `Verification`) all existed at `e8d9a95`; nothing was extracted or added in this window. The set stayed at 19 (1 entry + 18 libraries).
- **"Parallel" dispatch is now a misnomer in the code, kept as a type name in the docs.** `ParallelDispatcher` is hard-pinned to concurrency 1 and the parent-chain path bypasses it with its own `SemaphoreSlim(1,1)`; the docs describe the dispatch as serial while still naming the `ParallelDispatcher` type that implements it, and explain that width 1 is what eliminates the worktree races the parallel surface used to risk.
- **Two contract overlaps re-checked.** The "two `TicketGraph` types" overlap flagged in prior refreshes is now resolved - `TicketDependencyGraph` was deleted, leaving the single live `TicketGraph`; `07` records the resolution. The two `Size` enums (`Size`, `WorkerSize`) still coexist and remain documented as an overlap.
- **Default-agent split stated both ways.** The shipped template and `.example` (and therefore `build init`) still default to `claude-code`; the checked-in operator `.build/config.toml` defaults to `codex`. There is no hardcoded vendor default in C# (`default_agent` is required config). The docs state both rather than picking one, and flag the template-vs-live drift as a loose end in `04`.
- **Stale `CliUsage.cs` ship string re-checked.** The prior refresh recorded `CliUsage.cs:12` as wrongly describing ship as local-only/no-push; at HEAD that string has been corrected (it documents push plus `--no-push`). The remaining documented disagreement is `docs/throughline-build-architecture.md`, which still posits never-push semantics; the code wins.

**2026-06-11 refresh judgment calls.**

- **Prompt-source merge.** The generic prompt file ([state-of-the-system-prompt.md](state-of-the-system-prompt.md), updated 2026-06-10 in `202ba8d`) now carries `Repo: tradetrack2` in its CONFIG - it was re-filled for use on another repository. The CONFIG in this set's verbatim copy stays `latticeflow`; only the PROMPT body changes were adopted. The "Nested AGENTS.md breadcrumbs" subsection, which is latticeflow-specific and was never in the generic source, is retained in this set's verbatim prompt.
- **No new documents.** The ~250-commit interim was the largest yet (the gate ecosystem, the integration-branch chain rework, batch implement, four new verbs, the tiered help system, the connected `init` bootstrap, the worker telemetry layer) but every delta maps onto the existing ten-question structure: the gate into `09`/`10`, its contracts into `06`/`07`, the new verbs into `01`/`02`/`06`, config growth into `04`, worktree/sweep lifecycle into `05`/`08`, worker changes into `03`/`11`.
- **Parallel sub-agent execution.** This pass was executed by six concurrent sub-agents over disjoint doc groups, with cross-doc facts (EventKind 14, Phase 11, ChainOutcome 20, chain exit codes 0-11, plan promote-by-default, the `default_agent` re-convergence to `claude-code`) reconciled centrally before the index and this file were written. One agent died mid-run on a transient API error and was resumed; its first pass had completed `10` and not touched `11`, and both were verified complete after the resume.
- **Scope-column backfill.** The new refresh-history `scope` column was backfilled onto prior rows: the two whole-set passes and the two code-true refreshes as `full`, the `11-llm-architecture.md` addition and the breadcrumb-creation pass as `targeted`.
- **`CliUsage.UsageText` flipped to Legacy.** The tiered help registry under `src/ThroughlineBuild.Cli/Help/` is what `build help`/`-h` actually serves; `CliUsage.UsageText` has zero production references and already lags the code (it documents chain exit codes only through 9 while `ChainExitCodeMapper` emits 10 and 11). Recorded as a source-side staleness, not a doc one.
- **Untracked Linear debris.** `src/ThroughlineBuild.Linear/` (and two test directories) exist on disk as untracked `bin/`/`obj/` output only - no tracked sources, not in the solution. Documented as debris with the project count held at 19 rather than counted as a 20th project.
- **`ParentHasGrandchildren` tagged Legacy.** The enum value, exit mapping, and triage text survive, but the depth-capped recursion (`MaxDepth=16`) means the outcome is no longer produced. The docs keep the value documented (it is public surface) and tag the path Legacy.

---

## Refresh history

| Date | HEAD commit | Scope | Notes |
|---|---|---|---|
| 2026-05-28 | `164e733` on `main` | full | First publication. 12 documents created (00-index, 01-10, PROMPT.md). Doc set built from `Program.cs`, the 14 `ThroughlineBuild.*` projects, `tests/`, `docs/throughline-build-architecture.md`, `.build/config.toml.example`, `.claude/plane-config.md`, `.claude/ticket-config.md`, `.github/workflows/build.yml`, `build.sh`, `.gitignore`, `.gitattributes`. |
| 2026-05-28 | `164e733` on `main` | targeted | Added [11-llm-architecture.md](11-llm-architecture.md) at operator request: a dedicated map of LLM interfaces (`ILlmClient`, `IWorkerAgent`), vendor-specific code locations, and the architectural choices required to add a second provider. Updated `00-index.md` and this `PROMPT.md` to reference the new doc. No other docs touched. |
| 2026-05-30 | `68d6fa2` on `main` | full | Full code-true refresh; baseline `164e733` was ~150 commits behind. All 13 files rewritten in place against HEAD source (no docs added/removed). Major code deltas absorbed: project count 14 -> 19 (`ModelClient`, `Workers.Common`, `Workers.Codex`, `Workers.Gemini`, `Workers.Copilot`); four wired worker agents replacing the claude-only worker (`WorkerAgentFactory`, per-agent config/sizes/templates, `WorkerSize`); `ClaudeCodeReviewer` -> `WorkerAgentReviewer`; `WorkerResultParser` relocated to `Workers.Common`; new verbs `decompose`/`init`/`list`; multi-ticket and tree-aware chain (`ParallelDispatcher`, `TopologicalSorter`, `AncestorSkipFilter`, parent recursion, all-children-Done ship gate, cascade close/defer); obsolete-claim ratification (`ObsoleteRatifier`, `TicketSubsumed`); divergence probe + auto-rebase + push-after-FF (`DivergenceState`, `MainAutoRebased`, `MainWorktreeLock`); Plane surface extensions (`QueryAsync`/`TransitionLifecycleAsync`/`UpdateDescriptionAsync`/`CreateChildTicketsAsync`, issue-type name->UUID, `RequestThrottle` 60/min, `?next_path=` deep-link); `EventKind` 9 -> 13, `Phase` 9 -> 10; `ANTHROPIC_API_KEY` hard gate removed (lazy `LlmClientFactory`); `IModelClient`/`AnthropicModelClient` SSE streaming built and tested but unwired. The verbatim prompt above was unchanged from the prior run. |
| 2026-06-02 | `80b07a3` on `main` | targeted | Breadcrumb pass (no numbered-doc rewrite). Added the "Nested AGENTS.md breadcrumbs" required-deliverable subsection to the verbatim prompt and created the first breadcrumb set: 9 `AGENTS.md` files (`src/`, `Cli`, `Contracts`, `Workers.Common`, `Workers.ClaudeCode`, `Briefs/Templates`, `Plane`, `Phases`, `tests/`) each with a sibling `CLAUDE.md` `@AGENTS.md` import shim so Claude Code loads them. First change to the verbatim PROMPT body since publication; the change is additive (new subsection only). Root `AGENTS.md`/`CLAUDE.md` left untouched (ticket-workflow files). |
| 2026-06-01 | `e8d9a95` on `main` | full | Code-true refresh; 52 commits past `68d6fa2`. All 13 files updated in place (no docs added/removed). Major code deltas absorbed: new `settarget` verb (`SetTargetCommand`, dispatched pre-config-load) + `[work].target_branch` config + `BuildConfig.ResolveTargetBranch()`; target-branch-aware ship (`ShipOptions.TargetBranch`, target-aware `BaseRefResolver`, preflight `wrong_worktree_branch` guard, `MainAutoRebased` -> `TargetAutoRebased` rename at ordinal 10, FF-merge+push to the configured target, `--debug`/progress stderr output, worktree create-from-local-branch fallback); op-27 fenced-block payload protocol (TLB-333..342: `WorkerResultParser` fenced pre-pass + `FencedBlockResolver`, new AOT `MarkdownRenderer` in `Workers.Common`, `WorkerResult.Blocks`, plan/implement/review/draft bodies migrated to `PLAN_BODY`/`IMPLEMENT_SUMMARY`/`REVIEW_CRITIQUE`/`DRAFT_BODY` blocks via `*_ref` metadata); TLB-366 per-run Plane issue snapshot cache (`_seqToUuid`/`_issueByUuid`, single-flight load, write-through `AddOrUpdate`, `next_page_results` pagination, loud truncation warning at `MaxListPages=50`, `KeyNotFoundException` catch in the chain batch path) - throttle re-confirmed at 40/min; AOT ILC OOM mitigation (`IlcOptimizationPreference=Size`, `IlcMaxParallelism=1`, `RunAsync` split); TLB-329 sibling-`blocked_by` dependency-ordered parent-chain levels + `--max-parallel`/`ForceParallel` override; state-aware implement guidance on non-Ready tickets; embedded-but-unsurfaced user-guide template (TLB-320). `EventKind` stays at 13 (rename only), `Phase` stays at 10. Verbatim prompt unchanged. Note: `PlaneTicketingClient.cs` cites in `03`/`05` shifted ~+300 lines; snapshot/pagination references re-verified, some untouched prose cites left at prior neighborhood. |
| 2026-06-02 | `420d9c4` on `main` | full | Code-true refresh; 57 commits past `e8d9a95`. All 13 files updated in place (no docs added/removed) and all 9 breadcrumb `AGENTS.md` files re-verified against HEAD. Major code deltas absorbed: **net10 upgrade** (97e6a87) retargeting all 19 `csproj` to `net10.0` (CI `setup-dotnet 10.x`, `build.sh`/CI publish from `net10.0/` paths); **new `user-guide` verb** (TLB-322, `UserGuideCommand`/`UserGuideLoader`, writes `docs/throughline_build_userguide.md`) and **interactive `build init`** (TLB-370, TTY-gated prompting); the **op-29 chain rework** - one shared `chain/{slug}` worktree per parent chain with in-place `ticket/{id}`-only child branches (TLB-408), `ParallelDispatcher` concurrency hard-pinned to 1 (dispatch now serial; `--max-parallel`/`ForceParallel` removed), `TicketDependencyGraph` deleted (single live `TicketGraph` + `TopologicalSorter` remains, resolving the prior two-type overlap), `WorkingTreeHygieneGate` preflight before implement/chain/ship + post-phase cleanliness validation (TLB-396/400/402/407), repo-global `git stash` ban for workers and read-only verifier, chain-commit-range handoff into the implement brief (`ChainCommitRange`/`HandoffPointerEnabled`), per-phase START notices (TLB-415), sibling lowest-number-first ordering (TLB-397), children stacking on the accumulating base (TLB-411), and chain resume of Planning/InProgress states (652682d); **ship** `--no-push` local-only merge + `[ship].push` (TLB-409), resolved-target surfacing + `[work].target_branch` validation (TLB-410), baseline-aware regression checks (TLB-401, `.worktrees/baseline-{sha}`), unconditional detached-HEAD guard (TLB-402); **markers** freshest-by-timestamp selection (TLB-412) and review-attributes-to-worktree-HEAD (TLB-414, `implemented_at_superseded`); **config** unknown-key warnings (TLB-405), required-field annotations (TLB-369), `[plan].mode` promote + PlanPhase promote path (TLB-374, `--from-brief`), `[llm] default_model` deprecated for worker-model selection; **close/defer/reopen** degrade to verbatim reason via `EchoLlmClient` when no LLM key (TLB-371); **default-agent split** (template/`build init` -> `claude-code`, checked-in `.build/config.toml` -> `codex`, 420d9c4). `EventKind` stays at 13, `Phase` stays at 10. Verbatim prompt unchanged. Note: `Config.cs` cites in `04` shifted ~+250 lines and `Program.cs` cites moved throughout; changed cites were re-verified against HEAD, a few untouched-prose cites left at prior neighborhood. |
| 2026-06-11 | `3a73eb9` on `main` | full | Full code-true refresh; ~250 commits past `420d9c4` (~38k insertions). All 13 files updated in place; all 11 breadcrumb `AGENTS.md` re-verified (2 pairs added: `Scaffold`, `Verification`). Major code deltas absorbed: **CLI 17 -> 21 verbs** (`setup` TLB-369-adjacent provisioning incl. `WorkspaceSchema` states/labels + git init + welcome commit; `sweep` TLB-531 merged-gated worktree/branch recovery via `ChainWorktreeSweeper`; `models refresh` rewriting `[workers.codex.sizes]` via `CodexModelProbe`/`CodexTierMapper`; `op-doc spec|new` via `OpDocSkeletonGenerator`), `-V/--version` (`BuildVersion.Current` stamped by MSBuild), and the **tiered help system** (`HelpRegistryFactory`/`Tier0Renderer`/`Tier1Renderer` + 4 topics; `CliUsage.UsageText` flipped Legacy, test-only, lags exit codes 10/11); **the gate ecosystem** - `GatePhase` (TLB-506) between implement and review in the chain, `CompletionClaim` (TLB-500/505, `CompletionClaimParser`, `completion_claim_ref` fenced block), consumes-provides preflight (TLB-507), smoke signals (TLB-503, `SmokeCollector`), `GateVacuityProver` canaries (vacuous = hard-fail), `GateControlProber` base-ref control runs classifying environment failures (TLB-538), structured-failure-to-rework (TLB-509) with `FailedCheckDetails` evidence persisted in `VerifierVerdict` (7af36fb), advisory checks excluded from rework (d30dbac) and from the ship regression gate (22a79ab); **chain rework** - integration-branch model (`chain/<slug>` accumulates child ships, root landing via `LandRootIntegrationBranchAsync`, TLB-546 stale-branch refresh), depth-capped recursion (`MaxDepth=16`) retiring `ParentHasGrandchildren` (now Legacy), `--batch-implement` (`BatchImplementBriefBuilder`/`BatchReviewBriefBuilder`/`BatchCommitVerifier`, wiring fixes e76ac5d), sweep-on-success/preserve-on-failure, `ChainPhaseComposition` + `ChainExitCodeMapper` (new exit codes 8 GateVacuous, 9 ReviewUnavailable, 10 GateEnvironmentFailure, 11 TicketingUnavailable), `RefusedWrongBranch` preflight, `MaxCheckRetriesPerReworkRound=2` check-recheck loop (`MaxReworkRounds` still 2); **Plane** - client now implements `ITicketing`/`ITicketingProvisioner`/`ITicketingConnectivity`/`IProjectDiscovery`, transport retry + environmental classification raising `TicketingUnavailableException` (TLB-545), `ProjectResolver`; **connected `build init`** (creds file via `CredsFileParser`, create-or-pick project menu, `WelcomeCommit`, Codex tier probe); **config** - `ModelTier` sizes hard-break (`{ model, effort }` inline tables), `[batch]`, `[review].verify_gate_vacuity`, check `role`/`canary`, `[project].convention_files`/`preload_context`/`context_hygiene`, TLB-512 undefined-agent and TLB-544 claude-code-model fail-fast at load, `.build/config.toml.example` deleted, `default_agent` re-converged to `claude-code` (prior split resolved); **workers** - full-transcript `WORKER_RESULT`+fenced-block parsing (945f4b4), blocks+envelope in one final message (3cbf64c), `ClaudeCodeTurnParser`/`ClaudeCodeModelValidator`/`WorkerTranscriptWriter` (`WorkerProgressDigest` deleted), codex `exec --json -` stdin invocation, `ProviderErrorClassifier` -> `ReviewUnavailable` (TLB-527), `ProcessStreamEncoding` UTF-8 pinning, `WorkerDiagnostics`; **plan promote is the default mode** (no worker spawned; investigate is opt-in); **telemetry** - `CostLedger` event kind (TLB-510), `context_attribution`/`preload_summary`, per-turn usage (d484d2a), analyze-event-log claude-fable-5 pricing + all-chain aggregation preferring the pricing table (TLB-547); **implement preload** - `PreloadedContextBuilder` convention/named-input inlining with preload events. Counts: `EventKind` 13 -> 14, `Phase` 10 -> 11, `ChainOutcome` 20, projects stay 19 (`ThroughlineBuild.Linear/` is untracked debris). The verbatim PROMPT body changed this pass (see set evolution); its new rules (symbol-bearing cites, registry pointers, freshness headers, index standing notes, update-as-you-go contract, scope column) were applied set-wide. |
| 2026-06-12 | `130e61a` on `heartbeat` | targeted | Update-as-you-go refresh for heartbeat Stage 02. Re-verified and updated [04-configuration.md](04-configuration.md) and [11-llm-architecture.md](11-llm-architecture.md) for the Claude-only `transport` config and internal print transport seam; clarified that misplaced `transport` keys on non-Claude workers warn and are ignored. |
| 2026-06-13 | `493cca0` on `heartbeat` | targeted | Update-as-you-go correction for heartbeat Stage 03. Updated [11-llm-architecture.md](11-llm-architecture.md) to record the landed correlated run-directory identity, versioned atomic completion store, duplicate-event handling, cancellation-aware reader, hidden `internal claude-stop-hook` CLI dispatch, and per-run settings builder; kept only the interactive Claude process host deferred to Stage 4. |
| 2026-06-13 | `a62cb84` on `heartbeat/stage-04-interactive-worker` | targeted | Update-as-you-go refresh for heartbeat Stage 04. Updated [04-configuration.md](04-configuration.md), [11-llm-architecture.md](11-llm-architecture.md), and the generated config template to describe the functional opt-in `interactive-hook` transport: native Windows ConPTY hosting, correlated Stop-hook completion, process-tree cleanup, existing `WorkerResultParser` reuse, debug evidence retention, and no fallback to `--print`. Kept `print` as the documented and generated default; observability parity remains deferred. |
| 2026-06-13 | `682f333` on `heartbeat/stage-06-process-hardening` | targeted | Update-as-you-go correction for heartbeat Stages 05-06 (the prior row had stamped `11` at Stage 04 and left it claiming only `last_assistant_message` parsing with observability deferred), landed across `5e0060f` (initial rewrite) and `682f333` (CI-pending wording + the Unix disposal-drain note). |
| 2026-06-13 | `cf3fe2b` on `heartbeat-stage-06-process-hardening` | targeted | Recorded the real macOS 26.4 arm64 validation of `cf3fe2b` in [11-llm-architecture.md](11-llm-architecture.md) (its Unix runtime stamp moved from "pending CI" to "validated on macOS arm64, linux-x64 pending"): both `UnixProcessTreeCleanupTests` cases passed against real `sh -> sleep` trees with zero leftover processes, every `libc` symbol resolved, the full 2441-test solution was green, and the osx-arm64 native AOT publish was warning-free and ran. Also applied a live-test launcher fix (route `LiveSentinel` through `InteractiveClaudeProcessLauncherFactory.Create()` instead of a hardcoded Windows host, and move its `--debug` capture outside the throwaway worktree) and noted the open interactive-hook completion item (Stage 03/04 layer, not the Stage 06 PTY layer). The documented production code surface is unchanged since `682f333`, so `11`'s code stamp stays `682f333`. Rewrote the interactive-transport entry in [11-llm-architecture.md](11-llm-architecture.md) to record Stage 05 observability (the isolated `ClaudePersistedTranscriptReader`: full multi-message transcript, model identity, token/cache usage marked unavailable not invented, per-turn `context_turns`, provider-error text, redacted debug side channel; optional telemetry failure never fails a valid result) and Stage 06 process/terminal hardening (the platform terminal-host abstraction + factory; Windows ConPTY with a mandatory kill-on-close job object; the implemented Unix `posix_openpt`/`posix_spawnp` PTY host with `POSIX_SPAWN_SETSID` process-group containment, whose runtime behavior is exercised by the Linux/macOS CI tree-cleanup tests, pending this branch's first CI run, not on the Windows dev host; the bounded graceful-then-forced `ProcessShutdownSequence`; per-run `ClaudeRunLease` + lock-based `ClaudeRunDirectorySweeper`; the per-worktree `InteractiveClaudeWorktreeLock`). Only `11` touched; the generic print-path process-tree-kill prose in `03`/`09` was re-checked and remains accurate. |
