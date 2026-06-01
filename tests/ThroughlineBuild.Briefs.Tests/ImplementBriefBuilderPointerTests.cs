using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

/// <summary>
/// Pins the chain-handoff pointer injection added by Brief 09 (Plan C):
///   (1) A non-empty ChainCommitRange folds touched-files into RelevantFiles and a
///       pointer line into Context["chain_pointer"].
///   (2) An empty ChainCommitRange (first ticket) leaves the brief unchanged with no
///       empty headers or stray context keys.
///   (3) A file touched by several prior tickets appears exactly once (deduplication).
/// </summary>
public class ImplementBriefBuilderPointerTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-2",
        Uuid: "test-uuid-2",
        Title: "Second ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static RepoState MinimalRepo() => new RepoState(
        MainSha: "abc1234",
        TopLevelEntries: Array.Empty<string>());

    private const string Branch = "ticket/tlb-2-second-ticket";
    private const string Worktree = "/repo/.worktrees/ticket-tlb-2-second-ticket";

    private const string StartSha = "1111111111111111111111111111111111111111";
    private const string EndSha   = "2222222222222222222222222222222222222222";

    // -----------------------------------------------------------------------
    // Acceptance: non-empty range - touched-files in RelevantFiles and pointer in Context
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_WithNonEmptyChainCommitRange_RelevantFilesContainsTouchedFiles()
    {
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/Feature.cs", "tests/FeatureTests.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        Assert.Equal(new[] { "src/Feature.cs", "tests/FeatureTests.cs" }, brief.RelevantFiles);
    }

    [Fact]
    public void Build_WithNonEmptyChainCommitRange_ContextContainsChainPointer()
    {
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/Feature.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        Assert.True(brief.Context.ContainsKey("chain_pointer"),
            "Context should contain 'chain_pointer' when range is non-empty");
        var pointer = brief.Context["chain_pointer"];
        Assert.Contains(StartSha, pointer);
        Assert.Contains(EndSha, pointer);
        Assert.Contains("1 commit", pointer);
    }

    [Fact]
    public void Build_WithNonEmptyChainCommitRange_PointerContainsBothShas()
    {
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 3,
            TouchedFiles: new[] { "src/A.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        var pointer = brief.Context["chain_pointer"];
        // Pointer must contain both anchoring SHAs and the commit count.
        Assert.Contains(StartSha, pointer);
        Assert.Contains(EndSha, pointer);
        Assert.Contains("3 commit", pointer);
    }

    // -----------------------------------------------------------------------
    // Acceptance: empty range - brief unchanged, no empty headers or stray keys
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_WithNullChainCommitRange_RelevantFilesIsEmpty()
    {
        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: null);

        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_WithNullChainCommitRange_ContextDoesNotContainChainPointer()
    {
        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: null);

        Assert.False(brief.Context.ContainsKey("chain_pointer"),
            "Context should NOT contain 'chain_pointer' when range is null");
    }

    [Fact]
    public void Build_WithEmptyChainCommitRange_RelevantFilesIsEmpty()
    {
        // An empty range (CommitCount=0, IsEmpty=true) is what the chain produces
        // for the very first ticket before any sibling has shipped.
        var emptyRange = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: StartSha,     // same SHA -> IsEmpty
            CommitCount: 0,
            TouchedFiles: Array.Empty<string>());

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: emptyRange);

        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_WithEmptyChainCommitRange_ContextDoesNotContainChainPointer()
    {
        var emptyRange = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: StartSha,
            CommitCount: 0,
            TouchedFiles: Array.Empty<string>());

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: emptyRange);

        Assert.False(brief.Context.ContainsKey("chain_pointer"),
            "Context should NOT contain 'chain_pointer' when range is empty (first ticket)");
    }

    [Fact]
    public void Build_WithEmptyChainCommitRange_BriefInstructionIdenticalToNullRange()
    {
        // Empty range must produce a brief instruction byte-identical to no-pointer baseline.
        var emptyRange = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: StartSha,
            CommitCount: 0,
            TouchedFiles: Array.Empty<string>());

        var briefWithEmpty = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: emptyRange);

        var briefWithNull = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: null);

        Assert.Equal(briefWithNull.Instruction, briefWithEmpty.Instruction);
    }

    // -----------------------------------------------------------------------
    // Acceptance: files touched by several prior tickets appear exactly once
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_DuplicateTouchedFiles_RelevantFilesDeduped()
    {
        // Even if ChainCommitRangeHelper already dedupes, the builder must also
        // handle duplicates defensively in case the range is constructed directly
        // in tests or by callers that skip the helper.
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 2,
            TouchedFiles: new[] { "src/Shared.cs", "src/Shared.cs", "tests/SharedTests.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        Assert.Equal(new[] { "src/Shared.cs", "tests/SharedTests.cs" }, brief.RelevantFiles);
    }

    [Fact]
    public void Build_MultipleDistinctTouchedFiles_AllPresentInRelevantFiles()
    {
        var touched = new[] { "src/A.cs", "src/B.cs", "tests/ATests.cs", "tests/BTests.cs" };
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 2,
            TouchedFiles: touched);

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        Assert.Equal(4, brief.RelevantFiles.Count);
        foreach (var file in touched)
            Assert.Contains(file, brief.RelevantFiles);
    }

    // -----------------------------------------------------------------------
    // Acceptance: existing Context fields are preserved when pointer is injected
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_WithNonEmptyRange_ExistingContextFieldsUnchanged()
    {
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/Feature.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree,
            chainCommitRange: range);

        // All baseline Context keys must still be present.
        Assert.Equal("abc1234", brief.Context["main_sha"]);
        Assert.Equal(Branch, brief.Context["branch"]);
        Assert.Equal(Worktree, brief.Context["worktree_path"]);
    }
}
