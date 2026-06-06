using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexAgentTests
{
    [Fact]
    public void Name_IsCodex()
    {
        var agent = new CodexAgent();
        Assert.Equal("codex", agent.Name);
    }

    [Fact]
    public void Digester_IsNotNull()
    {
        var agent = new CodexAgent();
        Assert.NotNull(agent.Digester);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_ValidWorkerResult_ReturnsOk()
    {
        var stdout = """
            Some Codex output here.
            WORKER_RESULT
            {
              "status": "Ok",
              "summary": "Did the thing",
              "files_changed": ["foo.cs"],
              "failure_reason": null,
              "metadata": {}
            }
            """;

        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("Did the thing", result.Summary);
        Assert.Contains("foo.cs", result.FilesChanged);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_JsonlAgentMessage_ReturnsOk()
    {
        var stdout = """
            {"type":"thread.started","thread_id":"019e8e73-30f0-7ab1-ba36-daedaae11bcf"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Done.\nWORKER_RESULT\n{\n  \"status\": \"Ok\",\n  \"summary\": \"JSONL worked\",\n  \"files_changed\": [\"bar.cs\"],\n  \"failure_reason\": null,\n  \"metadata\": {\"commit_sha\":\"abc123\",\"summary_ref\":\"IMPLEMENT_SUMMARY\"}\n}"}}
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":12,"reasoning_output_tokens":3}}
            """;

        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("JSONL worked", result.Summary);
        Assert.Contains("bar.cs", result.FilesChanged);
        Assert.True(result.Metadata.TryGetValue("commit_sha", out var commitObj));
        var commit = Assert.IsType<System.Text.Json.JsonElement>(commitObj);
        Assert.Equal("abc123", commit.GetString());
    }

    [Fact]
    public void ParseStdoutForWorkerResult_JsonlAgentMessage_PreservesFencedBlocks()
    {
        var stdout = """
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"<<<REVIEW_CRITIQUE_START\napply the feedback\n<<<REVIEW_CRITIQUE_END\n\nWORKER_RESULT\n{\"status\":\"Ok\",\"summary\":\"review complete\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{\"verdict\":\"Rework\",\"rationale_ref\":\"REVIEW_CRITIQUE\",\"checks_failed\":[]}}"}}
            """;

        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.NotNull(result.Blocks);
        Assert.True(result.Blocks.ContainsKey("REVIEW_CRITIQUE"));
        Assert.Equal("apply the feedback", result.Blocks["REVIEW_CRITIQUE"]);
    }

    [Fact]
    public void ExtractAgentMessagesFromJsonl_ConcatenatesAgentMessageText()
    {
        var stdout = """
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"pwsh -Command rg x","exit_code":0,"status":"completed"}}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"first"}}
            {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"second"}}
            """;

        var result = CodexAgent.ExtractAgentMessagesFromJsonl(stdout);

        Assert.Contains("first", result);
        Assert.Contains("second", result);
        Assert.DoesNotContain("command_execution", result);
    }

    [Fact]
    public void TryExtractUsageFromJsonl_ReadsTurnCompletedUsage()
    {
        var stdout = """
            {"type":"turn.completed","usage":{"input_tokens":123,"cached_input_tokens":20,"output_tokens":45,"reasoning_output_tokens":6}}
            """;

        var usage = CodexAgent.TryExtractUsageFromJsonl(stdout);

        Assert.Equal(123, usage.InputTokens);
        Assert.Equal(45, usage.OutputTokens);
        Assert.Equal(20, usage.CachedInputTokens);
        Assert.Equal(6, usage.ReasoningOutputTokens);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NoMarker_ReturnsFailed()
    {
        var result = CodexAgent.ParseStdoutForWorkerResult("no marker here", 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NonZeroExit_ReturnsFailed()
    {
        var result = CodexAgent.ParseStdoutForWorkerResult("", 1, "some error");
        Assert.Equal(Status.Failed, result.Status);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_InvalidJson_ReturnsFailed()
    {
        var stdout = "WORKER_RESULT\n{not valid json}";
        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsTrue_IncludesFullBypassFlag()
    {
        var options = new CodexOptions { BypassPermissions = true };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
    }

    [Fact]
    public void BuildArgs_IncludesJsonFlag()
    {
        var options = new CodexOptions();
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        Assert.Contains("--json", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsFalse_OmitsFullAuto()
    {
        var options = new CodexOptions { BypassPermissions = false };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
    }

    [Fact]
    public void BuildArgs_UsesStdinSentinelInsteadOfPromptArgument()
    {
        var options = new CodexOptions();
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void BuildArgs_TierWithEffort_AppendsReasoningEffortFlag()
    {
        var options = new CodexOptions
        {
            Sizes = new Dictionary<WorkerSize, ModelTier>
            {
                { WorkerSize.Medium, new ModelTier("gpt-5.5", "xhigh") },
            },
        };
        // WorkerOptions.Size defaults to Medium.
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        var idx = args.IndexOf("model_reasoning_effort=xhigh");
        Assert.True(idx > 0, $"expected model_reasoning_effort=xhigh in argv: {string.Join(" ", args)}");
        // Passed as two discrete ArgumentList entries: "-c" then the key=value.
        Assert.Equal("-c", args[idx - 1]);
        // The effort flag precedes the stdin sentinel.
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void BuildArgs_TierWithoutEffort_OmitsReasoningEffortFlag()
    {
        var options = new CodexOptions
        {
            Sizes = new Dictionary<WorkerSize, ModelTier>
            {
                { WorkerSize.Medium, new ModelTier("gpt-5.5") }, // no effort
            },
        };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = CodexAgent.BuildArgs(options, workerOptions);

        Assert.DoesNotContain("-c", args);
        Assert.DoesNotContain(args, a => a.StartsWith("model_reasoning_effort="));
    }

    [Fact]
    public void WriteDebugCapture_RecordsBriefAsStdin()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-capture-{Guid.NewGuid():N}");
        try
        {
            var result = new WorkerResult(Status.Ok, "done", Array.Empty<string>(), null,
                new Dictionary<string, object>());

            CodexAgent.WriteDebugCapture(directory, "brief with unicode: café", "stdout", "stderr", result);

            Assert.Equal("brief with unicode: café", File.ReadAllText(Path.Combine(directory, "worker-stdin.txt")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
