using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class TicketCommandRegistryTests
{
    private sealed class MockTicketCommand : ITicketCommand
    {
        public TicketCommandContext? LastContext { get; private set; }
        public int InvocationCount { get; private set; }
        public CommandResult ResultToReturn { get; set; } = new CommandResult(true, null);

        public Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
        {
            LastContext = ctx;
            InvocationCount++;
            return Task.FromResult(ResultToReturn);
        }
    }

    private static TicketCommandContext MakeContext(string ticketId = "TLB-1") =>
        new TicketCommandContext(ticketId, new Dictionary<string, string>());

    [Fact]
    public void Register_then_TryGet_returns_the_registered_command()
    {
        var registry = new TicketCommandRegistry();
        var mock = new MockTicketCommand();

        registry.Register("amend", mock);

        Assert.True(registry.TryGet("amend", out var found));
        Assert.NotNull(found);
        Assert.Same(mock, found);
    }

    [Fact]
    public void TryGet_unknown_verb_returns_false_and_null()
    {
        var registry = new TicketCommandRegistry();

        var ok = registry.TryGet("does-not-exist", out var found);

        Assert.False(ok);
        Assert.Null(found);
    }

    [Fact]
    public async Task Mock_command_ExecuteAsync_called_through_registry()
    {
        var registry = new TicketCommandRegistry();
        var mock = new MockTicketCommand();
        registry.Register("close", mock);
        var ctx = MakeContext("TLB-42");

        Assert.True(registry.TryGet("close", out var cmd));
        Assert.NotNull(cmd);
        var result = await cmd!.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, mock.InvocationCount);
        Assert.NotNull(mock.LastContext);
        Assert.Equal("TLB-42", mock.LastContext!.TicketId);
    }

    [Fact]
    public async Task CommandResult_success_false_path()
    {
        var registry = new TicketCommandRegistry();
        var mock = new MockTicketCommand
        {
            ResultToReturn = new CommandResult(false, "intentional failure")
        };
        registry.Register("defer", mock);

        Assert.True(registry.TryGet("defer", out var cmd));
        Assert.NotNull(cmd);
        var result = await cmd!.ExecuteAsync(MakeContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("intentional failure", result.Message);
    }
}
