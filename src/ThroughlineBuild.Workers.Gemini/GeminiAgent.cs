using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiAgent : IWorkerAgent
{
    private readonly GeminiOptions _options;
    private readonly GeminiProgressDigester _digester = new();

    public GeminiAgent(GeminiOptions options) => _options = options;
    public GeminiAgent() : this(new GeminiOptions()) { }

    public string Name => "gemini";
    public IWorkerProgressDigester? Digester => _digester;

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Build args: gemini -p "<brief>" --output-format json [--yolo] [--model <model>] [extra...]
        // Brief is delivered as the -p prompt argument (not stdin, not positional).
        var args = BuildArgs(brief.Instruction, _options, options);
        // modelArg is needed below for llm_usage metadata regardless of whether
        // it was emitted on the CLI; resolve it here too.
        _options.Sizes.TryGetValue(options.Size, out var tier);
        var modelArg = NormalizeModel(tier?.Model);

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
                WriteWorkerLine(options.LiveStderrSink, "worker! ", e.Data);
            }
        };

        _digester.ResetStart();
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = $"Worker executable not found: '{_options.ExecutablePath}'. " +
                         $"Verify it is on PATH or set workers.gemini.executable in config.toml. Win32: {ex.Message}";
            Console.Error.WriteLine($"[GeminiAgent] {reason}");
            return new WorkerResult(Status.Failed, $"Worker executable not found: '{_options.ExecutablePath}'",
                Array.Empty<string>(), reason, new Dictionary<string, object>());
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Brief is in args (via -p); close stdin immediately (nothing to pipe)
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
                WriteCancellationCapture(options.DebugCaptureDirectory,
                    stdoutBuilder.ToString(), stderrBuilder.ToString());
            }
            catch { }

            return new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        // Best-effort envelope deserialization for the debug capture (so we can
        // surface envelope.response as a separate file). ParseJsonOutput re-runs
        // the same deserialization independently; the duplication keeps it a
        // clean test seam.
        GeminiResultEnvelope? envelopeForDebug = null;
        try
        {
            envelopeForDebug = JsonSerializer.Deserialize(stdout, GeminiJsonContext.Default.GeminiResultEnvelope);
        }
        catch (JsonException) { /* leave null */ }
        catch (NotSupportedException) { /* leave null */ }

        var result = ParseJsonOutput(stdout, process.ExitCode, stderr);

        // Merge llm_usage metadata regardless of success/failure.
        // B03 (real token counts) is out of scope - pass null for usage so
        // input_tokens/output_tokens default to 0.
        var mergedMeta = new Dictionary<string, object>(result.Metadata);
        mergedMeta["llm_usage"] = BuildLlmUsageMetadata(usage: null, stopwatch.ElapsedMilliseconds, modelArg);
        result = result with { Metadata = mergedMeta };

        if (options.DebugCaptureDirectory is not null)
        {
            WriteDebugCapture(options.DebugCaptureDirectory, stdout, stderr, envelopeForDebug, result);
        }

        return result;
    }

    // Parses Gemini CLI JSON output for a WORKER_RESULT envelope.
    //
    // Gemini's --output-format json emits a top-level envelope:
    //   { "response": "<model text>", "stats": { "tokens": { "total": N }, ... } }
    //
    // The WORKER_RESULT block (the contract envelope) appears inside the .response
    // text. Strategy:
    //   1. Deserialize stdout as GeminiResultEnvelope. If .response is non-null,
    //      run the shared WorkerResultParser on it.
    //   2. If deserialization fails or .response is null, fall back to scanning
    //      raw stdout (covers plain-text error output paths).
    //   3. Otherwise surface deserialize errors / non-zero exit codes / missing
    //      marker as Failed.
    internal static WorkerResult ParseJsonOutput(string stdout, int exitCode, string stderr)
    {
        GeminiResultEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize(stdout, GeminiJsonContext.Default.GeminiResultEnvelope);
        }
        catch (JsonException) { /* fall through to raw-stdout parse */ }
        catch (NotSupportedException) { /* fall through to raw-stdout parse */ }

        if (envelope is not null && envelope.Response is not null)
        {
            var inner = WorkerResultParser.TryParse(envelope.Response);
            if (inner.Result != null)
            {
                return inner.Result with { Blocks = inner.Blocks };
            }
            if (inner.DeserializeErrorType != null)
            {
                var reason = $"Failed to deserialize WORKER_RESULT JSON: {inner.DeserializeErrorType}: {inner.DeserializeErrorMessage}";
                Console.Error.WriteLine($"[GeminiAgent] {reason}");
                return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                    reason, new Dictionary<string, object>());
            }
            // inner.Result is null and no deserialize error -> marker missing in
            // envelope.response. Continue to the failure decisions below using
            // the same flow as the raw-stdout fallback (so we still get the
            // "No WORKER_RESULT found" message or the exit-code message).
        }
        else
        {
            // Envelope missing or .response null -> attempt a raw-stdout scan.
            var outcome = WorkerResultParser.TryParse(stdout);
            if (outcome.Result != null)
            {
                return outcome.Result with { Blocks = outcome.Blocks };
            }
            if (outcome.DeserializeErrorType != null)
            {
                var reason = $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}";
                Console.Error.WriteLine($"[GeminiAgent] {reason}");
                return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                    reason, new Dictionary<string, object>());
            }
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        var markerReason = $"No WORKER_RESULT block found in stdout. Stderr: {stderr}";
        Console.Error.WriteLine($"[GeminiAgent] {markerReason}");
        return new WorkerResult(Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
            markerReason, new Dictionary<string, object>());
    }

    // Builds the argv passed to the gemini CLI.
    //
    // --yolo puts gemini into its unattended-execution mode (no permission
    // gate). Emitted only when options.BypassPermissions is true. The brief
    // is delivered as the -p prompt argument (not stdin, not positional).
    internal static List<string> BuildArgs(string briefInstruction, GeminiOptions options, WorkerOptions workerOptions)
    {
        var args = new List<string> { "-p", briefInstruction, "--output-format", "json" };
        if (options.BypassPermissions)
            args.Add("--yolo");
        options.Sizes.TryGetValue(workerOptions.Size, out var tier);
        var modelArg = NormalizeModel(tier?.Model);
        if (modelArg is not null)
            args.AddRange(new[] { "--model", modelArg });
        foreach (var extra in options.ExtraArgs)
            args.Add(extra);
        return args;
    }

    // Strips the optional "google:" vendor prefix from a configured model id so the
    // bare id (e.g. "gemini-2.5-pro") can be passed to `gemini --model`. Returns
    // null when the configured value is null/empty or prefix-only so callers can
    // skip emitting the flag and let the Gemini CLI use its own default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string googlePrefix = "google:";
        if (trimmed.StartsWith(googlePrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(googlePrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Removes GEMINI_API_KEY and GOOGLE_API_KEY from the child process environment
    // to force OAuth / subscription auth. The Gemini CLI will fall back to the
    // user's ADC (Application Default Credentials) or gcloud login when neither
    // API-key variable is present.
    //
    // Note on MaxOutputTokens: the Gemini CLI headless mode does not expose an
    // output-token-cap environment variable equivalent to CLAUDE_CODE_MAX_OUTPUT_TOKENS.
    // _options.MaxOutputTokens is reserved for future use once the CLI supports it.
    //
    // User-supplied EnvironmentVariables are applied after the strip so an explicit
    // user override still wins.
    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        psi.Environment.Remove("GEMINI_API_KEY");
        psi.Environment.Remove("GOOGLE_API_KEY");
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Builds the llm_usage metadata dictionary for Gemini runs.
    // vendor is always "google". cost_usd is always null (Gemini CLI does not
    // report USD cost). input_tokens is sourced from usage.Tokens.Total (the
    // Gemini CLI reports a combined token total, not a split); output_tokens is
    // 0 because the Gemini CLI reports a combined token total, not a split input/output count.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(
        GeminiUsage? usage, long wallClockMs, string? model)
    {
        var metadata = new Dictionary<string, object>
        {
            { "vendor",        "google" },
            { "model",         (object)(model ?? "") },
            { "wall_clock_ms", wallClockMs },
            { "input_tokens",  (object)(usage?.Tokens?.Total ?? 0) },
            { "output_tokens", (object)0 },
            { "cost_usd",      (object?)null! },
        };
        return metadata;
    }

    internal static void WriteDebugCapture(
        string directory,
        string stdout,
        string stderr,
        GeminiResultEnvelope? envelope,
        WorkerResult result)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "worker-stdin.txt"), "(brief delivered via -p arg)", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stdout.txt"), stdout, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stderr.txt"), stderr, System.Text.Encoding.UTF8);

        if (envelope?.Response is not null)
        {
            File.WriteAllText(Path.Combine(directory, "envelope-response.txt"),
                envelope.Response, System.Text.Encoding.UTF8);
        }

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

        var dto = new GeminiResultDebugDto(result.Status.ToString(), result.Summary,
            result.FilesChanged, result.FailureReason);
        var resultJson = JsonSerializer.Serialize(dto,
            GeminiDebugCaptureJsonContext.Default.GeminiResultDebugDto);
        File.WriteAllText(Path.Combine(directory, "worker-result.json"), resultJson, System.Text.Encoding.UTF8);
    }

    internal static void WriteCancellationCapture(
        string? captureDir,
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
