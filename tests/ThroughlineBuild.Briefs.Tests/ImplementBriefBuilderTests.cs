using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class ImplementBriefBuilderTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-1",
        Uuid: "test-uuid-1",
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
    public void Build_MinimalTicket_ReturnsImplementBrief()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree);

        Assert.Equal(Phase.Implement, brief.Phase);
        Assert.Equal("TLB-1", brief.TicketId);
        Assert.Empty(brief.AllowedWrites);
        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_Context_ContainsMainShaBranchAndWorktreePath()
    {
        var repo = MinimalRepo() with { MainSha = "deadbeef" };

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), repo, Branch, Worktree);

        Assert.Equal("deadbeef", brief.Context["main_sha"]);
        Assert.Equal(Branch, brief.Context["branch"]);
        Assert.Equal(Worktree, brief.Context["worktree_path"]);
    }

    [Fact]
    public void Build_Instruction_ContainsWorkerResultEnvelopeWithRequiredMetadataKeys()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree);

        Assert.Contains("WORKER_RESULT", brief.Instruction);
        Assert.Contains("commit_sha", brief.Instruction);
        Assert.Contains("files_changed", brief.Instruction);
    }

    [Fact]
    public void Build_Instruction_IncludesTicketDescriptionHtmlVerbatim()
    {
        var ticket = MinimalTicket() with
        {
            DescriptionHtml = "<h3>Goal</h3><p>Do the thing</p><ul><li>Step one</li></ul>"
        };

        var brief = ImplementBriefBuilder.Build("claude-code", ticket, MinimalRepo(), Branch, Worktree);

        Assert.Contains("<h3>Goal</h3>", brief.Instruction);
        Assert.Contains("<p>Do the thing</p>", brief.Instruction);
        Assert.Contains("<li>Step one</li>", brief.Instruction);
    }

    [Fact]
    public void Build_Instruction_ForbidsForcePushRebaseAndWritesOutsideWorktree()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree);

        Assert.Contains("force-push", brief.Instruction);
        Assert.Contains("rebase", brief.Instruction);
        Assert.Contains("outside the worktree", brief.Instruction);
    }

    [Fact]
    public void Build_Instruction_NamesBranchAndWorktreePath()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree);

        Assert.Contains(Branch, brief.Instruction);
        Assert.Contains(Worktree, brief.Instruction);
    }

    [Fact]
    public void Build_TicketWithRelations_InstructionIncludesRelations()
    {
        var ticket = MinimalTicket() with
        {
            Relations = new[]
            {
                new Relation("blocks", "TLB-5"),
                new Relation("blocked_by", "TLB-3")
            }
        };

        var brief = ImplementBriefBuilder.Build("claude-code", ticket, MinimalRepo(), Branch, Worktree);

        Assert.Contains("TLB-5", brief.Instruction);
        Assert.Contains("blocks", brief.Instruction);
        Assert.Contains("TLB-3", brief.Instruction);
        Assert.Contains("blocked_by", brief.Instruction);
    }

    [Fact]
    public void Build_EmptyDescription_BuildsWithoutException()
    {
        var ticket = MinimalTicket() with { DescriptionHtml = "" };

        var exception = Record.Exception(() => ImplementBriefBuilder.Build("claude-code", ticket, MinimalRepo(), Branch, Worktree));

        Assert.Null(exception);
    }

    [Fact]
    public void Build_AllowedWrites_IsAlwaysEmpty()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree);

        Assert.Empty(brief.AllowedWrites);
    }

    [Fact]
    public void Build_TicketIdPropagatedToBrief()
    {
        var ticket = MinimalTicket() with { Id = "TLB-99" };

        var brief = ImplementBriefBuilder.Build("claude-code", ticket, MinimalRepo(), Branch, Worktree);

        Assert.Equal("TLB-99", brief.TicketId);
    }

    [Fact]
    public void Build_InstructionLength_StaysReasonable()
    {
        var ticket = MinimalTicket() with { DescriptionHtml = "<p>Short plan</p>" };

        var brief = ImplementBriefBuilder.Build("claude-code", ticket, MinimalRepo(), Branch, Worktree);

        // Rough sanity cap. The ~1500 token guideline -> ~6000 chars is a soft ceiling
        // assuming the ticket description itself is small.
        Assert.True(brief.Instruction.Length < 6000,
            $"Instruction length {brief.Instruction.Length} exceeded soft cap (~6000 chars) on minimal ticket");
    }

    [Fact]
    public void Build_MatchesSnapshot_Original()
    {
        var expected = SnapshotLoader.Load("implement-original.txt");

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree);

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("claude-code", "implement.md"));

        Assert.Null(ex);
    }

    [Fact]
    public void Build_MatchesSnapshot_Rework()
    {
        var expected = SnapshotLoader.Load("implement-rework.txt");

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            reviewFeedback: SnapshotFixtures.ReworkFeedback());

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_MatchesSnapshot_GateRework()
    {
        var expected = SnapshotLoader.Load("implement-gate-rework.txt");

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            reviewFeedback: SnapshotFixtures.GateFeedback());

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_NoPreloadedSection_InstructionUnchangedFromBaseline()
    {
        // Default (empty) preloaded section must leave the brief byte-identical to the pre-preload
        // baseline - this is the ablation / review-reconstruction case. The original snapshot is that
        // baseline; if the empty placeholder added a stray line this assertion (and the snapshot) fail.
        var expected = SnapshotLoader.Load("implement-original.txt");

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            preloadedContextSection: "");

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_WithPreloadedSection_InjectedBetweenPlanAndWorktree()
    {
        var section = "\n## Pre-loaded context\n\n`src/data/types.ts`\n```\nexport type Survey = {};\n```\n";

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            preloadedContextSection: section);

        Assert.Contains("## Pre-loaded context", brief.Instruction);
        Assert.Contains("export type Survey = {};", brief.Instruction);
        // Ordering: the section lands after the Plan block and before the Worktree block.
        int plan = brief.Instruction.IndexOf("## Plan (from ticket description)");
        int preload = brief.Instruction.IndexOf("## Pre-loaded context");
        int worktree = brief.Instruction.IndexOf("## Worktree and branch");
        Assert.True(plan >= 0 && preload > plan && worktree > preload,
            $"order plan={plan} preload={preload} worktree={worktree}");
    }
}
