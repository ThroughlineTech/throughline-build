using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for UserGuideCommand.Execute and the UserGuideLoader it delegates to.
/// Tests use in-process calls and temp directories; no subprocess infrastructure required.
/// </summary>
public class UserGuideCommandTests
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
    // Helper: create a fresh temp directory for each test
    // ------------------------------------------------------------------

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb322-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "throughline-build.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    // ------------------------------------------------------------------
    // Template loader
    // ------------------------------------------------------------------

    [Fact]
    public void UserGuideLoader_Load_ReturnsNonEmptyString()
    {
        var guide = UserGuideLoader.Load();
        Assert.NotNull(guide);
        Assert.NotEmpty(guide);
    }

    [Fact]
    public void UserGuideLoader_Load_MatchesTrackedUserGuide()
    {
        var repositoryRoot = FindRepositoryRoot();
        var trackedGuide = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "throughline_build_userguide.md"));

        Assert.Equal(trackedGuide, UserGuideLoader.Load());
    }

    [Fact]
    public void UserGuideLoader_Load_DocumentsBindingSemanticContracts()
    {
        var guide = UserGuideLoader.Load();

        Assert.Contains("For a semantic-risk ticket, record a `Ticket execution contract`", guide);
        Assert.Contains("Every worker brief carries that", guide);
        Assert.Contains("treats the recorded contract as binding.", guide);
    }

    // ------------------------------------------------------------------
    // Execute: file creation
    // ------------------------------------------------------------------

    [Fact]
    public void Execute_NoExistingGuide_CreatesFileAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = UserGuideCommand.Execute(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            Assert.True(File.Exists(Path.Combine(dir, "docs", "throughline_build_userguide.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_NoExistingGuide_CreatesDocs_DirIfMissing()
    {
        var dir = MakeTempDir();
        try
        {
            var docsDir = Path.Combine(dir, "docs");
            Assert.False(Directory.Exists(docsDir));

            var console = new FakeConsole();
            UserGuideCommand.Execute(dir, force: false, printTemplate: false, console);

            Assert.True(Directory.Exists(docsDir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_WrittenFile_IsNonEmpty()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            UserGuideCommand.Execute(dir, force: false, printTemplate: false, console);

            var written = File.ReadAllText(Path.Combine(dir, "docs", "throughline_build_userguide.md"));
            Assert.NotEmpty(written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Execute: clobber guard (exit 2)
    // ------------------------------------------------------------------

    [Fact]
    public void Execute_ExistingGuide_NoForce_ReturnsTwoAndDoesNotOverwrite()
    {
        var dir = MakeTempDir();
        try
        {
            var docsDir = Path.Combine(dir, "docs");
            Directory.CreateDirectory(docsDir);
            var target = Path.Combine(docsDir, "throughline_build_userguide.md");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = UserGuideCommand.Execute(dir, force: false, printTemplate: false, console);

            Assert.Equal(2, result);
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
    public void Execute_ExistingGuide_WithForce_OverwritesAndReturnsZero()
    {
        var dir = MakeTempDir();
        try
        {
            var docsDir = Path.Combine(dir, "docs");
            Directory.CreateDirectory(docsDir);
            var target = Path.Combine(docsDir, "throughline_build_userguide.md");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = UserGuideCommand.Execute(dir, force: true, printTemplate: false, console);

            Assert.Equal(0, result);
            var written = File.ReadAllText(target);
            Assert.NotEqual("# original", written);
            Assert.NotEmpty(written);
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
    public void Execute_PrintTemplate_WritesToStdoutAndReturnsZero_NoFileCreated()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = UserGuideCommand.Execute(dir, force: false, printTemplate: true, console);

            Assert.Equal(0, result);
            Assert.NotEmpty(console.Stdout);
            // No file should be created.
            Assert.False(File.Exists(Path.Combine(dir, "docs", "throughline_build_userguide.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Usage text
    // ------------------------------------------------------------------

    [Fact]
    public void UsageText_ContainsUserGuideVerb()
    {
        Assert.Contains("build user-guide", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_UserGuideVerb_ContainsForceFlag()
    {
        Assert.Contains("--force", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_UserGuideVerb_ContainsPrintTemplateFlag()
    {
        Assert.Contains("--print-template", CliUsage.UsageText);
    }

    // ------------------------------------------------------------------
    // InitCommand pointer
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitCommand_SuccessOutput_MentionsUserGuideVerb()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = await InitCommand.ExecuteAsync(dir, force: false, printTemplate: false, console);

            Assert.Equal(0, result);
            Assert.Contains("user-guide", console.Stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
