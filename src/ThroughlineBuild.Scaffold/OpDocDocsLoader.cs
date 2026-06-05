using System.Reflection;

namespace ThroughlineBuild.Scaffold;

public static class OpDocDocsLoader
{
    private const string ResourcePrefix = "ThroughlineBuild.Scaffold.Docs.";
    private static readonly Assembly Assembly = typeof(OpDocDocsLoader).Assembly;

    public static string LoadSpec() => Load("op-doc-spec.md");

    public static string LoadExample() => Load("op-doc-example.md");

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
