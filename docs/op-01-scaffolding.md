# Operation:build-scaffolding

Validate the .NET 8 AOT toolchain works for the Throughline Build architecture across Mac, Windows, and Linux. Establish project layout, test framework, and CI baseline before any business logic ships.

## Why this exists

The architecture commits to .NET 8 native AOT for single-binary cross-platform distribution. Some libraries break under AOT (reflection-heavy paths). Some CI runners have platform-specific gotchas. This op-doc validates the stack works for the actual dependency set before committing any business logic to .NET specifics. Five hours of work that gates everything downstream.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | AOT-validated project skeleton | - | S |

## Plan A: AOT-validated project skeleton

### Goal

A solution that builds clean to a static AOT binary on Mac, Windows, and Linux. The binary exercises HTTP, JSON, and subprocess spawning (the three capabilities the orchestrator depends on most). A test project runs xUnit tests deterministically. CI builds AOT artifacts on all three OSes and runs tests.

Brief sequence: B01 establishes the solution. B02 wires the AOT publish target. B03 adds a working spike that exercises critical APIs. B04 adds the test project. B05 adds CI.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | solution-layout | Create solution with src/ and tests/ subfolders | - | throughline-build.sln, src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj, src/ThroughlineBuild.Cli/Program.cs |
| 02 | aot-publish-config | Configure native AOT in the CLI csproj | 01 | src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj, Directory.Build.props |
| 03 | aot-spike | Hello-world that calls HTTP, parses JSON, spawns subprocess | 02 | src/ThroughlineBuild.Cli/Program.cs |
| 04 | test-project | xUnit test project that runs deterministically | 01 | tests/ThroughlineBuild.Cli.Tests/ThroughlineBuild.Cli.Tests.csproj, tests/ThroughlineBuild.Cli.Tests/SpikeTests.cs |
| 05 | ci-pipeline | GitHub Actions workflow: build AOT and run tests on Mac, Windows, Linux | 04 | .github/workflows/build.yml |

### Briefs - detail

#### Brief 01: solution-layout

Goal: Create a .NET 8 solution with the conventional layout. One CLI project under `src/`, future test projects under `tests/`. The solution should open clean in Visual Studio, Rider, and VS Code with the C# Dev Kit. The CLI produces a binary named `build` (with `tl-build` as an alternative name if the operator needs to avoid collision with system `build` commands).

Inputs:
- .NET 8 SDK
- Standard csproj/sln conventions

Outputs:
- `throughline-build.sln` at repo root
- `src/ThroughlineBuild.Cli/ThroughlineBuild.Cli.csproj` targeting `net8.0` with `<AssemblyName>build</AssemblyName>` so the output binary is named `build`
- `src/ThroughlineBuild.Cli/Program.cs` with `Console.WriteLine("throughline build");`
- `.gitignore` for .NET projects (bin/, obj/, *.user, .vs/)

Acceptance:
- [ ] `dotnet build` succeeds on a clean clone
- [ ] `dotnet run --project src/ThroughlineBuild.Cli` prints `throughline build`
- [ ] The built executable is named `build` (or `build.exe` on Windows)
- [ ] No warnings on build
- [ ] `<Nullable>enable</Nullable>` and `<LangVersion>12</LangVersion>` set in the csproj

Notes: Project SDK is `Microsoft.NET.Sdk`. Solution file uses the modern format. No NuGet dependencies at this brief.

OOS:
- Do not add NuGet dependencies yet
- Do not add AOT config yet (B02)
- Do not add business logic
- Do not import any other repo

#### Brief 02: aot-publish-config

Goal: Configure the CLI csproj to publish as a native AOT binary on each supported platform (osx-arm64, osx-x64, win-x64, linux-x64).

Inputs:
- The csproj from B01
- .NET 8 native AOT documentation: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/

Outputs:
- Updated `ThroughlineBuild.Cli.csproj` with `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>true</InvariantGlobalization>`, `<IlcOptimizationPreference>Speed</IlcOptimizationPreference>`
- `Directory.Build.props` at repo root if shared MSBuild properties are useful

Acceptance:
- [ ] `dotnet publish -r osx-arm64 -c Release` succeeds and produces a native binary named `build`
- [ ] `dotnet publish -r win-x64 -c Release` succeeds and produces `build.exe`
- [ ] `dotnet publish -r linux-x64 -c Release` succeeds and produces a native `build`
- [ ] Each binary is under 30 MB
- [ ] Each binary runs on its target OS and prints `throughline build`
- [ ] No AOT analyzer warnings

Notes: AOT requires `InvariantGlobalization=true` for the smallest binary. Cross-compilation works from any host but the binary only runs on its target OS.

OOS:
- Do not add a self-contained non-AOT publish profile
- Do not add code-signing or notarization
- Do not add installer packaging

#### Brief 03: aot-spike

Goal: Replace the hello-world with code that exercises the three capabilities the orchestrator depends on most: HTTP GET, JSON parse, subprocess spawn. Validates these work under AOT.

Inputs:
- .NET 8 stdlib (`System.Net.Http`, `System.Text.Json`, `System.Diagnostics.Process`)
- A public URL that returns text or JSON (e.g., https://api.github.com/zen)

Outputs:
- Updated `src/ThroughlineBuild.Cli/Program.cs` that:
  - Issues an HTTP GET to a public URL
  - Parses the response as text or JSON via `JsonDocument.Parse`
  - Spawns `git --version` as a subprocess and captures stdout
  - Prints both results to console

Acceptance:
- [ ] Runs from a fresh `dotnet run`
- [ ] Runs from each platform's published AOT binary
- [ ] HTTP call succeeds; JSON or text parse succeeds; subprocess output captured
- [ ] No runtime errors related to AOT trimming or reflection
- [ ] No AOT analyzer warnings

Notes: HttpClient + System.Text.Json + System.Diagnostics.Process are AOT-compatible in .NET 8 if used straightforwardly. Use `JsonDocument.Parse` (not `JsonSerializer.Deserialize<T>` without source generators) to keep this AOT-simple. If anything fails, this is where we find out and revise.

OOS:
- Do not add error handling beyond what proves the spike worked
- Do not add CLI argument parsing
- Do not add logging frameworks

#### Brief 04: test-project

Goal: Add an xUnit test project that references the CLI project, runs deterministically, and gives CI something to execute.

Inputs:
- xUnit 2.x with `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`
- The CLI project

Outputs:
- `tests/ThroughlineBuild.Cli.Tests/ThroughlineBuild.Cli.Tests.csproj` targeting `net8.0`
- `tests/ThroughlineBuild.Cli.Tests/SpikeTests.cs` with at least one non-trivial passing test
- Test project added to the solution

Acceptance:
- [ ] `dotnet test` runs and reports passing
- [ ] At least one test exists and is non-trivial
- [ ] Exit code 0 on success, non-zero on failure
- [ ] Test discovery works in VS / Rider / VS Code C# Dev Kit

Notes: Foundation later needs fast tests with no external dependencies. Start that discipline here.

OOS:
- Do not add integration tests
- Do not add tests that hit external services
- Do not add fluent assertion libraries

#### Brief 05: ci-pipeline

Goal: GitHub Actions workflow that builds the AOT binary on Mac, Windows, and Linux runners and runs `dotnet test` on each.

Inputs:
- GitHub Actions documentation
- The repo with prior briefs in place

Outputs:
- `.github/workflows/build.yml` with matrix strategy across `macos-latest`, `windows-latest`, `ubuntu-latest`
- Each job: checkout, setup-dotnet (8.x), restore, test, publish AOT for the runner's RID

Acceptance:
- [ ] All three matrix jobs pass on first push to main
- [ ] AOT binaries produced as build artifacts
- [ ] Test failures fail the CI job
- [ ] Jobs run in parallel (matrix), not sequentially

Notes: `macos-latest` is currently arm64. Add `macos-13` (x64) as a fourth matrix entry if x64 Mac coverage matters; otherwise skip for v1.

OOS:
- Do not add deployment steps
- Do not add release automation
- Do not add code-signing
- Do not gate on PRs yet (push-to-main is fine for the spike)

## What done looks like

A fresh clone of the repo builds clean on any of Mac, Windows, or Linux. Running `dotnet publish -r <rid> -c Release` produces a native AOT binary named `build` (under 30 MB) that, when executed, performs an HTTP GET, parses JSON, spawns a subprocess, and exits zero. `dotnet test` runs and passes. CI on GitHub Actions is green across all three OS matrix jobs. The repo is ready to receive the typed contracts and helpers from op-doc 2.
