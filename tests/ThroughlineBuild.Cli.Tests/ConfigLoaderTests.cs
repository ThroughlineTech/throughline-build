using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
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
default_agent = "claude-code"

[workers.claude-code]
executable = "claude"

[workers.claude-code.sizes]
small  = { model = "gpt-5.4-mini", effort = "low" }
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
}
