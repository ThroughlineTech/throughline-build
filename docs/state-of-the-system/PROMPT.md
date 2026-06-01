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
  reference.
- **Point to schemas and contracts, do not reproduce them.** Reference schema
  files, type definitions, and contract files by path. Keep the prose at the
  level of what they mean and how they are used.
- **Status-tag every command and major code path** as one of: Functional,
  Partial, Legacy, Aspirational, Broken.
- **End every section with a "loose ends" call-out** - declared but unused
  capabilities, dead references, planned-but-not-shipped behavior, known gaps.
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
- **Refresh history.** A table: date, branch / HEAD commit, notes on what
  changed in that pass.

On every later run, update `PROMPT.md` in place: refresh the verbatim prompt
if it changed, update the document set list, and append a row to the refresh
history table.

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
| `01-inventory.md` | The 16 CLI verbs (incl. `settarget`), 19 src projects (1 entry + 18 libraries), 2 AOT tools, scripts and CI - what each is, what it reads/writes, status. |
| `02-install-build-run.md` | Toolchain, `build.sh`, `dotnet publish` flow, host requirements, update / uninstall. |
| `03-external-dependencies.md` | Plane REST API, Anthropic REST API, `claude` CLI, NuGet packages, handshake on missing dependency. |
| `04-configuration.md` | `.build/config.toml` sections, env vars, secrets, precedence. |
| `05-state-and-persistence.md` | `.build/`, `.worktrees/`, Plane writes, in-process caches, cleanup. |
| `06-public-surfaces.md` | CLI surface, library-level public types, stability call-outs (`WORKER_RESULT` envelope, marker comments, JSONL schema). |
| `07-contracts.md` | Inter-project contracts within this repo, shared artifacts with Plane / Claude Code / the older claude-config flow. |
| `08-workspace-assumptions.md` | Branch conventions, required tooling, OS specifics, CI matrix, worktree-aware behavior. |
| `09-failure-modes.md` | Per-phase failure modes, idempotency posture, cross-cutting failure modes. |
| `10-lifecycle-orchestration.md` | The state machine, per-phase step sequences, chain rework loop (`MaxReworkRounds = 2`), event kinds emitted. |
| `11-llm-architecture.md` | The two LLM layers - the wired four-vendor worker layer (`IWorkerAgent`) and the built-but-unwired model-client layer (`ILlmClient` / `IModelClient`) - vendor-specific code map, what it takes to add a new provider. Added 2026-05-28 by request to support multi-provider planning; rewritten 2026-05-30 once the multi-provider worker set actually landed. |
| `PROMPT.md` | This file. |

Set evolution:
- 2026-05-28 - initial publication (12 documents: 00-index, 01-10, PROMPT.md).
- 2026-05-28 - added `11-llm-architecture.md` after operator request for a multi-provider planning document. No existing docs were modified except this `PROMPT.md` and `00-index.md` to link the new entry.
- 2026-05-30 - full code-true refresh against HEAD `68d6fa2` (the baseline `164e733` was ~150 commits behind). No documents added or removed; the same 13-file set was rewritten in place. The multi-provider planning anticipated by `11` shipped at the worker layer in the interim, so `11` flipped from "Codex/Gemini Aspirational" to "four agents Functional and wired", and the new Aspirational item became the unwired `IModelClient` layer.
- 2026-06-01 - code-true refresh against HEAD `e8d9a95` (52 commits past `68d6fa2`). No documents added or removed; the same 13-file set was updated in place. Major deltas absorbed: the new `settarget` verb and `[work].target_branch` config; target-branch-aware ship (configurable merge destination, preflight wrong-branch guard, `MainAutoRebased` -> `TargetAutoRebased`, progress/`--debug` stderr output); the op-27 fenced-block payload protocol (parser pre-pass + `FencedBlockResolver` + new AOT `MarkdownRenderer`, migrating plan/implement/review/draft bodies out of JSON-string metadata); the TLB-366 per-run Plane issue snapshot cache with `next_page_results` pagination and write-through updates (throttle re-confirmed at 40/min); the AOT ILC OOM mitigation; TLB-329 sibling-`blocked_by` dependency-ordered parent-chain levels with the `--max-parallel` override; and state-aware implement guidance.

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

---

## Refresh history

| Date | HEAD commit | Notes |
|---|---|---|
| 2026-05-28 | `164e733` on `main` | First publication. 12 documents created (00-index, 01-10, PROMPT.md). Doc set built from `Program.cs`, the 14 `ThroughlineBuild.*` projects, `tests/`, `docs/throughline-build-architecture.md`, `.build/config.toml.example`, `.claude/plane-config.md`, `.claude/ticket-config.md`, `.github/workflows/build.yml`, `build.sh`, `.gitignore`, `.gitattributes`. |
| 2026-05-28 | `164e733` on `main` | Added [11-llm-architecture.md](11-llm-architecture.md) at operator request: a dedicated map of LLM interfaces (`ILlmClient`, `IWorkerAgent`), vendor-specific code locations, and the architectural choices required to add a second provider. Updated `00-index.md` and this `PROMPT.md` to reference the new doc. No other docs touched. |
| 2026-05-30 | `68d6fa2` on `main` | Full code-true refresh; baseline `164e733` was ~150 commits behind. All 13 files rewritten in place against HEAD source (no docs added/removed). Major code deltas absorbed: project count 14 -> 19 (`ModelClient`, `Workers.Common`, `Workers.Codex`, `Workers.Gemini`, `Workers.Copilot`); four wired worker agents replacing the claude-only worker (`WorkerAgentFactory`, per-agent config/sizes/templates, `WorkerSize`); `ClaudeCodeReviewer` -> `WorkerAgentReviewer`; `WorkerResultParser` relocated to `Workers.Common`; new verbs `decompose`/`init`/`list`; multi-ticket and tree-aware chain (`ParallelDispatcher`, `TopologicalSorter`, `AncestorSkipFilter`, parent recursion, all-children-Done ship gate, cascade close/defer); obsolete-claim ratification (`ObsoleteRatifier`, `TicketSubsumed`); divergence probe + auto-rebase + push-after-FF (`DivergenceState`, `MainAutoRebased`, `MainWorktreeLock`); Plane surface extensions (`QueryAsync`/`TransitionLifecycleAsync`/`UpdateDescriptionAsync`/`CreateChildTicketsAsync`, issue-type name->UUID, `RequestThrottle` 60/min, `?next_path=` deep-link); `EventKind` 9 -> 13, `Phase` 9 -> 10; `ANTHROPIC_API_KEY` hard gate removed (lazy `LlmClientFactory`); `IModelClient`/`AnthropicModelClient` SSE streaming built and tested but unwired. The verbatim prompt above was unchanged from the prior run. |
| 2026-06-01 | `e8d9a95` on `main` | Code-true refresh; 52 commits past `68d6fa2`. All 13 files updated in place (no docs added/removed). Major code deltas absorbed: new `settarget` verb (`SetTargetCommand`, dispatched pre-config-load) + `[work].target_branch` config + `BuildConfig.ResolveTargetBranch()`; target-branch-aware ship (`ShipOptions.TargetBranch`, target-aware `BaseRefResolver`, preflight `wrong_worktree_branch` guard, `MainAutoRebased` -> `TargetAutoRebased` rename at ordinal 10, FF-merge+push to the configured target, `--debug`/progress stderr output, worktree create-from-local-branch fallback); op-27 fenced-block payload protocol (TLB-333..342: `WorkerResultParser` fenced pre-pass + `FencedBlockResolver`, new AOT `MarkdownRenderer` in `Workers.Common`, `WorkerResult.Blocks`, plan/implement/review/draft bodies migrated to `PLAN_BODY`/`IMPLEMENT_SUMMARY`/`REVIEW_CRITIQUE`/`DRAFT_BODY` blocks via `*_ref` metadata); TLB-366 per-run Plane issue snapshot cache (`_seqToUuid`/`_issueByUuid`, single-flight load, write-through `AddOrUpdate`, `next_page_results` pagination, loud truncation warning at `MaxListPages=50`, `KeyNotFoundException` catch in the chain batch path) - throttle re-confirmed at 40/min; AOT ILC OOM mitigation (`IlcOptimizationPreference=Size`, `IlcMaxParallelism=1`, `RunAsync` split); TLB-329 sibling-`blocked_by` dependency-ordered parent-chain levels + `--max-parallel`/`ForceParallel` override; state-aware implement guidance on non-Ready tickets; embedded-but-unsurfaced user-guide template (TLB-320). `EventKind` stays at 13 (rename only), `Phase` stays at 10. Verbatim prompt unchanged. Note: `PlaneTicketingClient.cs` cites in `03`/`05` shifted ~+300 lines; snapshot/pagination references re-verified, some untouched prose cites left at prior neighborhood. |
