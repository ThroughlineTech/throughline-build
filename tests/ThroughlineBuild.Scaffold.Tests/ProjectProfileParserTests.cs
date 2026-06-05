using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public class ProjectProfileParserTests
{
    static ProjectProfileParserTests()
    {
        // Force the source-gen path so these tests catch AOT serialization regressions.
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    }

    private const string ValidJson = """
    {
      "language": "typescript",
      "framework": "react-vite",
      "package_manager": "npm",
      "install_command": "npm install",
      "build_command": "npm run build",
      "test_command": "npm test",
      "dev_command": "npm run dev",
      "review_checks": [
        { "name": "build", "executable": "npm", "arguments": ["run", "build"], "timeout_minutes": 5 },
        { "name": "test", "executable": "npm", "arguments": ["test"], "timeout_minutes": 10 }
      ],
      "regression_checks": [
        { "name": "build", "executable": "npm", "arguments": ["run", "build"], "timeout_minutes": 5 }
      ]
    }
    """;

    [Fact]
    public void ValidProfile_ParsesAllFields()
    {
        Assert.True(ProjectProfileParser.TryParse(ValidJson, out var profile, out var err), err);
        Assert.NotNull(profile);
        Assert.Equal("typescript", profile!.Language);
        Assert.Equal("react-vite", profile.Framework);
        Assert.Equal("npm", profile.PackageManager);
        Assert.Equal("npm run build", profile.BuildCommand);

        Assert.Equal(2, profile.ReviewChecks.Count);
        var build = profile.ReviewChecks[0];
        Assert.Equal("build", build.Name);
        Assert.Equal("npm", build.Executable);
        Assert.Equal(new[] { "run", "build" }, build.Arguments);
        Assert.Equal(5, build.TimeoutMinutes);

        Assert.Single(profile.RegressionChecks);
    }

    [Fact]
    public void MissingRegressionChecks_DefaultsToReviewChecks()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"] }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(profile!.ReviewChecks, profile.RegressionChecks);
    }

    [Fact]
    public void MissingTimeout_DefaultsToFive()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [ { "name": "build", "executable": "npm", "arguments": ["run", "build"] } ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out _));
        Assert.Equal(5, profile!.ReviewChecks[0].TimeoutMinutes);
    }

    [Theory]
    [InlineData("{ \"test_command\": \"npm test\", \"review_checks\": [{\"name\":\"t\",\"executable\":\"npm\"}] }", "build_command")]
    [InlineData("{ \"build_command\": \"npm run build\", \"review_checks\": [{\"name\":\"b\",\"executable\":\"npm\"}] }", "test_command")]
    public void MissingCoreCommand_Fails(string json, string expectedMention)
    {
        Assert.False(ProjectProfileParser.TryParse(json, out var profile, out var err));
        Assert.Null(profile);
        Assert.Contains(expectedMention, err);
    }

    [Fact]
    public void EmptyReviewChecks_Fails()
    {
        var json = """
        { "build_command": "npm run build", "test_command": "npm test", "review_checks": [] }
        """;
        Assert.False(ProjectProfileParser.TryParse(json, out _, out var err));
        Assert.Contains("review_checks", err);
    }

    [Fact]
    public void CheckMissingExecutable_Fails()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [ { "name": "build", "arguments": ["run", "build"] } ]
        }
        """;
        Assert.False(ProjectProfileParser.TryParse(json, out _, out var err));
        Assert.Contains("executable", err);
    }

    [Fact]
    public void Garbage_Fails()
    {
        Assert.False(ProjectProfileParser.TryParse("not json at all", out _, out var err));
        Assert.Contains("parse", err);
    }
}
