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
    bool Vacuous = false,
    // Environment failure (TLB-538): the failed gating checks ALSO fail on the untouched base ref,
    // so the breakage is the machine/config, not this ticket's code. The chain must hard-fail
    // WITHOUT rework (a worker cannot fix the environment) and skip remaining siblings.
    bool EnvironmentFailure = false,
    // Output evidence from the base-ref control run (check name, exit code, output tail),
    // surfaced verbatim in the operator triage so the diagnosis is actionable.
    string? ControlEvidence = null);

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
    // TLB-538: classifies a gate hard-fail as code-vs-environment by re-running the failed
    // checks on the untouched base ref. Null disables classification (every hard-fail reworks).
    private readonly GateControlProber? _controlProber;
    // TLB-538: re-reads the gate CheckSpecs from the on-disk config. The CLI loads config once
    // at startup, so a config fixed mid-run (by the operator or a worker) is otherwise invisible;
    // when an environment failure is detected and the on-disk specs differ, the gate re-runs once
    // with the fresh specs and recovers in-run. Null disables the recovery arm.
    private readonly Func<IReadOnlyList<CheckSpec>?>? _gateChecksReloader;

    public GatePhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        GateOptions gateOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null,
        GateVacuityProver? vacuityProver = null,
        GateControlProber? controlProber = null,
        Func<IReadOnlyList<CheckSpec>?>? gateChecksReloader = null)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
        _gateOptions = gateOptions;
        _git = gitClient ?? new ProcessGitClient();
        _checksRunner = checksRunner;
        _vacuityProver = vacuityProver;
        _controlProber = controlProber;
        _gateChecksReloader = gateChecksReloader;
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

        // The check specs the vacuity prover (below) probes against. Normally the startup config;
        // replaced when the environment-failure recovery arm reloads a fixed config from disk.
        var activeSpecs = _gateOptions.Checks;

        // Setup steps (CheckRole.Setup) are prerequisites the runner executes BEFORE the real checks
        // (e.g. a codegen/install step the gitignored build inputs depend on). A failed setup step means
        // the checks could not run against a prepared worktree - hard-fail and bounce into rework: it may
        // be worker-fixable (a broken manifest) and the rework cap bounds it. Reported as setup_failed so
        // it is not mistaken for a code-level gating failure, and checked BEFORE the gating failures it
        // would have cascaded into (a missing prerequisite fails the build check too).
        var setupFailed = SetupFailures(checkResults);

        if (setupFailed.Count > 0)
        {
            var failedSetupNames = setupFailed.Select(r => r.Name).ToArray();
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "setup_failed",
                ["checks_failed"] = failedSetupNames
            }, ct).ConfigureAwait(false);
            await TransitionInReviewToInProgressAsync(ticketId, ct).ConfigureAwait(false);
            var escapedSetupNames = WebUtility.HtmlEncode(string.Join(", ", failedSetupNames));
            var setupCommentHtml = $"<p>[gate: hard-fail] setup step(s) failed before checks could run: {escapedSetupNames}</p>";
            await PostInformationalCommentAsync(
                ticketId, "gate_setup_failure_comment", setupCommentHtml, ct).ConfigureAwait(false);
            return new GateOutcome(false, checkResults, smokeSignals.AsReadOnly(),
                $"gate: setup step(s) failed: {string.Join(", ", failedSetupNames)}");
        }

        // Hard-fail only on Gating role failures; Advisory failures (lint, format) are recorded
        // in checkResults but never block the gate.
        var gatingFailed = GatingFailures(checkResults);

        // TLB-538: before bouncing the ticket into rework, attribute the failure. A control run
        // re-executes the failed gating checks (plus Setup prerequisites) against the untouched
        // base ref. Base fails too -> the environment/config is broken, not the ticket: first try
        // the recovery arm (a config fixed on disk mid-run), else hard-fail WITHOUT rework so no
        // worker session is spent on a failure no worker can fix. Base passes or the control is
        // inconclusive -> the normal rework path below, unchanged.
        if (gatingFailed.Count > 0 && _controlProber is not null)
        {
            var failedGatingNames = new HashSet<string>(gatingFailed.Select(r => r.Name), StringComparer.Ordinal);
            var controlSpecs = activeSpecs
                .Where(s => s.Role == CheckRole.Setup
                    || (s.Role == CheckRole.Gating && failedGatingNames.Contains(s.Name)))
                .ToList();

            var control = await _controlProber.ProbeAsync(
                controlSpecs, baseRef, workingDirectory, runner, _git, ct).ConfigureAwait(false);

            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "gate_control_run",
                ["outcome"] = control.Outcome.ToString(),
                ["base_ref"] = baseRef,
                ["base_sha"] = control.BaseSha ?? "",
                ["checks"] = controlSpecs.Select(s => s.Name).ToArray(),
                ["detail"] = control.Detail ?? ""
            }, ct).ConfigureAwait(false);

            if (control.Outcome == GateControlOutcome.BaseFails)
            {
                // Recovery arm: the config may have been fixed on disk after startup (the CLI
                // loads it once). If the on-disk gate specs differ, re-run once with them.
                var freshSpecs = _gateChecksReloader?.Invoke();
                if (freshSpecs is not null && !CheckSpecsEquivalent(freshSpecs, activeSpecs))
                {
                    var rerunResults = await runner.RunAsync(freshSpecs, worktreePath, ct).ConfigureAwait(false);
                    var recovered = SetupFailures(rerunResults).Count == 0
                        && GatingFailures(rerunResults).Count == 0;
                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = "gate_config_reloaded",
                        ["recovered"] = recovered
                    }, ct).ConfigureAwait(false);
                    if (recovered)
                    {
                        // The fresh checks are green: continue the normal pass path (vacuity
                        // probe + review) against the reloaded specs and results.
                        checkResults = rerunResults;
                        activeSpecs = freshSpecs;
                        gatingFailed = GatingFailures(checkResults);
                    }
                }

                if (gatingFailed.Count > 0)
                {
                    var envFailedNames = gatingFailed.Select(r => r.Name).ToArray();
                    var evidence = BuildControlEvidence(control);
                    var shortSha = control.BaseSha is { Length: >= 8 } sha ? sha[..8] : (control.BaseSha ?? baseRef);
                    var reason =
                        $"gate: {string.Join(", ", envFailedNames)} failed - environment failure: the same gating " +
                        $"check(s) also fail on the untouched base ref ({shortSha}), so the breakage is the " +
                        "environment or the check config, not this ticket's changes. Fix the environment or " +
                        ".build/config.toml, then re-run the chain - the ticket's work is preserved.";

                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = "gate_environment_failure",
                        ["checks_failed"] = envFailedNames,
                        ["base_sha"] = control.BaseSha ?? "",
                        ["evidence"] = evidence
                    }, ct).ConfigureAwait(false);
                    var escapedNames = WebUtility.HtmlEncode(string.Join(", ", envFailedNames));
                    var commentHtml = "<p>[gate: environment failure] gating checks failed on the ticket " +
                        $"worktree AND on the untouched base ref: {escapedNames}. Chain stopped without " +
                        "spending rework rounds; fix the environment or the check config and re-run.</p>";
                    await PostInformationalCommentAsync(
                        ticketId, "gate_environment_failure_comment", commentHtml, ct).ConfigureAwait(false);

                    // Environment defect: hard-fail WITHOUT rework and leave the ticket InReview
                    // (no InProgress transition) so a re-run after the fix resumes cleanly.
                    return new GateOutcome(false, checkResults, smokeSignals.AsReadOnly(),
                        reason, EnvironmentFailure: true, ControlEvidence: evidence);
                }
            }
        }

        if (gatingFailed.Count > 0)
        {
            var failedNames = gatingFailed.Select(r => r.Name).ToArray();
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "gating_checks_failed",
                ["checks_failed"] = failedNames
            }, ct).ConfigureAwait(false);
            await TransitionInReviewToInProgressAsync(ticketId, ct).ConfigureAwait(false);
            var escapedNames = WebUtility.HtmlEncode(string.Join(", ", failedNames));
            var commentHtml = $"<p>[gate: hard-fail] gating checks failed: {escapedNames}</p>";
            await PostInformationalCommentAsync(
                ticketId, "gate_failure_comment", commentHtml, ct).ConfigureAwait(false);
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
            foreach (var spec in activeSpecs)
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

    private static List<CheckResult> SetupFailures(IReadOnlyList<CheckResult> results) =>
        results.Where(r => r.Role == CheckRole.Setup && !r.Passed && !r.Skipped).ToList();

    private static List<CheckResult> GatingFailures(IReadOnlyList<CheckResult> results) =>
        results.Where(r => r.Role == CheckRole.Gating && !r.Passed && !r.Skipped).ToList();

    // Command-equivalence for the config-reload recovery arm: a reload only counts as "changed"
    // when something that affects a check's execution differs (name, executable, arguments,
    // timeout, role). Canary edits are irrelevant to whether a check passes, so they do not
    // trigger a re-run.
    private static bool CheckSpecsEquivalent(IReadOnlyList<CheckSpec> a, IReadOnlyList<CheckSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].Executable, b[i].Executable, StringComparison.Ordinal)) return false;
            if (!a[i].Arguments.SequenceEqual(b[i].Arguments, StringComparer.Ordinal)) return false;
            if (a[i].Timeout != b[i].Timeout) return false;
            if (a[i].Role != b[i].Role) return false;
        }
        return true;
    }

    // Evidence block for the operator: per failed control check, the name, exit code, and an
    // output tail (stderr preferred). Bounded so a noisy build log cannot flood the rationale;
    // the full 4 KB tails remain in the event log's CheckResults.
    private static string BuildControlEvidence(GateControlVerdict control)
    {
        const int perCheckCap = 1500;
        var parts = new List<string>();
        foreach (var r in control.CheckResults)
        {
            if (r.Passed || r.Skipped) continue;
            var tail = !string.IsNullOrWhiteSpace(r.StderrTail) ? r.StderrTail : r.StdoutTail;
            if (tail.Length > perCheckCap) tail = "..." + tail[^perCheckCap..];
            parts.Add($"[{r.Name}] exit {r.ExitCode} on base ref:\n{tail}".TrimEnd());
        }
        return string.Join("\n---\n", parts);
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

    private Task<bool> PostInformationalCommentAsync(
        string ticketId, string operation, string html, CancellationToken ct) =>
        TicketingWritePolicy.BestEffortAsync(
            _events, _options.SessionId, ticketId, Phase.Gate, operation,
            () => _ticketing.CreateCommentAsync(ticketId, html, ct), ct);
}
