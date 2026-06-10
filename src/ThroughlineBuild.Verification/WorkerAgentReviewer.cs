using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Verification;

/// <summary>
/// IVerifier implementation that dispatches a worker agent against a review brief
/// and maps the WORKER_RESULT envelope into a typed Verdict.
/// The review brief is passed in by the caller (ReviewPhase) via the brief parameter of VerifyAsync.
/// Constructor captures the worker, worker options, and working directory used for dispatch.
/// </summary>
public sealed class WorkerAgentReviewer : IVerifier
{
    private readonly IWorkerAgent _worker;
    private readonly Ticket _ticket;
    private readonly IReadOnlyList<CheckResult> _checkResults;
    private readonly WorkerOptions _workerOptions;
    private readonly string _workingDirectory;
    private readonly ProjectContext _project;
    private readonly IReadOnlyList<SmokeSignal>? _smokeSignals;

    public WorkerResult? LastWorkerResult { get; private set; }

    /// <summary>
    /// Set when the most recent VerifyAsync call's worker failed with a transient provider error
    /// (usage limit / quota, rate limit, or auth) rather than producing a verdict. The verifier
    /// never ran, so ReviewPhase must treat this as "review unavailable", NOT as a Fail verdict.
    /// Null when the worker produced a verdict or failed for some other (real) reason. See TLB-527.
    /// </summary>
    public ProviderError? LastProviderError { get; private set; }

    public WorkerAgentReviewer(
        IWorkerAgent worker,
        Ticket ticket,
        IReadOnlyList<CheckResult> checkResults,
        WorkerOptions workerOptions,
        string workingDirectory,
        ProjectContext? project = null,
        IReadOnlyList<SmokeSignal>? smokeSignals = null)
    {
        _worker = worker;
        _ticket = ticket;
        _checkResults = checkResults;
        _workerOptions = workerOptions;
        _workingDirectory = workingDirectory;
        _project = project ?? ProjectContext.Empty;
        _smokeSignals = smokeSignals;
    }

    /// <summary>
    /// Builds a review brief via ReviewBriefBuilder and dispatches the worker against the feature worktree.
    /// Maps the resulting WORKER_RESULT metadata into a typed Verdict.
    /// </summary>
    /// <param name="implementerBrief">The implementer brief (retained on the public surface; not forwarded to the worker in v1).</param>
    /// <param name="diff">The git diff being reviewed.</param>
    /// <param name="implementerResult">The implementer worker result.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Verdict> VerifyAsync(
        Brief implementerBrief,
        GitDiff diff,
        WorkerResult implementerResult,
        CancellationToken ct)
    {
        // implementerBrief is on the public surface for future use; not forwarded in v1
        _ = implementerBrief;

        LastProviderError = null;

        var reviewBrief = ReviewBriefBuilder.Build(_worker.Name, _ticket, diff, implementerResult, _checkResults, _project, _smokeSignals);

        var workerResult = await _worker.ExecuteAsync(reviewBrief, _workingDirectory, _workerOptions, ct)
            .ConfigureAwait(false);

        LastWorkerResult = workerResult;

        if (workerResult.Status != Status.Ok)
        {
            // A provider-level block (quota/rate-limit/auth) means the verifier never produced a
            // judgment. Surface it as a typed signal so ReviewPhase can route it to "review
            // unavailable" instead of recording a Fail verdict the reviewer never made. The Fail
            // verdict below remains a defensive fallback for genuine worker failures (crash,
            // timeout, malformed output). See TLB-527.
            LastProviderError = ProviderErrorClassifier.Classify(workerResult, _worker.Name);

            var reason = workerResult.FailureReason ?? workerResult.Status.ToString();
            return new Verdict(VerdictKind.Fail, $"verifier worker failed: {reason}", Array.Empty<string>());
        }

        var metadata = workerResult.Metadata;

        var raw = TryGetString(metadata, "verdict");
        VerdictKind kind;

        if (string.Equals(raw, "Pass", StringComparison.OrdinalIgnoreCase))
            kind = VerdictKind.Pass;
        else if (string.Equals(raw, "Rework", StringComparison.OrdinalIgnoreCase))
            kind = VerdictKind.Rework;
        else if (string.Equals(raw, "Fail", StringComparison.OrdinalIgnoreCase))
            kind = VerdictKind.Fail;
        else
            kind = VerdictKind.Fail;

        // Determine whether the verdict was a recognized value or malformed/missing.
        bool verdictMalformed = !string.Equals(raw, "Pass", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(raw, "Rework", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(raw, "Fail", StringComparison.OrdinalIgnoreCase);

        string rationale;
        if (verdictMalformed)
        {
            rationale = $"malformed verdict from verifier: '{raw}'";
        }
        else
        {
            // Resolve rationale from REVIEW_CRITIQUE block via rationale_ref.
            // Falls back to direct "rationale" metadata key for backward compatibility.
            var blocks = workerResult.Blocks ?? new Dictionary<string, string>();
            if (FencedBlockResolver.TryResolveRef(blocks, metadata, "rationale_ref", out var resolvedRationale, out _)
                && resolvedRationale is not null)
            {
                rationale = resolvedRationale;
            }
            else
            {
                rationale = TryGetString(metadata, "rationale") ?? "";
            }
        }

        var checksFailed = ParseChecksFailed(metadata);

        // Check role semantics are a cross-phase contract: advisory failures must never drive
        // rework. The review brief already tells the verifier not to list advisory checks in
        // checks_failed; this filter makes it true by construction even when the verifier
        // ignores the framing, so an advisory-only finding can never burn a rework round
        // downstream (ChainPhase feeds Verdict.ChecksFailed straight into ReviewFeedback).
        var advisoryNames = new HashSet<string>(
            _checkResults.Where(r => r.Role == CheckRole.Advisory).Select(r => r.Name),
            StringComparer.Ordinal);
        if (advisoryNames.Count > 0)
            checksFailed = checksFailed.Where(name => !advisoryNames.Contains(name)).ToList();

        return new Verdict(kind, rationale, checksFailed);
    }

    private static IReadOnlyList<string> ParseChecksFailed(IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("checks_failed", out var raw) || raw is null)
            return Array.Empty<string>();

        // Shape 1: already IEnumerable<string>
        if (raw is IEnumerable<string> strings)
            return strings.ToArray();

        // Shape 2: IEnumerable<object> with JsonElement(String) entries
        if (raw is IEnumerable<object> objects)
        {
            var result = new List<string>();
            foreach (var item in objects)
            {
                if (item is string s)
                    result.Add(s);
                else if (item is JsonElement je && je.ValueKind == JsonValueKind.String)
                    result.Add(je.GetString() ?? "");
            }
            return result;
        }

        // Shape 3: JsonElement(Array)
        if (raw is JsonElement arrayElem && arrayElem.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var elem in arrayElem.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.String)
                    result.Add(elem.GetString() ?? "");
            }
            return result;
        }

        return Array.Empty<string>();
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }
}
