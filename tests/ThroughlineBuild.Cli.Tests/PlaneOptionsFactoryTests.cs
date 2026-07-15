using ThroughlineBuild.Cli;
using ThroughlineBuild.Plane;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// TLB-565. The bug these cover was not "the option is wrong" but "the option never
// arrives": PlaneClientOptions.RequestsPerMinute was settable and configurable in
// principle, yet no call site assigned it, so every deployment silently ran the
// Plane-Cloud default. Config-parsing tests alone would NOT have caught that - they
// stop at TicketingConfig. These drive the real toml -> config -> options path, which
// is where the value was being dropped.
public class PlaneOptionsFactoryTests
{
    private const string TomlWithoutBudget = """
[ticketing]
backend = "plane"
plane_base_url = "https://plane.example.net"
plane_workspace_slug = "my-workspace"
plane_project_id = "abc-123"
plane_project_identifier = "TLB"
plane_api_token = "plane_api_token_value"

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

    private static PlaneClientOptions OptionsFromToml(string toml)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var path = Path.Combine(tmpDir, "config.toml");
        File.WriteAllText(path, toml);
        try
        {
            var config = BuildConfigLoader.Load(path);
            return PlaneOptionsFactory.From(config, BuildConfigLoader.ResolveSecrets(config));
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void From_CarriesConfiguredRequestsPerMinute_IntoClientOptions()
    {
        var toml = TomlWithoutBudget.Replace(
            "plane_api_token = \"plane_api_token_value\"",
            "plane_api_token = \"plane_api_token_value\"\nplane_requests_per_minute = 300");

        Assert.Equal(300, OptionsFromToml(toml).RequestsPerMinute);
    }

    [Fact]
    public void From_BudgetOmitted_LeavesCloudSizedDefault()
    {
        Assert.Equal(
            PlaneClientOptions.DefaultRequestsPerMinute,
            OptionsFromToml(TomlWithoutBudget).RequestsPerMinute);
    }

    [Fact]
    public void From_MapsEveryTicketingFieldAndTheToken()
    {
        var options = OptionsFromToml(TomlWithoutBudget);

        Assert.Equal("https://plane.example.net", options.BaseUrl);
        Assert.Equal("my-workspace", options.WorkspaceSlug);
        Assert.Equal("abc-123", options.ProjectId);
        Assert.Equal("TLB", options.ProjectIdentifier);
        // The token comes from resolved secrets, never straight off the config record.
        Assert.Equal("plane_api_token_value", options.ApiToken);
    }
}
