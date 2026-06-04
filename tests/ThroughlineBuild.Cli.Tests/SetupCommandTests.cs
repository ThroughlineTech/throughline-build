using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for SetupCommand: diff against WorkspaceSchema, create missing states/labels,
/// idempotency, and the --check (verify-only) exit code. Uses an in-memory fake provisioner.
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

    /// <summary>
    /// In-memory provisioner. Created states/labels are added to the live set so a second
    /// ExecuteAsync run observes them - this is what exercises idempotency.
    /// </summary>
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

    // Plane's stock states for a brand-new project: Backlog/In Progress/Done/Cancelled plus a
    // default "Todo". Missing vs WorkspaceSchema: Planning, Ready, In Review.
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

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task FreshProject_CreatesMissingStatesAndLabels_ReturnsZero()
    {
        var fake = FreshProject();
        var console = new FakeConsole();

        var code = await new SetupCommand(fake).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(3, fake.StateCreates);                       // Planning, Ready, In Review
        Assert.Equal(WorkspaceSchema.Labels.Count, fake.LabelCreates); // all 9 labels
        Assert.Contains("created state: Planning", console.Stdout);
        Assert.Contains("created label: risk:low", console.Stdout);
        Assert.Contains("Setup complete", console.Stdout);
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_CreatesNothing()
    {
        var fake = FreshProject();

        await new SetupCommand(fake).ExecuteAsync(checkOnly: false, new FakeConsole(), CancellationToken.None);
        var before = (fake.StateCreates, fake.LabelCreates);

        var console = new FakeConsole();
        var code = await new SetupCommand(fake).ExecuteAsync(checkOnly: false, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(before, (fake.StateCreates, fake.LabelCreates)); // no new creates on rerun
        Assert.Contains("meets criteria", console.Stdout);
    }

    [Fact]
    public async Task CheckOnly_WithGaps_ReturnsOne_CreatesNothing()
    {
        var fake = FreshProject();
        var console = new FakeConsole();

        var code = await new SetupCommand(fake).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

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
        var fake = FullyProvisioned();
        var console = new FakeConsole();

        var code = await new SetupCommand(fake).ExecuteAsync(checkOnly: true, console, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(0, fake.StateCreates);
        Assert.Contains("meets criteria", console.Stdout);
    }

    [Fact]
    public async Task NewStateSequences_ContinuePastHighestExisting()
    {
        // Existing max sequence is 5 (Cancelled); created states should sort after it.
        var fake = FreshProject();
        await new SetupCommand(fake).ExecuteAsync(checkOnly: false, new FakeConsole(), CancellationToken.None);

        var created = (await fake.ListStatesAsync(CancellationToken.None))
            .Where(s => s.Name is "Planning" or "Ready" or "In Review")
            .ToList();

        Assert.Equal(3, created.Count);
        Assert.All(created, s => Assert.True(s.Sequence > 5));
    }
}
