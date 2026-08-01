using ThroughlineBuild.Cli;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class PlaneCliErrorTests
{
    [Fact]
    public void AuthFailureMessage_IncludesRepositoryScopedContext_AndRedactsSecrets()
    {
        using var fixture = AuthFixture.Create();
        var ex = new PlaneApiException(403, "server echoed SECRET_TOKEN_SENTINEL");

        var message = PlaneCliError.MessageFor(ex, fixture.Context);

        AssertAuthContext(message, fixture);
    }

    [Fact]
    public void WrappedAuthFailureMessage_PreservesOuterContext_AndRedactsInnerPlaneBody()
    {
        using var fixture = AuthFixture.Create();
        var inner = new PlaneApiException(403, "server echoed SECRET_TOKEN_SENTINEL");
        var outer = new InvalidOperationException(
            $"Ticket TLB-100 was created, but final read-back failed. {inner.Message}",
            inner);

        var message = PlaneCliError.MessageFor(outer, fixture.Context);

        Assert.Contains("Ticket TLB-100 was created", message);
        Assert.Contains("final read-back failed", message);
        AssertAuthContext(message, fixture);
    }

    [Fact]
    public void WrappedNonAuthFailureMessage_PreservesOuterMessage()
    {
        var inner = new PlaneApiException(500, "server broke");
        var outer = new InvalidOperationException($"Ticket TLB-100 was created. {inner.Message}", inner);

        var message = PlaneCliError.MessageFor(outer, context: null);

        Assert.Contains("Ticket TLB-100 was created", message);
        Assert.Contains("server broke", message);
    }

    [Fact]
    public void NonAuthFailureMessage_PreservesPlaneMessage()
    {
        var ex = new PlaneApiException(500, "server broke");

        var message = PlaneCliError.MessageFor(ex, context: null);

        Assert.Contains("Plane API returned 500", message);
        Assert.Contains("server broke", message);
    }

    private static void AssertAuthContext(string message, AuthFixture fixture)
    {
        Assert.Contains(Path.GetFullPath(fixture.ConfigPath), message);
        Assert.Contains(Path.GetFullPath(fixture.RepoRoot), message);
        Assert.Contains("my-workspace", message);
        Assert.Contains("My Project (project-123)", message);
        Assert.Contains("repository-local", message);
        Assert.Contains("sibling repositories", message);
        Assert.Contains("build init", message);
        Assert.DoesNotContain("SECRET_TOKEN_SENTINEL", message);
        Assert.DoesNotContain("server echoed", message);
        Assert.DoesNotContain("plane_api_token", message);
    }

    private sealed class AuthFixture : IDisposable
    {
        private AuthFixture(string repoRoot, string configPath, CliContext context)
        {
            RepoRoot = repoRoot;
            ConfigPath = configPath;
            Context = context;
        }

        public string RepoRoot { get; }
        public string ConfigPath { get; }
        public CliContext Context { get; }

        public static AuthFixture Create()
        {
            var repoRoot = Path.Combine(Path.GetTempPath(), "plane-auth-" + Guid.NewGuid().ToString("N"));
            var buildDir = Path.Combine(repoRoot, ".build");
            Directory.CreateDirectory(buildDir);
            var configPath = Path.Combine(buildDir, "config.toml");
            File.WriteAllText(configPath, """
            [ticketing]
            backend = "plane"
            plane_base_url = "https://plane.example.com"
            plane_workspace_slug = "my-workspace"
            plane_project_id = "project-123"
            plane_project_name = "My Project"
            plane_api_token = "SECRET_TOKEN_SENTINEL"

            [llm]
            default_model = ""
            anthropic_api_key_env = ""

            [workers]
            default_agent = "claude-code"
            timeout_minutes = 20

            [workers.claude-code]
            executable = "claude"

            [workers.claude-code.sizes]
            small = { model = "haiku" }
            medium = { model = "sonnet" }
            large = { model = "opus" }

            [events]
            log_directory = ".build/events"
            """);

            var config = BuildConfigLoader.Load(configPath);
            return new AuthFixture(repoRoot, configPath, new CliContext(repoRoot, repoRoot, configPath, config));
        }

        public void Dispose()
        {
            Context.Dispose();
            if (Directory.Exists(RepoRoot))
                Directory.Delete(RepoRoot, recursive: true);
        }
    }
}
