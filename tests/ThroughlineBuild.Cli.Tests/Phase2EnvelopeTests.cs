using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// Envelope shapes for the Phase 2 read/query verbs (list, comments, comment, transition).
// Reflection-based JSON is disabled for this assembly, so these also exercise the AOT
// source-gen path the shipped binary uses.
public class Phase2EnvelopeTests
{
    private static Ticket Row(string id, string title, TicketState state, string? parent) => new(
        Id: id, Uuid: "u-" + id, Title: title, Type: "feature", State: state,
        Size: Size.L, Risk: Risk.High, DescriptionHtml: "", Relations: Array.Empty<Relation>(),
        Labels: new[] { "size:l", "risk:high", "team:core" }, ParentId: parent);

    [Fact]
    public void WriteList_EmitsRowArray()
    {
        var sw = new StringWriter();
        CliEnvelopeWriter.WriteList(sw, new[]
        {
            Row("1", "first", TicketState.Backlog, null),
            Row("2", "second", TicketState.InReview, "TLB-1"),
        });

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var rows = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("first", rows[0].GetProperty("title").GetString());
        Assert.Equal("Backlog", rows[0].GetProperty("state").GetString());
        Assert.Equal("L", rows[0].GetProperty("size").GetString());
        Assert.Equal("High", rows[0].GetProperty("risk").GetString());
        Assert.Equal(
            new[] { "size:l", "risk:high", "team:core" },
            rows[0].GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("InReview", rows[1].GetProperty("state").GetString());
        Assert.Equal("TLB-1", rows[1].GetProperty("parentId").GetString());
        // Lean projection: no description on list rows.
        Assert.False(rows[0].TryGetProperty("descriptionHtml", out _));
    }

    [Fact]
    public void WriteComments_EmitsCommentArray()
    {
        var sw = new StringWriter();
        CliEnvelopeWriter.WriteComments(sw, new[]
        {
            new TicketComment("c1", "<p>first note</p>", new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero)),
        });

        using var doc = JsonDocument.Parse(sw.ToString());
        var data = doc.RootElement.GetProperty("data");
        var c0 = Assert.Single(data.EnumerateArray());
        Assert.Equal("c1", c0.GetProperty("id").GetString());
        // Plane HTML is rendered to plain text for the envelope.
        Assert.Equal("first note", c0.GetProperty("body").GetString());
        Assert.True(c0.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public void WriteCommentCreated_EmitsCommentId()
    {
        var sw = new StringWriter();
        CliEnvelopeWriter.WriteCommentCreated(sw, "comment-uuid-9");

        using var doc = JsonDocument.Parse(sw.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("comment-uuid-9", doc.RootElement.GetProperty("data").GetProperty("id").GetString());
    }

    [Fact]
    public void WriteTransition_EmitsIdAndStateName()
    {
        var sw = new StringWriter();
        CliEnvelopeWriter.WriteTransition(sw, "TLB-541", TicketState.InProgress);

        using var doc = JsonDocument.Parse(sw.ToString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("TLB-541", data.GetProperty("id").GetString());
        Assert.Equal("InProgress", data.GetProperty("state").GetString());
    }

    [Fact]
    public void WriteAck_EmitsIdAndAction()
    {
        var sw = new StringWriter();
        CliEnvelopeWriter.WriteAck(sw, "TLB-541", "close");

        using var doc = JsonDocument.Parse(sw.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("TLB-541", data.GetProperty("id").GetString());
        Assert.Equal("close", data.GetProperty("action").GetString());
    }
}
