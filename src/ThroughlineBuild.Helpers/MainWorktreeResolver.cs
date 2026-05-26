using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Helpers;

public static class MainWorktreeResolver
{
    /// <summary>
    /// Returns the Path of the first entry from ListWorktreesAsync (which git porcelain
    /// always reports as the main worktree). Falls back to fallbackDir on any error or
    /// when the list is empty.
    /// </summary>
    public static async Task<string> ResolveAsync(IGitClient git, string fallbackDir, CancellationToken ct)
    {
        try
        {
            var worktrees = await git.ListWorktreesAsync(ct).ConfigureAwait(false);
            if (worktrees.Count > 0)
                return worktrees[0].Path;
        }
        catch
        {
            // intentional: fall through to return fallbackDir
        }
        return fallbackDir;
    }
}
