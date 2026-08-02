using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

internal static class SopResourceLoader
{
    private static readonly Assembly Assembly = typeof(SopResourceLoader).Assembly;
    private static readonly Lazy<HashSet<string>> ResourceNames =
        new(() => new HashSet<string>(Assembly.GetManifestResourceNames(), StringComparer.Ordinal));
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string LoadProcedure(SopCatalogEntry entry)
    {
        var text = new StringBuilder();
        AppendResource(text, entry.ProcedureResourceName);
        foreach (var resourceName in entry.SupplementalProcedureResourceNames)
        {
            if (text.Length > 0 && text[^1] != '\n')
                text.Append('\n');
            text.AppendLine();
            text.AppendLine("---");
            text.AppendLine();
            AppendResource(text, resourceName);
        }

        return text.ToString();
    }

    public static string LoadResource(string resourceName) =>
        Cache.GetOrAdd(resourceName, LoadResourceUncached);

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendResource(StringBuilder text, string resourceName) =>
        text.Append(LoadResource(resourceName));

    private static string LoadResourceUncached(string resourceName)
    {
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", ResourceNames.Value.Order());
            throw new InvalidOperationException(
                $"Embedded SOP resource '{resourceName}' was not found. Available resources: {available}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
