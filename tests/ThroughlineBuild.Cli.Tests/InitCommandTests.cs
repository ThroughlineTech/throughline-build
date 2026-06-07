using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for InitCommand.ExecuteAsync and the ConfigTemplateLoader it delegates to.
/// Tests use in-process calls and temp directories; no subprocess infrastructure required.
/// </summary>
public class InitCommandTests
{
    // ------------------------------------------------------------------
    // Minimal fake console
    // ------------------------------------------------------------------

    private sealed class FakeInteractiveConsole : IConsole
    {
        private readonly System.Text.StringBuilder _stdout = new();
        private readonly System.Text.StringBuilder _stderr = new();
        public Queue<string?> Responses { get; } = new Queue<string?>();

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        public bool IsInputRedirected => false;
        public void WriteLine(string value) => _stdout.AppendLine(value);
        public void Write(string value) => _stdout.Append(value);
        public void ErrorWriteLine(string value) => _stderr.AppendLine(value);
        public string? ReadLine() => Responses.Count > 0 ? Responses.Dequeue() : null;
        public char? ReadKeyChar() => null;
    }

    private sealed class FakeConsole : IConsole
    {
        private readonly System.Text.StringBuilder _stdout = new();
        private readonly System.Text.StringBuilder _stderr = new();

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        public bool IsInputRedirected => true;
        public void WriteLine(string value) => _stdout.AppendLine(value);
        public void Write(string value) => _stdout.Append(value);
        public void ErrorWriteLine(string value) => _stderr.AppendLine(value);
        public string? ReadLine() => null;
        public char? ReadKeyChar() => null;
    }

    // ------------------------------------------------------------------
    // Helper: create a fresh temp directory for each test
    // ------------------------------------------------------------------

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb317-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ------------------------------------------------------------------
    // Template loader
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigTemplateLoader_Load_ReturnsNonEmptyString()
    {
        var template = ConfigTemplateLoader.Load();
        Assert.NotNull(template);
        Assert.NotEmpty(template);
    }

    [Fact]
    public void ConfigTemplateLoader_Load_ContainsExpectedTomlKeys()
    {
        var template = ConfigTemplateLoader.Load();
        Assert.Contains("[ticketing]", template);
        Assert.Contains("plane_base_url", template);
        Assert.Contains("plane_workspace_slug", template);
        Assert.Contains("plane_project_id", template);
        Assert.Contains("plane_api_token", template);
    }

    // ------------------------------------------------------------------
    // Execute: file creation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_NoExistingConfig_CreatesFileAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            Assert.True(File.Exists(Path.Combine(dir, ".build", "config.toml")));
            Assert.Contains("Created", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_NoExistingConfig_WrittenFileIsNonEmpty()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.NotEmpty(written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: clobber guard
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_ExistingConfig_NoForce_ReturnsOneAndDoesNotOverwrite()
    {
        var dir = MakeTempDir();
        try
        {
            var buildDir = Path.Combine(dir, ".build");
            Directory.CreateDirectory(buildDir);
            var target = Path.Combine(buildDir, "config.toml");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(1, result);
            Assert.Equal("# original", File.ReadAllText(target));
            Assert.Contains("already exists", console.Stderr);
            Assert.Contains("--force", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_ExistingConfig_WithForce_OverwritesAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var buildDir = Path.Combine(dir, ".build");
            Directory.CreateDirectory(buildDir);
            var target = Path.Combine(buildDir, "config.toml");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: true, printTemplate: false, console);

            Assert.Equal(0, result);
            var written = File.ReadAllText(target);
            Assert.NotEqual("# original", written);
            Assert.Contains("[ticketing]", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: --print-template
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_PrintTemplate_WritesToStdoutAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: true, console);

            Assert.Equal(0, result);
            Assert.Contains("[ticketing]", console.Stdout);
            // No file should be created.
            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: flag-based value injection
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_PlaneUrlFlag_ReplacesPlaceholder()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://plane.example.com");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://plane.example.com", written);
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_WorkspaceFlag_ReplacesPlaceholder()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                workspace: "my-workspace");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("my-workspace", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_ProjectIdFlag_ReplacesPlaceholder()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                projectId: "abc-1234-uuid");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("abc-1234-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_TokenFlag_ReplacesPlaceholder()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                token: "my-secret-token");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("my-secret-token", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_TokenEnvFlag_ReplacesLiteralTokenLineWithEnvLine()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                tokenEnv: "PLANE_API_TOKEN");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("plane_api_token_env = \"PLANE_API_TOKEN\"", written);
            // The literal plane_api_token = "REQUIRED_..." line should be gone.
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_AllFlags_ReplacesAllRequiredPlaceholders()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                projectId: "proj-uuid-999",
                tokenEnv: "PLANE_TOKEN");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.Contains("https://api.plane.so", written);
            Assert.Contains("acme", written);
            Assert.Contains("proj-uuid-999", written);
            Assert.Contains("plane_api_token_env = \"PLANE_TOKEN\"", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: written file is parseable by BuildConfigLoader
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_WithAllRequiredFlags_ProducesParseableConfig()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "test-ws",
                projectId: "test-proj-id",
                tokenEnv: "PLANE_TOKEN");

            Assert.Equal(0, result);

            var configPath = Path.Combine(dir, ".build", "config.toml");
            // Should not throw ConfigException.
            var config = BuildConfigLoader.Load(configPath);
            Assert.Equal("plane", config.Ticketing.BackendName);
            Assert.Equal("https://api.plane.so", config.Ticketing.PlaneBaseUrl);
            Assert.Equal("test-ws", config.Ticketing.PlaneWorkspaceSlug);
            Assert.Equal("test-proj-id", config.Ticketing.PlaneProjectId);
            Assert.Equal("PLANE_TOKEN", config.Ticketing.PlaneApiTokenEnv);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // ApplyFlags internal helper
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyFlags_NoFlags_ReturnTemplateUnchanged()
    {
        var template = "plane_base_url = \"REQUIRED_PLANE_BASE_URL\"";
        var result = InitCommand.ApplyFlags(template, null, null, null, null, null);
        Assert.Equal(template, result);
    }

    [Fact]
    public void ApplyFlags_TokenEnvTakesPrecedenceOverToken()
    {
        var template = "plane_api_token = \"REQUIRED_PLANE_API_TOKEN\"  # REQUIRED";
        var result = InitCommand.ApplyFlags(template, null, null, null, token: "literal-tok", tokenEnv: "MY_ENV_VAR");
        // tokenEnv wins: replaces the whole line
        Assert.Contains("plane_api_token_env = \"MY_ENV_VAR\"", result);
        Assert.DoesNotContain("literal-tok", result);
    }

    // ------------------------------------------------------------------
    // Usage text
    // ------------------------------------------------------------------

    [Fact]
    public void UsageText_ContainsInitVerb()
    {
        Assert.Contains("build init", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_InitVerb_ContainsForcFlag()
    {
        Assert.Contains("--force", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_InitVerb_ContainsPrintTemplateFlag()
    {
        Assert.Contains("--print-template", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_InitVerb_ContainsFlagDocumentation()
    {
        Assert.Contains("--plane-url", CliUsage.UsageText);
        Assert.Contains("--workspace", CliUsage.UsageText);
        Assert.Contains("--project-id", CliUsage.UsageText);
        Assert.Contains("--token", CliUsage.UsageText);
        Assert.Contains("--token-env", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_InitVerb_ContainsProjectNameFlag()
    {
        Assert.Contains("--project-name", CliUsage.UsageText);
    }

    // ------------------------------------------------------------------
    // Execute: interactive prompting
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_InteractiveAllPrompts_FillsAllPlaceholders()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("https://plane.example.com");
            console.Responses.Enqueue("my-workspace");
            console.Responses.Enqueue("my-project-id");
            console.Responses.Enqueue("my-api-token");

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.Contains("https://plane.example.com", written);
            Assert.Contains("my-workspace", written);
            Assert.Contains("my-project-id", written);
            Assert.Contains("my-api-token", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_InteractivePartialFlags_OnlyPromptsMissing()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("my-workspace");
            console.Responses.Enqueue("my-project-id");
            console.Responses.Enqueue("my-api-token");

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so");

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://api.plane.so", written);
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.Contains("my-workspace", written);
            Assert.Contains("my-project-id", written);
            Assert.Contains("my-api-token", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // CliArgParser.GetFlagValue
    // ------------------------------------------------------------------

    [Fact]
    public void GetFlagValue_FlagPresent_ReturnsValue()
    {
        var args = new List<string> { "init", "--plane-url", "https://example.com" };
        var result = CliArgParser.GetFlagValue(args, "--plane-url");
        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public void GetFlagValue_FlagAbsent_ReturnsNull()
    {
        var args = new List<string> { "init", "--force" };
        var result = CliArgParser.GetFlagValue(args, "--plane-url");
        Assert.Null(result);
    }

    [Fact]
    public void GetFlagValue_FlagAtLastPosition_ReturnsNull()
    {
        // flag with no following value - should return null, not throw
        var args = new List<string> { "init", "--plane-url" };
        var result = CliArgParser.GetFlagValue(args, "--plane-url");
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Execute: Codex tier enrichment (injected probe)
    // ------------------------------------------------------------------

    private static CodexModelDiscovery SampleDiscovery() => new(new[]
    {
        new CodexModelInfo("gpt-5.5", "medium", new[] { "minimal", "low", "medium", "high", "xhigh" }),
        new CodexModelInfo("gpt-5.4-mini", "low", new[] { "low", "medium", "high" }),
    });

    [Fact]
    public async Task Execute_SuccessfulProbe_RewritesCodexBlock_LeavesClaudeStatic()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                probeCodex: () => CodexProbeResult.Ok(SampleDiscovery()));

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));

            // Codex block now carries the discovered slugs and a discovered-menu comment.
            Assert.Contains("gpt-5.4-mini", written);
            Assert.Contains("gpt-5.5", written);
            Assert.Contains("# # models: gpt-5.5, gpt-5.4-mini", written);

            // Claude block still reads the static stable aliases.
            Assert.Contains("small  = { model = \"haiku\" }", written);
            Assert.Contains("medium = { model = \"sonnet\" }", written);
            Assert.Contains("large  = { model = \"opus\" }", written);

            // No warning on success.
            Assert.DoesNotContain("Warning", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FailedProbe_WritesStaticDefaults_PrintsOneWarning()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                probeCodex: () => CodexProbeResult.Fail(CodexProbeFailureKind.CommandFailed, "codex not found"));

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));

            // Static codex defaults remain (template's commented example).
            Assert.Contains("# small  = { model = \"gpt-5.4-mini\", effort = \"low\" }", written);
            Assert.Contains("# large  = { model = \"gpt-5.5\", effort = \"high\" }", written);

            // Claude block still static aliases.
            Assert.Contains("small  = { model = \"haiku\" }", written);
            Assert.Contains("large  = { model = \"opus\" }", written);

            // Exactly one warning, mentioning the refresh command.
            Assert.Contains("build models refresh", console.Stderr);
            var warningCount = console.Stderr
                .Split('\n')
                .Count(line => line.Contains("Warning"));
            Assert.Equal(1, warningCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_PrintTemplate_DoesNotInvokeProbe()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: true, console,
                probeCodex: () => throw new Exception("probe must not run on print"));

            Assert.Equal(0, result);
            Assert.Contains("[ticketing]", console.Stdout);
            // No file written.
            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: --from file (credentials input file)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_FromFile_AllFields_FillsAllPlaceholders()
    {
        var dir = MakeTempDir();
        try
        {
            var credsPath = Path.Combine(dir, "creds.txt");
            File.WriteAllText(credsPath, """
                plane_base_url = "https://api.plane.so"
                plane_workspace_slug = "acme"
                plane_api_token = "tok-secret"
                plane_project_id = "uuid-abcd"
                """);

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                fromFile: credsPath);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://api.plane.so", written);
            Assert.Contains("acme", written);
            Assert.Contains("tok-secret", written);
            Assert.Contains("uuid-abcd", written);
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FromFile_ExplicitFlagOverridesFileValue()
    {
        var dir = MakeTempDir();
        try
        {
            var credsPath = Path.Combine(dir, "creds.txt");
            File.WriteAllText(credsPath, """
                plane_base_url = "https://from-file.example.com"
                plane_workspace_slug = "file-workspace"
                """);

            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://from-flag.example.com",  // explicit flag wins
                fromFile: credsPath);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            // Flag value takes precedence over file value.
            Assert.Contains("https://from-flag.example.com", written);
            Assert.DoesNotContain("https://from-file.example.com", written);
            // File fills in the workspace slug that was not supplied as a flag.
            Assert.Contains("file-workspace", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FromFile_NotFound_ReturnsOneAndWritesError()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                fromFile: Path.Combine(dir, "nonexistent.txt"));

            Assert.Equal(1, result);
            Assert.Contains("not found", console.Stderr);
            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FromFile_CommentsAndBlankLines_AreIgnored()
    {
        var dir = MakeTempDir();
        try
        {
            var credsPath = Path.Combine(dir, "creds.txt");
            File.WriteAllText(credsPath, """
                # Workspace creds
                plane_base_url = "https://api.plane.so"

                # Per-project
                plane_project_id = "uuid-proj"
                """);

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                fromFile: credsPath);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://api.plane.so", written);
            Assert.Contains("uuid-proj", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_StdinAsCredsFile_FillsPlaceholders()
    {
        var dir = MakeTempDir();
        try
        {
            // FakeConsole has IsInputRedirected=true and returns lines from Responses.
            var console = new FakeConsole();
            // Override FakeConsole to supply stdin lines (creds file via stdin).
            var stdinConsole = new FakeStdinCredsConsole(
                "plane_base_url = \"https://stdin.example.com\"",
                "plane_workspace_slug = \"stdin-ws\"",
                "plane_project_id = \"stdin-uuid\"",
                "plane_api_token = \"stdin-tok\"");

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, stdinConsole);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://stdin.example.com", written);
            Assert.Contains("stdin-ws", written);
            Assert.Contains("stdin-uuid", written);
            Assert.Contains("stdin-tok", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_FromFileWithProjectName_ProjectIdFillsPlaceholder()
    {
        var dir = MakeTempDir();
        try
        {
            var credsPath = Path.Combine(dir, "creds.txt");
            File.WriteAllText(credsPath, """
                plane_base_url = "https://api.plane.so"
                plane_workspace_slug = "acme"
                plane_api_token = "tok"
                plane_project_name = "My App"
                plane_project_id = "bypass-uuid"
                """);

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                fromFile: credsPath);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            // project_id from file is used to replace the placeholder.
            Assert.Contains("bypass-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Usage text: --from flag
    // ------------------------------------------------------------------

    [Fact]
    public void UsageText_InitVerb_ContainsFromFlag()
    {
        Assert.Contains("--from", CliUsage.UsageText);
    }

    // ------------------------------------------------------------------
    // Helpers for stdin-as-creds tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Fake console that simulates redirected stdin delivering creds file lines.
    /// </summary>
    private sealed class FakeStdinCredsConsole : IConsole
    {
        private readonly System.Text.StringBuilder _stdout = new();
        private readonly System.Text.StringBuilder _stderr = new();
        private readonly Queue<string?> _lines;

        public FakeStdinCredsConsole(params string[] lines)
        {
            _lines = new Queue<string?>(lines);
            _lines.Enqueue(null); // EOF sentinel
        }

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        public bool IsInputRedirected => true;
        public void WriteLine(string value) => _stdout.AppendLine(value);
        public void Write(string value) => _stdout.Append(value);
        public void ErrorWriteLine(string value) => _stderr.AppendLine(value);
        public string? ReadLine() => _lines.Count > 0 ? _lines.Dequeue() : null;
        public char? ReadKeyChar() => null;
    }

    // ------------------------------------------------------------------
    // Connected mode: project name + credentials triggers resolution
    // ------------------------------------------------------------------

    private sealed class FakeResolver : IProjectResolver
    {
        private readonly string _returnedId;
        private readonly ProjectResolveOutcome _outcome;

        public FakeResolver(string returnedId, ProjectResolveOutcome outcome)
        {
            _returnedId = returnedId;
            _outcome = outcome;
        }

        public Task<ProjectResolveResult> ResolveAsync(string name, CancellationToken ct) =>
            Task.FromResult(new ProjectResolveResult(_returnedId, _outcome));
    }

    private sealed class FakeProvisioner : ITicketingProvisioner
    {
        public int StateCreates { get; private set; }
        public int LabelCreates { get; private set; }

        public Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingState>>(
                WorkspaceSchema.States.Select((s, i) => new ExistingState(s.Name, s.Group, i)).ToList());

        public Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(WorkspaceSchema.Labels.ToList());

        public Task CreateStateAsync(string name, string group, double seq, CancellationToken ct)
        { StateCreates++; return Task.CompletedTask; }

        public Task CreateLabelAsync(string name, CancellationToken ct)
        { LabelCreates++; return Task.CompletedTask; }
    }

    private sealed class FakeConnectivity : ITicketingConnectivity
    {
        private readonly bool _success;
        private readonly string _message;

        public FakeConnectivity(bool success = true, string message = "all checks passed")
        {
            _success = success;
            _message = message;
        }

        public Task<TicketingConnectivityResult> TestConnectivityAsync(CancellationToken ct) =>
            Task.FromResult(new TicketingConnectivityResult(_success, _message));
    }

    private sealed class ReadyLocalRepo : ILocalRepoOps
    {
        public bool IsGitRepository() => true;
        public void GitInit() { }
        public string? ReadGitignore() =>
            string.Join("\n", GitignoreManager.RequiredEntries) + "\n";
        public void WriteGitignore(string content) { }
        public bool HasAnyCommits() => true;
        public void StageAndCommit(string[] paths, string message) { }
    }

    [Fact]
    public async Task ConnectedMode_ExistingProject_WritesResolvedIdReportsFound()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("resolved-uuid-found", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity(success: true, "connected OK");
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Existing Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);

            // Config was written with the resolved ID.
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("resolved-uuid-found", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);

            // Summary output names the project, id, and outcome.
            Assert.Contains("Existing Project", console.Stdout);
            Assert.Contains("resolved-uuid-found", console.Stdout);
            Assert.Contains("found", console.Stdout);
            Assert.Contains("connected OK", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_NewProject_WritesResolvedIdReportsCreated()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("new-uuid-created", ProjectResolveOutcome.Created);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity(success: true, "all checks passed");
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Brand New Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("new-uuid-created", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);

            Assert.Contains("Brand New Project", console.Stdout);
            Assert.Contains("new-uuid-created", console.Stdout);
            Assert.Contains("created", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_SetupProvisioningRuns()
    {
        var dir = MakeTempDir();
        try
        {
            // Local repo starts empty; provisioner starts fresh (all states/labels missing).
            var resolver = new FakeResolver("proj-id-setup", ProjectResolveOutcome.Found);
            var freshProvisioner = new FreshFakeProvisioner();
            var connectivity = new FakeConnectivity();
            var freshLocalRepo = new EmptyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "My Project",
                resolverOverride: resolver,
                setupFactory: _ => (freshProvisioner, connectivity),
                localRepoOverride: freshLocalRepo);

            Assert.Equal(0, result);

            // Git init was called.
            Assert.Equal(1, freshLocalRepo.InitCalls);
            // .gitignore was written.
            Assert.Equal(1, freshLocalRepo.WriteCalls);
            // States and labels were created.
            Assert.True(freshProvisioner.StateCreates > 0, "expected states to be created");
            Assert.True(freshProvisioner.LabelCreates > 0, "expected labels to be created");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_ConnectivityCheckedAndSummaryPrinted()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("cid-check", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity(success: true, "label list OK, state list OK, issue-create OK");
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Checked Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);
            Assert.Contains("OK", console.Stdout);
            Assert.Contains("label list OK", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_ConnectivityFailed_ReturnsOneWithWarning()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("cid-fail", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity(success: false, "permission denied on issue-create");
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Bad Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(1, result);
            Assert.Contains("FAILED", console.Stdout);
            Assert.Contains("Warning", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoCredsMode_NoProjectName_WritesTemplateAndPrompts()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok"
                // No projectName - must stay offline
            );

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            // ID placeholder is still in config (no resolution without projectName).
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.Contains("Fill in the REQUIRED fields", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_ClobberGuardStillApplies()
    {
        var dir = MakeTempDir();
        try
        {
            var buildDir = Path.Combine(dir, ".build");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "config.toml"), "# original");

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Some Project",
                // Resolver must NOT be called because the clobber guard fires first.
                resolverOverride: new ThrowingResolver());

            Assert.Equal(1, result);
            Assert.Contains("already exists", console.Stderr);
            // Original file untouched.
            Assert.Equal("# original", File.ReadAllText(Path.Combine(buildDir, "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_FromFile_ProjectNameTriggersConnectedMode()
    {
        var dir = MakeTempDir();
        try
        {
            var credsPath = Path.Combine(dir, "creds.txt");
            File.WriteAllText(credsPath, """
                plane_base_url = "https://api.plane.so"
                plane_workspace_slug = "acme"
                plane_api_token = "tok"
                plane_project_name = "File Project"
                """);

            var resolver = new FakeResolver("file-project-uuid", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                fromFile: credsPath,
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("file-project-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.Contains("File Project", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Additional fakes for setup provisioning test
    // ------------------------------------------------------------------

    private sealed class FreshFakeProvisioner : ITicketingProvisioner
    {
        private readonly List<ExistingState> _states = new();
        private readonly List<string> _labels = new();
        public int StateCreates { get; private set; }
        public int LabelCreates { get; private set; }

        public Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingState>>(_states.ToList());

        public Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_labels.ToList());

        public Task CreateStateAsync(string name, string group, double seq, CancellationToken ct)
        {
            _states.Add(new ExistingState(name, group, seq));
            StateCreates++;
            return Task.CompletedTask;
        }

        public Task CreateLabelAsync(string name, CancellationToken ct)
        {
            _labels.Add(name);
            LabelCreates++;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyLocalRepo : ILocalRepoOps
    {
        private bool _isRepo;
        private bool _hasCommits;
        public int InitCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public string? LastCommitMessage { get; private set; }
        public string[]? LastCommitPaths { get; private set; }
        public string? Gitignore { get; private set; }

        public bool IsGitRepository() => _isRepo;
        public void GitInit() { InitCalls++; _isRepo = true; }
        public string? ReadGitignore() => Gitignore;
        public void WriteGitignore(string content) { WriteCalls++; Gitignore = content; }
        public bool HasAnyCommits() => _hasCommits;
        public void StageAndCommit(string[] paths, string message)
        {
            CommitCalls++;
            LastCommitPaths = paths;
            LastCommitMessage = message;
            _hasCommits = true;
        }
    }

    private sealed class ThrowingResolver : IProjectResolver
    {
        public Task<ProjectResolveResult> ResolveAsync(string name, CancellationToken ct) =>
            throw new InvalidOperationException("resolver must not be called before clobber guard");
    }

    // ------------------------------------------------------------------
    // Welcome commit: fresh repo gets an initial commit; existing repo does not
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectedMode_FreshRepo_CreatesWelcomeCommit()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("proj-id-welcome", ProjectResolveOutcome.Created);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            var freshRepo = new EmptyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Fresh Repo Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: freshRepo);

            Assert.Equal(0, result);
            // A welcome commit must have been made.
            Assert.Equal(1, freshRepo.CommitCalls);
            Assert.Contains(".gitignore", freshRepo.LastCommitPaths!);
            Assert.Equal("welcome to throughline build", freshRepo.LastCommitMessage);
            // .build/config.toml must NOT be in the staged paths.
            Assert.DoesNotContain(".build/config.toml", freshRepo.LastCommitPaths!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_ExistingRepo_SkipsWelcomeCommit()
    {
        var dir = MakeTempDir();
        try
        {
            var resolver = new FakeResolver("proj-id-existing", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            // ReadyLocalRepo.HasAnyCommits() returns true -> welcome commit must be skipped.
            var existingRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Existing Repo Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: existingRepo);

            Assert.Equal(0, result);
            // No warning and no mention of "Welcome" commit for an already-initialized repo.
            Assert.DoesNotContain("welcome commit", console.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
