using System.Net;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

// ---------------------------------------------------------------------------
// CreateTicketAsync tests
// ---------------------------------------------------------------------------
public class CreateTicketAsyncTests
{
    private const string NewIssueUuid = "11111111-0000-0000-0000-000000000042";
    private const int NewSequenceId = 42;
    private const string CreatedAtIso = "2026-05-27T10:00:00Z";

    private static string CreateIssueResponseJson(
        string uuid = NewIssueUuid,
        int seq = NewSequenceId,
        string createdAt = CreatedAtIso) =>
        $$"""{"id":"{{uuid}}","sequence_id":{{seq}},"created_at":"{{createdAt}}"}""";

    // Fact (a): successful create returns NewTicketResult with Id "TLB-42" and correct Uuid
    [Fact]
    public async Task CreateTicketAsync_ReturnsNewTicketResultWithIdAndUuid()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.CreateTicketAsync(
            title: "My new ticket",
            type: "",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: null,
            ct: CancellationToken.None);

        Assert.Equal("TLB-42", result.Id);
        Assert.Equal(NewIssueUuid, result.Uuid);
        Assert.Equal(
            DateTime.Parse(CreatedAtIso, null, System.Globalization.DateTimeStyles.RoundtripKind),
            result.CreatedAt);

        var postReq = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.Contains("issues", postReq.RequestUri!.ToString());
    }

    // Fact (b): initial labels are resolved to UUIDs and included in the request body
    [Fact]
    public async Task CreateTicketAsync_WithInitialLabels_ResolvesUuidsInRequestBody()
    {
        var handler = new FakeMessageHandler();
        // First request: labels GET
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        // Second request: issues POST
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.CreateTicketAsync(
            title: "Labeled ticket",
            type: "",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: ["Size: S"],
            ct: CancellationToken.None);

        Assert.Equal("TLB-42", result.Id);

        var postReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.Contains("\"labels\"", postReq.Body);
        Assert.Contains(TestData.LabelUuid, postReq.Body);
    }

    [Fact]
    public async Task CreateTicketAsync_WithInitialLabels_ThenGetAsync_ReadsLabelsBack()
    {
        const string sizeLUuid = "cccccccc-0000-0000-0000-000000000010";
        const string teamLabelUuid = "cccccccc-0000-0000-0000-000000000011";
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson($$"""
            {
              "results": [
                { "id": "{{sizeLUuid}}", "name": "size:l" },
                { "id": "{{teamLabelUuid}}", "name": "team:core" }
              ]
            }
            """));
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));
        handler.Enqueue(FakeMessageHandler.OkJson($$"""
            {
              "results": [{
                "id": "{{NewIssueUuid}}",
                "sequence_id": {{NewSequenceId}},
                "name": "Labeled ticket",
                "description_html": "<p>desc</p>",
                "state": "{{TestData.StateUuid}}",
                "labels": ["{{sizeLUuid}}", "{{teamLabelUuid}}"],
                "parent": null,
                "type": null
              }]
            }
            """));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var created = await client.CreateTicketAsync(
            title: "Labeled ticket",
            type: "",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: ["size:l", "team:core"],
            ct: CancellationToken.None);
        var ticket = await client.GetAsync(created.Id, CancellationToken.None);

        Assert.Equal(Size.L, ticket.Size);
        Assert.Equal(new[] { "size:l", "team:core" }, ticket.Labels);
    }

    // Fact (c): null initial labels -> no extra list-labels GET, labels empty in request
    [Fact]
    public async Task CreateTicketAsync_NullInitialLabels_NoLabelLookupAndEmptyLabels()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateTicketAsync(
            title: "No labels",
            type: "",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: null,
            ct: CancellationToken.None);

        // Only one HTTP request: the POST (no label-list GET)
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        // labels must be present but empty in the JSON body
        var body = handler.Requests[0].Body;
        Assert.Contains("\"labels\"", body);
        Assert.Contains("[]", body);
    }

    // Fact (d): 422 API error throws PlaneApiException with correct status
    [Fact]
    public async Task CreateTicketAsync_ApiError_ThrowsPlaneApiException()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(422, "unprocessable entity"));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ex = await Assert.ThrowsAsync<PlaneApiException>(() =>
            client.CreateTicketAsync(
                title: "Bad ticket",
                type: "",
                descriptionHtml: "<p>desc</p>",
                initialLabelNames: null,
                ct: CancellationToken.None));

        Assert.Equal(422, ex.Status);
        Assert.Contains("422", ex.Message);
    }

    // Fact (e): unknown label name throws InvalidOperationException with clear message
    [Fact]
    public async Task CreateTicketAsync_UnknownLabelName_ThrowsInvalidOperationException()
    {
        var handler = new FakeMessageHandler();
        // Labels GET returns known labels (not including "unknown-label")
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateTicketAsync(
                title: "Ticket with bad label",
                type: "",
                descriptionHtml: "<p>desc</p>",
                initialLabelNames: ["unknown-label"],
                ct: CancellationToken.None));

        Assert.Contains("unknown-label", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    // Fact (f): non-empty type name is resolved to UUID and sent in request body
    [Fact]
    public async Task CreateTicketAsync_WithTypeName_ResolvesUuidInRequestBody()
    {
        var handler = new FakeMessageHandler();
        // First request: issue-types GET
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));
        // Second request: issues POST
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateTicketAsync(
            title: "Typed ticket",
            type: "task",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: null,
            ct: CancellationToken.None);

        var postReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.Contains(TestData.IssueTypeUuid, postReq.Body);
    }

    // Fact (g): type name lookup is case-insensitive
    [Fact]
    public async Task CreateTicketAsync_TypeNameCaseInsensitive_ResolvesUuid()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateTicketAsync(
            title: "Typed ticket",
            type: "TASK",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: null,
            ct: CancellationToken.None);

        var postReq = handler.Requests[1];
        Assert.Contains(TestData.IssueTypeUuid, postReq.Body);
    }

    // Fact (h): unknown type name throws InvalidOperationException with clear message
    [Fact]
    public async Task CreateTicketAsync_UnknownTypeName_ThrowsInvalidOperationException()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.IssueTypeListJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateTicketAsync(
                title: "Bad type ticket",
                type: "unknown-type",
                descriptionHtml: "<p>desc</p>",
                initialLabelNames: null,
                ct: CancellationToken.None));

        Assert.Contains("unknown-type", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    // Fact (i): empty type string skips issue-types lookup entirely
    [Fact]
    public async Task CreateTicketAsync_EmptyType_NoIssueTypeLookup()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(CreateIssueResponseJson()));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        await client.CreateTicketAsync(
            title: "No type ticket",
            type: "",
            descriptionHtml: "<p>desc</p>",
            initialLabelNames: null,
            ct: CancellationToken.None);

        // Only the POST -- no issue-types GET
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }
}

public class PlaneConnectivityTests
{
    [Fact]
    public async Task TestConnectivityAsync_ReadsLabelsStatesAndProbesIssueCreate()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.ErrorJson(400, """{"name":["Not a valid string."]}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.TestConnectivityAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("OK", result.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Contains("/labels/", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("/states/", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("/issues/", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Contains("throughline_build_probe", handler.Requests[2].Body);
    }

    [Fact]
    public async Task TestConnectivityAsync_ForbiddenReadReportsAuthorizationFailure()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(403, """{"detail":"You do not have permission to perform this action."}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.TestConnectivityAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not authorized", result.Message);
        Assert.Contains("403", result.Message);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task TestConnectivityAsync_ProjectNotFoundReportsActionableMessage()
    {
        // A 404 on the first project-scoped GET means the project id does not resolve.
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.ErrorJson(404, """{"error":"Page not found."}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.TestConnectivityAsync(CancellationToken.None);

        Assert.False(result.Success);
        // Names the project id and workspace.
        Assert.Contains("my-project", result.Message);
        Assert.Contains("my-workspace", result.Message);
        // States the likely cause and a remedy.
        Assert.Contains("wrong or the project was never created", result.Message);
        Assert.Contains("build init", result.Message);
        Assert.Contains("plane_project_id", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TestConnectivityAsync_CreateForbiddenAfterSuccessfulReadsReportsAuthorizationFailure()
    {
        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.LabelListJson()));
        handler.Enqueue(FakeMessageHandler.OkJson(TestData.StateListJson()));
        handler.Enqueue(FakeMessageHandler.ErrorJson(403, """{"detail":"You do not have permission to perform this action."}"""));

        var client = new PlaneTicketingClient(new HttpClient(handler), TestData.Options());
        var result = await client.TestConnectivityAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not authorized", result.Message);
        Assert.Contains("create issues", result.Message);
        Assert.Contains("403", result.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Contains("/issues/", handler.Requests[2].RequestUri!.AbsolutePath);
    }
}
