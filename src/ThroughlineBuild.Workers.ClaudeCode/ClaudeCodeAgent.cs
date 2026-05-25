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
        // --output-format json must immediately follow --print (claude --help: "only works with --print").
        var args = new List<string> { "--print", "--output-format", "json" };
        if (options.AllowedTools is { Count: > 0 })
            args.AddRange(new[] { "--allowedTools", string.Join(",", options.AllowedTools) });
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
            return new WorkerResult(Status.Failed, "Process cancelled or timed out", Array.Empty<string>(),
                "Execution cancelled or timed out", new Dictionary<string, object>());
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        var result = ParseStdoutEnvelope(stdout, process.ExitCode, stderr, stopwatch.ElapsedMilliseconds);

        if (options.DebugCaptureDirectory is not null)
        {
            ClaudeCodeJsonEnvelope? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize(stdout.Trim(), ClaudeCodeJsonContext.Default.ClaudeCodeJsonEnvelope);
            }
            catch { }
            WriteDebugCapture(options.DebugCaptureDirectory, brief.Instruction, stdout, stderr, envelope, result);
        }

        return result;
    }

    // Parses the Claude Code JSON envelope from stdout, extracts the inner result text,
    // and routes it through WorkerResultParser. Extracted as an internal static method
    // so envelope-parsing logic can be unit-tested without spawning a real process
    // (mirrors the ConfigureEnvironment pattern; InternalsVisibleTo allows test access).
    internal static WorkerResult ParseStdoutEnvelope(string stdout, int exitCode, string stderr, long wallClockMs = 0)
    {
        ClaudeCodeJsonEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(stdout.Trim(), ClaudeCodeJsonContext.Default.ClaudeCodeJsonEnvelope);
        }
        catch (JsonException ex)
        {
            var head = stdout.Length > 200 ? stdout[..200] : stdout;
            return new WorkerResult(Status.Escalate, "Failed to parse Claude Code JSON envelope", Array.Empty<string>(),
                $"Failed to parse Claude Code JSON envelope: {ex.Message}. Stdout head: {head}", new Dictionary<string, object>());
        }

        if (envelope is null)
        {
            var head = stdout.Length > 200 ? stdout[..200] : stdout;
            return new WorkerResult(Status.Escalate, "Claude Code JSON envelope was null after deserialization", Array.Empty<string>(),
                $"Deserialized envelope was null. Stdout head: {head}", new Dictionary<string, object>());
        }

        if (envelope.IsError)
        {
            return new WorkerResult(Status.Escalate, "Claude Code reported is_error=true", Array.Empty<string>(),
                $"Claude Code envelope has is_error=true. Subtype: {envelope.Subtype}. Stderr: {stderr}", new Dictionary<string, object>());
        }

        if (envelope.Result is null)
        {
            return new WorkerResult(Status.Escalate, "Claude Code JSON envelope missing result field", Array.Empty<string>(),
                $"Envelope result field is null. Subtype: {envelope.Subtype}. Stderr: {stderr}", new Dictionary<string, object>());
        }

        // Route the inner result text through the existing WORKER_RESULT marker parser.
        var outcome = WorkerResultParser.TryParse(envelope.Result);
        if (outcome.Result != null)
        {
            // Merge llm_usage metadata on success path
            var mergedMetadata = new Dictionary<string, object>(outcome.Result.Metadata);
            mergedMetadata["llm_usage"] = BuildLlmUsageMetadata(envelope, wallClockMs);
            return outcome.Result with { Metadata = mergedMetadata };
        }

        if (outcome.DeserializeErrorType != null)
        {
            return new WorkerResult(Status.Escalate, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
                $"Failed to deserialize WORKER_RESULT JSON: {outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}", new Dictionary<string, object>());
        }

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        return new WorkerResult(Status.Escalate, "No WORKER_RESULT found in output", Array.Empty<string>(),
            $"Envelope result did not contain a WORKER_RESULT block. Stderr: {stderr}", new Dictionary<string, object>());
    }

    // Builds the llm_usage metadata dictionary from the Claude Code JSON envelope.
    // Returns a dictionary with snake_case keys including model, token counts, cache fields, and wall_clock_ms.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(ClaudeCodeJsonEnvelope envelope, long wallClockMs)
    {
        var metadata = new Dictionary<string, object>
        {
            { "model", null! },
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
}
