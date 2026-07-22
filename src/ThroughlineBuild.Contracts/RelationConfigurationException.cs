namespace ThroughlineBuild.Contracts;

/// <summary>Raised when relation management cannot establish the configured project identity.</summary>
public sealed class RelationConfigurationException : Exception
{
    public RelationConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
