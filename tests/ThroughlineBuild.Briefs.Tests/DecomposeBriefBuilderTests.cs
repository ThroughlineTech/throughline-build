using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class DecomposeBriefBuilderTests
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
    public void Build_MinimalTicket_ReturnsBriefWithDecomposePhase()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Equal(Phase.Decompose, brief.Phase);
    }

    [Fact]
    public void Build_MinimalTicket_InstructionContainsWorkerResultEnvelope()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Contains("WORKER_RESULT", brief.Instruction);
    }

    [Fact]
    public void Build_MinimalTicket_InstructionContainsChildSpecsKey()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Contains("child_specs", brief.Instruction);
    }

    [Fact]
    public void Build_AllowedWrites_IsAlwaysEmpty()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Empty(brief.AllowedWrites);
    }

    [Fact]
    public void Build_TicketId_PropagatedToBrief()
    {
        var ticket = MinimalTicket() with { Id = "TLB-99" };
        var repo = MinimalRepo();

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Equal("TLB-99", brief.TicketId);
    }

    [Fact]
    public void Build_ContextMainSha_MatchesRepoState()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo() with { MainSha = "deadbeef" };

        var brief = DecomposeBriefBuilder.Build("claude-code", ticket, repo);

        Assert.Equal("deadbeef", brief.Context["main_sha"]);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("claude-code", "decompose.md"));

        Assert.Null(ex);
    }
}
