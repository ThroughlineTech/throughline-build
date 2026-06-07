using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Copilot;

public class CopilotAgent : IWorkerAgent
{
    private readonly CopilotOptions _options;

    public CopilotAgent(CopilotOptions options) => _options = options;
    public CopilotAgent() : this(new CopilotOptions()) { }

    public string Name => "copilot";
    public IWorkerProgressDigester? Digester => null;

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Build args: copilot -p "<brief>" -s --no-ask-user [ExtraArgs] [--model <model>] [--allow-tool <tool> ...]
        // Brief is delivered via -p arg (stdin is ignored when -p is present).
        var args = new List<string> { "-p", brief.Instruction, "-s", "--no-ask-user" };
        foreach (var extra in _options.ExtraArgs)
            args.Add(extra);
        // Resolve size -> model, normalize, add --model flag if resolved
        _options.Sizes.TryGetValue(options.Size, out var tier);
        var modelArg = NormalizeModel(tier?.Model);
        if (modelArg is not null)
            args.AddRange(new[] { "--model", modelArg });
        // Map AllowedTools to --allow-tool flags (copilot uses per-tool flags, not a comma list)
        if (options.AllowedTools is { Count: > 0 })
            foreach (var tool in options.AllowedTools)
                args.AddRange(new[] { "--allow-tool", tool });

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
                if (options.LiveStdoutSink is not null)
                {
                    WriteWorkerLine(options.LiveStdoutSink, "worker> ", e.Data);
                }
                // No ProgressDigestSink path: Digester is null for Copilot
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

        if (options.ProgressDigestSink is not null)
        {
            var startPayload = string.IsNullOrEmpty(modelArg) ? Name : $"{Name} model {modelArg}";
            options.ProgressDigestSink.WriteLine($"[0:00] {"agent".PadRight(10)} {startPayload}");
        }
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = $"Worker executable not found: '{_options.ExecutablePath}'. " +
                         $"Verify it is on PATH or set workers.copilot.executable in config.toml. Win32: {ex.Message}";
            WorkerDiagnostics.Write($"[CopilotAgent] {reason}");
            return new WorkerResult(Status.Failed, $"Worker executable not found: '{_options.ExecutablePath}'",
                Array.Empty<string>(), reason, new Dictionary<string, object>());
        }
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

        // Merge llm_usage metadata regardless of success/failure
        var mergedMeta = new Dictionary<string, object>(result.Metadata);
        mergedMeta["llm_usage"] = BuildLlmUsageMetadata(stopwatch.ElapsedMilliseconds, modelArg);
        result = result with { Metadata = mergedMeta };

        if (options.DebugCaptureDirectory is not null)
        {
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, result);
        }

        return result;
    }

    // Scans stdout directly for WORKER_RESULT via the shared parser.
    // Copilot outputs plain text (no JSON envelope), so stdout is passed directly.
    internal static WorkerResult ParseStdoutForWorkerResult(string stdout, int exitCode, string stderr)
    {
        var outcome = WorkerResultParser.TryParse(stdout);
        if (outcome.Result != null)
        {
            return outcome.Result with { Blocks = outcome.Blocks };
        }

        if (outcome.DeserializeErrorType != null)
        {
            var reason = $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}";
            WorkerDiagnostics.Write($"[CopilotAgent] {reason}");
            // A valid JSON object missing only the required 'status' field is a committed session
            // worth salvaging; tag it so ImplementPhase can recover it. See TLB-476.
            var metadata = outcome.MissingStatusField
                ? new Dictionary<string, object> { [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissingStatus }
                : new Dictionary<string, object>();
            return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                reason, metadata);
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        var markerReason = $"No WORKER_RESULT block found in stdout. Stderr: {stderr}";
        WorkerDiagnostics.Write($"[CopilotAgent] {markerReason}");
        // Clean exit but no WORKER_RESULT marker: tag it so ImplementPhase can salvage a
        // committed session that merely omitted the envelope. See TLB-471.
        return new WorkerResult(Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
            markerReason,
            new Dictionary<string, object> { [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissing });
    }

    // Strips the optional "github:" vendor prefix from a configured model id so the
    // bare id can be passed to `copilot --model`. Returns null when the configured
    // value is null/empty so callers can skip the flag and let copilot use its default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string githubPrefix = "github:";
        if (trimmed.StartsWith(githubPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(githubPrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Applies user-supplied EnvironmentVariables to the child process environment.
    // NOTE: Unlike other worker agents (ClaudeCode, Codex, Gemini), Copilot auth
    // is additive (set GH_TOKEN), not subtractive (strip key). The caller passes
    // GH_TOKEN via options.EnvironmentVariables when explicit auth is needed;
    // otherwise the gh keyring credential is inherited from the parent process.
    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Builds llm_usage metadata for Copilot runs. vendor is always "github".
    // cost_usd is always null (Copilot bills in premium-request quota, not USD).
    // Token counts are not available in silent mode (-s); both are 0.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(long wallClockMs, string? model)
    {
        return new Dictionary<string, object>
        {
            { "vendor",        "github" },
            { "model",         (object)(model ?? "") },
            { "wall_clock_ms", wallClockMs },
            { "input_tokens",  (object)0 },
            { "output_tokens", (object)0 },
            { "cost_usd",      (object?)null! },
        };
    }

    internal static void WriteDebugCapture(
        string directory,
        string briefInstruction,
        string stdout,
        string stderr,
        WorkerResult result)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "worker-stdin.txt"), "(brief delivered via -p arg)", System.Text.Encoding.UTF8);
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

        var dto = new CopilotResultDebugDto(result.Status.ToString(), result.Summary,
            result.FilesChanged, result.FailureReason);
        var resultJson = System.Text.Json.JsonSerializer.Serialize(dto,
            CopilotDebugCaptureJsonContext.Default.CopilotResultDebugDto);
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
            File.WriteAllText(Path.Combine(captureDir, "worker-stdin.txt"), "(brief delivered via -p arg)", System.Text.Encoding.UTF8);
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
