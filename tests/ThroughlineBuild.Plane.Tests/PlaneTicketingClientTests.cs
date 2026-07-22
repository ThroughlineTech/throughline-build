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
// RoutingOkHandler: thread-safe handler that routes by URL/method and always 200s.
// Unlike FakeMessageHandler (ordered, non-thread-safe queue) this can serve many
// concurrent requests, so it can exercise the client's concurrency paths.
// ---------------------------------------------------------------------------
internal sealed class RoutingOkHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string> _bodyFor;
    private readonly object _lock = new();

    public RoutingOkHandler(Func<HttpRequestMessage, string> bodyFor) => _bodyFor = bodyFor;

    // Thread-safe request log (method + uri) for assertions under concurrency.
    public List<(HttpMethod Method, string Uri)> Log { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (_lock)
            Log.Add((request.Method, request.RequestUri!.ToString()));
        var json = _bodyFor(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
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
              "labels": {{labelIdsJson}},
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
            { "id": "eeeeeeee-0000-0000-0000-000000000001", "relation_type": "blocks",
              "related_issue": { "id": "ffffffff-0000-0000-0000-000000000001", "sequence_id": 999, "name": "related" } }
          ]
        }
        """;

    public static string CommentJson() =>
        $$"""{"id":"{{CommentUuid}}","comment_html":"<p>hello</p>"}""";

    public static string PatchOkJson() =>
        $$"""{"id":"{{IssueUuid}}","sequence_id":24,"name":"plane-client","description_html":"<p>desc</p><p>appended</p>","state":"{{StateUuid}}","labels":[],"parent":null,"type":null}""";

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
    public async Task GetAsync_EmptyProjectIdentifier_ReturnsSequenceIdWithoutLeadingDash()
    {
        // When plane_project_identifier is not configured (empty string) the naive format
        // string "{ProjectIdentifier}-{SequenceId}" would produce "-24"; the FormatTicketId
        // helper must return "24" instead so display IDs match branch slugs.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var options = new PlaneClientOptions
        {
            BaseUrl = "https://plane.example.com",
            ApiToken = "test-token",
            WorkspaceSlug = "my-workspace",
            ProjectId = "my-project",
            ProjectIdentifier = ""
        };
        var client = new PlaneTicketingClient(new HttpClient(handler), options);
        var ticket = await client.GetAsync("24", CancellationToken.None);

        Assert.Equal("24", ticket.Id);
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
    public async Task Snapshot404_TranslatesToActionableProjectNotFoundMessage()
    {
        // The project's issues route 404s (wrong/unset project). Every read/write verb hits this via
        // the snapshot, so the client rewrites the message to name the workspace/project and the fix,
        // while preserving the raw status/body for programmatic callers.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, "Page not found."));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var ex = await Assert.ThrowsAsync<PlaneApiException>(() => client.GetAsync("TLB-99", CancellationToken.None));

        Assert.Equal(404, ex.Status);                              // raw status preserved
        Assert.Equal("Page not found.", ex.Body);                  // raw body preserved
        Assert.Contains("does not resolve to a project", ex.Message);
        Assert.Contains("my-project", ex.Message);                 // names the configured project
        Assert.Contains("my-workspace", ex.Message);               // and workspace
    }

    [Fact]
    public async Task QueryByType_WhenIssueTypesFeatureDisabled_TranslatesTo404FeatureMessage()
    {
        // --type resolves the type name via the issue-types endpoint, a Plane feature that is not
        // enabled on every deployment (it 404s when off). The snapshot loads fine first, so the 404
        // here must read as "feature not enabled", not a wrong project id or a bad type name.
        var handler = new FakeMessageHandler();
        handler.Enqueue(req => req.RequestUri!.AbsoluteUri.Contains("/issues/"),
            FakeMessageHandler.OkJson("""{"results":[]}"""));
        handler.Enqueue(req => req.RequestUri!.AbsoluteUri.Contains("issue-types"),
            FakeMessageHandler.ErrorJson(404, "Page not found."));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var ex = await Assert.ThrowsAsync<PlaneApiException>(
            () => client.QueryAsync(new TicketQuery(Type: "Feature"), CancellationToken.None));

        Assert.Equal(404, ex.Status);
        Assert.Contains("issue-types feature", ex.Message);
        Assert.Contains("--type", ex.Message);
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

    [Fact]
    public async Task TransitionAsync_ToDone_RollsUpParentImmediately()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            RollupTestData.ChildAndParentIssueListJson(childStateUuid: TestData.StateUuid))); // snapshot
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.FullStateListJson()));       // state cache
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));                   // child PATCH Done
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ParentExpandedJson()));      // parent expanded
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.SiblingsDoneJson()));        // all children Done
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));       // parent PATCH Done
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));           // rollup comment

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.TransitionAsync("TLB-25", TicketState.Done, CancellationToken.None);

        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patchReqs.Count);
        Assert.Contains(RollupTestData.DoneStateUuid, patchReqs[0].Body);
        Assert.Contains(RollupTestData.DoneStateUuid, patchReqs[1].Body);
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

    [Fact]
    public async Task AppendDescriptionAsync_CachesServerStoredDescription_NotOptimisticConcat()
    {
        // Plane may normalize the HTML it stores. The write-through must cache what the PATCH
        // response returned (so a later read / further append builds on the canonical value),
        // not the value we optimistically sent.
        var serverNormalized =
            $$"""{"id":"{{TestData.IssueUuid}}","sequence_id":24,"name":"plane-client","description_html":"<p>A</p><p>B-normalized</p>","state":"{{TestData.StateUuid}}","labels":[],"parent":null,"type":null}""";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson(descHtml: "<p>A</p>"))); // snapshot
        handler.Enqueue(FakeMessageHandler.OkJson(serverNormalized));                             // PATCH response
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));                     // GetAsync ToTicket states
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));                     // GetAsync ToTicket labels

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.AppendDescriptionAsync("TLB-24", "<p>B</p>", CancellationToken.None);

        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);
        Assert.Equal("<p>A</p><p>B-normalized</p>", ticket.DescriptionHtml); // server value, not "<p>A</p><p>B</p>"
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
        Assert.Contains("\"labels\"", patchReq.Body);
        Assert.Contains(TestData.LabelUuid, patchReq.Body);
        Assert.DoesNotContain(priorLabelUuid, patchReq.Body);
    }

    [Fact]
    public async Task ApplyLabelsAsync_ThenGetAsync_RoundTripsSizeRiskAndLabels()
    {
        const string sizeLUuid = "cccccccc-0000-0000-0000-000000000010";
        const string riskLowUuid = "cccccccc-0000-0000-0000-000000000020";
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson($$"""
            {
              "results": [
                { "id": "{{sizeLUuid}}", "name": "size:l" },
                { "id": "{{riskLowUuid}}", "name": "risk:low" }
              ]
            }
            """));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.ApplyLabelsAsync("TLB-24", ["size:l", "risk:low"], CancellationToken.None);
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(Size.L, ticket.Size);
        Assert.Equal(Risk.Low, ticket.Risk);
        Assert.Equal(new[] { "size:l", "risk:low" }, ticket.Labels);
    }

    [Fact]
    public async Task ApplyLabelsAsync_ThrowsForUnknownLabel()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson())); // label cache

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ApplyLabelsAsync("TLB-24", ["Unknown Label"], CancellationToken.None));
    }

    [Fact]
    public async Task ValidateLabelsAsync_unknown_label_fails_without_issue_lookup_or_patch()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.ValidateLabelsAsync(["Unknown Label"], CancellationToken.None));

        Assert.Contains("Label 'Unknown Label' not found", exception.Message);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }
}

public class AmendMetadataAsyncTests
{
    [Fact]
    public async Task UpdateTitleAsync_sends_exact_patch_and_writes_through_cache()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.UpdateTitleAsync("TLB-24", "New title", CancellationToken.None);
        var after = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal("{\"name\":\"New title\"}", handler.Requests[1].Body);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("New title", after.Title);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Get
            && r.RequestUri!.ToString().Contains("per_page=100")));
    }

    [Fact]
    public async Task UpdatePriorityAsync_sends_exact_normalized_priority_patch()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.UpdatePriorityAsync("TLB-24", "high", CancellationToken.None);

        Assert.Equal("{\"priority\":\"high\"}", handler.Requests[1].Body);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
    }

    [Fact]
    public async Task UpdateTypeAsync_resolves_name_sends_exact_uuid_patch_and_writes_through_cache()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.UpdateTypeAsync("TLB-24", "task", CancellationToken.None);
        var after = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal($"{{\"type\":\"{TestData.IssueTypeUuid}\"}}", handler.Requests[2].Body);
        Assert.Equal(TestData.IssueTypeUuid, after.Type);
    }

    [Fact]
    public async Task UpdateTypeAsync_rejects_unknown_type_without_patch()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.UpdateTypeAsync("TLB-24", "Unknown", CancellationToken.None));

        Assert.Contains("Issue type 'Unknown' not found", exception.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ValidateTypeAsync_unknown_type_fails_without_issue_lookup_or_patch()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.ValidateTypeAsync("Unknown", CancellationToken.None));

        Assert.Contains("Issue type 'Unknown' not found", exception.Message);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SetParentAsync_sends_exact_uuid_patch_and_writes_through_cache()
    {
        const string parentUuid = "ffffffff-0000-0000-0000-000000000009";
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.GetAsync("TLB-24", CancellationToken.None);
        await client.SetParentAsync(TestData.IssueUuid, parentUuid, CancellationToken.None);
        var children = await client.QueryAsync(new TicketQuery(ParentId: parentUuid), CancellationToken.None);

        Assert.Equal($"{{\"parent\":\"{parentUuid}\"}}", handler.Requests[3].Body);
        Assert.Equal("TLB-24", Assert.Single(children).Id);
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
        // TargetId is the canonical display id (FormatTicketId of the nested sequence_id),
        // not the related issue's raw UUID - that is the form the chain graph keys on.
        Assert.Equal("TLB-999", relations[0].TargetId);
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

    [Fact]
    public async Task GetRelationsAsync_EndpointReturns404_DegradesToEmpty()
    {
        // Regression: the relations lookup is an optional dependency-graph read. A 404 from the
        // endpoint must degrade to "no relations", never bubble out and abort a `build chain` run.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue lookup
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, """{"error": "Page not found."}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var relations = await client.GetRelationsAsync("TLB-24", CancellationToken.None);

        Assert.Empty(relations);
    }

    [Fact]
    public async Task GetRelationsAsync_RepeatedCall_UsesPerRunCache()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // issue snapshot
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson())); // first relations fetch

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var first = await client.GetRelationsAsync("TLB-24", CancellationToken.None);
        var second = await client.GetRelationsAsync("TLB-24", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);

        var relationGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get
                && r.RequestUri!.ToString().Contains("/issue-relation/"))
            .ToList();
        Assert.Single(relationGets);
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

public class TicketRiskResolutionTests
{
    private const string RiskLabelUuid = "cccccccc-0000-0000-0000-000000000030";

    [Theory]
    [InlineData("risk:low", Risk.Low)]
    [InlineData("RISK:HIGH", Risk.High)]
    [InlineData("risk:medium", Risk.Medium)]
    public async Task GetAsync_RiskLabel_ReturnsMatchingTicketRisk(string labelName, Risk expected)
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            TestData.IssueListJson(labelIdsJson: $"[\"{RiskLabelUuid}\"]")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson($$"""
            { "results": [{ "id": "{{RiskLabelUuid}}", "name": "{{labelName}}" }] }
            """));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(expected, ticket.Risk);
        Assert.Equal(labelName, Assert.Single(ticket.Labels));
    }

    [Fact]
    public async Task GetAsync_NoRiskLabel_FallsBackToRiskMedium()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal(Risk.Medium, ticket.Risk);
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
    public const string GrandparentUuid = "gggggggg-0000-0000-0000-000000000001";
    public const string ChildUuid = "aaaaaaaa-0000-0000-0000-000000000002";
    public const string InProgressStateUuid = "bbbbbbbb-0000-0000-0000-000000000002";
    public const string DoneStateUuid = "bbbbbbbb-0000-0000-0000-000000000003";
    public const string CancelledStateUuid = "bbbbbbbb-0000-0000-0000-000000000005";

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
              "labels": [],
              "parent": "{{ParentUuid}}",
              "type": null
            }
          ]
        }
        """;

    public static string ChildAndParentIssueListJson(string childStateUuid = InProgressStateUuid, string? parentOfParentUuid = null) =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "name": "child-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{childStateUuid}}",
              "labels": [],
              "parent": "{{ParentUuid}}",
              "type": null
            },
            {
              "id": "{{ParentUuid}}",
              "sequence_id": 10,
              "name": "parent-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{TestData.StateUuid}}",
              "labels": [],
              "parent": {{(parentOfParentUuid is null ? "null" : $"\"{parentOfParentUuid}\"")}},
              "type": null
            }
          ]
        }
        """;

    public static string ChildParentAndGrandparentIssueListJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "name": "child-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{DoneStateUuid}}",
              "labels": [],
              "parent": "{{ParentUuid}}",
              "type": null
            },
            {
              "id": "{{ParentUuid}}",
              "sequence_id": 10,
              "name": "parent-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{TestData.StateUuid}}",
              "labels": [],
              "parent": "{{GrandparentUuid}}",
              "type": null
            },
            {
              "id": "{{GrandparentUuid}}",
              "sequence_id": 1,
              "name": "grandparent-ticket",
              "description_html": "<p>desc</p>",
              "state": "{{TestData.StateUuid}}",
              "labels": [],
              "parent": null,
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
            { "id": "{{DoneStateUuid}}", "name": "Done", "group": "completed" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000004", "name": "In Review", "group": "unstarted" },
            { "id": "{{CancelledStateUuid}}", "name": "Cancelled", "group": "cancelled" }
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

    public static string GrandparentExpandedJson() =>
        $$"""
        {
          "id": "{{GrandparentUuid}}",
          "sequence_id": 1,
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

    public static string SiblingsDoneJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "parent": "{{ParentUuid}}",
              "state": { "id": "{{DoneStateUuid}}", "name": "Done" }
            }
          ]
        }
        """;

    public static string GrandparentSiblingsDoneJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ParentUuid}}",
              "sequence_id": 10,
              "parent": "{{GrandparentUuid}}",
              "state": { "id": "{{DoneStateUuid}}", "name": "Done" }
            }
          ]
        }
        """;

    public static string SiblingsDoneAndCancelledJson() =>
        $$"""
        {
          "results": [
            {
              "id": "{{ChildUuid}}",
              "sequence_id": 25,
              "parent": "{{ParentUuid}}",
              "state": { "id": "{{DoneStateUuid}}", "name": "Done" }
            },
            {
              "id": "aaaaaaaa-0000-0000-0000-000000000003",
              "sequence_id": 26,
              "parent": "{{ParentUuid}}",
              "state": { "id": "{{CancelledStateUuid}}", "name": "Cancelled" }
            }
          ]
        }
        """;

    public static string PatchParentOkJson() =>
        $$"""{"id":"{{ParentUuid}}","sequence_id":10,"name":"parent-ticket","description_html":"<p>desc</p>","state":"{{InProgressStateUuid}}","labels":[],"parent":null,"type":null}""";

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
    public async Task AllDirectChildrenDone_TransitionsParentToDone()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            RollupTestData.ChildAndParentIssueListJson(childStateUuid: RollupTestData.DoneStateUuid)));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.FullStateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ParentExpandedJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.SiblingsDoneJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.RollupParentAsync("TLB-25", CancellationToken.None);

        Assert.True(result.ParentTransitioned);
        Assert.Equal("Done", result.NewParentState);
        Assert.Null(result.FailureReason);

        var patchReq = Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Patch));
        Assert.Contains(RollupTestData.DoneStateUuid, patchReq.Body);
    }

    [Fact]
    public async Task CancelledSibling_BlocksParentDoneCompletion()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            RollupTestData.ChildAndParentIssueListJson(childStateUuid: RollupTestData.DoneStateUuid)));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.FullStateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ParentExpandedJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.SiblingsDoneAndCancelledJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.RollupParentAsync("TLB-25", CancellationToken.None);

        Assert.True(result.ParentTransitioned);
        Assert.Equal("In Review", result.NewParentState);

        var patchReq = Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Patch));
        Assert.DoesNotContain(RollupTestData.DoneStateUuid, patchReq.Body);
    }

    [Fact]
    public async Task DoneParent_ReevaluatesItsOwnParent()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ChildParentAndGrandparentIssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.FullStateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.ParentExpandedJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.SiblingsDoneJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.GrandparentExpandedJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.GrandparentSiblingsDoneJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.PatchParentOkJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(RollupTestData.CommentOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.RollupParentAsync("TLB-25", CancellationToken.None);

        Assert.True(result.ParentTransitioned);
        Assert.Equal("Done", result.NewParentState);

        var patchReqs = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patchReqs.Count);
        Assert.Contains($"/issues/{RollupTestData.ParentUuid}/", patchReqs[0].RequestUri!.ToString());
        Assert.Contains($"/issues/{RollupTestData.GrandparentUuid}/", patchReqs[1].RequestUri!.ToString());
        Assert.All(patchReqs, req => Assert.Contains(RollupTestData.DoneStateUuid, req.Body));
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

    [Fact]
    public async Task WriteThrough_ConcurrentTransitionAndLabel_NeitherFieldReverts()
    {
        // Guards against a lost-update in write-through: under parallel dispatch many mutations
        // hit the shared client concurrently. A non-atomic read-modify-write would let a label
        // update (which copies StateId from its own stale read) revert a concurrent transition -
        // resurrecting the stale-state class this cache exists to kill. The atomic AddOrUpdate
        // composes against the live value, so both fields must survive regardless of interleaving.
        var handler = new RoutingOkHandler(req =>
            req.Method == HttpMethod.Patch ? TestData.PatchOkJson()
            : req.RequestUri!.AbsolutePath.Contains("/states/") ? TestData.StateListJson()
            : req.RequestUri!.AbsolutePath.Contains("/labels/") ? TestData.LabelListJson()
            : TestData.IssueListJson());

        // Uncap the throttle: it serializes HTTP sends, which would both slow this test and
        // damp the contention we want. The write-through races after the PATCH returns, so a
        // high budget fires the mutations near-simultaneously and maximizes interleaving.
        var options = new PlaneClientOptions
        {
            BaseUrl = "https://plane.example.com",
            ApiToken = "test-token",
            WorkspaceSlug = "my-workspace",
            ProjectId = "my-project",
            ProjectIdentifier = "TLB",
            RequestsPerMinute = 1_000_000
        };
        var client = new PlaneTicketingClient(new HttpClient(handler), options);

        // Warm the snapshot + state/label caches (issue starts Backlog with no labels).
        await client.GetAsync("TLB-24", CancellationToken.None);

        // Interleave state and label write-throughs on the same issue, all in flight at once.
        var tasks = new List<Task>();
        for (int i = 0; i < 64; i++)
        {
            tasks.Add(client.TransitionAsync("TLB-24", TicketState.InProgress, CancellationToken.None));
            tasks.Add(client.ApplyLabelsAsync("TLB-24", new[] { "Size: S" }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);
        Assert.Equal(TicketState.InProgress, ticket.State);  // a racing label write did not revert the transition
        Assert.Contains(ticket.Labels, l => l.Equals("Size: S", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateTicketAsync_SeedsSnapshot_LaterGetSeesIt_WithoutReload()
    {
        const string newUuid = "99999999-0000-0000-0000-000000000200";
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));   // snapshot warm (TLB-24)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));   // ToTicket states
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));   // ToTicket labels
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(newUuid, 200)));        // POST create

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        // Warm the snapshot FIRST so the create cannot be picked up by a (no-op) reload.
        await client.GetAsync("TLB-24", CancellationToken.None);
        var created = await client.CreateTicketAsync("New ticket", null, "<p>body</p>", null, CancellationToken.None);
        Assert.Equal("TLB-200", created.Id);

        var ticket = await client.GetAsync("TLB-200", CancellationToken.None);
        Assert.Equal(newUuid, ticket.Uuid);
        Assert.Equal(TicketState.Backlog, ticket.State);  // new ticket defaults to Backlog (empty StateId)

        // The project was paginated exactly once (the warm); the created ticket came from the seed.
        var listGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("per_page=100"))
            .ToList();
        Assert.Single(listGets);
    }

    [Fact]
    public async Task CreateChildTicketsAsync_SeedsSnapshot_ParentProbeSeesNewChild()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));               // snapshot warm (no children of parent)
        handler.Enqueue(FakeMessageHandler.OkJson(CreateChildTestData.LabelListJson()));    // create label cache
        handler.Enqueue(FakeMessageHandler.OkJson(
            CreateChildTestData.CreateIssueResponseJson(CreateChildTestData.Child1Uuid, 100))); // POST child
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));               // ToTicket states for the child

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        // Warm the snapshot; the parent has no children yet.
        var before = await client.QueryAsync(new TicketQuery(ParentId: CreateChildTestData.ParentUuid), CancellationToken.None);
        Assert.Empty(before);

        var result = await client.CreateChildTicketsAsync(
            CreateChildTestData.ParentUuid,
            new[] { new ChildTicketSpec("Child A", "<p>desc</p>", Array.Empty<string>()) },
            CancellationToken.None);
        Assert.Single(result.Created);

        // The parent-probe now sees the freshly created child via the seed (no reload).
        var after = await client.QueryAsync(new TicketQuery(ParentId: CreateChildTestData.ParentUuid), CancellationToken.None);
        var child = Assert.Single(after);
        Assert.Equal(CreateChildTestData.Child1Uuid, child.Uuid);
        Assert.Equal(CreateChildTestData.ParentUuid, child.ParentId);
    }
}

// ---------------------------------------------------------------------------
// Snapshot load: single-flight + multi-page cursor pagination
// ---------------------------------------------------------------------------
public class SnapshotLoadTests
{
    private static string IssueJson(int seq, string uuid) =>
        $$"""{ "id": "{{uuid}}", "sequence_id": {{seq}}, "name": "t{{seq}}", "description_html": "<p>d</p>", "state": "{{TestData.StateUuid}}", "labels": [], "parent": null, "type": null }""";

    private static PlaneClientOptions FastOptions() => new()
    {
        BaseUrl = "https://plane.example.com",
        ApiToken = "test-token",
        WorkspaceSlug = "my-workspace",
        ProjectId = "my-project",
        ProjectIdentifier = "TLB",
        RequestsPerMinute = 1_000_000  // don't let the throttle serialize sends (which would itself mask races)
    };

    [Fact]
    public async Task EnsureSnapshot_IsSingleFlight_ConcurrentReadsPaginateProjectOnce()
    {
        var issues = "{\"results\":[" + string.Join(",", new[]
        {
            IssueJson(24, "aaaaaaaa-0000-0000-0000-000000000024"),
            IssueJson(25, "aaaaaaaa-0000-0000-0000-000000000025"),
            IssueJson(26, "aaaaaaaa-0000-0000-0000-000000000026"),
            IssueJson(27, "aaaaaaaa-0000-0000-0000-000000000027"),
        }) + "]}";

        var handler = new RoutingOkHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/states/") ? TestData.StateListJson()
            : req.RequestUri!.AbsolutePath.Contains("/labels/") ? TestData.LabelListJson()
            : issues);

        var client = new PlaneTicketingClient(new HttpClient(handler), FastOptions());

        // Four concurrent first-readers; the snapshot must load exactly once.
        var tasks = new[] { 24, 25, 26, 27 }.Select(seq => client.GetAsync($"TLB-{seq}", CancellationToken.None));
        var tickets = await Task.WhenAll(tasks);

        Assert.Equal(4, tickets.Length);
        var listGets = handler.Log
            .Where(r => r.Method == HttpMethod.Get && r.Uri.Contains("per_page=100"))
            .ToList();
        Assert.Single(listGets);
    }

    [Fact]
    public async Task EnsureSnapshot_StopsOnNextPageResultsFalse_EvenWithNonEmptyCursor_AndEncodesCursorOnce()
    {
        // Real Plane keeps echoing an advancing next_cursor past the last page; next_page_results
        // is the authoritative end signal. Page 2 carries a NON-empty cursor but
        // next_page_results=false, so the loader must stop after page 2 - a page-3 fetch would hit
        // the empty queue's 404 and fail this test. This is the regression that caused build chain
        // to walk to the 50-page cap and trip the throttle on every run.
        var page1 = $$"""{ "results": [ {{IssueJson(24, "aaaaaaaa-0000-0000-0000-000000000024")}} ], "next_cursor": "100:1:0", "next_page_results": true }""";
        var page2 = $$"""{ "results": [ {{IssueJson(25, "aaaaaaaa-0000-0000-0000-000000000025")}} ], "next_cursor": "100:2:0", "next_page_results": false }""";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(page1));                    // page 0
        handler.Enqueue(FakeMessageHandler.OkJson(page2));                    // page 1 (last)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson())); // ToTicket states
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson())); // ToTicket labels

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(), CancellationToken.None);

        Assert.Equal(2, tickets.Count);
        Assert.Contains(tickets, t => t.Id == "TLB-24");
        Assert.Contains(tickets, t => t.Id == "TLB-25");

        // Exactly two issue-list GETs: the walk stopped at the last page, it did not run to the cap.
        var listGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("per_page=100"))
            .ToList();
        Assert.Equal(2, listGets.Count);

        // Page-2 request carried page-1's cursor encoded exactly once (':' -> %3A, no '%25').
        var uri = handler.Requests[1].RequestUri!.ToString();
        Assert.Contains("cursor=100%3A1%3A0", uri);
        Assert.DoesNotContain("%25", uri);
    }

    [Fact]
    public async Task EnsureSnapshot_StopsOnEmptyPage_WhenNextPageResultsAbsent()
    {
        // Defensive fallback: if a response omits next_page_results and keeps echoing a cursor,
        // an empty results page must still end the walk (rather than running to the cap).
        var page1 = $$"""{ "results": [ {{IssueJson(24, "aaaaaaaa-0000-0000-0000-000000000024")}} ], "next_cursor": "100:1:0" }""";
        var page2 = """{ "results": [], "next_cursor": "100:2:0" }""";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(page1));
        handler.Enqueue(FakeMessageHandler.OkJson(page2));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(), CancellationToken.None);

        Assert.Single(tickets);
        Assert.Equal("TLB-24", tickets[0].Id);
        var listGets = handler.Requests
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("per_page=100"))
            .ToList();
        Assert.Equal(2, listGets.Count);  // page1 + the empty page, then stop
    }
}

public class AddRelationAsyncTests
{
    [Fact]
    public async Task AddRelationAsync_PostsIssuesListNotRelatedIssue()
    {
        // Regression: Plane's create-relation endpoint requires the related issue under an
        // "issues" array, not a singular "related_issue" field. Sending "related_issue" gets a
        // 400 {"issues":["This field is required."]} - the key names the missing field. The old
        // payload mirrored the GET *response* shape (related_issue) onto the POST *request*.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // snapshot (both lookups)
        handler.Enqueue(FakeMessageHandler.OkJson("{}"));                     // POST relations

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.AddRelationAsync("TLB-24", "TLB-24", CancellationToken.None);

        var postReq = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.EndsWith($"issues/{TestData.IssueUuid}/issue-relation/", postReq.RequestUri!.AbsolutePath);
        Assert.Contains("\"relation_type\":\"blocked_by\"", postReq.Body);
        Assert.Contains($"\"issues\":[\"{TestData.IssueUuid}\"]", postReq.Body);
        Assert.DoesNotContain("related_issue", postReq.Body);
    }
}

public class RelationManagementTests
{
    private static PlaneClientOptions LegacyOptionsWithoutIdentifier() => new()
    {
        BaseUrl = "https://plane.example.com",
        ApiToken = "test-token",
        WorkspaceSlug = "my-workspace",
        ProjectId = "my-project",
        ProjectIdentifier = ""
    };

    private const string ProjectListWithTlb =
        """{"results":[{"id":"my-project","name":"Project","identifier":"TLB"}]}""";

    [Fact]
    public async Task LegacyConfig_MatchingPrefix_DiscoversIdentifierThenLists()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(ProjectListWithTlb));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        var relation = Assert.Single(await client.ListRelationsAsync("tlb-24", CancellationToken.None));

        Assert.Equal("TLB-999", relation.TargetId);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/projects/"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/issues/"));
    }

    [Fact]
    public async Task LegacyConfig_MismatchedPrefix_StopsAfterProjectDiscovery()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(ProjectListWithTlb));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.ListRelationsAsync("OTHER-24", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/projects/", request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/issues/"));
    }

    [Fact]
    public async Task LegacyConfig_CreateTargetMismatch_DiscoversOnceWithoutIssueOrRelationWrite()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(ProjectListWithTlb));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.CreateRelationAsync("TLB-24", "blocking", "OTHER-25", CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/issues/"));
    }

    [Fact]
    public async Task LegacyConfig_ProjectIdentifierDiscovery_IsCachedOnce()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(ProjectListWithTlb));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.ListRelationsAsync("OTHER-24", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.ListRelationsAsync("OTHER-25", CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LegacyConfig_BareNumericId_SkipsProjectDiscovery()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        _ = await client.ListRelationsAsync("24", CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/projects/"));
    }

    [Fact]
    public async Task LegacyConfig_UnresolvableProjectIdentifier_IsClearConfigurationError()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            """{"results":[{"id":"different-project","name":"Other","identifier":"OTHER"}]}"""));
        var client = new PlaneTicketingClient(new HttpClient(handler), LegacyOptionsWithoutIdentifier());

        var error = await Assert.ThrowsAsync<RelationConfigurationException>(() =>
            client.ListRelationsAsync("TLB-24", CancellationToken.None));

        Assert.Contains("Cannot resolve the identifier", error.Message);
        Assert.Contains("plane_project_identifier", error.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("tlb-24", "24")]
    [InlineData("24", "TLB-24")]
    public async Task CreateRelationAsync_AcceptsMatchingPrefixCaseInsensitively_AndBareIds(
        string sourceId, string targetId)
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson("{}"));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.CreateRelationAsync(sourceId, "blocking", targetId, CancellationToken.None);

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("OTHER-24", "TLB-24")]
    [InlineData("TLB-24", "OTHER-24")]
    public async Task CreateRelationAsync_MismatchedPrefix_IsNotFoundBeforeAnyHttp(
        string sourceId, string targetId)
    {
        var handler = new FakeMessageHandler();
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.CreateRelationAsync(sourceId, "blocking", targetId, CancellationToken.None));

        Assert.Contains("outside configured project", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("remove")]
    public async Task ReadOrRemove_MismatchedSourcePrefix_IsNotFoundBeforeAnyHttp(string operation)
    {
        var handler = new FakeMessageHandler();
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        if (operation == "list")
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                client.ListRelationsAsync("OTHER-24", CancellationToken.None));
        else
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                client.RemoveRelationAsync("OTHER-24", "edge-1", CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateRelationAsync_NormalizesType_AndPostsOneCanonicalEdge()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson("{}"));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.CreateRelationAsync("TLB-24", "finish-before", "TLB-24", CancellationToken.None);

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"relation_type\":\"finish_before\"", request.Body);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ListRelationsAsync_ReturnsStableEdgeIdentity()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var relation = Assert.Single(await client.ListRelationsAsync("TLB-24", CancellationToken.None));

        Assert.Equal("eeeeeeee-0000-0000-0000-000000000001", relation.Id);
        Assert.Equal("TLB-999", relation.TargetId);
    }

    [Fact]
    public async Task RemoveRelationAsync_ListsThenDeletesExactEdge()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(""));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.RemoveRelationAsync(
            "TLB-24", "eeeeeeee-0000-0000-0000-000000000001", CancellationToken.None);

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/issue-relation/eeeeeeee-0000-0000-0000-000000000001/", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RemoveRelationAsync_UnknownEdge_IsNotFoundWithoutDelete()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.RelationListJson()));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.RemoveRelationAsync("TLB-24", "missing", CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task StrictList_Endpoint404_IsClearConfigurationFailure()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, "not found"));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        var error = await Assert.ThrowsAsync<RelationEndpointUnavailableException>(() =>
            client.ListRelationsAsync("TLB-24", CancellationToken.None));

        Assert.Contains("relation endpoint is unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_Endpoint404_IsClearConfigurationFailure()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, "not found"));
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await Assert.ThrowsAsync<RelationEndpointUnavailableException>(() =>
            client.CreateRelationAsync("TLB-24", "blocking", "TLB-24", CancellationToken.None));
    }
}

// WI-06: ListProjectsAsync must walk every cursor page so find-or-create sees the whole
// workspace and never creates a duplicate of a project living beyond the first page.
public class ProjectDiscoveryPaginationTests
{
    private const string Page1 =
        """{ "results": [ {"id":"id-1","name":"Alpha","identifier":"AL"} ], "next_cursor": "100:1:0", "next_page_results": true }""";
    private const string Page2 =
        """{ "results": [ {"id":"id-2","name":"Survey Smoketest","identifier":"ST"} ], "next_cursor": "100:2:0", "next_page_results": false }""";

    [Fact]
    public async Task ListProjectsAsync_ReturnsProjectsAcrossAllPages()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(Page1));
        handler.Enqueue(FakeMessageHandler.OkJson(Page2));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var projects = await client.ListProjectsAsync(CancellationToken.None);

        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, p => p.Id == "id-1" && p.Name == "Alpha");
        Assert.Contains(projects, p => p.Id == "id-2" && p.Name == "Survey Smoketest");

        // Exactly two project-list GETs; page 2 carried page 1's cursor encoded once.
        var gets = handler.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        Assert.Equal(2, gets.Count);
        Assert.Contains("/projects/", gets[0].RequestUri!.AbsolutePath);
        Assert.Contains("cursor=100%3A1%3A0", gets[1].RequestUri!.ToString());
        Assert.DoesNotContain("%25", gets[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task FindProjectByNameAsync_FindsProjectBeyondFirstPage_CaseInsensitive()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(Page1));
        handler.Enqueue(FakeMessageHandler.OkJson(Page2));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        // Target lives on page 2 and is matched case-insensitively.
        var id = await client.FindProjectByNameAsync("survey smoketest", CancellationToken.None);

        Assert.Equal("id-2", id);
    }

    [Fact]
    public async Task ListProjectsAsync_SinglePage_NoCursorFields_ReturnsAll()
    {
        // A single-page response that omits next_cursor/next_page_results must not loop.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(
            """{ "results": [ {"id":"only","name":"Solo","identifier":"SO"} ] }"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var projects = await client.ListProjectsAsync(CancellationToken.None);

        Assert.Single(projects);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SecondClientOnSameHttpClient_AfterFirstRequest_Throws()
    {
        // Root-cause guard for the interactive-init crash: the constructor sets BaseAddress and a
        // default header, which throw once the HttpClient has sent a request. So two
        // PlaneTicketingClients can never share one HttpClient - each must get its own (the caller
        // in InitCommand was fixed to do exactly that).
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson("""{ "results": [] }"""));

        var http = new HttpClient(handler);
        var first = new PlaneTicketingClient(http, TestData.Options());
        await first.ListProjectsAsync(CancellationToken.None); // first request "starts" the HttpClient

        Assert.Throws<InvalidOperationException>(() => new PlaneTicketingClient(http, TestData.Options()));
    }
}
