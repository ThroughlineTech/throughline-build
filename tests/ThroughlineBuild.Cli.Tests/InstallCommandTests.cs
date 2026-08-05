using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class InstallCommandTests
{
    private const string ProfileJson = """
    {
      "language": "TypeScript",
      "framework": "Vite",
      "package_manager": "pnpm",
      "build_command": "pnpm build",
      "test_command": "pnpm test",
      "review_checks": [
        {
          "name": "web-test",
          "executable": "pnpm",
          "arguments": ["test"],
          "timeout_minutes": 5,
          "role": "gating",
          "canary": [{ "path": "app/canary.txt", "content": "broken" }]
        }
      ]
    }
    """;

    private const string InvariantsToml = """
    [[conductor.review.invariants]]
    id = "browser-contract"
    statement = "Browser entry points preserve their public navigation contract."
    paths = ["app/**"]
    blocks_done = true

    [[conductor.review.invariants]]
    id = "asset-boundary"
    statement = "Static assets remain isolated from application source modules."
    paths = ["public/**"]
    blocks_done = false
    """;

    private static readonly string[] StubPaths =
    [
        ".agents/skills/run-backlog/SKILL.md",
        ".claude/commands/run-backlog.md",
    ];

    [Fact]
    public void Parse_ExposesExactlyThreeResumableStages()
    {
        Assert.True(InstallCommand.TryParse(["install"], out var first, out _));
        Assert.Null(first!.ProfilePath);
        Assert.Null(first.InvariantsPath);

        Assert.True(InstallCommand.TryParse(["install", "--profile", "-"], out var second, out _));
        Assert.Equal("-", second!.ProfilePath);

        Assert.True(InstallCommand.TryParse(["install", "--invariants", "invariants.toml"], out var third, out _));
        Assert.Equal("invariants.toml", third!.InvariantsPath);

        Assert.False(InstallCommand.TryParse(
            ["install", "--profile", "p.json", "--invariants", "i.toml"], out _, out var error));
        Assert.Contains("not both", error);
    }

    [Fact]
    public void JsonHandoffEnvelope_IsSourceGeneratedAndMachineReadable()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var output = new StringWriter();

        CliEnvelopeWriter.WriteInstall(output, new InstallView(
            "profile_handoff", false, "STOP: agent handoff required", "prompt", "next", null, null, []));

        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("profile_handoff", json.RootElement.GetProperty("data").GetProperty("stage").GetString());
        Assert.Equal("prompt", json.RootElement.GetProperty("data").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_AllThreeStages_ReachesReadyInNonDotnetRepoAndRerunsIdempotently()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var readinessCalls = 0;
            var dependencies = EndToEndDependencies(() => readinessCalls++);

            var first = await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies);
            Assert.Equal(0, first.Exit);
            Assert.Equal("profile_handoff", Stage(first.Stdout));
            Assert.Contains(".build/profile.json", first.Stdout);
            Assert.True(File.Exists(Path.Combine(repo, ".gitignore")));

            Write(repo, ".build/profile.json", ProfileJson);
            var second = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);
            Assert.Equal(0, second.Exit);
            Assert.Equal("invariants_handoff", Stage(second.Stdout));
            Assert.Contains(".build/invariants.toml", second.Stdout);
            Assert.Contains("platform = \"Vite\"", File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));

            Write(repo, ".build/invariants.toml", InvariantsToml);
            var third = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);
            Assert.Equal(0, third.Exit);
            Assert.Equal("ready", Stage(third.Stdout));
            Assert.Equal(1, readinessCalls);
            Assert.Equal("run/backlog", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal(string.Empty, await GitAsync(repo, "status", "--porcelain"));
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var expected = SopBundleCatalog.All.SelectMany(entry => entry.OwnedPaths)
                .Where(path => path.Class == SopBundleCatalog.EmittedPathClass)
                .Select(path => path.Path)
                .Append(".gitignore")
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected, committed.Order(StringComparer.Ordinal));

            var head = await GitAsync(repo, "rev-parse", "HEAD");
            var rerun = await ExecuteStageAsync(
                repo, new InstallInvocation(null, ".build/invariants.toml"), dependencies);
            Assert.Equal(0, rerun.Exit);
            Assert.Equal("ready", Stage(rerun.Stdout));
            Assert.Equal(2, readinessCalls);
            Assert.Equal(head, await GitAsync(repo, "rev-parse", "HEAD"));
            Assert.Equal(string.Empty, await GitAsync(repo, "status", "--porcelain"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPlatformLeavesStageTwoStateUnchangedAndCorrectedProfileResumes()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var sopCalls = 0;
            var baseline = EndToEndDependencies(() => { });
            var dependencies = baseline with
            {
                InstallSop = async (cwd, diagnostics, ct) =>
                {
                    sopCalls++;
                    return await baseline.InstallSop(cwd, diagnostics, ct);
                },
            };

            var first = await ExecuteStageAsync(repo, new InstallInvocation(null, null), dependencies);
            Assert.Equal(0, first.Exit);
            var ownedPaths = StubPaths
                .Append(".build/config.toml")
                .Append(".build/conductor.toml")
                .Append(".build/sop-manifest.json")
                .ToArray();
            var before = ownedPaths.ToDictionary(
                path => path,
                path => ReadBytesIfPresent(repo, path),
                StringComparer.Ordinal);

            var emptyProfile = ProfileJson
                .Replace("\"language\": \"TypeScript\"", "\"language\": \"  \"", StringComparison.Ordinal)
                .Replace("\"framework\": \"Vite\"", "\"framework\": \"\"", StringComparison.Ordinal);
            Write(repo, ".build/profile.json", emptyProfile);
            var failed = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);

            Assert.NotEqual(0, failed.Exit);
            Assert.Contains("both empty", failed.Stdout);
            Assert.Equal(0, sopCalls);
            foreach (var path in ownedPaths)
                Assert.Equal(before[path], ReadBytesIfPresent(repo, path));

            Write(repo, ".build/profile.json", ProfileJson);
            var corrected = await ExecuteStageAsync(
                repo, new InstallInvocation(".build/profile.json", null), dependencies);

            Assert.Equal(0, corrected.Exit);
            Assert.Equal("invariants_handoff", Stage(corrected.Stdout));
            Assert.Equal(1, sopCalls);
            Assert.Contains("platform = \"Vite\"",
                File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData("SwiftUI", "Swift", "SwiftUI")]
    [InlineData("", "Swift", "Swift")]
    public async Task ResolveGeneratedPlatform_UsesFrameworkThenLanguage(
        string framework, string language, string expected)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, ".build/conductor.toml", "[constellation]\nplatform = \"unknown\"\ncontract_authority = \"app\"\n");
            var result = InstallCommand.ResolveGeneratedPlatform(repo, framework, language);
            Assert.True(result.Success, result.Message);
            Assert.Contains($"platform = \"{expected}\"", File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveGeneratedPlatform_FailsWhenProfileValuesAreEmpty()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, ".build/conductor.toml", "[constellation]\nplatform = \"unknown\"\n");
            var result = InstallCommand.ResolveGeneratedPlatform(repo, "  ", "");
            Assert.False(result.Success);
            Assert.Contains("both empty", result.Message);
            Assert.Contains("platform = \"unknown\"", File.ReadAllText(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task ResolveGeneratedPlatform_PreservesExistingNonPlaceholderBytes()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string existing = "[constellation]\r\nplatform = \"ios-custom\"\r\ncontract_authority = \"app\"\r\n";
            Write(repo, ".build/conductor.toml", existing);
            var before = File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml"));
            var result = InstallCommand.ResolveGeneratedPlatform(repo, "SwiftUI", "Swift");
            Assert.True(result.Success, result.Message);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo, ".build/conductor.toml")));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Readiness_FromProtectedBranch_CommitsExactlyStubsAndIsIdempotent()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, StubPaths[0], "codex stub\n");
            Write(repo, StubPaths[1], "claude stub\n");

            var first = await ReadyAsync(repo);

            Assert.True(first.Success, first.Message);
            Assert.Equal("run/backlog", first.Branch);
            Assert.Equal(string.Empty, first.Porcelain);
            Assert.Equal("run/backlog", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(StubPaths.Order(StringComparer.Ordinal), committed.Order(StringComparer.Ordinal));

            var head = await GitAsync(repo, "rev-parse", "HEAD");
            var second = await ReadyAsync(repo);
            Assert.True(second.Success, second.Message);
            Assert.Equal(head, await GitAsync(repo, "rev-parse", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Readiness_CommitsOnlyProvablySetupOwnedGitignoreWithStubs(bool trackedAtHead)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            string? headGitignore = null;
            if (trackedAtHead)
            {
                headGitignore = "repository-specific/\n";
                Write(repo, ".gitignore", headGitignore);
                await GitAsync(repo, "add", ".gitignore");
                await GitAsync(repo, "commit", "-m", "add repository ignore");
            }
            Write(repo, ".gitignore", GitignoreManager.Merge(headGitignore)!);
            Write(repo, StubPaths[0], "codex stub\n");
            Write(repo, StubPaths[1], "claude stub\n");

            var result = await ReadyAsync(repo);

            Assert.True(result.Success, result.Message);
            var committed = (await GitAsync(repo, "show", "--pretty=", "--name-only", "HEAD"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(StubPaths.Append(".gitignore").Order(StringComparer.Ordinal),
                committed.Order(StringComparer.Ordinal));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_BlocksGitignoreContainingPreexistingUserChangePlusSetupMerge()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            const string head = "repository-specific/\n";
            Write(repo, ".gitignore", head);
            await GitAsync(repo, "add", ".gitignore");
            await GitAsync(repo, "commit", "-m", "add repository ignore");
            Write(repo, ".gitignore", GitignoreManager.Merge(head + "user-change/\n")!);
            var expected = await GitAsync(repo, "status", "--porcelain");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Equal(expected + "\n", result.Porcelain);
            Assert.Contains("differs from the deterministic build setup merge over HEAD", result.Message);
            Assert.Equal("main", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_DirtyTreeFailsWithExactPorcelainAndBranch()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            Write(repo, "unrelated.txt", "do not commit\n");
            var expected = await GitAsync(repo, "status", "--porcelain");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Equal("main", result.Branch);
            Assert.Equal(expected + "\n", result.Porcelain);
            Assert.Contains($"branch=main", result.Message);
            Assert.Contains(expected, result.Message);
            Assert.Equal("main", await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_MergeInProgressBlocks()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var gitDir = await GitAsync(repo, "rev-parse", "--git-dir");
            File.WriteAllText(Path.Combine(repo, gitDir, "MERGE_HEAD"), new string('a', 40) + "\n");

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Contains("interrupted merge", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Theory]
    [InlineData("rebase-merge")]
    [InlineData("rebase-apply")]
    public async Task Readiness_RebaseStateBlocks(string marker)
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var gitDir = await GitAsync(repo, "rev-parse", "--git-dir");
            Directory.CreateDirectory(Path.Combine(repo, gitDir, marker));

            var result = await ReadyAsync(repo);

            Assert.False(result.Success);
            Assert.Contains("interrupted rebase", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task Readiness_WorktreeQueryFailureBlocksReady()
    {
        var repo = await CreateRepositoryAsync();
        try
        {
            var result = await ReadyAsync(repo, _ => throw new IOException("lease root unavailable"));

            Assert.False(result.Success);
            Assert.Contains("worktree list query failed", result.Message);
            Assert.Contains("lease root unavailable", result.Message);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private static Task<InstallReadinessResult> ReadyAsync(
        string repo,
        Func<CancellationToken, Task>? query = null) =>
        InstallReadiness.PrepareAndAssertAsync(
            repo,
            "main",
            "run/backlog",
            StubPaths,
            ".worktrees/conductor",
            [],
            string.Empty,
            CancellationToken.None,
            query ?? (_ => Task.CompletedTask));

    private static InstallDependencies EndToEndDependencies(Action readinessCalled) => new(
        async (repo, _, diagnostics, ct) =>
        {
            Write(repo, ".build/config.toml", MinimalConfig);
            return await new SetupCommand(
                new FullyProvisionedFake(),
                new FileSystemLocalRepoOps(repo)).ExecuteAsync(false, new TestConsole(diagnostics), ct);
        },
        (repo, _, _) =>
        {
            var result = SopInstaller.Run(
                "install", repo, SopBundleCatalog.All, BuildVersion.Current, DateTimeOffset.UtcNow,
                conductorIdentity: new SopConductorIdentity("WEB", ["app", "public"]));
            return Task.FromResult(result.Passed ? 0 : 1);
        },
        repo => SopDoctorCommand.RunDoctor(repo, BuildVersion.Current),
        async (repo, protectedBranch, stubPaths, config, ct) =>
        {
            readinessCalled();
            return await InstallReadiness.PrepareAndAssertAsync(
                repo, protectedBranch, "run/backlog", stubPaths,
                config.Worktree.Root, config.Worktree.SeedFiles, config.Project.InstallCommand,
                ct, _ => Task.CompletedTask);
        });

    private static async Task<(int Exit, string Stdout, string Stderr)> ExecuteStageAsync(
        string repo, InstallInvocation invocation, InstallDependencies dependencies)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await InstallCommand.ExecuteAsync(
            invocation, true, repo, new StringReader(string.Empty), stdout, stderr,
            CancellationToken.None, dependencies);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Stage(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("data").GetProperty("stage").GetString()!;
    }

    private const string MinimalConfig = """
    [ticketing]
    backend = "plane"
    plane_base_url = "https://plane.invalid"
    plane_workspace_slug = "workspace"
    plane_project_id = "project-id"
    plane_project_identifier = "WEB"
    plane_api_token_env = "UNUSED_TOKEN"

    [llm]
    default_model = "unused"
    anthropic_api_key_env = "UNUSED_ANTHROPIC"

    [workers]
    default_agent = "codex"
    timeout_minutes = 5

    [workers.codex]
    executable = "codex"

    [workers.codex.sizes]
    small = { model = "unused" }
    medium = { model = "unused" }
    large = { model = "unused" }

    [events]
    log_directory = ".build/events"

    [ship]
    base_branch = "main"

    [worktree]
    root = ".worktrees/conductor"
    """;

    private sealed class FullyProvisionedFake : ITicketingProvisioner
    {
        public Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingState>>(WorkspaceSchema.States
                .Select((state, index) => new ExistingState(state.Name, state.Group, index)).ToList());
        public Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(WorkspaceSchema.Labels.ToList());
        public Task CreateStateAsync(string name, string group, double sequence, CancellationToken ct) =>
            throw new InvalidOperationException("already provisioned");
        public Task CreateLabelAsync(string name, CancellationToken ct) =>
            throw new InvalidOperationException("already provisioned");
    }

    private sealed class TestConsole(TextWriter diagnostics) : IConsole
    {
        public bool IsInputRedirected => false;
        public string? ReadLine() => null;
        public char? ReadKeyChar() => null;
        public void Write(string value) => diagnostics.Write(value);
        public void WriteLine(string value) => diagnostics.WriteLine(value);
        public void ErrorWriteLine(string value) => diagnostics.WriteLine(value);
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var repo = Path.Combine(Path.GetTempPath(), "install-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init", "-b", "main");
        await GitAsync(repo, "config", "user.name", "Install Test");
        await GitAsync(repo, "config", "user.email", "install@example.invalid");
        Write(repo, "README.md", "non-dotnet fixture\n");
        await GitAsync(repo, "add", "README.md");
        await GitAsync(repo, "commit", "-m", "initial");
        return repo;
    }

    private static void Write(string repo, string relativePath, string content)
    {
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static byte[]? ReadBytesIfPresent(string repo, string relativePath)
    {
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static async Task<string> GitAsync(string repo, params string[] args)
    {
        var result = await new InstallGit(repo).RunAsync(CancellationToken.None, args);
        Assert.True(result.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout.TrimEnd('\r', '\n');
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
