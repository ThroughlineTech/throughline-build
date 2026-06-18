using System.Reflection;
using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

// Guard: shipped prompt templates must be stack-agnostic. The brief templates render into
// prompts sent to target builds of any stack (TS/dotnet/python/text-doc), so they must never
// name the engine's own repo - its assembly/namespace path (`ThroughlineBuild`) or a captured
// engine commit SHA. A real self-build escalation envelope was once captured verbatim into the
// obsolete-detection example (decompose.md @ 80ccafa), leaking engine state into every plan and
// implement prompt (TLB-bugfix). This institutionalizes the "grep the templates for the leak"
// check across all agent dirs + shared, so a future capture-into-template is caught everywhere,
// not only where a snapshot happens to render it.
public class TemplateStackAgnosticTests
{
    private static readonly string[] PromptSegments =
        { "claude_code", "codex", "copilot", "gemini", "shared" };

    private static IReadOnlyList<(string Name, string Content)> PromptTemplates()
    {
        var assembly = typeof(TemplateLoader).Assembly;
        var asmName = assembly.GetName().Name!;
        var prefixes = PromptSegments
            .Select(seg => $"{asmName}.Templates.{seg}.")
            .ToArray();

        var result = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".md", StringComparison.Ordinal))
                continue;
            if (!prefixes.Any(p => resource.StartsWith(p, StringComparison.Ordinal)))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            result.Add((resource, reader.ReadToEnd()));
        }
        return result;
    }

    [Fact]
    public void PromptTemplates_AreDiscovered()
    {
        // Guard against the filter silently matching nothing, which would make the leak checks
        // below vacuously pass.
        Assert.NotEmpty(PromptTemplates());
    }

    [Fact]
    public void PromptTemplates_DoNotNameEngineAssembly()
    {
        var offenders = PromptTemplates()
            .Where(t => t.Content.Contains("ThroughlineBuild", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Shipped prompt templates must be stack-agnostic and must not name the engine's own "
            + "assembly/namespace ('ThroughlineBuild'). Offending templates: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void PromptTemplates_DoNotEmbedCapturedSelfBuildCommit()
    {
        // The specific SHA that leaked in via the captured obsolete-detection escalate example.
        var offenders = PromptTemplates()
            .Where(t => t.Content.Contains("80ccafa", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Prompt templates must not embed the captured self-build commit 80ccafa. "
            + "Offending templates: " + string.Join(", ", offenders));
    }
}
