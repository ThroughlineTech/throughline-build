namespace ThroughlineBuild.Cli;

/// <summary>
/// The "welcome to throughline build" initial commit. A brand-new repo has no commits,
/// so the first 'build ship' would fail when resolving the base ref. Committing
/// <c>.gitignore</c> at provisioning time closes that gap at the exact moment the repo is
/// created. Lives in one place and is called from both connected <c>build init</c> and
/// <c>build setup</c>; the <see cref="ILocalRepoOps.HasAnyCommits"/> guard makes it
/// idempotent, so a repo that already has commits (or both paths running in one init) is
/// committed exactly once.
/// </summary>
public static class WelcomeCommit
{
    public const string Message = "welcome to throughline build";

    /// <summary>
    /// On a fresh repo (no commits yet), commit only <c>.gitignore</c> as the initial commit.
    /// Repos that already have a commit are left untouched. Commit failure (e.g. missing
    /// git user.name / user.email) is reported as a non-fatal warning, not a hard error.
    /// <c>.build/config.toml</c> is gitignored and is never staged.
    /// </summary>
    public static void EnsureInitialCommit(ILocalRepoOps localRepo, IConsole console)
    {
        if (localRepo.HasAnyCommits())
            return;

        try
        {
            localRepo.StageAndCommit(new[] { ".gitignore" }, Message);
            console.WriteLine("Welcome:      committed .gitignore as initial commit");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine(
                $"Warning: welcome commit skipped ({ex.Message}). "
                + "Commit at least one file before running 'build ship'.");
        }
    }
}
