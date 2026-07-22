namespace ThroughlineBuild.Contracts;

/// <summary>Raised when explicit relation management targets a backend without the endpoint.</summary>
public sealed class RelationEndpointUnavailableException : Exception
{
    public RelationEndpointUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
