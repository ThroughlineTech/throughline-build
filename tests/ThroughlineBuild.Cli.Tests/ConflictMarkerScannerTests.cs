using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ConflictMarkerScannerTests
{
    [Fact]
    public async Task ScanAsync_CleanFile_ReturnsEmptyResult()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "line 1\nline 2\nline 3\n");
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Empty(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_SingleConflictBlock_ReturnsThreeHits()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "line 1\n<<<<<<<\nline 2\n=======\nline 3\n>>>>>>>\nline 4\n";
            await File.WriteAllTextAsync(tempFile, content);
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.Contains(result, h => h.LineNumber == 2 && h.MarkerKind == "start");
            Assert.Contains(result, h => h.LineNumber == 4 && h.MarkerKind == "separator");
            Assert.Contains(result, h => h.LineNumber == 6 && h.MarkerKind == "end");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_MultipleConflictBlocks_ReturnsSixHits()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "line 1\n" +
                         "<<<<<<<\nline 2\n=======\nline 3\n>>>>>>>\n" +
                         "line 4\n" +
                         "<<<<<<<\nline 5\n=======\nline 6\n>>>>>>>\n" +
                         "line 7\n";
            await File.WriteAllTextAsync(tempFile, content);
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Equal(6, result.Count);
            var startMarkers = result.Where(h => h.MarkerKind == "start").ToList();
            var separators = result.Where(h => h.MarkerKind == "separator").ToList();
            var endMarkers = result.Where(h => h.MarkerKind == "end").ToList();

            Assert.Equal(2, startMarkers.Count);
            Assert.Equal(2, separators.Count);
            Assert.Equal(2, endMarkers.Count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_BinaryFile_SkipsFile()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write a file with a NUL byte in the first KB
            var buffer = new byte[100];
            buffer[50] = 0x00;
            File.WriteAllBytes(tempFile, buffer);
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Empty(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_OversizedFile_SkipsFile()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            // Create a file that exceeds 5 MB
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(5 * 1024 * 1024 + 1);
            }

            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Empty(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_EmptyInputList_ReturnsEmptyResult()
    {
        // Arrange
        var filePaths = new string[] { };

        // Act
        var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_MarkerWithoutProperSpacing_NotDetected()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "<<<<<<<X\nsome content\n=======\nline\n>>>>>>>X\n";
            await File.WriteAllTextAsync(tempFile, content);
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            // Only the separator marker should be detected
            Assert.Single(result);
            Assert.Equal("separator", result[0].MarkerKind);
            Assert.Equal(3, result[0].LineNumber);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_MarkerWithSpace_IsDetected()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "<<<<<<< HEAD\nsome content\n=======\nline\n>>>>>>> branch\n";
            await File.WriteAllTextAsync(tempFile, content);
            var filePaths = new[] { tempFile };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.Contains(result, h => h.LineNumber == 1 && h.MarkerKind == "start");
            Assert.Contains(result, h => h.LineNumber == 3 && h.MarkerKind == "separator");
            Assert.Contains(result, h => h.LineNumber == 5 && h.MarkerKind == "end");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScanAsync_NonexistentFile_Skipped()
    {
        // Arrange
        var nonexistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}.txt");
        var filePaths = new[] { nonexistentPath };

        // Act
        var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_MultipleFiles_AllScanned()
    {
        // Arrange
        var tempFile1 = Path.GetTempFileName();
        var tempFile2 = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile1, "<<<<<<<\n=======\n>>>>>>>\n");
            await File.WriteAllTextAsync(tempFile2, "clean\nfile\n");
            var filePaths = new[] { tempFile1, tempFile2 };

            // Act
            var result = await ConflictMarkerScanner.ScanAsync(filePaths, CancellationToken.None);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.All(result, h => Assert.Equal(tempFile1, h.Path));
        }
        finally
        {
            File.Delete(tempFile1);
            File.Delete(tempFile2);
        }
    }
}
