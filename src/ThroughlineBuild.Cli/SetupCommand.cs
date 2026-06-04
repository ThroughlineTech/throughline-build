using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build setup' verb: makes a Plane project meet the criteria the build
/// workflow assumes. It diffs the live project against <see cref="WorkspaceSchema"/> and creates
/// any missing states and labels. Idempotent - a project that already meets criteria is left
/// untouched. With <c>checkOnly</c> it reports the gap and returns a non-zero exit code without
/// mutating anything (CI-friendly). This is the step between <c>build init</c> and the first
/// <c>build new</c> / <c>build chain</c> on a fresh project.
/// </summary>
public sealed class SetupCommand
{
    private readonly ITicketingProvisioner _provisioner;

    public SetupCommand(ITicketingProvisioner provisioner) => _provisioner = provisioner;

    /// <summary>
    /// Run the setup check/provision. Returns 0 when the project meets (or is brought to meet)
    /// criteria, 1 when <paramref name="checkOnly"/> is set and the project does not meet criteria.
    /// </summary>
    public async Task<int> ExecuteAsync(bool checkOnly, IConsole console, CancellationToken ct)
    {
        var existingStates = await _provisioner.ListStatesAsync(ct).ConfigureAwait(false);
        var existingLabels = await _provisioner.ListLabelNamesAsync(ct).ConfigureAwait(false);

        var stateNames = new HashSet<string>(existingStates.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var labelNames = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);

        var missingStates = WorkspaceSchema.States.Where(s => !stateNames.Contains(s.Name)).ToList();
        var missingLabels = WorkspaceSchema.Labels.Where(l => !labelNames.Contains(l)).ToList();

        var stateCount = WorkspaceSchema.States.Count;
        var labelCount = WorkspaceSchema.Labels.Count;

        if (missingStates.Count == 0 && missingLabels.Count == 0)
        {
            console.WriteLine($"Plane project meets criteria: all {stateCount} states and {labelCount} labels present.");
            return 0;
        }

        if (checkOnly)
        {
            console.ErrorWriteLine(
                $"Plane project does NOT meet criteria: {missingStates.Count} state(s) and {missingLabels.Count} label(s) missing.");
            foreach (var s in missingStates)
                console.ErrorWriteLine($"  missing state: {s.Name} ({s.Group})");
            foreach (var l in missingLabels)
                console.ErrorWriteLine($"  missing label: {l}");
            console.ErrorWriteLine("Run 'build setup' (without --check) to create them.");
            return 1;
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
            $"Setup complete: created {missingStates.Count} state(s) and {missingLabels.Count} label(s). Project now meets criteria.");
        return 0;
    }
}
