using System.Globalization;
using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Common;

/// <summary>
/// Classifies a failed <see cref="WorkerResult"/> as a transient provider-level error
/// (usage limit / quota, rate limit, or auth) by pattern-matching the worker's summary and
/// failure reason. The vendor agents disagree on how they tag a quota error - codex returns
/// <see cref="Status.Failed"/> (in-band JSONL error, TLB-490), claude-code returns
/// <see cref="Status.Escalate"/> (is_error envelope) - so this looks at the message text, not just
/// the status. A provider error means the verifier never ran, so the caller must NOT treat it as a
/// review verdict. See TLB-527.
///
/// Deliberately signature-positive: returns null for <see cref="Status.Ok"/> (a real verdict),
/// for verifier timeouts / cancellations, and for parse failures - so genuine review failures and
/// verifier crashes are unaffected.
/// </summary>
public static class ProviderErrorClassifier
{
    // Usage-limit / quota / rate-limit phrases. Lowercased Contains match.
    private static readonly string[] RateLimitSignatures =
    {
        "usage limit", "rate limit", "rate_limit", "ratelimit", "too many requests",
        "quota", "overloaded", "resource_exhausted", "insufficient_quota"
    };

    // Authentication / authorization phrases. Lowercased Contains match.
    private static readonly string[] AuthSignatures =
    {
        "invalid_api_key", "invalid api key", "authentication_error", "authentication error",
        "unauthorized", "not logged in", "/login", "oauth token", "login expired",
        "session expired", "token has expired", "credentials"
    };

    // Timeout / cancellation markers the vendor agents emit on a killed subprocess. These are
    // verifier crashes, NOT provider errors - exclude them explicitly so AC#4 cannot regress.
    private static readonly string[] TimeoutSignatures =
    {
        "cancelled or timed out", "timed out", "execution cancelled"
    };

    private static readonly Regex HttpRateLimitCode = new(@"\b(429|529)\b", RegexOptions.Compiled);
    private static readonly Regex HttpAuthCode = new(@"\b401\b", RegexOptions.Compiled);

    // claude form: "Claude AI usage limit reached|1749590820" (unix seconds, sometimes ms).
    private static readonly Regex PipeUnixTimestamp = new(@"\|\s*(\d{10,13})\b", RegexOptions.Compiled);
    // codex form: "... or try again at Jun 10th, 2026 5:27 PM."
    private static readonly Regex TryAgainAt = new(
        @"try again at\s+(.+?)(?:\.\s*$|\.$|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OrdinalSuffix = new(@"(\d{1,2})(st|nd|rd|th)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns a typed <see cref="ProviderError"/> when the result is a transient provider failure,
    /// or null otherwise. <paramref name="agentName"/> (e.g. the verifier worker's Name) labels the
    /// provider in operator messaging; when null the provider is inferred from the message text.
    /// </summary>
    public static ProviderError? Classify(WorkerResult result, string? agentName = null)
    {
        // A worker that ran cleanly and produced a verdict is never a provider error.
        if (result.Status == Status.Ok)
            return null;

        var haystack = ((result.FailureReason ?? "") + " " + (result.Summary ?? "")).ToLowerInvariant();
        if (haystack.Length == 0)
            return null;

        // Verifier timeout / cancellation is a crash, not a provider block. Never reclassify it.
        foreach (var t in TimeoutSignatures)
            if (haystack.Contains(t, StringComparison.Ordinal))
                return null;

        bool rateLimited = HttpRateLimitCode.IsMatch(haystack);
        if (!rateLimited)
        {
            foreach (var s in RateLimitSignatures)
            {
                if (haystack.Contains(s, StringComparison.Ordinal)) { rateLimited = true; break; }
            }
        }

        if (rateLimited)
        {
            var msg = ExtractRawMessage(result);
            return new ProviderError(
                ProviderErrorKind.RateLimitOrQuota,
                ResolveProvider(agentName, haystack),
                msg,
                TryParseRetryAt(msg));
        }

        bool authFailed = HttpAuthCode.IsMatch(haystack);
        if (!authFailed)
        {
            foreach (var s in AuthSignatures)
            {
                if (haystack.Contains(s, StringComparison.Ordinal)) { authFailed = true; break; }
            }
        }

        if (authFailed)
        {
            var msg = ExtractRawMessage(result);
            return new ProviderError(
                ProviderErrorKind.Auth,
                ResolveProvider(agentName, haystack),
                msg,
                null);
        }

        return null;
    }

    // The verbatim provider text the operator should see. FailureReason carries the full detail
    // (the in-band codex error or the claude is_error message); fall back to the summary.
    private static string ExtractRawMessage(WorkerResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.FailureReason))
            return result.FailureReason!.Trim();
        return (result.Summary ?? "").Trim();
    }

    private static string ResolveProvider(string? agentName, string loweredHaystack)
    {
        if (!string.IsNullOrWhiteSpace(agentName))
            return agentName!;
        if (loweredHaystack.Contains("codex", StringComparison.Ordinal) || loweredHaystack.Contains("openai", StringComparison.Ordinal))
            return "codex";
        if (loweredHaystack.Contains("claude", StringComparison.Ordinal) || loweredHaystack.Contains("anthropic", StringComparison.Ordinal))
            return "claude-code";
        if (loweredHaystack.Contains("gemini", StringComparison.Ordinal) || loweredHaystack.Contains("google", StringComparison.Ordinal))
            return "gemini";
        return "the configured agent";
    }

    // Best-effort: never throws. The raw message is always shown regardless, so a parse miss just
    // omits the machine-readable reset time.
    internal static DateTimeOffset? TryParseRetryAt(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var pipe = PipeUnixTimestamp.Match(message);
        if (pipe.Success && long.TryParse(pipe.Groups[1].Value, out var epoch))
        {
            try
            {
                return pipe.Groups[1].Value.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Implausible epoch - fall through to the textual form.
            }
        }

        var again = TryAgainAt.Match(message);
        if (again.Success)
        {
            var candidate = OrdinalSuffix.Replace(again.Groups[1].Value, "$1").Trim().TrimEnd('.').Trim();
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
                return parsed;
        }

        return null;
    }
}
