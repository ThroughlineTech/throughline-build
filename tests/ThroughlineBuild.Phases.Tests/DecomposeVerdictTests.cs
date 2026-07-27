using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class DecomposeVerdictTests
{
    private static ChildSpec MakeChildSpec(
        string title = "Child 1",
        string description = "Description",
        string acceptanceCriteria = "AC",
        string size = "S",
        string scopeBoundary = "Scope")
    {
        return new ChildSpec(title, description, acceptanceCriteria, size, scopeBoundary);
    }

    // ------------------------------------------------------------------
    // All-pass case: returns empty list
    // ------------------------------------------------------------------

    [Fact]
    public void Check_ValidSpecs_ReturnsEmpty()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", size: "S", scopeBoundary: "Scope 1"),
            MakeChildSpec("Child 2", size: "M", scopeBoundary: "Scope 2"),
            MakeChildSpec("Child 3", size: "L", scopeBoundary: "Scope 3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------
    // coverage_check: empty scope_boundary triggers failure
    // ------------------------------------------------------------------

    [Fact]
    public void Check_ChildMissingScope_TriggersCoverageCheck()
    {
        var specs = new ChildSpec[]
        {
            MakeChildSpec("Child 1", scopeBoundary: "Scope 1"),
            MakeChildSpec("Child 2", scopeBoundary: ""),  // empty
            MakeChildSpec("Child 3", scopeBoundary: "Scope 3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("coverage_check", failures[0]);
        Assert.Contains("1", failures[0]);  // count
        Assert.Contains("Child 2", failures[0]);
    }

    [Fact]
    public void Check_MultipleMissingScope_ReportedTogether()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", scopeBoundary: ""),
            MakeChildSpec("Child 2", scopeBoundary: null!),  // deliberate: exercises the null-scope path
            MakeChildSpec("Child 3", scopeBoundary: "   ")  // whitespace
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("coverage_check", failures[0]);
        Assert.Contains("3", failures[0]);
    }

    [Fact]
    public void Check_AllMissingScope_ReportedAsCoverageCheck()
    {
        var specs = new[]
        {
            MakeChildSpec("A", scopeBoundary: ""),
            MakeChildSpec("B", scopeBoundary: "")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("coverage_check", failures[0]);
    }

    // ------------------------------------------------------------------
    // uniqueness_check: duplicate titles (case-insensitive) trigger failure
    // ------------------------------------------------------------------

    [Fact]
    public void Check_DuplicateTitle_CaseSensitive_TriggerUniquenessCheck()
    {
        var specs = new[]
        {
            MakeChildSpec("Auth module", scopeBoundary: "S1"),
            MakeChildSpec("Auth Module", scopeBoundary: "S2"),  // different case
            MakeChildSpec("Other", scopeBoundary: "S3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("uniqueness_check", failures[0]);
        Assert.Contains("duplicate", failures[0].ToLowerInvariant());
    }

    [Fact]
    public void Check_ExactDuplicateTitle_TriggersUniquenessCheck()
    {
        var specs = new[]
        {
            MakeChildSpec("Auth", scopeBoundary: "S1"),
            MakeChildSpec("Auth", scopeBoundary: "S2")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("uniqueness_check", failures[0]);
    }

    [Fact]
    public void Check_MultipleDuplicatePairs_ReportedTogether()
    {
        var specs = new[]
        {
            MakeChildSpec("A", scopeBoundary: "S1"),
            MakeChildSpec("a", scopeBoundary: "S2"),  // matches A (case-insensitive)
            MakeChildSpec("B", scopeBoundary: "S3"),
            MakeChildSpec("b", scopeBoundary: "S4")   // matches B
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("uniqueness_check", failures[0]);
    }

    // ------------------------------------------------------------------
    // size_check: unrecognized size triggers failure
    // ------------------------------------------------------------------

    [Fact]
    public void Check_UnrecognizedSize_TriggersizeCheck()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", size: "S", scopeBoundary: "S1"),
            MakeChildSpec("Child 2", size: "XL", scopeBoundary: "S2"),  // invalid
            MakeChildSpec("Child 3", size: "M", scopeBoundary: "S3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("size_check", failures[0]);
        Assert.Contains("1", failures[0]);  // count
        Assert.Contains("Child 2", failures[0]);
        Assert.Contains("XL", failures[0]);
    }

    [Fact]
    public void Check_EmptySize_TriggersizeCheck()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", size: "", scopeBoundary: "S1"),
            MakeChildSpec("Child 2", size: "M", scopeBoundary: "S2")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("size_check", failures[0]);
    }

    [Fact]
    public void Check_NullSize_TriggersizeCheck()
    {
        var specs = new ChildSpec[]
        {
            MakeChildSpec("Child 1", size: null!, scopeBoundary: "S1"),
            MakeChildSpec("Child 2", size: "M", scopeBoundary: "S2")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("size_check", failures[0]);
    }

    [Fact]
    public void Check_SizeCaseInsensitive_Accepts()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", size: "s", scopeBoundary: "S1"),
            MakeChildSpec("Child 2", size: "M", scopeBoundary: "S2"),
            MakeChildSpec("Child 3", size: "l", scopeBoundary: "S3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Empty(failures);
    }

    [Fact]
    public void Check_MultipleBadSizes_ReportedTogether()
    {
        var specs = new[]
        {
            MakeChildSpec("A", size: "XL", scopeBoundary: "S1"),
            MakeChildSpec("B", size: "", scopeBoundary: "S2"),
            MakeChildSpec("C", size: "M", scopeBoundary: "S3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Single(failures);
        Assert.Contains("size_check", failures[0]);
        Assert.Contains("2", failures[0]);  // count of bad sizes
    }

    // ------------------------------------------------------------------
    // Multiple failures reported together
    // ------------------------------------------------------------------

    [Fact]
    public void Check_CoverageAndUniquenessFailures_ReportedTogether()
    {
        var specs = new[]
        {
            MakeChildSpec("A", scopeBoundary: ""),  // missing scope
            MakeChildSpec("B", scopeBoundary: ""),  // missing scope
            MakeChildSpec("B", scopeBoundary: "S3")  // duplicate title
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Equal(2, failures.Count);
        Assert.Single(failures, f => f.Contains("coverage_check"));
        Assert.Single(failures, f => f.Contains("uniqueness_check"));
    }

    [Fact]
    public void Check_AllThreeChecksFail_AllReported()
    {
        var specs = new[]
        {
            MakeChildSpec("Auth", size: "XL", scopeBoundary: ""),  // all three fail
            MakeChildSpec("Auth", size: "M", scopeBoundary: "S2")  // duplicate title, all three fail on first
        };

        var failures = DecomposeVerdict.Check(specs);

        // Should report: coverage_check, uniqueness_check, size_check
        Assert.Equal(3, failures.Count);
        Assert.Single(failures, f => f.Contains("coverage_check"));
        Assert.Single(failures, f => f.Contains("uniqueness_check"));
        Assert.Single(failures, f => f.Contains("size_check"));
    }

    // ------------------------------------------------------------------
    // Boundary: valid L size
    // ------------------------------------------------------------------

    [Fact]
    public void Check_AllValidSizesAccepted()
    {
        var specs = new[]
        {
            MakeChildSpec("Child 1", size: "S", scopeBoundary: "S1"),
            MakeChildSpec("Child 2", size: "M", scopeBoundary: "S2"),
            MakeChildSpec("Child 3", size: "L", scopeBoundary: "S3")
        };

        var failures = DecomposeVerdict.Check(specs);

        Assert.Empty(failures);
    }
}
