using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

/// <summary>
/// Guards the canonical WorkspaceSchema that `build setup` provisions against. The drift guard
/// is the point: if a TicketState is added without a matching schema entry, a fresh project
/// would be missing that state and the failure would only surface at runtime against Plane.
/// </summary>
public class WorkspaceSchemaTests
{
    [Fact]
    public void States_CoverEveryTicketStateExactlyOnce()
    {
        var schemaStates = WorkspaceSchema.States.Select(s => s.State).OrderBy(x => (int)x);
        var enumStates = Enum.GetValues<TicketState>().OrderBy(x => (int)x);
        Assert.Equal(enumStates, schemaStates);
    }

    [Fact]
    public void States_MapToValidPlaneGroups()
    {
        var validGroups = new[] { "backlog", "unstarted", "started", "completed", "cancelled" };
        Assert.All(WorkspaceSchema.States, s => Assert.Contains(s.Group, validGroups));
    }

    [Fact]
    public void Labels_IncludeTheRuntimeMandatoryRiskAndSize()
    {
        // These six are the ones the plan phase applies and that hard-fail the run when absent.
        var mandatory = new[] { "risk:low", "risk:medium", "risk:high", "size:s", "size:m", "size:l" };
        Assert.All(mandatory, m => Assert.Contains(m, WorkspaceSchema.Labels));
    }

    [Fact]
    public void Schema_HasNoDuplicateNames()
    {
        Assert.Equal(WorkspaceSchema.States.Count, WorkspaceSchema.States.Select(s => s.Name).Distinct().Count());
        Assert.Equal(WorkspaceSchema.Labels.Count, WorkspaceSchema.Labels.Distinct().Count());
    }
}
