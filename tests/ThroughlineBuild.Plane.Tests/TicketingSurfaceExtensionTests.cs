using System.Net;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

// ---------------------------------------------------------------------------
// Shared helpers for surface extension tests
// ---------------------------------------------------------------------------
internal static class SurfaceTestData
{
    public const string CancelledStateUuid = "bbbbbbbb-0000-0000-0000-000000000099";
    public const string BacklogStateUuid   = TestData.StateUuid; // "bbbbbbbb-0000-0000-0000-000000000001"

    /// <summary>State list that includes Backlog, In Progress, Done, Cancelled.</summary>
    public static string StateListWithCancelledJson() =>
        $$"""
        {
          "results": [
            { "id": "{{BacklogStateUuid}}", "name": "Backlog", "group": "backlog" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000002", "name": "In Progress", "group": "started" },
            { "id": "bbbbbbbb-0000-0000-0000-000000000003", "name": "Done", "group": "completed" },
            { "id": "{{CancelledStateUuid}}", "name": "Cancelled", "group": "cancelled" }
          ]
        }
        """;

    public static string CommentOkJson() =>
        $$"""{"id":"{{TestData.CommentUuid}}","comment_html":"<p>ok</p>"}""";

    public static string PatchOkJson() => TestData.PatchOkJson();
}

// ---------------------------------------------------------------------------
// QueryAsync tests
// ---------------------------------------------------------------------------
public class QueryAsyncTests
{
    [Fact]
    public async Task QueryAsync_NoFilters_GetsIssuesListWithPerPage100()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(), CancellationToken.None);

        Assert.Single(tickets);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("per_page=100", req.RequestUri!.ToString());
        // No state/parent/type filters when query is empty
        Assert.DoesNotContain("&state=", req.RequestUri!.ToString());
        Assert.DoesNotContain("&parent=", req.RequestUri!.ToString());
        Assert.DoesNotContain("&type=", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task QueryAsync_StateFilter_AppendsStateUuidInQueryString()
    {
        var handler = new FakeMessageHandler();
        // First: states cache (needed to resolve TicketState.Backlog -> UUID)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        // Second: issue list (with state filter applied)
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        // Third + fourth: state/label caches for ToTicketAsync
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(State: TicketState.Backlog), CancellationToken.None);

        // Find the GET request to the issues list (has state filter)
        var issuesReq = handler.Requests.FirstOrDefault(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("&state="));

        Assert.NotNull(issuesReq);
        Assert.Contains(TestData.StateUuid, issuesReq!.RequestUri!.ToString());
    }

    [Fact]
    public async Task QueryAsync_ParentFilter_AppendsParentInQueryString()
    {
        const string parentUuid = "pppppppp-0000-0000-0000-000000000042";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(ParentId: parentUuid), CancellationToken.None);

        var req = handler.Requests[0];
        Assert.Contains($"&parent={parentUuid}", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task QueryAsync_EmptyResults_ReturnsEmptyList()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson("""{"results":[]}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(), CancellationToken.None);

        Assert.Empty(tickets);
    }

    [Fact]
    public async Task QueryAsync_TypeFilter_AppendsTypeInQueryString()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var tickets = await client.QueryAsync(new TicketQuery(Type: "Task"), CancellationToken.None);

        var req = handler.Requests[0];
        Assert.Contains("&type=Task", req.RequestUri!.ToString());
    }
}

// ---------------------------------------------------------------------------
// TransitionLifecycleAsync tests
// ---------------------------------------------------------------------------
public class TransitionLifecycleAsyncTests
{
    /// <summary>
    /// Sets up handler with: issues list, comment post OK, state cache, PATCH OK.
    /// </summary>
    private static (FakeMessageHandler handler, PlaneTicketingClient client) BuildClientForLifecycle()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // FindIssue
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.CommentOkJson())); // POST comment
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.StateListWithCancelledJson())); // state cache
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.PatchOkJson())); // PATCH state
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        return (handler, client);
    }

    [Fact]
    public async Task Close_PostsWontfixCommentAndTransitionsToCancelled()
    {
        var (handler, client) = BuildClientForLifecycle();

        await client.TransitionLifecycleAsync("TLB-24", LifecycleTransition.Close, null, CancellationToken.None);

        var postReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Post);
        Assert.NotNull(postReq);
        Assert.Contains("wontfix", postReq!.Body);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        Assert.Contains(SurfaceTestData.CancelledStateUuid, patchReq!.Body);
    }

    [Fact]
    public async Task Defer_PostsDeferredCommentAndTransitionsToCancelled()
    {
        var (handler, client) = BuildClientForLifecycle();

        await client.TransitionLifecycleAsync("TLB-24", LifecycleTransition.Defer, null, CancellationToken.None);

        var postReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Post);
        Assert.NotNull(postReq);
        Assert.Contains("deferred", postReq!.Body);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        Assert.Contains(SurfaceTestData.CancelledStateUuid, patchReq!.Body);
    }

    [Fact]
    public async Task Reopen_PostsReopenedCommentAndTransitionsToBacklog()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson())); // FindIssue
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.CommentOkJson())); // POST comment
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.StateListWithCancelledJson())); // state cache
        handler.Enqueue(FakeMessageHandler.OkJson(SurfaceTestData.PatchOkJson())); // PATCH state
        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());

        await client.TransitionLifecycleAsync("TLB-24", LifecycleTransition.Reopen, null, CancellationToken.None);

        var postReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Post);
        Assert.NotNull(postReq);
        Assert.Contains("reopened", postReq!.Body);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        Assert.Contains(SurfaceTestData.BacklogStateUuid, patchReq!.Body);
    }

    [Fact]
    public async Task ReasonString_AppendedToComment()
    {
        var (handler, client) = BuildClientForLifecycle();

        await client.TransitionLifecycleAsync("TLB-24", LifecycleTransition.Close, "out of scope", CancellationToken.None);

        var postReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Post);
        Assert.NotNull(postReq);
        Assert.Contains("out of scope", postReq!.Body);
    }

    [Fact]
    public async Task NoReason_CommentContainsOnlyMarker()
    {
        var (handler, client) = BuildClientForLifecycle();

        await client.TransitionLifecycleAsync("TLB-24", LifecycleTransition.Defer, null, CancellationToken.None);

        var postReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Post);
        Assert.NotNull(postReq);
        // Comment should still be well-formed HTML
        Assert.Contains("<p>", postReq!.Body);
        Assert.Contains("deferred", postReq!.Body);
    }
}

// ---------------------------------------------------------------------------
// UpdateDescriptionAsync tests
// ---------------------------------------------------------------------------
public class UpdateDescriptionAsyncTests
{
    [Fact]
    public async Task UpdateDescriptionAsync_PatchesWithOnlyNewHtml()
    {
        const string newHtml = "<p>completely new description</p>";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson(descHtml: "<p>old description</p>")));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.UpdateDescriptionAsync("TLB-24", newHtml, CancellationToken.None);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        Assert.Contains(newHtml, patchReq!.Body);
        // Unlike AppendDescriptionAsync, UpdateDescriptionAsync must NOT include old content
        Assert.DoesNotContain("old description", patchReq!.Body);
    }

    [Fact]
    public async Task UpdateDescriptionAsync_UsesCorrectEndpoint()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.UpdateDescriptionAsync("TLB-24", "<p>new</p>", CancellationToken.None);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        // Endpoint should contain the issue UUID
        Assert.Contains(TestData.IssueUuid, patchReq!.RequestUri!.ToString());
    }

    [Fact]
    public async Task UpdateDescriptionAsync_BodyContainsDescriptionHtmlKey()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.PatchOkJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.UpdateDescriptionAsync("TLB-24", "<p>content</p>", CancellationToken.None);

        var patchReq = handler.Requests.FirstOrDefault(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patchReq);
        Assert.Contains("description_html", patchReq!.Body);
    }
}
