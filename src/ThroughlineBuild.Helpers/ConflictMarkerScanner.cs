namespace ThroughlineBuild.Helpers;

public static class ConflictMarkerScanner
{
    private const string StartMarker = "<<<<<<<";
    private const string SeparatorMarker = "=======";
    private const string EndMarker = ">>>>>>>";

    private const int MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    private const int BinaryProbeBytes = 8 * 1024; // 8 KB

    public static async Task<IReadOnlyList<ConflictMarkerHit>> ScanAsync(
        IEnumerable<string> filePaths,
        CancellationToken ct)
    {
        var hits = new List<ConflictMarkerHit>();
        var paths = filePaths.ToList();

        foreach (var filePath in paths)
        {
            ct.ThrowIfCancellationRequested();

            // Skip files that don't exist
            if (!File.Exists(filePath))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);

            // Skip files exceeding the 5 MB ceiling
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                continue;
            }

            // Check for binary content in the first 8 KB
            if (await IsBinaryFileAsync(filePath, ct))
            {
                continue;
            }

            // Read all lines and detect markers
            var lines = await File.ReadAllLinesAsync(filePath, ct);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineNumber = i + 1; // 1-based line numbers

                // Check for separator marker: exactly "======="
                if (line == SeparatorMarker)
                {
                    hits.Add(new ConflictMarkerHit(filePath, lineNumber, "separator"));
                    continue;
                }

                // Check for start marker: starts with "<<<<<<< " or is exactly "<<<<<<<<"
                if (IsStartMarker(line))
                {
                    hits.Add(new ConflictMarkerHit(filePath, lineNumber, "start"));
                    continue;
                }

                // Check for end marker: starts with ">>>>>>> " or is exactly ">>>>>>>"
                if (IsEndMarker(line))
                {
                    hits.Add(new ConflictMarkerHit(filePath, lineNumber, "end"));
                }
            }
        }

        return hits.AsReadOnly();
    }

    private static async Task<bool> IsBinaryFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[BinaryProbeBytes];
            using (var stream = File.OpenRead(filePath))
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, BinaryProbeBytes, ct);

                // Check for NUL byte in the probe buffer
                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0x00)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // If we can't read the file, treat it as binary (skip it)
            return true;
        }

        return false;
    }

    private static bool IsStartMarker(string line)
    {
        if (!line.StartsWith(StartMarker))
        {
            return false;
        }

        // Must be exactly 7 chars or followed by a space
        return line.Length == 7 || (line.Length > 7 && line[7] == ' ');
    }

    private static bool IsEndMarker(string line)
    {
        if (!line.StartsWith(EndMarker))
        {
            return false;
        }

        // Must be exactly 7 chars or followed by a space
        return line.Length == 7 || (line.Length > 7 && line[7] == ' ');
    }
}

public record ConflictMarkerHit(string Path, int LineNumber, string MarkerKind);
