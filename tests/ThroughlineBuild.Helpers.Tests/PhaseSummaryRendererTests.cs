using System.Text.Json;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

// Smoke tests for the human-readable + JSON renderers. These verify the public CLI
// contract: each phase produces a header line and the documented field labels, and
// --summary-json emits parseable JSON with the documented keys.
public class PhaseSummaryRendererTests
{
    [Fact]
    public void RenderText_Plan_ContainsHeaderAndDocumentedFields()
    {
        var summary = new PlanPhaseSummary
        {
            PhaseKind = "Plan",
            TicketId = "TLB-1",
            Success = true,
            SizeLabel = "M",
            RiskLabel = "low",
            StateFrom = "Backlog",
            StateTo = "Ready",
            PlaneOps = new[] { "append_description", "apply_labels" },
            InvestigatedAtSha = "deadbeef",
            PlaneUrl = "https://plane.example/browse/TLB-1/",
            WorkerWallMs = 1234,
            OutputTokens = 42,
            CacheReadTokens = 10,
        };

        var text = PhaseSummaryRenderer.RenderText(summary);

        Assert.Contains("TLB-1 Plan Complete", text);
        Assert.Contains("Labels: size:M, risk:low", text);
        Assert.Contains("State: Backlog -> Ready", text);
        Assert.Contains("Plane ops: append_description, apply_labels", text);
        Assert.Contains("investigated_at_sha: deadbeef", text);
        Assert.Contains("Worker:", text);
        Assert.Contains("Plane:", text);
        Assert.Contains("Next: build implement TLB-1", text);
    }

    [Fact]
    public void RenderText_Implement_ContainsHeaderAndCommits()
    {
        var summary = new ImplementPhaseSummary
        {
            PhaseKind = "Implement",
            TicketId = "TLB-2",
            Success = true,
            BranchName = "ticket/tlb-2-x",
            CommitSha = "abc123",
            CommitCount = 2,
            CommitShas = new[] { "abc123", "def456" },
            FilesChanged = 3,
            FilesAdded = 2,
            FilesEdited = 1,
            TestsAddedCount = 1,
            StateFrom = "InProgress",
            StateTo = "InReview",
        };

        var text = PhaseSummaryRenderer.RenderText(summary);
        Assert.Contains("TLB-2 Implementation Complete", text);
        Assert.Contains("Branch: ticket/tlb-2-x", text);
        Assert.Contains("Commits: 2 (abc123, def456)", text);
        Assert.Contains("Files Changed: 3 (2 new + 1 edit)", text);
        Assert.Contains("Tests Added: 1", text);
        Assert.Contains("Next: build review TLB-2", text);
    }

    [Fact]
    public void RenderText_Review_ContainsVerdictAndShipNextOnPass()
    {
        var summary = new ReviewPhaseSummary
        {
            PhaseKind = "Review",
            TicketId = "TLB-3",
            Success = true,
            Verdict = "Pass",
            VerdictRationale = "ok",
            ChecksFailed = Array.Empty<string>(),
            StateFrom = "InReview",
            StateTo = "InReview",
        };

        var text = PhaseSummaryRenderer.RenderText(summary);
        Assert.Contains("TLB-3 Review Complete", text);
        Assert.Contains("Next: build ship TLB-3", text);
        Assert.DoesNotContain("Verifier:", text);
    }

    [Fact]
    public void RenderText_Review_OmitsShipNextOnFail()
    {
        var summary = new ReviewPhaseSummary
        {
            PhaseKind = "Review",
            TicketId = "TLB-4",
            Success = true,
            Verdict = "Rework",
            VerdictRationale = "missing tests",
            ChecksFailed = new[] { "build" },
        };

        var text = PhaseSummaryRenderer.RenderText(summary);
        Assert.Contains("Verifier: Rework", text);
        Assert.Contains("Scope Violations: build", text);
        Assert.DoesNotContain("Next: build ship", text);
    }

    [Fact]
    public void RenderText_Ship_ContainsMergedShaAndArchived()
    {
        var summary = new ShipPhaseSummary
        {
            PhaseKind = "Ship",
            TicketId = "TLB-5",
            Success = true,
            BranchName = "ticket/tlb-5-x",
            MergedSha = "feedface",
            FilesChanged = 7,
            StateFrom = "InReview",
            StateTo = "Done",
            Archived = true,
        };

        var text = PhaseSummaryRenderer.RenderText(summary);
        Assert.Contains("TLB-5 Shipped", text);
        Assert.Contains("Merged: ticket/tlb-5-x -> main (fast-forward, feedface)", text);
        Assert.Contains("Files: 7 changed", text);
        Assert.Contains("State: InReview -> Done", text);
        Assert.Contains("Archived: yes", text);
    }

    [Fact]
    public void RenderText_FailurePath_ContainsArtifactsPath()
    {
        var summary = new PlanPhaseSummary
        {
            PhaseKind = "Plan",
            TicketId = "TLB-9",
            Success = false,
            FailureReason = "worker timed out",
            FailedAt = "worker",
            SessionArtifactsPath = ".build/events/sess9.jsonl",
            PlaneUrl = "https://plane.example/browse/TLB-9/",
        };

        var text = PhaseSummaryRenderer.RenderText(summary);
        Assert.Contains("TLB-9 Plan Failed", text);
        Assert.Contains("Failed at: worker", text);
        Assert.Contains("Reason: worker timed out", text);
        Assert.Contains("Artifacts: .build/events/sess9.jsonl", text);
    }

    [Fact]
    public void RenderJson_ProducesParseableJsonWithExpectedKeys()
    {
        var summary = new PlanPhaseSummary
        {
            PhaseKind = "Plan",
            TicketId = "TLB-1",
            Success = true,
            SizeLabel = "M",
            RiskLabel = "low",
            StateFrom = "Backlog",
            StateTo = "Ready",
            InvestigatedAtSha = "deadbeef",
        };

        var json = PhaseSummaryRenderer.RenderJson(summary);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Plan", root.GetProperty("PhaseKind").GetString());
        Assert.Equal("TLB-1", root.GetProperty("TicketId").GetString());
        Assert.True(root.GetProperty("Success").GetBoolean());
        Assert.Equal("M", root.GetProperty("SizeLabel").GetString());
        Assert.Equal("low", root.GetProperty("RiskLabel").GetString());
        Assert.Equal("deadbeef", root.GetProperty("InvestigatedAtSha").GetString());
    }
}
