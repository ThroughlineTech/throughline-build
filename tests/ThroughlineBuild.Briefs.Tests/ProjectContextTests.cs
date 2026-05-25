using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class ProjectContextTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-1",
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan body</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static RepoState MinimalRepo() => new RepoState(
        MainSha: "abc1234",
        TopLevelEntries: Array.Empty<string>());

    private const string Branch = "ticket/tlb-1-test-ticket";
    private const string Worktree = "/repo/.worktrees/ticket-tlb-1-test-ticket";

    [Fact]
    public void PlanBriefBuilder_Build_DefaultProject_InstructionUnchangedFromNoProject()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var withoutProject = PlanBriefBuilder.Build(ticket, repo);
        var withEmpty = PlanBriefBuilder.Build(ticket, repo, ProjectContext.Empty);
        var withNull = PlanBriefBuilder.Build(ticket, repo, null);

        Assert.Equal(withoutProject.Instruction, withEmpty.Instruction);
        Assert.Equal(withoutProject.Instruction, withNull.Instruction);
        Assert.Equal(string.Empty, withEmpty.Context["project_language"]);
        Assert.Equal(string.Empty, withEmpty.Context["project_notes"]);
    }

    [Fact]
    public void ImplementBriefBuilder_Build_DefaultProject_InstructionUnchangedFromNoProject()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();

        var withoutProject = ImplementBriefBuilder.Build(ticket, repo, Branch, Worktree);
        var withEmpty = ImplementBriefBuilder.Build(ticket, repo, Branch, Worktree, ProjectContext.Empty);
        var withNull = ImplementBriefBuilder.Build(ticket, repo, Branch, Worktree, null);

        Assert.Equal(withoutProject.Instruction, withEmpty.Instruction);
        Assert.Equal(withoutProject.Instruction, withNull.Instruction);
        Assert.Equal(string.Empty, withEmpty.Context["project_language"]);
        Assert.Equal(string.Empty, withEmpty.Context["project_notes"]);
    }

    [Fact]
    public void ReviewBriefBuilder_Build_DefaultProject_InstructionUnchangedFromNoProject()
    {
        var ticket = MinimalTicket();
        var diff = new GitDiff(
            FromRef: "origin/main",
            ToRef: "feature/test",
            Entries: Array.Empty<DiffEntry>());
        var implementerResult = new WorkerResult(
            Status: Status.Ok,
            Summary: "ok",
            FilesChanged: Array.Empty<string>(),
            FailureReason: null,
            Metadata: new Dictionary<string, object>());
        var checks = Array.Empty<CheckResult>();

        var withoutProject = ReviewBriefBuilder.Build(ticket, diff, implementerResult, checks);
        var withEmpty = ReviewBriefBuilder.Build(ticket, diff, implementerResult, checks, ProjectContext.Empty);
        var withNull = ReviewBriefBuilder.Build(ticket, diff, implementerResult, checks, null);

        Assert.Equal(withoutProject.Instruction, withEmpty.Instruction);
        Assert.Equal(withoutProject.Instruction, withNull.Instruction);
        Assert.Equal(string.Empty, withEmpty.Context["project_language"]);
        Assert.Equal(string.Empty, withEmpty.Context["project_notes"]);
    }

    [Fact]
    public void PlanBriefBuilder_Build_PopulatedProject_AddsAllProjectKeysToContext()
    {
        var ticket = MinimalTicket();
        var repo = MinimalRepo();
        var proj = new ProjectContext(
            Language: "typescript",
            Framework: "react-vite",
            PackageManager: "npm",
            BuildCommand: "npm run build",
            TestCommand: "npm test",
            InstallCommand: "npm install",
            DevCommand: "npm run dev",
            PlaneProjectUrl: "https://plane.example/",
            Notes: "## extra notes");

        var brief = PlanBriefBuilder.Build(ticket, repo, proj);

        Assert.Equal("typescript", brief.Context["project_language"]);
        Assert.Equal("react-vite", brief.Context["project_framework"]);
        Assert.Equal("npm", brief.Context["project_package_manager"]);
        Assert.Equal("npm run build", brief.Context["project_build_command"]);
        Assert.Equal("npm test", brief.Context["project_test_command"]);
        Assert.Equal("npm install", brief.Context["project_install_command"]);
        Assert.Equal("npm run dev", brief.Context["project_dev_command"]);
        Assert.Equal("https://plane.example/", brief.Context["project_plane_project_url"]);
        Assert.Equal("## extra notes", brief.Context["project_notes"]);
    }
}
