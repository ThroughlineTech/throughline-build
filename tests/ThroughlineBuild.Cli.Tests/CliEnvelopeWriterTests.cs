using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
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
        Assert.Empty(data.GetProperty("children").EnumerateArray());
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
        Assert.Empty(view.Children);
        var rel = Assert.Single(view.Relations);
        Assert.Equal("blocked_by", rel.Kind);
        Assert.Equal("TLB-540", rel.TargetId);
    }

    [Fact]
    public void ToView_MapsDirectChildren()
    {
        var view = CliEnvelopeWriter.ToView(SampleTicket(), new[]
        {
            new Ticket(
                Id: "TLB-542",
                Uuid: "child-uuid",
                Title: "child",
                Type: "Task",
                State: TicketState.Backlog,
                Size: Size.S,
                Risk: Risk.Low,
                DescriptionHtml: "",
                Relations: Array.Empty<Relation>(),
                Labels: Array.Empty<string>(),
                ParentId: "541")
        });

        var child = Assert.Single(view.Children);
        Assert.Equal("TLB-542", child.Id);
        Assert.Equal("child", child.Title);
        Assert.Equal(TicketState.Backlog, child.State);
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

    [Fact]
    public void WriteAttachments_UsesSourceGeneratedNormalizedEnvelope()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteAttachments(sw,
        [
            new TicketAttachment(
                "11111111-1111-1111-1111-111111111111",
                "description_inline_image",
                null,
                "image/png",
                null)
        ]);

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var item = root.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("description_inline_image", item.GetProperty("source").GetString());
        Assert.False(item.TryGetProperty("name", out _));
    }

    [Fact]
    public void WriteAttachmentDownload_PreservesRequestedPathAndByteCount()
    {
        var sw = new StringWriter();
        var attachment = new TicketAttachment(
            "11111111-1111-1111-1111-111111111111",
            "work_item_attachment",
            "evidence.bin",
            null,
            4);

        CliEnvelopeWriter.WriteAttachmentDownload(sw, attachment, "out/evidence.bin", 4);

        using var doc = JsonDocument.Parse(sw.ToString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("out/evidence.bin", data.GetProperty("path").GetString());
        Assert.Equal(4, data.GetProperty("bytesWritten").GetInt64());
    }

    [Fact]
    public void WriteWorktreeLease_UsesSourceGeneratedManifestEnvelope()
    {
        var manifest = new WorktreeLeaseManifest(
            1,
            "TLB-582",
            "safe",
            "lease/tlb-582-safe",
            "0123456789abcdef0123456789abcdef01234567",
            "C:\\repo",
            "C:\\repo",
            "C:\\repo\\.worktrees\\conductor",
            "C:\\repo\\.worktrees\\conductor\\tlb-582-safe",
            [".dev.vars"],
            [],
            new WorktreeInstallRecord("succeeded", 25));
        var sw = new StringWriter();

        CliEnvelopeWriter.WriteWorktreeLease(sw, manifest);

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        Assert.Equal(manifest.WorktreePath, data.GetProperty("path").GetString());
        Assert.Equal("TLB-582", data.GetProperty("manifest").GetProperty("ticket").GetString());
        Assert.Equal("succeeded", data.GetProperty("manifest").GetProperty("install").GetProperty("status").GetString());
    }
}
