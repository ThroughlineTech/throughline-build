using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal interface IClaudeCodeTransport
{
    Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct);
}

internal sealed class ClaudeCodePrintTransport : IClaudeCodeTransport
{
    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodeProgressDigester _digester;

    internal ClaudeCodePrintTransport(ClaudeCodeOptions options, ClaudeCodeProgressDigester digester)
    {
        _options = options;
        _digester = digester;
    }

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        var buildDir = Path.Combine(workingDirectory, ".build");
        Directory.CreateDirectory(buildDir);
        var briefPath = Path.Combine(buildDir, "brief.md");
        await File.WriteAllTextAsync(briefPath, brief.Instruction, ct);

        var args = ClaudeCodeAgent.BuildArgs(_options, options);
        _options.Sizes.TryGetValue(options.Size, out var tier);

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
        ProcessStreamEncoding.ApplyUtf8(psi);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        ClaudeCodeAgent.ConfigureEnvironment(psi, _options, options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var timestampedStdout = options.DebugCaptureDirectory is not null
            ? new List<(DateTimeOffset At, string Line)>()
            : null;

        var process = new Process { StartInfo = psi };
        _digester.ResetStart();
        if (options.ProgressDigestSink is not null)
        {
            var startModel = ClaudeCodeAgent.NormalizeModel(tier?.Model);
            var startPayload = string.IsNullOrEmpty(startModel) ? "claude-code" : $"claude-code model {startModel}";
            options.ProgressDigestSink.WriteLine($"[0:00] {"agent".PadRight(10)} {startPayload}");
        }
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
                timestampedStdout?.Add((DateTimeOffset.UtcNow, e.Data));
                if (options.LiveStdoutSink is not null)
                {
                    ClaudeCodeAgent.WriteWorkerLine(options.LiveStdoutSink, "", e.Data);
                }
                else if (options.ProgressDigestSink is not null)
                {
                    var dl = _digester.FormatLine(e.Data);
                    if (dl != null) options.ProgressDigestSink.WriteLine(dl);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
                ClaudeCodeAgent.WriteWorkerLine(options.LiveStderrSink, "worker! ", e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = $"Worker executable not found: '{_options.ExecutablePath}'. " +
                         $"Verify it is on PATH or set workers.claude-code.executable in config.toml. Win32: {ex.Message}";
            WorkerDiagnostics.Write($"[ClaudeCodeAgent] {reason}");
            return new WorkerResult(Status.Failed, $"Worker executable not found: '{_options.ExecutablePath}'",
                Array.Empty<string>(), reason, new Dictionary<string, object>());
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.StandardInput.WriteAsync(brief.Instruction);
            process.StandardInput.Close();
        }
        catch (IOException ex)
        {
            stderrBuilder.AppendLine($"[worker stdin] subprocess closed stdin before the brief was sent: {ex.Message}");
        }

        try
        {
            await process.WaitForExitAsync(cts.Token);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stopwatch.Stop();

            var cancelResult = new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
            try
            {
                ClaudeCodeAgent.WriteCancellationCapture(options.DebugCaptureDirectory, brief.Instruction,
                    stdoutBuilder.ToString(), stderrBuilder.ToString());
                if (options.DebugCaptureDirectory is not null && timestampedStdout is not null)
                {
                    var cancelModel = ClaudeCodeAgent.TryExtractModelFromStream(stdoutBuilder.ToString())
                        ?? ClaudeCodeAgent.NormalizeModel(tier?.Model);
                    WorkerTranscriptWriter.Write(options.DebugCaptureDirectory, brief, timestampedStdout,
                        cancelResult, options.DebugTranscript, cancelModel, args, stopwatch.ElapsedMilliseconds, startedAt);
                }
            }
            catch
            {
                // Best-effort debug artifacts must not mask cancellation.
            }
            return cancelResult;
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();
        var fallbackModel = ClaudeCodeAgent.NormalizeModel(tier?.Model);
        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, process.ExitCode, stderr,
            stopwatch.ElapsedMilliseconds, fallbackModel);
        result = ClaudeCodeAgent.AttachContextTurns(result, stdout);

        if (options.DebugCaptureDirectory is not null)
        {
            ClaudeCodeJsonEnvelope? envelope = ClaudeCodeAgent.TryParseEnvelopeFromStdout(stdout, out _);
            ClaudeCodeAgent.WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, envelope, result);
            var transcriptModel = ClaudeCodeAgent.TryExtractModelFromStream(stdout) ?? fallbackModel;
            WorkerTranscriptWriter.Write(options.DebugCaptureDirectory, brief,
                timestampedStdout ?? new List<(DateTimeOffset, string)>(), result, options.DebugTranscript,
                transcriptModel, args, stopwatch.ElapsedMilliseconds, startedAt);
        }

        return result;
    }
}
