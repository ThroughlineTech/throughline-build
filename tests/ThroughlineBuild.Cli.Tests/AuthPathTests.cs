using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[CollectionDefinition("Cli Tests Environment", DisableParallelization = true)]
public class AuthPathTestsCollection;

[Collection("Cli Tests Environment")]
public class AuthPathTests
{
    private static string WriteToml(string content)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var filePath = Path.Combine(tmpDir, "config.toml");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    private const string ValidToml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[llm]
default_model = "anthropic:claude-opus-4-7"
anthropic_api_key_env = "ANTHROPIC_KEY"

[workers]
default_agent = "claude-code"
timeout_minutes = 20

[workers.claude-code]
executable = "claude"

[events]
log_directory = ".build/events"
""";

    [Fact]
    public void Cli_runs_plan_without_anthropic_key()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Environment.SetEnvironmentVariable("PLANE_TOKEN", "test-plane-token");
            Environment.SetEnvironmentVariable("ANTHROPIC_KEY", null);
            try
            {
                var secrets = BuildConfigLoader.ResolveSecrets(config);
                Assert.Equal("test-plane-token", secrets.PlaneApiToken);
                Assert.Null(secrets.AnthropicApiKey);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PLANE_TOKEN", null);
                Environment.SetEnvironmentVariable("ANTHROPIC_KEY", null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cli_fails_when_plane_token_missing()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Environment.SetEnvironmentVariable("PLANE_TOKEN", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_KEY", null);
            try
            {
                var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.ResolveSecrets(config));
                Assert.Contains("PLANE_TOKEN", ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PLANE_TOKEN", null);
                Environment.SetEnvironmentVariable("ANTHROPIC_KEY", null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
