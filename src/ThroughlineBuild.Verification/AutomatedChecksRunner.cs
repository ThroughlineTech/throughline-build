using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Verification;

public class AutomatedChecksRunner
{
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
        var results = new List<CheckResult>(specs.Count);
        bool shouldSkipRemaining = false;

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];

            // If we should skip (due to cancellation or stopOnFirstFailure), add not-run result
            if (shouldSkipRemaining)
            {
                results.Add(new CheckResult(spec.Name, false, -1, "", "", TimeSpan.Zero));
                continue;
            }

            // Check caller cancellation before starting
            if (ct.IsCancellationRequested)
            {
                results.Add(new CheckResult(spec.Name, false, -1, "", "", TimeSpan.Zero));
                shouldSkipRemaining = true;
                continue;
            }

            var result = await RunSingleAsync(spec, workingDirectory, ct);
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

    private static async Task<CheckResult> RunSingleAsync(
        CheckSpec spec,
        string workingDirectory,
        CancellationToken callerCt)
    {
        var psi = new ProcessStartInfo
        {
            // Resolve bare names to a launchable path on Windows (npm -> npm.cmd). No-op elsewhere.
            FileName = ExecutableResolver.Resolve(spec.Executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            return new CheckResult(spec.Name, false, -1, "", ex.Message, sw.Elapsed);
        }

        if (proc == null)
        {
            sw.Stop();
            return new CheckResult(spec.Name, false, -1, "", "[runner] failed to start process", sw.Elapsed);
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

            return new CheckResult(spec.Name, false, exitCode, stdoutText, stderrText, sw.Elapsed);
        }

        if (callerCancelled)
        {
            return new CheckResult(spec.Name, false, exitCode, stdoutText, stderrText, sw.Elapsed);
        }

        bool passed = exitCode == 0;
        return new CheckResult(spec.Name, passed, exitCode, stdoutText, stderrText, sw.Elapsed);
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
