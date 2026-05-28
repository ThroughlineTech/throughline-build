# 09 - Failure Modes and Idempotency

For each major operation, how it fails and whether re-running is safe. Exit codes summarized in [06-public-surfaces.md](06-public-surfaces.md).

---

## Failure-mode summary table

| Operation | Pre-flight gates | Common failures | Failed-at state | Idempotent on rerun? |
|---|---|---|---|---|
| `plan` | ticket exists; state == `Backlog`; `git rev-parse main` resolves | worker non-Ok status; missing required metadata keys (`plan_html`, `risk_label`, `size_label`, `planned_at_sha`) | ticket left in `Backlog` (no transition until worker succeeds) | yes - rerun replays the brief; partial Plane writes (description, labels) may duplicate if a prior run got partway |
| `implement` | ticket exists; state == `Ready` (or `InProgress` for rework); `git worktree add` succeeds | worktree creation fails; worker non-Ok; missing `commit_sha` metadata | `Ready` if pre-worker; `InProgress` if worker ran but didn't deliver | yes if worktree was created (rerun reuses it); no clean rollback if worktree creation half-finished |
| `review` | ticket exists; state == `InReview`; worktree locatable; `[implemented_at]` marker present | check timeout; verifier subprocess crash; missing `verdict` / `rationale` metadata | state unchanged (only Rework changes it) | yes - rerun re-runs checks and verifier; one extra verdict comment posted |
| `ship` | ticket exists; state == `InReview`; worktree locatable; build.exe not inside it; both worktrees clean of tracked changes; rebase succeeds; no conflict markers; regression checks pass; FF merge succeeds | listed at each stage via `ShipFailureStage` | enum value identifies stage (`StateCheck`, `PreFlight`, `Fetch`, `Rebase`, `ConflictMarkerScan`, `RegressionChecks`, `FastForwardMerge`, `Decruft`) | partially - rebase + checks idempotent, post-merge transitions not retried by `ship` itself |
| `chain` | starting state must be `Backlog`, `Ready`, or `InReview` (else `RefusedInitialState`) | any inner phase failure propagates as `StoppedAt*`; rework cap (`MaxReworkRounds = 2`) | `ChainOutcome` value identifies stop point | yes - rerunning chains starts at whatever state the ticket landed in |
| `rework` | state == `InProgress`; either manual `--feedback` or a `Rework` verdict in event log | feedback retrieval fails; underlying `ImplementPhase` fails | `ImplementFailed`, `NoFeedbackAvailable`, `TicketNotInProgress` | yes |
| `new` (file mode) | body file readable; title present | validation throws on missing title / empty body | nothing to roll back | yes - duplicates the Plane ticket on rerun |
| `new` (draft mode) | worker dispatchable | draft worker fails / wrong shape; user quits review loop | nothing posted | yes |
| `scaffold` | op-doc parses; validation passes (or `--accept-warnings`) | per-plan or per-brief create or parent-link failures collected in `ScaffoldFailure[]` | partial creation possible | no - rerun creates duplicates because plan/brief tickets are never matched back by content |
| `amend` | state not terminal; at least one of `--size` / `--note` | invalid size value; terminal state | nothing | yes - replacing labels is idempotent; appending notes accumulates |
| `close` / `defer` | state not terminal; reason supplied; (close/defer) Anthropic key present | translator failure (fatal); rollup failure (soft); decruft failure (soft) | nothing if pre-translation; `Cancelled` after transition | no - rerunning on a `Cancelled` ticket fails the state check |
| `reopen` | state is `Done` or `Cancelled`; (translator if reason supplied) | translator failure (fatal); ambiguous target state defaults to `Backlog` | nothing if pre-transition | yes once ticket is back in active state - rerun fails the state check |

---

## Per-phase failure detail

### `plan` (`PlanPhase`)

- **Wrong state:** returns `Success=false` with reason "ticket not in Backlog state" at [src/ThroughlineBuild.Phases/PlanPhase.cs:59-60](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L59-L60). CLI exit 1.
- **`git rev-parse` failure:** "git rev-parse failed: ..." at [src/ThroughlineBuild.Phases/PlanPhase.cs:65-69](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L65-L69). Indicates the worktree's `main` ref can't be resolved.
- **Worker failure:** worker `Status != Ok` returns reason from the envelope ([src/ThroughlineBuild.Phases/PlanPhase.cs:98-100](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L98-L100)).
- **Missing metadata keys:** "worker metadata missing required keys (...)" at [src/ThroughlineBuild.Phases/PlanPhase.cs:111-114](../../src/ThroughlineBuild.Phases/PlanPhase.cs#L111-L114).
- **Idempotency caveat:** if a prior run posted the description but failed before posting the marker comment, rerunning will append the description a second time. The description-append is `existing + html` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:277-278](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L277-L278)) so duplication is visible.

### `implement` (`ImplementPhase`)

- **Wrong state:** writes `phase-status.json` via `EarlyExitManifest` and returns early ([src/ThroughlineBuild.Phases/ImplementPhase.cs:57-69](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L57-L69)).
- **Worktree creation fails:** returns with reason from `WorktreeCreateResult.FailureReason` ([src/ThroughlineBuild.Phases/ImplementPhase.cs:128-134](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L128-L134)). Common cause: branch already exists.
- **Worker fails after worktree created:** ticket transitions to `InProgress`, then worker fails. Ticket stays `InProgress`. Rerun goes through the rework path (state must be `InProgress` for rework).
- **Missing `commit_sha`:** "worker metadata missing commit_sha" at [src/ThroughlineBuild.Phases/ImplementPhase.cs:186-188](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L186-L188).
- **`commit_sha` mismatch with actual HEAD:** the actual HEAD SHA wins ([src/ThroughlineBuild.Phases/ImplementPhase.cs:190-196](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L190-L196)). Informational only.
- **Drift warning:** emitted as `GateFailure` but does not block ([src/ThroughlineBuild.Phases/ImplementPhase.cs:104-112](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L104-L112)).

### `review` (`ReviewPhase`)

- **Wrong state:** "ticket not in InReview state" ([src/ThroughlineBuild.Phases/ReviewPhase.cs:66-68](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L66-L68)).
- **Worktree not found:** "feature worktree not found at ..." ([src/ThroughlineBuild.Phases/ReviewPhase.cs:90-92](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L90-L92)). Common cause: someone renamed the worktree directory.
- **No `[implemented_at]` marker:** "no implemented_at marker found - ..." ([src/ThroughlineBuild.Phases/ReviewPhase.cs:126-128](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L126-L128)). Indicates the implement phase did not finish writing.
- **Check timeout:** the check is marked failed; verifier sees it in the brief. The phase itself does not abort.
- **Verifier subprocess crash:** propagates as phase infra failure, CLI exit 4 ([src/ThroughlineBuild.Cli/Program.cs:1095-1103](../../src/ThroughlineBuild.Cli/Program.cs#L1095-L1103)).
- **Verdict `Pass`:** no transition; ticket stays `InReview` for `ship` to pick up.
- **Verdict `Rework`:** transitions back to `InProgress` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:201-206](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L201-L206)).
- **Verdict `Fail`:** no transition; ticket stays `InReview`. Operator decides next move.

### `ship` (`ShipPhase`)

By stage:

| Stage | Trigger | Recovery |
|---|---|---|
| `StateCheck` | ticket not `InReview`; worktree not found | fix state via `review` or `implement` rerun; recreate worktree manually if missing |
| `PreFlight` | build.exe running from inside the worktree; either worktree has dirty tracked changes | move binary; commit or stash; rerun |
| `Fetch` | `git fetch <remote>` failed; diverged bases (neither local main nor `origin/main` is an ancestor) | resolve manually; rerun |
| `Rebase` | rebase conflicts; rebase fails for another reason. Rebase is aborted by `RebaseAbortAsync` | resolve conflicts on the feature branch manually; rerun |
| `ConflictMarkerScan` | leftover `<<<<<<<` / `=======` / `>>>>>>>` in committed files | clean them up; recommit; rerun |
| `RegressionChecks` | a `CheckSpec` returned non-zero or timed out | fix the failure on the feature branch; rerun |
| `FastForwardMerge` | rare - usually means a race | refetch; rerun |
| `Decruft` | post-merge worktree cleanup failed | merge already landed; CLI returns exit 0 ([src/ThroughlineBuild.Cli/Program.cs:893](../../src/ThroughlineBuild.Cli/Program.cs#L893)); clean up manually |

**Failed ship leaves the worktree on disk by design** so the operator can inspect. The feature branch is also preserved on failure.

**Once `Done` transition lands, decruft and branch-delete failures are non-fatal** ([src/ThroughlineBuild.Phases/ShipPhase.cs:337-371](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L337-L371)) - the merge is the load-bearing operation.

### `chain` (`ChainPhase`)

Wraps the others. Exit codes are remapped from `ChainOutcome`:

| Outcome | Exit | Meaning |
|---|---|---|
| `Completed` | 0 | shipped |
| `RefusedInitialState` | 2 | ticket state not `Backlog` / `Ready` / `InReview` |
| `StoppedAtPlan` | 3 | planning failed |
| `StoppedAtImplement` | 4 | implementation failed before review |
| `StoppedAtReview` | 5 | review returned `Fail` |
| `ReworkCapExceeded` | 6 | review returned `Rework` more than `MaxReworkRounds` (2) ([src/ThroughlineBuild.Phases/ChainPhase.cs:11](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L11)) |
| `StoppedAtShip` | 7 | ship gate failed |

The chain emits `ChainStart` and `ChainEnd` events bracketing every run, and `ReworkRound` events for each rework iteration.

### `rework` (`ReworkPhase`)

- **Wrong state:** `TicketNotInProgress` (exit 2). Ticket must be `InProgress` ([src/ThroughlineBuild.Phases/ReworkPhase.cs:62-70](../../src/ThroughlineBuild.Phases/ReworkPhase.cs#L62-L70)).
- **No feedback available:** `NoFeedbackAvailable` (exit 3). Either supply `--feedback "..."` or run a `review` first to produce a `Rework` verdict in the event log.
- **Implement fails:** `ImplementFailed` (exit 4).

### `new` (`NewPhase`)

- **Body file missing / unreadable:** `NewPhaseValidationException` with the IO error ([src/ThroughlineBuild.Phases/NewPhase.cs:64-75](../../src/ThroughlineBuild.Phases/NewPhase.cs#L64-L75)).
- **Title missing:** `NewPhaseValidationException` "No title found: ...".
- **Body empty:** `NewPhaseValidationException` "Body is empty".
- **Non-fatal warnings** (missing Acceptance / Out of scope / Type, body < 200 chars) are collected and surfaced in the CLI output but do not block creation.

Draft-mode failure paths add: draft worker non-Ok, missing `body_markdown` metadata, missing required sections.

### `scaffold` (`ScaffoldPhase`)

- **Parse errors** (hard, e.g., missing H1 `# Operation: <slug>`): abort, `EXIT:ValidationError` (exit 2).
- **Validation errors:** abort, `EXIT:ValidationError`.
- **Warnings without `--accept-warnings`:** abort with `WasBlockedByWarnings = true`.
- **Per-plan or per-brief create failures:** collected in `ScaffoldFailure[]` and processing continues. Result is `EXIT:PartialCreation` (exit 3) if anything was created, otherwise generic exit 1.
- **`--dry-run`** previews counts without API calls.
- **Not idempotent.** Reruns create duplicates because there is no content-match against existing Plane tickets.

### `amend` / `close` / `defer` / `reopen`

Each rejects terminal state (or non-terminal for `reopen`) up front. `close` / `defer` / `reopen` require Anthropic API key for `ReasonTranslator`; missing key fails at wire-up time with exit 3.

`close` / `defer` produce **soft failures** for parent rollup and worktree decruft - the primary state transition and comment land first; rollup / decruft errors are reported to stderr but do not change the exit code.

---

## Cross-cutting failure modes

### Plane unreachable

- `HttpRequestException` propagates from `PlaneTicketingClient`. Polly retries 3 times on 429/5xx with exponential backoff; other status codes throw `PlaneApiException` immediately. The CLI catches typed exceptions where it can; otherwise exit 1 with an unhandled exception trace.

### Anthropic rate-limited

- Polly retries in `AnthropicClient`. Failures propagate as `AnthropicApiException`. Only `close`/`defer`/`reopen` are affected directly; worker subprocess rate limits come through the `claude` CLI's own error envelope.

### Claude Code worker hangs

- `WorkerOptions.Timeout` (default 30 min via `workers.timeout_minutes`) triggers a `CancellationTokenSource.CancelAfter`. The process tree is killed via `Process.Kill(entireProcessTree: true)` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:107](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L107)). Partial output is captured to `.build/sessions/<stem>/` when `--debug`. Phase returns `Status.Failed`, CLI exit 1.

### Ctrl-C

- Every verb installs `Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };` early. The active phase observes cancellation, kills worker subprocesses, writes a `cancel-reason.txt` if in debug mode, and exits 1.

### Disk full

- Event log writes fail; `JsonlEventSink.EmitAsync` throws. The phase observes the exception and surfaces as phase failure (exit 1 or 4 depending on path).

### Process tree kill fails (Windows / unusual platform)

- `Process.Kill(entireProcessTree: true)` may not work on some platforms. The exception is swallowed ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs)). Stale subprocess can leak; operator needs to clean up manually.

---

## Idempotency posture summary

`build`'s rerun safety is **state-driven**: each phase enforces the precondition on the ticket state. If a phase succeeded in a prior run and transitioned the ticket, rerunning fails the state check at exit 1.

If a phase failed before transitioning, rerunning is safe and replays the brief. The Plane side may accumulate duplicate comments or description appendages if a phase died between writes; the markers `[planned_at]` / `[implemented_at]` / `[shipped_at]` are not de-duplicated.

The most expensive non-idempotent verb is `scaffold` - duplicate plan/brief tickets must be cleaned up by hand in Plane.

---

## Loose ends

- **No transactional Plane writes.** A phase interrupted between description-append and label-application leaves a partial write visible.
- **No rollback verb.** There is no `build undo` to revert a ticket to a prior state.
- **`scaffold` idempotency** is the biggest sharp edge - operators must use `--dry-run` and `--validate-only` to avoid duplicates.
- **Decruft failure during ship is silent in exit code** (exit 0) but visible on stderr - scripted callers might miss it.
- **`Status.Escalate` from a worker** is checked at parse time ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:279-283](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L279-L283)) but no phase code today treats `Escalate` differently from `Failed`. Architecture Section 10 contemplates routing this through the verifier; not yet wired.
- **Ctrl-C in the middle of a Plane write** can leave the ticket in a half-updated state because each HTTP call is its own atomic unit but a phase makes several.
