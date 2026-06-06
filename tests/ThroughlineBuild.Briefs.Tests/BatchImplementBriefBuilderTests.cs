using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class BatchImplementBriefBuilderTests
{
    [Fact]
    public void Build_MultiTicketGroup_ReturnsSingleImplementBrief()
    {
        var brief = BuildSnapshotBrief();

        Assert.Equal(Phase.Implement, brief.Phase);
        Assert.Equal("TLB-201, TLB-202", brief.TicketId);
        Assert.Empty(brief.AllowedWrites);
    }

    [Fact]
    public void Build_MultiTicketGroup_ListsEveryTicketInDeclaredOrder()
    {
        var brief = BuildSnapshotBrief();

        var firstIndex = brief.Instruction.IndexOf("### 1. TLB-201 - First batch ticket", StringComparison.Ordinal);
        var secondIndex = brief.Instruction.IndexOf("### 2. TLB-202 - Second batch ticket", StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, "First ticket section should be present.");
        Assert.True(secondIndex >= 0, "Second ticket section should be present.");
        Assert.True(firstIndex < secondIndex, "Ticket sections should follow declared order.");
    }

    [Fact]
    public void Build_MultiTicketGroup_IncludesDescriptionsVerbatim()
    {
        var brief = BuildSnapshotBrief();

        Assert.Contains("<p>Build the first feature.</p>", brief.Instruction);
        Assert.Contains("<li>Use the first ticket's output.</li>", brief.Instruction);
    }

    [Fact]
    public void Build_MultiTicketGroup_IncludesOrderingConstraints()
    {
        var brief = BuildSnapshotBrief();

        Assert.Contains("First in this batch. Start from the base commit pointer.", brief.Instruction);
        Assert.Contains("Must be implemented after ticket TLB-201 and on top of that ticket's commit.", brief.Instruction);
    }

    [Fact]
    public void Build_MultiTicketGroup_IncludesBaseCommitPointer()
    {
        var brief = BuildSnapshotBrief();

        Assert.Equal("chain-current-def", brief.Context["base_commit_sha"]);
        Assert.Contains("Current base commit pointer for this group: chain-current-def", brief.Instruction);
        Assert.Contains("prior chain range: chain-start-abc..chain-current-def (2 commit(s))", brief.Instruction);
    }

    [Fact]
    public void Build_MultiTicketGroup_IncludesPerTicketOutputContract()
    {
        var brief = BuildSnapshotBrief();

        Assert.Contains("Make exactly one local commit per ticket", brief.Instruction);
        Assert.Contains("\"tickets\":[", brief.Instruction);
        Assert.Contains("\"ticket_id\":\"TLB-201\"", brief.Instruction);
        Assert.Contains("\"ticket_id\":\"TLB-202\"", brief.Instruction);
        Assert.Contains("\"commit_sha\"", brief.Instruction);
        Assert.Contains("\"stack_position\":1", brief.Instruction);
        Assert.Contains("\"stack_position\":2", brief.Instruction);
        Assert.Contains("\"files_changed\"", brief.Instruction);
        Assert.Contains("\"summary_ref\":\"IMPLEMENT_SUMMARY_1\"", brief.Instruction);
    }

    [Fact]
    public void Build_WithNonEmptyChainCommitRange_RelevantFilesContainsTouchedFiles()
    {
        var brief = BuildSnapshotBrief();

        Assert.Equal(new[] { "src/Prior.cs", "tests/PriorTests.cs" }, brief.RelevantFiles);
    }

    [Fact]
    public void Build_WithNullChainCommitRange_UsesRepoMainShaAsBasePointer()
    {
        var brief = BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            chainCommitRange: null);

        Assert.Equal("abc1234567890def", brief.Context["base_commit_sha"]);
        Assert.Contains("current base commit pointer: abc1234567890def", brief.Context["chain_pointer"]);
        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_EmptyTicketGroup_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            BatchImplementBriefBuilder.Build(
                "claude-code",
                Array.Empty<Ticket>(),
                SnapshotFixtures.Repo(),
                SnapshotFixtures.FixtureBranch,
                SnapshotFixtures.FixtureWorktree,
                chainCommitRange: null));

        Assert.Contains("at least one ticket", exception.Message);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("claude-code", "batch-implement.md"));

        Assert.Null(ex);
    }

    [Fact]
    public void Build_MatchesSnapshot_Batch()
    {
        var expected = SnapshotLoader.Load("batch-implement.txt");

        var brief = BuildSnapshotBrief();

        Assert.Equal(expected, brief.Instruction);
    }

    // --- Rework feedback section (TLB-453) ---

    [Fact]
    public void Build_WithReworkFeedback_IncludesReworkSectionInInstruction()
    {
        var feedback = new ReviewFeedback(
            Rationale: "TLB-201 is missing error handling for null inputs.",
            ChecksFailed: new[] { "unit-tests" },
            ReworkRoundNumber: 1);

        var brief = BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            SnapshotFixtures.ChainCommitRange(),
            reworkFeedback: feedback);

        Assert.Contains("## Rework feedback (round 1)", brief.Instruction);
        Assert.Contains("TLB-201 is missing error handling for null inputs.", brief.Instruction);
        Assert.Contains("Do NOT amend or rewrite commits", brief.Instruction);
        Assert.Contains("- unit-tests", brief.Instruction);
    }

    [Fact]
    public void Build_WithReworkFeedback_RoundNumberReflectedInHeader()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Fix the interface.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 2);

        var brief = BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            SnapshotFixtures.ChainCommitRange(),
            reworkFeedback: feedback);

        Assert.Contains("## Rework feedback (round 2)", brief.Instruction);
    }

    [Fact]
    public void Build_WithReworkFeedback_NoChecks_OmitsChecksList()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Add missing guard.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 1);

        var brief = BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            SnapshotFixtures.ChainCommitRange(),
            reworkFeedback: feedback);

        Assert.Contains("## Rework feedback (round 1)", brief.Instruction);
        Assert.DoesNotContain("Checks that failed:", brief.Instruction);
    }

    [Fact]
    public void Build_WithoutReworkFeedback_OmitsReworkSection()
    {
        var brief = BuildSnapshotBrief();

        Assert.DoesNotContain("## Rework feedback", brief.Instruction);
        Assert.DoesNotContain("Do NOT amend or rewrite commits", brief.Instruction);
    }

    [Fact]
    public void Build_WithReworkFeedback_ReworkSectionAppearsBeforeConstraints()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Fix the bug.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 1);

        var brief = BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            SnapshotFixtures.ChainCommitRange(),
            reworkFeedback: feedback);

        var reworkIdx = brief.Instruction.IndexOf("## Rework feedback", StringComparison.Ordinal);
        var constraintsIdx = brief.Instruction.IndexOf("## Constraints", StringComparison.Ordinal);
        Assert.True(reworkIdx >= 0 && constraintsIdx >= 0 && reworkIdx < constraintsIdx,
            "Rework section should appear before Constraints section.");
    }

    private static Brief BuildSnapshotBrief() =>
        BatchImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.BatchTickets(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            SnapshotFixtures.ChainCommitRange());
}
