using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class RelationTests
{
    [Fact]
    public void Relation_EqualWhenSameValues()
    {
        var a = new Relation("blocks", "TLB-5");
        var b = new Relation("blocks", "TLB-5");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Relation_NotEqualWhenDifferentValues()
    {
        var a = new Relation("blocks", "TLB-5");
        var b = new Relation("blocked-by", "TLB-5");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Relation_WithExpression_ChangesField()
    {
        var a = new Relation("blocks", "TLB-5");
        var b = a with { TargetId = "TLB-6" };
        Assert.Equal("blocks", b.Kind);
        Assert.Equal("TLB-6", b.TargetId);
        Assert.NotEqual(a, b);
    }
}
