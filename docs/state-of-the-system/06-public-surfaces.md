# 06 - Public Surfaces

Last refreshed: 2026-08-11 (HEAD 758ad56a)

The CLI surface, the exported library interfaces, and the inter-project contracts that anything outside this repo (or any unfamiliar reader inside it) might depend on. Status for each.

For inter-project contracts (records and interfaces) in detail, see [07-contracts.md](07-contracts.md). For verb behavior in detail, see [01-inventory.md](01-inventory.md).

---

## CLI surface

The whole user-facing API of this repository.

```
build <verb> [args] [--debug | --quiet] [--summary-json] [--json] [--error-location]

  plan <id> [id ...]      [--agent <name>] [--from-brief]
  implement <id> [id ...] [--agent <name>]
  review <id> [id ...]    [--agent <name>]
  ship <id> [id ...]      [--no-auto-merge] [--no-push] [--skip-baseline]
  chain <id> [id ...]     [--batch-implement [<id,...>]] [--dry-run] [--max-depth <n>]
                          [--agent <name>] [--agent-plan <name>] [--agent-implement <name>] [--agent-review <name>]
                          [--from-brief] [--no-auto-resolve] [--no-auto-merge] [--continue-past-failure]
  rework <id> [--feedback "..."]
  decompose <id> [--agent <name>]
  new <body-path | text | -> [--title "..."] [--type "..."] [--label "..."]* [--review]
  new --print-template
  scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--no-profile] [--force-profile]
  init [--force] [--print-template] [--no-interactive] [--from FILE] [--plane-url URL] [--workspace SLUG]
       [--project-id UUID | --project-name NAME] [--token TOKEN | --token-env VAR]
  setup [--check]
  user-guide [--force] [--print-template]
  op-doc spec [--print] [--write] [--force]
  op-doc new <slug> [--write]
  models refresh
  settarget [<branch> | --unset]
  sweep [--target <branch>] [--force]
  list [--state <name>] [--parent <id>] [--type <name>]
  get <id> [--json]
  comments <id> [--json]
  comment <id> <text|-> [--json]
  attachments <id> [--json]
  attachment <id> <asset-id> --output <path> [--json]
  amend <id> [--title "..."] [--priority urgent|high|medium|low|none] [--type <name>]
             [--label-add <name>]... [--label-remove <name>]... [--parent <id>]
             [--size S|M|L] [--note "..."] [--description <path|->]
  close <id> <reason>
  defer <id> <reason>
  reopen <id> [reason]
  help [<topic>]
  --help | -h
  --version | -V
```

There are **38 dispatchable action verbs**, declared once by `CliVerbRegistryFactory.Verbs` ([CliVerbRegistryFactory.cs:7-47](../../src/ThroughlineBuild.Cli/CliVerbRegistryFactory.cs#L7-L47)). Twelve added since the 2026-07 refresh are `install`, `profile`, `conductor`, `sop`, `worker`, `worktree`, `gate`, `waves`, `candidate`, `attachments`, `attachment`, and `evidence`. Help and version are meta-surfaces. Nine verbs run before full config bootstrap because they create/edit config or deliberately load a standalone slice: `init`, `install`, `settarget`, `user-guide`, `op-doc`, `models`, `sop`, `conductor`, and `profile`.

New or changed since the last refresh:

- **`setup`** (TLB-460, op-34): makes a fresh project workflow-ready. Does `git init` plus a standard ignore list when needed, then diffs the live Plane project against the canonical `WorkspaceSchema` (states + labels, see 07-contracts) and creates whatever is missing via `ITicketingProvisioner`. `--check` verifies only (exit 1 when anything is missing). Implemented by the `SetupCommand` class ([SetupCommand.cs:18](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L18)); dispatched after config load ([CliApplication.cs:563](../../src/ThroughlineBuild.Cli/CliApplication.cs#L563)). Since the Stage 07 cutover it then runs a Claude transport capability preflight (`ClaudeTransportPreflight.ReportAsync`), returning non-zero when a configured `interactive-hook` agent cannot run on this host. Status: Functional.
- **`sweep`** (TLB-531): the recovery verb after an interrupted chain. Removes leftover `.worktrees/ticket-*` / `chain-*` worktrees and deletes their branches when fully merged into the target; `--force` also removes worktrees whose branch is unmerged (the branch itself is kept). Pure git + filesystem, no worker, no Plane. The work is done by `ChainWorktreeSweeper.SweepAsync` ([ChainWorktreeSweeper.cs:47](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs#L47)); dispatch at [CliApplication.cs:480](../../src/ThroughlineBuild.Cli/CliApplication.cs#L480). Status: Functional.
- **`models refresh`** (op-33): re-probes Codex (`codex debug models`) and rewrites the `[workers.codex.sizes]` block in `.build/config.toml` in place, printing a current-to-proposed diff; a probe failure leaves the file unchanged. Implemented by the `ModelsRefreshCommand` class ([ModelsRefreshCommand.cs:18](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L18)). Status: Functional.
- **`op-doc spec` / `op-doc new`** (TLB-456/457): `spec` prints (or `--write`s) the embedded op-doc authoring spec via the `OpDocSpecCommand` class ([OpDocSpecCommand.cs:9](../../src/ThroughlineBuild.Cli/OpDocSpecCommand.cs#L9)); `new <slug>` emits a minimal valid op-doc skeleton via `OpDocSkeletonGenerator` ([OpDocSkeletonGenerator.cs:5](../../src/ThroughlineBuild.Scaffold/OpDocSkeletonGenerator.cs#L5)). This makes the op-doc format itself a published surface - see the stability call-outs below. Status: Functional.
- **`chain` traversal flags**: `--dry-run` prints the full post-order tree schedule and branch topology without executing phases; `--max-depth <n>` is root-based (0 = root only); `--batch-implement` (TLB-444/447/473) batches the implement phase for direct children in one warm worker session - bare flag batches ALL eligible children, a comma list batches exactly that group, and an oversized group falls back to per-ticket chaining. Flag extraction happens before dispatch ([CliApplication.cs:110-135](../../src/ThroughlineBuild.Cli/CliApplication.cs#L110-L135)). Status: Functional.
- **`init`** grew a guided connected mode (op-34): at a TTY it prompts for base URL / workspace / token, then offers create-or-pick from a most-recently-used project menu (no UUID pasting), provisions, makes a welcome commit, and verifies connectivity. Non-interactive paths: `--project-name` resolves or creates by name via `IProjectResolver`; `--from FILE` (or redirected stdin) reads a key=value credentials file; unknown flags are rejected with exit 2 ([CliApplication.cs:236-250](../../src/ThroughlineBuild.Cli/CliApplication.cs#L236-L250)). Status: Functional.
- **Ticket attachments:** `attachments` lists normalized ticket-owned metadata; `attachment` requires an explicit `--output` path, revalidates ownership, keeps binary bytes off stdout, and atomically refuses overwrite ([CliApplication.cs:987-1137](../../src/ThroughlineBuild.Cli/CliApplication.cs#L987-L1137), [CliApplication.cs:3175-3209](../../src/ThroughlineBuild.Cli/CliApplication.cs#L3175-L3209)). Status: Functional.
- **Deterministic conductor surface:** `worker brief` renders role artifacts without starting a worker; `worktree` leases/lists/tears down manifest-backed helper trees; `gate` runs selected configured checks; `waves` schedules dependency/conflict-safe groups; `candidate status` emits git fingerprints; `evidence add` posts and reads back one typed audit comment. Their canonical option/exit contracts are registered together in `HelpRegistryFactory` ([HelpRegistryFactory.cs:252](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L252)). Status: Functional.
- **Repository readiness and SOPs:** `install` drives profile and invariant handoffs; `profile` prompts/applies/verifies canaries; `conductor` prompts/applies invariant blocks; `sop` lists/doctors/briefs and safely installs/upgrades/uninstalls/status-checks embedded host artifacts. No path starts a nested model worker ([HelpRegistryFactory.cs:349](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L349), [HelpRegistryFactory.cs:480](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L480), [HelpRegistryFactory.cs:900](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L900)). Status: Functional.
- **`scaffold` profile derivation was removed.** The verb now only parses/validates/creates the op-doc ticket hierarchy; profile generation moved to the explicit prompt/apply surface. Status: Functional.
- **`--version` / `-V`** (TLB-459) prints `0.1.0+<shortsha>`. The value is the `BuildVersion.Current` constant - `BuildVersion` is a generated partial class ([BuildVersion.cs:3](../../src/ThroughlineBuild.Cli/BuildVersion.cs#L3)) whose other half is emitted by the `GenerateBuildVersionSource` MSBuild target ([ThroughlineBuild.Cli.csproj:54](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L54)). The same string is stamped into the event log (`SessionContext.BuildVersion`) and debug transcripts. Status: Functional.

Full usage text: the `CliUsage.UsageText` constant in [CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs). `--agent` (and the per-phase `--agent-plan` / `--agent-implement` / `--agent-review` for `chain`) selects which worker agent runs the phase; the name must be a key in the `[workers.<name>]` config sub-table. See [11-llm-architecture.md](11-llm-architecture.md).

### Tiered help system (op-30)

Help is no longer one usage dump. Three tiers, all AOT-safe and I/O-free:

- **Tier 0**: `build`, `build --help`, `build help` render a grouped command index via the `Tier0Renderer` class ([Tier0Renderer.cs](../../src/ThroughlineBuild.Cli/Help/Tier0Renderer.cs)).
- **Tier 1**: `build <verb> --help` (any position) renders that verb's options/exit-codes/examples via the `Tier1Renderer` class ([Tier1Renderer.cs](../../src/ThroughlineBuild.Cli/Help/Tier1Renderer.cs)).
- **Topics**: `build help <topic>` renders one of four prose topics - `config`, `digest`, `exit-codes`, `summary` - registered in `HelpTopicRegistry.Build` ([HelpTopicRegistry.cs:32-35](../../src/ThroughlineBuild.Cli/Help/Topics/HelpTopicRegistry.cs#L32-L35)); an unknown topic lists the valid names and exits 2.

The model behind tiers 0/1 is the `HelpRegistry` populated by `HelpRegistryFactory.Build` ([HelpRegistryFactory.cs:25](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L25)), which registers **36** commands in four groups. Status: Functional, with a gap: `models` and `sweep` are dispatchable but not registered.

### Versioned JSON ticket contract

The global `--json` flag is handled before positional parsing. Ticket operations include `AttachmentsEnvelope` for metadata lists and `AttachmentDownloadedEnvelope` for a completed file write; the binary body is never serialized to stdout ([CliEnvelope.cs:139-158](../../src/ThroughlineBuild.Cli/Json/CliEnvelope.cs#L139-L158)). In addition to ticket operations, the conductor/readiness commands emit source-generated versioned envelopes: install handoffs/readiness, profile prompt/apply/proof, conductor prompt/apply, SOP list/doctor/brief/lifecycle, worker brief metadata, lease/list/teardown, gate results, wave plans, candidate fingerprints, and evidence read-back. Successful responses remain `{schemaVersion:1, ok:true, data:...}` and failures `{schemaVersion:1, ok:false, error:{code,message}}`; each command's AOT DTO registrations live in `CliJsonContext`/`CliEnvelope` rather than reflection ([CliEnvelopeWriter.cs:15](../../src/ThroughlineBuild.Cli/Json/CliEnvelopeWriter.cs#L15)). Status: Functional.

`TicketDraft` is the strict structured-new schema: title, type, description, acceptance criteria, labels, optional parent, and typed relations. Unknown fields fail parsing through the source-generated `CliJsonContext`; relation kinds point to `RelationKinds.Allowed` rather than being duplicated here. The command resolves the parent and all relation targets before creating the issue, but relation POSTs occur after creation and are not transactional. Status: Functional with non-atomic relation attachment.

### Stable contracts on the CLI

These are not just convenience flags - downstream tooling (CI, the operator's other agents, the `analyze-event-log` tool) reads them.

| Contract | Status |
|---|---|
| **Exit codes** are deterministic. Global scheme plus per-verb overrides for `chain`, `rework`, `scaffold` - full enumeration below. | Functional |
| **`--summary-json`** emits a structured JSON object on stdout - the schema is the `PhaseSummary` records ([PhaseSummary.cs](../../src/ThroughlineBuild.Helpers/PhaseSummary.cs)) rendered by `PhaseSummaryRenderer` ([PhaseSummaryRenderer.cs](../../src/ThroughlineBuild.Helpers/PhaseSummaryRenderer.cs)). | Functional |
| **Default summary text block** is stable per-phase; operators redirect it (`build plan TLB-N 2>/dev/null > summary.txt`). | Functional |
| **`--debug`** captures worker stdio to `.build/sessions/<stem>/` with a stable layout: `worker-stdin.txt`, `worker-stdout.txt`, `worker-stderr.txt`, `envelope-result.txt` (or `parse-error.txt`), `worker-result.json`, plus a structured per-turn `transcript.jsonl` side channel keyed by `DebugTranscriptContext` (build version, session id, rework round - see [build-debug-transcript-format.md](../build-debug-transcript-format.md)). `--debug` is a no-op for `ship` (no worker subprocess). | Functional |
| **Progress digest** (default, `[m:ss] kind <payload>`) auto-suppresses when stderr is redirected unless `BUILD_PROGRESS=1`. Produced per-agent by `IWorkerProgressDigester` (null for Copilot). Under a multi-ticket `chain` each ticket's lines are prefixed `[{ticketId}] ` by a `PrefixedTextWriter` wrapping the digest sink ([PhaseOptionsBuilder.cs:47](../../src/ThroughlineBuild.Phases/PhaseOptionsBuilder.cs#L47)). The Claude digester now filters system stream events by subtype and throttles the thinking-tokens ticker. | Functional |
| **Plane comment markers** `[planned_at: <sha>]`, `[implemented_at: <sha>]`, `[decomposed_at: <sha>]`, `[shipped_at: <sha>]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>` are load-bearing - downstream phases parse them. The gate also posts advisory `[gate: hard-fail]` comments, which nothing parses back. See the marker call-out below. | Functional |

### Exit codes (full enumeration)

The global mapping (any verb that does not override it): 0 success, 1 phase/command failure, 2 config error or unknown verb, 3 missing secret, 4 phase infrastructure failure. Documented in the exit-codes section of `CliUsage.UsageText` ([CliUsage.cs:89-118](../../src/ThroughlineBuild.Cli/CliUsage.cs#L89-L118)) and in the `exit-codes` help topic.

`chain` overrides these via the public `ChainExitCodeMapper.GetExitCode` switch ([ChainExitCodeMapper.cs:13-35](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L13-L35)), keyed on the `ChainOutcome` enum ([ChainOutcome.cs:3-25](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3-L25), now 20 members):

| Code | ChainOutcome |
|---|---|
| 0 | `Completed`, `RatifiedObsolete`, `ParentCompleted`, `DryRunPreview` |
| 2 | `RefusedInitialState`, `RefusedDirtyTree`, `RefusedWrongBranch`, `ParentHasGrandchildren` |
| 3 | `StoppedAtPlan`, `ParentStoppedEarly`, `Skipped` |
| 4 | `StoppedAtImplement` |
| 5 | `StoppedAtReview` |
| 6 | `ReworkCapExceeded` |
| 7 | `StoppedAtShip` |
| 8 | `GateVacuous` |
| 9 | `ReviewUnavailable` |
| 10 | `GateEnvironmentFailure` |
| 11 | `TicketingUnavailable` |

The four codes new since the last refresh classify *environmental* stops that must not look like code failures: `GateVacuous` (a gating check could not be proven to fail on broken input - config defect, no rework), `ReviewUnavailable` (TLB-527 - provider quota/rate-limit/auth blocked the verifier; review never ran, ticket left InReview and resumable), `GateEnvironmentFailure` (TLB-538 - the failed checks also fail on the untouched base ref), and `TicketingUnavailable` (TLB-545 - Plane unreachable at the transport level after retries). Disagreement note: the usage text in `CliUsage.UsageText` enumerates chain codes only through 9; codes 10 and 11 exist in `ChainExitCodeMapper` but are not yet documented there - code wins. `rework` overrides codes 2/4 and `scaffold` overrides codes 2/3 as spelled out in the same `CliUsage` block. `BatchImplemented` is an internal per-ticket outcome (the aggregate run maps it, not the operator).

### Conventions the CLI follows

- Always reads `.build/config.toml` from the nearest ancestor directory.
- Always resolves the main worktree root before phase dispatch, so `build` invoked from inside a feature worktree still operates on the right paths.
- `ship` pushes the merge target after a fast-forward merge (no other verb pushes), unless `--no-push` / `[ship] push = false`; see [05-state-and-persistence.md](05-state-and-persistence.md).
- Never amends or force-resets anything (no `git push --force`, `git reset --hard`, or interactive rebase anywhere). `sweep` deletes branches only when merged-gated.
- Single-shot, no daemon, no shared state between invocations - with one new caveat: `GatePhase` may re-read the gate check specs from disk mid-run via its `gateChecksReloader` recovery arm (TLB-538).

### Loose ends (CLI surface)

- `models` and `sweep` are missing from the tiered help registry (`HelpRegistryFactory.Build` registers 36 of the 38 action verbs).
- `CliUsage.UsageText` lags `ChainExitCodeMapper` on chain exit codes 10/11.
- The `decompose` verb is dispatched directly to `DecomposePhase`, not through an `ITicketCommand`.
- `--agent` selection names are validated at construction; an unknown name surfaces as a `ConfigException` from `WorkerAgentFactory.Create` - and a `[workers] default_agent` naming an undefined worker is now a clear Config error (TLB-512).

---

## Exported library surfaces

Most libraries are internal implementation packages by convention. `ThroughlineBuild.ClaudeCode` is the exception: it is an intentional reusable public facade and contains NuGet package metadata. The solution builds and tests it, but the repository has no pack/publish pipeline.

### `ThroughlineBuild.ClaudeCode`

`ClaudeCodeClient` exposes `CheckAsync`, `RunAsync(string, workingDirectory, options)`, and an advanced `RunAsync(Brief, ...)` overload ([ClaudeCodeClient.cs:9](../../src/ThroughlineBuild.ClaudeCode/ClaudeCodeClient.cs#L9)). `ClaudeCodeClientOptions` selects executable, transport, permissions, output limits, and hook behavior; `ClaudeCodeRunOptions` selects timeout, tools, environment, debug sinks, worker size, transcript context, and whether the public helper appends the `WORKER_RESULT` contract. `ClaudeCodeWorkerResultContract.EnsurePresent` makes that append idempotent. Runtime status: Functional. Distribution status: Partial.

Below: the interfaces and record types with the most consumer surface area.

### `ThroughlineBuild.Contracts`

The leaf of the dependency graph. Pure interfaces, records, enums - no I/O, no static state. 07-contracts.md tabulates the types file-by-file; the summary:

- **Ticketing**: `ITicketing` covers reads, comments, attachment discovery/download, transitions, mutation, and typed relation management (`ListRelationsAsync`, `GetRelationTicketAsync`, `CreateRelationAsync`, `RemoveRelationAsync`) in [ITicketing.cs:6-122](../../src/ThroughlineBuild.Contracts/ITicketing.cs#L6-L122). Attachment metadata and byte payloads are the `TicketAttachment` and `TicketAttachmentContent` records ([TicketAttachment.cs:3-14](../../src/ThroughlineBuild.Contracts/Models/TicketAttachment.cs#L3-L14)). Bootstrap is split into `ITicketingConnectivity`, `IProjectDiscovery`, `IProjectResolver`, and `ITicketingProvisioner`. `TicketingUnavailableException` is the typed transport-outage signal.
- **`WorkspaceSchema`** ([WorkspaceSchema.cs:13](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L13)): the canonical 7 states (with Plane state-groups) and 9 labels the workflow assumes; single source of truth shared by the Plane client's runtime state map and `build setup` provisioning. A shared artifact with Plane - see 07-contracts.
- **Workers**: `IWorkerAgent`, `IWorkerAgentFactory`, `IWorkerProgressDigester`, `WorkerOptions` - `WorkerOptions` ([IWorkerAgent.cs:51](../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L51)) gained `DebugTranscript` (a `DebugTranscriptContext`) and `LeanPlanning` (effort-gated hygiene, exp-4) on top of `Size`.
- **Gate contract types** (op TLB-500..510, new): `CompletionClaim` + `AcBinding` + `VerifierKind` ([CompletionClaim.cs](../../src/ThroughlineBuild.Contracts/Models/CompletionClaim.cs) - the `CompletionClaim` record at `#L18` carries `Provides`/`Consumes`/`AcBindings`/`TestsAdded` plus three explicitly UNENFORCED hook fields), `SmokeSignal` + `SmokeSignalKind` ([SmokeSignal.cs:10](../../src/ThroughlineBuild.Contracts/Models/SmokeSignal.cs#L10)). `CheckSpec`/`CheckResult` grew `CheckRole` (`Gating`/`Advisory`/`Setup`, [CheckResult.cs:8](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L8)), per-check `CanaryFile` lists, and a `CommandLine` echo on results so rework briefs can carry the oracle verbatim.
- **Provider/transport classification (new)**: `ProviderError` + `ProviderErrorKind` ([ProviderError.cs:10](../../src/ThroughlineBuild.Contracts/Models/ProviderError.cs#L10)) - a transient provider failure distinct from a verdict (TLB-527).
- **Batch implement (new)**: `BatchWorkerResult` / `BatchTicketResult`; `WorkerResult` itself ([WorkerResult.cs:3](../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs#L3)) gained an optional `Tickets` list alongside `Blocks`.
- **Misc new models**: `ModelTier` ([ModelTier.cs:9](../../src/ThroughlineBuild.Contracts/Models/ModelTier.cs#L9)) - `{model, effort}` per `WorkerSize` (op-33; effort acted on only by Codex); `WorkerResultMetadata` ([WorkerResultMetadata.cs:8](../../src/ThroughlineBuild.Contracts/Models/WorkerResultMetadata.cs#L8)) - well-known `envelope_status` key/values for salvage (TLB-471/476); `DirtyTreeCause`; `DebugTranscriptContext`.
- **Phases/verifier**: `IWorkflowPhase`, `IVerifier`, `IObsoleteRatifier`, `IReviewFeedbackRetriever`, `ITicketCommand`, `IGitClient` (~33 async methods incl. interface defaults), `IEventSink`, `ILlmClient` - shapes unchanged at the interface level except where noted in 07.

Enums: `TicketState` (7), `Size` (3), `Risk` (3), `Phase` (**11** - `Gate` added; [Phase.cs:3](../../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3)), `Status` (4: `Ok`/`NeedsRework`/`Failed`/`Escalate`), `VerdictKind` (3), `EventKind` (**14** - see the JSONL section), `ChainOutcome` (20), `DiffKind` (4), `DraftOutcome`, `WorkerSize` (3), `CheckRole` (3), `VerifierKind` (7), `SmokeSignalKind` (3), `ProviderErrorKind` (2). The two size enums still coexist: ticket-domain `Size` and worker-domain `WorkerSize`, now joined by `ModelTier` for the per-size model mapping. Status: Functional.

### `ThroughlineBuild.Phases`

The phase classes are the next-most-public surface. Ten phase/orchestration classes: `PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `NewPhase`, `DraftPhase`, `DecomposePhase`, and `GatePhase`. `GatePhase` runs between implement and review, validates `CompletionClaim`, runs configured checks, collects `SmokeSignal` values, and classifies code versus environment failures through `GateControlProber` ([GatePhase.cs:34](../../src/ThroughlineBuild.Phases/GatePhase.cs#L34)). The dependency/order machinery (`ParallelDispatcher`, `AncestorSkipFilter`, `TicketGraph`, `ChainDependencyGraph`, `BatchCommitVerifier`, and hygiene/rework helpers) is also public. `ChainPhaseComposition` is the CLI's testable composition root. Status: Functional.

### `ThroughlineBuild.Briefs`

Static `*BriefBuilder.Build(...)` factories: `PlanBriefBuilder`, `ImplementBriefBuilder`, `ReviewBriefBuilder`, `DecomposeBriefBuilder`, `DraftBriefBuilder`, plus the new `BatchImplementBriefBuilder` and `BatchReviewBriefBuilder` for warm batch sessions, and `PreloadedContextBuilder` ([PreloadedContextBuilder.cs](../../src/ThroughlineBuild.Briefs/PreloadedContextBuilder.cs)), which inlines op-doc `Preload:` paths and `convention_files` contents into implement briefs from the live worktree (exp-2/3; gated by `[project].preload_context`).

Templates live under per-agent subdirectories at [Templates/](../../src/ThroughlineBuild.Briefs/Templates/): `claude-code/`, `codex/`, `gemini/`, `copilot/`, each now holding **seven** templates (`plan`, `implement`, `review`, `decompose`, `draft`, `batch-implement`, `batch-review`), plus a `shared/` directory of ten cross-agent fragments (WORKER_RESULT envelope stubs, obsolete-detection blocks, patch-fetch directives, batch rework guidance) that the per-agent templates compose in - the shared fragments exist so the envelope contract is written once, not 4x. `TemplateLoader.Load(agentName, templateName)` ([TemplateLoader.cs:14](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs#L14)) resolves embedded resources (hyphens map to underscores); substitution is `{{key}}` via `TemplateExtensions.Substitute`. The directory layout and resource-name mapping remain a public-by-convention surface. Status: Functional.

### `ThroughlineBuild.Workers.Common`

The shared worker surface. Two genuinely public types now: the `ProviderErrorClassifier` class ([ProviderErrorClassifier.cs:20](../../src/ThroughlineBuild.Workers.Common/ProviderErrorClassifier.cs#L20)), whose `Classify` method turns a failed `WorkerResult` into a `ProviderError?` (consumed by `WorkerAgentReviewer` and `ClaudeCodeAgent`), and `ProcessStreamEncoding` ([ProcessStreamEncoding.cs:17](../../src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs#L17)), which pins worker subprocess stdio to UTF-8 (TLB-439). Everything else is `internal`: `WorkerResultParser`, `CompletionClaimParser`, `FencedBlockResolver`, `MarkdownRenderer`, `WorkerDiagnostics`. The internals are shared via `InternalsVisibleTo` ([ThroughlineBuild.Workers.Common.csproj:13-52](../../src/ThroughlineBuild.Workers.Common/ThroughlineBuild.Workers.Common.csproj#L13-L52)) with the four worker assemblies, their tests, and - new - `ThroughlineBuild.Phases` and `ThroughlineBuild.Verification`, which now consume the parser internals directly (claim resolution, fenced-block resolution).

The `WORKER_RESULT` envelope contract (what a worker must emit at the end of its session) is documented in the doc comment block of the `WorkerResultParser` class ([WorkerResultParser.cs:78-115](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L78-L115)). Shape:

```json
{
  "status": "Ok | NeedsRework | Failed | Escalate",
  "summary": "string (required, non-empty)",
  "files_changed": ["string", ...],
  "failure_reason": "string?",
  "metadata": { "key": <JsonElement>, ... },
  "tickets": [ { "ticket_id", "commit_sha", "stack_position", "files_changed", "summary_ref" }, ... ]
}
```

Parser rules, all in `WorkerResultParser.TryParse` ([WorkerResultParser.cs:117](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L117)):

- The literal marker line `WORKER_RESULT` precedes the JSON payload (optionally fenced in triple backticks); multiple markers are tolerated and the LAST valid envelope wins.
- **Full-transcript parsing (945f4b4, new):** the input is no longer just the final message. `ClaudeCodeAgent.TryExtractAssistantTranscript` ([ClaudeCodeAgent.cs:268](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L268)) reconstructs the complete assistant-visible text from every `type=assistant` NDJSON event and feeds THAT to the parser (falling back to the envelope `result` field), so a worker that emits blocks across several messages still parses. Correspondingly the fenced-block pre-pass in `TryScanFencedBlocks` ([WorkerResultParser.cs:445](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L445)) scans up to the LAST marker (not the first) and duplicate block names are last-wins.
- **Trailing narration tolerated:** `ExtractLeadingJsonValue` ([WorkerResultParser.cs:357](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L357)) takes the first complete JSON value after the marker and ignores anything after it.
- `status` and a non-empty `summary` are required; the specific "valid JSON object but no `status` key" failure is flagged as `MissingStatus` so agents can tag the result `envelope_status=missing_status` and `ImplementPhase` can salvage a committed-but-non-conforming session (TLB-471/476).
- An optional top-level `tickets` array (batch implement) is validated per-entry; `TryParseBatch` ([WorkerResultParser.cs:252](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L252)) is the batch-session variant that REQUIRES it.

**Fenced-block payload protocol (op-27).** Large markdown payloads are emitted as named fenced blocks (`<<<NAME_START` / `<<<NAME_END`, names matching `^[A-Z][A-Z0-9_]*$`) before the marker, referenced by `*_ref` metadata fields, captured into `WorkerResult.Blocks`, and resolved by `FencedBlockResolver.TryResolveRef` ([WorkerResultParser.cs:664-705](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs#L664-L705)). Per-phase block names and consumers:

| Phase | Block name | Metadata ref field | Consumer |
|---|---|---|---|
| plan | `PLAN_BODY` | `plan_body_ref` | `PlanPhase` -> `MarkdownRenderer.Render` -> Plane description |
| implement | `IMPLEMENT_SUMMARY` | `summary_ref` | `ImplementPhase` -> rendered HTML comment (optional) |
| implement | `COMPLETION_CLAIM` | `completion_claim_ref` | `ImplementPhase` -> `CompletionClaimParser` -> `GatePhase` (new, TLB-505) |
| review | `REVIEW_CRITIQUE` | `rationale_ref` | `WorkerAgentReviewer` -> `Verdict.Rationale` (falls back to direct `rationale`) |
| draft | `DRAFT_BODY` | `body_markdown_ref` | `DraftPhase` (falls back to legacy `body_markdown`) |
| batch implement | per-ticket summary blocks | `tickets[].summary_ref` | `ChainPhase` batch path |

Since 3cbf64c the brief templates instruct workers to emit the blocks AND the envelope **in one final message**; combined with full-transcript parsing this closed the truncated-envelope failure class. The `COMPLETION_CLAIM` block body is JSON, not markdown - parsed by `CompletionClaimParser.TryParse` ([CompletionClaimParser.cs:37](../../src/ThroughlineBuild.Workers.Common/CompletionClaimParser.cs#L37)), which requires all four arrays (`provides`, `consumes`, `ac_bindings`, `tests_added`, each possibly empty). A null claim (pre-claim-format worker) is allowed by the gate.

`metadata.escalation` (obsolete escalation with required `subsumed_by`) and `metadata.llm_usage` are unchanged in shape; `MarkdownRenderer.Render` ([MarkdownRenderer.cs:9](../../src/ThroughlineBuild.Workers.Common/MarkdownRenderer.cs#L9)) remains the hand-rolled CommonMark-subset renderer for block bodies into Plane HTML. Status: Functional.

### `ThroughlineBuild.Workers.ClaudeCode` / `.Codex` / `.Gemini` / `.Copilot`

Each exports its `IWorkerAgent` plus an `*Options` record, AOT JSON contexts, and (except Copilot) a progress digester. All four emit the same `WORKER_RESULT` contract through `Workers.Common`, print an agent/model startup line (TLB-468), and surface in-band worker errors instead of blank stderr (TLB-490). The Codex agent additionally carries reasoning-effort plumbing (`ModelTier.Effort`, op-33) and a model probe (`CodexModelProbe`) consumed by `init`/`models refresh`. The Claude agent fails fast with a clear classification when its configured model is unresolvable (TLB-544). Status: Functional.

### `ThroughlineBuild.Plane`

Public types: `PlaneTicketingClient` (implements `ITicketing`, `ITicketingConnectivity`, `IProjectDiscovery`, `ITicketingProvisioner`), `ProjectResolver` (implements `IProjectResolver`; [ProjectResolver.cs:13](../../src/ThroughlineBuild.Plane/ProjectResolver.cs#L13)), `PlaneClientOptions`, `PlaneApiException` (now carries `RetryAfter`), `RequestThrottle`. Retry policy is two-layered in `PlaneClientOptions` ([PlaneClientOptions.cs](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs)): HTTP-status retries (`MaxRetryAttempts`, 429/5xx, Retry-After-aware) and - new, TLB-545 - transport retries (`TransportRetryAttempts` at `#L47`, DNS/connect/TLS/timeout) that end in a typed `TicketingUnavailableException`. See [03-external-dependencies.md](03-external-dependencies.md). Status: Functional.

### `ThroughlineBuild.EventLog`

Public types: `JsonlEventSink`, `RecordingEventSink`, `EventLogOptions`, `SessionContext`, `SessionFileNameBuilder`, `ReviewFeedbackRetriever`; `EventLineDto` remains `internal` ([EventLineDto.cs:12](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12)). `ReviewFeedbackRetriever.GetLatestRework` ([ReviewFeedbackRetriever.cs:30](../../src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs#L30)) now also reconstructs persisted failing-check evidence - see the JSONL section.

#### JSONL event-log schema

Each line is a serialized `EventLineDto` wrapping a `WorkflowEvent` ([WorkflowEvent.cs:3-9](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L3-L9)). The six original fields keep PascalCase names; the four newer session-level fields are snake_case and `[JsonIgnore(WhenWritingNull)]` (`project_id`, `project_name`, `workspace_slug`, `build_version`), preserving the pre-TLB-147 shape for sinks without a `SessionContext`.

`Kind` serializes as the integer ordinal of `EventKind`. The enum now has **14 members** ([WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14)); ordinals are pinned in the comment above it and are load-bearing for the `analyze-event-log` and `token-audit` tools:

| Ordinal | EventKind |
|---|---|
| 0 | `StateTransition` |
| 1 | `LlmCall` |
| 2 | `WorkerSpawn` |
| 3 | `VerifierVerdict` |
| 4 | `GateFailure` |
| 5 | `TicketWrite` |
| 6 | `ChainStart` |
| 7 | `ChainEnd` |
| 8 | `ReworkRound` |
| 9 | `TicketSubsumed` |
| 10 | `TargetAutoRebased` |
| 11 | `DispatchStart` |
| 12 | `DispatchEnd` |
| 13 | `CostLedger` (new, TLB-510) |

`CostLedger` (13) is the cost/telemetry workhorse and, like `GateFailure`, carries a `kind` discriminator in `Data`: the per-ticket gate ledger emitted by `ChainEventEmitter.EmitCostLedgerAsync` ([ChainEventEmitter.cs:102](../../src/ThroughlineBuild.Phases/ChainEventEmitter.cs#L102), driven from `ImplementReviewLoop` at [ImplementReviewLoop.cs:90](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L90) - `gate_wall_ms`, gate-attributable rework rounds/tokens, `false_fails`, with `Phase: Phase.Gate`), `context_attribution` (per-turn context telemetry from claude-code workers, emitted in `ImplementPhase` at [ImplementPhase.cs:383-401](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L383-L401)), and `preload_summary` (pre-load telemetry from `ImplementPhase.BuildAndReportPreloadAsync` at [ImplementPhase.cs:644](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L644)).

The `GateFailure` (4) discriminator set keeps growing; new values since the last refresh include `claim_schema_invalid`, `setup_failed`, `gate_control_run` (all `GatePhase`), `preload_file_not_found`, `preload_empty` (`ImplementPhase`), alongside the existing hygiene/worktree kinds. Anything reading the discriminator must tolerate new `kind` values.

**`VerifierVerdict` now persists the oracle (7af36fb, new):** when the verdict is Rework, `ReviewPhase` writes the cited failing checks' raw evidence (command line, exit code, output tails, re-capped) into the event's `Data` under `checks_failed_details` ([ReviewPhase.cs:457-476](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L457-L476)); `ReviewFeedbackRetriever.ParseFailedCheckDetails` ([ReviewFeedbackRetriever.cs:186](../../src/ThroughlineBuild.EventLog/ReviewFeedbackRetriever.cs#L186)) reconstructs them so a rework resumed in a fresh process still hands the worker the check's own output. Advisory check failures are excluded from `checks_failed` by construction (see 07-contracts).

The format is documented in [docs/build-event-log-format.md](../build-event-log-format.md).

### `ThroughlineBuild.Helpers`

Pure helpers plus I/O-bearing orchestration primitives. New public surfaces are `WavePlanner` and its plan/ticket/conflict/rule records, `WorktreeLeaseManager`, lease request/result/manifest/options records, `IInstallCommandRunner`, and `TicketIdOrdering` ([WavePlanner.cs:3](../../src/ThroughlineBuild.Helpers/WavePlanner.cs#L3), [WorktreeLease.cs:7](../../src/ThroughlineBuild.Helpers/WorktreeLease.cs#L7), [WorktreeLeaseManager.cs:7](../../src/ThroughlineBuild.Helpers/WorktreeLeaseManager.cs#L7)). `MainWorktreeResolver` was removed; the CLI-internal `RepositoryLayout` now owns that concern. Existing scan, summary, chain, worktree, marker, and locking helpers remain Functional; `DocOnlyDetector` and `DriftComparator` remain production-unwired.

### `ThroughlineBuild.Git`

Public: `ProcessGitClient` (implements `IGitClient`; hardened against subprocess deadlock since 6b78877), `BaseRefResolver`.

### `ThroughlineBuild.Verification`

Public: `AutomatedChecksRunner` (now role-aware), `WorkerAgentReviewer` (the `IVerifier`; exposes `LastProviderError` for the TLB-527 ReviewUnavailable path, [WorkerAgentReviewer.cs:33](../../src/ThroughlineBuild.Verification/WorkerAgentReviewer.cs#L33)), `ObsoleteRatifier`, and the new gate-integrity types: `SmokeCollector` ([SmokeCollector.cs:11](../../src/ThroughlineBuild.Verification/SmokeCollector.cs#L11) - diff facts + grep signals, TLB-503), `GateVacuityProver` ([GateVacuityProver.cs:31](../../src/ThroughlineBuild.Verification/GateVacuityProver.cs#L31) - canary-driven non-vacuity proof on first green), `GateControlProber` ([GateControlProber.cs:32](../../src/ThroughlineBuild.Verification/GateControlProber.cs#L32) - base-ref control run, TLB-538), `PreComputedChecksRunner`, `ExecutableResolver`, `RatificationPromptLoader` (the obsolete-ratification prompt moved to an embedded template under this project's `Templates/`).

### `ThroughlineBuild.Scaffold`

Public: `ScaffoldPhase`, parser/validator/rendering types, op-doc records, `ProjectProfile` plus its parser/check schema, `OpDocSkeletonGenerator`, embedded docs, and prompt/rules loaders. `ScaffoldProfileDeriver` was removed; no Scaffold public type starts a worker. Profile prompt/apply orchestration now lives in Cli while this project owns the repository/rules resources and parsed contract. Status: Functional.

### `ThroughlineBuild.Commands`

Public: the `ITicketCommand` implementations, command runners and template loaders, plus the new generic `ICliVerb`/`CliVerbRegistry` contract that decouples verb identity from CLI composition ([CliVerbRegistry.cs:3](../../src/ThroughlineBuild.Commands/CliVerbRegistry.cs#L3)).

### `ThroughlineBuild.JudgmentSlots`

Public: `ReasonTranslator` only, constructed with an `ILlmClient`; its default model id is the `ModelId` const ([ReasonTranslator.cs:15](../../src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs#L15)).

### `ThroughlineBuild.ModelClient` and `ThroughlineBuild.Anthropic`

Unchanged in status: `IModelClient` and its request/response records remain **Partial as a public surface** - built and unit-tested, but no production path constructs an `IModelClient`. The live LLM path is still `LlmClientFactory.Create` -> `AnthropicClient : ILlmClient` ([CliApplication.cs:2407](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2407)); `AnthropicClient.InvokeStreamAsync` ([AnthropicClient.cs:93](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L93)) and `ModelClientLlmAdapter.InvokeStreamAsync` ([ModelClientLlmAdapter.cs:65](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L65)) still throw `NotImplementedException`. See [11-llm-architecture.md](11-llm-architecture.md).

---

## Surfaces called out for stability

- **`WORKER_RESULT` envelope JSON schema** - the contract between every worker agent and the orchestrator. Now explicitly includes: the optional `tickets` array (batch), the `envelope_status` salvage metadata key (`WorkerResultMetadata`), the `metadata.escalation`/`subsumed_by` sub-schema, and `metadata.llm_usage`. Single-final-message emission (blocks + envelope together) is part of the brief contract since 3cbf64c.
- **Fenced-block payload protocol** (`<<<NAME_START`/`<<<NAME_END` + `*_ref` fields; spec at [docs/op-docs/examples/op-27-worker-result-fenced-payloads.md](../op-docs/examples/op-27-worker-result-fenced-payloads.md)), now including the JSON-bodied `COMPLETION_CLAIM` block.
- **`COMPLETION_CLAIM` schema** (TLB-500/505) - `provides`/`consumes`/`ac_bindings`/`tests_added`, all required arrays; `ac_bindings[].kind` is a `VerifierKind` name. The hook fields (`RedGreenKind`, `Tier`, `RoutingKey`) are declared but UNENFORCED by every consumer - do not build against them. Status: Functional (claim emission + gate validation), with the hook fields Aspirational.
- **Plane marker comment formats** (`[planned_at:]`, `[implemented_at:]`, `[decomposed_at:]`, `[shipped_at:]`, `<strong>wontfix:</strong>`, `<strong>deferred:</strong>`, `<strong>reopened:</strong>`) - parsed back through the `MarkerParser` class ([MarkerParser.cs:5](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L5)) after HTML-tag stripping; freshest-by-timestamp lookup via `CommentMarkers.LatestValue`.
- **JSONL event log line schema** (`EventLineDto` + the `EventKind` integer ordinals, now 0..13) - the `analyze-event-log` and `token-audit` tools (sources in [tools/](../../tools/)) depend on it.
- **CLI exit code mapping**, including the `ChainOutcome` overrides (`ChainExitCodeMapper`).
- **The op-doc authoring format** - now self-published by `build op-doc spec` from a single embedded source (TLB-456) and consumed by `OpDocParser`. Treat the embedded spec as the authoritative format document; `op-doc new` skeletons are guaranteed to validate against it.
- **The derived-profile JSON schema** (`ProjectProfile` DTOs - `review_checks`/`regression_checks` with `role`, `canary`, `timeout_minutes`, plus `convention_files`) - produced by a worker during `scaffold`, written into `.build/config.toml` by `ConfigProfileWriter`, consumed by gate/review/ship checks. Stack knowledge lives in this derived data, not in engine code.
- **`WorkspaceSchema` states + labels** - what `build setup` provisions and the runtime state map derives from. Renaming a state in Plane without updating this class breaks transitions.
- **`build new --print-template`** output and the per-agent template directory layout under `Templates/<agent>/` (plus `Templates/shared/`).

---

## Loose ends

- **Mostly no public-vs-internal distinction.** Most library types default to `public`; no NuGet packaging, no API analyzer. Exceptions: `Workers.Common` internals (shared via a now-longer `InternalsVisibleTo` list that includes `Phases` and `Verification` - the parser internals are creeping toward de-facto public) and `EventLog.EventLineDto`.
- **`CliUsage.UsageText` vs `ChainExitCodeMapper`**: chain exit codes 10 (`GateEnvironmentFailure`) and 11 (`TicketingUnavailable`) are live but undocumented in the usage text.
- **Help registry coverage**: `models` and `sweep` have no Tier 1 help entry.
- **`CompletionClaim` hook fields** (`RedGreenKind`, `Tier`, `RoutingKey`) are dead-public by design - declared, serialized nowhere, ignored by all consumers.
- **The `IModelClient` surface is still built but unwired** - dead-public until something constructs it.
- **`src/ThroughlineBuild.Linear/`** is an empty directory (stale `bin`/`obj` only, no csproj, not in the solution) - either delete it or land the Linear backend it implies.
- **Brief template files** remain public-by-convention; the `shared/` fragment set adds a second axis (per-agent template x shared fragment) that must stay composable by hand.
- **No semantic versioning** - `BuildVersion.Current` is `0.1.0+<shortsha>`, generated at compile time; the `0.1.0` prefix is not bumped by anything.
