using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for SetupCommand: local-repo readiness (git init + .gitignore), Plane schema diff,
/// create, idempotency, and the --check (verify-only) exit code. All fakes are in-memory.
/// </summary>
public class SetupCommandTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeConsole : IConsole
    {
        private readonly System.Text.StringBuilder _out = new();
        private readonly System.Text.StringBuilder _err = new();
        public string Stdout => _out.ToString();
        public string Stderr => _err.ToString();
        public bool IsInputRedirected => true;
        public void WriteLine(string value) => _out.AppendLine(value);
        public void Write(string value) => _out.Append(value);
        public void ErrorWriteLine(string value) => _err.AppendLine(value);
        public string? ReadLine() => null;
        public char? ReadKeyChar() => null;
    }

    private sealed class FakeLocalRepo : ILocalRepoOps
    {
        private bool _isRepo;
        private bool _hasCommits;
        public string? Gitignore;
        public int InitCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public string[]? LastCommitPaths { get; private set; }
        public string? LastCommitMessage { get; private set; }

        public FakeLocalRepo(bool isRepo, string? gitignore, bool hasCommits = false)
        {
            _isRepo = isRepo;
            Gitignore = gitignore;
            _hasCommits = hasCommits;
        }

        public bool IsGitRepository() => _isRepo;
        public void GitInit() { InitCalls++; _isRepo = true; }
        public string? ReadGitignore() => Gitignore;
        public void WriteGitignore(string content) { WriteCalls++; Gitignore = content; }
        public bool HasAnyCommits() => _hasCommits;
        public void StageAndCommit(string[] paths, string message)
        {
            CommitCalls++;
            LastCommitPaths = paths;
            LastCommitMessage = message;
            _hasCommits = true;
        }
    }

    // A local repo that is already initialized and already carries every standard ignore entry,
    // so a test focused on the Plane half exercises no local-repo work.
    private static FakeLocalRepo ReadyRepo() =>
        new(isRepo: true, gitignore: string.Join("\n", GitignoreManager.RequiredEntries) + "\n");

    private sealed class FakeProvisioner : ITicketingProvisioner
    {
        private readonly List<ExistingState> _states;
        private readonly List<string> _labels;
        public int StateCreates { get; private set; }
        public int LabelCreates { get; private set; }

        public FakeProvisioner(IEnumerable<ExistingState> states, IEnumerable<string> labels)
        {
            _states = states.ToList();
            _labels = labels.ToList();
        }

        public Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingState>>(_states.ToList());

        public Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_labels.ToList());

        public Task CreateStateAsync(string name, string group, double sequence, CancellationToken ct)
        {
            _states.Add(new ExistingState(name, group, sequence));
            StateCreates++;
            return Task.CompletedTask;
        }

        public Task CreateLabelAsync(string name, CancellationToken ct)
        {
            _labels.Add(name);
            LabelCreates++;
            return Task.CompletedTask;
        }
    }

    // Plane's stock states for a brand-new project (missing Planning, Ready, In Review).
    private static FakeProvisioner FreshProject() => new(
        states: new[]
        {
            new ExistingState("Backlog", "backlog", 1),
            new ExistingState("Todo", "unstarted", 2),
            new ExistingState("In Progress", "started", 3),
            new ExistingState("Done", "completed", 4),
            new ExistingState("Cancelled", "cancelled", 5),
        },
        labels: System.Array.Empty<string>());

    private static FakeProvisioner FullyProvisioned() => new(
        states: WorkspaceSchema.States.Select((s, i) => new ExistingState(s.Name, s.Group, i)).ToList(),
        labels: WorkspaceSchema.Labels.ToList());

    // ------------------------------------------------------------------ Plane half

    [Fact]
    public async Task FreshProject_CreatesMissingStatesAndLabels_ReturnsZero()
    {
        var fake = FreshProject();
        var console = new FakeConsole();

        var code = await new SetupCommand(fake, ReadyRepo()).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(3, fake.StateCreates);
        Assert.Equal(WorkspaceSchema.Labels.Count, fake.LabelCreates);
        Assert.Contains("created state: Planning", console.Stdout);
        Assert.Contains("created label: risk:low", console.Stdout);
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_CreatesNothing()
    {
        var fake = FreshProject();
        await new SetupCommand(fake, ReadyRepo()).ExecuteAsync(checkOnly: false, new FakeConsole(), CancellationToken.None);
        var before = (fake.StateCreates, fake.LabelCreates);

        var console = new FakeConsole();
        var code = await new SetupCommand(fake, ReadyRepo()).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(before, (fake.StateCreates, fake.LabelCreates));
        Assert.Contains("meets criteria", console.Stdout);
    }

    [Fact]
    public async Task CheckOnly_WithGaps_ReturnsOne_CreatesNothing()
    {
        var fake = FreshProject();
        var console = new FakeConsole();

        var code = await new SetupCommand(fake, ReadyRepo()).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(0, fake.StateCreates);
        Assert.Equal(0, fake.LabelCreates);
        Assert.Contains("does NOT meet criteria", console.Stderr);
        Assert.Contains("missing state: Planning", console.Stderr);
        Assert.Contains("missing label: risk:low", console.Stderr);
    }

    [Fact]
    public async Task CheckOnly_WhenComplete_ReturnsZero()
    {
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), ReadyRepo()).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("meets criteria", console.Stdout);
    }

    [Fact]
    public async Task NewStateSequences_ContinuePastHighestExisting()
    {
        var fake = FreshProject();
        await new SetupCommand(fake, ReadyRepo()).ExecuteAsync(checkOnly: false, new FakeConsole(), CancellationToken.None);

        var created = (await fake.ListStatesAsync(CancellationToken.None))
            .Where(s => s.Name is "Planning" or "Ready" or "In Review")
            .ToList();

        Assert.Equal(3, created.Count);
        Assert.All(created, s => Assert.True(s.Sequence > 5));
    }

    // ------------------------------------------------------------------ local-repo half

    [Fact]
    public async Task NonGitDir_RunsGitInitAndWritesGitignore()
    {
        var repo = new FakeLocalRepo(isRepo: false, gitignore: null);
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(1, repo.InitCalls);
        Assert.Equal(1, repo.WriteCalls);
        Assert.NotNull(repo.Gitignore);
        Assert.Contains(".build/*.md", repo.Gitignore!);
        Assert.Contains("initialized empty repository", console.Stdout);
    }

    [Fact]
    public async Task ExistingRepoWithFullGitignore_DoesNotInitOrWrite()
    {
        var repo = ReadyRepo();

        await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, new FakeConsole(), CancellationToken.None);

        Assert.Equal(0, repo.InitCalls);
        Assert.Equal(0, repo.WriteCalls);
    }

    [Fact]
    public async Task PartialGitignore_AppendsOnlyMissing_PreservesExisting()
    {
        var repo = new FakeLocalRepo(isRepo: true, gitignore: "node_modules/\n.build/*.md\n");
        var console = new FakeConsole();

        await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(1, repo.WriteCalls);
        Assert.Contains("node_modules/", repo.Gitignore!);      // existing content preserved
        Assert.Contains(".worktrees/", repo.Gitignore!);        // a missing entry appended
        Assert.DoesNotContain("added 0 entr", console.Stdout);  // .build/*.md was already present, others missing
    }

    [Fact]
    public async Task CheckOnly_NonGitDir_ReportsGap_ReturnsOne_NoMutation()
    {
        var repo = new FakeLocalRepo(isRepo: false, gitignore: null);
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(1, code);                       // local gap fails --check even though Plane is complete
        Assert.Equal(0, repo.InitCalls);
        Assert.Equal(0, repo.WriteCalls);
        Assert.Contains("not a git repository", console.Stderr);
        Assert.Contains(".gitignore", console.Stderr);
    }

    // ------------------------------------------------------------------ TLB-627: tracked-token safety

    [Fact]
    public async Task CheckOnly_ConfigHasLiteralToken_ReportsGap_ReturnsOne()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configPath = Path.Combine(dir, "config.toml");
            File.WriteAllText(configPath, "[ticketing]\nplane_api_token = \"plane_api_live_secret\"\n");
            var console = new FakeConsole();

            var code = await new SetupCommand(FullyProvisioned(), ReadyRepo(), configPath)
                .ExecuteAsync(checkOnly: true, console, CancellationToken.None);

            Assert.Equal(1, code);
            Assert.Contains("plane_api_token", console.Stderr);
            Assert.DoesNotContain("plane_api_live_secret", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NotCheckOnly_ConfigHasLiteralToken_WarnsButDoesNotRewriteFile()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configPath = Path.Combine(dir, "config.toml");
            var original = "[ticketing]\nplane_api_token = \"plane_api_live_secret\"\n";
            File.WriteAllText(configPath, original);
            var console = new FakeConsole();

            var code = await new SetupCommand(FullyProvisioned(), ReadyRepo(), configPath)
                .ExecuteAsync(checkOnly: false, console, CancellationToken.None);

            Assert.Equal(0, code); // non-checkOnly always returns 0; the warning does not fail the run
            Assert.Contains("plane_api_token", console.Stderr);
            Assert.Equal(original, File.ReadAllText(configPath)); // never rewritten
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CheckOnly_ConfigUsesEnvVarForm_NoGapFromTokenScan()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configPath = Path.Combine(dir, "config.toml");
            File.WriteAllText(configPath, "[ticketing]\nplane_api_token_env = \"PLANE_API_TOKEN\"\n");
            var console = new FakeConsole();

            var code = await new SetupCommand(FullyProvisioned(), ReadyRepo(), configPath)
                .ExecuteAsync(checkOnly: true, console, CancellationToken.None);

            Assert.Equal(0, code);
            Assert.DoesNotContain("plane_api_token", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NullConfigPath_SkipsTokenScan()
    {
        // Existing call sites (configPath omitted) must keep working unaffected.
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), ReadyRepo())
            .ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.DoesNotContain("plane_api_token", console.Stderr);
    }

    // ------------------------------------------------------------------ WI-05: welcome commit

    [Fact]
    public async Task FreshRepo_GetsWelcomeCommitOfGitignoreOnly()
    {
        var repo = new FakeLocalRepo(isRepo: false, gitignore: null, hasCommits: false);
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(1, repo.CommitCalls);
        Assert.Equal(WelcomeCommit.Message, repo.LastCommitMessage);
        Assert.Contains(".gitignore", repo.LastCommitPaths!);
        Assert.DoesNotContain(".build/config.toml", repo.LastCommitPaths!);
        Assert.Contains("committed .gitignore", console.Stdout);
    }

    [Fact]
    public async Task ExistingRepoWithCommits_GetsNoSecondBootstrapCommit()
    {
        var repo = new FakeLocalRepo(isRepo: true,
            gitignore: string.Join("\n", GitignoreManager.RequiredEntries) + "\n",
            hasCommits: true);
        var console = new FakeConsole();

        var code = await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(0, repo.CommitCalls);
    }

    [Fact]
    public async Task CheckOnly_MakesNoWelcomeCommit()
    {
        var repo = new FakeLocalRepo(isRepo: false, gitignore: null, hasCommits: false);
        var console = new FakeConsole();

        // --check on a fresh repo reports gaps (returns 1) but must mutate nothing - no commit.
        var code = await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(0, repo.CommitCalls);
    }
}
