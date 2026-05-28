using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Helpers;

public static class WorkerSizeMapper
{
    public static WorkerSize FromTicketSize(Size size) => size switch
    {
        Size.S => WorkerSize.Small,
        Size.L => WorkerSize.Large,
        _ => WorkerSize.Medium
    };
}
