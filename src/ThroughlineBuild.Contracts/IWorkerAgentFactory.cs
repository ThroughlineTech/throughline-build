namespace ThroughlineBuild.Contracts;

/// <summary>
/// Creates IWorkerAgent instances by name. The factory owns the mapping from a
/// configured agent name (e.g. "claude-code") to a concrete IWorkerAgent
/// implementation, decoupling callers from construction details.
/// </summary>
public interface IWorkerAgentFactory
{
    /// <summary>
    /// Creates the worker agent registered under <paramref name="agentName"/>.
    /// </summary>
    /// <param name="agentName">The name of the agent to create (e.g. "claude-code").</param>
    /// <returns>A new IWorkerAgent instance.</returns>
    IWorkerAgent Create(string agentName);
}
