using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.ClaudeCode;

public class ClaudeCodeAgent : IWorkerAgent
{
    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodeProgressDigester _digester = new();

    public ClaudeCodeAgent(ClaudeCodeOptions options) => _options = options;
    public ClaudeCodeAgent() : this(new ClaudeCodeOptions()) { }

    public string Name => "claude-code";
    public IWorkerProgressDigester? Digester => _digester;

    public async Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // Write brief to .build/brief.md (persisted for diagnostics)
        var buildDir = Path.Combine(workingDirectory, ".build");
        Directory.CreateDirectory(buildDir);
        var briefPath = Path.Combine(buildDir, "brief.md");
        await File.WriteAllTextAsync(briefPath, brief.Instruction, ct);

        // Build args - brief is delivered via stdin.
        var args = BuildArgs(_options, options);
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
        ConfigureEnvironment(psi, options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;

        // Under --debug, capture each stdout line with its arrival timestamp so the structured
        // transcript can report per-turn latency. The CLI's stream events carry no timestamps;
        // arrival time is the authoritative inter-turn clock. Allocated only when capturing -
        // the non-debug path keeps the exact same zero-overhead handler it had before.
        var timestampedStdout = options.DebugCaptureDirectory is not null
            ? new List<(DateTimeOffset At, string Line)>()
            : null;

        var process = new Process { StartInfo = psi };
        _digester.ResetStart();
        if (options.ProgressDigestSink is not null)
        {
            var startModel = NormalizeModel(tier?.Model);
            var startPayload = string.IsNullOrEmpty(startModel) ? Name : $"{Name} model {startModel}";
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
                    // --debug path: raw firehose. Digest is suppressed (mutually exclusive).
                    WriteWorkerLine(options.LiveStdoutSink, "", e.Data);
                }
                else if (options.ProgressDigestSink is not null)
                {
                    // Default path: per-event digest. Best-effort - a malformed line or
                    // unexpected schema must not crash the worker dispatch.
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

        // Send brief via stdin then close to signal EOF. If the subprocess exits before
        // reading stdin - an immediate startup error, a rate-limit exit, or the nested-session
        // guard rejecting `claude` inside another Claude Code session - the pipe closes
        // mid-write and WriteAsync throws IOException. Swallow it: a broken stdin pipe must
        // NOT abort the whole orchestrator. WaitForExit below still runs, and the captured
        // stderr (surfaced by ParseStdoutEnvelope) carries the real cause as a clean
        // WorkerResult.Failed. See TLB-472.
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

            // Write partial output to debug capture directory (best-effort)
            try
            {
                WriteCancellationCapture(options.DebugCaptureDirectory, brief.Instruction,
                    stdoutBuilder.ToString(), stderrBuilder.ToString());

                // Partial transcript too: a worker killed mid-session still spent turns, and the
                // truncated stream synthesizes an "incomplete" result record so the file is usable.
                if (options.DebugCaptureDirectory is not null && timestampedStdout is not null)
                {
                    var cancelModel = TryExtractModelFromStream(stdoutBuilder.ToString()) ?? NormalizeModel(tier?.Model);
                    WorkerTranscriptWriter.Write(options.DebugCaptureDirectory, brief, timestampedStdout,
                        cancelResult, options.DebugTranscript, cancelModel, args, stopwatch.ElapsedMilliseconds, startedAt);
                }
            }
            catch
            {
                // Best-effort: failure to write debug artifacts never masks the cancellation.
            }

            return cancelResult;
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var fallbackModel = NormalizeModel(tier?.Model);
        var result = ParseStdoutEnvelope(stdout, process.ExitCode, stderr, stopwatch.ElapsedMilliseconds, fallbackModel);

        result = AttachContextTurns(result, stdout);

        if (options.DebugCaptureDirectory is not null)
        {
            // Same envelope-parse fallback chain as ParseStdoutEnvelope: try single
            // object first (legacy), fall back to last type=result NDJSON line.
            ClaudeCodeJsonEnvelope? envelope = TryParseEnvelopeFromStdout(stdout, out _);
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, envelope, result);

            // Structured side-channel transcript: the raw stream re-emitted in a stable,
            // mechanically-comparable JSONL schema. Pure observation; best-effort internally.
            var transcriptModel = TryExtractModelFromStream(stdout) ?? fallbackModel;
            WorkerTranscriptWriter.Write(options.DebugCaptureDirectory, brief,
                timestampedStdout ?? new List<(DateTimeOffset, string)>(), result, options.DebugTranscript,
                transcriptModel, args, stopwatch.ElapsedMilliseconds, startedAt);
        }

        return result;
    }

    // Behavior-inert telemetry: after the worker exits, parse the already-captured NDJSON stream
    // for per-turn context attribution and stash it on Metadata["context_turns"] as a flat dict of
    // scalar + List<long> values (a flat shape so the event-log AOT context needs only List<long>).
    // Best-effort: any parse failure leaves the result untouched. Attaches nothing when no turns parsed.
    internal static WorkerResult AttachContextTurns(WorkerResult result, string stdout)
    {
        try
        {
            var series = ClaudeCodeTurnParser.Parse(stdout);
            if (series.Turns == 0) return result;
            var ctx = new Dictionary<string, object>
            {
                ["cache_read_series"] = new List<long>(series.CacheReadSeries),
                ["cache_creation_series"] = new List<long>(series.CacheCreationSeries),
                ["output_series"] = new List<long>(series.OutputSeries),
                ["turns"] = series.Turns,
                ["total_cache_read"] = series.TotalCacheRead,
                ["slope_ratio"] = series.SlopeRatio,
                ["read_bytes"] = series.ReadBytes,
                ["write_bytes"] = series.WriteBytes,
                ["todo_bytes"] = series.TodoBytes,
                ["task_bytes"] = series.TaskBytes,
                ["bash_bytes"] = series.BashBytes,
                ["other_bytes"] = series.OtherBytes,
            };
            var merged = new Dictionary<string, object>(result.Metadata) { ["context_turns"] = ctx };
            return result with { Metadata = merged };
        }
        catch
        {
            return result;
        }
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

    // Reconstructs the complete assistant-visible output of the session from the NDJSON
    // stream: every `text` content block of every type=assistant event, concatenated in
    // stream order. Each assistant NDJSON line carries the NEW content block(s) of its
    // message (lines of one message share a message id; content is additive across lines,
    // never cumulative - same shape ClaudeCodeTurnParser and WorkerTranscriptWriter rely on),
    // so plain in-order concatenation is the faithful transcript. Thinking blocks and
    // tool_use blocks are skipped: the WORKER_RESULT protocol lives in assistant text only.
    // Returns null when the stream carries no assistant text at all (e.g. the legacy
    // --output-format json single-blob shape), so callers can fall back to the envelope's
    // own result field. AOT-safe: JsonDocument reads only, best-effort per line.
    internal static string? TryExtractAssistantTranscript(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        var sb = new StringBuilder();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(trimmed); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String
                    || typeEl.GetString() != "assistant")
                    continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                    continue;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (!block.TryGetProperty("type", out var btEl)
                        || btEl.ValueKind != JsonValueKind.String
                        || btEl.GetString() != "text")
                        continue;
                    if (!block.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                        continue;
                    var text = textEl.GetString();
                    if (string.IsNullOrEmpty(text)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
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

    // Best-effort: parse a single NDJSON line via ClaudeCodeProgressDigester and write
    // the formatted digest line to the sink. Any exception (malformed JSON,
    // unexpected schema, sink-write failure) is swallowed: digest emission must
    // never crash the worker dispatch.
    internal static void TryEmitDigestLine(string ndjsonLine, System.IO.TextWriter sink, DateTimeOffset startTime)
    {
        try
        {
            using var doc = JsonDocument.Parse(ndjsonLine);
            var digester = new ClaudeCodeProgressDigester();
            var formatted = digester.FormatLine(doc.RootElement, startTime);
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
            // Include stderr: when stdout is empty/garbage the cause almost always lives in
            // stderr (e.g. the worker subprocess exited immediately with an error message),
            // and without it the failure reason is undiagnosable. See TLB-472.
            if (parseError is not null)
            {
                var failureReason = $"Failed to parse Claude Code JSON envelope: {parseError}. Stdout head: {head}. Stderr: {stderr}";
                WorkerDiagnostics.Write($"[WorkerResultParser] {failureReason}");
                return new WorkerResult(Status.Failed, "Failed to parse Claude Code JSON envelope", Array.Empty<string>(),
                    failureReason, new Dictionary<string, object>());
            }
            var nullFailureReason = $"Deserialized envelope was null. Stdout head: {head}. Stderr: {stderr}";
            WorkerDiagnostics.Write($"[WorkerResultParser] {nullFailureReason}");
            return new WorkerResult(Status.Failed, "Claude Code JSON envelope was null after deserialization", Array.Empty<string>(),
                nullFailureReason, new Dictionary<string, object>());
        }

        if (envelope.IsError)
        {
            // The CLI rejects an unresolvable --model value at session init with this phrasing
            // ("There's an issue with the selected model (fable). It may not exist or you may
            // not have access to it."). That is a config problem, not a transient provider
            // error - name the model and the config key so the operator fixes it in one step.
            if (TryDescribeInvalidModelError(envelope.Result, stderr) is string invalidModelReason)
                return new WorkerResult(Status.Escalate, "Claude Code rejected the configured model", Array.Empty<string>(),
                    invalidModelReason, new Dictionary<string, object>());

            // The human-readable failure (e.g. "Claude AI usage limit reached|<ts>") lives in the
            // envelope's result field; include it so the reason is not just a subtype + blank stderr. See TLB-490.
            var message = string.IsNullOrWhiteSpace(envelope.Result) ? "" : $" Message: {envelope.Result}.";
            return new WorkerResult(Status.Escalate, "Claude Code reported is_error=true", Array.Empty<string>(),
                $"Claude Code envelope has is_error=true. Subtype: {envelope.Subtype}.{message} Stderr: {stderr}", new Dictionary<string, object>());
        }

        if (envelope.Result is null)
        {
            var failureReason = $"Envelope result field is null. Subtype: {envelope.Subtype}. Stderr: {stderr}";
            WorkerDiagnostics.Write($"[WorkerResultParser] {failureReason}");
            return new WorkerResult(Status.Failed, "Claude Code JSON envelope missing result field", Array.Empty<string>(),
                failureReason, new Dictionary<string, object>());
        }

        // Extract model from the NDJSON system event; fall back to the configured default.
        var model = TryExtractModelFromStream(stdout) ?? fallbackModel;

        // Parse the FULL assistant transcript, not just the envelope's result field. Claude
        // Code's `result` carries only the FINAL assistant message, so a worker that emits a
        // fenced block in one message and the WORKER_RESULT envelope in a later message (or
        // narrates in a fresh message after the envelope) loses content if `result` alone is
        // parsed. Fable splits output across messages far more often than Opus/Sonnet, which
        // surfaced this as a nondeterministic blocks-missing failure in scaffold profile
        // derivation. The transcript is a superset of `result`; when the stream carries no
        // assistant lines (legacy --output-format json single-blob), fall back to `result`.
        // If the transcript parse fails outright, retry on `result` so behavior is never
        // worse than the pre-transcript path (e.g. a malformed fence echoed early in the
        // session must not poison an otherwise clean final message).
        var transcript = TryExtractAssistantTranscript(stdout);
        var outcome = WorkerResultParser.TryParse(transcript ?? envelope.Result);
        if (transcript is not null && outcome.Result is null)
            outcome = WorkerResultParser.TryParse(envelope.Result);
        if (outcome.Result != null)
        {
            // Merge llm_usage metadata on success path
            var mergedMetadata = new Dictionary<string, object>(outcome.Result.Metadata);
            mergedMetadata["llm_usage"] = BuildLlmUsageMetadata(envelope, wallClockMs, model, vendor: "anthropic");
            return outcome.Result with { Metadata = mergedMetadata, Blocks = outcome.Blocks };
        }

        if (outcome.DeserializeErrorType != null)
        {
            var failureReason = $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}";
            WorkerDiagnostics.Write($"[WorkerResultParser] {failureReason}");
            // A valid JSON object that merely omitted the required 'status' field is a committed
            // session worth salvaging (clean tree + HEAD advanced); tag it so ImplementPhase can
            // recover it instead of discarding the branch. Any other deserialize error stays
            // untagged (untrustworthy). See TLB-476.
            var metadata = outcome.MissingStatusField
                ? new Dictionary<string, object> { [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissingStatus }
                : new Dictionary<string, object>();
            return new WorkerResult(Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                failureReason, metadata);
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        var markerFailureReason = $"Envelope result did not contain a WORKER_RESULT block. Stderr: {stderr}";
        WorkerDiagnostics.Write($"[WorkerResultParser] {markerFailureReason}");
        // Clean exit (code 0, parseable envelope) but no WORKER_RESULT marker: tag it so
        // ImplementPhase can salvage a committed session that merely omitted the envelope. See TLB-471.
        return new WorkerResult(Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
            markerFailureReason,
            new Dictionary<string, object> { [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissing });
    }

    // Builds the llm_usage metadata dictionary from the Claude Code JSON envelope.
    // Returns a dictionary with snake_case keys including model, vendor, token counts, cache fields, and wall_clock_ms.
    // anthropic_request_id is not included: the Claude Code CLI does not expose it in the stream envelope.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(ClaudeCodeJsonEnvelope envelope, long wallClockMs, string? model = null, string vendor = "anthropic")
    {
        var metadata = new Dictionary<string, object>
        {
            { "model", model! },
            { "vendor", vendor },
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

        if (envelope.TotalCostUsd is decimal cost)
            metadata["cost_usd"] = (double)cost;

        return metadata;
    }

    // Builds the argv passed to the claude CLI.
    //
    // --output-format must immediately follow --print (claude --help:
    // "only works with --print"). --verbose is required by claude-code when
    // combining --print with --output-format stream-json (the CLI rejects the
    // combination otherwise). The terminal NDJSON event is type=result and is
    // bit-for-bit identical to the legacy --output-format json single-blob
    // envelope, so envelope parsing downstream is unchanged.
    //
    // --dangerously-skip-permissions: worker runs headless via --print; the
    // interactive approval gate is always unreachable. settings.json
    // defaultMode:bypassPermissions is ignored by subprocesses, so this flag
    // must be explicit. AllowedTools still constrains which tools the worker
    // may call (e.g. review phase restricts to Read/Grep/Glob). The flag is
    // emitted only when options.BypassPermissions is true; setting it false in
    // config opts back into the gate.
    internal static List<string> BuildArgs(ClaudeCodeOptions options, WorkerOptions workerOptions)
    {
        var args = new List<string> { "--print", "--verbose", "--output-format", "stream-json" };
        if (options.BypassPermissions)
            args.Add("--dangerously-skip-permissions");
        if (workerOptions.AllowedTools is { Count: > 0 })
            args.AddRange(new[] { "--allowedTools", string.Join(",", workerOptions.AllowedTools) });
        // Experiment 4 (L2b): lean planning drops the planning tools from the child's context. The
        // phase expresses a stack-agnostic LeanPlanning intent; this adapter is the ONE place the
        // claude-code tool-name literals live (mirrors the review phase's --allowedTools list).
        // --disallowedTools removes the tools (--allowedTools only auto-approves, so disallow is the
        // correct flag to actually disable them).
        if (workerOptions.LeanPlanning)
            args.AddRange(new[] { "--disallowedTools", "TodoWrite,Task" });
        options.Sizes.TryGetValue(workerOptions.Size, out var tier);
        var modelArg = NormalizeModel(tier?.Model);
        if (modelArg is not null)
            args.AddRange(new[] { "--model", modelArg });
        foreach (var extra in options.ExtraArgs)
            args.Add(extra);
        return args;
    }

    // Recognizes the CLI's invalid-model session-init error and rewrites it into an
    // operator-actionable failure reason naming the configured model and the config key
    // to fix. Returns null for every other is_error message so the generic envelope
    // path (and ProviderErrorClassifier's signature matching) is untouched.
    internal static string? TryDescribeInvalidModelError(string? envelopeResult, string stderr)
    {
        if (string.IsNullOrWhiteSpace(envelopeResult))
            return null;
        const string signature = "issue with the selected model";
        var idx = envelopeResult.IndexOf(signature, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        // Best-effort extraction of the model name from "...selected model (fable)...".
        var modelClause = "";
        var open = envelopeResult.IndexOf('(', idx);
        if (open >= 0)
        {
            var close = envelopeResult.IndexOf(')', open + 1);
            if (close > open + 1)
                modelClause = $" '{envelopeResult.Substring(open + 1, close - open - 1)}'";
        }

        return $"Claude Code rejected the configured model{modelClause} at session init - the CLI only accepts " +
               "the tier aliases haiku/sonnet/opus or a full claude-* model id (e.g. \"claude-fable-5\"). " +
               $"Fix the model value under [workers.claude-code.sizes] in .build/config.toml. " +
               $"Original message: {envelopeResult.Trim()} Stderr: {stderr}";
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
