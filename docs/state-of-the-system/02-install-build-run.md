# 02 - Install, Build, Run

How the repository gets onto a machine, what the build produces, what running it requires from the host, and what changes vs. cleans up on disk.

For runtime state details see [05-state-and-persistence.md](05-state-and-persistence.md). For configuration files see [04-configuration.md](04-configuration.md). For external service dependencies see [03-external-dependencies.md](03-external-dependencies.md).

---

## Toolchain prerequisites

The repository is a `.NET 10` solution with native AOT publication (all 38 csproj - 19 `src/` + 19 `tests/` - target `net10.0`).

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

**AOT code-gen memory mitigation.** The Cli csproj sets `<IlcOptimizationPreference>Size</IlcOptimizationPreference>` and `<IlcMaxParallelism>1</IlcMaxParallelism>` ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:12-13](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L12-L13)) to keep the ILC/LLVM backend from OOM-ing during publish (the crash surfaced as ILC exit `-1073740791`). Single-threaded code-gen trades publish wall-time for a bounded memory footprint. The same fix split the oversized `RunAsync` in `Program.cs` into `RunTicketVerbBodyAsync` / `RunChainVerbAsync` so no single method blew up the code generator (commit `dd7d781`).

**Host-coupling caveat: [Directory.Build.props](../../Directory.Build.props) hardcodes one machine's MSVC/WinSDK link paths.** The root `Directory.Build.props` sets `IlcUseEnvironmentalTools=true` and points `CppLinker`/`CppLibCreator` plus `AdditionalNativeLibraryDirectories` at absolute paths (`...\VC\Tools\MSVC\14.44.35207`, `...\Windows Kits\10\Lib\10.0.26100.0`) to skip `vswhere.exe` discovery that fails in the author's environment ([Directory.Build.props:13-24](../../Directory.Build.props#L13-L24)). These paths only take effect during a native-AOT Windows publish (`-r win-x64 -c Release`); managed builds and tests are unaffected. On any other Windows machine these exact tool versions will likely differ, so a `win-x64` native publish there may fail to link until the paths are edited.

### Three-binary bundle via `build.sh` - Functional

```
./build.sh                 # RID auto-detected from uname; falls back to win-x64
RID=osx-arm64 ./build.sh   # cross-target
```

[build.sh](../../build.sh) selects the RID from `uname -s`/`uname -m` (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, otherwise `win-x64`), creating `bin/` and publishing three AOT binaries into it ([build.sh:6-30](../../build.sh#L6-L30)):

- `build` from `src/ThroughlineBuild.Cli`, copied from `src/ThroughlineBuild.Cli/bin/Release/net10.0/$RID/publish/` to `bin/build$EXT` ([build.sh:20-22](../../build.sh#L20-L22)).
- `token-audit` from the single-file source `src/tools/token-audit.cs`, copied from `src/tools/artifacts/token-audit/` to `bin/token-audit$EXT` ([build.sh:24-26](../../build.sh#L24-L26)).
- `analyze-event-log` from `src/tools/analyze-event-log.cs`, copied from `src/tools/artifacts/analyze-event-log/` to `bin/analyze-event-log$EXT` ([build.sh:28-30](../../build.sh#L28-L30)). It now tolerates malformed JSONL rows (commit c965d30): a row that fails to parse (`JsonException`) or is missing expected fields (`KeyNotFoundException`/`InvalidOperationException`) is skipped and counted, and a trailing `!! WARNING: skipped N malformed/truncated line(s)` is printed rather than aborting the whole report ([src/tools/analyze-event-log.cs:125-130](../../src/tools/analyze-event-log.cs#L125-L130), [:205-209](../../src/tools/analyze-event-log.cs#L205-L209)).

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

After AOT publish, the binary is single-file (`<PublishAot>true</PublishAot>`, [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:10](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L10)) - no `dotnet` runtime is required on the target machine. Put it on `PATH` (operationally, copy from `bin/` or symlink there) and run `build --help`. Architecture note: a future operator may want the alias `tl-build` to avoid colliding with project-local `build` commands (architecture Appendix item 7); no support for that alias today.

### Invocation contract

```
build <verb> [args] [--debug | --quiet] [--summary-json] [--error-location]
```

Verb dispatch lives in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs) (~2209 lines; the dispatch is a chain of `if (verb == ...)` blocks, not a registry). On startup, `RunAsync` ([src/ThroughlineBuild.Cli/Program.cs:22](../../src/ThroughlineBuild.Cli/Program.cs#L22)):

1. Strips bare bool flags `--debug`, `--quiet`, `--summary-json`, `--error-location`, `--no-auto-resolve`, `--no-auto-merge`, `--no-push`, `--continue-past-failure`, `--from-brief`, `--skip-baseline` from `args` ([src/ThroughlineBuild.Cli/Program.cs:74-100](../../src/ThroughlineBuild.Cli/Program.cs#L74-L100)).
2. Extracts the agent-selection flags `--agent` / `--agent-plan` / `--agent-implement` / `--agent-review` via `CliArgParser.ExtractAgentFlags` ([src/ThroughlineBuild.Cli/Program.cs:102-105](../../src/ThroughlineBuild.Cli/Program.cs#L102-L105)).
3. For `init`, bootstraps the config file and exits before any config load ([src/ThroughlineBuild.Cli/Program.cs:231](../../src/ThroughlineBuild.Cli/Program.cs#L231)) - see "The `build init` verb" below. `settarget` ([:294](../../src/ThroughlineBuild.Cli/Program.cs#L294)), `user-guide` ([:304](../../src/ThroughlineBuild.Cli/Program.cs#L304)), `op-doc` ([:313](../../src/ThroughlineBuild.Cli/Program.cs#L313)), and `models refresh` ([:403](../../src/ThroughlineBuild.Cli/Program.cs#L403)) are the other pre-config verbs, dispatched the same way - they edit or ignore the config rather than read it; see [01-inventory.md](01-inventory.md) and [04-configuration.md](04-configuration.md).
4. Resolves the main worktree root via [src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs) so that being invoked from inside a feature worktree still locates `.build/config.toml` and the project root ([src/ThroughlineBuild.Cli/Program.cs:422-423](../../src/ThroughlineBuild.Cli/Program.cs#L422-L423)).
5. Walks up from cwd to find `.build/config.toml` via `BuildConfigLoader.FindConfigFile` ([src/ThroughlineBuild.Cli/Program.cs:430-438](../../src/ThroughlineBuild.Cli/Program.cs#L430-L438)); missing file exits 2.
6. Loads the TOML via `Tomlyn` and resolves secrets from config or environment ([src/ThroughlineBuild.Cli/Program.cs:440-464](../../src/ThroughlineBuild.Cli/Program.cs#L440-L464)); a missing required secret exits 3.
7. Constructs per-verb dependencies (HttpClient, PlaneTicketingClient, a `WorkerAgentFactory` over all referenced agents, JsonlEventSink wrapping a RecordingEventSink) and dispatches.

### Working-directory expectations

- Must be inside (or under) a git working tree - `MainWorktreeResolver` calls `git worktree list --porcelain`.
- Must contain a discoverable `.build/config.toml`, either in the cwd or any ancestor (except `build init`, which creates it).
- For phases that operate inside a worktree (`implement`, `review`, `ship`), the worktree must be locatable by branch name or path via `git worktree list`.

### Worker CLIs

The orchestrator constructs one agent per name referenced by `default_agent` or the `[workers.phases]` map. The name -> implementation switch now lives in the shared `WorkerAgentBuilder.Create` so the phase-verb factory and the scaffold profile path build agents identically ([src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:16-45](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L16-L45)); `Program.cs` collects the referenced agent names and registers a `WorkerAgentFactory` over them ([src/ThroughlineBuild.Cli/Program.cs:1078-1087](../../src/ThroughlineBuild.Cli/Program.cs#L1078-L1087)):

| Agent name | Implementation | External CLI | Auth posture | Status |
|---|---|---|---|---|
| `claude-code` (or any other name) | `ClaudeCodeAgent` (fallback) | `claude` | Strips `ANTHROPIC_API_KEY` from the child env to force Claude Code OAuth ([src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:408](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L408)). | Functional |
| `codex` | `CodexAgent` | `codex` | Strips `CODEX_API_KEY` and `OPENAI_API_KEY` to force subscription auth ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:338-339](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L338-L339)). | Functional |
| `gemini` | `GeminiAgent` | `gemini` | Strips `GEMINI_API_KEY` and `GOOGLE_API_KEY`, falling back to ADC / gcloud login ([src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs:266-267](../../src/ThroughlineBuild.Workers.Gemini/GeminiAgent.cs#L266-L267)). | Functional |
| `copilot` | `CopilotAgent` | `copilot` | Additive, not subtractive: inherits the `gh` keyring credential, or the caller supplies `GH_TOKEN` via env ([src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs:178-188](../../src/ThroughlineBuild.Workers.Copilot/CopilotAgent.cs#L178-L188)). | Functional |

Any agent name that is not `gemini`/`codex`/`copilot` falls through to `ClaudeCodeAgent` (the `_ =>` arm of the switch, [src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs:38-44](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs#L38-L44)). The executable path per agent is `[workers.<name>].executable` (default `"claude"` for `ClaudeCodeAgent` if not overridden, [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs:7](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeOptions.cs#L7)). Codex additionally receives `-c model_reasoning_effort=<effort>` on its argv when the resolved size tier sets an `effort` ([src/ThroughlineBuild.Workers.Codex/CodexAgent.cs:373-375](../../src/ThroughlineBuild.Workers.Codex/CodexAgent.cs#L373-L375)). All non-Anthropic worker LLM cost flows to the operator's subscription/quota, not to the orchestrator's API key.

### Host machine requirements

| Requirement | Why | Source |
|---|---|---|
| `git` on PATH | Every phase that touches the repo runs `git` subprocesses. | [src/ThroughlineBuild.Git/ProcessGitClient.cs](../../src/ThroughlineBuild.Git/ProcessGitClient.cs) |
| The configured worker CLI(s) on PATH (or absolute path in config) | Plan / Implement / Review / Decompose / Draft phases spawn the agent named for that phase. | [src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs](../../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs) |
| Plane API token | Every Plane operation needs it; a missing token aborts at secret resolution with exit 3 ([src/ThroughlineBuild.Cli/Config.cs:120-125](../../src/ThroughlineBuild.Cli/Config.cs#L120-L125)). | Config (or env) |
| Network reachability to the configured `plane_base_url` | Every ticket fetch / write hits the Plane REST API. | [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs) |
| Network reachability to `api.anthropic.com` | Only for `close`/`defer`/`reopen` (`ReasonTranslator`) - other phases reach their provider via the worker CLI's own auth. | [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs) |
| Anthropic API key | Optional even for `close`/`defer`/`reopen` (reason translation via `LlmClientFactory`). Since TLB-371 a missing key no longer exits 3: `WireUpConditionalCommands` catches the `ConfigException`, prints `WARNING: LLM unavailable (...); recording reason verbatim without translation.`, and substitutes `EchoLlmClient` so the verb still completes ([src/ThroughlineBuild.Cli/Program.cs:2162-2172](../../src/ThroughlineBuild.Cli/Program.cs#L2162-L2172), [src/ThroughlineBuild.Cli/LlmClientFactory.cs:16-19](../../src/ThroughlineBuild.Cli/LlmClientFactory.cs#L16-L19)). |
| Native exe execution permissions | AOT binary; no JIT. |
| LF-EOL handling for templates | `.gitattributes` pins brief template files and snapshot test data to LF so substitution is byte-stable across OS ([.gitattributes:1-3](../../.gitattributes#L1-L3)). |

### The `build init` verb - Functional

`build init` scaffolds `.build/config.toml` from an embedded template. It is a pre-config verb that runs before config load, because it produces the config ([src/ThroughlineBuild.Cli/Program.cs:231](../../src/ThroughlineBuild.Cli/Program.cs#L231)). Since op-34 it is no longer a write-only stamper: when given (or interactively able to obtain) a live connection it resolves a real Plane project and runs the same provisioning as `build setup`, so the operator never types a project UUID.

```
build init                              # interactive guided connect: URL -> workspace -> token, then create-or-pick a project
build init --force                      # overwrite an existing file
build init --print-template             # print the template to stdout, write nothing (never probes / never connects)
build init --plane-url URL --workspace SLUG --project-name NAME --token TOKEN  # non-interactive connected mode
build init --plane-url URL --workspace SLUG --project-id UUID --token TOKEN    # offline: explicit id, no resolution
build init --token-env PLANE_API_TOKEN  # write an env-var indirection line instead of an inline token
build init --from FILE                  # read creds from a key=value file (or redirected stdin); flags still win
build init --no-interactive             # never prompt, even at a TTY
```

`InitCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/InitCommand.cs:68-183](../../src/ThroughlineBuild.Cli/InitCommand.cs#L68-L183)) loads the template, gathers credentials (flags, then `--from`/stdin, then prompts), and picks one of three paths:

- **Offline mode** (no project name, or an explicit `--project-id`/`plane_project_id`): substitutes flag values into the template and writes it, optionally enriching the Codex sizes block from a model probe, then prints the still-`REQUIRED` field names plus the next steps - run `build setup`, or re-run `init` in connected mode ([src/ThroughlineBuild.Cli/InitCommand.cs:190-220](../../src/ThroughlineBuild.Cli/InitCommand.cs#L190-L220)). Behavior unchanged from the old write-only form.
- **Non-interactive connected mode** (`--project-name` + full creds, no explicit id): resolves or creates the named Plane project via `ProjectResolver` (find-or-create), substitutes the resolved id, runs `SetupCommand` provisioning (git init, `.gitignore`, states, labels), makes the welcome commit, verifies connectivity, and prints a one-line summary ([src/ThroughlineBuild.Cli/InitCommand.cs:255-484](../../src/ThroughlineBuild.Cli/InitCommand.cs#L255-L484)).
- **Interactive guided connect** (TTY, no creds file, not `--no-interactive`, with URL + workspace + a non-blank token): prompts in connection order - base URL, workspace slug, token ([src/ThroughlineBuild.Cli/InitCommand.cs:640-666](../../src/ThroughlineBuild.Cli/InitCommand.cs#L640-L666)) - then offers create-or-pick: create a new project (with a derived default identifier the operator can accept or override) or choose an existing one from a most-recently-used menu ([src/ThroughlineBuild.Cli/InitCommand.cs:492-595](../../src/ThroughlineBuild.Cli/InitCommand.cs#L492-L595)). The raw "paste a project UUID" prompt is gone. A blank token at the prompt declines the connection and falls back to writing the offline template.

Notes:

- The template is an embedded resource `ThroughlineBuild.Commands.Templates.config.toml.template` loaded by `ConfigTemplateLoader.Load()` - no disk-relative lookup, preserving the single-binary AOT contract ([src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs:20-36](../../src/ThroughlineBuild.Commands/ConfigTemplateLoader.cs#L20-L36)). The source file is [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template), embedded via the csproj `<EmbeddedResource>` entry. There is no longer a checked-in `.build/config.toml.example`; the template is the embedded resource only.
- Flag substitution replaces the `REQUIRED_PLANE_BASE_URL`, `REQUIRED_PLANE_WORKSPACE_SLUG`, `REQUIRED_PLANE_PROJECT_ID`, and `REQUIRED_PLANE_API_TOKEN` placeholders. `--token-env` rewrites the whole `plane_api_token = "..."` line into `plane_api_token_env = "VALUE"` and takes precedence over `--token` ([src/ThroughlineBuild.Cli/InitCommand.cs:703-740](../../src/ThroughlineBuild.Cli/InitCommand.cs#L703-L740)).
- **Codex tier discovery (op-33).** Unless `--print-template`, on any write path `init` shells out to the codex CLI's `codex debug models` and, on success, rewrites the commented `[workers.codex.sizes]` block with a discovered small/medium/large mapping; on probe failure it leaves the static defaults and prints one warning, still exiting 0 ([src/ThroughlineBuild.Cli/InitCommand.cs:227-249](../../src/ThroughlineBuild.Cli/InitCommand.cs#L227-L249)). The Claude block is never touched. See [03-external-dependencies.md](03-external-dependencies.md) for the probe contract.
- With `--print-template`, content is written to stdout and no file is created; this path returns before any probe or connection ([src/ThroughlineBuild.Cli/InitCommand.cs:125-129](../../src/ThroughlineBuild.Cli/InitCommand.cs#L125-L129)).
- Without `--force`, an existing `.build/config.toml` causes `Error: <path> already exists. Use --force to overwrite.` and exit 1; the clobber guard runs before any probe or connection ([src/ThroughlineBuild.Cli/InitCommand.cs:134-138](../../src/ThroughlineBuild.Cli/InitCommand.cs#L134-L138)).
- Unknown `init` flags fail loudly (exit 2) rather than being silently dropped ([src/ThroughlineBuild.Cli/Program.cs:242-250](../../src/ThroughlineBuild.Cli/Program.cs#L242-L250)).

In offline mode, placeholders left in the file are non-empty strings, so the file loads but every un-filled `REQUIRED_*` value is meaningless until edited or provisioned. In connected mode the project id and the Plane states/labels are real on exit. See [04-configuration.md](04-configuration.md) for the resulting sections key-by-key.

### Loose ends

- **Offline `build init` does not validate** the substituted values against Plane - it only writes the file. There is no `build config check` verb. Connected mode does probe connectivity and reports OK/FAILED.
- The README documents `build new --print-template` for ticket bodies separately from `build init --print-template` ([README.md:13-26](../../README.md#L13-L26)); the `--print-template` flag now belongs to four pre-config verbs (`new`, `init`, `user-guide`, `op-doc spec`) and emits different content for each.

---

## The `build setup` verb - Functional

`build setup` (op-34) is the provisioning step that makes a fresh repo workflow-ready: it brings the local repo and the Plane project up to the criteria the rest of `build` assumes, idempotently. It runs after config load (it needs the resolved Plane credentials) but spawns no worker and writes no event log ([src/ThroughlineBuild.Cli/Program.cs:518-555](../../src/ThroughlineBuild.Cli/Program.cs#L518-L555)). Connected `build init` invokes the same `SetupCommand` internally, so a one-shot connected init and a separate `init` + `setup` reach the same end state.

```
build setup            # provision: git init + standard .gitignore + missing Plane states/labels
build setup --check    # verify-only: report every gap and exit non-zero if any remain; mutate nothing
```

`SetupCommand.ExecuteAsync` ([src/ThroughlineBuild.Cli/SetupCommand.cs:33-48](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L33-L48)) does two idempotent passes:

- **Local repo** ([src/ThroughlineBuild.Cli/SetupCommand.cs:51-91](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L51-L91)): `git init` if the directory is not yet a git repo, then append any missing standard `.gitignore` entries under a managed header without disturbing existing content. The canonical entry list and append-only merge live in `GitignoreManager` - build-tool artifacts (`.build/config.toml`, `.build/events/`, `.worktrees/`, `secrets/`, ...) plus language-neutral OS/editor noise; nothing stack-specific ([src/ThroughlineBuild.Cli/LocalRepoSetup.cs:13-79](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs#L13-L79)). On a non-`--check` run, a brand-new repo also gets a welcome commit so the first `build ship` has a base ref ([src/ThroughlineBuild.Cli/SetupCommand.cs:40-41](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L40-L41)).
- **Plane project** ([src/ThroughlineBuild.Cli/SetupCommand.cs:94-150](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L94-L150)): lists existing states/labels and creates whatever is missing against the canonical `WorkspaceSchema` - 7 states (`Backlog`, `Planning`, `Ready`, `In Progress`, `In Review`, `Done`, `Cancelled`) and 9 labels (`risk:low|medium|high`, `size:s|m|l`, `plan-ticket`, `stub`, `delegated`) ([src/ThroughlineBuild.Contracts/WorkspaceSchema.cs:23-44](../../src/ThroughlineBuild.Contracts/WorkspaceSchema.cs#L23-L44)). This schema is the single source of truth shared with the runtime state-name map, so the two cannot drift; a project missing a required label hard-fails the plan/chain phases at runtime, which is why `setup` exists.

With `--check`, nothing is mutated: each gap is reported to stderr and the command exits 1 if any local or Plane gap remains, 0 otherwise ([src/ThroughlineBuild.Cli/SetupCommand.cs:45-47](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L45-L47)). A `404` on the project route is surfaced as the actionable `BuildProjectNotFoundMessage` ("project id does not resolve ... re-run 'build init' connected mode"), not the raw Plane body ([src/ThroughlineBuild.Cli/Program.cs:542-549](../../src/ThroughlineBuild.Cli/Program.cs#L542-L549)).

### Loose ends

- **`build setup` does not provision worker CLIs**, register MCP tools, or write the config itself - it assumes `.build/config.toml` already exists. The architecture posited a richer `build install` (Section 9, Appendix item 6); `init` + `setup` are the narrower, implemented form.
- **The `.gitignore` entry list and `WorkspaceSchema` are hardcoded** - a project that needs a different ignore set or non-standard state names must edit after the fact; `setup` only adds, never removes or renames.

---

## The `build models` verb - Functional

`build models refresh` re-runs the Codex `debug models` probe and rewrites the `[workers.codex.sizes]` block in the existing `.build/config.toml`. Like `init`, it is a pre-config verb (it edits the config rather than reading it for behavior) and shells out to the same `CodexModelProbe` ([src/ThroughlineBuild.Cli/Program.cs:403-420](../../src/ThroughlineBuild.Cli/Program.cs#L403-L420)). `build models` with no/unknown subcommand prints a usage error and exits 2. See [03-external-dependencies.md](03-external-dependencies.md) for the probe contract.

---

## The `build user-guide` verb - Functional

`build user-guide` (TLB-322) writes the embedded operator setup guide to `docs/throughline_build_userguide.md`. Like `init` and `settarget`, it is a pre-config verb that runs without a `.build/config.toml` present ([src/ThroughlineBuild.Cli/Program.cs:304-309](../../src/ThroughlineBuild.Cli/Program.cs#L304-L309)). `build init` now points new operators here as part of the getting-started flow (run `build init`, then `build user-guide`, then read the generated guide).

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

The binary now embeds a compile-time version string `{VersionPrefix}+{shortSha}` (e.g. `0.1.0+09172e5`). The `GenerateBuildVersionSource` MSBuild target writes it as a `const string BuildVersion.Current` into a generated source file before compile, degrading to the bare version when `SourceRevisionId` is empty (non-git / shallow build) and staying a const to keep the AOT publish reflection-free ([src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:54-80](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L54-L80)). `build -V` / `build --version` prints `BuildVersion.Current` ([src/ThroughlineBuild.Cli/Program.cs:27-31](../../src/ThroughlineBuild.Cli/Program.cs#L27-L31)) and it flows into the `SessionContext.BuildVersion` and the event-log `build_version` field ([src/ThroughlineBuild.Cli/Program.cs:472](../../src/ThroughlineBuild.Cli/Program.cs#L472)) for telemetry; it does not gate behavior.

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
- **Version stamp now carries the short SHA** - `build -V` prints `{VersionPrefix}+{shortSha}` (e.g. `0.1.0+09172e5`), baked in at compile time. A non-git or shallow build degrades to the bare version with no SHA.

---

## What the host machine must provide (summary)

Repeated as a single list for operators:

1. `.NET 10 SDK` to build; nothing at runtime (single-file AOT).
2. `git`.
3. At least the worker CLI named by `[workers] default_agent` (and any agent named in `[workers.phases]`): one or more of `claude`, `codex`, `gemini`, `copilot`.
4. Network to the configured Plane base URL and, only for `close`/`defer`/`reopen`, to `api.anthropic.com`.
5. A Plane API token (env `PLANE_API_TOKEN` by default, or inline in config). An Anthropic API key is optional even for `close`/`defer`/`reopen` - it only translates reason text; absent a key those verbs record the reason verbatim and still complete (TLB-371). `build init --project-name`/connected mode and `CreateProjectAsync` additionally need a token with workspace-admin scope to create a new Plane project.
6. Provider auth for the chosen worker CLI: Claude Code OAuth, Codex subscription, Gemini ADC/gcloud, or a `gh` credential / `GH_TOKEN` for Copilot. None of these are read by `build` directly; the worker CLI handles them.

---

## Loose ends

- **`build init` + `build setup`** are the implemented bootstrap: offline `init` writes config only, while connected `init`/`setup` also resolve-or-create the Plane project and provision its states/labels + the local repo. Neither provisions the worker CLIs. A fuller `build install` remains aspirational (architecture Section 9).
- **`build.sh` RID fallback** silently defaults to `win-x64` for any unrecognized `uname` output ([build.sh:11](../../build.sh#L11)); a cross-target build on an exotic platform may publish the wrong RID without warning.
- **Version stamp is `{VersionPrefix}+{shortSha}`** baked at compile time (e.g. `0.1.0+09172e5`); `VersionPrefix` is hand-edited in the Cli csproj, so the `0.1.0` prefix is not auto-bumped per release.
- **No release pipeline** in `.github/`; CI artifacts are not promoted, signed, or tagged.
- **AOT trim warnings** are not gated by CI; the single reference regression test is the only AOT-aware test.
