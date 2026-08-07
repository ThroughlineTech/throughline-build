using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build setup' verb: makes a fresh project ready for the build workflow. Two
/// concerns, both idempotent:
/// <list type="number">
/// <item>Local repo - <c>git init</c> if the directory is not yet a git repository, and append a
/// standard language-neutral ignore list (see <see cref="GitignoreManager"/>) to <c>.gitignore</c>
/// without disturbing existing entries.</item>
/// <item>Plane project - create any missing states/labels so it meets <see cref="WorkspaceSchema"/>
/// (the binary resolves these by name at runtime and hard-fails on a missing label).</item>
/// </list>
/// With <c>checkOnly</c> nothing is mutated: each gap is reported and the command exits non-zero.
/// This is the step between <c>build init</c> and the first <c>build new</c> / <c>build chain</c>.
/// </summary>
public sealed class SetupCommand
{
    private readonly ITicketingProvisioner _provisioner;
    private readonly ILocalRepoOps _localRepo;
    private readonly string? _configPath;

    /// <param name="configPath">
    /// Path to .build/config.toml, when known, so RunLocalRepo can scan it for a literal Plane
    /// token (TLB-627). Optional and defaults to null so existing call sites that predate that
    /// check keep compiling; null simply skips the scan.
    /// </param>
    public SetupCommand(ITicketingProvisioner provisioner, ILocalRepoOps localRepo, string? configPath = null)
    {
        _provisioner = provisioner;
        _localRepo = localRepo;
        _configPath = configPath;
    }

    /// <summary>
    /// Run the setup check/provision. Returns 0 when the project meets (or is brought to meet)
    /// criteria; returns 1 when <paramref name="checkOnly"/> is set and any local or Plane gap remains.
    /// </summary>
    public async Task<int> ExecuteAsync(bool checkOnly, IConsole console, CancellationToken ct)
    {
        var localGap = RunLocalRepo(checkOnly, console);

        // After provisioning the local repo (git init + .gitignore), give a brand-new repo its
        // welcome commit so the first 'build ship' has a base ref. --check mutates nothing, so it
        // never commits. The shared helper is idempotent (skips repos that already have commits).
        if (!checkOnly)
            WelcomeCommit.EnsureInitialCommit(_localRepo, console);

        var planeGap = await RunPlaneAsync(checkOnly, console, ct).ConfigureAwait(false);

        if (checkOnly)
            return (localGap || planeGap) ? 1 : 0;
        return 0;
    }

    // Returns true when a gap was found (only meaningful in checkOnly mode; otherwise the gap is fixed).
    private bool RunLocalRepo(bool checkOnly, IConsole console)
    {
        console.WriteLine("Local repo:");
        var gap = false;

        if (_localRepo.IsGitRepository())
        {
            console.WriteLine("  git: already a git repository");
        }
        else
        {
            gap = true;
            if (checkOnly)
                console.ErrorWriteLine("  git: not a git repository (run 'build setup' to initialize)");
            else
            {
                _localRepo.GitInit();
                console.WriteLine("  git: initialized empty repository");
            }
        }

        var existing = _localRepo.ReadGitignore();
        var missing = GitignoreManager.MissingEntries(existing);
        if (missing.Count == 0)
        {
            console.WriteLine("  .gitignore: all standard entries present");
        }
        else
        {
            gap = true;
            if (checkOnly)
                console.ErrorWriteLine($"  .gitignore: {missing.Count} entr(ies) missing: {string.Join(", ", missing)}");
            else
            {
                _localRepo.WriteGitignore(GitignoreManager.Merge(existing)!);
                console.WriteLine($"  .gitignore: added {missing.Count} entr(ies): {string.Join(", ", missing)}");
            }
        }

        if (_configPath is not null && File.Exists(_configPath))
        {
            var remediation = InlineTokenScanner.Scan(File.ReadAllText(_configPath));
            if (remediation is not null)
            {
                // No mutating branch here (unlike git/.gitignore above): build never rewrites a file
                // that may hold a live secret, so this always just warns, checkOnly or not.
                gap = true;
                console.ErrorWriteLine($"  ticketing: {remediation}");
            }
        }

        return gap;
    }

    // Returns true when a gap was found (only meaningful in checkOnly mode; otherwise the gap is fixed).
    private async Task<bool> RunPlaneAsync(bool checkOnly, IConsole console, CancellationToken ct)
    {
        var existingStates = await _provisioner.ListStatesAsync(ct).ConfigureAwait(false);
        var existingLabels = await _provisioner.ListLabelNamesAsync(ct).ConfigureAwait(false);

        var stateNames = new HashSet<string>(existingStates.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var labelNames = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);

        var missingStates = WorkspaceSchema.States.Where(s => !stateNames.Contains(s.Name)).ToList();
        var missingLabels = WorkspaceSchema.Labels.Where(l => !labelNames.Contains(l)).ToList();

        var stateCount = WorkspaceSchema.States.Count;
        var labelCount = WorkspaceSchema.Labels.Count;

        console.WriteLine("Plane project:");

        if (missingStates.Count == 0 && missingLabels.Count == 0)
        {
            console.WriteLine($"  meets criteria: all {stateCount} states and {labelCount} labels present");
            return false;
        }

        if (checkOnly)
        {
            console.ErrorWriteLine(
                $"  does NOT meet criteria: {missingStates.Count} state(s) and {missingLabels.Count} label(s) missing");
            foreach (var s in missingStates)
                console.ErrorWriteLine($"  missing state: {s.Name} ({s.Group})");
            foreach (var l in missingLabels)
                console.ErrorWriteLine($"  missing label: {l}");
            console.ErrorWriteLine("  run 'build setup' (without --check) to create them");
            return true;
        }

        // Display sequence for new states: continue past the highest existing one so created
        // states sort after the project's current set. Plane matches by name, so this is cosmetic.
        var nextSequence = existingStates.Count == 0
            ? 10_000d
            : existingStates.Max(s => s.Sequence) + 1;

        foreach (var s in missingStates)
        {
            await _provisioner.CreateStateAsync(s.Name, s.Group, nextSequence, ct).ConfigureAwait(false);
            nextSequence += 1;
            console.WriteLine($"  created state: {s.Name} ({s.Group})");
        }

        foreach (var l in missingLabels)
        {
            await _provisioner.CreateLabelAsync(l, ct).ConfigureAwait(false);
            console.WriteLine($"  created label: {l}");
        }

        console.WriteLine(
            $"  created {missingStates.Count} state(s) and {missingLabels.Count} label(s); project now meets criteria");
        return true;
    }
}
