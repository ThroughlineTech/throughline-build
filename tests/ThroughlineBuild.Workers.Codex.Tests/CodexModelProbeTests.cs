using System.Linq;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexModelProbeTests
{
    // Representative `codex debug models` payload: 2 list-visible models + 1 hidden.
    private const string RepresentativeJson = """
    {
      "models": [
        {
          "slug": "gpt-5.5",
          "default_reasoning_level": "medium",
          "supported_reasoning_levels": [
            { "effort": "minimal" },
            { "effort": "low" },
            { "effort": "medium" },
            { "effort": "high" },
            { "effort": "xhigh" }
          ],
          "visibility": "list"
        },
        {
          "slug": "gpt-5.4-mini",
          "default_reasoning_level": "low",
          "supported_reasoning_levels": [
            { "effort": "low" },
            { "effort": "medium" },
            { "effort": "high" }
          ],
          "visibility": "list"
        },
        {
          "slug": "gpt-5.5-codex-internal",
          "default_reasoning_level": "high",
          "supported_reasoning_levels": [
            { "effort": "high" }
          ],
          "visibility": "hide"
        }
      ]
    }
    """;

    private static void DisableReflectionJson() =>
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);

    [Fact]
    public void TryParse_RepresentativePayload_ReturnsTwoListVisibleModels()
    {
        DisableReflectionJson();

        var discovery = CodexModelProbe.TryParse(RepresentativeJson);

        Assert.NotNull(discovery);
        Assert.Equal(2, discovery!.Models.Count);

        var first = discovery.Models[0];
        Assert.Equal("gpt-5.5", first.Slug);
        Assert.Equal("medium", first.DefaultEffort);
        Assert.Equal(new[] { "minimal", "low", "medium", "high", "xhigh" }, first.SupportedEfforts);

        var second = discovery.Models[1];
        Assert.Equal("gpt-5.4-mini", second.Slug);
        Assert.Equal("low", second.DefaultEffort);
        Assert.Equal(new[] { "low", "medium", "high" }, second.SupportedEfforts);
    }

    [Fact]
    public void TryParse_ExcludesHiddenVisibilityModel()
    {
        DisableReflectionJson();

        var discovery = CodexModelProbe.TryParse(RepresentativeJson);

        Assert.NotNull(discovery);
        Assert.Equal(2, discovery!.Models.Count);
        Assert.DoesNotContain(discovery.Models, m => m.Slug == "gpt-5.5-codex-internal");
    }

    [Fact]
    public void TryParse_Garbage_ReturnsNull()
    {
        DisableReflectionJson();

        Assert.Null(CodexModelProbe.TryParse("not json at all <<<"));
    }

    [Fact]
    public void TryParse_NoModelsKey_ReturnsNull()
    {
        DisableReflectionJson();

        Assert.Null(CodexModelProbe.TryParse("{}"));
    }

    [Fact]
    public void TryParse_EmptyModelsArray_ReturnsEmptyDiscovery()
    {
        DisableReflectionJson();

        var discovery = CodexModelProbe.TryParse("""{"models":[]}""");

        Assert.NotNull(discovery);
        Assert.Empty(discovery!.Models);
    }

    [Fact]
    public void Interpret_NonZeroExit_FailsCommandFailedWithStderr()
    {
        DisableReflectionJson();

        var result = CodexModelProbe.Interpret(exitCode: 1, stdout: "", stderr: "boom");

        Assert.False(result.Success);
        Assert.Equal(CodexProbeFailureKind.CommandFailed, result.FailureKind);
        Assert.Contains("boom", result.Diagnostic);
        Assert.Null(result.Discovery);
    }

    [Fact]
    public void Interpret_ZeroExitGarbage_FailsOutputUnparseable()
    {
        DisableReflectionJson();

        var result = CodexModelProbe.Interpret(exitCode: 0, stdout: "garbage not json", stderr: "");

        Assert.False(result.Success);
        Assert.Equal(CodexProbeFailureKind.OutputUnparseable, result.FailureKind);
        Assert.False(string.IsNullOrEmpty(result.Diagnostic));
        Assert.Null(result.Discovery);
    }

    [Fact]
    public void Interpret_ZeroExitValidJson_SucceedsWithTwoModels()
    {
        DisableReflectionJson();

        var result = CodexModelProbe.Interpret(exitCode: 0, stdout: RepresentativeJson, stderr: "");

        Assert.True(result.Success);
        Assert.NotNull(result.Discovery);
        Assert.Equal(2, result.Discovery!.Models.Count);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task ProbeAsync_MissingExecutable_FailsCommandFailedWithoutThrowing()
    {
        DisableReflectionJson();

        var probe = new CodexModelProbe("definitely-not-codex-xyzzy");
        var result = await probe.ProbeAsync();

        Assert.False(result.Success);
        Assert.Equal(CodexProbeFailureKind.CommandFailed, result.FailureKind);
        Assert.Null(result.Discovery);
    }
}
