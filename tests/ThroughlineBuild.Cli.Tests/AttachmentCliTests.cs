using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class AttachmentCliTests
{
    private const string AssetId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task ExistingOutputIsRejectedBeforePlaneOrStorageAndIsNotOverwritten()
    {
        var repository = await CreateConfiguredRepositoryAsync();
        var outputPath = Path.Combine(repository, "existing.bin");
        var original = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(outputPath, original);

        try
        {
            var result = await RunCliInDirectoryAsync(
                repository,
                ["attachment", "TLB-24", AssetId, "--output", outputPath, "--json"]);

            Assert.True(
                result.Exit == 1,
                $"Expected exit 1 but got {result.Exit}. stdout: {result.Stdout} stderr: {result.Stderr}");
            Assert.Equal(original, File.ReadAllBytes(outputPath));
            Assert.Equal(string.Empty, result.Stderr);
            using var json = JsonDocument.Parse(result.Stdout);
            Assert.Equal(
                CliErrorCodes.Failure,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(
                "Output path already exists.",
                json.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        finally
        {
            TryDeleteDirectory(repository);
        }
    }

    [Fact]
    public async Task CancelledLocalWriteRemovesTempAndNeverCreatesPartialOutput()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "attachment-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "result.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var method = typeof(CliApplication).GetMethod(
                "WriteAttachmentAtomicallyAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task>(method.Invoke(
                null,
                [outputPath, new byte[1024], cts.Token]));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(directory, ".result.bin.*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task<string> CreateConfiguredRepositoryAsync()
    {
        var repository = Path.Combine(
            Path.GetTempPath(), "attachment-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repository, ".build"));
        await RunGitAsync(repository, "init");
        File.WriteAllText(
            Path.Combine(repository, ".build", "config.toml"),
            """
            [ticketing]
            backend = "plane"
            plane_base_url = "http://127.0.0.1:1"
            plane_workspace_slug = "test-workspace"
            plane_project_id = "test-project"
            plane_project_identifier = "TLB"
            plane_api_token = "test-token"

            [llm]
            default_model = "anthropic:test"
            anthropic_api_key = "unused"

            [workers]
            default_agent = "claude-code"

            [workers.claude-code]
            executable = "unused"

            [workers.claude-code.sizes]
            small = { model = "haiku" }
            medium = { model = "sonnet" }
            large = { model = "opus" }

            [events]
            log_directory = ".build/events"
            """);
        return repository;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCliInDirectoryAsync(
        string repository,
        string[] args)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Directory.SetCurrentDirectory(repository);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"),
                new InProcessCliConsole(TextReader.Null, stdout, stderr));
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
