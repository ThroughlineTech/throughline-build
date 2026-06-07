using System.Net;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Plane.Tests;

// ---------------------------------------------------------------------------
// StubProjectDiscovery: controllable IProjectDiscovery for unit tests
// ---------------------------------------------------------------------------
internal sealed class StubProjectDiscovery : IProjectDiscovery
{
    public IReadOnlyList<ProjectInfo> Projects { get; set; } = [];
    public string? CreatedId { get; init; } = "created-uuid";

    public string LastCreatedName { get; private set; } = string.Empty;
    public string LastCreatedIdentifier { get; private set; } = string.Empty;

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct)
        => Task.FromResult(Projects);

    public Task<string?> FindProjectByNameAsync(string name, CancellationToken ct)
    {
        var match = Projects
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => (string?)p.Id)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<string> CreateProjectAsync(string name, string identifier, CancellationToken ct)
    {
        LastCreatedName = name;
        LastCreatedIdentifier = identifier;
        return Task.FromResult(CreatedId!);
    }
}

// ---------------------------------------------------------------------------
// ProjectResolverTests
// ---------------------------------------------------------------------------
public class ProjectResolverTests
{
    // ---- existing project -> Found ----------------------------------------

    [Fact]
    public async Task ResolveAsync_ExistingProject_ReturnsFoundWithId()
    {
        var discovery = new StubProjectDiscovery
        {
            Projects = [new ProjectInfo("existing-uuid", "My Project", "MP")],
        };
        var resolver = new ProjectResolver(discovery);

        var result = await resolver.ResolveAsync("My Project", CancellationToken.None);

        Assert.Equal("existing-uuid", result.ProjectId);
        Assert.Equal(ProjectResolveOutcome.Found, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ExistingProject_MatchIsCaseInsensitive()
    {
        var discovery = new StubProjectDiscovery
        {
            Projects = [new ProjectInfo("uuid-123", "lattice flow", "LF")],
        };
        var resolver = new ProjectResolver(discovery);

        var result = await resolver.ResolveAsync("LATTICE FLOW", CancellationToken.None);

        Assert.Equal("uuid-123", result.ProjectId);
        Assert.Equal(ProjectResolveOutcome.Found, result.Outcome);
    }

    // ---- missing project -> Created ----------------------------------------

    [Fact]
    public async Task ResolveAsync_MissingProject_CreatesAndReturnsCreated()
    {
        var discovery = new StubProjectDiscovery
        {
            Projects = [],
            CreatedId = "new-uuid",
        };
        var resolver = new ProjectResolver(discovery);

        var result = await resolver.ResolveAsync("New Project", CancellationToken.None);

        Assert.Equal("new-uuid", result.ProjectId);
        Assert.Equal(ProjectResolveOutcome.Created, result.Outcome);
        Assert.Equal("New Project", discovery.LastCreatedName);
    }

    [Fact]
    public async Task ResolveAsync_MissingProject_DrivesIdentifierFromName_MultiWord()
    {
        var discovery = new StubProjectDiscovery { Projects = [] };
        var resolver = new ProjectResolver(discovery);

        await resolver.ResolveAsync("My Cool Project", CancellationToken.None);

        // First letter of each word, uppercased
        Assert.Equal("MCP", discovery.LastCreatedIdentifier);
    }

    [Fact]
    public async Task ResolveAsync_MissingProject_DrivesIdentifierFromName_SingleWord()
    {
        var discovery = new StubProjectDiscovery { Projects = [] };
        var resolver = new ProjectResolver(discovery);

        await resolver.ResolveAsync("LatticeFlow", CancellationToken.None);

        // First 6 letters of the single word, uppercased
        Assert.Equal("LATTIC", discovery.LastCreatedIdentifier);
    }

    // ---- raw-credentials constructor (covers the acceptance criterion that the resolver
    //      builds its client from raw creds rather than from a loaded config) --------------

    [Fact]
    public async Task ResolveAsync_ViaRawCredentials_FindsExistingProject()
    {
        const string projectId = "eeeeeeee-1234-0000-0000-000000000001";
        const string projectName = "LatticeFlow";

        var projectListJson = $$"""
            {"results":[{"id":"{{projectId}}","name":"{{projectName}}","identifier":"LF"}]}
            """;

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(projectListJson));

        var http = new HttpClient(handler);
        var resolver = new ProjectResolver(
            http,
            baseUrl: "https://plane.example.com",
            workspaceSlug: "my-workspace",
            apiToken: "test-token");

        var result = await resolver.ResolveAsync(projectName, CancellationToken.None);

        Assert.Equal(projectId, result.ProjectId);
        Assert.Equal(ProjectResolveOutcome.Found, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ViaRawCredentials_CreatesProjectWhenMissing()
    {
        const string newProjectId = "ffffffff-5678-0000-0000-000000000001";

        // First call: empty project list
        var emptyListJson = """{"results":[]}""";
        // Second call: create-project response
        var createResponseJson = $$"""{"id":"{{newProjectId}}"}""";

        var handler = new FakeMessageHandler();
        handler.Enqueue(FakeMessageHandler.OkJson(emptyListJson));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(createResponseJson, Encoding.UTF8, "application/json")
        });

        var http = new HttpClient(handler);
        var resolver = new ProjectResolver(
            http,
            baseUrl: "https://plane.example.com",
            workspaceSlug: "my-workspace",
            apiToken: "test-token");

        var result = await resolver.ResolveAsync("Brand New Project", CancellationToken.None);

        Assert.Equal(newProjectId, result.ProjectId);
        Assert.Equal(ProjectResolveOutcome.Created, result.Outcome);
    }
}
