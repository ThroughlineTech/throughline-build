using ThroughlineBuild.Briefs;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ProjectConfigTests
{
    private const string BaseToml = """
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

    private static string WriteToml(string content, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "config.toml");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public void Load_FullProjectSection_PopulatesAllStackFields()
    {
        var toml = BaseToml + """

[project]
language = "typescript"
framework = "react-vite"
package_manager = "npm"
build_command = "npm run build"
test_command = "npm test"
install_command = "npm install"
dev_command = "npm run dev"
plane_project_url = "https://plane.example.com/workspace/browse/PROJ/"
""";
        var path = WriteToml(toml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("typescript", config.Project.Language);
            Assert.Equal("react-vite", config.Project.Framework);
            Assert.Equal("npm", config.Project.PackageManager);
            Assert.Equal("npm run build", config.Project.BuildCommand);
            Assert.Equal("npm test", config.Project.TestCommand);
            Assert.Equal("npm install", config.Project.InstallCommand);
            Assert.Equal("npm run dev", config.Project.DevCommand);
            Assert.Equal("https://plane.example.com/workspace/browse/PROJ/", config.Project.PlaneProjectUrl);
            Assert.Equal(string.Empty, config.Project.Notes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingProjectSection_ReturnsEmptyProjectContext()
    {
        var path = WriteToml(BaseToml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(ProjectContext.Empty, config.Project);
            Assert.Equal(string.Empty, config.Project.Language);
            Assert.Equal(string.Empty, config.Project.Framework);
            Assert.Equal(string.Empty, config.Project.PackageManager);
            Assert.Equal(string.Empty, config.Project.BuildCommand);
            Assert.Equal(string.Empty, config.Project.TestCommand);
            Assert.Equal(string.Empty, config.Project.InstallCommand);
            Assert.Equal(string.Empty, config.Project.DevCommand);
            Assert.Equal(string.Empty, config.Project.PlaneProjectUrl);
            Assert.Equal(string.Empty, config.Project.Notes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_WorktreeSectionPopulatesRootAndSeedAllowlist()
    {
        var toml = BaseToml + """

[worktree]
root = ".leases"
seed_files = [".dev.vars", ".npmrc"]
""";
        var path = WriteToml(toml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(".leases", config.Worktree.Root);
            Assert.Equal([".dev.vars", ".npmrc"], config.Worktree.SeedFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingWorktreeSectionUsesSafeDefaults()
    {
        var path = WriteToml(BaseToml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(".worktrees/conductor", config.Worktree.Root);
            Assert.Empty(config.Worktree.SeedFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_WavesSectionPopulatesCapAndSerializationRules()
    {
        var toml = BaseToml + """

[waves]
cap = 3

[[waves.serialize]]
kind = "global"
paths = ["package.json", "*.lock", "migrations/**"]

[[waves.serialize]]
kind = "cohesive-module"
paths = ["src/admin", "src/owner"]

[[waves.serialize]]
kind = "pairwise"
paths = ["src/contract.ts", "share-contract.md"]
""";
        var path = WriteToml(toml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(3, config.Waves.Cap);
            Assert.Equal(
                [
                    WaveSerializeKind.Global,
                    WaveSerializeKind.CohesiveModule,
                    WaveSerializeKind.Pairwise,
                ],
                config.Waves.Serialize.Select(rule => rule.Kind));
            Assert.Equal(
                ["package.json", "*.lock", "migrations/**"],
                config.Waves.Serialize[0].Paths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingWavesSectionUsesSafeDefaults()
    {
        var path = WriteToml(BaseToml, out var dir);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal(WavePlanner.DefaultCap, config.Waves.Cap);
            Assert.Empty(config.Waves.Serialize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void Load_InvalidWavesRuleKindFailsConfig(string kind)
    {
        var toml = BaseToml + $"""

[waves]
[[waves.serialize]]
kind = "{kind}"
paths = ["src"]
""";
        var path = WriteToml(toml, out var dir);
        try
        {
            Assert.Throws<ConfigException>(() => BuildConfigLoader.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_NotesFilePresent_InlinesFileContents()
    {
        var notesContent = "## Notes\n\nThis is the project's notes file.\n";
        var toml = BaseToml + """

[project]
language = "csharp"
notes_file = "project-notes.md"
""";
        var path = WriteToml(toml, out var dir);
        var notesPath = Path.Combine(dir, "project-notes.md");
        File.WriteAllText(notesPath, notesContent);
        try
        {
            var config = BuildConfigLoader.Load(path);

            Assert.Equal("csharp", config.Project.Language);
            Assert.Equal(notesContent, config.Project.Notes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_NotesFileMissing_LeavesNotesEmptyAndWarnsToStderr()
    {
        var toml = BaseToml + """

[project]
language = "csharp"
notes_file = "does-not-exist.md"
""";
        var path = WriteToml(toml, out var dir);
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        try
        {
            Console.SetError(capturedErr);

            var config = BuildConfigLoader.Load(path);

            Assert.Equal(string.Empty, config.Project.Notes);
            var stderr = capturedErr.ToString();
            Assert.Contains("does-not-exist.md", stderr);
            Assert.Contains("not found", stderr);
        }
        finally
        {
            Console.SetError(originalErr);
            Directory.Delete(dir, recursive: true);
        }
    }
}
