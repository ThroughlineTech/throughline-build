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
    public static int Report(string verb, PlaneApiException ex, bool jsonOutput)
    {
        if (jsonOutput)
            CliEnvelopeWriter.WriteError(
                Console.Out,
                ex.Status == 404 ? CliErrorCodes.ConfigError : CliErrorCodes.Failure,
                ex.Message);
        else
            Console.Error.WriteLine($"Command '{verb}' failed: {ex.Message}");
        return 1;
    }
}
