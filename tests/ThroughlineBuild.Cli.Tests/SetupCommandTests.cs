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
        public string? Gitignore;
        public int InitCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public FakeLocalRepo(bool isRepo, string? gitignore)
        {
            _isRepo = isRepo;
            Gitignore = gitignore;
        }

        public bool IsGitRepository() => _isRepo;
        public void GitInit() { InitCalls++; _isRepo = true; }
        public string? ReadGitignore() => Gitignore;
        public void WriteGitignore(string content) { WriteCalls++; Gitignore = content; }
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
        Assert.Contains(".build/brief.md", repo.Gitignore!);
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
        var repo = new FakeLocalRepo(isRepo: true, gitignore: "node_modules/\n.build/brief.md\n");
        var console = new FakeConsole();

        await new SetupCommand(FullyProvisioned(), repo).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(1, repo.WriteCalls);
        Assert.Contains("node_modules/", repo.Gitignore!);      // existing content preserved
        Assert.Contains(".worktrees/", repo.Gitignore!);        // a missing entry appended
        Assert.DoesNotContain("added 0 entr", console.Stdout);  // brief.md was already present
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
}
