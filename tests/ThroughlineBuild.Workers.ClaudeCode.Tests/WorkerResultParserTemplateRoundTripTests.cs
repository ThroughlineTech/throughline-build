using System.Text.RegularExpressions;
using Xunit;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

/// <summary>
/// Template-to-parser round-trip test: validates that PlanBriefBuilder's
/// WORKER_RESULT example block can be successfully parsed by WorkerResultParser.
/// This catches misalignment between template and parser (e.g., a field name
/// changed in the template but not in the parser validation logic).
/// </summary>
public class WorkerResultParserTemplateRoundTripTests
{
    [Fact]
    public void TemplateRoundTrip_PlanBriefBuilderOutputParsesSuccessfully()
    {
        // Build a minimal fixture ticket and repo state.
        var ticket = new Ticket(
            Id: "TEST-001",
            Title: "Test ticket",
            Type: "feature",
            State: TicketState.Backlog,
            Size: Size.S,
            Risk: Risk.Low,
            DescriptionHtml: "<p>Test description</p>",
            Relations: Array.Empty<Relation>(),
            Labels: Array.Empty<string>(),
            ParentId: null
        );

        var repo = new RepoState(
            MainSha: "deadbeef",
            TopLevelEntries: new List<string> { "src", "tests", "README.md" }
        );

        // Build the brief (this renders the template).
        var brief = PlanBriefBuilder.Build(ticket, repo);

        // Locate the WORKER_RESULT block: it is inside a triple-backtick fence.
        // Pattern: ```NEWLINE WORKER_RESULT NEWLINE {...JSON...} NEWLINE```
        var workerResultMatch = Regex.Match(
            brief.Instruction,
            @"```\s*\nWORKER_RESULT\s*\n([\s\S]*?)\n```",
            RegexOptions.Multiline
        );
        Assert.True(workerResultMatch.Success, "Template must contain WORKER_RESULT block inside fenced code block");

        // Extract the JSON (everything between WORKER_RESULT and the closing fence).
        var jsonBlock = workerResultMatch.Groups[1].Value.Trim();

        // Substitute placeholder values. We also substitute a non-empty
        // files_changed array so the round-trip can assert on a value that
        // exercises the key binding (the parser coalesces null to an empty
        // list, so an empty fixture cannot distinguish "key bound to []" from
        // "key absent / renamed and parser fell back to []").
        var substituted = jsonBlock
            .Replace("<one-line root cause or approach>", "fixture summary")
            .Replace("<the complete HTML block from Output structure, JSON-escaped>", "<p>plan</p>")
            .Replace("<low|medium|high>", "low")
            .Replace("<S|M|L>", "S")
            .Replace("\"files_changed\": []", "\"files_changed\": [\"sample.cs\"]");

        // Build synthetic worker stdout: WORKER_RESULT + substituted JSON.
        var stdout = "WORKER_RESULT\n" + substituted;

        // Parse using WorkerResultParser.
        var outcome = WorkerResultParser.TryParse(stdout);

        // Assertions per the ticket plan.
        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("fixture summary", outcome.Result.Summary);
        // Load-bearing files_changed key check: if the template renames
        // files_changed (e.g., to changed_files), the parser will not bind
        // the value, falling back to an empty list - Assert.Single fails on
        // count, catching the regression.
        Assert.Single(outcome.Result.FilesChanged);
        Assert.Equal("sample.cs", outcome.Result.FilesChanged[0]);
        Assert.Null(outcome.Result.FailureReason);

        // Metadata must contain the expected keys (extracted from the template example).
        Assert.NotNull(outcome.Result.Metadata);
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_html"),
            "Metadata must contain 'plan_html' key");
        Assert.True(outcome.Result.Metadata.ContainsKey("risk_label"),
            "Metadata must contain 'risk_label' key");
        Assert.True(outcome.Result.Metadata.ContainsKey("size_label"),
            "Metadata must contain 'size_label' key");
        Assert.True(outcome.Result.Metadata.ContainsKey("planned_at_sha"),
            "Metadata must contain 'planned_at_sha' key");
    }
}
