using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class PerPhaseAgentSelectionTests
{
    // Helper: write TOML content to a temp file and return the path.
    private static string WriteToml(string content)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var filePath = Path.Combine(tmpDir, "config.toml");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    // Base TOML shared by config-parsing tests.
    private const string BaseToml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[llm]
default_model = "anthropic:claude-sonnet-4-6"
anthropic_api_key_env = "ANTHROPIC_KEY"

[workers]
default_agent = "claude-code"
timeout_minutes = 20

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

[events]
log_directory = ".build/events"
""";

    // Minimal stub for factory routing tests.
    private sealed class StubWorkerAgent : IWorkerAgent
    {
        public StubWorkerAgent(string name) => Name = name;
        public string Name { get; }
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(
            Brief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct)
            => throw new NotImplementedException();
    }

    // --- Config parsing: phases populated ---

    [Fact]
    public void Load_PhasesPopulated_AllThreeKeysParsedCorrectly()
    {
        var toml = BaseToml + """

[workers.phases]
plan = "claude-code"
implement = "claude-code"
review = "claude-code"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(3, config.Workers.Phases.Count);
            Assert.Equal("claude-code", config.Workers.Phases["plan"]);
            Assert.Equal("claude-code", config.Workers.Phases["implement"]);
            Assert.Equal("claude-code", config.Workers.Phases["review"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Config parsing: phases absent ---

    [Fact]
    public void Load_PhasesAbsent_EmptyPhasesDict()
    {
        var path = WriteToml(BaseToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Empty(config.Workers.Phases);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PhasesAbsent_AllPhasesFallBackToDefaultAgent()
    {
        var path = WriteToml(BaseToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            // All three phases should fall back to default when Phases dict is empty.
            string AgentFor(string phase) =>
                config.Workers.Phases.TryGetValue(phase, out var n) ? n : config.Workers.DefaultAgent;

            Assert.Equal("claude-code", AgentFor("plan"));
            Assert.Equal("claude-code", AgentFor("implement"));
            Assert.Equal("claude-code", AgentFor("review"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Config parsing: partial phases ---

    [Fact]
    public void Load_PartialPhases_MissingKeysFallBackToDefaultAgent()
    {
        var toml = BaseToml + """

[workers.phases]
implement = "claude-code"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Single(config.Workers.Phases);
            Assert.Equal("claude-code", config.Workers.Phases["implement"]);

            // plan and review are absent - they should fall back to default.
            string AgentFor(string phase) =>
                config.Workers.Phases.TryGetValue(phase, out var n) ? n : config.Workers.DefaultAgent;

            Assert.Equal("claude-code", AgentFor("plan"));
            Assert.Equal("claude-code", AgentFor("implement"));
            Assert.Equal("claude-code", AgentFor("review"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Config parsing: unknown phase key throws ---

    [Fact]
    public void Load_UnknownPhaseKey_ThrowsConfigException()
    {
        var toml = BaseToml + """

[workers.phases]
ship = "claude-code"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("ship", ex.Message);
            Assert.Contains("workers.phases", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Factory routing: unknown agent name throws ConfigException ---

    [Fact]
    public void Create_UnknownAgentNameInPhases_ThrowsConfigException()
    {
        var factory = new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                ["claude-code"] = () => new StubWorkerAgent("claude-code")
            });

        // Simulates what happens when AgentFor returns an agent name not registered in factory.
        var ex = Assert.Throws<ConfigException>(() => factory.Create("unknown-agent"));
        Assert.Contains("unknown-agent", ex.Message);
    }
}
