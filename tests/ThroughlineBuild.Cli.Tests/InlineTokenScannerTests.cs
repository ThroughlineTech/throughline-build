using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for the plain-text scan that flags a literal Plane token in a tracked config.toml
/// (TLB-627). Pure string in, string? out - no TOML parsing, no I/O.
/// </summary>
public class InlineTokenScannerTests
{
    [Fact]
    public void Scan_LiteralToken_ReturnsRemediation()
    {
        var remediation = InlineTokenScanner.Scan("plane_api_token = \"plane_api_abc123\"\n");

        Assert.NotNull(remediation);
        Assert.Contains("plane_api_token_env", remediation);
        Assert.DoesNotContain("plane_api_abc123", remediation);
    }

    [Fact]
    public void Scan_PlaceholderValue_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("plane_api_token = \"REQUIRED_PLANE_API_TOKEN\"\n"));
    }

    [Fact]
    public void Scan_EmptyValue_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("plane_api_token = \"\"\n"));
    }

    [Fact]
    public void Scan_EnvVarForm_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("plane_api_token_env = \"PLANE_API_TOKEN\"\n"));
    }

    [Fact]
    public void Scan_TokenFileForm_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("plane_api_token_file = \"secrets/plane-api-token\"\n"));
    }

    [Fact]
    public void Scan_CommentedOutLiteral_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("# plane_api_token = \"your-token-here\"\n"));
    }

    [Fact]
    public void Scan_NoTicketingSection_ReturnsNull()
    {
        Assert.Null(InlineTokenScanner.Scan("[workers]\ndefault_agent = \"claude-code\"\n"));
    }

    [Fact]
    public void Scan_LiteralTokenAmongOtherKeys_ReturnsRemediation()
    {
        var content =
            "[ticketing]\n" +
            "backend = \"plane\"\n" +
            "plane_base_url = \"https://api.plane.so\"\n" +
            "plane_api_token = \"plane_api_live_secret\"\n" +
            "plane_workspace_slug = \"acme\"\n";

        var remediation = InlineTokenScanner.Scan(content);

        Assert.NotNull(remediation);
        Assert.DoesNotContain("plane_api_live_secret", remediation);
    }
}
