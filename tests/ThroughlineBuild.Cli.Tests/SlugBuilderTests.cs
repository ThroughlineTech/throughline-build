using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class SlugBuilderTests
{
    [Fact]
    public void BuildBranchSlug_CanonicalCase_ReturnsExpectedSlug()
    {
        // Arrange
        var ticketId = "TT2-113";
        var title = "Register canonical-symbol normalizers in DI";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal("tt2-113-register-canonical-symbol-normalizers-in-di", result);
    }

    [Fact]
    public void BuildBranchSlug_VeryLongTitle_TruncatesTo80Chars()
    {
        // Arrange
        var ticketId = "TLB-42";
        var title = "This is a very long title that has way more than eighty characters when combined with the ticket id and will definitely need to be truncated";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.True(result.Length <= 80, $"Result length {result.Length} exceeds 80 chars");
        Assert.DoesNotMatch(@"-$", result); // No trailing hyphen
    }

    [Fact]
    public void BuildBranchSlug_SpecialCharacters_CollapsesToSingleHyphens()
    {
        // Arrange
        var ticketId = "TLB-10";
        var title = "Fix: auth & session!! management";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal("tlb-10-fix-auth-session-management", result);
        Assert.DoesNotContain("--", result); // No consecutive hyphens
    }

    [Fact]
    public void BuildBranchSlug_NonAsciiCharacters_StripsThem()
    {
        // Arrange
        var ticketId = "TLB-20";
        var title = "Add support for caf (accent on a)";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal("tlb-20-add-support-for-caf-accent-on-a", result);
        // Verify result is ASCII-only
        foreach (var c in result)
        {
            Assert.True(c <= 127, $"Found non-ASCII character: {c}");
        }
    }

    [Fact]
    public void BuildBranchSlug_EmptyTitle_ReturnsLowercasedTicketId()
    {
        // Arrange
        var ticketId = "TLB-5";
        var title = "";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal("tlb-5", result);
        Assert.DoesNotMatch(@"-$", result); // No trailing hyphen
    }

    [Fact]
    public void BuildBranchSlug_ConsecutiveHyphens_CollapseToOne()
    {
        // Arrange
        var ticketId = "TLB-15";
        var title = "---multiple---hyphens---here---";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.DoesNotContain("--", result);
        Assert.DoesNotMatch(@"^-", result); // No leading hyphen
        Assert.DoesNotMatch(@"-$", result); // No trailing hyphen
    }

    [Fact]
    public void BuildBranchSlug_MixedCase_AlwaysLowercase()
    {
        // Arrange
        var ticketId = "TLB-25";
        var title = "SoMe RaNdOm CaSe TeXt";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal(result, result.ToLowerInvariant());
    }

    [Fact]
    public void BuildBranchSlug_ResultNeverStartsOrEndsWithHyphen()
    {
        // Arrange
        var ticketId = "TLB-99";
        var title = "!!!start and end with special chars!!!";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.DoesNotMatch(@"^-", result);
        Assert.DoesNotMatch(@"-$", result);
    }

    [Fact]
    public void BuildBranchSlug_OnlySpecialCharactersInTitle_ReturnsTicketIdOnly()
    {
        // Arrange
        var ticketId = "TLB-7";
        var title = "!@#$%^&*()";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        Assert.Equal("tlb-7", result);
    }

    [Fact]
    public void BuildBranchSlug_UnicodeCharacters_Stripped()
    {
        // Arrange
        var ticketId = "TLB-30";
        var title = "Add emoji support test";

        // Act
        var result = SlugBuilder.BuildBranchSlug(ticketId, title);

        // Assert
        foreach (var c in result)
        {
            Assert.True(c <= 127, $"Found non-ASCII character: {c}");
        }
    }
}
