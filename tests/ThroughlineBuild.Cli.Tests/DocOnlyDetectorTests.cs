using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class DocOnlyDetectorTests
{
    [Fact]
    public void IsDocOnly_AllMarkdownFiles_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "README.md", "docs/guide.md", "CHANGELOG.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_MixedListWithCSharpFile_ReturnsFalse()
    {
        // Arrange
        var files = new[] { "README.md", "src/Program.cs", "docs/guide.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDocOnly_AllCSharpFiles_ReturnsFalse()
    {
        // Arrange
        var files = new[] { "src/Program.cs", "src/Utils.cs", "tests/Tests.cs" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDocOnly_EmptyList_ReturnsFalse()
    {
        // Arrange
        var files = new string[] { };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDocOnly_FilesInNestedDocsDirectory_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "docs/guides/tutorial.txt", "docs/api/reference.md", "docs/CONTRIBUTING.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_ReadmeMarkdownFile_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "README.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_ReadmeWithoutExtension_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "src/README", "docs/README" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_TextFiles_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "CHANGES.txt", "NOTES.txt" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_RestructuredTextFiles_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "index.rst", "docs/build.rst" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_AsciiDocFiles_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "README.adoc", "docs/guide.adoc" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_DocExtensionsCaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "README.MD", "docs/GUIDE.MD", "CHANGELOG.MARKDOWN" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_DocumentationDirectoryPathMatches_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "documentation/guide.txt", "documentation/api/reference.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_ReadmeWithVariousExtensions_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "README.md", "readme.txt", "README.rst" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_MixedPathSeparators_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "docs\\guide.md", "docs/tutorial.md" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDocOnly_SingleNonDocFile_ReturnsFalse()
    {
        // Arrange
        var files = new[] { "src/main.cs" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDocOnly_ReadmeNestedDeep_ReturnsTrue()
    {
        // Arrange
        var files = new[] { "src/components/widgets/README" };

        // Act
        var result = DocOnlyDetector.IsDocOnly(files);

        // Assert
        Assert.True(result);
    }
}
