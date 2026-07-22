using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli.Json;

/// <summary>Maps ticket-command domain results onto the uniform CLI JSON envelope contract.</summary>
internal static class TicketCommandEnvelopeWriter
{
    internal static void Write(TextWriter output, string ticketId, string action, CommandResult result)
    {
        if (result.Success)
            CliEnvelopeWriter.WriteAck(output, ticketId, action);
        else
            CliEnvelopeWriter.WriteError(
                output,
                CliErrorCodes.Failure,
                result.Message ?? "command failed");
    }
}
