# 08 - Workspace and Environment Assumptions

Last refreshed: 2026-07-26 (HEAD 00dc074)

What `build` assumes about the environment it runs in beyond the obvious - branch conventions, required tooling, OS specifics, CI behavior, places where the code branches on stack or platform.

For pure prerequisite tooling see [02-install-build-run.md](02-install-build-run.md). For configuration see [04-configuration.md](04-configuration.md).

---

## Repository layout

Status: Functional.

- A git repository, working tree (not a bare repo). A directory that is *not* yet a repo can be brought up by `build setup`, which runs `git init`, appends the managed `.gitignore` block, and makes the welcome commit (see below).
- A `.build/config.toml` exists somewhere at or above the cwd. `BuildConfig.FindConfigFile` walks up from cwd ([src/ThroughlineBuild.Cli/Config.cs:106-117](../../src/ThroughlineBuild.Cli/Config.cs#L106-L117)). Verbs that dispatch **before** config load in `Program`: `init` (:231), `settarget` (:294), `user-guide` (:304), `op-doc` (:313, `op-doc new` pre-pass at :137), and `models` (:403) ([src/ThroughlineBuild.Cli/Program.cs:231](../../src/ThroughlineBuild.Cli/Program.cs#L231)).
- The directory holding `.build/` is the project root - relative `events.log_directory` resolves against the config file's parent via `ResolveLogDirectory` ([src/ThroughlineBuild.Cli/Config.cs:174](../../src/ThroughlineBuild.Cli/Config.cs#L174)).
- `MainWorktreeResolver.ResolveAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs:12](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs#L12)) returns the first entry from `git worktree list` (git porcelain reports the main worktree first), falling back to the raw cwd on error, so `build` can be invoked from inside a feature worktree.

### Loose ends

- `build list`, `setup`, `sweep` and the ticket verbs all require the config; only the five pre-config verbs above run without it.

---

## Bootstrap: `build setup` (NEW)

Status: Functional.

`SetupCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/SetupCommand.cs:33-48](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L33-L48)) makes a fresh project workflow-ready, idempotently:

- **`git init`** when `ILocalRepoOps.IsGitRepository()` is false ([SetupCommand.cs:56-67](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L56-L67)).
- **`.gitignore` append.** `GitignoreManager.Merge` appends only the missing entries from the 12-entry `RequiredEntries` list (engine dirs `.build/config.toml`, `.build/*.md`, `.build/events/`, `.build/sessions/`, `.worktrees/`, `secrets/`, `.tmp/`, plus OS/editor noise) under the `# Throughline Build (managed by 'build setup')` header, preserving existing content ([src/ThroughlineBuild.Cli/LocalRepoSetup.cs:15-33](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs#L15-L33), merge at :60-78).
- **Welcome commit.** On a repo with zero commits, `WelcomeCommit.EnsureInitialCommit` stages `.gitignore` alone and commits `welcome to throughline build` so the first ship can resolve a base ref; failure (e.g. missing `user.name`/`user.email`) is a warning, not an error ([src/ThroughlineBuild.Cli/WelcomeCommit.cs:14-38](../../src/ThroughlineBuild.Cli/WelcomeCommit.cs#L14-L38)). This implies `build setup` assumes a configured git identity for full effect.
- **Plane provisioning.** Creates missing states/labels per `WorkspaceSchema` through `ITicketingProvisioner`; `--check` mutates nothing and exits non-zero on gaps.

### Loose ends

- The welcome commit only stages `.gitignore`; a repo with files but no commits still gets a `.gitignore`-only first commit, which is intentional but can surprise.

---

## Branch conventions

Status: Functional.

- **Base / target branch.** `main` by default; merge destination is `BuildConfig.ResolveTargetBranch()` = `[work].target_branch ?? [ship].base_branch` ([src/ThroughlineBuild.Cli/Config.cs:89](../../src/ThroughlineBuild.Cli/Config.cs#L89)); override with `build settarget <branch>`. `BaseRefResolver.ResolveAsync` is target-parameterized and prefers `origin/<target>`, falling back to local; the accumulation rule (TLB-411) prefers the *local* target when it is strictly ahead of origin so chain children stack on shipped siblings ([src/ThroughlineBuild.Git/BaseRefResolver.cs:24-64](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L24-L64); the `origin/` prefix is still hardcoded at :27).
- **Remote.** `origin`, configurable as `ship.remote` ([src/ThroughlineBuild.Cli/Config.cs:780](../../src/ThroughlineBuild.Cli/Config.cs#L780)).
- **Feature branches.** `ticket/<id>` - id only, no title slug (Windows MAX_PATH; TLB-408). `PhaseWorktreeLayout.Compute` builds branch + worktree names ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:7-16](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L7-L16)); `IsTicketBranch` still accepts the legacy `ticket/<id>-<slug>` prefix form and `MentionsBranch` uses a boundary regex so `ticket/24` never matches `ticket/240` (:29-47).
- **Chain integration branch (CHANGED).** A parent chain creates one shared worktree on `chain/<parent-id>` (`ChainIntegrationBranchFromId`, [src/ThroughlineBuild.Phases/ChainPhase.cs:2966](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2966)). This is no longer a commit-less placeholder: children ship into it (each child chain runs with `ChainTargetBranch` = the integration branch, [ChainPhase.cs:2550](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2550)), it is retained across runs for resume, refreshed by rebase against the base ref when reused (TLB-546, :2229-2245), and landed onto the configured target by the outermost chain (`LandRootIntegrationBranchAsync`, :2782). Cleanup is merged-gated via the chain success sweep or `build sweep` (see 05).
- **Batch-implement branch.** When the chain batches sibling implements into one warm worker session, all batch commits stack on the *first* ticket's `ticket/<id>` branch inside the integration worktree; `BatchCommitVerifier` then maps commits back to tickets (see 09).
- **Chain preflight wrong-branch guard (NEW).** The outermost chain refuses up front when the main worktree is not checked out on the target branch (or detached): `GateFailure` kind `chain_preflight_wrong_branch`, outcome `RefusedWrongBranch`, exit 2 ([src/ThroughlineBuild.Phases/ChainPhase.cs:198-236](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L198-L236)). It mirrors ship's own pre-merge guard so the operator fixes the branch before any planning, not after.
- **Ship wrong-branch guard.** `ShipPhase` still verifies the main worktree is on the target before merging - unconditional, including `main` (`wrong_worktree_branch`, [src/ThroughlineBuild.Phases/ShipPhase.cs:260-275](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L260-L275)); the chain landing repeats it (`chain_landing_wrong_branch`, [ChainPhase.cs:2791-2804](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L2791-L2804)).
- **Auto-rebase of the local target.** On `DivergedNoConflict` (probed via `IGitClient.ProbeDivergenceAsync`, `git merge-tree --write-tree`) ship rebases the local target onto the remote and emits `TargetAutoRebased` ([src/ThroughlineBuild.Phases/ShipPhase.cs:357-390](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L357-L390)); `DivergedWithConflict` or `--no-auto-merge` block at `Fetch`.
- **Push: default-on, opt-out.** `useRemote = remoteConfigured && !NoPush` ([src/ThroughlineBuild.Phases/ShipPhase.cs:285](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L285)); `--no-push` / `[ship].push = false` make ship purely local (`fetch_skipped`, :288-296). An unpushed remote target is treated as not-diverged (`remote_branch_absent`, :322). The chain root landing has the symmetric no-remote guard (`chain_landing_push_skipped`, reason `no_remote`).
- **No force operations.** No `git push --force`, `git reset --hard`, or `git rebase -i` anywhere; failed rebases are aborted with `git rebase --abort`.

### Loose ends

- **`BaseRefResolver` still hardcodes `origin/`** ([src/ThroughlineBuild.Git/BaseRefResolver.cs:27](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L27)); a repo whose remote is not `origin` mis-resolves the base ref outside ship.
- The chain preflight guard and ship guard compare against the same target and must agree; a mid-run `settarget` change would desynchronize them.

---

## Working-tree hygiene

Status: Functional.

`build` assumes - and enforces - a clean working tree at phase boundaries via `WorkingTreeHygieneGate`.

- **Preflight gates.** `CheckAsync` rejects unmerged/conflicted paths and dangling stash entries unrelated to the ticket branch ([src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs:24-62](../../src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs#L24-L62)). It runs before implement (`GateFailure` kind `hygiene_gate`, [src/ThroughlineBuild.Phases/ImplementPhase.cs:139-142](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L139-L142)) and once at outermost chain start (kind `hygiene_gate_preflight` -> `RefusedDirtyTree`, [src/ThroughlineBuild.Phases/ChainPhase.cs:250](../../src/ThroughlineBuild.Phases/ChainPhase.cs#L250)); chain preflight also refuses tracked changes in the main checkout (kind `chain_preflight_dirty`, :282; untracked ignored). `ShipPreflightAsync` checks both worktrees plus the repo-global stash ([WorkingTreeHygieneGate.cs:83-156](../../src/ThroughlineBuild.Phases/WorkingTreeHygieneGate.cs#L83-L156), invoked at [src/ThroughlineBuild.Phases/ShipPhase.cs:209](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L209)).
- **`git stash` is banned for workers and the verifier.** Implement briefs carry "Do NOT use git stash" ([src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md:31](../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L31)); review briefs forbid `git stash`, `git checkout`, `git reset`, `git rebase` ([src/ThroughlineBuild.Briefs/Templates/claude-code/review.md:20](../../src/ThroughlineBuild.Briefs/Templates/claude-code/review.md#L20)). The hygiene gate's unrelated-stash detection backstops the prompt.
- **Post-phase validation.** After the implement worker exits, dirty tracked files trigger one bounded retry (`dirty_worktree_first_attempt` -> `dirty_worktree_retry_failed`, [src/ThroughlineBuild.Phases/ImplementPhase.cs:437-464](../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L437-L464)); after the verifier exits the same check hard-fails with no retry (`dirty_worktree_after_review`, [src/ThroughlineBuild.Phases/ReviewPhase.cs:266](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L266)).

### Loose ends

- The gate ignores untracked files everywhere by policy; a worker that litters untracked scratch files is invisible to all hygiene checks.

---

## Required tooling

Spelled out in [02-install-build-run.md](02-install-build-run.md). Recap of the assumptions:

- `git` >= 2.5 (worktrees); the divergence probe needs `git merge-tree --write-tree` (git >= 2.38).
- A worker CLI on PATH or absolute in config: `claude`, plus `codex` / `gemini` / `copilot` for those agents. A missing executable is a graceful `Status.Failed`, not a crash (see OS assumptions). The configured claude-code model is validated at config load by `ClaudeCodeModelValidator.Validate` - only the tier aliases `haiku`/`sonnet`/`opus` or a full `claude-*` slug (optionally `anthropic:`-prefixed) are accepted; anything else (canonically `model = "fable"`) is an immediate `Config error` instead of a mid-chain envelope failure ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs:22-47](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeModelValidator.cs#L22-L47), wired at [src/ThroughlineBuild.Cli/Config.cs:646](../../src/ThroughlineBuild.Cli/Config.cs#L646), TLB-544).
- `build models refresh` and connected `build init` additionally assume the Codex CLI supports `codex debug models`, which `CodexModelProbe` parses ([src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs:36-64](../../src/ThroughlineBuild.Workers.Codex/CodexModelProbe.cs#L36-L64)).
- `.NET 10 SDK` for builds (all 20 `src/` projects target `net10.0`, e.g. [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:4](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L4)) and for `dotnet`-based checks.
- A C/C++ toolchain for AOT publish.
- **Host-coupled native-AOT link overrides (win-x64), untracked.** A developer may supply machine-specific `Directory.Build.props` or `Directory.Build.targets` files for MSVC and Windows SDK discovery, but both paths are gitignored ([.gitignore:17-19](../../.gitignore#L17-L19)) and absent from a fresh clone by design. Windows native publish otherwise relies on the default toolchain discovery.
- Network to Plane and (for `close`/`defer`/`reopen` reason translation only) Anthropic.

### Loose ends

- Because `Directory.Build.props` is untracked, a fresh clone's native publish behavior differs silently from this machine's; nothing documents the expected file beyond the gitignore comment.

---

## Operating system assumptions

Status: Functional. `build` is cross-platform - CI builds three RIDs (see CI section). Platform-specific branches:

- **Worker executable not found is non-fatal.** All four agents wrap `process.Start()` in `catch (Win32Exception)` and return a `Status.Failed` `WorkerResult` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:106](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L106), [src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:108](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L108), [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:90](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L90), [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:90](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L90)).
- **Worker stream encoding is pinned to UTF-8 (NEW).** `ProcessStreamEncoding.ApplyUtf8` sets `StandardOutputEncoding`/`StandardErrorEncoding` to UTF-8 on every worker spawn ([src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs:19-23](../../src/ThroughlineBuild.Workers.Common/ProcessStreamEncoding.cs#L19-L23); applied at [ClaudeCodeAgent.cs:46](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L46) and in `CodexModelProbe`). Without it, Windows decodes child output with the OEM code page (CP437/CP850), garbling UTF-8 punctuation in the progress digest, debug captures, and event log. This removes the prior "worker output encoding is the console's problem" assumption on Windows.
- **EDITOR resolver Windows fallback.** `--review` resolves `$EDITOR`, then `vim, nano, code --wait` plus `notepad.exe` on Windows; on-PATH probe uses `where` vs `which` ([src/ThroughlineBuild.Cli/ReviewLoop.cs:265-286](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L265-L286)).
- **Windows reparse points** are pre-cleaned by `WorktreeDecrufter` before directory deletion (junctions under `node_modules`; [src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:111-136](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L111-L136)).
- **Path case folding.** Windows-only lowercase normalization in `MainWorktreeLock` ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:13-15](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L13-L15)) and case-insensitive comparison in ship's exe-in-worktree preflight.
- **`build.sh` adds `.exe`** for Windows RIDs ([build.sh:15-16](../../build.sh#L15-L16)).
- **Interactive Claude hosting is platform-specific.** Windows uses ConPTY with a mandatory kill-on-close job object; Linux and macOS use a native PTY plus process-group containment. `InteractiveClaudeProcessLauncherFactory.Create` selects the implementation ([InteractiveClaudeProcessHost.cs:48](../../src/ThroughlineBuild.Workers.ClaudeCode/InteractiveClaudeProcessHost.cs#L48)). The capability preflight requires Claude CLI 2.1.177 or newer for this transport.
- **Local install needs a home or explicit destination.** `build.sh` defaults `INSTALL_DIR` to `$HOME/.local/bin`; callers without a usable home must set `INSTALL_DIR`.
- **`<InvariantGlobalization>true</InvariantGlobalization>`** ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:11](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L11)) - invariant culture everywhere, no ICU at runtime.
- **`SlugBuilder` strips non-ASCII** silently - mostly moot for branch names since they carry the ticket id alone.

No subprocess is launched through a shell - every spawn sets `UseShellExecute = false` and passes the executable + args directly. There is no `bash -c` / `cmd /c` wrapping in the binary.

### Loose ends

- Worker *stdin* (the brief) is still written with the process default encoding; only stdout/stderr are pinned. ASCII-only briefs make this moot in practice.

---

## CI integration

Status: Functional. [.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow, one job per OS via `strategy.matrix` ([.github/workflows/build.yml:11-23](../../.github/workflows/build.yml#L11-L23)), on push/PR to `main`.

OS x RID matrix (the `artifact` matrix key carries `.exe` only on Windows): `macos-latest`/`osx-arm64`, `windows-latest`/`win-x64`, `ubuntu-latest`/`linux-x64`.

Steps ([.github/workflows/build.yml:25-36](../../.github/workflows/build.yml#L25-L36)): checkout@v4; setup-dotnet@v4 with `dotnet-version: '10.x'`; `dotnet restore --nologo -v q`; `dotnet test --no-restore --nologo -v q --logger "console;verbosity=minimal"` (quiet flags are new since the last refresh); `dotnet publish src/ThroughlineBuild.Cli -r <rid> -c Release`; upload-artifact@v4 named `build-<rid>`.

CI builds **only the main `build` binary**; `token-audit` and `analyze-event-log` are built only by the local `build.sh`. Still absent: release tagging, signing, coverage, SAST, deployment. Note CI's Windows leg publishes *without* the local `Directory.Build.props` (untracked), so it exercises default toolchain discovery.

### Loose ends

- No CI lane runs the tools under `src/tools/`; a compile break there only surfaces locally.

---

## Stack-specific code paths

Status: Functional (stack-neutral by design).

`ProjectContext` flows from `[project]` in `.build/config.toml` into the brief context. New `[project]` keys since the last refresh ([src/ThroughlineBuild.Cli/Config.cs:267-272](../../src/ThroughlineBuild.Cli/Config.cs#L267-L272) for the known-key list): `convention_files` (files inlined into every implement brief, :908), `preload_context` (default true - preload named-input/convention files from the live worktree, :915), and `context_hygiene` (default false - opt-in lean-planning experiments, :918).

The verifier and ship checks know nothing about languages: `AutomatedChecksRunner.RunAsync` ([src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs:16](../../src/ThroughlineBuild.Verification/AutomatedChecksRunner.cs#L16)) spawns each configured `CheckSpec` and treats exit 0 as pass. `CheckSpec` ([src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:17](../../src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs#L17)) gained two stack-agnostic fields that shape workspace requirements:

- **`role`** - `gating` / `advisory` / `setup`, validated in config ([src/ThroughlineBuild.Cli/Config.cs:468](../../src/ThroughlineBuild.Cli/Config.cs#L468)). Lint/format are derived as advisory so they never hard-gate.
- **`canary`** - per-check broken-input files parsed by `ParseCanary` ([src/ThroughlineBuild.Cli/Config.cs:475-489](../../src/ThroughlineBuild.Cli/Config.cs#L475-L489)), used by the gate's vacuity prover (see 09). The workspace must tolerate a canary file being briefly materialized and removed in the ticket worktree.

**Derived-profile hygiene requirements (NEW).** The scaffold profile deriver's prompt ([src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md:69-84](../../src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md#L69-L84)) imposes two workspace-relevant rules on every derived check, in the target stack's own idiom:

- **Hermetic test command** (8a90e5f): the test command must exclude the engine's working directories (`.worktrees/`, `.build/`) and nested installs - e.g. vitest `test.exclude`, pytest `--ignore`/`norecursedirs`, jest `testPathIgnorePatterns`; project-scoped runners like `dotnet test` are already hermetic.
- **No user-global tool caches** (50645c7): checks run in throwaway worktrees against different code, so path/mtime-keyed user-global caches (e.g. SwiftLint's `~/Library/Caches/SwiftLint`) can replay wrong results into the ship baseline; derived checks must pass cache-disabling flags (`swiftlint --no-cache` always) and never opt into linter caches.

These are prompt-enforced derivation rules, not engine code - the engine just runs the commands.

### Loose ends

- Nothing validates that a hand-written check is hermetic or cache-free; the rules bind only worker-derived profiles.

---

## Worktree-aware behavior

Status: Functional.

- **Verifier runs in the feature worktree.** `ReviewPhase` builds the brief and runs the verifier + checks against the located worktree (`canonicalWorktreePath`, [src/ThroughlineBuild.Phases/ReviewPhase.cs:91-136](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L91-L136)); a missing worktree is reconstructed from the ticket branch via `CheckoutWorktreeAsync` before failing (:129-136, TLB-407). Review attributes against worktree HEAD, emitting `implemented_at_superseded` when the marker is stale (:185, TLB-414).
- **Ship regression checks run in the feature worktree;** the baseline run executes the same checks in the detached `.worktrees/baseline-<sha>` worktree, and the contradiction recheck runs them once more in a fresh control worktree on the base SHA ([src/ThroughlineBuild.Phases/ShipPhase.cs:544-595](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L544-L595)). Under a chain, ship advances the **integration branch inside the integration worktree** (children) while only the root landing touches the main worktree.
- **Pre-flight: exe-in-worktree.** Ship refuses if the running binary lives inside the worktree being rebased ([src/ThroughlineBuild.Phases/ShipPhase.cs:186-203](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L186-L203)).
- **Gate control runs use throwaway worktrees.** `GateControlProber` (TLB-538) creates a temporary worktree at the base SHA to re-run failed gate checks, and `GateVacuityProver` materializes canaries in the ticket worktree with leak-back assertions (see 09); both assume `.worktrees/` has room for short-lived extra checkouts.

### Loose ends

- A worktree whose branch was renamed *and* whose path no longer matches the layout still reports "feature worktree not found".

---

## Worker-subprocess environment

`ClaudeCodeAgent.ConfigureEnvironment` ([ClaudeCodeAgent.cs:463](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L463)):

- Removes `ANTHROPIC_API_KEY` (:620) so the worker authenticates via Claude Code OAuth.
- Sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from config when configured (:623-624); caller-supplied env overrides win.
- Working directory is the worktree path (:39) so the worker sees only the feature checkout.
- **Effort-gated tool restriction.** When `[project].context_hygiene = true` and the ticket size is S, `ImplementPhase` dispatches with `LeanPlanning: true`; `ClaudeCodeAgent.BuildArgs` always disallows Agent/Task and additionally disallows TodoWrite for lean planning ([ClaudeCodeAgent.cs:392](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L392)).
- **Verifier allowlist honesty check (NEW, TLB-478).** `verifier_allowed_tools` is enforced only by workers that forward a per-tool allowlist to their CLI (`claude-code`, `copilot`); `VerifierToolEnforcement.UnenforcedWarning` prints a startup warning when the review worker is codex/gemini, which ignore the allowlist and run the verifier unsandboxed ([src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs:20-29](../../src/ThroughlineBuild.Cli/VerifierToolEnforcement.cs#L20-L29)).

Other env vars pass through from the parent process (PATH, HOME, etc.).

The interactive Claude transport additionally reads `CLAUDE_CONFIG_DIR` or the applicable home/profile directory to locate the user trust file and persisted project transcripts. It writes a trust record for the canonical worktree and uses OS-temp run/lock directories; these are deliberate host-side state, not repository artifacts.

### Loose ends

- The lean-planning disallow list is claude-code-only; other agents receive `LeanPlanning` but have no equivalent CLI mechanism.

---

## Filesystem assumptions

- **Case-sensitive path matching** in `DriftComparator.Compare` (ordinal `List.Contains`, called out in the comment at [src/ThroughlineBuild.Helpers/DriftComparator.cs:17-19](../../src/ThroughlineBuild.Helpers/DriftComparator.cs#L17-L19)); worktree/path lookups elsewhere use `OrdinalIgnoreCase` deliberately.
- **Path separators** are normalized where it matters (`DocOnlyDetector.IsDocFile`, [src/ThroughlineBuild.Helpers/DocOnlyDetector.cs:43](../../src/ThroughlineBuild.Helpers/DocOnlyDetector.cs#L43)).
- **Line endings.** `.gitattributes` pins LF for all four template directories and test fixtures ([.gitattributes:1-6](../../.gitattributes#L1-L6)) so byte-stable brief substitution is identical across Windows checkouts.

### Loose ends

- None new.

---

## Time and identity

- Timestamps are emitted at event time (`DateTimeOffset.UtcNow` in phase emit helpers; `DateTimeOffset.Now` in the file-name builder). No clock-skew correction.
- `build` itself never runs `git commit` for ticket work - the worker commits inside the feature worktree; `build` rebases, fast-forward-merges, and pushes. The one exception is the `build setup` welcome commit (`WelcomeCommit`, `.gitignore` only), which requires a configured git identity.
- The commit-message-format convention is enforced by the briefs, not the binary.

### Loose ends

- None new.

---

## What `build` does **not** assume

- No IDE, no build server or daemon.
- No runtime besides .NET for the engine itself; checks shell out to whatever the config names.
- No shell - processes are spawned directly.
- No preview tool - `.preview.pid`/`.preview.meta` cleanup is compatibility-only.
- No remote: ship and the chain landing both degrade to local-only merges when `origin` is absent (`fetch_skipped` / `chain_landing_push_skipped`).
- No pre-existing repo or Plane schema: `build setup` provisions both.

---

## Loose ends

- **`BaseRefResolver` remote prefix** is still the literal `origin/` (see Branch conventions).
- **Global install is outside repository cleanup.** Removing the clone does not remove the three executables installed by `build.sh`; uninstall them from `INSTALL_DIR` separately.
- **Case-insensitive Plane name caches** ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:386-429](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L386-L429), `OrdinalIgnoreCase` dictionaries) alias names differing only by case.
- **Container / WSL support** untested; no telemetry leaves the operator's box.
- **`Directory.Build.props` is now machine-local** - the only documentation of its required shape is the gitignore comment and this doc set.
