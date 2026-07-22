using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// Verifies the --json envelope contract (TLB-541): a versioned, camelCase envelope where
// success is {schemaVersion, ok:true, data} and failure is {schemaVersion, ok:false, error}.
// The test assembly runs with reflection-based JSON disabled (build.runtimeconfig.json), so
// these also prove the source-generated context serializes the envelope without reflection -
// the AOT path the shipped binary actually uses.
public class CliEnvelopeWriterTests
{
    private static Ticket SampleTicket() => new(
        Id: "541",
        Uuid: "1b8289ec-18e1-4ef7-aba7-946818707501",
        Title: "Replace /ticket-* slash commands",
        Type: "feature",
        State: TicketState.InProgress,
        Size: Size.M,
        Risk: Risk.Medium,
        DescriptionHtml: "<p>body</p>",
        Relations: new[] { new Relation("blocked_by", "TLB-540") },
        Labels: new[] { "build", "tooling" },
        ParentId: "TLB-500");

    [Fact]
    public void WriteError_EmitsVersionedFailureEnvelope_WithCamelCaseKeys()
    {
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteError(sw, CliErrorCodes.NotFound, "Issue with sequence_id 99999 not found in Plane");

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("not_found", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Issue with sequence_id 99999 not found in Plane",
            root.GetProperty("error").GetProperty("message").GetString());
        // A failure envelope carries no data payload.
        Assert.False(root.TryGetProperty("data", out _));
    }

    [Fact]
    public void WriteTicket_EmitsVersionedSuccessEnvelope_WithStringEnumsAndCollections()
    {
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteTicket(sw, CliEnvelopeWriter.ToView(SampleTicket()));

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        // A success envelope carries no error.
        Assert.False(root.TryGetProperty("error", out _));

        var data = root.GetProperty("data");
        Assert.Equal("541", data.GetProperty("id").GetString());
        Assert.Equal("1b8289ec-18e1-4ef7-aba7-946818707501", data.GetProperty("uuid").GetString());
        // Enums render as names, not integers.
        Assert.Equal("InProgress", data.GetProperty("state").GetString());
        Assert.Equal("M", data.GetProperty("size").GetString());
        Assert.Equal("Medium", data.GetProperty("risk").GetString());
        Assert.Equal("TLB-500", data.GetProperty("parentId").GetString());
        // Body is exposed both as readable plain text and as the raw Plane HTML.
        Assert.Equal("body", data.GetProperty("description").GetString());
        Assert.Equal("<p>body</p>", data.GetProperty("descriptionHtml").GetString());

        var labels = data.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "build", "tooling" }, labels);

        var rel = data.GetProperty("relations").EnumerateArray().Single();
        Assert.Equal("blocked_by", rel.GetProperty("kind").GetString());
        Assert.Equal("TLB-540", rel.GetProperty("targetId").GetString());
    }

    [Fact]
    public void ToView_MapsEveryTicketField()
    {
        var view = CliEnvelopeWriter.ToView(SampleTicket());

        Assert.Equal("541", view.Id);
        Assert.Equal("feature", view.Type);
        Assert.Equal(TicketState.InProgress, view.State);
        Assert.Equal("<p>body</p>", view.DescriptionHtml);
        Assert.Equal("body", view.Description);
        Assert.Equal("TLB-500", view.ParentId);
        Assert.Equal(2, view.Labels.Count);
        var rel = Assert.Single(view.Relations);
        Assert.Equal("blocked_by", rel.Kind);
        Assert.Equal("TLB-540", rel.TargetId);
    }

    [Fact]
    public void WriteRelations_UsesSourceGeneratedEnvelope_AndIncludesStableId()
    {
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteRelations(sw,
            new[] { new Relation("blocking", "TLB-9", "edge-123") });

        using var doc = JsonDocument.Parse(sw.ToString());
        var relation = doc.RootElement.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("edge-123", relation.GetProperty("id").GetString());
        Assert.Equal("blocking", relation.GetProperty("kind").GetString());
        Assert.Equal("TLB-9", relation.GetProperty("targetId").GetString());
    }
}
