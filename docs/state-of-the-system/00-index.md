# 00 - State of the System: latticeflow

This doc set is a code-true map of the `latticeflow` repository as it exists at commit `164e733` on `main` (refresh history in [PROMPT.md](PROMPT.md)).

The repository is **Throughline Build** - a `.NET 8` native-AOT CLI named `build` that orchestrates an Agile ticket workflow against a Plane backend by spawning the `claude` CLI as a worker subprocess for the LLM-bearing phases and running everything else as deterministic C# code. The architecture is fully described in [docs/throughline-build-architecture.md](../throughline-build-architecture.md); this doc set documents what is actually in the tree, not what was proposed.

Voice: technical, `file:line` references throughout, status-tagged. The reader is expected to have all sets open side-by-side.

---

## Architectural map

```
        Operator invokes `build <verb> <ticket-id>` in a repo
                                |
                                v
        +--------------------------------------------------+
        |  ThroughlineBuild.Cli (Program.cs verb dispatch) |
        +-------------------+---------------+--------------+
                            |               |
                            v               v
                  +---------+---+    +------+--------------+
                  |  Phases     |    |  Commands           |
                  | Plan        |    |  AmendCommand        |
                  | Implement   |    |  ChainCommand        |
                  | Review      |    |  CloseCommand        |
                  | Ship        |    |  DeferCommand        |
                  | Chain       |    |  NewCommand          |
                  | Rework      |    |  ReopenCommand       |
                  | New / Draft |    |  ReworkCommand       |
                  | Scaffold    |    |  ScaffoldCommand     |
                  +--+---+---+--+    +---+---+---+----+----+
                     |   |   |           |   |   |    |
                     |   |   |           |   |   |    |
        +------------+   |   +---+   +---+   |   |    +-----+
        v                v       v   v       v   v          v
   +----+----+   +-------+--+  +-+---+-+  +-+---+-+   +-----+----+
   | Briefs  |   | Workers. |  | Plane |  |Verifi-|   |EventLog  |
   | Builders|   | ClaudeCode|  |Ticket-|  |cation |   |JsonlSink |
   +----+----+   +-----+----+  | Client|  |Checks +   +-----+----+
        |              |       +---+---+  | +Review|        |
        |              |           |      +---+---+         |
        |              |           |          |             |
        |              v           v          v             v
        |        spawn `claude`  Plane     run check        write
        |        --print --...   REST      processes        JSONL
        |              |           ^          ^             ^
        +----+----+    |           |          |             |
             |    |    |           |          |             |
             v    v    v           |          |             |
        +----+----+----+----+      |          |             |
        | Helpers / Git / Scaffold / Contracts (leaf types) |
        +-----------------------+--------------------------+
                                |
                                v
            +-------+ Anthropic API (close/defer/reopen translator)
            |
            +-------+ JudgmentSlots.ReasonTranslator
```

The CLI dispatches one of 13 verbs. Most route to a phase or command, which composes calls against `ITicketing` (Plane), `IWorkerAgent` (Claude Code), `IGitClient` (git subprocesses), and `IEventSink` (JSONL log). The whole binary exits at the end of each verb - there is no daemon, no shared in-process state across invocations.

Coordination between phases happens through three persistent channels:

- **Plane**: ticket state, labels, description, comments (with markers like `[planned_at: <sha>]`).
- **Git**: the feature branch `ticket/<slug>` and its worktree at `.worktrees/ticket-<slug>/`.
- **`.build/events/<stem>.jsonl`**: the append-only event log.

LLM contact is split into three tiers (architecture Section 3):
- **Deterministic** code paths - state machines, gates, scans (~`Ship`).
- **Judgment slots** - scoped Anthropic API calls (today: `ReasonTranslator` only).
- **Agentic work** - `claude` CLI dispatched in a worktree (plan / implement / review / draft).

---

## Document set

| Doc | One-line summary |
|---|---|
| [00-index.md](00-index.md) | This file. Architectural map + index. |
| [01-inventory.md](01-inventory.md) | Every CLI verb, library project, tool, script, and CI workflow - what it does, what it reads/writes, status. |
| [02-install-build-run.md](02-install-build-run.md) | Toolchain prerequisites, `build.sh` and `dotnet publish` paths, runtime host requirements, update/uninstall. |
| [03-external-dependencies.md](03-external-dependencies.md) | Plane REST API, Anthropic API, `claude` CLI, NuGet packages, what failure looks like for each. |
| [04-configuration.md](04-configuration.md) | `.build/config.toml` sections key-by-key, environment variables, secrets, precedence. |
| [05-state-and-persistence.md](05-state-and-persistence.md) | Everything written to disk and to Plane during a session - locations, lifetime, cleanup posture. |
| [06-public-surfaces.md](06-public-surfaces.md) | CLI exit codes, summary contract, `WORKER_RESULT` envelope, library-level public types. |
| [07-contracts.md](07-contracts.md) | Inter-project type contracts inside the repo, and shared artifacts with Plane / Claude Code / the older claude-config workflow. |
| [08-workspace-assumptions.md](08-workspace-assumptions.md) | Branch conventions, required tooling, OS / shell / git assumptions, CI matrix. |
| [09-failure-modes.md](09-failure-modes.md) | Per-phase failure modes, idempotency, recovery. |
| [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md) | The state machine, per-phase step sequences, the chain rework loop, event kinds. |
| [11-llm-architecture.md](11-llm-architecture.md) | The two LLM-contact interfaces (`ILlmClient`, `IWorkerAgent`), where vendor code lives, what it takes to add a new provider. |
| [PROMPT.md](PROMPT.md) | Verbatim prompt that produced this set, refresh history, interpretation notes. |

---

## Status legend

Every command and major code path is tagged with one of these throughout the set:

- **Functional** - implemented, tested, used in production paths.
- **Partial** - implemented but some behavior is stubbed or guarded behind config / flags.
- **Legacy** - present but superseded by a newer code path.
- **Aspirational** - declared in code or design docs but not actually used end-to-end.
- **Broken** - present but does not work as documented.

As of the refresh in [PROMPT.md](PROMPT.md), the only **Partial** items are `AnthropicClient.InvokeStreamAsync` (`NotImplementedException`) and the `BackendCapabilities` plumbing that is declared in `ITicketing` but never read by any consumer. **Aspirational** items include the `install` verb, the OpenAI / Google `ILlmClient` implementations, the Codex / Gemini `IWorkerAgent` implementations, the GitHub `ITicketing` adapter, MCP server packaging, and the replay verb - all named in the architecture but not in the source tree. There are no **Broken** components.

---

## How to read this set on a refresh

1. Start at this index for the orientation.
2. Jump directly to the doc covering the change you are investigating.
3. Each doc ends with a "Loose ends" section - skim those first if you want to find the rough edges quickly.
4. The most current source of architectural intent is still [docs/throughline-build-architecture.md](../throughline-build-architecture.md), but where it disagrees with the source code, this set wins - we note the disagreement explicitly.
