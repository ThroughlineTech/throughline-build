using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// TLB-638: 'build setup --write-token-file' persists an already-resolved token to a file and
// records the path (never the token) in config.toml.
public class TokenFileInstallerTests
{
    private static string CreateRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "token-file-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".build"));
        return repo;
    }

    private const string MinimalConfig = """
    [ticketing]
    backend = "plane"
    plane_base_url = "https://plane.invalid"
    plane_workspace_slug = "workspace"
    plane_project_id = "project-id"
    plane_api_token_env = "UNUSED_TOKEN"

    [events]
    log_directory = ".build/events"
    """;

    [Fact]
    public void Write_CreatesFileWithTrimmedTokenAndRecordsRelativePath()
    {
        var repo = CreateRepo();
        try
        {
            var configPath = Path.Combine(repo, ".build", "config.toml");
            File.WriteAllText(configPath, MinimalConfig);

            var result = TokenFileInstaller.Write(repo, configPath, "secrets/plane-api-token", "  a-real-token  \n");

            Assert.True(result.Success, result.Message);
            var tokenPath = Path.Combine(repo, "secrets", "plane-api-token");
            Assert.Equal("a-real-token", File.ReadAllText(tokenPath).Trim());
            Assert.Contains("plane_api_token_file = \"secrets/plane-api-token\"", File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Write_NeverWritesTokenValueIntoConfigToml()
    {
        var repo = CreateRepo();
        try
        {
            var configPath = Path.Combine(repo, ".build", "config.toml");
            File.WriteAllText(configPath, MinimalConfig);

            const string secretToken = "sk-do-not-leak-this-9f3a";
            var result = TokenFileInstaller.Write(repo, configPath, "secrets/plane-api-token", secretToken);

            Assert.True(result.Success, result.Message);
            Assert.DoesNotContain(secretToken, File.ReadAllText(configPath));
            Assert.DoesNotContain(secretToken, result.Message);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Write_OwnerOnlyPermissionsOnPosix()
    {
        if (OperatingSystem.IsWindows()) return;

        var repo = CreateRepo();
        try
        {
            var configPath = Path.Combine(repo, ".build", "config.toml");
            File.WriteAllText(configPath, MinimalConfig);

            var result = TokenFileInstaller.Write(repo, configPath, "secrets/plane-api-token", "a-real-token");

            Assert.True(result.Success, result.Message);
            var mode = File.GetUnixFileMode(Path.Combine(repo, "secrets", "plane-api-token"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Write_RerunReplacesExistingTokenFileKeyInsteadOfDuplicatingIt()
    {
        var repo = CreateRepo();
        try
        {
            var configPath = Path.Combine(repo, ".build", "config.toml");
            File.WriteAllText(configPath, MinimalConfig);

            Assert.True(TokenFileInstaller.Write(repo, configPath, "secrets/plane-api-token", "first-token").Success);
            Assert.True(TokenFileInstaller.Write(repo, configPath, "secrets/renamed-token", "second-token").Success);

            var configText = File.ReadAllText(configPath);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(configText, "plane_api_token_file"));
            Assert.Contains("plane_api_token_file = \"secrets/renamed-token\"", configText);
            Assert.Equal("second-token", File.ReadAllText(Path.Combine(repo, "secrets", "renamed-token")).Trim());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void SetTokenFileKey_PreservesEverythingElseInTheFile()
    {
        const string original = """
        [ticketing]
        backend = "plane"
        plane_base_url = "https://plane.invalid"
        plane_api_token_env = "PLANE_API_TOKEN"

        [llm]
        default_model = "unused"
        """;

        var updated = TokenFileInstaller.SetTokenFileKey(original, "secrets/plane-api-token");

        Assert.Contains("plane_api_token_env = \"PLANE_API_TOKEN\"", updated);
        Assert.Contains("[llm]", updated);
        Assert.Contains("default_model = \"unused\"", updated);
        Assert.Contains("plane_api_token_file = \"secrets/plane-api-token\"", updated);
    }
}
