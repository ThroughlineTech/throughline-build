namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// A transient provider-level failure (usage limit / quota, rate limit, or auth) detected from a
/// worker's exit code and output. Distinct from a review <see cref="Verdict"/>: the verifier never
/// produced a judgment - the provider blocked the call before review could run, so treating it as a
/// Fail verdict (and a chain StoppedAtReview) is wrong. Carried up through <c>ReviewResult</c> and
/// surfaced as <see cref="ChainOutcome.ReviewUnavailable"/>. See TLB-527.
/// </summary>
public record ProviderError(
    ProviderErrorKind Kind,
    // The agent/provider that was blocked (e.g. "codex", "claude-code"), for operator messaging.
    string Provider,
    // The raw provider message, verbatim, so the operator sees the original quota/auth text.
    string RawMessage,
    // Best-effort parsed reset time when the provider supplied one ("try again at ...",
    // or the claude "...reached|<unix-ts>" form); null when none was present or it did not parse.
    DateTimeOffset? RetryAt = null);

public enum ProviderErrorKind
{
    // Usage limit / quota exhausted, rate limited, HTTP 429/529, overloaded.
    RateLimitOrQuota,
    // Authentication / authorization failure (expired login, invalid key, 401).
    Auth
}
