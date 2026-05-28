using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class WorkerSizeMapperTests
{
    [Fact]
    public void S_maps_to_Small() =>
        Assert.Equal(WorkerSize.Small, WorkerSizeMapper.FromTicketSize(Size.S));

    [Fact]
    public void M_maps_to_Medium() =>
        Assert.Equal(WorkerSize.Medium, WorkerSizeMapper.FromTicketSize(Size.M));

    [Fact]
    public void L_maps_to_Large() =>
        Assert.Equal(WorkerSize.Large, WorkerSizeMapper.FromTicketSize(Size.L));
}
