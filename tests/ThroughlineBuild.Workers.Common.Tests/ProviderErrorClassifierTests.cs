using System.Collections.Generic;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;
using Xunit;

namespace ThroughlineBuild.Workers.Common.Tests;

// Classification coverage for the review provider-error fix (TLB-527). One test per error class
// the acceptance criteria call out: quota/rate-limit, auth, genuine review failure, verifier timeout.
public class ProviderErrorClassifierTests
{
    private static WorkerResult Failed(string summary, string failureReason) =>
        new(Status.Failed, summary, System.Array.Empty<string>(), failureReason, new Dictionary<string, object>());

    private static WorkerResult Escalate(string summary, string failureReason) =>
        new(Status.Escalate, summary, System.Array.Empty<string>(), failureReason, new Dictionary<string, object>());

    // ---- quota / rate-limit -------------------------------------------------

    [Fact]
    public void CodexUsageLimit_ClassifiedAsRateLimitOrQuota_WithProviderAndRetryAt()
    {
        // Mirrors CodexAgent's in-band error path (TLB-490): Status.Failed, the message on FailureReason.
        var result = Failed(
            "Process exited with non-zero code",
            "Exit code 1. Codex error: You've hit your usage limit. Upgrade to Pro (https://openai.com) or try again at Jun 10th, 2026 5:27 PM.");

        var pe = ProviderErrorClassifier.Classify(result, agentName: "codex");

        Assert.NotNull(pe);
        Assert.Equal(ProviderErrorKind.RateLimitOrQuota, pe!.Kind);
        Assert.Equal("codex", pe.Provider);
        Assert.Contains("usage limit", pe.RawMessage);
        Assert.NotNull(pe.RetryAt);
        Assert.Equal(2026, pe.RetryAt!.Value.Year);
        Assert.Equal(6, pe.RetryAt.Value.Month);
        Assert.Equal(10, pe.RetryAt.Value.Day);
        Assert.Equal(17, pe.RetryAt.Value.Hour);
        Assert.Equal(27, pe.RetryAt.Value.Minute);
    }

    [Fact]
    public void ClaudeUsageLimit_PipeUnixTimestamp_ClassifiedWithParsedRetryAt()
    {
        // Mirrors ClaudeCodeAgent's is_error envelope path: Status.Escalate, "reached|<unix-ts>".
        var result = Escalate(
            "Claude Code reported is_error=true",
            "Claude Code envelope has is_error=true. Subtype: error_during_execution. Message: Claude AI usage limit reached|1749590820. Stderr: ");

        var pe = ProviderErrorClassifier.Classify(result, agentName: "claude-code");

        Assert.NotNull(pe);
        Assert.Equal(ProviderErrorKind.RateLimitOrQuota, pe!.Kind);
        Assert.Equal("claude-code", pe.Provider);
        Assert.Equal(System.DateTimeOffset.FromUnixTimeSeconds(1749590820), pe.RetryAt);
    }

    [Fact]
    public void AnthropicRateLimitError_ClassifiedAsRateLimitOrQuota()
    {
        var result = Failed(
            "Process exited with non-zero code",
            "Exit code 1. error: {\"type\":\"rate_limit_error\",\"message\":\"Number of requests has exceeded your rate limit\"}");

        var pe = ProviderErrorClassifier.Classify(result, agentName: "claude-code");

        Assert.NotNull(pe);
        Assert.Equal(ProviderErrorKind.RateLimitOrQuota, pe!.Kind);
    }

    [Fact]
    public void Http429_ClassifiedAsRateLimitOrQuota()
    {
        var result = Failed("Process exited with non-zero code", "Exit code 1. Codex error: HTTP 429 Too Many Requests");

        var pe = ProviderErrorClassifier.Classify(result, agentName: "codex");

        Assert.NotNull(pe);
        Assert.Equal(ProviderErrorKind.RateLimitOrQuota, pe!.Kind);
    }

    [Fact]
    public void ProviderInferredFromMessage_WhenAgentNameAbsent()
    {
        var result = Failed("Process exited with non-zero code", "Codex error: You've hit your usage limit.");

        var pe = ProviderErrorClassifier.Classify(result, agentName: null);

        Assert.NotNull(pe);
        Assert.Equal("codex", pe!.Provider);
    }

    // ---- auth ---------------------------------------------------------------

    [Fact]
    public void Auth401InvalidKey_ClassifiedAsAuth()
    {
        var result = Failed("Process exited with non-zero code", "Exit code 1. Codex error: 401 Unauthorized: invalid_api_key");

        var pe = ProviderErrorClassifier.Classify(result, agentName: "codex");

        Assert.NotNull(pe);
        Assert.Equal(ProviderErrorKind.Auth, pe!.Kind);
        Assert.Null(pe.RetryAt);
    }

    // ---- NOT provider errors (no regression) --------------------------------

    [Fact]
    public void GenuineReviewVerdict_OkStatus_ReturnsNull()
    {
        // A worker that ran and produced a verdict is Status.Ok - never a provider error,
        // even if the rationale text happened to mention a "rate limit".
        var ok = new WorkerResult(Status.Ok, "review complete", System.Array.Empty<string>(), null,
            new Dictionary<string, object> { ["verdict"] = "Fail", ["rationale"] = "the rate limit handling is wrong" });

        Assert.Null(ProviderErrorClassifier.Classify(ok, agentName: "codex"));
    }

    [Fact]
    public void VerifierTimeout_ReturnsNull()
    {
        // Mirrors the OperationCanceledException path in the vendor agents - a crash, not a block.
        var timedOut = Failed("Process cancelled or timed out", "Execution cancelled or timed out");

        Assert.Null(ProviderErrorClassifier.Classify(timedOut, agentName: "codex"));
    }

    [Fact]
    public void GenericWorkerFailure_NoSignature_ReturnsNull()
    {
        var noEnvelope = Failed("No WORKER_RESULT found in output", "No WORKER_RESULT block found in stdout. processed 4290 tokens. Stderr: ");

        // "4290" must not trip the \b429\b HTTP-code matcher.
        Assert.Null(ProviderErrorClassifier.Classify(noEnvelope, agentName: "codex"));
    }

    // ---- retry-at parsing unit ----------------------------------------------

    [Fact]
    public void TryParseRetryAt_PipeUnixSeconds_Parsed()
    {
        var at = ProviderErrorClassifier.TryParseRetryAt("Claude AI usage limit reached|1749590820");
        Assert.Equal(System.DateTimeOffset.FromUnixTimeSeconds(1749590820), at);
    }

    [Fact]
    public void TryParseRetryAt_NoHint_ReturnsNull()
    {
        Assert.Null(ProviderErrorClassifier.TryParseRetryAt("You've hit your usage limit. Upgrade to Pro."));
    }
}
