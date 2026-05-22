using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

public class ClaudeCodeAgent : IWorkerAgent
{
    private readonly ClaudeCodeOptions _options;

    public ClaudeCodeAgent(ClaudeCodeOptions options) => _options = options;
    public ClaudeCodeAgent() : this(new ClaudeCodeOptions()) { }

    public string Name => "claude-code";

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Write brief to .build/brief.md
        var buildDir = Path.Combine(workingDirectory, ".build");
        Directory.CreateDirectory(buildDir);
        var briefPath = Path.Combine(buildDir, "brief.md");
        await File.WriteAllTextAsync(briefPath, brief.Instruction, ct);

        // Build args
        var args = new List<string> { "--print", "--input-file", briefPath };
        if (options.AllowedTools is { Count: > 0 })
            args.AddRange(new[] { "--allowedTools", string.Join(",", options.AllowedTools) });
        foreach (var extra in _options.ExtraArgs)
            args.Add(extra);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var psi = new ProcessStartInfo(_options.ExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var result = WorkerResultParser.TryParse(stdout);
        if (result != null)
            return result;

        if (process.ExitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {process.ExitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        return new WorkerResult(Status.Escalate, "No WORKER_RESULT found in output", Array.Empty<string>(),
            $"Stdout did not contain a WORKER_RESULT block. Stderr: {stderr}", new Dictionary<string, object>());
    }
}
