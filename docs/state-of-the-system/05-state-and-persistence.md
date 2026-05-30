# 05 - State and Persistence

Everything `build` writes over the lifetime of a session: filesystem state, logs, scratch, Plane records, git refs. Where each lives and whether it is cleaned up.

For configuration files (read-only) see [04-configuration.md](04-configuration.md). For orchestration / lifecycle see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Local on-disk state (per repo)

### `.build/` (project-local runtime root)

Gitignored fragments at [.gitignore:11-14](../../.gitignore#L11-L14): `.build/brief.md` (:11), `.build/events/` (:12), `.build/sessions/` (:13), `.build/config.toml` (:14). The rest of `.build/` (e.g., the example config) is tracked.

| Path | Written by | Lifetime | Cleanup |
|---|---|---|---|
| `.build/config.toml` | operator, or `build init` from the embedded template | persistent | manual delete (gitignored) |
| `.build/config.toml.example` | tracked in git | persistent | tracked |
| `.build/events/<stem>.jsonl` | `JsonlEventSink` | persistent (one file per session) | never auto-deleted |
| `.build/sessions/<stem>/` | `--debug` only | persistent | never auto-deleted |
| `.build/brief.md` | every claude-code worker dispatch (`ClaudeCodeAgent`) | overwritten each dispatch | never auto-deleted (gitignored) |

Status: Functional.

**`build init` writes `.build/config.toml`.** `InitCommand.Execute` ([src/ThroughlineBuild.Cli/InitCommand.cs:24-59](../../src/ThroughlineBuild.Cli/InitCommand.cs#L24-L59)) loads the embedded `config.toml.template` via `ConfigTemplateLoader.Load` ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:20-36](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L20-L36)), applies any flag values (`--plane-url`, `--workspace`, `--project-id`, `--token` / `--token-env`) by string-replacing the `REQUIRED_*` placeholders ([src/ThroughlineBuild.Cli/InitCommand.cs:64-101](../../src/ThroughlineBuild.Cli/InitCommand.cs#L64-L101)), creates `.build/` if absent, and writes `config.toml`. It refuses to overwrite an existing file unless `--force` is passed, and `--print-template` writes the template to stdout without touching disk. The verb runs before config load in [src/ThroughlineBuild.Cli/Program.cs:129-144](../../src/ThroughlineBuild.Cli/Program.cs#L129-L144) since it bootstraps the config that every other verb needs.

**`.build/brief.md`.** Each claude-code worker dispatch writes the full brief instruction here before spawning the subprocess ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:24-28](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L24-L28)) "for diagnostics". It is overwritten on every dispatch and gitignored; the codex / gemini / copilot agents do not write it (they deliver the brief via process args). The old claim that this name was reserved-but-unused is no longer true.

**Event log file naming.** `SessionFileNameBuilder.Build(...)` ([src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs:20-49](../../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs#L20-L49)) produces `{project}-{ticket_or_slug}-{verb}-{yyyy-MM-dd-HHmmss}` (no extension). `.jsonl` is appended by `JsonlEventSink.EnsureOpened` ([src/ThroughlineBuild.EventLog/JsonlEventSink.cs:28-41](../../src/ThroughlineBuild.EventLog/JsonlEventSink.cs#L28-L41)), which opens the stream in `FileMode.Append`. When `FileNameStem` is unset the sink falls back to the raw `SessionId` GUID for the filename ([src/ThroughlineBuild.EventLog/EventLogOptions.cs:6-13](../../src/ThroughlineBuild.EventLog/EventLogOptions.cs#L6-L13)), but every CLI verb sets the stem, so live runs all produce stem-named files (e.g. `275-plan-2026-05-29-112419.jsonl`). The current [.build/events/](../../.build/events/) listing contains only stem-named files; no GUID-named legacy logs remain.

**Event log schema** is documented in [docs/event-log-format.md](../event-log-format.md). The wire DTO is [src/ThroughlineBuild.EventLog/EventLineDto.cs:12-36](../../src/ThroughlineBuild.EventLog/EventLineDto.cs#L12-L36) - six original PascalCase fields (`SessionId`, `Timestamp`, `Kind`, `TicketId`, `Phase`, `Data`) plus four snake_case session-context fields (`project_id`, `project_name`, `workspace_slug`, `build_version`) that are `JsonIgnore(WhenWritingNull)` for forward-compat with pre-TLB-147 readers. The set of event kinds has grown (e.g. `MainAutoRebased`, `TicketSubsumed`, `DispatchStart`/`DispatchEnd`) per [src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs:14](../../src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs#L14).

**`--debug` capture.** When `--debug` is passed to a worker-spawning verb (`plan`, `implement`, `review`, `chain`, `new`, `rework`, `decompose`, `scaffold`), the orchestrator computes `.build/sessions/<stem>/` and creates it eagerly ([src/ThroughlineBuild.Cli/Program.cs:818-827](../../src/ThroughlineBuild.Cli/Program.cs#L818-L827)). The claude-code worker writes ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:437-457](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L437-L457)):

- `worker-stdin.txt` - the brief instruction
- `worker-stdout.txt` - complete raw stdout
- `worker-stderr.txt` - complete raw stderr
- `envelope-result.txt` - inner `result` field from the type=result envelope (when present)
- `worker-result.json` - parsed `WorkerResult` (core fields only; metadata excluded for AOT-safe serialization)
- `parse-error.txt` - failure reason when envelope absent / parse failed
- `cancel-reason.txt` - present on timeout / Ctrl-C ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:491-494](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L491-L494))

The codex / gemini / copilot agents write the same set except they emit `worker-result-summary.txt` instead of `envelope-result.txt` and note "brief delivered via args" for stdin (e.g. [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:236-255](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L236-L255)).

`phase-status.json` is written by `EarlyExitManifest.Write` when a phase exits before the worker spawns ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs:17-35](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs#L17-L35)) - a manual `{phase, ticket_id, reason}` JSON object (AOT-safe, no serializer). The dir is created eagerly so the "Debug capture: .build/sessions/<stem>/" footer always points somewhere real even when the phase fails first.

### `.worktrees/` (git worktrees)

Status: Functional.

Gitignored at [.gitignore:1](../../.gitignore#L1). Created by `ImplementPhase` (initial round only; rework reuses the existing one) via `IGitClient.CreateWorktreeAsync` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:142-156](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L142-L156)), which runs `git worktree add -b <branch> <path> <fromRef>` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:203-208](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L203-L208)). The branch name `ticket/<slug>` and path `.worktrees/ticket-<slug>` come from `PhaseWorktreeLayout.Compute` ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:5-11](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L5-L11)). Removed by `WorktreeDecrufter.DecruftAsync` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:55-192](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L55-L192)) - called from `ShipPhase` after a successful merge ([src/ThroughlineBuild.Phases/ShipPhase.cs:418-438](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L418-L438)), and from `CloseCommand` / `DeferCommand` if the worktree exists ([src/ThroughlineBuild.Commands/CloseCommand.cs:118](../../src/ThroughlineBuild.Commands/CloseCommand.cs#L118), [src/ThroughlineBuild.Commands/DeferCommand.cs:118](../../src/ThroughlineBuild.Commands/DeferCommand.cs#L118)).

`WorktreeDecrufter` runs a 7-step decruft ladder: kill preview PIDs, remove preview state, pre-clean Windows reparse points, `git worktree remove`, `git worktree remove --force`, `Directory.Delete`, `git worktree prune` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:6-16](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L6-L16)). Per-worktree metadata files it knows to clean:

- `.preview.pid` - PID lines of a running preview process; `WorktreeDecrufter` reads it (third whitespace field) and kills the process tree before removing the worktree ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:73-93](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L73-L93)).
- `.preview.meta` - sidecar metadata, deleted in step 2 ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:95-109](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L95-L109)).

These files are produced by the older claude-config preview workflow, not by `build` itself, but `WorktreeDecrufter` knows to clean them.

### `MainWorktreeLock` (in-process serialization of main-worktree git ops)

Status: Functional. (NEW, TLB-290/291)

`MainWorktreeLock.WithLockAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:11-28](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L11-L28)) is a process-local lock (a static `ConcurrentDictionary<string, SemaphoreSlim>` keyed by the full main-worktree path, lowercased on Windows). It is not an on-disk lock and provides no cross-process protection - it exists so parallel chain dispatch within one `build` process does not run two mutating main-worktree git operations at the same time. `ShipPhase` wraps the fetch ([src/ThroughlineBuild.Phases/ShipPhase.cs:194-197](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L194-L197)), the local-main auto-rebase ([src/ThroughlineBuild.Phases/ShipPhase.cs:243-246](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L243-L246)), and the fast-forward merge ([src/ThroughlineBuild.Phases/ShipPhase.cs:380-383](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L380-L383)) in this lock. The semaphores live in a static dictionary, so the lock state is discarded on process exit like all other in-process state.

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

### Loose ends

- **`.build/brief.md` is claude-code-only.** Switching the default worker to codex/gemini/copilot means no brief diagnostics file is written; the gitignore entry and `--debug` capture remain the documented diagnostic surface.
- **`build init` does not validate the resulting config.** It writes the template with `REQUIRED_*` placeholders intact unless flags were passed; the first real verb is what surfaces a missing value.
- **`MainWorktreeLock` is in-process only.** Two separate `build` processes shipping concurrently against the same main worktree are not serialized by it - only intra-process parallel dispatch is protected.
- **`.build/sessions/` and `.build/events/` never auto-rotate** (see Cleanup posture below).

---

## Plane records (remote state)

Status: Functional. Every phase that writes to Plane does so via [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Plane records are durable: they persist in the Plane workspace database and are never reset by `build`. Writes are not transactional - a phase that fails partway through can leave a comment without a state transition (or vice versa). The event log records each write event so the operator can reconstruct exactly what landed.

| Phase | Writes |
|---|---|
| `plan` | description append via `AppendDescriptionAsync` (plan HTML), labels (risk + size) via `ApplyLabelsAsync`, one `[planned_at: <sha>]` comment, state `Backlog -> Planning -> Ready` ([src/ThroughlineBuild.Phases/PlanPhase.cs:98-148](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98-L148)) |
| `implement` | one `[implemented_at: <sha>]` comment with branch name, state transitions `Ready -> InProgress` (initial round) and `InProgress -> InReview` |
| `review` | one verdict comment (Pass / Rework / Fail wording), state `InReview -> InProgress` only on Rework |
| `ship` | one `[shipped_at: <sha>]` comment, state `InReview -> Done`; or, on a gate failure, a `<strong>ship_blocked:</strong> ...` comment with no state change. Parent ship posts a `<strong>shipped:</strong> all N children are Done` comment and transitions only the parent ([src/ThroughlineBuild.Phases/ShipPhase.cs:402-516](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L402-L516)) |
| `rework` | delegates to `ImplementPhase`; same writes as that phase |
| `new` | new ticket (Plane default state), labels (if provided) |
| `decompose` | N child sub-issues via `CreateChildTicketsAsync` (each with parent link + size label), then one `[decomposed_at: <sha>]` comment on the parent ([src/ThroughlineBuild.Phases/DecomposePhase.cs:136-146](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L136-L146)) |
| `scaffold` | N plan-tickets + M brief-tickets, each with parent link |
| `amend` | optional size-label swap + optional dated context-note paragraph appended via `AppendDescriptionAsync`, or a full-description rewrite via `UpdateDescriptionAsync` ([src/ThroughlineBuild.Commands/AmendCommand.cs:66](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L66), [src/ThroughlineBuild.Commands/AmendCommand.cs:101](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L101)) |
| `close` | one `<strong>wontfix:</strong>` comment, state `-> Cancelled`, optional parent rollup; decrufts the worktree if present |
| `defer` | one `<strong>deferred:</strong>` comment, state `-> Cancelled`, optional parent rollup; decrufts the worktree if present |
| `reopen` | one `<strong>reopened:</strong> from <prior_marker> - <reason>` comment, state `-> Backlog` or `-> Ready` |

`AppendDescriptionAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:313](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L313)) does a read-modify-write append; `UpdateDescriptionAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:701-714](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L701-L714)) PATCHes the description wholesale. Sub-issue creation flows through `CreateChildTicketsAsync` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:716-757](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L716-L757)), which resolves the label cache once, then POSTs each child with `ParentId` set; unknown label names are silently skipped.

Plane comments are HTML. Markers in the form `[name: value]` are parsed back by `MarkerParser` ([src/ThroughlineBuild.Helpers/MarkerParser.cs:5-42](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L5-L42)) - HTML tags are stripped before regex match. This is a load-bearing convention: if a comment loses its marker, downstream phases cannot find the SHA they need.

### Loose ends

- **No transactional Plane writes.** A phase interrupted between writing a comment and transitioning state leaves the ticket in a state visible to humans but not to subsequent phases (which key off markers, not state alone).
- **Partial decompose.** `CreateChildTicketsAsync` returns per-child failures; `DecomposePhase` only fails outright if every child failed ([src/ThroughlineBuild.Phases/DecomposePhase.cs:139-141](../../src/ThroughlineBuild.Phases/DecomposePhase.cs#L139-L141)). A mixed result posts `decomposed_at` and leaves the parent partially decomposed.

---

## Git refs and branches

Status: Functional.

| Ref | Created by | Removed by |
|---|---|---|
| Local branch `ticket/<slug>` | `ImplementPhase` (`git worktree add -b`) | `ShipPhase` if `ship.delete_feature_branch = true` (default); not removed by `close`/`defer` |
| Worktree `.worktrees/ticket-<slug>` | `ImplementPhase` | `WorktreeDecrufter` from `ShipPhase` / `CloseCommand` / `DeferCommand` |
| `main` advancement (FF only) | `ShipPhase.FastForwardMergeAsync` | n/a |
| `main` auto-rebase onto `origin/main` (NEW, TLB-296/297) | `ShipPhase` on `DivergedNoConflict` when `--no-auto-merge` is not set ([src/ThroughlineBuild.Phases/ShipPhase.cs:235-259](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L235-L259)) | n/a (rewrites local `main`) |
| Push of `main` to `origin` (NEW, TLB-293) | `ShipPhase` after the FF merge, skipped when no remote ([src/ThroughlineBuild.Phases/ShipPhase.cs:389-397](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L389-L397)) | n/a |

**`build` now pushes to the remote.** Earlier the ship phase was local-merge-only; as of TLB-293 `ShipPhase` runs `git push <remote> <baseBranch>` after the fast-forward merge succeeds ([src/ThroughlineBuild.Git/ProcessGitClient.cs:488-500](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L488-L500)). It also fetches the remote up front and, when local `main` and `origin/main` have diverged without conflicts, auto-rebases local `main` onto `origin/main` (the divergence subspecies comes from `IGitClient.ProbeDivergenceAsync`, which uses `git merge-tree --write-tree` to detect conflicts without mutating anything, [src/ThroughlineBuild.Git/ProcessGitClient.cs:866-912](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L866-L912)). Both the fetch/auto-rebase and the FF merge are wrapped in `MainWorktreeLock`. This contradicts architecture Section 5.9 (still says "v1 is local-merge-only with no `git push origin main`") - see Loose ends. No force operations exist anywhere (`git push --force`, `git reset --hard`, `git rebase -i`).

---

## In-process state (per invocation, discarded on exit)

| Held in | Lifetime | Why |
|---|---|---|
| `PlaneTicketingClient._statesByName` | one process invocation | State table name->uuid cache, lazy-loaded under `_stateLock` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:25-27](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L25-L27)). |
| `PlaneTicketingClient._labelsByName` | one process invocation | Label table name->uuid cache, lazy-loaded under `_labelLock` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:29-31](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L29-L31)). |
| `PlaneTicketingClient._issueTypesByName` (NEW) | one process invocation | Issue-type name->uuid cache, lazy-loaded under `_issueTypeLock` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:33-35](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L33-L35), populated at [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:177-192](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L177-L192)). |
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
| `.worktrees/ticket-<slug>/` | Yes - by `WorktreeDecrufter` on successful `ship` (and on `close`/`defer`). Failed `ship` deliberately leaves it for inspection. |
| Local feature branch | Yes, on `ship` if `ship.delete_feature_branch = true`. |
| Temp files under the OS temp dir | The draft-mode `new` flow writes a temp `.md` body (`Path.GetTempFileName` -> `.md`) and deletes it in `finally` ([src/ThroughlineBuild.Cli/Program.cs:584-589](../../src/ThroughlineBuild.Cli/Program.cs#L584-L589), [src/ThroughlineBuild.Cli/Program.cs:619](../../src/ThroughlineBuild.Cli/Program.cs#L619)). The `--review` editor loop allocates and deletes its own temp `.md` ([src/ThroughlineBuild.Cli/ReviewLoop.cs:124-128](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L124-L128), [src/ThroughlineBuild.Cli/ReviewLoop.cs:161-163](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L161-L163)). |
| `.tmp/`, `.scratch/`, `secrets/` | n/a - not written by `build`. |

---

## Loose ends

- **Architecture doc disagreement: ship no longer local-merge-only.** [docs/throughline-build-architecture.md:174](../throughline-build-architecture.md) (Section 5.9) still states "v1 is local-merge-only with no `git push origin main`", but `ShipPhase` now fetches, can auto-rebase local `main` onto `origin/main`, and pushes after the FF merge (TLB-293/296/297). The architecture doc is stale on this point.
- **Failed ship leaves a worktree on disk by design** - useful for debugging but accumulates if the operator forgets. `WorktreeDecrufter` is not invoked from a standalone cleanup verb (no `build cleanup`).
- **`.preview.pid` / `.preview.meta` handling** is in `WorktreeDecrufter` for compatibility with the older claude-config preview flow; `build` itself never writes these files.
- **Rotation / retention of `.build/events/`** is not handled. Long-running repos will accumulate megabytes of JSONL.
- **`.build/sessions/<stem>/parse-error.txt`** and `phase-status.json` have no documented schema beyond their source ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs)).
