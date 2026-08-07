# Throughline Build: Architecture

**Status:** As built
**Last verified:** 2026-07-26

This is the de-facto architecture reference for the current source tree. Public
wire formats have their own stable specifications:

- [event log](build-event-log-format.md)
- [worker debug transcript](build-debug-transcript-format.md)
- [WORKER_RESULT envelope](build-worker-result-envelope.md)

## 1. Context

Throughline Build moves workflow orchestration out of a persistent agent chat
and into deterministic code. The `build` executable reads ticket and repository
state, runs gates and state transitions itself, and starts a coding-agent CLI
only for work that needs an agentic tool loop.

The product name is Throughline Build, the binary and command name are `build`,
the namespace root is `ThroughlineBuild.*`, and per-repository runtime state
lives under `.build/`.

## 2. Goals, Constraints, Non-Goals

### Goals

- Make workflow state, gates, retries, and ticket writes deterministic and
  testable.
- Support interchangeable worker CLIs through one typed contract.
- Ship a cross-platform .NET 10 Native AOT executable.
- Preserve the plan, implement, review, ship, and chain operator workflow.
- Leave enough durable telemetry to diagnose a run without retaining a live
  orchestrator session.

### Constraints

- Plane is the concrete ticketing backend.
- Git is required; implementation and chain workflows use branches and
  worktrees.
- Worker CLIs are external processes and have provider-specific protocols.
- There is no daemon or server. Each invocation loads state, performs work, and
  exits.
- `.build/config.toml` is local configuration and may contain credentials; it
  must remain ignored. `.build/conductor.toml` is machine-local conductor data
  for binary-hosted SOPs; it is also ignored, is recreated per clone by
  `build install` or `build sop install`, and must contain no secrets.
- `.build/` is per clone, not per worktree. A linked worktree holds no copy of
  it, so a verb run inside one resolves config and conductor data from the
  clone's main worktree, and Build does not seed `.build/` into leased trees.

### Non-Goals

- A web UI or dashboard
- A generic workflow engine independent of the shipped ticket lifecycle
- Transparent emulation of every worker provider's permission model
- A second production ticketing adapter
- Backward compatibility with the retired slash-command runtime

## 3. Architectural Philosophy

The main boundary is between deterministic orchestration and agentic work.

- **Deterministic code** owns argument parsing, configuration, Plane transport,
  state checks and transitions, git topology, automated checks, retry policy,
  event logging, and exit codes.
- **Worker agents** own scoped planning, implementation, review, drafting,
  decomposition, and scaffold work. Each worker receives a typed `Brief` and
  must return the shared result protocol.
- **Direct LLM calls** are limited. The current `AnthropicClient` implements
  `ILlmClient`; lifecycle reason translation can fall back to `EchoLlmClient`
  when no model credential is configured.

Typed records cross internal boundaries. The intentionally fuzzy edges are the
ticket backend's HTML representation and agent output. Both are normalized by
dedicated adapters and parsers.

## 4. System Overview

```text
operator
   |
   v
build (ThroughlineBuild.Cli)
   |-- configuration and command composition
   |-- phases, gates, and lifecycle commands
   |-- git/worktree adapter
   |-- Plane adapter
   |-- event and debug writers
   `-- IWorkerAgent
         |-- claude-code
         |-- codex
         |-- gemini
         `-- copilot
```

### Invocation flow

1. Pre-configuration verbs such as `--help`, `init`, `user-guide`, and
   `sop` are dispatched before config loading.
2. Most configured verbs find `.build/config.toml` through the shared repository
   resolver, load it, and compose the configured Plane, git, worker, event, and
   verification services. One resolver answers for config, conductor, and SOP
   catalog paths: git names the tree the verb runs in and the clone's main
   worktree, `.build` data resolves from the main worktree when the verb runs in
   a linked worktree, and the search never climbs past the repository, so a tree
   whose repository has no `.build/config.toml` fails closed instead of adopting
   an ancestor's. Without git, resolution falls back to a walk bounded by the
   first `.git` or `.build` it finds.
3. A ticket phase fetches the current ticket and runs its state and repository
   preconditions.
4. LLM-bearing phases build a provider-specific brief and execute the selected
   `IWorkerAgent` in the appropriate working directory.
5. The adapter normalizes provider output to `WorkerResult`; the phase applies
   gates and ticket writes.
6. Significant activity is appended to the invocation's JSONL event log. With
   `--debug`, worker transport artifacts and the structured transcript are also
   captured.
7. The CLI renders a human summary or the verb's versioned JSON envelope and
   exits with the documented code.

### Data flow

Plane is authoritative for ticket state and relationships. Git is authoritative
for repository history, feature branches, integration branches, and worktrees.
`.build/events/` and `.build/sessions/` are diagnostic records, not a separate
workflow database.

## 5. Component Specifications

### 5.1 Build Binary (Orchestrator)

`ThroughlineBuild.Cli` produces the `build` executable and composes the
class-library projects. The CLI supports configuration/bootstrap commands,
work-item commands, individual pipeline phases, and recursive `chain`.

The project targets `net10.0`, sets `PublishAot=true`, and publishes one native
executable per runtime identifier. See [Building from source](build-command-setup.md).

### 5.2 State Machine

Ticket states are `Backlog`, `Planning`, `Ready`, `InProgress`, `InReview`,
`Done`, and `Cancelled`. Phase classes enforce their own preconditions and use
`ITicketing` for transitions; there is no separate in-memory state-machine
service.

The principal typed phases are `PlanPhase`, `ImplementPhase`, `ReviewPhase`,
`ShipPhase`, `DraftPhase`, `NewPhase`, `DecomposePhase`, and `ReworkPhase`.
`GatePhase` and `ChainPhase` expose richer orchestration-specific results rather
than implementing the generic phase interface directly.

Parent tickets deliberately follow different rules from leaves. The
operator-facing matrix is in the
[user guide](throughline_build_userguide.md#parent-tickets), and recursive
behavior is detailed in [The Grandparent Chain](build-grandparent-chain.md).

### 5.3 Ticketing Backend

[`ITicketing`](../src/ThroughlineBuild.Contracts/ITicketing.cs) is the workflow
contract. `PlaneTicketingClient` is the concrete implementation and also
implements provisioning, connectivity, and project-discovery capabilities.
The interface covers reads, state/lifecycle transitions, comments,
descriptions, labels, issue types, parent/child operations, and typed relation
management.

Plane requests use internal issue UUIDs where the API requires them, but
`PlaneTicketingClient` translates operator-facing IDs such as `TLB-42` at the
boundary. Ticket reads and JSON envelopes return stable parent IDs and direct
child summaries. Work-item type UUIDs are resolved through Plane's optional
work-item-type endpoint when it is available, and 401/403 diagnostics are
reported with repository-local config context without echoing token values or
Plane response bodies.

### 5.4 LLM Client

[`ILlmClient`](../src/ThroughlineBuild.Contracts/ILlmClient.cs) defines
request/response and streaming calls. The current production provider is
`AnthropicClient`; `EchoLlmClient` is a deterministic no-model fallback for
lifecycle reason text. Worker-model selection does not use `ILlmClient`: it is
resolved independently from `[workers.<agent>.sizes]`.

### 5.5 Worker Dispatcher

[`IWorkerAgent`](../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) standardizes
the adapter name, optional progress digester, and asynchronous brief execution.
`WorkerOptions` carries timeout, tool hints, environment overrides, debug and
streaming sinks, size tier, transcript context, and the lean-planning hint.

The four registered adapters are `claude-code`, `codex`, `gemini`, and
`copilot`. Invocation, authentication environment, permissions, model-name
normalization, provider envelopes, and usage extraction remain adapter
responsibilities. See [Worker agent adapter mapping](build-agent-tool-name-mapping.md).

All adapters normalize terminal output with the common `WorkerResultParser`.
Malformed or missing results fail loudly; they are not treated as successful
free-form output.

### 5.6 Helpers

`ThroughlineBuild.Helpers` contains reusable repository and workflow helpers,
including worktree naming, summaries, parent/tree inspection, result metadata
flattening, and scheduling support. `ThroughlineBuild.Git` owns the process
adapter for git. `WorktreeLeaseManager` exposes the same ownership boundary to
a caller-owned conductor: it validates collisions, seed policy, manifest
identity, and teardown containment without constructing a worker agent. A
per-ticket filesystem lock closes concurrent lease races, and creation rollback
tracks branch and worktree ownership separately. The standalone `worktree`,
`gate`, `waves`, and `candidate status` paths load only their consumed config
sections without requiring `[ticketing]`, `[workers]`, or `[events]`, resolving
ticketing secrets, or constructing a Plane client. `build sop` runs in the same
no-worker/no-ticketing band. `sop doctor` reads `.build/conductor.toml`,
validates manifest-recorded or present emitted stubs byte-for-byte against the
catalog, and only consults local `.build/config.toml` for `[[review.checks]]`;
both files come from the same resolved `.build` directory, so a report that
names a repository root and a config path never pairs them with a conductor from
another tree, and emitted stubs are graded in the tree the verb runs in because
they are tracked content rather than machine-local data;
standard `sop brief` runs doctor first, then emits embedded SOP text and the
resolved conductor data, owned catalog paths, and run mode. Admission-only
inspection is a brief run mode with a validated absolute inspection root, a full
40-character inspection SHA, inherited `BUILD_SOP_*` environment values, and an
explicit verb policy; admission input validation happens before doctor reads
conductor data. The inspection root must be a git worktree root in the invoking
repository, so Build does not pair one repository's tree with another
repository's conductor rules. With admission active, mutating verbs refuse before
config bootstrap with the JSON error code `sop_admission_refused`. Doctor can
therefore report an absent local config file as a finding instead of failing
before conductor data is loaded. Unknown keys in conductor TOML are findings,
and the local check list must include at least one setup or gating check so an
advisory-only list cannot satisfy the gate contract. Review invariants remain
structured prose: doctor validates their shape and surfacing data, not the truth
of their statements.
`sop install`, `upgrade`, `uninstall`, and `status` use the embedded catalog as
the authority and treat `.build/sop-manifest.json` as a cache only. Install emits
stubs for every known host by default; `--host` narrows emitted stubs to Claude
or Codex while preserving shared scaffolded paths. Emitted stub files are
content-compared against the current catalog and, for upgrade, against trusted
previous hashes embedded in the current catalog. Scaffolded conductor data is
validated by shape and is never overwritten after creation. Every
catalog target and the manifest path are resolved strictly below the repository
root and are refused if any existing segment is a symlink or reparse point
before a write or delete is attempted.

`ThroughlineBuild.Verification` runs configured `CheckSpec` commands and
returns typed `CheckResult` values. Check roles distinguish setup, gating, and
advisory work. `build gate` exposes that same runner in the invocation directory
without constructing a worker agent; absent declared inputs produce an
inconclusive result rather than a command failure. Repository cleanliness and baseline attribution prevent worker
or environment failures from being mistaken for product regressions.

### 5.7 Brief Constructor

Brief construction is per operation and per provider. `ThroughlineBuild.Briefs`
contains builders and embedded templates for plan, implement, review, draft,
new, decompose, scaffold, and batch work. Builders compose the ticket,
repository evidence, prior-chain context, and relevant policy into a `Brief`;
workers receive only that scoped instruction.

The shared terminal contract is documented in
[WORKER_RESULT Envelope Specification](build-worker-result-envelope.md).

### 5.8 Verifier

Review is an independent worker invocation, not an in-memory continuation of
implementation. `ReviewPhase` runs configured automated checks, builds a review
brief from the ticket and git evidence, and asks the configured review worker
for a typed verdict.

Gating check failures stop before an agent verdict. Advisory failures are
reported to the reviewer but cannot independently force rework. After review,
git-state guards detect and clean up prohibited verifier mutations.

Every configured check runs with its own stdout and stderr captured and its
stdin redirected and closed. A check never inherits the stdin of whatever
invoked `build`, so it reads EOF identically under an interactive terminal, an
agent session, and CI, and a check that prompts fails fast instead of blocking
until its timeout. A gate verdict is a fact about the worktree, never about the
caller.

### 5.9 Ship

`ShipPhase` is deterministic. Its path includes repository hygiene, target/base
resolution, remote reconciliation when enabled, rebase, conflict-marker
scanning, regression checks with baseline attribution, fast-forward landing,
optional branch/worktree cleanup, Plane writes, and push.

Push is enabled by default when the configured remote exists. `--no-push` or
`[ship] push = false` selects local-only behavior. Chain leaf ships target a
local integration branch and never push it; the outermost successful chain
lands the accumulated integration branch on the configured target and performs
the single push.

### 5.10 Event Log

Each invocation writes append-only JSONL under the configured event directory,
which defaults to `.build/events/`. Optional session context adds project,
workspace, and build identifiers without changing the original event fields.

`--debug` writes transport captures under `.build/sessions/<run-stem>/` and a
structured `transcript.jsonl` when the adapter can derive it. These contracts
have different scopes:

- [Event Log File Format](build-event-log-format.md) describes orchestration events.
- [Worker Debug Transcript Format](build-debug-transcript-format.md) describes
  per-turn diagnostic telemetry.
- [WORKER_RESULT Envelope Specification](build-worker-result-envelope.md) describes
  the model-to-adapter terminal protocol.

## 6. Interfaces & Contracts

`ThroughlineBuild.Contracts` is the shared dependency leaf. Important current
types include:

- `Ticket`, which carries both human `Id` and internal `Uuid`;
- `Brief`;
- `WorkerResult`, including `Metadata`, named `Blocks`, and optional batch
  `Tickets`;
- `WorkflowEvent` and the 14-value `EventKind` enum;
- the 11-value `Phase` enum (`Plan`, `Implement`, `Review`, `Ship`, `Chain`,
  `New`, `Command`, `Draft`, `Scaffold`, `Decompose`, `Gate`);
- `IWorkerAgent`, `IWorkflowPhase`, `ITicketing`, `ILlmClient`, `IGitClient`,
  and `IEventSink`.

The source declarations are normative. The three public format documents linked
above are normative for persisted or model-facing wire shapes.

### Core data types

```csharp
public record WorkerResult(
    Status Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason,
    IReadOnlyDictionary<string, object> Metadata,
    IReadOnlyDictionary<string, string>? Blocks = null,
    IReadOnlyList<BatchTicketResult>? Tickets = null);

public enum Status { Ok, NeedsRework, Failed, Escalate }

public record WorkflowEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    EventKind Kind,
    string TicketId,
    Phase Phase,
    IReadOnlyDictionary<string, object> Data);
```

### Ticketing backend

`ITicketing` is intentionally wider than a minimal CRUD client because the
workflow depends on comments, labels, typed relations, parent rollups, child
creation, lifecycle markers, and exact relation removal. Optional interface
members provide safe defaults for test fakes, but production composition uses
the Plane implementation.

### LLM client

`ILlmClient` accepts provider model IDs, messages, and invocation options and
returns typed content and usage. Its streaming surface yields text, usage, and
completion events. This is separate from worker-agent subprocess execution.

### Worker agent

Agent names are exact, case-sensitive configuration keys. The factory is a
provider-independent registry; `WorkerAgentBuilder` constructs the configured
adapter and its size tiers.

### Git client

`IGitClient` covers worktree creation/removal, branch creation/switching,
diffs, fetch/rebase/merge/push, divergence probing, cleanliness inspection,
commit/log queries, and safe cleanup operations. `ProcessGitClient` is the
production implementation; tests use focused fakes.

### Workflow phase

`IWorkflowPhase.RunAsync` returns the common `PhaseResult`. Concrete phases also
expose richer typed result records to callers that need phase-specific fields.
Chain uses `ChainResult` and `ChainOutcome` because recursive child results,
skips, and partial stops do not fit the flat phase result.

### Verifier

Verification is represented by configured `CheckSpec` values, typed
`CheckResult` evidence, and the review worker's `Verdict`. The current code
does not use the obsolete `ClaudeCodeReviewer` abstraction from the original
proposal.

### Verification types

Check specs include the executable, discrete argument list, timeout, role, and
optional required paths. Results retain pass/skip state, exit code, bounded
stdout/stderr tails, elapsed time, role, and diagnostic metadata used by gate,
review, rework, and ship.

### Event sink

`IEventSink.EmitAsync` accepts a `WorkflowEvent`. `JsonlEventSink` is the disk
writer; `RecordingEventSink` adds in-memory observation while forwarding to
another sink.

## 7. Bootstrap Discipline

`build init` materializes the embedded config template. Interactive connected
mode can discover or create a Plane project; non-interactive flags can supply
the exact values. `--token-env` writes an environment-variable name instead of
a token value.

`build setup` provisions repository ignore rules and the required Plane
states/labels, verifies connectivity, and runs Claude transport preflight where
applicable. Both operations are idempotent within their documented overwrite
rules.

Configuration and diagnostic directories remain local. Templates are embedded
resources so the native binary does not depend on source-tree-relative files.
The operator guide is likewise embedded and materialized by `build user-guide`.

## 8. Evolution Discipline

The repository ships the C# workflow and no longer uses the former
slash-command orchestrator for ticket operations.

Current evolution happens behind typed contracts and focused tests:

1. change the relevant adapter, phase, or helper;
2. preserve persisted/public contracts or version them deliberately;
3. update embedded templates and checked-in generated copies together;
4. publish and exercise the Native AOT binary on supported runtime identifiers.

## 9. Current Lifecycle

The current lifecycle is:

```text
Backlog --plan--> Ready --implement--> InReview
                              ^             |
                              |--rework-----|
                                            |
                                  review Pass
                                            |
                                          ship
                                            |
                                           Done
```

`chain` resumes a leaf from its current state and recursively processes live
children for a parent. Internal nodes use integration branches; only the root
landing moves the configured target. See
[The Grandparent Chain](build-grandparent-chain.md).

## 10. Risk Register

- **Provider CLI drift:** keep adapters thin, test their argument construction
  and output parsing, and fail loudly on unknown formats.
- **Ticketing partial failure:** validate before writes where possible, keep
  relation IDs explicit, and surface non-atomic creation outcomes.
- **Git topology mistakes:** require clean tracked state, use explicit
  worktrees/branches, preserve resumable work on failure, and avoid force
  operations in automatic paths.
- **Environment failures mistaken for code failures:** run baseline controls
  and preserve check evidence.
- **Schema drift:** keep event, debug transcript, and worker envelope specs
  synchronized with writers/readers and their focused tests.
- **AOT-only failures:** use source-generated JSON metadata and exercise
  reflection-disabled paths in tests.

## 11. AOT Serialization Traps

`ThroughlineBuild.Cli.csproj` sets `<PublishAot>true</PublishAot>`. AOT and
reflection-disabled test projects cannot rely on `System.Text.Json` runtime
metadata. Calls such as `JsonSerializer.Deserialize<T>(json, options)` may
throw `NotSupportedException` even when the same code appears to work in a
normal test host.

### Pattern to follow

Use a source-generated `JsonTypeInfo<T>`:

```csharp
// Wrong for reflection-disabled execution
var dto = JsonSerializer.Deserialize<MyDto>(json, options);

// Correct
var dto = JsonSerializer.Deserialize(json, MyJsonContext.Default.MyDto);
```

Register every wire DTO with `[JsonSerializable]`. The shared worker parser
registers `WorkerResultDto` and `BatchWorkerResultDto` in
`WorkersCommonJsonContext`; each provider and public writer owns the context for
its own DTOs.

### Type constraints under source-gen

Polymorphic `object` values require deliberate handling. The event writer uses
registered object/dictionary/list shapes and normalization rather than assuming
arbitrary runtime types are serializable. Parser DTOs use concrete types or
`JsonElement` where the payload is open-ended.

Enum wire representation must also be explicit. Use the appropriate
source-generated context and converters instead of assuming a runtime options
policy will be discovered through reflection.

### Test coverage rule

Tests for JSON hot paths must exercise reflection-disabled execution, either
through a test project with
`JsonSerializerIsReflectionEnabledByDefault=false` or an explicit
`AppContext` switch. `WorkerResultParserAotRegressionTests`,
`JsonlEventSinkListValueTests`, and the scaffold parser tests are current
examples.
