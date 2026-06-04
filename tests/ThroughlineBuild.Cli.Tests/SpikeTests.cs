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
    public void BuildVersion_is_semver_with_optional_short_sha()
    {
        // "0.1.0" or "0.1.0+09172e5" - a 3-part version, optionally followed by a
        // 7-char hex short sha. Never the full 40-char sha the SDK appends.
        Assert.Matches(@"^\d+\.\d+\.\d+(\+[0-9a-f]{7})?$", BuildVersion.Current);
    }

    [Fact]
    public void BuildVersion_derives_from_build_informational_version()
    {
        var informationalVersion = typeof(BuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        Assert.NotNull(informationalVersion);

        // BuildVersion.Current keeps the version prefix verbatim and truncates the
        // SDK-appended full commit sha to 7 chars.
        var fullParts = informationalVersion!.Split('+', 2);
        var currentParts = BuildVersion.Current.Split('+', 2);

        Assert.Equal(fullParts[0], currentParts[0]);
        if (fullParts.Length == 2 && fullParts[1].Length > 0)
        {
            Assert.Equal(fullParts[1][..7], currentParts[1]);
        }
        else
        {
            Assert.Single(currentParts);
        }
    }
}
