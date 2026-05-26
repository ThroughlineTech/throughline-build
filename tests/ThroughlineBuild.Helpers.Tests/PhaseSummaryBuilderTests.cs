using System.Collections.Generic;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

// Unit tests for PhaseSummaryBuilder. Each fact constructs a synthetic event stream
// + primitive inputs and asserts that every PhaseSummary field is populated from
// the documented source (event stream / phase result / git / Plane).
public class PhaseSummaryBuilderTests
{
    [Fact]
    public void BuildPlan_PopulatesAllFieldsFromInputsAndEvents()
    {
        var events = new List<WorkflowEvent>
        {
            MakeEvent(Phase.Plan, EventKind.WorkerSpawn, new() { ["worker"] = "claude-code" }),
            MakeEvent(Phase.Plan, EventKind.TicketWrite, new() { ["action"] = "append_description" }),
            MakeEvent(Phase.Plan, EventKind.TicketWrite, new() { ["action"] = "apply_labels" }),
            MakeEvent(Phase.Plan, EventKind.TicketWrite, new() { ["action"] = "create_comment" }),
            MakeEvent(Phase.Plan, EventKind.LlmCall, new()
            {
                ["wall_ms"] = (long)1234,
                ["output_tokens"] = (long)42,
                ["cache_read_tokens"] = (long)10,
            }),
            MakeEvent(Phase.Plan, EventKind.StateTransition, new()
            {
                ["from"] = "Backlog",
                ["to"] = "Ready",
            }),
        };

        var summary = PhaseSummaryBuilder.BuildPlan(
            ticketId: "TLB-1",
            success: true,
            riskLabel: "low",
            sizeLabel: "M",
            plannedAtSha: "deadbeef",
            failureReason: null,
            ticket: null,
            events: events,
            sessionId: "sess1",
            planeUrl: "https://plane.example/browse/TLB-1/",
            sessionArtifactsPath: ".build/events/sess1.jsonl");

        Assert.True(summary.Success);
        Assert.Equal("Plan", summary.PhaseKind);
        Assert.Equal("TLB-1", summary.TicketId);
        Assert.Equal("M", summary.SizeLabel);
        Assert.Equal("low", summary.RiskLabel);
        Assert.Equal("Backlog", summary.StateFrom);
        Assert.Equal("Ready", summary.StateTo);
        Assert.Equal("deadbeef", summary.InvestigatedAtSha);
        Assert.Equal(new[] { "append_description", "apply_labels", "create_comment" }, summary.PlaneOps);
        Assert.Equal(1234, summary.WorkerWallMs);
        Assert.Equal(42, summary.OutputTokens);
        Assert.Equal(10, summary.CacheReadTokens);
        Assert.Equal("https://plane.example/browse/TLB-1/", summary.PlaneUrl);
    }

    [Fact]
    public void BuildPlan_Failure_PopulatesFailureFields()
    {
        var summary = PhaseSummaryBuilder.BuildPlan(
            ticketId: "TLB-2",
            success: false,
            riskLabel: null,
            sizeLabel: null,
            plannedAtSha: null,
            failureReason: "worker timed out",
            ticket: null,
            events: new List<WorkflowEvent>(),
            sessionId: "sess2",
            planeUrl: null,
            sessionArtifactsPath: ".build/events/sess2.jsonl");

        Assert.False(summary.Success);
        Assert.Equal("worker timed out", summary.FailureReason);
        Assert.Equal("worker", summary.FailedAt);
        Assert.Equal(".build/events/sess2.jsonl", summary.SessionArtifactsPath);
    }

    [Fact]
    public void BuildImplement_PopulatesCommitsAndDiffCounts()
    {
        var events = new List<WorkflowEvent>
        {
            MakeEvent(Phase.Implement, EventKind.StateTransition, new()
            {
                ["from"] = "Ready",
                ["to"] = "InProgress",
            }),
            MakeEvent(Phase.Implement, EventKind.LlmCall, new()
            {
                ["wall_ms"] = (long)5000,
                ["output_tokens"] = (long)200,
            }),
            MakeEvent(Phase.Implement, EventKind.StateTransition, new()
            {
                ["from"] = "InProgress",
                ["to"] = "InReview",
            }),
        };

        var diff = new List<DiffEntry>
        {
            new("src/Foo.cs", DiffKind.Added, null, 10, 0, null),
            new("src/Bar.cs", DiffKind.Modified, null, 4, 2, null),
            new("tests/FooTests.cs", DiffKind.Added, null, 30, 0, null),
        };

        var summary = PhaseSummaryBuilder.BuildImplement(
            ticketId: "TLB-3",
            success: true,
            branchName: "ticket/tlb-3-feature",
            commitSha: "abc123",
            failureReason: null,
            events: events,
            diff: diff,
            commitOnelines: new[] { "abc123 first commit", "def456 second commit" },
            commitCount: 2,
            planeUrl: "https://plane.example/browse/TLB-3/",
            sessionArtifactsPath: ".build/events/sess3.jsonl");

        Assert.True(summary.Success);
        Assert.Equal("Implement", summary.PhaseKind);
        Assert.Equal("ticket/tlb-3-feature", summary.BranchName);
        Assert.Equal(2, summary.CommitCount);
        Assert.Equal(new[] { "abc123", "def456" }, summary.CommitShas);
        Assert.Equal(3, summary.FilesChanged);
        Assert.Equal(2, summary.FilesAdded);
        Assert.Equal(1, summary.FilesEdited);
        Assert.Equal(1, summary.TestsAddedCount);
        Assert.Equal("InProgress", summary.StateFrom);
        Assert.Equal("InReview", summary.StateTo);
        Assert.Equal(5000, summary.WorkerWallMs);
        Assert.Equal(200, summary.OutputTokens);
    }

    [Fact]
    public void BuildReview_PopulatesVerdictAndChecks()
    {
        var events = new List<WorkflowEvent>
        {
            MakeEvent(Phase.Review, EventKind.LlmCall, new()
            {
                ["wall_ms"] = (long)900,
            }),
        };

        var summary = PhaseSummaryBuilder.BuildReview(
            ticketId: "TLB-4",
            success: true,
            verdict: "Pass",
            rationale: "all checks ok",
            checksFailed: Array.Empty<string>(),
            failureReason: null,
            events: events,
            planeUrl: null,
            sessionArtifactsPath: null);

        Assert.True(summary.Success);
        Assert.Equal("Pass", summary.Verdict);
        Assert.Equal("all checks ok", summary.VerdictRationale);
        Assert.Empty(summary.ChecksFailed);
        Assert.Equal(900, summary.WorkerWallMs);
    }

    [Fact]
    public void BuildShip_DetectsArchivedFromDecruftEvent()
    {
        var events = new List<WorkflowEvent>
        {
            MakeEvent(Phase.Ship, EventKind.StateTransition, new()
            {
                ["from"] = "InReview",
                ["to"] = "Done",
            }),
            MakeEvent(Phase.Ship, EventKind.TicketWrite, new()
            {
                ["action"] = "decruft",
                ["halted_at"] = "complete",
            }),
            MakeEvent(Phase.Ship, EventKind.TicketWrite, new()
            {
                ["action"] = "delete_branch",
                ["success"] = true,
            }),
        };

        var diff = new List<DiffEntry>
        {
            new("src/Foo.cs", DiffKind.Modified, null, 5, 5, null),
            new("src/Bar.cs", DiffKind.Added, null, 12, 0, null),
        };

        var summary = PhaseSummaryBuilder.BuildShip(
            ticketId: "TLB-5",
            success: true,
            branchName: "ticket/tlb-5-x",
            mergedSha: "feedface",
            failureReason: null,
            failedAtStage: null,
            events: events,
            diff: diff,
            planeUrl: "https://plane.example/browse/TLB-5/",
            sessionArtifactsPath: null);

        Assert.True(summary.Success);
        Assert.Equal("feedface", summary.MergedSha);
        Assert.Equal(2, summary.FilesChanged);
        Assert.Equal("InReview", summary.StateFrom);
        Assert.Equal("Done", summary.StateTo);
        Assert.True(summary.Archived);
    }

    [Fact]
    public void BuildShip_FailurePath_NotArchived()
    {
        var summary = PhaseSummaryBuilder.BuildShip(
            ticketId: "TLB-6",
            success: false,
            branchName: "ticket/tlb-6-x",
            mergedSha: null,
            failureReason: "rebase conflicts",
            failedAtStage: "Rebase",
            events: new List<WorkflowEvent>(),
            diff: new List<DiffEntry>(),
            planeUrl: null,
            sessionArtifactsPath: null);

        Assert.False(summary.Success);
        Assert.Equal("Rebase", summary.FailedAt);
        Assert.False(summary.Archived);
    }

    private static WorkflowEvent MakeEvent(Phase phase, EventKind kind, Dictionary<string, object> data) =>
        new("sess", DateTimeOffset.UtcNow, kind, "TLB-X", phase, data);
}
