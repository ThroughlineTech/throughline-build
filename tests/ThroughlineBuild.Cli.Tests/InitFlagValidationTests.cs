using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for CliArgParser.FindUnknownFlag, the validator that backs WI-03: 'build init'
/// must reject unknown/misspelled flags (e.g. --workplace for --workspace) instead of
/// silently dropping them and falling through to a raw project-id prompt.
/// </summary>
public class InitFlagValidationTests
{
    private static readonly IReadOnlySet<string> InitBoolFlags =
        new HashSet<string>(StringComparer.Ordinal) { "--force", "--print-template" };

    private static readonly IReadOnlySet<string> InitValueFlags =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "--plane-url", "--workspace", "--project-id", "--project-name",
            "--token", "--token-env", "--from",
        };

    private static string? Find(params string[] args) =>
        CliArgParser.FindUnknownFlag(args, InitBoolFlags, InitValueFlags);

    [Fact]
    public void UnknownFlag_IsReported()
    {
        // The real first confusing session: --workplace is not --workspace.
        var unknown = Find("init", "--workplace", "throughline", "--token", "plane_api_x");
        Assert.Equal("--workplace", unknown);
    }

    [Fact]
    public void AllRecognizedFlags_ReturnNull()
    {
        var unknown = Find(
            "init",
            "--plane-url", "https://plane.example.net",
            "--workspace", "throughline",
            "--project-name", "Survey Smoketest",
            "--token", "plane_api_x",
            "--force");
        Assert.Null(unknown);
    }

    [Fact]
    public void ValueThatLooksLikeAWord_IsNotMisclassified()
    {
        // "setup" is a value of --project-name, not a verb/flag - must not be reported.
        var unknown = Find("init", "--project-name", "setup", "--workspace", "throughline");
        Assert.Null(unknown);
    }

    [Fact]
    public void ValueFlag_ConsumesFollowingToken_EvenIfItStartsWithDashes()
    {
        // A bizarre value like "--weird" supplied to a recognized value flag is its value,
        // not an unknown flag.
        var unknown = Find("init", "--workspace", "--weird-but-a-value");
        Assert.Null(unknown);
    }

    [Fact]
    public void BoolFlagsAlone_AreAccepted()
    {
        Assert.Null(Find("init", "--print-template"));
        Assert.Null(Find("init", "--force"));
    }

    [Fact]
    public void UnknownFlagAfterRecognizedFlags_IsStillCaught()
    {
        var unknown = Find("init", "--workspace", "throughline", "--bogus");
        Assert.Equal("--bogus", unknown);
    }

    [Fact]
    public void NoFlags_ReturnsNull()
    {
        Assert.Null(Find("init"));
    }
}
