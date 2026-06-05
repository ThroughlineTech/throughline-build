using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

// Regression tests for the H1 slug pattern. The original strict pattern (\S+) failed to match
// a multi-word "# Operation:" line at all, so a formatting mistake surfaced as the misleading
// "missing_h1" instead of letting the validator report the real problem (SLUG_INVALID).
public class OpDocParserH1SlugTests
{
    // No reflection-switch setup: these tests only exercise the line parser and validator
    // (no JSON serialization). Toggling the process-global reflection switch here would leak
    // into other test classes that round-trip OpDoc via reflection.

    [Fact]
    public void MultiWordH1_IsRecognized_NotReportedAsMissing()
    {
        var lines = "# Operation: batch-implement cohesive ticket groups\n\nLead paragraph.\n".Split('\n');

        var result = OpDocParser.ParseLines(lines);

        Assert.NotNull(result.Parsed);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("missing_h1"));
        Assert.Equal("batch-implement cohesive ticket groups", result.Parsed!.OperationSlug);
    }

    [Fact]
    public void MultiWordH1_FailsValidationWithSlugInvalid()
    {
        var lines = "# Operation: batch-implement cohesive ticket groups\n\nLead paragraph.\n".Split('\n');

        var parsed = OpDocParser.ParseLines(lines).Parsed;
        Assert.NotNull(parsed);

        var validation = OpDocValidator.Validate(parsed!);
        Assert.Contains(validation.Errors, e => e.Code == "SLUG_INVALID");
    }

    [Fact]
    public void SingleKebabH1_StillParsesCleanly()
    {
        var lines = "# Operation: batch-implement\n\nLead paragraph.\n".Split('\n');

        var result = OpDocParser.ParseLines(lines);

        Assert.NotNull(result.Parsed);
        Assert.Equal("batch-implement", result.Parsed!.OperationSlug);
    }
}
