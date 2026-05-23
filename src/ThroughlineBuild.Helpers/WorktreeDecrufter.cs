using System.Diagnostics;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Helpers;

public enum DecruftStep
{
    KillPreviewPids,
    RemovePreviewState,
    PreCleanReparsePoints,
    GitWorktreeRemove,
    GitWorktreeRemoveForce,
    DirectoryDelete,
    GitWorktreePrune,
    WorktreeNotFound
}

public record DecruftStepOutcome(bool Attempted, bool Success, string? Notes);

public record DecruftResult(DecruftStep? HaltedAt, IReadOnlyDictionary<DecruftStep, DecruftStepOutcome> Outcomes);

public interface IProcessKiller
{
    bool TryKill(int pid, out string? notes);
}

internal sealed class DefaultProcessKiller : IProcessKiller
{
    public bool TryKill(int pid, out string? notes)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            notes = null;
            return true;
        }
        catch (ArgumentException) { notes = "already exited"; return true; }
        catch (InvalidOperationException) { notes = "exited during kill"; return true; }
        catch (Exception ex) { notes = ex.Message; return false; }
    }
}

public sealed class WorktreeDecrufter
{
    private readonly IGitClient _git;
    private readonly IProcessKiller _killer;

    public WorktreeDecrufter(IGitClient git, IProcessKiller? killer = null)
    {
        _git = git;
        _killer = killer ?? new DefaultProcessKiller();
    }

    public async Task<DecruftResult> DecruftAsync(
        string worktreePath,
        string mainWorktreePath,
        CancellationToken ct)
    {
        var outcomes = new Dictionary<DecruftStep, DecruftStepOutcome>();

        // Guard: verify worktree is in list
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        var normalized = Path.GetFullPath(worktreePath);
        if (!worktrees.Any(w => string.Equals(
                Path.GetFullPath(w.Path),
                normalized,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new DecruftResult(DecruftStep.WorktreeNotFound, outcomes);
        }

        // Step 1: kill preview PIDs
        var pidFile = Path.Combine(worktreePath, ".preview.pid");
        bool killSuccess = true;
        string? killNotes = null;
        if (File.Exists(pidFile))
        {
            var lines = await File.ReadAllLinesAsync(pidFile, ct).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var fields = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3) continue;
                if (fields[1] == "-") continue;
                if (!int.TryParse(fields[1], out var pid)) continue;
                if (!_killer.TryKill(pid, out var killNote))
                {
                    killSuccess = false;
                    killNotes = killNote;
                }
            }
        }
        outcomes[DecruftStep.KillPreviewPids] = new(Attempted: true, Success: killSuccess, Notes: killNotes);

        // Step 2: remove .preview.pid and .preview.meta
        bool removeStateSuccess = true;
        string? removeStateNotes = null;
        try
        {
            var metaFile = Path.Combine(worktreePath, ".preview.meta");
            if (File.Exists(pidFile)) File.Delete(pidFile);
            if (File.Exists(metaFile)) File.Delete(metaFile);
        }
        catch (Exception ex)
        {
            removeStateSuccess = false;
            removeStateNotes = ex.Message;
        }
        outcomes[DecruftStep.RemovePreviewState] = new(Attempted: true, Success: removeStateSuccess, Notes: removeStateNotes);

        // Step 3: pre-clean Windows node_modules reparse points
        bool reparseAttempted = OperatingSystem.IsWindows();
        bool reparseSuccess = true;
        string? reparseNotes = null;
        if (reparseAttempted)
        {
            var nodeModules = Path.Combine(worktreePath, "node_modules");
            if (Directory.Exists(nodeModules))
            {
                try
                {
                    var di = new DirectoryInfo(nodeModules);
                    if (di.LinkTarget != null)
                        di.Delete(false);
                }
                catch (Exception ex)
                {
                    reparseSuccess = false;
                    reparseNotes = ex.Message;
                }
            }
        }
        outcomes[DecruftStep.PreCleanReparsePoints] = new(Attempted: reparseAttempted, Success: reparseSuccess, Notes: reparseNotes);

        // Step 4: git worktree remove (no force)
        var r1 = await _git.RemoveWorktreeAsync(worktreePath, false, ct).ConfigureAwait(false);
        outcomes[DecruftStep.GitWorktreeRemove] = new(true, r1.Success, r1.FailureReason);
        if (r1.Success) return new DecruftResult(null, outcomes);

        // Step 5: git worktree remove --force
        var r2 = await _git.RemoveWorktreeAsync(worktreePath, true, ct).ConfigureAwait(false);
        outcomes[DecruftStep.GitWorktreeRemoveForce] = new(true, r2.Success, r2.FailureReason);
        if (r2.Success) return new DecruftResult(null, outcomes);

        // Step 6: Directory.Delete
        try
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, true);
            outcomes[DecruftStep.DirectoryDelete] = new(true, true, null);
        }
        catch (Exception ex)
        {
            outcomes[DecruftStep.DirectoryDelete] = new(true, false, ex.Message);
            return new DecruftResult(DecruftStep.DirectoryDelete, outcomes);
        }

        // Step 7: git worktree prune (via Process directly)
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = mainWorktreePath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("prune");

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var pruneErr = proc.ExitCode != 0
                ? await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)
                : null;
            outcomes[DecruftStep.GitWorktreePrune] = new(
                true,
                proc.ExitCode == 0,
                pruneErr?.Trim());
            return new DecruftResult(
                proc.ExitCode != 0 ? DecruftStep.GitWorktreePrune : null,
                outcomes);
        }
        catch (Exception ex)
        {
            outcomes[DecruftStep.GitWorktreePrune] = new(true, false, ex.Message);
            return new DecruftResult(DecruftStep.GitWorktreePrune, outcomes);
        }
    }
}
