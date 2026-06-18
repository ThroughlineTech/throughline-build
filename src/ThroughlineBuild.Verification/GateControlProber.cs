using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Verification;

public enum GateControlOutcome
{
    // The same checks pass on the untouched base ref: the failure is the ticket's code.
    BasePasses,
    // The same checks fail on the untouched base ref too: the environment/config is broken,
    // not the ticket - reworking the implementer cannot fix it.
    BaseFails,
    // The control run could not be executed (worktree creation failed, git error). Callers
    // must treat this as "not proven environmental" and take the normal rework path - a
    // broken prober must never misclassify a real code failure as environmental.
    Inconclusive
}

public record GateControlVerdict(
    GateControlOutcome Outcome,
    string? BaseSha,
    IReadOnlyList<CheckResult> CheckResults,
    string? Detail = null);

/// <summary>
/// Control run for gate-failure attribution (TLB-538): when the deterministic gate hard-fails,
/// re-run the failed gating checks (plus their Setup prerequisites) against the chain's base ref
/// in a throwaway worktree. If the base fails too, the failure is environmental (toolchain drift,
/// stale check config) and the chain must stop without burning rework rounds. Costs nothing on
/// the green path - it only runs after a gate hard-fail. Stack-agnostic by construction: it
/// re-executes the configured CheckSpecs, never interpreting their output.
/// </summary>
public class GateControlProber
{
    public virtual async Task<GateControlVerdict> ProbeAsync(
        IReadOnlyList<CheckSpec> checks,
        string baseRef,
        string mainWorktreePath,
        AutomatedChecksRunner runner,
        IGitClient git,
        CancellationToken ct)
    {
        if (checks.Count == 0)
            return new GateControlVerdict(GateControlOutcome.Inconclusive, null,
                Array.Empty<CheckResult>(), "no checks to control-run");

        string baseSha;
        try
        {
            baseSha = await git.RevParseAsync(baseRef, mainWorktreePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new GateControlVerdict(GateControlOutcome.Inconclusive, null,
                Array.Empty<CheckResult>(), $"could not resolve base ref '{baseRef}': {ex.Message}");
        }

        // CreateWorktreeAsync always creates a branch (-b); use a clearly-transient name and
        // delete it in the finally so a control run never leaves debris for 'build sweep'.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var worktreePath = Path.Combine(Path.GetTempPath(), $"gate-control-{suffix}");
        var branchName = $"gate-control/{suffix}";

        WorktreeCreateResult created;
        try
        {
            created = await git.CreateWorktreeAsync(worktreePath, branchName, baseSha, mainWorktreePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new GateControlVerdict(GateControlOutcome.Inconclusive, baseSha,
                Array.Empty<CheckResult>(), $"control worktree creation threw: {ex.Message}");
        }

        if (!created.Success)
            return new GateControlVerdict(GateControlOutcome.Inconclusive, baseSha,
                Array.Empty<CheckResult>(), $"control worktree creation failed: {created.FailureReason}");

        try
        {
            var controlWorktreePath = created.AbsolutePath ?? worktreePath;
            var missingInputs = await FirstMissingBaseInputsAsync(checks, controlWorktreePath, git, ct)
                .ConfigureAwait(false);
            if (missingInputs is not null)
                return new GateControlVerdict(GateControlOutcome.Inconclusive, baseSha,
                    Array.Empty<CheckResult>(), missingInputs);

            var results = await runner.RunAsync(checks, controlWorktreePath, ct)
                .ConfigureAwait(false);
            // A failed Setup step counts as a base failure too: the prerequisites the gate
            // depends on cannot be established even on pristine code.
            var baseFails = results.Any(r =>
                (r.Role == CheckRole.Gating || r.Role == CheckRole.Setup) && !r.Passed && !r.Skipped);
            return new GateControlVerdict(
                baseFails ? GateControlOutcome.BaseFails : GateControlOutcome.BasePasses,
                baseSha, results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GateControlVerdict(GateControlOutcome.Inconclusive, baseSha,
                Array.Empty<CheckResult>(), $"control run threw: {ex.Message}");
        }
        finally
        {
            try { await git.RemoveWorktreeAsync(worktreePath, force: true, ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            try { await git.DeleteBranchAsync(branchName, force: true, mainWorktreePath, ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<string?> FirstMissingBaseInputsAsync(
        IReadOnlyList<CheckSpec> checks,
        string controlWorktreePath,
        IGitClient git,
        CancellationToken ct)
    {
        foreach (var check in checks)
        {
            if (check.Role is not (CheckRole.Gating or CheckRole.Setup))
                continue;

            var requiredPaths = RequiredPathInputs(check);
            if (requiredPaths.Count == 0)
                continue;

            var missing = new List<string>();
            foreach (var path in requiredPaths)
            {
                IReadOnlyList<string> tracked;
                try
                {
                    tracked = await git.FilterTrackedPathsAsync(new[] { path }, controlWorktreePath, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return $"control base-input probe threw for check '{check.Name}': {ex.Message}";
                }

                if (tracked.Count == 0)
                    missing.Add(path);
            }

            if (missing.Count > 0)
            {
                var noun = missing.Count == 1 ? "path is" : "paths are";
                return $"control check '{check.Name}' cannot run on base ref: required {noun} not tracked ({string.Join(", ", missing)})";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> RequiredPathInputs(CheckSpec check)
    {
        var explicitPaths = NormalizePathList(check.RequiredPaths);
        if (explicitPaths.Count > 0)
            return explicitPaths;

        if (check.Canary is not { Count: > 0 })
            return Array.Empty<string>();

        var canaryParents = new List<string>();
        foreach (var canary in check.Canary)
        {
            var path = NormalizePath(canary.Path);
            if (path.Length == 0)
                continue;

            var slash = path.LastIndexOf('/');
            if (slash > 0)
                canaryParents.Add(path[..slash]);
        }

        return NormalizePathList(canaryParents);
    }

    private static IReadOnlyList<string> NormalizePathList(IEnumerable<string>? paths)
    {
        if (paths is null)
            return Array.Empty<string>();

        var normalized = paths
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return normalized.Count == 0 ? Array.Empty<string>() : normalized.AsReadOnly();
    }

    private static string NormalizePath(string path) =>
        path.Trim().Replace('\\', '/').Trim('/');
}
