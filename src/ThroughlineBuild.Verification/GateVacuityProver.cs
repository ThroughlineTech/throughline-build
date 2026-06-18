using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Verification;

// The outcome of probing whether a green gating check is structurally capable of failing.
public enum GateVacuityOutcome
{
    // The check rejected its canary: it is non-vacuous.
    Ok,
    // The check PASSED with its broken canary present: it does not inspect what it claims to.
    Vacuous,
    // No canary was declared, so non-vacuity cannot be proven (caller decides what to do).
    Unverified,
    // The probe ran (Ok) but a canary file leaked and could be committed into a real ship.
    CleanupFailed,
    // This check name was already probed this run; not re-probed.
    AlreadyProven
}

// A human-actionable verdict. Reason names the check and what went wrong; null for Ok/AlreadyProven.
public record GateVacuityVerdict(GateVacuityOutcome Outcome, string CheckName, string? Reason);

/// <summary>
/// Proves a green GATING check CAN fail by materializing the check's declared canary
/// (a deliberately-broken file), re-running ONLY that check, and asserting it now fails.
/// Stack-agnostic by construction: the canary is data on the CheckSpec; this prover only
/// writes files and runs a command. It never knows any language, tool, or stack.
/// One instance is created per chain run and shared across every gate invocation, so the
/// per-check-once set lives on the instance.
/// </summary>
public class GateVacuityProver
{
    // Per-run, per-check-once: a check name probed (or skipped) once is never probed again.
    private readonly HashSet<string> _proven = new(StringComparer.Ordinal);

    /// <summary>
    /// The caller guarantees this is only called for a GATING check that just returned GREEN
    /// against real source. Returns a verdict; never throws for vacuity/cleanup defects (those
    /// are returned as outcomes the caller turns into a chain hard-fail). If the runner itself
    /// throws, the exception propagates AFTER the canary is cleaned up.
    /// </summary>
    public virtual async Task<GateVacuityVerdict> ProveAsync(
        CheckSpec spec,
        AutomatedChecksRunner runner,
        IGitClient git,
        string worktreePath,
        CancellationToken ct)
    {
        // 1. PER-CHECK-ONCE: already probed/skipped this run -> do not probe again.
        if (_proven.Contains(spec.Name))
            return new GateVacuityVerdict(GateVacuityOutcome.AlreadyProven, spec.Name, null);

        // 2. NO CANARY: cannot prove non-vacuity. Mark proven so we do not re-check, and let
        //    the caller emit a countable event; the prover itself does not block.
        if (spec.Canary is null || spec.Canary.Count == 0)
        {
            _proven.Add(spec.Name);
            return new GateVacuityVerdict(
                GateVacuityOutcome.Unverified,
                spec.Name,
                $"gating check '{spec.Name}' has no canary; cannot prove it is non-vacuous");
        }

        var canary = spec.Canary;
        var firstPath = canary[0].Path;
        var writtenFull = new List<string>(canary.Count);

        try
        {
            // 3a. MATERIALIZE: write each canary file (overwrite), creating parent dirs as needed.
            foreach (var cf in canary)
            {
                var full = Path.GetFullPath(Path.Combine(worktreePath, cf.Path));
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(full, cf.Content);
                writtenFull.Add(full);
            }

            // 3b. RE-RUN ONLY THIS CHECK against the worktree (now containing the canary).
            //     If the runner throws, it propagates out through the finally (which cleans up).
            var results = await runner.RunAsync(new[] { spec }, worktreePath, ct);

            // 3c. ASSERT.
            GateVacuityVerdict verdict;
            if (results.Count == 0)
            {
                // RunAsync always returns one-per-spec; defensive only.
                verdict = new GateVacuityVerdict(
                    GateVacuityOutcome.Vacuous,
                    spec.Name,
                    $"gating check '{spec.Name}' produced no probe result; treating as vacuous");
            }
            else
            {
                var probe = results[0];
                if (probe.Passed)
                {
                    // PASSED with the broken canary present => structurally vacuous.
                    verdict = new GateVacuityVerdict(
                        GateVacuityOutcome.Vacuous,
                        spec.Name,
                        $"gating check '{spec.Name}' passed with its canary present ({firstPath}); " +
                        "the check is structurally vacuous (it does not inspect the canary)");
                }
                else
                {
                    verdict = new GateVacuityVerdict(GateVacuityOutcome.Ok, spec.Name, null);
                }
            }

            // 4. CLEANUP + CLEAN-ASSERT (delete first, then assert nothing leaked).
            verdict = await CleanupAndAssertAsync(verdict, spec.Name, canary, writtenFull, git, worktreePath, ct);
            return verdict;
        }
        finally
        {
            // Best-effort delete of every canary file written. This runs even when the runner
            // threw (the exception propagates AFTER this), so a thrown runner still leaves no
            // canary on disk. On the normal path CleanupAndAssertAsync already deleted; the
            // File.Exists guard makes the repeat harmless.
            DeleteCanaries(writtenFull);

            // 5. A probed check is never re-probed regardless of verdict (including a thrown runner).
            _proven.Add(spec.Name);
        }
    }

    // Deletes every written canary file (best-effort), then asserts no canary leaked on disk or
    // as an untracked git path. A cleanup failure OVERRIDES an Ok verdict but NEVER a Vacuous one
    // (vacuity is the worse signal and is what the caller acts on; the chain hard-fails either way).
    private static async Task<GateVacuityVerdict> CleanupAndAssertAsync(
        GateVacuityVerdict probeVerdict,
        string checkName,
        IReadOnlyList<CanaryFile> canary,
        IReadOnlyList<string> writtenFull,
        IGitClient git,
        string worktreePath,
        CancellationToken ct)
    {
        // Best-effort delete.
        DeleteCanaries(writtenFull);

        // Disk check: any written canary still on disk -> cleanup failed.
        string? leftover = null;
        foreach (var full in writtenFull)
        {
            if (File.Exists(full))
            {
                leftover = full;
                break;
            }
        }

        // Git hygiene check: any canary path reported as untracked -> cleanup failed.
        if (leftover is null)
        {
            var untracked = await git.GetUntrackedFilesAsync(worktreePath, ct);
            if (untracked is { Count: > 0 })
            {
                var normalizedUntracked = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in untracked)
                    normalizedUntracked.Add(Normalize(u));

                for (int i = 0; i < canary.Count; i++)
                {
                    var relNorm = Normalize(canary[i].Path);
                    var fullNorm = Normalize(writtenFull.Count > i ? writtenFull[i] : canary[i].Path);

                    bool matched = false;
                    foreach (var u in untracked)
                    {
                        var un = Normalize(u);
                        // Match by normalized relative path, or by full-path suffix.
                        if (un == relNorm || fullNorm.EndsWith(un, StringComparison.Ordinal) || un.EndsWith(relNorm, StringComparison.Ordinal))
                        {
                            matched = true;
                            break;
                        }
                    }

                    if (matched || normalizedUntracked.Contains(relNorm))
                    {
                        leftover = canary[i].Path;
                        break;
                    }
                }
            }
        }

        if (leftover is not null && probeVerdict.Outcome != GateVacuityOutcome.Vacuous)
        {
            return new GateVacuityVerdict(
                GateVacuityOutcome.CleanupFailed,
                checkName,
                $"canary '{leftover}' for gating check '{checkName}' could not be cleaned up after probing; " +
                "it could be committed into a real ship");
        }

        // No leftover, or the probe was already Vacuous (the worse, root signal).
        return probeVerdict;
    }

    // Best-effort deletion of every written canary file. Idempotent (File.Exists guard).
    private static void DeleteCanaries(IReadOnlyList<string> writtenFull)
    {
        foreach (var full in writtenFull)
        {
            try
            {
                if (File.Exists(full))
                    File.Delete(full);
            }
            catch
            {
                // best-effort; the clean-asserts catch a survivor on the normal path.
            }
        }
    }

    // Normalize a path for comparison: forward/back slashes unified to '/'.
    private static string Normalize(string path) => path.Replace('\\', '/');
}
