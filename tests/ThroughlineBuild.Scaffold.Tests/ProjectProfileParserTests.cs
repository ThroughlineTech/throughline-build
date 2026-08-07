using ThroughlineBuild.Contracts;
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
        Assert.Equal("", profile.ContractAuthority);
    }

    [Fact]
    public void ContractAuthority_ParsesWhenPresentAndTrimsWhitespace()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"] }
          ],
          "contract_authority": "  packages/shared-types  "
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal("packages/shared-types", profile!.ContractAuthority);
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

    [Fact]
    public void Canary_PresentOnReviewCheck_ParsesPathAndContent()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            {
              "name": "typecheck",
              "executable": "npm",
              "arguments": ["run", "typecheck"],
              "canary": [
                { "path": "src/probe/__tlb_probe.ts", "content": "let y: number = \"s\";" }
              ]
            }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        var canary = profile!.ReviewChecks[0].Canary;
        Assert.NotNull(canary);
        var only = Assert.Single(canary!);
        Assert.Equal("src/probe/__tlb_probe.ts", only.Path);
        Assert.Equal("let y: number = \"s\";", only.Content);
    }

    [Fact]
    public void Canary_Absent_YieldsNull()
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
        Assert.Null(profile!.ReviewChecks[0].Canary);
    }

    [Fact]
    public void RequiredPaths_PresentOnCheck_ParsesTrimmedBlanksDropped()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            {
              "name": "build",
              "executable": "npm",
              "arguments": ["run", "build"],
              "required_paths": ["package.json", "  src  ", "", "src"]
            }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(new[] { "package.json", "src" }, profile!.ReviewChecks[0].RequiredPaths);
    }

    [Fact]
    public void ConventionFiles_Present_ParsedTrimmedBlanksDropped()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"] }
          ],
          "convention_files": ["src/setupTests.ts", "  vite.config.ts  ", "", "   "]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(new[] { "src/setupTests.ts", "vite.config.ts" }, profile!.ConventionFiles);
    }

    [Fact]
    public void ConventionFiles_Absent_YieldsEmptyNotNull()
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
        Assert.NotNull(profile!.ConventionFiles);
        Assert.Empty(profile.ConventionFiles);
    }

    [Fact]
    public void Role_Explicit_ParsesAdvisoryAndGating()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"], "role": "gating" },
            { "name": "lint", "executable": "npm", "arguments": ["run", "lint"], "role": "advisory" }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(CheckRole.Gating, profile!.ReviewChecks[0].Role);
        Assert.Equal(CheckRole.Advisory, profile.ReviewChecks[1].Role);
    }

    [Fact]
    public void Role_Omitted_InfersAdvisoryForLintFormat_GatingOtherwise()
    {
        // Protects projects scaffolded by an older worker (no role field) and any model that ignores
        // the role instruction: a cosmetic name de-gates, everything else stays gating.
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"] },
            { "name": "lint", "executable": "npm", "arguments": ["run", "lint"] },
            { "name": "format", "executable": "npm", "arguments": ["run", "format"] }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(CheckRole.Gating, profile!.ReviewChecks[0].Role);   // build
        Assert.Equal(CheckRole.Advisory, profile.ReviewChecks[1].Role);  // lint
        Assert.Equal(CheckRole.Advisory, profile.ReviewChecks[2].Role);  // format
    }

    [Fact]
    public void Role_Unrecognized_FallsBackToNameInference()
    {
        var json = """
        {
          "build_command": "npm run build",
          "test_command": "npm test",
          "review_checks": [
            { "name": "build", "executable": "npm", "arguments": ["run", "build"], "role": "blocking" },
            { "name": "lint", "executable": "npm", "arguments": ["run", "lint"], "role": "???" }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(CheckRole.Gating, profile!.ReviewChecks[0].Role);   // unknown role, name build -> gating
        Assert.Equal(CheckRole.Advisory, profile.ReviewChecks[1].Role);  // unknown role, name lint -> advisory
    }

    [Fact]
    public void Role_Setup_ParsedExplicitly()
    {
        var json = """
        {
          "build_command": "xcodebuild build",
          "test_command": "xcodebuild test",
          "review_checks": [
            { "name": "xcodegen", "executable": "xcodegen", "arguments": ["generate"], "role": "setup" },
            { "name": "build", "executable": "xcodebuild", "arguments": ["build"], "role": "gating" }
          ]
        }
        """;
        Assert.True(ProjectProfileParser.TryParse(json, out var profile, out var err), err);
        Assert.Equal(CheckRole.Setup, profile!.ReviewChecks[0].Role);
        Assert.Equal(CheckRole.Gating, profile.ReviewChecks[1].Role);
    }
}
