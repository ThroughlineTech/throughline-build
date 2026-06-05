namespace ThroughlineBuild.Contracts;

/// <summary>
/// An existing Plane state as read back from the project, used by `build setup` to diff the
/// live project against <see cref="WorkspaceSchema"/> and to pick the next display sequence.
/// </summary>
public readonly record struct ExistingState(string Name, string Group, double Sequence);

/// <summary>
/// Project-level provisioning operations: read the live state/label set and create missing
/// entries. Distinct from <see cref="ITicketing"/> (which operates on individual tickets) so
/// the `setup` command depends only on what it needs. Implemented by the Plane backend.
/// </summary>
public interface ITicketingProvisioner
{
    /// <summary>Lists the project's existing states (name, group, sequence).</summary>
    Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct);

    /// <summary>Lists the names of the project's existing labels.</summary>
    Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct);

    /// <summary>Creates a state in the given Plane state-group at the given display sequence.</summary>
    Task CreateStateAsync(string name, string group, double sequence, CancellationToken ct);

    /// <summary>Creates a label with the given name.</summary>
    Task CreateLabelAsync(string name, CancellationToken ct);
}
