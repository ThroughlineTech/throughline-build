using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Verification;

public class AutomatedChecksRunner
{
    public enum RequiredPathHandling
    {
        Ignore,
        Inconclusive
    }

    private readonly bool _stopOnFirstFailure;

    public AutomatedChecksRunner(bool stopOnFirstFailure = false)
    {
        _stopOnFirstFailure = stopOnFirstFailure;
    }

    public virtual async Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct)
    {
        return await RunAsync(
            specs,
            workingDirectory,
            ct,
            RequiredPathHandling.Ignore).ConfigureAwait(false);
    }

    public virtual async Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct,
        RequiredPathHandling requiredPathHandling)
    {
        // Setup-role specs are repeatable prerequisites (for example, codegen) the real checks depend
        // on; run them FIRST on every invocation so the gating/advisory checks they enable see a
        // prepared worktree. Dependency installation belongs to lease creation, never setup. Stable, and a
        // no-op when no setup spec is present, so single-role check lists keep their original order.
        specs = OrderSetupFirst(specs);

        var results = new List<CheckResult>(specs.Count);
        bool shouldSkipRemaining = false;

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];

            // If we should skip (due to cancellation or stopOnFirstFailure), add not-run result
            if (shouldSkipRemaining)
            {
                results.Add(new CheckResult(spec.Name, false, -1, "", "", TimeSpan.Zero, spec.Role, CommandLine: FormatCommandLine(spec)));
                continue;
            }

            // Check caller cancellation before starting
            if (ct.IsCancellationRequested)
            {
                results.Add(new CheckResult(spec.Name, false, -1, "", "", TimeSpan.Zero, spec.Role, CommandLine: FormatCommandLine(spec)));
                shouldSkipRemaining = true;
                continue;
            }

            var result = await RunSpecAsync(spec, workingDirectory, ct, requiredPathHandling);
            results.Add(result);

            // Determine whether to stop
            if (!result.Passed)
            {
                if (ct.IsCancellationRequested || _stopOnFirstFailure)
                {
                    shouldSkipRemaining = true;
                }
            }
        }

        return results;
    }

    // Run a single named check from a configured list against the given worktree.
    // Returns Skipped=true when the check name is absent from specs, so callers
    // can distinguish "not configured" from "ran and failed" without treating it
    // as a gate-failing outcome.
    public virtual async Task<CheckResult> RunNamedAsync(
        string checkName,
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct)
    {
        return await RunNamedAsync(
            checkName,
            specs,
            workingDirectory,
            ct,
            RequiredPathHandling.Ignore).ConfigureAwait(false);
    }

    public virtual async Task<CheckResult> RunNamedAsync(
        string checkName,
        IReadOnlyList<CheckSpec> specs,
        string workingDirectory,
        CancellationToken ct,
        RequiredPathHandling requiredPathHandling)
    {
        var spec = specs.FirstOrDefault(s => s.Name == checkName);
        if (spec is null)
            return new CheckResult(checkName, false, 0, "", "", TimeSpan.Zero, Skipped: true);
        return await RunSpecAsync(spec, workingDirectory, ct, requiredPathHandling);
    }

    // Reorder so every CheckRole.Setup spec runs before the rest, preserving relative order within each
    // group (stable). Returns the input unchanged when there is nothing to reorder (no setup specs, or
    // only setup specs), so existing single-role check lists run byte-for-byte as before.
    public static IReadOnlyList<CheckSpec> OrderSetupFirst(IReadOnlyList<CheckSpec> specs)
    {
        if (specs.Count < 2)
            return specs;

        bool anySetup = false, anyOther = false;
        foreach (var s in specs)
        {
            if (s.Role == CheckRole.Setup) anySetup = true;
            else anyOther = true;
        }
        if (!anySetup || !anyOther)
            return specs;

        var ordered = new List<CheckSpec>(specs.Count);
        foreach (var s in specs) if (s.Role == CheckRole.Setup) ordered.Add(s);
        foreach (var s in specs) if (s.Role != CheckRole.Setup) ordered.Add(s);
        return ordered;
    }

    // The launchable form of a spec, recorded on every result so rework briefs can instruct
    // the worker to re-run the exact failing command and confirm exit 0 before committing.
    public static string FormatCommandLine(CheckSpec spec) =>
        spec.Arguments.Count == 0 ? spec.Executable : spec.Executable + " " + string.Join(" ", spec.Arguments);

    private static Task<CheckResult> RunSpecAsync(
        CheckSpec spec,
        string workingDirectory,
        CancellationToken ct,
        RequiredPathHandling requiredPathHandling)
    {
        if (requiredPathHandling == RequiredPathHandling.Inconclusive)
        {
            var missingPaths = MissingRequiredPaths(spec, workingDirectory);
            if (missingPaths.Count > 0)
            {
                return Task.FromResult(new CheckResult(
                    spec.Name,
                    Passed: false,
                    ExitCode: -1,
                    StdoutTail: "",
                    StderrTail: $"[runner] required paths absent: {string.Join(", ", missingPaths)}",
                    Elapsed: TimeSpan.Zero,
                    Role: spec.Role,
                    CommandLine: FormatCommandLine(spec),
                    Inconclusive: true,
                    MissingRequiredPaths: missingPaths));
            }
        }

        return RunSingleAsync(spec, workingDirectory, ct);
    }

    private static IReadOnlyList<string> MissingRequiredPaths(
        CheckSpec spec,
        string workingDirectory)
    {
        if (spec.RequiredPaths is not { Count: > 0 })
            return Array.Empty<string>();

        var missing = new List<string>();
        foreach (var path in spec.RequiredPaths)
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(workingDirectory, path));
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    missing.Add(path);
            }
            catch
            {
                missing.Add(path);
            }
        }

        return missing.AsReadOnly();
    }

    private static async Task<CheckResult> RunSingleAsync(
        CheckSpec spec,
        string workingDirectory,
        CancellationToken callerCt)
    {
        var commandLine = FormatCommandLine(spec);
        var psi = new ProcessStartInfo
        {
            // Resolve bare names to a launchable path on Windows (npm -> npm.cmd). No-op elsewhere.
            FileName = ExecutableResolver.Resolve(spec.Executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // A check must never inherit the caller's stdin. Left un-redirected, the child gets
            // whatever handle the process that ran 'build' happened to hold: a terminal gives a
            // readable console, an agent/CI harness can pass a handle that is closed or not
            // inheritable, and reads against that fail with ERROR_INVALID_HANDLE. That made the
            // gate verdict a function of who invoked it. Redirect stdin and close it immediately
            // (below) so every check sees the same thing everywhere: immediate EOF. A check that
            // prompts then fails fast instead of blocking until its timeout.
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Link caller CT with a timeout CT
        using var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(spec.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt, timeoutCts.Token);
        var linkedCt = linkedCts.Token;

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var sw = Stopwatch.StartNew();

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult(spec.Name, false, -1, "", ex.Message, sw.Elapsed, spec.Role, CommandLine: commandLine);
        }

        if (proc == null)
        {
            sw.Stop();
            return new CheckResult(spec.Name, false, -1, "", "[runner] failed to start process", sw.Elapsed, spec.Role, CommandLine: commandLine);
        }

        // Close the child's stdin right away: the check gets EOF on its first read rather than
        // blocking on a pipe nobody writes to. Best-effort - a process that exited already makes
        // this throw, which is not a check failure.
        try
        {
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // child already gone; nothing to close
        }
        catch (ObjectDisposedException)
        {
            // child already gone; nothing to close
        }

        // Read stdout and stderr concurrently to avoid deadlock
        var stdoutTask = ReadStreamAsync(proc.StandardOutput, stdoutBuilder, linkedCt);
        var stderrTask = ReadStreamAsync(proc.StandardError, stderrBuilder, linkedCt);

        bool timedOut = false;
        bool callerCancelled = false;

        try
        {
            await proc.WaitForExitAsync(linkedCt);
        }
        catch (OperationCanceledException)
        {
            // Distinguish between timeout and caller cancellation
            if (timeoutCts.IsCancellationRequested)
                timedOut = true;
            else
                callerCancelled = true;

            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort kill
            }
        }

        // Wait for streams to finish (they may have been cancelled too)
        try { await stdoutTask; } catch { /* ignore */ }
        try { await stderrTask; } catch { /* ignore */ }

        sw.Stop();

        int exitCode = -1;
        try
        {
            exitCode = proc.ExitCode;
        }
        catch
        {
            // process may not have exited cleanly
        }

        var stdoutText = Tail(stdoutBuilder.ToString());
        var stderrText = Tail(stderrBuilder.ToString());

        if (timedOut)
        {
            var seconds = (int)spec.Timeout.TotalSeconds;
            stderrText = stderrText.Length > 0
                ? stderrText + $"\n[runner] timeout after {seconds}s"
                : $"[runner] timeout after {seconds}s";

            return new CheckResult(spec.Name, false, exitCode, stdoutText, stderrText, sw.Elapsed, spec.Role, CommandLine: commandLine);
        }

        if (callerCancelled)
        {
            return new CheckResult(spec.Name, false, exitCode, stdoutText, stderrText, sw.Elapsed, spec.Role, CommandLine: commandLine);
        }

        bool passed = exitCode == 0;
        return new CheckResult(spec.Name, passed, exitCode, stdoutText, stderrText, sw.Elapsed, spec.Role, CommandLine: commandLine);
    }

    private static async Task ReadStreamAsync(
        System.IO.StreamReader reader,
        StringBuilder builder,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }

            if (read == 0)
                break;

            builder.Append(buffer, 0, read);
        }
    }

    private static string Tail(string s)
    {
        return s.Length > 4096 ? s.Substring(s.Length - 4096) : s;
    }
}
