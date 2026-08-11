using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public sealed class TicketAttachmentTests
{
    [Fact]
    public void RecordCarriesNormalizedPublicMetadata()
    {
        var attachment = new TicketAttachment(
            "11111111-1111-1111-1111-111111111111",
            "work_item_attachment",
            "evidence.bin",
            "application/octet-stream",
            5);

        Assert.Equal("work_item_attachment", attachment.Source);
        Assert.Equal(5, attachment.SizeBytes);
    }
}
