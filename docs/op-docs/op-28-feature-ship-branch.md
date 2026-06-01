# Operation: target-branch-configurable

Add a configurable work-context branch (`target_branch`) so TLB supports feature-branch workflows alongside trunk-based development. Worktrees are cut from `target_branch`, diffs are computed against `target_branch`, and ticket branches ship into `target_branch`. Defaults to `base_branch` when unset, so existing operators see no behavior change. `target_branch` is managed via a new `build settarget` CLI command. The future `build promote` command (out of scope here) will handle moving target_branch content into base_branch.

## Why this exists

TLB hardcodes `origin/main` in `BaseRefResolver`, which makes every worktree cut from main, every diff computed against main, and every ship go to main. There is no way to work on a feature branch today without editing source code. The fix is a two-level branch model: `base_branch` (existing, the project's ultimate landing target) stays exactly as it is, and a new `target_branch` (the current work context) is introduced alongside it.

When `target_branch` resolves to `base_branch` (the default behavior when the key is absent), everything works exactly as it does today. This is the migration safety net - every existing config and every existing repo keeps working without changes. When `target_branch` is explicitly set to a feature branch, all the phase-level branch references redirect through it: implement cuts worktrees from the feature branch, plan diffs against it, ship merges into it. The change is invisible until an operator opts in.

The structural change is small: one new config key, one resolver wiring update, one ship-destination update, one new CLI command. But it unlocks a workflow that operators expect by default in any tool that orchestrates git, and removes a real adoption barrier for anyone whose work doesn't fit a trunk-only model.

This op-doc deliberately scopes to ticket-closure (target_branch as the destination of ship) and explicitly not branch promotion (target_branch -> base_branch as a separate operation). Ship today fuses per-ticket closure with per-branch promotion; that fusion needs to be decoupled, but the decoupling is bigger than this op-doc and gets its own future ticket (`build promote`). Here the work is: parameterize the existing ship machinery on target_branch instead of hardcoding main. The divergence auto-resolve and the main-checkout mutex are already landed in the codebase and just need to inherit the parameterization.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Foundation: config schema + base-ref-resolver | - | S |
| B | Applications: ship destination + CLI command | A | M |

A first; B depends on A's config shape and resolver. Within Plan B, the ship-destination brief and the CLI brief are independent of each other (different files, no shared logic beyond the config read path) and can land in either order.

## Plan A: Foundation

### Goal

A new `[work]` section in `.build/config.toml` carries a `target_branch` key; `BaseRefResolver` reads the resolved target-branch value (target_branch when set, base_branch as fallback) and constructs refs accordingly. After this plan, Plan/Implement/Review/Decompose phases inherit the new branch-context behavior through the resolver without their own code changes. Ship still hardcodes main - that is B03's brief.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | target-branch-config-schema | New `[work]` section with `target_branch` key; Config.cs reads with fallback to base_branch when absent | - | src/ThroughlineBuild.Cli/Config.cs, .build/config.toml.example, init config template, source-gen contexts |
| 02 | base-ref-resolver-target-aware | BaseRefResolver constructs refs from the resolved target_branch value instead of hardcoded main | 01 | src/ThroughlineBuild.Git/BaseRefResolver.cs, callers in Plan/Implement/Review/Decompose phases, tests |

### Briefs - detail

#### Brief 01: target-branch-config-schema

Goal: A new `[work]` section in the config schema with an optional `target_branch` key. Config.cs exposes a resolved target-branch value that returns `target_branch` when present and falls back to `[ship] base_branch` when absent. No phase or CLI behavior changes in this brief - just the schema, the read path, and the example documentation.

Inputs: existing `Config.cs` schema and `LoadShipConfigCore`; existing `.build/config.toml.example`; existing `[ship]` section with `base_branch`; the init template (TLB-316 output) for keeping the example shape in lockstep.

Outputs:
- New `[work]` section in `Config.cs` with `target_branch` as an optional string. No default value at the config level - absence is meaningful.
- A resolved-target-branch helper on the config record (or a free function) that returns `target_branch` if present, else `base_branch` from `[ship]`.
- `.build/config.toml.example` includes a commented-out `[work]` section with explanatory comments describing when an operator would set `target_branch` and what `--unset` does to it.
- The init template (TLB-316) includes the same commented-out shape so freshly-init'd configs document the option without enabling it.
- New record (e.g. `WorkSettings` or similar) representing the parsed section.
- DTO registered for source-gen JSON / TOML contexts so AOT publish succeeds.

Acceptance:
- [ ] A config file with `[work] target_branch = "feature/x"` parses and the resolved target-branch value is `feature/x`
- [ ] A config file without `[work]` parses and the resolved target-branch value equals the `[ship] base_branch` value
- [ ] A config file with `[work]` present but `target_branch` absent parses and the resolved value falls back to `base_branch`
- [ ] `.build/config.toml.example` documents the `[work]` section with explanatory comments
- [ ] The init template includes the same shape
- [ ] AOT publish succeeds across all three release RIDs

Notes: The fallback is the migration safety net - every existing config keeps working because target_branch resolves to base_branch when absent. The `[work]` section starts minimal (one key) and is intentionally separate from `[ship]` because target_branch affects work setup (where worktrees are cut from, what diffs are against) far more than it affects ship destination. Future operator-context keys (the eventual `build promote` source/destination pairing, perhaps) land in `[work]` when their op-docs ship.

OOS:
- BaseRefResolver wiring (B02 owns)
- Ship destination changes (B03 owns)
- CLI command for setting and unsetting target_branch (B04 owns)
- Validation of the target_branch value against git refs at config-load time (B04 owns the operator-facing validation; this brief just reads what's in the file)

#### Brief 02: base-ref-resolver-target-aware

Goal: `BaseRefResolver` constructs refs from the resolved target_branch value rather than hardcoding `main`. The resolver still constructs `origin/<branch>` for remote-ref operations, but the branch part is now dynamic. Plan, Implement, Review, and Decompose phases inherit the behavior change automatically through the resolver - the brief's phase-side work is updating the constructor call sites to pass the new parameter, not changing any phase-level logic.

Inputs: current `BaseRefResolver.cs` (hardcoded `origin/main` in three ways per the design summary); the resolved target-branch helper from B01; the four phase code paths that consume `BaseRefResolver` (PlanPhase, ImplementPhase, ReviewPhase, DecomposePhase).

Outputs:
- BaseRefResolver constructor (or factory) accepts a target-branch string rather than computing or hardcoding it internally.
- The resolver's output ref names use the dynamic branch (e.g. `origin/feature/payment` when target_branch is `feature/payment`).
- All four phase callers updated to pass the resolved target-branch from B01 into BaseRefResolver's constructor.
- Phase-level logic is unchanged - the only modifications to phase code are the constructor call sites where BaseRefResolver is instantiated.
- Tests cover: resolver with target_branch = base_branch produces refs identical to today's hardcoded `origin/main` behavior; resolver with target_branch = a feature branch produces feature-branch refs; resolver with a slash-containing branch name (`feature/sub/thing`) handles the slashes correctly.

Acceptance:
- [ ] BaseRefResolver constructs refs using the passed target-branch value rather than a hardcoded literal
- [ ] When target_branch resolves to base_branch, the constructed refs are identical to today's `origin/main` behavior
- [ ] When target_branch is a non-default branch, the constructed refs name that branch with the `origin/` prefix
- [ ] Branch names containing slashes are handled correctly in the constructed ref names
- [ ] PlanPhase, ImplementPhase, ReviewPhase, and DecomposePhase compile and run against the new resolver shape with no logic changes beyond the constructor call sites
- [ ] AOT publish succeeds

Notes: BaseRefResolver is the choke point - every phase that needs to know "what's the work-context branch" routes through it. Once this brief lands, ShipPhase is the only phase still carrying main-specific behavior beyond what the resolver provides (B03 owns that). Tests should exercise the slash-containing branch case explicitly because that is the common shape for feature branches and any bug in ref-name construction would surface as a malformed git command at runtime rather than at config-load time.

OOS:
- Ship-phase destination changes (B03 owns)
- CLI command for managing target_branch (B04 owns)
- Per-ticket recording of which target_branch was active at worktree creation (out of scope per design decision - mid-flight target changes follow current config, no per-ticket history tracking)
- Schema migration of older config files (B01's fallback already handles this)

## Plan B: Applications

### Goal

ShipPhase rebases against and pushes to the resolved target_branch instead of hardcoded main, inheriting the divergence auto-resolve and main-checkout mutex behaviors that are already implemented. A new `build settarget` CLI verb manages the target_branch config value with set, unset, and display modes. After this plan, target_branch is end-to-end functional: an operator can set it, see all phases (including ship) honor it, and unset it to return to base_branch behavior.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | ship-target-branch-destination | ShipPhase rebases against and pushes to target_branch; divergence auto-resolve and main-checkout mutex are parameterized accordingly | - | src/ThroughlineBuild.Phases/ShipPhase.cs, event payload definitions, tests |
| 04 | build-settarget-command | CLI verb with set, unset, and display modes; validates that the named branch exists locally before setting | - | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliUsage.cs, config-write helpers, tests |

### Briefs - detail

#### Brief 03: ship-target-branch-destination

Goal: ShipPhase rebases the feature-worktree branch against the resolved target_branch, ff-merges into local target_branch, and pushes to `origin/target_branch`. The existing chain-integrity behaviors (Done marking, archiving, feature-branch deletion) are untouched. The divergence auto-resolve logic (already in the codebase from op-ship-auto-resolve-divergence) and the main-checkout mutex (already in the codebase from op-multi-ticket-prerequisites) inherit the parameterization so they operate on target_branch rather than hardcoded main.

Inputs: current ShipPhase.cs with its rebase target (around line 243), ff-merge (around line 312), and divergence-check logic; the resolved target_branch value flowing in from B01/B02 via BaseRefResolver; the existing main-checkout mutex which is keyed by main-worktree path (path unaffected by branch choice).

Outputs:
- ShipPhase's rebase target is the resolved target_branch (e.g. rebases the feature-worktree branch against `origin/feature/payment` when target_branch is `feature/payment`).
- ShipPhase's ff-merge target is the resolved target_branch in the main checkout (ff-merges the feature-worktree branch into local `feature/payment`).
- ShipPhase's push destination is `origin/target_branch` (pushes to `origin/feature/payment`).
- The existing divergence-check operates on `local target_branch vs origin/target_branch`; the auto-rebase replays local-only commits onto origin/target_branch; the event payload that records the auto-rebase reflects the actual target_branch name rather than a hardcoded literal.
- The chain-integrity behaviors (Done marking, archiving, feature-branch cleanup via WorktreeDecrufter) are byte-for-byte unchanged - they operate on the ticket's worktree and Plane state, not on the target branch.
- The main-checkout mutex still protects the two main-checkout windows in ship (fetch and ff-merge); since it is keyed by main-worktree path and not by branch name, no mutex-side changes are needed.
- Event-name consistency: the existing event recording the auto-rebase (currently `MainAutoRebased` per the divergence op-doc) is renamed if its current name implies main-only applicability. Renaming is a side decision documented in Notes.

Acceptance:
- [ ] A ship invocation with target_branch = base_branch produces the same end state as today's behavior (ticket merges to local base, pushes to origin/base, ticket marked Done, feature worktree cleaned up)
- [ ] A ship invocation with target_branch = feature/x ff-merges the feature-worktree branch into local feature/x and pushes to origin/feature/x
- [ ] The divergence auto-resolve path operates on target_branch in its check, its rebase replay, and its event payload
- [ ] Done marking, archiving, and feature-branch cleanup happen identically regardless of target_branch value
- [ ] The main-checkout mutex continues to protect the fetch and ff-merge windows regardless of target_branch
- [ ] AOT publish succeeds

Notes: This brief is largely careful search-and-replace through ShipPhase, looking for every place "main" or the existing `base_branch` value flows into a rebase/merge/push target and routing it through the resolved target_branch instead. Subtle assumptions about the merge target may be embedded in error messages, log lines, or event payloads beyond the obvious git-invocation call sites - read ShipPhase end-to-end before editing rather than grep-and-replace, because a log line that says "rebased against origin/main" is misleading if the actual operation rebased against `origin/feature/x`. The MainAutoRebased event name from op-ship-auto-resolve-divergence applies more narrowly than the behavior now does; rename to TargetAutoRebased or similar in this brief and update any consumer (analyze-event-log, etc.) consistently.

OOS:
- The future `build promote` command that moves target_branch content into base_branch (separate ticket)
- Config schema (B01 owns)
- BaseRefResolver wiring (B02 owns)
- CLI command for managing target_branch (B04 owns)

#### Brief 04: build-settarget-command

Goal: A `build settarget` CLI verb with three modes - set (writes the key under `[work]`), unset (removes the key), and display (prints the current resolved value and whether it comes from `[work]` override or `[ship] base_branch` fallback). Set mode validates that the named branch exists as a local git ref and refuses to write if it does not. Existing config sections are preserved when writing.

Inputs: the config-read path from B01; existing config-write patterns (init writes a fresh config from a template; settarget is a targeted edit of an existing config); existing CLI dispatch from `build init` and other early-dispatch verbs; the resolved target-branch helper from B01.

Outputs:
- `build settarget <branch>` writes `target_branch = "<branch>"` under `[work]` in `.build/config.toml`. Validates by running `git rev-parse --verify refs/heads/<branch>` (or equivalent) first; refuses with a clear error if the branch does not exist locally, directing the operator to `git checkout -b <branch>` first.
- `build settarget --unset` removes the `target_branch` key from `[work]` in `.build/config.toml`. If the key is already absent, prints a noop message and exits 0.
- `build settarget` with no arguments prints the current resolved target_branch value and its source: either `target_branch = <value> (from [work])` when overridden, or `target_branch = <base_branch_value> (default, no [work] override)` when relying on the fallback.
- Verb is dispatched in the same early-dispatch region as `build init` because it manages config without requiring Plane or worker setup; it does require an existing `.build/config.toml` and refuses with a clear error if absent, directing the operator to `build init` first.
- Other config sections (`[ship]`, `[ticketing]`, `[workers]`, `[events]`, `[review]`, `[project]`, `[llm]`) are preserved byte-for-byte through any settarget operation (or as close as the config-writer can achieve; document any unavoidable reformatting in Notes).
- Usage text in `CliUsage.cs` documents the verb and all three modes with examples.

Acceptance:
- [ ] `build settarget feature/x` with `feature/x` existing as a local branch writes the key under `[work]` and exits 0
- [ ] `build settarget feature/x` with `feature/x` not existing locally exits 2 with a clear error message directing the operator to create the branch first
- [ ] `build settarget --unset` removes the key from `[work]` and exits 0
- [ ] `build settarget --unset` when the key is already absent exits 0 with a noop message
- [ ] `build settarget` with no arguments prints the current resolved value and labels its source as either the `[work]` override or the `base_branch` default
- [ ] Other config sections in `.build/config.toml` are unchanged after any settarget operation
- [ ] `build settarget` in a directory without `.build/config.toml` refuses with a clear error and directs the operator to `build init`
- [ ] `build --help` documents the verb with all three modes

Notes: Config writing is touchy - the operator's TOML may contain comments, custom formatting, or sections the verb doesn't know about, and the write must preserve those. The TOML library used by the project should support targeted edits; if it doesn't, a careful line-edit approach (find the `[work]` section, update or remove the `target_branch` line, leave everything else untouched, append a new `[work]` section if it doesn't exist) is acceptable as long as the implementation is documented. Strict branch validation is the v1 default per design call - the operator does `git checkout -b feature/x && build settarget feature/x`. Relaxing the validation later (auto-create, ignore-not-found, remote-branch validation) is a separate concern when one is needed.

OOS:
- Auto-creating the named branch if it doesn't exist locally
- Remote-branch validation (only local validation in v1; operator handles remote setup via normal git operations)
- A separate `build gettarget` verb (the display mode of `settarget` with no arguments covers this case)
- Warning the operator about in-flight tickets when target_branch changes (deferred per design decision; revisit if it becomes a frequent footgun in practice)

## What done looks like

An operator working on `feature/payment-flow` runs `build settarget feature/payment-flow`. TLB validates the branch exists locally, writes the key to `[work]` in config.toml, and confirms with the current resolved target. From that point: every `build new` ticket creates a worktree cut from `feature/payment-flow`; every `build plan` / `build implement` / `build review` / `build decompose` operation computes diffs against `feature/payment-flow`; every `build ship` ff-merges into local `feature/payment-flow` and pushes to `origin/feature/payment-flow`, with the existing divergence auto-resolve handling clean rebases the same way it does on main. The main-checkout mutex continues to protect concurrent ship operations against the same repo. When the feature work is complete, `build settarget --unset` removes the override and TLB returns to working against `base_branch`. The future `build promote` (separate ticket) will handle moving feature-branch content into base_branch. Operators who never run `settarget` see zero behavior change because the resolved target_branch falls back to base_branch when the key is absent.