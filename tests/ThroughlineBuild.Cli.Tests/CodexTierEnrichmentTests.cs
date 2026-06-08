using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for the three pure Codex-enrichment classes (CodexTierMapper,
/// CodexSizesBlockRenderer, CodexSizesBlockEditor). No IO, no subprocess.
/// </summary>
public class CodexTierEnrichmentTests
{
    // Representative discovery: a main model and a mini.
    private static CodexModelDiscovery RepresentativeDiscovery() => new(new[]
    {
        new CodexModelInfo("gpt-5.5", "medium", new[] { "minimal", "low", "medium", "high", "xhigh" }),
        new CodexModelInfo("gpt-5.4-mini", "low", new[] { "low", "medium", "high" }),
    });

    // ------------------------------------------------------------------
    // CodexTierMapper
    // ------------------------------------------------------------------

    [Fact]
    public void Map_RepresentativeDiscovery_PicksMiniForSmall_MainForLarge_EscalatesLargeEffort()
    {
        var mapping = CodexTierMapper.Map(RepresentativeDiscovery());

        Assert.NotNull(mapping);
        Assert.Equal("gpt-5.4-mini", mapping![WorkerSize.Small].Model);
        Assert.Equal("low", mapping[WorkerSize.Small].Effort);

        Assert.Equal("gpt-5.5", mapping[WorkerSize.Large].Model);
        // Large escalates to the highest supported effort.
        Assert.Equal("xhigh", mapping[WorkerSize.Large].Effort);

        // Medium is present (only one main here, so medium == large model with its default effort).
        Assert.Equal("gpt-5.5", mapping[WorkerSize.Medium].Model);
        Assert.Equal("medium", mapping[WorkerSize.Medium].Effort);
    }

    [Fact]
    public void Map_TwoMains_LargeIsMostCapable_MediumIsNext()
    {
        // 'codex debug models' lists models most-capable-first (verified against the live
        // CLI: gpt-5.5, gpt-5.4, gpt-5.4-mini). large takes the first main, medium the next.
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-5.5", "medium", new[] { "minimal", "low", "medium", "high", "xhigh" }),
            new CodexModelInfo("gpt-5.4", "low", new[] { "low", "medium", "high" }),
            new CodexModelInfo("gpt-5.4-mini", "low", new[] { "low", "medium" }),
        });

        var mapping = CodexTierMapper.Map(discovery);

        Assert.NotNull(mapping);
        Assert.Equal("gpt-5.4-mini", mapping![WorkerSize.Small].Model);   // the mini
        Assert.Equal("gpt-5.5", mapping[WorkerSize.Large].Model);         // first (most capable) main
        Assert.Equal("gpt-5.4", mapping[WorkerSize.Medium].Model);        // second main
        Assert.Equal("xhigh", mapping[WorkerSize.Large].Effort);          // escalated
    }

    [Fact]
    public void Map_NullDefaultEffort_YieldsNullTierEffort()
    {
        // small/medium take DefaultEffort verbatim; a null default -> null tier effort.
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-x", null, System.Array.Empty<string>()),
            new CodexModelInfo("gpt-x-mini", null, System.Array.Empty<string>()),
        });

        var mapping = CodexTierMapper.Map(discovery);

        Assert.NotNull(mapping);
        Assert.Null(mapping![WorkerSize.Small].Effort);
        Assert.Null(mapping[WorkerSize.Medium].Effort);
        // Large has empty supported efforts -> falls back to (null) default -> null.
        Assert.Null(mapping[WorkerSize.Large].Effort);
    }

    [Fact]
    public void Map_EmptyStringEffort_TreatedAsNull()
    {
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-only", "", System.Array.Empty<string>()),
        });

        var mapping = CodexTierMapper.Map(discovery);

        Assert.NotNull(mapping);
        Assert.Null(mapping![WorkerSize.Small].Effort);
    }

    [Fact]
    public void Map_LargeEffortFallsBackToDefaultWhenNoSupportedEfforts()
    {
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-5.5", "high", System.Array.Empty<string>()),
        });

        var mapping = CodexTierMapper.Map(discovery);

        Assert.NotNull(mapping);
        // No supported efforts -> escalation falls back to the default effort.
        Assert.Equal("high", mapping![WorkerSize.Large].Effort);
    }

    [Fact]
    public void Map_AllModelsLookLikeMinis_StillProducesMapping()
    {
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-mini-a", "low", new[] { "low", "high" }),
            new CodexModelInfo("gpt-nano-b", "minimal", new[] { "minimal", "low" }),
        });

        var mapping = CodexTierMapper.Map(discovery);

        Assert.NotNull(mapping);
        // mains was empty -> fell back to the whole list (most-capable-first); small is the
        // first mini, large is the first (most capable) entry.
        Assert.Equal("gpt-mini-a", mapping![WorkerSize.Small].Model);
        Assert.Equal("gpt-mini-a", mapping[WorkerSize.Large].Model);
        Assert.Equal("gpt-nano-b", mapping[WorkerSize.Medium].Model);
        Assert.Equal("high", mapping[WorkerSize.Large].Effort); // highest supported of mini-a [low,high]
    }

    [Fact]
    public void Map_EmptyDiscovery_ReturnsNull()
    {
        var mapping = CodexTierMapper.Map(new CodexModelDiscovery(System.Array.Empty<CodexModelInfo>()));
        Assert.Null(mapping);
    }

    // ------------------------------------------------------------------
    // CodexSizesBlockRenderer
    // ------------------------------------------------------------------

    [Fact]
    public void Render_Commented_ContainsHeaderMenuAndSizeLines()
    {
        var discovery = RepresentativeDiscovery();
        var mapping = CodexTierMapper.Map(discovery)!;

        var block = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        Assert.Contains("[workers.codex.sizes]", block);
        Assert.Contains("# models: gpt-5.5, gpt-5.4-mini", block);
        Assert.Contains("# effort: gpt-5.5 default=medium [minimal,low,medium,high,xhigh]; gpt-5.4-mini default=low [low,medium,high]", block);
        Assert.Contains("# Discovered by 'build init' via 'codex debug models'", block);
        Assert.Contains("small  = { model = \"gpt-5.4-mini\", effort = \"low\" }", block);
        Assert.Contains("medium = { model = \"gpt-5.5\", effort = \"medium\" }", block);
        Assert.Contains("large  = { model = \"gpt-5.5\", effort = \"xhigh\" }", block);
    }

    [Fact]
    public void Render_Active_HasLiveHeaderAndSizeLines()
    {
        var discovery = RepresentativeDiscovery();
        var mapping = CodexTierMapper.Map(discovery)!;

        var block = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        // Active header: no leading "#".
        Assert.Contains("\n[workers.codex.sizes]", "\n" + block);
        Assert.StartsWith("[workers.codex.sizes]", block);
        // Active size lines.
        Assert.Contains("small  = { model = \"gpt-5.4-mini\", effort = \"low\" }", block);
        Assert.Contains("large  = { model = \"gpt-5.5\", effort = \"xhigh\" }", block);
        // Explanatory lines remain real single-"#" comments.
        Assert.Contains("# openai: prefix is optional", block);
        Assert.Contains("# models: gpt-5.5, gpt-5.4-mini", block);
    }

    [Fact]
    public void Render_NullEffort_OmitsEffortKey()
    {
        var discovery = new CodexModelDiscovery(new[]
        {
            new CodexModelInfo("gpt-x", null, System.Array.Empty<string>()),
        });
        var mapping = CodexTierMapper.Map(discovery)!;

        var block = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        Assert.Contains("# small  = { model = \"gpt-x\" }", block);
        Assert.DoesNotContain("effort = ", block);
    }

    // ------------------------------------------------------------------
    // CodexSizesBlockEditor
    // ------------------------------------------------------------------

    [Fact]
    public void TryFind_TemplateCommentedBlock_IsLocated()
    {
        var template = ConfigTemplateLoader.Load();

        Assert.True(CodexSizesBlockEditor.TryFindCodexSizesBlock(template, out int start, out int endExcl));

        var lines = template.Split('\n');
        var headerContent = lines[start].TrimStart('#', ' ');
        Assert.Equal("[workers.codex.sizes]", headerContent.TrimEnd('\r'));
        // Block end is exclusive and the line at end is blank (or EOF).
        Assert.True(endExcl > start);
        Assert.True(endExcl >= lines.Length || string.IsNullOrWhiteSpace(lines[endExcl]));
    }

    [Fact]
    public void Replace_SwapsCodexBlock_LeavesOtherSectionsVerbatim()
    {
        var template = ConfigTemplateLoader.Load();
        var discovery = RepresentativeDiscovery();
        var mapping = CodexTierMapper.Map(discovery)!;
        var newBlock = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        var edited = CodexSizesBlockEditor.ReplaceCodexSizesBlock(template, newBlock);

        // New codex content is present.
        Assert.Contains("# models: gpt-5.5, gpt-5.4-mini", edited);
        Assert.Contains("small  = { model = \"gpt-5.4-mini\", effort = \"low\" }", edited);
        // The old static codex high-effort large line is gone (escalation rewrote it to xhigh).
        Assert.DoesNotContain("# large  = { model = \"gpt-5.5\", effort = \"high\" }", edited);
        // Other sections untouched.
        Assert.Contains("[ticketing]", edited);
        Assert.Contains("[workers.claude-code.sizes]", edited);
        Assert.Contains("small  = { model = \"haiku\" }", edited);
        Assert.Contains("medium = { model = \"sonnet\" }", edited);
        Assert.Contains("large  = { model = \"opus\" }", edited);
        Assert.Contains("[events]", edited);
    }

    [Fact]
    public void Replace_IsIdempotent()
    {
        var template = ConfigTemplateLoader.Load();
        var discovery = RepresentativeDiscovery();
        var mapping = CodexTierMapper.Map(discovery)!;
        var newBlock = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        var once = CodexSizesBlockEditor.ReplaceCodexSizesBlock(template, newBlock);
        var twice = CodexSizesBlockEditor.ReplaceCodexSizesBlock(once, newBlock);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Replace_PreservesTrailingNewlineStyle()
    {
        var template = ConfigTemplateLoader.Load();
        var discovery = RepresentativeDiscovery();
        var mapping = CodexTierMapper.Map(discovery)!;
        var newBlock = CodexSizesBlockRenderer.Render(mapping, discovery, commented: false);

        var edited = CodexSizesBlockEditor.ReplaceCodexSizesBlock(template, newBlock);

        Assert.Equal(template.EndsWith("\n"), edited.EndsWith("\n"));
    }

    [Fact]
    public void Replace_NoCodexBlock_ReturnsInputUnchanged()
    {
        var text = "[ticketing]\nbackend = \"plane\"\n\n[events]\nlog_directory = \".build/events\"\n";

        var edited = CodexSizesBlockEditor.ReplaceCodexSizesBlock(text, "[workers.codex.sizes]\nsmall = { model = \"x\" }");

        Assert.Equal(text, edited);
        Assert.False(CodexSizesBlockEditor.TryFindCodexSizesBlock(text, out _, out _));
    }
}
