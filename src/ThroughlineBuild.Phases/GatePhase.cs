using System.Net;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Phases;

public record GateOptions(IReadOnlyList<CheckSpec> Checks);

// Structured outcome of the gate phase, consumed by ChainPhase and forwarded to ReviewPhase.
public record GateOutcome(
    bool Passed,
    IReadOnlyList<CheckResult> CheckResults,
    IReadOnlyList<SmokeSignal> SmokeSignals,
    string? HardFailReason = null);

// Gate phase: runs between implement and review in the chain loop.
// Validates the completion claim, runs the configured checks once against the warm worktree
// the implementer left, and collects smoke signals. Hard-fails only on Gating role failures
// (build, test, typecheck); lint, format, and smoke signals are advisory. On hard-fail,
// transitions the ticket InReview -> InProgress so ChainPhase re-enters the rework loop.
public class GatePhase
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly GateOptions _gateOptions;
    private readonly IGitClient _git;
    private readonly AutomatedChecksRunner? _checksRunner;

    public GatePhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        GateOptions gateOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
        _gateOptions = gateOptions;
        _git = gitClient ?? new ProcessGitClient();
        _checksRunner = checksRunner;
    }

    public async Task<GateOutcome> RunAsync(
        string ticketId,
        string worktreePath,
        string branchName,
        string baseRef,
        string workingDirectory,
        CompletionClaim? claim,
        CancellationToken ct,
        IReadOnlySet<string>? accumulatedUpstreamProvides = null)
    {
        // Validate claim schema when one is present (null means pre-claim-format worker - allowed).
        if (claim is not null)
        {
            var claimError = ValidateClaim(claim);
            if (claimError is not null)
            {
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "claim_schema_invalid",
                    ["detail"] = claimError
                }, ct).ConfigureAwait(false);
                await TransitionInReviewToInProgressAsync(ticketId, ct).ConfigureAwait(false);
                return new GateOutcome(false, Array.Empty<CheckResult>(), Array.Empty<SmokeSignal>(),
                    $"gate: claim schema invalid - {claimError}");
            }
        }

        // Run the configured checks against the warm worktree the implementer left.
        var runner = _checksRunner ?? new AutomatedChecksRunner();
        var checkResults = await runner.RunAsync(_gateOptions.Checks, worktreePath, ct).ConfigureAwait(false);

        // Collect smoke signals (advisory, never gate-failing).
        var smokeSignals = new List<SmokeSignal>();
        try
        {
            var diff = await _git.DiffAsync(baseRef, branchName, workingDirectory,
                includePatchContent: true, ct).ConfigureAwait(false);
            var collector = new SmokeCollector();
            smokeSignals.Add(collector.CollectDiffFacts(diff));
        }
        catch (Exception ex)
        {
            smokeSignals.Add(new SmokeSignal("diff-facts", SmokeSignalKind.DiffFacts, false,
                $"diff unavailable: {ex.Message}"));
        }

        // Consumes-provides preflight: check whether the current ticket's consumes are
        // a subset of the accumulated upstream provides. No-op (no signal) when consumes
        // is empty or absent; never hard-fails the gate.
        if (claim is not null && claim.Consumes.Count > 0)
        {
            var upstream = accumulatedUpstreamProvides ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
            var unsatisfied = claim.Consumes.Where(c => !upstream.Contains(c)).ToList();
            var satisfied = unsatisfied.Count == 0;
            var details = satisfied
                ? $"all {claim.Consumes.Count} consume(s) satisfied by upstream provides"
                : $"{unsatisfied.Count} unsatisfied: {string.Join(", ", unsatisfied)}";
            smokeSignals.Add(new SmokeSignal("consumes-provides-preflight", SmokeSignalKind.GrepPresent, satisfied, details));
        }

        // Hard-fail only on Gating role failures; Advisory failures (lint, format) are recorded
        // in checkResults but never block the gate.
        var gatingFailed = checkResults
            .Where(r => r.Role == CheckRole.Gating && !r.Passed && !r.Skipped)
            .ToList();

        if (gatingFailed.Count > 0)
        {
            var failedNames = gatingFailed.Select(r => r.Name).ToArray();
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "gating_checks_failed",
                ["checks_failed"] = failedNames
            }, ct).ConfigureAwait(false);
            await TransitionInReviewToInProgressAsync(ticketId, ct).ConfigureAwait(false);
            try
            {
                var escapedNames = WebUtility.HtmlEncode(string.Join(", ", failedNames));
                var commentHtml = $"<p>[gate: hard-fail] gating checks failed: {escapedNames}</p>";
                await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: comment failure must not block the rework loop */ }
            return new GateOutcome(false, checkResults, smokeSignals.AsReadOnly(),
                $"gate: {string.Join(", ", failedNames)} failed");
        }

        return new GateOutcome(true, checkResults, smokeSignals.AsReadOnly());
    }

    private async Task TransitionInReviewToInProgressAsync(string ticketId, CancellationToken ct)
    {
        try
        {
            await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
            {
                ["from"] = "InReview",
                ["to"] = "InProgress"
            }, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal: transition failure is surfaced to the operator via the event log */ }
    }

    private static string? ValidateClaim(CompletionClaim claim)
    {
        if (claim.Provides is null) return "provides is null";
        if (claim.Consumes is null) return "consumes is null";
        if (claim.AcBindings is null) return "ac_bindings is null";
        if (claim.TestsAdded is null) return "tests_added is null";
        return null;
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Gate,
            data), ct).ConfigureAwait(false);
    }
}
