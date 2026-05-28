using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Unit tests for NewVerbArgumentClassifier.Classify.
/// fileExists is injected so no IO occurs.
/// </summary>
public class NewVerbArgumentClassifierTests
{
    // Helpers
    private static Func<string, bool> NoFiles() => _ => false;
    private static Func<string, bool> FilesExist(params string[] paths) =>
        p => Array.IndexOf(paths, p) >= 0;

    // --- PrintTemplate ---

    [Fact]
    public void PrintTemplate_Flag_ReturnsPrintTemplate()
    {
        var result = NewVerbArgumentClassifier.Classify(new[] { "new", "--print-template" }, NoFiles());
        Assert.Equal(NewVerbKind.PrintTemplate, result.Kind);
    }

    [Fact]
    public void PrintTemplate_Flag_BeforeOtherArgs_ReturnsPrintTemplate()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "--print-template", "some text" }, NoFiles());
        Assert.Equal(NewVerbKind.PrintTemplate, result.Kind);
    }

    // --- HelpExitNonZero (no args) ---

    [Fact]
    public void NoArgs_ReturnsHelpExitNonZero()
    {
        var result = NewVerbArgumentClassifier.Classify(new[] { "new" }, NoFiles());
        Assert.Equal(NewVerbKind.HelpExitNonZero, result.Kind);
    }

    [Fact]
    public void OnlyFlagArgs_NoPositional_ReturnsHelpExitNonZero()
    {
        // --title without a value won't consume extra; only one element after "new".
        // Actually --title with a value: both consumed, no positional left.
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "--title", "My Title" }, NoFiles());
        Assert.Equal(NewVerbKind.HelpExitNonZero, result.Kind);
    }

    // --- StdinDraftMode ---

    [Fact]
    public void SingleDash_ReturnsStdinDraftMode()
    {
        var result = NewVerbArgumentClassifier.Classify(new[] { "new", "-" }, NoFiles());
        Assert.Equal(NewVerbKind.StdinDraftMode, result.Kind);
    }

    // --- FileMode ---

    [Fact]
    public void SingleArg_ExistingFile_ReturnsFileMode()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "/some/path/body.md" },
            FilesExist("/some/path/body.md"));
        Assert.Equal(NewVerbKind.FileMode, result.Kind);
        Assert.Equal("/some/path/body.md", result.FilePath);
    }

    [Fact]
    public void SingleArg_RelativeExistingFile_ReturnsFileMode()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "body.md" },
            FilesExist("body.md"));
        Assert.Equal(NewVerbKind.FileMode, result.Kind);
        Assert.Equal("body.md", result.FilePath);
    }

    // --- DraftMode ---

    [Fact]
    public void SingleArg_PlainText_ReturnsDraftMode_NotLooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "fix the readme typo" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.Equal("fix the readme typo", result.DraftText);
        Assert.False(result.LooksLikePathButMissing);
    }

    [Fact]
    public void SingleArg_NonExistentMdFile_ReturnsDraftMode_LooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "body.md" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.Equal("body.md", result.DraftText);
        Assert.True(result.LooksLikePathButMissing);
    }

    [Fact]
    public void SingleArg_NonExistentTxtFile_ReturnsDraftMode_LooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "notes.txt" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.True(result.LooksLikePathButMissing);
    }

    [Fact]
    public void SingleArg_PathWithForwardSlash_NotFound_LooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "docs/notes" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.True(result.LooksLikePathButMissing);
    }

    [Fact]
    public void SingleArg_PathWithBackslash_NotFound_LooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", @"docs\notes" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.True(result.LooksLikePathButMissing);
    }

    [Fact]
    public void MultiplePositionalArgs_JoinedAsDraftText()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "fix", "the", "readme" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.Equal("fix the readme", result.DraftText);
        Assert.False(result.LooksLikePathButMissing);
    }

    [Fact]
    public void MultiplePositionalArgs_FlagInterspersed_OnlyPositionalJoined()
    {
        // --label value should NOT be included in draft text.
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "fix", "--label", "bug", "the", "readme" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        // positional: "fix", "the", "readme" (--label + "bug" consumed as flag+value)
        Assert.Equal("fix the readme", result.DraftText);
    }

    [Fact]
    public void FlagArgs_WithPositional_PositionalGoesToDraftText()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "--type", "bug", "add metrics endpoint" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.Equal("add metrics endpoint", result.DraftText);
    }

    // --- LooksLikePath case insensitive extension ---

    [Fact]
    public void SingleArg_UppercaseMdExtension_NotFound_LooksLikePath()
    {
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "BODY.MD" }, NoFiles());
        Assert.Equal(NewVerbKind.DraftMode, result.Kind);
        Assert.True(result.LooksLikePathButMissing);
    }

    // --- File takes precedence over draft ---

    [Fact]
    public void SingleArg_ExistingPath_PreferFileModeOverDraft()
    {
        // Even if the name looks like plain text, file existence wins.
        var result = NewVerbArgumentClassifier.Classify(
            new[] { "new", "ticket-body" },
            FilesExist("ticket-body"));
        Assert.Equal(NewVerbKind.FileMode, result.Kind);
    }
}
