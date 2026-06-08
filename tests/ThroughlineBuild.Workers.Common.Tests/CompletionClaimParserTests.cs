using System.Text.Json;
using Xunit;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Common.Tests;

// Tests for CompletionClaimParser.TryParse and the AOT source-gen wiring of
// CompletionClaimDto / AcBindingDto via WorkersCommonJsonContext.
public class CompletionClaimParserTests
{
    // --- Source-gen AOT registration ---

    [Fact]
    public void SourceGenContext_HasCompletionClaimDto_Registered()
    {
        // If CompletionClaimDto is not registered, .Default.CompletionClaimDto
        // throws InvalidOperationException at class-load time.
        var dto = JsonSerializer.Deserialize(
            "{\"provides\":[],\"consumes\":[],\"ac_bindings\":[],\"tests_added\":[]}",
            WorkersCommonJsonContext.Default.CompletionClaimDto);

        Assert.NotNull(dto);
        Assert.NotNull(dto.Provides);
        Assert.NotNull(dto.Consumes);
        Assert.NotNull(dto.AcBindings);
        Assert.NotNull(dto.TestsAdded);
    }

    [Fact]
    public void SourceGenContext_HasAcBindingDto_Registered()
    {
        var dto = JsonSerializer.Deserialize(
            "{\"ac_ref\":\"AC-1\",\"kind\":\"Test\"}",
            WorkersCommonJsonContext.Default.AcBindingDto);

        Assert.NotNull(dto);
        Assert.Equal("AC-1", dto.AcRef);
        Assert.Equal(VerifierKind.Test, dto.Kind);
    }

    // --- Happy-path parsing ---

    [Fact]
    public void TryParse_EmptyArrays_ReturnsClaimWithEmptyLists()
    {
        var json = "{\"provides\":[],\"consumes\":[],\"ac_bindings\":[],\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.True(ok);
        Assert.NotNull(claim);
        Assert.Null(error);
        Assert.Empty(claim!.Provides);
        Assert.Empty(claim.Consumes);
        Assert.Empty(claim.AcBindings);
        Assert.Empty(claim.TestsAdded);
    }

    [Fact]
    public void TryParse_PopulatedArrays_ReturnsClaimWithAllFields()
    {
        var json =
            "{\"provides\":[\"src/Foo.cs\",\"src/Bar.cs\"]," +
            "\"consumes\":[\"src/Dep.cs\"]," +
            "\"ac_bindings\":[{\"ac_ref\":\"AC-1\",\"kind\":\"Test\"},{\"ac_ref\":\"AC-2\",\"kind\":\"GrepPresent\"}]," +
            "\"tests_added\":[\"FooTests.TestMethod\"]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.True(ok);
        Assert.NotNull(claim);
        Assert.Null(error);
        Assert.Equal(new[] { "src/Foo.cs", "src/Bar.cs" }, claim!.Provides);
        Assert.Equal(new[] { "src/Dep.cs" }, claim.Consumes);
        Assert.Equal(2, claim.AcBindings.Count);
        Assert.Equal("AC-1", claim.AcBindings[0].AcRef);
        Assert.Equal(VerifierKind.Test, claim.AcBindings[0].Kind);
        Assert.Equal("AC-2", claim.AcBindings[1].AcRef);
        Assert.Equal(VerifierKind.GrepPresent, claim.AcBindings[1].Kind);
        Assert.Equal(new[] { "FooTests.TestMethod" }, claim.TestsAdded);
    }

    [Fact]
    public void TryParse_AllVerifierKinds_ParseCorrectly()
    {
        var kinds = new[] { "Test", "GrepPresent", "GrepAbsent", "File", "Exit", "Golden", "Smoke" };
        foreach (var kind in kinds)
        {
            var json =
                $"{{\"provides\":[],\"consumes\":[]," +
                $"\"ac_bindings\":[{{\"ac_ref\":\"AC-1\",\"kind\":\"{kind}\"}}]," +
                $"\"tests_added\":[]}}";

            var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

            Assert.True(ok, $"Expected ok for kind '{kind}', got error: {error}");
            Assert.NotNull(claim);
            Assert.Single(claim!.AcBindings);
        }
    }

    [Fact]
    public void TryParse_ClaimIgnoresUnknownFields()
    {
        var json =
            "{\"provides\":[],\"consumes\":[],\"ac_bindings\":[],\"tests_added\":[]," +
            "\"extra_field\":\"ignored\",\"another\":42}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out _);

        Assert.True(ok);
        Assert.NotNull(claim);
    }

    // --- Failure cases ---

    [Fact]
    public void TryParse_EmptyString_ReturnsFalseWithError()
    {
        var ok = CompletionClaimParser.TryParse("", out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsFalseWithError()
    {
        var ok = CompletionClaimParser.TryParse("not-json", out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("parse error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingProvides_ReturnsFalseWithError()
    {
        var json = "{\"consumes\":[],\"ac_bindings\":[],\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("provides", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingConsumes_ReturnsFalseWithError()
    {
        var json = "{\"provides\":[],\"ac_bindings\":[],\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("consumes", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingAcBindings_ReturnsFalseWithError()
    {
        var json = "{\"provides\":[],\"consumes\":[],\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("ac_bindings", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingTestsAdded_ReturnsFalseWithError()
    {
        var json = "{\"provides\":[],\"consumes\":[],\"ac_bindings\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("tests_added", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_AcBindingMissingAcRef_ReturnsFalseWithError()
    {
        var json =
            "{\"provides\":[],\"consumes\":[]," +
            "\"ac_bindings\":[{\"kind\":\"Test\"}]," +
            "\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("ac_ref", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_AcBindingMissingKind_ReturnsFalseWithError()
    {
        var json =
            "{\"provides\":[],\"consumes\":[]," +
            "\"ac_bindings\":[{\"ac_ref\":\"AC-1\"}]," +
            "\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
        Assert.Contains("kind", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_AcBindingUnknownKind_ReturnsFalseWithError()
    {
        var json =
            "{\"provides\":[],\"consumes\":[]," +
            "\"ac_bindings\":[{\"ac_ref\":\"AC-1\",\"kind\":\"NotAKind\"}]," +
            "\"tests_added\":[]}";

        var ok = CompletionClaimParser.TryParse(json, out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_NullJson_ReturnsFalseWithError()
    {
        // null deserialization result
        var ok = CompletionClaimParser.TryParse("null", out var claim, out var error);

        Assert.False(ok);
        Assert.Null(claim);
        Assert.NotNull(error);
    }
}
