using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

/// <summary>Testable argument, dispatch, and envelope boundary for <c>build relate</c>.</summary>
internal static class RelateCommand
{
    internal const string Usage = "Usage: build relate <ticket-id> <relation-type> <target-id> [--json]\n" +
        "       build relate <ticket-id> --list [--json]\n" +
        "       build relate <ticket-id> --remove <relation-id> [--json]";

    public static async Task<int> ExecuteAsync(
        string[] args,
        bool jsonOutput,
        ITicketing ticketing,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        string? usageError = null;
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            usageError = "ticket-id is required";
        else if (args.Length == 3 && args[2] == "--list")
            usageError = null;
        else if (args.Length == 4 && args[2] == "--remove" && !string.IsNullOrWhiteSpace(args[3]))
            usageError = null;
        else if (args.Length == 4 && !args[2].StartsWith("--") && !args[3].StartsWith("--"))
            usageError = null;
        else
            usageError = "expected create, --list, or --remove arguments";

        string? normalizedKind = null;
        if (usageError is null && args.Length == 4 && args[2] != "--remove"
            && !RelationKinds.TryNormalize(args[2], out normalizedKind))
        {
            usageError = $"invalid relation type '{args[2]}'; valid types: {string.Join(", ", RelationKinds.Allowed)}";
        }

        if (usageError is not null)
        {
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, usageError);
            else { error.WriteLine($"Error: {usageError}"); error.WriteLine(Usage); }
            return 2;
        }

        var ticketId = args[1];
        try
        {
            if (args[2] == "--list")
            {
                var relations = await ticketing.ListRelationsAsync(ticketId, ct).ConfigureAwait(false);
                if (jsonOutput) CliEnvelopeWriter.WriteRelations(output, relations);
                else if (relations.Count == 0) output.WriteLine("no relations");
                else foreach (var relation in relations)
                    output.WriteLine($"{relation.Id}  {relation.Kind} -> {relation.TargetId}");
            }
            else if (args[2] == "--remove")
            {
                await ticketing.RemoveRelationAsync(ticketId, args[3], ct).ConfigureAwait(false);
                if (jsonOutput) CliEnvelopeWriter.WriteRelate(output,
                    new RelateView(ticketId, "removed", RelationId: args[3]));
                else output.WriteLine($"Removed relation {args[3]} from {ticketId}");
            }
            else
            {
                await ticketing.CreateRelationAsync(ticketId, normalizedKind!, args[3], ct).ConfigureAwait(false);
                if (jsonOutput) CliEnvelopeWriter.WriteRelate(output,
                    new RelateView(ticketId, "created", normalizedKind, args[3]));
                else output.WriteLine($"{ticketId} {normalizedKind} -> {args[3]}");
            }
            return 0;
        }
        catch (ArgumentException ex)
        {
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, ex.Message);
            else error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (KeyNotFoundException ex)
        {
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, CliErrorCodes.NotFound, ex.Message);
            else error.WriteLine($"Command 'relate' failed: {ex.Message}");
            return 1;
        }
        catch (RelationEndpointUnavailableException ex)
        {
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, CliErrorCodes.ConfigError, ex.Message);
            else error.WriteLine($"Command 'relate' failed: {ex.Message}");
            return 2;
        }
        catch (RelationConfigurationException ex)
        {
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, CliErrorCodes.ConfigError, ex.Message);
            else error.WriteLine($"Command 'relate' failed: {ex.Message}");
            return 2;
        }
        catch (PlaneApiException ex)
        {
            var code = ex.Status == 404 ? CliErrorCodes.ConfigError : CliErrorCodes.Failure;
            if (jsonOutput) CliEnvelopeWriter.WriteError(output, code, ex.Message);
            else error.WriteLine($"Command 'relate' failed: {ex.Message}");
            return ex.Status == 404 ? 2 : 1;
        }
    }
}
