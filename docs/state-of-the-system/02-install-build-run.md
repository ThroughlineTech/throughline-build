# 02 - Install, Build, Run

How the repository gets onto a machine, what the build produces, what running it requires from the host, and what changes vs. cleans up on disk.

For runtime state details see [05-state-and-persistence.md](05-state-and-persistence.md). For configuration files see [04-configuration.md](04-configuration.md). For external service dependencies see [03-external-dependencies.md](03-external-dependencies.md).

---

## Toolchain prerequisites

The repository is a `.NET 8` solution with native AOT publication.

- **`.NET 8 SDK`** - required for `dotnet build`, `dotnet test`, `dotnet publish`. Verified in CI via `actions/setup-dotnet@v4` with `dotnet-version: '8.x'` ([.github/workflows/build.yml:24-26](../../.github/workflows/build.yml#L24-L26)).
- **A native toolchain** for AOT publication on the target RID: MSVC on Windows, Xcode CLT on macOS, gcc/clang + system libc on Linux. This is implicit in `dotnet publish -r <rid>` and is not enforced by the scripts.
- **`git`** - assumed available on `PATH`. `ProcessGitClient` shells out to `git` without checking it exists; missing-git produces process-start failures that become `InvalidOperationException` ([src/ThroughlineBuild.Git/ProcessGitClient.cs:26](../../src/ThroughlineBuild.Git/ProcessGitClient.cs#L26)).
- **`claude` CLI** - the Claude Code worker. Required for any phase that dispatches a worker (`plan`, `implement`, `review`, `chain`, `new` in draft mode). Path is configurable as `workers.claude-code.executable` in the agent sub-table; default is the bare command `claude`. Authenticated via the user's existing Claude Code OAuth (not via `ANTHROPIC_API_KEY`, which `ClaudeCodeAgent` actively strips from the child environment, [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:374](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L374)).

The solution file is [throughline-build.sln](../../throughline-build.sln) covering all 14 library projects and 14 test projects.

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

Per [README.md:1-2](../../README.md#L1-L2) and `.claude/ticket-config.md:8`. Discovers and runs all 14 xUnit projects (~819 test methods). Tests target `net8.0` without `PublishAot=true`, so they do not exercise AOT-sensitive code paths under their default runner (see [architecture Section 11](../throughline-build-architecture.md), `WorkerResultParserAotRegressionTests` is the reference example for tests that opt in to the AOT switch).

### Native AOT publish of `build`

```
dotnet publish src/ThroughlineBuild.Cli -r win-x64 -c Release
```

Per [README.md:4-9](../../README.md#L4-L9). Produces `src/ThroughlineBuild.Cli/bin/Release/net8.0/<rid>/publish/build.exe` (the `.exe` extension is dropped on non-Windows RIDs because of `<AssemblyName>build</AssemblyName>` in [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:8](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L8) combined with `<PublishAot>true</PublishAot>` and `<InvariantGlobalization>true</InvariantGlobalization>`).

Cross-platform RIDs are noted in the README: `osx-arm64`, `linux-x64`.

### Three-binary bundle via `build.sh`

```
./build.sh                 # defaults to RID=win-x64
RID=osx-arm64 ./build.sh   # cross-target
```

[build.sh](../../build.sh) publishes three AOT binaries (`build`, `token-audit`, `analyze-event-log`) and copies them into `bin/` next to the script. The tools (`src/tools/token-audit.cs`, `src/tools/analyze-event-log.cs`) are single-file project-less C# sources that `dotnet publish` compiles individually.

### CI build matrix

[.github/workflows/build.yml](../../.github/workflows/build.yml) builds `ThroughlineBuild.Cli` only across `{macos-latest, windows-latest, ubuntu-latest}` on push/PR to `main`, runs `dotnet test --no-restore`, publishes per-RID artifacts via `actions/upload-artifact@v4`. No release tagging, no deploy step.

---

## Run

### The binary

After AOT publish, the binary is single-file (`<PublishAot>true</PublishAot>`, [src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj:9](../../src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj#L9)) - no `dotnet` runtime is required on the target machine. Drop it on `PATH`, run `build --help`. Architecture note: a future operator may want the alias `tl-build` to avoid colliding with project-local `build` commands (architecture Appendix item 7); no support for that alias today.

### Invocation contract

```
build <verb> [args] [--debug | --quiet] [--summary-json]
```

Verb dispatch lives in [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs). On startup, `RunAsync` (line 20):

1. Strips `--debug`, `--quiet`, `--summary-json` from `args` (these are bare bool flags, lines 28-46).
2. Validates positional args for the chosen verb.
3. Resolves the main worktree root via [src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs](../../src/ThroughlineBuild.Helpers/MainWorktreeResolver.cs) so that being invoked from inside a feature worktree still locates `.build/config.toml` and the project root.
4. Walks up from cwd to find `.build/config.toml` ([src/ThroughlineBuild.Cli/Config.cs:60-71](../../src/ThroughlineBuild.Cli/Config.cs#L60-L71)).
5. Loads the TOML via `Tomlyn`. Resolves secrets from config or environment.
6. Constructs per-verb dependencies (HttpClient, PlaneTicketingClient, ClaudeCodeAgent, JsonlEventSink wrapping a RecordingEventSink) and dispatches.

### Working-directory expectations

- Must be inside (or under) a git working tree - `MainWorktreeResolver` calls `git worktree list --porcelain`.
- Must contain a discoverable `.build/config.toml`, either in the cwd or any ancestor.
- For phases that operate inside a worktree (`implement`, `review`, `ship`), the worktree must be locatable by branch name or path via `git worktree list` ([src/ThroughlineBuild.Phases/ReviewPhase.cs:71-92](../../src/ThroughlineBuild.Phases/ReviewPhase.cs#L71-L92)).

### Host machine requirements

| Requirement | Why | Source |
|---|---|---|
| `git` on PATH | Every phase that touches the repo runs `git` subprocesses. | [src/ThroughlineBuild.Git/ProcessGitClient.cs](../../src/ThroughlineBuild.Git/ProcessGitClient.cs) |
| `claude` CLI on PATH (or absolute path in config) | Plan / Implement / Review / Draft phases spawn it. | [src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs:33-40](../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L33-L40) |
| Plane API token | Every Plane operation needs it; missing token aborts at config-load with exit 3 ([src/ThroughlineBuild.Cli/Config.cs:120-121](../../src/ThroughlineBuild.Cli/Config.cs#L120-L121)). | Config (or env) |
| Network reachability to `plane.example.com` (or whatever `plane_base_url`) | Every ticket fetch / write hits the Plane REST API. | [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs) |
| Network reachability to `api.anthropic.com` | Only for `close`/`defer`/`reopen` (`ReasonTranslator`) - other phases reach Anthropic via the `claude` CLI's own OAuth. | [src/ThroughlineBuild.Anthropic/AnthropicOptions.cs:7](../../src/ThroughlineBuild.Anthropic/AnthropicOptions.cs#L7) |
| Anthropic API key | Same: only `close`/`defer`/`reopen` need it. If missing, those verbs fail with `"anthropic api key required for close/defer/reopen (reason translation)"` ([src/ThroughlineBuild.Cli/Program.cs:1130-1133](../../src/ThroughlineBuild.Cli/Program.cs#L1130-L1133)). |
| Native exe execution permissions | AOT binary; no JIT. |
| LF-EOL handling for templates | `.gitattributes` pins template files to LF so brief substitution is byte-stable across OS. |

### What an `install` would do (not implemented)

The architecture doc names a `build install` verb for bootstrapping `.build/config.toml`, registering MCP tools, and validating Plane credentials (Section 9, Appendix item 6). No such verb exists in `Program.cs` today - configuration files must be authored by hand from [.build/config.toml.example](../../.build/config.toml.example).

---

## Updating

There is no built-in update mechanism. Operationally the only change is rebuilding the binary:

```
git pull
./build.sh
```

The binary contains no embedded version baked from build metadata beyond the assembly version (`Assembly.GetExecutingAssembly().GetName().Version`) read into `BuildVersion` in the `SessionContext` ([src/ThroughlineBuild.Cli/Program.cs:154-159](../../src/ThroughlineBuild.Cli/Program.cs#L154-L159)). That value flows into `EventLineDto.build_version` for downstream telemetry but does not gate behavior.

---

## Uninstalling

Removing the repo and its binaries:

| Artifact | Location | Cleanup |
|---|---|---|
| AOT binaries | `bin/` (gitignored) | Delete the directory. |
| Build output | each project's `bin/` and `obj/` | `dotnet clean` or delete. |
| Config | `.build/config.toml` (gitignored) | Delete. Removes the operator's secrets-in-clear from disk. |
| Event logs | `.build/events/*.jsonl` (gitignored) | Delete; safe at any time. |
| Debug sessions | `.build/sessions/<stem>/` (gitignored) | Delete; safe at any time. |
| Worktrees | `.worktrees/ticket-<slug>/` (gitignored) | `git worktree remove` each, or rely on `WorktreeDecrufter` triggered by `ship` / `close` / `defer` ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:44-192](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L44-L192)). |
| Scratch | `.scratch/` (gitignored) | Delete. |
| `secrets/` | gitignored top-level | Delete. |

The binary itself writes no state outside the repo. No global config, no per-user state. No service or daemon to stop - architecture Section 2 explicitly forbids a persistent server.

---

## What the host machine must provide (summary)

Repeated as a single list for operators:

1. `.NET 8 SDK` to build; nothing at runtime (single-file AOT).
2. `git`.
3. `claude` CLI (Claude Code) for any phase that dispatches a worker.
4. Network to Plane (`plane.example.com` by default) and Anthropic (`api.anthropic.com`) where applicable.
5. A `PLANE_API_TOKEN` (or the token in `.build/config.toml`) and an `ANTHROPIC_API_KEY` for `close`/`defer`/`reopen`.

---

## Loose ends

- **No `build install`** verb exists; the architecture posits one. Operators must hand-author `.build/config.toml` and provision the `claude` CLI and Plane token themselves.
- **No version stamp** beyond assembly version on the binary - `bin/build.exe` does not advertise the git SHA it was built from.
- **`build.sh` does not chain to test** - operators publishing locally must run `dotnet test` separately. Only CI runs both.
- **No release pipeline** in `.github/`. The published artifacts uploaded by CI are not promoted, signed, or tagged.
- **`uninstall` mode** in `WorktreeDecrufter` is best-effort - some Windows reparse points and locked files may need manual cleanup if a phase was killed mid-worktree-creation ([src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs:112](../../src/ThroughlineBuild.Helpers/WorktreeDecrufter.cs#L112)).
- **AOT trim warnings** are not gated by CI; reflection-using DTOs that slip past source-gen would produce runtime `NotSupportedException` only on the published binary (architecture Section 11). The reference regression test exists, but it is the only AOT-aware test.
