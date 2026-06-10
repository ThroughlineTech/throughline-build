using System.Net.Http;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

// ---------------------------------------------------------------------------
// ScriptedHandler: per-request script entries that either return a response or
// throw, so transport failures (DNS, reset, timeout shapes) can be simulated
// between successful sends. Counts every send for retry assertions.
// ---------------------------------------------------------------------------
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();

    public int Sends { get; private set; }

    public void EnqueueOk(string json) =>
        _script.Enqueue(_ => FakeMessageHandler.OkJson(json));

    public void EnqueueThrow(Exception ex) =>
        _script.Enqueue(_ => throw ex);

    /// <summary>Every send after the script runs dry throws this (persistent outage).</summary>
    public Exception? FallbackThrow { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Sends++;
        if (_script.TryDequeue(out var step))
            return Task.FromResult(step(request));
        if (FallbackThrow is not null)
            throw FallbackThrow;
        return Task.FromResult(FakeMessageHandler.ErrorJson(404, "script exhausted"));
    }
}

// TLB-545: transient transport failures (DNS, connect, reset, timeout) must be retried in the
// client layer so a one-shot blip never aborts a chain, and a persistent outage must surface as
// TicketingUnavailableException so orchestration classifies it as environmental.
public class TransportRetryTests
{
    private static PlaneClientOptions FastTransportOptions(int attempts = 3) => new()
    {
        BaseUrl = "https://plane.example.com",
        ApiToken = "test-token",
        WorkspaceSlug = "my-workspace",
        ProjectId = "my-project",
        ProjectIdentifier = "TLB",
        TransportRetryAttempts = attempts,
        TransportRetryBaseDelay = TimeSpan.FromMilliseconds(1),
        TransportMaxRetryDelay = TimeSpan.FromMilliseconds(2)
    };

    private static Exception DnsFailure() =>
        new HttpRequestException(HttpRequestError.NameResolutionError,
            "nodename nor servname provided, or not known (plane.example.com:443)");

    // The TLB-545 incident shape: the worker had just committed, and a one-shot getaddrinfo
    // failure on the very next call (a comment POST) killed the whole chain. With transport
    // retry the blip is absorbed and the call succeeds.
    [Fact]
    public async Task CreateCommentAsync_OneShotDnsFailure_RetriedAndSucceeds()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueOk(TestData.IssueListJson()); // snapshot load
        handler.EnqueueThrow(DnsFailure());          // first comment POST: transient DNS failure
        handler.EnqueueOk(TestData.CommentJson());   // retried POST succeeds

        var client = new PlaneTicketingClient(new HttpClient(handler), FastTransportOptions());
        var commentId = await client.CreateCommentAsync("TLB-24", "<p>hello</p>", CancellationToken.None);

        Assert.Equal(TestData.CommentUuid, commentId);
        Assert.Equal(3, handler.Sends);
    }

    [Fact]
    public async Task CreateCommentAsync_PersistentDnsFailure_ThrowsTicketingUnavailable_AfterConfiguredAttempts()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueOk(TestData.IssueListJson()); // snapshot load
        handler.FallbackThrow = DnsFailure();        // every comment POST fails

        var client = new PlaneTicketingClient(new HttpClient(handler), FastTransportOptions(attempts: 2));
        var ex = await Assert.ThrowsAsync<TicketingUnavailableException>(
            () => client.CreateCommentAsync("TLB-24", "<p>hello</p>", CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        // 1 snapshot GET + initial POST + 2 retries
        Assert.Equal(4, handler.Sends);
    }

    // A mid-response failure on a POST must NOT be retried (the comment may already exist
    // server-side), but it must still be CLASSIFIED so the chain stops resumably instead of
    // crashing on a raw HttpRequestException.
    [Fact]
    public async Task CreateCommentAsync_ResponseEndedOnPost_NotRetried_ButStillClassified()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueOk(TestData.IssueListJson());
        handler.EnqueueThrow(new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset mid-response"));

        var client = new PlaneTicketingClient(new HttpClient(handler), FastTransportOptions());
        await Assert.ThrowsAsync<TicketingUnavailableException>(
            () => client.CreateCommentAsync("TLB-24", "<p>hello</p>", CancellationToken.None));

        Assert.Equal(2, handler.Sends); // no retry of the POST
    }

    [Fact]
    public async Task GetAsync_ResponseEndedOnGet_IsRetried()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueThrow(new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset mid-response"));
        handler.EnqueueOk(TestData.IssueListJson()); // retried snapshot GET
        handler.EnqueueOk(TestData.StateListJson());
        handler.EnqueueOk(TestData.LabelListJson());

        var client = new PlaneTicketingClient(new HttpClient(handler), FastTransportOptions());
        var ticket = await client.GetAsync("TLB-24", CancellationToken.None);

        Assert.Equal("TLB-24", ticket.Id);
        Assert.Equal(4, handler.Sends);
    }

    [Fact]
    public void IsTransportError_UserCancellation_IsNotTransport()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var timeoutShaped = new TaskCanceledException("canceled", new TimeoutException());

        // Same exception shape, but the caller's token is cancelled: user cancellation, not a timeout.
        Assert.False(PlaneTicketingClient.IsTransportError(timeoutShaped, cts.Token));
        Assert.True(PlaneTicketingClient.IsTransportError(timeoutShaped, CancellationToken.None));
    }

    [Theory]
    // Pre-send failures retry for every verb - no bytes reached the server.
    [InlineData(HttpRequestError.NameResolutionError, "POST", true)]
    [InlineData(HttpRequestError.ConnectionError, "POST", true)]
    [InlineData(HttpRequestError.SecureConnectionError, "POST", true)]
    // Response-phase failures retry only idempotent verbs.
    [InlineData(HttpRequestError.ResponseEnded, "POST", false)]
    [InlineData(HttpRequestError.ResponseEnded, "GET", true)]
    [InlineData(HttpRequestError.ResponseEnded, "PATCH", true)]
    public void IsRetryableTransportError_VerbSensitivity(HttpRequestError error, string method, bool expected)
    {
        var ex = new HttpRequestException(error, "transport fault");
        Assert.Equal(expected, PlaneTicketingClient.IsRetryableTransportError(ex, new HttpMethod(method)));
    }
}
