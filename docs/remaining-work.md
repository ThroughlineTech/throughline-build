# Throughline Build - remaining work after build-new, build-chain, build-scaffold

## Multi-agent support (foundation + per-CLI implementations)

Big architectural addition. Today `ClaudeCodeAgent` is the only worker; phases hardcode it. Need an abstraction so different commands can use different LLM CLIs, and so additional CLIs can be added incrementally.

### Foundation: agent abstraction + per-command configuration

Required first; everything else in this bucket depends on it.

- Extract `IWorkerAgent` interface from existing `ClaudeCodeAgent`
- Common contract: takes a brief, returns a structured result; hides whether implementation is subprocess or HTTP
- Per-command agent selection in `.build/config.toml`:
  ```toml
  [agents]
  plan = "claude-code"
  implement = "claude-code"
  review = "codex"
  ship = "claude-code"  # ship has no worker today but reserved
  ```
- `IAgentFactory.GetAgent(phaseName)` resolves the configured agent
- Phase classes accept `IWorkerAgent` via DI rather than instantiating `ClaudeCodeAgent` directly
- WORKER_RESULT envelope generalized OR agent-specific parsers map to a common internal result type (probably the latter - cleaner separation)
- Affects `[workers] max_output_tokens` config (becomes `[agents.claude_code] max_output_tokens` or similar; per-agent settings)

Probably 4-6 briefs across 2 plans. Refactor-heavy.

### Per-CLI agent implementations (priority order)

Each is its own op-doc with similar shape:
- Implement `IWorkerAgent` for the CLI
- CLI-specific invocation (subprocess args, env vars, auth)
- CLI-specific output parsing → common result type
- Per-agent config section in config.toml
- Tests including round-trip envelope parsing
- Brief template variants if the CLI needs different prompt scaffolding

1. **Codex** - first additional agent. OpenAI's Codex CLI. Establishes the pattern for non-Claude agents.
2. **Copilot** - GitHub Copilot CLI.
3. **Gemini** - Google's Gemini CLI.
4. **Ollama (local LLM)** - qualitatively different. HTTP API instead of subprocess. No cost tracking (free / compute-bound). Model selection matters. Has exploratory research component before scaffolding: which Ollama models can plausibly run the planner/implementer briefs, what context windows are usable, what output format adherence to expect. Possibly a research op-doc (or just a research spike) before the implementation op-doc.

Brief template question: do all agents share the existing `Templates/plan.md` etc., or does each agent get its own variant? Likely shared for v1 (lowest common denominator), with per-agent overrides as a v1.1 concern when specific agents need different scaffolding. Worth resolving as part of the foundation op-doc.

Cost tracking question: each LLM CLI reports costs differently or not at all. The harness and event log need to handle "cost unknown" gracefully. Foundation op-doc should make `Cost` field nullable in the common result type.

## Command surface gaps

- **build close** - cancel/close a ticket with reason. Small op-doc; 2-3 briefs.
- **build list** - query tickets, show state. Filter by state, parent, type. Convenience.
- **build amend** - modify existing ticket content (description, acceptance criteria) post-creation
- **build defer** - move ticket to deferred/later state without cancelling
- **build reopen** - bring a Done/Cancelled ticket back into the active flow

These five form the ticket lifecycle management surface. Could be one op-doc grouped as "lifecycle commands" since they share IPlaneTicketing surface and ticket-state semantics.

## Install / distribution

Current state: `build.exe` binary (AOT-compiled, no .NET runtime needed) + per-repo `.build/config.toml`. That's basically it.

Gaps:
- **build init** - scaffold `.build/config.toml` in a new repo with sensible defaults + prompts for project_id, Plane URL, workflow_tool, agents-per-command. Small op-doc, ~2 briefs. Note: as multi-agent support lands, init's config scaffolding grows.
- **Binary distribution** - currently builds from source. Future options: github release artifact, package manager. Not urgent at solo-operator stage.

## Decompose - take a big ticket, break into children

Big op-doc. LLM-driven decomposition where worker reads a parent ticket and produces 2-N child ticket specs; orchestrator creates them in Plane with parent-child relationships.

Implies:
- New phase: DecomposePhase
- New brief template: `Templates/decompose.md` with discipline for child sizing, acceptance criteria splitting, scope boundaries
- Plane API integration for parent-child relationships (sub-issues)
- Decision on parent state after decomposition (stays Backlog with children? Moves to a "decomposed" pseudo-state? Just gets children linked?)
- CLI command + structured result
- Verdict on quality of decomposition (does reviewer concept apply, or fire-and-forget?)

6-8 briefs across 3 plans. Comparable scope to build-chain.

## Tree-aware commands - parent → children → grandchildren

Foundational structural change. Currently `/ti` pre-ticket investigation walks parent + children but stops at grandchildren. The depth-stop semantics need to be applied consistently across all commands that can be invoked on a parent.

Op-doc for the foundation:
- Tree-walk utility in orchestrator (BFS or DFS, with grandchildren-stop depth limit)
- Behavior contract for what each command does when invoked on a parent with children
- Shared code path for "is this ticket a parent? if yes, how do we handle it?"

Then per-command updates (probably folded into the same op-doc or split as downstream tickets):
- plan on parent: ?
- implement on parent: probably refuse (children must be worked individually)
- review on parent: aggregate verdict from children? or refuse?
- ship on parent: ship when all children Done?
- chain on parent: recursively chain children?

5-7 briefs across 2 plans (foundation + per-command behaviors). Cuts across the whole command surface.

## Multi-ticket per command

Depends on tree-awareness landing first. Once a command can be invoked on a parent and recurse to children, multi-ticket via explicit list (`build chain T1 T2 T3`) is a smaller addition.

## Brief enrichment (waiting on more data)

- **Templates/implement.md enrichment** - build-chain Plan A B02 adds the rework feedback section; broader enrichment waits on 5+ coding-ticket comparison data points
- **Templates/review.md enrichment** - verdict criteria land in build-chain. Further enrichment (checks_failed taxonomy, severity bands) waits on chain runs

## Bug fix tickets to file in TLB

1. **WorkerResultParser hardening + DTO snake_case + smoke tests** (drafted as tn-worker-result-parser.md)
2. **max_tokens config addition** (drafted as tn-max-tokens-config.md). Note: config schema will evolve once multi-agent foundation lands (`[workers]` becomes `[agents.claude_code]` or similar)
3. **Missing session folder for implement --debug** (drafted as tn-implement-debug-session-folder.md)
4. **Empty event JSONL in worktree** (drafted as tn-empty-event-jsonl-worktree.md, likely 2-for-1 with #3)
5. **workflow_tool ambiguity** (drafted as tn-workflow-tool-config.md)

## Comparison harness

- **op-pipeline-compare-harness** - already drafted (3 plans, 6 briefs in PowerShell). Needs to be scaffolded to its own repo.

## Comparison data collection

- **B02-B08 of survey-app-build through both pipelines** - feeds implement.md and review.md enrichment decisions
- **Once chain ships: run survey briefs through build chain** - rework loop convergence data. Distribution of rounds and outcomes.
- **Once multi-agent lands: comparison across agents** - same brief, different agent, compare cost / quality / completion rate

## Architectural debt / nice-to-haves

- **Roslyn analyzer for template-DTO field alignment** - catches camelCase-vs-snake_case bug class at build time. Deferred from WorkerResultParser ticket as v1.1.

## Out of scope for build pipeline (different threads)

- TravelAgent VS Code extension for typed state integration
- Throughline product work (CIS, React Flow canvas, etc.)
- Local LLM inference / fine-tuning paths beyond Ollama integration (QLoRA/Unsloth distillation is a different project)