using System.Reflection;

namespace ThroughlineBuild.Scaffold;

public static class OpDocDocsLoader
{
    private const string ResourcePrefix = "ThroughlineBuild.Scaffold.Docs.";
    private const string ExampleStartMarker = "<!-- canonical-example-start -->";
    private const string ExampleEndMarker = "<!-- canonical-example-end -->";
    private static readonly Assembly Assembly = typeof(OpDocDocsLoader).Assembly;

    public static string LoadSpec() => Load("op-doc-spec.md");

    public static string LoadExample()
    {
        string spec = LoadSpec();
        int start = spec.IndexOf(ExampleStartMarker, StringComparison.Ordinal);
        int end = spec.IndexOf(ExampleEndMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                "The embedded op-doc spec does not contain the canonical example markers.");
        }

        string fencedExample = spec[(start + ExampleStartMarker.Length)..end].Trim();
        const string openingFence = "```markdown";
        const string closingFence = "```";
        if (!fencedExample.StartsWith(openingFence, StringComparison.Ordinal)
            || !fencedExample.EndsWith(closingFence, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The canonical op-doc example must be enclosed in a markdown code fence.");
        }

        return fencedExample[openingFence.Length..^closingFence.Length].Trim() + "\n";
    }

    private static string Load(string fileName)
    {
        string resourceName = ResourcePrefix + fileName;
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", Assembly.GetManifestResourceNames().OrderBy(name => name));
            throw new InvalidOperationException(
                $"Embedded op-doc resource '{resourceName}' was not found. Available resources: {available}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
