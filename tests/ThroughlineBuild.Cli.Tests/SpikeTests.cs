using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class SpikeTests
{
    [Fact]
    public void Runtime_major_version_is_at_least_8()
    {
        Assert.True(Environment.Version.Major >= 8,
            $"Expected .NET 8+, got {Environment.Version}");
    }

    [Fact]
    public void String_to_lower_invariant_returns_lowercase()
    {
        Assert.Equal("throughline", "THROUGHLINE".ToLowerInvariant());
    }
}
