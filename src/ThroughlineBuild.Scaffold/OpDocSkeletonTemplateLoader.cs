using System.Collections.Concurrent;
using System.Reflection;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Loads the embedded op-doc skeleton templates (Templates/op-doc-skeleton*.md) that hold the
/// static document shape, keeping each piece of wording in one editable file.
/// Cached, AOT-safe (no disk-relative lookup), reads via GetManifestResourceStream.
/// </summary>
public static class OpDocSkeletonTemplateLoader
{
    private const string ResourcePrefix = "ThroughlineBuild.Scaffold.Templates.";
    private static readonly Assembly Assembly = typeof(OpDocSkeletonTemplateLoader).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Load(string fileName) =>
        Cache.GetOrAdd(fileName, name =>
        {
            string resourceName = ResourcePrefix + name;
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var available = string.Join(", ", Assembly.GetManifestResourceNames().OrderBy(n => n));
                throw new InvalidOperationException(
                    $"Embedded op-doc template '{resourceName}' was not found. Available resources: {available}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
}
