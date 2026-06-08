# 05 - State and Persistence

Everything `build` writes over the lifetime of a session: filesystem state, logs, scratch, Plane records, git refs. Where each lives and whether it is cleaned up.

For configuration files (read-only) see [04-configuration.md](04-configuration.md). For orchestration / lifecycle see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Local on-disk state (per repo)

### `.build/` (project-local runtime root)

Gitignored fragments at [.gitignore:11-14](../../.gitignore#L11-L14): `.build/brief.md` (:11), `.build/events/` (:12), `.build/sessions/` (:13), `.build/config.toml` (:14). The rest of `.build/` (e.g., the example config) is tracked. In a freshly-provisioned project these entries are written by `build setup` / connected `build init` rather than by hand: `GitignoreManager` ([src/ThroughlineBuild.Cli/LocalRepoSetup.cs:13-79](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs#L13-L79)) appends a managed block under the `# Throughline Build (managed by 'build setup')` header containing `.build/config.toml`, `.build/*.md`, `.build/events/`, `.build/sessions/`, `.worktrees/`, `secrets/`, `.tmp/`, plus language-neutral OS/editor noise - append-only and idempotent (a fully-covered file yields no write). The managed list uses the broader `.build/*.md` glob (covering `brief.md` and any other generated markdown) rather than the narrower `.build/brief.md` shown in this repo's own checked-in `.gitignore`. See the "what `build setup` / `build init` write" inventory below.

| Path | Written by | Lifetime | Cleanup |
|---|---|---|---|
| `.build/config.toml` | operator, or `build init` from the embedded template | persistent | manual delete (gitignored) |
| `.build/config.toml.example` | tracked in git | persistent | tracked |
| `.build/events/<stem>.jsonl` | `JsonlEventSink` (every emitted `WorkflowEvent`, incl. the new `CostLedger` line) | persistent (one file per session) | never auto-deleted |
| `.build/sessions/<stem>/` | `--debug` only | persistent | never auto-deleted |
| `.build/brief.md` | every claude-code worker dispatch (`ClaudeCodeAgent`) | overwritten each dispatch | never auto-deleted (gitignored) |

Status: Functional.

**`build init` writes `.build/config.toml`.** `InitCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/InitCommand.cs:68+](../../src/ThroughlineBuild.Cli/InitCommand.cs#L68)) loads the embedded `config.toml.template` via `ConfigTemplateLoader.Load` ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:20-36](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L20-L36)), applies any flag values (`--plane-url`, `--workspace`, `--project-id`, `--token` / `--token-env`) by string-replacing the `REQUIRED_*` placeholders ([src/ThroughlineBuild.Cli/InitCommand.cs:714-720](../../src/ThroughlineBuild.Cli/InitCommand.cs#L714-L720)), creates `.build/` if absent, and writes `config.toml`. It refuses to overwrite an existing file unless `--force` is passed ([:136](../../src/ThroughlineBuild.Cli/InitCommand.cs#L136)), and `--print-template` writes the template to stdout without touching disk. The verb runs before config load in [src/ThroughlineBuild.Cli/Program.cs:231-290](../../src/ThroughlineBuild.Cli/Program.cs#L231-L290) since it bootstraps the config that every other verb needs. In **connected mode** (a `--project-name` plus complete credentials, or an interactive TTY), `init` additionally resolves/creates the Plane project, substitutes the resolved id, and delegates to `SetupCommand` for git init + `.gitignore` + welcome commit + states/labels provisioning ([InitCommand.cs:399,434](../../src/ThroughlineBuild.Cli/InitCommand.cs#L399)) - so connected `init` writes everything `setup` writes, not just `config.toml`. Offline mode leaves the `REQUIRED_*` placeholders intact.

**`.build/brief.md`.** Each claude-code worker dispatch writes the full brief instruction here before spawning the subprocess ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:24-28](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L24-L28)) "for diagnostics". It is overwritten on every dispatch and gitignored; the codex / gemini / copilot agents do not write it (they deliver the brief via process args). The old claim that this name was reserved-but-unused is no longer true.

**Event log file naming.** `SessionFileNameBuilder.Build(...)` ([src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs:20-49](../../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs#L20-L49)) produces `{project}-{ticket_or_slug}-{verb}-{yyyy-MM-dd-HHmmss}` (no extension). `.jsonl` is appended by `JsonlEventSink.EnsureOpened` ([src/ThroughlineBuild.EventLog/JsonlEventSink.cs:28-41](../../src/ThroughlineBuild.EventLog/JsonlEventSink.cs#L28-L41)), which opens the stream in `FileMode.Append`. When `FileNameStem` is unset the sink falls back to the raw `SessionId` GUID for the filename ([src/ThroughlineBuild.EventLog/EventLogOptions.cs:6-13](../../src/ThroughlineBuild.EventLog/EventLogOptions.cs#L6-L13)), but every CLI verb sets the stem, so live runs all produce stem-named files (e.g. `275-plan-2026-05-29-112419.jsonl`). The current [.build/events/](../../.build/events/) listing contains only stem-named files; no GUID-named legacy logs remain.

**Event log schema** is documented in [docs/event-log-format.md](../event-log-format.md). The wire DTO is [src/ThroughlineBuild.EventLog/EventLineDto.cs:12-36](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12-L36) - six original PascalCase fields (`SessionId`, `Timestamp`, `Kind`, `TicketId`, `Phase`, `Data`) plus four snake_case session-context fields (`project_id`, `project_name`, `workspace_slug`, `build_version`) that are `JsonIgnore(WhenWritingNull)` for forward-compat with pre-TLB-147 readers. The set of event kinds has grown to 14 (e.g. `TargetAutoRebased` - renamed from `MainAutoRebased` now that ship rebases a configurable target branch, `TicketSubsumed`, `DispatchStart`/`DispatchEnd`, and `CostLedger`=13) per [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14) (integer map at [:11-13](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L11-L13)).

**Cost-ledger event (TLB-510).** New data written to the event log - not a separate file, just another JSONL line in `.build/events/<stem>.jsonl`. `ChainPhase.EmitCostLedgerAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:805-838](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L805-L838)) emits one `WorkflowEvent` of `Kind = EventKind.CostLedger` (=13), `Phase = Phase.Gate`, once per gate-engaged ticket (it is called from every gate exit branch in the chain implement/review loop, [ChainPhase.cs:625,704,715,724,731,756](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L625)). The `Data` dictionary always carries `gate_wall_ms`, `gate_attributable_rework_rounds`, `cascade_caught` (always 0), and `false_fails` (always 0); when rework rounds occurred and token counts were tracked it adds `gate_attributable_rework_input_tokens` / `gate_attributable_rework_output_tokens`, otherwise (rounds occurred but counts untracked) it adds `gate_attributable_rework_tokens_available = false`. Nothing in `src/` reads or renders this line yet - the consumer (post-run cost analysis) is **Aspirational**; the data is written but inert. `cascade_caught` / `false_fails` are deliberately reserved annotation slots pinned to 0 for now.

**`--debug` capture.** When `--debug` is passed to a worker-spawning verb (`plan`, `implement`, `review`, `chain`, `new`, `rework`, `decompose`, `scaffold`), the orchestrator computes `.build/sessions/<stem>/` and creates it eagerly. The directory is now computed per verb-branch rather than in one central place: the phase-verb path at [src/ThroughlineBuild.Cli/Program.cs:1347-1351](../../src/ThroughlineBuild.Cli/Program.cs#L1347-L1351), the rework/decompose path at [:1147-1150](../../src/ThroughlineBuild.Cli/Program.cs#L1147-L1150), and the `new` path at [:694-698](../../src/ThroughlineBuild.Cli/Program.cs#L694-L698). Under a chain, each batch-implement session also gets its own capture sub-directory created before the worker spawns ([src/ThroughlineBuild.Phases/ChainPhase.cs:1168-1169](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1168-L1169)). The claude-code worker writes ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:463-492](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L463-L492)):

- `worker-stdin.txt` - the brief instruction
- `worker-stdout.txt` - complete raw stdout
- `worker-stderr.txt` - complete raw stderr
- `envelope-result.txt` - inner `result` field from the type=result envelope (when present)
- `worker-result.json` - parsed `WorkerResult` (core fields only; metadata excluded for AOT-safe serialization)
- `parse-error.txt` - failure reason when envelope absent / parse failed
- `cancel-reason.txt` - present on timeout / Ctrl-C ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:527-530](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L527-L530))

The codex / gemini / copilot agents write the same set except they emit `worker-result-summary.txt` instead of `envelope-result.txt`. Codex captures the brief delivered over stdin in `worker-stdin.txt`.

`phase-status.json` is written by `EarlyExitManifest.Write` when a phase exits before the worker spawns ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs:17-35](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs#L17-L35)) - a manual `{phase, ticket_id, reason}` JSON object (AOT-safe, no serializer). The dir is created eagerly so the "Debug capture: .build/sessions/<stem>/" footer always points somewhere real even when the phase fails first.

### `.worktrees/` (git worktrees)

Status: Functional.

Gitignored at [.gitignore:1](../../.gitignore#L1). In the standalone (non-chain) path, created by `ImplementPhase` (initial round only; rework reuses the existing one) via `IGitClient.CreateWorktreeAsync` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:234-248](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L234-L248)), which runs `git worktree add -b <branch> <path> <fromRef>` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:203-208](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L203-L208)). Both the branch name and the worktree directory use the **ticket id only** (no title slug): branch `ticket/<id>` and path `.worktrees/ticket-<id>`, where the slug is `SlugBuilder.BuildTicketSlug(ticketId)` and `<id>` carries no title text ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:7-16](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L7-L16)) - this keeps the path short so deep repo trees stay under Windows `MAX_PATH` (TLB-408). The legacy `ticket/<id>-<title-slug>` form is still recognized for in-flight worktrees created before the rename (`IsTicketBranch` [:29-35](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L29-L35), `MentionsBranch` hyphen-boundary regex [:43-48](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L43-L48)). Removed by `WorktreeDecrufter.DecruftAsync` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:55-192](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L55-L192)) - called from `ShipPhase` after a successful merge ([src/ThroughlineBuild.Phases/ShipPhase.cs:418-438](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L418-L438)), and from `CloseCommand` / `DeferCommand` if the worktree exists ([src/ThroughlineBuild.Commands/CloseCommand.cs:118](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L118), [src/ThroughlineBuild.Commands/DeferCommand.cs:118](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L118)).

**Shared chain integration worktree (retained, op-29).** Under a parent chain the per-ticket layout above is bypassed: `ChainPhase` creates **one** shared integration worktree for the whole chain on an integration branch `chain/<slug>` (`chain/{slug}`, slug = parent ticket id, [src/ThroughlineBuild.Phases/ChainPhase.cs:2649](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2649)), at the parent's `.worktrees/ticket-<parentid>` path (the parent's own `PhaseWorktreeLayout.Compute` path), via `EnsureIntegrationWorktreeAsync` ([src/ThroughlineBuild.Phases/ChainPhase.cs:1947-1958,2651-2681](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1947-L1958)). Each child then creates its own `ticket/<id>` branch *in place inside* this shared worktree via `CreateBranchAsync` rather than allocating its own worktree (`isSharedWorktree` path, [src/ThroughlineBuild.Phases/ImplementPhase.cs:157-166,245-261](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L157-L166)). The shared worktree survives between children because chain ship uses a `SkipDecruft=true` factory ([src/ThroughlineBuild.Phases/ChainPhase.cs:70](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L70), `ShipOptions.SkipDecruft` [src/ThroughlineBuild.Phases/ShipPhase.cs:16,706-709](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L16)).

Unlike the older model, the integration worktree and branches are **intentionally retained at chain end, not torn down**: the `chain/<...>` integration branches and the per-leaf `ticket/<...>` branches/worktrees are left in place so a failed or retried chain can resume from the accumulated topology ([src/ThroughlineBuild.Phases/ChainPhase.cs:2546-2550](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2546-L2550), test `ParentChain_RetainsIntegrationBranch_AtChainEnd`). There is no longer a chain-end force-delete of the integration branch. If the integration worktree cannot be created, the chain does **not** fall back to per-ticket standalone worktrees (that fallback was removed): it stops early, returning `ChainOutcome.ParentStoppedEarly` and emitting a `GateFailure` of kind `integration_worktree_unavailable` ([src/ThroughlineBuild.Phases/ChainPhase.cs:1959-1984](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1959-L1984)) - the prior `shared_worktree_unavailable` kind no longer exists.

**Batch implement (TLB-499..510).** When a sibling group is opted into one batch (`chain --batch-implement <ids>`), all batch commits stack on the **first ticket's** `ticket/<firstId>` branch inside the shared chain worktree; the worker runs one combined implement session there and `BatchCommitVerifier` confirms each ticket's commit appears in declared `stack_position` order ([src/ThroughlineBuild.Phases/ChainPhase.cs:1079-1102](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1079-L1102), [BatchCommitVerifier.cs:78-109](../../src/ThroughlineBuild.Phases/BatchCommitVerifier.cs#L78-L109)).

**Baseline worktree (transient).** Ship's baseline-aware regression check creates a short-lived detached worktree at the onto-ref under `.worktrees/baseline-<sha[..8]>` via `CreateDetachedWorktreeAsync`, runs the same regression checks there to learn which were already failing on the merge base, caches the failing set, then decrufts the worktree best-effort (failure non-blocking) (`ComputeBaselineAsync` [src/ThroughlineBuild.Phases/ShipPhase.cs:819-855](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L819-L855), invoked from [:503](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L503)). The cache is keyed by onto-SHA in `ShipOptions.BaselineCache`, so within one process a repeated onto-SHA reuses the result without re-creating the worktree ([src/ThroughlineBuild.Phases/ShipPhase.cs:825-828](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L825-L828)). `--skip-baseline` skips this entirely (emits `baseline_skipped`, [:492-496](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L492-L496)).

`WorktreeDecrufter` runs a 7-step decruft ladder: kill preview PIDs, remove preview state, pre-clean Windows reparse points, `git worktree remove`, `git worktree remove --force`, `Directory.Delete`, `git worktree prune` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:6-16](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L6-L16)). Per-worktree metadata files it knows to clean:

- `.preview.pid` - PID lines of a running preview process; `WorktreeDecrufter` reads it (third whitespace field) and kills the process tree before removing the worktree ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:73-93](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L73-L93)).
- `.preview.meta` - sidecar metadata, deleted in step 2 ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:95-109](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L95-L109)).

These files are produced by the older claude-config preview workflow, not by `build` itself, but `WorktreeDecrufter` knows to clean them.

**No `git stash` scratch state.** Worker briefs are now forbidden from writing to the stash stack: the implement template says "Do NOT use git stash ... the stash stack is repo-global and leaks across worktrees" ([src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md:31](../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L31), same line in the codex/gemini/copilot variants), and the read-only review verifier is blocked from `git stash`, `git checkout`, `git reset`, and `git rebase` for the same reason ([src/ThroughlineBuild.Briefs/Templates/claude-code/review.md:19-20](../../src/ThroughlineBuild.Briefs/Templates/claude-code/review.md#L19-L20)). Because the stash stack is a single repo-global structure shared by every worktree, scratch state stashed in one ticket's worktree would surface in another - so the contract is to build in place, never stash. `build` itself never writes to the stash stack.

### `MainWorktreeLock` (in-process serialization of main-worktree git ops)

Status: Functional. (NEW, TLB-290/291)

`MainWorktreeLock.WithLockAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:11-28](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L11-L28)) is a process-local lock (a static `ConcurrentDictionary<string, SemaphoreSlim>` keyed by the full main-worktree path, lowercased on Windows). It is not an on-disk lock and provides no cross-process protection - it exists so parallel chain dispatch within one `build` process does not run two mutating main-worktree git operations at the same time. `ShipPhase` wraps the fetch ([src/ThroughlineBuild.Phases/ShipPhase.cs:268-271](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L268-L271)), the target-branch auto-rebase ([src/ThroughlineBuild.Phases/ShipPhase.cs:318-321](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L318-L321)), and the fast-forward merge ([src/ThroughlineBuild.Phases/ShipPhase.cs:496-498](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L496-L498)) in this lock. The semaphores live in a static dictionary, so the lock state is discarded on process exit like all other in-process state.

### `.scratch/` and `secrets/`

Both gitignored ([.gitignore:2](../../.gitignore#L2) for `secrets/`, [.gitignore:10](../../.gitignore#L10) for `.scratch/`). Empty in the working tree today; reserved by convention. Neither is read or written by `build`.

### `bin/`

Gitignored at [.gitignore:3](../../.gitignore#L3). Created by `build.sh`, which copies the AOT binaries `build`, `token-audit`, `analyze-event-log` here (`.exe` suffix on Windows RIDs) ([build.sh:18-34](../../build.sh#L18-L34)). Replaced wholesale on each rebuild.

### `src/tools/artifacts/`

Gitignored at [.gitignore:16](../../.gitignore#L16). `build.sh` publishes the `token-audit` and `analyze-event-log` single-file tools here (`dotnet publish src/tools/<tool>.cs`) before copying the binaries into `bin/` ([build.sh:24-30](../../build.sh#L24-L30)). Not auto-cleaned; deleting the directory is safe.

### Per-project `bin/` and `obj/`

Gitignored at [.gitignore:3-4](../../.gitignore#L3-L4). Created by `dotnet build` / `dotnet publish`. Removed by `dotnet clean` or by deleting the directories.

### `.tmp/`

Gitignored at [.gitignore:15](../../.gitignore#L15). Reserved by convention; not used by `build` today. Note that the actual one-shot temp file used by the draft-mode `new` flow and the `--review` editor loop is created under the OS temp directory (`Path.GetTempFileName`), not `.tmp/` (see Cleanup posture).

### What `build setup` / `build init` / doc verbs write to the repo

Beyond `.build/`, several verbs write durable, tracked files into the project tree:

| Verb | Writes | Where |
|---|---|---|
| `build setup` (and connected `build init`) | `git init` on a non-repo; appends the managed `.gitignore` block (only the missing entries); a `welcome to throughline build` initial git commit staging `.gitignore` on a fresh repo (HEAD-guarded, idempotent); and the missing Plane states + labels | [src/ThroughlineBuild.Cli/SetupCommand.cs:41-148](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L41-L148); `.gitignore` via [LocalRepoSetup.cs:60-78](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs#L60-L78); welcome commit via [WelcomeCommit.cs:22-38](../../src/ThroughlineBuild.Cli/WelcomeCommit.cs#L22-L38) |
| `build init` (offline) | `.build/config.toml` from the embedded template (states/labels/git only run in the connected path that delegates to `SetupCommand`) | [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs) |
| `build user-guide` | `docs/throughline_build_userguide.md` (the embedded operator guide); `--force` overwrites, `--print-template` dumps to stdout | [src/ThroughlineBuild.Cli/Program.cs:304-309](../../src/ThroughlineBuild.Cli/Program.cs#L304-L309), `UserGuideCommand` |
| `build op-doc spec --write` | `docs/op-docs/op-doc-spec.md` (the embedded authoring spec) | [src/ThroughlineBuild.Cli/Program.cs:317-340](../../src/ThroughlineBuild.Cli/Program.cs#L317-L340) |
| `build op-doc new <slug> --write` | `docs/op-docs/op-<slug>.md` (a minimal valid skeleton; refuses to overwrite) | [src/ThroughlineBuild.Cli/Program.cs:342-392](../../src/ThroughlineBuild.Cli/Program.cs#L342-L392) |

The welcome commit's purpose is to give a brand-new repo at least one commit so the first `build ship` can resolve a base ref; it stages only `.gitignore` (`.build/config.toml` is gitignored and never staged) and degrades a commit failure (e.g. missing git identity) to a non-fatal warning.

Status: Functional.

### Loose ends

- **`.build/brief.md` is claude-code-only.** Switching the default worker to codex/gemini/copilot means no brief diagnostics file is written; the gitignore entry and `--debug` capture remain the documented diagnostic surface.
- **Offline `build init` does not validate the resulting config.** In offline mode it writes the template with `REQUIRED_*` placeholders intact unless flags were passed; the first real verb is what surfaces a missing value. Connected mode (with `--project-name`/credentials or an interactive TTY) instead resolves a live project and provisions via `SetupCommand`.
- **`MainWorktreeLock` is in-process only.** Two separate `build` processes shipping concurrently against the same main worktree are not serialized by it - only intra-process parallel dispatch is protected.
- **`.build/sessions/` and `.build/events/` never auto-rotate** (see Cleanup posture below).
- **Cost-ledger lines are write-only.** The `CostLedger` event (TLB-510) is emitted to the event log per gate-engaged ticket but no `src/` reader consumes it yet, and `cascade_caught` / `false_fails` are pinned to 0 - the post-run cost analysis that would read these is Aspirational.

---

## Plane records (remote state)

Status: Functional. Every phase that writes to Plane does so via [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Plane records are durable: they persist in the Plane workspace database and are never reset by `build`. Writes are not transactional - a phase that fails partway through can leave a comment without a state transition (or vice versa). The event log records each write event so the operator can reconstruct exactly what landed.

| Phase | Writes |
|---|---|
| `plan` | description append via `AppendDescriptionAsync` (plan HTML), labels (risk + size) via `ApplyLabelsAsync`, one `[planned_at: <sha>]` comment, state `Backlog -> Planning -> Ready` ([src/ThroughlineBuild.Phases/PlanPhase.cs:98-148](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98-L148)) |
| `implement` | one `[implemented_at: <sha>]` comment with branch name, state transitions `Ready -> InProgress` (initial round) and `InProgress -> InReview`. Under a batch, each batch ticket gets a parenthesized `[implemented_at: <sha>] (branch <name>) (batch: stack_position=N)` comment + `InProgress -> InReview` ([src/ThroughlineBuild.Phases/ChainPhase.cs:1216-1227](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1216-L1227)) |
| `review` | one verdict comment (Pass / Rework / Fail wording), state `InReview -> InProgress` only on Rework. Batch review posts a single `[batch_review: <verdict>]` comment ([src/ThroughlineBuild.Phases/ChainPhase.cs:1583](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1583)) |
| `gate` (chain only) | on a hard-fail of a gating check, one `[gate: hard-fail] gating checks failed: <names>` comment plus state `InReview -> InProgress` (so the rework loop can re-engage); emits a `GateFailure` event of kind `gating_checks_failed` ([src/ThroughlineBuild.Phases/GatePhase.cs:114-132](../../src/ThroughlineBuild.Phases/GatePhase.cs#L114-L132), transition at [:137-149](../../src/ThroughlineBuild.Phases/GatePhase.cs#L137-L149)) |
| `ship` | one `[shipped_at: <sha>]` comment, state `InReview -> Done`; or, on a gate failure, a `<strong>ship_blocked:</strong> ...` comment with no state change. Parent ship posts a `<strong>shipped:</strong> all N children are Done` comment and transitions only the parent. Batch ship posts a parenthesized `[shipped_at: <sha>] (batch into <integration-branch>)` comment + `-> Done` per batch ticket ([src/ThroughlineBuild.Phases/ChainPhase.cs:2429-2439](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2429-L2439)) |
| `rework` | delegates to `ImplementPhase`; same writes as that phase |
| `new` | new ticket (Plane default state), labels (if provided) |
| `decompose` | N child sub-issues via `CreateChildTicketsAsync` (each with parent link + size label), then one `[decomposed_at: <sha>]` comment on the parent ([src/ThroughlineBuild.Phases/DecomposePhase.cs:136-146](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L136-L146)) |
| `scaffold` | N plan-tickets + M brief-tickets, each with parent link |
| `amend` | optional size-label swap + optional dated context-note paragraph appended via `AppendDescriptionAsync`, or a full-description rewrite via `UpdateDescriptionAsync` ([src/ThroughlineBuild.Commands/AmendCommand.cs:66](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L66), [src/ThroughlineBuild.Commands/AmendCommand.cs:101](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L101)) |
| `close` | one `<strong>wontfix:</strong>` comment, state `-> Cancelled`, optional parent rollup; decrufts the worktree if present |
| `defer` | one `<strong>deferred:</strong>` comment, state `-> Cancelled`, optional parent rollup; decrufts the worktree if present |
| `reopen` | one `<strong>reopened:</strong> from <prior_marker> - <reason>` comment, state `-> Backlog` or `-> Ready` |
| `setup` / connected `init` | creates any missing Plane states + labels so the project meets `WorkspaceSchema` (no comments / transitions on existing tickets) ([src/ThroughlineBuild.Cli/SetupCommand.cs:96-148](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L96-L148)) |

`AppendDescriptionAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:313](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L313)) does a read-modify-write append; `UpdateDescriptionAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:701-714](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L701-L714)) PATCHes the description wholesale. Sub-issue creation flows through `CreateChildTicketsAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:716-757](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L716-L757)), which resolves the label cache once, then POSTs each child with `ParentId` set; unknown label names are silently skipped.

Plane comments are HTML. Markers in the form `[name: value]` are parsed back by `MarkerParser` ([src/ThroughlineBuild.Helpers/MarkerParser.cs:5-42](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L5-L42)) - HTML tags are stripped before regex match. This is a load-bearing convention: if a comment loses its marker, downstream phases cannot find the SHA they need. The batch annotations (`branch ...`, `batch: stack_position=N`, `batch into ...`) are deliberately written in *parentheses*, not square brackets: the marker regex only matches `[...]` ([MarkerParser.cs:8](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L8)), so the parser reads exactly the `[implemented_at: <sha>]` / `[shipped_at: <sha>]` marker and ignores the parenthesized context, instead of mis-reading `branch`/`batch` as additional markers.

Because a chain re-run accumulates several `[planned_at]` / `[implemented_at]` markers on the same ticket (each run posts its own), the marker that matters is the **freshest** one. `CommentMarkers.LatestValue` selects the marker on the comment with the maximum `CreatedAt` rather than by list position ([src/ThroughlineBuild.Phases/CommentMarkers.cs:19-37](../../src/ThroughlineBuild.Phases/CommentMarkers.cs#L19-L37), TLB-412). The previous call sites read by list order, and since Plane returns comments newest-first they picked up a stale `implemented_at` from a prior run (an orphaned commit on a different base), mis-attributing the diff and surfacing a spurious Rework.

### Loose ends

- **No transactional Plane writes.** A phase interrupted between writing a comment and transitioning state leaves the ticket in a state visible to humans but not to subsequent phases (which key off markers, not state alone).
- **Partial decompose.** `CreateChildTicketsAsync` returns per-child failures; `DecomposePhase` only fails outright if every child failed ([src/ThroughlineBuild.Phases/DecomposePhase.cs:139-141](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L139-L141)). A mixed result posts `decomposed_at` and leaves the parent partially decomposed.

---

## Git refs and branches

Status: Functional.

| Ref | Created by | Removed by |
|---|---|---|
| Local branch `ticket/<id>` (id only, no title slug; legacy `ticket/<id>-<slug>` still recognized) | `ImplementPhase` standalone (`git worktree add -b`) or `CreateBranchAsync` in place inside a shared chain worktree | `ShipPhase` if `ship.delete_feature_branch = true` (default); not removed by `close`/`defer`; under a chain, leaf `ticket/<id>` branches are **retained** at chain end for resume |
| Integration branch `chain/<slug>` (shared chain integration worktree; accumulates merged child commits) | `ChainPhase.EnsureIntegrationWorktreeAsync` ([:1947-1958,2651-2681](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L1947-L1958)) | **Retained** at chain end for resume - no longer force-deleted ([:2546-2550](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2546-L2550)) |
| Worktree `.worktrees/ticket-<id>` | `ImplementPhase` (standalone) or `ChainPhase` (shared integration worktree at the parent's path, once per chain) | `WorktreeDecrufter` from `ShipPhase` / `CloseCommand` / `DeferCommand`; the shared chain integration worktree is **retained** (ship uses `SkipDecruft=true`) |
| Detached baseline worktree `.worktrees/baseline-<sha[..8]>` | `ShipPhase.ComputeBaselineAsync` ([:810-855](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L810-L855)) | same method, best-effort decruft right after checks run |
| target-branch advancement (FF only) | `ShipPhase.FastForwardMergeAsync` ([src/ThroughlineBuild.Phases/ShipPhase.cs:496-498](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L496-L498)) | n/a |
| target-branch auto-rebase onto `<remote>/<target>` | `ShipPhase` on `DivergedNoConflict` when `--no-auto-merge` is not set; emits `TargetAutoRebased` ([src/ThroughlineBuild.Phases/ShipPhase.cs:305-345](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L305-L345)) | n/a (rewrites the local target branch) |
| Push of the target branch to `<remote>` | `ShipPhase` after the FF merge, skipped when no remote ([src/ThroughlineBuild.Phases/ShipPhase.cs:509-510](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L509-L510)) | n/a |

**`build` pushes the target branch to the remote.** The ship phase is no longer local-merge-only: it resolves the merge target (`[work].target_branch` if set, else `[ship].base_branch`), runs `git push <remote> <target>` after the fast-forward merge succeeds ([src/ThroughlineBuild.Git/ProcessGitClient.cs:583](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L583)), and when the target is a non-default branch first guards that the main worktree is checked out on it (a `git push` of a fast-forwarded wrong branch would otherwise send stale bytes - [src/ThroughlineBuild.Phases/ShipPhase.cs:227-247](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L227-L247)). It also fetches the remote up front and, when the local target and `<remote>/<target>` have diverged without conflicts, auto-rebases the local target onto the remote (the divergence subspecies comes from `IGitClient.ProbeDivergenceAsync`, which uses `git merge-tree --write-tree` to detect conflicts without mutating anything, [src/ThroughlineBuild.Git/ProcessGitClient.cs:962](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L962)). Both the fetch/auto-rebase and the FF merge are wrapped in `MainWorktreeLock`. This contradicts architecture Section 5.9 (still says "v1 is local-merge-only with no `git push origin main`") - see Loose ends. No force operations exist anywhere (`git push --force`, `git reset --hard`, `git rebase -i`).

---

## In-process state (per invocation, discarded on exit)

| Held in | Lifetime | Why |
|---|---|---|
| `PlaneTicketingClient._statesByName` | one process invocation | State table name->uuid cache, lazy-loaded under `_stateLock` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:25-27](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L25-L27)). |
| `PlaneTicketingClient._labelsByName` | one process invocation | Label table name->uuid cache, lazy-loaded under `_labelLock` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:29-31](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L29-L31)). |
| `PlaneTicketingClient._issueTypesByName` | one process invocation | Issue-type name->uuid cache, lazy-loaded under `_issueTypeLock`. |
| `PlaneTicketingClient._seqToUuid` + `_issueByUuid` (NEW, TLB-366) | one process invocation | The per-run issue snapshot: the whole project is paginated once into these two `ConcurrentDictionary` indexes, then every `FindIssueAsync`/`QueryAsync` answers from memory and every PATCH write-throughs via `UpdateCachedIssue` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:47-58,277-361](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L47-L58)). Loaded under a `SemaphoreSlim` single-flight gate. See [03-external-dependencies.md](03-external-dependencies.md) for the cache semantics. |
| `RecordingEventSink._events` | one process invocation | Mirror of the JSONL log so `PhaseSummaryBuilder` renders the per-phase completion summary deterministically from in-memory events ([src/ThroughlineBuild.Helpers/PhaseSummaryBuilder.cs](../../src/ThroughlineBuild.Helpers/PhaseSummaryBuilder.cs)) without re-reading JSONL mid-session. |
| `TemplateLoader._cache` | static (process lifetime) | `ConcurrentDictionary` caching embedded brief-template lookups across builder calls ([src/ThroughlineBuild.Briefs/TemplateLoader.cs:12-33](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs#L12-L33)). `ConfigTemplateLoader._cached` plays the same role for `config.toml.template` ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:13](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L13)). |
| `MainWorktreeLock.Locks` | static (process lifetime) | Semaphore-per-main-worktree map for intra-process git serialization ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:8-9](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L8-L9)). |

There is **no** cross-invocation persistent state in process. The binary is a one-shot CLI - on exit, everything in memory is gone, and every subsequent invocation reloads from disk + Plane.

---

## State that survives the binary but predates it

- **`~/.claude/projects/<encoded-path>/*.jsonl`** - Claude Code session logs. Not written by `build` directly; the `claude` subprocess writes them on each invocation. `token-audit` reads them.
- **Plane database** - durable, never reset by `build`.

---

## Cleanup posture

| Class of artifact | Auto-cleanup? |
|---|---|
| `.build/events/*.jsonl` | No. Accumulate forever. `analyze-event-log` can process a directory of them in one pass. |
| `.build/sessions/<stem>/` | No. Operator must prune. |
| `.worktrees/ticket-<id>/` | Standalone: Yes - by `WorktreeDecrufter` on successful `ship` (and on `close`/`defer`); failed `ship` deliberately leaves it for inspection. Under a chain: **No** - the shared integration worktree (and the leaf branches inside it) is intentionally retained at chain end so a retried chain can resume from the accumulated topology. |
| `.worktrees/baseline-<sha[..8]>/` | Yes - decrufted by `ShipPhase` immediately after the baseline regression checks run (best-effort; a stranded one is safe to delete). |
| Local feature branch `ticket/<id>` | Standalone: Yes, on `ship` if `ship.delete_feature_branch = true`. Chain leaf branches: No (retained). |
| Integration branch `chain/<slug>` | **No** - retained at chain end for resume (the prior chain-end force-delete was removed). |
| Temp files under the OS temp dir | The draft-mode `new` flow writes a temp `.md` body (`Path.GetTempFileName` -> `.md`) and deletes it in `finally` ([src/ThroughlineBuild.Cli/Program.cs:903-907](../../src/ThroughlineBuild.Cli/Program.cs#L903-L907), [src/ThroughlineBuild.Cli/Program.cs:937](../../src/ThroughlineBuild.Cli/Program.cs#L937)). The `--review` editor loop allocates and deletes its own temp `.md` ([src/ThroughlineBuild.Cli/ReviewLoop.cs:126](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L126), [src/ThroughlineBuild.Cli/ReviewLoop.cs:163](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L163)). |
| `.tmp/`, `.scratch/`, `secrets/` | n/a - not written by `build`. |

---

## Loose ends

- **Architecture doc disagreement: ship no longer local-merge-only.** [docs/throughline-build-architecture.md:174](../throughline-build-architecture.md) (Section 5.9) still states "v1 is local-merge-only with no `git push origin main`", but `ShipPhase` now fetches, can auto-rebase local `main` onto `origin/main`, and pushes after the FF merge (TLB-293/296/297). The architecture doc is stale on this point.
- **Failed ship leaves a worktree on disk by design** - useful for debugging but accumulates if the operator forgets. `WorktreeDecrufter` is not invoked from a standalone cleanup verb (no `build cleanup`).
- **`.preview.pid` / `.preview.meta` handling** is in `WorktreeDecrufter` for compatibility with the older claude-config preview flow; `build` itself never writes these files.
- **Rotation / retention of `.build/events/`** is not handled. Long-running repos will accumulate megabytes of JSONL.
- **`.build/sessions/<stem>/parse-error.txt`** and `phase-status.json` have no documented schema beyond their source ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs)).
