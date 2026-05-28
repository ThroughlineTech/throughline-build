# 05 - State and Persistence

Everything `build` writes over the lifetime of a session: filesystem state, logs, scratch, Plane records, git refs. Where each lives and whether it is cleaned up.

For configuration files (read-only) see [04-configuration.md](04-configuration.md). For orchestration / lifecycle see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

---

## Local on-disk state (per repo)

### `.build/` (project-local runtime root)

Gitignored fragments at [.gitignore:11-13](../../.gitignore#L11-L13): `.build/brief.md`, `.build/events/`, `.build/sessions/`, `.build/config.toml`. The rest of `.build/` (e.g., the example config) is tracked.

| Path | Written by | Lifetime | Cleanup |
|---|---|---|---|
| `.build/config.toml` | operator | persistent | manual delete (gitignored) |
| `.build/config.toml.example` | tracked in git | persistent | tracked |
| `.build/events/<stem>.jsonl` | `JsonlEventSink` | persistent (one file per session) | never auto-deleted |
| `.build/events/<sessionId>.jsonl` (legacy format) | older sessions | persistent | never auto-deleted |
| `.build/sessions/<stem>/` | `--debug` only | persistent | never auto-deleted |
| `.build/brief.md` | unused today (gitignore reserves the name) | n/a | n/a |

**Event log file naming.** `SessionFileNameBuilder.Build(...)` ([src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs:18-98](../../src/ThroughlineBuild.EventLog/SessionFileNameBuilder.cs#L18-L98)) produces `{project}-{ticket_or_slug}-{verb}-{yyyy-MM-dd-HHmmss}` (no extension). `.jsonl` is appended by `JsonlEventSink`. Older invocations created files named by raw `SessionId` (a GUID); the directory listing in [.build/events/](../../.build/events/) currently shows nine such legacy GUID-named files alongside any newer stem-named files.

**Event log schema** is documented in [docs/event-log-format.md](../event-log-format.md). The wire DTO is [src/ThroughlineBuild.EventLog/EventLineDto.cs](../../src/ThroughlineBuild.EventLog/EventLineDto.cs) - six original PascalCase fields (`SessionId`, `Timestamp`, `Kind`, `TicketId`, `Phase`, `Data`) plus four snake_case session-context fields (`project_id`, `project_name`, `workspace_slug`, `build_version`) that are `JsonIgnore` when null for forward-compat with pre-TLB-147 readers.

**`--debug` capture.** When `--debug` is passed to `plan`, `implement`, `review`, `chain`, `new`, or `rework`, the orchestrator creates `.build/sessions/<stem>/` and the worker writes:

- `worker-stdin.txt` - the brief instruction
- `worker-stdout.txt` - complete raw stdout
- `worker-stderr.txt` - complete raw stderr
- `envelope-result.txt` - inner `result` field from the type=result envelope (when present)
- `worker-result.json` - parsed `WorkerResult` (core fields only; metadata excluded for AOT-safe serialization)
- `parse-error.txt` - failure reason when envelope absent / parse failed
- `cancel-reason.txt` - present on timeout / Ctrl-C
- `phase-status.json` - written by `EarlyExitManifest` when a phase exits before the worker spawns ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs:24-28](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs#L24-L28))

The dir is created eagerly so the "Debug capture: .build/sessions/<stem>/" footer always points somewhere real, even when the phase fails before the worker runs ([src/ThroughlineBuild.Cli/Program.cs:622-629](../../src/ThroughlineBuild.Cli/Program.cs#L622-L629)).

### `.worktrees/` (git worktrees)

Gitignored at [.gitignore:1](../../.gitignore#L1). Created by `ImplementPhase` via `git worktree add -b ticket/<slug> .worktrees/ticket-<slug> <baseRef>` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:122-127](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L122-L127)). Removed by `WorktreeDecrufter.DecruftAsync` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:44-192](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L44-L192)) - called from `ShipPhase` after a successful merge, and from `CloseCommand` / `DeferCommand` if the worktree exists.

Per-worktree metadata files written by external preview tooling:

- `.preview.pid` - PID of a running preview process; `WorktreeDecrufter` reads it and kills the process tree before removing the worktree.
- `.preview.meta` - sidecar metadata.

These files are produced by the older claude-config workflow, not by `build` itself, but `WorktreeDecrufter` knows to clean them.

### `.scratch/` and `secrets/`

Both gitignored at [.gitignore:2,10](../../.gitignore#L2). Empty in the working tree today; reserved by convention. Neither is read or written by `build`.

### `bin/`

Gitignored at [.gitignore:3](../../.gitignore#L3). Created by `build.sh` containing the AOT binaries (`build.exe`, `token-audit.exe`, `analyze-event-log.exe`). Replaced wholesale on each rebuild.

### Per-project `bin/` and `obj/`

Gitignored at [.gitignore:3-4](../../.gitignore#L3). Created by `dotnet build` / `dotnet publish`. Removed by `dotnet clean` or by deleting the directories.

### `.tmp/`

Gitignored at [.gitignore:15](../../.gitignore#L15). Reserved by convention; not used by `build` today.

---

## Plane records (remote state)

Every phase that writes to Plane does so via [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs). Writes are not transactional - a phase that fails partway through can leave a comment without a state transition (or vice versa). The event log records each write event so the operator can reconstruct exactly what landed.

| Phase | Writes |
|---|---|
| `plan` | description append (plan HTML), labels (risk + size), one `[planned_at: <sha>]` comment, state `Backlog -> Planning -> Ready` |
| `implement` | one `[implemented_at: <sha>]` comment with branch name, state transitions `Ready -> InProgress` (initial round) and `InProgress -> InReview` |
| `review` | one verdict comment (Pass / Rework / Fail wording), state `InReview -> InProgress` only on Rework |
| `ship` | one `[shipped_at: <sha>]` comment, state `InReview -> Done` |
| `rework` | delegates to `ImplementPhase`; same writes as that phase |
| `new` | new ticket (Plane default state), labels (if provided) |
| `scaffold` | N plan-tickets + M brief-tickets, each with parent link via `SetParentAsync` |
| `amend` | optional size-label swap + optional dated context-note paragraph appended to description |
| `close` | one `<strong>wontfix:</strong>` comment, state `-> Cancelled`, optional parent rollup |
| `defer` | one `<strong>deferred:</strong>` comment, state `-> Cancelled`, optional parent rollup |
| `reopen` | one `<strong>reopened:</strong> from <prior_marker> - <reason>` comment, state `-> Backlog` or `-> Ready` |

Plane comments are HTML. Markers in the form `[name: value]` are parsed back by `MarkerParser` ([src/ThroughlineBuild.Helpers/MarkerParser.cs:5-42](../../src/ThroughlineBuild.Helpers/MarkerParser.cs#L5-L42)) - HTML tags are stripped before regex match. This is a load-bearing convention: if a comment loses its marker, downstream phases cannot find the SHA they need.

---

## Git refs and branches

| Ref | Created by | Removed by |
|---|---|---|
| Local branch `ticket/<slug>` | `ImplementPhase` (`git worktree add -b`) | `ShipPhase` if `ship.delete_feature_branch = true` (default); not removed by `close`/`defer` |
| Worktree `.worktrees/ticket-<slug>` | `ImplementPhase` | `WorktreeDecrufter` from `ShipPhase` / `CloseCommand` / `DeferCommand` |
| `main` advancement (FF only) | `ShipPhase.FastForwardMergeAsync` | n/a |

`build` never pushes to a remote. Architecture Section 5.9 spells this out: v1 is local-merge-only.

---

## In-process state (per invocation, discarded on exit)

| Held in | Why |
|---|---|
| `PlaneTicketingClient._statesByName`, `_labelsByName` | Avoid re-fetching the state and label tables on every API call. |
| `RecordingEventSink._events` | Mirror of the JSONL log so `PhaseSummaryBuilder` can render the per-phase completion summary deterministically from in-memory events ([src/ThroughlineBuild.Helpers/PhaseSummaryBuilder.cs](../../src/ThroughlineBuild.Helpers/PhaseSummaryBuilder.cs)) without re-reading JSONL mid-session. |
| `ConcurrentDictionary` in `TemplateLoader` | Cache embedded resource string lookups across multiple builder calls per invocation ([src/ThroughlineBuild.Briefs/TemplateLoader.cs:6-30](../../src/ThroughlineBuild.Briefs/TemplateLoader.cs#L6-L30)). |

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
| Temp files in `Path.GetTempPath()` | `NewCommand` writes a temp `body.md` for the draft-mode flow and deletes it in `finally` ([src/ThroughlineBuild.Cli/Program.cs:516-518](../../src/ThroughlineBuild.Cli/Program.cs#L516-L518)). |
| `.tmp/`, `.scratch/`, `secrets/` | n/a - not written by `build`. |

---

## Loose ends

- **Legacy GUID-named event logs** in `.build/events/` cannot be matched back to a verb/ticket without parsing the contents. New runs use the stem-based name. There is no migration tool.
- **Failed ship leaves a worktree on disk by design** - useful for debugging but accumulates if the operator forgets. `WorktreeDecrufter` is not invoked from a standalone cleanup verb (no `build cleanup`).
- **`.preview.pid` / `.preview.meta` handling** is in `WorktreeDecrufter` for compatibility with the older claude-config preview flow; `build` itself never writes these files.
- **Rotation / retention of `.build/events/`** is not handled. Long-running repos will accumulate megabytes of JSONL.
- **`.build/sessions/<stem>/parse-error.txt`** and `phase-status.json` have no documented schema beyond their source ([src/ThroughlineBuild.Phases/EarlyExitManifest.cs](../../src/ThroughlineBuild.Phases/EarlyExitManifest.cs)).
- **No transactional Plane writes** - a phase interrupted between writing a comment and transitioning state leaves the ticket in an inconsistent state visible to humans but not to subsequent phases (which key off markers, not state alone).
