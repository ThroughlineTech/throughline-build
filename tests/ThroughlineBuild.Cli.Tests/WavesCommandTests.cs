using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class WavesCommandTests
{
    [Fact]
    public async Task JsonObjectInputEmitsStandardSourceGeneratedEnvelope()
    {
        AppContext.SetSwitch(
            "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault",
            false);
        var input = new StringReader(
            """
            {
              "cap": 3,
              "verifiedExternalDeps": ["TEST-99"],
              "tickets": [
                {"id":"TEST-2","files":["src/a.cs"],"deps":["TEST-99"]},
                {"id":"TEST-10","files":["docs/b.md"],"deps":[]}
              ]
            }
            """);
        var output = new StringWriter();

        var exit = await WavesCommand.ExecuteAsync(
            ["waves", "--input", "-"],
            json: true,
            WavesConfig.Default,
            input,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var data = root.GetProperty("data");
        Assert.Equal(3, data.GetProperty("cap").GetInt32());
        Assert.Equal("TEST-99", data.GetProperty("verifiedExternalDeps")[0].GetString());
        Assert.Equal(2, data.GetProperty("waves")[0].GetProperty("tickets").GetArrayLength());
        Assert.Equal("parallelism available", data.GetProperty("speedup").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task TicketArrayUsesConfiguredCapAndRules()
    {
        var config = new WavesConfig(
            8,
            [new WaveSerializeRule(WaveSerializeKind.Global, ["migrations/**"])]);
        var output = new StringWriter();

        var exit = await WavesCommand.ExecuteAsync(
            ["waves", "--input", "-"],
            json: true,
            config,
            new StringReader(
                """
                [
                  {"id":"TEST-1","files":["migrations/001.sql"],"deps":[]},
                  {"id":"TEST-2","files":["docs/readme.md"],"deps":[]}
                ]
                """),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(8, data.GetProperty("cap").GetInt32());
        Assert.Equal(2, data.GetProperty("waves").GetArrayLength());
        var reason = data.GetProperty("conflicts")[0].GetProperty("reasons")[0];
        Assert.Equal("global", reason.GetProperty("rule").GetString());
        Assert.Equal("migrations/001.sql", reason.GetProperty("path").GetString());
        Assert.Equal("migrations/**", reason.GetProperty("pattern").GetString());
    }

    [Fact]
    public async Task CycleHasDistinctExitAndErrorCode()
    {
        var output = new StringWriter();

        var exit = await WavesCommand.ExecuteAsync(
            ["waves", "--input", "-"],
            json: true,
            WavesConfig.Default,
            new StringReader(
                """
                [
                  {"id":"TEST-1","files":["a"],"deps":["TEST-2"]},
                  {"id":"TEST-2","files":["b"],"deps":["TEST-1"]}
                ]
                """),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(WavesCommand.DependencyCycleExitCode, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "dependency_cycle",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tickets\":[],\"cap\":0}")]
    [InlineData("[{\"id\":\"TEST-1\",\"files\":[123],\"deps\":[]}]")]
    [InlineData("[{\"id\":\"TEST-1\",\"files\":[\"../outside\"],\"deps\":[]}]")]
    public async Task MalformedInputReturnsUsageEnvelope(string raw)
    {
        var output = new StringWriter();

        var exit = await WavesCommand.ExecuteAsync(
            ["waves", "--input", "-"],
            json: true,
            WavesConfig.Default,
            new StringReader(raw),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "usage",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NullTicketReturnsUsageEnvelope()
    {
        var output = new StringWriter();

        var exit = await WavesCommand.ExecuteAsync(
            ["waves", "--input", "-"],
            json: true,
            WavesConfig.Default,
            new StringReader("[null]"),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Equal(
            "each ticket must be a non-null object",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CliWavesDoesNotConstructWorkerAgent()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "waves-command-tests",
            Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(repository, ".build"));
        var inputPath = Path.Combine(repository, "waves.json");

        try
        {
            File.WriteAllText(
                Path.Combine(repository, ".build", "config.toml"),
                """
                [ticketing]
                backend = "plane"
                plane_base_url = "https://api.plane.test"
                plane_workspace_slug = "workspace"
                plane_project_id = "project"
                plane_api_token = "test-token"

                [workers]
                default_agent = "codex"

                [workers.codex]
                executable = "worker-must-not-run"

                [workers.codex.sizes]
                small = { model = "test" }
                medium = { model = "test" }
                large = { model = "test" }

                [events]
                log_directory = ".build/events"
                """);
            File.WriteAllText(
                inputPath,
                """[{"id":"TEST-1","files":["README.md"],"deps":[]}]""");
            Directory.SetCurrentDirectory(repository);
            var constructionCount = 0;

            var exit = await CliApplication.RunAsync(
                ["waves", "--input", inputPath],
                (_, _) =>
                {
                    constructionCount++;
                    throw new InvalidOperationException("worker must not be constructed");
                });

            Assert.Equal(0, exit);
            Assert.Equal(0, constructionCount);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(repository, recursive: true);
        }
    }
}
