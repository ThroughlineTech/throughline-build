using System.Reflection;

namespace ThroughlineBuild.Briefs.Tests;

internal static class SnapshotLoader
{
    public static string Load(string snapshotName)
    {
        var assembly = typeof(SnapshotLoader).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Snapshots.{snapshotName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", assembly.GetManifestResourceNames().OrderBy(s => s));
            throw new InvalidOperationException(
                $"Snapshot '{snapshotName}' not found. Available resources: {available}");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
