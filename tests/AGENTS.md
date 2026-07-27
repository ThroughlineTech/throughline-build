# tests/ - xUnit test suites

One test project mirrors each `src/` project (20 in the solution), all net10.0,
with about 2,450 `[Fact]`/`[Theory]` declarations across about 230 C# files. Run
all: `dotnet test` (tests/Directory.Build.props defaults to test.runsettings:
quiet on green, full output on failure).

AOT discipline: test projects do NOT inherit `PublishAot=true`, so
AOT-sensitive paths flip reflection off explicitly first:
`AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)`.
Keep this when adding parser/serialization tests, or they pass under
reflection and miss the AOT regression they exist to catch.

Doubles are per-project copies, not a shared library: most projects carry
their own `FakeTicketing`, `FakeEventSink`, `FakeGitClient`,
`FakeWorkerAgent`, etc. Reuse the project-local ones. Console/diagnostic
muting is assembly-wide via `[ModuleInitializer]` (`TestConsoleSilencer`,
`TestDiagnosticsSilencer`); tests that assert on output capture it locally
with `Console.SetOut`.

Snapshot infrastructure lives in `ThroughlineBuild.Briefs.Tests` (`Snapshots/`
+ `Templates/`, LF-pinned via `.gitattributes`) - template changes need a
snapshot update. Fable stream-json NDJSON fixtures live in
`Workers.ClaudeCode.Tests/Fixtures/`.
