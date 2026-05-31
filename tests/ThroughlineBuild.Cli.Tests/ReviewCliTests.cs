using System.Diagnostics;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ReviewCliTests
{
    [Fact]
    public void UsageText_ContainsReviewVerb()
    {
        Assert.Contains("review", CliUsage.UsageText);
        Assert.Contains("build review <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_ListsAllExitCodes()
    {
        Assert.Contains("0  Success", CliUsage.UsageText);
        Assert.Contains("1  Phase or command failure", CliUsage.UsageText);
        Assert.Contains("2  Config error or unknown verb", CliUsage.UsageText);
        Assert.Contains("3  Missing secret", CliUsage.UsageText);
        Assert.Contains("4  Phase infrastructure failure", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsReviewOnSameStyleAsPlan()
    {
        // Both verbs take a ticket-id and no other required flags.
        var planLine = "build plan <ticket-id>";
        var reviewLine = "build review <ticket-id>";
        Assert.Contains(planLine, CliUsage.UsageText);
        Assert.Contains(reviewLine, CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_MentionsSummaryJsonFlag()
    {
        // TLB-123
        Assert.Contains("--summary-json", CliUsage.UsageText);
    }

    [Fact]
    public void ReviewPhase_AcceptsExpectedDependencyShape()
    {
        var ticketing = new StubTicketing();
        var worker = new StubWorker();
        var events = new StubSink();
        var options = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5));
        var workerOptions = new WorkerOptions(TimeSpan.FromMinutes(15), new List<string> { "Read", "Grep", "Glob" }.AsReadOnly());
        var reviewOptions = new ReviewOptions(Array.Empty<CheckSpec>(), workerOptions);

        var phase = new ReviewPhase(ticketing, worker, events, options, reviewOptions);

        Assert.Equal(Phase.Review, phase.Phase);
    }

    [Fact]
    public async Task BuildBinary_ReviewWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "review" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build review <ticket-id>", stderr);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsReviewVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build review <ticket-id>", stdout);
    }

    private static string? LocateBuildExecutable()
    {
        var here = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var config = here.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", config, "net8.0");
        var exeName = OperatingSystem.IsWindows() ? "build.exe" : "build";
        var fullPath = Path.Combine(binDir, exeName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcess(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class StubWorker : IWorkerAgent
    {
        public string Name => "stub";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class StubSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
