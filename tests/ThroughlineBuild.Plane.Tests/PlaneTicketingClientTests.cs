using System.Net;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

// ---------------------------------------------------------------------------
// CapturedRequest: stores request data after HttpClient disposes the content
// ---------------------------------------------------------------------------
internal sealed class CapturedRequest
{
    public HttpMethod Method { get; }
    public Uri? RequestUri { get; }
    public System.Net.Http.Headers.HttpRequestHeaders Headers { get; }
    public string Body { get; }

    public CapturedRequest(HttpMethod method, Uri? uri, System.Net.Http.Headers.HttpRequestHeaders headers, string body)
    {
        Method = method;
        RequestUri = uri;
        Headers = headers;
        Body = body;
    }
}

// ---------------------------------------------------------------------------
// FakeMessageHandler: simple request/response queue
// ---------------------------------------------------------------------------
internal sealed class FakeMessageHandler : HttpMessageHandler
{
    private readonly Queue<(Func<HttpRequestMessage, bool> Predicate, HttpResponseMessage Response)> _responses = new();

    public void Enqueue(Func<HttpRequestMessage, bool> predicate, HttpResponseMessage response)
        => _responses.Enqueue((predicate, response));

    public void Enqueue(HttpResponseMessage response)
        => _responses.Enqueue((_ => true, response));

    public List<CapturedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer body before content gets disposed
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : string.Empty;

        Requests.Add(new CapturedRequest(request.Method, request.RequestUri, request.Headers, body));

        if (_responses.TryDequeue(out var entry) && entry.Predicate(request))
            return entry.Response;

        // Default: 404
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        };
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage OkJson(string json) => Json(json);
    public static HttpResponseMessage ErrorJson(int status, string body)
        => new((HttpStatusCode)status) { Content = new StringContent(body) };
}

// ---------------------------------------------------------------------------
// Shared test data
// ---------------------------------------------------------------------------
internal static class TestData
{
    public const string IssueUuid = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string StateUuid = "bbbbbbbb-0000-0000-0000-000000000001";
    public const string LabelUuid = "cccccccc-0000-0000-0000-000000000001";
    public const string CommentUuid = "dddddddd-0000-0000-0000-000000000001";

    public static string IssueListJson(string stateId = StateUuid, string descHtml = "<p>desc</p>") =>
        $$"""
        {
          "results": [
            {
              "id": "{{IssueUuid}}",
              "sequence_id": 24,
              "name": "plane-client",
              "description_html": "{{descHtml}}",
              "state": "{{stateId}}",
              "label_ids": [],
              "parent": null,
              "type": null
            }
          ]
        }
        """;

    public static string StateListJson() =>
        $$"""
        {
          "results": [
            { "id": "{{StateUuid}}", "name": "Backlog", "group": "backlog" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000002", "name": "In Progress", "group": "started" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000003", "name": "Done", "group": "completed" }
          ]
        }
        """;

    public static string LabelListJson() =>
        $$"""
        {
          "results": [
            { "id": "{{LabelUuid}}", "name": "Size: S" }
          ]
        }
        """;

    public static string RelationListJson() =>
        """
        {
          "results": [
            { "id": "eeeeeeee-0000-0000-0000-000000000001", "relation_type": "blocks", "related_issue": "ffffffff-0000-0000-0000-000000000001" }
          ]
        }
        """;

    public static string CommentJson() =>
        $$"""{"id":"{{CommentUuid}}","comment_html":"<p>hello</p>"}""";

    public static string PatchOkJson() =>
        $$"""{"id":"{{IssueUuid}}","sequence_id":24,"name":"plane-client","description_html":"<p>desc</p><p>appended</p>","state":"{{StateUuid}}","label_ids":[],"parent":null,"type":null}""";

    public static PlaneClientOptions Options() => new()
    {
        BaseUrl = "https://plane.example.com",
        ApiToken = "test-token",
        WorkspaceSlug = "my-workspace",
        ProjectId = "my-project"
    };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------
public class CapabilitiesTests
{
    [Fact]
    public void Capabilities_ReturnsExpectedFlags()
    {
        var handler = new FakeMessageHandler();
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var caps = client.Capabilities;

        Assert.True(caps.TypedRelations);
        Assert.True(caps.TypedLabels);
        Assert.True(caps.RichHtmlComments);
        Assert.True(caps.Attachments);
    }
}

public class GetAsyncTests
{
    [Fact]
    public async Task GetAsync_ReturnsTicketWithCorrectFields()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(TestData.IssueUuid, ticket.Id);
        Assert.Equal("plane-client", ticket.Title);
        Assert.Equal(TicketState.Backlog, ticket.State);
        Assert.Equal("<p>desc</p>", ticket.DescriptionHtml);
    }

    [Fact]
    public async Task GetAsync_ParsesSequenceIdWithoutPrefix()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("24", CancellationToken.None);

        Assert.Equal(TestData.IssueUuid, ticket.Id);
    }

    [Fact]
    public async Task GetAsync_ThrowsPlaneApiException_On404()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, "not found"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ex = await Assert.ThrowsAsync<PlaneApiException>(() => client.GetAsync("TLB-99", CancellationToken.None));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task GetAsync_SetsXApiKeyHeader()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.GetAsync("TLB-24", CancellationToken.None);

        var req = handler.Requests[0];
        Assert.True(req.Headers.Contains("X-API-Key"));
        Assert.Equal("test-token", req.Headers.GetValues("X-API-Key").First());
    }
}

public class GetBatchAsyncTests
{
    [Fact]
    public async Task GetBatchAsync_ReturnsManyTickets()
    {
        var handler = new FakeMessageHandler();
        // Two issues - each GetAsync call hits issues list + states
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        // Second GetAsync reuses cached states, no extra state request

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.GetBatchAsync(["TLB-24", "TLB-24"], CancellationToken.None);

        Assert.Equal(2, tickets.Count);
    }
}

public class TransitionAsyncTests
{
    [Fact]
    public async Task TransitionAsync_PatchesCorrectStateUuid()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson())); // state cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));   // PATCH

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.TransitionAsync("TLB-24", TicketState.Backlog, CancellationToken.None);

        var patchReq = handler.Requests[2];
        Assert.Equal(HttpMethod.Patch, patchReq.Method);
        Assert.Contains(TestData.StateUuid, patchReq.Body);
    }

    [Fact]
    public async Task TransitionAsync_ThrowsPlaneApiException_On500()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        // Polly will retry 3x on 5xx - we need enough responses
        // (On retry exhaustion it rethrows the last PlaneApiException)
        // Enqueue remaining retries
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));

        var ex = await Assert.ThrowsAsync<PlaneApiException>(
            () => client.TransitionAsync("TLB-24", TicketState.Done, CancellationToken.None));

        Assert.Equal(500, ex.Status);
    }
}

public class AppendDescriptionAsyncTests
{
    [Fact]
    public async Task AppendDescriptionAsync_CombinesExistingAndNew()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson(descHtml: "<p>existing</p>")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.AppendDescriptionAsync("TLB-24", "<p>new</p>", CancellationToken.None);

        var patchReq = handler.Requests[1];
        Assert.Contains("<p>existing</p>", patchReq.Body);
        Assert.Contains("<p>new</p>", patchReq.Body);
    }
}

public class CreateCommentAsyncTests
{
    [Fact]
    public async Task CreateCommentAsync_ReturnsCommentId()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.CommentJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var commentId = await client.CreateCommentAsync("TLB-24", "<p>hello</p>", CancellationToken.None);

        Assert.Equal(TestData.CommentUuid, commentId);
    }

    [Fact]
    public async Task CreateCommentAsync_PostsToCommentsEndpoint()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.CommentJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateCommentAsync("TLB-24", "<p>hello</p>", CancellationToken.None);

        var postReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.Contains("comments", postReq.RequestUri!.ToString());
    }
}

public class ApplyLabelsAsyncTests
{
    [Fact]
    public async Task ApplyLabelsAsync_ResolvesNamesToUuidsAndPatches()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson())); // label cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));   // PATCH

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.ApplyLabelsAsync("TLB-24", ["Size: S"], CancellationToken.None);

        var patchReq = handler.Requests[2];
        Assert.Contains(TestData.LabelUuid, patchReq.Body);
    }

    [Fact]
    public async Task ApplyLabelsAsync_ThrowsForUnknownLabel()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson())); // label cache

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ApplyLabelsAsync("TLB-24", ["Unknown Label"], CancellationToken.None));
    }
}

public class GetRelationsAsyncTests
{
    [Fact]
    public async Task GetRelationsAsync_ReturnsMappedRelations()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson())); // relations

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var relations = await client.GetRelationsAsync("TLB-24", CancellationToken.None);

        Assert.Single(relations);
        Assert.Equal("blocks", relations[0].Kind);
        Assert.Equal("ffffffff-0000-0000-0000-000000000001", relations[0].TargetId);
    }
}

public class PlaneApiExceptionTests
{
    [Fact]
    public void PlaneApiException_HasCorrectProperties()
    {
        var ex = new PlaneApiException(422, "unprocessable");
        Assert.Equal(422, ex.Status);
        Assert.Equal("unprocessable", ex.Body);
        Assert.Contains("422", ex.Message);
    }
}
