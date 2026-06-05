using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Codex;

public class CodexAgent : IWorkerAgent
{
    private readonly CodexOptions _options;
    private readonly CodexProgressDigester _digester = new();

    public CodexAgent(CodexOptions options) => _options = options;
    public CodexAgent() : this(new CodexOptions()) { }

    public string Name => "codex";
    public IWorkerProgressDigester? Digester => _digester;

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Build args: codex exec --json [--dangerously-bypass-approvals-and-sandbox] -
        // The brief is delivered over stdin to avoid the Windows command-line length limit.
        var args = BuildArgs(_options, options);
        // modelArg is needed below for llm_usage metadata regardless of whether
        // it was emitted on the CLI; resolve it here too.
        _options.Sizes.TryGetValue(options.Size, out var resolvedModelRaw);
        var modelArg = NormalizeModel(resolvedModelRaw);

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
        psi.StandardInputEncoding = Encoding.UTF8;
        ProcessStreamEncoding.ApplyUtf8(psi);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        ConfigureEnvironment(psi, options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        var stopwatch = Stopwatch.StartNew();

        var process = new Process { StartInfo = psi };
        var progressLock = new object();
        var lastProgressEmit = DateTimeOffset.UtcNow;
        var lastActivity = "starting";
        using var heartbeat = CreateProgressHeartbeat(options, _digester, progressLock,
            () => lastProgressEmit,
            t => lastProgressEmit = t,
            () => lastActivity);
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
                    var activity = _digester.FormatActivity(e.Data);
                    lock (progressLock)
                    {
                        if (activity is not null)
                            lastActivity = activity;
                        if (dl != null)
                        {
                            options.ProgressDigestSink.WriteLine(dl);
                            lastProgressEmit = DateTimeOffset.UtcNow;
                        }
                    }
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
                         $"Verify it is on PATH or set workers.codex.executable in config.toml. Win32: {ex.Message}";
            Console.Error.WriteLine($"[CodexAgent] {reason}");
            return new WorkerResult(Status.Failed, $"Worker executable not found: '{_options.ExecutablePath}'",
                Array.Empty<string>(), reason, new Dictionary<string, object>());
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.StandardInput.WriteAsync(brief.Instruction.AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);
            process.StandardInput.Close();
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
        var usage = TryExtractUsageFromJsonl(stdout);

        // Merge llm_usage metadata regardless of success/failure
        var mergedMeta = new Dictionary<string, object>(result.Metadata);
        mergedMeta["llm_usage"] = BuildLlmUsageMetadata(
            usage.InputTokens,
            usage.OutputTokens,
            stopwatch.ElapsedMilliseconds,
            modelArg,
            usage.CachedInputTokens,
            usage.ReasoningOutputTokens);
        result = result with { Metadata = mergedMeta };

        if (options.DebugCaptureDirectory is not null)
        {
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, result);
        }

        return result;
    }

    // Scans stdout for WORKER_RESULT. In --json mode, Codex emits JSONL and the
    // contract block appears inside item.completed agent_message text; keep the
    // raw-stdout fallback for older/plain-text captures and error output.
    internal static WorkerResult ParseStdoutForWorkerResult(string stdout, int exitCode, string stderr)
    {
        var text = ExtractAgentMessagesFromJsonl(stdout);
        var parseTarget = string.IsNullOrWhiteSpace(text) ? stdout : text;
        var outcome = WorkerResultParser.TryParse(parseTarget);
        if (outcome.Result != null)
        {
            return outcome.Result with { Blocks = outcome.Blocks };
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

    internal static string ExtractAgentMessagesFromJsonl(string stdout)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String ||
                    typeEl.GetString() != "item.completed" ||
                    !root.TryGetProperty("item", out var item) ||
                    item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out var itemTypeEl) ||
                    itemTypeEl.ValueKind != JsonValueKind.String ||
                    itemTypeEl.GetString() != "agent_message")
                {
                    continue;
                }

                if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(textEl.GetString());
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }
        return sb.ToString();
    }

    internal static (int? InputTokens, int? OutputTokens, int? CachedInputTokens, int? ReasoningOutputTokens)
        TryExtractUsageFromJsonl(string stdout)
    {
        int? inputTokens = null;
        int? outputTokens = null;
        int? cachedInputTokens = null;
        int? reasoningOutputTokens = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String ||
                    typeEl.GetString() != "turn.completed" ||
                    !root.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                    inputTokens = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                    outputTokens = ot.GetInt32();
                if (usage.TryGetProperty("cached_input_tokens", out var cit) && cit.ValueKind == JsonValueKind.Number)
                    cachedInputTokens = cit.GetInt32();
                if (usage.TryGetProperty("reasoning_output_tokens", out var rot) && rot.ValueKind == JsonValueKind.Number)
                    reasoningOutputTokens = rot.GetInt32();
            }
            catch (JsonException)
            {
                continue;
            }
        }
        return (inputTokens, outputTokens, cachedInputTokens, reasoningOutputTokens);
    }

    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        // Strip API-key env vars to force subscription auth (same pattern as ClaudeCodeAgent).
        psi.Environment.Remove("CODEX_API_KEY");
        psi.Environment.Remove("OPENAI_API_KEY");
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Builds the argv passed to the codex CLI.
    //
    // --dangerously-bypass-approvals-and-sandbox puts codex into unattended,
    // unsandboxed execution. Emitted only when options.BypassPermissions is
    // true. ExtraArgs is appended before the resolved model so explicit user
    // flags can override default ordering. The final "-" tells Codex to read
    // the brief from stdin instead of a positional command-line argument.
    internal static List<string> BuildArgs(CodexOptions options, WorkerOptions workerOptions)
    {
        var args = new List<string> { "exec", "--json" };
        if (options.BypassPermissions)
            args.Add("--dangerously-bypass-approvals-and-sandbox");
        foreach (var extra in options.ExtraArgs)
            args.Add(extra);
        options.Sizes.TryGetValue(workerOptions.Size, out var resolvedModelRaw);
        var modelArg = NormalizeModel(resolvedModelRaw);
        if (modelArg is not null)
            args.AddRange(new[] { "--model", modelArg });
        args.Add("-");
        return args;
    }

    private static Timer? CreateProgressHeartbeat(
        WorkerOptions options,
        CodexProgressDigester digester,
        object progressLock,
        Func<DateTimeOffset> getLastProgressEmit,
        Action<DateTimeOffset> setLastProgressEmit,
        Func<string> getLastActivity)
    {
        if (options.ProgressDigestSink is null || options.LiveStdoutSink is not null)
            return null;

        var sink = options.ProgressDigestSink;
        var interval = TimeSpan.FromSeconds(15);
        return new Timer(_ =>
        {
            lock (progressLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - getLastProgressEmit() < interval)
                    return;
                sink.WriteLine($"[{digester.FormatElapsed(now)}] {PadKind("progress")} still running; last: {getLastActivity()}");
                setLastProgressEmit(now);
            }
        }, null, interval, interval);
    }

    private static string PadKind(string kind) => kind.PadRight(10);

    // Strips the "openai:" vendor prefix from a configured model id so the
    // bare id can be passed to `codex --model`. Returns null when the configured
    // value is null/empty so callers can skip the flag and let codex use its default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string openaiPrefix = "openai:";
        if (trimmed.StartsWith(openaiPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(openaiPrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Builds llm_usage metadata for Codex runs. vendor is always "openai".
    // cost_usd is always null (Codex emits tokens, not USD).
    // Token counts are populated from usage when available; null otherwise.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(
        int? inputTokens,
        int? outputTokens,
        long wallClockMs,
        string? model,
        int? cachedInputTokens = null,
        int? reasoningOutputTokens = null)
    {
        var metadata = new Dictionary<string, object>
        {
            { "model",         (object)(model ?? "") },
            { "vendor",        "openai" },
            { "wall_clock_ms", wallClockMs },
            { "input_tokens",  (object)(inputTokens ?? 0) },
            { "output_tokens", (object)(outputTokens ?? 0) },
            { "cache_read_tokens", (object)(cachedInputTokens ?? 0) },
            { "cache_create_tokens", 0 },
            { "cached_input_tokens", (object)(cachedInputTokens ?? 0) },
            { "reasoning_output_tokens", (object)(reasoningOutputTokens ?? 0) },
            { "cost_usd",      (object?)null! },
        };
        return metadata;
    }

    internal static void WriteDebugCapture(
        string directory,
        string briefInstruction,
        string stdout,
        string stderr,
        WorkerResult result)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "worker-stdin.txt"), briefInstruction, System.Text.Encoding.UTF8);
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
            File.WriteAllText(Path.Combine(captureDir, "worker-stdin.txt"), briefInstruction, System.Text.Encoding.UTF8);
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
