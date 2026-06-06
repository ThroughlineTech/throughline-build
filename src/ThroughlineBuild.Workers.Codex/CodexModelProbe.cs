using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Codex;

// One discovered, operator-selectable model.
public record CodexModelInfo(
    string Slug,
    string? DefaultEffort,                    // from default_reasoning_level (may be null)
    IReadOnlyList<string> SupportedEfforts);  // non-null efforts from supported_reasoning_levels, in payload order

// The curated discovery result (list-visible models only).
public record CodexModelDiscovery(IReadOnlyList<CodexModelInfo> Models);

public enum CodexProbeFailureKind
{
    CommandFailed,      // executable not found, timed out, or non-zero exit
    OutputUnparseable,  // process ran (exit 0) but stdout could not be parsed into a models list
}

// Non-throwing result. Success => Discovery non-null, FailureKind/Diagnostic null.
// Failure => Discovery null, FailureKind set, Diagnostic carries stderr (CommandFailed)
// or the first ~500 chars of stdout (OutputUnparseable) for the caller's message.
public record CodexProbeResult(
    bool Success,
    CodexModelDiscovery? Discovery,
    CodexProbeFailureKind? FailureKind,
    string? Diagnostic)
{
    public static CodexProbeResult Ok(CodexModelDiscovery discovery) => new(true, discovery, null, null);
    public static CodexProbeResult Fail(CodexProbeFailureKind kind, string diagnostic) => new(false, null, kind, diagnostic);
}

// Runs `<exe> debug models`, parses the JSON, and returns the list-visible model
// slugs (each with its supported reasoning-effort levels and default effort) or a
// typed failure. Never throws for an absent executable, non-zero exit, timeout, or
// bad output. Consumed by `build init` and `build models refresh`.
public sealed class CodexModelProbe
{
    private const int DiagnosticHeadChars = 500;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly string _executablePath;

    public CodexModelProbe(string executablePath = "codex") =>
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? "codex" : executablePath;

    // Spawns `<exe> debug models`, captures stdout/stderr/exit, returns a typed result. Never throws for
    // an absent executable, non-zero exit, timeout, or bad output. Default timeout ~60s via a linked CTS.
    // This is a read-only query: it inherits the parent environment and passes no bypass flags.
    public async Task<CodexProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("debug");
        psi.ArgumentList.Add("models");
        ProcessStreamEncoding.ApplyUtf8(psi);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return CodexProbeResult.Fail(CodexProbeFailureKind.CommandFailed,
                $"codex executable not found: '{_executablePath}'. {ex.Message}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return CodexProbeResult.Fail(CodexProbeFailureKind.CommandFailed, "codex debug models timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return Interpret(process.ExitCode, stdout, stderr);
    }

    // PURE seam: turn a parsed model list (JSON text) into a discovery, or null when it can't be parsed.
    // Returns null if deserialize throws JsonException, envelope is null, or envelope.Models is null.
    // Filters to visibility == "list" (ordinal, case-sensitive). For each kept model: Slug must be a
    // non-empty string; DefaultEffort = DefaultReasoningLevel; SupportedEfforts = the non-null/non-empty
    // Effort strings from SupportedReasoningLevels (empty list if none).
    internal static CodexModelDiscovery? TryParse(string stdout)
    {
        CodexDebugModelsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(stdout, CodexProbeJsonContext.Default.CodexDebugModelsEnvelope);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope?.Models is null)
            return null;

        var models = new List<CodexModelInfo>();
        foreach (var model in envelope.Models)
        {
            if (model is null)
                continue;
            if (!string.Equals(model.Visibility, "list", StringComparison.Ordinal))
                continue;
            if (string.IsNullOrEmpty(model.Slug))
                continue;

            var efforts = new List<string>();
            if (model.SupportedReasoningLevels is not null)
            {
                foreach (var level in model.SupportedReasoningLevels)
                {
                    if (level?.Effort is { Length: > 0 } effort)
                        efforts.Add(effort);
                }
            }

            models.Add(new CodexModelInfo(model.Slug, model.DefaultReasoningLevel, efforts));
        }

        return new CodexModelDiscovery(models);
    }

    // PURE seam: decide a result from a completed process. exitCode != 0 => Fail(CommandFailed, stderr-or-head);
    // exitCode == 0 => TryParse(stdout); null => Fail(OutputUnparseable, stdout-head); else Ok(discovery).
    internal static CodexProbeResult Interpret(int exitCode, string stdout, string stderr)
    {
        if (exitCode != 0)
        {
            var head = Head(stderr);
            var diagnostic = head.Length > 0 ? head : $"codex debug models exited with code {exitCode}";
            return CodexProbeResult.Fail(CodexProbeFailureKind.CommandFailed, diagnostic);
        }

        var discovery = TryParse(stdout);
        if (discovery is null)
            return CodexProbeResult.Fail(CodexProbeFailureKind.OutputUnparseable, Head(stdout));

        return CodexProbeResult.Ok(discovery);
    }

    // First ~500 chars (trimmed) so diagnostics stay bounded.
    private static string Head(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var trimmed = text.Trim();
        return trimmed.Length <= DiagnosticHeadChars ? trimmed : trimmed.Substring(0, DiagnosticHeadChars);
    }
}
