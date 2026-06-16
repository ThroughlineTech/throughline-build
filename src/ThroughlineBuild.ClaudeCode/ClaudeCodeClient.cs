using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.ClaudeCode;

/// <summary>
/// Small reusable facade over ThroughlineBuild's Claude Code worker transport.
/// </summary>
public sealed class ClaudeCodeClient
{
    private readonly ClaudeCodeClientOptions _options;

    public ClaudeCodeClient(ClaudeCodeClientOptions? options = null)
    {
        _options = options ?? new ClaudeCodeClientOptions();
    }

    public Task<ClaudePreflightResult> CheckAsync(CancellationToken cancellationToken = default) =>
        ClaudeCodePreflight.CheckAsync(
            _options.ExecutablePath,
            ClaudeCodeClientOptions.ToWorkerTransport(_options.Transport),
            cancellationToken);

    public Task<WorkerResult> RunAsync(
        string instruction,
        string workingDirectory,
        ClaudeCodeRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        options ??= new ClaudeCodeRunOptions();
        var finalInstruction = options.AppendWorkerResultContract
            ? ClaudeCodeWorkerResultContract.EnsurePresent(instruction)
            : instruction;
        return RunAsync(BuildBrief(finalInstruction, options), workingDirectory, options, cancellationToken);
    }

    public Task<WorkerResult> RunAsync(
        Brief brief,
        string workingDirectory,
        ClaudeCodeRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        options ??= new ClaudeCodeRunOptions();
        var agent = new ClaudeCodeAgent(_options.ToWorkerOptions());
        return agent.ExecuteAsync(brief, workingDirectory, options.ToWorkerOptions(), cancellationToken);
    }

    internal static Brief BuildBrief(string instruction, ClaudeCodeRunOptions options) =>
        new(
            options.TicketId,
            options.Phase,
            instruction,
            options.RelevantFiles,
            options.AllowedWrites,
            options.Context);
}
