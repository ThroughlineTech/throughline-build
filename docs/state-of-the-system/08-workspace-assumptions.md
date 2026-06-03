# 08 - Workspace and Environment Assumptions

What `build` assumes about the environment it runs in beyond the obvious - branch conventions, required tooling, OS specifics, CI behavior, places where the code branches on stack or platform.

For pure prerequisite tooling see [02-install-build-run.md](02-install-build-run.md). For configuration see [04-configuration.md](04-configuration.md).

---

## Repository layout

Status: Functional.

- A git repository, working tree (not a bare repo).
- A `.build/config.toml` exists somewhere at or above the cwd. `BuildConfig.FindConfigFile` walks up from cwd ([src/ThroughlineBuild.Cli/Config.cs:85-96](../../src/ThroughlineBuild.Cli/Config.cs#L85-L96)). The exception is `build init`, which creates that file and runs before config load ([src/ThroughlineBuild.Cli/Program.cs:137-153](../../src/ThroughlineBuild.Cli/Program.cs#L137-L153)); `build settarget` likewise dispatches before config load ([src/ThroughlineBuild.Cli/Program.cs:157-164](../../src/ThroughlineBuild.Cli/Program.cs#L157-L164)).
- The directory holding `.build/` is treated as the project root - relative `events.log_directory` resolves against the parent of the config file via `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:152-156](../../src/ThroughlineBuild.Cli/Config.cs#L152-L156)).
- `MainWorktreeResolver.ResolveAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs:12-25](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs#L12-L25)) returns the first entry from `git worktree list` (git porcelain always reports the main worktree first), falling back to the raw cwd on any error. Operators can invoke `build` from inside a feature worktree and the resolved cwd will still be the main worktree root ([src/ThroughlineBuild.Cli/Program.cs:175](../../src/ThroughlineBuild.Cli/Program.cs#L175)).

---

## Branch conventions

Status: Functional.

- **Base / target branch.** `main` by default. The merge destination is resolved by `BuildConfig.ResolveTargetBranch()` = `[work].target_branch ?? [ship].base_branch` ([src/ThroughlineBuild.Cli/Config.cs:68](../../src/ThroughlineBuild.Cli/Config.cs#L68)). `BaseRefResolver.ResolveAsync` is now **target-aware**: it takes the resolved target branch and tries `origin/<target>` then falls back to the local `<target>` ([src/ThroughlineBuild.Git/BaseRefResolver.cs:24-62](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L24-L62)) - the branch is passed in, no longer a literal `main`. It now also prefers the *local* target when it is strictly ahead of origin (the accumulation rule, TLB-411 - see lifecycle doc) so chain children stack on each shipped sibling. The resolved target flows into `ShipOptions.TargetBranch` and `BuildOptions.TargetBranch`. Set the override with `build settarget <branch>`.
- **Remote.** `origin`. Configurable as `ship.remote` (default `"origin"`, [src/ThroughlineBuild.Cli/Config.cs:610](../../src/ThroughlineBuild.Cli/Config.cs#L610)).
- **Feature branches (CHANGED, TLB-408).** `ticket/<id>` - the **ticket id only**, no title slug. `PhaseWorktreeLayout.Compute` calls `SlugBuilder.BuildTicketSlug(ticketId)` (= `BuildBranchSlug` with an empty title), so the branch is the sanitized lowercased id alone ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:7-16](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L7-L16), [src/ThroughlineBuild.Helpers/SlugBuilder.cs:10-16](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L10-L16)). Dropping the title keeps the worktree directory short so deep repo trees stay under the Windows `MAX_PATH` limit, which long titles used to blow past. Legacy `ticket/<id>-<slug>` branches are still recognized for in-flight worktrees: `IsTicketBranch` matches the canonical id exactly *or* a `ticket/<id>-` prefix, and `MentionsBranch` uses a hyphen-boundary regex so `ticket/24` never matches `ticket/240` ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:29-48](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L29-L48)).
- **Chain placeholder branch.** A parent chain creates ONE shared worktree on a placeholder branch `chain/<slug>` ([src/ThroughlineBuild.Phases/ChainPhase.cs:703](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L703)); the placeholder never receives commits (each child immediately switches the shared worktree to its own `ticket/<id>` branch) and is torn down at chain end. See the lifecycle doc for the worktree model.
- **Worktree paths.** `.worktrees/ticket-<id>/` (absolute, rooted at the main worktree, [src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:14](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L14)).
- **Preflight wrong-branch guard (TLB-349 / TLB-402).** Before merging, `ShipPhase` verifies the main worktree is checked out on the target branch; if not (including a detached HEAD), it posts `ship_blocked: main worktree is on '<x>' (or detached); must be on '<target>' ...`, emits a `wrong_worktree_branch` `GateFailure`, and returns `ShipFailureStage.PreFlight` ([src/ThroughlineBuild.Phases/ShipPhase.cs:256-277](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L256-L277)). The check is now **unconditional** - it fires even when targeting `main`, not only for non-default targets (TLB-402). `FastForwardMergeAsync` advances whatever is checked out, so without this guard a wrong/detached HEAD would fast-forward and push the wrong ref.
- **Auto-rebase of the local target branch (TLB-296/297/298/347).** When the remote exists and the local target has diverged from `<remote>/<target>`, `ShipPhase` probes the divergence subspecies via `IGitClient.ProbeDivergenceAsync` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:962](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L962), uses `git merge-tree --write-tree` to detect conflicts without mutating). On `DivergedNoConflict` (and unless `--no-auto-merge` is set) it rebases the local target onto the remote target and emits a `TargetAutoRebased` event ([src/ThroughlineBuild.Phases/ShipPhase.cs:356-406](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L356-L406)). On `DivergedWithConflict`, or when `--no-auto-merge` is passed, it posts `ship_blocked: ... diverged; manual resolution required` and aborts (emitting a `diverged_bases` `GateFailure`). The `--no-auto-merge` flag is a bare bool stripped in the CLI pre-pass ([src/ThroughlineBuild.Cli/Program.cs:54-55](../../src/ThroughlineBuild.Cli/Program.cs#L54-L55)) and threaded into `ShipOptions.NoAutoMerge`.
- **Push of the target branch after FF merge (TLB-293) - default-on but opt-out (TLB-410).** After the fast-forward merge, `ShipPhase` pushes the target branch when a remote is configured and push is enabled. A push failure fails the ship at `ShipFailureStage.Push`. **Ship is no longer push-only:** `--no-push` (or `[ship].push = false`) flips it to a purely local merge. `ShipOptions.NoPush = noPush || !config.Ship.Push`, and the effective gate is `useRemote = remoteConfigured && !NoPush` ([src/ThroughlineBuild.Phases/ShipPhase.cs:282](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L282)); when `useRemote` is false ship skips fetch / reconcile / push entirely, emits `fetch_skipped` (reason `push_disabled` or `no_remote`), and rebases the feature branch onto the **local** target ([src/ThroughlineBuild.Phases/ShipPhase.cs:285-296](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L285-L296)). The local-only path needs no remote at all. The actual push is gated on `useRemote` and a post-FF-merge HEAD re-verify runs inside the lock (TLB-402). This is the inverse of the old "never pushes" rule - see Loose ends for the architecture-doc disagreement.
- **Resolved-target surfacing (TLB-410).** Ship prints `[ship] target branch: <target> (<source>)` where source is `from [work]` or `default, no [work] override` ([src/ThroughlineBuild.Phases/ShipPhase.cs:248-254](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L248-L254)), and the `base_ref_resolved` event carries `target_branch` + `source`. An unpushed remote target is treated as not-diverged: `RemoteBranchExistsAsync` is probed, and if absent ship rebases onto the local target (reason `remote_branch_absent`) and lets the push create the branch ([src/ThroughlineBuild.Phases/ShipPhase.cs:315-320](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L315-L320), TLB-409).
- **No force operations.** No code path calls `git push --force`, `git reset --hard`, or `git rebase -i`. A failed auto-rebase is aborted with `git rebase --abort` rather than reset.

If no remote is configured (`IGitClient.RemoteExistsAsync` returns false) - or push is disabled - `ShipPhase` emits `fetch_skipped`, skips fetch / auto-rebase / push, and rebases the feature branch onto the local target branch.

### Loose ends

- **`BaseRefResolver` is now target-parameterized**, but the `remote` is still the literal `origin/` prefix inside it ([src/ThroughlineBuild.Git/BaseRefResolver.cs:27](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L27)); `ship.remote` reaches `ShipPhase` but not the resolver's remote prefix. Phases other than ship resolve against `BuildOptions.TargetBranch` (default `main`).
- **`--no-auto-merge` toggles the auto-rebase; `--no-push` / `[ship].push=false` toggle the push.** These are now independent knobs - `--no-push` skips fetch+reconcile+push together (local-only ship), while `--no-auto-merge` only declines the divergence rebase.

---

## Working-tree hygiene (op-29)

Status: Functional.

`build` now assumes - and enforces - a clean working tree at phase boundaries. The shared gate is `WorkingTreeHygieneGate` ([src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs](../../src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs)).

- **Preflight gate before implement / chain / ship.** `CheckAsync` ([src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs:24-62](../../src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs#L24-L62)) rejects any unmerged/conflicted paths (UU/AA/DD/AU/UA/UD/DU) and any **dangling stash entries unrelated to the ticket branch**. It runs before implement (Step 2b, `GateFailure` kind `hygiene_gate`, [src/ThroughlineBuild.Phases/ImplementPhase.cs:93-106](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L93-L106)) and once at outermost chain start (kind `hygiene_gate_preflight` -> `ChainOutcome.RefusedDirtyTree`, [src/ThroughlineBuild.Phases/ChainPhase.cs:86-110](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L86-L110)). `ShipPreflightAsync` checks **both** the feature and main worktrees plus repo-global stash (Step 3b, kind `pre_flight_hygiene` -> `ShipFailureStage.PreFlight`, [src/ThroughlineBuild.Phases/ShipPhase.cs:204-219](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L204-L219)).
- **`git stash` is banned for workers and the read-only verifier.** The stash stack is repo-global, so a stash created in one worktree leaks across all of them. The agent brief templates carry the prohibition: implement briefs say "Do NOT use git stash" and review briefs forbid `git stash`, `git checkout`, `git reset`, and `git rebase` ([src/ThroughlineBuild.Briefs/Templates/](../../src/ThroughlineBuild.Briefs/Templates/) - per-vendor `implement.md` / `review.md`). The hygiene gate's unrelated-stash detection backstops this.
- **Post-phase worktree-cleanliness validation.** After the implement worker exits, `DirtyFilesCheckAsync` checks the feature worktree for uncommitted tracked changes; a dirty tree triggers **one bounded retry** with an injected "commit before returning" note ([src/ThroughlineBuild.Phases/ImplementPhase.cs:301-333](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L301-L333)). After the verifier exits, the same check is a **hard fail with no retry** (`GateFailure` kind `dirty_worktree_after_review`, [src/ThroughlineBuild.Phases/ReviewPhase.cs:217-235](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L217-L235)) - the verifier is read-only, so any dirt it leaves is a contract violation.

---

## Required tooling

Spelled out in [02-install-build-run.md](02-install-build-run.md). Recap of the assumptions:

- `git` >= 2.5 (worktrees). The auto-rebase divergence probe additionally needs `git merge-tree --write-tree` (git >= 2.38).
- A worker CLI on PATH or absolute in config: `claude` for the claude-code agent, plus `codex` / `gemini` / `copilot` for those agents. The configured agent's `executable` is resolved per `[workers.<agent>]`. A missing executable is now a graceful failure, not a crash (see OS assumptions).
- `.NET 10 SDK` for builds (all 19 projects target `net10.0` since commit 97e6a87, e.g. [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:4](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L4)), and for any verifier / regression `CheckSpec` that invokes `dotnet` (the project's `test_command`).
- A C/C++ toolchain for AOT publish (MSVC / Xcode CLT / gcc).
- **Host-coupled native-AOT link paths (win-x64).** The root [Directory.Build.props:13-24](../../Directory.Build.props#L13-L24) hardcodes machine-specific MSVC and Windows SDK paths (`VC\Tools\MSVC\14.44.35207`, Win SDK `10.0.26100.0`) and sets `IlcUseEnvironmentalTools=true` to skip `vswhere.exe` discovery. These only take effect during a native publish (`-r win-x64 -c Release`); managed builds and tests are unaffected. A different machine doing a Windows native publish must edit these paths - a real host assumption, not a portable default.
- Network to Plane and (for some verbs) Anthropic.

---

## Operating system assumptions

Status: Functional. `build` is genuinely cross-platform - CI builds three RIDs ([.github/workflows/build.yml:11-23](../../.github/workflows/build.yml#L11-L23)) and the architecture targets Mac / Windows / Linux. The platform-specific branches:

- **Worker executable not found is non-fatal (NEW, 0f9d114).** `ClaudeCodeAgent.ExecuteAsync` wraps `process.Start()` in a `try/catch (Win32Exception)` and returns a `Status.Failed` `WorkerResult` with a "Worker executable not found" reason instead of crashing ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:85-96](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L85-L96)). The same guard is present in all four worker agents ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:80-83](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L80-L83), [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:80-84](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L80-L84), [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:80-84](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L80-L84)). This was the "Don't crash when Claude isn't found in Bash in Windows" fix (commit 0f9d114 covered claude-code + codex; gemini/copilot match) - on Windows a `.cmd`/`.ps1` shim that is not on PATH surfaces as a `Win32Exception` rather than a clean exit.
- **EDITOR resolver Windows fallback.** The `--review` editor loop resolves `$EDITOR` first, then a fallback chain `vim, nano, code --wait` plus `notepad.exe` only on Windows ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-278](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L278)). The on-PATH probe uses `where` on Windows and `which` elsewhere ([src/ThroughlineBuild.Cli/ReviewLoop.cs:280-302](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L280-L302)).
- **Windows reparse points** in `WorktreeDecrufter` get pre-cleaned before directory deletion: on Windows it walks `node_modules`, finds subdirectories whose `LinkTarget` is set, and deletes those reparse-point links first ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:111-136](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L111-L136)).
- **Path comparison case folding.** Windows-specific case-insensitive path comparison appears in the ship exe-in-worktree pre-flight ([src/ThroughlineBuild.Phases/ShipPhase.cs:190](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L190)) and the `MainWorktreeLock` key normalization, which lowercases the path on Windows ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:14-15](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L14-L15)).
- **`build.sh` adds `.exe`** for Windows RIDs only ([build.sh:15-16](../../build.sh#L15-L16)).
- **`<InvariantGlobalization>true</InvariantGlobalization>`** ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) means the binary uses invariant culture everywhere - dates, casing, comparisons. No ICU required at runtime.
- **`SlugBuilder` strips non-ASCII** silently ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:53-64](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L53-L64)) so a ticket *title* with non-Latin characters produces a slug derived only from the ASCII subset - now mostly moot for branch names since they carry the ticket id alone.

No subprocess is launched through a shell - every spawn sets `UseShellExecute = false` and passes the executable + argument list directly (e.g. [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:43](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L43), [src/ThroughlineBuild.Git/ProcessGitClient.cs:195-201](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L195-L201)). There is no `bash -c` / `cmd /c` wrapping in the binary itself.

ASCII-only output at tool boundaries is an explicit constraint (architecture Section 2). Operators with non-ASCII in tickets see escaped or stripped artifacts in markers, branch names, and filenames.

### Loose ends

- **`build init`'s template-write goes through `File.WriteAllText(..., Encoding.UTF8)`** ([src/ThroughlineBuild.Cli/InitCommand.cs:54](../../src/ThroughlineBuild.Cli/InitCommand.cs#L54)). If the embedded template ever carries non-ASCII bytes the round-trip through MSYS/curl noted in the operator's global conventions does not apply (this is a local file write, not an HTTP body), but the constraint is worth keeping in mind for any template edits.
- **Worker stdin encoding.** The brief is written to the worker via `StandardInput.WriteAsync` with the process default encoding; non-ASCII in a brief is the worker's problem, not normalized by `build`.

---

## CI integration

Status: Functional. [.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow, one job per OS via a `strategy.matrix` ([.github/workflows/build.yml:11-23](../../.github/workflows/build.yml#L11-L23)). On push or PR to `main` ([.github/workflows/build.yml:3-7](../../.github/workflows/build.yml#L3-L7)):

OS x RID matrix (the artifact name carries the `.exe` suffix only on Windows):

| OS | RID | artifact |
|---|---|---|
| `macos-latest` | `osx-arm64` | `build` |
| `windows-latest` | `win-x64` | `build.exe` |
| `ubuntu-latest` | `linux-x64` | `build` |

Steps ([.github/workflows/build.yml:25-36](../../.github/workflows/build.yml#L25-L36)):

1. `actions/checkout@v4`.
2. `actions/setup-dotnet@v4` with `dotnet-version: '10.x'` ([.github/workflows/build.yml:29](../../.github/workflows/build.yml#L29)).
3. `dotnet restore`.
4. `dotnet test --no-restore`.
5. `dotnet publish src/ThroughlineBuild.Cli -r <rid> -c Release --no-restore`.
6. `actions/upload-artifact@v4` of `src/ThroughlineBuild.Cli/bin/Release/net10.0/<rid>/publish/<artifact>` ([.github/workflows/build.yml:36](../../.github/workflows/build.yml#L36)).

CI builds **only the main `build` binary**. The `token-audit` and `analyze-event-log` tools under `src/tools/` are not published or tested in CI - they are built only by the local `build.sh` ([build.sh:24-30](../../build.sh#L24-L30)).

What is **not** in CI:

- No release tagging.
- No signing.
- No automated dogfooding (the architecture's promotion criteria "five real tickets handled without surprise" is a manual judgment).
- No coverage report, no SAST, no AOT analyzers.
- No deployment of artifacts past the upload step.

---

## Stack-specific code paths

Status: Functional (stack-neutral by design).

`ProjectContext` flows from `.build/config.toml`'s `[project]` section ([src/ThroughlineBuild.Cli/Config.cs:378-433](../../src/ThroughlineBuild.Cli/Config.cs#L378-L433)) into the brief context dictionary. Brief templates can reference `{{project_build_command}}`, `{{project_test_command}}`, etc., and the worker reads what stack to use. This is the only stack-aware path in the binary - everything else is stack-neutral.

The verifier and ship checks know **nothing** about specific languages or build tools. `AutomatedChecksRunner.RunAsync` ([src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs:16-57](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L16-L57)) simply spawns each configured `CheckSpec` executable with its argument list and treats exit code 0 as pass ([src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs:64-171](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L64-L171)). Whether a check runs `dotnet test`, `npm test`, or `cargo test` is purely a function of the operator's `[[review.checks]]` / `[ship.regression_checks]` config ([src/ThroughlineBuild.Cli/Config.cs:301-376](../../src/ThroughlineBuild.Cli/Config.cs#L301-L376)). `workflow_tool` is validated to be `"build"` or `"claude-config"` ([src/ThroughlineBuild.Cli/Config.cs:391-395](../../src/ThroughlineBuild.Cli/Config.cs#L391-L395)); the `.NET`-ness of this repo is incidental.

The brief templates do not branch by stack today; they embed the project's commands as suggestions. A non-`.NET` project using `build` gets the same template shape and passes its own commands through context.

---

## Worktree-aware behavior

Status: Functional.

- **Verifier runs in the feature worktree (TLB-226).** `ReviewPhase` constructs `WorkerAgentReviewer` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:205](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L205)) and the `AutomatedChecksRunner` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:196](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L196)) against `canonicalWorktreePath` (the feature worktree), not the main working directory. The diff and brief are computed from the main repo, but the verifier worker and the checks execute in the checkout containing the change. Review now attributes against the worktree HEAD rather than a superseded `implemented_at` marker (TLB-414, [src/ThroughlineBuild.Phases/ReviewPhase.cs:152-181](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L152-L181)).
- **Ship regression checks run in the feature worktree.** `ShipPhase` runs `_checksRunner.RunAsync(_shipOptions.RegressionChecks, canonicalWorktreePath, ct)` ([src/ThroughlineBuild.Phases/ShipPhase.cs:508](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L508), legacy fallback :595), and the rebase happens in the feature worktree, while fetch / FF-merge / push happen in the main worktree. The baseline run (TLB-401) executes the same checks in a detached `.worktrees/baseline-<sha>` worktree ([src/ThroughlineBuild.Phases/ShipPhase.cs:833](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L833)).
- **Pre-flight: exe-in-worktree check.** `ShipPhase` refuses to ship if the running `build` binary is inside the worktree being rebased ([src/ThroughlineBuild.Phases/ShipPhase.cs:181-202](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L181-L202)) - killing the binary midway would leave inconsistent state. The check compares `processPathProvider()` (defaults to `Environment.ProcessPath`) against the worktree path with Windows-case-insensitive comparison ([src/ThroughlineBuild.Phases/ShipPhase.cs:190](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L190)). Failure: posts `ship_blocked: build.exe is running from inside the worktree`, `ShipFailureStage.PreFlight`.
- **Pre-flight: hygiene + dirty-tracked check.** `ShipPhase` runs `WorkingTreeHygieneGate.ShipPreflightAsync` (conflicts + unrelated stash in both worktrees, Step 3b) then refuses if either the feature or main worktree has uncommitted tracked changes ([src/ThroughlineBuild.Phases/ShipPhase.cs:204-243](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L204-L243)). Untracked files are ignored. See Working-tree hygiene above.
- **Worktree location.** `ReviewPhase` and `ShipPhase` locate the feature worktree by walking `IGitClient.ListWorktreesAsync` and matching either branch name or full path against the deterministic layout ([src/ThroughlineBuild.Phases/ReviewPhase.cs:79-104](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L79-L104), [src/ThroughlineBuild.Phases/ShipPhase.cs:123-178](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L123-L178)). A manually renamed worktree loses this link. `ReviewPhase` will reconstruct a missing review worktree from the ticket branch (TLB-407, [src/ThroughlineBuild.Phases/ReviewPhase.cs:110-126](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L110-L126)).

### Loose ends

- **Worktree match on rename.** Both `ReviewPhase` and `ShipPhase` match on branch name OR path; a worktree whose branch was renamed and whose path no longer matches `.worktrees/ticket-<slug>` will report "feature worktree not found".

---

## Worker-subprocess environment

`ClaudeCodeAgent.ConfigureEnvironment` modifies the child env explicitly ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:404-416](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L404-L416)):

- Removes `ANTHROPIC_API_KEY` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:408](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L408)) so the worker authenticates through Claude Code OAuth, not per-token API billing.
- Sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from config when configured ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:411-412](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L411-L412)); a caller-supplied env override still wins because the user loop runs after.
- Sets working directory to the worktree path ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:39](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L39)) so the worker sees only the feature-branch checkout.

Other env vars from the parent process pass through. The worker inherits the parent's PATH, HOME, USER, etc.

---

## Filesystem assumptions

- **Case-sensitive path matching.** `DriftComparator.Compare` intersects file lists with the default ordinal `List.Contains`, which is case-sensitive (the comment at [src/ThroughlineBuild.Helpers/DriftComparator.cs:17-21](../../src/ThroughlineBuild.Helpers/DriftComparator.cs#L17-L21) calls it out). Operates correctly on case-insensitive filesystems for most paths because git itself preserves case. Worktree/path lookups elsewhere use `OrdinalIgnoreCase` deliberately (e.g. `WorktreeDecrufter`, `ShipPhase`).
- **Path separators.** Helpers normalize back slashes to forward slashes where it matters (`DocOnlyDetector.IsDocFile`, [src/ThroughlineBuild.Helpers/DocOnlyDetector.cs:45-46](../../src/ThroughlineBuild.Helpers/DocOnlyDetector.cs#L45-L46)).
- **Line endings.** `.gitattributes` pins LF (`text eol=lf`) for the brief templates and snapshot fixtures ([.gitattributes:1-3](../../.gitattributes#L1-L3)) so the byte-stable substitution that produces the worker brief is identical across Windows checkouts.

---

## Time and identity

- All timestamps are emitted at event-emission time. Sinks vary: `JsonlEventSink` records `ev.Timestamp` (set by the phase); `ShipPhase` events route through one `EmitAsync` helper stamping `DateTimeOffset.UtcNow` ([src/ThroughlineBuild.Phases/ShipPhase.cs:857-866](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L857-L866)) and the file-name builder uses `DateTimeOffset.Now`. No central clock-skew correction - useful for sorting within a session, not cross-machine correlation.
- `Environment.UserName` is not used by `build` (the orchestrator is identityless; the human is identified only by the Plane API token hitting the workspace).
- The commit-message-format rule (`{TKT-ID}: short description`) is followed by the worker / Claude Code sessions, not enforced by `build`. `build` itself never runs `git commit` - the worker subprocess commits inside the feature worktree; `build` only rebases, fast-forward-merges, and now pushes.

---

## What `build` does **not** assume

- It does not assume an IDE.
- It does not assume a build server or daemon.
- It does not assume node, python, ruby, or any runtime besides .NET (and even .NET is only required for `dotnet`-based checks; the verifier itself shells out to whatever the config names).
- It does not assume any specific shell - it spawns processes directly (`UseShellExecute = false`), never through `bash -c` or `cmd /c`.
- It does not assume a particular preview tool exists - `WorktreeDecrufter` knows to clean up `.preview.pid` / `.preview.meta` from a sibling preview workflow but `build` itself never creates them.
- It no longer assumes a purely local workflow: with a configured remote, `ship` fetches, may auto-rebase local `main`, and pushes. A remote that rejects the push (protected branch, auth failure) fails the ship.

---

## Loose ends

- **`BaseRefResolver` resolves a passed-in target branch** but hardcodes the `origin/` remote prefix ([src/ThroughlineBuild.Git/BaseRefResolver.cs:27](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L27)); plan/implement/review/decompose resolve against `BuildOptions.TargetBranch` (default `main`), while `ship` uses the fully-resolved `[work].target_branch`/`[ship]` settings. A repo whose remote is not `origin` would still mis-resolve the base ref outside ship.
- **Architecture-doc disagreement on push.** [docs/throughline-build-architecture.md:174](../throughline-build-architecture.md) (Section 5.9) still asserts "v1 is local-merge-only with no `git push origin main`" and the never-force-push rule. The never-force rule holds, but the no-push and no-fetch claims are now false (TLB-293/296/297; push is default-on, opt-out via `--no-push` / `[ship].push=false` per TLB-409/410). The architecture doc is stale here. (The `build ship` usage string in [src/ThroughlineBuild.Cli/CliUsage.cs:12](../../src/ThroughlineBuild.Cli/CliUsage.cs#L12) is accurate - it already documents the push + `--no-push` behavior.)
- **Slug truncation at 80 chars** is silent ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:38-50](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L38-L50)). Branch/worktree slugs are now id-only (`BuildTicketSlug`), so truncation/collision no longer bites them; `BuildBranchSlug` with a title is still reachable elsewhere and keeps the cap.
- **Non-ASCII handling** is silent strip / collapse ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:24-25](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L24-L25)), again only material to title-bearing slugs now that branch names carry the id alone.
- **Case-insensitive label/state/issue-type matching.** All three Plane caches key on `OrdinalIgnoreCase` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:150](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L150), [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:168](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L168), [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:186](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L186)), so a workspace with names differing only by case would alias to one entry.
- **No telemetry to a central service** - everything stays on the operator's box.
- **Container support** is not tested. Running `build` in a container with no DNS to Plane fails to start.
- **WSL paths** are not specifically handled - `.exe` on Windows RIDs is the only Windows-aware bit; the Windows AOT binary run from WSL has not been validated.
