using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Plane;
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

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
            // Omitted-value default: a [workers.claude-code] block with no `transport` key resolves to
            // InteractiveHook after the Stage 07 cutover (interactive-hook is the default; print is the
            // documented rollback).
            Assert.Equal(ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport.InteractiveHook,
                config.Workers.Agents["claude-code"].Transport);
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

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
    public void Load_UnreplacedRequiredPlaceholder_ThrowsConfigException()
    {
        // A 'build init' scaffold whose plane_project_id placeholder was never replaced. It is a
        // non-empty string, so RequireString passes it; without the placeholder guard it loads
        // cleanly and only fails later as an opaque Plane 404. Reject it at load as a Config error.
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "REQUIRED_PLANE_PROJECT_ID"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "claude-code"

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("plane_project_id", ex.Message);
            Assert.Contains("placeholder", ex.Message);
            // The message points the operator at the offending file, not a network endpoint.
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FilledNonUuidProjectId_LoadsCleanly()
    {
        // Guard against the placeholder check over-reaching: a real-but-non-UUID id like "abc-123"
        // must still load. The check keys on the documented REQUIRED_ scaffold prefix, not id shape,
        // precisely so it never false-positives on a valid id.
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal("abc-123", config.Ticketing.PlaneProjectId);
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

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
            Assert.Equal("claude-haiku-4-5-20251001", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Model);
            Assert.Equal("claude-sonnet-4-6", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Model);
            Assert.Equal("claude-opus-4-7", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClaudeCode_sizes_fable_alias_throws_ConfigException_with_full_slug_hint()
    {
        // "fable" is not a Claude Code tier alias; without load-time validation it only
        // fails at session init deep inside a chain run ("There's an issue with the
        // selected model (fable)"). The config loader must reject it up front and point
        // at the full slug.
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
small  = { model = "haiku" }
medium = { model = "fable" }
large  = { model = "fable" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("workers.claude-code.sizes.medium", ex.Message);
            Assert.Contains("claude-fable-5", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClaudeCode_sizes_full_fable_slug_and_aliases_load()
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
small  = { model = "haiku" }
medium = { model = "claude-fable-5" }
large  = { model = "anthropic:claude-fable-5" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            var sizes = config.Workers.Agents["claude-code"].Sizes;
            Assert.Equal("haiku", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Model);
            Assert.Equal("claude-fable-5", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Model);
            Assert.Equal("anthropic:claude-fable-5", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NonClaude_agents_are_not_subject_to_claude_model_validation()
    {
        // The model-shape rule is claude-code-specific; a codex block with OpenAI ids
        // (which would fail the claude-* check) must load untouched.
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "codex"

[workers.codex]
executable = "codex"

[workers.codex.sizes]
small  = { model = "gpt-5.4-mini" }
medium = { model = "gpt-5.4" }
large  = { model = "gpt-5.5" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal("gpt-5.5", config.Workers.Agents["codex"].Sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large].Model);
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }

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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[workers.gemini]
executable = "gemini"

[workers.gemini.sizes]
small  = { model = "gemini-2.0-flash" }
medium = { model = "gemini-2.5-flash" }
large  = { model = "gemini-2.5-pro" }

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
            Assert.Equal("gemini-2.0-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Model);
            Assert.Equal("gemini-2.5-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Model);
            Assert.Equal("gemini-2.5-pro", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultAgentNotDefined_ThrowsConfigExceptionWithGuidance()
    {
        // The reported bug: default_agent names "codex" but the [workers.codex]
        // sections were left commented out. Previously this loaded fine and then
        // crashed later with an UNHANDLED ConfigException at agent-resolution time.
        // Now Load() rejects it with an actionable message routed through the
        // friendly "Config error:" handler.
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "codex"

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            // Names the offending setting and the missing sub-table.
            Assert.Contains("default_agent", ex.Message);
            Assert.Contains("[workers.codex]", ex.Message);
            // Tells the operator how to fix it and what is actually configured.
            Assert.Contains(".build/config.toml", ex.Message);
            Assert.Contains("claude-code", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PhaseAgentNotDefined_ThrowsConfigExceptionWithGuidance()
    {
        // A [workers.phases] entry pointing at an undefined agent has the same
        // failure mode as an undefined default_agent and must be caught at load time.
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[workers.phases]
implement = "codex"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("implement", ex.Message);
            Assert.Contains("[workers.codex]", ex.Message);
            Assert.Contains("claude-code", ex.Message);
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

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
    public void Load_BypassPermissionsDefaultsTrue_WhenAbsent()
    {
        // ValidToml has no bypass_permissions key under [workers.claude-code];
        // the loader must default the AgentConfig field to true so the historic
        // CLI behavior (passing the unattended-mode flag) is preserved.
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.True(config.Workers.Agents["claude-code"].BypassPermissions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BypassPermissionsFalse_ParsesAsFalse()
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
bypass_permissions = false

[workers.claude-code.sizes]
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.False(config.Workers.Agents["claude-code"].BypassPermissions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("print", ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport.Print)]
    [InlineData("interactive-hook", ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport.InteractiveHook)]
    public void Load_ClaudeTransport_ParsesSupportedValues(
        string value,
        ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport expected)
    {
        var path = WriteToml(ValidToml.Replace(
            "executable = \"claude\"",
            $"executable = \"claude\"\ntransport = \"{value}\""));
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(expected, config.Workers.Agents["claude-code"].Transport);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_GeneratedConfigTemplate_DefaultsToInteractiveHook()
    {
        // The generated `build init` config sets transport = "interactive-hook" explicitly after the
        // Stage 07 cutover, so it resolves to InteractiveHook. (print is the documented rollback.)
        var filled = ThroughlineBuild.Commands.ConfigTemplateLoader.Load()
            .Replace("REQUIRED_PLANE_BASE_URL", "https://api.plane.so")
            .Replace("REQUIRED_PLANE_WORKSPACE_SLUG", "my-workspace")
            .Replace("REQUIRED_PLANE_PROJECT_ID", "abc-123")
            .Replace("REQUIRED_PLANE_API_TOKEN", "PLANE_TOKEN");
        var path = WriteToml(filled);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(
                ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport.InteractiveHook,
                config.Workers.Agents["claude-code"].Transport);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TransportInCustomNamedClaudeAgent_ParsesAndDoesNotWarn()
    {
        // A custom-named agent block maps to ClaudeCodeAgent in WorkerAgentBuilder, so its `transport`
        // key must be honored (and not warned as unknown) - the documented print rollback works there.
        var toml = ValidToml.Replace("[events]", """
[workers.my-claude]
executable = "claude"
transport = "interactive-hook"

[workers.my-claude.sizes]
small  = { model = "haiku" }
medium = { model = "sonnet" }
large  = { model = "opus" }

[events]
""");
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, captured.Add);
            Assert.Equal(
                ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport.InteractiveHook,
                config.Workers.Agents["my-claude"].Transport);
            Assert.DoesNotContain(captured, w => w.Contains("my-claude.transport"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ClaudeTransportUnknown_ThrowsActionableConfigException()
    {
        var path = WriteToml(ValidToml.Replace(
            "executable = \"claude\"",
            "executable = \"claude\"\ntransport = \"telepathy\""));
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("telepathy", ex.Message);
            Assert.Contains("print", ex.Message);
            Assert.Contains("interactive-hook", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WorkTargetBranchSet_ResolvesToTargetBranch()
    {
        var toml = ValidToml + "\n[work]\ntarget_branch = \"feature/x\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("feature/x", config.Work.TargetBranch);
            Assert.Equal("feature/x", config.ResolveTargetBranch());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TargetBranchUnresolvable_EmitsWarning()
    {
        var toml = ValidToml + "\n[work]\ntarget_branch = \"dashboard\"";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w), branchExists: _ => false);

            Assert.Contains(captured, w => w.Contains("target_branch") && w.Contains("dashboard"));
            // Non-fatal: config still loads and resolves to the configured value.
            Assert.Equal("dashboard", config.Work.TargetBranch);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TargetBranchResolvable_NoWarning()
    {
        var toml = ValidToml + "\n[work]\ntarget_branch = \"dashboard\"";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w), branchExists: _ => true);

            Assert.DoesNotContain(captured, w => w.Contains("target_branch"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TargetBranchSet_NoValidator_SkipsBranchCheck()
    {
        // Without a validator (default), no git is consulted and no branch warning fires.
        var toml = ValidToml + "\n[work]\ntarget_branch = \"dashboard\"";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.DoesNotContain(captured, w => w.Contains("target_branch"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WorkSectionAbsent_ResolvesToShipBaseBranch()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Null(config.Work.TargetBranch);
            Assert.Equal("main", config.ResolveTargetBranch());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WorkSectionPresentNoTargetBranch_FallsBackToBaseBranch()
    {
        var toml = ValidToml + "\n[work]";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Null(config.Work.TargetBranch);
            Assert.Equal("main", config.ResolveTargetBranch());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Unknown-key warning tests (TLB-405) ---

    [Fact]
    public void Load_StrayKeyInWorkersAgentSubTable_EmitsWarning()
    {
        // "bypass_permission" is a misspelling of "bypass_permissions" - it should warn.
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
bypass_permission = true

[workers.claude-code.sizes]
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.NotEmpty(captured);
            Assert.Contains(captured, w => w.Contains("bypass_permission") && w.Contains("workers.claude-code"));
            // Config still loads successfully (non-fatal)
            Assert.Equal("plane", config.Ticketing.BackendName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TransportInNonClaudeAgent_EmitsWarning()
    {
        var toml = ValidToml.Replace("[events]", """
[workers.codex]
executable = "codex"
transport = "print"

[workers.codex.sizes]
small  = { model = "gpt-5.4-mini" }
medium = { model = "gpt-5.5" }
large  = { model = "gpt-5.5" }

[events]
""");
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, captured.Add);

            Assert.Contains(captured, w =>
                w.Contains("workers.codex.transport") && w.Contains("ignored"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TransportInClaudeAgent_DoesNotWarn()
    {
        var toml = ValidToml.Replace(
            "executable = \"claude\"",
            "executable = \"claude\"\ntransport = \"print\"");
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, captured.Add);

            Assert.DoesNotContain(captured, w => w.Contains("workers.claude-code.transport"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownTopLevelSection_EmitsWarning()
    {
        var toml = ValidToml + "\n[plans]\nsome_key = \"value\"";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.NotEmpty(captured);
            Assert.Contains(captured, w => w.Contains("plans"));
            // Config still loads successfully (non-fatal)
            Assert.Equal("plane", config.Ticketing.BackendName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_StrayKeyInSizesSubTable_EmitsWarning()
    {
        // "executable" accidentally nested under sizes - should warn with dotted path.
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }
executable = "oops"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.NotEmpty(captured);
            Assert.Contains(captured, w => w.Contains("workers.claude-code.sizes.executable"));
            Assert.Equal("plane", config.Ticketing.BackendName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CleanConfig_EmitsNoWarnings()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.Empty(captured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RecognizedOptionalKeys_DoNotWarn()
    {
        // All of these are recognized optional keys; none should trigger a warning.
        var toml = ValidToml + """


[workers.phases]
plan = "claude-code"
implement = "claude-code"

[work]
target_branch = "feature/x"

[plan]
mode = "investigate"

[project]
language = "csharp"
plane_project_url = "https://plane.so/proj"
notes_file = "nonexistent-but-recognized.md"
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.Empty(captured);
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[workers.gemini]
executable = "gemini"

[workers.gemini.sizes]
small  = { model = "google:gemini-2.0-flash" }
medium = { model = "google:gemini-2.5-flash" }
large  = { model = "google:gemini-2.5-pro" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            var sizes = config.Workers.Agents["gemini"].Sizes;
            Assert.NotNull(sizes);
            Assert.Equal("google:gemini-2.0-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Model);
            Assert.Equal("google:gemini-2.5-flash", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Model);
            Assert.Equal("google:gemini-2.5-pro", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Large].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- ModelTier inline-table schema (op-33 Brief 01) ---

    [Fact]
    public void Load_SizeTableWithModelAndEffort_ParsesModelTier()
    {
        var toml = """
[ticketing]
backend = "plane"
plane_base_url = "https://api.plane.so"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_api_token_env = "PLANE_TOKEN"

[workers]
default_agent = "codex"

[workers.codex]
executable = "codex"

[workers.codex.sizes]
small  = { model = "gpt-5.4-mini", effort = "low" }
medium = { model = "gpt-5.4" }
large  = { model = "gpt-5.5" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            var sizes = config.Workers.Agents["codex"].Sizes;
            Assert.Equal("gpt-5.4-mini", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Model);
            Assert.Equal("low", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Small].Effort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SizeTableWithoutEffort_ParsesNullEffort()
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            var sizes = config.Workers.Agents["claude-code"].Sizes;
            Assert.Equal("claude-sonnet-4-6", sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Model);
            Assert.Null(sizes[ThroughlineBuild.Contracts.Models.WorkerSize.Medium].Effort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BareStringSizeValue_ThrowsConfigException()
    {
        // The bare-string size form was dropped; a scalar value must throw.
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
small  = { model = "claude-haiku-4-5-20251001" }
medium = { model = "claude-sonnet-4-6" }
large  = "claude-opus-4-7"

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.True(
                ex.Message.Contains("inline table") || ex.Message.Contains("model"),
                $"expected message mentioning 'inline table' or 'model', got: {ex.Message}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownKeyInsideSizeTable_EmitsWarning()
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
small  = { model = "claude-haiku-4-5-20251001", reasoning = "z" }
medium = { model = "claude-sonnet-4-6" }
large  = { model = "claude-opus-4-7" }

[events]
log_directory = ".build/events"
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.Contains(captured, w => w.Contains("workers.claude-code.sizes.small.reasoning"));
            // Config still loads successfully (non-fatal).
            Assert.Equal("plane", config.Ticketing.BackendName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- [batch] section tests (TLB-454) ---

    [Fact]
    public void Load_NoBatchSection_ReturnsBatchDefaults()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(8, config.Batch.MaxTickets);
            Assert.Equal(16, config.Batch.MaxSizeScore);
            Assert.Equal(200_000, config.Batch.MaxDescriptionBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BatchSectionWithAllKeys_ReturnsConfiguredValues()
    {
        var toml = ValidToml + """

[batch]
max_tickets = 5
max_size_score = 10
max_description_bytes = 50000
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(5, config.Batch.MaxTickets);
            Assert.Equal(10, config.Batch.MaxSizeScore);
            Assert.Equal(50_000, config.Batch.MaxDescriptionBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BatchSectionWithPartialKeys_UsesDefaultsForMissing()
    {
        var toml = ValidToml + """

[batch]
max_tickets = 3
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(3, config.Batch.MaxTickets);
            Assert.Equal(16, config.Batch.MaxSizeScore);
            Assert.Equal(200_000, config.Batch.MaxDescriptionBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BatchSectionWithUnknownKey_EmitsWarningAndLoads()
    {
        var toml = ValidToml + """

[batch]
max_tickets = 4
unknown_batch_key = "ignored"
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, w => captured.Add(w));

            Assert.Contains(captured, w => w.Contains("batch.unknown_batch_key"));
            Assert.Equal(4, config.Batch.MaxTickets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BatchMaxTicketsZero_ThrowsConfigException()
    {
        var toml = ValidToml + """

[batch]
max_tickets = 0
""";
        var path = WriteToml(toml);
        try
        {
            Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_AbsentRole_DefaultsToGating()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "build"
executable = "dotnet"
arguments = ["build"]
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Single(config.Review.Checks);
            Assert.Equal(CheckRole.Gating, config.Review.Checks[0].Role);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_ExplicitGatingRole_Parsed()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "build"
executable = "dotnet"
role = "gating"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(CheckRole.Gating, config.Review.Checks[0].Role);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_AdvisoryRole_Parsed()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "lint"
executable = "dotnet"
arguments = ["format", "--verify-no-changes"]
role = "advisory"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(CheckRole.Advisory, config.Review.Checks[0].Role);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_SetupRole_Parsed()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "xcodegen"
executable = "xcodegen"
arguments = ["generate"]
role = "setup"
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(CheckRole.Setup, config.Review.Checks[0].Role);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CheckRequiredPaths_ParsedForReviewAndShipWithoutUnknownWarnings()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "build"
executable = "npm"
arguments = ["run", "build"]
required_paths = ["package.json", "  src  ", "", "src"]

[[ship.regression_checks]]
name = "xcodegen"
executable = "xcodegen"
arguments = ["generate"]
role = "setup"
required_paths = ["project.yml"]
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            var config = BuildConfigLoader.Load(path, captured.Add);

            Assert.Equal(new[] { "package.json", "src" }, config.Review.Checks[0].RequiredPaths);
            Assert.Equal(new[] { "project.yml" }, config.Ship.RegressionChecks[0].RequiredPaths);
            Assert.DoesNotContain(captured, warning => warning.Contains("required_paths"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_InvalidRole_ThrowsConfigException()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "build"
executable = "dotnet"
role = "blocking"
""";
        var path = WriteToml(toml);
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("role", ex.Message);
            Assert.Contains("blocking", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReviewChecks_Absent_ReturnsEmpty_NotFailure()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Empty(config.Review.Checks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_VerifyGateVacuity_DefaultsToTrue_WhenReviewOmitsIt()
    {
        var toml = ValidToml + """

[[review.checks]]
name = "build"
executable = "dotnet"
arguments = ["build"]
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.True(config.Review.VerifyGateVacuity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_VerifyGateVacuity_DefaultsToTrue_WhenReviewSectionAbsent()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.True(config.Review.VerifyGateVacuity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_VerifyGateVacuity_ParsesFalse_WhenSet()
    {
        var toml = ValidToml + """

[review]
verify_gate_vacuity = false
""";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.False(config.Review.VerifyGateVacuity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_VerifyGateVacuity_DoesNotWarnAsUnknownKey()
    {
        var toml = ValidToml + """

[review]
verify_gate_vacuity = false
""";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));
            Assert.DoesNotContain(captured, w => w.Contains("verify_gate_vacuity"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ConventionFiles_ParsedIntoProjectContext()
    {
        var toml = ValidToml + "\n[project]\nconvention_files = [\"src/setupTests.ts\", \"vite.config.ts\"]";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(new[] { "src/setupTests.ts", "vite.config.ts" }, config.Project.ConventionFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ConventionFilesAbsent_DefaultsToEmpty()
    {
        var toml = ValidToml + "\n[project]\nlanguage = \"typescript\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Empty(config.Project.ConventionFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ConventionFiles_BlankEntriesDropped()
    {
        var toml = ValidToml + "\n[project]\nconvention_files = [\"src/setupTests.ts\", \"\", \"  \"]";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(new[] { "src/setupTests.ts" }, config.Project.ConventionFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PreloadContext_DefaultsTrueWhenAbsent()
    {
        var toml = ValidToml + "\n[project]\nlanguage = \"typescript\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.True(config.Project.PreloadContext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PreloadContext_ParsesFalse()
    {
        var toml = ValidToml + "\n[project]\npreload_context = false";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.False(config.Project.PreloadContext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ConventionFilesAndPreloadContext_DoNotWarnAsUnknownKeys()
    {
        var toml = ValidToml + "\n[project]\nconvention_files = [\"a.ts\"]\npreload_context = false";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));
            Assert.DoesNotContain(captured, w => w.Contains("convention_files"));
            Assert.DoesNotContain(captured, w => w.Contains("preload_context"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ContextHygiene_DefaultsFalseWhenAbsent()
    {
        var toml = ValidToml + "\n[project]\nlanguage = \"typescript\"";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.False(config.Project.ContextHygiene);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ContextHygiene_ParsesTrue()
    {
        var toml = ValidToml + "\n[project]\ncontext_hygiene = true";
        var path = WriteToml(toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.True(config.Project.ContextHygiene);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ContextHygiene_DoesNotWarnAsUnknownKey()
    {
        var toml = ValidToml + "\n[project]\ncontext_hygiene = true";
        var path = WriteToml(toml);
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));
            Assert.DoesNotContain(captured, w => w.Contains("context_hygiene"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // plane_requests_per_minute (TLB-565): the throttle budget is a self-imposed per-process
    // cap sized for Plane Cloud's 60/min. A self-hosted Plane sets its own limit, so operators
    // need to raise it; before this key the only way was to edit and rebuild the binary.
    private static string TomlWithTicketingKey(string line) =>
        ValidToml.Replace(
            "plane_api_token_env = \"PLANE_TOKEN\"",
            "plane_api_token_env = \"PLANE_TOKEN\"\n" + line);

    [Fact]
    public void Load_PlaneRequestsPerMinute_Omitted_DefaultsToCloudSizedBudget()
    {
        var path = WriteToml(ValidToml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(40, config.Ticketing.PlaneRequestsPerMinute);
            Assert.Equal(PlaneClientOptions.DefaultRequestsPerMinute, config.Ticketing.PlaneRequestsPerMinute);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PlaneRequestsPerMinute_Set_OverridesDefault()
    {
        var path = WriteToml(TomlWithTicketingKey("plane_requests_per_minute = 300"));
        try
        {
            var config = BuildConfigLoader.Load(path);
            Assert.Equal(300, config.Ticketing.PlaneRequestsPerMinute);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PlaneRequestsPerMinute_IsAKnownKey_DoesNotWarnAsUnknown()
    {
        var path = WriteToml(TomlWithTicketingKey("plane_requests_per_minute = 300"));
        try
        {
            var captured = new List<string>();
            BuildConfigLoader.Load(path, w => captured.Add(w));
            Assert.DoesNotContain(captured, w => w.Contains("plane_requests_per_minute"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Load_PlaneRequestsPerMinute_NonPositive_ThrowsConfigException(int value)
    {
        // RequestThrottle's ctor throws ArgumentOutOfRangeException on a non-positive budget,
        // which would surface as an unhandled crash naming neither the key nor the file.
        var path = WriteToml(TomlWithTicketingKey($"plane_requests_per_minute = {value}"));
        try
        {
            var ex = Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
            Assert.Contains("plane_requests_per_minute", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
