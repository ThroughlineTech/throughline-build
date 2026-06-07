using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for CredsFileParser.Parse.
/// </summary>
public class CredsFileParserTests
{
    // ------------------------------------------------------------------
    // All five keys - quoted values
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_AllKeysQuoted_ReturnsAllValues()
    {
        var content = """
            plane_base_url = "https://api.plane.so"
            plane_workspace_slug = "my-org"
            plane_api_token = "tok-secret"
            plane_project_id = "uuid-1234"
            plane_project_name = "My Project"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Equal("my-org", result.PlaneWorkspaceSlug);
        Assert.Equal("tok-secret", result.PlaneApiToken);
        Assert.Equal("uuid-1234", result.PlaneProjectId);
        Assert.Equal("My Project", result.PlaneProjectName);
    }

    // ------------------------------------------------------------------
    // Unquoted values
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_UnquotedValues_ReturnsValues()
    {
        var content = """
            plane_base_url = https://api.plane.so
            plane_workspace_slug = my-org
            plane_api_token = tok-secret
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Equal("my-org", result.PlaneWorkspaceSlug);
        Assert.Equal("tok-secret", result.PlaneApiToken);
    }

    // ------------------------------------------------------------------
    // Comment lines are ignored
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_CommentLines_AreIgnored()
    {
        var content = """
            # This is a comment
            plane_base_url = "https://api.plane.so"
            # Another comment
            plane_workspace_slug = "my-org"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Equal("my-org", result.PlaneWorkspaceSlug);
        Assert.Null(result.PlaneApiToken);
    }

    // ------------------------------------------------------------------
    // Blank lines are tolerated
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_BlankLines_AreIgnored()
    {
        var content = "\nplane_base_url = \"https://api.plane.so\"\n\n\nplane_workspace_slug = \"my-org\"\n";

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Equal("my-org", result.PlaneWorkspaceSlug);
    }

    // ------------------------------------------------------------------
    // Unknown keys are silently ignored
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownKeys_AreIgnored()
    {
        var content = """
            plane_base_url = "https://api.plane.so"
            some_other_key = "value"
            backend = "plane"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Null(result.PlaneWorkspaceSlug);
    }

    // ------------------------------------------------------------------
    // Absent keys yield null
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_MissingKeys_AreNull()
    {
        var content = "plane_base_url = \"https://api.plane.so\"";

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
        Assert.Null(result.PlaneWorkspaceSlug);
        Assert.Null(result.PlaneApiToken);
        Assert.Null(result.PlaneProjectId);
        Assert.Null(result.PlaneProjectName);
    }

    // ------------------------------------------------------------------
    // Empty input
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_EmptyContent_ReturnsAllNull()
    {
        var result = CredsFileParser.Parse(string.Empty);

        Assert.Null(result.PlaneBaseUrl);
        Assert.Null(result.PlaneWorkspaceSlug);
        Assert.Null(result.PlaneApiToken);
        Assert.Null(result.PlaneProjectId);
        Assert.Null(result.PlaneProjectName);
    }

    // ------------------------------------------------------------------
    // plane_project_id present bypasses name resolution
    // (i.e. both project_id and project_name can be present; both are parsed)
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_BothProjectIdAndProjectName_BothParsed()
    {
        var content = """
            plane_project_id = "uuid-5678"
            plane_project_name = "Alpha"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("uuid-5678", result.PlaneProjectId);
        Assert.Equal("Alpha", result.PlaneProjectName);
    }

    [Fact]
    public void Parse_OnlyProjectName_ProjectIdIsNull()
    {
        var content = "plane_project_name = \"Beta Project\"";

        var result = CredsFileParser.Parse(content);

        Assert.Equal("Beta Project", result.PlaneProjectName);
        Assert.Null(result.PlaneProjectId);
    }

    // ------------------------------------------------------------------
    // Inline comment on unquoted value is stripped
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_UnquotedValueWithInlineComment_CommentStripped()
    {
        var content = "plane_workspace_slug = my-org  # workspace slug";

        var result = CredsFileParser.Parse(content);

        Assert.Equal("my-org", result.PlaneWorkspaceSlug);
    }

    // ------------------------------------------------------------------
    // Whitespace around = is tolerated
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_WhitespaceAroundEquals_Tolerated()
    {
        var content = "  plane_base_url   =   \"https://api.plane.so\"  ";

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://api.plane.so", result.PlaneBaseUrl);
    }

    // ------------------------------------------------------------------
    // Mixed comment and data - realistic creds file shape
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_RealisticCredsFile_ParsesCorrectly()
    {
        var content = """
            # Workspace credentials (reuse across projects)
            plane_base_url = "https://plane.example.com"
            plane_workspace_slug = "acme"
            plane_api_token = "secret-token"

            # Per-project: change this per repo
            plane_project_name = "Backend API"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://plane.example.com", result.PlaneBaseUrl);
        Assert.Equal("acme", result.PlaneWorkspaceSlug);
        Assert.Equal("secret-token", result.PlaneApiToken);
        Assert.Null(result.PlaneProjectId);
        Assert.Equal("Backend API", result.PlaneProjectName);
    }

    // ------------------------------------------------------------------
    // Realistic creds file with project_id to bypass resolution
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_RealisticCredsFileWithProjectId_AllFiveFieldsParsed()
    {
        var content = """
            plane_base_url = "https://plane.example.com"
            plane_workspace_slug = "acme"
            plane_api_token = "secret-token"
            plane_project_id = "aaaabbbb-1234-5678-cccc-ddddeeee0001"
            plane_project_name = "Backend API"
            """;

        var result = CredsFileParser.Parse(content);

        Assert.Equal("https://plane.example.com", result.PlaneBaseUrl);
        Assert.Equal("acme", result.PlaneWorkspaceSlug);
        Assert.Equal("secret-token", result.PlaneApiToken);
        Assert.Equal("aaaabbbb-1234-5678-cccc-ddddeeee0001", result.PlaneProjectId);
        Assert.Equal("Backend API", result.PlaneProjectName);
    }
}
