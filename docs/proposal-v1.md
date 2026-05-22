# Workflow Core: Architecture

**Status:** Proposal for review
**Date:** 2026-05-21
**Author:** Dan + Claude (synthesis from design conversation)

> Placeholder name throughout: `workflow-core` for the system, `ticket` for the binary, `.ticket/` for the per-repo config directory. Final naming TBD.

---

## 1. Context

The current system (`claude-config` plus the practice built on top) is a slash-command workflow that orchestrates ticket lifecycles through Plane via a Claude Code chat session as runtime. Six markdown command files (`ticket-new`, `ticket-investigate`, `ticket-approve`, `ticket-review`, `ticket-ship`, `ticket-chain`) total ~1,326 lines (~26-28k tokens) and are loaded into every invocation. The same prompt corpus is mirrored to GitHub Copilot Chat and Codex/LatticeFlow via generator scripts (`bin/sync-copilot-prompts`, `bin/sync-codex-workflow`) with parity guards. The workflow lives inside the agent host's chat loop, which becomes the orchestrator runtime, the persistent context, the model dispatcher, and the tool gateway, all at once.

The cost shape: the TT2-113..119 chain run (7 tickets, opus orchestrator, 4 hours wall-clock) consumed ~190M cache_read tokens, 45k input, 627k output, costing ~$258. Roughly 76% of that cost is the persistent Opus session re-reading the 26k-token prompt corpus on every action because the chat session is being used as a state machine. The 90% reduction projection in the runbook analysis comes almost entirely from removing this pattern: moving the state machine into deterministic code and calling LLMs only for scoped judgment and agentic work. The refactor is in anticipation of vendor pricing changes within ~90 days. Current architecture assumes free or subsidized tokens. When that ends, the system becomes financially untenable. This work is preparation, not optimization.

---

## 2. Goals, Constraints, Non-Goals

### Goals

- ~10x cost reduction by removing persistent-LLM orchestration
- Vendor neutrality at the LLM API layer and at the worker-spawn layer (not via mirrored prompt content)
- Cross-platform: native AOT single binary per OS (Mac, Windows, Linux)
- Agile workflow shape preserved as the human-facing interface
- Multi-agent worker dispatch as first-class (Claude Code for investigation, Codex for implementation, Gemini for review, mixable per phase)
- Self-installable to any repo (single binary + small config file)
- Stability over cleverness

### Constraints

- .NET 8+ with native AOT for distribution
- Plane as the primary ticketing contract; GitHub Issues as the secondary partial adapter
- Git assumed present (workflow uses worktrees, branches, refs)
- No persistent server or daemon: single binary, invoked and exits
- No dependency on a specific IDE
- ASCII-only output at tool boundaries (preserved lesson from CCONF-86: Git Bash + curl.exe + MSYS mangles non-ASCII bytes)

### Non-Goals

- Other ticketing backends beyond Plane and GitHub (Linear, Jira, Asana out of scope)
- UI, dashboard, or web app
- Backwards compatibility with existing markdown configs (clean break)
- Mirror infrastructure for cross-host prompts
- Hypothetical multi-tenancy or shared workspace
- IDE-specific plugins or browser extensions

---

## 3. Architectural Philosophy

Five principles govern every decision below.

**Three-tier LLM contact.** Every interaction with an LLM falls in one of three tiers, and the tier dictates the runtime:

- *Deterministic phases* (state transitions, gates, drift checks, Plane writes, wave computation, slug building, conflict-marker scans): code only. No LLM contact.
- *Judgment slots* (decide if a conflict is mergeable, score a verdict, pick a model size, decide whether to skip a reviewer): binary calls an LLM API directly with a small scoped prompt. Discrete, observable, swappable.
- *Agentic work* (investigate, implement, review): binary spawns an agent CLI (Claude Code, Codex, Gemini) as a subprocess in a worktree. The worker has full codebase access via the agent's native tool loop.

**Right layer.** State machines belong in code. Agentic work belongs in agent CLIs. Judgment belongs as discrete API calls. Vendor neutrality lives at the API layer and the worker-spawn layer. The Agile workflow shape lives at the human interface. Putting any of these at the wrong layer is the root cause of the current cost shape.

**Lessons as fixtures, not prose.** Every CCONF-style operational lesson is a unit of behavior. It belongs as a test fixture, an automated check, or a typed gate, not as a paragraph in a prompt. Prose lessons grow forever and cost tokens forever. Fixture lessons can be evaluated, refactored, and deleted when superseded. The eval suite is the lesson library.

**Dogfooding over benchmarking.** The new system ships phase by phase while the old system continues to do real work. A phase is promoted when it has handled five real tickets without surprise. Quality regression detection is qualitative and continuous, not benchmark-driven. This trades formal rigor for shipping velocity, and is appropriate for a system whose ground truth is itself fuzzy.

**Workflow shape is the interface.** The Agile phases (Backlog -> Investigating -> Ready -> InProgress -> InReview -> Done/Cancelled) are the protocol between human intent and agent execution. Microsoft's Agentic Agile piece confirms the direction. The state machine in the binary implements this shape directly; the prompts and helpers are implementation, not interface.

---

## 4. System Overview

```
         +----------------------------------+
         |       User invocation surfaces   |
         |  Terminal      Agent CLI chat    |
         |  (direct)      (via MCP tool)    |
         +----------------+-----------------+
                          |
                          v
         +----------------------------------+
         |        Orchestrator Binary       |
         |  (single .NET AOT executable)    |
         |                                  |
         |  +----------------------------+  |
         |  | State Machine              |  |
         |  +----------------------------+  |
         |  | Helpers (deterministic)    |  |
         |  +----------------------------+  |
         |  | Brief Constructor          |  |
         |  +----------------------------+  |
         |  | Event Log                  |  |
         |  +----------------------------+  |
         +----+-------------+-----------+---+
              |             |           |
              v             v           v
       +-----------+ +-----------+ +----------+
       | Ticketing | | LLM       | | Worker   |
       | Backend   | | Client    | | Dispatch |
       | (Plane/GH)| | (multi-   | | (spawn   |
       |           | |  vendor)  | |  agents) |
       +-----------+ +-----------+ +----------+
              |             |           |
              v             v           v
        Plane API     Anthropic     Claude Code CLI
        GitHub API    OpenAI        Codex CLI
                      Google        Gemini CLI
```

### Invocation flow

1. User invokes `ticket investigate TT2-113` from a terminal, or via an agent host's MCP tool that wraps the binary.
2. Binary loads ticket state from Plane via ITicketing.
3. Binary runs deterministic gates (preflight, state check, drift check, doc-only test).
4. Binary constructs a typed Brief from ticket state and current repo state.
5. For agentic work: binary spawns the chosen agent CLI in a worktree, passes the brief, captures the structured WORKER_RESULT.
6. For judgment slots within the orchestration: binary calls an LLM API directly with a small scoped prompt.
7. Binary parses worker output, applies any policy gates, writes results to Plane.
8. Binary appends WorkflowEvent entries to the per-session log throughout.
9. Binary exits with a structured result on stdout for the calling context.

### Data flow

Typed objects throughout. No fuzzy parsing of model output except at two well-defined edges: the Plane backend's HTML serialization, and the WORKER_RESULT fenced block returned by an agent CLI. Both edges have explicit parsers with fail-loud behavior when malformed.

---

## 5. Component Specifications

### 5.1 Orchestrator Binary

Single .NET 8 AOT executable. Entry point: `ticket <phase> <id> [flags]`. Phases: investigate, approve, review, ship, chain, new, install. Reads `.ticket/config.toml` from the working directory (walks up to find it). No persistent state between invocations; all state lives in Plane, in git, and in `.ticket/events/`.

### 5.2 State Machine

Implements the Agile phases as typed transitions. Per-phase preconditions and gates are typed predicates that compose. Transitions go through the state machine; there are no side-channel state writes. The state machine has its own unit tests and is the canonical reference for "what can happen next" at any point.

### 5.3 Ticketing Backend

ITicketing interface (Section 6). PlaneTicketingClient is the full implementation. GitHubTicketingClient is a partial adapter with explicit capability flags: no typed relations, no rich HTML comments, emulated label semantics. The state machine queries `Capabilities` to decide whether a feature is available; phases that require absent capabilities fail loudly at config time, not at runtime.

### 5.4 LLM Client

ILlmClient interface. AnthropicClient, OpenAIClient, GoogleClient are the initial implementations. Model dispatch by string identifier (`anthropic:claude-sonnet-4-7`, `openai:gpt-5`, `google:gemini-2.5-pro`). Both streaming and non-streaming modes supported (different phases want different shapes). Retries via Polly with per-provider rate limiting. New vendors plug in by implementing ILlmClient; no other changes required.

### 5.5 Worker Dispatcher

IWorkerAgent interface. ClaudeCodeAgent, CodexAgent, GeminiAgent are the initial implementations. Each knows how to spawn its CLI in non-interactive mode (`claude --print`, `codex exec --print`, `gemini --print` or equivalent), pass the brief via stdin or file, and capture the structured result. Per-worker config includes tool allowlists and sandbox flags where the vendor supports them. Handles timeouts, retries, kill-on-shutdown. Cross-vendor verification is enabled because IVerifier (Section 5.8) is separate from IWorkerAgent.

### 5.6 Helpers

Pure functions, no I/O where avoidable. Slug, drift, doc-only, wave-compute, conflict-marker scan, ASCII check, tree-clean, marker-comment parser. Each is unit-tested in isolation with fixtures. These are direct algorithm ports from the current bash scripts where the algorithm is sound; the shell-script-as-discrete-file ceremony does not survive the port.

### 5.7 Brief Constructor

Pure function: `(Ticket, RepoState, Phase) -> Brief`. The Brief is a typed object that gets serialized to markdown at the worker boundary. Per-phase templates compose typed inputs into a minimal prompt; there are no giant static prompt files loaded into context. Briefs are constructed from the ticket's actual current state, not from prose that describes the workflow's general shape.

### 5.8 Verifier

Independent process invoked after a worker completes. Receives only the Brief, the GitDiff, and the WorkerResult. No shared context with the implementer. This is a deliberate upgrade over the current chain, where the orchestrator-LLM passes shared context to the reviewer subagent and contaminates the verdict. The Verifier can be a different vendor than the implementer (cross-vendor verification). Returns a typed Verdict.

### 5.9 Event Log

Append-only JSONL written per invocation to `.ticket/events/<session-id>.jsonl`. Every state transition, every LLM call, every worker spawn, every verifier outcome captured with inputs, outputs, model used, tokens consumed, wall time. Replayable: a recorded chain can be re-run against a different model or agent and compared. This is the substrate for both debugging and dogfooding-style evaluation.

---

## 6. Interfaces & Contracts

### Core data types

```csharp
public record Ticket(
    string Id,
    string Title,
    string Type,
    TicketState State,
    Size Size,
    Risk Risk,
    string DescriptionHtml,
    IReadOnlyList<Relation> Relations,
    IReadOnlyList<string> Labels,
    string? ParentId);

public enum TicketState { Backlog, Investigating, Ready, InProgress, InReview, Done, Cancelled }
public enum Size { S, M, L }
public enum Risk { Low, Medium, High }
public enum Phase { Investigate, Approve, Review, Ship, Chain, New }

public record Brief(
    string TicketId,
    Phase Phase,
    string Instruction,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> AllowedWrites,
    IReadOnlyDictionary<string, string> Context);

public record WorkerResult(
    Status Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason,
    IReadOnlyDictionary<string, object> Metadata);

public enum Status { Ok, NeedsRework, Failed, Escalate }

public record Verdict(
    VerdictKind Kind,
    string Rationale,
    IReadOnlyList<string> ChecksFailed);

public enum VerdictKind { Pass, Rework, Fail }

public record WorkflowEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    EventKind Kind,
    string TicketId,
    Phase Phase,
    IReadOnlyDictionary<string, object> Data);
```

### Ticketing backend

```csharp
public interface ITicketing
{
    Task<Ticket> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct);
    Task TransitionAsync(string id, TicketState newState, CancellationToken ct);
    Task AppendDescriptionAsync(string id, string html, CancellationToken ct);
    Task<string> CreateCommentAsync(string id, string html, CancellationToken ct);
    Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct);
    Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct);
    BackendCapabilities Capabilities { get; }
}

public record BackendCapabilities(
    bool TypedRelations,
    bool TypedLabels,
    bool RichHtmlComments,
    bool Attachments);
```

`Capabilities` is the explicit feature-flag mechanism for partial backends. The state machine reads it at config time and refuses to start phases that require unsupported capabilities.

### LLM client

```csharp
public interface ILlmClient
{
    Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);

    IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);
}
```

Model identifiers are `vendor:model` strings. The dispatcher routes to the right ILlmClient implementation. Vendor neutrality is config, not code.

### Worker agent

```csharp
public interface IWorkerAgent
{
    string Name { get; }
    Task<WorkerResult> ExecuteAsync(
        Brief brief,
        string workingDirectory,
        WorkerOptions options,
        CancellationToken ct);
}
```

Each implementation knows the vendor CLI's invocation pattern, allowed-tools flags, output parsing. Adding a new vendor is one new class implementing this interface.

### Verifier

```csharp
public interface IVerifier
{
    Task<Verdict> VerifyAsync(
        Brief brief,
        GitDiff diff,
        WorkerResult workerResult,
        CancellationToken ct);
}
```

Separate from IWorkerAgent so verification can run against a different vendor with no shared context. The default verifier wraps an IWorkerAgent in review mode; alternative implementations can use deterministic checks plus a small LLM judgment call.

### Event sink

```csharp
public interface IEventSink
{
    Task EmitAsync(WorkflowEvent ev, CancellationToken ct);
}
```

Default implementation writes JSONL to disk. Alternative implementations could push to a logging service or in-memory buffer for tests.

---

## 7. Bootstrap Discipline

The new system will be written by agents using the old system. The risk: agents transcribe the old structure into the new language, producing System B that thinks like System A. The installer effort that already happened was a calibration shot for this. The kind of guidance the agent needed there is the kind it will need here, multiplied.

### Failure modes to watch for

**Transcription instead of redesign.** Agent reads a 400-line markdown command, produces a 400-line C# class with sections matching the markdown headings. The class shape is wrong because the markdown shape was wrong.

**Preserving accidental complexity.** The current base64 INVESTIGATION_RESULT round-trip exists because of a settings allowlist mismatch. Agent might port the pattern because it sees it. The new binary has direct Plane access and does not need the round-trip.

**Over-abstraction to match old conventions.** Sees `size-to-model.sh`, creates `ISizeToModelService` with a factory. The abstraction existed in bash because shell scripts are discrete files. C# does not need it; it's a three-line function on a config record.

**Inherited vendor-specific quirks.** CCONF-148's "haiku has literal-text substitution issues" was true at that time, against those prompts, in that harness. In the new system, measure it. The old workaround may not apply or may not even reproduce.

**Mirror infrastructure surviving.** Agent sees `bin/sync-copilot-prompts`, thinks "I should make sure the new system generates these mirrors." The new system has no mirrors. The whole layer dies.

**Backwards compatibility creep.** Agent wants to support "users with the old markdown configs" because it's helpful. Clean break. Old users run the old system; new users get the new one.

### Mitigations

1. Define the typed data model first (Ticket, StateTransition, Brief, Verdict, WorkflowEvent) before any logic ports.
2. Write tests first for each phase. Anchor the agent to behavior, not prior implementation.
3. Forbid the agent from reading old commands while writing new code. Read once for helper algorithms, then close them.
4. After each phase, separately review: did this inherit patterns that only made sense in the old world? Comments referencing the old system are debt to delete, not context to preserve.
5. Use the installer pain as calibration. The kind of guidance the agent needed there is the kind needed here.

---

## 8. Migration Plan

Parallel operation. The old system stays in production until the new system has earned each phase.

### Phase-by-phase cutover order

1. `investigate` (most LLM-heavy, cleanest contract, easiest to validate)
2. `approve` (cuts branches, runs baseline tests)
3. `review` (automated checks plus checklist)
4. `ship` (rebase, test, merge)
5. `chain` (orchestrator over the others; the big one)
6. `new` (ticket creation)
7. `install` (bootstrap into new repo)

### Promotion criteria per phase

- Implementation passes typed unit tests
- Interfaces with adjacent phases are validated
- Five real tickets handled without surprise: no manual intervention required, output equivalent to what the old system would have produced
- Token cost compared to chat-session equivalent shows the expected reduction (at least 5x for single-phase, on track for 10x overall)
- Event log is sane and replayable

### Final cutover

When `chain` ships clean and survives a real 5-ticket chain end-to-end, the markdown corpus (`commands/`) and the entire mirror infrastructure (`bin/sync-*`, `copilot-prompts/`, `plugins/latticeflow/`) get deleted in one commit. That's the cutover. From that point the old system no longer exists in the repo.

### Eval-as-dogfooding

Each phase port produces a small fixture set drawn from past tickets in TradeTrack2 history (and Throughline once it's ported there). Fixtures are not pass/fail benchmarks; they are reference outputs that allow "is the new system at least as good as the old?" comparison. Surprises (unexpected outputs, costs, failures) get logged and reviewed before promoting a phase. This is intentionally less rigorous than formal A/B testing and intentionally more useful than nothing.

---

## 9. First Vertical Slice

Scope: `ticket investigate <id>` end-to-end in C#, invoked from a terminal, no agent host involved.

### Prerequisite: AOT spike

Build a hello-world AOT binary that calls Plane API + spawns a subprocess + parses JSON, on Mac, Windows, Linux. Five hours of work. Validates the AOT pin against the dependency set before any architecture commits to .NET specifics. Gate on this passing.

### Components needed

- Orchestrator binary skeleton with `ticket investigate` subcommand
- PlaneTicketingClient: get, transition, append-description, create-comment, apply-labels (subset needed for investigate)
- AnthropicClient: single InvokeAsync, model Opus
- Helpers: marker comment parser (`[investigated_at: <sha>]`), drift check, slug, doc-only gate, ASCII check
- Brief constructor for the investigate phase
- ClaudeCodeAgent: IWorkerAgent implementation that spawns `claude --print --cwd <worktree>` with the brief
- EventLog: JSONL writer to `.ticket/events/`
- One eval fixture from a past TT2 ticket with a known-good investigation outcome

### Validation

- Run on a real Backlog ticket in TradeTrack2
- Confirm Plane updates land correctly (description appended, labels applied, state transitioned Backlog -> Ready, marker comment posted)
- Compare to baseline chat-session investigation of an equivalent ticket: cost should be at least 5x lower for this single-phase comparison
- Event log captures the full invocation cleanly

### Effort

A few days for the binary slice after the AOT spike clears. The AOT spike itself is the gating prerequisite and should not be skipped.

---

## 10. Risk Register

**Eval gap.** Without stringent A/B testing, qualitative regressions could go undetected across several tickets. Mitigation: five-ticket minimum per phase before promotion, paired with event log review for every promotion. The dogfooding posture is a deliberate trade of formal rigor for shipping velocity.

**Vendor CLI drift.** Claude Code, Codex, and Gemini CLIs are evolving rapidly; flag interfaces and output formats may change without notice. Mitigation: thin per-vendor adapters (target ~50 lines each), version-pin the CLIs the binary targets, fail loudly on unrecognized output rather than silently misinterpreting.

**AOT compatibility.** Native AOT in .NET 8 requires libraries to be trim-friendly and reflection-free in hot paths. Some libraries break under AOT. Mitigation: the AOT spike up front validates the dependency set; if a needed library breaks AOT, find an alternative or accept self-contained deployment for the affected build.

**Bootstrap pattern inheritance.** Covered in Section 7. The mitigation is operational discipline during the port. This risk does not have an architectural fix.

**Plane API breaking changes.** Plane is open source but moves quickly; the REST API could change in ways that break PlaneTicketingClient. Mitigation: ITicketing abstraction insulates the workflow from API changes; PlaneTicketingClient is one file that can be updated independently. Self-hosted Plane gives version control of the upgrade timing.

**Worker output parsing.** Agent CLIs return free-form text that the binary needs to parse into typed WorkerResult. Vendor changes in default verbosity or structured-output formats could break parsers silently. Mitigation: workers are instructed to emit a fenced JSON block as the canonical result; the binary parses that block specifically; if absent or malformed, fail-loud and route to the verifier as `Status.Escalate`.

**Multi-agent verification quality.** Cross-vendor verification is an upgrade in principle, but vendors have different judgment "tastes." A Gemini verifier might flag idiomatic Claude code as suspicious. Mitigation: Verdict.ChecksFailed is a structured list; the orchestrator can filter or weight by which checks matter, and disagreements between vendors are themselves a signal worth surfacing.

**Coordinator-LLM judgment loss.** The current persistent-Opus orchestrator occasionally makes adaptive in-flight decisions (Wave 5 "skip reviewer, spot-verify directly") that the deterministic binary needs to make explicit. Mitigation: every "deterministic decision point" is logged with its context. When a decision surprises in retrospect, the fix is either a hardcoded policy update or promoting the decision to a judgment slot (small scoped LLM call). The judgment does not disappear; it migrates from ambient cognition to discrete API calls.

---

## Appendix: Open questions for the partner review

1. **Naming.** Working placeholder is `workflow-core` for the system, `ticket` for the binary, `.ticket/` for the per-repo directory. Better candidates welcome.

2. **Config format.** TOML is the working assumption (matches modern .NET tooling conventions). YAML and JSON are alternatives. TOML preferred because it has clear precedent in .NET land and reads cleanly.

3. **MCP server packaging.** The binary can expose itself as an MCP server in addition to its CLI. Decide whether this is in scope for v1 or a follow-up (recommendation: stub it in v1, fill it out once CLI is stable).

4. **Replay tooling.** The event log enables replay against alternative models. Building a `ticket replay <session-id> --model X` subcommand would be powerful for evaluation. Probably v1.1, not v1.

5. **The GitHub adapter.** Should it ship with v1 (proves the abstraction) or follow once Plane is solid? Recommendation: ship the interface and the capability flags in v1, leave the GitHub adapter for v1.1 once the abstraction has been validated against a second consumer.

6. **Self-installable scope.** "Install into any repo" implies bootstrapping `.ticket/config.toml`, registering MCP tools where applicable, and validating Plane credentials. The `install` phase needs its own brief design pass before the first slice ships.