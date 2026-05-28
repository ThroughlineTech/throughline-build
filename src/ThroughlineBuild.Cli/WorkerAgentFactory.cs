using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Resolves a configured agent name to a concrete IWorkerAgent instance.
/// The registry is supplied at construction time as a string-keyed dictionary
/// of factory functions, keeping this class independent of any specific
/// worker implementation.
/// </summary>
public sealed class WorkerAgentFactory : IWorkerAgentFactory
{
    private readonly Dictionary<string, Func<IWorkerAgent>> _registry;

    /// <summary>
    /// Initialises the factory with a name-to-constructor registry.
    /// </summary>
    /// <param name="registry">
    /// Map of agent name to a zero-argument factory delegate that creates one
    /// IWorkerAgent instance. The comparison is case-sensitive (Ordinal).
    /// </param>
    public WorkerAgentFactory(Dictionary<string, Func<IWorkerAgent>> registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc/>
    public IWorkerAgent Create(string agentName)
    {
        if (_registry.TryGetValue(agentName, out var factory))
            return factory();

        var known = string.Join(", ", _registry.Keys.Order());
        throw new ConfigException(
            $"unknown agent name \"{agentName}\"; known agents: {known}");
    }
}
