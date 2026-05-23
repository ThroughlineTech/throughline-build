# Operation: build-ticket-state-commands

Add four ticket-level commands to the `build` binary: `amend`, `close`, `defer`, `reopen`. These operate outside the workflow state machine (the plan -> implement -> review -> ship spine) but are necessary for cutover. The op-doc also adds the shared foundations they need: a rollup-parent method on the ticketing client, worktree decruft for terminal transitions, and the first judgment-slot use of `AnthropicClient` for reason translation.

## Why this exists

The lifecycle phases advance tickets through the workflow state machine. The `/ticket-*` surface also includes commands that operate OUTSIDE that machine:

- `amend`: modify a ticket's size label or append a context note without changing state
- `close`: terminate a ticket as wontfix (won't be done)
- `defer`: terminate a ticket as deferred (not now, may revisit)
- `reopen`: pull a terminated ticket back into the active set

These four are priority-1 per the broader cutover plan. Without them, the new binary cannot fully replace the slash-command workflow - users still need the old `/ticket-amend`, `/ticket-close`, `/ticket-defer`, `/ticket-reopen` for any ticket that needs management outside the happy path.

The four commands share a thin set of foundations:

- **Rollup-parent**: close and defer transition tickets to Cancelled. If the ticket has a parent, the parent's rollup state may need updating (fail-soft). PlaneTicketingClient lacks this method today.
- **Worktree decruft**: close and defer terminate the ticket; if there's a worktree (created by a future implement phase) it gets removed, along with any `.preview.pid` / `.preview.meta` state.
- **Reason translation**: close, defer, and reopen accept a free-text reason. The reason is posted to Plane in a comment with a load-bearing prefix (`wontfix:`, `deferred:`, `reopened:`). The prior system normalizes non-English reasons to English for parsing stability downstream. This is the first real judgment-slot use of `AnthropicClient` in the codebase.

**Architectural commitment**: these are NOT phases in the workflow-state-machine sense. `PlanPhase` advances state through the workflow; the new commands either don't change state (amend), or move out of the workflow (close, defer), or move back in (reopen). They live alongside the phases at the CLI surface (`build amend TLB-X`, `build close TLB-X "reason"`) but implement a different interface, `ITicketCommand`, distinct from the future `IWorkflowPhase`. Both compose the same lower-level abstractions (`ITicketing`, `IGitClient`, `IEventSink`).

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Shared foundations | - | M |
| B    | Ticket state commands | A | M |

## Plan A: Shared foundations

### Goal

Add the four shared foundations consumed by Plan B's commands: rollup-parent on the ticketing interface, worktree management on the git client, a worktree decrufter helper, and a reason translator wrapping `AnthropicClient` as the first judgment-slot use in the codebase.

Brief sequence: B01 and B02 are independent (different files, different abstractions); both must land before B03 (decrufter consumes the git client extension). B04 is independent of the others.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | rollup-parent | Add `RollupParentAsync` to `ITicketing` + `PlaneTicketingClient` | - | src/ThroughlineBuild.Contracts/Interfaces/ITicketing.cs, src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, tests/ThroughlineBuild.Plane.Tests/RollupParentTests.cs |
| 02 | git-worktree-ops | Extend `IGitClient` with worktree list/remove operations | - | src/ThroughlineBuild.Contracts/Interfaces/IGitClient.cs, src/ThroughlineBuild.Git/ProcessGitClient.cs, tests/ThroughlineBuild.Git.Tests/WorktreeOpsTests.cs |
| 03 | worktree-decrufter | Build `WorktreeDecrufter` helper | 02 | src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs, tests/ThroughlineBuild.Helpers.Tests/WorktreeDecrufterTests.cs |
| 04 | reason-translator | Build `ReasonTranslator` judgment slot wrapping `AnthropicClient` | - | src/ThroughlineBuild.JudgmentSlots/ReasonTranslator.cs, src/ThroughlineBuild.JudgmentSlots/JudgmentSlotsJsonContext.cs, tests/ThroughlineBuild.JudgmentSlots.Tests/ReasonTranslatorTests.cs |

### Briefs - detail

#### Brief 01: rollup-parent

Goal: Add a `RollupParentAsync(string ticketId, CancellationToken ct)` method to `ITicketing` and implement it in `PlaneTicketingClient`. The method asks Plane to evaluate whether the given ticket's parent should auto-transition based on its children's states, and applies the transition if Plane's logic says so. Fail-soft semantics: any error is logged and swallowed, never propagated.

Inputs:
- The ticket ID whose parent should be rolled up
- The existing `PlaneTicketingClient` HTTP helper infrastructure

Outputs:
- `ITicketing.RollupParentAsync` interface method
- `PlaneTicketingClient.RollupParentAsync` implementation that calls the appropriate Plane endpoint
- Unit tests covering the happy path, the no-parent case, and the API-error case
- Return type: `Task<RollupResult>` where `RollupResult` is a small record `(bool ParentTransitioned, string? NewParentState, string? FailureReason)`

Acceptance:
- [ ] `ITicketing` has the new method signature
- [ ] `PlaneTicketingClient` implements it and returns a `RollupResult`
- [ ] On Plane API error, the method returns `RollupResult(false, null, "<error>")`, never throws
- [ ] On no-parent (ticket has no parent), the method returns `RollupResult(false, null, null)` (not an error, just a no-op)
- [ ] xUnit tests cover the three paths above using a mocked HTTP layer

Notes: The exact Plane endpoint for parent rollup is whichever endpoint the prior system uses; check the spec doc for `bin/plane-rest rollup-parent` for the URL shape. If Plane's API has changed since, capture the current contract from the Plane API docs rather than guessing. Do not read the prior system's source code to learn the endpoint - read Plane's documentation directly.

OOS:
- Do not read `bin/plane-rest` source from claude-config
- Do not implement parent rollup as a client-side state computation; let Plane do it
- Do not throw on rollup failure (every caller treats it as fail-soft)
- Do not add this method to any other ITicketing-style interface

#### Brief 02: git-worktree-ops

Goal: Extend `IGitClient` (the interface promoted from `PlanPhase`'s inline git usage) with methods for listing worktrees and removing them. The `ProcessGitClient` implementation wraps `git worktree list --porcelain` and `git worktree remove` via `Process`.

Inputs:
- The existing `IGitClient` interface (which currently has at least `RevParseAsync` for `origin/main`)
- Git's `worktree` subcommand documentation

Outputs:
- `IGitClient.ListWorktreesAsync(CancellationToken)` returning `IReadOnlyList<WorktreeInfo>`
- `IGitClient.RemoveWorktreeAsync(string path, bool force, CancellationToken)` returning a `WorktreeRemoveResult`
- New `WorktreeInfo` record: `(string Path, string Branch, string HeadSha, bool IsLocked, bool IsPrunable)`
- New `WorktreeRemoveResult` record: `(bool Success, string? FailureReason)`
- `ProcessGitClient` implementations that wrap `git worktree list --porcelain` (parsed line-by-line per the porcelain format) and `git worktree remove [--force] <path>`

Acceptance:
- [ ] Both methods are on the interface and implemented
- [ ] `ListWorktreesAsync` parses `git worktree list --porcelain` correctly (worktree, HEAD, branch, locked, prunable fields)
- [ ] `RemoveWorktreeAsync(path, force=false)` succeeds on a clean worktree
- [ ] `RemoveWorktreeAsync(path, force=true)` succeeds where force is required
- [ ] On `git worktree remove` failure (locked worktree without force, missing worktree, etc.) returns `WorktreeRemoveResult(false, "<reason>")`, does not throw
- [ ] xUnit tests use a temp git repo, create real worktrees, list them, remove them

Notes: The porcelain format is documented at `git help worktree`. The Windows `node_modules` reparse-point caveat (where `git worktree remove --force` follows a junction and deletes files from the main repo) is real but is handled in B03 (decrufter), not here. B02 stays close to the git CLI surface.

OOS:
- Do not handle the Windows node_modules reparse-point pre-clean here; that belongs in B03
- Do not implement worktree CREATION here; that's for the future implement phase
- Do not parse non-porcelain output formats
- Do not read claude-config's git wrappers

#### Brief 03: worktree-decrufter

Goal: Build a `WorktreeDecrufter` helper that consumes `IGitClient` and removes a ticket's worktree completely: kill any preview processes recorded in `.preview.pid`, remove `.preview.pid` and `.preview.meta`, perform the Windows `node_modules` reparse-point pre-clean, then `git worktree remove` with the fallback chain (try without force, try with force, fall back to `rm -rf` + `git worktree prune`).

Inputs:
- `IGitClient` from B02
- File-system primitives (`System.IO.File`, `System.IO.Directory`)
- Process-management primitives for killing preview PIDs

Outputs:
- `WorktreeDecrufter` class with a single `DecruftAsync(string worktreePath, CancellationToken)` method returning a `DecruftResult` with per-step outcomes
- `DecruftResult` record listing which steps succeeded, which failed, and on which step the operation halted (if any)
- xUnit tests covering: clean worktree (full success), worktree with `.preview.pid` (PIDs killed first), worktree with `node_modules` junction (pre-clean before remove), worktree where remove fails (fallback chain tried)

Acceptance:
- [ ] `.preview.pid` file (if present) is read; each PID is sent SIGTERM, then SIGKILL after a short delay (use `Process.Kill(entireProcessTree: true)` on Windows; equivalent on POSIX)
- [ ] `.preview.pid` and `.preview.meta` are removed before worktree removal
- [ ] Windows `node_modules` reparse-point pre-clean: detect junctions inside the worktree's node_modules with `Directory.GetDirectories` + `FileSystemInfo.LinkTarget`; remove them with `Directory.Delete` (or `rmdir` shellout on Windows where managed code fails) before invoking `git worktree remove`
- [ ] Fallback chain: `git worktree remove <path>` -> if fail, `git worktree remove --force <path>` -> if fail, `Directory.Delete(<path>, true)` + `git worktree prune`
- [ ] Result captures which steps ran and which (if any) failed
- [ ] xUnit tests assert the fallback chain triggers correctly using a mocked `IGitClient`

Notes: PID files are line-formatted as `<name>  <pid>  <port>` per the spec doc. A `-` in the PID column means "no PID for this component, skip"; the decrufter must skip those lines rather than trying to parse `-` as an int. The Windows junction detection is genuinely tricky; if `FileSystemInfo.LinkTarget` doesn't return a useful value on the dev machine's .NET version, use `GetFileAttributes` P/Invoke or shell out to `fsutil reparsepoint query`. Capture which approach worked in the brief's commit message for the next agent.

OOS:
- Do not implement preview process LAUNCH here; that's for the future preview phase
- Do not implement rollup-preview REBUILD here; that's for defer command in B08, and the rebuild is OOS for v1 anyway (see B08)
- Do not assume the worktree directory layout from claude-config; query what's actually there

#### Brief 04: reason-translator

Goal: Build a `ReasonTranslator` class that wraps `AnthropicClient` to normalize a free-text reason to English. This is the first judgment-slot use of `AnthropicClient` in the codebase. It validates that `AnthropicClient` works end-to-end and establishes the pattern for future judgment slots.

Inputs:
- The existing `AnthropicClient` (`ILlmClient` implementation) from op-doc 3
- The free-text reason string from the user

Outputs:
- `ReasonTranslator` class with `TranslateAsync(string reason, CancellationToken)` returning a `string` (the English translation, or the original if already English)
- A small system prompt: "Translate the following text to English if it is not already in English. If it is already in English, return it unchanged. Return only the translated text with no preamble or explanation."
- User message: just the reason string
- Use a small model: `claude-haiku-4-5-20251001` (lowest tier sufficient for translation)
- xUnit tests covering: already-English input returns unchanged (with a real-API or fixture-based test), non-English input is translated (fixture-based to avoid API-call cost during CI), API failure surfaces a clear exception

Acceptance:
- [ ] `ReasonTranslator.TranslateAsync` returns a string
- [ ] The system prompt is short, scoped, and forbids preamble
- [ ] The model is haiku (the cheapest tier)
- [ ] AnthropicClient is instantiated lazily; the secret resolution path from op-doc 4 is exercised here for the first time in a real phase
- [ ] On API failure, the exception bubbles up with a clear message (don't swallow; caller decides whether to fall back to the original reason)
- [ ] xUnit tests use a mocked `ILlmClient` to avoid hitting the real API in CI

Notes: This brief is the validation that `AnthropicClient` actually works end-to-end. If the AnthropicClient implementation from op-doc 3 turns out to be incomplete (e.g. missing system-prompt handling, broken auth), this brief surfaces it. Resist the temptation to translate the bug fixes into ReasonTranslator's body; surface them as their own follow-up tickets and have ReasonTranslator block on the fix.

OOS:
- Do not add this judgment slot to any phase other than close/defer/reopen (Plan B will consume it)
- Do not use a model larger than haiku
- Do not implement language detection separately; let the model handle "already English -> return unchanged"
- Do not store the original-language reason anywhere; English-only per spec

## Plan B: Ticket state commands

### Goal

Add four CLI subcommands to the `build` binary, each implementing a new `ITicketCommand` interface. Each command performs deterministic Plane writes and emits a structured event sequence to the existing event log. The four commands compose the Plan A foundations.

Brief sequence: B05 establishes the `ITicketCommand` abstraction and CLI dispatch; the four command implementations (B06-B09) depend on it but are independent of each other.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 05 | ticket-command-abstraction | Define `ITicketCommand` and wire CLI dispatch for the four new verbs | A | src/ThroughlineBuild.Contracts/Interfaces/ITicketCommand.cs, src/ThroughlineBuild.Cli/Program.cs |
| 06 | amend-command | Implement `AmendCommand` (size and note flags; no state change) | 05 | src/ThroughlineBuild.Commands/AmendCommand.cs, tests/ThroughlineBuild.Commands.Tests/AmendCommandTests.cs |
| 07 | close-command | Implement `CloseCommand` with `wontfix:` marker | 05 | src/ThroughlineBuild.Commands/CloseCommand.cs, tests/ThroughlineBuild.Commands.Tests/CloseCommandTests.cs |
| 08 | defer-command | Implement `DeferCommand` with `deferred:` marker | 05 | src/ThroughlineBuild.Commands/DeferCommand.cs, tests/ThroughlineBuild.Commands.Tests/DeferCommandTests.cs |
| 09 | reopen-command | Implement `ReopenCommand` with description-aware state inference | 05 | src/ThroughlineBuild.Commands/ReopenCommand.cs, tests/ThroughlineBuild.Commands.Tests/ReopenCommandTests.cs |

### Briefs - detail

#### Brief 05: ticket-command-abstraction

Goal: Define `ITicketCommand` as the parallel to (future) `IWorkflowPhase`, and wire CLI dispatch so that `build amend`, `build close`, `build defer`, `build reopen` route to the right command class with parsed args. Establish the convention that these commands share a flat CLI surface with phase verbs.

Inputs:
- The existing CLI `Program.cs` and its `System.CommandLine` (or equivalent) wiring
- The existing `IEventSink`, `ITicketing`, `IGitClient` injection patterns used by `PlanPhase`

Outputs:
- `ITicketCommand` interface in `ThroughlineBuild.Contracts.Interfaces`. Shape: `Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)` where `CommandResult` is a small record `(bool Success, string? Message)` and `TicketCommandContext` carries the ticket ID and any command-specific args
- A `TicketCommandRegistry` or equivalent that maps verb strings to command class instances (constructor-injected with their dependencies)
- CLI dispatch updates in `Program.cs` so that the four new verbs are recognized and routed
- Top-level CLI help text updated to list the new verbs alongside the existing `plan` verb

Acceptance:
- [ ] `ITicketCommand` interface exists
- [ ] `build amend TLB-X --size m` (or equivalent flag combo) parses successfully and dispatches to AmendCommand
- [ ] `build close TLB-X "reason here"` parses successfully and dispatches to CloseCommand
- [ ] Same for defer and reopen
- [ ] Help text from `build --help` shows all four verbs with one-line descriptions
- [ ] Invalid verb prints help and exits non-zero
- [ ] xUnit tests cover the dispatch routing using mock command implementations

Notes: The CLI surface is flat: `build <verb> <id> [args]`. Do not nest under a `build ticket <verb>` group; the verbs are distinct enough that grouping adds noise. Phase verbs and ticket-command verbs coexist at the same level. If `System.CommandLine` makes flat dispatch awkward, work around it; do not change the user-facing surface to accommodate the framework.

OOS:
- Do not retrofit `PlanPhase` to implement `ITicketCommand`; phases stay distinct
- Do not add other verbs (decompose, op-scaffold, etc.) here; future op-docs cover them
- Do not change the existing `plan` verb's wiring

#### Brief 06: amend-command

Goal: Implement `AmendCommand` per the spec. Two independent edits: `--size {S|M|L}` swaps the size label; `--note "..."` (or trailing free text) appends a `Context Note` block to the description. At least one flag is required; both can be combined.

Inputs:
- `ITicketing` (for `GetByIdAsync`, `ApplyLabelsAsync`, `AppendDescriptionAsync`)
- `TicketCommandContext` from B05 carrying parsed args
- `IEventSink` for emitting events

Outputs:
- `AmendCommand` class implementing `ITicketCommand`
- Behavior matches spec section "/ticket-amend":
  - Validate not terminal; if terminal, return `CommandResult(false, "Cancelled or Done; reopen first")`
  - At least one of --size or --note required; if neither, return usage error
  - If --size: read current labels, strip prior `size:*`, add new size, PATCH labels (single call). Use the union pattern that op-04's known follow-up will eventually formalize; for now implement it inline in AmendCommand
  - If --note: build the `<hr/><h3>Context Note</h3><p><em>Added YYYY-MM-DD</em></p><p>NOTE</p>` block, call `AppendDescriptionAsync`
  - Both: size first, then note
- Event emissions:
  - One `WorkerSpawn`-equivalent? No; AmendCommand has no worker. Emit a new event or reuse existing: probably emit `TicketWrite` events for each PATCH (one for labels, one for description append)
  - Final `StateTransition` event is omitted because state doesn't change
- xUnit tests covering: size-only, note-only, both, terminal-rejected, missing-flags-rejected, size-label-not-in-project (warn + skip path)

Acceptance:
- [ ] `build amend TLB-X --size m` swaps the size label
- [ ] `build amend TLB-X --note "context"` appends the context note
- [ ] `build amend TLB-X --size l --note "rationale"` does both
- [ ] `build amend TLB-X` (no flags) prints usage and exits non-zero
- [ ] `build amend <terminal-ticket-id>` returns the "reopen first" message and exits non-zero
- [ ] Existing non-risk-non-size labels are preserved (test fixture)
- [ ] Event log captures one `TicketWrite` event per Plane PATCH

Notes: The size-label union pattern is the same shape as op-04's label-preservation follow-up. Implement it cleanly in AmendCommand; if a future op-doc generalizes the union into a helper, AmendCommand can be refactored to use it. The Context Note's date format is local-date `YYYY-MM-DD` per spec.

OOS:
- Do not invoke `ReasonTranslator` (amend's --note is appended verbatim, NOT translated)
- Do not transition state under any circumstance
- Do not implement the rollup-parent call (state doesn't change)
- Do not invoke `WorktreeDecrufter` (amend doesn't terminate)
- Do not read `commands/ticket-amend.md` from claude-config

#### Brief 07: close-command

Goal: Implement `CloseCommand` per the spec. Posts a `wontfix:` comment with translated reason, transitions to Cancelled, attempts parent rollup (fail-soft), runs worktree decruft. Reason is required.

Inputs:
- `ITicketing`, `IEventSink`, `IGitClient`
- `ReasonTranslator` from B04
- `WorktreeDecrufter` from B03
- `TicketCommandContext` carrying ticket ID and reason

Outputs:
- `CloseCommand` class implementing `ITicketCommand`
- Behavior:
  - Validate not terminal; if terminal, return appropriate `CommandResult`
  - Reason required; if missing, usage error
  - Check for unmerged commits on `ticket/<lowered-id>-*` branches; if any, prompt for confirmation (in interactive mode) or proceed (in non-interactive mode, log a warning)
  - Translate reason via `ReasonTranslator`
  - Post comment with body `<p><strong>wontfix:</strong> {translated}</p>` via `CreateCommentAsync`
  - Transition state to Cancelled via `TransitionStateAsync`
  - Call `RollupParentAsync` (swallow failure, log it)
  - Run `WorktreeDecrufter` against `.worktrees/ticket-<lowered-id>/` if it exists
- Event sequence: TicketWrite (comment), StateTransition (Cancelled), TicketWrite (rollup-attempt; capture result in Data), optional decruft event

Acceptance:
- [ ] `build close TLB-X "duplicate of TLB-9"` posts a wontfix comment, transitions to Cancelled
- [ ] `build close TLB-X` (no reason) prints usage and exits non-zero
- [ ] `build close <terminal-id>` returns "already terminal" and exits non-zero
- [ ] Unmerged-commits warning fires when applicable
- [ ] Rollup-parent failure logs a warning but does NOT unwind the Cancelled transition
- [ ] Worktree decruft runs if a worktree exists; no-op if not
- [ ] Event log captures the full sequence

Notes: The `wontfix:` comment-prefix is load-bearing. `/ticket-status` and `/ticket-reopen` (B09) parse it. Do not change the prefix string. The English-only constraint on the reason is deliberate; translation happens once at close time, the original-language version is not preserved.

OOS:
- Do not delete the feature branch automatically; leave it for the user (per spec)
- Do not transition the ticket to Done (that's only `/ship`'s job)
- Do not implement rollup-preview rebuild here (close doesn't rebuild; only defer does, and even that is OOS for v1 per B08)
- Do not read claude-config's `/ticket-close` source

#### Brief 08: defer-command

Goal: Implement `DeferCommand`. Near-identical to `CloseCommand` but uses the `deferred:` marker. The spec mentions defer also "rebuilds the rollup preview if this ticket was part of one" - this is OOS for v1 (the preview / rollup-preview subsystems don't exist yet in throughline-build) and is captured as a follow-up.

Inputs:
- Same as CloseCommand

Outputs:
- `DeferCommand` class implementing `ITicketCommand`
- Behavior is identical to CloseCommand with two changes:
  - Comment body is `<p><strong>deferred:</strong> {translated}</p>` instead of `wontfix:`
  - Mid-implementation branch warning text differs (per spec, defer warns about leaving the branch for potential reopen)
- Event sequence: same as close
- Add a follow-up TODO in code comments referencing the rollup-preview rebuild gap; do NOT implement it
- xUnit tests covering the same paths as close, plus: confirms the marker is `deferred:` and not `wontfix:`

Acceptance:
- [ ] `build defer TLB-X "blocked on legal review"` posts a deferred comment, transitions to Cancelled
- [ ] All the same failure modes as close (no reason, terminal, etc.) behave correctly
- [ ] The comment marker is literally `deferred:` (load-bearing)
- [ ] Code comment notes the rollup-preview rebuild gap for v1.1
- [ ] xUnit tests share fixtures with close where possible to reduce drift

Notes: Defer and close are near-duplicates by design. Resist the urge to extract a `TerminalCommandBase` class for them right now; the abstraction is premature until reopen's inverse logic settles. After B09 lands, a future refactor can pull out shared structure if it's worth it. For now, two near-identical command classes is fine.

OOS:
- Do not implement rollup-preview rebuild (capture as TODO comment; reference future op-doc)
- Do not extract a shared base class between close and defer for v1
- Do not change the `deferred:` marker text

#### Brief 09: reopen-command

Goal: Implement `ReopenCommand`. Inverse of close/defer: transitions a terminal ticket back to an active state, with the destination state inferred from the description's content and the most recent terminal comment's marker prefix.

Inputs:
- `ITicketing`, `IEventSink`
- `ReasonTranslator` from B04
- `TicketCommandContext`

Outputs:
- `ReopenCommand` class
- Behavior per spec:
  - Validate state IS terminal (Done or Cancelled); if active, error "already active"
  - Optional reason (defaults to "reopened on {date}" if not given)
  - Translate reason (or use the default)
  - Determine prior-state classification by scanning recent comments for `deferred:` or `wontfix:` prefix
  - Determine new active state:
    - From Done -> Backlog
    - From Cancelled with `deferred:` + description contains `<h3>Implementation Plan</h3>` -> Ready
    - From Cancelled with `deferred:` but no plan -> Backlog
    - From Cancelled with `wontfix:` -> Backlog
    - Default (doubt) -> Backlog
  - Post `<p><strong>reopened:</strong> from {prior} - {translated}</p>` comment (`reopened:` marker is load-bearing)
  - Transition state to inferred destination
- Event sequence: TicketWrite (comment), StateTransition (to Backlog or Ready)
- xUnit tests covering each of the five state-transition branches

Acceptance:
- [ ] Reopening a Done ticket transitions to Backlog
- [ ] Reopening a Cancelled-with-deferred ticket WITH plan transitions to Ready
- [ ] Reopening a Cancelled-with-deferred ticket WITHOUT plan transitions to Backlog
- [ ] Reopening a Cancelled-with-wontfix ticket transitions to Backlog
- [ ] When in doubt (no parseable marker), transitions to Backlog
- [ ] `reopened:` marker is literally that string
- [ ] Reopening an active ticket returns "already active" and exits non-zero
- [ ] Description content is NEVER modified or deleted (audit trail preserved)

Notes: The `<h3>Implementation Plan</h3>` marker is what `/ticket-investigate` writes. Throughline Build's `plan` phase needs to write the same marker (it currently writes the plan as appended description; verify the heading format matches). If it doesn't, that's a known-gap follow-up to capture for op-03's revision, not a blocker for B09.

OOS:
- Do not delete or modify the existing description on reopen
- Do not touch feature branches or worktrees (user cleanup per spec)
- Do not invoke `WorktreeDecrufter` (reopen doesn't decruft)
- Do not invoke `RollupParentAsync` (reopen doesn't roll up; the state machine goes inward)

## What done looks like

After op-06 lands, the four new verbs work on real tickets:

```
$ build amend TLB-42 --size l --note "scope expanded after architecture review"
TLB-42 AMENDED
  size: m -> l
  note: appended (2026-05-23)

$ build close TLB-99 "duplicate of TLB-87"
TLB-99 CLOSED (wontfix)
  state: Backlog -> Cancelled
  reason: duplicate of TLB-87
  Next: /ticket-reopen if needed

$ build defer TLB-101 "blocked on Q3 legal review"
TLB-101 DEFERRED
  state: Ready -> Cancelled
  reason: blocked on Q3 legal review

$ build reopen TLB-99 "turned out to be distinct from TLB-87"
TLB-99 REOPENED
  state: Cancelled -> Backlog (from wontfix)
```

Event logs from each invocation include the full structured sequence: comment writes, state transitions, optional rollup-parent attempts, optional decruft events. The `LlmCall` event from op-05 fires on close, defer, and reopen (since they invoke the ReasonTranslator judgment slot) but is skipped on amend (no LLM contact).

After op-06, the cutover surface for the four most common non-phase commands is closed. The remaining gaps for full cutover are:

- `/ticket-decompose` - LLM-agentic ticket-splitter; its own op-doc
- `/op-scaffold` - introduces the operation concept; needs architecture-doc revision plus its own op-doc
- `/op-run` aspirational revival - depends on what scope is chosen
- Lower-priority utility commands (`/ticket-list`, `/ticket-status`, `/ticket-preview`, `/ticket-feature-land`, `/ticket-reopen` - the last already covered here)

The user can now run `build amend`, `build close`, `build defer`, `build reopen` against any throughline-build TLB ticket instead of the prior slash commands. Comparison against the prior commands (token cost, wall-clock, output structure) becomes feasible for each, using the same comparison method established in op-05.