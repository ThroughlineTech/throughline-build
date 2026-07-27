using System;
using System.IO;
using System.Linq;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

/// <summary>
/// Drift guard over the experiment-3 op-doc: every Brief.PreloadFiles path must also appear verbatim in
/// that brief's body text (the prose Inputs read-map). The positive-only Preload block and the prose
/// read-map are two hand-maintained copies of the same path set - this fails CI if a path is added to
/// one and not the other. A TEST over the op-doc DATA, not an engine validation: the engine stays
/// path-blind (experiment 3, plan section 6).
/// </summary>
public class ExperimentThreeOpDocDriftTests
{
    private static readonly string[] OpDocRelParts =
        { "docs", "analysis", "workloads", "survey-experiment-3-and-4.md" };

    private static string FindOpDocPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
                return Path.Combine(new[] { dir.FullName }.Concat(OpDocRelParts).ToArray());
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (throughline-build.sln) from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryPreloadPath_AppearsVerbatimInItsBriefBody()
    {
        var path = FindOpDocPath();
        Assert.True(File.Exists(path), $"experiment-3 op-doc not found at {path}");

        var result = OpDocParser.Parse(path);
        Assert.NotNull(result.Parsed);

        int checkedPaths = 0;
        foreach (var plan in result.Parsed!.Plans)
        {
            foreach (var brief in plan.Briefs)
            {
                if (brief.PreloadFiles.Count == 0)
                    continue;
                var body = BriefBody(brief);
                foreach (var p in brief.PreloadFiles)
                {
                    Assert.True(body.Contains(p, StringComparison.Ordinal),
                        $"Brief {brief.Number:D2} ({brief.Slug}): Preload path '{p}' is not mentioned anywhere " +
                        "in the brief body. The Preload block and the prose read-map have drifted - add the path " +
                        "to both, or remove it from the Preload block.");
                    checkedPaths++;
                }
            }
        }

        // Sanity: the experiment-3 op-doc must actually exercise the mechanism (Briefs 02-08 each declare
        // a Preload block). If this drops, the op-doc lost its Preload blocks and the run would no-op.
        Assert.True(checkedPaths >= 7,
            $"expected the experiment-3 op-doc to declare Preload paths across its cross-brief briefs; only {checkedPaths} checked");
    }

    private static string BriefBody(Brief brief)
    {
        // The parenthetical `Inputs (read these...):` label is absorbed into Goal by the parser (by
        // design - plan section 7 leaves that bug alone), so the read-map prose where the paths live
        // lands in Goal. Join every parsed text field so the check is robust to wherever a path is named.
        return string.Join("\n", new[]
        {
            brief.Goal,
            string.Join("\n", brief.Inputs),
            string.Join("\n", brief.Outputs),
            brief.Notes ?? string.Empty,
            string.Join("\n", brief.AcceptanceCriteria),
            string.Join("\n", brief.OutOfScope),
        });
    }
}
