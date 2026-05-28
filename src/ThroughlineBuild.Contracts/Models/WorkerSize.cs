namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// An abstract size signal agents map to their own model tiers.
/// Sourced from the ticket size (Plan C). Worker-domain only - separate
/// from the ticket-domain <see cref="Size"/> enum.
/// </summary>
public enum WorkerSize
{
    Small,
    Medium,
    Large
}
