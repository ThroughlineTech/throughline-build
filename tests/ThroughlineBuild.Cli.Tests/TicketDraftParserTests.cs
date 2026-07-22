using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// The strict JSON draft contract for build new - --json (TLB-541). Runs with reflection-based
// JSON disabled (build.runtimeconfig.json), so it also proves the source-generated TicketDraft
// deserializer - including unknown-member rejection - works on the AOT path.
public class TicketDraftParserTests
{
    [Fact]
    public void TryParse_FullDraft_PopulatesEveryField()
    {
        var json = """
        {
          "title": "Add a thing",
          "type": "feature",
          "description": "the body",
          "acceptanceCriteria": "- it works",
          "labels": ["build", "tooling"],
          "parent": "TLB-541",
          "relations": [{"kind":"blocked_by","targetId":"TLB-540"}]
        }
        """;

        var ok = TicketDraftParser.TryParse(json, out var draft, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("Add a thing", draft!.Title);
        Assert.Equal("feature", draft.Type);
        Assert.Equal("the body", draft.Description);
        Assert.Equal("- it works", draft.AcceptanceCriteria);
        Assert.Equal(new[] { "build", "tooling" }, draft.Labels);
        Assert.Equal("TLB-541", draft.Parent);
        var relation = Assert.Single(draft.Relations!);
        Assert.Equal("blocked_by", relation.Kind);
        Assert.Equal("TLB-540", relation.TargetId);
    }

    [Fact]
    public void TryParse_UnknownField_IsRejected()
    {
        var ok = TicketDraftParser.TryParse("""{"title":"x","bogus":1}""", out var draft, out var error);

        Assert.False(ok);
        Assert.Null(draft);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"title\":")]
    [InlineData("not json at all")]
    public void TryParse_EmptyOrMalformed_Fails(string json)
    {
        var ok = TicketDraftParser.TryParse(json, out var draft, out var error);

        Assert.False(ok);
        Assert.Null(draft);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_MinimalDraft_LeavesOptionalsNull()
    {
        var ok = TicketDraftParser.TryParse("""{"title":"only a title"}""", out var draft, out var error);

        Assert.True(ok);
        Assert.Equal("only a title", draft!.Title);
        Assert.Null(draft.Description);
        Assert.Null(draft.Labels);
        Assert.Null(draft.Parent);
    }

    [Fact]
    public void WriteNewTicket_EmitsVersionedSuccessEnvelope()
    {
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteNewTicket(sw, new NewTicketView(
            Id: "554",
            Uuid: "48a6db15-331a-476d-8d3d-fdd76544fd7b",
            Labels: new[] { "build" },
            Parent: "TLB-541",
            Relations: Array.Empty<RelationView>()));

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        Assert.Equal("554", data.GetProperty("id").GetString());
        Assert.Equal("48a6db15-331a-476d-8d3d-fdd76544fd7b", data.GetProperty("uuid").GetString());
        Assert.Equal("TLB-541", data.GetProperty("parent").GetString());
        Assert.Single(data.GetProperty("labels").EnumerateArray());
        Assert.Empty(data.GetProperty("relations").EnumerateArray());
    }
}
