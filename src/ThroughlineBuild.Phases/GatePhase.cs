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
    string? HardFailReason = null,
    // Gate-integrity failure (a gating check could not be proven to fail, or its canary leaked):
    // the chain must hard-fail WITHOUT rework. Distinct from an ordinary gate hard-fail (Passed=false,
    // Vacuous=false), which is a code defect the rework loop can fix.
    bool Vacuous = false);

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
    private readonly GateVacuityProver? _vacuityProver;

    public GatePhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        GateOptions gateOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null,
        GateVacuityProver? vacuityProver = null)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
        _gateOptions = gateOptions;
        _git = gitClient ?? new ProcessGitClient();
        _checksRunner = checksRunner;
        _vacuityProver = vacuityProver;
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

        // Non-vacuity probe: on a gating check's first GREEN, prove it CAN fail by materializing its
        // canary, re-running only that check, and asserting it now fails. If it cannot be proven to
        // fail (vacuous) or its canary leaked (cleanup failed), that is a config/setup defect - hard
        // -fail WITHOUT rework. A green gating check with no canary yields a countable advisory event
        // but never blocks. Stack-agnostic by construction: the canary is data on the CheckSpec.
        if (_vacuityProver is not null)
        {
            foreach (var spec in _gateOptions.Checks)
            {
                if (spec.Role != CheckRole.Gating) continue;
                var res = checkResults.FirstOrDefault(r => r.Name == spec.Name);
                if (res is null || !res.Passed || res.Skipped) continue; // only green gating checks
                var verdict = await _vacuityProver.ProveAsync(spec, runner, _git, worktreePath, ct).ConfigureAwait(false);
                if (verdict.Outcome == GateVacuityOutcome.Unverified)
                {
                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = "gate_unverified",
                        ["check"] = spec.Name,
                        ["detail"] = verdict.Reason ?? ""
                    }, ct).ConfigureAwait(false);
                    continue; // advisory: do not block, do not transition
                }
                if (verdict.Outcome == GateVacuityOutcome.Vacuous || verdict.Outcome == GateVacuityOutcome.CleanupFailed)
                {
                    var kind = verdict.Outcome == GateVacuityOutcome.Vacuous ? "gate_vacuous" : "gate_canary_cleanup_failed";
                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = kind,
                        ["check"] = spec.Name,
                        ["reason"] = verdict.Reason ?? ""
                    }, ct).ConfigureAwait(false);
                    // Config/setup defect: hard-fail WITHOUT rework. Do NOT transition InReview->InProgress.
                    return new GateOutcome(false, checkResults, smokeSignals.AsReadOnly(),
                        verdict.Reason ?? $"gate: check '{spec.Name}' is vacuous", Vacuous: true);
                }
                // Ok / AlreadyProven: nothing to do.
            }
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
