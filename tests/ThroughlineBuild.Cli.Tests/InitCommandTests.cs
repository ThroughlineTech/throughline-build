using System.Net;
using System.Text;
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
    // Execute: existing config is a no-op (TLB-627 - config.toml is now tracked, so a repeat
    // 'build init' on an already-configured clone is the NORMAL case, not an error)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_ExistingCompleteConfig_NoForce_ReturnsZeroAndDoesNotOverwrite()
    {
        var dir = MakeTempDir();
        try
        {
            var buildDir = Path.Combine(dir, ".build");
            Directory.CreateDirectory(buildDir);
            var target = Path.Combine(buildDir, "config.toml");
            File.WriteAllText(target, "# original, no REQUIRED_ placeholders");

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            Assert.Equal("# original, no REQUIRED_ placeholders", File.ReadAllText(target));
            Assert.Contains("already exists", console.Stdout);
            Assert.Contains("--force", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_ExistingIncompleteConfig_NoForce_ReturnsZeroButListsPlaceholders()
    {
        var dir = MakeTempDir();
        try
        {
            var buildDir = Path.Combine(dir, ".build");
            Directory.CreateDirectory(buildDir);
            var target = Path.Combine(buildDir, "config.toml");
            File.WriteAllText(target, "plane_base_url = \"REQUIRED_PLANE_BASE_URL\"");

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            Assert.Equal("plane_base_url = \"REQUIRED_PLANE_BASE_URL\"", File.ReadAllText(target));
            Assert.Contains("already exists", console.Stdout);
            Assert.Contains("plane_base_url", console.Stdout);
            Assert.Contains("--force", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_RunTwice_SecondRunIsByteIdempotent()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);
            var target = Path.Combine(dir, ".build", "config.toml");
            var afterFirst = File.ReadAllText(target);

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, new FakeConsole());

            Assert.Equal(0, result);
            Assert.Equal(afterFirst, File.ReadAllText(target));
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
    // WI-04: offline init prints accurate, complete next steps
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_Offline_NextSteps_NameSetupAndConnectedMode()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            // Points at the required provisioning step.
            Assert.Contains("build setup", console.Stdout);
            // Surfaces the one-shot connected path.
            Assert.Contains("--project-name", console.Stdout);
            // Names the still-unresolved REQUIRED fields (none supplied here -> the three that still
            // have a REQUIRED_ placeholder; plane_api_token defaults to plane_api_token_env, which
            // has no placeholder to fill in).
            Assert.Contains("Still REQUIRED", console.Stdout);
            Assert.Contains("plane_project_id", console.Stdout);
            Assert.DoesNotContain("plane_api_token", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_Offline_AllFieldsSupplied_DoesNotClaimStillRequired()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(
                dir, force: false, printTemplate: false, console,
                planeUrl: "https://plane.example.com",
                workspace: "my-workspace",
                projectId: "abc-uuid",
                token: "tok-1");

            Assert.Equal(0, result);
            // Every REQUIRED placeholder was filled, so the message must not claim fields remain.
            Assert.DoesNotContain("Still REQUIRED", console.Stdout);
            // But the next-step pointers still appear.
            Assert.Contains("build setup", console.Stdout);
            Assert.Contains("--project-name", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_PrintTemplate_DoesNotEmitNextStepHints()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: true, console);

            // --print-template is pure template output: none of the offline hints leak in. The
            // template itself legitimately mentions 'build setup' in a plane_api_token_file
            // comment (TLB-638), so check for the specific hint sentence, not the bare phrase.
            Assert.DoesNotContain("Next: run 'build setup'", console.Stdout);
            Assert.DoesNotContain("Still REQUIRED", console.Stdout);
            Assert.DoesNotContain("Next:", console.Stdout);
            Assert.DoesNotContain("user-guide", console.Stdout);
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
    public async Task Execute_TokenFlag_ReplacesDefaultEnvLineWithLiteral()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                token: "my-secret-token");

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("plane_api_token = \"my-secret-token\"", written);
            Assert.DoesNotContain("plane_api_token_env = \"PLANE_API_TOKEN\"", written);
            Assert.Contains("Warning", console.Stderr);
            Assert.Contains("--token", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_NoTokenFlags_DefaultsToEnvVarFormAndDoesNotWarn()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("plane_api_token_env = \"PLANE_API_TOKEN\"", written);
            // The active (uncommented) plane_api_token key is absent; only the commented-out
            // alternative ("# plane_api_token = ...") is present, which is expected.
            Assert.DoesNotContain("\nplane_api_token = \"", written);
            Assert.DoesNotContain("Warning", console.Stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_TokenEnvFlag_ReplacesDefaultEnvLineWithCustomName()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                tokenEnv: "CUSTOM_PLANE_TOKEN");

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("plane_api_token_env = \"CUSTOM_PLANE_TOKEN\"", written);
            Assert.DoesNotContain("plane_api_token_env = \"PLANE_API_TOKEN\"", written);
            Assert.DoesNotContain("Warning", console.Stderr);
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
    public async Task Execute_InteractiveAllPrompts_FillConnectionValuesThenPickProject()
    {
        var dir = MakeTempDir();
        try
        {
            // url -> workspace -> token are prompted (no GUID prompt), then create-or-pick.
            var discovery = new StubProjectDiscovery
            {
                Projects = [new ProjectInfo("picked-uuid", "My Project", "MP", DateTimeOffset.Parse("2026-06-01T00:00:00Z"))],
            };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("https://plane.example.com"); // base URL
            console.Responses.Enqueue("my-workspace");              // workspace slug
            console.Responses.Enqueue("my-api-token");              // API token
            console.Responses.Enqueue("e");                         // use existing
            console.Responses.Enqueue("1");                         // pick #1

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.Contains("https://plane.example.com", written);
            Assert.Contains("my-workspace", written);
            Assert.Contains("my-api-token", written);
            Assert.Contains("picked-uuid", written);
            // The raw GUID-paste prompt is gone.
            Assert.DoesNotContain("Plane project ID", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_InteractivePartialFlags_OnlyPromptsMissingConnectionValues()
    {
        var dir = MakeTempDir();
        try
        {
            // planeUrl supplied as a flag -> only workspace + token are prompted, then create-or-pick.
            var discovery = new StubProjectDiscovery { Projects = [], CreatedId = "made-uuid" };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("my-workspace");  // workspace slug
            console.Responses.Enqueue("my-api-token");  // API token
            console.Responses.Enqueue("c");             // create new (no existing projects)
            console.Responses.Enqueue("My App");        // project name
            console.Responses.Enqueue("MA");            // identifier

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://api.plane.so", written);
            Assert.DoesNotContain("REQUIRED_PLANE_BASE_URL", written);
            Assert.DoesNotContain("REQUIRED_PLANE_WORKSPACE_SLUG", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("REQUIRED_PLANE_API_TOKEN", written);
            Assert.Contains("my-workspace", written);
            Assert.Contains("my-api-token", written);
            Assert.Contains("made-uuid", written);
            Assert.Equal("My App", discovery.LastCreatedName);
            Assert.Equal("MA", discovery.LastCreatedIdentifier);
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
            Assert.Contains("models: gpt-5.5, gpt-5.4-mini", written);

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
            Assert.Contains("small  = { model = \"gpt-5.4-mini\", effort = \"low\" }", written);
            Assert.Contains("large  = { model = \"gpt-5.5\", effort = \"high\" }", written);

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
            // url/workspace/token were supplied, so only plane_project_id remains REQUIRED.
            Assert.Contains("Still REQUIRED", console.Stdout);
            Assert.Contains("plane_project_id", console.Stdout);
            Assert.DoesNotContain("plane_base_url", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_ExistingConfigIsNoOp_ConnectedFlowNeverRuns()
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
                // Resolver must NOT be called because the existing-file no-op fires first.
                resolverOverride: new ThrowingResolver());

            Assert.Equal(0, result);
            Assert.Contains("already exists", console.Stdout);
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
            // No mention of "Welcome" commit for an already-initialized repo. Stderr does carry the
            // --token literal-value warning (expected - --token was passed above), but nothing else.
            Assert.DoesNotContain("welcome commit", console.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("welcome commit", console.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // App-doc scaffold handoff: post-bootstrap doc detection (TLB-487)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectedMode_WithOpDoc_SummaryShowsScaffoldPointer()
    {
        var dir = MakeTempDir();
        try
        {
            // Seed a doc in the canonical op-docs location.
            var opDocsDir = Path.Combine(dir, "docs", "op-docs");
            Directory.CreateDirectory(opDocsDir);
            File.WriteAllText(Path.Combine(opDocsDir, "op-01-my-operation.md"), "# Operation: my-operation\n");

            var resolver = new FakeResolver("doc-proj-id", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Doc Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);
            // Summary must name the detected doc.
            Assert.Contains("op-01-my-operation.md", console.Stdout);
            // Summary must show the exact scaffold command.
            Assert.Contains("build scaffold", console.Stdout);
            Assert.Contains("docs/op-docs/op-01-my-operation.md", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_WithProposalDoc_SummaryShowsScaffoldPointer()
    {
        var dir = MakeTempDir();
        try
        {
            // Seed a doc in the canonical proposals location.
            var proposalsDir = Path.Combine(dir, "docs", "proposals");
            Directory.CreateDirectory(proposalsDir);
            File.WriteAllText(Path.Combine(proposalsDir, "my-proposal.md"), "# Proposal\n");

            var resolver = new FakeResolver("prop-proj-id", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Proposal Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);
            Assert.Contains("my-proposal.md", console.Stdout);
            Assert.Contains("build scaffold", console.Stdout);
            Assert.Contains("docs/proposals/my-proposal.md", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedMode_WithNoDoc_SummaryOmitsScaffoldPointer()
    {
        var dir = MakeTempDir();
        try
        {
            // No docs/op-docs or docs/proposals directory exists.
            var resolver = new FakeResolver("no-doc-proj-id", ProjectResolveOutcome.Found);
            var provisioner = new FakeProvisioner();
            var connectivity = new FakeConnectivity();
            var localRepo = new ReadyLocalRepo();

            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so",
                workspace: "acme",
                token: "tok",
                projectName: "Clean Project",
                resolverOverride: resolver,
                setupFactory: _ => (provisioner, connectivity),
                localRepoOverride: localRepo);

            Assert.Equal(0, result);
            Assert.DoesNotContain("Scaffold:", console.Stdout);
            Assert.DoesNotContain("build scaffold", console.Stdout);
            Assert.DoesNotContain("Op doc:", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_OpDocsDir_ReturnsRelativeForwardSlashPaths()
    {
        var dir = MakeTempDir();
        try
        {
            var opDocsDir = Path.Combine(dir, "docs", "op-docs");
            Directory.CreateDirectory(opDocsDir);
            File.WriteAllText(Path.Combine(opDocsDir, "op-01.md"), "");
            File.WriteAllText(Path.Combine(opDocsDir, "op-02.md"), "");

            var paths = InitCommand.FindDocPaths(dir);

            Assert.Equal(2, paths.Count);
            // Paths must use forward slashes and be relative to cwd.
            Assert.All(paths, p => Assert.StartsWith("docs/op-docs/", p));
            Assert.All(paths, p => Assert.DoesNotContain('\\', p));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_BothDirs_ReturnsBothSets()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "docs", "op-docs"));
            Directory.CreateDirectory(Path.Combine(dir, "docs", "proposals"));
            File.WriteAllText(Path.Combine(dir, "docs", "op-docs", "op-01.md"), "");
            File.WriteAllText(Path.Combine(dir, "docs", "proposals", "plan-a.md"), "");

            var paths = InitCommand.FindDocPaths(dir);

            Assert.Equal(2, paths.Count);
            Assert.Contains("docs/op-docs/op-01.md", paths);
            Assert.Contains("docs/proposals/plan-a.md", paths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_TopLevelOpDocsDir_IsDetected()
    {
        // WI-08: the operator dropped their doc at top-level op-docs/, not docs/op-docs/.
        var dir = MakeTempDir();
        try
        {
            var topLevel = Path.Combine(dir, "op-docs");
            Directory.CreateDirectory(topLevel);
            File.WriteAllText(Path.Combine(topLevel, "01-survey-site.md"), "");

            var paths = InitCommand.FindDocPaths(dir);

            Assert.Contains("op-docs/01-survey-site.md", paths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_StaysBoundedToAllowList_IgnoresArbitraryMarkdown()
    {
        // Detection must NOT scan arbitrary repo markdown (an explicit op-33 non-goal).
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "");
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "src", "notes.md"), "");
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            File.WriteAllText(Path.Combine(dir, "docs", "guide.md"), "");

            var paths = InitCommand.FindDocPaths(dir);

            Assert.Empty(paths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_NoDirs_ReturnsEmpty()
    {
        var dir = MakeTempDir();
        try
        {
            var paths = InitCommand.FindDocPaths(dir);
            Assert.Empty(paths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindDocPaths_NonMdFilesIgnored()
    {
        var dir = MakeTempDir();
        try
        {
            var opDocsDir = Path.Combine(dir, "docs", "op-docs");
            Directory.CreateDirectory(opDocsDir);
            File.WriteAllText(Path.Combine(opDocsDir, "op-01.md"), "");
            File.WriteAllText(Path.Combine(opDocsDir, "notes.txt"), "");
            File.WriteAllText(Path.Combine(opDocsDir, "schema.json"), "");

            var paths = InitCommand.FindDocPaths(dir);

            Assert.Single(paths);
            Assert.Equal("docs/op-docs/op-01.md", paths[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // WI-07: interactive guided connected init (create-or-pick, no GUID)
    // ------------------------------------------------------------------

    private sealed class StubProjectDiscovery : IProjectDiscovery
    {
        public IReadOnlyList<ProjectInfo> Projects { get; set; } = [];
        public string CreatedId { get; set; } = "created-uuid";
        public string? LastCreatedName { get; private set; }
        public string? LastCreatedIdentifier { get; private set; }
        public int CreateCalls { get; private set; }

        public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct) =>
            Task.FromResult(Projects);

        public Task<string?> FindProjectByNameAsync(string name, CancellationToken ct) =>
            Task.FromResult(Projects
                .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(p => (string?)p.Id)
                .FirstOrDefault());

        public Task<string> CreateProjectAsync(string name, string identifier, CancellationToken ct)
        {
            CreateCalls++;
            LastCreatedName = name;
            LastCreatedIdentifier = identifier;
            return Task.FromResult(CreatedId);
        }
    }

    // Routes every Plane REST call to a canned response so the REAL connected/interactive path
    // (two PlaneTicketingClients: discovery + provisioning) can run without a live server. The
    // project is reported fully provisioned so setup creates nothing; the create-permission probe
    // returns 400, which the client treats as "create allowed". Order-independent.
    private sealed class RoutingPlaneHandler : HttpMessageHandler
    {
        private readonly string _statesJson;
        private readonly string _labelsJson;
        private readonly object _lock = new();
        public List<string> Requests { get; } = new();

        public RoutingPlaneHandler(string statesJson, string labelsJson)
        {
            _statesJson = statesJson;
            _labelsJson = labelsJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;
            lock (_lock) Requests.Add($"{method} {path}");

            static HttpResponseMessage Resp(int status, string body) =>
                new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (method == HttpMethod.Post && path.EndsWith("/projects/", StringComparison.Ordinal))
                return Task.FromResult(Resp(201, "{\"id\":\"created-uuid\"}"));
            if (method == HttpMethod.Get && path.EndsWith("/projects/", StringComparison.Ordinal))
                return Task.FromResult(Resp(200, "{\"results\":[]}"));
            if (method == HttpMethod.Get && path.EndsWith("/states/", StringComparison.Ordinal))
                return Task.FromResult(Resp(200, _statesJson));
            if (method == HttpMethod.Get && path.EndsWith("/labels/", StringComparison.Ordinal))
                return Task.FromResult(Resp(200, _labelsJson));
            if (method == HttpMethod.Post && path.EndsWith("/issues/", StringComparison.Ordinal))
                return Task.FromResult(Resp(400, "{\"name\":[\"create-permission probe\"]}")); // 400 => probe OK
            return Task.FromResult(Resp(200, "{}")); // any provisioning create
        }
    }

    private static string FullyProvisionedStatesJson() =>
        "{\"results\":[" + string.Join(",", WorkspaceSchema.States.Select((s, i) =>
            $"{{\"id\":\"st{i}\",\"name\":\"{s.Name}\",\"group\":\"{s.Group}\",\"sequence\":{i}}}")) + "]}";

    private static string FullyProvisionedLabelsJson() =>
        "{\"results\":[" + string.Join(",", WorkspaceSchema.Labels.Select((l, i) =>
            $"{{\"id\":\"lb{i}\",\"name\":\"{l}\"}}")) + "]}";

    // Regression for the operator crash: "This instance has already started one or more requests"
    // - the interactive flow built a second PlaneTicketingClient on the discovery client's already-
    // used HttpClient. This drives the REAL two-client path (discovery + provisioning) end to end.
    [Fact]
    public async Task Interactive_CreateNew_RealClients_DoesNotReuseHttpClient()
    {
        var dir = MakeTempDir();
        try
        {
            var handler = new RoutingPlaneHandler(FullyProvisionedStatesJson(), FullyProvisionedLabelsJson());
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("c");                // create new
            console.Responses.Enqueue("survey-smoketest4"); // name
            console.Responses.Enqueue("ST");               // identifier

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://plane.example.net", workspace: "throughline", token: "plane_api_x",
                // No discoveryOverride / setupFactory: exercise the real PlaneTicketingClient
                // construction for BOTH discovery and provisioning, each on its own HttpClient.
                localRepoOverride: new EmptyLocalRepo(),
                httpClientFactory: () => new HttpClient(handler));

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("created-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            // Both clients actually ran: discovery created the project, provisioning read states.
            Assert.Contains(handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal) && r.EndsWith("/projects/", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, r => r.StartsWith("GET", StringComparison.Ordinal) && r.EndsWith("/states/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The same fix covers the non-interactive --project-name path (resolver client + provisioning
    // client also previously shared one HttpClient).
    [Fact]
    public async Task ConnectedByName_RealClients_DoesNotReuseHttpClient()
    {
        var dir = MakeTempDir();
        try
        {
            var handler = new RoutingPlaneHandler(FullyProvisionedStatesJson(), FullyProvisionedLabelsJson());
            var console = new FakeConsole(); // redirected stdin: non-interactive

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://plane.example.net", workspace: "throughline", token: "plane_api_x",
                projectName: "survey-smoketest4",
                localRepoOverride: new EmptyLocalRepo(),
                httpClientFactory: () => new HttpClient(handler));

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("created-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_PickExisting_WritesChosenId_MruFirst_NoUuidPrompt()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery
            {
                Projects =
                [
                    new ProjectInfo("uuid-older", "Throughline Build", "TLB", DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
                    new ProjectInfo("uuid-newer", "Survey Smoketest", "ST", DateTimeOffset.Parse("2026-06-05T00:00:00Z")),
                ],
            };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("e");  // use existing
            console.Responses.Enqueue("1");  // pick #1 (most-recently-used)

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            // #1 is the most recently updated -> Survey Smoketest (uuid-newer).
            Assert.Contains("uuid-newer", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            // No project was created (existing was picked).
            Assert.Equal(0, discovery.CreateCalls);
            // The GUID-paste prompt is gone, and the menu is MRU-first.
            Assert.DoesNotContain("Plane project ID", console.Stdout);
            var idxNewer = console.Stdout.IndexOf("Survey Smoketest", StringComparison.Ordinal);
            var idxOlder = console.Stdout.IndexOf("Throughline Build", StringComparison.Ordinal);
            Assert.True(idxNewer >= 0 && idxOlder >= 0 && idxNewer < idxOlder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_CreateNew_CreatesWithEnteredNameAndIdentifier()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery { Projects = [], CreatedId = "new-proj-uuid" };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("c");                 // create new
            console.Responses.Enqueue("Survey Smoketest");  // project name
            console.Responses.Enqueue("ST");                // identifier

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            Assert.Equal(1, discovery.CreateCalls);
            Assert.Equal("Survey Smoketest", discovery.LastCreatedName);
            Assert.Equal("ST", discovery.LastCreatedIdentifier);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("new-proj-uuid", written);
            Assert.DoesNotContain("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.DoesNotContain("Plane project ID", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_CreateNew_BlankIdentifier_UsesDerivedDefault()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery { Projects = [] };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("c");                 // create new
            console.Responses.Enqueue("Survey Smoketest");  // name
            console.Responses.Enqueue("");                  // identifier blank -> accept derived default

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            // "Survey Smoketest" -> initials "SS".
            Assert.Equal("SS", discovery.LastCreatedIdentifier);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_BlankToken_FallsBackToOfflineTemplate()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("");  // token prompt -> blank (decline to connect)

            // url + workspace supplied as flags; token omitted so it is prompted (and left blank).
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme",
                discoveryOverride: new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] });

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", written);
            // Token was never supplied, so the template's default (safe) env-var form is untouched.
            Assert.Contains("plane_api_token_env = \"PLANE_API_TOKEN\"", written);
            // Offline next-steps hints appear; no create-or-pick prompt was shown.
            Assert.Contains("build setup", console.Stdout);
            Assert.DoesNotContain("Create a new project", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_DeclineAtCreateOrPick_FallsBackToOfflineTemplate()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("");  // blank at create-or-pick -> decline -> offline

            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                discoveryOverride: discovery,
                setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                localRepoOverride: new EmptyLocalRepo());

            Assert.Equal(0, result);
            Assert.Equal(0, discovery.CreateCalls);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", written);
            Assert.Contains("build setup", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Operator bail-out: typing 'q' / 'quit' aborts with InitAbortedException
    // (Program.cs maps it to "Aborted." / exit 5; Ctrl-C stays "Cancelled." / exit 1).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Interactive_QuitAtCreateOrPick_Aborts_WritesNoConfig()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("q"); // bail out at the create-or-pick prompt

            await Assert.ThrowsAsync<InitAbortedException>(() =>
                InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                    planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                    discoveryOverride: discovery,
                    setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                    localRepoOverride: new EmptyLocalRepo()));

            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
            Assert.Equal(0, discovery.CreateCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_QuitWord_AtCreateOrPick_Aborts()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("QUIT"); // full word, any case

            await Assert.ThrowsAsync<InitAbortedException>(() =>
                InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                    planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                    discoveryOverride: new StubProjectDiscovery()));

            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_QuitAtProjectMenu_Aborts_WritesNoConfig()
    {
        var dir = MakeTempDir();
        try
        {
            var discovery = new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] };
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("e"); // use existing -> opens the menu
            console.Responses.Enqueue("q"); // bail out at the menu

            await Assert.ThrowsAsync<InitAbortedException>(() =>
                InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                    planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                    discoveryOverride: discovery,
                    setupFactory: _ => (new FakeProvisioner(), new FakeConnectivity()),
                    localRepoOverride: new EmptyLocalRepo()));

            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
            Assert.Equal(0, discovery.CreateCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_QuitAtConnectionPrompt_Aborts_WritesNoConfig()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole();
            console.Responses.Enqueue("q"); // bail out at the prompted token value

            // url + workspace supplied as flags so only the token is prompted; 'q' there aborts
            // before any connection or write happens.
            await Assert.ThrowsAsync<InitAbortedException>(() =>
                InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                    planeUrl: "https://api.plane.so", workspace: "acme"));

            Assert.False(File.Exists(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoInteractiveFlag_AtTty_NeverPrompts_WritesOfflineTemplate()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeInteractiveConsole(); // TTY (IsInputRedirected == false)
            // No responses enqueued: any prompt would be a bug.
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                noInteractive: true,
                discoveryOverride: new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] });

            Assert.Equal(0, result);
            Assert.DoesNotContain("Create a new project", console.Stdout);
            Assert.DoesNotContain("Plane project ID", console.Stdout);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RedirectedStdin_NeverEntersInteractive()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole(); // IsInputRedirected == true (automation)
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", token: "tok",
                discoveryOverride: new StubProjectDiscovery { Projects = [new ProjectInfo("x", "X", "XX")] });

            Assert.Equal(0, result);
            Assert.DoesNotContain("Create a new project", console.Stdout);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Console whose stdin claims to be redirected but throws on every read - the state a process
    /// is in when its launcher hands it a stdin handle that is closed or was never made inheritable.
    /// Windows reports that handle as redirected (GetFileType cannot classify it) and then fails the
    /// read with ERROR_INVALID_HANDLE, surfaced as IOException("The handle is invalid.").
    /// </summary>
    private sealed class UnreadableStdinConsole : IConsole
    {
        private readonly System.Text.StringBuilder _stdout = new();
        private readonly System.Text.StringBuilder _stderr = new();

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();
        public int ReadAttempts { get; private set; }

        public bool IsInputRedirected => true;
        public void WriteLine(string value) => _stdout.AppendLine(value);
        public void Write(string value) => _stdout.Append(value);
        public void ErrorWriteLine(string value) => _stderr.AppendLine(value);

        public string? ReadLine()
        {
            ReadAttempts++;
            throw new IOException("The handle is invalid.");
        }

        public char? ReadKeyChar() => throw new IOException("The handle is invalid.");
    }

    [Fact]
    public async Task UnreadableRedirectedStdin_StillWritesOfflineConfig()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new UnreadableStdinConsole();

            var result = await InitCommand.ExecuteAsync(
                dir, force: false, printTemplate: false, console, noInteractive: true);

            // Redirected-but-unreadable stdin carries no credentials. That is the same outcome as
            // an empty pipe, so init must complete offline rather than crash out of the read.
            Assert.Equal(0, result);
            Assert.True(console.ReadAttempts > 0, "the stdin creds read should still have been attempted");
            Assert.True(File.Exists(Path.Combine(dir, ".build", "config.toml")));
            Assert.Contains("Created", console.Stdout);
            Assert.Contains("REQUIRED_PLANE_PROJECT_ID", File.ReadAllText(Path.Combine(dir, ".build", "config.toml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnreadableRedirectedStdin_DoesNotDiscardCredentialFlags()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new UnreadableStdinConsole();

            var result = await InitCommand.ExecuteAsync(
                dir, force: false, printTemplate: false, console,
                planeUrl: "https://api.plane.so", workspace: "acme", tokenEnv: "PLANE_API_TOKEN",
                noInteractive: true);

            Assert.Equal(0, result);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("https://api.plane.so", written);
            Assert.Contains("acme", written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
