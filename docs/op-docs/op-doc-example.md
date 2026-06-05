# Operation: cli-build-version-embedding

Add a `--version` flag to the TLB CLI and embed the build version into every structured log event. Two plans cover the work: first embed and expose the version in-process, then wire the CLI flag and event-log consumers to use it.

## Why this exists

When a chain run produces unexpected output, the first diagnostic question is "which build was this?" Currently there is no answer: the binary embeds no version, log lines carry no version metadata, and `analyze-event-log` has no way to group events by build. Bug reports arrive citing a symptom but not a binary, which forces a "reproduce from source at HEAD" step before any diagnosis can begin.

The version embedding also gates a downstream improvement: the comparison harness needs a reliable build identifier to correlate benchmark runs across TLB and the config-based baseline. Landing this before the harness runs means the harness measures TLB on the versioned contract rather than an unidentified binary.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Version foundation | - | M |
| B    | CLI and log integration | A | S |

Plan A establishes the version source and in-process accessor. Plan B depends on that accessor before exposing the CLI flag and event-log behavior.

## Plan A: Version foundation

### Goal

After this plan, the build version is embedded at compile time and readable through a single in-process accessor without requiring Plane config, event logs, or command dispatch.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | version-source | Select and document the compile-time version source | - | Directory.Build.props |
| 02 | version-accessor | Expose the embedded version through an AOT-safe accessor | 01 | src/ThroughlineBuild.Cli/BuildVersion.cs, tests/ThroughlineBuild.Cli.Tests/ |
| 03 | version-publish-gate | Prove published binaries carry a non-empty version | 02 | tests/ThroughlineBuild.Cli.Tests/ |

### Briefs - detail

#### Brief 01: version-source

Goal: The repository has one documented compile-time source for the build version so local and CI builds stamp binaries through the same MSBuild path.

Inputs:
- `Directory.Build.props`
- Existing CLI project build properties
- Current release publish command

Outputs:
- `Directory.Build.props` contains the selected version property.
- A short comment explains the local-dev fallback and CI override path.
- Existing project defaults remain unchanged except for version metadata.

Acceptance:
- [ ] The selected MSBuild property is non-empty in a local build
- [ ] The CI override path is documented next to the property
- [ ] Existing project target frameworks remain unchanged

Notes: The version source belongs in shared MSBuild configuration because the value describes the compiled binary, not runtime state. Keeping the fallback local and deterministic avoids making tests depend on CI-only environment variables.

OOS:
- Semantic versioning policy
- Release tag creation
- CLI output changes

#### Brief 02: version-accessor

Goal: Application code can read the embedded build version from one AOT-safe accessor that returns a non-empty value in tests and published binaries.

Inputs:
- Version property from Brief 01
- `src/ThroughlineBuild.Cli/Program.cs`
- Existing AOT publish settings

Outputs:
- `BuildVersion.Current` returns the embedded informational version.
- A unit test covers the non-empty runtime value.
- The accessor avoids runtime file reads and ticket-system calls.

Acceptance:
- [ ] `BuildVersion.Current` is non-empty in the unit test runner
- [ ] The accessor does not read the working tree or config files
- [ ] The accessor works before CLI verb dispatch

Notes: Reading assembly metadata keeps the value tied to the binary being executed. The accessor should be tiny because all policy decisions were made in Brief 01.

OOS:
- `build --version` command behavior
- Event-log schema changes
- Analyzer output changes

#### Brief 03: version-publish-gate

Goal: The release publish path produces a binary whose embedded build version is available at runtime.

Inputs:
- `BuildVersion.Current` from Brief 02
- Release publish command for the CLI
- Existing CLI test helpers

Outputs:
- A publish-oriented verification covers the non-empty version value.
- The check documents the release gate used by this project.
- Failures point at version stamping rather than general CLI dispatch.

Acceptance:
- [ ] Release publish produces a binary with a non-empty version
- [ ] The verification names the release gate it exercises
- [ ] AOT publish succeeds

Notes: Unit tests prove the accessor shape, while the publish gate proves the deployed artifact carries the same metadata. Keeping those concerns separate makes failures easier to diagnose.

OOS:
- Multi-RID publish matrix
- Installer packaging
- Release-note generation

## Plan B: CLI and log integration

### Goal

After this plan, operators can ask the CLI for its version without touching external services, and every structured log produced by the CLI carries that same version for later analysis.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 04 | version-flag | Add pre-dispatch `build --version` behavior | - | src/ThroughlineBuild.Cli/Program.cs, tests/ThroughlineBuild.Cli.Tests/ |
| 05 | versioned-event-log | Stamp and surface build versions in event logs | 04 | src/ThroughlineBuild.EventLog/, src/tools/analyze-event-log.cs, tests/ |

### Briefs - detail

#### Brief 04: version-flag

Goal: `build --version` prints the embedded build version and exits before config loading, Plane calls, or normal verb dispatch.

Inputs:
- `BuildVersion.Current` from Plan A
- `src/ThroughlineBuild.Cli/Program.cs`
- Existing CLI integration tests

Outputs:
- Top-level CLI argument handling recognizes `--version`.
- The command writes `throughline-build {version}` to stdout.
- The path exits successfully before any external-service setup.

Acceptance:
- [ ] `build --version` prints a non-empty version string
- [ ] `build --version` exits before config loading
- [ ] Unknown command handling remains unchanged

Notes: The version flag is a health-check path, so it must be available in minimal environments where Plane authentication and workspace config are absent. Pre-dispatch handling is the important behavioral boundary.

OOS:
- Adding version text to help output
- JSON-formatted version output
- Version comparison logic

#### Brief 05: versioned-event-log

Goal: Structured event logs carry the build version and the analyzer surfaces it in chain summaries.

Inputs:
- `BuildVersion.Current` from Plan A
- Existing event-log record types
- `src/tools/analyze-event-log.cs`

Outputs:
- Event records include a build-version field populated at construction time.
- Source-generated JSON metadata includes the new field.
- The analyzer prints the build version from the first event in a chain log.

Acceptance:
- [ ] New event-log entries contain a non-empty build-version field
- [ ] Existing event-log fixtures still deserialize
- [ ] Chain summaries include the build version
- [ ] AOT publish succeeds

Notes: Stamping the base event shape keeps all event kinds consistent and avoids per-event drift. The analyzer should read the first event because a log file is produced by one process invocation.

OOS:
- Backfilling old event logs
- Cross-run version comparison
- Plane ticket metadata updates

## What done looks like

An operator running a chain and then inspecting `analyze-event-log` output sees the build version in the chain summary, confirming exactly which binary produced the run. `build --version` works in CI health checks and local shells without reading config or contacting Plane. Bug reports now have a concrete build identifier derivable from any new log.
