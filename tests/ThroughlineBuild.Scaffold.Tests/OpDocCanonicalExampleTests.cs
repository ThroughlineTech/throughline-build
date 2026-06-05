using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public class OpDocCanonicalExampleTests
{
    static OpDocCanonicalExampleTests()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    }

    [Fact]
    public void EmbeddedExample_ParsesAndValidatesCleanly()
    {
        var parseResult = OpDocParser.ParseLines(OpDocDocsLoader.LoadExample().Split('\n'));

        Assert.NotNull(parseResult.Parsed);
        Assert.Empty(parseResult.Errors);

        var validationResult = OpDocValidator.Validate(parseResult.Parsed);

        Assert.Empty(validationResult.Errors);
        Assert.Empty(validationResult.Warnings);
    }
}
