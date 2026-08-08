# 05 - State and Persistence

Last refreshed: 2026-08-08 (HEAD 5961f807)

Everything `build` writes over the lifetime of a session: filesystem state, logs, scratch, Plane records, git refs. Where each lives and whether it is cleaned up.

For configuration file ownership and mutation see [04-configuration.md](04-configuration.md). For orchestration / lifecycle see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Local on-disk state (per repo)

### `.build/` (project-local runtime root)

`.build` now mixes tracked repository facts with ignored machine/session state. `.build/config.toml` is deliberately not ignored and is meant to travel with the repository; `.build/conductor.toml`, `.build/sop-manifest.json`, generated handoff files, events, sessions, and transient briefs are ignored ([.gitignore:1](../../.gitignore#L1)).

| Path | Written by | Lifetime | Cleanup |
|---|---|---|---|
| `.build/config.toml` | `build init`; `models refresh`; `settarget`; `profile apply` / `install --profile`; `setup --write-token-file` | persistent, tracked repository fact | normal git history; do not treat as disposable local state |
| `.build/conductor.toml` | SOP install scaffold; install profile identity derivation; `conductor apply` invariant-block replacement | persistent, ignored | manual reset or SOP lifecycle; never silently overwritten wholesale |
| `.build/sop-manifest.json` | SOP install/upgrade/uninstall | persistent ignored cache | deleted when last manifest entry is removed; safe to rebuild from catalog/status |
| `.build/profile.json`, `.build/invariants.toml` | external agent/operator handoff convention, not automatically written by `build` | optional ignored handoff artifacts | manual |
| `.build/events/<stem>.jsonl` | `JsonlEventSink` | persistent (one file per session) | never auto-deleted |
| `.build/sessions/<stem>/` | `--debug` only | persistent | never auto-deleted |
| `.build/brief.md` | every claude-code worker dispatch (`ClaudeCodeAgent`) | overwritten each dispatch | never auto-deleted (gitignored) |

Status: Functional.

**`build init` writes `.build/config.toml`.** `InitCommand` (now 741 lines) has grown well past the old template-substitution shape: it supports an offline mode (template + flag substitution), a connected mode that probes Plane and resolves or creates the project interactively, credentials intake from a file or redirected stdin via `CredsFileParser.Parse` ([src/ThroughlineBuild.Cli/InitCommand.cs:105-111](../../src/ThroughlineBuild.Cli/InitCommand.cs#L105-L111)), interactive prompts via `PromptForConnectionValues` gated off by `--no-interactive` ([src/ThroughlineBuild.Cli/InitCommand.cs:115-120](../../src/ThroughlineBuild.Cli/InitCommand.cs#L115-L120)), and a Codex probe that enriches the commented `[workers.codex.sizes]` block ([src/ThroughlineBuild.Cli/InitCommand.cs:47](../../src/ThroughlineBuild.Cli/InitCommand.cs#L47)). The actual write is `WriteOfflineConfig`, which creates `.build/` and writes UTF-8 without BOM ([src/ThroughlineBuild.Cli/InitCommand.cs:190-197](../../src/ThroughlineBuild.Cli/InitCommand.cs#L190-L197)). It refuses to overwrite without `--force` ([src/ThroughlineBuild.Cli/InitCommand.cs:136](../../src/ThroughlineBuild.Cli/InitCommand.cs#L136)); `--print-template` stays offline and never probes ([src/ThroughlineBuild.Cli/InitCommand.cs:124](../../src/ThroughlineBuild.Cli/InitCommand.cs#L124)). The verb dispatches before config load in `Program` ([src/ThroughlineBuild.Cli/CliApplication.cs:231](../../src/ThroughlineBuild.Cli/CliApplication.cs#L231)) since it bootstraps the config every other verb needs.

**Other verbs rewrite `.build/config.toml` in place.** `models refresh` owns only `[workers.codex.sizes]`; `settarget` owns one `[work]` key; `profile apply` and `install --profile` use `ConfigProfileWriter` for project/check blocks after canary proof; `setup --write-token-file` writes a token file and only its path into `[ticketing]` ([TokenFileInstaller.cs:14](../../src/ThroughlineBuild.Cli/TokenFileInstaller.cs#L14)).

**SOP and conductor persistence.** `SopInstaller.Run` resolves every catalog target below the repository root, refuses symlink/reparse traversal, hash-gates upgrades/uninstalls, scaffolds conductor state without clobbering it, and writes the source-generated manifest cache ([SopInstallCommand.cs:525](../../src/ThroughlineBuild.Cli/SopInstallCommand.cs#L525), [SopInstallCommand.cs:1472](../../src/ThroughlineBuild.Cli/SopInstallCommand.cs#L1472)). `ConductorCommand` applies invariant edits through a temp sibling plus atomic move, preserving all bytes outside the invariant block ([ConductorCommand.cs:359](../../src/ThroughlineBuild.Cli/ConductorCommand.cs#L359)).

**`.build/brief.md`.** Each claude-code worker dispatch writes the full brief instruction here before spawning the subprocess ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:24-27](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L24-L27)) "for diagnostics". It is overwritten on every dispatch and gitignored; the codex / gemini / copilot agents do not write it.

**Event log file naming.** `SessionFileNameBuilder.Build` ([src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs:20](../../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs#L20)) produces `{project}-{ticket_or_slug}-{verb}-{yyyy-MM-dd-HHmmss}` (no extension); `.jsonl` is appended by `JsonlEventSink.EnsureOpened`, which opens the stream in `FileMode.Append` ([src/ThroughlineBuild.EventLog/JsonlEventSink.cs:28-41](../../src/ThroughlineBuild.EventLog/JsonlEventSink.cs#L28-L41)). When `FileNameStem` is unset the sink falls back to the raw `SessionId` GUID, but every CLI verb sets the stem.

**Event log schema.** The wire DTO is `EventLineDto` ([src/ThroughlineBuild.EventLog/EventLineDto.cs:12-36](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12-L36)) - six original PascalCase fields plus four snake_case session-context fields. The `EventKind` enum has grown to 14 kinds ([src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14)); the newest is `CostLedger` (see telemetry below). The schema is documented in [docs/build-event-log-format.md](../build-event-log-format.md).

**Cost / telemetry rows in the event log (NEW, TLB-510 / exp-4).** Two `EventKind.CostLedger` payload kinds are emitted by `ImplementPhase` into the regular JSONL event log (no separate ledger file):

- `kind = "context_attribution"` - per-worker-session context telemetry parsed from the claude-code per-turn usage stream (turn count, cache-read series, slope ratio, per-tool-class byte buckets such as `read_bytes` / `bash_bytes`), emitted after the worker completes when `context_turns` metadata is present ([src/ThroughlineBuild.Phases/ImplementPhase.cs:379-401](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L379-L401)).
- `kind = "preload_summary"` - per-ticket preload telemetry (files requested / loaded / truncated, bytes) emitted by `BuildAndReportPreloadAsync` after worktree materialization ([src/ThroughlineBuild.Phases/ImplementPhase.cs:652-664](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L652-L664)). Two advisory `GateFailure` kinds accompany it: `preload_file_not_found` (a declared Preload path absent after materialization, :672) and `preload_empty` (declared paths but zero loaded, :682).

The `VerifierVerdict` event now also persists **failed-check evidence**: a `checks_failed_details` array (name, role, exit code, command, stdout/stderr tails capped at 2000 chars) so resumed rework briefs carry the check's own output, not a paraphrase ([src/ThroughlineBuild.Phases/ReviewPhase.cs:444-476](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L444-L476), TLB-509 / 7af36fb). `ReviewFeedbackRetriever` reads it back when a chain resumes an `InProgress` ticket.

**`--debug` capture.** A worker-spawning verb computes `.build/sessions/<stem>/` and creates it eagerly. The Claude print transport writes its base files through `ClaudeCodeAgent.WriteDebugCapture` ([ClaudeCodeAgent.cs:486](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L486)):

- `worker-stdin.txt` / `worker-stdout.txt` / `worker-stderr.txt` - brief and raw streams (UTF-8, no BOM)
- `envelope-result.txt` - inner `result` field from the type=result envelope (when present)
- `worker-result.json` - parsed `WorkerResult` (core fields only; metadata excluded for AOT-safe serialization)
- `parse-error.txt` - failure reason when envelope absent / parse failed
- `cancel-reason.txt` - present on timeout / Ctrl-C (`ClaudeCodeAgent.WriteCancellationCapture`, [ClaudeCodeAgent.cs:537](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L537))
- `transcript.jsonl` (NEW, d651481) - structured per-turn transcript written by `WorkerTranscriptWriter` (`FileName` constant at [src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs:37](../../src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs#L37)): one `meta` record (build version, session id, rework round via `DebugTranscriptContext`), one `turn` record per assistant message (usage, latency, tool calls, a discovery/production/verification/respond/reason classification), `tool_result` records (bytes, error flag), and a terminal `result` record (synthesized if the worker died mid-session). Written on both the success path ([ClaudeCodeAgent.cs:185-190](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L185-L190)) and the cancellation path (:155-158). Best-effort; pure observation of the captured stream.
- `rework-round.json` (NEW) - the rework-round side channel written by `ReworkRoundManifest.Write` from `ImplementReviewLoop` when a rework round re-dispatches the implementer ([src/ThroughlineBuild.Phases/ImplementReviewLoop.cs:146](../../src/ThroughlineBuild.Phases/ImplementReviewLoop.cs#L146)): round number, trigger (`gate` or `review`), rationale, failed check names, before/after SHAs, and per-check gate evidence ([src/ThroughlineBuild.Phases/ReworkRoundManifest.cs:21](../../src/ThroughlineBuild.Phases/ReworkRoundManifest.cs#L21)). No-op when `--debug` is off.

The Claude print transport retains the stream-oriented files above. The interactive transport instead retains correlated evidence under `<capture>/claude-interactive-runs/<run-id>/`, including `worker-stdin.txt`, `hook-completion.json`, `assistant-transcript.txt`, `provider-transcript.jsonl`, `process-host.txt`, and `worker-result.json`. Non-debug interactive run directories live under the OS temp root and are deleted on normal completion. Codex, Gemini, and Copilot retain their own stream capture sets.

`phase-status.json` is written by `EarlyExitManifest.Write` when a phase exits before the worker spawns (e.g. parent-ticket refusal, wrong state, hygiene gate; call sites at [src/ThroughlineBuild.Phases/ImplementPhase.cs:113-139](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L113-L139)).

### `.worktrees/` (git worktrees)

Status: Functional. The chain worktree model changed substantially since the last refresh: the placeholder-branch model is gone, replaced by an **integration branch** that accumulates commits and a success-time **sweep**.

Gitignored at [.gitignore:1](../../.gitignore#L1).

**Conductor leases.** `[worktree].root` defaults to `.worktrees/conductor`. `WorktreeLeaseManager.LeaseAsync` creates a contained helper branch/path, optionally copies one allowlisted seed, runs the configured install command with stdin closed, and writes `.build-worktree-lease.json` inside the leased worktree; the schema records ticket, branch/base, roots, seeded files, leased resources, and install result ([WorktreeLease.cs:7](../../src/ThroughlineBuild.Helpers/WorktreeLease.cs#L7), [WorktreeLeaseManager.cs:32](../../src/ThroughlineBuild.Helpers/WorktreeLeaseManager.cs#L32)). List is read-only. Teardown removes the manifest-backed worktree and branch only after cleanliness, identity, containment, and optional merged-into gates; partial git/filesystem failure can leave a manifest or directory for inspection and retry.

**Standalone (non-chain) ticket worktrees.** Created by `ImplementPhase` (initial round only; rework reuses the existing one) via `IGitClient.CreateWorktreeAsync` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:297-306](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L297-L306)). Branch `ticket/<id>` and path `.worktrees/ticket-<id>` come from `PhaseWorktreeLayout.Compute` (id only, no title slug, for Windows MAX_PATH; [src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:7-16](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L7-L16)); the legacy `ticket/<id>-<slug>` form is still recognized by `IsTicketBranch` / `MentionsBranch` (:29-47). Removed by `WorktreeDecrufter.DecruftAsync` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:55](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L55)) - called from `ShipPhase` after a successful merge ([src/ThroughlineBuild.Phases/ShipPhase.cs:809](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L809)) and from `CloseCommand` / `DeferCommand` if the worktree exists ([src/ThroughlineBuild.Commands/CloseCommand.cs:111](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L111)).

**Chain integration worktree (CHANGED).** A parent chain creates ONE shared worktree at `.worktrees/ticket-<parent-id>` checked out on the integration branch `chain/<parent-id>` (`ChainIntegrationBranch.BranchName`, [src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:34](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L34); creation via `EnsureIntegrationWorktreeAsync` at :246, invoked by [ParentChainRunner.cs:111](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L111)). Unlike the old placeholder branch, `chain/<id>` **receives commits**: each child runs its full chain with `ChainTargetBranch` set to the integration branch ([ParentChainRunner.cs:445](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L445)), so leaf ships fast-forward the integration branch, and the outermost chain finally lands the accumulated branch onto the configured target via `ChainIntegrationBranch.LandRootIntegrationBranchAsync` ([ChainIntegrationBranch.cs:111](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L111)).

- **Reuse + refresh (TLB-546).** The integration branch and worktree are *retained across runs* so a failed chain can resume. Before dispatching children, `ParentChainRunner` invokes `ChainIntegrationBranch.RefreshIntegrationBranchAsync` to rebase a reused branch onto the current base ref; a conflicted refresh aborts the rebase and stops the chain with `ParentStoppedEarly` before work is burned ([ParentChainRunner.cs:120](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L120); [ChainIntegrationBranch.cs:282](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L282)).
- **No fallback.** If the integration worktree cannot be created, `ParentChainRunner` stops with `ParentStoppedEarly` and emits `integration_worktree_unavailable` ([src/ThroughlineBuild.Phases/ParentChainRunner.cs:139](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L139)). The old `shared_worktree_unavailable` per-ticket-fallback path no longer exists.

**Sweep on success, preserve on failure (1972324).** At the end of a *successful* outermost chain, both leaf orchestration ([ChainPhase.cs:511](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L511)) and parent orchestration ([ParentChainRunner.cs:570](../../src/ThroughlineBuild.Phases/ParentChainRunner.cs#L570)) invoke `ChainIntegrationBranch.SweepChainWorktreesAsync`. It decrufts every worktree whose branch is in the `ticket/` or `chain/` namespace, best-effort; cleanup never fails a successful chain, and halts emit `worktree_sweep_incomplete` ([ChainIntegrationBranch.cs:364](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L364)). A failed or interrupted chain deliberately leaves all worktrees and branches in place for resume/inspection.

**`build sweep` recovery verb (NEW, TLB-531).** `build sweep [--target BRANCH] [--force]` dispatches to `ChainWorktreeSweeper.SweepAsync` ([src/ThroughlineBuild.Cli/CliApplication.cs:480-490](../../src/ThroughlineBuild.Cli/CliApplication.cs#L480-L490)). The sweeper removes leftover worktrees and branches in the `ticket/` / `chain/` namespaces only (`IsChainBranch`, [src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs:35-38](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs#L35-L38)). Branch deletion is **always merged-gated**: a branch is deleted only when `IsAncestorAsync(branch, target)` confirms it is fully merged into the target ([ChainWorktreeSweeper.cs:47-113](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs#L47-L113), gates at :68 and :104). Without `--force`, unmerged branches keep both their branch and worktree; `--force` additionally removes the *worktree* of an unmerged branch but still never deletes the branch, so committed work is never lost. Orphan branches with no worktree are deleted only when merged.

**Baseline worktree (transient).** Ship's baseline-aware regression check creates a short-lived detached worktree at the onto-ref under `.worktrees/baseline-<sha[..8]>` via `ComputeBaselineAsync` ([src/ThroughlineBuild.Phases/ShipPhase.cs:916-963](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L916-L963); path built at :927, invoked from :506), runs the regression checks there to learn the already-failing set, caches it in `ShipOptions.BaselineCache` keyed by onto-SHA (:923-925), then decrufts best-effort (:955). `--skip-baseline` skips this entirely.

**`WorktreeDecrufter`** still runs the 7-step decruft ladder (`DecruftStep` enum at [src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:6-16](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L6-L16)): kill `.preview.pid` PIDs, delete `.preview.pid`/`.preview.meta`, pre-clean Windows reparse points under `node_modules`, `git worktree remove`, `remove --force`, `Directory.Delete`, `git worktree prune`. The preview files belong to the older claude-config preview flow; `build` never writes them.

**No `git stash` scratch state.** Worker briefs forbid the stash stack (implement brief: "Do NOT use git stash ... repo-global and leaks across worktrees", [src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md:31](../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L31)); the read-only review verifier is additionally barred from `git checkout` / `git reset` / `git rebase` ([src/ThroughlineBuild.Briefs/Templates/claude-code/review.md:20](../../src/ThroughlineBuild.Briefs/Templates/claude-code/review.md#L20)). `build` itself never writes to the stash stack; `WorkingTreeHygieneGate` backstops with unrelated-stash detection.

### `MainWorktreeLock` (in-process serialization of main-worktree git ops)

Status: Functional.

`MainWorktreeLock.WithLockAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:11-28](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L11-L28)) is a process-local semaphore map keyed by the full main-worktree path (lowercased on Windows). Not an on-disk lock; no cross-process protection. `ShipPhase` wraps the fetch ([src/ThroughlineBuild.Phases/ShipPhase.cs:304](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L304)), the target-branch auto-rebase (:367), and the fast-forward merge (:748) in it.

### `bin/`, `src/tools/artifacts/`, per-project `bin/`/`obj/`, `.tmp/`, `.scratch/`, `secrets/`

- `bin/` (gitignored, [.gitignore:3](../../.gitignore#L3)): `build.sh` copies the published `build`, `token-audit`, `analyze-event-log` binaries here ([build.sh:20-31](../../build.sh#L20-L31)). Replaced wholesale per rebuild.
- `src/tools/artifacts/` (gitignored, [.gitignore:16](../../.gitignore#L16)): `dotnet publish src/tools/<tool>.cs` output staging for the two tools ([build.sh:24-30](../../build.sh#L24-L30)). Safe to delete.
- Per-project `bin/`/`obj/`: standard `dotnet` outputs.
- `.tmp/`, `.scratch/`, `secrets/`: gitignored, reserved by convention, not written by `build`. The draft-mode `new` flow and `--review` editor loop use the OS temp dir, not `.tmp/`.
- **`Directory.Build.props` is now gitignored** ([.gitignore:17-19](../../.gitignore#L17-L19)) - the machine-specific native-AOT linker overrides are kept local and never committed (see 08).

### State outside the repository

`build.sh` installs all three executables into `INSTALL_DIR` (default `$HOME/.local/bin`) through `install_atomic` ([build.sh:23](../../build.sh#L23), [build.sh:33](../../build.sh#L33)). The interactive Claude transport also pre-seeds workspace trust in the Claude user configuration, reads persisted Claude transcripts, and coordinates with per-worktree locks plus run directories under the OS temporary root. `ClaudeCodeInteractiveTransport.ExecuteAsync` owns this lifecycle ([ClaudeCodeInteractiveTransport.cs:97](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeInteractiveTransport.cs#L97)). Status: Functional; cleanup is automatic for normal non-debug runs and best effort after crashes.

### `build setup` writes (NEW)

`build setup` provisions a fresh repo: `git init` when needed, then reconciles the managed `.gitignore` block without adding `.build/config.toml` (that file is tracked), and on a commit-less repo makes the welcome commit so ship has a base. `--check` mutates nothing. Setup also provisions Plane states/labels; optional `--write-token-file` writes the resolved token plus config indirection after setup succeeds.

### Loose ends

- **`.build/brief.md` and `transcript.jsonl` are claude-code-only.** Other workers leave no brief diagnostic or per-turn transcript.
- **Only selected `.build` state is ignored.** Config is tracked; conductor, manifest, prompts, logs, and sessions remain local. A clone can receive repository policy but still needs a resolvable Plane token and local conductor state.
- **`MainWorktreeLock` is in-process only** - two separate `build` processes are not serialized.
- **`.build/sessions/` and `.build/events/` never auto-rotate** (see Cleanup posture).
- **`rework-round.json` is overwritten per round** within the same capture dir; only the last round's manifest survives a multi-round session.

---

## Plane records (remote state)

Status: Functional. Every phase that writes to Plane does so via [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Plane records are durable and never reset by `build`. Writes are not transactional - a phase that fails partway can leave a comment without a state transition (or vice versa). The event log records each write so the operator can reconstruct what landed.

| Phase | Writes |
|---|---|
| `plan` (promote mode, the `build chain` default via `PlanConfig.Default = "promote"`, or explicit `--from-brief`) | no worker: labels (risk + size), one `[planned_at: <base-sha>]` comment, state `Backlog -> Planning -> Ready` (`RunPromoteAsync`, [src/ThroughlineBuild.Phases/PlanPhase.cs:224-250](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L224-L250)) |
| `plan` (`mode = "investigate"`) | description append (plan HTML), labels, `[planned_at: <sha>]` comment, transitions; the `Planning` transition now lands *after* the worker runs ([src/ThroughlineBuild.Phases/PlanPhase.cs:124](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L124)) |
| `implement` | one `[implemented_at: <sha>] (branch ...)` comment ([src/ThroughlineBuild.Phases/ImplementPhase.cs:535](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L535)), state `Ready -> InProgress` (initial, :312-315) and `InProgress -> InReview` (:542-543) |
| `gate` (standalone or chain-composed) | no comment; on a chain gating hard-fail that feeds rework, state `InReview -> InProgress`; environment-failure and vacuity hard-fails leave the state untouched (see 09). Standalone `build gate` emits its result without starting a worker. |
| `review` | one verdict comment (Pass / Rework / Fail), state `InReview -> InProgress` only on Rework; **no comment and no transition** when the provider was unavailable (TLB-527) |
| `ship` | one `[shipped_at: <sha>]` comment ([src/ThroughlineBuild.Phases/ShipPhase.cs:782-785](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L782-L785)), state `InReview -> Done` (:792); or a `ship_blocked:` comment with no state change. Parent ship posts `shipped: all N children are Done` and transitions only the parent (:880-895) |
| `chain` (batch-implement path) | per confirmed batch ticket: `[implemented_at]` markers + transitions, exactly like per-ticket implement |
| `rework` / `new` / `decompose` / `scaffold` / `amend` / `close` / `defer` / `reopen` | unchanged shapes: see `DecomposePhase` `[decomposed_at]` comment ([src/ThroughlineBuild.Phases/DecomposePhase.cs:143-147](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L143-L147)), `AmendCommand` append/rewrite ([src/ThroughlineBuild.Commands/AmendCommand.cs:66](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L66), :101), close/defer cascade + decruft, reopen comment |
| `setup` (NEW) | creates any missing Plane states/labels required by `WorkspaceSchema` (`ITicketingProvisioner`); `--check` reports gaps without mutating |
| `relate` / structured `new` | creates or removes issue-relation edges; structured `new` may also set a parent and multiple typed relations after the issue itself is created |
| `evidence add` | posts exactly one structured claim/review/commit/integrate/gate/final comment and immediately reads the returned id back; never transitions ticket state ([EvidenceCommand.ExecuteAsync:49](../../src/ThroughlineBuild.Cli/EvidenceCommand.cs#L49)) |

`TicketingWritePolicy.BestEffortAsync` ([TicketingWritePolicy.cs:15](../../src/ThroughlineBuild.Phases/TicketingWritePolicy.cs#L15)) distinguishes informational writes from state-bearing writes. Informational labels/comments in plan, gate, review, ship, and chain warn and continue during a Plane outage; lifecycle transitions, resume markers, and shipped markers remain hard requirements. A best-effort failure emits a local `ticketing_write_failed` event when possible.

`AppendDescriptionAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:753](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L753)) does a read-modify-write append. Plane comments are HTML; `[name: value]` markers are parsed back by `MarkerParser` ([src/ThroughlineBuild.Helpers/MarkerParser.cs:5-42](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L5-L42)). `CommentMarkers.LatestValue` selects the freshest marker by comment `CreatedAt`, not list order ([src/ThroughlineBuild.Phases/CommentMarkers.cs:19-37](../../src/ThroughlineBuild.Phases/CommentMarkers.cs#L19-L37), TLB-412), so chain re-runs do not read stale prior-run SHAs.

### Loose ends

- **No transactional Plane writes.** Unchanged.
- **Partial decompose** still stamps `[decomposed_at]` when only some children were created.
- **Promote-mode plan posts `planned_at` = the base SHA**, not a worker-computed SHA; downstream drift checks treat it identically, but the marker no longer proves a plan was investigated.

---

## Git refs and branches

Status: Functional.

| Ref | Created by | Removed by |
|---|---|---|
| Local branch `ticket/<id>` (legacy `ticket/<id>-<slug>` recognized) | `ImplementPhase` standalone (`git worktree add -b`) or `CreateBranchAsync` inside the chain integration worktree | `ShipPhase` if `ship.delete_feature_branch = true` (default); otherwise `build sweep` (merged-gated) |
| Integration branch `chain/<id>` (accumulates child ships; retained across runs) | `ChainIntegrationBranch.EnsureIntegrationWorktreeAsync`, orchestrated by `ParentChainRunner`; refreshed by rebase onto the base ref on reuse (TLB-546) | `build sweep`, merged-gated; the chain success sweep removes only its worktree |
| Worktree `.worktrees/ticket-<id>` | `ImplementPhase` (standalone) or `ParentChainRunner` through `ChainIntegrationBranch` (integration worktree, once per parent) | `WorktreeDecrufter` from `ShipPhase` / `CloseCommand` / `DeferCommand`; chain success sweep; `build sweep` |
| Detached baseline worktree `.worktrees/baseline-<sha[..8]>` | `ShipPhase.ComputeBaselineAsync` | same method, best-effort decruft after checks run |
| Target-branch advancement (FF only) + push | `ShipPhase` Step 8/8a ([src/ThroughlineBuild.Phases/ShipPhase.cs:744](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L744)); chain root landing `ChainIntegrationBranch.LandRootIntegrationBranchAsync` ([src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:111](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L111)) | n/a |
| Target-branch auto-rebase onto `<remote>/<target>` | `ShipPhase` on `DivergedNoConflict`, emits `TargetAutoRebased` ([src/ThroughlineBuild.Phases/ShipPhase.cs:357-390](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L357-L390)) | n/a |

**`build` pushes the target branch.** Ship resolves the merge target, fast-forwards it in the main worktree, and pushes when a remote is configured and push is enabled (`useRemote = remoteConfigured && !NoPush`, [src/ThroughlineBuild.Phases/ShipPhase.cs:285](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L285); push at :766). The chain root landing mirrors this with its own no-remote guard: a missing remote is a clean local land, emitting a `chain_landing_push_skipped` event with reason `no_remote` rather than failing ([src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs:139](../../src/ThroughlineBuild.Phases/ChainIntegrationBranch.cs#L139)). No force operations exist anywhere (`git push --force`, `git reset --hard`, `git rebase -i`); failed rebases are aborted.

### Loose ends

- **Architecture doc still says local-merge-only.** [docs/throughline-build-architecture.md](../throughline-build-architecture.md) Section 5.9's "no `git push origin main`" claim remains stale; ship and the chain landing both push.
- **`chain/<id>` branches survive chain runs by design.** A successful run removes the integration worktree but retains the branch; `build sweep` is the merged-gated branch-deletion path. Operators who never run it accumulate merged-but-undeleted branches.

---

## In-process state (per invocation, discarded on exit)

| Held in | Lifetime | Why |
|---|---|---|
| `PlaneTicketingClient._statesByName` / `_labelsByName` / `_issueTypesByName` | one process invocation | name->uuid caches, lazy-loaded under locks ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:28-36](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L28-L36)) |
| `PlaneTicketingClient._seqToUuid` + `_issueByUuid` (TLB-366) | one process invocation | per-run issue snapshot: the project is paginated once, lookups answer from memory, PATCHes write through ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:48-59](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L48-L59)) |
| `ShipOptions.BaselineCache` | one process invocation | onto-SHA -> failing-check-set cache so repeated ships in one chain reuse the baseline worktree result; ship also *corrects* a contradicted baseline entry in place ([src/ThroughlineBuild.Phases/ShipPhase.cs:590](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L590)) |
| `GateVacuityProver` proven-set | one process invocation | per-run per-check-once canary probes (see 09) |
| `RecordingEventSink._events` | one process invocation | in-memory mirror for the phase summary renderer |
| `TemplateLoader._cache` / `ConfigTemplateLoader._cached` | static (process lifetime) | embedded template caches |
| `MainWorktreeLock.Locks` | static (process lifetime) | semaphore-per-main-worktree map |

There is **no** cross-invocation persistent state in process; the binary is a one-shot CLI.

### Loose ends

- The snapshot cache only sees this process's own writes; a concurrent second `build` mutating the same project is invisible until the next run reloads.

---

## State that survives the binary but predates it

- **`~/.claude/projects/<encoded-path>/*.jsonl`** - Claude Code session logs, written by the `claude` subprocess; `token-audit` reads them.
- **Plane database** - durable, never reset by `build`.

### Loose ends

- None new; this surface is unchanged.

---

## Cleanup posture

| Class of artifact | Auto-cleanup? |
|---|---|
| `.build/events/*.jsonl` | No. Accumulate forever; `analyze-event-log` can aggregate a directory in one pass. |
| `.build/sessions/<stem>/` | No. Operator must prune. |
| `.worktrees/ticket-<id>/` | Yes - `WorktreeDecrufter` on successful `ship` (and `close`/`defer`); chain success sweep removes all `ticket/`/`chain/` worktrees. Failed ship/chain deliberately leaves them. `build sweep` is the recovery path. |
| `.worktrees/baseline-<sha[..8]>/` | Yes - decrufted by `ShipPhase` right after the baseline run (best-effort). |
| Local branch `ticket/<id>` | Yes, on `ship` if `ship.delete_feature_branch = true`; otherwise retained by chain cleanup until merged-gated deletion by `build sweep`. |
| Integration branch `chain/<id>` | Retained across chain outcomes; `build sweep` provides merged-gated deletion. |
| Temp files under the OS temp dir | Draft-mode `new` and the `--review` editor loop allocate and delete their own temp `.md` files in `finally`. |
| `.tmp/`, `.scratch/`, `secrets/` | n/a - not written by `build`. |

---

## Loose ends

- **Failed chains park more state than before** (integration branch + per-leaf branches + worktrees), by design; the recovery story is `build sweep`, which is merged-gated and safe by default.
- **Rotation / retention of `.build/events/`** is still not handled.
- **`.preview.pid` / `.preview.meta` handling** remains compatibility-only for the older claude-config preview flow.
- **`transcript.jsonl` / `rework-round.json` schemas** are documented only in their source (`WorkerTranscriptWriter`, `ReworkRoundManifest`); no external schema doc.
- **Architecture doc disagreement on push** persists (see Git refs Loose ends).
