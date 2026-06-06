namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// A worker model tier: the model id for a <see cref="WorkerSize"/> plus an
/// optional reasoning effort. Effort is carried for every vendor but acted on
/// only by Codex (a no-op for claude-code/gemini/copilot). Leaf and I/O-free
/// per the Contracts constraint.
/// </summary>
public record ModelTier(string Model, string? Effort = null);
