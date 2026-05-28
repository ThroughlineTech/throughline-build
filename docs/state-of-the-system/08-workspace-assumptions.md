# 08 - Workspace and Environment Assumptions

What `build` assumes about the environment it runs in beyond the obvious - branch conventions, required tooling, OS specifics, CI behavior, places where the code branches on stack or platform.

For pure prerequisite tooling see [02-install-build-run.md](02-install-build-run.md). For configuration see [04-configuration.md](04-configuration.md).

---

## Repository layout

- A git repository, working tree (not a bare repo).
- A `.build/config.toml` exists somewhere at or above the cwd. `BuildConfigLoader.FindConfigFile` walks up from cwd ([src/ThroughlineBuild.Cli/Config.cs:60-71](../../src/ThroughlineBuild.Cli/Config.cs#L60-L71)).
- The directory holding `.build/` is treated as the project root - `events.log_directory` resolves against it ([src/ThroughlineBuild.Cli/Config.cs:106-112](../../src/ThroughlineBuild.Cli/Config.cs#L106-L112)).
- `MainWorktreeResolver.ResolveAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs:5-26](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs#L5-L26)) calls `git worktree list --porcelain` and uses the first entry. Operators can invoke `build` from inside a feature worktree and the resolved cwd will still be the main worktree root.

---

## Branch conventions

- **Base branch.** `main`. Configurable as `ship.base_branch` ([src/ThroughlineBuild.Cli/Config.cs:267](../../src/ThroughlineBuild.Cli/Config.cs#L267)) but every other phase assumes `main` via `BaseRefResolver.ResolveAsync` which tries `origin/main` then falls back to `main` ([src/ThroughlineBuild.Git/BaseRefResolver.cs:5-26](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L5-L26)).
- **Remote.** `origin`. Configurable as `ship.remote` ([src/ThroughlineBuild.Cli/Config.cs:266](../../src/ThroughlineBuild.Cli/Config.cs#L266)).
- **Feature branches.** `ticket/<slug>` where slug is derived from ticket id + title by `SlugBuilder.BuildBranchSlug` ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:5-76](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L5-L76)). 80-char cap, hyphen-collapsed, non-ASCII stripped silently.
- **Worktree paths.** `.worktrees/ticket-<slug>/` (relative to main worktree). Set by `PhaseWorktreeLayout.Compute` ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:3-17](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L3-L17)).
- **No `git push origin main` ever.** `ShipPhase` only fast-forward-merges into local main; architecture Section 5.9 commits to this rule.
- **No force operations.** No code path calls `git push --force`, `git reset --hard`, or `git rebase -i`.

If no remote is configured (i.e., `git config --get remote.origin.url` returns nothing), `ShipPhase` notes the missing remote, skips the fetch, and rebases onto local `main` ([src/ThroughlineBuild.Phases/ShipPhase.cs:170-180](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L170-L180)).

---

## Required tooling

Spelled out in [02-install-build-run.md](02-install-build-run.md). Recap of the assumptions:

- `git` >= 2.5 (worktrees).
- `claude` CLI on PATH or absolute in config.
- `.NET 8 SDK` for builds.
- A C/C++ toolchain for AOT publish (MSVC / Xcode CLT / gcc).
- Network to Plane and (for some verbs) Anthropic.

---

## Operating system assumptions

`build` is genuinely cross-platform - CI builds three RIDs ([.github/workflows/build.yml](../../.github/workflows/build.yml)) and the architecture targets Mac / Windows / Linux. A few spots branch on platform:

- **Windows symlink reparse points** in `WorktreeDecrufter.cs` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:112](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L112)) get special handling before directory deletion.
- **`build.sh` adds `.exe`** for Windows targets only ([build.sh:7-8](../../build.sh#L7-L8)).
- **`<InvariantGlobalization>true</InvariantGlobalization>`** ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) means the binary uses invariant culture everywhere - dates, casing, comparisons. No ICU required at runtime.
- **`SlugBuilder` strips non-ASCII** silently ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:45-55](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L45-L55)) so a ticket title with non-Latin characters produces a slug derived only from the ASCII subset.

ASCII-only output at tool boundaries is an explicit constraint (architecture Section 2). Operators with non-ASCII in tickets see escaped or stripped artifacts in markers, branch names, and filenames.

---

## CI integration

[.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow, single job per OS. On push or PR to `main`:

1. Checkout.
2. `actions/setup-dotnet@v4` with `dotnet-version: '8.x'`.
3. `dotnet restore`.
4. `dotnet test --no-restore`.
5. `dotnet publish src/ThroughlineBuild.Cli -r <rid> -c Release --no-restore`.
6. `actions/upload-artifact@v4` with the publish output.

What is **not** in CI:

- No release tagging.
- No signing.
- No automated dogfooding (the architecture's promotion criteria "five real tickets handled without surprise" is a manual judgment).
- No coverage report, no SAST, no AOT analyzers.
- No deployment of artifacts past the upload step.

---

## Stack-specific code paths in command bodies

`ProjectContext` flows from `.build/config.toml`'s `[project]` section into the brief context dictionary. That means brief templates can reference `{{project_build_command}}`, `{{project_test_command}}`, etc., and the worker reads what stack to use. This is the only stack-aware code path in the binary - everything else is stack-neutral.

The brief templates themselves do not branch by stack today; they embed the project's commands as suggestions. A non-`.NET` project using `build` would still get the same template shape and pass its own commands through context.

---

## Worktree-aware behavior

- **Pre-flight: exe-in-worktree check.** `ShipPhase` refuses to merge if the running `build` binary is itself inside the worktree being shipped ([src/ThroughlineBuild.Phases/ShipPhase.cs:118-138](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L118-L138)) - because killing the binary midway would leave inconsistent state. The check compares `processPathProvider()` (defaults to the running process path) against the worktree path. Failure: `ShipFailureStage.PreFlight`, exit 1.
- **Pre-flight: dirty-tracked check.** `ShipPhase` refuses if either the feature or main worktree has uncommitted tracked changes ([src/ThroughlineBuild.Phases/ShipPhase.cs:140-162](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L140-L162)). Untracked files are ignored.
- **Worktree location.** `ReviewPhase` and `ShipPhase` locate the feature worktree by walking `git worktree list --porcelain` and matching either branch name or path against the deterministic layout ([src/ThroughlineBuild.Phases/ReviewPhase.cs:71-92](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L71-L92)). A worktree manually renamed loses this link.

---

## Worker-subprocess environment

`ClaudeCodeAgent` modifies the child env explicitly:

- Removes `ANTHROPIC_API_KEY` ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:374](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L374)) so the worker can only authenticate through Claude Code OAuth. Mixing keys would cause the worker to bill against the wrong account.
- Sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS` from config.
- Sets working directory to the worktree path (so the worker sees only the feature branch checkout).

Other env vars from the parent process pass through. The worker inherits the parent's PATH, HOME, USER, etc.

---

## Filesystem assumptions

- **Case-sensitive paths.** The codebase assumes case-sensitive matching in places (`DriftComparator` uses ordinal comparison, [src/ThroughlineBuild.Helpers/DriftComparator.cs:19](../../src/ThroughlineBuild.Helpers/DriftComparator.cs#L19)). Operates correctly on case-insensitive filesystems for most paths because git itself preserves case.
- **Path separators.** Forward and back slashes are normalized in helpers where it matters (`DocOnlyDetector` normalizes to forward slashes, [src/ThroughlineBuild.Helpers/DocOnlyDetector.cs:46](../../src/ThroughlineBuild.Helpers/DocOnlyDetector.cs#L46)).
- **Line endings.** `.gitattributes` pins LF for brief templates so the byte-stable substitution that produces the worker brief is identical across Windows checkouts.

---

## Time and identity

- All timestamps are `DateTimeOffset.Now` at the moment of event emission. No central clock skew correction - useful for sorting events within a session, not for cross-machine correlation.
- `Environment.UserName` is not used by `build` (the orchestrator is identityless; the human is identified only by the Plane API token that is hitting the workspace).
- Architecture rule from operator's global `CLAUDE.md` about commit message format `{TKT-ID}: short description` is followed by humans / Claude Code sessions, not enforced by `build` (which never commits).

---

## What `build` does **not** assume

- It does not assume an IDE.
- It does not assume a build server or daemon.
- It does not assume node, python, ruby, or any runtime besides .NET.
- It does not assume any specific shell - it spawns processes directly, not via shell.
- It does not assume a particular preview tool exists - `WorktreeDecrufter` knows to clean up `.preview.pid` from a sibling preview workflow but `build` itself never creates one.

---

## Loose ends

- **`origin/main` assumption** is hardcoded in `BaseRefResolver`; configurable in `[ship]` but not for plan/implement/review. A repo with a different base branch will fail those phases until refactored.
- **Slug truncation at 80 chars** is silent ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:30-74](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L30-L74)) - very long ticket titles collide.
- **Non-ASCII handling** is silent strip / collapse. Tickets in non-Latin languages would get nearly-empty slugs.
- **Case-sensitive label matching** would matter if the Plane workspace had labels differing only by case - currently the lookup is `OrdinalIgnoreCase`, so this is safe.
- **No telemetry to a central service** - everything stays on the operator's box. Architecture treats this as a feature; downstream this means there is no central dashboard of "ticket failures across the org".
- **Container support** is not tested. Architecture targets bare-metal / VM / desktop; running `build` inside a container with no DNS to Plane would just fail to start.
- **WSL paths** are not specifically handled - `.exe` extension on Windows RIDs is the only Windows-aware bit; running the Windows AOT binary from WSL has not been validated.
