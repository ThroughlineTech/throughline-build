# tests/ - xUnit test suites

One net10.0 test project mirrors each `src/` project. Run all with `dotnet test`;
`tests/Directory.Build.props` uses `test.runsettings` for quiet green output and
full failure output.

Test projects do not inherit `PublishAot=true`. AOT-sensitive parser and
serialization tests must flip reflection off first:
`AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)`.
Keep that pattern or AOT regressions pass under reflection.

Doubles are per-project, not shared: reuse each suite's local `FakeTicketing`,
`FakeEventSink`, `FakeGitClient`, `FakeWorkerAgent`, etc. Some suites still mute
console/diagnostic noise with `[ModuleInitializer]`; Phases tests use injected
`TextWriter.Null` or `StringWriter` and must not restore process-global console
redirection.

Brief snapshots live in `ThroughlineBuild.Briefs.Tests` (`Snapshots/` +
`Templates/`, LF-pinned); template changes need snapshot updates. Fable
stream-json NDJSON fixtures live in `Workers.ClaudeCode.Tests/Fixtures/`.
