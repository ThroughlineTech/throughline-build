using System.Reflection;
using System.Text;

namespace ThroughlineBuild.Cli;

internal static class ConductorPromptLoader
{
    internal const string ResourceName = "ThroughlineBuild.Cli.Templates.ConductorInvariantsPrompt.md";
    private static readonly Lazy<string> CachedPrompt = new(LoadPrompt);

    public static string Load() => CachedPrompt.Value;

    private static string LoadPrompt()
    {
        var assembly = typeof(ConductorPromptLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded conductor prompt '{ResourceName}' was not found");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var prompt = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return prompt.EndsWith('\n') ? prompt : prompt + "\n";
    }
}
