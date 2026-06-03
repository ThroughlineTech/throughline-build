# 02 - Install, Build, Run

How the repository gets onto a machine, what the build produces, what running it requires from the host, and what changes vs. cleans up on disk.

For runtime state details see [05-state-and-persistence.md](05-state-and-persistence.md). For configuration files see [04-configuration.md](04-configuration.md). For external service dependencies see [03-external-dependencies.md](03-external-dependencies.md).

---

## Toolchain prerequisites

The repository is a `.NET 10` solution with native AOT publication (all 19 csproj target `net10.0` since commit `97e6a87`).

- **`.NET 10 SDK`** - required for `dotnet build`, `dotnet test`, `dotnet publish`. Verified in CI via `actions/setup-dotnet@v4` with `dotnet-version: '10.x'` ([.github/workflows/build.yml:27-29](../../.github/workflows/build.yml#L27-L29)).
- **A native toolchain** for AOT publication on the target RID: MSVC on Windows, Xcode CLT on macOS, gcc/clang + system libc on Linux. This is implicit in `dotnet publish -r <rid>` and is not enforced by the scripts.
- **`git`** - assumed available on `PATH`. `ProcessGitClient` shells out to `git` without checking it exists; a failed process start becomes `InvalidOperationException` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:25-26](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L25-L26)).
- **One or more worker CLIs** - the orchestrator dispatches phase work to whichever agent is configured. The bundled agents are `claude` (Claude Code), `codex`, `gemini`, and `copilot`. The README lists install commands for all four ([README.md:28-41](../../README.md#L28-L41)). Which CLI must be present depends entirely on `[workers]` config; see "Worker CLIs" below.

The solution file is [throughline-build.sln](../../throughline-build.sln). The `src/` tree now carries the four worker projects (`ThroughlineBuild.Workers.ClaudeCode`, `.Codex`, `.Gemini`, `.Copilot`) plus `ThroughlineBuild.Workers.Common`, alongside `ThroughlineBuild.ModelClient`, `.Scaffold`, `.JudgmentSlots`, and `.Verification`.

---

## Build

### Compile-check only (no native binary)

```
dotnet build throughline-build.sln
```

Per [README.md:11](../../README.md#L11). Produces managed assemblies under each project's `bin/` and `obj/`. Fastest path for verifying a code change without paying AOT compile cost.

### Test

```
dotnet test
```

Per [README.md:1-2](../../README.md#L1-L2). Discovers and runs the xUnit test projects under `tests/`. Tests target `net10.0` without `PublishAot=true`, so they do not exercise AOT-sensitive code paths under their default runner (see [architecture Section 11](../throughline-build-architecture.md); `WorkerResultParserAotRegressionTests` is the reference example for tests that opt in to the AOT switch).

### Native AOT publish of `build`

```
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release
```

Per [README.md:4-9](../../README.md#L4-L9). Produces `src/ThroughlineBuild.Cli/bin/Release/net10.0/<rid>/publish/build.exe` (the `.exe` extension is dropped on non-Windows RIDs because of `<AssemblyName>build</AssemblyName>` in [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:8](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L8) combined with `<PublishAot>true</PublishAot>`).

Cross-platform RIDs are noted in the README: `osx-arm64`, `linux-x64`.

**AOT code-gen memory mitigation.** The Cli csproj sets `<IlcOptimizationPreference>Size</IlcOptimizationPreference>` and `<IlcMaxParallelism>1</IlcMaxParallelism>` ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:11-12](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L11-L12)) to keep the ILC/LLVM backend from OOM-ing during publish (the crash surfaced as ILC exit `-1073740791`). Single-threaded code-gen trades publish wall-time for a bounded memory footprint. The same fix split the oversized `RunAsync` in `Program.cs` into `RunTicketVerbBodyAsync` / `RunChainVerbAsync` so no single method blew up the code generator (commit `dd7d781`).

**Host-coupling caveat: [Directory.Build.props](../../Directory.Build.props) hardcodes one machine's MSVC/WinSDK link paths.** The root `Directory.Build.props` sets `IlcUseEnvironmentalTools=true` and points `CppLinker`/`CppLibCreator` plus `AdditionalNativeLibraryDirectories` at absolute paths (`...\VC\Tools\MSVC\14.44.35207`, `...\Windows Kits\10\Lib\10.0.26100.0`) to skip `vswhere.exe` discovery that fails in the author's environment ([Directory.Build.props:13-24](../../Directory.Build.props#L13-L24)). These paths only take effect during a native-AOT Windows publish (`-r win-x64 -c Release`); managed builds and tests are unaffected. On any other Windows machine these exact tool versions will likely differ, so a `win-x64` native publish there may fail to link until the paths are edited.

### Three-binary bundle via `build.sh` - Functional

```
./build.sh                 # RID auto-detected from uname; falls back to win-x64
RID=osx-arm64 ./build.sh   # cross-target
```

[build.sh](../../build.sh) selects the RID from `uname -s`/`uname -m` (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, otherwise `win-x64`), creating `bin/` and publishing three AOT binaries into it ([build.sh:6-30](../../build.sh#L6-L30)):

- `build` from `src/ThroughlineBuild.Cli`, copied from `src/ThroughlineBuild.Cli/bin/Release/net10.0/$RID/publish/` to `bin/build$EXT` ([build.sh:20-22](../../build.sh#L20-L22)).
- `token-audit` from the single-file source `src/tools/token-audit.cs`, copied from `src/tools/artifacts/token-audit/` to `bin/token-audit$EXT` ([build.sh:24-26](../../build.sh#L24-L26)).
- `analyze-event-log` from `src/tools/analyze-event-log.cs`, copied from `src/tools/artifacts/analyze-event-log/` to `bin/analyze-event-log$EXT` ([build.sh:28-30](../../build.sh#L28-L30)).

`EXT` is `.exe` only when `RID` starts with `win-` ([build.sh:15-16](../../build.sh#L15-L16)). The two tools are project-less C# sources that `dotnet publish` compiles individually; their artifacts land under `src/tools/artifacts/` (gitignored, [.gitignore:16](../../.gitignore#L16)).

### CI build matrix

[.github/workflows/build.yml](../../.github/workflows/build.yml) builds `ThroughlineBuild.Cli` only across `{macos-latest (osx-arm64), windows-latest (win-x64), ubuntu-latest (linux-x64)}` on push/PR to `main` ([.github/workflows/build.yml:11-23](../../.github/workflows/build.yml#L11-L23)). Each leg runs `dotnet restore`, `dotnet test --no-restore`, then `dotnet publish ... --no-restore`, and uploads the per-RID `build`/`build.exe` artifact (from `.../net10.0/<rid>/publish/`) via `actions/upload-artifact@v4` ([.github/workflows/build.yml:30-36](../../.github/workflows/build.yml#L30-L36)). No release tagging, no deploy step. CI does not build the `token-audit`/`analyze-event-log` tools.

### Loose ends

- **`build.sh` does not chain to test** - operators publishing locally must run `dotnet test` separately. Only CI runs both.
- **No release pipeline** in `.github/`. The published artifacts uploaded by CI are not promoted, signed, or tagged.
- **AOT trim warnings** are not gated by CI; reflection-using DTOs that slip past source-gen would produce a runtime `NotSupportedException` only on the published binary (architecture Section 11). The reference regression test exists, but it is the only AOT-aware test.

---

## Run

### The binary

After AOT publish, the binary is single-file (`<PublishAot>true</PublishAot>`, [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:9](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L9)) - no `dotnet` runtime is required on the target machine. Put it on `PATH` (operationally, copy from `bin/` or symlink there) and run `build --help`. Architecture note: a future operator may want the alias `tl-build` to avoid colliding with project-local `build` commands (architecture Appendix item 7); no support for that alias today.

### Invocation contract

```
build <verb> [args] [--debug | --quiet] [--summary-json] [--error-location]
```

Verb dispatch lives in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs). On startup, `RunAsync` ([src/ThroughlineBuild.Cli/Program.cs:23](../../src/ThroughlineBuild.Cli/Program.cs#L23)):

1. Strips bare bool flags `--debug`, `--quiet`, `--summary-json`, `--error-location`, `--no-auto-resolve`, `--no-auto-merge`, `--continue-past-failure` from `args` ([src/ThroughlineBuild.Cli/Program.cs:31-61](../../src/ThroughlineBuild.Cli/Program.cs#L31-L61)).
2. Extracts the agent-selection flags `--agent` / `--agent-plan` / `--agent-implement` / `--agent-review` ([src/ThroughlineBuild.Cli/Program.cs:63-66](../../src/ThroughlineBuild.Cli/Program.cs#L63-L66)).
3. For `init`, bootstraps the config file and exits before any config load ([src/ThroughlineBuild.Cli/Program.cs:138-153](../../src/ThroughlineBuild.Cli/Program.cs#L138-L153)) - see "The `build init` verb" below. `settarget` likewise dispatches here, before config load, since it edits `.build/config.toml` ([src/ThroughlineBuild.Cli/Program.cs:157-164](../../src/ThroughlineBuild.Cli/Program.cs#L157-L164)); see [01-inventory.md](01-inventory.md) and [04-configuration.md](04-configuration.md). `user-guide` is a third pre-config verb, dispatched the same way ([src/ThroughlineBuild.Cli/Program.cs:167-172](../../src/ThroughlineBuild.Cli/Program.cs#L167-L172)) - see "The `build user-guide` verb" below.
4. Resolves the main worktree root via [src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs) so that being invoked from inside a feature worktree still locates `.build/config.toml` and the project root ([src/ThroughlineBuild.Cli/Program.cs:174-175](../../src/ThroughlineBuild.Cli/Program.cs#L174-L175)).
5. Walks up from cwd to find `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:64-75](../../src/ThroughlineBuild.Cli/Config.cs#L64-L75)); missing file exits 2.
6. Loads the TOML via `Tomlyn` and resolves secrets from config or environment ([src/ThroughlineBuild.Cli/Program.cs:164-186](../../src/ThroughlineBuild.Cli/Program.cs#L164-L186)).
7. Constructs per-verb dependencies (HttpClient, PlaneTicketingClient, a `WorkerAgentFactory` over all referenced agents, JsonlEventSink wrapping a RecordingEventSink) and dispatches.

### Working-directory expectations

- Must be inside (or under) a git working tree - `MainWorktreeResolver` calls `git worktree list --porcelain`.
- Must contain a discoverable `.build/config.toml`, either in the cwd or any ancestor (except `build init`, which creates it).
- For phases that operate inside a worktree (`implement`, `review`, `ship`), the worktree must be locatable by branch name or path via `git worktree list`.

### Worker CLIs

The orchestrator constructs one agent per name referenced by `default_agent` or the `[workers.phases]` map, choosing the implementation by agent name ([src/ThroughlineBuild.Cli/Program.cs:737-778](../../src/ThroughlineBuild.Cli/Program.cs#L737-L778)):

| Agent name | Implementation | External CLI | Auth posture | Status |
|---|---|---|---|---|
| `claude-code` (or any other name) | `ClaudeCodeAgent` (fallback) | `claude` | Strips `ANTHROPIC_API_KEY` from the child env to force Claude Code OAuth ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:408](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L408)). | Functional |
| `codex` | `CodexAgent` | `codex` | Strips `CODEX_API_KEY` and `OPENAI_API_KEY` to force subscription auth ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:166-167](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L166-L167)). | Functional |
| `gemini` | `GeminiAgent` | `gemini` | Strips `GEMINI_API_KEY` and `GOOGLE_API_KEY`, falling back to ADC / gcloud login ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:266-267](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L266-L267)). | Functional |
| `copilot` | `CopilotAgent` | `copilot` | Additive, not subtractive: inherits the `gh` keyring credential, or the caller supplies `GH_TOKEN` via env ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:178-188](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L178-L188)). | Functional |

Any agent name that is not `gemini`/`codex`/`copilot` falls through to `ClaudeCodeAgent` ([src/ThroughlineBuild.Cli/Program.cs:769-775](../../src/ThroughlineBuild.Cli/Program.cs#L769-L775)). The executable path per agent is `[workers.<name>].executable` (default `"claude"` for `ClaudeCodeAgent` if not overridden, [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs:7](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs#L7)). All non-Anthropic worker LLM cost flows to the operator's subscription/quota, not to the orchestrator's API key.

### Host machine requirements

| Requirement | Why | Source |
|---|---|---|
| `git` on PATH | Every phase that touches the repo runs `git` subprocesses. | [src/ThroughlineBuild.Git/ProcessGitClient.cs](../../src/ThroughlineBuild.Git/ProcessGitClient.cs) |
| The configured worker CLI(s) on PATH (or absolute path in config) | Plan / Implement / Review / Decompose / Draft phases spawn the agent named for that phase. | [src/ThroughlineBuild.Cli/Program.cs:737-778](../../src/ThroughlineBuild.Cli/Program.cs#L737-L778) |
| Plane API token | Every Plane operation needs it; a missing token aborts at secret resolution with exit 3 ([src/ThroughlineBuild.Cli/Config.cs:120-125](../../src/ThroughlineBuild.Cli/Config.cs#L120-L125)). | Config (or env) |
| Network reachability to the configured `plane_base_url` | Every ticket fetch / write hits the Plane REST API. | [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs) |
| Network reachability to `api.anthropic.com` | Only for `close`/`defer`/`reopen` (`ReasonTranslator`) - other phases reach their provider via the worker CLI's own auth. | [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs) |
| Anthropic API key | Only `close`/`defer`/`reopen` need it (reason translation via `LlmClientFactory`). If missing, those verbs exit 3 with `Secret error: anthropic_api_key not set and env var '<name>' is not set; ...` ([src/ThroughlineBuild.Cli/Program.cs:270-274](../../src/ThroughlineBuild.Cli/Program.cs#L270-L274), [src/ThroughlineBuild.Cli/LlmClientFactory.cs:16-19](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L16-L19)). |
| Native exe execution permissions | AOT binary; no JIT. |
| LF-EOL handling for templates | `.gitattributes` pins brief template files and snapshot test data to LF so substitution is byte-stable across OS ([.gitattributes:1-3](../../.gitattributes#L1-L3)). |

### The `build init` verb - Functional

`build init` scaffolds `.build/config.toml` from an embedded template. It is a pre-config verb that runs before config load, because it produces the config ([src/ThroughlineBuild.Cli/Program.cs:138-153](../../src/ThroughlineBuild.Cli/Program.cs#L138-L153)).

```
build init                              # interactive: prompts for Plane base URL / workspace / project ID / token
build init --force                      # overwrite an existing file
build init --print-template             # print the template to stdout, write nothing
build init --plane-url URL --workspace SLUG --project-id UUID --token TOKEN
build init --token-env PLANE_API_TOKEN  # write an env-var indirection line instead of an inline token
```

`InitCommand.Execute` loads the template, optionally prompts for any missing required values, applies flag substitutions, and writes the file ([src/ThroughlineBuild.Cli/InitCommand.cs:24-64](../../src/ThroughlineBuild.Cli/InitCommand.cs#L24-L64)):

- **Interactive prompting (TLB-370).** When stdin is a TTY (`!console.IsInputRedirected`), `init` prompts on the console for any of the four required values not already supplied by a flag - Plane base URL, workspace slug, project ID, and API token (the token prompt allows a blank "fill in later") ([src/ThroughlineBuild.Cli/InitCommand.cs:37-38](../../src/ThroughlineBuild.Cli/InitCommand.cs#L37-L38), [src/ThroughlineBuild.Cli/InitCommand.cs:66-100](../../src/ThroughlineBuild.Cli/InitCommand.cs#L66-L100)). Each typed answer is trimmed and ignored if empty, leaving the placeholder in place. When stdin is redirected (piped/CI), no prompts fire and the verb is fully non-interactive. Flag values always win: prompting only fills the still-`null` slots, then `ApplyFlags` substitutes ([src/ThroughlineBuild.Cli/InitCommand.cs:105-142](../../src/ThroughlineBuild.Cli/InitCommand.cs#L105-L142)).
- The template is an embedded resource `ThroughlineBuild.Commands.Templates.config.toml.template` loaded by `ConfigTemplateLoader.Load()` - no disk-relative lookup, preserving the single-binary AOT contract ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:20-36](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L20-L36)). The source file is [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template), embedded via the csproj `<EmbeddedResource>` entry ([src/ThroughlineBuild.Commands/ThroughlineBuild.Commands.csproj:11](../../src/ThroughlineBuild.Commands/ThroughlineBuild.Commands.csproj#L11)).
- Flag substitution replaces the `REQUIRED_PLANE_BASE_URL`, `REQUIRED_PLANE_WORKSPACE_SLUG`, `REQUIRED_PLANE_PROJECT_ID`, and `REQUIRED_PLANE_API_TOKEN` placeholders. `--token-env` rewrites the whole `plane_api_token = "..."` line into `plane_api_token_env = "VALUE"` and takes precedence over `--token` ([src/ThroughlineBuild.Cli/InitCommand.cs:105-142](../../src/ThroughlineBuild.Cli/InitCommand.cs#L105-L142)).
- With `--print-template`, content is written to stdout and no file is created ([src/ThroughlineBuild.Cli/InitCommand.cs:42-46](../../src/ThroughlineBuild.Cli/InitCommand.cs#L42-L46)).
- Without `--force`, an existing `.build/config.toml` causes `Error: <path> already exists. Use --force to overwrite.` and exit 1 ([src/ThroughlineBuild.Cli/InitCommand.cs:50-54](../../src/ThroughlineBuild.Cli/InitCommand.cs#L50-L54)).
- On success it creates `.build/` if needed, writes the file UTF-8, prints `Created <path>` plus a reminder to fill in the REQUIRED fields, and points the operator at `build user-guide` for the setup walkthrough ([src/ThroughlineBuild.Cli/InitCommand.cs:56-63](../../src/ThroughlineBuild.Cli/InitCommand.cs#L56-L63)).

Any placeholders left in the file are non-empty strings, so the file loads but every un-filled `REQUIRED_*` value is meaningless until edited; the operator must still supply them (interactively, via flags, or by editing). See [04-configuration.md](04-configuration.md) for the resulting sections key-by-key.

### Loose ends

- **`build init` does not validate** the substituted values against Plane - it only writes the file. There is no `build config check` verb.
- **`init` writes only the config**; it does not provision worker CLIs, register MCP tools, or validate credentials. The architecture posited a richer `build install` (Section 9, Appendix item 6); `init` is the narrower, implemented form.
- The README still documents `build new --print-template` for ticket bodies separately from `build init --print-template` ([README.md:13-26](../../README.md#L13-L26)); the `--print-template` flag now belongs to three verbs (`new`, `init`, `user-guide`) and emits different content for each.

---

## The `build user-guide` verb - Functional

`build user-guide` (TLB-322) writes the embedded operator setup guide to `docs/throughline_build_userguide.md`. Like `init` and `settarget`, it is a pre-config verb that runs without a `.build/config.toml` present ([src/ThroughlineBuild.Cli/Program.cs:167-172](../../src/ThroughlineBuild.Cli/Program.cs#L167-L172)). `build init` now points new operators here, making it the second step of the getting-started flow (run `build init`, then `build user-guide`, then read the generated guide).

```
build user-guide                 # write docs/throughline_build_userguide.md under cwd
build user-guide --force         # overwrite an existing guide file
build user-guide --print-template  # print the guide to stdout, write nothing
```

`UserGuideCommand.Execute` loads the embedded guide and writes it ([src/ThroughlineBuild.Cli/UserGuideCommand.cs:19-43](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L19-L43)):

- The guide content is an embedded resource loaded by `UserGuideLoader.Load()` - no disk lookup, preserving the single-binary AOT contract ([src/ThroughlineBuild.Commands/UserGuideLoader.cs](../../src/ThroughlineBuild.Commands/UserGuideLoader.cs)).
- With `--print-template`, content goes to stdout and no file is created ([src/ThroughlineBuild.Cli/UserGuideCommand.cs:23-27](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L23-L27)).
- Without `--force`, an existing `docs/throughline_build_userguide.md` causes `Error: <path> already exists. Use --force to overwrite.` and exit 2 ([src/ThroughlineBuild.Cli/UserGuideCommand.cs:32-36](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L32-L36)).
- On success it creates `docs/` if needed, writes UTF-8, and prints the absolute path of the written file ([src/ThroughlineBuild.Cli/UserGuideCommand.cs:38-42](../../src/ThroughlineBuild.Cli/UserGuideCommand.cs#L38-L42)).

---

## Updating

There is no built-in update mechanism. Operationally the only change is rebuilding the binary:

```
git pull
./build.sh
```

The binary contains no embedded version baked from build metadata beyond the assembly version (`Assembly.GetExecutingAssembly().GetName().Version`) read into `BuildVersion` on the `SessionContext` ([src/ThroughlineBuild.Cli/Program.cs:190-195](../../src/ThroughlineBuild.Cli/Program.cs#L190-L195)). That value flows into the event-log `build_version` field for downstream telemetry but does not gate behavior.

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
| Worktrees | `.worktrees/` (gitignored, [.gitignore:1](../../.gitignore#L1)) | `git worktree remove` each, or rely on `WorktreeDecrufter` triggered by `ship` / `close` / `defer` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:55](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L55)). |
| Scratch | `.scratch/` (gitignored, [.gitignore:10](../../.gitignore#L10)) | Delete. |
| `secrets/` | gitignored top-level ([.gitignore:2](../../.gitignore#L2)) | Delete. |

The binary itself writes no state outside the repo. No global config, no per-user state. No service or daemon to stop - architecture Section 2 explicitly forbids a persistent server.

### Loose ends

- **`WorktreeDecrufter` is best-effort** on Windows - it pre-cleans `node_modules` reparse points and retries `git worktree remove` with `--force`, but locked files from a phase killed mid-creation may need manual cleanup ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:111-144](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L111-L144)).
- **No version stamp** beyond assembly version on the binary - `bin/build.exe` does not advertise the git SHA it was built from.

---

## What the host machine must provide (summary)

Repeated as a single list for operators:

1. `.NET 10 SDK` to build; nothing at runtime (single-file AOT).
2. `git`.
3. At least the worker CLI named by `[workers] default_agent` (and any agent named in `[workers.phases]`): one or more of `claude`, `codex`, `gemini`, `copilot`.
4. Network to the configured Plane base URL and, only for `close`/`defer`/`reopen`, to `api.anthropic.com`.
5. A Plane API token (env `PLANE_API_TOKEN` by default, or inline in config) and - only for `close`/`defer`/`reopen` - an Anthropic API key.
6. Provider auth for the chosen worker CLI: Claude Code OAuth, Codex subscription, Gemini ADC/gcloud, or a `gh` credential / `GH_TOKEN` for Copilot. None of these are read by `build` directly; the worker CLI handles them.

---

## Loose ends

- **`build init`** is the implemented bootstrap; it writes config only and does not provision tools or validate credentials. A fuller `build install` remains aspirational (architecture Section 9).
- **`build.sh` RID fallback** silently defaults to `win-x64` for any unrecognized `uname` output ([build.sh:11](../../build.sh#L11)); a cross-target build on an exotic platform may publish the wrong RID without warning.
- **No version stamp** beyond assembly version on the binary.
- **No release pipeline** in `.github/`; CI artifacts are not promoted, signed, or tagged.
- **AOT trim warnings** are not gated by CI; the single reference regression test is the only AOT-aware test.
