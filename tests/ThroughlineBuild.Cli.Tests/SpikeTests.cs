using System.Reflection;
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

    [Fact]
    public void BuildVersion_is_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildVersion.Current));
    }

    [Fact]
    public void BuildVersion_matches_build_informational_version()
    {
        var informationalVersion = typeof(BuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal(BuildVersion.Current, informationalVersion);
    }
}
