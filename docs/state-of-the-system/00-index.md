# 00 - State of the System: Throughline Build

Last refreshed: 2026-07-27 (TLB-580)

This doc set is a detailed historical snapshot of the Throughline Build repository
at the HEAD stamped above (refresh history in [PROMPT.md](PROMPT.md)). It is not
the authority for the current tree. Start with the current
[documentation map](../README.md) and
[architecture](../throughline-build-architecture.md), then use this set for
point-in-time implementation detail.

At that commit, the repository was **Throughline Build** - a `.NET 10`
native-AOT CLI named `build` that orchestrated an Agile ticket workflow against
a Plane backend by spawning an external coding-agent CLI for LLM-bearing phases
and running everything else as deterministic C# code. The snapshot documents
four selectable workers (`claude-code`, `codex`, `gemini`, and `copilot`) and
the configuration and architecture that existed at that point.

Voice: technical, `file:line` references throughout, status-tagged. The reader is expected to have all sets open side-by-side.

---

## Trusting this set

Every claim in this set is point-in-time at the HEAD in each doc's `Last refreshed` header. Before relying on a claim about code that may have moved since that stamp, run `git log <docHEAD>..HEAD --oneline -- <cited paths>` and treat claims about changed files as unverified until re-checked against source. Status tags age the same way: a path tagged Aspirational at the stamp may have landed (or a Functional path been removed) since - a stale status tag may have inverted, and the set will not warn you.

## Keeping this set current

The set has two maintenance modes, both first-class: full refreshes run from the prompt recorded in [PROMPT.md](PROMPT.md), and update-as-you-go edits made by any agent whose change alters a documented surface (an endpoint, a verb, a config key, a contract, a status tag). The update-as-you-go contract - edit only affected sections, bump the touched docs' `Last refreshed` headers, append a `targeted` row to the refresh history - is spelled out in the "Keeping the set current (update-as-you-go)" section of the verbatim prompt in [PROMPT.md](PROMPT.md). If you land code that flips a status tag in this set, fixing the tag is part of your change.

---

## Architectural map

```
        Operator invokes `build <verb> ...` in a repo
                                |
                                v
        +--------------------------------------------------+
        |  ThroughlineBuild.Cli (Program.cs verb dispatch) |
        |  config load + tiered Help/ + WorkerAgentBuilder |
        |  -> WorkerAgentFactory, ChainPhaseComposition    |
        +-------------------+---------------+--------------+
                            |               |
                            v               v
                  +---------+---+    +------+--------------+
                  |  Phases     |    |  Commands + Cli      |
                  | Plan        |    |  New / Amend / List  |
                  | Implement   |    |  Close/Defer/Reopen  |
                  | Gate        |    |  Init / Setup        |
                  | Review      |    |  SetTarget / Sweep   |
                  | Ship        |    |  UserGuide / OpDoc   |
                  | Chain       |    |  ModelsRefresh       |
                  | Rework      |    |  Scaffold (+profile) |
                  | Decompose   |    +---+---+---+----+----+
                  +--+---+---+--+        |   |   |    |
                     |   |   |           |   |   |    |
        +------------+   |   +---+   +---+   |   |    +-----+
        v                v       v   v       v   v          v
   +----+----+   +-------+--+  +-+---+-+  +-+---+-+   +-----+----+
   | Briefs  |   | Workers.*|  | Plane |  |Verifi-|   |EventLog  |
   | Builders|   | 4 agents |  |Ticket-|  |cation |   |JsonlSink |
   |per-agent|   |+ Common  |  | Client|  |Gate   |   +-----+----+
   | +Batch  |   +-----+----+  +---+---+  |provers|        |
   | +Preload|         |           |      |Smoke  |        |
   +----+----+         v           v      |Verdict|        v
        |     spawn worker CLI   Plane    +---+---+      write
        |  claude/codex/gemini/  REST         |          JSONL
        |  copilot (stdin/flags)  ^           v            ^
        |              |           |     run check         |
        +----+----+    |           |     processes         |
             |    |    |           |          ^            |
             v    v    v           |          |            |
        +----+----+----+----+      |          |            |
        | Helpers / Git / Scaffold / Contracts (leaf types)|
        | (tree walk, worktree sweep, divergence probe)    |
        +-----------------------+--------------------------+
                                |
                                v
            +-------+ Anthropic API (close/defer/reopen reason translator)
            |         via ILlmClient / AnthropicClient (anthropic-only)
            |
            +-------+ ModelClient / IModelClient (built + tested, UNWIRED)
```

The CLI dispatches one of twenty-six action verbs (`init`, `settarget`, `user-guide`, `op-doc`, `models`, `sweep`, `list`, `get`, `comments`, `comment`, `transition`, `relate`, `setup`, `amend`, `close`, `defer`, `reopen`, `new`, `scaffold`, `rework`, `decompose`, `plan`, `implement`, `review`, `ship`, `chain`), plus help and version meta-surfaces. Dispatch begins in `RunAsync` ([Program.cs:23](../../src/ThroughlineBuild.Cli/Program.cs#L23)); the authoritative per-verb inventory is [01-inventory.md](01-inventory.md). Help is served by `HelpRegistryFactory.Build` ([HelpRegistryFactory.cs:7](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L7)); `models` and `sweep` are the only visible verbs omitted from its twenty-four entries. Five verbs run ahead of config load: `init`, `settarget`, `user-guide`, `op-doc`, and `models refresh`. The global `--json` pre-pass enables versioned machine-readable envelopes for supported ticket verbs through `CliEnvelopeWriter` ([CliEnvelopeWriter.cs:8](../../src/ThroughlineBuild.Cli/Json/CliEnvelopeWriter.cs#L8)). Most verbs route to a phase or command, which composes calls against `ITicketing`, `IWorkerAgent`, `IGitClient`, and `IEventSink`. The binary exits at the end of each verb; there is no daemon or shared in-process state across invocations.

Coordination between phases happens through three persistent channels:

- **Plane**: ticket state, labels, description, comments (with markers like `[planned_at: <sha>]`), and parent/child sub-issue links. `build setup` provisions the canonical state/label set from `WorkspaceSchema` (Contracts).
- **Git**: the feature branch `ticket/<id>` and its worktree under `.worktrees/`; a chain runs its subtree inside one shared worktree on a `chain/<slug>` **integration branch** (built by `ChainIntegrationBranch.BranchNameFromId`, [src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:36](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L36)) that accumulates child ships and is landed onto the resolved target branch at the root (rebase, fast-forward merge, push unless `--no-push`/`[ship].push=false`). Chain sweeps its worktrees on success and preserves them on failure; `build sweep` is the standalone recovery verb.
- **`.build/events/<stem>.jsonl`**: the append-only event log (`EventKind` now has 14 values including `CostLedger`; `Phase` has 11 including `Gate`).

LLM contact splits into three tiers (architecture Section 3), but at two different maturity levels - see [11-llm-architecture.md](11-llm-architecture.md):
- **Deterministic** code paths - state machines, gates, scans (e.g. `Ship`, `GatePhase` with its vacuity and control provers).
- **Judgment slots** - scoped Anthropic API calls. Today the only live consumer is the `ReasonTranslator` for close/defer/reopen, through `ILlmClient`/`AnthropicClient` (anthropic-only, non-streaming), degrading to `EchoLlmClient` (verbatim reason) when no API key is configured. A newer `IModelClient`/`AnthropicModelClient` with working SSE streaming exists and is tested but is not wired onto any production path.
- **Agentic work** - a worker CLI dispatched in a worktree for plan / implement / review / draft / decompose / scaffold profile derivation. This layer is genuinely multi-vendor and wired: `WorkerAgentFactory` selects one of four `IWorkerAgent` implementations from config or `--agent`. `[plan].mode` controls planning inside `build chain`; standalone `build plan` always spawns a worker unless `--from-brief` explicitly requests promotion.

---

## Document set

| Doc | One-line summary |
|---|---|
| [00-index.md](00-index.md) | This file. Architectural map + index + standing notes. |
| [01-inventory.md](01-inventory.md) | Every CLI verb (21), library project (19), tool, script, and CI workflow - what it does, what it reads/writes, status. |
| [02-install-build-run.md](02-install-build-run.md) | Toolchain prerequisites, `build.sh` and `dotnet publish` paths, runtime host requirements, the `build init`/`build setup` bootstrap, update/uninstall. |
| [03-external-dependencies.md](03-external-dependencies.md) | Plane REST API (incl. transport retry + provisioning), Anthropic API, the worker CLIs (claude/codex/gemini/copilot), NuGet packages, what failure looks like for each. |
| [04-configuration.md](04-configuration.md) | `.build/config.toml` sections key-by-key, per-agent worker blocks and model tiers, per-phase agent selection, environment variables, secrets, precedence. |
| [05-state-and-persistence.md](05-state-and-persistence.md) | Everything written to disk and to Plane during a session - locations, lifetime, cleanup posture, the sweep story. |
| [06-public-surfaces.md](06-public-surfaces.md) | CLI exit codes, versioned `--json` envelopes, summary contract, `WORKER_RESULT` + fenced blocks + `COMPLETION_CLAIM`, JSONL schema, tiered help, and the reusable Claude Code facade. |
| [07-contracts.md](07-contracts.md) | Inter-project type contracts inside the repo (incl. the gate contract), and shared artifacts with Plane / the worker CLIs / the older claude-config workflow. |
| [08-workspace-assumptions.md](08-workspace-assumptions.md) | Branch conventions, auto-rebase/push, required tooling, OS / shell / git / encoding assumptions, CI matrix, worktree-aware behavior. |
| [09-failure-modes.md](09-failure-modes.md) | Per-phase failure modes (incl. the gate family and environmental classification), idempotency, recovery, chain exit codes 0-11. |
| [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) | The state machine, per-phase step sequences, the gate, integration-branch chain traversal, batch implement, the rework loop, event kinds. |
| [11-llm-architecture.md](11-llm-architecture.md) | The two LLM layers - the wired four-vendor worker layer and the built-but-unwired model-client layer - where vendor code lives, what it takes to add a new provider. |
| [PROMPT.md](PROMPT.md) | Verbatim prompt that produced this set, refresh history, interpretation notes. |

---

## Status legend

Every command and major code path is tagged with one of these throughout the set:

- **Functional** - implemented, tested, used in production paths.
- **Partial** - implemented but some behavior is stubbed or guarded behind config / flags.
- **Legacy** - present but superseded by a newer code path.
- **Aspirational** - declared in code or design docs but not actually used end-to-end.
- **Broken** - present but does not work as documented.

As of the refresh stamped in this doc's header:

- The four worker agents (`claude-code`, `codex`, `gemini`, `copilot`) are all **Functional** and reachable from `WorkerAgentFactory` by config name or `--agent` flag. The new gate machinery (`GatePhase`, `GateVacuityProver`, `GateControlProber`, `SmokeCollector`), the bootstrap verbs (`setup`, connected `init`), and the recovery verb (`sweep`) entered as **Functional**.
- **Legacy**: `CliUsage.UsageText` - superseded by the tiered help registry, kept alive only by tests, and already lagging the code (it documents chain exit codes only through 9 while `ChainExitCodeMapper` emits 10 and 11).
- **Partial / Aspirational** items centre on the model-client layer: `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException`, and the entire `ThroughlineBuild.ModelClient` layer (`IModelClient`, `AnthropicModelClient` with real SSE streaming, `ModelClientLlmAdapter`) is built and unit-tested but constructed on no production path. The new `ThroughlineBuild.ClaudeCode` public facade is Functional and tested, but NuGet publication is Partial because neither `build.sh` nor CI packs or publishes it. The `BackendCapabilities` plumbing declared in `ITicketing` is still never read, and the `CompletionClaim` hook fields are declared but unenforced.
- **Aspirational** items named in the architecture but absent from the source tree: the `install` verb (the real bootstrap pair is `init` + `setup`), the OpenAI / Google `ILlmClient` implementations, the GitHub `ITicketing` adapter, MCP server packaging, and the replay verb. `src/ThroughlineBuild.Linear/` exists on disk as untracked build debris only - there is no Linear backend in the tree.
- There are no **Broken** components.

---

## How to read this set on a refresh

1. Start at this index for the orientation.
2. Jump directly to the doc covering the change you are investigating, checking its `Last refreshed` header against the paths you care about (see "Trusting this set" above).
3. Each doc ends with a "Loose ends" section - skim those first if you want to find the rough edges quickly.
4. The current architecture reference is [docs/throughline-build-architecture.md](../throughline-build-architecture.md), but source and generated help remain authoritative when documentation drifts.

## Loose ends

- `models` and `sweep` remain outside the tiered help registry even though both action verbs are Functional.
- The public Claude Code facade has package metadata but no pack/publish pipeline.
- The direct model-client abstraction remains unwired, and no GitHub ticketing or MCP server adapter exists.
