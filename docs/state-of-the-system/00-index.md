# 00 - State of the System: latticeflow

Last refreshed: 2026-06-11 (HEAD 3a73eb9)

This doc set is a code-true map of the `latticeflow` repository as it exists at the HEAD stamped above (refresh history in [PROMPT.md](PROMPT.md)).

The repository is **Throughline Build** - a `.NET 10` native-AOT CLI named `build` that orchestrates an Agile ticket workflow against a Plane backend by spawning an external coding-agent CLI as a worker subprocess for the LLM-bearing phases and running everything else as deterministic C# code. The worker subprocess is multi-vendor: four agents (`claude-code`, `codex`, `gemini`, `copilot`) are implemented and selectable by config or `--agent` flag; `default_agent` is required config with no hardcoded C# fallback, and both the shipped template and the checked-in operator `.build/config.toml` now set `claude-code` (the earlier template-vs-live drift is resolved). The architecture is described in [docs/throughline-build-architecture.md](../throughline-build-architecture.md); that document is a forward-looking proposal and disagrees with the tree in several places - this doc set documents what is actually in the source, and calls out the disagreements as loose ends.

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

The CLI dispatches one of twenty-one action verbs (`plan`, `implement`, `review`, `ship`, `chain`, `rework`, `decompose`, `new`, `init`, `setup`, `settarget`, `user-guide`, `op-doc`, `models`, `sweep`, `scaffold`, `list`, `amend`, `close`, `defer`, `reopen`), plus `help`/`-h` and `-V`/`--version` (the stamped `BuildVersion.Current`); any other token returns exit 2. Dispatch is a chain of `if (verb == ...)` blocks in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs); the authoritative per-verb inventory is [01-inventory.md](01-inventory.md). Help is served by the tiered registry under [src/ThroughlineBuild.Cli/Help/](../../src/ThroughlineBuild.Cli/Help/) (`HelpRegistryFactory`, `Tier0Renderer`, `Tier1Renderer`, topic files); the old `CliUsage.UsageText` is Legacy, referenced only by tests. Five verbs run ahead of config load: `init` (interactive connected bootstrap), `settarget`, `user-guide`, `op-doc`, and `models refresh`. Most verbs route to a phase or command, which composes calls against `ITicketing` (Plane), `IWorkerAgent` (the selected agent CLI, constructed through `WorkerAgentBuilder.Create`), `IGitClient` (git subprocesses), and `IEventSink` (JSONL log). The whole binary exits at the end of each verb - there is no daemon, no shared in-process state across invocations.

Coordination between phases happens through three persistent channels:

- **Plane**: ticket state, labels, description, comments (with markers like `[planned_at: <sha>]`), and parent/child sub-issue links. `build setup` provisions the canonical state/label set from `WorkspaceSchema` (Contracts).
- **Git**: the feature branch `ticket/<id>` and its worktree under `.worktrees/`; a chain runs its subtree inside one shared worktree on a `chain/<slug>` **integration branch** (built by `ChainIntegrationBranchFromId`, [src/ThroughlineBuild.Phases/ChainPhase.cs:2966](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2966)) that accumulates child ships and is landed onto the resolved target branch at the root (rebase, fast-forward merge, push unless `--no-push`/`[ship].push=false`). Chain sweeps its worktrees on success and preserves them on failure; `build sweep` is the standalone recovery verb.
- **`.build/events/<stem>.jsonl`**: the append-only event log (`EventKind` now has 14 values including `CostLedger`; `Phase` has 11 including `Gate`).

LLM contact splits into three tiers (architecture Section 3), but at two different maturity levels - see [11-llm-architecture.md](11-llm-architecture.md):
- **Deterministic** code paths - state machines, gates, scans (e.g. `Ship`, `GatePhase` with its vacuity and control provers).
- **Judgment slots** - scoped Anthropic API calls. Today the only live consumer is the `ReasonTranslator` for close/defer/reopen, through `ILlmClient`/`AnthropicClient` (anthropic-only, non-streaming), degrading to `EchoLlmClient` (verbatim reason) when no API key is configured. A newer `IModelClient`/`AnthropicModelClient` with working SSE streaming exists and is tested but is not wired onto any production path.
- **Agentic work** - a worker CLI dispatched in a worktree for plan / implement / review / draft / decompose / scaffold profile derivation. This layer is genuinely multi-vendor and wired: `WorkerAgentFactory` selects one of four `IWorkerAgent` implementations from config or `--agent`. Note that plan's default mode is now `promote` ([plan].mode), which spawns no worker.

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
| [06-public-surfaces.md](06-public-surfaces.md) | CLI exit codes, summary contract, `WORKER_RESULT` envelope + fenced blocks + `COMPLETION_CLAIM`, JSONL event schema, tiered help, library-level public types. |
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
- **Partial / Aspirational** items centre on the model-client layer: `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException`, and the entire `ThroughlineBuild.ModelClient` layer (`IModelClient`, `AnthropicModelClient` with real SSE streaming, `ModelClientLlmAdapter`) is built and unit-tested but constructed on no production path. The `BackendCapabilities` plumbing declared in `ITicketing` is still never read, and the `CompletionClaim` hook fields are declared but unenforced.
- **Aspirational** items named in the architecture but absent from the source tree: the `install` verb (the real bootstrap pair is `init` + `setup`), the OpenAI / Google `ILlmClient` implementations, the GitHub `ITicketing` adapter, MCP server packaging, and the replay verb. `src/ThroughlineBuild.Linear/` exists on disk as untracked build debris only - there is no Linear backend in the tree.
- There are no **Broken** components.

---

## How to read this set on a refresh

1. Start at this index for the orientation.
2. Jump directly to the doc covering the change you are investigating, checking its `Last refreshed` header against the paths you care about (see "Trusting this set" above).
3. Each doc ends with a "Loose ends" section - skim those first if you want to find the rough edges quickly.
4. The most current statement of architectural intent is still [docs/throughline-build-architecture.md](../throughline-build-architecture.md), but where it disagrees with the source code, this set wins, and the disagreement is noted explicitly.
