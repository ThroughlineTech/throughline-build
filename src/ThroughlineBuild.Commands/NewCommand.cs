using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

public sealed class NewCommand : ITicketCommand
{
    private readonly NewPhase _phase;
    private readonly string _planeProjectUrl;
    private readonly string? _debugCaptureDir;

    public NewCommand(NewPhase phase, string planeProjectUrl, string? debugCaptureDir = null)
    {
        _phase = phase;
        _planeProjectUrl = planeProjectUrl;
        _debugCaptureDir = debugCaptureDir;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        // Extract body_path from context (required).
        if (!ctx.Args.TryGetValue("body_path", out var bodyPath) || string.IsNullOrWhiteSpace(bodyPath))
        {
            return new CommandResult(false, "body_path is required");
        }

        // Extract optional flags.
        ctx.Args.TryGetValue("title", out var overrideTitle);
        ctx.Args.TryGetValue("type", out var overrideType);
        var initialLabels = ctx.Args.TryGetValue("labels", out var labelsStr)
            ? (labelsStr ?? "").Split('\t', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        var options = new NewPhaseOptions(
            bodyPath,
            InitialLabels: initialLabels.Count > 0 ? initialLabels : null,
            OverrideTitle: overrideTitle,
            OverrideType: overrideType);

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var result = await _phase.RunAsync(options, cts.Token).ConfigureAwait(false);

            // Format success message.
            var output = new System.Text.StringBuilder();
            output.AppendLine($"Created {result.Id} (uuid: {result.Uuid})");

            // Add plane URL if configured.
            var planeUrl = BuildPlaneUrl(_planeProjectUrl, result.Id);
            if (!string.IsNullOrEmpty(planeUrl))
            {
                output.AppendLine(planeUrl);
            }

            // Add warnings if any.
            foreach (var warning in result.ValidationWarnings)
            {
                output.AppendLine($"Warning: {warning}");
            }

            // Write debug artifacts if --debug was set.
            if (_debugCaptureDir is not null)
            {
                await WriteDebugArtifactsAsync(bodyPath, result, ct).ConfigureAwait(false);
            }

            return new CommandResult(true, output.ToString().TrimEnd());
        }
        catch (NewPhaseValidationException ex)
        {
            return new CommandResult(false, $"validation error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "cancelled");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"failed to create ticket: {ex.Message}");
        }
    }

    private static string BuildPlaneUrl(string planeProjectUrl, string ticketId)
    {
        if (string.IsNullOrEmpty(planeProjectUrl) || string.IsNullOrEmpty(ticketId))
            return string.Empty;
        var trimmed = planeProjectUrl.TrimEnd('/');
        return $"{trimmed}/browse/{ticketId}/";
    }

    private async Task WriteDebugArtifactsAsync(string bodyPath, NewResult result, CancellationToken ct)
    {
        try
        {
            // Create debug capture directory if it doesn't exist.
            Directory.CreateDirectory(_debugCaptureDir!);

            // Write validation warnings to validation.txt.
            var validationPath = Path.Combine(_debugCaptureDir!, "validation.txt");
            if (result.ValidationWarnings.Count > 0)
            {
                var validationContent = string.Join(Environment.NewLine, result.ValidationWarnings);
                await File.WriteAllTextAsync(validationPath, validationContent, ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(validationPath, "No validation warnings", ct).ConfigureAwait(false);
            }

            // Copy body file to debug capture.
            var bodyDestPath = Path.Combine(_debugCaptureDir!, "body.md");
            if (File.Exists(bodyPath))
            {
                File.Copy(bodyPath, bodyDestPath, overwrite: true);
            }

            // Write API response stub (actual API response comes from PlaneTicketingClient via event log).
            var responseStubPath = Path.Combine(_debugCaptureDir!, "api-response.json");
            var responseContent = $"{{\"id\":\"{result.Id}\",\"uuid\":\"{result.Uuid}\"}}";
            await File.WriteAllTextAsync(responseStubPath, responseContent, ct).ConfigureAwait(false);
        }
        catch
        {
            // Tolerate debug artifact write failures.
        }
    }
}
