using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class WorkerOptionsTests
{
    [Fact]
    public void Default_size_is_Medium()
    {
        var opts = new WorkerOptions(TimeSpan.FromMinutes(5));
        Assert.Equal(WorkerSize.Medium, opts.Size);
    }

    [Fact]
    public void Explicit_size_is_carried()
    {
        var opts = new WorkerOptions(TimeSpan.FromMinutes(5), Size: WorkerSize.Large);
        Assert.Equal(WorkerSize.Large, opts.Size);
    }
}
