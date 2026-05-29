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
            Assert.Equal("claude", config.Workers.Agents["claude-code"].Executable);
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

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

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
    public void Load_NewAgentSubTable_ParsesCorrectly()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.True(config.Workers.Agents.ContainsKey("claude-code"));
            Assert.Equal("claude", config.Workers.Agents["claude-code"].Executable);
            Assert.Null(config.Workers.Agents["claude-code"].MaxOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AgentSubTableWithMaxOutputTokens_ParsesValue()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "claude-code"

[workers.claude-code]
executable = "claude"
max_output_tokens = 16000

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(16000, config.Workers.Agents["claude-code"].MaxOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFlatKeyClaudeCodeExecutable_ThrowsConfigException()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

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
            Assert.Contains("claude_code_executable", ex.Message);
            Assert.Contains("workers.", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFlatKeyMaxOutputTokens_ThrowsConfigException()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "claude-code"
max_output_tokens = 32000

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("max_output_tokens", ex.Message);
            Assert.Contains("workers.", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveLogDirectory_RelativePath_AnchoredToProjectRoot()
    {
        // config is at <root>/.build/config.toml
        // project root is <root>
        // relative .build/events should resolve to <root>/.build/events, NOT <root>/.build/.build/events
        var root = Path.Combine(Path.GetTempPath(), "tlb134-test-repo");
        var configPath = Path.Combine(root, ".build", "config.toml");
        var result = BuildConfigLoader.ResolveLogDirectory(configPath, Path.Combine(".build", "events"), Path.Combine(Path.GetTempPath(), "fallback"));
        var expected = Path.Combine(root, ".build", "events");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveLogDirectory_AbsolutePath_PassesThroughUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "tlb134-test-repo");
        var configPath = Path.Combine(root, ".build", "config.toml");
        var absoluteLogDir = Path.Combine(Path.GetTempPath(), "var", "log", "events");
        var result = BuildConfigLoader.ResolveLogDirectory(configPath, absoluteLogDir, Path.Combine(Path.GetTempPath(), "fallback"));
        Assert.Equal(absoluteLogDir, result);
    }

    [Fact]
    public void ResolveLogDirectory_BareFilenameConfigPath_FallsBackToCwdFallback()
    {
        // config path with no directory component - GetDirectoryName returns null for bare names
        var sentinel = Path.Combine(Path.GetTempPath(), "sentinel");
        var result = BuildConfigLoader.ResolveLogDirectory("config.toml", "events", sentinel);
        Assert.StartsWith(sentinel, result);
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

    [Fact]
    public void Load_ProjectSectionMissing_DefaultsWorkflowToolToBuild()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("build", config.Project.WorkflowTool);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ProjectWorkflowToolSetToBuild_ParsesSuccessfully()
    {
        var toml = ValidToml + "\n[project]\nworkflow_tool = \"build\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("build", config.Project.WorkflowTool);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ProjectWorkflowToolSetToClaudeConfig_ParsesSuccessfully()
    {
        var toml = ValidToml + "\n[project]\nworkflow_tool = \"claude-config\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("claude-config", config.Project.WorkflowTool);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ProjectWorkflowToolSetToInvalidValue_ThrowsConfigException()
    {
        var toml = ValidToml + "\n[project]\nworkflow_tool = \"vibes\"";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("workflow_tool", ex.Message);
            Assert.Contains("build", ex.Message);
            Assert.Contains("claude-config", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sizes_are_parsed_per_agent()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            var sizes = config.Workers.Agents["claude-code"].Sizes;
            Assert.NotNull(sizes);
            Assert.Equal("claude-haiku-4-5-20251001", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small]);
            Assert.Equal("claude-sonnet-4-6", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium]);
            Assert.Equal("claude-opus-4-7", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_sizes_table_throws_ConfigException()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "claude-code"

[workers.claude-code]
executable = "claude"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("sizes", ex.Message);
            Assert.Contains("workers.claude-code", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_individual_size_key_throws_ConfigException()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "claude-code"

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("large", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_GeminiAgentSubTable_ParsesExecutableAndSizes()
    {
        var toml = """
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

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

[workers.gemini]
executable = "gemini"

[workers.gemini.sizes]
small  = "gemini-2.0-flash"
medium = "gemini-2.5-flash"
large  = "gemini-2.5-pro"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.True(config.Workers.Agents.ContainsKey("gemini"));
            Assert.Equal("gemini", config.Workers.Agents["gemini"].Executable);
            var sizes = config.Workers.Agents["gemini"].Sizes;
            Assert.NotNull(sizes);
            Assert.Equal("gemini-2.0-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small]);
            Assert.Equal("gemini-2.5-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium]);
            Assert.Equal("gemini-2.5-pro", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_GeminiAgentSubTable_MissingSizes_ThrowsConfigException()
    {
        var toml = """
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

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

[workers.gemini]
executable = "gemini"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("gemini", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_GeminiAgentSubTable_WithGooglePrefixSizes_ParsesRawString()
    {
        var toml = """
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

[workers.claude-code.sizes]
small  = "claude-haiku-4-5-20251001"
medium = "claude-sonnet-4-6"
large  = "claude-opus-4-7"

[workers.gemini]
executable = "gemini"

[workers.gemini.sizes]
small  = "google:gemini-2.0-flash"
medium = "google:gemini-2.5-flash"
large  = "google:gemini-2.5-pro"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            var sizes = config.Workers.Agents["gemini"].Sizes;
            Assert.NotNull(sizes);
            Assert.Equal("google:gemini-2.0-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small]);
            Assert.Equal("google:gemini-2.5-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium]);
            Assert.Equal("google:gemini-2.5-pro", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
