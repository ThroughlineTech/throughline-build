# 08 - Workspace and Environment Assumptions

What `build` assumes about the environment it runs in beyond the obvious - branch conventions, required tooling, OS specifics, CI behavior, places where the code branches on stack or platform.

For pure prerequisite tooling see [02-install-build-run.md](02-install-build-run.md). For configuration see [04-configuration.md](04-configuration.md).

---

## Repository layout

Status: Functional.

- A git repository, working tree (not a bare repo).
- A `.build/config.toml` exists somewhere at or above the cwd. `BuildConfigLoader.FindConfigFile` walks up from cwd ([src/ThroughlineBuild.Cli/Config.cs:64-75](../../src/ThroughlineBuild.Cli/Config.cs#L64-L75)). The exception is `build init`, which creates that file and runs before config load ([src/ThroughlineBuild.Cli/Program.cs:129-144](../../src/ThroughlineBuild.Cli/Program.cs#L129-L144)).
- The directory holding `.build/` is treated as the project root - relative `events.log_directory` resolves against the parent of the config file ([src/ThroughlineBuild.Cli/Config.cs:110-116](../../src/ThroughlineBuild.Cli/Config.cs#L110-L116)).
- `MainWorktreeResolver.ResolveAsync` ([src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs:12-25](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs#L12-L25)) returns the first entry from `git worktree list` (git porcelain always reports the main worktree first), falling back to the raw cwd on any error. Operators can invoke `build` from inside a feature worktree and the resolved cwd will still be the main worktree root ([src/ThroughlineBuild.Cli/Program.cs:146-149](../../src/ThroughlineBuild.Cli/Program.cs#L146-L149)).

---

## Branch conventions

Status: Functional.

- **Base branch.** `main`. Configurable as `ship.base_branch` (default `"main"`, [src/ThroughlineBuild.Cli/Config.cs:349](../../src/ThroughlineBuild.Cli/Config.cs#L349)) but every other phase assumes `main` via `BaseRefResolver.ResolveAsync`, which tries the literal `origin/main` then falls back to the literal `main` ([src/ThroughlineBuild.Git/BaseRefResolver.cs:12-25](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L12-L25)) - both refs are hardcoded, not read from config.
- **Remote.** `origin`. Configurable as `ship.remote` (default `"origin"`, [src/ThroughlineBuild.Cli/Config.cs:348](../../src/ThroughlineBuild.Cli/Config.cs#L348)).
- **Feature branches.** `ticket/<slug>` where slug is derived from ticket id + title by `SlugBuilder.BuildBranchSlug` ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:10-43](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L10-L43)). 80-char cap on a hyphen boundary, hyphen-collapsed, non-ASCII stripped silently. Branch name and worktree path are produced together by `PhaseWorktreeLayout.Compute` ([src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs:5-11](../../src/ThroughlineBuild.Helpers/PhaseWorktreeLayout.cs#L5-L11)).
- **Worktree paths.** `.worktrees/ticket-<slug>/` (absolute, rooted at the main worktree).
- **Auto-rebase of local `main` (NEW, TLB-296/297/298).** When the remote exists and local `main` has diverged from `origin/main`, `ShipPhase` probes the divergence subspecies via `IGitClient.ProbeDivergenceAsync` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:866-912](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L866-L912), uses `git merge-tree --write-tree` to detect conflicts without mutating). On `DivergedNoConflict` (and unless `--no-auto-merge` is set) it rebases local `main` onto `origin/main` and emits a `MainAutoRebased` event ([src/ThroughlineBuild.Phases/ShipPhase.cs:230-298](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L230-L298)). On `DivergedWithConflict`, or when `--no-auto-merge` is passed, it posts `ship_blocked: ... diverged; manual resolution required` and aborts. The `--no-auto-merge` flag is a bare bool stripped in the CLI pre-pass ([src/ThroughlineBuild.Cli/Program.cs:54-55](../../src/ThroughlineBuild.Cli/Program.cs#L54-L55)) and threaded into `ShipOptions.NoAutoMerge`.
- **Push to `origin` after FF merge (NEW, TLB-293).** After the fast-forward merge into local `main`, `ShipPhase` runs `git push <remote> <baseBranch>` ([src/ThroughlineBuild.Phases/ShipPhase.cs:389-397](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L389-L397), [src/ThroughlineBuild.Git/ProcessGitClient.cs:488-500](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L488-L500)). A push failure fails the ship at `ShipFailureStage.Push`. This is the inverse of the old "never pushes" rule - see Loose ends for the architecture-doc disagreement.
- **No force operations.** No code path calls `git push --force`, `git reset --hard`, or `git rebase -i`. A failed auto-rebase is aborted with `git rebase --abort` rather than reset.

If no remote is configured (`IGitClient.RemoteExistsAsync` returns false), `ShipPhase` emits `fetch_skipped` (reason `no_remote`), skips fetch / auto-rebase / push, and rebases the feature branch onto local `main` ([src/ThroughlineBuild.Phases/ShipPhase.cs:173-190](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L173-L190)).

### Loose ends

- **`origin/main` is hardcoded** in `BaseRefResolver` ([src/ThroughlineBuild.Git/BaseRefResolver.cs:17-23](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L17-L23)). The `ship.base_branch` / `ship.remote` config only reaches `ShipPhase`; plan/implement/review/decompose still assume `main` and `origin`.
- **`--no-auto-merge` is the only knob** for the new auto-rebase. There is no per-config opt-out, and no flag to skip the push independently of the rebase.

---

## Required tooling

Spelled out in [02-install-build-run.md](02-install-build-run.md). Recap of the assumptions:

- `git` >= 2.5 (worktrees). The auto-rebase divergence probe additionally needs `git merge-tree --write-tree` (git >= 2.38).
- A worker CLI on PATH or absolute in config: `claude` for the claude-code agent, plus `codex` / `gemini` / `copilot` for those agents. The configured agent's `executable` is resolved per `[workers.<agent>]` ([src/ThroughlineBuild.Cli/Config.cs:203-289](../../src/ThroughlineBuild.Cli/Config.cs#L203-L289)). A missing executable is now a graceful failure, not a crash (see OS assumptions).
- `.NET 8 SDK` for builds, and for any verifier / regression `CheckSpec` that invokes `dotnet` (the project's `test_command`).
- A C/C++ toolchain for AOT publish (MSVC / Xcode CLT / gcc).
- Network to Plane and (for some verbs) Anthropic.

---

## Operating system assumptions

Status: Functional. `build` is genuinely cross-platform - CI builds three RIDs ([.github/workflows/build.yml:11-23](../../.github/workflows/build.yml#L11-L23)) and the architecture targets Mac / Windows / Linux. The platform-specific branches:

- **Worker executable not found is non-fatal (NEW, 0f9d114).** `ClaudeCodeAgent.ExecuteAsync` wraps `process.Start()` in a `try/catch (Win32Exception)` and returns a `Status.Failed` `WorkerResult` with a "Worker executable not found" reason instead of crashing ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:85-96](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L85-L96)). The same guard is present in all four worker agents ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:80-83](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L80-L83), [src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:80-84](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L80-L84), [src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:80-84](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L80-L84)). This was the "Don't crash when Claude isn't found in Bash in Windows" fix (commit 0f9d114 covered claude-code + codex; gemini/copilot match) - on Windows a `.cmd`/`.ps1` shim that is not on PATH surfaces as a `Win32Exception` rather than a clean exit.
- **EDITOR resolver Windows fallback.** The `--review` editor loop resolves `$EDITOR` first, then a fallback chain `vim, nano, code --wait` plus `notepad.exe` only on Windows ([src/ThroughlineBuild.Cli/ReviewLoop.cs:259-278](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L259-L278)). The on-PATH probe uses `where` on Windows and `which` elsewhere ([src/ThroughlineBuild.Cli/ReviewLoop.cs:280-302](../../src/ThroughlineBuild.Cli/ReviewLoop.cs#L280-L302)).
- **Windows reparse points** in `WorktreeDecrufter` get pre-cleaned before directory deletion: on Windows it walks `node_modules`, finds subdirectories whose `LinkTarget` is set, and deletes those reparse-point links first ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:111-136](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L111-L136)).
- **Path comparison case folding.** Windows-specific case-insensitive path comparison appears in the ship exe-in-worktree pre-flight ([src/ThroughlineBuild.Phases/ShipPhase.cs:135](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L135)) and the `MainWorktreeLock` key normalization, which lowercases the path on Windows ([src/ThroughlineBuild.Helpers/MainWorktreeLock.cs:14-15](../../src/ThroughlineBuild.Helpers/MainWorktreeLock.cs#L14-L15)).
- **`build.sh` adds `.exe`** for Windows RIDs only ([build.sh:15-16](../../build.sh#L15-L16)).
- **`<InvariantGlobalization>true</InvariantGlobalization>`** ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) means the binary uses invariant culture everywhere - dates, casing, comparisons. No ICU required at runtime.
- **`SlugBuilder` strips non-ASCII** silently ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:45-56](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L45-L56)) so a ticket title with non-Latin characters produces a slug derived only from the ASCII subset.

No subprocess is launched through a shell - every spawn sets `UseShellExecute = false` and passes the executable + argument list directly (e.g. [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:43](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L43), [src/ThroughlineBuild.Git/ProcessGitClient.cs:195-201](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L195-L201)). There is no `bash -c` / `cmd /c` wrapping in the binary itself.

ASCII-only output at tool boundaries is an explicit constraint (architecture Section 2). Operators with non-ASCII in tickets see escaped or stripped artifacts in markers, branch names, and filenames.

### Loose ends

- **`build init`'s template-write goes through `File.WriteAllText(..., Encoding.UTF8)`** ([src/ThroughlineBuild.Cli/InitCommand.cs:54](../../src/ThroughlineBuild.Cli/InitCommand.cs#L54)). If the embedded template ever carries non-ASCII bytes the round-trip through MSYS/curl noted in the operator's global conventions does not apply (this is a local file write, not an HTTP body), but the constraint is worth keeping in mind for any template edits.
- **Worker stdin encoding.** The brief is written to the worker via `StandardInput.WriteAsync` with the process default encoding; non-ASCII in a brief is the worker's problem, not normalized by `build`.

---

## CI integration

Status: Functional. [.github/workflows/build.yml](../../.github/workflows/build.yml). Single workflow, one job per OS via a `strategy.matrix` ([.github/workflows/build.yml:11-24](../../.github/workflows/build.yml#L11-L24)). On push or PR to `main` ([.github/workflows/build.yml:3-7](../../.github/workflows/build.yml#L3-L7)):

OS x RID matrix (the artifact name carries the `.exe` suffix only on Windows):

| OS | RID | artifact |
|---|---|---|
| `macos-latest` | `osx-arm64` | `build` |
| `windows-latest` | `win-x64` | `build.exe` |
| `ubuntu-latest` | `linux-x64` | `build` |

Steps ([.github/workflows/build.yml:25-36](../../.github/workflows/build.yml#L25-L36)):

1. `actions/checkout@v4`.
2. `actions/setup-dotnet@v4` with `dotnet-version: '8.x'`.
3. `dotnet restore`.
4. `dotnet test --no-restore`.
5. `dotnet publish src/ThroughlineBuild.Cli -r <rid> -c Release --no-restore`.
6. `actions/upload-artifact@v4` of `src/ThroughlineBuild.Cli/bin/Release/net8.0/<rid>/publish/<artifact>`.

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

- **Verifier runs in the feature worktree (TLB-226).** `ReviewPhase` constructs `WorkerAgentReviewer` and the `AutomatedChecksRunner` against `canonicalWorktreePath` (the feature worktree), not the main working directory ([src/ThroughlineBuild.Phases/ReviewPhase.cs:152-163](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L152-L163)). The diff and brief are computed from the main repo, but the verifier worker and the checks execute in the checkout containing the change.
- **Ship regression checks run in the feature worktree.** `ShipPhase` runs `_checksRunner.RunAsync(..., canonicalWorktreePath, ct)` ([src/ThroughlineBuild.Phases/ShipPhase.cs:362](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L362)), and the rebase happens in the feature worktree, while fetch / FF-merge / push happen in the main worktree.
- **Pre-flight: exe-in-worktree check.** `ShipPhase` refuses to ship if the running `build` binary is inside the worktree being rebased ([src/ThroughlineBuild.Phases/ShipPhase.cs:127-147](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L127-L147)) - killing the binary midway would leave inconsistent state. The check compares `processPathProvider()` (defaults to `Environment.ProcessPath`) against the worktree path with Windows-case-insensitive comparison. Failure: posts `ship_blocked: build.exe is running from inside the worktree`, `ShipFailureStage.PreFlight`.
- **Pre-flight: dirty-tracked check.** `ShipPhase` refuses if either the feature or main worktree has uncommitted tracked changes ([src/ThroughlineBuild.Phases/ShipPhase.cs:149-171](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L149-L171)). Untracked files are ignored.
- **Worktree location.** `ReviewPhase` and `ShipPhase` locate the feature worktree by walking `IGitClient.ListWorktreesAsync` and matching either branch name or full path against the deterministic layout ([src/ThroughlineBuild.Phases/ReviewPhase.cs:77-102](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L77-L102), [src/ThroughlineBuild.Phases/ShipPhase.cs:98-125](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L98-L125)). A manually renamed worktree loses this link.

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

- All timestamps are emitted at event-emission time. Sinks vary: `JsonlEventSink` records `ev.Timestamp` (set by the phase) while several `ShipPhase` events use `DateTimeOffset.UtcNow` ([src/ThroughlineBuild.Phases/ShipPhase.cs:519-528](../../src/ThroughlineBuild.Phases/ShipPhase.cs#L519-L528)) and the file-name builder uses `DateTimeOffset.Now`. No central clock-skew correction - useful for sorting within a session, not cross-machine correlation.
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

- **`origin/main` assumption** is hardcoded in `BaseRefResolver` ([src/ThroughlineBuild.Git/BaseRefResolver.cs:17-23](../../src/ThroughlineBuild.Git/BaseRefResolver.cs#L17-L23)); `ship.base_branch` / `ship.remote` reach only `ShipPhase`, not plan/implement/review/decompose. A repo with a different base branch fails those phases.
- **Architecture-doc disagreement on push.** [docs/throughline-build-architecture.md:174](../throughline-build-architecture.md) (Section 5.9) still asserts "v1 is local-merge-only with no `git push origin main`" and the never-force-push rule. The never-force rule holds, but the no-push and no-fetch claims are now false (TLB-293/296/297). The architecture doc is stale here.
- **Slug truncation at 80 chars** is silent ([src/ThroughlineBuild.Helpers/SlugBuilder.cs:30-43](../../src/ThroughlineBuild.Helpers/SlugBuilder.cs#L30-L43)) - very long ticket titles can collide.
- **Non-ASCII handling** is silent strip / collapse. Tickets in non-Latin languages get nearly-empty slugs.
- **Case-insensitive label/state/issue-type matching.** All three Plane caches key on `OrdinalIgnoreCase` ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:150](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L150), [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:168](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L168), [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:186](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L186)), so a workspace with names differing only by case would alias to one entry.
- **No telemetry to a central service** - everything stays on the operator's box.
- **Container support** is not tested. Running `build` in a container with no DNS to Plane fails to start.
- **WSL paths** are not specifically handled - `.exe` on Windows RIDs is the only Windows-aware bit; the Windows AOT binary run from WSL has not been validated.
