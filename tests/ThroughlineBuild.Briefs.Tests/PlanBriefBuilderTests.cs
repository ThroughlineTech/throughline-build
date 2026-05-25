using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class PlanBriefBuilderTests
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

    [Fact]
    public void Build_MinimalTicket_ReturnsPlanBrief()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Equal(Phase.Plan, brief.Phase);
        Assert.Empty(brief.AllowedWrites);
        Assert.True(brief.Context.ContainsKey("main_sha"));
    }

    [Fact]
    public void Build_MinimalTicket_InstructionContainsWorkerResultEnvelope()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Contains("WORKER_RESULT", brief.Instruction);
    }

    [Fact]
    public void Build_MinimalTicket_InstructionContainsRequiredMetadataKeys()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Contains("plan_html", brief.Instruction);
        Assert.Contains("risk_label", brief.Instruction);
        Assert.Contains("size_label", brief.Instruction);
        Assert.Contains("planned_at_sha", brief.Instruction);
    }

    [Fact]
    public void Build_EmptyDescription_BuildsWithoutException()
    {
        var ticket = MinimalTicket() with { DescriptionHtml = "" };
        var repo = MinimalRepo();

        var exception = Record.Exception(() => PlanBriefBuilder.Build(ticket, repo));

        Assert.Null(exception);
    }

    [Fact]
    public void Build_EmptyTopLevelEntries_BuildsWithoutException()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo() with { TopLevelEntries = Array.Empty<string>() };

        var exception = Record.Exception(() => PlanBriefBuilder.Build(ticket, repo));

        Assert.Null(exception);
    }

    [Fact]
    public void Build_AllowedWrites_IsAlwaysEmpty()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Empty(brief.AllowedWrites);
    }

    [Fact]
    public void Build_ContextMainSha_MatchesRepoState()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo() with { MainSha = "deadbeef" };

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Equal("deadbeef", brief.Context["main_sha"]);
    }

    [Fact]
    public void Build_TicketId_PropagatedToBrief()
    {
        var ticket = MinimalTicket() with { Id = "TLB-99" };
        var repo = MinimalRepo();

        var brief = PlanBriefBuilder.Build(ticket, repo);

        Assert.Equal("TLB-99", brief.TicketId);
    }

    [Fact]
    public void Build_MatchesSnapshot_Enriched()
    {
        var expected = SnapshotLoader.Load("plan-enriched.txt");

        var brief = PlanBriefBuilder.Build(SnapshotFixtures.Ticket(), SnapshotFixtures.Repo());

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("plan.md"));

        Assert.Null(ex);
    }
}
