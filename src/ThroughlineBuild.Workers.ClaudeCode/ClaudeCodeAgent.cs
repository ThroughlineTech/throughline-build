using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        // Write brief to .build/brief.md (persisted for diagnostics)
        var buildDir = Path.Combine(workingDirectory, ".build");
        Directory.CreateDirectory(buildDir);
        var briefPath = Path.Combine(buildDir, "brief.md");
        await File.WriteAllTextAsync(briefPath, brief.Instruction, ct);

        // Build args - brief is delivered via stdin.
        // --output-format must immediately follow --print (claude --help: "only works with --print").
        // --verbose is required by claude-code when combining --print with --output-format stream-json
        // (the CLI rejects the combination otherwise). The terminal NDJSON event is type=result and is
        // bit-for-bit identical to the legacy --output-format json single-blob envelope, so envelope
        // parsing downstream is unchanged.
        var args = new List<string> { "--print", "--verbose", "--output-format", "stream-json" };
        if (options.AllowedTools is { Count: > 0 })
            args.AddRange(new[] { "--allowedTools", string.Join(",", options.AllowedTools) });
        var modelArg = NormalizeModel(_options.Model);
        if (modelArg is not null)
            args.AddRange(new[] { "--model", modelArg });
        foreach (var extra in _options.ExtraArgs)
            args.Add(extra);

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
        var digestStart = DateTimeOffset.UtcNow;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
                if (options.LiveStdoutSink is not null)
                {
                    // --debug path: raw firehose. Digest is suppressed (mutually exclusive).
                    WriteWorkerLine(options.LiveStdoutSink, "worker> ", e.Data);
                }
                else if (options.ProgressDigestSink is not null)
                {
                    // Default path: per-event digest. Best-effort - a malformed line or
                    // unexpected schema must not crash the worker dispatch.
                    TryEmitDigestLine(e.Data, options.ProgressDigestSink, digestStart);
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

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Send brief via stdin then close to signal EOF
        await process.StandardInput.WriteAsync(brief.Instruction);
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

            // Write partial output to debug capture directory (best-effort)
            try
            {
                WriteCancellationCapture(options.DebugCaptureDirectory, brief.Instruction,
                    stdoutBuilder.ToString(), stderrBuilder.ToString());
            }
            catch
            {
                // Best-effort: failure to write debug artifacts never masks the cancellation.
            }

            return new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        // Strip vendor prefix from DefaultModel (e.g. "anthropic:claude-sonnet-4-6" -> "claude-sonnet-4-6")
        // so the fallback model matches the bare form reported by the stream system event.
        var rawDefaultModel = _options.DefaultModel;
        var fallbackModel = rawDefaultModel is not null && rawDefaultModel.Contains(':')
            ? rawDefaultModel.Substring(rawDefaultModel.IndexOf(':') + 1)
            : rawDefaultModel;

        var result = ParseStdoutEnvelope(stdout, process.ExitCode, stderr, stopwatch.ElapsedMilliseconds, fallbackModel);

        if (options.DebugCaptureDirectory is not null)
        {
            // Same envelope-parse fallback chain as ParseStdoutEnvelope: try single
            // object first (legacy), fall back to last type=result NDJSON line.
            ClaudeCodeJsonEnvelope? envelope = TryParseEnvelopeFromStdout(stdout, out _);
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, envelope, result);
        }

        return result;
    }

    // Scans the NDJSON stream for the first "system" event and returns its
    // "model" field. The system event is emitted as the first line of every
    // --output-format stream-json run and carries the actual model used.
    // Returns null if no system event is found or if the model field is absent.
    internal static string? TryExtractModelFromStream(string stdout)
    {
        var lines = stdout.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            // Fast path: only try to parse lines that look like system events.
            if (!trimmed.Contains("\"type\":\"system\""))
                continue;
            try
            {
                var ev = JsonSerializer.Deserialize(trimmed, ClaudeCodeJsonContext.Default.ClaudeCodeSystemEvent);
                if (ev?.Type == "system" && ev.Model is not null)
                    return ev.Model;
            }
            catch (JsonException)
            {
                continue;
            }
        }
        return null;
    }

    // Try to extract the terminal "result" envelope from stdout. Returns null when
    // no envelope can be located; on a hard JSON parse failure, sets parseError to
    // the deserializer message. Tries single-object parse first (legacy
    // --output-format json path; preserves all existing single-blob tests), then
    // falls back to NDJSON scanning for the last line whose type=result.
    internal static ClaudeCodeJsonEnvelope? TryParseEnvelopeFromStdout(string stdout, out string? parseError)
    {
        parseError = null;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return null;

        // Single-object fast path: matches legacy --output-format json output.
        try
        {
            var envelope = JsonSerializer.Deserialize(trimmed, ClaudeCodeJsonContext.Default.ClaudeCodeJsonEnvelope);
            if (envelope is not null && envelope.Type == "result")
                return envelope;
            // Parsed as JSON but not a result envelope - fall through to NDJSON scan.
        }
        catch (JsonException ex)
        {
            // Stream-json case: stdout is NDJSON, not a single object. Remember the
            // first error message in case the NDJSON scan also fails to find a
            // terminal result line.
            parseError = ex.Message;
        }

        // NDJSON fallback: scan from the end, keep the last line that parses as
        // a result envelope. This tolerates leading non-result events (system,
        // assistant, user, rate_limit_event, ...) without bespoke per-type parsing.
        var lines = stdout.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            ClaudeCodeJsonEnvelope? candidate;
            try
            {
                candidate = JsonSerializer.Deserialize(line, ClaudeCodeJsonContext.Default.ClaudeCodeJsonEnvelope);
            }
            catch (JsonException)
            {
                continue;
            }
            if (candidate is not null && candidate.Type == "result")
            {
                parseError = null;
                return candidate;
            }
        }

        return null;
    }

    // Best-effort: parse a single NDJSON line via WorkerProgressDigest and write
    // the formatted digest line to the sink. Any exception (malformed JSON,
    // unexpected schema, sink-write failure) is swallowed: digest emission must
    // never crash the worker dispatch.
    internal static void TryEmitDigestLine(string ndjsonLine, System.IO.TextWriter sink, DateTimeOffset startTime)
    {
        try
        {
            using var doc = JsonDocument.Parse(ndjsonLine);
            var formatted = WorkerProgressDigest.FormatLine(doc.RootElement, startTime);
            if (formatted is not null)
                sink.WriteLine(formatted);
        }
        catch
        {
            // Swallow: best-effort digest. The terminal result envelope is parsed
            // separately by ParseStdoutEnvelope and is NOT best-effort.
        }
    }

    // Parses the Claude Code JSON envelope from stdout, extracts the inner result text,
    // and routes it through WorkerResultParser. Extracted as an internal static method
    // so envelope-parsing logic can be unit-tested without spawning a real process
    // (mirrors the ConfigureEnvironment pattern; InternalsVisibleTo allows test access).
    //
    // Supports both legacy --output-format json (single JSON object) and the new
    // --output-format stream-json (NDJSON; one JSON object per line, terminal
    // line is type=result). Single-object parse is tried first to preserve
    // back-compat with the legacy envelope tests; on JsonException we fall back
    // to scanning the NDJSON stream for the last line whose type=result.
    internal static WorkerResult ParseStdoutEnvelope(string stdout, int exitCode, string stderr, long wallClockMs = 0, string? fallbackModel = null)
    {
        ClaudeCodeJsonEnvelope? envelope = TryParseEnvelopeFromStdout(stdout, out var parseError);
        if (envelope is null)
        {
            var head = stdout.Length > 200 ? stdout[..200] : stdout;
            if (parseError is not null)
            {
                var failureReason = $"Failed to parse Claude Code JSON envelope: {parseError}. Stdout head: {head}";
                Console.Error.WriteLine($"[WorkerResultParser] {failureReason}");
                return new WorkerResult(Status.Failed, "Failed to parse Claude Code JSON envelope", Array.Empty<string>(),
                    failureReason, new Dictionary<string, object>());
            }
            var nullFailureReason = $"Deserialized envelope was null. Stdout head: {head}";
            Console.Error.WriteLine($"[WorkerResultParser] {nullFailureReason}");
            return new WorkerResult(Status.Failed, "Claude Code JSON envelope was null after deserialization", Array.Empty<string>(),
                nullFailureReason, new Dictionary<string, object>());
        }

        if (envelope.IsError)
        {
            return new WorkerResult(Status.Escalate, "Claude Code reported is_error=true", Array.Empty<string>(),
                $"Claude Code envelope has is_error=true. Subtype: {envelope.Subtype}. Stderr: {stderr}", new Dictionary<string, object>());
        }

        if (envelope.Result is null)
        {
            var failureReason = $"Envelope result field is null. Subtype: {envelope.Subtype}. Stderr: {stderr}";
            Console.Error.WriteLine($"[WorkerResultParser] {failureReason}");
            return new WorkerResult(Status.Failed, "Claude Code JSON envelope missing result field", Array.Empty<string>(),
                failureReason, new Dictionary<string, object>());
        }

        // Extract model from the NDJSON system event; fall back to the configured default.
        var model = TryExtractModelFromStream(stdout) ?? fallbackModel;

        // Route the inner result text through the existing WORKER_RESULT marker parser.
        var outcome = WorkerResultParser.TryParse(envelope.Result);
        if (outcome.Result != null)
        {
            // Merge llm_usage metadata on success path
            var mergedMetadata = new Dictionary<string, object>(outcome.Result.Metadata);
            mergedMetadata["llm_usage"] = BuildLlmUsageMetadata(envelope, wallClockMs, model);
            return outcome.Result with { Metadata = mergedMetadata };
        }

        if (outcome.DeserializeErrorType != null)
        {
            var failureReason = $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}";
            Console.Error.WriteLine($"[WorkerResultParser] {failureReason}");
            return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                failureReason, new Dictionary<string, object>());
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        var markerFailureReason = $"Envelope result did not contain a WORKER_RESULT block. Stderr: {stderr}";
        Console.Error.WriteLine($"[WorkerResultParser] {markerFailureReason}");
        return new WorkerResult(Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
            markerFailureReason, new Dictionary<string, object>());
    }

    // Builds the llm_usage metadata dictionary from the Claude Code JSON envelope.
    // Returns a dictionary with snake_case keys including model, vendor, token counts, cache fields, and wall_clock_ms.
    // anthropic_request_id is not included: the Claude Code CLI does not expose it in the stream envelope.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(ClaudeCodeJsonEnvelope envelope, long wallClockMs, string? model = null)
    {
        var metadata = new Dictionary<string, object>
        {
            { "model", model! },
            { "vendor", "anthropic" },
            { "wall_clock_ms", wallClockMs }
        };

        if (envelope.Usage is not null)
        {
            metadata["input_tokens"] = envelope.Usage.InputTokens ?? 0;
            metadata["output_tokens"] = envelope.Usage.OutputTokens ?? 0;
            metadata["cache_read_tokens"] = (object?)envelope.Usage.CacheReadInputTokens ?? null!;
            metadata["cache_create_tokens"] = (object?)envelope.Usage.CacheCreationInputTokens ?? null!;
        }
        else
        {
            metadata["input_tokens"] = 0;
            metadata["output_tokens"] = 0;
            metadata["cache_read_tokens"] = null!;
            metadata["cache_create_tokens"] = null!;
            metadata["partial"] = true;
        }

        return metadata;
    }

    // Strips the "anthropic:" provider prefix from a configured model id so the
    // bare id (e.g. "claude-sonnet-4-6") can be passed to `claude --model`. Returns
    // null when the configured value is null/empty so callers can skip emitting
    // the flag and let the Claude Code CLI use its own default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string anthropicPrefix = "anthropic:";
        if (trimmed.StartsWith(anthropicPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(anthropicPrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        // Ensure Claude Code uses OAuth auth rather than API-key auth; worker LLM cost
        // flows to the user's subscription, not to per-token API billing.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        // Pin max output tokens if configured; do this before the user-supplied
        // EnvironmentVariables loop so an explicit user override still wins.
        if (_options.MaxOutputTokens is int n)
            psi.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Writes raw worker inputs/outputs to a debug capture directory for post-mortem diagnosis.
    // Files written:
    //   worker-stdin.txt   - the brief instruction sent to the subprocess
    //   worker-stdout.txt  - complete raw stdout from the subprocess
    //   worker-stderr.txt  - complete raw stderr from the subprocess
    //   envelope-result.txt - the inner result field from the JSON envelope (when envelope is non-null)
    //   worker-result.json  - JSON serialization of the final WorkerResult
    //   parse-error.txt     - failure reason when envelope is null or result field absent (parse-failure path)
    // Directory is created idempotently. File writes use UTF-8 with BOM to preserve any non-ASCII bytes.
    internal static void WriteDebugCapture(
        string directory,
        string briefInstruction,
        string stdout,
        string stderr,
        ClaudeCodeJsonEnvelope? envelope,
        WorkerResult result)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "worker-stdin.txt"), briefInstruction, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stdout.txt"), stdout, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "worker-stderr.txt"), stderr, System.Text.Encoding.UTF8);

        if (envelope?.Result is not null)
        {
            File.WriteAllText(Path.Combine(directory, "envelope-result.txt"), envelope.Result, System.Text.Encoding.UTF8);
        }
        else
        {
            // Parse-failure path: envelope absent or result field missing - preserve the failure reason
            var parseErrorText = result.FailureReason ?? result.Summary;
            File.WriteAllText(Path.Combine(directory, "parse-error.txt"), parseErrorText, System.Text.Encoding.UTF8);
        }

        // Serialize WorkerResult core fields to JSON for diagnostic purposes.
        // Excludes Metadata (IReadOnlyDictionary<string, object>) to keep AOT serialization safe.
        var dto = new WorkerResultDebugDto(result.Status.ToString(), result.Summary,
            result.FilesChanged, result.FailureReason);
        var resultJson = JsonSerializer.Serialize(dto, DebugCaptureJsonContext.Default.WorkerResultDebugDto);
        File.WriteAllText(Path.Combine(directory, "worker-result.json"), resultJson, System.Text.Encoding.UTF8);
    }

    // Writes a prefixed line to the sink when the sink is non-null, otherwise no-ops.
    // Used to tee worker stdout/stderr lines to an optional live stream without touching
    // the StringBuilder accumulators that feed the parse pipeline.
    internal static void WriteWorkerLine(System.IO.TextWriter? sink, string prefix, string line)
    {
        if (sink is null) return;
        sink.WriteLine(prefix + line);
    }

    // Writes partial worker input/output to a debug capture directory when the worker
    // subprocess is cancelled or times out. Writes partial stdout/stderr (accumulated
    // before cancellation) to preserve diagnostic information.
    // No-op when captureDir is null. Exceptions are swallowed (best-effort).
    // Files written:
    //   worker-stdin.txt   - the brief instruction sent to the subprocess
    //   worker-stdout.txt  - partial raw stdout (accumulated before cancellation)
    //   worker-stderr.txt  - partial raw stderr (accumulated before cancellation)
    //   cancel-reason.txt  - reason for cancellation
    internal static void WriteCancellationCapture(
        string? captureDir,
        string briefInstruction,
        string partialStdout,
        string partialStderr)
    {
        if (captureDir is null)
            return;

        try
        {
            Directory.CreateDirectory(captureDir);

            File.WriteAllText(Path.Combine(captureDir, "worker-stdin.txt"), briefInstruction, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "worker-stdout.txt"), partialStdout, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "worker-stderr.txt"), partialStderr, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDir, "cancel-reason.txt"), "Process cancelled or timed out", System.Text.Encoding.UTF8);
        }
        catch
        {
            // Best-effort: failure to write debug artifacts never masks the cancellation.
        }
    }
}
