using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Renders a <see cref="PlaneApiException"/> to the CLI uniformly across verbs. The actionable text
/// for a misconfigured-project or feature-not-enabled 404 is set on <c>ex.Message</c> at the Plane
/// client layer (which knows the route), so this only chooses the output channel and exit code -
/// keeping every verb's catch identical instead of each re-deriving "Plane API {status}: {body}",
/// which is how `list` ended up printing a raw 404 while `get` had the friendly message.
/// </summary>
internal static class PlaneCliError
{
    /// <summary>Writes the error to the right channel (JSON envelope or stderr) and returns exit code 1.</summary>
    public static int Report(string verb, PlaneApiException ex, bool jsonOutput, CliContext? context = null)
    {
        var message = MessageFor(ex, context);
        if (jsonOutput)
            CliEnvelopeWriter.WriteError(
                Console.Out,
                ex.Status == 404 ? CliErrorCodes.ConfigError : CliErrorCodes.Failure,
                message);
        else
            Console.Error.WriteLine($"Command '{verb}' failed: {message}");
        return 1;
    }

    public static string MessageFor(Exception ex, CliContext? context)
    {
        if (ex is PlaneApiException plane)
            return MessageFor(plane, context);

        var authPlane = FindAuthPlaneException(ex);
        if (authPlane is null)
            return ex.Message;

        var authMessage = MessageFor(authPlane, context);
        var message = ex.Message;

        if (!string.IsNullOrEmpty(authPlane.Message))
            message = message.Replace(authPlane.Message, authMessage, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(authPlane.Body))
            message = message.Replace(authPlane.Body, "[redacted Plane response body]", StringComparison.Ordinal);
        if (!message.Contains(authMessage, StringComparison.Ordinal))
            message = $"{message} {authMessage}";

        return message;
    }

    public static string MessageFor(PlaneApiException ex, CliContext? context)
    {
        if (ex.Status is not (401 or 403))
            return ex.Message;

        if (context is null)
            return $"Plane authorization failed with HTTP {ex.Status}. Re-run connected 'build init' from the affected repository and retry.";

        var config = context.Config.Ticketing;
        var configPath = Path.GetFullPath(context.ConfigPath);
        var repoRoot = Path.GetFullPath(context.WorkingDirectory);
        var project = string.IsNullOrWhiteSpace(config.PlaneProjectName)
            ? config.PlaneProjectId
            : $"{config.PlaneProjectName} ({config.PlaneProjectId})";

        return $"Plane authorization failed with HTTP {ex.Status} for repository-local config '{configPath}' " +
            $"in repository '{repoRoot}', workspace '{config.PlaneWorkspaceSlug}', project '{project}'. " +
            "Configuration is repository-local; sibling repositories can select different .build/config.toml files. " +
            "Re-run connected 'build init' from the affected repository, or update that repository's supported Plane configuration and retry.";
    }

    private static PlaneApiException? FindAuthPlaneException(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is PlaneApiException { Status: 401 or 403 } plane)
                return plane;
        return null;
    }
}
