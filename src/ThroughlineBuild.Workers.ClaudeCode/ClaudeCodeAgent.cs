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

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Send brief via stdin then close to signal EOF
        await process.StandardInput.WriteAsync(brief.Instruction);
        process.StandardInput.Close();

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

        return ParseStdoutEnvelope(stdout, process.ExitCode, stderr);
    }

    // Parses the Claude Code JSON envelope from stdout, extracts the inner result text,
    // and routes it through WorkerResultParser. Extracted as an internal static method
    // so envelope-parsing logic can be unit-tested without spawning a real process
    // (mirrors the ConfigureEnvironment pattern; InternalsVisibleTo allows test access).
    internal static WorkerResult ParseStdoutEnvelope(string stdout, int exitCode, string stderr)
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
        var parsed = WorkerResultParser.TryParse(envelope.Result);
        if (parsed != null)
            return parsed;

        if (exitCode != 0)
            return new WorkerResult(Status.Failed, "Process exited with non-zero code", Array.Empty<string>(),
                $"Exit code {exitCode}. Stderr: {stderr}", new Dictionary<string, object>());

        return new WorkerResult(Status.Escalate, "No WORKER_RESULT found in output", Array.Empty<string>(),
            $"Envelope result did not contain a WORKER_RESULT block. Stderr: {stderr}", new Dictionary<string, object>());
    }

    internal static void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        // Ensure Claude Code uses OAuth auth rather than API-key auth; worker LLM cost
        // flows to the user's subscription, not to per-token API billing.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }
}
