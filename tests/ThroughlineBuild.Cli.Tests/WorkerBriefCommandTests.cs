using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class WorkerBriefCommandTests
{
    private static readonly string BaseSha = new('a', 40);
    private static readonly string HeadSha = new('b', 40);

    // Every agent that ships a brief template set. Only two distinct texts exist today
    // (claude-code differs; codex, copilot, and gemini are byte-identical), so the role
    // tests below run against all of them rather than exercising one repeatedly.
    public static TheoryData<string> BriefAgents()
    {
        var data = new TheoryData<string>();
        foreach (var agent in TemplateLoader.AvailableAgents())
            data.Add(agent);
        return data;
    }

    // Text present only in the claude-code implement and review templates. Used to tell which
    // template set actually rendered a brief; asserting on the agent name would not, since the
    // name is never echoed into the artifact.
    private const string ClaudeCodeOnlyMarker = "do not split them across separate messages";

    [Theory]
    [MemberData(nameof(BriefAgents))]
    public async Task Implement_WritesTicketRoleGateAndWorkspaceContent(string agent)
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "implement.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing { Ticket = TicketWithExecutionContract() };
            var git = new BriefGitClient
            {
                Branch = "ticket/TLB-602-worker-brief",
                Diff = DiffWithPatch()
            };

            var exit = await RunAsync(
                ticketing,
                git,
                worktree,
                outputPath,
                role: "implement",
                json: false,
                checks: [Check("test", ["test"])],
                agentOverrides: new WorkerBriefAgentOverrides(All: agent));

            Assert.Equal(0, exit);
            var brief = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Ticket body and acceptance criteria", brief);
            Assert.Contains("Implement the feature", brief);
            Assert.Contains("Acceptance criteria", brief);
            Assert.Contains("Role boundaries", brief);
            Assert.Contains("Semantic execution contract", brief);
            Assert.Contains("Parent intent: Preserve the declared contract.", brief);
            Assert.Contains("Treat a recorded contract as binding", brief);
            Assert.Contains("Do NOT stage or commit changes", brief);
            Assert.DoesNotContain("Commit all changes locally", brief);
            Assert.Contains("Do not mutate the ticket, main branch, unrelated branches, other worktrees, or deployments.", brief);
            Assert.Contains("build gate --ticket TLB-602 --role gating --require-checks --json", brief);
            Assert.DoesNotContain(
                brief.Split('\n'),
                line => line.Contains("build gate", StringComparison.Ordinal) &&
                    !line.Contains("--require-checks", StringComparison.Ordinal));
            Assert.Contains(worktree, brief, StringComparison.Ordinal);
            Assert.Contains("# Implement Phase Brief", brief);
            Assert.Contains("does not spawn a worker", brief);
            Assert.Empty(ticketing.Mutations);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [MemberData(nameof(BriefAgents))]
    public async Task Review_EmitsAotJsonMetadataAndActualDiffStatusEvidence(string agent)
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "review.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing
            {
                Ticket = TicketWithExecutionContract(),
                Comments =
                [
                    new("implement", "<p>Implemented the feature and it is correct.</p>", DateTimeOffset.UtcNow)
                ]
            };
            var git = new BriefGitClient
            {
                Branch = "ticket/TLB-602-worker-brief",
                Diff = DiffWithPatch(),
                TrackedStatus = [" M src/changed.cs"],
                UntrackedPaths = ["review-notes.txt"]
            };
            var stdout = new StringWriter();

            var exit = await RunAsync(
                ticketing,
                git,
                worktree,
                outputPath,
                role: "review",
                json: true,
                checks: [Check("test", ["test"] )],
                output: stdout,
                agentOverrides: new WorkerBriefAgentOverrides(All: agent));

            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(stdout.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("TLB-602", data.GetProperty("sourceTicketId").GetString());
            Assert.Equal("review", data.GetProperty("role").GetString());
            Assert.Equal(Path.GetFullPath(outputPath), data.GetProperty("outputPath").GetString());
            Assert.Equal(worktree, data.GetProperty("worktreePath").GetString());
            Assert.Contains("src/changed.cs", JsonStrings(data.GetProperty("changedPaths")));
            Assert.Contains(" M src/changed.cs", JsonStrings(data.GetProperty("trackedStatus")));
            Assert.Contains("review-notes.txt", JsonStrings(data.GetProperty("untrackedPaths")));

            var brief = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("@@ -1 +1 @@", brief);
            Assert.Contains("Tracked worktree status", brief);
            Assert.Contains("review-notes.txt", brief);
            Assert.Contains("No implementer conclusion is supplied", brief);
            Assert.Contains("No implementer conclusion is a verdict", brief);
            Assert.Contains("This artifact did not run automated checks", brief);
            Assert.Contains("Checks were not run by build worker brief; run the exact gate command in the artifact.", brief);
            Assert.Contains("Verify declared authority, forbidden shortcuts, required negative tests, and declared shared surfaces", brief);
            Assert.Contains("Parent intent: Preserve the declared contract.", brief);
            Assert.DoesNotContain("automated check results above already capture build/test status", brief);
            Assert.DoesNotContain("Implemented the feature and it is correct", brief);
            Assert.Empty(ticketing.Mutations);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [MemberData(nameof(BriefAgents))]
    public async Task Rework_IncludesPriorBlockingFindingAndKeepsSameWorktreeSemantics(string agent)
    {
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "rework.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing
            {
                Ticket = TicketWithExecutionContract(),
                Comments =
                [
                    new("implement", "<p>[implemented_at: abc]</p><p>Implemented the feature.</p>", DateTimeOffset.UtcNow.AddMinutes(-2)),
                    new("review", "<p><strong>reviewed:</strong> rework - API validation is missing<br/>checks_failed: test</p>", DateTimeOffset.UtcNow.AddMinutes(-1))
                ]
            };
            var git = new BriefGitClient { Diff = DiffWithPatch() };

            var exit = await RunAsync(
                ticketing,
                git,
                worktree,
                outputPath,
                role: "rework",
                json: false,
                checks: [],
                agentOverrides: new WorkerBriefAgentOverrides(All: agent));

            Assert.Equal(0, exit);
            var brief = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Prior blocking findings", brief);
            Assert.Contains("API validation is missing", brief);
            Assert.Contains("Blocking checks:", brief);
            Assert.Contains("test", brief);
            Assert.Contains("same supplied worktree", brief);
            Assert.Contains("Rework round", brief);
            Assert.Contains("Touched files from prior implement commit", brief);
            Assert.Contains("Do not reinterpret the ticket execution contract", brief);
            Assert.Contains("Do NOT stage or commit changes", brief);
            Assert.DoesNotContain("Commit all changes locally", brief);
            Assert.Contains("Parent intent: Preserve the declared contract.", brief);
            Assert.Equal(1, ticketing.GetCommentsCalls);
            Assert.Empty(ticketing.Mutations);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Rework_UsesReviewedFailAsBlockingFinding()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "rework.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing
            {
                Ticket = TicketWithBody(),
                Comments =
                [
                    new("review", "<p><strong>reviewed:</strong> fail - Security validation is missing<br/>checks_failed: security-gate-42</p>", DateTimeOffset.UtcNow)
                ]
            };

            var exit = await RunAsync(
                ticketing,
                new BriefGitClient { Diff = DiffWithPatch() },
                worktree,
                outputPath,
                role: "rework",
                json: false,
                checks: []);

            Assert.Equal(0, exit);
            var brief = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Security validation is missing", brief);
            Assert.Contains("Blocking checks:", brief);
            Assert.Contains("security-gate-42", brief);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Rework_IgnoresQuotedMarkersAndCompletionProse()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "rework.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing
            {
                Ticket = TicketWithBody(),
                Comments =
                [
                    new("audit", "<p>The audit discusses reviewed: rework, reviewed: fail, and [gate: markers.</p>", DateTimeOffset.UtcNow.AddMinutes(-2)),
                    new("implement", "<p>Rework complete - reviewer feedback mentions reviewed: rework and [gate: but reports no blocking finding.</p>", DateTimeOffset.UtcNow.AddMinutes(-1))
                ]
            };

            var exit = await RunAsync(
                ticketing,
                new BriefGitClient { Diff = DiffWithPatch() },
                worktree,
                outputPath,
                role: "rework",
                json: false,
                checks: []);

            Assert.Equal(1, exit);
            Assert.False(File.Exists(outputPath));
            Assert.Equal(1, ticketing.GetCommentsCalls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Rework_UsesNewerReviewedFailOverOlderReworkFinding()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "rework.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing
            {
                Ticket = TicketWithBody(),
                Comments =
                [
                    new("review", "<p><strong>reviewed:</strong> rework - Older finding</p>", DateTimeOffset.UtcNow.AddMinutes(-2)),
                    new("review", "<p><strong>reviewed:</strong> fail - Newer blocking finding</p>", DateTimeOffset.UtcNow.AddMinutes(-1))
                ]
            };

            var exit = await RunAsync(
                ticketing,
                new BriefGitClient { Diff = DiffWithPatch() },
                worktree,
                outputPath,
                role: "rework",
                json: false,
                checks: []);

            Assert.Equal(0, exit);
            var brief = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Newer blocking finding", brief);
            Assert.DoesNotContain("Older finding", brief);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UnknownRole_FailsBeforeTicketOrFilesystemReads()
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var ticketing = new FakeTicketing { Ticket = TicketWithBody() };
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await WorkerBriefCommand.ExecuteAsync(
                ["worker", "brief", "--ticket", "TLB-602", "--role", "ship", "--worktree", root, "--output", Path.Combine(root, "brief.md")],
                json: true,
                ticketing,
                new BriefGitClient(),
                ProjectContext.Empty,
                [],
                "main",
                "codex",
                output,
                error,
                CancellationToken.None);

            Assert.Equal(2, exit);
            Assert.Equal("usage", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, ticketing.GetCalls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DuplicateOption_FailsAsBadArgumentCombination()
    {
        DisableReflectionSerialization();
        var output = new StringWriter();
        var exit = await WorkerBriefCommand.ExecuteAsync(
            ["worker", "brief", "--ticket", "TLB-602", "--ticket", "TLB-602", "--role", "implement", "--worktree", ".", "--output", "brief.md"],
            json: true,
            new FakeTicketing(),
            new BriefGitClient(),
            ProjectContext.Empty,
            [],
            "main",
            "codex",
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("usage", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("only once", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingTicket_ReportsNotFoundAndWritesNoArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "brief.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing { GetError = new KeyNotFoundException("TLB-999 not found") };
            var output = new StringWriter();

            var exit = await RunAsync(
                ticketing,
                new BriefGitClient(),
                worktree,
                outputPath,
                role: "implement",
                json: true,
                checks: [],
                output: output);

            Assert.Equal(1, exit);
            Assert.Equal("not_found", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UnreadableWorktreeAndUnwritableOutput_AreFailures()
    {
        var root = CreateTempDirectory();
        try
        {
            var missingWorktree = Path.Combine(root, "missing-worktree");
            var missingWorktreeOutput = Path.Combine(root, "missing-worktree.md");
            var ticketing = new FakeTicketing { Ticket = TicketWithBody() };

            var missingWorktreeExit = await RunAsync(
                ticketing,
                new BriefGitClient(),
                missingWorktree,
                missingWorktreeOutput,
                role: "implement",
                json: false,
                checks: []);
            Assert.Equal(1, missingWorktreeExit);
            Assert.Equal(0, ticketing.GetCalls);

            var worktree = Path.Combine(root, "worktree");
            var outputDirectory = Path.Combine(root, "output-directory");
            Directory.CreateDirectory(worktree);
            Directory.CreateDirectory(outputDirectory);
            var unwritableOutputExit = await RunAsync(
                ticketing,
                new BriefGitClient(),
                worktree,
                outputDirectory,
                role: "implement",
                json: false,
                checks: []);
            Assert.Equal(1, unwritableOutputExit);
            Assert.True(Directory.Exists(outputDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AgentFlag_IsHonored_SoDistinctTemplateSetsProduceDifferentBriefs()
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var claudeCode = await RenderBriefAsync(root, "implement", new WorkerBriefAgentOverrides(All: "claude-code"));
            var codex = await RenderBriefAsync(root, "implement", new WorkerBriefAgentOverrides(All: "codex"));

            Assert.NotEqual(claudeCode, codex);
            Assert.Contains(ClaudeCodeOnlyMarker, claudeCode);
            Assert.DoesNotContain(ClaudeCodeOnlyMarker, codex);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    // Per-phase flag beats --agent, which beats [workers.phases], which beats default_agent.
    [InlineData("implement", "claude-code", "codex", null, "codex", true)]
    [InlineData("implement", null, "claude-code", "codex", "codex", true)]
    [InlineData("implement", null, null, "claude-code", "codex", true)]
    [InlineData("implement", null, null, null, "claude-code", true)]
    [InlineData("implement", "codex", "claude-code", "claude-code", "claude-code", false)]
    // Rework maps onto the implement phase; review takes the review-phase flag instead.
    [InlineData("rework", "claude-code", "codex", "codex", "codex", true)]
    [InlineData("review", "claude-code", "codex", null, "codex", false)]
    public async Task AgentResolution_FollowsFlagThenPhaseThenDefaultPrecedence(
        string role,
        string? implementFlag,
        string? agentAll,
        string? configuredPhaseAgent,
        string configuredDefaultAgent,
        bool expectsClaudeCodeTemplates)
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var phase = role == "review" ? "review" : "implement";
            var brief = await RenderBriefAsync(
                root,
                role,
                new WorkerBriefAgentOverrides(All: agentAll, Implement: implementFlag),
                configuredPhaseAgent is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal) { [phase] = configuredPhaseAgent },
                configuredDefaultAgent);

            if (expectsClaudeCodeTemplates)
                Assert.Contains(ClaudeCodeOnlyMarker, brief);
            else
                Assert.DoesNotContain(ClaudeCodeOnlyMarker, brief);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ReviewRole_UsesReviewPhaseFlagOverAgentAll()
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var brief = await RenderBriefAsync(
                root,
                "review",
                new WorkerBriefAgentOverrides(All: "codex", Review: "claude-code"));

            Assert.Contains(ClaudeCodeOnlyMarker, brief);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("no-such-agent", null)]
    [InlineData(null, "claude-code")]
    public async Task UnusableAgentFlag_IsUsageErrorBeforeAnyTicketOrOutputWrite(
        string? unknownAgentAll,
        string? planFlag)
    {
        DisableReflectionSerialization();
        var root = CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var outputPath = Path.Combine(root, "brief.md");
            Directory.CreateDirectory(worktree);
            var ticketing = new FakeTicketing { Ticket = TicketWithBody() };
            var output = new StringWriter();

            var exit = await RunAsync(
                ticketing,
                new BriefGitClient { Diff = DiffWithPatch() },
                worktree,
                outputPath,
                role: "implement",
                json: true,
                checks: [],
                output: output,
                agentOverrides: new WorkerBriefAgentOverrides(All: unknownAgentAll, Plan: planFlag));

            Assert.Equal(2, exit);
            Assert.Equal("usage", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, ticketing.GetCalls);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    // Renders one brief into a fresh output file under <paramref name="root"/> and returns its text.
    private static async Task<string> RenderBriefAsync(
        string root,
        string role,
        WorkerBriefAgentOverrides overrides,
        IReadOnlyDictionary<string, string>? phaseAgents = null,
        string configuredAgent = "codex")
    {
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);
        var outputPath = Path.Combine(root, $"{role}-{Guid.NewGuid():N}.md");
        var ticketing = new FakeTicketing
        {
            Ticket = TicketWithBody(),
            Comments =
            [
                new("review", "<p><strong>reviewed:</strong> rework - API validation is missing</p>", DateTimeOffset.UtcNow)
            ]
        };

        var exit = await RunAsync(
            ticketing,
            new BriefGitClient { Diff = DiffWithPatch() },
            worktree,
            outputPath,
            role,
            json: false,
            checks: [],
            agentOverrides: overrides,
            phaseAgents: phaseAgents,
            configuredAgent: configuredAgent);

        Assert.Equal(0, exit);
        return await File.ReadAllTextAsync(outputPath);
    }

    private static async Task<int> RunAsync(
        FakeTicketing ticketing,
        BriefGitClient git,
        string worktree,
        string outputPath,
        string role,
        bool json,
        IReadOnlyList<CheckSpec> checks,
        TextWriter? output = null,
        WorkerBriefAgentOverrides? agentOverrides = null,
        IReadOnlyDictionary<string, string>? phaseAgents = null,
        string configuredAgent = "codex")
    {
        return await WorkerBriefCommand.ExecuteAsync(
            ["worker", "brief", "--ticket", "TLB-602", "--role", role, "--worktree", worktree, "--output", outputPath],
            json,
            ticketing,
            git,
            ProjectContext.Empty,
            checks,
            "main",
            configuredAgent,
            output ?? TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None,
            phaseAgents,
            agentOverrides);
    }

    private static Ticket TicketWithBody() => new(
        "TLB-602",
        "uuid-602",
        "Add worker brief artifact",
        "Task",
        TicketState.Backlog,
        Size.M,
        Risk.Medium,
        "<h2>Goal</h2><p>Implement the feature.</p><h3>Acceptance criteria</h3><ul><li>Writes a Markdown brief.</li><li>Uses source generated JSON.</li></ul>",
        [],
        [],
        null);

    private static Ticket TicketWithExecutionContract() => new(
        "TLB-602",
        "uuid-602",
        "Add worker brief artifact",
        "Task",
        TicketState.Backlog,
        Size.M,
        Risk.Medium,
        "<h2>Goal</h2><p>Implement the feature.</p><h2>Ticket execution contract</h2><p>Parent intent: Preserve the declared contract.</p><p>Authoritative source: ticket body.</p><p>Forbidden shortcuts: Do not infer missing authority.</p><p>Required negative tests: Cover the forbidden shortcut.</p><h2>Acceptance criteria</h2><ul><li>Writes a Markdown brief.</li></ul>",
        [],
        [],
        null);

    private static GitDiff DiffWithPatch() => new(
        "main",
        "ticket/TLB-602-worker-brief",
        [new("src/changed.cs", DiffKind.Modified, null, 1, 1, "@@ -1 +1 @@\n-old\n+new")]);

    private static CheckSpec Check(string name, IReadOnlyList<string> arguments) => new(
        name,
        "dotnet",
        arguments,
        TimeSpan.FromMinutes(1));

    private static IReadOnlyList<string> JsonStrings(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToList();

    private static void DisableReflectionSerialization() =>
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "throughline-worker-brief-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class FakeTicketing : ITicketing
    {
        public Ticket Ticket { get; init; } = TicketWithBody();
        public IReadOnlyList<TicketComment> Comments { get; init; } = [];
        public Exception? GetError { get; init; }
        public List<string> Mutations { get; } = [];
        public int GetCalls { get; private set; }
        public int GetCommentsCalls { get; private set; }

        public BackendCapabilities Capabilities => new(true, true, true, true);

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            GetCalls++;
            if (GetError is not null)
                throw GetError;
            return Task.FromResult(Ticket);
        }

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct)
        {
            GetCommentsCalls++;
            return Task.FromResult(Comments);
        }

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<Relation>>([]);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>([]);
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Mutate();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Mutate();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) { Mutations.Add("comment"); return Task.FromResult("comment"); }
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Mutate();
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Mutate();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => Task.FromResult(new RollupResult(false, null, null));
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotSupportedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Mutate();
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>([]);
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Mutate();
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Mutate();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotSupportedException();

        private Task Mutate()
        {
            Mutations.Add("mutation");
            return Task.CompletedTask;
        }
    }

    private sealed class BriefGitClient : IGitClient
    {
        public string Branch { get; init; } = "ticket/TLB-602-worker-brief";
        public string Head { get; init; } = HeadSha;
        public GitDiff Diff { get; init; } = new("main", "HEAD", []);
        public IReadOnlyList<string> TrackedStatus { get; init; } = [];
        public IReadOnlyList<string> UntrackedPaths { get; init; } = [];

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) => Task.FromResult(BaseSha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([]);
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) => Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) => Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) => Task.FromResult(Head);
        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) => Task.FromResult(Diff);
        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) => Task.FromResult(new GitOpResult(true, null));
        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) => Task.FromResult(new RebaseResult(true, false, [], null));
        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) => Task.FromResult(new GitOpResult(true, null));
        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) => Task.FromResult(new GitOpResult(true, null));
        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) => Task.FromResult(new GitOpResult(true, null));
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct) => Task.FromResult(Branch);
        public Task<GitPathQueryResult> GetTrackedChangesResultAsync(string workingDirectory, CancellationToken ct) => Task.FromResult(new GitPathQueryResult(true, TrackedStatus, null));
        public Task<GitPathQueryResult> GetUntrackedFilesResultAsync(string workingDirectory, CancellationToken ct) => Task.FromResult(new GitPathQueryResult(true, UntrackedPaths, null));
    }
}
