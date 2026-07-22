using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class RelateCommandTests
{
    public static IEnumerable<object[]> SupportedAliases()
    {
        foreach (var canonical in RelationKinds.Allowed)
        {
            yield return new object[] { canonical, canonical };
            if (canonical.Contains('_'))
            {
                yield return new object[] { canonical.Replace('_', ' '), canonical };
                yield return new object[] { canonical.Replace('_', '-'), canonical };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SupportedAliases))]
    public async Task Create_AllKindsAndAliases_NormalizeAndDispatch(string input, string expected)
    {
        var fake = new FakeTicketing();
        var (exit, json) = await RunAsync(["relate", "TLB-10", input, "TLB-9"], fake);

        Assert.Equal(0, exit);
        Assert.Equal(("TLB-10", expected, "TLB-9"), Assert.Single(fake.Created));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("created", doc.RootElement.GetProperty("data").GetProperty("action").GetString());
    }

    [Fact]
    public async Task List_EmitsStableIdentity()
    {
        var fake = new FakeTicketing
        {
            Relations = [new Relation("blocking", "TLB-9", "edge-1")]
        };

        var (exit, json) = await RunAsync(["relate", "TLB-10", "--list"], fake);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("edge-1", doc.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Remove_DispatchesExactListedIdentity()
    {
        var fake = new FakeTicketing();

        var (exit, json) = await RunAsync(["relate", "TLB-10", "--remove", "edge-1"], fake);

        Assert.Equal(0, exit);
        Assert.Equal(("TLB-10", "edge-1"), Assert.Single(fake.Removed));
        Assert.Contains("\"relationId\": \"edge-1\"", json);
    }

    [Fact]
    public async Task InvalidKind_IsUsage_WithoutDispatch()
    {
        var fake = new FakeTicketing();
        var (exit, json) = await RunAsync(["relate", "TLB-10", "blocks", "TLB-9"], fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.Usage, ErrorCode(json));
        Assert.Empty(fake.Created);
    }

    [Fact]
    public async Task UnknownTarget_IsNotFound()
    {
        var fake = new FakeTicketing { CreateError = new KeyNotFoundException("TLB-999 not found") };
        var (exit, json) = await RunAsync(["relate", "TLB-10", "blocking", "TLB-999"], fake);

        Assert.Equal(1, exit);
        Assert.Equal(CliErrorCodes.NotFound, ErrorCode(json));
    }

    [Theory]
    [InlineData("create-source")]
    [InlineData("create-target")]
    [InlineData("list-source")]
    [InlineData("remove-source")]
    public async Task MismatchedProjectPrefix_IsNotFound_ForEveryRelevantPosition(string scenario)
    {
        var mismatch = new KeyNotFoundException("Ticket is outside configured project 'TLB'");
        var fake = new FakeTicketing
        {
            CreateError = scenario.StartsWith("create", StringComparison.Ordinal) ? mismatch : null,
            ListError = scenario == "list-source" ? mismatch : null,
            RemoveError = scenario == "remove-source" ? mismatch : null
        };
        var args = scenario switch
        {
            "create-source" => new[] { "relate", "OTHER-10", "blocking", "TLB-9" },
            "create-target" => new[] { "relate", "TLB-10", "blocking", "OTHER-9" },
            "list-source" => new[] { "relate", "OTHER-10", "--list" },
            _ => new[] { "relate", "OTHER-10", "--remove", "edge-1" }
        };

        var (exit, json) = await RunAsync(args, fake);

        Assert.Equal(1, exit);
        Assert.Equal(CliErrorCodes.NotFound, ErrorCode(json));
    }

    [Theory]
    [InlineData("self relations are not allowed", "TLB-10")]
    [InlineData("duplicate relation already exists", "TLB-9")]
    public async Task Backend400_IncludingSelfAndDuplicate_IsFailure(string body, string target)
    {
        var fake = new FakeTicketing { CreateError = new PlaneApiException(400, body) };
        var (exit, json) = await RunAsync(["relate", "TLB-10", "blocking", target], fake);

        Assert.Equal(1, exit);
        Assert.Equal(CliErrorCodes.Failure, ErrorCode(json));
        Assert.Contains(body, json);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("list")]
    [InlineData("remove")]
    public async Task Endpoint404TranslatedByClient_IsConfigError_ForEveryOperation(string operation)
    {
        var endpoint404 = new RelationEndpointUnavailableException(
            "Plane relation endpoint is unavailable", new PlaneApiException(404, "not found"));
        var fake = new FakeTicketing
        {
            CreateError = operation == "create" ? endpoint404 : null,
            ListError = operation == "list" ? endpoint404 : null,
            RemoveError = operation == "remove" ? endpoint404 : null
        };
        var args = operation switch
        {
            "create" => new[] { "relate", "TLB-10", "blocking", "TLB-9" },
            "list" => new[] { "relate", "TLB-10", "--list" },
            _ => new[] { "relate", "TLB-10", "--remove", "edge-1" }
        };
        var (exit, json) = await RunAsync(args, fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.ConfigError, ErrorCode(json));
        Assert.Contains("endpoint is unavailable", json);
    }

    [Fact]
    public async Task ProjectIdentifierDiscoveryFailure_IsConfigError()
    {
        var fake = new FakeTicketing
        {
            ListError = new RelationConfigurationException("Cannot resolve configured project identifier")
        };

        var (exit, json) = await RunAsync(["relate", "TLB-10", "--list"], fake);

        Assert.Equal(2, exit);
        Assert.Equal(CliErrorCodes.ConfigError, ErrorCode(json));
    }

    private static async Task<(int Exit, string Json)> RunAsync(string[] args, FakeTicketing fake)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RelateCommand.ExecuteAsync(
            args, jsonOutput: true, fake, output, error, CancellationToken.None);
        Assert.Equal(string.Empty, error.ToString());
        return (exit, output.ToString());
    }

    private static string ErrorCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class FakeTicketing : ITicketing
    {
        public List<(string Source, string Kind, string Target)> Created { get; } = [];
        public List<(string Source, string RelationId)> Removed { get; } = [];
        public IReadOnlyList<Relation> Relations { get; init; } = [];
        public Exception? CreateError { get; init; }
        public Exception? ListError { get; init; }
        public Exception? RemoveError { get; init; }

        public BackendCapabilities Capabilities => new(true, true, true, true);

        public Task CreateRelationAsync(string sourceId, string relationKind, string targetId, CancellationToken ct)
        {
            if (CreateError is not null) throw CreateError;
            Created.Add((sourceId, relationKind, targetId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Relation>> ListRelationsAsync(string id, CancellationToken ct)
        {
            if (ListError is not null) throw ListError;
            return Task.FromResult(Relations);
        }

        public Task RemoveRelationAsync(string sourceId, string relationId, CancellationToken ct)
        {
            if (RemoveError is not null) throw RemoveError;
            Removed.Add((sourceId, relationId));
            return Task.CompletedTask;
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid,
            IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotImplementedException();
    }
}
