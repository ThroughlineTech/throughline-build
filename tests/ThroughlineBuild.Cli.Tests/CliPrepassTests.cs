using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class CliPrepassTests
{
    [Fact]
    public void ExtractBoolFlags_RemovesAllRecognizedFlagsAndPreservesPositionals()
    {
        string[] args =
        [
            "chain",
            "TLB-568",
            "--debug",
            "--quiet",
            "--summary-json",
            "--json",
            "--error-location",
            "--no-auto-resolve",
            "--no-auto-merge",
            "--no-push",
            "--continue-past-failure",
            "--from-brief",
            "--skip-baseline",
            "--max-depth",
            "3",
        ];

        var result = CliArgParser.ExtractBoolFlags(args);

        Assert.True(result.Debug);
        Assert.True(result.Quiet);
        Assert.True(result.SummaryJson);
        Assert.True(result.Json);
        Assert.True(result.ErrorLocation);
        Assert.True(result.NoAutoResolve);
        Assert.True(result.NoAutoMerge);
        Assert.True(result.NoPush);
        Assert.True(result.ContinuePastFailure);
        Assert.True(result.FromBrief);
        Assert.True(result.SkipBaseline);
        Assert.Equal(["chain", "TLB-568", "--max-depth", "3"], result.Remaining);
    }

    [Fact]
    public void ExtractChainTraversalFlags_RemovesTraversalFlags()
    {
        string[] args = ["chain", "TLB-568", "--dry-run", "--max-depth", "4", "--agent", "codex"];

        var result = CliArgParser.ExtractChainTraversalFlags(args);

        Assert.Null(result.Error);
        Assert.True(result.DryRun);
        Assert.Equal("4", result.MaxDepth);
        Assert.Equal(["chain", "TLB-568", "--agent", "codex"], result.Remaining);
    }

    [Fact]
    public void ExtractChainTraversalFlags_MissingDepthValueReturnsUsageError()
    {
        string[] args = ["chain", "TLB-568", "--max-depth", "--dry-run"];

        var result = CliArgParser.ExtractChainTraversalFlags(args);

        Assert.Equal("Error: --max-depth requires a non-negative integer", result.Error);
    }
}
