using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

/// <summary>
/// Defines the contract for a worker agent that executes briefs in a specified working directory.
/// </summary>
public interface IWorkerAgent
{
    /// <summary>
    /// Gets the name that identifies this worker (e.g., "claude-code", "codex", "gemini").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes a brief asynchronously in the specified working directory.
    /// </summary>
    /// <param name="brief">The brief to execute.</param>
    /// <param name="workingDirectory">The directory in which the brief should be executed.</param>
    /// <param name="options">Options for controlling the execution.</param>
    /// <param name="ct">A cancellation token to stop the execution.</param>
    /// <returns>A task that represents the asynchronous execution and returns the result.</returns>
    Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct);
}

/// <summary>
/// Configurable options for worker agent execution.
/// </summary>
/// <param name="Timeout">The maximum duration the worker is allowed to run.</param>
/// <param name="AllowedTools">Optional list of tool names the worker is allowed to use. Null means all tools are allowed.</param>
/// <param name="EnvironmentVariables">Optional dictionary of environment variables to set for the execution. Null means no additional environment variables.</param>
public record WorkerOptions(
    TimeSpan Timeout,
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
