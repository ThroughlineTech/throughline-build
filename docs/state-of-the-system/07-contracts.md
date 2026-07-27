# 07 - Contracts

Last refreshed: 2026-06-11 (HEAD 3a73eb9); older claude-config workflow corrected 2026-07-26 (HEAD 5d7eb6d)

The inter-project type contracts inside this repo, and the artifacts shared with sibling systems (Plane, Claude Code, the older claude-config slash commands).

This document points at the types - it does not reproduce the bodies. For type definitions, follow the cited paths.

For phase orchestration that uses these types, see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md). For external service contracts, see [03-external-dependencies.md](03-external-dependencies.md).

---

## Inter-project contracts (within this repo)

### Dependency graph

```
Contracts (leaf, no project refs)
    +-- Briefs
    +-- Helpers
    +-- Git
    +-- EventLog
    +-- Plane
    +-- ModelClient (leaf, no project refs)
    +-- Anthropic ........... refs Contracts + ModelClient
    +-- JudgmentSlots ....... refs Contracts
    +-- Workers.Common ...... refs Contracts (parsers; internals shared widely - see note)
    +-- Workers.ClaudeCode .. refs Contracts + Workers.Common
    +-- Workers.Codex ....... refs Contracts + Workers.Common
    +-- Workers.Gemini ...... refs Contracts + Workers.Common
    +-- Workers.Copilot ..... refs Contracts + Workers.Common
    +-- Scaffold ............ refs Contracts
    +-- Verification ........ refs Contracts + Briefs + Workers.Common
    +-- Phases .............. refs Contracts + Briefs + Git + Helpers + Verification + Workers.Common
                                    |
                                    v
                                Commands (refs Contracts + Helpers + JudgmentSlots + Phases + Scaffold)
                                    |
                                    v
                                   Cli (refs all four Workers.* + ModelClient + ...)
```

Verify by reading `<ProjectReference Include="..." />` lines in each `.csproj`. Change since the last refresh: **`Phases` and `Verification` now reference `Workers.Common`** directly - the phase layer consumes the (still-`internal`) `FencedBlockResolver` / `CompletionClaimParser` via `InternalsVisibleTo` grants in [ThroughlineBuild.Workers.Common.csproj:13-52](../../src/ThroughlineBuild.Workers.Common/ThroughlineBuild.Workers.Common.csproj#L13-L52). The grant list now covers the four worker assemblies, `Phases`, `Verification`, and all their test assemblies. An empty `src/ThroughlineBuild.Linear/` directory exists (stale build artifacts only - no csproj, not in `throughline-build.sln`).

### Core types live in `ThroughlineBuild.Contracts`

| File | Type(s) | Notes |
|---|---|---|
| [src/ThroughlineBuild.Contracts/Models/Ticket.cs](../../src/ThroughlineBuild.Contracts/Models/Ticket.cs) | `Ticket`, `TicketState` (7), `Size` (3), `Risk` (3) | Unchanged shape. `Size` is read from a `size:` label; `Risk.Low/High` still never constructed in production. |
| [src/ThroughlineBuild.Contracts/Models/Brief.cs](../../src/ThroughlineBuild.Contracts/Models/Brief.cs) | `Brief(TicketId, Phase, Instruction, RelevantFiles, AllowedWrites, Context)` | The unit handed to a worker. Unchanged. |
| [src/ThroughlineBuild.Contracts/Models/WorkerResult.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs) | `WorkerResult(Status, Summary, FilesChanged, FailureReason?, Metadata, Blocks?, Tickets?)`, `Status` (4) | Grew `Tickets` (`IReadOnlyList<BatchTicketResult>?`) for batch implement sessions, alongside the op-27 `Blocks` dictionary. |
| [src/ThroughlineBuild.Contracts/Models/BatchWorkerResult.cs](../../src/ThroughlineBuild.Contracts/Models/BatchWorkerResult.cs), [BatchTicketResult.cs](../../src/ThroughlineBuild.Contracts/Models/BatchTicketResult.cs) | `BatchWorkerResult`, `BatchTicketResult` | NEW (TLB-447). Per-ticket attribution (`ticket_id`, `commit_sha`, `stack_position`, `files_changed`, `summary_ref`) from a warm batch session; verified against git by `BatchCommitVerifier` (TLB-448). |
| [src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs) | `CompletionClaim`, `AcBinding`, `VerifierKind` (7) | NEW (TLB-500). See "The gate contract" below. |
| [src/ThroughlineBuild.Contracts/Models/SmokeSignal.cs](../../src/ThroughlineBuild.Contracts/Models/SmokeSignal.cs) | `SmokeSignal`, `SmokeSignalKind` (3) | NEW (TLB-503). Advisory, never gate-failing. |
| [src/ThroughlineBuild.Contracts/Models/ProviderError.cs](../../src/ThroughlineBuild.Contracts/Models/ProviderError.cs) | `ProviderError`, `ProviderErrorKind` (2) | NEW (TLB-527). A provider quota/rate-limit/auth block, distinct from a `Verdict`. |
| [src/ThroughlineBuild.Contracts/Models/WorkerResultMetadata.cs](../../src/ThroughlineBuild.Contracts/Models/WorkerResultMetadata.cs) | `WorkerResultMetadata` (const keys) | NEW (TLB-471/476). Centralizes the `envelope_status` = `missing` / `missing_status` salvage vocabulary so agents and `ImplementPhase` cannot drift. |
| [src/ThroughlineBuild.Contracts/Models/ModelTier.cs](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs) | `ModelTier(Model, Effort?)` | NEW (op-33). Per-`WorkerSize` model + reasoning effort; effort acted on only by Codex. |
| [src/ThroughlineBuild.Contracts/Models/Verdict.cs](../../src/ThroughlineBuild.Contracts/Models/Verdict.cs) | `Verdict(Kind, Rationale, ChecksFailed)`, `VerdictKind` (3) | Unchanged. |
| [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs) | `WorkflowEvent`, `EventKind` (**14**) | `CostLedger` added at ordinal 13 (TLB-510). Integer values pinned in the comment above the enum declaration ([WorkflowEvent.cs:11-14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11-L14)). |
| [src/ThroughlineBuild.Contracts/Models/Phase.cs](../../src/ThroughlineBuild.Contracts/Models/Phase.cs) | `Phase` enum (**11**) | `Gate` added (TLB-506) after `Decompose`. |
| [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs) | `ChainOutcome` enum (**20**) | Added since last refresh: `RefusedWrongBranch`, `BatchImplemented`, `DryRunPreview`, `GateVacuous`, `ReviewUnavailable` (TLB-527), `GateEnvironmentFailure` (TLB-538), `TicketingUnavailable` (TLB-545). Exit-code mapping in 06-public-surfaces. |
| [src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs](../../src/ThroughlineBuild.Contracts/Models/ReviewFeedback.cs) | `ReviewFeedback(Rationale, ChecksFailed, ReworkRoundNumber, GateFailedChecks?, FailedCheckDetails?)` | Grew two evidence fields: `GateFailedChecks` (gate-originated rework) and `FailedCheckDetails` (7af36fb - raw `CheckResult`s for the cited checks, rendered verbatim into the rework brief so the check is the oracle). |
| [src/ThroughlineBuild.Contracts/Models/DirtyTreeCause.cs](../../src/ThroughlineBuild.Contracts/Models/DirtyTreeCause.cs), [DebugTranscriptContext.cs](../../src/ThroughlineBuild.Contracts/Models/DebugTranscriptContext.cs) | `DirtyTreeCause`, `DebugTranscriptContext` | NEW. Structured dirty-refusal cause (TLB-462); debug-transcript keying metadata (build sha / session id / rework round). |
| [src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs) | `CheckSpec`, `CheckResult`, `CheckRole` (3), `CanaryFile` | `CheckRole` (`Gating`/`Advisory`/`Setup`) is the cross-phase role contract; `CheckSpec.Canary` carries deliberately-broken files for the vacuity prover; `CheckResult.CommandLine` echoes the exact command for rework briefs. |
| [src/ThroughlineBuild.Contracts/ITicketing.cs](../../src/ThroughlineBuild.Contracts/ITicketing.cs) | `ITicketing` (16 methods) + supporting records, and `ITicketingConnectivity` ([ITicketing.cs:127](../../src/ThroughlineBuild.Contracts/ITicketing.cs#L127)) | `ITicketingConnectivity.TestConnectivityAsync` is NEW (op-34 connected init / scaffold preflight). `BackendCapabilities` still advertised, still unread. |
| [src/ThroughlineBuild.Contracts/IProjectDiscovery.cs](../../src/ThroughlineBuild.Contracts/IProjectDiscovery.cs), [IProjectResolver.cs](../../src/ThroughlineBuild.Contracts/IProjectResolver.cs), [ITicketingProvisioner.cs](../../src/ThroughlineBuild.Contracts/ITicketingProvisioner.cs) | `IProjectDiscovery` + `ProjectInfo`; `IProjectResolver` + `ProjectResolveResult`; `ITicketingProvisioner` + `ExistingState` | NEW bootstrap-era interfaces (TLB-481/482, TLB-460). Discovery/resolution run on raw credentials BEFORE any config exists; provisioning diffs the live project against `WorkspaceSchema`. All implemented by the Plane backend. |
| [src/ThroughlineBuild.Contracts/WorkspaceSchema.cs](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs) | `WorkspaceSchema` (static) + `RequiredState` | NEW. The canonical 7 states (`WorkspaceSchema.States` at `#L23`, each with its Plane state-group) and 9 labels (`WorkspaceSchema.Labels` at `#L39`: `risk:*`, `size:*`, `plan-ticket`, `stub`, `delegated`). Single source of truth for both the runtime state map and `build setup`. |
| [src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs](../../src/ThroughlineBuild.Contracts/TicketingUnavailableException.cs) | `TicketingUnavailableException` | NEW (TLB-545). Thrown by a backend when transport retries are exhausted; orchestration catches it and stops with the resumable `ChainOutcome.TicketingUnavailable`. |
| [src/ThroughlineBuild.Contracts/IWorkerAgent.cs](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) | `IWorkerAgent`, `WorkerOptions` | `WorkerOptions` ([IWorkerAgent.cs:51](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L51)) gained `DebugTranscript` and `LeanPlanning` (effort-gated planning hygiene; the phase names no vendor tool, each agent maps the flag itself). |
| remaining interfaces (`IGitClient`, `IEventSink`, `ILlmClient`, `IWorkflowPhase`, `ITicketCommand`, `IVerifier`, `IObsoleteRatifier`, `IReviewFeedbackRetriever`, `IWorkerAgentFactory`, `IWorkerProgressDigester`) | - | Shapes substantially as last documented; `IGitClient` keeps accreting defaulted methods (~33 async methods). |

### The gate contract (briefs -> implement worker -> GatePhase -> review) - NEW

The op that landed TLB-500..510 introduced a typed contract between the implement worker and a new deterministic gate that runs between implement and review in the chain loop:

- **`CompletionClaim`** ([CompletionClaim.cs:18](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs#L18)): what the implementation `Provides`, what it `Consumes`, `AcBindings` (acceptance-criteria item -> `VerifierKind`), and `TestsAdded`. The worker emits it as a JSON-bodied `COMPLETION_CLAIM` fenced block referenced by a `completion_claim_ref` metadata field (TLB-505); `ImplementPhase` resolves and parses it via the internal `CompletionClaimParser` ([ImplementPhase.cs:487-505](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L487-L505)). A worker that emits no claim is allowed (pre-claim-format compatibility); a malformed claim hard-fails the gate as `claim_schema_invalid`.
- **`GatePhase`** ([GatePhase.cs:34](../../src/ThroughlineBuild.Phases/GatePhase.cs#L34), TLB-506) validates the claim, runs the configured `CheckSpec`s once against the warm worktree, and returns a `GateOutcome`. Only `CheckRole.Gating` failures hard-fail; `Setup` failures are reported as `setup_failed` (still rework-eligible); `Advisory` failures never block.
- **Consumes-provides preflight** (TLB-507): the gate checks the ticket's `Consumes` against the accumulated upstream `Provides` of earlier chain tickets and records the result as a `SmokeSignal` ([GatePhase.cs:120-133](../../src/ThroughlineBuild.Phases/GatePhase.cs#L120-L133)) - advisory only, never a hard fail.
- **Smoke signals** (TLB-503): the `SmokeCollector` class ([SmokeCollector.cs:11](../../src/ThroughlineBuild.Verification/SmokeCollector.cs#L11)) produces diff-facts and grep observations; advisory check results share the same `SmokeSignal` shape. The verifier consumes them as priors, not verdicts.
- **Gate integrity**: `GateVacuityProver` proves on first green that each gating check actually fails on its declared `CanaryFile`s (outcome `GateVacuous` on failure - config defect, no rework); `GateControlProber` (TLB-538) re-runs failed checks on the untouched base ref and classifies environment breakage (`GateEnvironmentFailure`, no rework, plus an in-run recovery arm that reloads fixed gate specs from disk).
- The claim's `RedGreenKind` / `Tier` / `RoutingKey` hook fields are deliberately UNENFORCED - the declaring comment says so and every consumer ignores them.

### Untyped / fragile contracts inside the model

Unchanged in kind: `PhaseResult.Outputs` (`IReadOnlyDictionary<string,string>`), `WorkflowEvent.Data` (`IReadOnlyDictionary<string,object>`, per-`Kind` shape in [docs/build-event-log-format.md](../build-event-log-format.md)), `WorkerResult.Metadata` (free-form, asserted per-phase), `TicketCommandContext.Args` (flag-name-keyed strings). Two partial mitigations since the last refresh: `WorkerResultMetadata` centralizes the `envelope_status` strings, and `ReviewFeedback.FailedCheckDetails` carries typed `CheckResult`s instead of re-parsing prose. The rest still narrows at use, not at the boundary.

---

## Contracts with sibling systems

This repo is the successor to `claude-config` (the older slash-command workflow). It also produces / consumes artifacts visible to:

- **Plane** (the ticketing backend) - the operational sibling.
- **The worker CLIs** (`claude`, `codex`, `gemini`, `copilot` - vendor binaries) - the worker siblings.
- **The claude-config flow** (the `/ticket-*` skills, now served from the operator's global claude-config install, not from this repo) - still operates on the same Plane data.

### Plane (`PlaneTicketingClient` <-> Plane REST API)

What `build` reads that Plane wrote:

- Ticket records (states, labels, comments, relations, parent links). Field names referenced verbatim in [PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs).
- State names: the runtime reverse map is now **derived from `WorkspaceSchema`** rather than hardcoded inline ([PlaneTicketingClient.cs:438-441](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L438-L441) builds the dictionary from `WorkspaceSchema.States`), so the state map and `build setup` provisioning can no longer drift from each other. A workspace with different state names still reads everything as `Backlog`.
- The `size:s|m|l` label mapped into `Ticket.Size`; the `parent` UUID -> `Ticket.ParentId` with client-side child filtering (Plane ignores the server-side `parent=` param).
- **Workspace-level reads (NEW, op-34)**: project list / find-by-name / create via `IProjectDiscovery`, and per-project state + label inventories via `ITicketingProvisioner` - both used before any `.build/config.toml` exists.

What `build` writes that other systems read:

- HTML descriptions and comments; markers embedded in comment HTML (`[planned_at:]`, `[implemented_at:]`, `[decomposed_at:]`, `[shipped_at:]`). Marker lookup is freshest-by-timestamp via `CommentMarkers.LatestValue` ([CommentMarkers.cs:19](../../src/ThroughlineBuild.Phases/CommentMarkers.cs#L19)) (TLB-412), and `ReviewPhase` still cross-checks the freshest `implemented_at` against worktree HEAD, emitting `GateFailure` `kind = implemented_at_superseded` on divergence (TLB-414, emission at [ReviewPhase.cs:185](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L185)).
- Lifecycle prefixes posted by `TransitionLifecycleAsync` ([PlaneTicketingClient.cs:1149-1155](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1149-L1155)): `<strong>wontfix:</strong>` / `<strong>deferred:</strong>` / `<strong>reopened:</strong>`; rollup `[rollup]` comments; advisory `[gate: hard-fail]` comments from `GatePhase` (operator-facing, never parsed back).
- **Provisioned states and labels (NEW)**: `build setup` creates any state or label missing from `WorkspaceSchema` (diff logic in `SetupCommand.ExecuteAsync`, [SetupCommand.cs:102-106](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L102-L106)) - so the workspace vocabulary is now a *written* artifact, not just an assumed one. The `risk:*` / `size:*` labels hard-fail plan/chain when absent; a missing state downgrades a transition to a warning.
- Sub-issue parent/child linkage via `CreateChildTicketsAsync` / `SetParentAsync` (native Plane sub-issues).

Transport behavior (NEW, TLB-545): `PlaneTicketingClient` retries transient transport failures (DNS/connect/TLS/timeout) with exponential backoff (`PlaneClientOptions.TransportRetryAttempts`, [PlaneClientOptions.cs:47](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L47)) before throwing `TicketingUnavailableException`; HTTP 429/5xx retries honor `Retry-After` (clamped). `ChainPhase` converts the exception into the resumable `TicketingUnavailable` outcome and skips remaining siblings/roots - the same environmental-classification pattern as TLB-538.

### Worker CLIs (`ClaudeCodeAgent` / `CodexAgent` / `GeminiAgent` / `CopilotAgent` <-> vendor CLIs)

This remains the most evolving contract. Spawn flags and output shapes per agent are tabulated in 03-external-dependencies.md; this section covers the shared envelope contract.

- The `WORKER_RESULT` envelope is the cross-agent contract, parsed by the shared internal `WorkerResultParser` ([WorkerResultParser.cs:115](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L115)). Required `status` + non-empty `summary`; optional `files_changed`, `failure_reason`, `metadata` (incl. `escalation`/`subsumed_by` and `llm_usage`), and - new - an optional `tickets` array for batch sessions.
- **The parse input is now the full assistant transcript (945f4b4), not the final message.** `ClaudeCodeAgent.TryExtractAssistantTranscript` ([ClaudeCodeAgent.cs:268](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L268)) concatenates every assistant text block from the NDJSON stream and parses that (fallback: the envelope `result` field). Matching parser-side rules: the fenced-block scan runs up to the LAST `WORKER_RESULT` marker, duplicate blocks are last-wins, the last valid envelope wins, and narration after the closing brace is ignored (`ExtractLeadingJsonValue`). Symmetrically, since 3cbf64c the brief templates instruct workers to emit all fenced blocks AND the envelope **in one final message**.
- **Recoverable non-conformance is now part of the contract**: a clean-exit session with no marker is tagged `envelope_status=missing` (TLB-471); a marker whose valid-JSON payload lacks `status` is tagged `envelope_status=missing_status` (TLB-476) - both via the `WorkerResultMetadata` constants - and `ImplementPhase` salvages the committed work when the worktree is clean and HEAD advanced, with review as the real gate.
- **Provider-level failures are classified, not judged**: `ProviderErrorClassifier.Classify` ([ProviderErrorClassifier.cs:60](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs#L60)) detects quota/rate-limit/auth blocks from worker output; during review, `WorkerAgentReviewer.LastProviderError` surfaces it and the chain stops as `ReviewUnavailable` instead of a Fail verdict (TLB-527).
- **Brief templates are part of the contract.** Templates now fan out 4 agents x 7 phases (`plan`, `implement`, `review`, `decompose`, `draft`, `batch-implement`, `batch-review`) under [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/), plus a `shared/` directory of ten fragments (the WORKER_RESULT envelope stubs, obsolete-detection blocks, patch-fetch directives, batch rework guidance) composed into the per-agent templates - the envelope text now lives in one place instead of being duplicated per agent (ad65a54, 7d264a9).
- **Fenced-block payload protocol (op-27).** Spec at [docs/op-docs/examples/op-27-worker-result-fenced-payloads.md](../op-docs/examples/op-27-worker-result-fenced-payloads.md). Per-phase refs and consumers (validation/resolution sites):
  - Plan: `PlanPhase` resolves `plan_body_ref` -> `PLAN_BODY` ([PlanPhase.cs:146](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L146)) and requires `risk_label`, `size_label`, `planned_at_sha`.
  - Implement: `ImplementPhase` requires `commit_sha` ([ImplementPhase.cs:474-478](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L474-L478)), optionally resolves `summary_ref` -> `IMPLEMENT_SUMMARY`, and resolves `completion_claim_ref` -> `COMPLETION_CLAIM` (JSON; see the gate contract above).
  - Review: `WorkerAgentReviewer` reads `verdict` / `checks_failed` and resolves `rationale_ref` -> `REVIEW_CRITIQUE` ([WorkerAgentReviewer.cs:118-130](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L118-L130)), then **filters advisory check names out of `ChecksFailed` by construction** ([WorkerAgentReviewer.cs:133-143](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L133-L143)) so an advisory-only finding can never burn a rework round (d30dbac).
  - Draft: `DraftPhase` resolves `body_markdown_ref` -> `DRAFT_BODY` with legacy `body_markdown` fallback ([DraftPhase.cs:72-85](../../src/ThroughlineBuild.Phases/DraftPhase.cs#L72-L85)).
  - Batch implement: the envelope's `tickets[]` entries each carry a `summary_ref` into per-ticket blocks; `WorkerResultParser.TryParseBatch` enforces the per-ticket required fields.
- **Rework briefs now carry the oracle**: review-cited failing checks are persisted as raw evidence (`checks_failed_details` on the `VerifierVerdict` event, [ReviewPhase.cs:457-476](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L457-L476)), reconstructed by `ReviewFeedbackRetriever.ParseFailedCheckDetails` ([ReviewFeedbackRetriever.cs:186](../../src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs#L186)), and rendered verbatim (command line, exit code, output tails) into the resumed rework brief via `ReviewFeedback.FailedCheckDetails` (7af36fb).

### Op-doc and derived-profile contracts (scaffold <-> op-plan <-> workers) - NEW at pointer level

Two scaffold-side formats hardened into published contracts since the last refresh:

- **The op-doc authoring format** is single-sourced as an embedded spec and printed by `build op-doc spec` (TLB-456); `build op-doc new` emits a skeleton guaranteed to parse. `OpDocParser` ([OpDocParser.cs](../../src/ThroughlineBuild.Scaffold/OpDocParser.cs)) grew: a positive-only `Preload:` brief label (61828bb) parsed into `Brief.PreloadFiles` ([OpDocTypes.cs:43](../../src/ThroughlineBuild.Scaffold/OpDocTypes.cs#L43)) and rendered as an `<h3>Preload</h3>` block in scaffolded brief tickets; check roles (0680cdf) so lint/format never hard-gate; setup steps (371de26) that run before gate checks; and convention bundles (0d86f76). The external `/op-plan` skill authors against this spec - the spec text, not the parser, is the operator-facing contract.
- **The derived `ProjectProfile`** ([ProjectProfile.cs:16](../../src/ThroughlineBuild.Scaffold/ProjectProfile.cs#L16)): during `scaffold`, a worker derives the project's toolchain (language/framework/package-manager/commands), role-tagged `review_checks`/`regression_checks` with optional per-check canaries, and a `convention_files` bundle from the op-doc prose; `ConfigProfileWriter` persists it into `.build/config.toml`, where gate/review/ship and `PreloadedContextBuilder` consume it. This is the mechanism that keeps stack knowledge in derived data rather than engine code - the JSON field names in the `ProjectProfileDto`s are the schema contract with the deriving worker.

### Older claude-config workflow

The in-repository half of the old flow has been removed.

- No `.claude/commands/`, `.claude/plane-config.md`, or `.claude/ticket-config.md` files are tracked at HEAD. Any global claude-config installation is external to this repository and is not part of the Throughline Build contract.
- The mirror infrastructure is also gone: there is no `bin/sync-*`, `copilot-prompts/`, or `plugins/latticeflow/` tree. The `bin/` directory is reserved for locally published binaries.
- `build` owns the current ticket workflow. `.build/config.toml` is the local backend configuration, `WorkspaceSchema` is the canonical state and label vocabulary, and `build setup` provisions it. `build new --print-template` emits the recognized ticket body shape from [new-ticket-body.md](../../src/ThroughlineBuild.Commands/Templates/new-ticket-body.md).

### Shared artifacts visible across flows

| Artifact | Written by | Read by |
|---|---|---|
| Plane ticket descriptions, comments, state transitions | both flows | both flows |
| Plane states + workspace-standard labels | `build setup` (provisions); Plane admin UI | `build` runtime state map (both derived from `WorkspaceSchema`) |
| `.claude/plane-config.md`, `.claude/ticket-config.md` | operator / old installer | documentation only; `build` does not consume them |
| `~/.claude/projects/<encoded>/...jsonl` | `claude` CLI | `token-audit` (this repo) |
| `.build/events/*.jsonl` | `build` only | `analyze-event-log` (this repo; now aggregates all chains and prefers pricing-table costs, TLB-547) |
| `.build/sessions/<id>/` incl. `transcript.jsonl` | `build --debug` | operator + cross-run analysis (keyed by `DebugTranscriptContext`) |
| `.worktrees/ticket-*`, `chain-*` | `build` | `build sweep` (recovery), operator |
| `WORKER_RESULT` envelope + fenced blocks + `COMPLETION_CLAIM` | worker CLI (per brief template) | all four `IWorkerAgent`s via shared parser; `GatePhase` |
| op-doc spec (embedded; `build op-doc spec`) | this repo (single source) | `/op-plan` skill, operators, `OpDocParser` validation |
| derived profile JSON -> `.build/config.toml` | scaffold profile worker | gate/review/ship checks, brief pre-load |

---

## Conflicts and overlaps

- **State-name vocabulary**: the worst duplication is resolved - the runtime map and `build setup` both derive from `WorkspaceSchema`. Still duplicated outside the binary: `.claude/plane-config.md` and the `.build/config.toml.example` comments.
- **Two `Size` enums** persist by design: ticket-domain `Size` and worker-domain `WorkerSize`, now bridged per-vendor through `ModelTier` (`{model, effort}` tables in config, op-33) and `WorkerSizeMapper`.
- **Two LLM abstractions** persist: `ILlmClient` (wired: `LlmClientFactory` -> `AnthropicClient`) and `IModelClient` (built, tested, unwired). Unchanged; reconciliation still unfinished.
- **Two check-result runners**: `AutomatedChecksRunner` (executes) and `PreComputedChecksRunner` (replays gate results into review, TLB-502) both satisfy the verifier's checks input; the latter exists precisely so gate and review cannot disagree about what ran.
- **Two verdict producers**: `IVerifier` (review) and `IObsoleteRatifier` (chain auto-resolve) both return `Verdict`. Unchanged.
- **Usage text vs exit-code mapper**: `CliUsage.UsageText` documents chain exit codes only through 9; `ChainExitCodeMapper` emits 10 and 11. Code wins.
- **Workspace / project IDs** still appear in both `.claude/plane-config.md` and `.build/config.toml`, operator-synced.
- **`/ticket-ship` vs `build ship`** - both can transition a ticket to Done. `build` is the current direction; the slash-command flow survives only via the global claude-config install.

---

## Loose ends

- **`BackendCapabilities`** is still never read - a typed promise without a runtime consumer, now sitting beside four bootstrap interfaces that DO get consumed.
- **`CompletionClaim` hook fields** (`RedGreenKind`, `Tier`, `RoutingKey`) are declared-but-unenforced by explicit design comment; nothing should gate on them until a future brief activates them.
- **`Phase.Command`** remains in the enum with no phase implementation behind it.
- **`Workers.Common` internals visible to `Phases`/`Verification`**: the `InternalsVisibleTo` list keeps growing; at some point the parser surface (`FencedBlockResolver`, `CompletionClaimParser`) should either go public or get a narrow public facade.
- **`ILlmClient` vs `IModelClient`** - unchanged: two abstractions, one wired; `AnthropicModelClient.StreamAsync` works but has no caller.
- **Schema-in-two-places fragility persists** for `metadata.escalation`, the per-phase `metadata` keys, and now the `COMPLETION_CLAIM` JSON: a markdown template stub on the producing side and C# validation on the consuming side, with no shared source of truth. The shared template fragments reduce per-agent drift but do not solve template-vs-validator drift.
- **Brief templates fan out 4 agents x 7 phases plus 10 shared fragments**; nothing enforces that the codex/gemini/copilot variants demand the same metadata keys as claude-code.
- **`.claude/plane-config.md` / `ticket-config.md`** remain as hand-synced documentation of config the binary owns; with `WorkspaceSchema` + `build setup` now canonical, these files are candidates for deletion or generation.
- **`src/ThroughlineBuild.Linear/`** - an empty directory implying a Linear backend that does not exist (a feasibility doc exists at `docs/linear-integration-feasibility.md`). Aspirational.
- **Old claude-config flow**: the in-repo corpus and mirrors are gone, but the architecture-doc Section 8 narrative ("delete in one commit") no longer matches how the cutover actually happened; the architecture doc is stale on this point.
