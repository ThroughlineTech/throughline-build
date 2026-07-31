using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Cli;

public static class WavesCommand
{
    public const int DependencyCycleExitCode = 5;

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        bool json,
        WavesConfig config,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        var parsed = Parse(args);
        if (parsed.Error is not null)
            return Usage(json, output, error, parsed.Error);

        try
        {
            var raw = parsed.InputPath == "-"
                ? await input.ReadToEndAsync(ct).ConfigureAwait(false)
                : await File.ReadAllTextAsync(parsed.InputPath!, ct).ConfigureAwait(false);
            var payload = ParseInput(raw, config.Cap);
            var plan = WavePlanner.Plan(
                payload.Tickets,
                payload.Cap,
                payload.VerifiedExternalDeps,
                config.Serialize);

            if (json)
                CliEnvelopeWriter.WriteWaves(output, plan);
            else
                WriteHuman(output, plan);
            return 0;
        }
        catch (WaveDependencyCycleException ex)
        {
            if (json)
                CliEnvelopeWriter.WriteError(output, CliErrorCodes.DependencyCycle, ex.Message);
            else
                error.WriteLine($"waves: {ex.Message}");
            return DependencyCycleExitCode;
        }
        catch (JsonException ex)
        {
            return Usage(json, output, error, $"invalid waves JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Usage(json, output, error, ex.Message);
        }
        catch (IOException ex)
        {
            return Usage(json, output, error, $"failed to read waves input: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Usage(json, output, error, $"failed to read waves input: {ex.Message}");
        }
    }

    private static ParsedPayload ParseInput(string raw, int configuredCap)
    {
        using var document = JsonDocument.Parse(raw);
        WaveTicketInput[] tickets;
        var cap = configuredCap;
        IReadOnlyList<string> verified = Array.Empty<string>();

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            tickets = (WaveTicketInput[]?)JsonSerializer.Deserialize(
                document.RootElement.GetRawText(),
                typeof(WaveTicketInput[]),
                CliJsonContext.Default)
                ?? throw new ArgumentException("input ticket array must not be null");
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var payload = (WavesInput?)JsonSerializer.Deserialize(
                document.RootElement.GetRawText(),
                typeof(WavesInput),
                CliJsonContext.Default)
                ?? throw new ArgumentException("input object must not be null");
            tickets = payload.Tickets?.ToArray()
                ?? throw new ArgumentException("input object must contain a tickets array");
            cap = payload.Cap ?? configuredCap;
            verified = payload.VerifiedExternalDeps ?? Array.Empty<string>();
        }
        else
        {
            throw new ArgumentException(
                "input must be a ticket array or an object with a tickets array");
        }

        var mapped = tickets.Select(ticket =>
        {
            if (ticket is null)
                throw new ArgumentException("each ticket must be a non-null object");
            if (string.IsNullOrWhiteSpace(ticket.Id))
                throw new ArgumentException("each ticket must contain a non-empty string id");
            return new WaveTicket(
                ticket.Id,
                ticket.Files ?? Array.Empty<string>(),
                ticket.Deps ?? Array.Empty<string>(),
                ticket.Uncertain);
        }).ToList();
        return new ParsedPayload(cap, verified, mapped);
    }

    private static ParsedOptions Parse(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] != "--input")
                return new ParsedOptions(inputPath, $"unknown option: {args[i]}");
            if (inputPath is not null)
                return new ParsedOptions(inputPath, "option may be specified only once: --input");
            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                return new ParsedOptions(inputPath, "--input requires a path or -");
            inputPath = args[i];
        }
        return inputPath is null
            ? new ParsedOptions(null, "--input is required")
            : new ParsedOptions(inputPath, null);
    }

    private static void WriteHuman(TextWriter output, WavePlan plan)
    {
        output.WriteLine($"Wave plan: {plan.TicketCount} tickets, cap {plan.Cap}");
        if (plan.VerifiedExternalDeps.Count > 0)
            output.WriteLine(
                $"Verified external dependencies: {string.Join(", ", plan.VerifiedExternalDeps)}");

        output.WriteLine("Serialization decisions:");
        if (plan.Conflicts.Count == 0)
        {
            output.WriteLine("  none");
        }
        else
        {
            foreach (var conflict in plan.Conflicts)
            {
                output.WriteLine($"  {conflict.A} X {conflict.B}:");
                foreach (var reason in conflict.Reasons)
                {
                    var pattern = reason.Pattern.Length > 0
                        ? $", pattern {reason.Pattern}"
                        : string.Empty;
                    output.WriteLine(
                        $"    {reason.Rule}: {reason.Detail} (path {reason.Path}{pattern})");
                }
            }
        }

        output.WriteLine("Wave schedule:");
        foreach (var wave in plan.Waves)
        {
            var mode = wave.Tickets.Count > 1 ? $"parallel x{wave.Tickets.Count}" : "serial";
            output.WriteLine(
                $"  wave {wave.Wave}, level {wave.Level} [{mode}]: " +
                string.Join(", ", wave.Tickets));
        }
        output.WriteLine("Speedup verdict:");
        output.WriteLine(
            $"  {plan.Speedup.SerialTicketSlots} serial ticket-slots -> " +
            $"{plan.Speedup.ScheduledWaves} waves, estimated {plan.Speedup.EstimatedSpeedup:F2}x");
        output.WriteLine(
            $"  {plan.Speedup.ParallelTickets} parallel tickets; " +
            $"{plan.Speedup.SerialBoundTickets} serial-bound; {plan.Speedup.Verdict}");
    }

    private static int Usage(
        bool json,
        TextWriter output,
        TextWriter error,
        string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build waves --input <path|-> [--json]");
        }
        return 2;
    }

    private sealed record ParsedOptions(string? InputPath, string? Error);
    private sealed record ParsedPayload(
        int Cap,
        IReadOnlyList<string> VerifiedExternalDeps,
        IReadOnlyList<WaveTicket> Tickets);
}
