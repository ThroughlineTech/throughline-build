using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Common.Tests;

// Regression lock for the AOT serialization trap documented in
// docs/throughline-build-architecture.md (section "AOT serialization traps").
//
// WorkerResultParser.TryParse must use the source-gen overload
// (WorkersCommonJsonContext.Default.WorkerResultDto), not the reflection-based
// JsonSerializer.Deserialize<T>. Under PublishAot=true the Cli project emits
// a build.runtimeconfig.json with IsReflectionEnabledByDefault=false; any
// call through the reflection path throws NotSupportedException which the
// bare catch swallows, producing a silent null result (the TLB-108 bug).
//
// These tests prove the source-gen path is wired and functional without
// touching the process-global AppContext reflection switch (which would race
// with parallel test classes that use reflection-based helpers).
public class WorkerResultParserAotRegressionTests
{
    // Verify that WorkersCommonJsonContext has WorkerResultDto registered and
    // that the source-gen overload deserializes the full happy-path payload.
    // This is the direct proof that the AOT path is wired: if WorkerResultDto
    // were absent from the context, .Default.WorkerResultDto would throw at
    // class-load time (not at runtime), so reaching the Assert.NotNull confirms
    // source-gen registration is present.
    [Fact]
    public void SourceGenContext_HasWorkerResultDto_AndDeserializesHappyPath()
    {
        var json =
            "{\"status\":\"Ok\",\"summary\":\"plan complete\",\"files_changed\":[\"src/Foo.cs\"]," +
            "\"failure_reason\":null,\"metadata\":{\"plan_html\":\"<p>plan</p>\"," +
            "\"risk_label\":\"low\",\"size_label\":\"M\",\"planned_at_sha\":\"abc123\"}}";

        // Direct source-gen call - this is exactly what TryParse now uses.
        // If WorkerResultDto is not registered in WorkersCommonJsonContext,
        // this line throws InvalidOperationException at runtime.
        var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.Ok, dto.Status);
        Assert.Equal("plan complete", dto.Summary);
        Assert.NotNull(dto.FilesChanged);
        Assert.Single(dto.FilesChanged, "src/Foo.cs");
        Assert.Null(dto.FailureReason);
        Assert.NotNull(dto.Metadata);
        Assert.True(dto.Metadata.ContainsKey("plan_html"));
    }

    [Fact]
    public void SourceGenContext_WorkerResultDto_DeserializesNeedsReworkStatus()
    {
        var json =
            "{\"status\":\"NeedsRework\",\"summary\":\"try again\"," +
            "\"files_changed\":[],\"failure_reason\":\"partial\",\"metadata\":{}}";

        var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.NeedsRework, dto.Status);
        Assert.Equal("partial", dto.FailureReason);
    }

    [Fact]
    public void SourceGenContext_WorkerResultDto_DeserializesEscalateStatus()
    {
        var json =
            "{\"status\":\"Escalate\",\"summary\":\"escalated\"," +
            "\"files_changed\":[],\"failure_reason\":\"unclear\",\"metadata\":{}}";

        var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.Escalate, dto.Status);
    }

    // End-to-end: TryParse with metadata containing plan_html returns a
    // WorkerResult whose Metadata map carries the key. Validates that the
    // Dictionary<string, JsonElement> -> Dictionary<string, object> projection
    // in TryParse preserves keys for downstream TryGetString consumers.
    [Fact]
    public void TryParse_MetadataWithPlanHtml_KeyPresentInResult()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null," +
            "\"metadata\":{\"plan_html\":\"<p>x</p>\",\"risk_label\":\"low\"," +
            "\"size_label\":\"S\",\"planned_at_sha\":\"deadbeef\"}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_html"));
        Assert.True(outcome.Result.Metadata.ContainsKey("risk_label"));
    }
}

public class BatchWorkerResultParserTests
{
    [Fact]
    public void SourceGenContext_BatchTicketResult_RoundTripsWithReflectionDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var ticket = new BatchTicketResult(
            TicketId: "TLB-443",
            CommitSha: "abc123",
            StackPosition: 2,
            FilesChanged: new[] { "src/A.cs", "tests/A.Tests.cs" },
            SummaryRef: "SUMMARY_TLB_443");

        var json = JsonSerializer.Serialize(ticket, WorkersCommonJsonContext.Default.BatchTicketResult);
        var roundTrip = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.BatchTicketResult);

        Assert.NotNull(roundTrip);
        Assert.Equal(ticket.TicketId, roundTrip.TicketId);
        Assert.Equal(ticket.CommitSha, roundTrip.CommitSha);
        Assert.Equal(ticket.StackPosition, roundTrip.StackPosition);
        Assert.Equal(ticket.FilesChanged, roundTrip.FilesChanged);
        Assert.Equal(ticket.SummaryRef, roundTrip.SummaryRef);
        Assert.Contains("\"ticket_id\"", json);
        Assert.Contains("\"commit_sha\"", json);
        Assert.Contains("\"stack_position\"", json);
        Assert.Contains("\"files_changed\"", json);
        Assert.Contains("\"summary_ref\"", json);
    }

    [Fact]
    public void TryParseBatch_WellFormedPayload_ReturnsTicketsInDeclaredOrder()
    {
        var stdout =
            "<<<SUMMARY_TLB_443_START\n" +
            "Implemented contract.\n" +
            "<<<SUMMARY_TLB_443_END\n" +
            "<<<SUMMARY_TLB_444_START\n" +
            "Implemented producer.\n" +
            "<<<SUMMARY_TLB_444_END\n" +
            "WORKER_RESULT\n" +
            "{\n" +
            "  \"status\": \"Ok\",\n" +
            "  \"summary\": \"batch complete\",\n" +
            "  \"files_changed\": [\"src/Batch.cs\"],\n" +
            "  \"failure_reason\": null,\n" +
            "  \"metadata\": {\"commit_sha\": \"stack-tip\"},\n" +
            "  \"tickets\": [\n" +
            "    {\"ticket_id\":\"TLB-443\",\"commit_sha\":\"sha-443\",\"stack_position\":1,\"files_changed\":[\"src/A.cs\"],\"summary_ref\":\"SUMMARY_TLB_443\"},\n" +
            "    {\"ticket_id\":\"TLB-444\",\"commit_sha\":\"sha-444\",\"stack_position\":2,\"files_changed\":[\"src/B.cs\"],\"summary_ref\":\"SUMMARY_TLB_444\"}\n" +
            "  ]\n" +
            "}\n";

        var outcome = WorkerResultParser.TryParseBatch(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("batch complete", outcome.Result.Summary);
        Assert.Collection(
            outcome.Result.Tickets,
            first =>
            {
                Assert.Equal("TLB-443", first.TicketId);
                Assert.Equal("sha-443", first.CommitSha);
                Assert.Equal(1, first.StackPosition);
                Assert.Single(first.FilesChanged, "src/A.cs");
                Assert.Equal("SUMMARY_TLB_443", first.SummaryRef);
            },
            second =>
            {
                Assert.Equal("TLB-444", second.TicketId);
                Assert.Equal("sha-444", second.CommitSha);
                Assert.Equal(2, second.StackPosition);
                Assert.Single(second.FilesChanged, "src/B.cs");
                Assert.Equal("SUMMARY_TLB_444", second.SummaryRef);
            });
        Assert.Equal("Implemented contract.", outcome.Blocks["SUMMARY_TLB_443"]);
        Assert.Equal("Implemented producer.", outcome.Blocks["SUMMARY_TLB_444"]);
    }

    [Fact]
    public void TryParseBatch_MissingTicketsArray_FailsWithClearMessage()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"batch complete\",\"files_changed\":[]," +
            "\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParseBatch(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("ValidationError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("tickets", outcome.DeserializeErrorMessage);
        Assert.Contains("array", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParseBatch_MalformedTicketsArray_FailsWithClearMessage()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"batch complete\",\"files_changed\":[]," +
            "\"failure_reason\":null,\"metadata\":{},\"tickets\":[" +
            "{\"ticket_id\":\"TLB-443\",\"commit_sha\":\"sha-443\",\"stack_position\":1," +
            "\"files_changed\":[],\"summary_ref\":\"SUMMARY_TLB_443\"}," +
            "{\"ticket_id\":\"TLB-444\",\"commit_sha\":\"sha-444\",\"stack_position\":2," +
            "\"summary_ref\":\"SUMMARY_TLB_444\"}]}\n";

        var outcome = WorkerResultParser.TryParseBatch(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("ValidationError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("tickets[1].files_changed", outcome.DeserializeErrorMessage);
    }
}

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
            Uuid: "test-uuid-1",
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
        var brief = PlanBriefBuilder.Build("claude-code", ticket, repo);

        // The template must instruct the worker to emit a PLAN_BODY fenced block.
        Assert.Contains("PLAN_BODY_START", brief.Instruction);

        // Locate the WORKER_RESULT envelope block. The template also contains an
        // escalation example; we want the final block (the envelope at the bottom).
        // WORKER_RESULT is a bare marker followed by a JSON object whose outer closing
        // brace sits at column 0 (no indent), so \n\} reliably terminates each block.
        var allWorkerResultMatches = Regex.Matches(
            brief.Instruction,
            @"WORKER_RESULT\s*\n(\{[\s\S]*?\n\})",
            RegexOptions.Multiline
        );
        Assert.True(allWorkerResultMatches.Count > 0, "Template must contain at least one WORKER_RESULT block");

        // Extract the JSON from the last (envelope) block.
        var jsonBlock = allWorkerResultMatches[^1].Groups[1].Value.Trim();

        // Substitute placeholder values. We also substitute a non-empty
        // files_changed array so the round-trip can assert on a value that
        // exercises the key binding (the parser coalesces null to an empty
        // list, so an empty fixture cannot distinguish "key bound to []" from
        // "key absent / renamed and parser fell back to []").
        var substituted = jsonBlock
            .Replace("<one-line root cause or approach>", "fixture summary")
            .Replace("<low|medium|high>", "low")
            .Replace("<S|M|L>", "S")
            .Replace("\"files_changed\": []", "\"files_changed\": [\"sample.cs\"]");

        // Build synthetic worker stdout: PLAN_BODY fenced block + WORKER_RESULT + substituted JSON.
        // The parser requires fenced blocks to appear before the WORKER_RESULT marker.
        var stdout =
            "<<<PLAN_BODY_START\n" +
            "# Investigation\n" +
            "Fixture plan body.\n" +
            "<<<PLAN_BODY_END\n\n" +
            "WORKER_RESULT\n" + substituted;

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
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_body_ref"),
            "Metadata must contain 'plan_body_ref' key");
        Assert.False(outcome.Result.Metadata.ContainsKey("plan_html"),
            "Metadata must NOT contain 'plan_html' key - it was replaced by plan_body_ref");
        Assert.True(outcome.Result.Metadata.ContainsKey("risk_label"),
            "Metadata must contain 'risk_label' key");
        Assert.True(outcome.Result.Metadata.ContainsKey("size_label"),
            "Metadata must contain 'size_label' key");
        Assert.True(outcome.Result.Metadata.ContainsKey("planned_at_sha"),
            "Metadata must contain 'planned_at_sha' key");

        // The PLAN_BODY block must be present in the parsed outcome.
        Assert.NotNull(outcome.Blocks);
        Assert.True(outcome.Blocks.ContainsKey("PLAN_BODY"),
            "Parsed outcome must have a PLAN_BODY block");
    }
}

/// <summary>
/// Canary test: PLAN_BODY block with unescaped quotes and special chars parses correctly.
/// Verifies the fenced-block protocol does not require JSON-escaping of plan content.
/// </summary>
public class WorkerResultParserPlanBodyCanaryTests
{
    [Fact]
    public void TryParse_PlanBodyBlock_WithUnescapedQuotesAndSpecialChars_ParsesSuccessfully()
    {
        // Synthetic stdout: PLAN_BODY block with content containing unescaped double-quotes
        // and special characters that would break JSON-string encoding if mistakenly escaped.
        var stdout =
            "<<<PLAN_BODY_START\n" +
            "# Plan with \"quoted\" text\n" +
            "Use `dotnet test` to verify. Path: src\\Foo.cs.\n" +
            "Risk: low - \"isolated change\".\n" +
            "<<<PLAN_BODY_END\n\n" +
            "WORKER_RESULT\n" +
            "{\n" +
            "  \"status\": \"Ok\",\n" +
            "  \"summary\": \"canary plan body test\",\n" +
            "  \"files_changed\": [],\n" +
            "  \"failure_reason\": null,\n" +
            "  \"metadata\": {\n" +
            "    \"plan_body_ref\": \"PLAN_BODY\",\n" +
            "    \"risk_label\": \"low\",\n" +
            "    \"size_label\": \"S\",\n" +
            "    \"planned_at_sha\": \"abc123\"\n" +
            "  }\n" +
            "}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("canary plan body test", outcome.Result.Summary);
        Assert.NotNull(outcome.Blocks);
        Assert.True(outcome.Blocks.ContainsKey("PLAN_BODY"),
            "Parsed outcome must have a PLAN_BODY block");
        Assert.Contains("\"quoted\"", outcome.Blocks["PLAN_BODY"]);
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_body_ref"),
            "Metadata must contain 'plan_body_ref'");
    }
}

/// <summary>
/// Tests for metadata.escalation schema validation in WorkerResultParser.
/// Covers: valid obsolete escalation, missing subsumed_by on obsolete reason, unknown reason passthrough.
/// </summary>
public class WorkerResultParserEscalationMetadataTests
{
    [Fact]
    public void TryParse_ValidObsoleteEscalation_ParsesCleanly()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Escalate\",\"summary\":\"ticket is obsolete\",\"files_changed\":[]," +
            "\"failure_reason\":null,\"metadata\":{\"escalation\":{\"reason\":\"obsolete\"," +
            "\"subsumed_by\":{\"commit\":\"abc123\",\"files\":[\"src/A.cs\"]," +
            "\"rationale\":\"already done\"}}}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Escalate, outcome.Result.Status);
        Assert.True(outcome.Result.Metadata.ContainsKey("escalation"));
    }

    [Fact]
    public void TryParse_ObsoleteEscalation_MissingSubsumedBy_FailsWithClearMessage()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Escalate\",\"summary\":\"ticket is obsolete\",\"files_changed\":[]," +
            "\"failure_reason\":null,\"metadata\":{\"escalation\":{\"reason\":\"obsolete\"}}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("subsumed_by", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_UnknownEscalationReason_ParsesAndPassesThrough()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Escalate\",\"summary\":\"blocked on dependency\",\"files_changed\":[]," +
            "\"failure_reason\":null,\"metadata\":{\"escalation\":{\"reason\":\"blocked\"}}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.True(outcome.Result.Metadata.ContainsKey("escalation"));
    }
}

/// <summary>
/// Tests for fenced-block extraction in WorkerResultParser (TLB-334).
/// </summary>
public class WorkerResultParserFencedBlockTests
{
    private const string MinimalWorkerResult =
        "WORKER_RESULT\n" +
        "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";

    [Fact]
    public void TryParse_SingleValidBlock_BlockMapContainsBlock_EnvelopeParsed()
    {
        var stdout =
            "<<<PLAN_START\n" +
            "line one\n" +
            "line two\n" +
            "<<<PLAN_END\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.NotNull(outcome.Blocks);
        Assert.True(outcome.Blocks.ContainsKey("PLAN"));
        Assert.Equal("line one\nline two", outcome.Blocks["PLAN"]);
    }

    [Fact]
    public void TryParse_NoBlocks_EmptyBlockMap_EnvelopeParsed()
    {
        var outcome = WorkerResultParser.TryParse(MinimalWorkerResult);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.NotNull(outcome.Blocks);
        Assert.Empty(outcome.Blocks);
    }

    [Fact]
    public void TryParse_TwoValidBlocks_BothInMap_EnvelopeParsed()
    {
        var stdout =
            "<<<ALPHA_START\n" +
            "alpha content\n" +
            "<<<ALPHA_END\n" +
            "<<<BETA_START\n" +
            "beta content\n" +
            "<<<BETA_END\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(2, outcome.Blocks.Count);
        Assert.Equal("alpha content", outcome.Blocks["ALPHA"]);
        Assert.Equal("beta content", outcome.Blocks["BETA"]);
    }

    [Fact]
    public void TryParse_UnclosedBlock_FenceScanFailed_ErrorContainsUnclosed()
    {
        var stdout =
            "<<<PLAN_START\n" +
            "some content\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("FenceScanError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("unclosed", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_MismatchedBlockName_FenceScanFailed_ErrorContainsMismatched()
    {
        var stdout =
            "<<<ALPHA_START\n" +
            "content\n" +
            "<<<BETA_END\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("FenceScanError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("mismatched", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_DuplicateBlockName_FenceScanFailed_ErrorContainsDuplicate()
    {
        var stdout =
            "<<<PLAN_START\n" +
            "first\n" +
            "<<<PLAN_END\n" +
            "<<<PLAN_START\n" +
            "second\n" +
            "<<<PLAN_END\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("FenceScanError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("duplicate", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_InvalidBlockName_Lowercase_FenceScanFailed_ErrorContainsInvalidBlockName()
    {
        var stdout =
            "<<<plan_START\n" +
            "content\n" +
            "<<<plan_END\n" +
            MinimalWorkerResult;

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("FenceScanError", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
        Assert.Contains("invalid block name", outcome.DeserializeErrorMessage);
    }
}

/// <summary>
/// Tests for FencedBlockResolver._ref helper (TLB-334).
/// </summary>
public class FencedBlockResolverTests
{
    private static IReadOnlyDictionary<string, string> MakeBlocks(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
            d[k] = v;
        return d;
    }

    private static IReadOnlyDictionary<string, object> MakeMeta(params (string, object)[] pairs)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
            d[k] = v;
        return d;
    }

    [Fact]
    public void TryResolveRef_ValidRef_ResolvesToBlockContent()
    {
        var blocks = MakeBlocks(("PLAN", "the plan body"));
        var meta = MakeMeta(("plan_body_ref", (object)"PLAN"));

        var resolved = FencedBlockResolver.TryResolveRef(blocks, meta, "plan_body_ref", out var content, out var error);

        Assert.True(resolved);
        Assert.Equal("the plan body", content);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolveRef_RefFieldAbsentFromMetadata_ReturnsFalse_ErrorMentionsField()
    {
        var blocks = MakeBlocks(("PLAN", "body"));
        var meta = MakeMeta();

        var resolved = FencedBlockResolver.TryResolveRef(blocks, meta, "plan_body_ref", out var content, out var error);

        Assert.False(resolved);
        Assert.Null(content);
        Assert.NotNull(error);
        Assert.Contains("plan_body_ref", error);
    }

    [Fact]
    public void TryResolveRef_RefFieldPresentButBlockMissing_ReturnsFalse_ErrorMentionsReferencedBlockNotFound()
    {
        var blocks = MakeBlocks(("OTHER", "something"));
        var meta = MakeMeta(("plan_body_ref", (object)"PLAN"));

        var resolved = FencedBlockResolver.TryResolveRef(blocks, meta, "plan_body_ref", out var content, out var error);

        Assert.False(resolved);
        Assert.Null(content);
        Assert.NotNull(error);
        Assert.Contains("referenced block not found", error);
        Assert.Contains("PLAN", error);
    }
}
