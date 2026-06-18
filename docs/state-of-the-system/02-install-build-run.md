# 02 - Install, Build, Run

Last refreshed: 2026-06-11 (HEAD 3a73eb9)

How the repository gets onto a machine, what the build produces, what running it requires from the host, and what changes vs. cleans up on disk.

For runtime state details see [05-state-and-persistence.md](05-state-and-persistence.md). For configuration files see [04-configuration.md](04-configuration.md). For external service dependencies see [03-external-dependencies.md](03-external-dependencies.md).

---

## Toolchain prerequisites

The repository is a `.NET 10` solution with native AOT publication. All 19 production csproj under `src/` target `net10.0` (a 20th directory, `src/ThroughlineBuild.Linear/`, holds only untracked build leftovers and has no csproj).

- **`.NET 10 SDK`** - required for `dotnet build`, `dotnet test`, `dotnet publish`. Verified in CI via `actions/setup-dotnet@v4` with `dotnet-version: '10.x'` ([.github/workflows/build.yml:27-29](../../.github/workflows/build.yml#L27-L29)).
- **A native toolchain** for AOT publication on the target RID: MSVC on Windows, Xcode CLT on macOS, gcc/clang + system libc on Linux. This is implicit in `dotnet publish -r <rid>` and is not enforced by the scripts.
- **`git`** - assumed available on `PATH`. `ProcessGitClient` shells out to `git` without checking it exists; a failed process start becomes `InvalidOperationException` ([src/ThroughlineBuild.Git/ProcessGitClient.cs](../../src/ThroughlineBuild.Git/ProcessGitClient.cs)).
- **One or more worker CLIs** - the orchestrator dispatches phase work to whichever agent is configured. The bundled agents are `claude` (Claude Code), `codex`, `gemini`, and `copilot`. The README lists install commands for all four ([README.md:28-41](../../README.md#L28-L41)). Which CLI must be present depends entirely on `[workers]` config; see "Worker CLIs" below.

The solution file is [throughline-build.sln](../../throughline-build.sln). The `src/` tree carries the four worker projects (`ThroughlineBuild.Workers.ClaudeCode`, `.Codex`, `.Gemini`, `.Copilot`) plus `ThroughlineBuild.Workers.Common`, alongside `ThroughlineBuild.ModelClient`, `.Scaffold`, `.JudgmentSlots`, and `.Verification`.

---

## Build

### Compile-check only (no native binary)

```
dotnet build throughline-build.sln --nologo -v q
```

Per [README.md:11](../../README.md#L11). Produces managed assemblies under each project's `bin/` and `obj/`. Fastest path for verifying a code change without paying AOT compile cost.

### Test

```
dotnet test --nologo -v q --logger "console;verbosity=minimal"
```

Per [README.md:1-2](../../README.md#L1-L2). Discovers and runs the xUnit test projects under `tests/`. The tracked `tests/Directory.Build.props` defaults `RunSettingsFilePath` to the repo's `test.runsettings` so `dotnet test` is quiet-on-green by default, and conditionally imports the machine-local root `Directory.Build.props` when it exists ([tests/Directory.Build.props](../../tests/Directory.Build.props)). Tests target `net10.0` without `PublishAot=true`, so they do not exercise AOT-sensitive code paths under their default runner (see [architecture Section 11](../throughline-build-architecture.md); `WorkerResultParserAotRegressionTests` is the reference example for tests that opt in to the AOT switch).

### Native AOT publish of `build`

```
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release --nologo -v q
```

Per [README.md:4-9](../../README.md#L4-L9). Produces `src/ThroughlineBuild.Cli/bin/Release/net10.0/<rid>/publish/build.exe` (the `.exe` extension is dropped on non-Windows RIDs because of `<AssemblyName>build</AssemblyName>` in [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:8](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L8) combined with `<PublishAot>true</PublishAot>`).

Cross-platform RIDs are noted in the README: `osx-arm64`, `linux-x64`.

**Version stamping (TLB-459).** The Cli csproj sets `<VersionPrefix>0.1.0</VersionPrefix>` and a `GenerateBuildVersionSource` MSBuild target that emits a generated partial class setting the compile-time const `BuildVersion.Current` to `{VersionPrefix}+{shortSha}` (e.g. `0.1.0+09172e5`); when `SourceRevisionId` is empty (non-git build) the const degrades to the bare version ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:54-84](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L54-L84)). The hand-written half of the partial is [src/ThroughlineBuild.Cli/BuildVersion.cs](../../src/ThroughlineBuild.Cli/BuildVersion.cs). `build -V` / `build --version` prints it ([src/ThroughlineBuild.Cli/Program.cs:27-31](../../src/ThroughlineBuild.Cli/Program.cs#L27-L31)), and the same value flows into the event-log `build_version` field via `SessionContext.BuildVersion` ([src/ThroughlineBuild.Cli/Program.cs:468-472](../../src/ThroughlineBuild.Cli/Program.cs#L468-L472)). Staying a const keeps the AOT publish reflection-free.

**AOT code-gen memory mitigation.** The Cli csproj sets `<IlcOptimizationPreference>Size</IlcOptimizationPreference>` and `<IlcMaxParallelism>1</IlcMaxParallelism>` ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:12-13](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L12-L13)) to keep the ILC/LLVM backend from OOM-ing during publish (the crash surfaced as ILC exit `-1073740791`). Single-threaded code-gen trades publish wall-time for a bounded memory footprint. The same fix split the oversized `RunAsync` in `Program.cs` into `RunTicketVerbBodyAsync` / `RunChainVerbAsync` so no single method blew up the code generator (commit `dd7d781`).

**Host-coupling caveat: the root `Directory.Build.props` is machine-local and gitignored.** A local (untracked) root `Directory.Build.props` sets `IlcUseEnvironmentalTools=true` and points `CppLinker`/`CppLibCreator` plus `AdditionalNativeLibraryDirectories` at absolute MSVC/WinSDK paths to skip the `vswhere.exe` discovery that fails in the author's environment. The file is deliberately excluded from the repo - `.gitignore` ignores `/Directory.Build.props` and `/Directory.Build.targets` as "machine-specific native-AOT linker overrides" ([.gitignore:17-19](../../.gitignore#L17-L19)) - so a fresh clone has no root props file and a Windows native publish relies on standard MSVC discovery. The tracked `tests/Directory.Build.props` chains to the root file only `Condition="Exists(...)"`, so CI simply skips it ([tests/Directory.Build.props:10-11](../../tests/Directory.Build.props#L10-L11)).

### Three-binary bundle via `build.sh` - Functional

```
./build.sh                 # RID auto-detected from uname; falls back to win-x64
RID=osx-arm64 ./build.sh   # cross-target
```

[build.sh](../../build.sh) selects the RID from `uname -s`/`uname -m` (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, otherwise `win-x64`), creating `bin/` and publishing three AOT binaries into it ([build.sh:6-30](../../build.sh#L6-L30)):

- `build` from `src/ThroughlineBuild.Cli`, copied from `src/ThroughlineBuild.Cli/bin/Release/net10.0/$RID/publish/` to `bin/build$EXT` ([build.sh:20-22](../../build.sh#L20-L22)).
- `token-audit` from the single-file source `src/tools/token-audit.cs`, copied from `src/tools/artifacts/token-audit/` to `bin/token-audit$EXT` ([build.sh:24-26](../../build.sh#L24-L26)).
- `analyze-event-log` from `src/tools/analyze-event-log.cs`, copied from `src/tools/artifacts/analyze-event-log/` to `bin/analyze-event-log$EXT` ([build.sh:28-30](../../build.sh#L28-L30)).

`EXT` is `.exe` only when `RID` starts with `win-` ([build.sh:15-16](../../build.sh#L15-L16)); the RID fallback to `win-x64` for unrecognized `uname` output is the `*)` case arm ([build.sh:12](../../build.sh#L12)). Publishes run quiet (`--nologo -v q`) and the script ends by listing the three copied binaries ([build.sh:32-34](../../build.sh#L32-L34)). The two tools are project-less C# sources that `dotnet publish` compiles individually; their artifacts land under `src/tools/artifacts/` (gitignored, [.gitignore:16](../../.gitignore#L16)). `analyze-event-log` carries its own pricing table including `claude-fable-5` and treats the bare alias `fable` as that slug ([src/tools/analyze-event-log.cs:41](../../src/tools/analyze-event-log.cs#L41), [src/tools/analyze-event-log.cs:402](../../src/tools/analyze-event-log.cs#L402)).

### CI build matrix

[.github/workflows/build.yml](../../.github/workflows/build.yml) builds `ThroughlineBuild.Cli` only across `{macos-latest (osx-arm64), windows-latest (win-x64), ubuntu-latest (linux-x64)}` on push/PR to `main` ([.github/workflows/build.yml:11-24](../../.github/workflows/build.yml#L11-L24)). Each leg runs `dotnet restore`, `dotnet test --no-restore`, then `dotnet publish ... --no-restore`, and uploads the per-RID `build`/`build.exe` artifact (from `.../net10.0/<rid>/publish/`) via `actions/upload-artifact@v4` ([.github/workflows/build.yml:30-36](../../.github/workflows/build.yml#L30-L36)). No release tagging, no deploy step. CI does not build the `token-audit`/`analyze-event-log` tools.

### Loose ends

- **`build.sh` does not chain to test** - operators publishing locally must run `dotnet test` separately. Only CI runs both.
- **No release pipeline** in `.github/`. The published artifacts uploaded by CI are not promoted, signed, or tagged.
- **AOT trim warnings** are not gated by CI; reflection-using DTOs that slip past source-gen would produce a runtime `NotSupportedException` only on the published binary (architecture Section 11). The reference regression test exists, but it is the only AOT-aware test.
- **The machine-local root `Directory.Build.props` is invisible to a fresh clone** - an operator whose MSVC install also defeats `vswhere.exe` discovery must reconstruct the override file themselves; nothing in the repo documents its required shape except `tests/Directory.Build.props`'s comment.

---

## Run

### The binary

After AOT publish, the binary is single-file (`<PublishAot>true</PublishAot>`, [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) - no `dotnet` runtime is required on the target machine. Put it on `PATH` (operationally, copy from `bin/` or symlink there) and run `build --help`. Architecture note: a future operator may want the alias `tl-build` to avoid colliding with project-local `build` commands (architecture Appendix item 7); no support for that alias today.

### Invocation contract

```
build <verb> [args] [--debug | --quiet] [--summary-json] [--error-location]
build -V | --version
build help [<topic>]
build <verb> -h | --help
```

Verb dispatch lives in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs). On startup, `RunAsync` ([src/ThroughlineBuild.Cli/Program.cs:22](../../src/ThroughlineBuild.Cli/Program.cs#L22)):

1. Short-circuits `-V`/`--version` (prints `BuildVersion.Current`) and the three help shapes: bare/`-h`/`--help` renders the `Tier0Renderer` command index, `build help <topic>` renders a `HelpTopicRegistry` topic (unknown topic exits 2), and a `-h`/`--help` anywhere after a verb renders that verb's `Tier1Renderer` page ([src/ThroughlineBuild.Cli/Program.cs:27-56](../../src/ThroughlineBuild.Cli/Program.cs#L27-L56), [src/ThroughlineBuild.Cli/Program.cs:150-170](../../src/ThroughlineBuild.Cli/Program.cs#L150-L170)). The registries live under [src/ThroughlineBuild.Cli/Help/](../../src/ThroughlineBuild.Cli/Help/).
2. Strips the bare bool flags `--debug`, `--quiet`, `--summary-json`, `--error-location`, `--no-auto-resolve`, `--no-auto-merge`, `--no-push`, `--continue-past-failure`, `--from-brief`, and `--skip-baseline` from `args` ([src/ThroughlineBuild.Cli/Program.cs:61-100](../../src/ThroughlineBuild.Cli/Program.cs#L61-L100)).
3. Extracts the agent-selection flags `--agent` / `--agent-plan` / `--agent-implement` / `--agent-review` via `CliArgParser.ExtractAgentFlags` ([src/ThroughlineBuild.Cli/Program.cs:102-105](../../src/ThroughlineBuild.Cli/Program.cs#L102-L105)), and for `chain` the traversal flags `--dry-run` / `--max-depth N` / `--batch-implement [ids]` ([src/ThroughlineBuild.Cli/Program.cs:110-135](../../src/ThroughlineBuild.Cli/Program.cs#L110-L135)).
4. Dispatches the pre-config verbs that run before any config load: `init` (bootstraps the config; see "The `build init` verb" below, [src/ThroughlineBuild.Cli/Program.cs:231-290](../../src/ThroughlineBuild.Cli/Program.cs#L231-L290)), `settarget` ([src/ThroughlineBuild.Cli/Program.cs:294-301](../../src/ThroughlineBuild.Cli/Program.cs#L294-L301)), `user-guide` ([src/ThroughlineBuild.Cli/Program.cs:303-309](../../src/ThroughlineBuild.Cli/Program.cs#L303-L309)), `op-doc spec|new` ([src/ThroughlineBuild.Cli/Program.cs:313-398](../../src/ThroughlineBuild.Cli/Program.cs#L313-L398)), and `models refresh` ([src/ThroughlineBuild.Cli/Program.cs:400-420](../../src/ThroughlineBuild.Cli/Program.cs#L400-L420)).
5. Resolves the main worktree root via `MainWorktreeResolver.ResolveAsync` so that being invoked from inside a feature worktree still locates `.build/config.toml` and the project root ([src/ThroughlineBuild.Cli/Program.cs:422-423](../../src/ThroughlineBuild.Cli/Program.cs#L422-L423), [src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs)).
6. Walks up from cwd to find `.build/config.toml` via `BuildConfigLoader.FindConfigFile` ([src/ThroughlineBuild.Cli/Config.cs:106-117](../../src/ThroughlineBuild.Cli/Config.cs#L106-L117)); a missing file exits 2 with `Config error:` ([src/ThroughlineBuild.Cli/Program.cs:427-438](../../src/ThroughlineBuild.Cli/Program.cs#L427-L438)).
7. Loads the TOML via `BuildConfigLoader.Load` (Tomlyn; exit 2 on `ConfigException`) and resolves secrets via `BuildConfigLoader.ResolveSecrets` (exit 3 on `Secret error:`) ([src/ThroughlineBuild.Cli/Program.cs:440-464](../../src/ThroughlineBuild.Cli/Program.cs#L440-L464)).
8. Constructs per-verb dependencies (HttpClient, `PlaneTicketingClient`, a `WorkerAgentFactory` over all referenced agents, `JsonlEventSink` wrapping a `RecordingEventSink`) and dispatches. Post-config verbs include the phase verbs (`plan`, `implement`, `review`, `ship`, `chain`, `rework`, `decompose`), the ticket-lifecycle verbs (`new`, `amend`, `close`, `defer`, `reopen`, `list`), `scaffold`, `setup` (see below), and `sweep` (chain-worktree recovery, [src/ThroughlineBuild.Cli/Program.cs:480-517](../../src/ThroughlineBuild.Cli/Program.cs#L480-L517)).

### Working-directory expectations

- Must be inside (or under) a git working tree - `MainWorktreeResolver` calls `git worktree list --porcelain`.
- Must contain a discoverable `.build/config.toml`, either in the cwd or any ancestor (except the pre-config verbs above, which either create it, edit it, or do not need it).
- For phases that operate inside a worktree (`implement`, `review`, `ship`), the worktree must be locatable by branch name or path via `git worktree list`.

### Worker CLIs

The orchestrator constructs one agent per name referenced by `default_agent`, the `[workers.phases]` map, or a CLI agent flag ([src/ThroughlineBuild.Cli/Program.cs:1117-1141](../../src/ThroughlineBuild.Cli/Program.cs#L1117-L1141)). The name-to-implementation mapping is centralized in `WorkerAgentBuilder.Create`, shared by the phase factory and the scaffold profile-derivation path ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)):

| Agent name | Implementation | External CLI | Auth posture | Status |
|---|---|---|---|---|
| `claude-code` (or any other name) | `ClaudeCodeAgent` (fallback) | `claude` | Strips `ANTHROPIC_API_KEY` from the child env to force Claude Code OAuth ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:620](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L620)). | Functional |
| `codex` | `CodexAgent` | `codex` | Strips `CODEX_API_KEY` and `OPENAI_API_KEY` to force subscription auth ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:338-339](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L338-L339)). | Functional |
| `gemini` | `GeminiAgent` | `gemini` | Strips `GEMINI_API_KEY` and `GOOGLE_API_KEY`, falling back to ADC / gcloud login ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:285-286](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L285-L286)). | Functional |
| `copilot` | `CopilotAgent` | `copilot` | Additive, not subtractive: inherits the `gh` keyring credential, or the caller supplies `GH_TOKEN` via env ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:192-200](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L192-L200)). | Functional |

Any agent name that is not `gemini`/`codex`/`copilot` falls through to `ClaudeCodeAgent` ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:38-44](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L38-L44)). The executable path per agent is `[workers.<name>].executable` (required key). Config load now fail-fasts when `default_agent` or a `[workers.phases]` value names an agent with no `[workers.<name>]` sub-table (TLB-512): `ReadWorkersSection` throws a `ConfigException` listing the agents that ARE defined, surfacing as `Config error:` exit 2 instead of a late unhandled exception ([src/ThroughlineBuild.Cli/Config.cs:679-686](../../src/ThroughlineBuild.Cli/Config.cs#L679-L686)). Both the shipped template and the checked-in operator config currently default to `claude-code` (see [04-configuration.md](04-configuration.md)). All non-Anthropic worker LLM cost flows to the operator's subscription/quota, not to the orchestrator's API key.

### Host machine requirements

| Requirement | Why |
|---|---|
| `git` on PATH | Every phase that touches the repo runs `git` subprocesses via `ProcessGitClient` ([src/ThroughlineBuild.Git/ProcessGitClient.cs](../../src/ThroughlineBuild.Git/ProcessGitClient.cs)). |
| The configured worker CLI(s) on PATH (or absolute path in config) | Plan / Implement / Review / Decompose / Draft phases - and scaffold profile derivation - spawn the agent named for that phase ([src/ThroughlineBuild.Cli/Program.cs:1117-1141](../../src/ThroughlineBuild.Cli/Program.cs#L1117-L1141)). |
| Plane API token | Every Plane operation needs it; a missing token aborts at secret resolution (`BuildConfigLoader.ResolveSecrets`) with exit 3 ([src/ThroughlineBuild.Cli/Config.cs:182-196](../../src/ThroughlineBuild.Cli/Config.cs#L182-L196)). |
| Network reachability to the configured `plane_base_url` | Every ticket fetch / write hits the Plane REST API; transport-level outages are retried then classified, not crashed on ([src/ThroughlineBuild.Plane/PlaneTicketingClient.cs:256-285](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L256-L285)). |
| Network reachability to `api.anthropic.com` | Only for `close`/`defer`/`reopen` (`ReasonTranslator`) - other phases reach their provider via the worker CLI's own auth ([src/ThroughlineBuild.Anthropic/AnthropicOptions.cs](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs)). |
| Anthropic API key | Optional even for `close`/`defer`/`reopen`: when `LlmClientFactory.Create` throws, those verbs fall back to `EchoLlmClient` and record the reason verbatim ([src/ThroughlineBuild.Cli/Program.cs:2252-2262](../../src/ThroughlineBuild.Cli/Program.cs#L2252-L2262)). |
| Native exe execution permissions | AOT binary; no JIT. |
| LF-EOL handling for templates | `.gitattributes` pins brief template files and snapshot test data to LF so substitution is byte-stable across OS ([.gitattributes:1-3](../../.gitattributes#L1-L3)). |

### The `build init` verb - Functional

`build init` bootstraps `.build/config.toml` and (when given credentials) provisions the Plane project in the same run. It is a pre-config verb dispatched before config load ([src/ThroughlineBuild.Cli/Program.cs:231-290](../../src/ThroughlineBuild.Cli/Program.cs#L231-L290)). Unknown/misspelled flags are rejected up front with the recognized-flag list (exit 2) via `CliArgParser.FindUnknownFlag` ([src/ThroughlineBuild.Cli/Program.cs:236-250](../../src/ThroughlineBuild.Cli/Program.cs#L236-L250)).

```
build init                                # interactive: prompts for URL / workspace / token, then create-or-pick project
build init --force                        # overwrite an existing file
build init --print-template               # print the template to stdout, write nothing (always offline)
build init --no-interactive               # suppress all prompting
build init --plane-url URL --workspace SLUG --project-id UUID --token TOKEN
build init --project-name NAME ...        # connected mode: resolve-or-create the project by name, no UUID
build init --token-env PLANE_API_TOKEN    # write an env-var indirection line instead of an inline token
build init --from creds.txt               # read credentials from a key = value file
build init < creds.txt                    # redirected stdin is read as a creds file
```

`InitCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/InitCommand.cs:68-183](../../src/ThroughlineBuild.Cli/InitCommand.cs#L68-L183)) implements three modes:

- **Credentials sourcing.** `--from <file>` (or, when stdin is redirected, stdin itself) is parsed by `CredsFileParser.Parse`, which accepts `key = value` / `key = "value"` lines with the `[ticketing]` key names `plane_base_url`, `plane_workspace_slug`, `plane_api_token`, `plane_project_id`, `plane_project_name`, tolerating comments and blank lines; unknown keys are silently ignored ([src/ThroughlineBuild.Cli/CredsFileParser.cs:27-74](../../src/ThroughlineBuild.Cli/CredsFileParser.cs#L27-L74)). Explicit flag values always win over file values ([src/ThroughlineBuild.Cli/InitCommand.cs:672-685](../../src/ThroughlineBuild.Cli/InitCommand.cs#L672-L685)).
- **Interactive guided onboarding.** At a real TTY (stdin not redirected, no `--from`, no `--no-interactive`), `PromptForConnectionValues` prompts only for the connection values - base URL, workspace slug, API token (blank = fill in later) ([src/ThroughlineBuild.Cli/InitCommand.cs:640-666](../../src/ThroughlineBuild.Cli/InitCommand.cs#L640-L666)). There is deliberately no project-UUID prompt: with a full connection available, `PromptCreateOrPickAsync` asks "create a new project or use an existing one?", listing workspace projects most-recently-updated first or prompting for a name + Plane identifier (default derived by `ProjectResolver.DeriveIdentifier`) and creating the project ([src/ThroughlineBuild.Cli/InitCommand.cs:492-595](../../src/ThroughlineBuild.Cli/InitCommand.cs#L492-L595)). Declining at the prompt falls back to the offline template.
- **Connected mode (non-interactive).** `--project-name` plus full credentials (and no explicit `--project-id`) triggers `RunConnectedAsync`: a `ProjectResolver` finds or creates the Plane project by name ([src/ThroughlineBuild.Plane/ProjectResolver.cs:46-55](../../src/ThroughlineBuild.Plane/ProjectResolver.cs#L46-L55)), then the shared pipeline `RunConnectedPipelineAsync` substitutes the resolved id into the config, writes it, runs `SetupCommand` provisioning (git init, `.gitignore`, Plane states + labels), ensures a welcome commit via `WelcomeCommit.EnsureInitialCommit` (idempotent - guarded by `ILocalRepoOps.HasAnyCommits`, [src/ThroughlineBuild.Cli/WelcomeCommit.cs:22-25](../../src/ThroughlineBuild.Cli/WelcomeCommit.cs#L22-L25)), verifies connectivity via `ITicketingConnectivity.TestConnectivityAsync`, and prints a summary including any op-doc files found by `FindDocPaths` with a ready-to-run `build scaffold` line ([src/ThroughlineBuild.Cli/InitCommand.cs:382-484](../../src/ThroughlineBuild.Cli/InitCommand.cs#L382-L484)). A failed connectivity check exits 1 with a pointer at `build setup --check`.
- **Offline mode.** Anything else writes the template with whatever values were supplied (`WriteOfflineConfig`), lists exactly which `REQUIRED_*` fields are still unresolved, and points at `build setup`, connected mode, and `build user-guide` as next steps ([src/ThroughlineBuild.Cli/InitCommand.cs:190-220](../../src/ThroughlineBuild.Cli/InitCommand.cs#L190-L220)).

Shared mechanics:

- The template is the embedded resource `ThroughlineBuild.Commands.Templates.config.toml.template` loaded by `ConfigTemplateLoader.Load()` - no disk-relative lookup, preserving the single-binary AOT contract ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs)). Source file: [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template).
- `InitCommand.ApplyFlags` substitutes the `REQUIRED_PLANE_BASE_URL` / `REQUIRED_PLANE_WORKSPACE_SLUG` / `REQUIRED_PLANE_PROJECT_ID` / `REQUIRED_PLANE_API_TOKEN` placeholders; `--token-env` rewrites the whole `plane_api_token = "..."` line into `plane_api_token_env = "VALUE"` and takes precedence over `--token` ([src/ThroughlineBuild.Cli/InitCommand.cs:703-740](../../src/ThroughlineBuild.Cli/InitCommand.cs#L703-L740)).
- **Codex tier discovery.** When writing (not `--print-template`), init runs `CodexModelProbe` (`codex debug models`) and, on success, rewrites the `[workers.codex.sizes]` block with a best-guess small/medium/large mapping from `CodexTierMapper` plus a discovered-menu comment; on probe failure it leaves the static template block, prints one warning pointing at `build models refresh`, and still exits 0 (`InitCommand.ApplyCodexProbe`, [src/ThroughlineBuild.Cli/InitCommand.cs:227-249](../../src/ThroughlineBuild.Cli/InitCommand.cs#L227-L249)).
- With `--print-template`, content goes to stdout, nothing is written, and the Codex probe never runs ([src/ThroughlineBuild.Cli/InitCommand.cs:124-129](../../src/ThroughlineBuild.Cli/InitCommand.cs#L124-L129)).
- Without `--force`, an existing `.build/config.toml` causes `Error: <path> already exists. Use --force to overwrite.` and exit 1; the clobber guard runs before any probing ([src/ThroughlineBuild.Cli/InitCommand.cs:133-138](../../src/ThroughlineBuild.Cli/InitCommand.cs#L133-L138)).

### The `build setup` verb - Functional

`build setup [--check]` makes a fresh project ready for the workflow; it is the step between `build init` (offline form) and the first `build new`/`build chain`. Dispatched after config load ([src/ThroughlineBuild.Cli/Program.cs:561-600](../../src/ThroughlineBuild.Cli/Program.cs#L561-L600)); `SetupCommand.ExecuteAsync` does two idempotent things ([src/ThroughlineBuild.Cli/SetupCommand.cs:33-48](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L33-L48)):

1. **Local repo** - `git init` if the directory is not yet a repository, append any missing entries from `GitignoreManager`'s standard language-neutral ignore list to `.gitignore` without disturbing existing lines, and (when not `--check`) give a brand-new repo its welcome commit so the first `build ship` has a base ref ([src/ThroughlineBuild.Cli/SetupCommand.cs:51-91](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L51-L91); `GitignoreManager` and `FileSystemLocalRepoOps` live in [src/ThroughlineBuild.Cli/LocalRepoSetup.cs](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs)).
2. **Plane project** - diff the project's states and labels against `WorkspaceSchema` (the 7 workflow states and 9 standard labels the binary resolves by name at runtime, [src/ThroughlineBuild.Contracts/WorkspaceSchema.cs:23-45](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L23-L45)) and create whatever is missing via `ITicketingProvisioner` ([src/ThroughlineBuild.Cli/SetupCommand.cs:94-150](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L94-L150)).

With `--check` nothing is mutated: each gap is reported and the command exits 1 if any local or Plane gap remains, 0 otherwise. A Plane 404 on the project route is mapped to the actionable `PlaneTicketingClient.BuildProjectNotFoundMessage` remedy rather than a raw body ([src/ThroughlineBuild.Cli/Program.cs:587-594](../../src/ThroughlineBuild.Cli/Program.cs#L587-L594)).

### The `build models refresh` verb - Functional

`build models refresh` re-probes Codex and rewrites only the `[workers.codex.sizes]` block (and its discovered-menu comment) in the existing config, in place, preserving every other byte including the leading BOM; it prints a current-to-proposed diff and never silently activates or comments out the block (`ModelsRefreshCommand.Execute`, [src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs:24-82](../../src/ThroughlineBuild.Cli/ModelsRefreshCommand.cs#L24-L82)). Exit codes: 0 rewrote or already up to date, 1 probe/IO failure (config untouched), 2 no config found. The block find/replace machinery is `CodexSizesBlockEditor.TryFindCodexSizesBlock`/`ReplaceCodexSizesBlock` with `CodexSizesBlockReader` and `CodexSizesBlockRenderer` ([src/ThroughlineBuild.Cli/CodexSizesBlockEditor.cs:15-59](../../src/ThroughlineBuild.Cli/CodexSizesBlockEditor.cs#L15-L59)). Like `init`, it dispatches in the pre-config-load band because it edits the config rather than consuming it ([src/ThroughlineBuild.Cli/Program.cs:400-420](../../src/ThroughlineBuild.Cli/Program.cs#L400-L420)).

### Loose ends

- **`build init` connected mode validates by doing** (project resolution, provisioning, connectivity probe), but offline `build init` still does not validate the substituted values; the check story is `build setup --check`, not a `build config check` verb.
- **The Codex probe heuristic is a best guess** - `CodexTierMapper.Map` documents its slug-ordering assumptions in code and makes no capability claim; operators are expected to hand-tune ([src/ThroughlineBuild.Cli/CodexTierMapper.cs:6-26](../../src/ThroughlineBuild.Cli/CodexTierMapper.cs#L6-L26)).
- The README still documents `build new --print-template` for ticket bodies separately from `build init --print-template` ([README.md:13-26](../../README.md#L13-L26)); the `--print-template` flag belongs to three verbs (`new`, `init`, `user-guide`) and emits different content for each.

---

## The `build user-guide` verb - Functional

`build user-guide` (TLB-322) writes the embedded operator setup guide to `docs/throughline_build_userguide.md`. Like `init` and `settarget`, it is a pre-config verb that runs without a `.build/config.toml` present ([src/ThroughlineBuild.Cli/Program.cs:303-309](../../src/ThroughlineBuild.Cli/Program.cs#L303-L309)).

```
build user-guide                 # write docs/throughline_build_userguide.md under cwd
build user-guide --force         # overwrite an existing guide file
build user-guide --print-template  # print the guide to stdout, write nothing
```

`UserGuideCommand.Execute` loads the embedded guide via `UserGuideLoader.Load()` (no disk lookup, preserving the single-binary AOT contract) and writes it; with `--print-template` content goes to stdout, without `--force` an existing file errors with exit 2, and on success it creates `docs/` if needed and prints the absolute path ([src/ThroughlineBuild.Cli/UserGuideCommand.cs:19-43](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L19-L43), [src/ThroughlineBuild.Commands/UserGuideLoader.cs](../../src/ThroughlineBuild.Commands/UserGuideLoader.cs)).

---

## Updating

There is no built-in update mechanism. Operationally the only change is rebuilding the binary:

```
git pull
./build.sh
```

The binary now carries a compile-time version stamp: `BuildVersion.Current` is `{VersionPrefix}+{shortSha}` (e.g. `0.1.0+09172e5`), generated at build time by the `GenerateBuildVersionSource` MSBuild target (TLB-459; see "Version stamping" above). `build --version` prints it and it flows into the event-log `build_version` field for downstream telemetry; it does not gate behavior.

---

## Uninstalling

Removing the repo and its binaries:

| Artifact | Location | Cleanup |
|---|---|---|
| AOT binaries | `bin/` (gitignored, [.gitignore:3](../../.gitignore#L3)) | Delete the directory. |
| Tool artifacts | `src/tools/artifacts/` (gitignored, [.gitignore:16](../../.gitignore#L16)) | Delete; regenerated by `build.sh`. |
| Build output | each project's `bin/` and `obj/` | `dotnet clean` or delete. |
| Config | `.build/config.toml` (gitignored, [.gitignore:14](../../.gitignore#L14)) | Delete. Removes the operator's secrets-in-clear from disk. |
| Event logs | `.build/events/` (gitignored, [.gitignore:12](../../.gitignore#L12)) | Delete; safe at any time. |
| Debug sessions | `.build/sessions/` (gitignored, [.gitignore:13](../../.gitignore#L13)) | Delete; safe at any time. |
| Draft brief | `.build/brief.md` (gitignored, [.gitignore:11](../../.gitignore#L11)) | Delete; safe at any time. |
| Worktrees | `.worktrees/` (gitignored, [.gitignore:1](../../.gitignore#L1)) | `build sweep` removes leftover chain worktrees and merged chain branches (merged-gated against the target so unshipped commits are never discarded; `--force` also removes worktrees with unmerged branches, [src/ThroughlineBuild.Cli/Program.cs:474-517](../../src/ThroughlineBuild.Cli/Program.cs#L474-L517), [src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs](../../src/ThroughlineBuild.Helpers/ChainWorktreeSweeper.cs)). Otherwise `git worktree remove` each, or rely on `WorktreeDecrufter` triggered by `ship` / `close` / `defer` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs)). |
| Scratch | `.scratch/` (gitignored, [.gitignore:10](../../.gitignore#L10)) | Delete. |
| `secrets/` | gitignored top-level ([.gitignore:2](../../.gitignore#L2)) | Delete. |
| Machine-local AOT overrides | `Directory.Build.props` / `.targets` at repo root (gitignored, [.gitignore:17-19](../../.gitignore#L17-L19)) | Delete; only affects native publishes on this machine. |

The binary itself writes no state outside the repo. No global config, no per-user state. No service or daemon to stop - architecture Section 2 explicitly forbids a persistent server.

### Loose ends

- **`WorktreeDecrufter` is best-effort** on Windows - it pre-cleans `node_modules` reparse points and retries `git worktree remove` with `--force`, but locked files from a phase killed mid-creation may need manual cleanup ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:111-145](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L111-L145)).
- **`build sweep` halts (exit 1) on worktrees it cannot remove**, listing them; locked files still need manual intervention.

---

## What the host machine must provide (summary)

Repeated as a single list for operators:

1. `.NET 10 SDK` to build; nothing at runtime (single-file AOT).
2. `git`.
3. At least the worker CLI named by `[workers] default_agent` (and any agent named in `[workers.phases]`): one or more of `claude`, `codex`, `gemini`, `copilot`. The config loader refuses to start if the named default agent has no `[workers.<name>]` block (TLB-512).
4. Network to the configured Plane base URL and, only for `close`/`defer`/`reopen`, to `api.anthropic.com`.
5. A Plane API token (env `PLANE_API_TOKEN` by default, or inline in config). An Anthropic API key is optional - without it, `close`/`defer`/`reopen` record reasons verbatim.
6. Provider auth for the chosen worker CLI: Claude Code OAuth, Codex subscription, Gemini ADC/gcloud, or a `gh` credential / `GH_TOKEN` for Copilot. None of these are read by `build` directly; the worker CLI handles them.

---

## Loose ends

- **`build init` + `build setup`** now cover bootstrap and provisioning; the architecture's fuller `build install` (worker-CLI provisioning, MCP registration) remains aspirational (architecture Section 9).
- **`build.sh` RID fallback** silently defaults to `win-x64` for any unrecognized `uname` output ([build.sh:12](../../build.sh#L12)); a cross-target build on an exotic platform may publish the wrong RID without warning.
- **No release pipeline** in `.github/`; CI artifacts are not promoted, signed, or tagged.
- **AOT trim warnings** are not gated by CI; the single reference regression test is the only AOT-aware test.
- **The machine-local root `Directory.Build.props`** required for native publishes on the author's Windows machine is gitignored and undocumented outside a comment in `tests/Directory.Build.props`.
