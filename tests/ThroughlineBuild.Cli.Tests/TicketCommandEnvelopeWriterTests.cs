using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class TicketCommandEnvelopeWriterTests
{
    [Fact]
    public void Failed_backend_name_resolution_writes_versioned_failure_envelope()
    {
        using var output = new StringWriter();

        TicketCommandEnvelopeWriter.Write(
            output,
            "TLB-563",
            "amend",
            new CommandResult(false, "Label 'missing' not found in Plane project"));

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("failure", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Label 'missing' not found in Plane project",
            root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Successful_amend_writes_ack_envelope()
    {
        using var output = new StringWriter();

        TicketCommandEnvelopeWriter.Write(
            output,
            "TLB-563",
            "amend",
            new CommandResult(true, null));

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("TLB-563", root.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("amend", root.GetProperty("data").GetProperty("action").GetString());
    }
}
