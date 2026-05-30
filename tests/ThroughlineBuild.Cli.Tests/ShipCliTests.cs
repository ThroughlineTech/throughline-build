using System.Diagnostics;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ShipCliTests
{
    // --debug flag tests: verify that --debug is accepted on all four verbs without parse errors.
    // These tests use the in-process CLI argument parser (CliUsage) to confirm usage text,
    // and WorkerOptions construction to confirm the flag parses correctly.

    [Fact]
    public void UsageText_ContainsDebugFlag()
    {
        Assert.Contains("--debug", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_MentionsSummaryJsonFlagAndContract()
    {
        // TLB-123
        Assert.Contains("--summary-json", CliUsage.UsageText);
        Assert.Contains("Summary contract:", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DebugFlagDocumentedForPlanImplementReview()
    {
        // The usage line for plan/implement/review must accept --debug (and after
        // TLB-122 also --quiet, declared as the [--debug|--quiet] pair).
        // TLB-191 added [--agent <name>] before the debug/quiet flags.
        // TLB-310 added multi-ticket support.
        Assert.Contains("build plan <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet]", CliUsage.UsageText);
        Assert.Contains("build implement <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet]", CliUsage.UsageText);
        Assert.Contains("build review <ticket-id> [ticket-id ...] [--agent <name>] [--debug|--quiet]", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_ShipDebugNoOpDocumented()
    {
        // Ship usage line must mention --debug is a no-op for ship.
        Assert.Contains("no-op", CliUsage.UsageText);
    }

    [Fact]
    public void WorkerOptions_DebugCaptureDirectory_CanBeSet()
    {
        // Verify WorkerOptions accepts DebugCaptureDirectory as an optional named param.
        var opts = new WorkerOptions(TimeSpan.FromMinutes(5), DebugCaptureDirectory: "/tmp/capture");
        Assert.Equal("/tmp/capture", opts.DebugCaptureDirectory);
    }

    [Fact]
    public void WorkerOptions_DebugCaptureDirectory_DefaultsToNull()
    {
        var opts = new WorkerOptions(TimeSpan.FromMinutes(5));
        Assert.Null(opts.DebugCaptureDirectory);
    }

    [Fact]
    public void BuildOptions_DebugCaptureDirectory_CanBeSet()
    {
        var opts = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5),
            DebugCaptureDirectory: "/tmp/capture");
        Assert.Equal("/tmp/capture", opts.DebugCaptureDirectory);
    }

    [Fact]
    public void BuildOptions_DebugCaptureDirectory_DefaultsToNull()
    {
        var opts = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5));
        Assert.Null(opts.DebugCaptureDirectory);
    }

    [Fact]
    public void UsageText_ContainsShipVerb()
    {
        Assert.Contains("ship", CliUsage.UsageText);
        Assert.Contains("build ship <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsNoPushConvention()
    {
        // The ship verb help line must document the local-only / no-push convention.
        Assert.Contains("no push", CliUsage.UsageText);
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
    public void ShipPhase_AcceptsExpectedDependencyShape()
    {
        var ticketing = new StubTicketing();
        var events = new StubSink();
        var options = new BuildOptions("sid", "claude-code", TimeSpan.FromMinutes(5));
        var shipOptions = new ShipOptions(
            RegressionChecks: Array.Empty<CheckSpec>(),
            Remote: "origin",
            BaseBranch: "main",
            DeleteFeatureBranch: true);

        var phase = new ShipPhase(ticketing, events, options, shipOptions);

        Assert.Equal(Phase.Ship, phase.Phase);
    }

    [Fact]
    public async Task BuildBinary_ShipWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "ship" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build ship <ticket-id>", stderr);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsShipVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build ship <ticket-id>", stdout);
    }

    [Fact]
    public void ConfigExampleFile_ContainsShipSection()
    {
        var examplePath = FindConfigExampleFile();
        Assert.NotNull(examplePath);

        var content = File.ReadAllText(examplePath!);
        Assert.Contains("[ship]", content);
        Assert.Contains("[[ship.regression_checks]]", content);
        // Must include the no-push comment per the brief.
        Assert.Contains("no push", content);
    }

    private static string? FindConfigExampleFile()
    {
        var here = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var candidate = Path.Combine(dir.FullName, ".build", "config.toml.example");
        return File.Exists(candidate) ? candidate : null;
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

        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", "Debug", "net8.0");
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

    private sealed class StubSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
