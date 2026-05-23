# Throughline Build: Briefing for New Agent Sessions

**Audience:** A fresh Claude conversation tasked with writing additional op-docs for the Throughline Build project.
**Purpose:** Compress the design conversation that produced the architecture and op-docs 1-5 into a single readable briefing so a new agent can start writing op-doc 6+ without needing to re-derive settled decisions.
**Reading order:** This doc first, then the reference artifacts it points to.

---

## 1. What Throughline Build is

Throughline Build (`tl-build`, or `build` for short) is a replacement for the markdown-prompt-driven slash-command workflow that previously orchestrated ticket lifecycles through Plane via a persistent Claude Code chat session. The replacement is a single .NET 8 AOT binary that runs phase by phase, calls LLMs only where LLMs add value, and spawns vendor CLIs (Claude Code, Codex, Gemini) as subprocess workers for the agentic parts of the work.

The driver is cost. A measured 7-ticket chain in the prior system consumed ~190M cache_read tokens, ~627k output tokens, and roughly $258, with about 76% of that cost attributable to a persistent Opus session re-reading a 26k-token static prompt corpus on every action. The new architecture eliminates that pattern. Target reduction is ~10x, validated by direct token-cost comparison between equivalent phases.

A secondary driver is vendor neutrality. The prior system was bound to whichever agent host loaded the markdown prompts (Claude Code, Copilot Chat, Codex/LatticeFlow), with parity maintained by mirror-generation scripts. The new system is vendor-agnostic at two layers: the LLM API client (`ILlmClient`) and the worker spawn (`IWorkerAgent`). A user can plan with one vendor, implement with another, review with a third.

---

## 2. The architectural commitment (must-knows)

These are the principles that govern every op-doc and every code review. If a design question arises, resolve it by reference to these:

**Three-tier LLM contact.** Every LLM interaction belongs in exactly one of three tiers:
- *Deterministic phases* (state transitions, gates, drift checks, slug building, conflict scans, Plane writes) live in code. No LLM contact.
- *Judgment slots* (decide if a conflict is mergeable, score a verdict, pick a model, decide whether to skip a step) call an LLM API directly via `ILlmClient` with a small scoped prompt.
- *Agentic work* (plan, implement, review the substance of a change) spawns an agent CLI as a subprocess in a worktree, where the agent uses its full tool loop.

**Right layer.** State machines belong in code; agentic work belongs in agent CLIs; judgment belongs as discrete API calls. The workflow shape (Backlog → Planning → Ready → InProgress → InReview → Done) is the interface humans interact with; everything else is implementation.

**Lessons as fixtures.** Every operational lesson (drift handling, format quirks, vendor-specific gotchas) belongs as a test fixture or typed gate, not as a paragraph in a prompt. Prose lessons grow forever and cost tokens forever. Fixture lessons are evaluable, refactorable, and deletable.

**Dogfooding over benchmarking.** Each phase ships when it handles five real tickets without surprise, not when it passes an A/B test corpus. Trade formal rigor for shipping velocity.

**Vendor neutrality at the right layers.** The LLM API client (`ILlmClient`) and worker spawn (`IWorkerAgent`) abstract vendors. Adding a new vendor is a 50-100 line adapter, not a parallel codebase. Vendor neutrality does NOT live in mirrored prompt content.

---

## 3. Names and conventions

- **C# namespaces:** `ThroughlineBuild.*`
- **Solution:** `throughline-build.sln`
- **Binary on disk:** `build` (the assembly's `AssemblyName` is set to `build`). `tl-build` is the collision-safe alternative if a user has a competing `build` on PATH; not used by default.
- **Per-repo config directory:** `.build/`
- **Config file:** `.build/config.toml`
- **Event log directory:** `.build/events/`
- **Event log files:** `<session-id>.jsonl`, where `<session-id>` is a GUID without hyphens
- **Ticket prefix observed in dogfood project:** `TLB-`
- **WORKER_RESULT envelope format:** a bare line containing `WORKER_RESULT` followed by a single JSON object on the next non-empty line. NOT a fenced code block. (Op-doc 3 spec'd fenced; the implementation chose bare; the implementation is canonical.)
- **Event Data keys:** snake_case lowercase. Examples: `worker`, `status`, `action`, `from`, `to`, `input_tokens`, `cache_read_tokens`. (Outer event fields like `SessionId`, `TicketId` are PascalCase because System.Text.Json defaults that way.)
- **Token field names:** must match the prior system's audit JSONL (`input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_create_tokens`) so cost comparison is direct.

---

## 4. The workflow phases and what each does

The Agile workflow shape is the user-facing contract. Phases map to `build <phase> <ticket-id>` subcommands.

| Phase | Old slash | Verb | Brief description |
|-------|-----------|------|-------------------|
| Plan | `/ti` | plan | Read a Backlog ticket, produce an investigation/plan, label it with risk/size, transition to Ready |
| Implement | `/ta` | implement | Read a Ready ticket, cut a worktree and branch, dispatch a worker to do the code change, transition to InReview |
| Review | `/tr` | review | Read an InReview ticket, run automated checks, dispatch a verifier on the diff, return a Verdict |
| Ship | `/tsh` | ship | Read a passed-review ticket, rebase against main, run regression tests, merge, clean up |
| Chain | `/tch` | chain | Orchestrate multiple tickets in dependency waves; per-wave parallelism with stop-on-failure |
| New | `/tn` | new | Create a new ticket from natural-language input (judgment slot infers title/type/priority/AC) |
| Install | `/ticket-install` | install | Bootstrap `.build/` into a new repo; validate Plane credentials |

The `TicketState` enum in `WorkflowCore.Contracts.Models.Ticket` is `Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled`. The `Phase` enum is `Plan, Implement, Review, Ship, Chain, New`. Note `Plan` (the phase) advances a ticket from `Backlog` through `Planning` to `Ready`; `Implement` (the phase) advances `Ready` through `InProgress` to `InReview`; and so on.

---

## 5. Current implementation state

| Op-doc | Scope | Status |
|--------|-------|--------|
| op-01 | AOT scaffolding (solution, CI, test framework) | Landed |
| op-02 | Contracts (data model, interfaces) and pure helpers | Landed |
| op-03 | Plan vertical slice (ITicketing, ILlmClient, IWorkerAgent, IEventSink concrete impls + PlanPhase + CLI entry) | Landed with known follow-ups |
| op-04 | Auth path fix (lazy ANTHROPIC_API_KEY resolution; subprocess env scrub) | Landed |
| op-05 | Worker usage capture (parse Claude Code's JSON envelope, emit `LlmCall` event) | Designed; implementation in progress at the time of this briefing |

Known follow-ups from op-03 that have not been scheduled (good candidates for first tickets handled by the new system itself once cost comparison validates the architecture):
- **WORKER_RESULT format spec divergence:** op-doc 3 spec says fenced, implementation uses bare. Documentation update only; the implementation is canonical.
- **Label preservation gap:** `PlanPhase` step 14 was specified as "read current labels, union with new, apply." Implementation calls `ApplyLabelsAsync(["risk:X", "size:Y"])` directly, clobbering existing labels.
- **WorkerResultParser robustness:** if a stray `WORKER_RESULT` line appears before the real one with invalid JSON on the next line, the parser bails without continuing to scan. Latent.

These are not blocking issues for the comparison run or for op-doc 6+. They are deliberately deferred.

---

## 6. The components that exist and the components that will be added

Component inventory after op-doc 5 lands. New op-docs will reuse most of these and add components specific to their phase.

### Existing libraries (post op-05)

- `ThroughlineBuild.Contracts` - typed data records and interface definitions
- `ThroughlineBuild.Helpers` - pure-function helpers (SlugBuilder, MarkerParser, DocOnlyDetector, DriftComparator)
- `ThroughlineBuild.Plane` - `PlaneTicketingClient` implementing `ITicketing`
- `ThroughlineBuild.Anthropic` - `AnthropicClient` implementing `ILlmClient` (defined, not yet wired into any phase)
- `ThroughlineBuild.Workers.ClaudeCode` - `ClaudeCodeAgent` implementing `IWorkerAgent`, with JSON envelope parsing after op-05
- `ThroughlineBuild.EventLog` - `JsonlEventSink` implementing `IEventSink`
- `ThroughlineBuild.Briefs` - `PlanBriefBuilder` (more brief builders to come per phase)
- `ThroughlineBuild.Phases` - `PlanPhase` (more phase classes to come)
- `ThroughlineBuild.Cli` - command parsing, TOML config loading, dispatch

### Components likely needed by remaining op-docs

- `IGitClient` interface and a default `Process`-based implementation, currently inline in `PlanPhase` for `git rev-parse origin/main`. Worth promoting to its own type before the implement phase needs `git worktree`, `git checkout -b`, `git diff`.
- `GitWorktreeManager` for creating and reaping worktrees
- `AutomatedChecksRunner` for running lint/build/test in a worktree
- `IVerifier` default implementation (defined as an interface in op-doc 2; no concrete implementation yet)
- `WaveComputer` for chain phase (the `compute-waves` algorithm; not yet ported but op-doc 2's helpers establish the pattern)
- A second `IWorkerAgent` implementation (`CodexAgent`) for cross-vendor verification in op-doc 7 or later

---

## 7. The op-doc format

Dan uses a specific op-doc format. Conform to it strictly. The format:

```
# Operation: <slug>

<framing paragraph: one or two sentences stating what this op-doc accomplishes>

## Why this exists

<paragraph(s) explaining the motivation, anchored to concrete evidence (cost numbers, state reports, prior op-doc references)>

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | ...  | -          | S/M/L  |
| B    | ...  | A          | S/M/L  |

## Plan A: <Name>

### Goal

<paragraph stating what Plan A produces and the brief sequence>

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | ... | ... | - | ... |
| 02 | ... | ... | 01 | ... |

### Briefs - detail

#### Brief 01: <slug>

Goal: <one paragraph>

Inputs: <bullets>

Outputs: <bullets>

Acceptance:
- [ ] WHAT must be true (NOT how to verify)
- [ ] ...

Notes: <prose; conventions, caveats>

OOS:
- Do not <explicit boundaries>
- ...

## Plan B: <Name>
(if more than one plan)

## What done looks like

<narrative description of the end state; what a user observes; what gates are met>
```

Rules that get repeatedly violated and must be enforced:

- **NO Verification blocks.** Claude Code adds them automatically. Do not author them.
- **NO Risks or Future sections.** Op-docs are scoped work; risk lives in the architecture doc; future work is its own op-doc.
- **Dispatch order "Depends on" uses plan IDs only**, never brief IDs. If brief 01 of Plan B depends on brief 03 of Plan A, write Plan B's dependency as `A` and put the precise brief-level sequencing in Plan B's `### Goal`.
- **Acceptance criteria are WHAT not HOW.** "All records compile under .NET 8" is WHAT; "Run `dotnet build` and verify zero warnings" is HOW. Use checkboxes for things to be true at completion, not steps to perform.
- **OOS sections are first-class.** They are the primary contamination prevention mechanism. Be explicit and exhaustive about what NOT to do.
- **Single hyphens, not em dashes.** Pure stylistic preference; respect it.
- **No flattery or marketing language.** "This is the right approach" is fine; "this elegant solution" is not.
- **Brief size target: 50-500 LOC of change per brief.** Smaller is fine; larger means split it.

---

## 8. Bootstrap discipline

The new system was largely built by agents using the old system. Without explicit guardrails, agents transcribe the old structure into the new language, producing System B that thinks like System A. Three failure modes have appeared:

**Transcription instead of redesign.** Agent reads a 400-line markdown command, produces a 400-line C# class matching the markdown headings. The class shape is wrong because the markdown shape was wrong. Mitigation: agents writing implementations should NOT read claude-config source files. They should work from the typed contracts and op-doc briefs only.

**Preserving accidental complexity.** The prior system had base64 round-trips because of a settings allowlist mismatch in the host environment. Agents might port the round-trip pattern because they see it. The new binary doesn't need it. Catch by asking: why does this exist; does the reason still apply?

**Documentation contamination.** Agents writing user-facing docs often reference the old system to disambiguate ("don't confuse this with `.claude/plane-config.md`"). This is generally benign and even helpful for users in the dogfooding period, but be aware: documentation-level references to the old system are intentional and bounded; code-level references are debt.

Concrete rules for an agent writing op-docs:

- Don't read claude-config source code. Don't transcribe markdown command bodies into op-docs.
- Don't reference old-system component names in OOS sections except to explicitly forbid them.
- When citing the prior system as motivation (in "Why this exists" sections), cite measurements (token counts, cost numbers) rather than implementation details.
- Each brief's OOS section should include `Do not preserve patterns from prior systems` or equivalent as a default rule.

---

## 9. Decisions made (do not re-litigate)

- **Language:** .NET 8 with native AOT for distribution. Verified working across Mac/Windows/Linux via op-doc 1's AOT spike.
- **Backend:** Plane primary, GitHub Issues as future second adapter, no others.
- **Distribution:** single static binary per OS, no daemon, no persistent server.
- **Config format:** TOML via Tomlyn.
- **Secrets:** environment variables only, never in config file. Lazy resolution (per op-doc 4) — only require what the active phase actually uses.
- **Worker auth:** Claude Code subprocess uses OAuth (subscription) by default. `ANTHROPIC_API_KEY` is stripped from the subprocess environment to prevent silent switching to API-key billing. Per op-doc 4.
- **LLM API for orchestrator:** `AnthropicClient` exists for future judgment slots but is not wired into the plan phase. Future phases that need direct LLM calls (judgment slots) will require `ANTHROPIC_API_KEY` only when those phases run.
- **Workflow phases:** the Agile shape is the interface. Don't redesign it. The phase names are `plan`, `implement`, `review`, `ship`, `chain`, `new`, `install`.
- **Mirror infrastructure:** dead. No `bin/sync-*`, no `copilot-prompts/`, no `plugins/latticeflow/`. Vendor neutrality lives at the API and worker-spawn layers, not in mirrored content.
- **No backwards compatibility** with old markdown configs. Clean break.

---

## 10. Decisions pending (need resolution before or during the relevant op-doc)

- **Orchestrator class name.** The internal class that runs the state machine is currently un-named (`PlanPhase` is per-phase). Candidates: `BuildOrchestrator`, `BuildEngine`, `BuildRunner`. Decide before the chain op-doc (op-09) since chain needs an orchestrator-of-orchestrators.
- **Binary name for shipped product.** Currently `build`. `tl-build` is the collision-safe alternative. Decide before the install op-doc (op-11), which is what users will run first.
- **MCP server packaging.** Should the binary expose itself as an MCP server in addition to its CLI? Probably yes, but timing (v1 vs follow-up) is open.
- **Replay tooling.** A `build replay <session-id> --model X` subcommand against the event log would be powerful. Probably v1.1.
- **GitHub adapter timing.** Ship with v1 to prove the abstraction, or follow once Plane is solid? Recommendation: defer to v1.1.

---

## 11. Remaining op-docs to write

The order below is the migration order. Each promotes when it handles five real tickets without surprise (dogfooding criterion).

### op-06: Implement phase

Goal: `build implement <id>` reads a Ready ticket, cuts a worktree and branch, dispatches a worker to make the code change, captures a structured result, transitions ticket to InReview.

New components likely needed:
- `IGitClient` interface and `Process`-based default implementation (promoted from PlanPhase's inline use)
- `GitWorktreeManager` (creates and tracks worktrees)
- `ImplementBriefBuilder` (constructs the implementation brief from a planned ticket's description)
- `ImplementPhase` class

Reuses: `PlaneTicketingClient`, `ClaudeCodeAgent`, `JsonlEventSink`, `SlugBuilder`, `MarkerParser`.

Open design questions for this op-doc:
- Where does the worktree live? Convention candidates: `.worktrees/<ticket-id>/`, or alongside the repo.
- What does the brief tell the worker about scope? The plan_html from the prior phase is the obvious input; how much additional structure does the implementation brief need?
- Worker output for implement: `files_changed` becomes load-bearing (verifier will use it). The worker must emit the actual diff or its `files_changed` list must match `git diff --name-only`.
- Failure modes: what if the worker writes outside `Brief.AllowedWrites`? What if the worker produces zero changes?

This op-doc should be considered the first real test of the multi-agent dispatch story. Cross-vendor implement (Codex agent instead of Claude Code) might be a v1.1 thing or might land here.

### op-07: Review phase

Goal: `build review <id>` reads an InReview ticket, runs automated checks (lint/build/test) against the worktree, dispatches a verifier on the diff, returns a Verdict.

New components likely needed:
- `AutomatedChecksRunner` (runs deterministic checks; results are typed)
- `ReviewBriefBuilder` (for the verifier, not a worker)
- `ReviewPhase` class
- A default `IVerifier` implementation. Candidate: `ClaudeCodeAgent` in a verifier role, fed only Brief + GitDiff + WorkerResult (no shared context with implementer). Or a separate `VerifierAgent` type.

Reuses: `PlaneTicketingClient`, `JsonlEventSink`, `GitWorktreeManager` (from op-06), `DocOnlyDetector`.

This is the first op-doc to exercise the `IVerifier` interface and cross-vendor verification. If `ClaudeCodeAgent` did the implement, the review can be done by a different vendor (Codex, Gemini) to get independent judgment. The architecture supports it; this op-doc decides whether to wire it for v1.

### op-08: Ship phase

Goal: `build ship <id>` reads a passed-review ticket, rebases the branch against origin/main, resolves trivial conflicts (or escalates), runs regression tests, merges to main, cleans up the worktree.

New components:
- `RebaseManager` (wraps git rebase semantics; reports conflicts as typed data)
- `ConflictDetector` (uses the existing conflict-marker scan from helpers)
- `ShipPhase` class

This phase has the most deterministic logic and the fewest LLM contacts. A possible judgment slot: "is this conflict trivial enough to auto-resolve?" Calls a small model with the conflict region. Defer that decision to the op-doc design.

### op-09: Chain phase

Goal: `build chain <ticket-ids...>` orchestrates multiple tickets in dependency waves: parallel within a wave, sequential across waves, stop on first failure.

The big one. New components:
- `WaveComputer` (the `compute-waves` algorithm; takes ticket relations, returns wave plan)
- `ChainOrchestrator` (runs the plan; per-wave parallelism)
- `ChainPhase` class

Reuses all prior phases. Heaviest op-doc; integrates everything. Worth treating as multiple plans within one op-doc, or possibly splitting across two op-docs (wave computation as one, orchestration as the other).

### op-10: New phase

Goal: `build new <description>` creates a new Plane ticket from a natural-language description, with a judgment slot inferring title/type/priority/acceptance-criteria/labels.

New components:
- `NewBriefBuilder` (simple; constructs the inference prompt)
- `NewPhase` class
- First real use of `AnthropicClient` as a judgment slot (the brief goes to the LLM API directly, not to a worker subprocess)

This op-doc is small and is the first to exercise the judgment-slot pattern. Good place to validate that pattern.

### op-11: Install phase

Goal: `build install` bootstraps `.build/` into a new repository: writes a config template, validates Plane credentials, optionally registers the MCP server.

New components:
- `InstallPhase` class
- Possibly MCP server registration logic if MCP packaging lands here

Smallest phase. Could end up as a glorified shell script wrapped in a phase class.

---

## 12. How to write the next op-doc

Recipe:

1. **Identify the phase.** Read this briefing's section 11 entry for the target phase.
2. **Refresh context.** Read the architecture doc (Section 5 component specs are particularly load-bearing), op-doc 3 as the template for vertical-slice op-docs, and op-doc 5 as the template for additive corrective op-docs.
3. **Read the latest event log reference doc** (`docs/event-log-file-format.md`) to confirm current event semantics.
4. **Identify components.** Which existing libraries get reused? Which new ones are added? Map each to a brief.
5. **Sequence the briefs.** Foundation first (new interfaces, new helpers), then concrete implementations, then phase composition, then CLI subcommand wiring, then integration tests.
6. **Compose using Dan's op-doc format** (section 7 above). Strict adherence.
7. **OOS sections are mandatory.** Include `do not read claude-config source`, `do not preserve patterns from prior systems`, plus phase-specific forbiddens.
8. **End with "What done looks like."** Concrete narrative of the end-state user experience.

Things to check before declaring an op-doc done:

- Every brief has explicit file paths in the briefs table
- Every brief's acceptance criteria are WHAT-not-HOW checkboxes
- OOS sections are non-empty and specific
- Dispatch order uses plan IDs, never brief IDs
- No Verification blocks, no Risks section, no Future section
- Single hyphens throughout

---

## 13. Reference artifacts to seed the new conversation with

When starting a fresh conversation to write op-doc N, attach these:

1. `throughline-build-architecture.md` - the authoritative design document
2. `op-01-scaffolding.md` through `op-05-worker-usage-capture.md` - the existing op-docs (templates and precedents)
3. `docs/event-log-file-format.md` - current event log reference
4. The README walkthrough (`README.md` or whatever the user-facing setup doc is) - shows what users actually do
5. The state report from the build agent on the current implementation status (most recent version)
6. The cost comparison data from the first real `build plan` vs `/ti` run (after op-05 ships and a real comparison is captured). Specifically:
   - An event log file with a populated `LlmCall` event
   - The corresponding `/ti` audit JSONL line
   - Any qualitative notes about output quality differences
7. This briefing document

Seven items. The new conversation has enough to be productive on turn one.

---

## 14. Things for the new agent to capture as it works

As the new conversation produces op-doc N, the agent should also maintain a small running handoff doc capturing:

- Decisions made during op-doc design (what we picked, what we rejected, why)
- Surprises in the existing code surface that affected the op-doc design
- New TBD items that surfaced (add to section 10 above's equivalent in the next briefing)
- Contamination patterns observed during implementation (so the next op-doc's OOS section can preempt them)

Hand this back to the user along with the finished op-doc. It becomes part of the briefing for the conversation after.

---

## 15. The big-picture goal

Five op-docs designed, two phases fully landed, one phase mid-implementation. Six op-docs remain. When they all ship and the final cutover deletes the markdown corpus and mirror infrastructure, the new system handles the full ticket lifecycle on its own. Comparison data along the way validates the cost reduction claim.

The next agent's job is to keep moving this forward without re-doing decisions that have already been made. This document plus the artifacts it references should be sufficient. If something is unclear, the user is the authority and is reachable; do not infer or guess on architectural questions.
