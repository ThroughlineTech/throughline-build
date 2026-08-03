using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class EvidenceCommandTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("claim")]
    [InlineData("review")]
    [InlineData("commit")]
    [InlineData("integrate")]
    [InlineData("gate")]
    [InlineData("final")]
    public async Task EverySupportedKind_PostsExactlyOneCommentAndReadsItBack(string kind)
    {
        var fake = new FakeTicketing
        {
            ReadBackComments =
            [
                new TicketComment(
                    "comment-1",
                    "<h3>Build evidence</h3><p>read back</p>",
                    CreatedAt)
            ]
        };

        var (exit, json) = await RunAsync(ValidArgs(kind), fake);

        Assert.Equal(0, exit);
        Assert.Single(fake.Created);
        Assert.Equal(1, fake.GetCommentsCalls);
        Assert.Empty(fake.Transitions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        Assert.Equal("TLB-604", data.GetProperty("ticket").GetString());
        Assert.Equal(kind, data.GetProperty("kind").GetString());
        Assert.Equal("comment-1", data.GetProperty("commentId").GetString());
        Assert.True(data.GetProperty("readBackVerified").GetBoolean());
        Assert.Contains("read back", data.GetProperty("body").GetString());
    }

    [Fact]
    public async Task MarkdownFormatting_UsesStableKindAndFieldNames()
    {
        var fake = new FakeTicketing
        {
            ReadBackComments = [new TicketComment("comment-1", "<p>ok</p>", CreatedAt)]
        };

        var (exit, _) = await RunAsync(
            [
                "evidence", "add", "--ticket", "TLB-604", "--kind", "gate",
                "--run-head-sha", "head-123", "--gate-result", "passed",
                "--fingerprint", "finger-456", "--summary", "all checks green"
            ],
            fake);

        Assert.Equal(0, exit);
        var html = Assert.Single(fake.Created).Html;
        Assert.Contains("Build evidence: gate", html);
        Assert.Contains("run_head_sha", html);
        Assert.Contains("head-123", html);
        Assert.Contains("gate_result", html);
        Assert.Contains("pass", html);
        Assert.Contains("finger-456", html);
        Assert.Contains("all checks green", html);
    }

    [Fact]
    public async Task MissingKindSpecificField_IsUsageAndDoesNotWrite()
    {
        var fake = new FakeTicketing();

        var (exit, json) = await RunAsync(
            [
                "evidence", "add", "--ticket", "TLB-604", "--kind", "review",
                "--run-head-sha", "head-123", "--fingerprint", "finger-456"
            ],
            fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.Usage, ErrorCode(json));
        Assert.Contains("--verdict", json);
        Assert.Empty(fake.Created);
        Assert.Equal(0, fake.GetCommentsCalls);
    }

    [Fact]
    public async Task MissingCandidateSha_IsUsageAndDoesNotWrite()
    {
        var fake = new FakeTicketing();

        var (exit, json) = await RunAsync(
            [
                "evidence", "add", "--ticket", "TLB-604", "--kind", "claim",
                "--claim", "the claim", "--fingerprint", "finger-456"
            ],
            fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.Usage, ErrorCode(json));
        Assert.Contains("--candidate-sha", json);
        Assert.Empty(fake.Created);
    }

    [Fact]
    public async Task InvalidGateResult_IsUsageAndDoesNotWrite()
    {
        var fake = new FakeTicketing();

        var (exit, json) = await RunAsync(
            [
                "evidence", "add", "--ticket", "TLB-604", "--kind", "gate",
                "--run-head-sha", "head-123", "--gate-result", "maybe",
                "--fingerprint", "finger-456"
            ],
            fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.Usage, ErrorCode(json));
        Assert.Empty(fake.Created);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("evidence", "list")]
    [InlineData("evidence", "add", "--ticket", "TLB-604", "--kind", "claim", "--no-transition")]
    public async Task NoOpOrUnknownShape_IsUsageWithoutBackendCall(params string[] args)
    {
        var fake = new FakeTicketing();

        var (exit, json) = await RunAsync(args, fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.Usage, ErrorCode(json));
        Assert.Empty(fake.Created);
        Assert.Equal(0, fake.GetCommentsCalls);
        Assert.Empty(fake.Transitions);
    }

    [Fact]
    public async Task PostBackendFailure_IsFailureAndDoesNotReadBackOrRetry()
    {
        var fake = new FakeTicketing
        {
            CreateException = new PlaneApiException(503, "service unavailable")
        };

        var (exit, json) = await RunAsync(ValidArgs("commit"), fake);

        Assert.Equal(1, exit);
        Assert.Equal(CliErrorCodes.Failure, ErrorCode(json));
        Assert.Contains("service unavailable", json);
        Assert.Empty(fake.Created);
        Assert.Equal(0, fake.GetCommentsCalls);
    }

    [Fact]
    public async Task ReadBackFailure_ReportsCreatedIdAndNeverPostsAgain()
    {
        var fake = new FakeTicketing
        {
            ReadBackException = new PlaneApiException(503, "read service unavailable")
        };

        var (exit, json) = await RunAsync(ValidArgs("integrate"), fake);

        Assert.Equal(1, exit);
        Assert.Equal(CliErrorCodes.Failure, ErrorCode(json));
        Assert.Contains("comment-1", json);
        Assert.Contains("read service unavailable", json);
        Assert.Contains("do not retry", json);
        Assert.Single(fake.Created);
        Assert.Equal(1, fake.GetCommentsCalls);
    }

    [Fact]
    public void HelpDocumentsKindsAndSeparateLifecycle()
    {
        var help = HelpRegistryFactory.Build().TryGet("evidence");

        Assert.NotNull(help);
        Assert.Contains("claim|review|commit|integrate|gate|final", help.Usage);
        Assert.Contains(help.Details!, detail => detail.Contains("never closes", StringComparison.Ordinal));
        Assert.Contains(help.Details!, detail => detail.Contains("read-back", StringComparison.Ordinal));
    }

    private static string[] ValidArgs(string kind) => kind switch
    {
        "claim" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--claim", "the implementation matches the ticket",
            "--candidate-sha", "candidate-123", "--fingerprint", "finger-456"
        ],
        "review" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--run-head-sha", "head-123", "--verdict", "Pass",
            "--fingerprint", "finger-456"
        ],
        "commit" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--candidate-sha", "candidate-123", "--fingerprint", "finger-456"
        ],
        "integrate" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--candidate-sha", "candidate-123", "--run-head-sha", "head-123",
            "--fingerprint", "finger-456"
        ],
        "gate" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--run-head-sha", "head-123", "--gate-result", "pass",
            "--fingerprint", "finger-456"
        ],
        "final" =>
        [
            "evidence", "add", "--ticket", "TLB-604", "--kind", kind,
            "--candidate-sha", "candidate-123", "--run-head-sha", "head-123",
            "--fingerprint", "finger-456"
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<(int Exit, string Json)> RunAsync(
        string[] args,
        FakeTicketing fake)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await EvidenceCommand.ExecuteAsync(
            args,
            jsonOutput: true,
            fake,
            output,
            error,
            CancellationToken.None);
        Assert.Equal(string.Empty, error.ToString());
        return (exit, output.ToString());
    }

    private static string ErrorCode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class FakeTicketing : ITicketing
    {
        public List<(string Id, string Html)> Created { get; } = [];
        public List<(string Id, TicketState State)> Transitions { get; } = [];
        public IReadOnlyList<TicketComment> ReadBackComments { get; init; } =
            [new TicketComment("comment-1", "<p>read back</p>", CreatedAt)];
        public Exception? CreateException { get; init; }
        public Exception? ReadBackException { get; init; }
        public int GetCommentsCalls { get; private set; }

        public BackendCapabilities Capabilities => new(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            if (CreateException is not null) throw CreateException;
            Created.Add((id, html));
            return Task.FromResult("comment-1");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct)
        {
            GetCommentsCalls++;
            if (ReadBackException is not null) throw ReadBackException;
            return Task.FromResult(ReadBackComments);
        }

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) => throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotImplementedException();
    }
}
