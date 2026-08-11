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
        var resourceNames = new List<string>(1 + entry.SupplementalProcedureResourceNames.Count)
        {
            entry.ProcedureResourceName,
        };
        resourceNames.AddRange(entry.SupplementalProcedureResourceNames);

        var anchorsByFileName = BuildAnchors(resourceNames);
        var text = new StringBuilder();
        AppendResource(text, entry.ProcedureResourceName, anchorsByFileName);
        foreach (var resourceName in entry.SupplementalProcedureResourceNames)
        {
            if (text.Length > 0 && text[^1] != '\n')
                text.Append('\n');
            text.AppendLine();
            text.AppendLine("---");
            text.AppendLine();
            AppendResource(text, resourceName, anchorsByFileName);
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

    private static IReadOnlyDictionary<string, string> BuildAnchors(IReadOnlyList<string> resourceNames)
    {
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in resourceNames)
        {
            var fileName = GetProcedureFileName(resourceName);
            if (fileName is not null)
                anchors[fileName] = $"sop-resource-{fileName.Replace('.', '-')}";
        }

        return anchors;
    }

    private static void AppendResource(
        StringBuilder text,
        string resourceName,
        IReadOnlyDictionary<string, string> anchorsByFileName)
    {
        var fileName = GetProcedureFileName(resourceName);
        if (fileName is not null && anchorsByFileName.TryGetValue(fileName, out var anchor))
        {
            text.AppendLine($"<a id=\"{anchor}\"></a>");
            text.AppendLine();
        }

        text.Append(RewriteSameBundleLinks(LoadResource(resourceName), anchorsByFileName));
    }

    private static string RewriteSameBundleLinks(
        string content,
        IReadOnlyDictionary<string, string> anchorsByFileName)
    {
        var rewritten = content;
        foreach (var pair in anchorsByFileName)
            rewritten = rewritten.Replace($"]({pair.Key})", $"](#{pair.Value})", StringComparison.Ordinal);
        return rewritten;
    }

    private static string? GetProcedureFileName(string resourceName) => resourceName switch
    {
        SopBundleCatalog.RunBacklogTicketTransactionResource => "ticket-transaction.md",
        SopBundleCatalog.RunBacklogFanOutSchedulingResource => "fan-out-scheduling.md",
        SopBundleCatalog.CrossImpactProcedureResource => "cross-impact.md",
        _ => null,
    };

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
