using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class DriftComparatorTests
{
    [Fact]
    public void Compare_SameSha_ReturnsNoDrift()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "abc123";
        var relevantFiles = new[] { "src/file1.cs", "src/file2.cs" };
        var filesChangedBetween = new[] { "src/file1.cs", "src/file3.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.False(result.HasDrift);
        Assert.Empty(result.OverlappingFiles);
    }

    [Fact]
    public void Compare_DifferentShasNoOverlap_ReturnsNoDrift()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new[] { "src/file1.cs", "src/file2.cs" };
        var filesChangedBetween = new[] { "src/file3.cs", "src/file4.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.False(result.HasDrift);
        Assert.Empty(result.OverlappingFiles);
    }

    [Fact]
    public void Compare_DifferentShasWithOverlap_ReturnsDriftAndOverlappingFiles()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new[] { "src/file1.cs", "src/file2.cs", "src/file3.cs" };
        var filesChangedBetween = new[] { "src/file2.cs", "src/file3.cs", "src/file4.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.True(result.HasDrift);
        Assert.Equal(2, result.OverlappingFiles.Count);
        Assert.Contains("src/file2.cs", result.OverlappingFiles);
        Assert.Contains("src/file3.cs", result.OverlappingFiles);
    }

    [Fact]
    public void Compare_EmptyRelevantFiles_ReturnsNoDrift()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new string[] { };
        var filesChangedBetween = new[] { "src/file1.cs", "src/file2.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.False(result.HasDrift);
        Assert.Empty(result.OverlappingFiles);
    }

    [Fact]
    public void Compare_EmptyFilesChangedBetween_ReturnsNoDrift()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new[] { "src/file1.cs", "src/file2.cs" };
        var filesChangedBetween = new string[] { };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.False(result.HasDrift);
        Assert.Empty(result.OverlappingFiles);
    }

    [Fact]
    public void Compare_OverlappingFilesAreSorted()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new[] { "z/file.cs", "a/file.cs", "m/file.cs" };
        var filesChangedBetween = new[] { "z/file.cs", "a/file.cs", "m/file.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.True(result.HasDrift);
        Assert.Equal(new[] { "a/file.cs", "m/file.cs", "z/file.cs" }, result.OverlappingFiles);
    }

    [Fact]
    public void Compare_CaseSensitiveFileMatching()
    {
        // Arrange
        var markerSha = "abc123";
        var currentSha = "def456";
        var relevantFiles = new[] { "src/File.cs", "src/file.cs" };
        var filesChangedBetween = new[] { "src/file.cs" };

        // Act
        var result = DriftComparator.Compare(markerSha, currentSha, relevantFiles, filesChangedBetween);

        // Assert
        Assert.True(result.HasDrift);
        Assert.Single(result.OverlappingFiles);
        Assert.Equal("src/file.cs", result.OverlappingFiles[0]);
    }
}
