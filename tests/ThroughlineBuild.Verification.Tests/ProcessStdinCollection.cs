using Xunit;

namespace ThroughlineBuild.Verification.Tests;

[CollectionDefinition(ProcessStdinCollection.Name, DisableParallelization = true)]
public sealed class ProcessStdinCollection
{
    public const string Name = "Process stdin";
}
