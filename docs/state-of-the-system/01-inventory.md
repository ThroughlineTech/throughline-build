# 01 - Inventory

Last refreshed: 2026-08-12 (HEAD 758ad56a)

Every command, library project, tool, script, and CI workflow currently in the repository, with a one-paragraph high-level description, inputs, outputs, and the major components it composes with. Status tags follow the convention defined in the index: Functional, Partial, Legacy, Aspirational, Broken.

For interface contracts see [07-contracts.md](07-contracts.md). For phase orchestration detail see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md). For the multi-vendor model/worker layout see [11-llm-architecture.md](11-llm-architecture.md).

---

## CLI verbs (the `build` binary)

`Program.cs` is a three-line process entry point that delegates to `CliApplication.RunAsync`. `CliVerbRegistryFactory.Verbs` declares all 38 action verbs and records whether each runs before configuration bootstrap ([CliVerbRegistryFactory.cs:7](../../src/ThroughlineBuild.Cli/CliVerbRegistryFactory.cs#L7)); `CliApplication` resolves the requested entry before executing either the pre-config band or the configured band. `CliBootstrap` resolves the repository layout, loads config and secrets, and creates one shared `HttpClient` and `PlaneTicketingClient` in an immutable `CliContext`. Help remains a separate registry: `HelpRegistryFactory.Build` declares 36 entries in four groups (Pipeline, Bring your own conductor, Work items, Configure) ([HelpRegistryFactory.cs:25](../../src/ThroughlineBuild.Cli/Help/HelpRegistryFactory.cs#L25)); `Tier0Renderer.Render` produces the grouped top-level menu, `Tier1Renderer.Render` produces per-command help, and `HelpTopicRegistry.Build` declares the reference topics.

All argument pre-passes live in `CliArgParser`: `ExtractBoolFlags` strips the global bare flags, `ExtractAgentFlags` pulls the `--agent` / `--agent-plan` / `--agent-implement` / `--agent-review` pairs, `ExtractChainTraversalFlags` pulls `--dry-run` / `--max-depth`, `ExtractBatchImplementFlag` pulls `--batch-implement`, and `ExtractTicketIds` extracts phase ticket IDs. Focused unit tests cover each pre-pass and registry tests pin the nine pre-config verbs so their ordering cannot silently regress.

Thirty-eight action verbs are reachable. The current registry adds `attachments` and `attachment` between the existing comment and evidence surfaces; the full ordered list is authoritative at `CliVerbRegistryFactory.Verbs` ([CliVerbRegistryFactory.cs:7-47](../../src/ThroughlineBuild.Cli/CliVerbRegistryFactory.cs#L7-L47)). Help and version are meta-surfaces. `CliApplication.RunAsync` performs dispatch ([CliApplication.cs:34](../../src/ThroughlineBuild.Cli/CliApplication.cs#L34)). `init`, `install`, `settarget`, `user-guide`, `op-doc`, `models`, `sop`, `conductor`, and `profile` dispatch before full config load.

### `build init [--force --print-template --no-interactive --from FILE --plane-url --workspace --project-id --project-name --token|--token-env]` - Functional
Dispatched pre-config-load at [CliApplication.cs:231-290](../../src/ThroughlineBuild.Cli/CliApplication.cs#L231-L290), implemented by `InitCommand.ExecuteAsync` ([InitCommand.cs:68](../../src/ThroughlineBuild.Cli/InitCommand.cs#L68)). Bootstraps `.build/config.toml` from the embedded template. Substantially grown since the last refresh (op-33/op-34 bootstrap-onboarding): unknown/misspelled flags are rejected with exit 2 via `CliArgParser.FindUnknownFlag`; `--from FILE` (or redirected stdin) reads a key=value credentials file parsed by `CredsFileParser` ([CredsFileParser.cs:22](../../src/ThroughlineBuild.Cli/CredsFileParser.cs#L22)); interactive mode at a TTY prompts for base URL, workspace, and token, then offers a create-or-pick project menu (the operator never pastes a project UUID) backed by `IProjectDiscovery`/`ProjectResolver`; `--project-name` resolves or creates the project by name non-interactively; after provisioning, init makes a welcome commit via `WelcomeCommit.EnsureInitialCommit` ([WelcomeCommit.cs:12](../../src/ThroughlineBuild.Cli/WelcomeCommit.cs#L12)) and probes Codex model tiers via `CodexModelProbe` to seed `[workers.codex.sizes]`. Flag values still replace `REQUIRED_*` placeholders; `--print-template` stays offline; refuses to overwrite without `--force`.

### `build install [--profile <path|-> [--force] | --invariants <path|->] [init options] [--json]` - Functional
`InstallCommand.ExecuteAsync` is a worker-free, three-stage readiness flow ([InstallCommand.cs:175](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L175)). A first invocation runs init/setup as needed and returns a repository-profile prompt; `--profile` parses and canary-proves `PROJECT_PROFILE` JSON, updates tracked `.build/config.toml`, installs the embedded SOP catalog, and returns a conductor-invariant prompt; `--invariants` atomically applies the invariant block and proves doctor, checks, secrets, branch cleanliness, and the worktree-lease surface before returning READY. Each handoff exits successfully but explicitly says STOP and names the next command. It does not start a worker or run the target stack's build/test commands; repeat invocations are idempotent, and the profile/SOP stage rolls config and conductor state back on failure ([InstallCommand.cs:389](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L389), [InstallCommand.cs:425](../../src/ThroughlineBuild.Cli/InstallCommand.cs#L425)).

### `build setup [--check] [--write-token-file <path>]` - Functional
Dispatched by `CliApplication` ([CliApplication.cs:1314](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1314)), implemented by `SetupCommand.ExecuteAsync` ([SetupCommand.cs:40](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L40)). Makes a freshly init'd project workflow-ready: (1) local repo - `git init` if needed plus a language-neutral `.gitignore` block via `GitignoreManager`/`FileSystemLocalRepoOps`, then the welcome commit; (2) Plane - creates any missing states and labels against `WorkspaceSchema` through the `ITicketingProvisioner` face of `PlaneTicketingClient`. Idempotent. `--check` mutates nothing and exits 1 when any local or Plane gap remains, else 0. `--write-token-file` persists the already-resolved Plane token to a named file and writes only its path into config; it cannot be combined with `--check` ([CliApplication.cs:1315](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1315), [TokenFileInstaller.cs:14](../../src/ThroughlineBuild.Cli/TokenFileInstaller.cs#L14)). After Plane provisioning setup also runs Claude transport capability preflight.

### `build settarget [<branch> | --unset]` - Functional
Dispatched pre-config-load at [CliApplication.cs:294-301](../../src/ThroughlineBuild.Cli/CliApplication.cs#L294-L301), implemented by `SetTargetCommand.Execute` ([SetTargetCommand.cs:31](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs#L31)). Manages the `[work].target_branch` key in `.build/config.toml`. Three modes: set (validates the branch exists locally via `DefaultBranchValidator`, then line-edits the key in), unset (removes the key, no-op if absent), and display (prints the resolved value with its source label). TOML is line-edited, not parsed-and-reserialized, so comments and formatting survive. Consumed by ship and the implement/chain phases via `BuildConfig.ResolveTargetBranch` ([Config.cs:89](../../src/ThroughlineBuild.Cli/Config.cs#L89)).

### `build user-guide [--force --print-template]` - Functional
Dispatched pre-config-load at [CliApplication.cs:304-309](../../src/ThroughlineBuild.Cli/CliApplication.cs#L304-L309), implemented by `UserGuideCommand.Execute` ([UserGuideCommand.cs:19](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L19)). Writes the embedded operator guide (loaded by `UserGuideLoader` in the Commands project) to `docs/throughline_build_userguide.md`; `--print-template` prints to stdout; refuses to overwrite without `--force` (exit 2).

### `build op-doc spec [--print --write --force]` / `build op-doc new <slug> [--write]` - Functional
Dispatched pre-config-load at [CliApplication.cs:313-398](../../src/ThroughlineBuild.Cli/CliApplication.cs#L313-L398). `spec` prints (default) or materializes the embedded op-doc authoring spec to `docs/op-docs/op-doc-spec.md` via `OpDocSpecCommand.Execute` ([OpDocSpecCommand.cs:19](../../src/ThroughlineBuild.Cli/OpDocSpecCommand.cs#L19)) and `OpDocDocsLoader`. `new <slug>` validates the slug as kebab-case and emits a minimal valid op-doc skeleton via `OpDocSkeletonGenerator.Render` ([OpDocSkeletonGenerator.cs](../../src/ThroughlineBuild.Scaffold/OpDocSkeletonGenerator.cs)) to stdout, or with `--write` to `docs/op-docs/op-<slug>.md` (refuses to clobber, exit 2). Both run without config.

### `build models refresh` - Functional
Dispatched pre-config-load at [CliApplication.cs:403-420](../../src/ThroughlineBuild.Cli/CliApplication.cs#L403-L420), implemented by `ModelsRefreshCommand.Execute` ([ModelsRefreshCommand.cs:24](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L24)). Re-probes Codex (`codex debug models`) via `CodexModelProbe` ([CodexModelProbe.cs:40](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs#L40)), maps the discovered menu to size tiers via `CodexTierMapper` ([CodexTierMapper.cs:26](../../src/ThroughlineBuild.Cli/CodexTierMapper.cs#L26)), prints a current-to-proposed diff, and rewrites ONLY the `[workers.codex.sizes]` block in place (`CodexSizesBlockReader`/`Editor`/`Renderer`). A probe failure leaves the config byte-unchanged (exit 1); no config found is exit 2.

### `build profile prompt|apply|verify-canaries` - Functional
`ProfileCommand` is the worker-free replacement for scaffold-time profile derivation ([ProfileCommand.cs:26](../../src/ThroughlineBuild.Cli/ProfileCommand.cs#L26)). `prompt` emits the embedded repository-inspection prompt. `apply <file|->` parses a `PROJECT_PROFILE`, refuses clobber unless `--force`, and by default creates a temporary worktree, runs the install command, runs the proposed setup/gating checks, and proves every gating canary fails before atomically updating `.build/config.toml`; `--skip-canary` is an explicit recorded opt-out. `verify-canaries` performs the same proof without writing config ([ProfileGateVerifier.cs:18](../../src/ThroughlineBuild.Cli/ProfileGateVerifier.cs#L18)). Reapplying an identical profile is a no-op success.

### `build conductor prompt|apply` - Functional
`ConductorCommand.Execute` runs before config and never touches ticketing, secrets, workers, or the network ([ConductorCommand.cs:25](../../src/ThroughlineBuild.Cli/ConductorCommand.cs#L25)). `prompt` emits the embedded invariant-authoring prompt. `apply <path|->` validates structured invariant TOML and atomically replaces only the contiguous `[[conductor.review.invariants]]` block in tracked `.build/conductor.toml`, preserving the rest byte-for-byte; malformed, empty, duplicate, placeholder, or misplaced invariant data is refused before the temp-file rename ([ConductorCommand.cs:10](../../src/ThroughlineBuild.Cli/ConductorCommand.cs#L10), [ConductorCommand.cs:359](../../src/ThroughlineBuild.Cli/ConductorCommand.cs#L359)).

### `build sop list|doctor|brief|install|upgrade|uninstall|status` - Functional
The SOP surface is backed by resources embedded in the CLI binary, including `run-backlog` and `cross-impact` ([SopResourceLoader.cs:9](../../src/ThroughlineBuild.Cli/SopResourceLoader.cs#L9)). `list` reports the catalog; `doctor` validates conductor schema, review-check capability, tracked/missing stubs, placeholders, repository paths, minimum build version, and inline-token leakage without loading full ticketing/worker config; `brief` emits a versioned JSON procedure envelope only after doctor succeeds. Admission briefs additionally pin an absolute worktree root and full inspection SHA and activate a mutating-verb refusal policy. Catalog-driven install/upgrade/uninstall/status manage Claude/Codex host stubs plus scaffolded `.build/conductor.toml`; emitted files are hash-gated, local edits are preserved, all targets are root-contained and symlink/reparse-refused, and `.build/sop-manifest.json` is only a cache ([SopDoctorCommand.cs:116](../../src/ThroughlineBuild.Cli/SopDoctorCommand.cs#L116), [SopInstaller.Run:525](../../src/ThroughlineBuild.Cli/SopInstallCommand.cs#L525)). No SOP verb starts a worker.

### `build worker brief ...` - Functional
`WorkerBriefCommand.ExecuteAsync` reads one Plane ticket plus a supplied existing worktree and writes one inspectable implement, review, or rework Markdown brief ([WorkerBriefCommand.cs:33](../../src/ThroughlineBuild.Cli/WorkerBriefCommand.cs#L33)). Review briefs include the current diff/status; rework briefs preserve prior blocking findings; all carry the ticket's recorded semantic contract. Agent-template precedence is per-role override, `--agent`, configured phase, then default. The command does not start a worker or mutate tickets, git refs, worktrees, or deployments.

### `build worktree lease|list|teardown ...` - Functional
`WorktreeCommand.ExecuteAsync` exposes `WorktreeLeaseManager` as a standalone conductor primitive ([WorktreeCommand.cs:8](../../src/ThroughlineBuild.Cli/WorktreeCommand.cs#L8), [WorktreeLeaseManager.cs:7](../../src/ThroughlineBuild.Helpers/WorktreeLeaseManager.cs#L7)). Lease derives a contained helper path/branch from the ticket, optionally copies one allowlisted seed, runs the configured install command with stdin closed, and writes `.build-worktree-lease.json`. List reports valid leases and unmanifested directories. Teardown validates the manifest and containment, refuses dirty/unmerged work unless its explicit gates pass, removes the worktree, and deletes the helper branch only when safe; `--force` is destructive and explicit.

### `build gate [--ticket <id>] [--role gating|advisory|all] [--require-checks] [--json]` - Functional
`GateCommand.ExecuteAsync` loads only the standalone gate portion of config and runs setup plus selected review checks in the current working directory ([GateCommand.cs:10](../../src/ThroughlineBuild.Cli/GateCommand.cs#L10)). Gating canaries must already be verified in persisted config; missing or inconclusive proof fails closed. `--require-checks` converts the otherwise successful empty-check case into failure. It emits per-check results and proof status, touches no ticket, and starts no worker.

### `build waves --input <path|-> [--json]` - Functional
`WavesCommand.ExecuteAsync` accepts a ticket-array or wave-plan JSON object and feeds deterministic `WavePlanner.Plan` ([WavesCommand.cs:11](../../src/ThroughlineBuild.Cli/WavesCommand.cs#L11), [WavePlanner.cs:64](../../src/ThroughlineBuild.Helpers/WavePlanner.cs#L64)). It topologically orders dependencies, serializes configured global/cohesive-module/pairwise path conflicts, applies `[waves].cap`, and reports both schedule and speedup verdict. Invalid scope/input exits 2; dependency cycles exit 5. It performs no ticket, git, or worker mutation.

### `build candidate status --ticket <id> --base <ref> [--json]` - Functional
`CandidateStatusCommand.ExecuteAsync` fingerprints a current worktree without mutation ([CandidateStatusCommand.cs:17](../../src/ThroughlineBuild.Cli/CandidateStatusCommand.cs#L17)). It resolves base/head/branch, hashes full-index tracked and cached diffs plus sorted untracked regular files, reports touched paths and dirty/conflict state, and validates any lease manifest against the requested ticket. Missing refs, conflicted trees, unreadable paths, untracked directories, and symlink/reparse-point inputs fail closed.

### `build plan <ticket-id> [ticket-id ...] [--from-brief]` - Functional
Dispatched per-ticket from the sequential multi-ticket loop ([CliApplication.cs:1319-1334](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1319-L1334)) into the `plan` branch of `RunTicketVerbBodyAsync` ([CliApplication.cs:1602-1650](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1602-L1650)). Investigates a `Backlog` ticket and writes the plan to the ticket description, plus risk/size labels and a `[planned_at: <sha>]` marker comment. Standalone `build plan` ignores `[plan].mode`; only the explicit `--from-brief` flag skips the worker and promotes the Backlog ticket to Ready using the description already on the ticket.

- **Inputs:** one or more ticket ids; `--debug | --quiet`, `--summary-json`, `--error-location`, `--agent <name>`, `--from-brief`. Reads `.build/config.toml`, the Plane ticket, current main SHA, top-level cwd entries.
- **Side effects:** spawns the configured worker in the main worktree (no branch cut), writes Plane HTML description + labels + one comment, appends events to `.build/events/<stem>.jsonl`, captures worker stdio under `--debug`. Deterministic completion summary via `PhaseSummaryBuilder.BuildPlan`.
- **Exits:** 0 success, 1 phase failure, 2 missing/unknown id, 3 missing secret, 4 infra.
- **Invokes:** `PlanPhase`, `PlanBriefBuilder`, `PlaneTicketingClient`, agent via `EffectiveAgentFor("plan")`, `JsonlEventSink`.

### `build implement <ticket-id> [ticket-id ...]` - Functional
The `implement` branch of `RunTicketVerbBodyAsync` ([CliApplication.cs:1652-1713](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1652-L1713)). Cuts a worktree, transitions `Ready -> InProgress`, dispatches the implementer worker, records `[implemented_at: <sha>]`, transitions `InProgress -> InReview`. Same multi-ticket sequential loop, flags, and exit codes as `plan`. The summary best-effort attaches diff stats and recent commit onelines via the shared read-only git client; the diff base is now target-aware via `BaseRefResolver.ResolveAsync` ([BaseRefResolver.cs:24](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L24)).

- **Invokes:** `ImplementPhase`, `ImplementBriefBuilder`, `ProcessGitClient`, agent via `EffectiveAgentFor("implement")`, `PhaseSummaryBuilder.BuildImplement`.

### `build review <ticket-id> [ticket-id ...]` - Functional
The `review` branch of `RunTicketVerbBodyAsync` ([CliApplication.cs:1715-1769](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1715-L1769)). Runs configured automated checks against the feature branch, dispatches a verifier worker, records `Verdict { Pass | Rework | Fail }`; on `Rework` transitions `InReview -> InProgress`. When the chosen review agent cannot enforce `verifier_allowed_tools`, `VerifierToolEnforcement.UnenforcedWarning` ([VerifierToolEnforcement.cs:20](../../src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs#L20)) prints an honesty warning to stderr.

- **Exit codes:** 0 Pass, 1 Rework/Fail, 4 verifier infra failure.
- **Invokes:** `ReviewPhase`, `ReviewBriefBuilder`, `WorkerAgentReviewer` (the `IVerifier`), `AutomatedChecksRunner`, `PhaseSummaryBuilder.BuildReview`.

### `build ship <ticket-id> [ticket-id ...] [--no-auto-merge --no-push --skip-baseline]` - Functional
The `ship` branch of `RunTicketVerbBodyAsync` ([CliApplication.cs:1771-1868](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1771-L1868)). Deterministic phase, no worker subprocess. Resolves the merge target (`[work].target_branch` if set, else `[ship].base_branch` - `ShipOptions.TargetBranch` resolution at [ShipPhase.cs:251](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L251)), fetches, ancestry-checks and (on clean divergence) auto-rebases, scans for conflict markers, runs `ship.regression_checks`, fast-forward-merges the feature branch, pushes the target unless pushing is disabled (`--no-push` or `[ship] push = false`, threaded as `ShipOptions.NoPush` at [CliApplication.cs:1781](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1781)), posts `[shipped_at: <sha>]`, transitions `InReview -> Done`, decrufts the worktree.

- **Baseline awareness:** failing regression checks are compared against a per-onto-SHA baseline (`BaselineCache` in Helpers) probed by `GateControlProber`, so a check that already fails on the untouched base is not blamed on the ticket; `--skip-baseline` disables this.
- **Preflight guard:** when the target branch is non-default, ship checks the main worktree is on that branch and otherwise emits a `wrong_worktree_branch` `GateFailure` ([ShipPhase.cs:272](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L272)).
- **`--debug`** makes ship verbose (streams git output and full check results to stderr via the `verbose` constructor flag, [CliApplication.cs:1788](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1788)); phase-level progress lines print regardless.
- **Exit codes:** mapped from `ShipFailureStage` at [CliApplication.cs:1856-1866](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1856-L1866): 0 success or post-success decruft warning; 1 rebase / conflict-marker / regression; 4 state-check / fetch / FF-merge infra.
- **Invokes:** `ShipPhase`, `ProcessGitClient`, `BaseRefResolver`, `AutomatedChecksRunner`, `ConflictMarkerScanner`, `WorktreeDecrufter`, `PhaseSummaryBuilder.BuildShip`.

### `build chain <ticket-id> [ticket-id ...] [--batch-implement [<ids>] --dry-run --max-depth <n> --no-auto-resolve --no-auto-merge --continue-past-failure --from-brief]` - Functional
The `chain` branch of `RunTicketVerbBodyAsync` delegates to `RunChainVerbAsync` ([CliApplication.cs:2304](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2304)). End-to-end orchestration is split by responsibility: `ChainPhase.RunAsync` ([ChainPhase.cs:176](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L176)) owns root classification and leaf phase routing; `ParentChainRunner.RunAsync` ([ParentChainRunner.cs:64](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L64)) traverses subtrees post-order and coordinates batching; `ChainIntegrationBranch` owns integration worktree creation, refresh, accumulation, landing, and cleanup. A parent cuts a local `chain/<slug>` branch, children ship into it with local-only integration ships, nested parents merge their integration branch upward, and only the outermost chain lands the accumulated branch onto the configured target and pushes. A reused integration branch is refreshed against its current base before children dispatch (`RefreshIntegrationBranchAsync`, [ChainIntegrationBranch.cs:282](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L282)). The per-ticket implement-review loop keeps `MaxReworkRounds = 2` ([ImplementReviewLoop.cs:17](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L17)). Between implement and review a deterministic `GatePhase` runs review checks once on the warm worktree; the CLI also builds the vacuity and base-ref control probes. Construction of the full chain assembly goes through `ChainPhaseComposition.BuildChainPhase` ([ChainPhaseComposition.cs:24](../../src/ThroughlineBuild.Cli/ChainPhaseComposition.cs#L24)) so tests can verify required dependencies are not dropped.

- **`--dry-run`** prints the full post-order tree schedule and branch topology without executing phases (`ChainOutcome.DryRunPreview`). **`--max-depth <n>`** is root-based (default 16, `ChainPhaseOptions.MaxDepth` at [ChainPhase.cs:34](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L34)); 0 is root-only. **`--batch-implement`** batches the implement phase for direct leaf children in one warm worker session - bare flag batches all eligible children, a comma list batches exactly that sibling group (validated by `ValidateBatchImplementGroupAsync`, [CliApplication.cs:2720](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2720)); batch size caps from `[batch]` config apply, oversized groups fall back to per-ticket chain. Internal-node candidates are downgraded to per-child recursion, and `BatchCommitVerifier` checks the batch commit stack afterward.
- **Multi-ticket dispatch:** extra positional IDs build a `TicketGraph` from `blocked_by` relations and run level-synchronously through `ParallelDispatcher`, whose execution width is pinned to 1 ([CliApplication.cs:2566](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2566)); a residual `SequentialChainDispatcher` fallback path also remains. `--continue-past-failure` runs descendants of a failed ancestor anyway (default: skipped).
- **Flags:** per-phase agent overrides (`--agent-plan`/`--agent-implement`/`--agent-review`; per-phase flag beats `--agent` beats config, `EffectiveAgentFor` at [CliApplication.cs:1699](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1699)), `--no-auto-resolve`, `--no-auto-merge`, `--from-brief`, `--debug`.
- **Exit codes:** centralized in `ChainExitCodeMapper.GetExitCode` ([ChainExitCodeMapper.cs:13](../../src/ThroughlineBuild.Cli/ChainExitCodeMapper.cs#L13)): 0 Completed / RatifiedObsolete / ParentCompleted / DryRunPreview; 2 RefusedInitialState / RefusedDirtyTree / RefusedWrongBranch / ParentHasGrandchildren; 3 StoppedAtPlan / ParentStoppedEarly / Skipped; 4 StoppedAtImplement; 5 StoppedAtReview; 6 ReworkCapExceeded; 7 StoppedAtShip; 8 GateVacuous; 9 ReviewUnavailable (verifier blocked by provider quota/rate-limit/auth, TLB-527); 10 GateEnvironmentFailure (TLB-538); 11 TicketingUnavailable (transport-level Plane outage, TLB-545). The 18-value `ChainOutcome` enum is declared at [ChainOutcome.cs:3](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs#L3).
- **Output discipline:** `ChainPhaseComposition` selects one human-output writer and injects distinct output and diagnostics writers into the phase assembly. Dry-run plans, dependency order, integration notices, `ChainCommand` start/step/final/child/triage text, aggregate reports, and multi-root result lines all use the human writer; warnings, refusals, and recovery notices use diagnostics. The chain verb emits no `--summary-json` envelope of its own (`WriteSummary`/`WriteSummaryLocal` are reached only from the decompose/plan/implement/review/ship branches), so its human writer stays bound to stdout under that flag - suppressing it would silence the verb with nothing to replace it (TLB-580). Phase progress remains routed through `ChainPhaseOptions.OnStep`.
- **Invokes:** `ChainPhase`, `GatePhase`, `ParallelDispatcher`, `TicketGraph`, `ChainCommand`, `DefaultChainRunner`, `SequentialChainDispatcher`, `ObsoleteRatifier`, `PreComputedChecksRunner` (review reuses gate check results so each ticket builds once).

### `build rework <ticket-id> [--feedback "..."]` - Functional
Single ticket only (multi rejected at [CliApplication.cs:192-196](../../src/ThroughlineBuild.Cli/CliApplication.cs#L192-L196)); the `rework` branch at [CliApplication.cs:1386-1454](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1386-L1454). Re-implements a ticket whose last `Verdict` was `Rework`, retrieving feedback from the event log via `ReviewFeedbackRetriever` (or `--feedback`).

- **Exit codes:** mapped from `ReworkOutcome` at [CliApplication.cs:1437-1444](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1437-L1444): 0 Implemented, 2 TicketNotInProgress, 3 NoFeedbackAvailable, 4 ImplementFailed.
- **Invokes:** `ReworkPhase` -> `ImplementPhase`; `DefaultReworkRunner`, `ReworkCommand`, agent via `EffectiveAgentFor("implement")`.

### `build decompose <ticket-id>` - Functional
Single ticket only; the `decompose` branch at [CliApplication.cs:1455-1510](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1455-L1510). Fetches the ticket, dispatches a worker (agent via `EffectiveAgentFor("decompose")`) to split it into independently-shippable sub-tickets, creates the children parent-linked in Plane, prints a decompose summary. Exits: 0 success, 1 phase failure, 2 ticket not found. The `--n`/`--no-promote` flags of the old slash command are still NOT parsed by the CLI.

### `build new <body-path | text | -> [--title --type --label --review --print-template]` - Functional
Dispatched at [CliApplication.cs:705-1138](../../src/ThroughlineBuild.Cli/CliApplication.cs#L705-L1138); modes selected by `NewVerbArgumentClassifier.Classify`. File mode files an existing body file directly via `NewPhase`; draft mode (free text or stdin `-`) spawns the implement-phase agent through `DraftPhase` and optionally an interactive `ReviewLoop` (`--review`: accept / edit / regenerate / quit); `--print-template` emits the embedded body template. A path-looking argument with no file prints a notice and brief Ctrl-C pause before drafting.

### `build scaffold <op-doc-path> [--validate-only --dry-run --accept-warnings]` - Functional
The configured scaffold branch in `CliApplication` parses a Markdown op doc into a plan -> brief ticket hierarchy in Plane ([CliApplication.cs:1915](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1915)). Exit categories remain Clean=0, ValidationError=2, PartialCreation=3, BackendUnavailable=4, unexpected=1. Profile derivation is no longer part of this verb: the former `ScaffoldProfileRunner` and `ScaffoldProfileDeriver` were deleted; use worker-free `build profile prompt`, an external agent, and `build profile apply` instead.

- **Invokes:** `OpDocParser`, `OpDocValidator`, `ScaffoldPhase`, `BriefHtmlRenderer`, `ScaffoldCommand`.

### `build sweep [--target <branch>] [--force]` - Functional
Dispatched at [CliApplication.cs:480-517](../../src/ThroughlineBuild.Cli/CliApplication.cs#L480-L517), implemented by `ChainWorktreeSweeper.SweepAsync` ([ChainWorktreeSweeper.cs:47](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs#L47)) (TLB-531). The recovery path after an interrupted or preserved-on-failure chain: removes leftover `.worktrees/ticket-*` and `chain-*` worktrees and deletes their branches **only when fully merged** into the target (default: resolved `[work].target_branch`; `--target` overrides). `--force` also removes worktrees whose branch is unmerged, but keeps the branch - unshipped commits are never lost. Pure git + filesystem; no worker, no Plane. Exit 1 if any worktree could not be removed.

### `build list [--state <name>] [--parent <id>] [--type <name>]` - Functional
Dispatched at [CliApplication.cs:519-559](../../src/ThroughlineBuild.Cli/CliApplication.cs#L519-L559), implemented in `ListCommand` ([ListCommand.cs](../../src/ThroughlineBuild.Commands/ListCommand.cs)). Queries tickets with optional filters and renders a fixed-width table. No event log. The `--all`/`--feature` flags of the old slash command remain unsupported.

### `build get <ticket-id> [--json]` - Functional

Resolves one configured-project ticket and prints a human view or a `TicketView` JSON envelope through `CliEnvelopeWriter.ToView` ([CliEnvelopeWriter.cs:255](../../src/ThroughlineBuild.Cli/Json/CliEnvelopeWriter.cs#L255)). The ordinary ticket read does not hydrate live relation edges; use `build relate <id> --list` for that graph.

### `build comments <ticket-id> [--json]` / `build comment <ticket-id> <text|-> [--json]` - Functional

`comments` cursor-paginates the full comment stream, bounded by the Plane client's 50-page cap, and HTML-to-text normalizes it; `comment` creates one comment, with `-` reading stdin. Their versioned JSON shapes are `CommentsEnvelope` and `CommentCreatedEnvelope` ([CliEnvelope.cs:125-137](../../src/ThroughlineBuild.Cli/Json/CliEnvelope.cs#L125-L137), [PlaneTicketingClient.FetchAllCommentsAsync:1771](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1771)).

### `build attachments <ticket-id> [--json]` - Functional

Lists normalized metadata for ticket-owned work-item attachments followed by supported inline description images, de-duplicated by UUID. It reads Plane only and emits either human rows or `AttachmentsEnvelope`; missing tickets, bad arguments, cancellation, and backend failures remain distinct error paths ([CliApplication.cs:987-1052](../../src/ThroughlineBuild.Cli/CliApplication.cs#L987-L1052), [PlaneTicketingClient.DiscoverAttachmentsAsync:1270](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1270)).

### `build attachment <ticket-id> <asset-id> --output <path> [--json]` - Functional

Re-runs the same discovery used by `attachments`, refuses an asset UUID not currently owned by that ticket, follows the Plane detail redirect or inline-asset storage URL without forwarding the Plane API key to storage, and writes bytes through a same-directory temporary file plus atomic non-overwriting move. Binary bytes never use stdout; JSON returns only normalized metadata and the requested path ([CliApplication.cs:1054-1137](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1054-L1137), [CliApplication.WriteAttachmentAtomicallyAsync:3175](../../src/ThroughlineBuild.Cli/CliApplication.cs#L3175), [PlaneTicketingClient.DownloadAttachmentAsync:1219](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L1219)).

### `build evidence add --ticket <id> --kind <claim|review|commit|integrate|gate|final> ...` - Functional

`EvidenceCommand.ExecuteAsync` validates the kind-specific SHA, verdict/gate result, fingerprint, and one-line optional fields; posts exactly one structured comment; then reads that returned comment id back ([EvidenceCommand.cs:49](../../src/ThroughlineBuild.Cli/EvidenceCommand.cs#L49)). Evidence never changes lifecycle state. Read-back proves only that the id is present, not body equality; if posting succeeded but read-back fails the command reports the id and does not retry, so the safe recovery is `build comments <id>` before another add.

### `build transition <ticket-id> <state> [--json]` - Functional

Transitions directly to a named lifecycle state and returns a typed acknowledgement. Backend state validation remains owned by `PlaneTicketingClient.TransitionAsync` ([PlaneTicketingClient.cs:953](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L953)).

### `build relate <ticket-id> <kind> <target> | --list | --remove <relation-id> [--json]` - Functional

Creates, lists, or removes a typed Plane relation. Accepted kinds are centralized in `RelationKinds.Allowed` ([RelationKinds.cs:6](../../src/ThroughlineBuild.Contracts/RelationKinds.cs#L6)); the command accepts bare sequence numbers, rejects cross-project prefixes, and returns stable backend edge IDs for removal.

### `build amend <ticket-id> [--title "..."] [--priority urgent|high|medium|low|none] [--type <name>] [--label-add <name>]... [--label-remove <name>]... [--parent <id>] [--size S|M|L] [--note "..."] [--description <path|->] [--ac <path|->]` - Functional
Implemented by `AmendCommand.ExecuteAsync` ([AmendCommand.cs:17](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L17)). It resolves and validates all file/stdin payloads, parent, issue type, and labels before the first mutation, then applies title, priority, type, labels/size, parent, note, description, and AC sequentially. The write set is not transactional: a later failure can leave earlier updates committed. Both `--description` and `--ac` replace the complete Plane description through `UpdateDescriptionAsync`; neither isolates an acceptance-criteria subsection. Done and Cancelled tickets are refused.

### `build close <ticket-id> <reason>` - Functional
`CloseCommand` ([CloseCommand.cs](../../src/ThroughlineBuild.Commands/CloseCommand.cs)). Translates the reason via `ReasonTranslator`, posts a `wontfix:` comment, transitions `-> Cancelled`, attempts a parent rollup, decrufts the worktree. **Changed:** a missing LLM secret no longer exits 3 - `WireUpConditionalCommands` falls back to `EchoLlmClient` with a warning and records the reason verbatim ([CliApplication.cs:3245-3254](../../src/ThroughlineBuild.Cli/CliApplication.cs#L3245-L3254)); close/defer/reopen are deterministic state commands and always run.

### `build defer <ticket-id> <reason>` - Functional
`DeferCommand` ([DeferCommand.cs](../../src/ThroughlineBuild.Commands/DeferCommand.cs)). As `close`, with marker `deferred:` and a note that branches are left in place. The v1.1 rollup-preview TODO is still open ([DeferCommand.cs:120](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L120)).

### `build reopen <ticket-id> [reason]` - Functional
`ReopenCommand` ([ReopenCommand.cs](../../src/ThroughlineBuild.Commands/ReopenCommand.cs)). Valid only from `Done`/`Cancelled`; scans recent comments for prior `deferred:`/`wontfix:` markers, picks a target state, transitions.

### `build help [topic]` / `build --help` / `build -V|--version` - Functional
See the tiered help description above. `build help <unknown-topic>` prints the topic list to stderr and exits 2.

### Loose ends (CLI verbs)
- **`sweep` and `models` are missing from the help registry.** `HelpRegistryFactory.Build` declares 34 commands; `sweep` and `models` are not among them, so `build sweep --help` falls back to the tier-0 menu instead of per-command help, and neither appears in the tier-0 listing.
- **`CliUsage.UsageText` is Legacy.** No production code references `CliUsage` ([CliUsage.cs:3](../../src/ThroughlineBuild.Cli/CliUsage.cs#L3)) - only tests assert against it. Its text has drifted from behavior in at least one place: it claims ship `--debug` "is a no-op", but `ShipPhase` is constructed with `verbose: debugMode` and streams git/check output ([CliApplication.cs:1788](../../src/ThroughlineBuild.Cli/CliApplication.cs#L1788)).
- **Chain exit codes 10 and 11 are undocumented in help.** `ChainExitCodeMapper` maps `GateEnvironmentFailure -> 10` and `TicketingUnavailable -> 11`, but neither the `exit-codes` help topic (`HelpTopicContent.ExitCodes`) nor `CliUsage` lists them (both stop at 9).
- **`--n` / `--no-promote` (decompose)** and **`--all` / `--feature` (list)** remain slash-command-layer flags the CLI does not parse.
- **`build amend --ac`** still calls `UpdateDescriptionAsync` ([AmendCommand.cs:136](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L136)) - the same call `--description` uses - so it replaces the whole description; if both are passed, the second write wins.
- **Multi-ticket chain still has two overlapping code paths** (`ParallelDispatcher` vs the `SequentialChainDispatcher` fallback); the in-code comment at [CliApplication.cs:2235-2236](../../src/ThroughlineBuild.Cli/CliApplication.cs#L2235-L2236) still marks it transitional (TLB-312).

---

## Library projects (`src/ThroughlineBuild.*/`)

Twenty tracked projects under `src/` (1 entry point + 19 libraries), as listed by `throughline-build.sln`. Approximate dependency order: `Contracts` -> `ModelClient`, `Git`, `Helpers`, `EventLog`, `Plane`, `Briefs`, `JudgmentSlots` -> `Anthropic`, `Workers.Common`, `Verification` -> `Workers.{ClaudeCode,Codex,Gemini,Copilot}`, `ThroughlineBuild.ClaudeCode`, `Scaffold`, `Phases` -> `Commands` -> `Cli`. The public facade consumes `Workers.ClaudeCode`; the CLI continues to consume the worker project directly.

| Project | Status | Role |
|---|---|---|
| `ThroughlineBuild.Contracts` | Functional | Interfaces, records, enums; no I/O. Grown since last refresh: `IProjectDiscovery`, `IProjectResolver`, `ITicketingProvisioner`, `TicketingUnavailableException` (TLB-545), `WorkspaceSchema` (canonical states/labels), and new models incl. `BatchTicketResult`, `BatchWorkerResult`, `CompletionClaim`, `DebugTranscriptContext`, `DirtyTreeCause`, `ModelTier`, `ProviderError`, `SmokeSignal`, `WorkerResultMetadata`. `Phase` is an 11-value enum (added `Gate`) and `EventKind` a 14-value enum (added `DispatchStart`/`DispatchEnd`/`CostLedger`/`TargetAutoRebased`/`TicketSubsumed` vs the original 9) - declared in [WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14) and [Phase.cs:3](../../src/ThroughlineBuild.Contracts/Models/Phase.cs#L3). See [07-contracts.md](07-contracts.md). |
| `ThroughlineBuild.ModelClient` | Partial | Vendor-neutral model abstraction (`IModelClient`, `ModelRequest`/`ModelResponse`, `ProviderConfig`, `UsageMapper`, AOT JSON context). Still not on the production path - unchanged since the last refresh; only `AnthropicModelClient` implements it and only tests construct it. |
| `ThroughlineBuild.Anthropic` | Partial | Unchanged since last refresh. `AnthropicClient : ILlmClient` is the wired legacy path; its `InvokeStreamAsync` still throws `NotImplementedException` ([AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)), as does `ModelClientLlmAdapter.InvokeStreamAsync` ([ModelClientLlmAdapter.cs:71](../../src/ThroughlineBuild.Anthropic/ModelClientLlmAdapter.cs#L71)). `AnthropicModelClient` has real SSE streaming and no production caller. |
| `ThroughlineBuild.Briefs` | Functional | Builds worker briefs. Per-phase builders now include `BatchImplementBriefBuilder` and `BatchReviewBriefBuilder` (op-31 warm-batch sessions) alongside `Plan`/`Implement`/`Review`/`Draft`/`Decompose`, plus `PreloadedContextBuilder` (preloads repo context into briefs) and a `TemplateLoader`. Templates remain per-agent (`claude-code/`, `codex/`, `copilot/`, `gemini/`) and now add `batch-implement.md`/`batch-review.md` per agent plus a [Templates/shared/](../../src/ThroughlineBuild.Briefs/Templates/shared/) directory of cross-agent fragments (obsolete-path prompts, patch-fetch directives, WORKER_RESULT skeletons). |
| `ThroughlineBuild.Helpers` | Functional | Pure helpers plus I/O-bearing git/worktree helpers. `WavePlanner` deterministically schedules dependency/conflict waves; `WorktreeLeaseManager` owns conductor lease/list/teardown and manifest validation; `TicketIdOrdering` centralizes stable numeric ordering. Existing surfaces include `BaselineCache`, `ChainWorktreeSweeper`, summaries, tree walking, markers, worktree decruft, and `MainWorktreeLock`. `MainWorktreeResolver` was deleted in favor of the CLI's worktree-aware `RepositoryLayout`. `DocOnlyDetector` and `DriftComparator` remain tested but production-unwired. |
| `ThroughlineBuild.Git` | Functional | `ProcessGitClient` (heavily extended this period - worktree/branch/rebase/FF/push surface for the integration-branch chain model). `BaseRefResolver` is accumulation-aware (TLB-411): prefers `origin/<target>` but uses the local target when it is strictly ahead (local-only chain ships). |
| `ThroughlineBuild.EventLog` | Functional | `JsonlEventSink` (now stamped with a `SessionContext` record - project id/name, workspace, build version - [SessionContext.cs:3](../../src/ThroughlineBuild.EventLog/SessionContext.cs#L3)); `RecordingEventSink`; `ReviewFeedbackRetriever`; `SessionFileNameBuilder`; AOT source-gen context. |
| `ThroughlineBuild.Verification` | Functional | Major buildout. `AutomatedChecksRunner` (+ `ExecutableResolver` for cross-platform executable resolution); `PreComputedChecksRunner` (replays gate results into review so each ticket builds once); `GateVacuityProver` (proves each gating check fails on broken input - backs `ChainOutcome.GateVacuous`); `GateControlProber` (re-runs failed checks on the untouched base ref - backs `GateEnvironmentFailure` and ship baseline contradiction re-checks); `SmokeCollector` (collects smoke signals for the review brief); `WorkerAgentReviewer` (the `IVerifier`); `ObsoleteRatifier` with its prompt externalized to [Templates/ratify-obsolete-prompt.md](../../src/ThroughlineBuild.Verification/Templates/ratify-obsolete-prompt.md) via `RatificationPromptLoader`. |
| `ThroughlineBuild.Plane` | Functional | `PlaneTicketingClient` now implements four contracts - `ITicketing, ITicketingProvisioner, ITicketingConnectivity, IProjectDiscovery` ([PlaneTicketingClient.cs:19](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L19)). Keeps the Polly retry pipeline (Retry-After-aware), the `RequestThrottle` (default 40/min, now configurable via `PlaneClientOptions.RequestsPerMinute`, [PlaneClientOptions.cs:20](../../src/ThroughlineBuild.Plane/PlaneClientOptions.cs#L20)), and the per-run issue snapshot cache (`_seqToUuid`/`_issueByUuid`, [PlaneTicketingClient.cs:51-62](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L51-L62)). New: transport-level retry with environmental classification (TLB-545; DNS/connect/TLS/timeout failures retry with `TransportRetryBaseDelay`/`TransportMaxRetryDelay` then surface as `TicketingUnavailableException`), and `ProjectResolver : IProjectResolver` ([ProjectResolver.cs](../../src/ThroughlineBuild.Plane/ProjectResolver.cs)) backing init's pick-or-create project flow. Still the sole ticketing backend - no GitHub or Linear adapter. |
| `ThroughlineBuild.JudgmentSlots` | Functional | One slot: `ReasonTranslator` (via `ILlmClient`), its prompt now externalized to [Templates/translate-reason-prompt.md](../../src/ThroughlineBuild.JudgmentSlots/Templates/translate-reason-prompt.md) loaded by `TranslateReasonPromptLoader`. Used by `CloseCommand`/`DeferCommand`/`ReopenCommand` (with the `EchoLlmClient` fallback wired in the Cli). |
| `ThroughlineBuild.Workers.Common` | Functional | Shared worker code. `WorkerResultParser` (envelope + fenced-block pre-pass, substantially extended); `MarkdownRenderer` (AOT-safe markdown->HTML); new: `CompletionClaimParser` (parses structured completion claims out of worker output), `ProviderErrorClassifier` (classifies quota/rate-limit/auth provider errors - feeds `ReviewUnavailable`), `ProcessStreamEncoding` (UTF-8 process stream discipline), `WorkerDiagnostics`. |
| `ThroughlineBuild.ClaudeCode` | Functional / distribution Partial | Reusable public `ClaudeCodeClient` facade over the Claude worker, with string and `Brief` overloads, preflight, run options, and optional `WORKER_RESULT` contract injection. Package metadata exists, but no CI or release path packs and publishes it. |
| `ThroughlineBuild.Workers.ClaudeCode` | Functional | `ClaudeCodeAgent : IWorkerAgent` selects `ClaudeCodeInteractiveTransport` by product config default, with `ClaudeCodePrintTransport` as rollback. Interactive completion comes from the persisted transcript; the Stop hook is best effort. |
| `ThroughlineBuild.Workers.Codex` | Functional | `CodexAgent : IWorkerAgent` (`Name => "codex"`); runs `codex exec -`. New: `CodexModelProbe` (`codex debug models` probe backing `build init` and `build models refresh`). |
| `ThroughlineBuild.Workers.Gemini` | Functional | `GeminiAgent : IWorkerAgent` (`Name => "gemini"`); JSON DTO parsing, `GeminiProgressDigester`. Minor changes only. |
| `ThroughlineBuild.Workers.Copilot` | Functional | `CopilotAgent : IWorkerAgent` (`Name => "copilot"`); per-tool `--allow-tool` mapping; still no digester (`Digester => null`, [CopilotAgent.cs:18](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L18)). |
| `ThroughlineBuild.Scaffold` | Functional | Parser/validator/`ScaffoldPhase`/`BriefHtmlRenderer`, `ProjectProfile` schema/parser, repository/rules prompt loaders, `OpDocSkeletonGenerator`, embedded authoring docs, and AOT JSON. The worker-spawning `ScaffoldProfileDeriver` was removed; profile generation is now an explicit prompt -> external agent -> deterministic apply protocol. The repository prompt is [Templates/derive-profile-repository-prompt.md](../../src/ThroughlineBuild.Scaffold/Templates/derive-profile-repository-prompt.md) and the stable output rules are [Templates/derive-profile-rules.md](../../src/ThroughlineBuild.Scaffold/Templates/derive-profile-rules.md). |
| `ThroughlineBuild.Phases` | Functional | Phase classes: `PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, plus `GatePhase`, `ReworkPhase`, `DecomposePhase`, `NewPhase`, and `DraftPhase`. Chain orchestration is split across `ChainPhase`, `ParentChainRunner`, `ImplementReviewLoop`, `ChainIntegrationBranch`, the batch runners, and the three required dependency records in `ChainPhaseDependencies`; `ParallelDispatcher`, `TicketGraph` (+`TopologicalSorter`), `AncestorSkipFilter`, and `EarlyExitManifest` cover multi-ticket ordering. All operator text leaves through injected `TextWriter` dependencies or `ChainPhaseOptions.OnStep`; the project contains no direct `Console` access. Hygiene/marker helpers include `WorkingTreeHygieneGate` and `CommentMarkers`. |
| `ThroughlineBuild.Commands` | Functional | `ITicketCommand` implementations and runners plus the generic `ICliVerb`/`CliVerbRegistry` registration contract ([CliVerbRegistry.cs:3](../../src/ThroughlineBuild.Commands/CliVerbRegistry.cs#L3)); embedded config, ticket-body, and user-guide templates are source-generated resources. |
| `ThroughlineBuild.Cli` | Functional | Three-line entry point, `CliApplication` dispatch/composition, TOML/secrets, help, JSON envelopes, and the deterministic command implementations for install/profile/conductor/SOP/worker brief/worktree/gate/waves/candidate/evidence. `RepositoryLayout` is the single worktree-aware resolver for tracked-tree versus main-worktree `.build` data ([RepositoryLayout.cs:21](../../src/ThroughlineBuild.Cli/RepositoryLayout.cs#L21)); `TokenFileInstaller` provides non-interactive secret persistence. `ScaffoldProfileRunner` was deleted. |

### Loose ends (library projects)
- **`ModelClient` is still not on the production path.** `LlmClientFactory.Create` constructs the legacy `AnthropicClient : ILlmClient` and only accepts an `anthropic:` model prefix ([LlmClientFactory.cs:14-28](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L14-L28)). Production LLM access remains single-vendor non-streaming, and is now optional even there (the `EchoLlmClient` fallback). Cross-doc note for [11-llm-architecture.md](11-llm-architecture.md).
- **`DocOnlyDetector` / `DriftComparator`** in Helpers still have no production caller - aspirational gates not wired into any phase.
- **Untracked sibling debris:** `src/ThroughlineBuild.Linear/`, `tests/ThroughlineBuild.Linear.Tests/`, and `tests/ThroughlineBuild.TicketingContract.Tests/` exist on disk as ignored `bin/`/`obj/` remnants of local experiments (the `ticketing-generalization-and-agent-cli-plan.md` op-doc sketches a Linear backend). Nothing is tracked; there is no Linear adapter in the codebase.
- **Worker support is fully multi-vendor** at the agent layer (four `IWorkerAgent` implementations, all built by `WorkerAgentBuilder.Create` from config name and `--agent`).

---

## Tools (`src/tools/`)

Two single-file C# programs, AOT-compiled by `build.sh` into `bin/` (note: `bin/` is git-ignored - the published binaries are local artifacts, not tracked files; intermediate publish output lands under `src/tools/artifacts/`).

### `analyze-event-log` - Functional
[src/tools/analyze-event-log.cs](../../src/tools/analyze-event-log.cs). Reads one or more `.build/events/*.jsonl` files (or dirs/globs) and prints per-phase token totals, estimated USD cost, wall time, and a chain summary. Updated this period: the `pricing` table ([analyze-event-log.cs:36-51](../../src/tools/analyze-event-log.cs#L36-L51)) now carries `claude-fable-5`, the repriced `claude-opus-4-5`..`4-8` rows ahead of the generic `claude-opus-4` row, and `gpt-5.4/5.5` rows; per TLB-547 the analyzer collects **every** `ChainEnd` event rather than only the last ([analyze-event-log.cs:166-169](../../src/tools/analyze-event-log.cs#L166-L169)) and reports duration across all chains, and the pricing table is now authoritative over worker-supplied `cost_usd` for recognized models (worker CLIs ship stale per-model pricing; a large mismatch prints a warning - [analyze-event-log.cs:215-233](../../src/tools/analyze-event-log.cs#L215-L233)).

### `token-audit` - Functional
[src/tools/token-audit.cs](../../src/tools/token-audit.cs). Extracts Claude Code session metadata from JSONL under `~/.claude/projects/{encoded-repo-path}/`. Subcommands `latest`, `extract <file|dir|glob>...`, or no-arg combined. Unchanged since the last refresh.

### Loose ends (tools)
- The tools have no test coverage (single-file programs outside the solution's test projects); the analyzer's pricing table is hand-maintained and silently goes stale when new model ids ship.

---

## Scripts, runner, and CI

### `build.sh` - Functional
[build.sh](../../build.sh). Publishes three AOT binaries for one RID (default derived from `uname`, override via `$RID`), atomically installs them to `bin/` and `${INSTALL_DIR:-$HOME/.local/bin}`, and warns when the install directory is not on `PATH` ([build.sh:41](../../build.sh#L41), [build.sh:58](../../build.sh#L58), [build.sh:66](../../build.sh#L66)). It snapshots every tracked `packages.lock.json` before RID-sensitive publish restores and restores those bytes on exit so a local install cannot dirty dependency locks ([build.sh:6](../../build.sh#L6)).

### Editor runner configuration - Partial
[.vscode/tasks.json](../../.vscode/tasks.json) provides VS Code build tasks. No launch configuration is tracked at HEAD; developers may keep one locally. The repository's editor-independent build contract is `build.sh` plus the commands documented above.

### `.github/workflows/build.yml` - Functional
[.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow, matrix `{osx-arm64, win-x64, linux-x64}`. On push/PR to `main`: setup .NET from `global.json` with a lockfile cache, `dotnet restore --locked-mode`, full tests, `dotnet format --verify-no-changes`, native publish, then `tools/publication_audit.py` before artifact upload ([build.yml:26](../../.github/workflows/build.yml#L26)). No release-tagging or deploy.

### `.gitattributes` - Functional
[.gitattributes](../../.gitattributes). Pins LF endings for brief templates and snapshot test data; now also covers the `JudgmentSlots`, `Scaffold`, and `Verification` template directories added this period.

### Loose ends (scripts/CI)
- CI publishes only the CLI binary, not the two tools `build.sh` builds; the tools are local-build-only.

---

## Test projects (`tests/`)

Twenty tracked xUnit projects mirror the solution's source projects. At this refresh the suites contain 2,852 `[Fact]`/`[Theory]` declarations across 266 tracked C# files, all `net10.0` with nullable enabled. The public Claude facade has its own `ThroughlineBuild.ClaudeCode.Tests` project. Every source and test project has a tracked `packages.lock.json`, and CI restores them in locked mode; the project-less `src/tools/packages.lock.json` is now ignored because file-based app restore rewrites it by RID and SDK feature band ([.gitignore:32-35](../../.gitignore#L32-L35)).

Shared doubles cover ticketing, workers, sinks, console, git, and LLM clients. Snapshot infra lives in the Briefs test project. AOT regression coverage remains concentrated in Workers.ClaudeCode/Workers.Common around `WorkerResultParser` (tests set the `System.Text.Json` reflection-off switch). See [11-llm-architecture.md](11-llm-architecture.md).

### Loose ends (tests)
- `ModelClient.Tests` and the `Anthropic.Tests` streaming cases still exercise the unwired `IModelClient` path; passing tests there do not imply the path is reachable from `build`.
- `tests/ThroughlineBuild.Linear.Tests/` and `tests/ThroughlineBuild.TicketingContract.Tests/` are untracked ignored debris (see library Loose ends), not test projects.

---

## op-docs (`docs/op-docs/`)

Two files at top level: `op-doc-spec.md`, the authoring guide for the format `build scaffold`
parses (also emittable via `build op-doc spec`), and [examples/](../../docs/op-docs/examples/).

[examples/](../../docs/op-docs/examples/) holds five real op-docs from this repository's own
development, kept as format specimens covering the range of shapes - op-26 (smallest complete
op-doc), op-24 (single plan, sequential briefs), op-31 (three plans, A -> B -> C chain), op-30
(design-heavy, with a "Deliberately not in this operation" section), and op-27, which doubles
as the live spec for the fenced-block payload protocol and is linked from
[06-public-surfaces.md](06-public-surfaces.md) and [07-contracts.md](07-contracts.md). They are
point-in-time plans, not contracts; see the folder's own README.

The other 36 completed op-docs (op-01 through op-34) were removed on 2026-07-26 ahead of the
repository going public. They were pre-implementation plans that read as documentation while
contradicting the shipped code in places - .NET 8 in op-01/02/08 against a `net10.0` repo,
op-23/op-25's parallel multi-ticket dispatch that op-29 later removed on purpose, and two
config-schema hard-breaks. The decisions in them not visible in the code were extracted into
[docs/history.md](../history.md) sections 4 and 5 instead. An archive zip sits outside the
repository.

Unstarted op-doc-shaped proposals live in [docs/research/](../research/), not here:
`op-32-batch-cohesion-detection.md` and `op-35-monorepo-multi-stack.md` alongside the
feasibility studies (Linear backend, partial test selection, plan-phase repo index, RTK).

---

## `.claude/` (agent settings only)

The repository tracks [.claude/settings.json.example](../../.claude/settings.json.example) as a safe starting point. SOP installation can also materialize catalog-owned Claude command stubs and Codex skill stubs at the paths declared by the embedded `SopBundleCatalog`; these are repository artifacts whose bytes are checked by `build sop doctor`, not live Plane credentials. The tracked [cross-impact Claude stub](../../.claude/commands/cross-impact.md) and [Codex skill](../../.agents/skills/cross-impact/SKILL.md) are thin launchers that require `build sop brief cross-impact --json` to succeed and explicitly forbid cached-prose fallback. Repository facts live in tracked `.build/config.toml` and `.build/conductor.toml`; only `.build/sop-manifest.json` remains local SOP cache state.

---

## Loose ends (cross-cutting)

- **Help-surface drift remains:** three help surfaces exist (tier-0/tier-1 registry, help topics, legacy `CliUsage`), and they disagree - `sweep`/`models` are the two missing tier-1 entries among 38 action verbs and 36 help entries. The code registry and command implementations win.
- **Multi-vendor LLM is real for workers, aspirational for model clients.** Four worker agents ship and are wired; the `IModelClient` layer is implemented but unreachable from `build`. The only production `ILlmClient` use (reason translation in close/defer/reopen) is now optional via `EchoLlmClient` fallback.
- **`DeferCommand` v1.1 TODO** (rebuild rollup preview for the parent) is still unimplemented.
- **Enum counts changed since the sibling docs were written:** `ChainOutcome` has 18 values (added `RefusedDirtyTree`, `RefusedWrongBranch`, `BatchImplemented`, `DryRunPreview`, `GateVacuous`, `ReviewUnavailable`, `GateEnvironmentFailure`, `TicketingUnavailable`), `EventKind` has 14, `Phase` has 11 (added `Gate`), and chain exit codes run 0-11. Sibling docs citing older counts (07-contracts, 10-lifecycle-orchestration) should be checked against these.
- **Documentation lag to re-check:** this state-of-the-system set is maintained as the as-built reference, but source/generated help remain authoritative for the newer conductor, SOP, tracked-config, and staged-install surfaces.
