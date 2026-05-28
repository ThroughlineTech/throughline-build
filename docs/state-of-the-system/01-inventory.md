# 01 - Inventory

Every command, module, service, script, and tool currently in the repository, with a one-paragraph high-level description, inputs, outputs, and the major components it composes with. Status tags follow the convention defined in the index: Functional, Partial, Legacy, Aspirational, Broken.

For interface contracts see [07-contracts.md](07-contracts.md). For phase orchestration detail see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## CLI verbs (the `build` binary)

All verbs are dispatched from [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs) (single top-level entry point, ~1172 lines). Usage text lives in [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs).

### `build plan <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:687-737](../../src/ThroughlineBuild.Cli/Program.cs#L687-L737). Investigates a `Backlog` ticket and produces a plan written to the ticket description, plus risk/size labels and a `[planned_at: <sha>]` marker comment.

- **Inputs:** ticket id (positional); `--debug | --quiet`, `--summary-json` flags. Reads `.build/config.toml` (TOML), Plane ticket via API, current `main` SHA via `git rev-parse`, top-level directory entries of cwd.
- **Side effects:** spawns the `claude` worker as a subprocess in the main worktree (no branch cut yet), writes Plane HTML description + size/risk labels + one comment, appends events to `.build/events/<stem>.jsonl`, optionally captures worker stdio to `.build/sessions/<stem>/` when `--debug`.
- **Exits:** 0 success, 1 phase failure, 2 missing/unknown id, 3 missing secret, 4 infra failure.
- **Invokes:** `PlanPhase` ([src/ThroughlineBuild.Phases/PlanPhase.cs:55-143](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L55-L143)), `PlanBriefBuilder`, `PlaneTicketingClient`, `ClaudeCodeAgent`, `JsonlEventSink`, `PhaseSummaryBuilder.BuildPlan`.

### `build implement <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:738-802](../../src/ThroughlineBuild.Cli/Program.cs#L738-L802). Cuts a worktree, transitions `Ready -> InProgress`, dispatches the implementer worker, then transitions `InProgress -> InReview` after recording `[implemented_at: <sha>]`.

- **Inputs:** ticket id; same flags as `plan`. Reads worktree layout from `PhaseWorktreeLayout.Compute` ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:3-17](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L3-L17)), prior `[planned_at: <sha>]` marker via `MarkerParser`.
- **Side effects:** `git worktree add -b ticket/<slug> .worktrees/ticket-<slug> <baseRef>`, runs worker inside that worktree, writes events, writes Plane comment, transitions state. Drift between `planned_at` SHA and current `main` SHA is emitted as a `GateFailure` warning ([src/ThroughlineBuild.Phases/ImplementPhase.cs:104-112](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L104-L112)) but does not block.
- **Exits:** same as `plan`.
- **Invokes:** `ImplementPhase`, `ImplementBriefBuilder`, `ProcessGitClient.CreateWorktreeAsync`, `PlaneTicketingClient`, `ClaudeCodeAgent`.

### `build review <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:1051-1104](../../src/ThroughlineBuild.Cli/Program.cs#L1051-L1104). Reads the feature branch diff and `[implemented_at]` marker, runs configured automated checks, dispatches a verifier worker, records `Verdict { Pass | Rework | Fail }`. On `Rework` transitions `InReview -> InProgress`; on `Pass`/`Fail` leaves state untouched.

- **Inputs:** ticket id; flags. Reads the worktree by branch via `IGitClient.ListWorktreesAsync`, the feature branch diff via `IGitClient.DiffAsync(baseRef, branch, includePatchContent: true)`, all `review.checks` from config.
- **Side effects:** runs each `CheckSpec` as a subprocess (timeouts enforced via process-tree kill, [src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs:128](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L128)), spawns the verifier worker, posts one Plane comment with the verdict.
- **Exit codes:** 0 Pass, 1 Rework/Fail, 4 verifier infra failure ([src/ThroughlineBuild.Cli/Program.cs:1095-1103](../../src/ThroughlineBuild.Cli/Program.cs#L1095-L1103)).
- **Invokes:** `ReviewPhase`, `ReviewBriefBuilder`, `ClaudeCodeReviewer` (an `IVerifier` that wraps `ClaudeCodeAgent`), `AutomatedChecksRunner`.

### `build ship <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:803-896](../../src/ThroughlineBuild.Cli/Program.cs#L803-L896). Deterministic phase, no worker subprocess. Fetches, rebases the feature branch onto `<remote>/<base>` (falls back to local `<base>` when no remote, [src/ThroughlineBuild.Phases/ShipPhase.cs:170-180](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L170-L180)), scans for conflict markers, runs `ship.regression_checks`, fast-forward-merges into local base branch, posts `[shipped_at: <sha>]`, transitions `InReview -> Done`, then decrufts the worktree.

- **Inputs:** ticket id; `--debug` accepted but no-op (no worker).
- **Side effects:** mutates `main` branch in main worktree (FF merge only - never pushes), removes feature worktree from disk, optionally deletes feature branch (`ship.delete_feature_branch`, default `true`).
- **Exit codes:** 0 success or post-success decruft warning, 1 gate failure (rebase/conflict-markers/regression), 4 infra failure (state, fetch, FF merge) ([src/ThroughlineBuild.Cli/Program.cs:884-895](../../src/ThroughlineBuild.Cli/Program.cs#L884-L895)).
- **v1 contract:** local merge only, no `git push origin main`. Match comment in [src/ThroughlineBuild.Cli/CliUsage.cs:12](../../src/ThroughlineBuild.Cli/CliUsage.cs#L12) and architecture doc Section 5.9.
- **Invokes:** `ShipPhase`, `ProcessGitClient` (`FetchAsync`, `RebaseAsync`, `FastForwardMergeAsync`, `DeleteBranchAsync`), `AutomatedChecksRunner`, `ConflictMarkerScanner`, `WorktreeDecrufter`.

### `build chain <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:897-981](../../src/ThroughlineBuild.Cli/Program.cs#L897-L981). Single-ticket end-to-end: routes to the appropriate starting phase based on current state (`Backlog`->Plan, `Ready`->Implement, `InReview`->Review), runs the implement-review loop with `MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:11](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L11)), then ships. Streams a one-line summary per phase to stdout via `onStep`.

- **Rejects multi-id explicitly** ([src/ThroughlineBuild.Cli/Program.cs:62-69](../../src/ThroughlineBuild.Cli/Program.cs#L62-L69)) - multi-ticket dispatch is planned for a future release.
- **Exit codes:** 0 Completed, 2 RefusedInitialState, 3 StoppedAtPlan, 4 StoppedAtImplement, 5 StoppedAtReview, 6 ReworkCapExceeded, 7 StoppedAtShip ([src/ThroughlineBuild.Cli/Program.cs:958-970](../../src/ThroughlineBuild.Cli/Program.cs#L958-L970)).
- **Invokes:** `ChainPhase` wired with per-phase factories closed over the shared `PlaneTicketingClient`, `ClaudeCodeAgent`, and `IEventSink`; `DefaultChainRunner` is the thin runner the `ChainCommand` calls.

### `build rework <ticket-id> [--feedback "..."]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:982-1050](../../src/ThroughlineBuild.Cli/Program.cs#L982-L1050). Re-implements a ticket whose last `Verdict` was `Rework`. Validates state is `InProgress`, retrieves the most recent `Rework` verdict from the JSONL event log via `ReviewFeedbackRetriever` (or uses the `--feedback` text if provided), and re-runs the implement step with that feedback woven into the brief.

- **Inputs:** ticket id; optional `--feedback "..."`. Reads from `.build/events/` (newest-first).
- **Exit codes:** 0 Implemented, 2 TicketNotInProgress, 3 NoFeedbackAvailable, 4 ImplementFailed ([src/ThroughlineBuild.Cli/Program.cs:1031-1041](../../src/ThroughlineBuild.Cli/Program.cs#L1031-L1041)).
- **Invokes:** `ReworkPhase` -> `ImplementPhase` (with `ReviewFeedback` in `ImplementPhaseOptions`); `DefaultReworkRunner`.

### `build new <body-path | text | -> [--title --type --label --review --print-template]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:249-519](../../src/ThroughlineBuild.Cli/Program.cs#L249-L519). Three modes selected by [src/ThroughlineBuild.Cli/NewVerbArgumentClassifier.cs](../../src/ThroughlineBuild.Cli/NewVerbArgumentClassifier.cs):

1. **File mode** - positional arg is an existing file: `NewPhase` is invoked directly with that body path.
2. **Draft mode** - positional arg is free-form text (or stdin when `-`): `DraftPhase` spawns `claude` to draft a body conforming to the template, then `NewPhase` files it. With `--review`, an interactive loop ([src/ThroughlineBuild.Cli/ReviewLoop.cs](../../src/ThroughlineBuild.Cli/ReviewLoop.cs)) lets the operator accept / edit / regenerate / quit before filing.
3. **`--print-template`** - emits the embedded template at [src/ThroughlineBuild.Commands/Templates/new-ticket-body.md](../../src/ThroughlineBuild.Commands/Templates/new-ticket-body.md) to stdout for redirection.

- **Side effects:** creates a Plane work item (no parent), applies labels, optionally writes debug artifacts. NewPhase validates title presence (fatal) and emits non-fatal warnings for missing Acceptance / Out-of-scope sections, short body, and missing Type ([src/ThroughlineBuild.Phases/NewPhase.cs:99-118](../../src/ThroughlineBuild.Phases/NewPhase.cs#L99-L118)).
- **Invokes:** `NewVerbArgumentClassifier`, `DraftPhase` -> `DraftBriefBuilder` -> `ClaudeCodeAgent`, `ReviewLoop`, `NewPhase` -> `PlaneTicketingClient.CreateTicketAsync`, `NewCommand`.

### `build scaffold <op-doc-path> [--validate-only --dry-run --accept-warnings]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:521-599](../../src/ThroughlineBuild.Cli/Program.cs#L521-L599). Parses a Markdown "op doc" describing a plan -> brief hierarchy and creates the matching ticket tree in Plane: one plan-ticket per plan, one brief-ticket per brief, parent-linked via `ITicketing.SetParentAsync`. Failures partway through leave a partial tree that the operator must inspect.

- **Inputs:** op-doc path. Format defined by [src/ThroughlineBuild.Scaffold/OpDocParser.cs](../../src/ThroughlineBuild.Scaffold/OpDocParser.cs) - requires `# Operation: <slug>`, H2s `Why this exists`, `Dispatch order`, `What done looks like`, plans as `## Plan <A>: <name>`, briefs as `#### Brief NN: <slug>`. Validation rules in [src/ThroughlineBuild.Scaffold/OpDocValidator.cs](../../src/ThroughlineBuild.Scaffold/OpDocValidator.cs).
- **Exit categories** (overrides global codes 2/3): EXIT:Clean=0, EXIT:ValidationError=2, EXIT:PartialCreation=3.
- **Invokes:** `OpDocParser`, `OpDocValidator`, `ScaffoldPhase`, `BriefHtmlRenderer`, `ScaffoldCommand`.

### `build amend <ticket-id> [--size S|M|L] [--note "..."]` - Functional
[src/ThroughlineBuild.Commands/AmendCommand.cs](../../src/ThroughlineBuild.Commands/AmendCommand.cs). Updates a non-terminal ticket: replaces the `size:*` label and/or appends a dated `<h3>Context Note</h3>` paragraph to the description. Requires at least one of `--size` or `--note`.

### `build close <ticket-id> <reason>` - Functional
[src/ThroughlineBuild.Commands/CloseCommand.cs](../../src/ThroughlineBuild.Commands/CloseCommand.cs). Translates the reason via Anthropic Haiku ([src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs)), posts a `<strong>wontfix:</strong>` comment, transitions `-> Cancelled`, attempts a rollup on the parent ticket, then decrufts any associated worktree. Warns on unmerged `ticket/*` branches (does not block).

### `build defer <ticket-id> <reason>` - Functional
[src/ThroughlineBuild.Commands/DeferCommand.cs](../../src/ThroughlineBuild.Commands/DeferCommand.cs). As `close`, with marker `<strong>deferred:</strong>` and a stderr note that branches are left in place for later reopen. A TODO at [src/ThroughlineBuild.Commands/DeferCommand.cs:116](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L116) marks a v1.1 "rebuild rollup-preview" step that is not yet implemented.

### `build reopen <ticket-id> [reason]` - Functional
[src/ThroughlineBuild.Commands/ReopenCommand.cs](../../src/ThroughlineBuild.Commands/ReopenCommand.cs). Only valid from `Done` or `Cancelled`. Scans most recent comments newest-first for prior `deferred:` / `wontfix:` markers, picks a target state via `DetermineTargetState` (`Done` -> `Backlog`; `Cancelled + deferred + has Implementation Plan` -> `Ready`; otherwise `Backlog`), posts `<strong>reopened:</strong> from <prior_marker> - <reason>`, transitions.

### `build --help` / `build help` - Functional
[src/ThroughlineBuild.Cli/Program.cs:22-26](../../src/ThroughlineBuild.Cli/Program.cs#L22-L26). Prints `CliUsage.UsageText`.

### Verbs reserved but not implemented
`install` is named in the architecture doc Section 5.1 and 8 (cutover sequence) but is **not implemented** in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs); attempting `build install` falls through to the "Unknown subcommand" branch at line 601.

---

## Library projects (one per `src/ThroughlineBuild.*/`)

Dependency graph (leaf -> root): `Contracts` -> `Briefs`, `Verification`, `Git`, `Helpers`, `EventLog`, `Plane`, `Anthropic`, `JudgmentSlots`, `Workers.ClaudeCode`, `Scaffold` -> `Phases`, `Commands` -> `Cli`.

| Project | Status | Role |
|---|---|---|
| `ThroughlineBuild.Contracts` | Functional | Interfaces, records, enums. No I/O. See [07-contracts.md](07-contracts.md). |
| `ThroughlineBuild.Briefs` | Functional | Builds the markdown brief handed to a worker subprocess. Loads templates from embedded resources at [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/) (draft.md, plan.md, implement.md, review.md). |
| `ThroughlineBuild.Helpers` | Functional | Pure helpers (slugs, drift, doc-only, conflict-marker scan, marker parsing, worktree layout, summary builders/renderers) plus the I/O-bearing `WorktreeDecrufter` and `MainWorktreeResolver`. Some helpers (`DocOnlyDetector`, `DriftComparator`) have no production caller today - tested but unwired. |
| `ThroughlineBuild.Git` | Functional | `ProcessGitClient` spawns `git` subprocesses; covers 15+ methods (`RevParseAsync`, `ListWorktreesAsync`, `CreateWorktreeAsync`, `DiffAsync`, `FetchAsync`, `RebaseAsync`, `FastForwardMergeAsync`, `DeleteBranchAsync`, `IsAncestorAsync`, `RemoteExistsAsync`, ...). `BaseRefResolver` chooses `origin/main` then falls back to `main`. |
| `ThroughlineBuild.EventLog` | Functional | `JsonlEventSink` writes append-only JSONL to `.build/events/<stem>.jsonl`; `RecordingEventSink` mirrors to memory for in-process summary builders. `EventLineDto` and `EventLogJsonContext` carry source-gen JSON for AOT. `ReviewFeedbackRetriever` scans event logs newest-first to recover the latest `Rework` verdict. `SessionFileNameBuilder` produces the filename stem (`{project}-{ticket_or_slug}-{verb}-{yyyy-MM-dd-HHmmss}`). |
| `ThroughlineBuild.Verification` | Functional | `AutomatedChecksRunner` spawns each `CheckSpec` as a subprocess with per-spec timeout (process-tree kill on timeout) and tails 4 KiB of stdout/stderr. `ClaudeCodeReviewer` is the v1 `IVerifier` - it builds a review brief and runs `ClaudeCodeAgent` as the verifier subprocess. Cross-vendor verifiers are deferred. |
| `ThroughlineBuild.Plane` | Functional | `PlaneTicketingClient` is the sole `ITicketing` implementation. 11 public API methods (GET/PATCH/POST against `/api/v1/workspaces/{slug}/projects/{id}/issues/`), Polly retry on 429/5xx, state-name and label-name caches loaded lazily and held in memory. Returns `BackendCapabilities(TypedRelations: true, TypedLabels: true, RichHtmlComments: true, Attachments: true)`. Hardcoded state name map ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:163-173](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L163-L173)). No GitHub or Linear adapter. |
| `ThroughlineBuild.Anthropic` | Partial | `AnthropicClient` implements `ILlmClient.InvokeAsync` (non-streaming) with Polly retry. `InvokeStreamAsync` throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)). Header `anthropic-version` is hardcoded `2023-06-01` in [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:6](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L6). Only direct caller in production today is `ReasonTranslator`. |
| `ThroughlineBuild.JudgmentSlots` | Functional | One slot today: `ReasonTranslator` (Anthropic Haiku, system prompt to translate-to-English). Used by `CloseCommand`, `DeferCommand`, `ReopenCommand`. Architecture Section 3 sketches this as a tier; expansion is open. |
| `ThroughlineBuild.Workers.ClaudeCode` | Functional | `ClaudeCodeAgent` is the sole `IWorkerAgent` implementation; spawns `claude --print --verbose --output-format stream-json` with brief on stdin. Parses NDJSON stream events; extracts the `WORKER_RESULT` JSON envelope (reverse scan so the last envelope wins). `WorkerProgressDigest` renders one-line stderr digest from stream events. `--debug` writes `worker-stdin.txt`, `worker-stdout.txt`, `worker-stderr.txt`, `envelope-result.txt`, `worker-result.json`. No Codex / Gemini agent today - architecture goal but no source. |
| `ThroughlineBuild.Scaffold` | Functional | Op-doc parser, validator, HTML brief renderer, `ScaffoldPhase` (does the Plane writes), and the typed `OpDoc` model. |
| `ThroughlineBuild.Phases` | Functional | The eight phase classes (`PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`). `EarlyExitManifest` writes a `phase-status.json` to the debug capture dir when a phase exits before the worker runs. |
| `ThroughlineBuild.Commands` | Functional | `ITicketCommand` implementations and runners (`AmendCommand`, `ChainCommand`, `CloseCommand`, `DeferCommand`, `NewCommand`, `ReopenCommand`, `ReworkCommand`, `ScaffoldCommand`; `DefaultChainRunner`, `DefaultReworkRunner`; `TicketCommandRegistry`; `BodyTemplateLoader`). |
| `ThroughlineBuild.Cli` | Functional | `Program.cs` (verb dispatch + DI wiring), `Config.cs` (TOML loader + secrets resolver), `CliUsage.cs`, `NewVerbArgumentClassifier.cs`, `ReviewLoop.cs`, `IConsole.cs`. |

---

## Tools (`src/tools/`)

Both are single-file C# projects compiled to AOT binaries by `build.sh`. Source: [src/tools/analyze-event-log.cs](../../src/tools/analyze-event-log.cs), [src/tools/token-audit.cs](../../src/tools/token-audit.cs).

### `analyze-event-log` - Functional
Reads one or more `.build/events/*.jsonl` files and prints per-phase token totals (input / output / cache-read / cache-write), estimated USD cost (per-model pricing table covering Opus 4, Sonnet 4, Haiku 4 families), wall time, and a chain summary (outcome, phases, rework rounds). Output to stdout, no files written.

Two artifacts ship today: [bin/analyze-event-log.exe](../../bin/analyze-event-log.exe) (the C# AOT build) and [bin/analyze-event-log.py](../../bin/analyze-event-log.py) (the pre-port Python reference). Both share the same input format; the Python copy is a historical reference and is not invoked by anything in the repo.

### `token-audit` - Functional
Extracts Claude Code session metadata from JSONL files under `~/.claude/projects/{encoded-repo-path}/`. Subcommands: `latest` (find newest session file), `extract <file|dir|glob>...` (parse and emit one JSON line per file with `label, command, session_id, ts, wall_clock_ms, model, input, output, cache_read, cache_create, subagent_*` totals), or no-arg (combined). Reads Claude Code's own session log, not the Throughline event log. Compiles to [bin/token-audit.exe](../../bin/token-audit.exe).

---

## Scripts and CI

### `build.sh` - Functional
[build.sh](../../build.sh). 27 lines. Publishes three AOT binaries for one RID (defaults to `win-x64`, override via `$RID`) and copies them into `bin/`. Targets: `src/ThroughlineBuild.Cli` -> `build`, `src/tools/token-audit.cs` -> `token-audit`, `src/tools/analyze-event-log.cs` -> `analyze-event-log`.

### `.github/workflows/build.yml` - Functional
[.github/workflows/build.yml](../../.github/workflows/build.yml). Matrix CI across `{macos-latest, windows-latest, ubuntu-latest}`. On push/PR to `main`: `dotnet restore`, `dotnet test --no-restore`, `dotnet publish src/ThroughlineBuild.Cli -r <rid>`, upload AOT artifact. No release-tagging or deploy.

### `.gitattributes` - Functional
[.gitattributes](../../.gitattributes). Pins LF line endings for `src/ThroughlineBuild.Briefs/Templates/*.md`, `tests/ThroughlineBuild.Briefs.Tests/Templates/*.md`, and snapshot test data. Keeps tests deterministic across Windows checkouts.

---

## Test projects (`tests/`)

Fourteen xUnit projects, ~819 test methods across ~200 files. Framework: `xunit 2.6.2` + `Microsoft.NET.Test.Sdk 17.8.0`, all targeting net8.0. Largest suites: `Cli.Tests` (185), `Phases.Tests` (126), `Scaffold.Tests` (93), `Workers.ClaudeCode.Tests` (84). Shared doubles include `StubTicketing`, `StubWorker`, `StubSink`, `FakeMessageHandler`, `FakeConsole`, `FakeGitClient`, `FakeLlmClient`, `FakeTicketing`, `FakeEventSink`, `FakeChainRunner`. Snapshot infra lives in [tests/ThroughlineBuild.Briefs.Tests/SnapshotFixtures.cs](../../tests/ThroughlineBuild.Briefs.Tests/SnapshotFixtures.cs) and `SnapshotLoader.cs`.

AOT regression coverage is concentrated in [tests/ThroughlineBuild.Workers.ClaudeCode.Tests/ClaudeCodeAgentTests.cs](../../tests/ThroughlineBuild.Workers.ClaudeCode.Tests/ClaudeCodeAgentTests.cs) (`WorkerResultParserAotRegressionTests` is the reference example called out in architecture Section 11).

Test projects do **not** inherit `PublishAot=true` from the Cli project (architecture Section 11). A parser test that passes under the normal runner does not prove AOT correctness; AOT-sensitive paths must set `AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)` before invoking the parser under test.

---

## `.claude/` (ticket workflow harness, external)

This directory is consumed by the `/ticket-*` slash commands in the Claude Code harness, not by `build`. It is included here because every contributor to the repo encounters it.

- [.claude/plane-config.md](../../.claude/plane-config.md) - workspace UUID, project UUID, state UUIDs, label UUIDs, pre-built Plane view URLs.
- [.claude/ticket-config.md](../../.claude/ticket-config.md) - stack info, `dotnet test` as the test command, `dotnet build` as the build command, `git fetch origin` + `dotnet test` as the preflight, no deploy and no lint command.
- `.claude/plane-rest/` - cached Plane API response fixtures.
- `.claude/tmp-batch/` - transient artifacts from op-doc scaffolding sessions.
- `.claude/worktrees/` - empty placeholder for git worktree state.

---

## Loose ends

- **`install` verb** named in architecture Sections 5.1, 8, and 9 but not present in `Program.cs`. Verbs `plan|implement|review|ship|chain|rework` and `new|scaffold|amend|close|defer|reopen` are exhaustively enumerated at [src/ThroughlineBuild.Cli/Program.cs:601-606](../../src/ThroughlineBuild.Cli/Program.cs#L601-L606); anything else returns exit 2.
- **Non-Anthropic LLM clients** (`OpenAIClient`, `GoogleClient`) named in architecture Section 5.4 do not exist in the codebase. The `ILlmClient` contract is in place; only `AnthropicClient` implements it.
- **Non-Claude-Code worker agents** (`CodexAgent`, `GeminiAgent`) named in architecture Section 5.5 do not exist. `IWorkerAgent.Name` is hardcoded `"claude-code"` by the only implementation.
- **GitHub ticketing adapter** named in architecture Section 5.3 is not present. The `BackendCapabilities` plumbing exists in `ITicketing` but no code inspects it - capability-driven gating is aspirational.
- **`AnthropicClient.InvokeStreamAsync`** throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)).
- **`Size.S` / `Size.L` / `Risk.Low` / `Risk.High`** enum values are declared in [src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs) but never constructed in source - `PlaneTicketingClient` always returns `Size.M` and `Risk.Medium` from `GetAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:204-205](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L204-L205)). Label-driven extraction is unimplemented.
- **`DocOnlyDetector`** and **`DriftComparator`** in `Helpers` have unit tests but no production callers - aspirational gates not yet wired into any phase.
- **`docs/op-docs/op-14-new-agent-foundation.md`** is an empty stub - the multi-agent foundation that would justify a second `IWorkerAgent` lives only as a placeholder.
- **`bin/analyze-event-log.py`** is a pre-port reference, not used by the C# code or build scripts.
- **`ClaudeCodeReviewer.FlattenLlmUsage` / `UnwrapJsonElement`** ([src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs:141-183](../../src/ThroughlineBuild.Verification/ClaudeCodeReviewer.cs#L141-L183)) duplicate `LlmUsageFlattener` and have no caller after a refactor.
- **`DeferCommand` v1.1 TODO** at [src/ThroughlineBuild.Commands/DeferCommand.cs:116](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L116) - rebuild rollup preview for parent when defer removes a ticket from a feature wave.
