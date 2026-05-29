# Operation: build-decompose

Add `build decompose`: an LLM-driven phase that reads a large parent ticket and produces 2-N child ticket specs, which the orchestrator creates in Plane with parent-child links. New `DecomposePhase`, a `decompose` brief template, Plane sub-issue integration, and a parent-state decision. Comparable scope to build-chain.

## Why this exists

Large tickets need to become small ones. Today that is manual. `decompose` is the worker-driven counterpart to the sizing work: where a ticket is too big for one worker pass, decompose splits it into right-sized children with their own acceptance criteria and scope boundaries, linked under the parent in Plane. This is the command Dan referenced when keeping `WorkerSize` and ticket `Size` separate - a large ticket is a unit of work that decompose breaks down, so a large ticket does not imply a large worker model.

Decompose creates the parent-child trees that the tree-aware command op-doc later operates on. It does not require tree-awareness to ship: children are worked individually through the existing per-ticket flow. Build decompose after op-14, so it uses per-phase agent selection and lands its templates in the per-agent directories rather than flat.

Decompose is a first-class capability across all worker agents - claude-code, codex, gemini, and copilot. This op-doc owns the per-agent decompose template variants directly rather than cascading a fifth template into each agent's op-doc. Practical consequence: build decompose after the agent op-docs whose decompose support you want at launch (so their template directories exist); any agent op-doc that ships later creates its own `decompose.md` as part of its initial template set, since by then decompose is a known phase.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Decompose phase + template | - | M |
| B | Plane parent-child integration + parent-state handling | A | M |
| C | CLI command + structured result + decomposition verdict | B | M |

A then B then C. A builds the phase and prompt; B persists the children and decides parent state; C exposes the verb and judges decomposition quality.

## Plan A: Decompose phase + template

### Goal

`DecomposePhase` reads a parent ticket and runs a worker against a `decompose` brief that yields a structured set of child specs (title, description, acceptance criteria, size, scope boundaries). The template lands in every shipped agent's template directory so decompose is available across the full agent surface.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | decompose-phase | DecomposePhase reads the parent, runs the worker, parses child specs from WORKER_RESULT | - | src/ThroughlineBuild.Phases/DecomposePhase.cs (new), src/ThroughlineBuild.Briefs/DecomposeBriefBuilder.cs (new) |
| 02 | decompose-template-claude-code | Templates/claude-code/decompose.md - the canonical variant defining child-spec schema, WORKER_RESULT emission, and decomposition discipline | 01 | src/ThroughlineBuild.Briefs/Templates/claude-code/decompose.md (new) |
| 03 | decompose-template-agent-variants | Templates/{codex,gemini,copilot}/decompose.md - the same canonical content reworded per each agent's tool vocabulary | 02 | src/ThroughlineBuild.Briefs/Templates/{codex,gemini,copilot}/decompose.md (new) |

### Briefs - detail

#### Brief 01: decompose-phase

Goal: A phase that turns a parent ticket into a parsed list of child specs.

Inputs: existing phase patterns (PlanPhase/ImplementPhase); `IWorkerAgent`; `Workers.Common.WorkerResultParser`; the `WORKER_RESULT` metadata convention; op-14 per-phase agent selection.

Outputs:
- `DecomposePhase` reads the parent ticket content, builds the decompose brief, runs the configured agent, and parses a structured set of child specs from the worker's `WORKER_RESULT` metadata (each: title, description, acceptance criteria, size label, scope boundary).
- `DecomposeBriefBuilder` assembling the brief from parent content + the decompose template, taking the agent name (per op-14 B13).
- Phase returns a typed result carrying the child specs (creation happens in Plan B).

Acceptance:
- [ ] `DecomposePhase` produces a parsed list of 2-N child specs from a parent ticket
- [ ] Child specs carry title, description, acceptance criteria, size, scope boundary
- [ ] Malformed/empty worker output yields a failure result, no crash
- [ ] Uses the configured agent via op-14 selection

Notes: Reuses the shared parser and per-agent selection - no new worker contract.

OOS:
- Plane creation (B04)
- parent-state handling (B05)
- CLI verb (B06)
- verdict (B07)

#### Brief 02: decompose-template-claude-code

Goal: The canonical decompose prompt as the claude-code variant. Defines the child-spec schema, the WORKER_RESULT emission shape the phase parses, and the decomposition discipline (sizing, acceptance splitting, scope boundaries). Other agent variants port this content.

Inputs: existing claude-code templates for tone/structure; the child-spec shape from B01; the `WORKER_RESULT` metadata convention; any prior decomposition prompt material in the codebase if present (e.g. `.claude/commands/`, the claude-config workflow ports under `copilot-prompts/` or `plane-ticket-workflow/`).

Outputs:
- `Templates/claude-code/decompose.md` with: instructions for splitting into right-sized children (S/M/L labels), splitting acceptance criteria cleanly, drawing scope boundaries between children, the explicit `WORKER_RESULT` block schema the phase parses (per-child fields).
- The template body is structured so the agent-variant brief can adapt only the tool-vocabulary and output-format phrasing, not the content.

Acceptance:
- [ ] `Templates/claude-code/decompose.md` exists and loads via the agent-aware loader
- [ ] A real claude-code decompose run using it produces parseable, sensibly-scoped child specs
- [ ] The child-spec schema in the template matches what `DecomposePhase` parses

Notes: This brief defines the canon. The agent-variant brief depends on it.

OOS:
- Other agents' variants (B03)
- template inheritance/macros
- Decomposition-quality heuristics in the prompt beyond the sizing rules (B07 verdict)

#### Brief 03: decompose-template-agent-variants

Goal: `decompose.md` in each shipped agent's template directory, semantically equivalent to the claude-code canon, reworded for each agent's tool taxonomy and output conventions per the Brief 14 research doc.

Inputs: the canonical content from B02; the Brief 14 research doc (`agent-tool-name-mapping.md`) for per-agent tool vocabulary; each agent's template directory established by its own op-doc.

Outputs:
- `Templates/codex/decompose.md`: canon content adapted for Codex's built-in tool loop and `--json` / final-message output. Tool actions described rather than naming Claude tools.
- `Templates/gemini/decompose.md`: canon content adapted for Gemini; `WORKER_RESULT` emission instructions tuned so the fenced block survives intact inside `.response`.
- `Templates/copilot/decompose.md`: canon content adapted for Copilot under `-s --no-ask-user`, leveraging Copilot's named tool-permission vocabulary where it helps; hardened per the Copilot spike's findings on WORKER_RESULT survival.
- Each variant emits the same child-spec schema; the only deltas are tool-vocabulary and output-format phrasing.
- Embedded-resource globs and `.gitattributes` LF pins updated.

Acceptance:
- [ ] All three agent decompose variants exist and load via the agent-aware loader
- [ ] A real decompose run on each agent produces parseable child specs identical in schema to the claude-code run
- [ ] Resource globs / LF pins updated

Notes: Brief 03 depends on the corresponding agent op-docs having shipped (or at least their template directories existing). If an agent op-doc has not shipped at decompose time, drop that agent's variant from this brief and have the agent's own op-doc create the variant when it ships. Future agents added after decompose create their `decompose.md` as part of their initial template set.

OOS:
- Variants for agents not yet shipped
- altering the canonical content (B02 owns that)
- altering claude-code templates not related to decompose

## Plan B: Plane parent-child integration + parent-state

### Goal

The phase's child specs become real Plane sub-issues linked under the parent, and the parent's state after decomposition is handled deliberately.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | plane-subissue-creation | Create children in Plane as sub-issues with parent-child links | A | src/ThroughlineBuild.Contracts/IPlaneTicketing.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs |
| 05 | parent-state-handling | Decide and implement the parent's post-decompose state | A | src/ThroughlineBuild.Phases/DecomposePhase.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs |

### Briefs - detail

#### Brief 04: plane-subissue-creation

Goal: Persist child specs as linked sub-issues.

Inputs: the Plane sub-issue / parent-child API; child specs from Plan A; `IPlaneTicketing`.

Outputs:
- `IPlaneTicketing` / `PlaneTicketingClient` gains child-creation with a parent link, applying title/description/acceptance/size-label per child.
- Children created atomically enough that a partial failure is reported clearly (which children were created).
- New DTOs registered for source-gen.

Acceptance:
- [ ] Children are created in Plane as sub-issues linked to the parent
- [ ] Each child carries its title, description, acceptance criteria, and size label
- [ ] Partial failure is reported with which children exist
- [ ] AOT publish succeeds

Notes: Edits `PlaneTicketingClient.cs` (shared with op-14 B11 and the lifecycle op-doc) - coordinate ordering.

OOS:
- Parent-state handling (B05)
- CLI verb (B06)
- Rollback of partially-created children (report the partial state; the operator decides)

#### Brief 05: parent-state-handling

Goal: Decide what happens to the parent after decomposition and implement it.

Inputs: the project's Plane states; how `build chain` / tree work will later treat parents.

Outputs:
- A decision (documented in the op-doc and code): parent stays in Backlog with children linked, moves to a decomposed/blocked state, or is otherwise marked. Default recommendation: leave the parent in place with children linked, and let tree-aware behavior (later) decide parent workflows - decompose should not invent a pseudo-state that tree-aware then has to reconcile.
- Implementation of the chosen behavior.

Acceptance:
- [ ] The parent's post-decompose state is deliberate and documented
- [ ] The behavior is implemented and visible in Plane after a decompose run
- [ ] The choice does not preclude later tree-aware parent handling

Notes: This is the one genuine design decision in decompose - flag the chosen state to Dan rather than picking silently if the default is not obviously right.

OOS:
- Tree-aware parent workflows (tree-aware op-doc)
- Label updates on the parent beyond the state transition (separate concern)
- Notifying watchers/assignees on the parent (Plane's own notifications cover this)

## Plan C: CLI + result + verdict

### Goal

`build decompose <id>` runs the phase end-to-end, reports a structured result, and optionally judges the quality of the decomposition.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 06 | build-decompose-command | CLI verb wiring DecomposePhase -> Plane creation -> result | A, B | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs |
| 07 | decompose-verdict | Quality check of the decomposition, or an explicit fire-and-forget decision | A, B | src/ThroughlineBuild.Phases/DecomposePhase.cs (or a verifier) |

### Briefs - detail

#### Brief 06: build-decompose-command

Goal: The verb.

Inputs: DecomposePhase (A); Plane creation (B); CLI dispatch; op-14 `--agent` flag support.

Outputs:
- `build decompose <id>` runs the phase, creates the children, and prints a structured result (children created, ids, sizes).
- Honors `--agent` / per-phase agent selection.
- Usage text documents the verb.

Acceptance:
- [ ] `build decompose <id>` produces linked children and a structured result
- [ ] `--agent` selection works
- [ ] Usage documents the verb

OOS:
- Recursive decomposition (decompose-on-parent is a tree-aware concern)
- multi-ticket decompose (multi-ticket op-doc)
- Dry-run mode (preview children without creating) - add as a flag later if requested

#### Brief 07: decompose-verdict

Goal: Decide whether decomposition quality is judged, and if so, judge it.

Inputs: the reviewer/verifier concept; the child specs.

Outputs:
- A decision: apply a verdict (e.g. children cover the parent's scope without overlap, each child is independently actionable) or run fire-and-forget. If judged, a lightweight check (worker- or rule-based) that flags a poor decomposition rather than silently accepting it.

Acceptance:
- [ ] The verdict question is resolved and documented
- [ ] If judged, a poor decomposition is flagged; if fire-and-forget, that is explicit

Notes: Keep this light - decompose is not implement; a full reviewer loop is likely overkill for v1. Flag the choice.

OOS:
- A full rework loop on decomposition
- per-child review (that is the children's own implement/review flow)
- Decomposition-rework workflow (re-running decompose with feedback; separate op-doc if/when needed)

## What done looks like

`build decompose 42` reads ticket 42, runs the configured agent against that agent's `decompose.md` template, produces 2-N right-sized child specs, and creates them in Plane as sub-issues linked under 42 with their own acceptance criteria and sizes. The decompose template exists for every shipped agent (claude-code, codex, gemini, copilot), so any of them can run the phase via op-14's `--agent` / per-phase selection. The parent's state is handled deliberately, the result is structured, and decomposition quality is either lightly judged or explicitly fire-and-forget. The children flow into the normal per-ticket implement/review path, and the parent-child trees are ready for tree-aware commands when those land.