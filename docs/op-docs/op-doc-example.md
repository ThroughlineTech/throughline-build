# Operation: cli-build-version-embedding

Add a `--version` flag to the TLB CLI and embed the build version into every structured
log event. Two briefs: one to embed the version into the binary at publish time and expose
it in-process, one to wire the CLI flag and stamp log events.

## Why this exists

When a chain run produces unexpected output, the first diagnostic question is "which build
was this?" Currently there is no answer: the binary embeds no version, log lines carry no
version metadata, and `analyze-event-log` has no way to group events by build. Bug reports
arrive citing a symptom but not a binary, which forces a "reproduce from source at HEAD"
step before any diagnosis can begin.

The version embedding also gates a downstream improvement: the comparison harness (in
progress) needs a reliable build identifier to correlate benchmark runs across TLB and the
config-based baseline. Landing this before the harness runs means the harness measures TLB
on the versioned contract rather than an unidentified binary.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Version embedding and CLI flag | - | S |

Single plan. Brief 01 (embedding) first; Brief 02 depends on it.

## Plan A: Version embedding and CLI flag

### Goal

After this plan, `build --version` prints the current build version and exits; every
structured log event carries a `build_version` metadata field; and `analyze-event-log`
surfaces the build version in chain summaries. The binary embeds the version at
`dotnet publish` time via a MSBuild property so no manual version-bump step exists.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | version-embedding | Embed version at publish time; expose via static accessor | - | Directory.Build.props, src/ThroughlineBuild.Cli/BuildVersion.cs (new), tests/ |
| 02 | version-flag-and-log | Wire `build --version`; stamp every log event with the build version | 01 | src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Events/EventLog.cs, tests/ |

### Briefs - detail

#### Brief 01: version-embedding

Goal: The build version is embedded into the binary at `dotnet publish` time via a MSBuild
`AssemblyInformationalVersion` property and readable in-process via a static
`BuildVersion.Current` accessor. No manual version-bump step; the value comes from the
build environment.

Inputs: The current `Directory.Build.props`; the MSBuild AOT publish targets
(`dotnet publish -r <rid> --self-contained -p:PublishAot=true`); any existing assembly
attribute wiring in `GlobalUsings.cs` or a project-level `AssemblyInfo.cs` if present
(grep for `AssemblyInformationalVersion` to check).

Outputs:
- `Directory.Build.props` sets `AssemblyInformationalVersion` to a value sourced from the
  build environment. Implementer picks the available source (`$(SourceRevisionId)` if
  GitVersion is not configured; the short git SHA stamped by dotnet at build time) and
  documents the choice in a comment in `Directory.Build.props`.
- A new `src/ThroughlineBuild.Cli/BuildVersion.cs` exposing a `static string
  BuildVersion.Current` property that reads the embedded value via
  `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.
- `BuildVersion.Current` is AOT-safe: the attribute read works in a published binary
  without reflection trimming the attribute (add `[DynamicallyAccessedMembers]` or
  a trim-safe equivalent if the attribute is trimmed in a test publish).
- A unit test asserting `BuildVersion.Current` is non-null and non-empty.

Acceptance:
- [ ] `dotnet publish` on any release RID embeds a non-empty `AssemblyInformationalVersion`
- [ ] `BuildVersion.Current` returns the embedded value at runtime in a published binary
- [ ] `BuildVersion.Current` does not throw or return empty in the unit test runner
- [ ] AOT publish succeeds on all three release RIDs

Notes: The version source is the implementer's call based on what is wired in the build
environment. `$(SourceRevisionId)` is the short git SHA stamped by `dotnet` at build time
and is available with no additional tooling - it is a safe fallback if GitVersion is not
configured. A static string like `0.0.0-dev` is acceptable for local dev builds as long as
CI sets the value via an environment variable or MSBuild property override at build time.
The important constraint is that the value is set at compile time, not at process startup,
so it reflects the binary's actual provenance.

OOS:
- Semantic versioning or release tagging (separate process decision)
- CI pipeline changes to set the version variable (separate ticket if the build env needs it)
- Wiring the version into the CLI flag or log events (Brief 02 owns both)

#### Brief 02: version-flag-and-log

Goal: `build --version` prints the embedded build version and exits 0; every structured
log event carries a `build_version` field; `analyze-event-log` surfaces the build version
in chain summaries.

Inputs: `BuildVersion.Current` from Brief 01; the current `Program.cs` CLI dispatch at
`src/ThroughlineBuild.Cli/Program.cs` (read end-to-end to find where to intercept before
verb dispatch); the base event type shared by all events in
`src/ThroughlineBuild.Events/EventLog.cs`; the `analyze-event-log` summary output path
(grep for "chain summary" or "analyze" in `src/ThroughlineBuild.Cli/`).

Outputs:
- `build --version` recognized at the top of `Main`, before any verb dispatch, Plane API
  calls, or config validation. Prints `throughline-build {BuildVersion.Current}` to stdout
  and exits 0.
- Every structured log event's base metadata carries a `build_version` string field
  populated from `BuildVersion.Current` at event-construction time. Added to the base event
  type so all event kinds inherit it without per-event-kind changes.
- New `build_version` field registered in the source-gen JSON context covering the base
  event type; AOT-safe.
- `analyze-event-log` chain summaries include one line - `Build: {version}` - taken from
  the first event in the log.
- A CLI integration test: `build --version` exits 0 and stdout matches
  `throughline-build \S+`.

Acceptance:
- [ ] `build --version` prints a non-empty version string and exits 0
- [ ] `build --version` exits before any Plane API calls or file I/O
- [ ] Every emitted log event contains a non-empty `build_version` field
- [ ] `analyze-event-log` on a chain log surfaces the build version in the summary
- [ ] New base-event field does not break deserialization of existing event log fixtures
- [ ] AOT publish succeeds

Notes: The `--version` flag must be intercepted before the normal verb-dispatch path
because the normal path attempts Plane authentication and config validation, which fail in
environments where TLB is invoked solely to check its version (CI health checks, container
startup probes). A pre-dispatch check at the top of `Main` is the right pattern. Stamping
every event at the base type level avoids patching each event kind individually - the base
type field is set once at event-construction time and is inherited by all event kinds.

OOS:
- Printing version in `--help` output (separate small improvement)
- Structured version parsing (major/minor/patch) for comparison logic
- Cross-referencing build version against Plane ticket metadata
- Backfilling `build_version` onto events emitted before this brief lands

## What done looks like

An operator running a chain and then inspecting `analyze-event-log` output sees the build
version in the chain summary, confirming exactly which binary produced the run. `build
--version` works in any environment including CI health checks - it exits 0 with the
version string and touches nothing else. Bug reports now have a concrete "which build"
answer derivable from any log. The MSBuild embedding means no manual version-bump step
exists; the published binary always reflects the version at which it was compiled.
