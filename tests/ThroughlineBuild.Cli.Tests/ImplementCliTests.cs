using System.Diagnostics;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ImplementCliTests
{
    [Fact]
    public void UsageText_ContainsImplementVerb()
    {
        Assert.Contains("implement", CliUsage.UsageText);
        Assert.Contains("build implement <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_ListsAllExitCodes()
    {
        Assert.Contains("0  Success", CliUsage.UsageText);
        Assert.Contains("1  Phase or command failure", CliUsage.UsageText);
        Assert.Contains("2  Config error or unknown verb", CliUsage.UsageText);
        Assert.Contains("3  Missing secret", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsImplementOnSameStyleAsPlan()
    {
        // Both verbs take a ticket-id and no other required flags.
        var planLine = "build plan <ticket-id>";
        var implementLine = "build implement <ticket-id>";
        Assert.Contains(planLine, CliUsage.UsageText);
        Assert.Contains(implementLine, CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsQuietFlag()
    {
        // --quiet must appear in both the verb usage and the Flags block, and the
        // digest behavior must be documented (BUILD_PROGRESS env var, TTY rule).
        Assert.Contains("--quiet", CliUsage.UsageText);
        Assert.Contains("--debug|--quiet", CliUsage.UsageText);
        Assert.Contains("Progress digest", CliUsage.UsageText);
        Assert.Contains("BUILD_PROGRESS", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_MentionsSummaryJsonFlagAndContract()
    {
        // TLB-123: --summary-json flag and the summary-contract paragraph must be documented.
        Assert.Contains("--summary-json", CliUsage.UsageText);
        Assert.Contains("Summary contract:", CliUsage.UsageText);
    }

    [Fact]
    public void ImplementPhase_AcceptsSameDependencyShapeAsPlanPhase()
    {
        // Both phases must construct from the same shape so the CLI's
        // ticketing / worker / sink / options bundle works for either path.
        var ticketing = new StubTicketing();
        var worker = new StubWorker();
        var events = new StubSink();
        var options = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5));

        var planPhase = new PlanPhase(ticketing, worker, events, options);
        var implementPhase = new ImplementPhase(ticketing, worker, events, options);

        Assert.Equal(Phase.Plan, planPhase.Phase);
        Assert.Equal(Phase.Implement, implementPhase.Phase);
    }

    [Fact]
    public async Task BuildBinary_NoArgs_PrintsUsageAndExitsZero()
    {
        var exe = LocateBuildExecutable();
        if (exe is null)
        {
            // Skip on CI shapes that haven't published the binary yet.
            return;
        }

        var (exitCode, stdout, _) = await RunProcess(exe, Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Contains("implement", stdout);
        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsImplementVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build implement <ticket-id>", stdout);
    }

    [Fact]
    public async Task BuildBinary_ImplementWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "implement" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build implement <ticket-id>", stderr);
    }

    private static string? LocateBuildExecutable()
    {
        var here = AppContext.BaseDirectory;
        // Walk up from the test assembly to the repo root.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", "Debug", "net8.0");
        var exeName = OperatingSystem.IsWindows() ? "build.exe" : "build";
        var fullPath = Path.Combine(binDir, exeName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcess(string exe, string[] args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed class StubTicketing : ITicketing
    {
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubWorker : IWorkerAgent
    {
        public string Name => "stub";
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class StubSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
