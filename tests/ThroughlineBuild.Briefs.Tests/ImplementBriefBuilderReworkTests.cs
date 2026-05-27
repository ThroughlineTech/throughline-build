using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class ImplementBriefBuilderReworkTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-1",
        Title: "Test ticket",
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

    private const string Branch = "ticket/tlb-1-test-ticket";
    private const string Worktree = "/repo/.worktrees/ticket-tlb-1-test-ticket";

    [Fact]
    public void Build_NullReviewFeedback_ReviewFeedbackSectionInContextIsEmptyString()
    {
        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: null);

        Assert.True(brief.Context.ContainsKey("review_feedback_section"));
        Assert.Equal("", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_NullReviewFeedback_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: null));

        Assert.Null(ex);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsRationale()
    {
        var feedback = new ReviewFeedback(
            Rationale: "The implementation missed edge cases.",
            ChecksFailed: new[] { "tests_pass" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("The implementation missed edge cases.", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsAllChecksFailed()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Multiple checks failed.",
            ChecksFailed: new[] { "tests_pass", "coverage_ok", "no_warnings" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("tests_pass", section);
        Assert.Contains("coverage_ok", section);
        Assert.Contains("no_warnings", section);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsRoundNumber()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Some feedback.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 2);

        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("2", brief.Context["review_feedback_section"]);
        Assert.Contains("Rework round 2", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_SectionHeadingMatchesExpectedFormat()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Rationale text.",
            ChecksFailed: new[] { "check_one" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.StartsWith("## Rework round 1 - reviewer feedback", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_EmptyChecksFailed_ShowsNone()
    {
        var feedback = new ReviewFeedback(
            Rationale: "General concerns only.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build(MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("(none)", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_SnapshotOriginal_StillPassesWithNullFeedback()
    {
        var expected = SnapshotLoader.Load("implement-original.txt");

        var brief = ImplementBriefBuilder.Build(
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            reviewFeedback: null);

        Assert.Equal(expected, brief.Instruction);
    }
}
