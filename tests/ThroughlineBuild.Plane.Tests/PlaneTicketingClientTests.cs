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
    public const string LabelUuid2 = "cccccccc-0000-0000-0000-000000000002";
    public const string CommentUuid = "dddddddd-0000-0000-0000-000000000001";
    public const string IssueTypeUuid = "eeeeeeee-1111-0000-0000-000000000001";

    public static string IssueListJson(string stateId = StateUuid, string descHtml = "<p>desc</p>", string labelIdsJson = "[]") =>
        $$"""
        {
          "results": [
            {
              "id": "{{IssueUuid}}",
              "sequence_id": 24,
              "name": "plane-client",
              "description_html": "{{descHtml}}",
              "state": "{{stateId}}",
              "label_ids": {{labelIdsJson}},
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
            { "id": "{{LabelUuid}}", "name": "Size: S" },
            { "id": "{{LabelUuid2}}", "name": "risk:low" }
          ]
        }
        """;

    public static string IssueTypeListJson() =>
        $$"""
        {
          "results": [
            { "id": "{{IssueTypeUuid}}", "name": "Task" },
            { "id": "eeeeeeee-1111-0000-0000-000000000002", "name": "Bug" }
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
        ProjectId = "my-project",
        ProjectIdentifier = "TLB"
    };

    // Zero-delay retry config so retry-path tests don't sleep on real wall-clock backoff.
    public static PlaneClientOptions FastRetryOptions(int maxRetryAttempts = 3) => new()
    {
        BaseUrl = "https://plane.example.com",
        ApiToken = "test-token",
        WorkspaceSlug = "my-workspace",
        ProjectId = "my-project",
        ProjectIdentifier = "TLB",
        MaxRetryAttempts = maxRetryAttempts,
        RetryBaseDelay = TimeSpan.Zero
    };

    // 429 response carrying a Retry-After: 0 hint (instant retry, exercises the parse path).
    public static HttpResponseMessage RateLimited(string body = "{\"error_code\":5900,\"error_message\":\"RATE_LIMIT_EXCEEDED\"}")
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body)
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }
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
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal("TLB-24", ticket.Id);
        Assert.Equal(TestData.IssueUuid, ticket.Uuid);
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
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("24", CancellationToken.None);

        Assert.Equal("TLB-24", ticket.Id);
        Assert.Equal(TestData.IssueUuid, ticket.Uuid);
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
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.GetAsync("TLB-24", CancellationToken.None);

        var req = handler.Requests[0];
        Assert.True(req.Headers.Contains("X-API-Key"));
        Assert.Equal("test-token", req.Headers.GetValues("X-API-Key").First());
    }
}

public class GetAsyncLabelResolutionTests
{
    [Fact]
    public async Task GetAsync_ResolvesLabelUuidsToNames()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            TestData.IssueListJson(labelIdsJson: $"[\"{TestData.LabelUuid}\"]")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Single(ticket.Labels);
        Assert.Contains("Size: S", ticket.Labels);
    }

    [Fact]
    public async Task GetAsync_FiltersOrphanLabelUuids()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            TestData.IssueListJson(labelIdsJson: "[\"ffffffff-0000-0000-0000-999999999999\"]")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Empty(ticket.Labels);
    }
}

public class GetBatchAsyncTests
{
    [Fact]
    public async Task GetBatchAsync_ReturnsManyTickets()
    {
        var handler = new FakeMessageHandler();
        // Two issues - each GetAsync call hits issues list + states + labels
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        // Second GetAsync reuses cached states and labels, no extra requests

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

        // FastRetryOptions defaults to 3 retries (4 attempts total) with zero backoff delay.
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.FastRetryOptions());
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

public class RateLimitRetryTests
{
    [Fact]
    public async Task Transition_RetriesAfter429_ThenSucceeds()
    {
        var handler = new FakeMessageHandler();
        // First GET issue list is rate-limited; the pipeline retries the whole delegate.
        handler.Enqueue(TestData.RateLimited());
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // retry: issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson())); // state cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));   // PATCH

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.FastRetryOptions());

        // Must not throw - the 429 is absorbed and the retry completes the transition.
        await client.TransitionAsync("TLB-24", TicketState.Backlog, CancellationToken.None);

        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Single(patchReqs);
    }

    [Fact]
    public async Task Transition_429RetriesExhausted_ThrowsWithRetryAfterParsed()
    {
        var handler = new FakeMessageHandler();
        // maxRetryAttempts: 2 -> 3 total attempts, all rate-limited.
        handler.Enqueue(TestData.RateLimited());
        handler.Enqueue(TestData.RateLimited());
        handler.Enqueue(TestData.RateLimited());

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.FastRetryOptions(maxRetryAttempts: 2));

        var ex = await Assert.ThrowsAsync<PlaneApiException>(
            () => client.TransitionAsync("TLB-24", TicketState.Done, CancellationToken.None));

        Assert.Equal(429, ex.Status);
        // Retry-After: 0 header is parsed onto the exception (zero, not null).
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
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

public class GetCommentsAsyncTests
{
    private const string CommentUuid1 = "dddddddd-0000-0000-0000-000000000010";
    private const string CommentUuid2 = "dddddddd-0000-0000-0000-000000000011";

    private static string CommentsListJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{CommentUuid1}}",
              "comment_html": "<p>first comment</p>",
              "created_at": "2025-01-01T00:00:00Z"
            },
            {
              "id": "{{CommentUuid2}}",
              "comment_html": "<p><strong>deferred:</strong> later</p>",
              "created_at": "2025-01-02T00:00:00Z"
            }
          ]
        }
        """;

    private static string EmptyCommentsListJson() =>
        """{"results":[]}""";

    [Fact]
    public async Task GetCommentsAsync_ReturnsMappedComments()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(CommentsListJson()));        // comments

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var comments = await client.GetCommentsAsync("TLB-24", CancellationToken.None);

        Assert.Equal(2, comments.Count);
        Assert.Equal(CommentUuid1, comments[0].Id);
        Assert.Equal("<p>first comment</p>", comments[0].Body);
        Assert.Equal(CommentUuid2, comments[1].Id);
        Assert.Contains("deferred:", comments[1].Body);

        var getReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, getReq.Method);
        Assert.Contains("comments", getReq.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetCommentsAsync_ReturnsEmptyOnEmptyResults()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(EmptyCommentsListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var comments = await client.GetCommentsAsync("TLB-24", CancellationToken.None);

        Assert.Empty(comments);
    }
}

public class ApplyLabelsAsyncTests
{
    [Fact]
    public async Task ApplyLabelsAsync_ResolvesNamesToUuidsAndPatches()
    {
        // Issue has a prior label UUID; set semantics means PATCH body has only the applied label
        var priorLabelUuid = "eeeeeeee-0000-0000-0000-000000000001";
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            TestData.IssueListJson(labelIdsJson: $"[\"{priorLabelUuid}\"]"))); // issue with prior label
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson())); // label cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));   // PATCH

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.ApplyLabelsAsync("TLB-24", ["Size: S"], CancellationToken.None);

        var patchReq = handler.Requests[2];
        Assert.Contains(TestData.LabelUuid, patchReq.Body);
        Assert.DoesNotContain(priorLabelUuid, patchReq.Body);
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

    [Fact]
    public async Task GetRelationsAsync_NullResultsField_ReturnsEmpty()
    {
        // Plane API may return {"results": null} - must not throw ArgumentNullException
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson("""{"results": null}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var relations = await client.GetRelationsAsync("TLB-24", CancellationToken.None);

        Assert.Empty(relations);
    }
}

public class TicketSizeResolutionTests
{
    public const string SizeLLabelUuid = "cccccccc-0000-0000-0000-000000000010";

    private static string LabelListWithSizeLJson() =>
        $$"""
        {
          "results": [
            { "id": "{{SizeLLabelUuid}}", "name": "size:l" }
          ]
        }
        """;

    private static string LabelListNoSizeJson() =>
        """
        {
          "results": [
            { "id": "cccccccc-0000-0000-0000-000000000020", "name": "risk:low" }
          ]
        }
        """;

    [Fact]
    public async Task GetAsync_SizeLLabel_ReturnsTicketWithSizeL()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            TestData.IssueListJson(labelIdsJson: $"[\"{SizeLLabelUuid}\"]")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(LabelListWithSizeLJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(Size.L, ticket.Size);
    }

    [Fact]
    public async Task GetAsync_NoSizeLabel_FallsBackToSizeM()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson(labelIdsJson: "[]")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(LabelListNoSizeJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(Size.M, ticket.Size);
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
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public void PlaneApiException_CarriesRetryAfter()
    {
        var ex = new PlaneApiException(429, "rate limited", TimeSpan.FromSeconds(30));
        Assert.Equal(429, ex.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }
}

// ---------------------------------------------------------------------------
// RollupParentAsync test data helpers
// ---------------------------------------------------------------------------
internal static class RollupTestData
{
    public const string ParentUuid = "pppppppp-0000-0000-0000-000000000001";
    public const string ChildUuid = "aaaaaaaa-0000-0000-0000-000000000002";
    public const string InProgressStateUuid = "bbbbbbbb-0000-0000-0000-000000000002";

    // Child issue list with parent set
    public static string ChildIssueListJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "name": "child-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{InProgressStateUuid}}",
              "label_ids": [],
              "parent": "{{ParentUuid}}",
              "type": null
            }
          ]
        }
        """;

    // State list (same as TestData but with In Review added)
    public static string FullStateListJson() =>
        $$"""
        {
          "results": [
            { "id": "{{TestData.StateUuid}}", "name": "Backlog", "group": "backlog" },
            { "id": "{{InProgressStateUuid}}", "name": "In Progress", "group": "started" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000003", "name": "Done", "group": "completed" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000004", "name": "In Review", "group": "unstarted" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000005", "name": "Cancelled", "group": "cancelled" }
          ]
        }
        """;

    // Parent with expand=state returning state as object (Backlog)
    public static string ParentExpandedJson() =>
        $$"""
        {
          "id": "{{ParentUuid}}",
          "sequence_id": 10,
          "parent": null,
          "state": { "id": "{{TestData.StateUuid}}", "name": "Backlog" }
        }
        """;

    // Siblings list with one child in "In Progress"
    public static string SiblingsInProgressJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "parent": "{{ParentUuid}}",
              "state": { "id": "{{InProgressStateUuid}}", "name": "In Progress" }
            }
          ]
        }
        """;

    public static string PatchParentOkJson() =>
        $$"""{"id":"{{ParentUuid}}","sequence_id":10,"name":"parent-ticket","description_html":"<p>desc</p>","state":"{{InProgressStateUuid}}","label_ids":[],"parent":null,"type":null}""";

    public static string CommentOkJson() =>
        """{"id":"dddddddd-0000-0000-0000-000000000002","comment_html":"<p>[rollup]</p>"}""";
}

// ---------------------------------------------------------------------------
// RollupParentAsync tests
// ---------------------------------------------------------------------------
public class RollupParentAsyncTests
{
    [Fact]
    public async Task HappyPath_ParentTransitioned()
    {
        var handler = new FakeMessageHandler();
        // 1. issues list (child with parent set)
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ChildIssueListJson()));
        // 2. states cache
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.FullStateListJson()));
        // 3. parent expanded (state = Backlog object)
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ParentExpandedJson()));
        // 4. siblings list (one child In Progress)
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.SiblingsInProgressJson()));
        // 5. PATCH parent state
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));
        // 6. POST comment
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.RollupParentAsync("TLB-25", CancellationToken.None);

        Assert.True(result.ParentTransitioned);
        Assert.Equal("In Progress", result.NewParentState);
        Assert.Null(result.FailureReason);

        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.NotEmpty(patchReqs);
    }

    [Fact]
    public async Task NoParent_ReturnsNoOp()
    {
        var handler = new FakeMessageHandler();
        // child issue has parent=null (TestData.IssueListJson default)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.RollupParentAsync("TLB-24", CancellationToken.None);

        Assert.False(result.ParentTransitioned);
        Assert.Null(result.NewParentState);
        Assert.Null(result.FailureReason);

        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Empty(patchReqs);
    }

    [Fact]
    public async Task ApiError_DoesNotThrow()
    {
        var handler = new FakeMessageHandler();
        // first GET (resolve child issues list) returns 500; enqueue extras for any Polly retries
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "internal server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "internal server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "internal server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "internal server error"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        // must not throw
        var result = await client.RollupParentAsync("TLB-24", CancellationToken.None);

        Assert.False(result.ParentTransitioned);
        Assert.NotNull(result.FailureReason);
        Assert.NotEmpty(result.FailureReason);
    }
}

// ---------------------------------------------------------------------------
// CreateChildTicketsAsync tests
// ---------------------------------------------------------------------------
internal static class CreateChildTestData
{
    public const string ParentUuid = "11111111-0000-0000-0000-000000000001";
    public const string Child1Uuid = "22222222-0000-0000-0000-000000000001";
    public const string Child2Uuid = "22222222-0000-0000-0000-000000000002";
    public const string SizeSLabelUuid = "33333333-0000-0000-0000-000000000001";

    public static string LabelListJson() =>
        $$"""
        {
          "results": [
            { "id": "{{SizeSLabelUuid}}", "name": "size:s" }
          ]
        }
        """;

    public static string CreateIssueResponseJson(string uuid, int seqId) =>
        $$"""{"id":"{{uuid}}","sequence_id":{{seqId}},"created_at":"2025-01-01T00:00:00Z"}""";

    public static IReadOnlyList<ChildTicketSpec> TwoChildren() =>
        new[]
        {
            new ChildTicketSpec("Child A", "<p>desc A</p>", new[] { "size:s" }),
            new ChildTicketSpec("Child B", "<p>desc B</p>", Array.Empty<string>())
        };
}

public class CreateChildTicketsAsyncTests
{
    [Fact]
    public async Task HappyPath_TwoChildren_ReturnsBothCreated()
    {
        var handler = new FakeMessageHandler();
        // label cache
        handler.Enqueue(FakeMessageHandler.OkJson(CreateChildTestData.LabelListJson()));
        // POST child A
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child1Uuid, 100)));
        // POST child B
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child2Uuid, 101)));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.CreateChildTicketsAsync(
            CreateChildTestData.ParentUuid,
            CreateChildTestData.TwoChildren(),
            CancellationToken.None);

        Assert.Equal(2, result.Created.Count);
        Assert.Empty(result.Failures);
        Assert.Equal("TLB-100", result.Created[0].Id);
        Assert.Equal(CreateChildTestData.Child1Uuid, result.Created[0].Uuid);
        Assert.Equal("TLB-101", result.Created[1].Id);
        Assert.Equal(CreateChildTestData.Child2Uuid, result.Created[1].Uuid);
    }

    [Fact]
    public async Task HappyPath_ParentFieldInPostBody()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateChildTestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child1Uuid, 100)));
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child2Uuid, 101)));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateChildTicketsAsync(
            CreateChildTestData.ParentUuid,
            CreateChildTestData.TwoChildren(),
            CancellationToken.None);

        // Both POST requests should contain the parent UUID
        var postRequests = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, postRequests.Count);
        foreach (var req in postRequests)
            Assert.Contains(CreateChildTestData.ParentUuid, req.Body);
    }

    [Fact]
    public async Task PartialFailure_SecondPostReturns422_FirstCreatedNoThrow()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateChildTestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child1Uuid, 100)));
        handler.Enqueue(FakeMessageHandler.ErrorJson(422, "unprocessable entity"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.CreateChildTicketsAsync(
            CreateChildTestData.ParentUuid,
            CreateChildTestData.TwoChildren(),
            CancellationToken.None);

        Assert.Single(result.Created);
        Assert.Single(result.Failures);
        Assert.Equal("TLB-100", result.Created[0].Id);
        Assert.Contains("Child B", result.Failures[0]);
    }

    [Fact]
    public async Task AllFail_ReturnsEmptyCreatedNoThrow()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateChildTestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));
        handler.Enqueue(FakeMessageHandler.ErrorJson(500, "server error"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.CreateChildTicketsAsync(
            CreateChildTestData.ParentUuid,
            CreateChildTestData.TwoChildren(),
            CancellationToken.None);

        Assert.Empty(result.Created);
        Assert.Equal(2, result.Failures.Count);
    }
}

// ---------------------------------------------------------------------------
// Issue cache tests
// ---------------------------------------------------------------------------
public class IssueCacheTests
{
    [Fact]
    public async Task FindIssueAsync_CachesResult_SecondOperationSkipsIssueListFetch()
    {
        var handler = new FakeMessageHandler();
        // GetAsync: issue list + states + labels
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        // CreateCommentAsync: cached issue - only the POST comment needed
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.CommentJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.GetAsync("TLB-24", CancellationToken.None);
        await client.CreateCommentAsync("TLB-24", "<p>cached</p>", CancellationToken.None);

        // Only one GET to the issues list endpoint across both operations
        var issueListGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get
                && r.RequestUri!.ToString().Contains("per_page=100"))
            .ToList();
        Assert.Single(issueListGets);
    }

    [Fact]
    public async Task TransitionAsync_WriteThrough_LaterReadSeesNewState_WithoutRefetch()
    {
        // Regression for the stale-state bug behind StoppedAtImplement: the issue cache was
        // frozen at first fetch, so after plan transitioned Backlog -> Ready, implement's
        // GetAsync still saw Backlog and failed its state guard. Write-through must make the
        // post-transition read reflect the new state without paginating the project again.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));   // snapshot (Backlog)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));   // states cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));   // labels cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));     // PATCH transition

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var before = await client.GetAsync("TLB-24", CancellationToken.None);
        Assert.Equal(TicketState.Backlog, before.State);

        await client.TransitionAsync("TLB-24", TicketState.InProgress, CancellationToken.None);

        var after = await client.GetAsync("TLB-24", CancellationToken.None);
        Assert.Equal(TicketState.InProgress, after.State);

        // The whole sequence paginated the project exactly once.
        var issueListGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get
                && r.RequestUri!.ToString().Contains("per_page=100"))
            .ToList();
        Assert.Single(issueListGets);
    }
}
