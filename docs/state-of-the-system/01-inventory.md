# 01 - Inventory

Every command, library project, tool, script, and CI workflow currently in the repository, with a one-paragraph high-level description, inputs, outputs, and the major components it composes with. Status tags follow the convention defined in the index: Functional, Partial, Legacy, Aspirational, Broken.

For interface contracts see [07-contracts.md](07-contracts.md). For phase orchestration detail see [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md). For the multi-vendor model/worker layout see [11-llm-architecture.md](11-llm-architecture.md).

---

## CLI verbs (the `build` binary)

All verbs are dispatched from [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs) (single top-level entry point, ~1654 lines). Usage text lives in [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs); there is no separate help/ directory or embedded help resource - the usage string is the only help text. Verb dispatch is a chain of `if (verb == ...)` blocks rather than a registry.

Three pre-passes run before verb dispatch: bare bool flags `--debug`, `--quiet`, `--summary-json`, `--error-location`, `--no-auto-resolve`, `--no-auto-merge`, `--continue-past-failure` are stripped first ([src/ThroughlineBuild.Cli/Program.cs:34-61](../../src/ThroughlineBuild.Cli/Program.cs#L34-L61)); then `--agent` / `--agent-plan` / `--agent-implement` / `--agent-review` pairs are extracted ([src/ThroughlineBuild.Cli/CliArgParser.cs:24-65](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L24-L65)); then ticket IDs for phase verbs are extracted ([src/ThroughlineBuild.Cli/CliArgParser.cs:94-118](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L94-L118)).

Seventeen verbs are reachable: `init`, `settarget`, `plan`, `implement`, `review`, `ship`, `chain`, `rework`, `decompose`, `new`, `scaffold`, `list`, `amend`, `close`, `defer`, `reopen`, and `--help`/`help`. Any other token returns exit 2 (unknown subcommand). `init` and `settarget` both dispatch before config load (they edit the config itself).

### `build init [--force --print-template --plane-url --workspace --project-id --token|--token-env]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:129-144](../../src/ThroughlineBuild.Cli/Program.cs#L129-L144), implemented in [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs). Bootstraps `.build/config.toml` from the embedded template before any config is loaded (it runs ahead of config discovery). Not an `ITicketCommand`.

- **Inputs:** flag values that replace `REQUIRED_*` placeholders in the template ([src/ThroughlineBuild.Cli/InitCommand.cs:64-101](../../src/ThroughlineBuild.Cli/InitCommand.cs#L64-L101)); `--token-env VAR` rewrites the literal `plane_api_token = "..."` line to `plane_api_token_env = "VAR"` and takes precedence over `--token`. Template loaded by `ConfigTemplateLoader` from embedded resources.
- **Side effects:** with `--print-template` writes the rendered template to stdout and exits 0 (no file). Otherwise creates `<cwd>/.build/config.toml` (UTF-8); refuses to overwrite an existing file without `--force` ([src/ThroughlineBuild.Cli/InitCommand.cs:46-50](../../src/ThroughlineBuild.Cli/InitCommand.cs#L46-L50)).
- **Exits:** 0 success or print, 1 file already exists without `--force`.

### `build settarget [<branch> | --unset]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:149-158](../../src/ThroughlineBuild.Cli/Program.cs#L149-L158), implemented in [src/ThroughlineBuild.Cli/SetTargetCommand.cs](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs). Manages the `[work].target_branch` key in `.build/config.toml`. Like `init`, it runs ahead of config discovery (it edits the config). Not an `ITicketCommand`. Three modes ([src/ThroughlineBuild.Cli/SetTargetCommand.cs:31-54](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs#L31-L54)):

1. **Set** (`build settarget <branch>`) - validates the branch exists as a local git ref (`git rev-parse --verify refs/heads/<branch>`, [src/ThroughlineBuild.Cli/SetTargetCommand.cs:200-223](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs#L200-L223)) then line-edits `target_branch = "<branch>"` into the `[work]` section.
2. **Unset** (`build settarget --unset`) - removes the `target_branch` key from `[work]`; no-op if already absent.
3. **Display** (`build settarget`, no args) - prints the resolved target branch with its source label (`[work]` override, or the `[ship].base_branch` default when unset).

- **Side effects:** rewrites `.build/config.toml` via line-edit (TOML is not parsed-and-reserialized, so comments and formatting are preserved - [src/ThroughlineBuild.Cli/SetTargetCommand.cs:234-289](../../src/ThroughlineBuild.Cli/SetTargetCommand.cs#L234-L289)).
- **Exits:** 0 success, 2 config not found or branch not found locally.
- **Consumed by:** `ship` and the implement/chain phases via `BuildConfig.ResolveTargetBranch()` ([src/ThroughlineBuild.Cli/Config.cs:58](../../src/ThroughlineBuild.Cli/Config.cs#L58)); see [04-configuration.md](04-configuration.md) and [10-lifecycle-orchestration.md](10-lifecycle-orchestration.md).

### `build plan <ticket-id> [ticket-id ...]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:858-913](../../src/ThroughlineBuild.Cli/Program.cs#L858-L913). Investigates a `Backlog` ticket and produces a plan written to the ticket description, plus risk/size labels and a `[planned_at: <sha>]` marker comment. Loops over every positional ticket ID (multi-ticket dispatch is sequential, stops at first failure - [src/ThroughlineBuild.Cli/Program.cs:807](../../src/ThroughlineBuild.Cli/Program.cs#L807)).

- **Inputs:** one or more ticket ids (positional); `--debug | --quiet`, `--summary-json`, `--error-location`, `--agent <name>`. Reads `.build/config.toml`, Plane ticket via API, current main SHA via git, top-level directory entries of cwd.
- **Side effects:** spawns the configured worker as a subprocess in the main worktree (no branch cut), writes Plane HTML description + size/risk labels + one comment, appends events to `.build/events/<stem>.jsonl`, optionally captures worker stdio to `.build/sessions/<stem>/` under `--debug`. Writes a deterministic completion summary to stdout (text or JSON).
- **Exits:** 0 success, 1 phase failure, 2 missing/unknown id, 3 missing secret, 4 infra.
- **Invokes:** `PlanPhase`, `PlanBriefBuilder`, `PlaneTicketingClient`, the agent resolved via `EffectiveAgentFor("plan")`, `JsonlEventSink`, `PhaseSummaryBuilder.BuildPlan`.

### `build implement <ticket-id> [ticket-id ...]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:914-983](../../src/ThroughlineBuild.Cli/Program.cs#L914-L983). Cuts a worktree, transitions `Ready -> InProgress`, dispatches the implementer worker, records `[implemented_at: <sha>]`, transitions `InProgress -> InReview`. Same multi-ticket sequential loop, flags, and exit codes as `plan`.

- **Side effects:** `git worktree add` for the ticket branch, runs worker inside it, writes events and a Plane comment, transitions state. The implement summary best-effort attaches diff stats and recent commit onelines via the shared read-only git client.
- **Invokes:** `ImplementPhase`, `ImplementBriefBuilder`, `ProcessGitClient`, agent via `EffectiveAgentFor("implement")`, `PhaseSummaryBuilder.BuildImplement`.

### `build review <ticket-id> [ticket-id ...]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:984-1041](../../src/ThroughlineBuild.Cli/Program.cs#L984-L1041). Runs configured automated checks against the feature branch, dispatches a verifier worker, records `Verdict { Pass | Rework | Fail }`. On `Rework` transitions `InReview -> InProgress`. Multi-ticket dispatch is sequential (stops at first failure).

- **Inputs:** ticket ids; flags incl. `--agent <name>`. Verifier timeout / allowed tools come from `config.Review`.
- **Side effects:** runs each `CheckSpec` as a subprocess, spawns the verifier worker, posts one Plane comment with the verdict.
- **Exit codes:** 0 Pass, 1 Rework/Fail, 4 verifier infra failure.
- **Invokes:** `ReviewPhase`, `ReviewBriefBuilder`, `WorkerAgentReviewer` (the `IVerifier`, agent-agnostic - see Verification below), `AutomatedChecksRunner`, `PhaseSummaryBuilder.BuildReview`.

### `build ship <ticket-id> [ticket-id ...] [--no-auto-merge]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:1042-1141](../../src/ThroughlineBuild.Cli/Program.cs#L1042-L1141). Deterministic phase, no worker subprocess. Resolves the merge target (`[work].target_branch` if set, else `[ship].base_branch`, [src/ThroughlineBuild.Phases/ShipPhase.cs:225](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L225)), fetches, ancestry-checks and (on clean divergence) auto-rebases the local target branch onto `<remote>/<target>`, scans for conflict markers, runs `ship.regression_checks`, fast-forward-merges the feature branch into the target, **pushes the target to the remote**, posts `[shipped_at: <sha>]`, transitions `InReview -> Done`, then decrufts the worktree. `--no-auto-merge` is threaded into `ShipOptions` ([src/ThroughlineBuild.Cli/Program.cs:1049](../../src/ThroughlineBuild.Cli/Program.cs#L1049)).

- **Inputs:** ticket ids; `--no-auto-merge`; `--debug` is **no longer a no-op for ship** - it makes ship stream git command output and full regression-check results to stderr (verbose mode, [src/ThroughlineBuild.Phases/ShipPhase.cs:64,82-87,442-459](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L82-L87)). Ship also emits phase-level progress lines ("[ship] fetching...", "[ship] merging into <target>...") even without `--debug` ([src/ThroughlineBuild.Phases/ShipPhase.cs:80](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L80)).
- **Preflight guard:** when the target branch is non-default (`!= base_branch`), ship first checks the main worktree is on that branch; otherwise it emits a `wrong_worktree_branch` `GateFailure` and returns `PreFlight` failure rather than fast-forwarding the wrong ref ([src/ThroughlineBuild.Phases/ShipPhase.cs:227-247](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L227-L247)).
- **Worktree resolution:** ship matches the feature worktree by `ticket/<id>-` prefix; if none is registered it falls back to creating one from a matching local branch ([src/ThroughlineBuild.Phases/ShipPhase.cs:122-169](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L122-L169)).
- **Side effects:** mutates and pushes the target branch in the main worktree (FF merge then `git push`), removes the feature worktree, optionally deletes the feature branch.
- **Exit codes:** mapped from `ShipFailureStage` at [src/ThroughlineBuild.Cli/Program.cs:1129-1139](../../src/ThroughlineBuild.Cli/Program.cs#L1129-L1139): 0 success or post-success decruft warning; 1 rebase / conflict-marker / regression / preflight gate; 4 state-check / fetch / FF-merge infra.
- **Invokes:** `ShipPhase`, `ProcessGitClient`, `BaseRefResolver` (now target-aware, [src/ThroughlineBuild.Git/BaseRefResolver.cs:13-25](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L13-L25)), `AutomatedChecksRunner`, `ConflictMarkerScanner`, `WorktreeDecrufter`, `PhaseSummaryBuilder.BuildShip`.

### `build chain <ticket-id> [ticket-id ...] [--no-auto-resolve --no-auto-merge --continue-past-failure]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:1142-1395](../../src/ThroughlineBuild.Cli/Program.cs#L1142-L1395). End-to-end orchestration: routes a single ticket to the appropriate starting phase by state, runs the implement-review loop with `MaxReworkRounds = 2` ([src/ThroughlineBuild.Phases/ChainPhase.cs:14](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L14)), then ships. `ChainPhase` also handles parent-ticket recursion (chains non-terminal children) and an "obsolete ticket" path via an `IObsoleteRatifier`. When recursing into a parent's children, it builds a `blocked_by` dependency graph over the eligible siblings and runs them in dependency-ordered levels (lowest-ticket-number-first within a level; dependents wait for their blockers), dispatched **serially** by `SemaphoreSlim(1, 1)` - one child at a time, even within a level (op-29 removed the prior concurrent `MaxParentChainConcurrency = 4` dispatch and the `--max-parallel`/`ForceParallel` flag because parallelism was causing issues). Non-terminal stuck children (`Planning`/`InProgress`) are resumed rather than refused.

- **Multi-ticket dispatch is implemented** (the old "rejects multi-id" behavior is gone). Two paths:
  - When extra positional IDs are supplied, the verb fetches the tickets, builds a `TicketGraph` from `blocked_by` relations within the dispatched set, and runs `ParallelDispatcher` level-synchronously with concurrency bounded by `workers.max_concurrency` ([src/ThroughlineBuild.Cli/Program.cs:1213-1289](../../src/ThroughlineBuild.Cli/Program.cs#L1213-L1289)). Prints `[TKT] Outcome (Nms)` per ticket; exit 0 if the dispatch succeeded, else 1.
  - The single-ID path goes through `ChainCommand` -> `DefaultChainRunner`; a residual sequential fallback for multiple IDs via `ChainCommand` + `SequentialChainDispatcher` also exists ([src/ThroughlineBuild.Cli/Program.cs:1310-1344](../../src/ThroughlineBuild.Cli/Program.cs#L1310-L1344)). `--continue-past-failure` (otherwise descendants of a failed ancestor are skipped) is threaded into the sequential dispatcher.
- **Flags:** `--agent`/`--agent-plan`/`--agent-implement`/`--agent-review` (per-phase override; per-phase flag beats `--agent` beats config, [src/ThroughlineBuild.Cli/Program.cs:786-790](../../src/ThroughlineBuild.Cli/Program.cs#L786-L790)), `--no-auto-resolve` (threaded into `ChainPhaseOptions`), `--no-auto-merge`, `--continue-past-failure`, `--debug`. (The `--max-parallel`/`ForceParallel` override was removed in op-29 when the parent chain became serial.)
- **Exit codes:** mapped from `ChainOutcome` at [src/ThroughlineBuild.Cli/Program.cs:1359-1374](../../src/ThroughlineBuild.Cli/Program.cs#L1359-L1374): 0 Completed / RatifiedObsolete / ParentCompleted; 2 RefusedInitialState / ParentHasGrandchildren; 3 StoppedAtPlan / ParentStoppedEarly / Skipped; 4 StoppedAtImplement; 5 StoppedAtReview; 6 ReworkCapExceeded; 7 StoppedAtShip. The full enum is in [src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs](../../src/ThroughlineBuild.Contracts/Models/ChainOutcome.cs).
- **Invokes:** `ChainPhase` (per-phase factories + `ratifierFactory` building an `ObsoleteRatifier`), `ParallelDispatcher`, `TicketGraph`, `ChainCommand`, `DefaultChainRunner`, `SequentialChainDispatcher`.

### `build rework <ticket-id> [--feedback "..."]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:1446-1514](../../src/ThroughlineBuild.Cli/Program.cs#L1446-L1514). Single ticket only (multiple positional IDs are rejected at [src/ThroughlineBuild.Cli/Program.cs:90-94](../../src/ThroughlineBuild.Cli/Program.cs#L90-L94)). Re-implements a ticket whose last `Verdict` was `Rework`. Retrieves the most recent `Rework` verdict from the JSONL event log via `ReviewFeedbackRetriever` (or uses `--feedback`), re-runs implement with that feedback in the brief.

- **Exit codes:** 0 Implemented, 2 TicketNotInProgress, 3 NoFeedbackAvailable, 4 ImplementFailed ([src/ThroughlineBuild.Cli/Program.cs:1497-1504](../../src/ThroughlineBuild.Cli/Program.cs#L1497-L1504)).
- **Invokes:** `ReworkPhase` -> `ImplementPhase`; `DefaultReworkRunner`, `ReworkCommand`, agent via `EffectiveAgentFor("implement")`.

### `build decompose <ticket-id>` - Functional
[src/ThroughlineBuild.Cli/Program.cs:1515-1570](../../src/ThroughlineBuild.Cli/Program.cs#L1515-L1570). Single ticket only (multi rejected with rework at [src/ThroughlineBuild.Cli/Program.cs:90-94](../../src/ThroughlineBuild.Cli/Program.cs#L90-L94)). Fetches the ticket, dispatches a worker to split it into independently-shippable sub-tickets, and creates the child tickets in Plane.

- **Inputs:** ticket id; `--debug | --quiet`, `--summary-json`, `--agent <name>`. `DecomposePhase` reads main SHA and top-level cwd entries to compose the brief.
- **Side effects:** creates child Plane work items parent-linked to the source ticket; writes a decompose summary (created ids, child sizes) to stdout.
- **Exits:** 0 success, 1 phase failure, 2 ticket not found.
- **Invokes:** `DecomposePhase`, `DecomposeBriefBuilder`, `PhaseSummaryBuilder.BuildDecompose`.
- **Note:** the `--n` and `--no-promote` flags carried by the `/ticket-decompose` slash command are NOT parsed by the CLI; the decompose branch reads only the positional ticket id. See Loose ends.

### `build new <body-path | text | -> [--title --type --label --review --print-template]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:342-621](../../src/ThroughlineBuild.Cli/Program.cs#L342-L621). Three modes selected by [src/ThroughlineBuild.Cli/NewVerbArgumentClassifier.cs](../../src/ThroughlineBuild.Cli/NewVerbArgumentClassifier.cs):

1. **File mode** - positional arg is an existing file: `NewPhase` files it directly.
2. **Draft mode** - free-form text (or stdin when `-`): `DraftPhase` spawns the implement-phase agent to draft a body, then `NewPhase` files it. With `--review`, an interactive loop ([src/ThroughlineBuild.Cli/ReviewLoop.cs](../../src/ThroughlineBuild.Cli/ReviewLoop.cs)) offers accept / edit / regenerate / quit before filing.
3. **`--print-template`** - emits the embedded ticket-body template to stdout.

- **Side effects:** creates a Plane work item (no parent), applies labels, optionally writes debug artifacts.
- **Invokes:** `NewVerbArgumentClassifier`, `DraftPhase` -> `DraftBriefBuilder` -> a `ClaudeCodeAgent` built from the implement-phase config ([src/ThroughlineBuild.Cli/Program.cs:516-534](../../src/ThroughlineBuild.Cli/Program.cs#L516-L534)), `ReviewLoop`, `NewPhase`, `NewCommand`.

### `build scaffold <op-doc-path> [--validate-only --dry-run --accept-warnings]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:623-702](../../src/ThroughlineBuild.Cli/Program.cs#L623-L702). Parses a Markdown "op doc" describing a plan -> brief hierarchy and creates the matching ticket tree in Plane, parent-linked. Op-doc-path is validated as a required positional before config load ([src/ThroughlineBuild.Cli/Program.cs:98-106](../../src/ThroughlineBuild.Cli/Program.cs#L98-L106)).

- **Inputs:** op-doc path. Format defined by [src/ThroughlineBuild.Scaffold/OpDocParser.cs](../../src/ThroughlineBuild.Scaffold/OpDocParser.cs); validation rules in [src/ThroughlineBuild.Scaffold/OpDocValidator.cs](../../src/ThroughlineBuild.Scaffold/OpDocValidator.cs). `--dry-run` parses/validates without writing.
- **Exit categories** (override global codes 2/3): Clean=0, ValidationError=2, PartialCreation=3, unexpected=1 ([src/ThroughlineBuild.Cli/Program.cs:689-695](../../src/ThroughlineBuild.Cli/Program.cs#L689-L695)).
- **Invokes:** `OpDocParser`, `OpDocValidator`, `ScaffoldPhase`, `BriefHtmlRenderer`, `ScaffoldCommand`.

### `build list [--state <name>] [--parent <id>] [--type <name>]` - Functional
[src/ThroughlineBuild.Cli/Program.cs:197-237](../../src/ThroughlineBuild.Cli/Program.cs#L197-L237), implemented in [src/ThroughlineBuild.Commands/ListCommand.cs](../../src/ThroughlineBuild.Commands/ListCommand.cs). Queries tickets with optional `--state`, `--parent`, `--type` filters and renders a fixed-width table (ID / Title / State / Type / Parent). No event log is written.

- **Exits:** 0 success (including "no tickets found"), 1 command failure (e.g. invalid `--state`).
- **Note:** the `--all` and `--feature` flags carried by the `/ticket-list` slash command are NOT supported by the CLI; only `--state`/`--parent`/`--type` exist.

### `build amend <ticket-id> [--size S|M|L] [--note "..."] [--description <path|->] [--ac <path|->]` - Functional
[src/ThroughlineBuild.Commands/AmendCommand.cs](../../src/ThroughlineBuild.Commands/AmendCommand.cs). Updates a non-terminal ticket; requires at least one of `--size`, `--note`, `--description`, `--ac` ([src/ThroughlineBuild.Commands/AmendCommand.cs:24-25](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L24-L25)). `--size` replaces the `size:*` label; `--note` appends a dated `<h3>Context Note</h3>` block via `AppendDescriptionAsync`; `--description` and `--ac` each read a file or stdin (`-`) and call `UpdateDescriptionAsync`. Refuses on `Done`/`Cancelled` tickets.

### `build close <ticket-id> <reason>` - Functional
[src/ThroughlineBuild.Commands/CloseCommand.cs](../../src/ThroughlineBuild.Commands/CloseCommand.cs). Translates the reason via the LLM (`ReasonTranslator`), posts a `<strong>wontfix:</strong>` comment, transitions `-> Cancelled`, attempts a parent rollup, then decrufts any associated worktree. Wired with an `ILlmClient` built by `LlmClientFactory` ([src/ThroughlineBuild.Cli/Program.cs:1595-1651](../../src/ThroughlineBuild.Cli/Program.cs#L1595-L1651)); a missing LLM secret returns exit 3.

### `build defer <ticket-id> <reason>` - Functional
[src/ThroughlineBuild.Commands/DeferCommand.cs](../../src/ThroughlineBuild.Commands/DeferCommand.cs). As `close`, with marker `<strong>deferred:</strong>` and a note that branches are left in place. A TODO marks a v1.1 "rebuild rollup-preview" step that is not implemented (see Loose ends).

### `build reopen <ticket-id> [reason]` - Functional
[src/ThroughlineBuild.Commands/ReopenCommand.cs](../../src/ThroughlineBuild.Commands/ReopenCommand.cs). Valid only from `Done` or `Cancelled`. Scans recent comments newest-first for prior `deferred:`/`wontfix:` markers, picks a target state, posts `<strong>reopened:</strong>`, transitions.

### `build --help` / `build help` - Functional
[src/ThroughlineBuild.Cli/Program.cs:25-29](../../src/ThroughlineBuild.Cli/Program.cs#L25-L29). Prints `CliUsage.UsageText`. An empty arg list does the same.

### Loose ends (CLI verbs)
- **`install` verb** is not present and never was wired; it is named in the architecture doc but falls through to "Unknown subcommand" (exit 2). `init` is the actual bootstrap verb now.
- **`--n` / `--no-promote` (decompose)** and **`--all` / `--feature` (list)** are slash-command flags that the CLI does not parse. The PROMPT for this doc set assumed they were CLI flags; the source does not parse them. Likewise `--sequential`, `--ship`, `--feature`, `--dry-run` (for chain), and `--in-given-order` exist only in the slash-command layer; the only `--dry-run` in source is for `scaffold`.
- **`build amend --ac`** reads the AC file and calls `UpdateDescriptionAsync` ([src/ThroughlineBuild.Commands/AmendCommand.cs:136](../../src/ThroughlineBuild.Commands/AmendCommand.cs#L136)) - the same call `--description` uses - so it replaces the whole description rather than only the acceptance-criteria block. If both `--description` and `--ac` are passed, the second write wins. Worth confirming against intent.
- **Multi-ticket chain has two overlapping code paths** (`ParallelDispatcher` for extra positional IDs vs the `SequentialChainDispatcher` fallback inside the single-ID branch). The in-code comment at [src/ThroughlineBuild.Cli/Program.cs:1313](../../src/ThroughlineBuild.Cli/Program.cs#L1313) flags this as transitional (TLB-312).

---

## Library projects (`src/ThroughlineBuild.*/`)

Nineteen library projects under `src/` (up from 14). Approximate dependency order (leaf -> root): `Contracts` -> `ModelClient`, `Git`, `Helpers`, `EventLog`, `Plane`, `Briefs`, `JudgmentSlots` -> `Anthropic`, `Workers.Common`, `Verification` -> `Workers.{ClaudeCode,Codex,Gemini,Copilot}`, `Scaffold`, `Phases` -> `Commands` -> `Cli`. The `Cli` project references all four worker projects, both model projects (`Anthropic` + `ModelClient`), and `Scaffold`/`Verification`/`Phases`/`Commands` ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj)).

| Project | Status | Role |
|---|---|---|
| `ThroughlineBuild.Contracts` | Functional | Interfaces, records, enums (incl. `ChainOutcome`, `IObsoleteRatifier`, `IVerifier`, `IWorkerAgent`, `ITicketing`, `ILlmClient`). No I/O. See [07-contracts.md](07-contracts.md). |
| `ThroughlineBuild.ModelClient` | Partial | Vendor-neutral model abstraction: `IModelClient` (`SendAsync` + `StreamAsync`), `ModelRequest`/`ModelResponse`, `ProviderConfig`, `UsageMapper`, AOT JSON context. Intended replacement for the legacy `ILlmClient`, but no production code constructs an `IModelClient` yet - only `AnthropicModelClient` implements it and only tests use it. |
| `ThroughlineBuild.Anthropic` | Partial | Hosts two Anthropic implementations plus an adapter. `AnthropicClient : ILlmClient` is the legacy path actually wired in production (via `LlmClientFactory`); its `InvokeStreamAsync` throws `NotImplementedException` ([src/ThroughlineBuild.Anthropic/AnthropicClient.cs:99](../../src/ThroughlineBuild.Anthropic/AnthropicClient.cs#L99)). `AnthropicModelClient : IModelClient` is the newer client with real SSE streaming ([src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs:82-150](../../src/ThroughlineBuild.Anthropic/AnthropicModelClient.cs#L82-L150)). `ModelClientLlmAdapter` bridges `IModelClient` -> `ILlmClient`. The latter two have no production caller. |
| `ThroughlineBuild.Briefs` | Functional | Builds the markdown brief handed to a worker. Per-phase builders: `Plan`, `Implement`, `Review`, `Draft`, `Decompose`. Templates are now per-agent subdirectories under [src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/): `claude-code/`, `codex/`, `copilot/`, `gemini/`, each holding `plan.md`, `implement.md`, `review.md`, `draft.md`, `decompose.md`. `ProjectContext` carries project facts into briefs. |
| `ThroughlineBuild.Helpers` | Functional | Pure helpers plus a few I/O-bearing ones: `SlugBuilder`, `MarkerParser`, `ConflictMarkerScanner`, `PhaseWorktreeLayout`, `PhaseSummary`/`PhaseSummaryBuilder`/`PhaseSummaryRenderer`, `WorkerSizeMapper`, `LlmUsageFlattener`, `DocOnlyDetector`, `DriftComparator`, `ParentDetector`, `TicketDependencyGraph`, `TicketTreeWalker`, and the I/O-bearing `WorktreeDecrufter`, `MainWorktreeResolver`, `MainWorktreeLock`. `DocOnlyDetector` and `DriftComparator` are tested but have no production caller (see Loose ends). |
| `ThroughlineBuild.Git` | Functional | `ProcessGitClient` spawns `git` subprocesses (rev-parse, worktree add/list, diff, fetch, rebase, FF-merge, delete-branch, rev-list count, log oneline, is-ancestor, remote-exists, ...). `BaseRefResolver` chooses `<remote>/<base>` then falls back to the local base branch. |
| `ThroughlineBuild.EventLog` | Functional | `JsonlEventSink` writes append-only JSONL to `.build/events/<stem>.jsonl`; `RecordingEventSink` mirrors to memory for summary builders. `ReviewFeedbackRetriever` scans logs newest-first for the latest `Rework` verdict. `SessionFileNameBuilder` produces the filename stem. AOT JSON via source-gen context. |
| `ThroughlineBuild.Verification` | Functional | `AutomatedChecksRunner` spawns each `CheckSpec` as a subprocess (process-tree kill on timeout, output tail). `WorkerAgentReviewer` is the `IVerifier` (agent-agnostic - takes any `IWorkerAgent`; replaces the old `ClaudeCodeReviewer`). `ObsoleteRatifier : IObsoleteRatifier` verifies an "obsolete" chain claim by dispatching a worker against the worktree. |
| `ThroughlineBuild.Plane` | Functional | `PlaneTicketingClient` is the sole `ITicketing` implementation. GET/PATCH/POST against the Plane REST API, Polly retry, a per-process `RequestThrottle` (40/min), lazily-cached state-name and label-name maps, and (TLB-366) a **per-run issue snapshot cache**: the whole project is paginated once into `_seqToUuid` + `_issueByUuid` and every `FindIssueAsync`/`QueryAsync` lookup answers from memory, with write-through `AddOrUpdate` mutations on every PATCH ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:47-58,277-361](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L47-L58)). Returns `BackendCapabilities` advertising typed relations/labels/rich HTML/attachments. See [03-external-dependencies.md](03-external-dependencies.md) / [05-state-and-persistence.md](05-state-and-persistence.md) / [07-contracts.md](07-contracts.md). No GitHub or Linear adapter. |
| `ThroughlineBuild.JudgmentSlots` | Functional | One slot: `ReasonTranslator` (translate-to-English via `ILlmClient`). Used by `CloseCommand`, `DeferCommand`, `ReopenCommand`. |
| `ThroughlineBuild.Workers.Common` | Functional | Shared worker code. `WorkerResultParser` extracts the `WORKER_RESULT` JSON envelope (reverse scan so the last envelope wins) with an AOT-safe `Dictionary<string, JsonElement>` metadata shape, and now runs a **fenced-block pre-pass** that captures `<<<NAME_START`/`<<<NAME_END` payload blocks emitted before the envelope; `FencedBlockResolver.TryResolveRef` later resolves a metadata `*_ref` field to its block body ([src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs](../../src/ThroughlineBuild.Workers.Common/WorkerResultParser.cs)). `MarkdownRenderer` is a new hand-rolled, AOT-safe CommonMark-subset markdown->HTML renderer used to turn resolved block bodies into Plane HTML ([src/ThroughlineBuild.Workers.Common/MarkdownRenderer.cs](../../src/ThroughlineBuild.Workers.Common/MarkdownRenderer.cs)). Referenced by all four worker projects and the phase layer. See [06-public-surfaces.md](06-public-surfaces.md) / [07-contracts.md](07-contracts.md). |
| `ThroughlineBuild.Workers.ClaudeCode` | Functional | `ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`); spawns `claude --print --output-format stream-json` with the brief on stdin, parses NDJSON stream events, and produces a one-line progress digest (`WorkerProgressDigest` / `ClaudeCodeProgressDigester`). AOT regression coverage is concentrated here. |
| `ThroughlineBuild.Workers.Codex` | Functional | `CodexAgent : IWorkerAgent` (`Name => "codex"`); runs `codex exec` with the brief as the positional prompt, maps size->model, has a `CodexProgressDigester`. |
| `ThroughlineBuild.Workers.Gemini` | Functional | `GeminiAgent : IWorkerAgent` (`Name => "gemini"`); spawns the Gemini CLI, parses its JSON DTOs, has a `GeminiProgressDigester`. |
| `ThroughlineBuild.Workers.Copilot` | Functional | `CopilotAgent : IWorkerAgent` (`Name => "copilot"`); runs `copilot -p "<brief>" -s --no-ask-user`, maps `AllowedTools` to per-tool `--allow-tool` flags, maps size->model. No progress digester (`Digester => null`). |
| `ThroughlineBuild.Scaffold` | Functional | Op-doc parser, validator, `BriefHtmlRenderer`, `ScaffoldPhase` (the Plane writes), and the typed `OpDoc` model. |
| `ThroughlineBuild.Phases` | Functional | Phase classes: `PlanPhase`, `ImplementPhase`, `ReviewPhase`, `ShipPhase`, `ChainPhase`, `ReworkPhase`, `DecomposePhase`, `NewPhase`, `DraftPhase`. Plus multi-ticket orchestration: `ParallelDispatcher`, `TicketGraph`, `AncestorSkipFilter`, and `EarlyExitManifest`. |
| `ThroughlineBuild.Commands` | Functional | `ITicketCommand` implementations and runners: `AmendCommand`, `ChainCommand`, `CloseCommand`, `DeferCommand`, `ListCommand`, `NewCommand`, `ReopenCommand`, `ReworkCommand`, `ScaffoldCommand`; `DefaultChainRunner`, `DefaultReworkRunner`, `SequentialChainDispatcher`; `TicketCommandRegistry`; `BodyTemplateLoader`, `ConfigTemplateLoader`. |
| `ThroughlineBuild.Cli` | Functional | `Program.cs` (verb dispatch + DI wiring), `Config.cs` (TOML loader + secrets resolver), `CliUsage.cs`, `CliArgParser.cs`, `InitCommand.cs`, `LlmClientFactory.cs`, `WorkerAgentFactory.cs`, `NewVerbArgumentClassifier.cs`, `ReviewLoop.cs`, `IConsole.cs`. The worker factory closes over config and builds Gemini/Codex/Copilot/Claude agents by name ([src/ThroughlineBuild.Cli/Program.cs:744-777](../../src/ThroughlineBuild.Cli/Program.cs#L744-L777)). |

### Loose ends (library projects)
- **`ModelClient` is not yet on the production path.** `LlmClientFactory` still constructs the legacy `AnthropicClient : ILlmClient` directly ([src/ThroughlineBuild.Cli/LlmClientFactory.cs:20](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L20)) and only accepts an `anthropic:` model prefix. `AnthropicModelClient` and `ModelClientLlmAdapter` (the path that would carry streaming and other vendors) are implemented and tested but unwired. This contradicts the multi-vendor model story in docs/throughline-build-architecture.md; trust the code - production LLM access is still single-vendor non-streaming. Cross-doc note for [11-llm-architecture.md](11-llm-architecture.md).
- **`AnthropicClient.InvokeStreamAsync`** and **`ModelClientLlmAdapter.InvokeStreamAsync`** both throw `NotImplementedException`. Only `AnthropicModelClient.StreamAsync` actually streams, and it has no caller.
- **`DocOnlyDetector` / `DriftComparator`** in Helpers are tested but have no production caller - aspirational gates not wired into any phase.
- **User-guide template is embedded but unsurfaced (TLB-320).** [src/ThroughlineBuild.Commands/Templates/throughline_build_userguide.md](../../src/ThroughlineBuild.Commands/Templates/throughline_build_userguide.md) is compiled in as an `<EmbeddedResource>` ([src/ThroughlineBuild.Commands/ThroughlineBuild.Commands.csproj](../../src/ThroughlineBuild.Commands/ThroughlineBuild.Commands.csproj)) for operator onboarding, but no CLI verb emits it today (no `userguide` verb in dispatch); it is loaded by nothing in production. Cross-check before adding a surfacing verb.
- **Worker support is fully multi-vendor** at the agent layer (four `IWorkerAgent` implementations, all reachable from the factory by config name and `--agent`). The old loose-end claiming only `claude-code` exists is obsolete.
- The old loose-end about `ClaudeCodeReviewer.FlattenLlmUsage` / `UnwrapJsonElement` duplicating `LlmUsageFlattener` is stale: `ClaudeCodeReviewer` no longer exists; the verifier is `WorkerAgentReviewer`.

---

## Tools (`src/tools/`)

Two single-file C# programs, AOT-compiled by `build.sh` into `bin/`. Build artifacts land under `src/tools/artifacts/`.

### `analyze-event-log` - Functional
[src/tools/analyze-event-log.cs](../../src/tools/analyze-event-log.cs). Reads one or more `.build/events/*.jsonl` files (or dirs/globs) and prints per-phase token totals (input/output/cache-read/cache-write), estimated USD cost from a per-model pricing table, wall time, and a chain summary. Output to stdout, no files written. Ships as [bin/analyze-event-log.exe](../../bin/analyze-event-log.exe). The pre-port Python reference is no longer present in `bin/`.

### `token-audit` - Functional
[src/tools/token-audit.cs](../../src/tools/token-audit.cs). Extracts Claude Code session metadata from JSONL under `~/.claude/projects/{encoded-repo-path}/`. Subcommands `latest`, `extract <file|dir|glob>...`, or no-arg combined. Reads Claude Code's own session log, not the Throughline event log. Ships as [bin/token-audit.exe](../../bin/token-audit.exe).

### Loose ends (tools)
- `bin/analyze-event-log.py` is gone; only the AOT `.exe` ships now (old loose-end retired).

---

## Scripts, runner, and CI

### `build.sh` - Functional
[build.sh](../../build.sh). Publishes three AOT binaries for one RID (defaults derived from `uname`, override via `$RID`) and copies them into `bin/`: `src/ThroughlineBuild.Cli` -> `build`, `src/tools/token-audit.cs` -> `token-audit`, `src/tools/analyze-event-log.cs` -> `analyze-event-log`.

### Runner (`.vscode/`) - Functional
Added by commit `71be047` ("added runner"): [.vscode/launch.json](../../.vscode/launch.json) and [.vscode/tasks.json](../../.vscode/tasks.json). These are the VS Code launch/task definitions for running and debugging the CLI; there is no standalone runner binary or script beyond `build.sh`.

### `.github/workflows/build.yml` - Functional
[.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow. Matrix across `{macos-latest (osx-arm64), windows-latest (win-x64), ubuntu-latest (linux-x64)}`. On push/PR to `main`: `dotnet restore`, `dotnet test --no-restore`, `dotnet publish src/ThroughlineBuild.Cli -r <rid> -c Release --no-restore`, upload the AOT artifact. No release-tagging or deploy.

### `.gitattributes` - Functional
[.gitattributes](../../.gitattributes). Pins LF endings for brief templates and snapshot test data to keep tests deterministic across Windows checkouts.

---

## Test projects (`tests/`)

Nineteen xUnit projects, ~1234 `[Fact]`/`[Theory]` methods across ~230 files, all net8.0. One test project mirrors each src library plus the worker split. Largest suites by test count: `Cli.Tests` (~262), `Phases.Tests` (~209), `Commands.Tests` (~117), `Scaffold.Tests` (~100), `Workers.ClaudeCode.Tests` (~96), `Briefs.Tests` (~81), `Plane.Tests` (~55). Smallest: `JudgmentSlots.Tests` (~5), `ModelClient.Tests` (~8).

Shared doubles (stubs/fakes) cover ticketing, workers, sinks, console, git, and LLM clients. Snapshot infra lives in the Briefs test project.

AOT regression coverage is concentrated in the Workers.ClaudeCode and Workers.Common tests around `WorkerResultParser`. Test projects do not inherit `PublishAot=true` from the Cli project, so AOT-sensitive paths set `AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)` before exercising the parser. See [11-llm-architecture.md](11-llm-architecture.md) for the AOT discipline.

### Loose ends (tests)
- `ModelClient.Tests` and the `Anthropic.Tests` streaming cases exercise the unwired `IModelClient` / `AnthropicModelClient` path; passing tests there do not imply the path is reachable from `build`.

---

## op-docs (`docs/op-docs/`)

Historical execution plans, not contracts. They are the narrative chain of how the system was built op-by-op. Active/recent docs at the top level include `op-21-tree-aware.md`, `op-25-user-guide-preinit.md`, `op-26-build-init.md`, and `op-27-worker-result-fenced-payloads.md` (the spec behind the fenced-block protocol - see [07-contracts.md](07-contracts.md)), plus `plan-brief-template-draft.md`. Completed ops are archived under [docs/op-docs/complete/](../../docs/op-docs/complete/) (op-01 through op-25, including `op-14-new-agent-foundation.md`, the per-agent ops op-15 codex / op-16 gemini / op-17 copilot, op-18 REST-API-LLM, op-19 lifecycle, op-20 decompose, op-22 obsolete-ticket-path, op-23 multi-ticket-prereqs, op-24 auto-resolve-ship, and op-25-multi-ticket).

### Loose ends (op-docs)
- The old loose-end calling `op-14-new-agent-foundation.md` an empty stub is obsolete: it has been completed and moved to `complete/`, and the four worker agents it foreshadowed all exist.

---

## `.claude/` (ticket workflow harness, external)

Consumed by the `/ticket-*` slash commands in the Claude Code harness, not by `build`. Included because every contributor encounters it.

- [.claude/plane-config.md](../../.claude/plane-config.md) - workspace/project/state/label UUIDs and pre-built Plane view URLs.
- [.claude/ticket-config.md](../../.claude/ticket-config.md) - stack info and test/build/preflight commands.
- `.claude/settings.json` - harness settings.
- `.claude/plane-rest/` - cached Plane API response fixtures.
- `.claude/worktrees/` - placeholder for git worktree state.
- `.claude/tmp_lengths.py` - transient helper script (not part of the workflow contract).

---

## Loose ends (cross-cutting)

- **`install` verb** is named in docs/throughline-build-architecture.md but never existed in code; `init` is the real bootstrap verb. Trust the code.
- **Multi-vendor LLM is real for workers, aspirational for model clients.** Four worker agents ship and are wired; the `IModelClient`/`AnthropicModelClient`/`ModelClientLlmAdapter` model layer is implemented but not on the production path, which still uses `AnthropicClient : ILlmClient` (single-vendor, non-streaming) for the `close`/`defer`/`reopen` reason translator. See [11-llm-architecture.md](11-llm-architecture.md).
- **`Size.S` / `Size.L` / `Risk.Low` / `Risk.High`** enum-value extraction from labels remains worth verifying against `PlaneTicketingClient.GetAsync` (carry-over from the prior inventory; confirm in [05-state-and-persistence.md](05-state-and-persistence.md) / [07-contracts.md](07-contracts.md)).
- **`DeferCommand` v1.1 TODO** - rebuild rollup preview for the parent when defer removes a ticket from a feature wave - is still unimplemented.
- **Architecture doc drift:** docs/throughline-build-architecture.md (and several sibling state-of-the-system docs written at commit 164e733) predate the worker-vendor split, the model-client refactor, multi-ticket chain, `decompose`, `list`, and `init`. Where they disagree with HEAD, the code wins.
