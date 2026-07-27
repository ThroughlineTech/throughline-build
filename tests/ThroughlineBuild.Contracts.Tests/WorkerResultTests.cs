using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class WorkerResultTests
{
    private static readonly IReadOnlyDictionary<string, object> EmptyMeta =
        new Dictionary<string, object>();

    private static WorkerResult MakeResult(Status status = Status.Ok) => new WorkerResult(
        Status: status,
        Summary: "All done",
        FilesChanged: Array.Empty<string>(),
        FailureReason: null,
        Metadata: EmptyMeta);

    [Fact]
    public void WorkerResult_EqualWhenSameValues()
    {
        var a = MakeResult();
        var b = MakeResult();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void WorkerResult_NotEqualWhenDifferentStatus()
    {
        var a = MakeResult(Status.Ok);
        var b = MakeResult(Status.Failed);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WorkerResult_WithExpression_ChangesStatus()
    {
        var a = MakeResult();
        var b = a with { Status = Status.NeedsRework };
        Assert.Equal(Status.NeedsRework, b.Status);
        Assert.Equal(Status.Ok, a.Status);
    }

    [Fact]
    public void WorkerResult_MetadataProperty_IsReadOnly()
    {
        var r = MakeResult();
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(r.Metadata);
    }
}
