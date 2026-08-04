using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Cli;

public sealed record ProfileGateVerificationResult(bool Success, string? FailureReason)
{
    public static ProfileGateVerificationResult Ok() => new(true, null);
    public static ProfileGateVerificationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Validates an LLM-derived review gate before it can be written to configuration. The profile is
/// exercised in an isolated worktree so a declared canary can never modify the operator's tree.
/// </summary>
public class ProfileGateVerifier
{
    public virtual async Task<ProfileGateVerificationResult> VerifyAsync(
        ProjectProfile profile,
        string mainWorktreePath,
        IGitClient git,
        AutomatedChecksRunner runner,
        CancellationToken ct)
    {
        var allReviewChecks = profile.ReviewChecks.Select(ToCheckSpec).ToList();
        var gatingChecks = allReviewChecks.Where(check => check.Role == CheckRole.Gating).ToList();
        if (gatingChecks.Count == 0)
            return ProfileGateVerificationResult.Fail(
                "derived profile has no gating review check; it cannot produce a blocking gate");

        string baseSha;
        try
        {
            baseSha = await git.RevParseAsync("HEAD", mainWorktreePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ProfileGateVerificationResult.Fail(
                $"could not resolve HEAD for gate verification: {ex.Message}");
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var worktreePath = Path.Combine(Path.GetTempPath(), $"profile-derive-{suffix}");
        var branchName = $"profile-derive/{suffix}";
        bool created = false;

        try
        {
            WorktreeCreateResult create;
            try
            {
                create = await git.CreateWorktreeAsync(
                    worktreePath, branchName, baseSha, mainWorktreePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ProfileGateVerificationResult.Fail(
                    $"could not create a throwaway verification worktree: {ex.Message}");
            }

            if (!create.Success)
            {
                return ProfileGateVerificationResult.Fail(
                    $"could not create a throwaway verification worktree: {create.FailureReason}");
            }

            created = true;
            var verificationWorktree = create.AbsolutePath ?? worktreePath;

            // Run setup and gating checks once before probing. This catches a profile that only
            // works after a previous local build, such as a --no-restore command in a fresh tree.
            var baselineSpecs = allReviewChecks
                .Where(check => check.Role is CheckRole.Setup or CheckRole.Gating)
                .ToList();
            var baseline = await runner.RunAsync(
                baselineSpecs,
                verificationWorktree,
                ct,
                AutomatedChecksRunner.RequiredPathHandling.Inconclusive).ConfigureAwait(false);
            var failedBaseline = baseline.FirstOrDefault(result =>
                result.Role is CheckRole.Setup or CheckRole.Gating &&
                (!result.Passed || result.Inconclusive || result.Skipped));
            if (failedBaseline is not null)
            {
                var detail = failedBaseline.Inconclusive
                    ? failedBaseline.StderrTail
                    : $"exit {failedBaseline.ExitCode}: {failedBaseline.StderrTail}";
                return ProfileGateVerificationResult.Fail(
                    $"derived {failedBaseline.Role.ToString().ToLowerInvariant()} check " +
                    $"'{failedBaseline.Name}' does not pass in a fresh worktree ({detail})");
            }

            // A distinct prover per check makes every configured gating check prove its own
            // canary, even when a poorly formed profile reuses a check name.
            foreach (var gatingCheck in gatingChecks)
            {
                var verdict = await new GateVacuityProver().ProveAsync(
                    gatingCheck,
                    runner,
                    git,
                    verificationWorktree,
                    ct).ConfigureAwait(false);
                if (verdict.Outcome != GateVacuityOutcome.Ok)
                {
                    return ProfileGateVerificationResult.Fail(
                        verdict.Reason ??
                        $"gating check '{gatingCheck.Name}' could not prove its canary is rejected");
                }
            }

            return ProfileGateVerificationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProfileGateVerificationResult.Fail($"gate verification threw: {ex.Message}");
        }
        finally
        {
            if (created)
            {
                try { await git.RemoveWorktreeAsync(worktreePath, force: true, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
                try { await git.DeleteBranchAsync(branchName, force: true, mainWorktreePath, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private static CheckSpec ToCheckSpec(ProfileCheck check) => new(
        check.Name,
        check.Executable,
        check.Arguments,
        TimeSpan.FromMinutes(check.TimeoutMinutes),
        check.Role,
        check.Canary,
        check.RequiredPaths);
}
