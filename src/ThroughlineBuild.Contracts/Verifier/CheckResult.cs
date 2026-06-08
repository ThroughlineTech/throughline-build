namespace ThroughlineBuild.Contracts;

// Gating: a non-zero exit code hard-fails the gate (build, test, typecheck).
// Advisory: failures are recorded and surfaced to the verifier but never hard-fail (lint, format).
public enum CheckRole { Gating, Advisory }

public record CheckSpec(
    string Name,           // e.g. "build", "test", "lint"
    string Executable,     // e.g. "dotnet"
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    CheckRole Role = CheckRole.Gating);

public record CheckResult(
    string Name,
    bool Passed,
    int ExitCode,
    string StdoutTail,     // last ~4 KB of stdout
    string StderrTail,     // last ~4 KB of stderr
    TimeSpan Elapsed,
    CheckRole Role = CheckRole.Gating,
    bool Skipped = false); // true when the check is absent from config; never counts as a failure
