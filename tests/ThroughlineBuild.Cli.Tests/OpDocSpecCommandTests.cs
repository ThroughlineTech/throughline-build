using ThroughlineBuild.Cli;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class OpDocSpecCommandTests
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
        var dir = Path.Combine(Path.GetTempPath(), "tlb455-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void OpDocDocsLoader_LoadSpec_ReturnsEmbeddedSpec()
    {
        var spec = OpDocDocsLoader.LoadSpec();

        Assert.NotEmpty(spec);
        Assert.StartsWith("# Op-Doc Format Spec", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Default_PrintsEmbeddedSpec()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = OpDocSpecCommand.Execute(dir, write: false, force: false, console);

            Assert.Equal(0, result);
            Assert.Equal(OpDocDocsLoader.LoadSpec(), console.Stdout);
            Assert.False(File.Exists(Path.Combine(dir, "docs", "op-docs", "op-doc-spec.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_Write_CreatesDocsCopyMatchingEmbeddedSpec()
    {
        var dir = MakeTempDir();
        try
        {
            var console = new FakeConsole();
            var result = OpDocSpecCommand.Execute(dir, write: true, force: false, console);
            var target = Path.Combine(dir, "docs", "op-docs", "op-doc-spec.md");

            Assert.Equal(0, result);
            Assert.True(File.Exists(target));
            Assert.Equal(OpDocDocsLoader.LoadSpec(), File.ReadAllText(target));
            Assert.Equal(
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(OpDocDocsLoader.LoadSpec()),
                File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_WriteExistingWithoutForce_ReturnsTwoAndDoesNotOverwrite()
    {
        var dir = MakeTempDir();
        try
        {
            var targetDir = Path.Combine(dir, "docs", "op-docs");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, "op-doc-spec.md");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = OpDocSpecCommand.Execute(dir, write: true, force: false, console);

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
    public void Execute_WriteExistingWithForce_Overwrites()
    {
        var dir = MakeTempDir();
        try
        {
            var targetDir = Path.Combine(dir, "docs", "op-docs");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, "op-doc-spec.md");
            File.WriteAllText(target, "# original");

            var console = new FakeConsole();
            var result = OpDocSpecCommand.Execute(dir, write: true, force: true, console);

            Assert.Equal(0, result);
            Assert.Equal(OpDocDocsLoader.LoadSpec(), File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UsageText_ContainsOpDocSpecVerb()
    {
        Assert.Contains("build op-doc spec", CliUsage.UsageText);
        Assert.Contains("--write", CliUsage.UsageText);
    }
}
