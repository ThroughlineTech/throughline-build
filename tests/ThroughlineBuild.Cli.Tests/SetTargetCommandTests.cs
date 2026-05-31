using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for SetTargetCommand.Execute.
/// Tests use in-process calls and temp directories; git validation is injected via delegate.
/// </summary>
public class SetTargetCommandTests
{
    // ------------------------------------------------------------------
    // Minimal fake console
    // ------------------------------------------------------------------

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
    // Helpers
    // ------------------------------------------------------------------

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb350-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Write a minimal .build/config.toml with the given content.</summary>
    private static string WriteConfig(string dir, string content)
    {
        var buildDir = Path.Combine(dir, ".build");
        Directory.CreateDirectory(buildDir);
        var path = Path.Combine(buildDir, "config.toml");
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    // Branch validator delegates for testing
    private static readonly Func<string, string, bool> BranchExists = (_, _) => true;
    private static readonly Func<string, string, bool> BranchMissing = (_, _) => false;

    // A config with [ship] and [[review.checks]] to verify preservation
    private const string BaseConfig = """
[ship]
remote = "origin"
base_branch = "develop"

[[review.checks]]
name = "test"
executable = "dotnet"
""";

    // ------------------------------------------------------------------
    // Display mode
    // ------------------------------------------------------------------

    [Fact]
    public void Display_NoWorkSection_ShowsShipBaseBranchFallback()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig);
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: false, console);

            Assert.Equal(0, rc);
            Assert.Contains("target_branch = develop", console.Stdout);
            Assert.Contains("default, no [work] override", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Display_WorkSectionWithKey_ShowsWorkValueAndLabel()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\ntarget_branch = \"feature/xyz\"\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: false, console);

            Assert.Equal(0, rc);
            Assert.Contains("target_branch = feature/xyz", console.Stdout);
            Assert.Contains("from [work]", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Display_WorkSectionWithoutKey_ShowsFallback()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\n# no target_branch key\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: false, console);

            Assert.Equal(0, rc);
            Assert.Contains("target_branch = develop", console.Stdout);
            Assert.Contains("default, no [work] override", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Display_NoShipSection_ShowsMainFallback()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, "[ticketing]\nbackend = \"plane\"\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: false, console);

            Assert.Equal(0, rc);
            Assert.Contains("target_branch = main", console.Stdout);
            Assert.Contains("default, no [work] override", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------
    // Set mode
    // ------------------------------------------------------------------

    [Fact]
    public void Set_ValidBranch_NoWorkSection_AppendsWorkSectionAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig);
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: "feature/x", unset: false, console, BranchExists);

            Assert.Equal(0, rc);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("[work]", written);
            Assert.Contains("target_branch = \"feature/x\"", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Set_ValidBranch_WorkSectionPresent_KeyAbsent_InsertsKeyAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\n# placeholder\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: "feature/y", unset: false, console, BranchExists);

            Assert.Equal(0, rc);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("target_branch = \"feature/y\"", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Set_ValidBranch_KeyAlreadyPresent_ReplacesKeyAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\ntarget_branch = \"old-branch\"\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: "new-branch", unset: false, console, BranchExists);

            Assert.Equal(0, rc);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("target_branch = \"new-branch\"", written);
            Assert.DoesNotContain("old-branch", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Set_BranchNotFound_Returns2WithStderrError()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig);
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: "nonexistent", unset: false, console, BranchMissing);

            Assert.Equal(2, rc);
            Assert.Contains("nonexistent", console.Stderr);
            Assert.Contains("does not exist", console.Stderr);
            Assert.Contains("git checkout -b", console.Stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------
    // Unset mode
    // ------------------------------------------------------------------

    [Fact]
    public void Unset_KeyPresent_RemovesKeyAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\ntarget_branch = \"feature/x\"\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: true, console);

            Assert.Equal(0, rc);
            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.DoesNotContain("target_branch", written);
            Assert.Contains("Removed", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Unset_KeyAbsent_WorkSectionPresent_PrintsNoopAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\n# no target_branch\n");
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: true, console);

            Assert.Equal(0, rc);
            Assert.Contains("noop", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Unset_NoWorkSection_PrintsNoopAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig);
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: true, console);

            Assert.Equal(0, rc);
            Assert.Contains("noop", console.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------
    // Missing config
    // ------------------------------------------------------------------

    [Fact]
    public void Execute_NoConfigFile_Returns2WithBuildInitDirective()
    {
        var dir = MakeTempDir();
        try
        {
            // No .build/config.toml written
            var console = new FakeConsole();

            var rc = SetTargetCommand.Execute(dir, branch: null, unset: false, console);

            Assert.Equal(2, rc);
            Assert.Contains("build init", console.Stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------
    // Section preservation
    // ------------------------------------------------------------------

    [Fact]
    public void Set_OtherSectionsPreservedAfterAppend()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig);
            var console = new FakeConsole();

            SetTargetCommand.Execute(dir, branch: "feature/x", unset: false, console, BranchExists);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            // [ship] section and [[review.checks]] entry must still be present
            Assert.Contains("[ship]", written);
            Assert.Contains("base_branch = \"develop\"", written);
            Assert.Contains("[[review.checks]]", written);
            Assert.Contains("name = \"test\"", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Unset_OtherSectionsPreservedAfterRemove()
    {
        var dir = MakeTempDir();
        try
        {
            WriteConfig(dir, BaseConfig + "\n[work]\ntarget_branch = \"feature/x\"\n");
            var console = new FakeConsole();

            SetTargetCommand.Execute(dir, branch: null, unset: true, console);

            var written = File.ReadAllText(Path.Combine(dir, ".build", "config.toml"));
            Assert.Contains("[ship]", written);
            Assert.Contains("base_branch = \"develop\"", written);
            Assert.Contains("[[review.checks]]", written);
            Assert.Contains("name = \"test\"", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------
    // Usage text
    // ------------------------------------------------------------------

    [Fact]
    public void UsageText_ContainsSettargetVerb()
    {
        Assert.Contains("build settarget", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_SettargetVerb_ContainsUnsetFlag()
    {
        Assert.Contains("--unset", CliUsage.UsageText);
    }
}
