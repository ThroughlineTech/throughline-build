# tests/ - xUnit test suites

One test project mirrors each `src/` library (19 projects, all net10.0,
~1200 `[Fact]`/`[Theory]` across ~230 files). Run all: `dotnet test`.

AOT discipline: test projects do NOT inherit `PublishAot=true` from the Cli
project, so AOT-sensitive paths flip reflection off explicitly before exercising
the parser:
`AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)`.
Keep this when adding parser/serialization tests, or they pass under reflection
and miss the AOT regression they exist to catch. Coverage is concentrated in
`Workers.ClaudeCode.Tests` and `Workers.Common.Tests`.

Shared doubles (stubs/fakes) cover ticketing, workers, sinks, console, git, and
LLM clients - reuse them rather than rolling new ones.

Snapshot infrastructure lives in `ThroughlineBuild.Briefs.Tests`
(`Templates/` + `Snapshots/`, both pinned to LF via `.gitattributes`). A brief
or template change will require a snapshot update.
