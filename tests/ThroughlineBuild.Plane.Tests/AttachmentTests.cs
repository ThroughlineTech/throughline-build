using System.Net;
using System.Text;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

public sealed class AttachmentTests
{
    private const string NormalId = "11111111-1111-1111-1111-111111111111";
    private const string InlineId = "22222222-2222-2222-2222-222222222222";
    private const string UrlInlineId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task GetAttachments_EmptyPlaneListAndDescriptionReturnsSuccessEmptyList()
    {
        var api = new FakeMessageHandler();
        api.Enqueue(FakeMessageHandler.OkJson(IssueWithDescription(string.Empty)));
        api.Enqueue(FakeMessageHandler.OkJson("[]"));
        var client = new PlaneTicketingClient(new HttpClient(api), TestData.Options());

        var attachments = await client.GetAttachmentsAsync("TLB-24", CancellationToken.None);

        Assert.Empty(attachments);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task GetAttachments_NormalizesOrdersAndDeduplicatesSupportedSources()
    {
        var api = new FakeMessageHandler();
        api.Enqueue(FakeMessageHandler.OkJson(IssueWithDescription(
            $"<image-component src='{NormalId}'></image-component>" +
            $"<image-component src='https://external.example/assets/{UrlInlineId}'></image-component>" +
            $"<image-component src='{InlineId}'></image-component>" +
            $"<image-component src='https://plane.example.com/api/v1/workspaces/my-workspace/assets/{UrlInlineId}/'></image-component>")));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$$"""[{"asset":"{{{NormalId}}}","attributes":{"name":"spec.pdf","size":41,"type":"application/pdf"}}]"""));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$"""{"asset_id":"{{InlineId}}","asset_url":"https://storage.example/inline?secret=one","asset_name":"inline.png","asset_type":"image/png"}"""));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$"""{"asset_id":"{{UrlInlineId}}","asset_url":"https://storage.example/url-inline?secret=two","asset_name":"url.png","asset_type":"image/png"}"""));
        var client = new PlaneTicketingClient(new HttpClient(api), TestData.Options());

        var attachments = await client.GetAttachmentsAsync("TLB-24", CancellationToken.None);

        Assert.Collection(
            attachments,
            first =>
            {
                Assert.Equal(NormalId, first.Id);
                Assert.Equal("work_item_attachment", first.Source);
                Assert.Equal("spec.pdf", first.Name);
                Assert.Equal("application/pdf", first.ContentType);
                Assert.Equal(41, first.SizeBytes);
            },
            second =>
            {
                Assert.Equal(InlineId, second.Id);
                Assert.Equal("description_inline_image", second.Source);
            },
            third => Assert.Equal(UrlInlineId, third.Id));
        Assert.Equal(4, api.Requests.Count);
        Assert.All(api.Requests, request => Assert.Equal("test-token", request.Headers.GetValues("X-API-Key").Single()));
    }

    [Fact]
    public async Task DownloadAttachment_RejectsUnrelatedIdBeforeDetailOrStorage()
    {
        var api = new FakeMessageHandler();
        api.Enqueue(FakeMessageHandler.OkJson(IssueWithDescription(string.Empty)));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$$"""[{"asset":"{{{NormalId}}}","attributes":{"name":"spec.pdf"}}]"""));
        var detail = new FakeMessageHandler();
        var storage = new FakeMessageHandler();
        var client = new PlaneTicketingClient(
            new HttpClient(api), TestData.Options(), new HttpClient(detail), new HttpClient(storage));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.DownloadAttachmentAsync(
            "TLB-24", InlineId, CancellationToken.None));

        Assert.Empty(detail.Requests);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task DownloadAttachment_FollowsDetailRedirectWithoutForwardingPlaneAuthToStorage()
    {
        var api = new FakeMessageHandler();
        api.Enqueue(FakeMessageHandler.OkJson(IssueWithDescription(string.Empty)));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$$"""[{"asset":"{{{NormalId}}}","attributes":{"name":"spec.bin","size":4,"type":"application/octet-stream"}}]"""));
        var detail = new FakeMessageHandler();
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://storage.example/file?signature=secret");
        detail.Enqueue(redirect);
        var storage = new FakeMessageHandler();
        storage.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0, 1, 2, 255])
        });
        var client = new PlaneTicketingClient(
            new HttpClient(api), TestData.Options(), new HttpClient(detail), new HttpClient(storage));

        var downloaded = await client.DownloadAttachmentAsync("TLB-24", NormalId, CancellationToken.None);

        Assert.Equal(new byte[] { 0, 1, 2, 255 }, downloaded.Content);
        Assert.Equal("test-token", detail.Requests.Single().Headers.GetValues("X-API-Key").Single());
        Assert.False(storage.Requests.Single().Headers.Contains("X-API-Key"));
    }

    [Fact]
    public async Task DownloadAttachment_InlineUsesMetadataUrlAndSkipsDetail()
    {
        var api = new FakeMessageHandler();
        api.Enqueue(FakeMessageHandler.OkJson(IssueWithDescription(
            $"<image-component src='{InlineId}'></image-component>")));
        api.Enqueue(FakeMessageHandler.OkJson("[]"));
        api.Enqueue(FakeMessageHandler.OkJson(
            $$"""{"asset_id":"{{InlineId}}","asset_url":"https://storage.example/inline?signature=secret","asset_name":"inline.png","asset_type":"image/png"}"""));
        var detail = new FakeMessageHandler();
        var storage = new FakeMessageHandler();
        storage.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([7, 8, 9])
        });
        var client = new PlaneTicketingClient(
            new HttpClient(api), TestData.Options(), new HttpClient(detail), new HttpClient(storage));

        var downloaded = await client.DownloadAttachmentAsync("24", InlineId, CancellationToken.None);

        Assert.Equal(new byte[] { 7, 8, 9 }, downloaded.Content);
        Assert.Empty(detail.Requests);
        Assert.False(storage.Requests.Single().Headers.Contains("X-API-Key"));
    }

    private static string IssueWithDescription(string description) =>
        $$"""
        {
          "results": [{
            "id": "{{TestData.IssueUuid}}",
            "sequence_id": 24,
            "name": "attachments",
            "description_html": "{{description}}",
            "state": "{{TestData.StateUuid}}",
            "labels": [],
            "parent": null,
            "type": null
          }]
        }
        """;
}
