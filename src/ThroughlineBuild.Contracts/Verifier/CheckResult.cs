namespace ThroughlineBuild.Contracts;

public record CheckSpec(
    string Name,           // e.g. "build", "test", "lint"
    string Executable,     // e.g. "dotnet"
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

public record CheckResult(
    string Name,
    bool Passed,
    int ExitCode,
    string StdoutTail,     // last ~4 KB of stdout
    string StderrTail,     // last ~4 KB of stderr
    TimeSpan Elapsed);
