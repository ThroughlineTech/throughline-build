using ThroughlineBuild.Cli;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for ModelsRefreshCommand ('build models refresh'). Uses temp config files
/// and injected probe results - no subprocess, no real codex.
/// </summary>
public class ModelsRefreshCommandTests
{
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

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb-models-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A config with an ACTIVE [workers.codex.sizes] block (the default_agent=codex case),
    // plus a claude block and other sections to assert verbatim preservation. LF endings.
    private static string SampleConfig() => string.Join("\n", new[]
    {
        "[ticketing]",
        "backend = \"plane\"",
        "plane_base_url = \"https://api.plane.so\"",
        "plane_workspace_slug = \"ws\"",
        "plane_project_id = \"pid\"",
        "plane_api_token_env = \"TOK\"",
        "",
        "[workers]",
        "default_agent = \"codex\"",
        "",
        "[workers.claude-code]",
        "executable = \"claude\"",
        "",
        "[workers.claude-code.sizes]",
        "small  = { model = \"haiku\" }",
        "medium = { model = \"sonnet\" }",
        "large  = { model = \"opus\" }",
        "",
        "[workers.codex]",
        "executable = \"codex\"",
        "",
        "[workers.codex.sizes]",
        "# openai prefix optional",
        "small  = { model = \"gpt-old-mini\", effort = \"low\" }",
        "medium = { model = \"gpt-old\", effort = \"medium\" }",
        "large  = { model = \"gpt-old\", effort = \"high\" }",
        "",
        "[events]",
        "log_directory = \".build/events\"",
        "",
    });

    private static string WriteConfig(string dir, string content)
    {
        var buildDir = Path.Combine(dir, ".build");
        Directory.CreateDirectory(buildDir);
        var path = Path.Combine(buildDir, "config.toml");
        File.WriteAllText(path, content);
        return path;
    }

    // Real-shape discovery: most-capable-first, all default medium, full effort menu.
    private static CodexProbeResult OkProbe() => CodexProbeResult.Ok(new CodexModelDiscovery(new[]
    {
        new CodexModelInfo("gpt-5.5", "medium", new[] { "low", "medium", "high", "xhigh" }),
        new CodexModelInfo("gpt-5.4", "medium", new[] { "low", "medium", "high", "xhigh" }),
        new CodexModelInfo("gpt-5.4-mini", "medium", new[] { "low", "medium", "high", "xhigh" }),
    }));

    [Fact]
    public void Refresh_ProbeDiffers_RewritesCodexBlock_PrintsDiff_ExitsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var path = WriteConfig(dir, SampleConfig());
            var console = new FakeConsole();

            var code = ModelsRefreshCommand.Execute(dir, console, OkProbe);

            Assert.Equal(0, code);
            var written = File.ReadAllText(path);
            // New tiers from the discovery (large = most capable, escalated to xhigh).
            Assert.Contains("small  = { model = \"gpt-5.4-mini\", effort = \"medium\" }", written);
            Assert.Contains("medium = { model = \"gpt-5.4\", effort = \"medium\" }", written);
            Assert.Contains("large  = { model = \"gpt-5.5\", effort = \"xhigh\" }", written);
            // Discovered-menu comment present (active form: single #).
            Assert.Contains("# models: gpt-5.5, gpt-5.4, gpt-5.4-mini", written);
            // Old models gone.
            Assert.DoesNotContain("gpt-old", written);
            // Diff table printed to stdout (current -> proposed).
            Assert.Contains("Codex tier changes", console.Stdout);
            Assert.Contains("Rewrote [workers.codex.sizes]", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refresh_ProbeMatchesFile_ReportsUpToDate_NoChange()
    {
        var dir = MakeTempDir();
        try
        {
            var path = WriteConfig(dir, SampleConfig());

            // First refresh writes the canonical block.
            ModelsRefreshCommand.Execute(dir, new FakeConsole(), OkProbe);
            var afterFirst = File.ReadAllBytes(path);

            // Second refresh with the same probe must be a no-op.
            var console = new FakeConsole();
            var code = ModelsRefreshCommand.Execute(dir, console, OkProbe);

            Assert.Equal(0, code);
            Assert.Contains("already up to date", console.Stdout);
            Assert.Equal(afterFirst, File.ReadAllBytes(path)); // byte-identical
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refresh_ProbeFails_LeavesFileByteUnchanged_ExitsOne()
    {
        var dir = MakeTempDir();
        try
        {
            var path = WriteConfig(dir, SampleConfig());
            var before = File.ReadAllBytes(path);
            var console = new FakeConsole();

            var code = ModelsRefreshCommand.Execute(dir, console,
                () => CodexProbeResult.Fail(CodexProbeFailureKind.CommandFailed, "codex not found"));

            Assert.Equal(1, code);
            Assert.Contains("could not query Codex", console.Stderr);
            Assert.Contains("left unchanged", console.Stderr);
            Assert.Equal(before, File.ReadAllBytes(path)); // byte-unchanged
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refresh_OnlyCodexBlockChanges_OtherSectionsVerbatim()
    {
        var dir = MakeTempDir();
        try
        {
            var path = WriteConfig(dir, SampleConfig());

            ModelsRefreshCommand.Execute(dir, new FakeConsole(), OkProbe);

            var written = File.ReadAllText(path);
            // Claude block untouched.
            Assert.Contains("small  = { model = \"haiku\" }", written);
            Assert.Contains("medium = { model = \"sonnet\" }", written);
            Assert.Contains("large  = { model = \"opus\" }", written);
            // [workers.codex] header + executable preserved (only the .sizes block changed).
            Assert.Contains("[workers.codex]\nexecutable = \"codex\"", written);
            // Other sections verbatim.
            Assert.Contains("[ticketing]", written);
            Assert.Contains("plane_base_url = \"https://api.plane.so\"", written);
            Assert.Contains("[events]", written);
            Assert.Contains("log_directory = \".build/events\"", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refresh_NoConfigFile_ExitsTwo()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var code = ModelsRefreshCommand.Execute(dir, console, OkProbe);

            Assert.Equal(2, code);
            Assert.Contains("no .build/config.toml", console.Stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UsageText_ContainsModelsRefresh()
    {
        Assert.Contains("build models refresh", CliUsage.UsageText);
    }
}
