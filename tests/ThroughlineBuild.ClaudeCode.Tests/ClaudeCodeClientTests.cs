using ThroughlineBuild.ClaudeCode;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.ClaudeCode.Tests;

public sealed class ClaudeCodeClientTests
{
    [Fact]
    public void WorkerResultContract_AppendsOnlyWhenMissing()
    {
        var instruction = "Edit the README.";

        var withContract = ClaudeCodeWorkerResultContract.EnsurePresent(instruction);
        var secondPass = ClaudeCodeWorkerResultContract.EnsurePresent(withContract);

        Assert.Contains("Edit the README.", withContract);
        Assert.Contains(ClaudeCodeWorkerResultContract.Marker, withContract);
        Assert.Equal(withContract, secondPass);
    }

    [Fact]
    public void BuildBrief_UsesRunMetadata()
    {
        var options = new ClaudeCodeRunOptions
        {
            TicketId = "LIB-1",
            Phase = Phase.Implement,
            RelevantFiles = ["README.md"],
            AllowedWrites = ["README.md"],
            Context = new Dictionary<string, string> { ["source"] = "test" },
        };

        var brief = ClaudeCodeClient.BuildBrief("do the task", options);

        Assert.Equal("LIB-1", brief.TicketId);
        Assert.Equal(Phase.Implement, brief.Phase);
        Assert.Equal("do the task", brief.Instruction);
        Assert.Equal(new[] { "README.md" }, brief.RelevantFiles);
        Assert.Equal(new[] { "README.md" }, brief.AllowedWrites);
        Assert.Equal("test", brief.Context["source"]);
    }

    [Fact]
    public void RunOptions_MapToWorkerOptions()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var progress = new StringWriter();
        var environment = new Dictionary<string, string> { ["KEY"] = "VALUE" };

        var worker = new ClaudeCodeRunOptions
        {
            Timeout = TimeSpan.FromSeconds(12),
            AllowedTools = ["Read"],
            EnvironmentVariables = environment,
            DebugCaptureDirectory = "debug",
            LiveStdoutSink = stdout,
            LiveStderrSink = stderr,
            ProgressDigestSink = progress,
            Size = WorkerSize.Large,
            LeanPlanning = true,
        }.ToWorkerOptions();

        Assert.Equal(TimeSpan.FromSeconds(12), worker.Timeout);
        Assert.Equal(new[] { "Read" }, worker.AllowedTools);
        Assert.Same(environment, worker.EnvironmentVariables);
        Assert.Equal("debug", worker.DebugCaptureDirectory);
        Assert.Same(stdout, worker.LiveStdoutSink);
        Assert.Same(stderr, worker.LiveStderrSink);
        Assert.Same(progress, worker.ProgressDigestSink);
        Assert.Equal(WorkerSize.Large, worker.Size);
        Assert.True(worker.LeanPlanning);
    }
}
