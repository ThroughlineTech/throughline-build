namespace ThroughlineBuild.Contracts;

// Gating: a non-zero exit code hard-fails the gate (build, test, typecheck).
// Advisory: failures are recorded and surfaced to the verifier but never hard-fail (lint, format).
// Setup: a prerequisite command (e.g. a codegen/install step) the runner executes BEFORE the gating
//   and advisory checks; it proves nothing, it prepares the worktree so the real checks can run. A
//   non-zero exit hard-fails the gate (the prerequisites the checks depend on could not be established).
public enum CheckRole { Gating, Advisory, Setup }

/// <summary>
/// A deliberately-broken file a gating check MUST reject. Path is relative to the target
/// project root; Content is the file body. Stack-agnostic: the deriver supplies the
/// stack-specific content (this engine never hard-codes what "broken" means for a stack).
/// </summary>
public record CanaryFile(string Path, string Content);

public record CheckSpec(
    string Name,           // e.g. "build", "test", "lint"
    string Executable,     // e.g. "dotnet"
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    CheckRole Role = CheckRole.Gating,
    // optional per-check canary files; null/empty means no canary declared (the gate cannot prove this check is non-vacuous)
    IReadOnlyList<CanaryFile>? Canary = null,
    // optional tracked files/directories that must exist before a control run on the base ref is meaningful
    IReadOnlyList<string>? RequiredPaths = null);

public record CheckResult(
    string Name,
    bool Passed,
    int ExitCode,
    string StdoutTail,     // last ~4 KB of stdout
    string StderrTail,     // last ~4 KB of stderr
    TimeSpan Elapsed,
    CheckRole Role = CheckRole.Gating,
    bool Skipped = false,  // true when the check is absent from config; never counts as a failure
    // The exact command line that was executed ("executable arg1 arg2 ..."). Carried so a rework
    // brief can tell the worker to re-run the failing check verbatim and confirm exit 0 before
    // committing - the check is the oracle, not anyone's interpretation of its rules. Empty when
    // the result was synthesized rather than executed.
    string CommandLine = "");
