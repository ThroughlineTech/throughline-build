using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class WorktreeCommandTests
{
    [Fact]
    public async Task ListJsonEmitsStandardEnvelopeWithoutUsingWorkerOrTicketing()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "unmanifested"));
        try
        {
            var manager = CreateManager(root);
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "list"],
                json: true,
                manager,
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("data").GetProperty("leases").EnumerateArray());
            Assert.Single(json.RootElement.GetProperty("data")
                .GetProperty("unmanifestedDirectories").EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeaseMissingTicketEmitsUsageEnvelopeAndExitTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-command-tests", Guid.NewGuid().ToString("N"));
        var output = new StringWriter();

        var exit = await WorktreeCommand.ExecuteAsync(
            ["worktree", "lease", "--slug", "missing-ticket"],
            json: true,
            CreateManager(root),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "usage",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static WorktreeLeaseManager CreateManager(string root)
    {
        var repository = Directory.GetCurrentDirectory();
        return new WorktreeLeaseManager(
            new ProcessGitClient(repository),
            new ProcessInstallCommandRunner(),
            new WorktreeLeaseOptions(
                repository,
                repository,
                root,
                Array.Empty<string>(),
                string.Empty));
    }
}
