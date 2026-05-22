using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ConfigLoaderTests
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
claude_code_executable = "claude"
timeout_minutes = 20

[events]
log_directory = ".build/events"
""";

    [Fact]
    public void Load_ValidToml_ReturnsCorrectConfig()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("plane", config.Ticketing.BackendName);
            Assert.Equal("https://api.plane.so", config.Ticketing.PlaneBaseUrl);
            Assert.Equal("my-workspace", config.Ticketing.PlaneWorkspaceSlug);
            Assert.Equal("abc-123", config.Ticketing.PlaneProjectId);
            Assert.Equal("PLANE_TOKEN", config.Ticketing.PlaneApiTokenEnv);
            Assert.Equal("anthropic:claude-opus-4-7", config.Llm.DefaultModel);
            Assert.Equal("ANTHROPIC_KEY", config.Llm.AnthropicApiKeyEnv);
            Assert.Equal("claude-code", config.Workers.DefaultAgent);
            Assert.Equal("claude", config.Workers.ClaudeCodeExecutable);
            Assert.Equal(20, config.Workers.TimeoutMinutes);
            Assert.Equal(".build/events", config.Events.LogDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingRequiredField_ThrowsConfigException()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_workspace_slug = "ws"
plane_project_id = "proj"
plane_api_token_env = "TOK"

[workers]
default_agent = "claude-code"
claude_code_executable = "claude"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("plane_base_url", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindConfigFile_WalksUpToConfigDir_ReturnsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var buildDir = Path.Combine(root, ".build");
        var deepDir = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(buildDir);
        Directory.CreateDirectory(deepDir);
        var configPath = Path.Combine(buildDir, "config.toml");
        File.WriteAllText(configPath, "# placeholder");
        try
        {
            var found = BuildConfigLoader.FindConfigFile(deepDir);
            Assert.NotNull(found);
            Assert.Equal(configPath, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSecrets_EnvVarSet_ReturnsSecret()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Environment.SetEnvironmentVariable("PLANE_TOKEN", "test-plane-token");
            Environment.SetEnvironmentVariable("ANTHROPIC_KEY", "test-anthropic-key");
            try
            {
                var secrets = BuildConfigLoader.ResolveSecrets(config);
                Assert.Equal("test-plane-token", secrets.PlaneApiToken);
                Assert.Equal("test-anthropic-key", secrets.AnthropicApiKey);
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
    public void ResolveSecrets_EnvVarMissing_ThrowsConfigException()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Environment.SetEnvironmentVariable("PLANE_TOKEN", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_KEY", null);
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.ResolveSecrets(config));
            Assert.Contains("PLANE_TOKEN", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSecrets_AnthropicKeyMissing_DoesNotThrow()
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
}
