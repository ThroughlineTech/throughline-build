# 00 - State of the System: latticeflow

This doc set is a code-true map of the `latticeflow` repository as it exists at commit `68d6fa2` on `main` (refresh history in [PROMPT.md](PROMPT.md)).

The repository is **Throughline Build** - a `.NET 8` native-AOT CLI named `build` that orchestrates an Agile ticket workflow against a Plane backend by spawning an external coding-agent CLI as a worker subprocess for the LLM-bearing phases and running everything else as deterministic C# code. As of this refresh the worker subprocess is no longer claude-only: four agents (`claude-code`, `codex`, `gemini`, `copilot`) are implemented and selectable by config or `--agent` flag. The architecture is described in [docs/throughline-build-architecture.md](../throughline-build-architecture.md); that document is a forward-looking proposal and disagrees with the tree in several places - this doc set documents what is actually in the source, and calls out the disagreements as loose ends.

Voice: technical, `file:line` references throughout, status-tagged. The reader is expected to have all sets open side-by-side.

---

## Architectural map

```
        Operator invokes `build <verb> <ticket-id> [ticket-id ...]` in a repo
                                |
                                v
        +--------------------------------------------------+
        |  ThroughlineBuild.Cli (Program.cs verb dispatch) |
        |  config load + DI wiring + WorkerAgentFactory    |
        +-------------------+---------------+--------------+
                            |               |
                            v               v
                  +---------+---+    +------+--------------+
                  |  Phases     |    |  Commands           |
                  | Plan        |    |  AmendCommand        |
                  | Implement   |    |  ChainCommand        |
                  | Review      |    |  CloseCommand        |
                  | Ship        |    |  DeferCommand        |
                  | Chain       |    |  ListCommand         |
                  | Rework      |    |  NewCommand          |
                  | Decompose   |    |  ReopenCommand       |
                  | New / Draft |    |  ReworkCommand       |
                  | Scaffold    |    |  ScaffoldCommand     |
                  |  + Parallel |    |  InitCommand         |
                  |  Dispatcher |    +---+---+---+----+----+
                  +--+---+---+--+        |   |   |    |
                     |   |   |           |   |   |    |
        +------------+   |   +---+   +---+   |   |    +-----+
        v                v       v   v       v   v          v
   +----+----+   +-------+--+  +-+---+-+  +-+---+-+   +-----+----+
   | Briefs  |   | Workers.*|  | Plane |  |Verifi-|   |EventLog  |
   | Builders|   | 4 agents |  |Ticket-|  |cation |   |JsonlSink |
   |per-agent|   |+ Common  |  | Client|  |Worker-|   +-----+----+
   +----+----+   +-----+----+  +---+---+  |Agent  |        |
        |              |           |      |Reviewer|        |
        |              v           v      +---+---+         |
        |     spawn worker CLI   Plane        |             v
        |  claude/codex/gemini/  REST     run check       write
        |  copilot --print ...    ^       processes        JSONL
        |              |           |          ^             ^
        +----+----+    |           |          |             |
             |    |    |           |          |             |
             v    v    v           |          |             |
        +----+----+----+----+      |          |             |
        | Helpers / Git / Scaffold / Contracts (leaf types) |
        | (tree walk, worktree lock, divergence probe)      |
        +-----------------------+--------------------------+
                                |
                                v
            +-------+ Anthropic API (close/defer/reopen reason translator)
            |         via ILlmClient / AnthropicClient (anthropic-only)
            |
            +-------+ ModelClient / IModelClient (built + tested, UNWIRED)
```

The CLI dispatches one of fifteen action verbs (`plan`, `implement`, `review`, `ship`, `chain`, `rework`, `decompose`, `new`, `init`, `scaffold`, `list`, `amend`, `close`, `defer`, `reopen`), plus a `--help`/`help` token; any other token returns exit 2 ([src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs), dispatch is a chain of `if (verb == ...)` blocks in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs)). Most verbs route to a phase or command, which composes calls against `ITicketing` (Plane), `IWorkerAgent` (the selected agent CLI), `IGitClient` (git subprocesses), and `IEventSink` (JSONL log). The whole binary exits at the end of each verb - there is no daemon, no shared in-process state across invocations.

Coordination between phases happens through three persistent channels:

- **Plane**: ticket state, labels, description, comments (with markers like `[planned_at: <sha>]`, `[decomposed_at: ...]`), and parent/child sub-issue links.
- **Git**: the feature branch `ticket/<slug>` and its worktree at `.worktrees/ticket-<slug>/`; ship now fetches, auto-rebases local `main` onto `origin/main` on a clean divergence, and pushes after the fast-forward merge.
- **`.build/events/<stem>.jsonl`**: the append-only event log.

LLM contact splits into three tiers (architecture Section 3), but at two different maturity levels - see [11-llm-architecture.md](11-llm-architecture.md):
- **Deterministic** code paths - state machines, gates, scans (e.g. `Ship`).
- **Judgment slots** - scoped Anthropic API calls. Today the only live consumer is the `ReasonTranslator` for close/defer/reopen, through `ILlmClient`/`AnthropicClient` (anthropic-only, non-streaming). A newer `IModelClient`/`AnthropicModelClient` with working SSE streaming exists and is tested but is not wired onto any production path.
- **Agentic work** - a worker CLI dispatched in a worktree for plan / implement / review / draft / decompose. This layer is genuinely multi-vendor and wired: `WorkerAgentFactory` selects one of four `IWorkerAgent` implementations from config or `--agent`.

---

## Document set

| Doc | One-line summary |
|---|---|
| [00-index.md](00-index.md) | This file. Architectural map + index. |
| [01-inventory.md](01-inventory.md) | Every CLI verb, library project, tool, script, and CI workflow - what it does, what it reads/writes, status. |
| [02-install-build-run.md](02-install-build-run.md) | Toolchain prerequisites, `build.sh` and `dotnet publish` paths, runtime host requirements, the `build init` bootstrap, update/uninstall. |
| [03-external-dependencies.md](03-external-dependencies.md) | Plane REST API, Anthropic API, the worker CLIs (claude/codex/gemini/copilot), NuGet packages, what failure looks like for each. |
| [04-configuration.md](04-configuration.md) | `.build/config.toml` sections key-by-key, per-agent worker blocks, per-phase agent selection, environment variables, secrets, precedence. |
| [05-state-and-persistence.md](05-state-and-persistence.md) | Everything written to disk and to Plane during a session - locations, lifetime, cleanup posture. |
| [06-public-surfaces.md](06-public-surfaces.md) | CLI exit codes, summary contract, `WORKER_RESULT` envelope, JSONL event schema, library-level public types. |
| [07-contracts.md](07-contracts.md) | Inter-project type contracts inside the repo, and shared artifacts with Plane / the worker CLIs / the older claude-config workflow. |
| [08-workspace-assumptions.md](08-workspace-assumptions.md) | Branch conventions, auto-rebase/push, required tooling, OS / shell / git assumptions, CI matrix, worktree-aware behavior. |
| [09-failure-modes.md](09-failure-modes.md) | Per-phase failure modes (incl. decompose and multi-ticket dispatch), idempotency, recovery. |
| [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) | The state machine, per-phase step sequences, tree-aware chain recursion, parallel/sequential dispatch, the rework loop, event kinds. |
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

As of the refresh in [PROMPT.md](PROMPT.md):

- The four worker agents (`claude-code`, `codex`, `gemini`, `copilot`) are all **Functional** and reachable from `WorkerAgentFactory` by config name or `--agent` flag; the configured default is still `claude-code`. They are no longer Aspirational.
- **Partial / Aspirational** items now centre on the model-client layer: `AnthropicClient.InvokeStreamAsync` still throws `NotImplementedException`, and the entire `ThroughlineBuild.ModelClient` layer (`IModelClient`, `AnthropicModelClient` with real SSE streaming, `ModelClientLlmAdapter`) is built and unit-tested but constructed on no production path. The `BackendCapabilities` plumbing declared in `ITicketing` is still never read.
- **Aspirational** items named in the architecture but absent from the source tree: the `install` verb (the real bootstrap verb is now `init`), the OpenAI / Google `ILlmClient` implementations, the GitHub `ITicketing` adapter, MCP server packaging, and the replay verb.
- There are no **Broken** components.

---

## How to read this set on a refresh

1. Start at this index for the orientation.
2. Jump directly to the doc covering the change you are investigating.
3. Each doc ends with a "Loose ends" section - skim those first if you want to find the rough edges quickly.
4. The most current statement of architectural intent is still [docs/throughline-build-architecture.md](../throughline-build-architecture.md), but where it disagrees with the source code, this set wins, and the disagreement is noted explicitly.
