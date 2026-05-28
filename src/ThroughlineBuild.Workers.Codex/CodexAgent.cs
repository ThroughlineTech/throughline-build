using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Codex;

public class CodexAgent : IWorkerAgent
{
    private readonly CodexOptions _options;

    public CodexAgent(CodexOptions options) => _options = options;
    public CodexAgent() : this(new CodexOptions()) { }

    public string Name => "codex";
    public IWorkerProgressDigester? Digester => null;

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Build args: codex exec --full-auto "<brief>"
        // Brief is delivered as the positional prompt argument (not stdin).
        var args = new List<string> { "exec", "--full-auto" };
        foreach (var extra in _options.ExtraArgs)
            args.Add(extra);
        args.Add(brief.Instruction);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var psi = new ProcessStartInfo(_options.ExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        ConfigureEnvironment(psi, options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        var stopwatch = Stopwatch.StartNew();

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
                WriteWorkerLine(options.LiveStdoutSink, "worker> ", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
                WriteWorkerLine(options.LiveStderrSink, "worker! ", e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Brief is in args; close stdin immediately (nothing to pipe)
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cts.Token);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stopwatch.Stop();

            try
            {
                WriteCancellationCapture(options.DebugCaptureDirectory, brief.Instruction,
                    stdoutBuilder.ToString(), stderrBuilder.ToString());
            }
            catch { }

            return new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var result = ParseStdoutForWorkerResult(stdout, process.ExitCode, stderr);

        if (options.DebugCaptureDirectory is not null)
        {
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, result);
        }

        return result;
    }

    // Scans stdout directly for WORKER_RESULT via the shared parser.
    // Codex outputs plain text (no JSON envelope), so stdout is passed directly.
    internal static WorkerResult ParseStdoutForWorkerResult(string stdout, int exitCode, string stderr)
    {
        var outcome = WorkerResultParser.TryParse(stdout);
        if (outcome.Result != null)
        {
            return outcome.Result with { Metadata = new Dictionary<string, object>() };
        }

        if (outcome.DeserializeErrorType != null)
        {
            var reason = $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}";
            Console.Error.WriteLine($"[CodexAgent] {reason}");
            return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                reason, new Dictionary<string, object>());
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        var markerReason = $"No WORKER_RESULT block found in stdout. Stderr: {stderr}";
        Console.Error.WriteLine($"[CodexAgent] {markerReason}");
        return new WorkerResult(Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
            markerReason, new Dictionary<string, object>());
    }

    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    internal static void WriteDebugCapture(
        string directory,
        string briefInstruction,
        string stdout,
        string stderr,
        WorkerResult result)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "worker-stdin.txt"), "(brief delivered via args)", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stdout.txt"), stdout, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stderr.txt"), stderr, System.Text.Encoding.UTF8);

        if (result.Status == Status.Ok || result.Status == Status.NeedsRework)
        {
            File.WriteAllText(Path.Combine(directory, "worker-result-summary.txt"),
                result.Summary, System.Text.Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "parse-error.txt"),
                result.FailureReason ?? result.Summary, System.Text.Encoding.UTF8);
        }

        var dto = new CodexResultDebugDto(result.Status.ToString(), result.Summary,
            result.FilesChanged, result.FailureReason);
        var resultJson = System.Text.Json.JsonSerializer.Serialize(dto,
            CodexDebugCaptureJsonContext.Default.CodexResultDebugDto);
        File.WriteAllText(Path.Combine(directory, "worker-result.json"), resultJson, System.Text.Encoding.UTF8);
    }

    internal static void WriteCancellationCapture(
        string? captureDir,
        string briefInstruction,
        string partialStdout,
        string partialStderr)
    {
        if (captureDir is null) return;
        try
        {
            Directory.CreateDirectory(captureDir);
            File.WriteAllText(Path.Combine(captureDir, "worker-stdin.txt"), "(brief delivered via args)", System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "worker-stdout.txt"), partialStdout, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "worker-stderr.txt"), partialStderr, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "cancel-reason.txt"), "Process cancelled or timed out", System.Text.Encoding.UTF8);
        }
        catch { }
    }

    internal static void WriteWorkerLine(System.IO.TextWriter? sink, string prefix, string line)
    {
        if (sink is null) return;
        sink.WriteLine(prefix + line);
    }
}
