# Plan-phase repo index: brainstorm + research prompt

## Context

latticeflow's plan phase ([PlanPhase.cs](../src/ThroughlineBuild.Phases/PlanPhase.cs)) does
almost no codebase exploration itself. It enumerates only the **top-level** directory names
(`Directory.EnumerateFileSystemEntries`, no recursion), bakes that list into the brief via
[PlanBriefBuilder.cs](../src/ThroughlineBuild.Briefs/PlanBriefBuilder.cs), and hands the
whole job to a spawned `claude` subprocess. The
[plan.md template](../src/ThroughlineBuild.Briefs/Templates/claude-code/plan.md) tells the
agent to "use Grep, Glob, and Read aggressively." Result: **every ticket re-discovers the
codebase from scratch** with a fresh tool-use loop. There is no index, symbol table, cache,
or DB anywhere in the project, and the architecture deliberately avoids persistent state
("single binary, invoked and exits, no daemon").

The idea: give the plan phase a deterministic repo index (where functions/types live, who
calls what) so the agent stops re-grepping from zero. This doc is a brainstorm plus a
research prompt to hand to a claude-web agent. No code has been written.

## Goals (what "more efficient" means here)

Ranked:
1. **Token cost** - fewer tokens per plan (caveat: smaller lever; reasoning tokens dominate
   over exploration, so don't over-index on this alone).
2. **Latency** - kill the sequential grep/read round-trips; each one is a model turn.
3. **Plan quality / consistency** - stop the agent missing call sites or hallucinating file
   paths; make plans repeatable.

Pure determinism-on-principle is a nice-to-have, not the driver.

## Key framing (the thing to get right)

"Repo database" hides two different problems:

- **Deterministic symbol lookup** ("where is `RunAsync` / who calls it / what implements
  `IPhase`"). For C# this is solved *exactly and for free* by Roslyn or any LSP - no
  embeddings, no ML. This is the cheap, high-value, on-brand win.
- **Semantic retrieval** ("what code handles plan briefs?"). Needs embeddings + vector store.
  Fuzzier, heavier infra, freshness pain. Defer this.

Honest payoff note: the win is mostly **latency** and **plan quality**, with token savings
secondary, because plan-phase cost is reasoning-dominated, not IO-dominated.

## Design axes the research must resolve

- **What to index:** Roslyn symbol table (exact, C#-native) vs tree-sitter tags
  (aider-style, language-agnostic) vs LSP-as-a-service vs embeddings.
- **Integration pattern** (research to recommend):
  - (a) **Front-load** - orchestrator pre-resolves symbols named in the ticket, bakes
    file:line + signatures into the brief. Stateless, no new agent tools.
  - (b) **Query tool** - expose `codeq find-symbol Foo` CLI/MCP the agent calls instead of
    grep. Agent-driven, on-demand.
  - (c) **Hybrid** - token-budgeted repo map in the brief + drill-down query tool.
- **Freshness:** invoke-and-exit means the index lives on disk (e.g. `.build/index/`), keyed
  by git SHA, incrementally rebuilt on changed files.
- **Build vs buy:** survey off-the-shelf (aider repomap, Serena MCP, universal-ctags,
  Sourcegraph SCIP/zoekt, csharp-ls / Roslyn LSP) AND sketch the custom Roslyn path, then
  recommend head-to-head.

## Constraints the research must respect

- Stateless orchestrator: single binary, invoked per-ticket, exits. Any index must persist
  to disk and self-validate against the current git SHA - no daemon assumed (though the
  research may argue for an optional one if the win is large).
- Primary language is **C#/.NET** (the latticeflow repo itself), but the tool is run against
  *other* repos too, so language-agnostic options stay relevant.
- Worker is Claude Code spawned as a subprocess; brief is delivered over stdin; results come
  back as an NDJSON envelope. Any "query tool" must be reachable from inside that subprocess
  (MCP server, or a CLI on PATH).
- ASCII-only, Windows + Git Bash environment.

---

## Research prompt (paste into claude-web)

> I'm designing a codebase-index feature for an agentic ticket-automation tool. The tool
> ("latticeflow") is a deterministic C#/.NET orchestrator that, for each ticket, spawns a
> Claude Code subprocess to write an implementation plan. Today that subprocess explores the
> repo from scratch every time using Grep/Glob/Read - there is no index, symbol table, or
> cache. I want to add a repo index so planning is faster, cheaper, and more reliable. The
> orchestrator is stateless (invoked per-ticket, then exits), runs on Windows, and is itself
> C# but is also run against repos in other languages. The Claude Code worker receives its
> instructions over stdin and can reach external tools only via an MCP server or a CLI on
> PATH.
>
> Produce a comparative research report covering:
>
> 1. **The landscape of code-indexing approaches used by agentic coding tools today.** For
>    each, explain the mechanism, what query it answers, freshness/invalidation strategy,
>    setup cost, and whether it needs a persistent daemon. Cover at minimum:
>    - aider's repo map (tree-sitter + PageRank ranking, token-budgeted skeleton)
>    - Serena (MCP server wrapping LSP for symbol-level navigation)
>    - Sourcegraph SCIP / zoekt
>    - universal-ctags / tree-sitter tags
>    - LSP servers as a queryable backend (OmniSharp, Roslyn LSP, csharp-ls)
>    - Cursor / Windsurf / Claude Code's own indexing approaches (whatever is publicly known)
>    - embeddings / vector-DB RAG retrieval (and when it's worth it vs symbol lookup)
>
> 2. **Deterministic symbol lookup vs semantic retrieval** - clearly separate "where is
>    symbol X / who calls it / what implements interface Y" (exact, e.g. Roslyn/LSP) from
>    fuzzy semantic search (embeddings). For a planning agent, quantify when each pays off.
>
> 3. **The C#/Roslyn custom path.** What do Roslyn workspace / symbol-finder APIs
>    (`SymbolFinder`, `Compilation`, `SemanticModel`) give you out of the box for an exact
>    symbol/xref index? Cost to build and keep fresh incrementally? How does it compare to
>    just shelling out to an LSP server?
>
> 4. **Integration patterns for a stateless, subprocess-based agent.** Compare:
>    (a) front-loading resolved symbols into the prompt, (b) exposing a fast symbol-query
>    CLI/MCP tool the agent calls instead of grep, (c) a hybrid repo-map-plus-query-tool
>    (aider-style). Which gives the best latency / token / plan-quality tradeoff, and why?
>    Note any evidence on how much exploration round-trips actually cost in agent loops.
>
> 5. **Freshness for an invoke-and-exit tool** - how to persist an index to disk, key it to
>    a git SHA, and incrementally update only changed files, without a long-running daemon.
>    Is a daemon ever worth it here?
>
> 6. **A concrete recommendation** ranked for these goals (in order): reduce token cost,
>    reduce latency, improve plan quality/consistency. Give a phased path: cheapest useful
>    win first (e.g. a pre-baked repo map, possibly prompt-cached across invocations), then
>    the higher-investment options. Call out failure modes and what to measure to know it
>    worked.
>
> Prefer primary sources (tool docs, source, design write-ups) and cite them. Flag anything
> that's speculative vs documented.

## Next steps after research comes back

- Read the report, pick an integration pattern + build-vs-buy call.
- Decide success metric up front (tokens-per-plan, plan wall-clock, or a plan-quality eval)
  and capture a baseline from the current event logs in `.build/events/`.
- Spin a TLB ticket (or an op-plan) for a thin prototype - likely a repo-map front-load or a
  symbol-query CLI - measured against the baseline before committing to the heavier path.
