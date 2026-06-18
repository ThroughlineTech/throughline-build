using ThroughlineBuild.ClaudeCode;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.ClaudeCode.Tests;

public sealed class ClaudeCodeClientOptionsTests
{
    [Fact]
    public void ClientDefaultsFavorInteractiveLibraryUse()
    {
        var worker = new ClaudeCodeClientOptions().ToWorkerOptions();

        Assert.Equal(ClaudeCodeTransport.InteractiveHook, worker.Transport);
        Assert.True(worker.BypassPermissions);
        Assert.False(worker.EnableStopHook);
        Assert.Null(worker.StopHookCommandPrefix);
    }

    [Fact]
    public void ClientOptionsMapToWorkerOptions()
    {
        var sizes = new Dictionary<WorkerSize, ModelTier>
        {
            [WorkerSize.Small] = new("claude-haiku-4-5")
        };

        var worker = new ClaudeCodeClientOptions
        {
            ExecutablePath = "custom-claude",
            Transport = ClaudeCodeTransportMode.Print,
            BypassPermissions = false,
            EnableStopHook = true,
            StopHookCommandPrefix = ["host.exe", "claude-hook"],
            ExtraArgs = ["--append-system-prompt", "extra"],
            MaxOutputTokens = 8192,
            Sizes = sizes,
        }.ToWorkerOptions();

        Assert.Equal("custom-claude", worker.ExecutablePath);
        Assert.Equal(ClaudeCodeTransport.Print, worker.Transport);
        Assert.False(worker.BypassPermissions);
        Assert.True(worker.EnableStopHook);
        Assert.Equal(new[] { "host.exe", "claude-hook" }, worker.StopHookCommandPrefix);
        Assert.Equal(new[] { "--append-system-prompt", "extra" }, worker.ExtraArgs);
        Assert.Equal(8192, worker.MaxOutputTokens);
        Assert.Same(sizes, worker.Sizes);
    }
}
