using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class DraftBriefBuilderTests
{
    private const string FixtureOperatorText = "Add a widget to the sidebar with a configurable label";

    [Fact]
    public void Build_SubstitutesOperatorText_ContainsLiteralText()
    {
        var result = DraftBriefBuilder.Build("claude-code", FixtureOperatorText);

        Assert.Contains(FixtureOperatorText, result);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("claude-code", "draft.md"));

        Assert.Null(ex);
    }

    [Fact]
    public void Build_DoesNotContainEmDashes()
    {
        var result = DraftBriefBuilder.Build("claude-code", FixtureOperatorText);

        Assert.DoesNotContain("—", result); // em-dash
        Assert.DoesNotContain("–", result); // en-dash
    }

    [Fact]
    public void Build_ContainsWorkerResultEnvelopeAndBodyMarkdownKey()
    {
        var result = DraftBriefBuilder.Build("claude-code", FixtureOperatorText);

        Assert.Contains("WORKER_RESULT", result);
        Assert.Contains("body_markdown", result);
    }

    [Fact]
    public void Build_MatchesSnapshot_Original()
    {
        var expected = SnapshotLoader.Load("draft-brief.txt");

        var result = DraftBriefBuilder.Build("claude-code", FixtureOperatorText);

        Assert.Equal(expected, result);
    }
}
